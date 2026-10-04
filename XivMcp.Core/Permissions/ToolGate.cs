using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;

namespace XivMcp.Permissions;

/// <summary>The player's answer. AlwaysAllow also sets the capabilities to Allow for that plugin (never critical ones).</summary>
public enum ApprovalDecision { Denied, ApprovedOnce, ApprovedForSession, AlwaysAllow }

/// <summary>A question to the player: may this plugin's tool do these things now?</summary>
/// <remarks><see cref="Area"/> is set for XIV MCP's own tools: the permission group (e.g. "Game &amp; navigation"); <see cref="Write"/> whether the call changes something.</remarks>
public sealed record ApprovalRequest(ToolProvider Provider, string Tool, IReadOnlyList<Capability> Capabilities, string Summary, string? ArgsPreview,
                                     string? Area = null, bool Write = true);

/// <summary>Asks the player (an approval window in game). Unanswered requests should end as <see cref="ApprovalDecision.Denied"/>.</summary>
public interface IApprovalGate
{
    Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct);
}

/// <summary>Captures what can be observed about the game; null if nothing can be read right now.</summary>
public interface IGameProbe
{
    GameSnapshot? Capture();
}

/// <summary>Tells the player that a plugin did (or asked for) something it didn't declare.</summary>
public interface ISecurityNotifier
{
    void Flagged(ToolProvider provider, string tool, IReadOnlyList<SideEffect> effects, bool suspended);
}

