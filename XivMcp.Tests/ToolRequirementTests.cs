using XivMcp.Integrations;
using XivMcp.Permissions;

namespace XivMcp.Tests;

public class ToolRequirementTests
{
    [Fact]
    public void Every_requirement_names_a_known_plugin_and_a_purpose()
    {
        Assert.All(ToolRequirements.Core.SelectMany(kv => kv.Value), r =>
        {
            Assert.NotNull(PluginCatalog.Find(r.PluginId));
            Assert.False(string.IsNullOrWhiteSpace(r.Purpose));
        });
    }

    [Fact]
    public void Core_tools_work_without_plugins_and_say_what_changes_without_them()
    {
        Assert.All(ToolRequirements.Core.SelectMany(kv => kv.Value), r =>
        {
            Assert.Equal(Need.Improves, r.Need);
            Assert.False(string.IsNullOrWhiteSpace(r.Without), r.PluginId);
        });
    }

    [Fact]
    public void Only_integration_tools_need_their_plugin() =>
        Assert.All(ToolRequirements.Tools.Where(t => ToolRequirements.For(t).Any(r => r.Need == Need.Needed)),
            t => Assert.NotNull(IntegrationCatalog.For(t)));

    [Fact]
    public void Requirements_are_only_listed_for_core_tools()
    {
        var core = PermissionCatalog.CoreTools.Keys.ToHashSet();
        Assert.All(ToolRequirements.Core.Keys, t => Assert.Contains(t, core));
    }

    [Fact]
    public void Every_integration_plugin_is_in_the_catalog()
    {
        Assert.All(IntegrationCatalog.All, i => Assert.NotNull(PluginCatalog.Find(i.PluginId)));
    }

    [Fact]
    public void Integration_tools_need_their_plugin()
    {
        var needs = ToolRequirements.For("run_duty");
        var autoDuty = Assert.Single(needs);
        Assert.Equal("AutoDuty", autoDuty.PluginId);
        Assert.Equal(Need.Needed, autoDuty.Need);
    }

    [Fact]
    public void Navigation_needs_walking_and_travel()
    {
        var needs = ToolRequirements.For("navigate_to").Select(r => r.PluginId).ToList();
        Assert.Contains("vnavmesh", needs);
        Assert.Contains("Lifestream", needs);
    }

    [Fact]
    public void Tools_without_plugins_need_nothing() => Assert.Empty(ToolRequirements.For("get_game_status"));

    [Fact]
    public void Plugin_lookup_ignores_case() => Assert.Equal("vnavmesh", PluginCatalog.Find("VNAVMESH")?.InternalName);

    [Fact]
    public void Repositories_are_official_or_https()
    {
        Assert.All(PluginCatalog.All, p => Assert.True(
            p.Repo == PluginCatalog.Official || p.Repo.StartsWith("https://", StringComparison.Ordinal), p.InternalName));
    }
}
