// XivMcpClient.cs: a drop-in client for the XIV MCP plugin API (API version 2).
//
// Copy this file into your Dalamud plugin. It has no dependencies besides Dalamud and System.Text.Json, and
// it works whether or not XIV MCP is installed: tools are (re)registered whenever XIV MCP loads.
//
//     mcp = new XivMcpClient(pluginInterface);
//     mcp.AddTool(new("myplugin_status", "What My Plugin is doing right now.") { ReadOnly = true },
//                 args => new { running = IsRunning });
//     ...
//     mcp.Dispose();   // in your plugin's Dispose
//
// The full guide is docs/plugin-api.md in the XIV MCP repository. MIT licensed like XIV MCP.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace XivMcp.Client;

/// <summary>Describes one tool. Its <see cref="Description"/> is what the assistant reads to decide when to call it, so write it for that reader.</summary>
public sealed record McpToolDefinition(string Name, string Description)
{
    /// <summary>JSON schema of the arguments (an object schema). Null means "no arguments".</summary>
    public string? InputSchema { get; init; }

    /// <summary>True if the tool only reads. Tools that change something keep the default (false): XIV MCP won't run them while a job step runs.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>True if the tool's changes are hard to undo (deleting, overwriting files, spending currency).</summary>
    public bool Destructive { get; init; }

    /// <summary>
    /// What the tool may do, from <see cref="McpCapabilities"/>. Required for tools that aren't read-only: the player decides per
    /// capability whether the tool runs, asks first or is blocked, and XIV MCP checks each call against what you declared.
    /// </summary>
    public string[] Capabilities { get; init; } = [];
}

/// <summary>Capability ids for <see cref="McpToolDefinition.Capabilities"/>. XivMcp.ListCapabilities returns the full catalog with descriptions.</summary>
public static class McpCapabilities
{
    public const string ReadGame = "read_game";            // low: reads game state (implied for every tool)
    public const string GameUi = "game_ui";                // medium: opens or clicks game windows, talks to NPCs
    public const string MoveCharacter = "move_character";  // medium: walks, teleports, changes zone, instance or world
    public const string Combat = "combat";                 // high: enters duties or fights
    public const string MoveItems = "move_items";          // medium: moves items between containers
    public const string SpendGil = "spend_gil";            // high
    public const string SpendCurrency = "spend_currency";  // high: tomestones, scrips, seals, MGP, item currencies
    public const string TradeItems = "trade_items";        // high: market board, vendors, player trades
    public const string DiscardItems = "discard_items";    // critical: discard or desynthesise; always asked
    public const string ChatSend = "chat_send";            // high: chat or commands other players can see
    public const string Login = "login";                   // high: log out, switch character
    public const string EditSettings = "edit_settings";    // high: game, plugin or file settings
    public const string Network = "network";               // medium: web requests
}

/// <summary>What the player set for a capability: run without asking, ask first, or blocked.</summary>
public enum McpPermission { Allow, Ask, Deny }

/// <summary>Where your plugin stands with the player in XIV MCP.</summary>
public enum McpPluginState
{
    /// <summary>XIV MCP isn't loaded (or speaks an older API).</summary>
    Unavailable,

    /// <summary>Registered; the player hasn't decided yet (they got a notification).</summary>
    Undecided,

    /// <summary>The player chose to keep you disabled; they aren't asked again until your registration changes.</summary>
    KeptDisabled,

    /// <summary>Enabled: your tools are offered and run under the player's capability policies.</summary>
    Enabled,

    /// <summary>You were enabled, but your registration grew (new tool or capability): all your tools are paused until the player consents.</summary>
    AwaitingConsent,

    /// <summary>A call did something it didn't declare; every call is refused until the player lifts the suspension.</summary>
    Suspended,
}

/// <summary>Your plugin's status and the player's setting for each capability you declared.</summary>
/// <remarks><see cref="Tools"/>: what each of your tools is set to as a whole (the player can set a tool on its own; otherwise its strictest capability).</remarks>
public sealed record McpStatus(McpPluginState State, string? SuspendReason, IReadOnlyDictionary<string, McpPermission> Capabilities,
                               IReadOnlyDictionary<string, McpPermission> Tools)
{
    public bool CanRun => State == McpPluginState.Enabled;
}

