using System.Text.Json.Nodes;
using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

public class PermissionCatalogTests
{
    [Fact]
    public void Group_ids_are_unique_and_described()
    {
        Assert.Equal(PermissionCatalog.Groups.Count, PermissionCatalog.Groups.Select(g => g.Id).Distinct().Count());
        Assert.All(PermissionCatalog.Groups, g =>
        {
            Assert.Matches("^[a-z_]+$", g.Id);
            Assert.False(string.IsNullOrWhiteSpace(g.Title));
            Assert.False(string.IsNullOrWhiteSpace(g.Description));
            Assert.True(g.HasRead || g.HasWrite);
        });
    }

    [Fact]
    public void Every_core_tool_maps_to_an_existing_group() =>
        Assert.All(PermissionCatalog.CoreTools, kv => Assert.NotNull(PermissionCatalog.Find(kv.Value)));

    [Fact]
    public void Every_integration_has_its_own_group_with_its_plugin()
    {
        foreach (var i in IntegrationCatalog.All)
        {
            var g = PermissionCatalog.ForIntegration(i.PluginId);
            Assert.NotNull(g);
            Assert.Equal(i.PluginId, g!.PluginId);
        }
    }

    [Fact]
    public void Integration_tools_are_not_also_core_tools() =>
        Assert.DoesNotContain(IntegrationCatalog.All.SelectMany(i => i.Tools.Keys), PermissionCatalog.CoreTools.ContainsKey);

    [Fact]
    public void Defaults_match_the_old_switches()
    {
        Assert.Equal(PolicyMode.Allow, PermissionCatalog.Find("game_data")!.DefaultRead);
        Assert.False(PermissionCatalog.Find("game_data")!.HasWrite);
        Assert.Equal(PolicyMode.Deny, PermissionCatalog.Find("game_navigation")!.DefaultWrite);
        Assert.Equal(PolicyMode.Allow, PermissionCatalog.Find("game_navigation")!.DefaultRead);
        Assert.Equal(PolicyMode.Deny, PermissionCatalog.Find("online")!.DefaultRead);
        Assert.Equal(PolicyMode.Deny, PermissionCatalog.Find("plugin_management")!.DefaultRead);
        Assert.Equal(PolicyMode.Allow, PermissionCatalog.Find("jobs")!.DefaultWrite);
        Assert.All(IntegrationCatalog.All, i => Assert.Equal(PolicyMode.Deny, PermissionCatalog.ForIntegration(i.PluginId)!.DefaultWrite));
    }

    [Fact]
    public void Tools_find_their_group_by_provider()
    {
        var core = new McpTool { Name = "navigate_to", Description = "d", Handler = (_, _) => Task.FromResult<object?>(null) };
        Assert.Equal("game_navigation", PermissionCatalog.GroupOf(core)!.Id);
        var duty = IntegrationCatalog.Apply([new McpTool { Name = "run_duty", Description = "d", Handler = (_, _) => Task.FromResult<object?>(null) }], _ => true).Single();
        Assert.Equal("AutoDuty", PermissionCatalog.GroupOf(duty)!.PluginId);
        Assert.Null(PermissionCatalog.GroupOf(WithName("no_such_tool")));
    }

    private static McpTool WithName(string name) => new() { Name = name, Description = "d", Handler = (_, _) => Task.FromResult<object?>(null) };
}

public class CorePolicyTests
{
    [Fact]
    public void Unset_modes_use_the_group_defaults()
    {
        var p = new CorePolicy();
        Assert.Equal(PolicyMode.Allow, p.ModeFor("game_navigation", Access.Read));
        Assert.Equal(PolicyMode.Deny, p.ModeFor("game_navigation", Access.Write));
    }

    [Fact]
    public void Set_modes_win_and_access_a_group_lacks_is_denied()
    {
        var p = new CorePolicy();
        p.Set("game_navigation", Access.Write, PolicyMode.Ask);
        Assert.Equal(PolicyMode.Ask, p.ModeFor("game_navigation", Access.Write));
        Assert.Equal(PolicyMode.Deny, p.ModeFor("game_data", Access.Write));
        Assert.Equal(PolicyMode.Deny, p.ModeFor("no_such_group", Access.Read));
    }

