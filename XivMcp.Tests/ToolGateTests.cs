using System.Text.Json.Nodes;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

public class ToolGateTests
{
    private static readonly ToolProvider Third = new("HelloMcp", "Hello MCP", ProviderTrust.ThirdParty);

    private sealed class FakeGate : IApprovalGate
    {
        public ApprovalDecision Answer { get; set; } = ApprovalDecision.ApprovedOnce;
        public List<ApprovalRequest> Requests { get; } = [];

        public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeProbe : IGameProbe
    {
        public GameSnapshot Current { get; set; } = new(true, 1, 129, 66, 1000, new Dictionary<uint, long>(), 50, 0);
        public GameSnapshot? Capture() => Current;
    }

    private sealed class FakeNotifier : ISecurityNotifier
    {
        public List<(ToolProvider Provider, string Tool, bool Suspended)> Flags { get; } = [];
        public void Flagged(ToolProvider provider, string tool, IReadOnlyList<SideEffect> effects, bool suspended) => Flags.Add((provider, tool, suspended));
    }

    private readonly InMemoryPolicyStore store = new();
    private readonly FakeGate gate = new();
    private readonly FakeProbe probe = new();
    private readonly FakeNotifier notifier = new();
    private readonly AuditLog audit = new(100);
    private DateTime now = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    private ToolGate Gate() => new(store, gate, probe, audit, notifier, () => now);

    private McpTool Tool(string[] caps, Func<Task<object?>>? body = null, ToolProvider? provider = null, bool readOnly = false) => new()
    {
        Name = "hellomcp_do",
        Description = "Does it.",
        Provider = provider ?? Third,
        ReadOnly = readOnly,
        Capabilities = [Capabilities.ReadGame, .. caps],
        Handler = (_, _) => body is null ? Task.FromResult<object?>("done") : body(),
    };

    private void Enable(Action<PluginPolicy>? configure = null)
    {
        var p = store.Get(Third.Id);
        p.Enabled = true;
        configure?.Invoke(p);
    }

    private static ToolArgs Args(string json = "{}") => new(JsonNode.Parse(json) as JsonObject);

