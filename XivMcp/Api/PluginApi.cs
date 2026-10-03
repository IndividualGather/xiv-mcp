using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Api;

/// <summary>
/// Lets other Dalamud plugins offer MCP tools through XIV MCP and start background jobs, over Dalamud IPC (plugins can't share
/// types, so everything crossing the boundary is a string of JSON). See docs/plugin-api.md for the contract.
///
/// XIV MCP provides (prefix "XivMcp."): ApiVersion, IsReady, RegisterTool, UnregisterTool, UnregisterAll, CompleteCall, FailCall,
/// ReportProgress, StartJob, GetJob, ListJobs, PauseJob, ResumeJob, CancelJob, and the messages Ready / Disposing.
/// A plugin that registers tools provides "&lt;InternalName&gt;.XivMcp.Invoke" (and optionally "&lt;InternalName&gt;.XivMcp.Cancel").
/// </summary>
internal sealed partial class PluginApi : IDisposable
{
    public const int Version = 1;
    private const string P = "XivMcp.";

    /// <summary>How long a cancelled call may take to report that it stopped (CompleteCall / FailCall) before XIV MCP gives up waiting.</summary>
    private static readonly TimeSpan CancelGrace = TimeSpan.FromMinutes(2);

    private readonly ToolRegistry registry;
    private readonly Configuration config;
    private readonly Func<JobManager?> jobs;
    private readonly List<Action> unregister = [];
    private readonly ConcurrentDictionary<string, PendingCall> pending = new();
    private readonly ICallGateProvider<object> ready;
    private readonly ICallGateProvider<object> disposing;

    private sealed record PendingCall(string Owner, string Tool, TaskCompletionSource<JsonNode?> Completion);

    public PluginApi(ToolRegistry registry, Configuration config, Func<JobManager?> jobs)
    {
        this.registry = registry;
        this.config = config;
        this.jobs = jobs;

        Func<int>(P + "ApiVersion", () => Version);
        Func<bool>(P + "IsReady", () => true);
        Func<string, string, string>(P + "RegisterTool", Guard2(RegisterTool));
        Func<string, string, string>(P + "UnregisterTool", Guard2((owner, name) =>
            Ok(new JsonObject { ["removed"] = registry.Remove(name, owner) })));
        Func<string, string>(P + "UnregisterAll", Guard1(owner => Ok(new JsonObject { ["removed"] = registry.RemoveOwner(owner) })));
        Func<string, string, string>(P + "CompleteCall", Guard2(CompleteCall));
        Func<string, string, string>(P + "FailCall", Guard2(FailCall));
        Func<string, string, string>(P + "ReportProgress", Guard2(ReportProgress));
        Func<string, string, string>(P + "StartJob", Guard2(StartJob));
        Func<string, string>(P + "GetJob", Guard1(id => Ok(new JsonObject { ["job"] = Job(m => JobManager.Describe(m.Get(id))) })));
        Func<string, string>(P + "ListJobs", Guard1(owner => Ok(new JsonObject
        {
            ["jobs"] = Job(m => m.All().Where(j => string.IsNullOrEmpty(owner) || j.Client == ClientName(owner)).Select(JobManager.Describe).ToList()),
        })));
        Func<string, string>(P + "PauseJob", Guard1(id => { Manager().Pause(id, "Paused by a plugin."); return Ok(new JsonObject()); }));
        Func<string, string>(P + "ResumeJob", Guard1(id => { Manager().Resume(id); return Ok(new JsonObject()); }));
        Func<string, string>(P + "CancelJob", Guard1(id => { Manager().Cancel(id); return Ok(new JsonObject()); }));

        ready = Svc.PluginInterface.GetIpcProvider<object>(P + "Ready");
        disposing = Svc.PluginInterface.GetIpcProvider<object>(P + "Disposing");
        Svc.PluginInterface.ActivePluginsChanged += OnPluginsChanged;
    }

