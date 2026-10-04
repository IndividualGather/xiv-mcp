using XivMcp.Api;
using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Permissions;
using XivMcp.TripleTriad;

namespace XivMcp.Tests;

public class SaucyIntegrationTests
{
    private static readonly string[] TriadReads = ["list_triad_npcs", "list_triad_cards", "get_triad_decks"];
    private static readonly string[] TriadWrites = ["set_triad_deck"];
    private static readonly string[] SaucyOnly = ["build_triad_deck", "play_triple_triad", "farm_triad_cards", "play_mini_cactpot", "play_jumbo_cactpot", "get_saucy_stats", "stop_saucy"];

    [Fact]
    public void Saucy_is_an_integration_with_its_tools()
    {
        var saucy = IntegrationCatalog.All.Single(i => i.PluginId == "Saucy");
        Assert.All(TriadReads.Concat(TriadWrites).Concat(SaucyOnly), t => Assert.True(saucy.Tools.ContainsKey(t), t));
        Assert.All(saucy.Tools.Keys, t => Assert.NotNull(ToolSummaries.For(t)));
    }

    [Fact]
    public void The_permission_card_is_the_gold_saucer_and_shows_with_triadbuddy_too()
    {
        var group = PermissionCatalog.Find("saucy")!;
        Assert.Equal("Gold Saucer", group.Title);
        Assert.Equal("Saucy", group.PluginId);
        Assert.Equal(["Saucy", "TriadBuddy"], group.Plugins);
        Assert.Equal(PolicyMode.Allow, group.DefaultRead);
        Assert.Equal(PolicyMode.Deny, group.DefaultWrite);
    }

    [Fact]
    public void Other_integrations_need_only_their_own_plugin()
    {
        Assert.Equal(["AutoDuty"], PermissionCatalog.Find("autoduty")!.Plugins);
    }

    [Fact]
    public void Triple_triad_reading_works_with_saucy_or_triadbuddy()
    {
        var available = IntegrationCatalog.IsAvailable("list_triad_npcs", p => p == "TriadBuddy");
        Assert.True(available);
        Assert.False(IntegrationCatalog.IsAvailable("play_triple_triad", p => p == "TriadBuddy"));
        Assert.True(IntegrationCatalog.IsAvailable("play_triple_triad", p => p == "Saucy"));
        Assert.True(IntegrationCatalog.IsAvailable("set_triad_deck", p => p == "TriadBuddy"));
        Assert.False(IntegrationCatalog.IsAvailable("list_triad_npcs", _ => false));
    }

    [Fact]
    public void Reading_tools_change_nothing_and_playing_declares_what_it_does()
    {
        var saucy = IntegrationCatalog.All.Single(i => i.PluginId == "Saucy");
        Assert.All(TriadReads.Append("get_saucy_stats"), t => Assert.Empty(saucy.Tools[t]));
        Assert.Contains(Capabilities.MoveCharacter, saucy.Tools["play_triple_triad"]);
        Assert.Contains(Capabilities.SpendCurrency, saucy.Tools["play_triple_triad"]); // match fees in MGP
        Assert.Contains(Capabilities.SpendCurrency, saucy.Tools["play_mini_cactpot"]);
    }

    [Fact]
    public void Saucy_and_the_plugins_it_uses_are_known_with_their_repositories()
    {
        foreach (var id in new[] { "Saucy", "TriadBuddy", "vnavmesh", "Lifestream", "BossMod", "Questionable", "AutoRetainer" })
            Assert.NotNull(PluginCatalog.Find(id));
        Assert.Equal(PluginCatalog.Official, PluginCatalog.Find("TriadBuddy")!.Repo);
        Assert.StartsWith("https://", PluginCatalog.Find("Saucy")!.Repo);
    }

    [Fact]
    public void Reading_needs_saucy_or_triadbuddy()
    {
        var need = Assert.Single(ToolRequirements.For("list_triad_npcs"), r => r.Need == Need.Needed);
        Assert.Equal("Saucy", need.PluginId);
        Assert.Equal(["TriadBuddy"], need.Alternatives);
    }

    [Fact]
    public void Playing_needs_saucy_and_walks_and_teleports_with_the_travel_plugins()
    {
        var needs = ToolRequirements.For("play_triple_triad");
        Assert.Contains(needs, r => r.PluginId == "Saucy" && r.Need == Need.Needed && r.Alternatives.Count == 0);
        Assert.Contains(needs, r => r.PluginId == "vnavmesh" && r.Need == Need.Improves && r.Without is not null);
        Assert.Contains(needs, r => r.PluginId == "Lifestream" && r.Need == Need.Improves && r.Without is not null);
    }

    private static PluginPresence Loaded(string id) => new(id, id, new Version(1, 0), true);

    [Fact]
    public void A_dependency_on_a_triad_read_is_ready_with_either_plugin()
    {
        var d = new ToolDependency("list_triad_npcs", null, null, null, null);
        var withBuddy = DependencyCheck.Check(d, id => id == "TriadBuddy" ? Loaded(id) : null, _ => null, _ => true);
        Assert.Equal(DependencyState.Ok, withBuddy.State);
        var withNone = DependencyCheck.Check(d, _ => null, _ => null, _ => true);
        Assert.Equal(DependencyState.NeedsPlugin, withNone.State);
        Assert.Contains("Saucy or TriadBuddy", withNone.Message);
    }
}

public class TriadFarmGoalTests
{
    [Fact]
    public void A_single_card_goal_is_met_once_it_is_owned_or_waiting_in_the_bags()
    {
        var goal = TriadFarmGoal.ForCard(42, rewardCards: [41, 42, 43]);
        Assert.False(goal.IsMet(owned: [41], inBags: []));
        Assert.True(goal.IsMet(owned: [42], inBags: []));
        Assert.True(goal.IsMet(owned: [], inBags: [42]));
    }

    [Fact]
    public void An_all_cards_goal_needs_every_reward_card()
    {
        var goal = TriadFarmGoal.AllCards(rewardCards: [1, 2, 3]);
        Assert.False(goal.IsMet(owned: [1, 2], inBags: []));
        Assert.True(goal.IsMet(owned: [1, 2], inBags: [3]));
        Assert.Equal([3], goal.Missing(owned: [1, 2], inBags: []));
    }

    [Fact]
    public void A_card_the_npc_does_not_give_is_refused()
    {
        Assert.Throws<ArgumentException>(() => TriadFarmGoal.ForCard(99, rewardCards: [1, 2]));
    }

    [Fact]
    public void An_npc_without_reward_cards_has_nothing_to_farm()
    {
        Assert.Throws<ArgumentException>(() => TriadFarmGoal.AllCards(rewardCards: []));
    }
}