    [Fact]
    public async Task Third_party_tools_are_refused_until_the_plugin_is_enabled()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Tool([]), Args(), default));
        Assert.Contains("/xivmcp", ex.Message);
        Assert.Equal("blocked", Assert.Single(audit.Recent()).Decision);
    }

    [Fact]
    public async Task Read_only_tools_of_an_enabled_plugin_run_without_asking_and_are_audited()
    {
        Enable();
        Assert.Equal("done", await Gate().InvokeAsync(Tool([], readOnly: true), Args("""{"a":1}"""), default));
        Assert.Empty(gate.Requests);
        var entry = Assert.Single(audit.Recent());
        Assert.Equal("allowed", entry.Decision);
        Assert.Equal("ok", entry.Outcome);
        Assert.False(entry.BuiltIn);
        Assert.Contains("\"a\"", entry.ArgsPreview);
    }

    [Fact]
    public async Task Ask_capabilities_prompt_once_per_call_with_all_of_them()
    {
        Enable();
        await Gate().InvokeAsync(Tool([Capabilities.MoveCharacter, Capabilities.SpendGil]), Args(), default);
        var request = Assert.Single(gate.Requests);
        Assert.Equal([Capabilities.MoveCharacter, Capabilities.SpendGil], request.Capabilities.Select(c => c.Id).Order());
        Assert.Equal("approved", audit.Recent()[0].Decision);
    }

    [Fact]
    public async Task Allowed_capabilities_are_not_asked()
    {
        Enable(p => p.Modes[Capabilities.MoveCharacter] = PolicyMode.Allow);
        await Gate().InvokeAsync(Tool([Capabilities.MoveCharacter]), Args(), default);
        Assert.Empty(gate.Requests);
    }

    [Fact]
    public async Task Denied_capability_blocks_the_call_without_running_it()
    {
        Enable(p => p.Modes[Capabilities.SpendGil] = PolicyMode.Deny);
        var ran = false;
        var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Tool([Capabilities.SpendGil], () => { ran = true; return Task.FromResult<object?>(null); }), Args(), default));
        Assert.False(ran);
        Assert.Contains("Spend gil", ex.Message);
        Assert.Equal("blocked", audit.Recent()[0].Decision);
    }

    [Fact]
    public async Task Declined_approval_blocks_the_call()
    {
        Enable();
        gate.Answer = ApprovalDecision.Denied;
        var ran = false;
        await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Tool([Capabilities.MoveCharacter], () => { ran = true; return Task.FromResult<object?>(null); }), Args(), default));
        Assert.False(ran);
        Assert.Equal("denied", audit.Recent()[0].Decision);
    }

    [Fact]
    public async Task Session_approval_is_remembered_per_tool_and_capability()
    {
        Enable();
        gate.Answer = ApprovalDecision.ApprovedForSession;
        var g = Gate();
        await g.InvokeAsync(Tool([Capabilities.MoveCharacter]), Args(), default);
        await g.InvokeAsync(Tool([Capabilities.MoveCharacter]), Args(), default);
        Assert.Single(gate.Requests);
        Assert.Equal("approved_session", audit.Recent()[0].Decision);

        g.Sessions.Clear(Third.Id);
        await g.InvokeAsync(Tool([Capabilities.MoveCharacter]), Args(), default);
        Assert.Equal(2, gate.Requests.Count);
    }

    [Fact]
    public async Task Always_allow_allows_just_that_tool_and_stops_asking()
    {
        var saves = 0;
        var savingStore = new InMemoryPolicyStore(save: () => saves++);
        var p = savingStore.Get(Third.Id);
        p.Enabled = true;
        gate.Answer = ApprovalDecision.AlwaysAllow;
        var g = new ToolGate(savingStore, gate, probe, audit, notifier, () => now);

        await g.InvokeAsync(Tool([Capabilities.MoveCharacter, Capabilities.SpendGil]), Args(), default);
        Assert.Equal(PolicyMode.Allow, p.ToolMode("hellomcp_do"));
        Assert.Equal(PolicyMode.Ask, p.ModeFor(Capabilities.SpendGil)); // the capability (and so other tools) keeps its setting
        Assert.True(saves > 0);
        Assert.Equal("approved_always", audit.Recent()[0].Decision);

        await g.InvokeAsync(Tool([Capabilities.MoveCharacter, Capabilities.SpendGil]), Args(), default);
        Assert.Single(gate.Requests);
    }

    [Fact]
    public async Task Always_allow_never_applies_to_tools_with_critical_capabilities()
    {
        Enable();
        gate.Answer = ApprovalDecision.AlwaysAllow;
        var g = Gate();
        await g.InvokeAsync(Tool([Capabilities.DiscardItems, Capabilities.GameUi]), Args(), default);
        Assert.Null(store.Get(Third.Id).ToolMode("hellomcp_do"));
        await g.InvokeAsync(Tool([Capabilities.DiscardItems, Capabilities.GameUi]), Args(), default);
        Assert.Equal(2, gate.Requests.Count);
    }

    [Fact]
    public async Task A_tool_set_to_deny_is_blocked_even_when_its_capabilities_are_allowed()
    {
        Enable(p => { p.Modes[Capabilities.GameUi] = PolicyMode.Allow; p.SetTool("hellomcp_do", PolicyMode.Deny); });
        var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Tool([Capabilities.GameUi]), Args(), default));
        Assert.Contains("hellomcp_do", ex.Message);
        Assert.Equal("blocked", audit.Recent()[0].Decision);
    }

    [Fact]
    public async Task A_tool_set_to_allow_runs_even_when_its_capability_is_denied_and_is_still_audited()
    {
        Enable(p => { p.Modes[Capabilities.SpendGil] = PolicyMode.Deny; p.SetTool("hellomcp_do", PolicyMode.Allow); });
        var tool = Tool([Capabilities.SpendGil], () => { probe.Current = probe.Current with { Gil = 10 }; return Task.FromResult<object?>("ok"); });
        Assert.Equal("ok", await Gate().InvokeAsync(tool, Args(), default));
        Assert.Empty(gate.Requests);
        var entry = audit.Recent()[0];
        Assert.Equal("spent_gil", Assert.Single(entry.SideEffects).Effect);
        Assert.False(entry.Flagged);
    }

    [Fact]
    public async Task A_tool_allowed_on_its_own_is_still_suspended_for_undeclared_side_effects()
    {
        Enable(p => p.SetTool("hellomcp_do", PolicyMode.Allow));
        var tool = Tool([Capabilities.GameUi], () => { probe.Current = probe.Current with { Gil = 10 }; return Task.FromResult<object?>("ok"); });
        await Gate().InvokeAsync(tool, Args(), default);
        Assert.True(store.Get(Third.Id).Suspended);
    }

    [Fact]
    public async Task A_read_only_tool_set_to_ask_asks_and_can_be_approved_for_the_session()
    {
        Enable(p => p.SetTool("hellomcp_do", PolicyMode.Ask));
        gate.Answer = ApprovalDecision.ApprovedForSession;
        var g = Gate();
        await g.InvokeAsync(Tool([], readOnly: true), Args(), default);
        await g.InvokeAsync(Tool([], readOnly: true), Args(), default);
        Assert.Equal(Capabilities.ReadGame, Assert.Single(Assert.Single(gate.Requests).Capabilities).Id);
    }

    [Fact]
    public async Task Always_allow_works_for_runtime_approvals_too()
    {
        Enable();
        gate.Answer = ApprovalDecision.AlwaysAllow;
        var g = Gate();
        var tool = Tool([Capabilities.SpendGil]);
        Assert.True(await g.RequestRuntimeApprovalAsync(tool, Capabilities.SpendGil, "Buy it", default));
        Assert.Equal(PolicyMode.Allow, store.Get(Third.Id).ToolMode("hellomcp_do"));
        Assert.True(await g.RequestRuntimeApprovalAsync(tool, Capabilities.SpendGil, "Buy it again", default));
        Assert.Single(gate.Requests);
    }

    [Fact]
    public async Task Critical_capabilities_ask_every_time_even_with_a_session_approval()
    {
        Enable();
        gate.Answer = ApprovalDecision.ApprovedForSession;
        var g = Gate();
        await g.InvokeAsync(Tool([Capabilities.DiscardItems]), Args(), default);
        await g.InvokeAsync(Tool([Capabilities.DiscardItems]), Args(), default);
        Assert.Equal(2, gate.Requests.Count);
    }

    [Fact]
    public async Task Plugins_waiting_for_consent_after_a_changed_registration_are_refused()
    {
        Enable(p => p.AwaitingConsent = true);
        var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Tool([], readOnly: true), Args(), default));
        Assert.Contains("changed", ex.Message);
        Assert.Equal(PolicyMode.Deny, Gate().Check(Third, Capabilities.ReadGame));
    }

    [Fact]
    public async Task Suspended_plugins_are_refused()
    {
        Enable(p => { p.Suspended = true; p.SuspendReason = "spent gil without declaring it"; });
        var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Tool([], readOnly: true), Args(), default));
        Assert.Contains("suspended", ex.Message);
    }

    [Fact]
    public async Task Undeclared_side_effect_of_a_short_call_suspends_the_plugin()
    {
        Enable();
        var tool = Tool([], async () => { probe.Current = probe.Current with { Gil = 10 }; await Task.Yield(); return "ok"; }, readOnly: true);
        Assert.Equal("ok", await Gate().InvokeAsync(tool, Args(), default));

        var policy = store.Get(Third.Id);
        Assert.True(policy.Suspended);
        Assert.Contains("gil", policy.SuspendReason);
        var entry = audit.Recent()[0];
        Assert.True(entry.Flagged);
        Assert.True(entry.SuspendedPlugin);
        Assert.Equal((Third, "hellomcp_do", true), Assert.Single(notifier.Flags));
    }

    [Fact]
    public async Task Undeclared_side_effect_of_a_long_call_also_suspends_but_says_it_may_have_been_the_player()
    {
        Enable();
        var tool = Tool([], async () =>
        {
            now = now.AddMinutes(30); // the player (or another plugin) may have spent gil meanwhile
            probe.Current = probe.Current with { Gil = 10 };
            await Task.Yield();
            return "ok";
        }, readOnly: true);
        await Gate().InvokeAsync(tool, Args(), default);

        // A plugin must not escape the check by running long: it is suspended too, and the reason says the player may have done it.
        var policy = store.Get(Third.Id);
        Assert.True(policy.Suspended);
        Assert.Contains("you", policy.SuspendReason);
        Assert.True(audit.Recent()[0].Flagged);
        Assert.True(Assert.Single(notifier.Flags).Suspended);
    }

    [Fact]
    public async Task Declared_side_effects_are_recorded_but_not_flagged()
    {
        Enable(p => p.Modes[Capabilities.SpendGil] = PolicyMode.Allow);
        var tool = Tool([Capabilities.SpendGil], () => { probe.Current = probe.Current with { Gil = 10 }; return Task.FromResult<object?>("ok"); });
        await Gate().InvokeAsync(tool, Args(), default);
        var entry = audit.Recent()[0];
        Assert.False(entry.Flagged);
        Assert.Equal("spent_gil", Assert.Single(entry.SideEffects).Effect);
        Assert.Empty(notifier.Flags);
    }

    [Fact]
    public async Task Side_effects_of_failing_calls_are_still_checked()
    {
        Enable();
        var tool = Tool([], () => { probe.Current = probe.Current with { Territory = 999 }; throw new ToolException("boom"); }, readOnly: true);
        await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(tool, Args(), default));
        var entry = audit.Recent()[0];
        Assert.Equal("error", entry.Outcome);
        Assert.Equal("boom", entry.Error);
        Assert.True(entry.Flagged);
    }

    [Fact]
    public async Task Cancelled_calls_are_audited_as_cancelled()
    {
        Enable();
        var tool = Tool([], () => throw new OperationCanceledException(), readOnly: true);
        await Assert.ThrowsAsync<OperationCanceledException>(() => Gate().InvokeAsync(tool, Args(), default));
        Assert.Equal("cancelled", audit.Recent()[0].Outcome);
    }

    // ---- runtime approval requests (XivMcp.RequestApproval)

    [Fact]
    public async Task Runtime_approval_for_a_declared_capability_asks_the_player()
    {
        Enable();
        Assert.True(await Gate().RequestRuntimeApprovalAsync(Tool([Capabilities.SpendGil]), Capabilities.SpendGil, "Buy 3 potions for 1,200 gil", default));
        var request = Assert.Single(gate.Requests);
        Assert.Equal("Buy 3 potions for 1,200 gil", request.Summary);
        Assert.Equal("approval", audit.Recent()[0].Kind);
    }

    [Fact]
    public async Task Runtime_approval_for_an_undeclared_capability_is_refused_and_flagged()
    {
        Enable();
        Assert.False(await Gate().RequestRuntimeApprovalAsync(Tool([Capabilities.GameUi]), Capabilities.SpendGil, "Buy stuff", default));
        Assert.Empty(gate.Requests);
        var entry = audit.Recent()[0];
        Assert.Equal("refused", entry.Decision);
        Assert.True(entry.Flagged);
    }

    [Fact]
    public async Task Runtime_approval_follows_allow_and_deny()
    {
        Enable(p => { p.Modes[Capabilities.SpendGil] = PolicyMode.Allow; p.Modes[Capabilities.ChatSend] = PolicyMode.Deny; });
        var g = Gate();
        var tool = Tool([Capabilities.SpendGil, Capabilities.ChatSend]);
        Assert.True(await g.RequestRuntimeApprovalAsync(tool, Capabilities.SpendGil, "x", default));
        Assert.False(await g.RequestRuntimeApprovalAsync(tool, Capabilities.ChatSend, "x", default));
        Assert.Empty(gate.Requests);
    }

    [Fact]
    public void Check_reports_the_effective_mode()
    {
        Enable(p => p.Modes[Capabilities.SpendGil] = PolicyMode.Deny);
        var g = Gate();
        Assert.Equal(PolicyMode.Deny, g.Check(Third, Capabilities.SpendGil));
        Assert.Equal(PolicyMode.Ask, g.Check(Third, Capabilities.MoveCharacter));
        store.Get(Third.Id).Enabled = false;
        Assert.Equal(PolicyMode.Deny, g.Check(Third, Capabilities.ReadGame));
    }
}

