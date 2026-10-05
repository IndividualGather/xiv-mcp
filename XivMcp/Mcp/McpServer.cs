using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace XivMcp.Mcp;

/// <summary>
/// Minimal MCP server implementing the Streamable HTTP transport (JSON responses only, no server-initiated SSE).
/// Dalamud does not ship ASP.NET Core, so this sits directly on <see cref="HttpListener"/>.
/// </summary>
public sealed partial class McpServer : IDisposable
{
    public const string ServerName = "xiv-mcp";
    private static readonly string[] SupportedProtocolVersions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly ToolRegistry tools;
    private readonly Configuration config;
    private readonly string version;
    private HttpListener? listener;
    private CancellationTokenSource? cts;

    public bool IsRunning => listener?.IsListening == true;
    public string? LastError { get; private set; }
    public long RequestCount => Interlocked.Read(ref requestCount);
    public DateTime? LastRequestUtc { get; private set; }
    public string? LastClient { get; private set; }
    public DateTime? StartedUtc { get; private set; }
    private long requestCount;

    private readonly XivMcp.Permissions.ToolGate gate;

    /// <summary>The tool calls running right now, for the activity overlay.</summary>
    internal XivMcp.Ui.ActivityTracker Activity { get; } = new();

    internal McpServer(ToolRegistry tools, XivMcp.Permissions.ToolGate gate, Configuration config, Util.CacheRegistry caches)
    {
        this.tools = tools;
        this.config = config;
        this.caches = caches;
        this.gate = gate;
        caches.Updated += OnCacheUpdated;
        availableTools = AvailableToolNames();
        Svc.PluginInterface.ActivePluginsChanged += OnPluginsChanged;
        tools.Changed += NotifyIfToolsChanged;
        version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public IReadOnlyCollection<McpTool> Tools => tools.All;

    public string Endpoint => $"http://localhost:{config.Port}/mcp";

    public void Start()
    {
        Stop();
        try
        {
            listener = new HttpListener();
            // "localhost" prefixes do not require a URL ACL / admin rights and never bind external interfaces.
            listener.Prefixes.Add($"http://localhost:{config.Port}/");
            listener.Start();
            cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoop(listener, cts.Token));
            LastError = null;
            StartedUtc = DateTime.UtcNow;
            Svc.Log.Information($"MCP server listening on {Endpoint}");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Svc.Log.Error(ex, "Failed to start MCP server");
            listener = null;
        }
    }

    public void Stop()
    {
        foreach (var id in sessions.Keys.ToList()) EndSession(id);
        cts?.Cancel();
        try { listener?.Close(); } catch { /* already closed */ }
        listener = null;
        cts?.Dispose();
        cts = null;
    }

    public void Dispose()
    {
        caches.Updated -= OnCacheUpdated;
        Svc.PluginInterface.ActivePluginsChanged -= OnPluginsChanged;
        tools.Changed -= NotifyIfToolsChanged;
        Stop();
    }

    private string availableTools;
    private readonly Lock toolListLock = new();

    private string AvailableToolNames() => string.Join(",", tools.All.Where(t => t.IsAvailable && gate.IsListed(t)).Select(t => t.Name).Order());

    /// <summary>Some tools only exist while the plugin they drive is loaded: tell clients when the tool list changes.</summary>
    private void OnPluginsChanged(Dalamud.Plugin.IActivePluginsChangedEventArgs args) => NotifyIfToolsChanged();

