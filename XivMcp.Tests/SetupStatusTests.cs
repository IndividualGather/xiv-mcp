using XivMcp.Connect;

namespace XivMcp.Tests;

public class SetupStatusTests
{
    [Theory]
    [InlineData("""{ "servers": { "ffxiv": { "type": "http" } } }""", "servers", true)]
    [InlineData("""{ "servers": { "other": {} } }""", "servers", false)]
    [InlineData("""{ "mcpServers": { "ffxiv": {} } }""", "mcpServers", true)]
    [InlineData("""{ "mcpServers": { "ffxiv": {} } }""", "servers", false)]
    [InlineData("not json", "servers", false)]
    [InlineData("", "servers", false)]
    public void Json_configs_list_the_server_under_their_key(string json, string key, bool expected) =>
        Assert.Equal(expected, SetupStatus.HasServer(json, key));

    [Fact]
    public void Vs_code_configs_may_have_comments_and_trailing_commas() =>
        Assert.True(SetupStatus.HasServer("{\n  // mine\n  \"servers\": { \"ffxiv\": { \"type\": \"http\" }, },\n}", "servers"));

    [Theory]
    [InlineData("""{ "mcpServers": { "ffxiv": {} } }""", true)]                                   // all projects
    [InlineData("""{ "projects": { "E:/x": { "mcpServers": { "ffxiv": {} } } } }""", true)]     // one project
    [InlineData("""{ "projects": { "E:/x": { "mcpServers": {} } }, "mcpServers": {} }""", false)]
    public void Claude_Code_counts_entries_for_all_projects_or_one(string json, bool expected) =>
        Assert.Equal(expected, SetupStatus.ClaudeCodeHasServer(json));

    [Fact]
    public void Claude_Code_lists_the_projects_that_have_their_own_entry() =>
        Assert.Equal(["E:/a"], SetupStatus.ClaudeCodeProjects("""{ "projects": { "E:/a": { "mcpServers": { "ffxiv": {} } }, "E:/b": { "mcpServers": {} }, "E:/c": {} } }"""));

    [Theory]
    [InlineData(new[] { "local.mcpb.individualgather.xiv-mcp" }, null, true)]
    [InlineData(new[] { "local.mcpb.individualgather.xiv-mcp" }, """{ "isEnabled": true }""", true)]
    [InlineData(new[] { "local.mcpb.individualgather.xiv-mcp" }, """{ "isEnabled": false }""", false)]
    [InlineData(new[] { "local.mcpb.someone.else" }, null, false)]
    public void Claude_Desktop_counts_an_installed_enabled_extension(string[] folders, string? settings, bool expected) =>
        Assert.Equal(expected, SetupStatus.ClaudeExtensionEnabled(folders, settings));
}
