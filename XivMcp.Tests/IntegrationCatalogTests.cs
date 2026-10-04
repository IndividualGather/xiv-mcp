using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

public class IntegrationCatalogTests
{
    private static McpTool T(string name, bool readOnly = true, Func<bool>? available = null) => new()
    {
        Name = name, Description = "d", ReadOnly = readOnly, Available = available, Handler = (_, _) => Task.FromResult<object?>(name),
    };

    [Fact]
    public void Every_integration_names_a_plugin_and_valid_capabilities()
    {
        Assert.NotEmpty(IntegrationCatalog.All);
        Assert.All(IntegrationCatalog.All, i =>
        {
            Assert.False(string.IsNullOrWhiteSpace(i.PluginId));
            Assert.False(string.IsNullOrWhiteSpace(i.Summary));
            Assert.NotEmpty(i.Tools);
            Assert.All(i.Tools.Values.SelectMany(c => c), c => Assert.NotNull(Capabilities.Find(c)));
        });
    }

    [Fact]
    public void No_tool_belongs_to_two_integrations()
    {
        var names = IntegrationCatalog.All.SelectMany(i => i.Tools.Keys).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Apply_moves_listed_tools_to_their_integration_and_leaves_the_rest_in_core()
    {
        var applied = IntegrationCatalog.Apply([T("run_duty", readOnly: false), T("get_game_status")], _ => true).ToDictionary(t => t.Name);

        var duty = applied["run_duty"];
        Assert.Equal(new ToolProvider("AutoDuty", "AutoDuty", ProviderTrust.Maintained), duty.Provider);
        Assert.Contains(Capabilities.Combat, duty.Capabilities);
        Assert.Contains(Capabilities.ReadGame, duty.Capabilities);
        Assert.Same(ToolProvider.Core, applied["get_game_status"].Provider);
    }

    [Fact]
    public async Task Applied_tools_keep_their_handler_and_metadata()
    {
        var applied = IntegrationCatalog.Apply([T("list_duties")], _ => true).Single();
        Assert.Equal("d", applied.Description);
        Assert.True(applied.ReadOnly);
        Assert.Equal("list_duties", await applied.Handler(new ToolArgs(null), default));
    }

    [Fact]
    public void Applied_tools_are_only_available_while_their_plugin_is_loaded()
    {
        var loaded = false;
        var tool = IntegrationCatalog.Apply([T("list_duties")], id => id == "AutoDuty" && loaded).Single();
        Assert.False(tool.IsAvailable);
        loaded = true;
        Assert.True(tool.IsAvailable);
    }

    [Fact]
    public void A_tools_own_availability_still_counts()
    {
        var tool = IntegrationCatalog.Apply([T("list_duties", available: () => false)], _ => true).Single();
        Assert.False(tool.IsAvailable);
    }

    [Fact]
    public void World_travel_is_a_core_tool_that_works_without_its_plugin()
    {
        Assert.Null(IntegrationCatalog.For("visit_world"));
        Assert.Equal("game_navigation", PermissionCatalog.CoreTools["visit_world"]);
        Assert.Contains(ToolRequirements.For("visit_world"), r => r.PluginId == "Lifestream" && r.Need == Need.Improves);
    }

    [Fact]
    public void Acting_integration_tools_declare_something_beyond_reading()
    {
        var acting = new[] { "run_duty", "set_crafting_list", "craft_item", "gather_until", "fc_chest_transfer" };
        foreach (var name in acting)
        {
            var integration = IntegrationCatalog.All.Single(i => i.Tools.ContainsKey(name));
            Assert.NotEmpty(integration.Tools[name]);
        }
    }
}