public class AuditLogTests
{
    private static AuditEntry Entry(string owner, int i) =>
        new() { Utc = DateTime.UtcNow.AddSeconds(i), ProviderId = owner, Tool = $"t{i}", Kind = "call", Decision = "allowed" };

    [Fact]
    public void Keeps_newest_first_and_drops_beyond_capacity()
    {
        var log = new AuditLog(3);
        for (var i = 0; i < 5; i++) log.Add(Entry("A", i));
        Assert.Equal(["t4", "t3", "t2"], log.Recent().Select(e => e.Tool));
    }

    [Fact]
    public void Restored_entries_are_listed_but_not_persisted_again()
    {
        var persisted = new List<AuditEntry>();
        var log = new AuditLog(10, persisted.Add);
        log.AddRestored(Entry("A", 1));
        log.AddRestored(Entry("A", 2));
        log.Add(Entry("A", 3));
        Assert.Equal(["t3", "t2", "t1"], log.Recent().Select(e => e.Tool));
        Assert.Equal("t3", Assert.Single(persisted).Tool);
    }

    [Fact]
    public void Filters_by_owner_and_persists_each_entry()
    {
        var persisted = new List<AuditEntry>();
        var log = new AuditLog(10, persisted.Add);
        log.Add(Entry("A", 1));
        log.Add(Entry("B", 2));
        Assert.Equal("t1", Assert.Single(log.Recent("A")).Tool);
        Assert.Equal(2, persisted.Count);
    }
}
