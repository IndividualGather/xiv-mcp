using XivMcp.Api;
using XivMcp.Mcp;

namespace XivMcp.Tests;

public class DependencyTests
{
    private static readonly HashSet<string> BuiltIn = ["navigate_to", "get_game_status", "run_duty", "buy_item", "sell_item"];

    private static IReadOnlyList<ToolDependency> Parse(string json) => DependencyParser.Parse(json, BuiltIn.Contains);

    private const string OtherTool = """
        { "tools": [ { "tool": "other_scan", "plugin": "OtherPlugin", "name": "Other Plugin",
                       "repo": "https://example.com/repo.json", "minVersion": "1.2.0" } ] }
        """;

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void Built_in_tools_need_only_their_name()
    {
        var d = Assert.Single(Parse("""{ "tools": [ { "tool": "navigate_to" } ] }"""));
        Assert.Equal("navigate_to", d.Tool);
        Assert.True(d.BuiltIn);
        Assert.Null(d.Plugin);
    }

    [Fact]
    public void Another_plugins_tool_names_plugin_repo_and_version()
    {
        var d = Assert.Single(Parse(OtherTool));
        Assert.False(d.BuiltIn);
        Assert.Equal("OtherPlugin", d.Plugin);
        Assert.Equal("Other Plugin", d.PluginName);
        Assert.Equal("https://example.com/repo.json", d.Repo);
        Assert.Equal(new Version(1, 2, 0), d.MinVersion);
    }

    [Theory]
    [InlineData("plugin")]
    [InlineData("name")]
    [InlineData("repo")]
    [InlineData("minVersion")]
    public void Another_plugins_tool_without_a_field_is_refused(string field)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(OtherTool)!;
        json["tools"]![0]!.AsObject().Remove(field);
        var ex = Assert.Throws<ToolException>(() => Parse(json.ToJsonString()));
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void A_misspelt_built_in_tool_is_explained()
    {
        var ex = Assert.Throws<ToolException>(() => Parse("""{ "tools": [ { "tool": "navigate_too" } ] }"""));
        Assert.Contains("not an XIV MCP tool", ex.Message);
    }

    [Fact]
    public void Built_in_tools_cannot_claim_another_plugin()
    {
        var ex = Assert.Throws<ToolException>(() => Parse(
            """{ "tools": [ { "tool": "navigate_to", "plugin": "OtherPlugin", "name": "x", "repo": "official", "minVersion": "1.0" } ] }"""));
        Assert.Contains("built into XIV MCP", ex.Message);
    }

    [Theory]
    [InlineData("http://example.com/repo.json")]
    [InlineData("example.com/repo.json")]
    [InlineData("")]
    public void Repositories_must_be_official_or_https(string repo)
    {
        var ex = Assert.Throws<ToolException>(() => Parse(OtherTool.Replace("https://example.com/repo.json", repo)));
        Assert.Contains("repo", ex.Message);
    }

    [Fact]
    public void The_official_repository_is_accepted() =>
        Assert.Equal("official", Assert.Single(Parse(OtherTool.Replace("https://example.com/repo.json", "official"))).Repo);

    [Fact]
    public void Versions_must_parse()
    {
        var ex = Assert.Throws<ToolException>(() => Parse(OtherTool.Replace("1.2.0", "latest")));
        Assert.Contains("minVersion", ex.Message);
    }

    [Fact]
    public void A_tool_is_declared_once()
    {
        var ex = Assert.Throws<ToolException>(() => Parse("""{ "tools": [ { "tool": "navigate_to" }, { "tool": "navigate_to" } ] }"""));
        Assert.Contains("twice", ex.Message);
    }

    [Fact]
    public void An_empty_list_clears_the_declaration() => Assert.Empty(Parse("""{ "tools": [] }"""));

    // ------------------------------------------------------------------ checking

    private sealed class World
    {
        public Dictionary<string, PluginPresence> Plugins { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ToolOwners { get; } = new();
        public HashSet<string> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);

        public World With(string id, string version, bool loaded = true)
        {
            Plugins[id] = new PluginPresence(id, id, Version.Parse(version), loaded);
            return this;
        }

        public DependencyStatus Check(ToolDependency d) =>
            DependencyCheck.Check(d, id => Plugins.GetValueOrDefault(id), t => ToolOwners.GetValueOrDefault(t), Enabled.Contains);
    }