/// <summary>Session approvals: "approve for this session" per plugin, tool and capability, kept until the plugin reloads or the player clears them.</summary>
public sealed class SessionApprovals
{
    private readonly ConcurrentDictionary<string, byte> approved = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string provider, string tool, string capability) => $"{provider}|{tool}|{capability}";

    public bool Has(string provider, string tool, string capability) => approved.ContainsKey(Key(provider, tool, capability));

    public void Add(string provider, string tool, string capability) => approved[Key(provider, tool, capability)] = 0;

    public void Clear(string provider)
    {
        foreach (var k in approved.Keys.Where(k => k.StartsWith(provider + "|", StringComparison.OrdinalIgnoreCase)).ToList()) approved.TryRemove(k, out _);
    }

    public int Count(string provider) => approved.Keys.Count(k => k.StartsWith(provider + "|", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Runs tool calls through the permission system. Core and maintained tools pass straight through (their own permission switches apply
/// inside). Third-party tools must be enabled and not suspended; each declared capability is checked against the plugin's policy
/// (deny blocks, ask prompts the player once per call), the game is snapshotted before and after, and the call is audited. A call
/// that did something it didn't declare is flagged; if it was short (so the change was most likely the call's own doing), the plugin
/// is suspended until the player lifts it.
/// </summary>
public sealed class ToolGate(IPolicyStore store, IApprovalGate gate, IGameProbe probe, AuditLog audit, ISecurityNotifier notifier, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);

    /// <summary>Calls shorter than this suspend their plugin on an undeclared side effect; longer ones are only flagged (the player may have acted meanwhile).</summary>
    public TimeSpan SuspendWindow { get; init; } = TimeSpan.FromMinutes(2);

    public SessionApprovals Sessions { get; } = new();

    public AuditLog Audit => audit;

    /// <summary>The mode a capability has for a provider right now (disabled or suspended plugins: Deny).</summary>
    public PolicyMode Check(ToolProvider provider, string capability)
    {
        if (provider.Trust != ProviderTrust.ThirdParty) return PolicyMode.Allow;
        var policy = store.Get(provider.Id);
        return !policy.Enabled || policy.Suspended || policy.AwaitingConsent ? PolicyMode.Deny : policy.ModeFor(capability);
    }

    /// <summary>The mode a capability has for one of the provider's tools right now: the tool's own setting first, then the capability's.</summary>
    public PolicyMode Check(ToolProvider provider, string capability, McpTool tool)
    {
        if (provider.Trust != ProviderTrust.ThirdParty) return PolicyMode.Allow;
        var policy = store.Get(provider.Id);
        return !policy.Enabled || policy.Suspended || policy.AwaitingConsent ? PolicyMode.Deny : policy.RuntimeMode(tool, capability);
    }

    /// <summary>The mode of a whole call to one of the provider's tools right now (disabled or suspended plugins: Deny).</summary>
    public PolicyMode CheckTool(McpTool tool)
    {
        if (tool.Provider.Trust != ProviderTrust.ThirdParty) return CheckBuiltIn(tool);
        var policy = store.Get(tool.Provider.Id);
        return !policy.Enabled || policy.Suspended || policy.AwaitingConsent ? PolicyMode.Deny : policy.ModeForTool(tool);
    }

    public async Task<object?> InvokeAsync(McpTool tool, ToolArgs args, CancellationToken ct, bool inJob = false)
    {
        if (tool.Provider.Trust != ProviderTrust.ThirdParty) return await InvokeBuiltInAsync(tool, args, ct, inJob).ConfigureAwait(false);

        var provider = tool.Provider;
        var policy = store.Get(provider.Id);
        var preview = Preview(args.Raw.ToJsonString());
        var caps = tool.Capabilities.Select(Capabilities.Find).OfType<Capability>().ToList();
        var capIds = caps.Select(c => c.Id).ToList();
        AuditEntry Entry(string decision) => new()
        {
            Utc = now(), ProviderId = provider.Id, ProviderName = provider.DisplayName, Tool = tool.Name, ArgsPreview = preview,
            Capabilities = capIds, Decision = decision, InJob = inJob,
        };

        if (!policy.Enabled)
        {
            audit.Add(Entry("blocked") with { Error = "Plugin not allowed." });
            throw new ToolException($"{provider.DisplayName}'s tools aren't allowed yet. The player can allow the plugin in /xivmcp → Third-party plugins.");
        }
        if (policy.AwaitingConsent)
        {
            audit.Add(Entry("blocked") with { Error = "Registration changed; waiting for the player's consent." });
            throw new ToolException($"{provider.DisplayName} changed its registration (new tools or capabilities) and waits for the player to consent again in /xivmcp → Third-party plugins.");
        }
        if (policy.Suspended)
        {
            audit.Add(Entry("blocked") with { Error = "Plugin suspended." });
            throw new ToolException($"{provider.DisplayName} is suspended by XIV MCP ({policy.SuspendReason}). Only the player can lift that, in /xivmcp → Third-party plugins.");
        }
        var mode = policy.ModeForTool(tool);
        if (mode == PolicyMode.Deny)
        {
            var own = policy.ToolMode(tool.Name) == PolicyMode.Deny;
            var denied = caps.FirstOrDefault(c => policy.ModeFor(c.Id) == PolicyMode.Deny && PluginPolicy.Sections(tool).Contains(c.Id));
            audit.Add(Entry("blocked") with { Error = own ? "Tool denied." : denied is not null ? $"{denied.Title} is denied." : "Unknown capability." });
            throw new ToolException(own
                ? $"{tool.Name} is set to Deny for {provider.DisplayName} in /xivmcp, so it can't run. The player can change it there."
                : $"\"{denied?.Title ?? "An unknown capability"}\" is blocked for {provider.DisplayName} in /xivmcp, so {tool.Name} can't run.");
        }

        var decision = "allowed";
        if (mode == PolicyMode.Ask)
        {
            if (SessionCovers(provider, tool)) decision = "approved_session";
            else
            {
                var asked = PluginPolicy.Sections(tool).Select(Capabilities.Find).OfType<Capability>().ToList();
                var answer = await gate.RequestAsync(new ApprovalRequest(provider, tool.Name, asked, FirstSentence(tool.Description), preview, Write: !tool.ReadOnly), ct)
                                       .ConfigureAwait(false);
                if (answer == ApprovalDecision.Denied)
                {
                    audit.Add(Entry("denied"));
                    throw new ToolException($"The player declined {tool.Name} ({string.Join(", ", asked.Select(c => c.Title))}).");
                }
                Remember(provider, tool, WholeCall, answer, policy);
                decision = Decision(answer);
            }
        }

        var before = probe.Capture();
        var started = now();
        var watch = Stopwatch.StartNew();
        string outcome = "ok";
        string? error = null;
        try
        {
            return await tool.Handler(args, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch (Exception ex) { outcome = "error"; error = ex.Message; throw; }
        finally
        {
            var duration = now() - started;
            var after = probe.Capture();
            var effects = before is not null && after is not null ? SideEffectAnalyzer.Analyze(before, after, capIds) : [];
            var undeclared = effects.Where(e => e.Undeclared).ToList();
            var suspend = undeclared.Count > 0 && duration < SuspendWindow;
            if (suspend)
            {
                policy.Suspend($"{tool.Name}: {string.Join(" ", undeclared.Select(e => e.Detail))} (not declared)", now());
                Sessions.Clear(provider.Id);
                store.Save();
            }
            audit.Add(Entry(decision) with
            {
                Outcome = outcome, Error = error, DurationMs = (long)Math.Max(duration.TotalMilliseconds, watch.Elapsed.TotalMilliseconds),
                SideEffects = effects, Flagged = undeclared.Count > 0, SuspendedPlugin = suspend,
            });
            if (undeclared.Count > 0) notifier.Flagged(provider, tool.Name, undeclared, suspend);
        }
    }

    /// <summary>
    /// A plugin asks, during a call, whether it may do something now (e.g. "Buy 3 potions for 1,200 gil"). The capability must be one
    /// the tool declared; asking for anything else is refused and flagged.
    /// </summary>
    public async Task<bool> RequestRuntimeApprovalAsync(McpTool tool, string capabilityId, string summary, CancellationToken ct)
    {
        var provider = tool.Provider;
        if (provider.Trust != ProviderTrust.ThirdParty) return true;
        var cap = Capabilities.Find(capabilityId);
        AuditEntry Entry(string decision) => new()
        {
            Utc = now(), ProviderId = provider.Id, ProviderName = provider.DisplayName, Tool = tool.Name, Kind = "approval",
            Summary = summary, Capabilities = [capabilityId], Decision = decision,
        };

        if (cap is null || !tool.Capabilities.Contains(capabilityId))
        {
            audit.Add(Entry("refused") with { Flagged = true, Error = cap is null ? "Unknown capability." : "Not declared by the tool." });
            notifier.Flagged(provider, tool.Name, [new SideEffect("undeclared_request", $"Asked to \"{cap?.Title ?? capabilityId}\": {summary}", [capabilityId], true)], false);
            return false;
        }
        switch (Check(provider, capabilityId, tool))
        {
            case PolicyMode.Deny:
                audit.Add(Entry("blocked"));
                return false;
            case PolicyMode.Allow:
                audit.Add(Entry("allowed"));
                return true;
        }
        if (cap.Risk != RiskLevel.Critical && (SessionCovers(provider, tool) || Sessions.Has(provider.Id, tool.Name, cap.Id)))
        {
            audit.Add(Entry("approved_session"));
            return true;
        }
        var answer = await gate.RequestAsync(new ApprovalRequest(provider, tool.Name, [cap], summary, null), ct).ConfigureAwait(false);
        if (answer != ApprovalDecision.Denied) Remember(provider, tool, cap.Id, answer, store.Get(provider.Id));
        audit.Add(Entry(Decision(answer)));
        return answer != ApprovalDecision.Denied;
    }

    /// <summary>
    /// Whether a tool is offered to the assistant at all: false for XIV MCP's own tools whose group the player turned off. Third-party
    /// tools and tools missing from the catalog aren't affected (they have their own switches and fail-safes).
    /// </summary>
    public bool IsListed(McpTool tool) =>
        tool.Provider.Trust == ProviderTrust.ThirdParty || PermissionCatalog.GroupOf(tool) is not { } g || store.Core.IsGroupOn(g.Id);

    /// <summary>
    /// The effective mode of one of XIV MCP's own tools: its group's read or write setting. Tools missing from the catalog fail safe
    /// (reads allowed, writes asked).
    /// </summary>
    public PolicyMode CheckBuiltIn(McpTool tool)
    {
        var access = PermissionCatalog.AccessOf(tool);
        if (store.Core.ToolMode(tool.Name) is { } own) return own; // the player set this tool on its own
        return PermissionCatalog.GroupOf(tool) is { } g ? store.Core.ModeFor(g.Id, access) : access == Access.Read ? PolicyMode.Allow : PolicyMode.Ask;
    }

    /// <summary>
    /// XIV MCP's own tools (core and maintained integrations): the group's read or write setting decides. Deny blocks before the tool
    /// runs, Ask shows the approval window (session approvals and "always allow" work as for third-party tools). Calls that change
    /// something, and every call that wasn't simply allowed, are audited.
    /// </summary>
    private async Task<object?> InvokeBuiltInAsync(McpTool tool, ToolArgs args, CancellationToken ct, bool inJob)
    {
        var group = PermissionCatalog.GroupOf(tool);
        var access = PermissionCatalog.AccessOf(tool);
        var area = group?.Title ?? "Unlisted XIV MCP tool";
        var sessionKey = $"{group?.Id ?? "unlisted"}:{access.ToString().ToLowerInvariant()}";
        var mode = CheckBuiltIn(tool);
        AuditEntry Entry(string decision) => new()
        {
            Utc = now(), ProviderId = tool.Provider.Id, ProviderName = tool.Provider.DisplayName, Tool = tool.Name,
            ArgsPreview = Preview(args.Raw.ToJsonString()), Capabilities = [sessionKey], Decision = decision, InJob = inJob, BuiltIn = true,
        };

        if (!IsListed(tool))
        {
            audit.Add(Entry("blocked") with { Error = $"{area} is turned off." });
            throw new ToolException($"{area} is turned off in /xivmcp → Modules, so {tool.Name} isn't available. The player can turn it on there.");
        }

        if (mode == PolicyMode.Deny)
        {
            audit.Add(Entry("blocked") with { Error = $"{area} {(access == Access.Read ? "reading" : "changes")} denied." });
            throw new ToolException(store.Core.ToolMode(tool.Name) is not null
                ? $"{tool.Name} is set to Deny in /xivmcp → Modules ({area}), so it can't run. The player can change it there."
                : $"{area} is set to Deny for {(access == Access.Read ? "reading" : "changes")}, so {tool.Name} can't run. The player can change it in /xivmcp → Modules.");
        }

        var decision = "allowed";
        if (mode == PolicyMode.Ask)
        {
            if (Sessions.Has(tool.Provider.Id, tool.Name, sessionKey)) decision = "approved_session";
            else
            {
                var caps = tool.Capabilities.Select(Capabilities.Find).OfType<Capability>().Where(c => c.Id != Capabilities.ReadGame).ToList();
                var answer = await gate.RequestAsync(new ApprovalRequest(tool.Provider, tool.Name, caps, ToolSummaries.For(tool.Name) ?? FirstSentence(tool.Description),
                    Preview(args.Raw.ToJsonString()), area, access == Access.Write), ct).ConfigureAwait(false);
                if (answer == ApprovalDecision.Denied)
                {
                    audit.Add(Entry("denied"));
                    throw new ToolException($"The player declined {tool.Name}.");
                }
                if (answer == ApprovalDecision.ApprovedForSession) Sessions.Add(tool.Provider.Id, tool.Name, sessionKey);
                if (answer == ApprovalDecision.AlwaysAllow)
                {
                    // Just this tool: the rest of its group keeps its setting.
                    store.Core.SetTool(tool.Name, PolicyMode.Allow);
                    store.Save();
                }
                decision = Decision(answer);
            }
        }

        // Plain allowed reads are the bulk of all calls; they aren't audited.
        if (decision == "allowed" && access == Access.Read) return await tool.Handler(args, ct).ConfigureAwait(false);

        var started = now();
        var outcome = "ok";
        string? error = null;
        try { return await tool.Handler(args, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        catch (Exception ex) { outcome = "error"; error = ex.Message; throw; }
        finally
        {
            audit.Add(Entry(decision) with { Outcome = outcome, Error = error, DurationMs = (long)(now() - started).TotalMilliseconds });
        }
    }

    /// <summary>The session key for approving a whole call (rather than one request during it).</summary>
    private const string WholeCall = "*call";

    /// <summary>
    /// Keeps a "for this session" answer (the whole call, or one capability during it) or an "always" answer (the tool on its own is set
    /// to Allow; its capabilities and other tools keep their settings). Never for tools with a critical capability: those ask every time.
    /// </summary>
    private void Remember(ToolProvider provider, McpTool tool, string scope, ApprovalDecision answer, PluginPolicy policy)
    {
        if (PluginPolicy.IsCritical(tool)) return;
        if (answer == ApprovalDecision.ApprovedForSession) Sessions.Add(provider.Id, tool.Name, scope);
        else if (answer == ApprovalDecision.AlwaysAllow)
        {
            policy.SetTool(tool.Name, PolicyMode.Allow);
            store.Save();
        }
    }

    private static string Decision(ApprovalDecision answer) => answer switch
    {
        ApprovalDecision.Denied => "denied",
        ApprovalDecision.ApprovedForSession => "approved_session",
        ApprovalDecision.AlwaysAllow => "approved_always",
        _ => "approved",
    };

    private static string FirstSentence(string text) => ToolText.Truncate(ToolText.FirstSentence(text), 160);

    private bool SessionCovers(ToolProvider provider, McpTool tool) => !PluginPolicy.IsCritical(tool) && Sessions.Has(provider.Id, tool.Name, WholeCall);

    private static string Preview(string json) => json.Length <= 300 ? json : json[..297] + "...";
}