/// <summary>A tool error the assistant should see as-is ("Not at a summoning bell."). Other exceptions are reported with their type.</summary>
public sealed class McpToolException(string message) : Exception(message);

/// <summary>A running long-running call: its arguments, cancellation and progress reporting.</summary>
public sealed class McpCall
{
    private readonly XivMcpClient client;

    internal McpCall(XivMcpClient client, string id, string tool, JsonObject args, CancellationToken cancellation)
    {
        this.client = client;
        Id = id;
        Tool = tool;
        Args = args;
        Cancellation = cancellation;
    }

    public string Id { get; }
    public string Tool { get; }
    public JsonObject Args { get; }

    /// <summary>Signalled when the call is cancelled (pause_job / cancel_job / the client gave up). Stop cleanly, then return or throw.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>Adds a line to the job log when the call runs as a job step ("Room 2 of 5"). Cheap; call it on milestones, not every frame.</summary>
    public void Progress(string text) => client.Call(XivMcpClient.Prefix + "ReportProgress", Id, text);

    /// <summary>
    /// Asks whether this call may do something it declared right now, with a summary the player decides on ("Buy 3 Hi-Potions for 1,200
    /// gil"). Allowed capabilities return true at once and denied ones false; "ask" shows XIV MCP's approval window and waits for the
    /// player (declined after two minutes without an answer). Asking for a capability the tool didn't declare is refused and flagged.
    /// Call it right before the risky step, with the real numbers.
    /// </summary>
    public async Task<bool> RequestApprovalAsync(string capability, string summary)
    {
        var request = new JsonObject { ["capability"] = capability, ["summary"] = summary }.ToJsonString();
        var reply = client.Call(XivMcpClient.Prefix + "RequestApproval", Id, request);
        var id = reply["approvalId"]!.GetValue<string>();
        var state = reply["state"]!.GetValue<string>();
        while (state == "pending")
        {
            await Task.Delay(250, Cancellation);
            state = client.Call(XivMcpClient.Prefix + "GetApproval", id)["state"]!.GetValue<string>();
        }
        return state == "approved";
    }
}

/// <summary>A step of a job: a tool (built-in or any plugin's) and its arguments. Arguments may use "{{stepId.path}}" placeholders.</summary>
public sealed record McpJobStep(string Tool, object? Args = null, string? Id = null, string? Note = null);

public sealed class XivMcpClient : IDisposable
{
    internal const string Prefix = "XivMcp.";
    public const int ApiVersion = 2;

    private readonly IDalamudPluginInterface pi;
    private readonly string owner;
    private readonly Dictionary<string, (McpToolDefinition Def, Func<JsonObject, object?>? Sync, Func<McpCall, Task<object?>>? Long)> tools = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();
    private readonly ICallGateProvider<string, string, string, string> invokeGate;
    private readonly ICallGateProvider<string, object> cancelGate;
    private readonly ICallGateSubscriber<object> readySub;
    private readonly ICallGateSubscriber<object> disposingSub;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public XivMcpClient(IDalamudPluginInterface pluginInterface)
    {
        pi = pluginInterface;
        owner = pi.InternalName;

        invokeGate = pi.GetIpcProvider<string, string, string, string>($"{owner}.XivMcp.Invoke");
        invokeGate.RegisterFunc(OnInvoke);
        cancelGate = pi.GetIpcProvider<string, object>($"{owner}.XivMcp.Cancel");
        cancelGate.RegisterAction(OnCancel);

        readySub = pi.GetIpcSubscriber<object>(Prefix + "Ready");
        readySub.Subscribe(RegisterAll);
        disposingSub = pi.GetIpcSubscriber<object>(Prefix + "Disposing");
        disposingSub.Subscribe(OnXivMcpDisposing);
    }

    /// <summary>True while XIV MCP is loaded and speaks this API version.</summary>
    public bool IsAvailable
    {
        get
        {
            try { return pi.GetIpcSubscriber<int>(Prefix + "ApiVersion").InvokeFunc() >= ApiVersion; }
            catch { return false; }
        }
    }

    /// <summary>Raised with a message when XIV MCP rejects a registration (bad name, schema, …). Log it.</summary>
    public event Action<string>? RegistrationFailed;