    private static ToolDependency BuiltInDep(string tool) => Assert.Single(Parse($$"""{ "tools": [ { "tool": "{{tool}}" } ] }"""));

    private static ToolDependency Other => Assert.Single(Parse(OtherTool));

    [Fact]
    public void A_built_in_tool_without_plugins_is_fine() =>
        Assert.Equal(DependencyState.Ok, new World().Check(BuiltInDep("get_game_status")).State);

    [Fact]
    public void A_built_in_tool_whose_plugins_are_missing_is_an_error_with_install_hints()
    {
        var status = new World().With("vnavmesh", "1.0").Check(BuiltInDep("navigate_to"));
        Assert.Equal(DependencyState.NeedsPlugin, status.State);
        Assert.True(status.IsError);
        var lifestream = Assert.Single(status.Install);
        Assert.Equal("Lifestream", lifestream.InternalName);
        Assert.False(lifestream.Installed);
        Assert.StartsWith("https://", lifestream.Repo);
    }

    [Fact]
    public void An_installed_but_disabled_plugin_is_listed_as_installed()
    {
        var status = new World().With("vnavmesh", "1.0").With("Lifestream", "2.0", loaded: false).Check(BuiltInDep("navigate_to"));
        Assert.Equal(DependencyState.NeedsPlugin, status.State);
        Assert.True(Assert.Single(status.Install).Installed);
    }

    [Fact]
    public void A_built_in_tool_with_its_plugins_is_fine() =>
        Assert.Equal(DependencyState.Ok, new World().With("vnavmesh", "1.0").With("Lifestream", "2.0").Check(BuiltInDep("navigate_to")).State);

    [Fact]
    public void Plugins_that_only_improve_a_tool_are_hints_not_errors()
    {
        var status = new World().With("ItemVendorLocation", "2.0").Check(BuiltInDep("sell_item"));
        Assert.Equal(DependencyState.Ok, status.State);
        Assert.False(status.IsError);
        Assert.Contains(status.Install, p => p.InternalName == "PennyPincher" && !p.Needed);
    }

    [Fact]
    public void Another_plugins_tool_whose_plugin_is_missing_offers_its_repository()
    {
        var status = new World().Check(Other);
        Assert.Equal(DependencyState.PluginMissing, status.State);
        var p = Assert.Single(status.Install);
        Assert.Equal(("OtherPlugin", "Other Plugin", "https://example.com/repo.json"), (p.InternalName, p.Name, p.Repo));
    }

    [Fact]
    public void Another_plugins_tool_whose_plugin_is_disabled_in_dalamud()
    {
        var status = new World().With("OtherPlugin", "1.3", loaded: false).Check(Other);
        Assert.Equal(DependencyState.PluginNotLoaded, status.State);
        Assert.True(status.IsError);
    }

    [Fact]
    public void Another_plugins_tool_whose_plugin_is_too_old()
    {
        var status = new World().With("OtherPlugin", "1.1.9").Check(Other);
        Assert.Equal(DependencyState.PluginOutdated, status.State);
        Assert.Contains("1.1.9", status.Message);
        Assert.Contains("1.2.0", status.Message);
    }

    [Fact]
    public void Another_plugins_tool_that_is_not_registered()
    {
        var status = new World().With("OtherPlugin", "1.2").Check(Other);
        Assert.Equal(DependencyState.ToolMissing, status.State);
    }

    [Fact]
    public void Another_plugins_tool_registered_by_someone_else_is_not_it()
    {
        var w = new World().With("OtherPlugin", "1.2");
        w.ToolOwners["other_scan"] = "Impostor";
        Assert.Equal(DependencyState.ToolMissing, w.Check(Other).State);
    }

    [Fact]
    public void Another_plugins_tool_that_the_player_has_not_enabled()
    {
        var w = new World().With("OtherPlugin", "1.2");
        w.ToolOwners["other_scan"] = "OtherPlugin";
        var status = w.Check(Other);
        Assert.Equal(DependencyState.NotEnabled, status.State);
        Assert.True(status.IsError);
        Assert.Empty(status.Install);
    }

    [Fact]
    public void Another_plugins_tool_that_is_ready()
    {
        var w = new World().With("OtherPlugin", "1.2.0.1");
        w.ToolOwners["other_scan"] = "OtherPlugin";
        w.Enabled.Add("OtherPlugin");
        Assert.Equal(DependencyState.Ok, w.Check(Other).State);
    }
}
