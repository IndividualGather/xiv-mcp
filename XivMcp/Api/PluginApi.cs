using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using XivMcp.Mcp;
using XivMcp.Permissions;
using XivMcp.Util;

namespace XivMcp.Api;

/// <summary>
/// Lets other Dalamud plugins offer MCP tools through XIV MCP and start background jobs, over Dalamud IPC (plugins can't share
/// types, so everything crossing the boundary is a string of JSON). See docs/plugin-api.md for the contract.
///
/// Third-party tools declare capabilities when they register; every call goes through <see cref="ToolGate"/>: the player's policy per
/// capability (allow / ask / deny), in-game approval, side-effect checks and the audit log.
///
/// XIV MCP provides (prefix "XivMcp."): ApiVersion, IsReady, ListCapabilities, RegisterTool, UnregisterTool, UnregisterAll,
/// CheckPermission, GetStatus, DeclareDependencies, RequestApproval, GetApproval, CompleteCall, FailCall, ReportProgress, StartJob,
/// GetJob, ListJobs, PauseJob, ResumeJob, CancelJob, and the messages Ready / Disposing. A plugin that registers tools provides "&lt;InternalName&gt;.XivMcp.Invoke"
/// (and optionally "&lt;InternalName&gt;.XivMcp.Cancel").
/// </summary>
internal sealed class PluginApi : IDisposable
{
    /// <summary>2: tools that aren't read-only must declare capabilities; permission checks and approvals.</summary>
    public const int Version = 2;
    private const string P = "XivMcp.";

    /// <summary>How long a cancelled call may take to report that it stopped (CompleteCall / FailCall) before XIV MCP gives up waiting.</summary>
    private static readonly TimeSpan CancelGrace = TimeSpan.FromMinutes(2);

    private readonly ToolRegistry registry;
    private readonly IPolicyStore policies;
    private readonly ToolGate gate;
    private readonly Func<JobManager?> jobs;
    private readonly List<Action> unregister = [];
    private readonly ConcurrentDictionary<string, PendingCall> pending = new();
    private readonly ConcurrentDictionary<string, Task<bool>> approvals = new();