    /// <summary>
    /// A tool that answers right away. XIV MCP calls <paramref name="handler"/> on the framework thread, so it may read game state
    /// directly; keep it short (no waiting). Return any JSON-serializable value (an anonymous object is ideal) or throw McpToolException.
    /// </summary>
    public void AddTool(McpToolDefinition definition, Func<JsonObject, object?> handler)
    {
        tools[definition.Name] = (definition, handler, null);
        Register(definition);
    }

    /// <summary>
    /// A tool that takes a while (seconds to hours), typically used as a job step. <paramref name="handler"/> starts on the framework
    /// thread; after its first await it continues on the thread pool, so use IFramework.RunOnFrameworkThread for game access there.
    /// Watch <see cref="McpCall.Cancellation"/> and stop cleanly (never mid-fight, never with a window half-filled).
    /// </summary>
    public void AddLongRunningTool(McpToolDefinition definition, Func<McpCall, Task<object?>> handler)
    {
        tools[definition.Name] = (definition, null, handler);
        Register(definition);
    }

    /// <summary>What the player set for one of your capabilities right now (Deny while your plugin is off, paused or suspended).</summary>
    public McpPermission CheckPermission(string capability) => Permission(Call(Prefix + "CheckPermission", owner, capability)["mode"]!.GetValue<string>());

    /// <summary>
    /// Where your plugin stands with the player (e.g. to show "Waiting for your approval in /xivmcp" in your own UI), and the setting of
    /// each capability you declared. Cheap; fine to call when your window opens, not every frame.
    /// </summary>
    public McpStatus GetStatus()
    {
        if (!IsAvailable) return new McpStatus(McpPluginState.Unavailable, null, new Dictionary<string, McpPermission>(), new Dictionary<string, McpPermission>());
        var reply = Call(Prefix + "GetStatus", owner);
        var state = reply["state"]?.GetValue<string>() switch
        {
            "undecided" => McpPluginState.Undecided,
            "kept_disabled" => McpPluginState.KeptDisabled,
            "enabled" => McpPluginState.Enabled,
            "awaiting_consent" => McpPluginState.AwaitingConsent,
            "suspended" => McpPluginState.Suspended,
            _ => McpPluginState.Unavailable,
        };
        var caps = new Dictionary<string, McpPermission>();
        foreach (var c in reply["capabilities"]?.AsArray() ?? [])
            if (c?["id"]?.GetValue<string>() is { } id) caps[id] = Permission(c["mode"]?.GetValue<string>());
        var toolModes = new Dictionary<string, McpPermission>();
        foreach (var t in reply["tools"]?.AsArray() ?? [])
            if (t?["name"]?.GetValue<string>() is { } name) toolModes[name] = Permission(t["mode"]?.GetValue<string>());
        return new McpStatus(state, reply["suspendReason"]?.GetValue<string>(), caps, toolModes);
    }

    private static McpPermission Permission(string? mode) => mode switch
    {
        "allow" => McpPermission.Allow,
        "ask" => McpPermission.Ask,
        _ => McpPermission.Deny,
    };

    public void RemoveTool(string name)
    {
        if (tools.Remove(name)) Call(Prefix + "UnregisterTool", owner, name);
    }

    // ------------------------------------------------------------------ jobs