    /// <summary>Tells plugins that XIV MCP is (re)loaded, so they register their tools. Call once everything is set up.</summary>
    public void AnnounceReady()
    {
        try { ready.SendMessage(); }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] Plugin API Ready message failed: {ex.Message}"); }
    }

    public void Dispose()
    {
        try { disposing.SendMessage(); } catch { /* a subscriber threw */ }
        Svc.PluginInterface.ActivePluginsChanged -= OnPluginsChanged;
        foreach (var u in unregister) u();
        foreach (var call in pending.Values) call.Completion.TrySetException(new ToolException("XIV MCP is unloading."));
        pending.Clear();
    }

    // ------------------------------------------------------------------ what the settings window shows

    public sealed record OwnerInfo(string InternalName, string DisplayName, bool Loaded, bool Allowed, List<McpTool> Tools);

    /// <summary>Plugins that registered tools, plus allowed ones that currently have none.</summary>
    public List<OwnerInfo> Owners()
    {
        var byOwner = registry.All.Where(t => t.Owner is not null).GroupBy(t => t.Owner!, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var allowed in config.AllowedToolPlugins) byOwner.TryAdd(allowed, []);
        return byOwner.Select(kv => new OwnerInfo(kv.Key, DisplayName(kv.Key), PluginCompat.IsLoaded(kv.Key), config.AllowedToolPlugins.Contains(kv.Key), kv.Value))
                      .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ registration

    [GeneratedRegex("^[a-z][a-z0-9_]{2,63}$")]
    private static partial Regex ToolName();

    private string RegisterTool(string owner, string definitionJson)
    {
        RequireOwner(owner);
        var def = JsonNode.Parse(definitionJson) as JsonObject ?? throw new ToolException("The tool definition must be a JSON object.");
        var name = def["name"]?.GetValue<string>() ?? throw new ToolException("The tool definition needs 'name'.");
        if (!ToolName().IsMatch(name))
            throw new ToolException($"Invalid tool name '{name}': use 3-64 characters a-z, 0-9 and _, starting with a letter (e.g. \"myplugin_do_thing\").");
        var description = def["description"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(description)) throw new ToolException("The tool definition needs a 'description' (it is what the assistant reads to decide when to use the tool).");
        var schema = def["inputSchema"] ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        if (schema is not JsonObject so || so["type"]?.GetValue<string>() != "object")
            throw new ToolException("'inputSchema' must be a JSON schema object with \"type\": \"object\".");
        var readOnly = def["readOnly"]?.GetValue<bool>() ?? false;
        var destructive = def["destructive"]?.GetValue<bool>() ?? false;

        var tool = new McpTool
        {
            Name = name,
            Owner = owner,
            Description = $"{description.Trim()} [Provided by the {DisplayName(owner)} plugin.]",
            InputSchema = schema.ToJsonString(),
            ReadOnly = readOnly,
            Destructive = destructive,
            Available = () => config.AllowedToolPlugins.Contains(owner) && PluginCompat.IsLoaded(owner),
            Handler = (args, ct) => Invoke(owner, name, args, ct),
        };
        if (!registry.AddOrReplace(tool))
            throw new ToolException(registry.TryGet(name, out var other) && other.Owner is null
                ? $"'{name}' is a built-in XIV MCP tool; pick another name (prefix it with your plugin's name)."
                : $"'{name}' is already registered by another plugin; pick another name (prefix it with your plugin's name).");
        Svc.Log.Information($"[MCP] Plugin {owner} registered tool {name}" + (config.AllowedToolPlugins.Contains(owner) ? "" : " (not offered until the player allows the plugin in /xivmcp)"));
        return Ok(new JsonObject { ["name"] = name, ["allowed"] = config.AllowedToolPlugins.Contains(owner) });
    }

    private void RequireOwner(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ToolException("Pass your plugin's internal name as the owner.");
        if (owner.Equals("XivMcp", StringComparison.OrdinalIgnoreCase)) throw new ToolException("'XivMcp' can't be an owner.");
        if (!Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName.Equals(owner, StringComparison.OrdinalIgnoreCase) && p.IsLoaded))
            throw new ToolException($"No loaded plugin has the internal name '{owner}'. Pass your plugin's InternalName (IDalamudPluginInterface.InternalName).");
    }

    private static string DisplayName(string owner) =>
        Svc.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName.Equals(owner, StringComparison.OrdinalIgnoreCase))?.Name ?? owner;

    /// <summary>A plugin that unloads takes its tools and running calls with it.</summary>
    private void OnPluginsChanged(IActivePluginsChangedEventArgs args)
    {
        var gone = registry.All.Select(t => t.Owner).OfType<string>().Distinct().Where(o => !PluginCompat.IsLoaded(o)).ToList();
        foreach (var owner in gone)
        {
            var n = registry.RemoveOwner(owner);
            Svc.Log.Information($"[MCP] Plugin {owner} unloaded; removed its {n} tool(s).");
        }
        foreach (var (id, call) in pending.Where(p => !PluginCompat.IsLoaded(p.Value.Owner)).ToList())
            if (pending.TryRemove(id, out _)) call.Completion.TrySetException(new ToolException($"The {DisplayName(call.Owner)} plugin was unloaded during the call."));
    }

    // ------------------------------------------------------------------ calls

    /// <summary>Runs a plugin tool: XIV MCP calls the plugin's Invoke gate on the framework thread, then waits for an async result if it said "pending".</summary>
    private async Task<object?> Invoke(string owner, string tool, ToolArgs args, CancellationToken ct)
    {
        var callId = Guid.NewGuid().ToString("N");
        var argsJson = args.Raw?.ToJsonString() ?? "{}";
        var call = new PendingCall(owner, tool, new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously));
        pending[callId] = call; // registered first: a plugin may complete the call from inside Invoke
        try
        {
            string reply;
            try
            {
                reply = await Game.Run(() => Svc.PluginInterface.GetIpcSubscriber<string, string, string, string>($"{owner}.XivMcp.Invoke").InvokeFunc(callId, tool, argsJson))
                                  .ConfigureAwait(false);
            }
            catch (IpcNotReadyError) { throw new ToolException($"The {DisplayName(owner)} plugin registered '{tool}' but doesn't provide {owner}.XivMcp.Invoke."); }
            catch (Exception ex) when (ex is not ToolException) { throw new ToolException($"The {DisplayName(owner)} plugin failed: {ex.GetType().Name}: {ex.Message}"); }

            var envelope = ParseEnvelope(reply, owner);
            if (envelope["error"] is { } error) throw new ToolException(error.ToString());
            if (envelope["pending"]?.GetValue<bool>() != true) return envelope["result"]?.DeepClone();

            // Long-running: wait for CompleteCall / FailCall. Cancelling (pause_job / cancel_job / client abort) asks the plugin to stop.
            using (ct.Register(() => _ = RequestCancel(owner, callId)))
            {
                try { return await call.Completion.Task.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // Let the plugin wind down (e.g. leave a duty after the fight) before the job moves on.
                    try { await call.Completion.Task.WaitAsync(CancelGrace).ConfigureAwait(false); }
                    catch (TimeoutException) { Svc.Log.Warning($"[MCP] {owner} did not report the end of cancelled call {tool} within {CancelGrace.TotalMinutes} min."); }
                    catch { /* it reported a failure: fine, the call was cancelled anyway */ }
                    throw;
                }
            }
        }
        finally { pending.TryRemove(callId, out _); }
    }

    private static async Task RequestCancel(string owner, string callId)
    {
        try { await Game.Run(() => { Svc.PluginInterface.GetIpcSubscriber<string, object>($"{owner}.XivMcp.Cancel").InvokeAction(callId); return true; }).ConfigureAwait(false); }
        catch (IpcNotReadyError) { Svc.Log.Warning($"[MCP] {owner} has no {owner}.XivMcp.Cancel gate; its long-running call can't be cancelled."); }
        catch (Exception ex) { Svc.Log.Warning($"[MCP] {owner}.XivMcp.Cancel failed: {ex.Message}"); }
    }

    private static JsonObject ParseEnvelope(string? reply, string owner)
    {
        if (string.IsNullOrWhiteSpace(reply)) return new JsonObject { ["result"] = null };
        try
        {
            return JsonNode.Parse(reply) as JsonObject is { } o && (o.ContainsKey("result") || o.ContainsKey("error") || o.ContainsKey("pending"))
                ? o
                : throw new ToolException($"{owner} returned an invalid reply: expected {{\"result\": …}}, {{\"error\": \"…\"}} or {{\"pending\": true}}.");
        }
        catch (JsonException ex) { throw new ToolException($"{owner} returned invalid JSON: {ex.Message}"); }
    }

    private string CompleteCall(string callId, string resultJson)
    {
        if (!pending.TryGetValue(callId, out var call)) throw new ToolException($"No pending call '{callId}' (finished, cancelled or never pending).");
        JsonNode? result;
        try { result = string.IsNullOrWhiteSpace(resultJson) ? null : JsonNode.Parse(resultJson); }
        catch (JsonException ex) { throw new ToolException($"resultJson is not valid JSON: {ex.Message}"); }
        return Ok(new JsonObject { ["accepted"] = call.Completion.TrySetResult(result) });
    }

    private string FailCall(string callId, string message)
    {
        if (!pending.TryGetValue(callId, out var call)) throw new ToolException($"No pending call '{callId}' (finished, cancelled or never pending).");
        return Ok(new JsonObject { ["accepted"] = call.Completion.TrySetException(new ToolException(string.IsNullOrWhiteSpace(message) ? "The call failed." : message)) });
    }

    private string ReportProgress(string callId, string text)
    {
        if (!pending.TryGetValue(callId, out var call)) throw new ToolException($"No pending call '{callId}'.");
        if (!string.IsNullOrWhiteSpace(text)) jobs()?.StepProgress(call.Tool, text.Length > 300 ? text[..300] : text);
        return Ok(new JsonObject());
    }

    // ------------------------------------------------------------------ jobs

    private static string ClientName(string owner) => $"plugin:{owner}";

    private JobManager Manager() => jobs() ?? throw new ToolException("The job system is not running.");

    private JsonNode? Job(Func<JobManager, object> f) => JsonSerializer.SerializeToNode(f(Manager()), JobManager.JsonOptions);

    private string StartJob(string owner, string jobJson)
    {
        RequireOwner(owner);
        if (!config.AllowedToolPlugins.Contains(owner))
            throw new ToolException($"The player has not allowed {DisplayName(owner)} in /xivmcp → Permissions → Tools from other plugins.");
        var def = JsonNode.Parse(jobJson) as JsonObject ?? throw new ToolException("The job must be a JSON object { name, steps }.");
        var name = def["name"]?.GetValue<string>() ?? throw new ToolException("The job needs a 'name'.");
        if (def["steps"] is not JsonArray array || array.Count == 0) throw new ToolException("'steps' must be a non-empty array.");
        var steps = array.OfType<JsonObject>().Select(s => new JobManager.Step
        {
            Id = s["id"]?.ToString() ?? "",
            Tool = s["tool"]?.ToString() ?? throw new ToolException("Each step needs 'tool'."),
            Args = s["args"] as JsonObject is { } a ? (JsonObject)a.DeepClone() : [],
            Note = s["note"]?.ToString(),
        }).ToList();
        var job = Manager().Start(name, steps, ClientName(owner));
        return Ok(new JsonObject { ["job"] = JsonSerializer.SerializeToNode(JobManager.Describe(job), JobManager.JsonOptions) });
    }

    // ------------------------------------------------------------------ plumbing

    private static string Ok(JsonObject data)
    {
        data["ok"] = true;
        return data.ToJsonString();
    }

    private static string Error(string message) => new JsonObject { ["ok"] = false, ["error"] = message }.ToJsonString();

    /// <summary>Errors become {"ok": false, "error": "…"} instead of exceptions crossing into the calling plugin.</summary>
    private static Func<string, string> Guard1(Func<string, string> f) => a =>
    {
        try { return f(a ?? ""); }
        catch (ToolException ex) { return Error(ex.Message); }
        catch (JsonException ex) { return Error($"Invalid JSON: {ex.Message}"); }
        catch (Exception ex) { Svc.Log.Error(ex, "[MCP] Plugin API call failed"); return Error($"{ex.GetType().Name}: {ex.Message}"); }
    };

    private static Func<string, string, string> Guard2(Func<string, string, string> f) => (a, b) =>
    {
        try { return f(a ?? "", b ?? ""); }
        catch (ToolException ex) { return Error(ex.Message); }
        catch (JsonException ex) { return Error($"Invalid JSON: {ex.Message}"); }
        catch (Exception ex) { Svc.Log.Error(ex, "[MCP] Plugin API call failed"); return Error($"{ex.GetType().Name}: {ex.Message}"); }
    };

    private void Func<TRet>(string name, Func<TRet> f)
    {
        var p = Svc.PluginInterface.GetIpcProvider<TRet>(name);
        p.RegisterFunc(f);
        unregister.Add(p.UnregisterFunc);
    }

    private void Func<T1, TRet>(string name, Func<T1, TRet> f)
    {
        var p = Svc.PluginInterface.GetIpcProvider<T1, TRet>(name);
        p.RegisterFunc(f);
        unregister.Add(p.UnregisterFunc);
    }

    private void Func<T1, T2, TRet>(string name, Func<T1, T2, TRet> f)
    {
        var p = Svc.PluginInterface.GetIpcProvider<T1, T2, TRet>(name);
        p.RegisterFunc(f);
        unregister.Add(p.UnregisterFunc);
    }
}
