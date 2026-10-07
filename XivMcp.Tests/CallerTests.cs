using System.Text.Json.Nodes;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

/// <summary>Who calls a tool matters: a plugin's job may only use XIV MCP's tools within what the player allowed that plugin.</summary>
public class CallerTests
{
    private static readonly ToolProvider Plugin = new("HelloMcp", "Hello MCP", ProviderTrust.ThirdParty);

    private sealed class FakeGate : IApprovalGate
    {
        public ApprovalDecision Answer { get; set; } = ApprovalDecision.ApprovedOnce;
        public List<ApprovalRequest> Requests { get; } = [];
        public Task<ApprovalDecision> RequestAsync(ApprovalRequest request, CancellationToken ct) { Requests.Add(request); return Task.FromResult(Answer); }
    }

    private sealed class NoProbe : IGameProbe { public GameSnapshot? Capture() => null; }
    private sealed class NoNotifier : ISecurityNotifier { public void Flagged(ToolProvider p, string t, IReadOnlyList<SideEffect> e, bool s) { } }

    private readonly InMemoryPolicyStore store = new();
    private readonly FakeGate approvals = new();
    private readonly AuditLog audit = new(100);

    private ToolGate Gate() => new(store, approvals, new NoProbe(), audit, new NoNotifier());

    private static McpTool Core(string name, bool readOnly = false) => new()
    {
        Name = name, Description = "Does it.", ReadOnly = readOnly, Handler = (_, _) => Task.FromResult<object?>("ran"),
    };

    private static ToolArgs Args() => new(new JsonObject());

    private static readonly Caller FromPlugin = Caller.ForPlugin(Plugin);

    private void EnablePlugin(Action<PluginPolicy>? configure = null)
    {
        var p = store.Get(Plugin.Id);
        p.Enabled = true;
        configure?.Invoke(p);
    }

    [Fact]
    public async Task A_plugin_job_cannot_use_a_built_in_tool_beyond_the_plugin_s_own_permissions()
    {
        store.Core.Set("trading", Access.Write, PolicyMode.Allow); // the player lets their AI trade
        EnablePlugin(); // the plugin itself: defaults (trading is High risk: Ask), but deny it explicitly
        store.Get(Plugin.Id).Modes[Capabilities.TradeItems] = PolicyMode.Deny;
        var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Core("trade_with_player"), Args(), default, inJob: true, caller: FromPlugin));
        Assert.Contains("Hello MCP", ex.Message);
        Assert.Equal("blocked", audit.Recent()[0].Decision);
        Assert.Equal("plugin:HelloMcp", audit.Recent()[0].Caller);
    }

    [Fact]
    public async Task A_disabled_or_unapproved_plugin_s_job_can_use_nothing_that_changes_the_game()
    {
        store.Core.Set("game_navigation", Access.Write, PolicyMode.Allow);
        await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Core("navigate_to"), Args(), default, inJob: true, caller: FromPlugin));
        EnablePlugin(p => p.AwaitingConsent = true);
        await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Core("navigate_to"), Args(), default, inJob: true, caller: FromPlugin));
    }

    [Fact]
    public async Task The_stricter_of_both_decides_and_the_prompt_names_the_plugin()
    {
        store.Core.Set("game_navigation", Access.Write, PolicyMode.Allow);
        EnablePlugin(p => p.Modes[Capabilities.MoveCharacter] = PolicyMode.Ask);
        Assert.Equal("ran", await Gate().InvokeAsync(Core("navigate_to"), Args(), default, inJob: true, caller: FromPlugin));
        var asked = Assert.Single(approvals.Requests);
        Assert.Equal("Hello MCP", asked.Caller);
    }

    [Fact]
    public async Task Tools_a_plugin_job_may_never_use()
    {
        store.Core.Set("plugin_management", Access.Read, PolicyMode.Allow);
        store.Core.Set("jobs", Access.Write, PolicyMode.Allow);
        EnablePlugin(p => { foreach (var c in Capabilities.All) p.Modes[c.Id] = PolicyMode.Allow; });
        foreach (var tool in new[] { "get_plugin_config", "start_job", "take_screenshot", "request_spending_approval", "press_xivmcp_control" })
        {
            var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Core(tool), Args(), default, inJob: true, caller: FromPlugin));
            Assert.Contains("plugin", ex.Message);
        }
    }

    [Fact]
    public async Task A_plugin_s_session_or_always_answer_never_carries_over_to_the_assistant()
    {
        store.Core.Set("game_navigation", Access.Write, PolicyMode.Ask);
        EnablePlugin(p => p.Modes[Capabilities.MoveCharacter] = PolicyMode.Allow);
        approvals.Answer = ApprovalDecision.AlwaysAllow;
        await Gate().InvokeAsync(Core("navigate_to"), Args(), default, inJob: true, caller: FromPlugin);
        Assert.Null(store.Core.ToolMode("navigate_to")); // the player's own setting for their AI is untouched
        approvals.Requests.Clear();
        await Gate().InvokeAsync(Core("navigate_to"), Args(), default, caller: Caller.Assistant("claude-code"));
        Assert.Single(approvals.Requests); // the AI is still asked
        Assert.Equal("your AI assistant (claude-code)", approvals.Requests[0].Caller);
    }

    [Fact]
    public async Task Always_allow_is_not_kept_for_built_in_tools_that_can_do_critical_things()
    {
        store.Core.Set("saucy", Access.Write, PolicyMode.Ask);
        approvals.Answer = ApprovalDecision.AlwaysAllow;
        var tool = Core("present_fashion_report").WithProvider(new ToolProvider("Saucy", "Gold Saucer", ProviderTrust.Maintained), [Capabilities.DiscardItems]);
        await Gate().InvokeAsync(tool, Args(), default);
        Assert.Null(store.Core.ToolMode("present_fashion_report"));
    }

    [Fact]
    public async Task Calls_without_a_caller_are_the_assistant_s()
    {
        store.Core.Set("game_navigation", Access.Write, PolicyMode.Allow);
        await Gate().InvokeAsync(Core("navigate_to"), Args(), default);
        Assert.Equal("assistant", audit.Recent()[0].Caller);
    }
}

public class BuiltInCapabilityTests
{
    private static McpTool Core(string name) =>
        new() { Name = name, Description = "d", ReadOnly = name.StartsWith("get_"), Handler = (_, _) => Task.FromResult<object?>(null) };

    [Theory]
    [InlineData("trade_with_player", Capabilities.TradeItems)]
    [InlineData("buy_from_market_board", Capabilities.SpendGil)]
    [InlineData("switch_character", Capabilities.Login)]
    [InlineData("set_macro", Capabilities.ChatSend)]
    [InlineData("navigate_to", Capabilities.MoveCharacter)]
    [InlineData("move_items", Capabilities.MoveItems)]
    [InlineData("get_game_status", Capabilities.ReadGame)]
    public void Built_in_tools_say_what_they_can_do(string tool, string capability)
    {
        Assert.Contains(capability, BuiltInCapabilities.Of(Core(tool)));
    }

    [Fact]
    public void Every_core_tool_has_capabilities() =>
        Assert.All(PermissionCatalog.CoreTools.Keys, t => Assert.NotEmpty(BuiltInCapabilities.Of(Core(t))));
}

public class DestructiveToolTests
{
    [Theory]
    [InlineData("present_fashion_report")]
    [InlineData("complete_fashion_report")]
    public void Tools_that_can_throw_items_away_say_so(string tool)
    {
        Assert.Contains(Capabilities.DiscardItems, XivMcp.Integrations.IntegrationCatalog.For(tool)!.Tools[tool]);
    }
}