    /// <summary>
    /// Starts a background job in XIV MCP: steps run one after another, each as long as it takes. Needs the player to have allowed this
    /// plugin in /xivmcp. Returns the job as JSON (id, state, …).
    /// </summary>
    public JsonObject StartJob(string name, params McpJobStep[] steps)
    {
        var job = new JsonObject
        {
            ["name"] = name,
            ["steps"] = new JsonArray(Array.ConvertAll(steps, s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id, ["tool"] = s.Tool, ["note"] = s.Note,
                ["args"] = s.Args is null ? new JsonObject() : JsonSerializer.SerializeToNode(s.Args, Json),
            })),
        };
        return Call(Prefix + "StartJob", owner, job.ToJsonString())["job"]!.AsObject();
    }

    public JsonObject GetJob(string id) => Call(Prefix + "GetJob", id)["job"]!.AsObject();

    /// <summary>Jobs this plugin started.</summary>
    public JsonArray ListJobs() => Call(Prefix + "ListJobs", owner)["jobs"]!.AsArray();

    public void PauseJob(string id) => Call(Prefix + "PauseJob", id);
    public void ResumeJob(string id) => Call(Prefix + "ResumeJob", id);
    public void CancelJob(string id) => Call(Prefix + "CancelJob", id);

    // ------------------------------------------------------------------ IPC plumbing

    private void Register(McpToolDefinition d)
    {
        if (!IsAvailable) return; // registered when XIV MCP sends Ready
        var def = new JsonObject
        {
            ["name"] = d.Name, ["description"] = d.Description, ["readOnly"] = d.ReadOnly, ["destructive"] = d.Destructive,
            ["capabilities"] = new JsonArray(Array.ConvertAll(d.Capabilities, c => (JsonNode)JsonValue.Create(c))),
            ["inputSchema"] = d.InputSchema is null ? null : JsonNode.Parse(d.InputSchema),
        };
        try { Call(Prefix + "RegisterTool", owner, def.ToJsonString()); }
        catch (McpToolException ex) { RegistrationFailed?.Invoke($"{d.Name}: {ex.Message}"); }
    }

    private void RegisterAll()
    {
        foreach (var (def, _, _) in tools.Values) Register(def);
    }

    private void OnXivMcpDisposing()
    {
        foreach (var cts in running.Values) cts.Cancel();
    }

    /// <summary>XIV MCP calls this on the framework thread. Replies {"result": …}, {"error": "…"} or {"pending": true}.</summary>
    private string OnInvoke(string callId, string tool, string argsJson)
    {
        try
        {
            if (!tools.TryGetValue(tool, out var t)) return Error($"{owner} has no tool '{tool}'.");
            var args = JsonNode.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson) as JsonObject ?? new JsonObject();
            if (t.Sync is not null) return new JsonObject { ["result"] = JsonSerializer.SerializeToNode(t.Sync(args), Json) }.ToJsonString();

            var cts = new CancellationTokenSource();
            running[callId] = cts;
            _ = RunLong(t.Long!, new McpCall(this, callId, tool, args, cts.Token), cts);
            return """{"pending":true}""";
        }
        catch (McpToolException ex) { return Error(ex.Message); }
        catch (Exception ex) { return Error($"{ex.GetType().Name}: {ex.Message}"); }
    }

    private async Task RunLong(Func<McpCall, Task<object?>> handler, McpCall call, CancellationTokenSource cts)
    {
        try
        {
            // Runs on the framework thread up to the handler's first await. Finishing before OnInvoke has replied is fine:
            // XIV MCP registers the call before invoking us.
            var result = await handler(call);
            Call(Prefix + "CompleteCall", call.Id, JsonSerializer.Serialize(result, Json));
        }
        catch (OperationCanceledException) { TryFail(call.Id, "Cancelled."); }
        catch (McpToolException ex) { TryFail(call.Id, ex.Message); }
        catch (Exception ex) { TryFail(call.Id, $"{ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            running.TryRemove(call.Id, out _);
            cts.Dispose();
        }
    }

    private void TryFail(string callId, string message)
    {
        try { Call(Prefix + "FailCall", callId, message); } catch { /* XIV MCP is gone or forgot the call */ }
    }

    private void OnCancel(string callId)
    {
        if (running.TryGetValue(callId, out var cts)) cts.Cancel();
    }

    private static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString();

    /// <summary>Calls one of XIV MCP's gates and unwraps its {"ok": …} reply; throws McpToolException with XIV MCP's message on errors.</summary>
    internal JsonObject Call(string gate, string a) => Unwrap(pi.GetIpcSubscriber<string, string>(gate).InvokeFunc(a));

    internal JsonObject Call(string gate, string a, string b) => Unwrap(pi.GetIpcSubscriber<string, string, string>(gate).InvokeFunc(a, b));

    private static JsonObject Unwrap(string reply)
    {
        var o = JsonNode.Parse(reply)?.AsObject() ?? throw new McpToolException("XIV MCP sent an empty reply.");
        return o["ok"]?.GetValue<bool>() == true ? o : throw new McpToolException(o["error"]?.ToString() ?? "XIV MCP reported an error.");
    }

    public void Dispose()
    {
        foreach (var cts in running.Values) cts.Cancel();
        try { if (IsAvailable) Call(Prefix + "UnregisterAll", owner); } catch { /* XIV MCP unloading too */ }
        readySub.Unsubscribe(RegisterAll);
        disposingSub.Unsubscribe(OnXivMcpDisposing);
        invokeGate.UnregisterFunc();
        cancelGate.UnregisterAction();
    }
}