    /// <summary>The tools each plugin's jobs use (XivMcp.DeclareDependencies), by plugin id. Gone when the plugin unloads.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<ToolDependency>> dependencies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last check per plugin: the settings window draws every frame, and checking reads Dalamud's plugin list.</summary>
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<DependencyStatus> Statuses)> checkedDependencies = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(2);
    private readonly ICallGateProvider<object> ready;
    private readonly ICallGateProvider<object> disposing;

    private sealed record PendingCall(McpTool Tool, TaskCompletionSource<JsonNode?> Completion, CancellationToken Cancellation)
    {
        public string Owner => Tool.Provider.Id;
    }

    public PluginApi(ToolRegistry registry, IPolicyStore policies, ToolGate gate, Func<JobManager?> jobs)
    {
        this.registry = registry;
        this.policies = policies;
        this.gate = gate;
        this.jobs = jobs;

        Func<int>(P + "ApiVersion", () => Version);
        Func<bool>(P + "IsReady", () => true);
        Func<string>(P + "ListCapabilities", () => Ok(new JsonObject
        {
            ["capabilities"] = new JsonArray(Capabilities.All.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c.Id, ["risk"] = c.Risk.ToString().ToLowerInvariant(), ["title"] = c.Title, ["description"] = c.Description,
                ["default"] = Capabilities.DefaultMode(c.Risk).ToString().ToLowerInvariant(),
            }).ToArray()),
        }));
        Func<string, string, string>(P + "RegisterTool", Guard2(RegisterTool));
        Func<string, string, string>(P + "UnregisterTool", Guard2((owner, name) => Ok(new JsonObject { ["removed"] = registry.Remove(name, Provider(owner)) })));
        Func<string, string>(P + "UnregisterAll", Guard1(owner =>
        {
            var provider = Provider(owner);
            dependencies.TryRemove(provider.Id, out _);
            checkedDependencies.TryRemove(provider.Id, out _);
            return Ok(new JsonObject { ["removed"] = registry.RemoveProvider(provider) });
        }));
        Func<string, string, string>(P + "DeclareDependencies", Guard2(DeclareDependencies));
        Func<string, string, string>(P + "CheckPermission", Guard2(CheckPermission));
        Func<string, string>(P + "GetStatus", Guard1(GetStatus));
        Func<string, string, string>(P + "RequestApproval", Guard2(RequestApproval));
        Func<string, string>(P + "GetApproval", Guard1(GetApproval));
        Func<string, string, string>(P + "CompleteCall", Guard2(CompleteCall));
        Func<string, string, string>(P + "FailCall", Guard2(FailCall));
        Func<string, string, string>(P + "ReportProgress", Guard2(ReportProgress));
        Func<string, string, string>(P + "StartJob", Guard2(StartJob));
        // A plugin sees and controls only its own jobs (the calling plugin, from the call stack).
        Func<string, string>(P + "GetJob", Guard1(id => Ok(new JsonObject { ["job"] = Job(m => JobManager.Describe(OwnJob(m, id))) })));
        Func<string, string>(P + "ListJobs", Guard1(_ =>
        {
            var mine = ClientName(CallingPlugin());
            return Ok(new JsonObject { ["jobs"] = Job(m => m.All().Where(j => j.Client == mine).Select(JobManager.Describe).ToList()) });
        }));
        Func<string, string>(P + "PauseJob", Guard1(id => { OwnJob(Manager(), id); Manager().Pause(id, "Paused by a plugin."); return Ok(new JsonObject()); }));
        Func<string, string>(P + "ResumeJob", Guard1(id => { OwnJob(Manager(), id); Manager().Resume(id); return Ok(new JsonObject()); }));
        Func<string, string>(P + "CancelJob", Guard1(id => { OwnJob(Manager(), id); Manager().Cancel(id); return Ok(new JsonObject()); }));

        ready = Svc.PluginInterface.GetIpcProvider<object>(P + "Ready");
        disposing = Svc.PluginInterface.GetIpcProvider<object>(P + "Disposing");
        Svc.PluginInterface.ActivePluginsChanged += OnPluginsChanged;
    }

    /// <summary>Raised with the plugin id after each successful registration.</summary>
    public event Action<string>? Registered;

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

    public sealed record PluginInfo(string InternalName, string DisplayName, bool Loaded, PluginPolicy Policy, List<McpTool> Tools,
                                    IReadOnlyList<DependencyStatus> Dependencies)
    {
        public IEnumerable<string> DeclaredCapabilities => Tools.SelectMany(t => t.Capabilities).Distinct();
    }

    /// <summary>Third-party plugins that registered tools, plus ones with a stored policy that currently have none.</summary>
    public List<PluginInfo> Plugins(Configuration config)
    {
        var byOwner = registry.All.Where(t => t.Provider.Trust == ProviderTrust.ThirdParty)
                              .GroupBy(t => t.Provider.Id, StringComparer.OrdinalIgnoreCase)
                              .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var known in config.ThirdPartyPolicies.Keys) byOwner.TryAdd(known, []);
        foreach (var declaring in dependencies.Keys) byOwner.TryAdd(declaring, []);
        return byOwner.Select(kv => new PluginInfo(kv.Key, DisplayName(kv.Key), PluginCompat.IsLoaded(kv.Key), policies.Get(kv.Key), kv.Value,
                                                   CheckedDependencies(kv.Key)))
                      .OrderBy(o => o.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ------------------------------------------------------------------ dependencies

    /// <summary>
    /// The tools a plugin's jobs use: {"tools": [{"tool": "navigate_to"}, {"tool": "x_scan", "plugin": "X", "name": "X",
    /// "repo": "https://…/repo.json" | "official", "minVersion": "1.2.0"}]}. Replaces the previous declaration; returns each tool's state.
    /// </summary>
    private string DeclareDependencies(string owner, string json)
    {
        var provider = Provider(owner);
        var declared = DependencyParser.Parse(json, IsBuiltIn);
        dependencies[provider.Id] = declared;
        checkedDependencies.TryRemove(provider.Id, out _);
        var statuses = CheckedDependencies(provider.Id);
        Svc.Log.Information($"[MCP] Plugin {owner} declared {declared.Count} tool(s) its jobs use" +
                            (statuses.Any(s => s.IsError) ? $"; missing: {string.Join(", ", statuses.Where(s => s.IsError).Select(s => s.Dependency.Tool))}" : ""));
        return Ok(new JsonObject { ["dependencies"] = DependenciesJson(statuses) });
    }

    /// <summary>XIV MCP's own tools (core and integrations): those a plugin can declare by name alone.</summary>
    private bool IsBuiltIn(string tool) =>
        registry.TryGet(tool, out var t) ? t.Provider.Trust != ProviderTrust.ThirdParty
            : PermissionCatalog.CoreTools.ContainsKey(tool) || XivMcp.Integrations.IntegrationCatalog.For(tool) is not null;

    private IReadOnlyList<DependencyStatus> CheckedDependencies(string pluginId)
    {
        if (!dependencies.TryGetValue(pluginId, out var declared) || declared.Count == 0) return [];
        if (checkedDependencies.TryGetValue(pluginId, out var cached) && DateTime.UtcNow - cached.At < RecheckAfter) return cached.Statuses;

        var installed = Svc.PluginInterface.InstalledPlugins
                           .GroupBy(p => p.InternalName, StringComparer.OrdinalIgnoreCase)
                           .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.IsLoaded).First(), StringComparer.OrdinalIgnoreCase);
        PluginPresence? Presence(string id) =>
            installed.TryGetValue(id, out var p) ? new PluginPresence(p.InternalName, p.Name, p.Version, p.IsLoaded) : null;
        string? Owner(string tool) => registry.TryGet(tool, out var t) && t.Provider.Trust == ProviderTrust.ThirdParty ? t.Provider.Id : null;
        bool Enabled(string id) => policies.Get(id) is { Enabled: true, AwaitingConsent: false, Suspended: false };

        var statuses = declared.Select(d => DependencyCheck.Check(d, Presence, Owner, Enabled)).ToList();
        checkedDependencies[pluginId] = (DateTime.UtcNow, statuses);
        return statuses;
    }

    private static JsonArray DependenciesJson(IEnumerable<DependencyStatus> statuses) => new(statuses.Select(s => (JsonNode)new JsonObject
    {
        ["tool"] = s.Dependency.Tool,
        ["plugin"] = s.Dependency.Plugin,
        ["state"] = StateId(s.State),
        ["message"] = s.Message,
        ["install"] = new JsonArray(s.Install.Select(i => (JsonNode)new JsonObject
        {
            ["plugin"] = i.InternalName, ["name"] = i.Name, ["repo"] = i.Repo, ["needed"] = i.Needed, ["installed"] = i.Installed,
            ["outdated"] = i.Outdated, ["reason"] = i.Reason, ["without"] = i.Without,
        }).ToArray()),
    }).ToArray());

    /// <summary>"ok", "needs_plugin", "plugin_missing", "plugin_not_loaded", "plugin_outdated", "tool_missing" or "not_enabled".</summary>
    private static string StateId(DependencyState s) => s switch
    {
        DependencyState.Ok => "ok",
        DependencyState.NeedsPlugin => "needs_plugin",
        DependencyState.PluginMissing => "plugin_missing",
        DependencyState.PluginNotLoaded => "plugin_not_loaded",
        DependencyState.PluginOutdated => "plugin_outdated",
        DependencyState.ToolMissing => "tool_missing",
        _ => "not_enabled",
    };

    // ------------------------------------------------------------------ registration

    private static ToolProvider Provider(string owner)
    {
        // The owner must be the plugin whose code makes the call: no plugin can act as another one.
        var id = PluginOwners.Validate(owner, Svc.PluginInterface.InstalledPlugins.Select(p => p.InternalName), IpcCaller.Find(), requireCaller: true);
        return new ToolProvider(id, DisplayName(id), ProviderTrust.ThirdParty);
    }

    private string RegisterTool(string owner, string definitionJson)
    {
        var provider = Provider(owner);
        var def = ToolDefinitionParser.Parse(definitionJson);
        var policy = policies.Get(provider.Id);
        var tool = new McpTool
        {
            Name = def.Name,
            Provider = provider,
            Capabilities = def.Capabilities,
            Description = $"{def.Description} [Provided by the third-party plugin {provider.DisplayName}.]",
            InputSchema = def.InputSchema.ToJsonString(),
            ReadOnly = def.ReadOnly,
            Destructive = def.Destructive,
            // Listed only once the player enabled the plugin; a suspended plugin stays listed so the assistant can explain why calls fail.
            Available = () => policy.Enabled && !policy.AwaitingConsent && PluginCompat.IsLoaded(owner),
            Handler = (args, ct) => Invoke(def.Name, args, ct),
        };
        // An enabled plugin whose registration grows (new tools or capabilities) waits for the player's consent again, before the new
        // tool is even listed.
        var mine = registry.All.Where(t => t.Provider == provider && t.Name != def.Name).Append(tool).ToList();
        var wasAwaiting = policy.AwaitingConsent;
        RegistrationReview.OnRegistered(policy, mine.Select(t => t.Name), mine.SelectMany(t => t.Capabilities));
        if (!registry.AddOrReplace(tool))
        {
            policy.AwaitingConsent = wasAwaiting;
            throw new ToolException(registry.TryGet(def.Name, out var other) && other.Provider.Trust != ProviderTrust.ThirdParty
                ? $"'{def.Name}' is an XIV MCP tool; pick another name (prefix it with your plugin's name)."
                : $"'{def.Name}' is already registered by another plugin; pick another name (prefix it with your plugin's name).");
        }
        if (policy.AwaitingConsent && !wasAwaiting)
        {
            policies.Save();
            Svc.Log.Information($"[MCP] {provider.DisplayName} changed its registration; its tools wait for the player's consent.");
        }
        Registered?.Invoke(provider.Id);
        Svc.Log.Information($"[MCP] Plugin {owner} registered tool {def.Name} [{string.Join(", ", def.Capabilities)}]" +
                            (policy.Enabled ? "" : " (not offered until the player enables the plugin in /xivmcp)"));
        return Ok(new JsonObject
        {
            ["name"] = def.Name,
            ["enabled"] = policy.Enabled,
            ["awaitingConsent"] = policy.AwaitingConsent,
            ["state"] = PluginStatus.Id(PluginStatus.Of(policy).State),
            ["capabilities"] = new JsonArray(def.Capabilities.Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c, ["mode"] = (policy.Enabled ? policy.ModeFor(c) : PolicyMode.Deny).ToString().ToLowerInvariant(),
            }).ToArray()),
        });
    }

    private static string DisplayName(string owner) =>
        Svc.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName.Equals(owner, StringComparison.OrdinalIgnoreCase))?.Name ?? owner;

    /// <summary>A plugin that unloads takes its tools and running calls with it.</summary>
    private void OnPluginsChanged(IActivePluginsChangedEventArgs args)
    {
        var gone = registry.All.Where(t => t.Provider.Trust == ProviderTrust.ThirdParty && !PluginCompat.IsLoaded(t.Provider.Id))
                               .Select(t => t.Provider).Distinct().ToList();
        foreach (var provider in gone)
        {
            var n = registry.RemoveProvider(provider);
            gate.Sessions.Clear(provider.Id);
            Svc.Log.Information($"[MCP] Plugin {provider.Id} unloaded; removed its {n} tool(s).");
        }
        // Declarations go with their plugin; the others are checked again, since a plugin they need may have come or gone.
        foreach (var id in dependencies.Keys.Where(id => !PluginCompat.IsLoaded(id)).ToList()) dependencies.TryRemove(id, out _);
        checkedDependencies.Clear();
        foreach (var (id, call) in pending.Where(p => !PluginCompat.IsLoaded(p.Value.Owner)).ToList())
            if (pending.TryRemove(id, out _)) call.Completion.TrySetException(new ToolException($"The {call.Tool.Provider.DisplayName} plugin was unloaded during the call."));
    }

    // ------------------------------------------------------------------ permissions

    /// <summary>What a capability is set to for the plugin right now: allow / ask / deny (deny while the plugin is off or suspended).</summary>
    private string CheckPermission(string owner, string capability)
    {
        var provider = Provider(owner);
        if (Capabilities.Find(capability) is null) throw new ToolException($"Unknown capability '{capability}'. See XivMcp.ListCapabilities.");
        var policy = policies.Get(provider.Id);
        return Ok(new JsonObject
        {
            ["capability"] = capability,
            ["mode"] = gate.Check(provider, capability).ToString().ToLowerInvariant(),
            ["enabled"] = policy.Enabled,
            ["suspended"] = policy.Suspended,
            ["state"] = PluginStatus.Id(PluginStatus.Of(policy).State),
        });
    }

    /// <summary>Where the plugin stands with the player (undecided, kept_disabled, enabled, awaiting_consent, suspended) and what each declared capability is set to.</summary>
    private string GetStatus(string owner)
    {
        var provider = Provider(owner);
        var policy = policies.Get(provider.Id);
        var status = PluginStatus.Of(policy);
        var mine = registry.All.Where(t => t.Provider == provider).ToList();
        return Ok(new JsonObject
        {
            ["state"] = PluginStatus.Id(status.State),
            ["canRun"] = status.CanRun,
            ["suspendReason"] = status.SuspendReason,
            // What each tool is set to as a whole: its own setting ("own"), else its strictest capability.
            ["tools"] = new JsonArray(mine.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name, ["mode"] = gate.CheckTool(t).ToString().ToLowerInvariant(), ["own"] = policy.ToolMode(t.Name) is not null,
            }).ToArray()),
            ["capabilities"] = new JsonArray(mine.SelectMany(t => t.Capabilities).Distinct().Select(c => (JsonNode)new JsonObject
            {
                ["id"] = c, ["mode"] = gate.Check(provider, c).ToString().ToLowerInvariant(),
            }).ToArray()),
            ["dependencies"] = DependenciesJson(CheckedDependencies(provider.Id)),
        });
    }

    /// <summary>
    /// During a call: asks whether the tool may now do something it declared ({"capability": "spend_gil", "summary": "Buy 3 potions for
    /// 1,200 gil"}). Returns an approval id; poll GetApproval until it is "approved" or "denied".
    /// </summary>
    private string RequestApproval(string callId, string requestJson)
    {
        if (!pending.TryGetValue(callId, out var call)) throw new ToolException($"No running call '{callId}'. Approvals can only be requested during a call.");
        var request = JsonNode.Parse(requestJson) as JsonObject ?? throw new ToolException("The request must be a JSON object { capability, summary }.");
        var capability = request["capability"]?.GetValue<string>() ?? throw new ToolException("The request needs 'capability'.");
        var summary = request["summary"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(summary)) throw new ToolException("The request needs a 'summary' the player can decide on, e.g. \"Buy 3 potions for 1,200 gil\".");
        summary = summary.Length > 200 ? summary[..200] : summary;

        var id = Guid.NewGuid().ToString("N")[..12];
        var task = gate.RequestRuntimeApprovalAsync(call.Tool, capability, summary, call.Cancellation);
        approvals[id] = task;
        return Ok(new JsonObject { ["approvalId"] = id, ["state"] = State(task) });
    }

    private string GetApproval(string approvalId)
    {
        if (!approvals.TryGetValue(approvalId, out var task)) throw new ToolException($"No approval '{approvalId}'.");
        var state = State(task);
        if (state != "pending") approvals.TryRemove(approvalId, out _);
        return Ok(new JsonObject { ["approvalId"] = approvalId, ["state"] = state });
    }

    private static string State(Task<bool> t) => !t.IsCompleted ? "pending" : t.IsCompletedSuccessfully && t.Result ? "approved" : "denied";

    // ------------------------------------------------------------------ calls

    /// <summary>
    /// Runs a plugin tool (the gate has already checked the policy): XIV MCP calls the plugin's Invoke gate on the framework thread,
    /// then waits for an async result if it replied "pending".
    /// </summary>
    private async Task<object?> Invoke(string toolName, ToolArgs args, CancellationToken ct)
    {
        if (!registry.TryGet(toolName, out var tool)) throw new ToolException($"'{toolName}' is no longer registered.");
        var owner = tool.Provider.Id;
        var callId = Guid.NewGuid().ToString("N");
        var call = new PendingCall(tool, new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously), ct);
        pending[callId] = call; // registered first: a plugin may complete the call (or ask for approval) from inside Invoke
        try
        {
            string reply;
            try
            {
                reply = await Game.Run(() => Svc.PluginInterface.GetIpcSubscriber<string, string, string, string>($"{owner}.XivMcp.Invoke")
                                                .InvokeFunc(callId, toolName, args.Raw.ToJsonString())).ConfigureAwait(false);
            }
            catch (IpcNotReadyError) { throw new ToolException($"{tool.Provider.DisplayName} registered '{toolName}' but doesn't provide {owner}.XivMcp.Invoke."); }
            catch (Exception ex) when (ex is not ToolException) { throw new ToolException($"{tool.Provider.DisplayName} failed: {ex.GetType().Name}: {ex.Message}"); }

            switch (PluginReply.Parse(reply, tool.Provider.DisplayName))
            {
                case PluginReply.Result r: return r.Value;
                case PluginReply.Failure f: throw new ToolException(f.Message);
            }

            // Long-running: wait for CompleteCall / FailCall. Cancelling (pause_job / cancel_job / client abort) asks the plugin to stop.
            using (ct.Register(() => _ = RequestCancel(owner, callId)))
            {
                try { return await call.Completion.Task.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    // Let the plugin wind down (e.g. leave a duty after the fight) before the job moves on.
                    try { await call.Completion.Task.WaitAsync(CancelGrace).ConfigureAwait(false); }
                    catch (TimeoutException) { Svc.Log.Warning($"[MCP] {owner} did not report the end of cancelled call {toolName} within {CancelGrace.TotalMinutes} min."); }
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
        if (!string.IsNullOrWhiteSpace(text)) jobs()?.StepProgress(call.Tool.Name, text.Length > 300 ? text[..300] : text);
        return Ok(new JsonObject());
    }

    // ------------------------------------------------------------------ jobs

    private static string ClientName(string owner) => $"plugin:{owner}";

    /// <summary>The plugin making the current IPC call (from the call stack); refused when it can't be told.</summary>
    private static string CallingPlugin() =>
        IpcCaller.Find() ?? throw new ToolException("XIV MCP could not tell which plugin made this call; call it from your plugin's own code.");

    /// <summary>The job, if it is the calling plugin's own (started by it); other jobs are not found.</summary>
    private static JobManager.Job OwnJob(JobManager manager, string id)
    {
        var job = manager.Get(id);
        if (job.Client != ClientName(CallingPlugin())) throw new ToolException($"No job '{id}' of yours.");
        return job;
    }

    private JobManager Manager() => jobs() ?? throw new ToolException("The job system is not running.");

    private JsonNode? Job(Func<JobManager, object> f) => JsonSerializer.SerializeToNode(f(Manager()), JobManager.JsonOptions);

    private string StartJob(string owner, string jobJson)
    {
        var provider = Provider(owner);
        var policy = policies.Get(provider.Id);
        if (!policy.Enabled || policy.Suspended || policy.AwaitingConsent)
            throw new ToolException($"{provider.DisplayName} can't start jobs: the player has not enabled it in /xivmcp → Third-party plugins" +
                                    (policy.Suspended ? " (it is suspended)." : policy.AwaitingConsent ? " (its registration changed and waits for the player's consent)." : "."));
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
        // Refuse up front what the job could never run: XIV MCP's tools that no plugin may use. (Each step is also checked against
        // the plugin's permissions when it runs.)
        var forbidden = steps.Select(s => s.Tool).Where(BuiltInCapabilities.NotForPlugins.Contains).Distinct().ToList();
        if (forbidden.Count > 0) throw new ToolException($"These tools are not available to plugins: {string.Join(", ", forbidden)}.");
        var job = Manager().Start($"{name} ({provider.DisplayName})", steps, ClientName(owner));
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