    [Fact]
    public void Legacy_switches_migrate_to_write_modes_and_integrations()
    {
        var p = CorePolicy.FromLegacy(new LegacySwitches(GameNavigation: true, ItemsRetainers: false, MarketPurchases: true, CraftingGathering: true,
                                                         UiEditing: false, OnlineData: true, PluginManagement: false));
        Assert.Equal(PolicyMode.Allow, p.ModeFor("game_navigation", Access.Write));
        Assert.Equal(PolicyMode.Deny, p.ModeFor("items_retainers", Access.Write));
        Assert.Equal(PolicyMode.Allow, p.ModeFor("market", Access.Write));
        Assert.Equal(PolicyMode.Deny, p.ModeFor("ui_editing", Access.Write));
        Assert.Equal(PolicyMode.Allow, p.ModeFor("online", Access.Read));
        Assert.Equal(PolicyMode.Deny, p.ModeFor("plugin_management", Access.Read));
        // Integrations inherit the switch their tools needed before.
        Assert.Equal(PolicyMode.Allow, p.ModeFor(PermissionCatalog.ForIntegration("AutoDuty")!.Id, Access.Write));
        Assert.Equal(PolicyMode.Allow, p.ModeFor(PermissionCatalog.ForIntegration("Artisan")!.Id, Access.Write));
        Assert.Equal(PolicyMode.Allow, p.ModeFor(PermissionCatalog.ForIntegration("GatherbuddyReborn")!.Id, Access.Write));
        Assert.Equal(PolicyMode.Deny, p.ModeFor(PermissionCatalog.ForIntegration("FCCH")!.Id, Access.Write));
    }
}

public class BuiltInGateTests
{
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

    private static McpTool Core(string name, bool readOnly) => new()
    {
        Name = name, Description = "Does it. More text.", ReadOnly = readOnly, Handler = (_, _) => Task.FromResult<object?>("ran"),
    };

    private static ToolArgs Args() => new(new JsonObject());

    [Fact]
    public async Task Reads_run_by_default_without_asking_or_audit()
    {
        Assert.Equal("ran", await Gate().InvokeAsync(Core("get_game_status", true), Args(), default));
        Assert.Equal("ran", await Gate().InvokeAsync(Core("get_navigation_status", true), Args(), default));
        Assert.Empty(approvals.Requests);
        Assert.Empty(audit.Recent());
    }

