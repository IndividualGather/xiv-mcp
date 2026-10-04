using XivMcp.Api;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

public class ToolDefinitionTests
{
    private static string Def(string name = "myplugin_do", string description = "Does a thing.", string? schema = null, bool? readOnly = null,
                              bool? destructive = null, string? capabilities = null)
    {
        var parts = new List<string> { $"\"name\": \"{name}\"", $"\"description\": \"{description}\"" };
        if (schema is not null) parts.Add($"\"inputSchema\": {schema}");
        if (readOnly is not null) parts.Add($"\"readOnly\": {readOnly.Value.ToString().ToLowerInvariant()}");
        if (destructive is not null) parts.Add($"\"destructive\": {destructive.Value.ToString().ToLowerInvariant()}");
        if (capabilities is not null) parts.Add($"\"capabilities\": {capabilities}");
        return "{" + string.Join(", ", parts) + "}";
    }

    [Fact]
    public void Parses_a_read_only_tool_and_adds_read_game()
    {
        var d = ToolDefinitionParser.Parse(Def(readOnly: true));
        Assert.Equal("myplugin_do", d.Name);
        Assert.True(d.ReadOnly);
        Assert.Equal([Capabilities.ReadGame], d.Capabilities);
        Assert.Equal("object", d.InputSchema["type"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_an_acting_tool_with_capabilities()
    {
        var d = ToolDefinitionParser.Parse(Def(capabilities: """["move_character", "game_ui", "move_character"]"""));
        Assert.False(d.ReadOnly);
        Assert.Equal([Capabilities.GameUi, Capabilities.MoveCharacter, Capabilities.ReadGame], d.Capabilities.Order());
    }

    [Theory]
    [InlineData("Do")]
    [InlineData("ab")]
    [InlineData("1abc")]
    [InlineData("my-plugin")]
    public void Rejects_bad_names(string name) =>
        Assert.Contains("Invalid tool name", Assert.Throws<ToolException>(() => ToolDefinitionParser.Parse(Def(name: name, readOnly: true))).Message);

    [Fact]
    public void Requires_a_description() =>
        Assert.Contains("description", Assert.Throws<ToolException>(() => ToolDefinitionParser.Parse(Def(description: " ", readOnly: true))).Message);

    [Fact]
    public void Requires_an_object_schema() =>
        Assert.Contains("inputSchema", Assert.Throws<ToolException>(() => ToolDefinitionParser.Parse(Def(schema: """{"type":"string"}""", readOnly: true))).Message);

    [Fact]
    public void Acting_tools_must_declare_what_they_do() =>
        Assert.Contains("capabilities", Assert.Throws<ToolException>(() => ToolDefinitionParser.Parse(Def())).Message);

    [Fact]
    public void Read_only_tools_cant_declare_acting_capabilities() =>
        Assert.Contains("read-only", Assert.Throws<ToolException>(() => ToolDefinitionParser.Parse(Def(readOnly: true, capabilities: """["spend_gil"]"""))).Message);

    [Fact]
    public void Unknown_capabilities_are_rejected_with_the_valid_list()
    {
        var ex = Assert.Throws<ToolException>(() => ToolDefinitionParser.Parse(Def(capabilities: """["fly"]""")));
        Assert.Contains("fly", ex.Message);
        Assert.Contains(Capabilities.MoveCharacter, ex.Message);
    }

    [Fact]
    public void Destructive_tools_cant_be_read_only() =>
        Assert.Throws<ToolException>(() => ToolDefinitionParser.Parse(Def(readOnly: true, destructive: true)));

    [Fact]
    public void Invalid_json_is_a_tool_error() => Assert.Throws<ToolException>(() => ToolDefinitionParser.Parse("{ nope"));
}

public class PluginReplyTests
{
    [Fact]
    public void Result_reply() => Assert.Equal(42, Assert.IsType<PluginReply.Result>(PluginReply.Parse("""{"result": 42}""", "P")).Value!.GetValue<int>());

    [Fact]
    public void Null_result_reply() => Assert.Null(Assert.IsType<PluginReply.Result>(PluginReply.Parse("""{"result": null}""", "P")).Value);

    [Fact]
    public void Empty_reply_is_a_null_result() => Assert.IsType<PluginReply.Result>(PluginReply.Parse("", "P"));

    [Fact]
    public void Error_reply() => Assert.Equal("Bags full.", Assert.IsType<PluginReply.Failure>(PluginReply.Parse("""{"error": "Bags full."}""", "P")).Message);

    [Fact]
    public void Pending_reply() => Assert.IsType<PluginReply.Pending>(PluginReply.Parse("""{"pending": true}""", "P"));

    [Theory]
    [InlineData("""{"value": 1}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""not json""")]
    public void Anything_else_is_rejected_naming_the_plugin(string reply) =>
        Assert.Contains("MyPlugin", Assert.Throws<ToolException>(() => PluginReply.Parse(reply, "MyPlugin")).Message);
}