    /// <summary>Sends notifications/tools/list_changed when the set of available tools differs from what clients last saw.</summary>
    public void NotifyIfToolsChanged()
    {
        var now = AvailableToolNames();
        lock (toolListLock)
        {
            if (now == availableTools) return;
            availableTools = now;
        }
        var frame = $"event: message\ndata: {new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" }.ToJsonString(JsonOptions)}\n\n";
        foreach (var session in sessions.Values)
        {
            EventStream[] streams;
            lock (session.Streams) streams = [.. session.Streams];
            foreach (var s in streams) _ = s.Write(frame);
        }
    }

    private async Task AcceptLoop(HttpListener l, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync().ConfigureAwait(false); }
            catch when (ct.IsCancellationRequested || !l.IsListening) { break; }
            catch (Exception ex) { Svc.Log.Warning(ex, "MCP accept failed"); continue; }

            _ = Task.Run(async () =>
            {
                try { await HandleHttp(ctx, ct).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, "Unhandled MCP request error");
                    try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { /* ignored */ }
                }
            }, ct);
        }
    }

    private async Task HandleHttp(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        res.Headers["Cache-Control"] = "no-store";

        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
        if (path is not ("/mcp" or ""))
        {
            await WriteText(res, 404, "Not found. The MCP endpoint is /mcp").ConfigureAwait(false);
            return;
        }

        // DNS rebinding protection: browsers always send Origin; only allow local origins.
        var origin = req.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin))
        {
            await WriteText(res, 403, "Forbidden origin").ConfigureAwait(false);
            return;
        }

        if (config.RequireToken)
        {
            var auth = req.Headers["Authorization"] ?? "";
            if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                !CryptoEquals(auth["Bearer ".Length..].Trim(), config.Token))
            {
                res.Headers["WWW-Authenticate"] = "Bearer";
                await WriteText(res, 401, "Missing or invalid bearer token. See the XIV MCP settings window (/xivmcp).").ConfigureAwait(false);
                return;
            }
        }

        // Sessions are optional (clients that ignore Mcp-Session-Id work statelessly), but an unknown id means the
        // server restarted — 404 tells the client to initialize again.
        var sessionId = req.Headers["Mcp-Session-Id"];
        Session? session = null;
        if (!string.IsNullOrEmpty(sessionId) && !sessions.TryGetValue(sessionId, out session))
        {
            await WriteText(res, 404, "Unknown session; initialize again.").ConfigureAwait(false);
            return;
        }

        switch (req.HttpMethod)
        {
            case "POST":
                break;
            case "GET": // server -> client event stream for resource update notifications
                await OpenEventStream(req, res, session, ct).ConfigureAwait(false);
                return;
            case "DELETE":
                if (sessionId is not null) EndSession(sessionId);
                res.StatusCode = 200;
                res.Close();
                return;
            default:
                res.Headers["Allow"] = "GET, POST, DELETE";
                await WriteText(res, 405, "Method not allowed").ConfigureAwait(false);
                return;
        }

        Interlocked.Increment(ref requestCount);
        LastRequestUtc = DateTime.UtcNow;
        if (session is not null) session.LastSeenUtc = DateTime.UtcNow;
        else lastStatelessRequestUtc = DateTime.UtcNow;

        string body;
        using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
            body = await reader.ReadToEndAsync(ct).ConfigureAwait(false);

        JsonNode? message;
        try { message = JsonNode.Parse(body); }
        catch (JsonException ex)
        {
            await WriteJson(res, 400, Error(null, -32700, "Parse error: " + ex.Message)).ConfigureAwait(false);
            return;
        }

        JsonNode? reply;
        if (message is JsonArray batch)
        {
            var replies = new JsonArray();
            foreach (var item in batch)
                if (await HandleMessage(item as JsonObject, res, session, ct).ConfigureAwait(false) is { } r)
                    replies.Add(r);
            reply = replies.Count > 0 ? replies : null;
        }
        else
        {
            reply = await HandleMessage(message as JsonObject, res, session, ct).ConfigureAwait(false);
        }

        if (reply is null)
        {
            res.StatusCode = 202; // only notifications / responses were received
            res.Close();
            return;
        }

        await WriteJson(res, 200, reply).ConfigureAwait(false);
    }

    /// <summary>Handles one JSON-RPC message. Returns null for notifications.</summary>
    private async Task<JsonNode?> HandleMessage(JsonObject? msg, HttpListenerResponse res, Session? session, CancellationToken ct)
    {
        if (msg is null) return Error(null, -32600, "Invalid request");

        var id = msg["id"]?.DeepClone();
        var method = msg["method"]?.GetValue<string>();
        if (method is null) return null; // a response to something we never sent; ignore
        var isNotification = !msg.ContainsKey("id");
        var @params = msg["params"] as JsonObject;
        if (method == "tools/call" && (@params?["task"] is not null || @params?["_meta"]?.ToJsonString().Contains("tasks") == true))
            Svc.Log.Information($"[MCP] Task-related tools/call params: task={@params?["task"]?.ToJsonString()} _meta={@params?["_meta"]?.ToJsonString()}");

        try
        {
            JsonNode? result = method switch
            {
                "initialize" => Initialize(@params, res),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = new JsonArray(tools.All.Where(t => t.IsAvailable && gate.IsListed(t)).OrderBy(t => t.Name).Select(t => (JsonNode)t.ToListEntry()).ToArray()) },
                "tools/call" => await CallTool(@params, ct).ConfigureAwait(false),
                "resources/list" => ListResources(),
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                "resources/read" => ReadResource(@params),
                "resources/subscribe" => Subscribe(@params, session, true),
                "resources/unsubscribe" => Subscribe(@params, session, false),
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ when method.StartsWith("notifications/", StringComparison.Ordinal) => null,
                _ => throw new RpcException(-32601, $"Method not found: {method}"),
            };

            if (isNotification) return null;
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (RpcException ex)
        {
            return isNotification ? null : Error(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"MCP method {method} failed");
            return isNotification ? null : Error(id, -32603, ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? p, HttpListenerResponse res)
    {
        var requested = p?["protocolVersion"]?.GetValue<string>();
        var negotiated = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];
        var clientName = p?["clientInfo"]?["name"]?.GetValue<string>();
        var clientVersion = p?["clientInfo"]?["version"]?.GetValue<string>();
        LastClient = clientName;
        Svc.Log.Information($"[MCP] Client connected: {clientName} {clientVersion}, protocol {requested} (using {negotiated}), capabilities {p?["capabilities"]?.ToJsonString() ?? "{}"}");
        res.Headers["Mcp-Session-Id"] = StartSession(clientName is null ? null : clientVersion is null ? clientName : $"{clientName} {clientVersion}");

        return new JsonObject
        {
            ["protocolVersion"] = negotiated,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = true },
                ["resources"] = new JsonObject { ["subscribe"] = true, ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject { ["name"] = ServerName, ["title"] = "Final Fantasy XIV (Dalamud)", ["version"] = version },
            ["instructions"] =
                "Live, read-only access to the Final Fantasy XIV character that is currently logged in, via a Dalamud plugin. " +
                "In addition, the plugin tools (list_plugins, set_plugin_enabled, reload_plugin, get/set_plugin_config) manage other Dalamud plugins " +
                "when the user has allowed it in the settings; always read a config before changing it and tell the user what you changed. " +
                "sort_inventory and move_items change the inventory (also only when allowed); read the inventory first, prefer sort_inventory " +
                "over many single moves, and confirm larger rearrangements with the user before starting them. " +
                "Start with get_game_status to see whether a character is logged in. Use the typed tools (character, jobs, inventory, " +
                "gear, currencies, quests, unlocks, party, objects, fates, retainers, submersibles) for live state, and search_game_data / get_game_data_row " +
                "to look up static game data (items, quests, achievements, ...) from the client's Excel sheets. " +
                "Some data is only available after the matching in-game window was opened once this session " +
                "(achievements, titles, saddlebag); tools report this when it applies. " +
                "Submersibles and retainers (list + every retainer's inventory) come from snapshot caches that refresh only at the FC workshop / " +
                "summoning bell: results carry a 'cache' block with age, stale flag and a refresh suggestion — relay stale data's age and the " +
                "suggestion to the user. get_cache_status shows all caches; wait_for_cache_refresh waits for the user to refresh one; the caches " +
                "are also subscribable resources (xiv://cache/<id>).",
        };
    }

    private async Task<JsonNode> CallTool(JsonObject? p, CancellationToken ct)
    {
        var name = p?["name"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing tool name");
        if (!tools.TryGet(name, out var tool)) throw new RpcException(-32602, $"Unknown tool: {name}");
        // Third-party tools go to the gate even when hidden, so the refusal says why (not enabled, suspended).
        if (!tool.IsAvailable && tool.Provider.Trust != ProviderTrust.ThirdParty)
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"'{name}' is not available right now: the plugin it needs is not installed or not loaded." }),
                ["isError"] = true,
            };
        // A background job step owns the character: other state-changing calls would collide with it.
        if (!tool.ReadOnly && !XivMcp.Util.JobManager.ControlTools.Contains(name) && !XivMcp.Util.JobManager.StopTools.Contains(name) && XivMcp.Util.JobManager.Instance is { ActingStepRunning: true } jm)
            return new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = $"The background job '{jm.RunningJobName}' is running a step; pause_job it first (reading tools still work)." }),
                ["isError"] = true,
            };

        string text;
        var isError = false;
        IReadOnlyList<ToolImage> images = [];
        var activity = Activity.Begin(name, LastClient, DateTime.UtcNow);
        try
        {
            var result = await gate.InvokeAsync(tool, new ToolArgs(p?["arguments"] as JsonObject), ct).ConfigureAwait(false);
            if (result is ToolResultWithImages withImages)
            {
                images = withImages.Images;
                result = withImages.Data;
            }
            text = result as string ?? JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (ToolException ex)
        {
            text = ex.Message;
            isError = true;
        }
        catch (Exception ex)
        {
            Svc.Log.Error(ex, $"Tool {name} failed");
            text = $"Tool '{name}' failed: {ex.GetType().Name}: {ex.Message}";
            isError = true;
        }

        Activity.End(activity, isError, DateTime.UtcNow);
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images)
        {
            if (image.Caption is not null) content.Add(new JsonObject { ["type"] = "text", ["text"] = image.Caption });
            content.Add(new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(image.Png), ["mimeType"] = "image/png" });
        }
        return new JsonObject { ["content"] = content, ["isError"] = isError };
    }

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static bool IsLocalOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
        (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));

    private static bool CryptoEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task WriteJson(HttpListenerResponse res, int status, JsonNode node)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(JsonOptions));
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    private static async Task WriteText(HttpListenerResponse res, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status;
        res.ContentType = "text/plain; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        res.Close();
    }

    private sealed class RpcException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