    [Fact]
    public async Task Writes_are_denied_by_default_and_the_refusal_says_where_to_change_it()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Core("navigate_to", false), Args(), default));
        Assert.Contains("Game & navigation", ex.Message);
        Assert.Contains("/xivmcp", ex.Message);
        Assert.Equal("blocked", audit.Recent()[0].Decision);
    }

    [Fact]
    public async Task Ask_prompts_with_the_group_and_audits_the_call()
    {
        store.Core.Set("game_navigation", Access.Write, PolicyMode.Ask);
        await Gate().InvokeAsync(Core("navigate_to", false), Args(), default);
        var request = Assert.Single(approvals.Requests);
        Assert.Equal("Game & navigation", request.Area);
        Assert.Equal(ToolSummaries.For("navigate_to"), request.Summary); // the player-facing text, not the assistant's description
        Assert.Equal("approved", audit.Recent()[0].Decision);
        Assert.Equal("ok", audit.Recent()[0].Outcome);
        Assert.True(audit.Recent()[0].BuiltIn);
    }

    [Fact]
    public async Task Always_allow_on_a_built_in_prompt_allows_just_that_tool()
    {
        store.Core.Set("items_retainers", Access.Write, PolicyMode.Ask);
        approvals.Answer = ApprovalDecision.AlwaysAllow;
        var g = Gate();
        await g.InvokeAsync(Core("move_items", false), Args(), default);
        Assert.Equal(PolicyMode.Allow, store.Core.ToolMode("move_items"));
        Assert.Equal(PolicyMode.Ask, store.Core.ModeFor("items_retainers", Access.Write));
        await g.InvokeAsync(Core("move_items", false), Args(), default);
        Assert.Single(approvals.Requests);
        await g.InvokeAsync(Core("sort_inventory", false), Args(), default); // the rest of the group still asks
        Assert.Equal(2, approvals.Requests.Count);
    }

    [Fact]
    public async Task A_tool_setting_overrides_its_group_both_ways()
    {
        // The group allows changes, but this one tool is denied …
        store.Core.Set("game_navigation", Access.Write, PolicyMode.Allow);
        store.Core.SetTool("switch_character", PolicyMode.Deny);
        var ex = await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Core("switch_character", false), Args(), default));
        Assert.Contains("switch_character", ex.Message);
        Assert.Equal("ran", await Gate().InvokeAsync(Core("navigate_to", false), Args(), default));

        // … and the group denies changes, but this one tool is allowed.
        store.Core.Set("items_retainers", Access.Write, PolicyMode.Deny);
        store.Core.SetTool("sort_inventory", PolicyMode.Allow);
        Assert.Equal("ran", await Gate().InvokeAsync(Core("sort_inventory", false), Args(), default));
        await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Core("move_items", false), Args(), default));
    }

    [Fact]
    public void Clearing_a_tool_setting_follows_the_group_again()
    {
        store.Core.SetTool("navigate_to", PolicyMode.Allow);
        Assert.Equal(PolicyMode.Allow, Gate().CheckBuiltIn(Core("navigate_to", false)));
        store.Core.SetTool("navigate_to", null);
        Assert.Null(store.Core.ToolMode("navigate_to"));
        Assert.Equal(PolicyMode.Deny, Gate().CheckBuiltIn(Core("navigate_to", false)));
    }

    [Fact]
    public async Task Tool_settings_apply_to_integration_tools_too()
    {
        var duty = IntegrationCatalog.Apply([Core("run_duty", false)], _ => true).Single();
        store.Core.SetTool("run_duty", PolicyMode.Allow);
        Assert.Equal("ran", await Gate().InvokeAsync(duty, Args(), default));
    }

    [Fact]
    public async Task Session_approval_on_a_built_in_prompt_covers_that_tool()
    {
        store.Core.Set("items_retainers", Access.Write, PolicyMode.Ask);
        approvals.Answer = ApprovalDecision.ApprovedForSession;
        var g = Gate();
        await g.InvokeAsync(Core("move_items", false), Args(), default);
        await g.InvokeAsync(Core("move_items", false), Args(), default);
        Assert.Single(approvals.Requests);
        await g.InvokeAsync(Core("sort_inventory", false), Args(), default); // another tool asks again
        Assert.Equal(2, approvals.Requests.Count);
    }

    [Fact]
    public async Task Reads_can_be_denied_or_asked_too()
    {
        store.Core.Set("game_data", Access.Read, PolicyMode.Deny);
        await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(Core("get_inventory", true), Args(), default));
        store.Core.Set("game_data", Access.Read, PolicyMode.Ask);
        await Gate().InvokeAsync(Core("get_inventory", true), Args(), default);
        Assert.Single(approvals.Requests);
    }

    [Fact]
    public async Task Integration_tools_follow_their_own_group_not_the_core_areas()
    {
        var duty = IntegrationCatalog.Apply([Core("run_duty", false)], _ => true).Single();
        await Assert.ThrowsAsync<ToolException>(() => Gate().InvokeAsync(duty, Args(), default));
        store.Core.Set(PermissionCatalog.ForIntegration("AutoDuty")!.Id, Access.Write, PolicyMode.Allow);
        store.Core.Set("game_navigation", Access.Write, PolicyMode.Deny);
        Assert.Equal("ran", await Gate().InvokeAsync(duty, Args(), default));
    }

    [Fact]
    public async Task Unknown_built_in_tools_fail_safe_writes_ask()
    {
        await Gate().InvokeAsync(Core("brand_new_tool", false), Args(), default);
        Assert.Single(approvals.Requests);
        Assert.Equal("ran", await Gate().InvokeAsync(Core("brand_new_reader", true), Args(), default));
    }

    [Fact]
    public void Check_reports_built_in_modes()
    {
        store.Core.Set("market", Access.Write, PolicyMode.Ask);
        Assert.Equal(PolicyMode.Ask, Gate().CheckBuiltIn(Core("buy_item", false)));
        Assert.Equal(PolicyMode.Allow, Gate().CheckBuiltIn(Core("get_market_listings", true)));
    }
}

public class CorePolicyToolTests
{
    [Fact]
    public void Tool_modes_are_stored_separately_and_can_be_cleared()
    {
        var p = new CorePolicy();
        Assert.Null(p.ToolMode("move_items"));
        p.SetTool("move_items", PolicyMode.Ask);
        Assert.Equal(PolicyMode.Ask, p.ToolMode("move_items"));
        Assert.Equal(PolicyMode.Deny, p.ModeFor("items_retainers", Access.Write)); // the group is untouched
        p.SetTool("move_items", null);
        Assert.Null(p.ToolMode("move_items"));
    }
}

public class IntegrationGroupAccessTests
{
    [Theory]
    [InlineData("Lifestream", false, true)]          // visit_world only changes
    [InlineData("ItemVendorLocation", true, false)]  // find_vendors only reads
    [InlineData("FCCH", false, true)]
    [InlineData("AutoDuty", true, true)]
    public void An_integration_group_only_has_the_access_its_tools_need(string plugin, bool hasRead, bool hasWrite)
    {
        var g = PermissionCatalog.ForIntegration(plugin)!;
        Assert.Equal(hasRead, g.HasRead);
        Assert.Equal(hasWrite, g.HasWrite);
    }
}
