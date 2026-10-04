using System.Text.Json.Nodes;
using XivMcp.Jobs;
using XivMcp.Mcp;

namespace XivMcp.Tests;

public class ToolRegistryTests
{
    private static readonly ToolProvider A = new("PluginA", "Plugin A", ProviderTrust.ThirdParty);
    private static readonly ToolProvider B = new("PluginB", "Plugin B", ProviderTrust.ThirdParty);
    private static readonly ToolProvider MaintainedA = new("PluginA", "Plugin A integration", ProviderTrust.Maintained);

    private static McpTool T(string name, ToolProvider? p = null) =>
        new() { Name = name, Description = "d", Provider = p ?? ToolProvider.Core, Handler = (_, _) => Task.FromResult<object?>(null) };

    [Fact]
    public void Built_in_duplicates_are_rejected() => Assert.Throws<InvalidOperationException>(() => new ToolRegistry([T("x"), T("x")]));

    [Fact]
    public void A_provider_can_add_and_replace_its_own_tools_and_raises_changed()
    {
        var r = new ToolRegistry([]);
        var changes = 0;
        r.Changed += () => changes++;
        Assert.True(r.AddOrReplace(T("a_do", A)));
        Assert.True(r.AddOrReplace(T("a_do", A)));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Nobody_can_take_over_built_in_or_foreign_names()
    {
        var r = new ToolRegistry([T("get_game_status")]);
        Assert.False(r.AddOrReplace(T("get_game_status", A)));
        Assert.True(r.AddOrReplace(T("a_do", A)));
        Assert.False(r.AddOrReplace(T("a_do", B)));
        Assert.False(r.AddOrReplace(T("a_do", MaintainedA))); // same id, different trust: still someone else
    }

    [Fact]
    public void Removal_only_touches_the_providers_own_tools()
    {
        var r = new ToolRegistry([T("core_tool")]);
        r.AddOrReplace(T("a_one", A));
        r.AddOrReplace(T("a_two", A));
        r.AddOrReplace(T("b_one", B));
        r.AddOrReplace(T("m_one", MaintainedA));
        Assert.False(r.Remove("core_tool", A));
        Assert.False(r.Remove("b_one", A));
        Assert.True(r.Remove("a_one", A));
        Assert.Equal(1, r.RemoveProvider(A));
        Assert.Equal(["b_one", "core_tool", "m_one"], r.All.Select(t => t.Name).Order());
    }
}

public class PlaceholderTests
{
    private static readonly Dictionary<string, JsonNode?> Results = new()
    {
        ["plan"] = JsonNode.Parse("""{"list": {"id": 7, "name": "Tacos"}, "items": [{"id": 5}, {"id": 9}]}"""),
        ["count"] = JsonNode.Parse("""{"seconds": 20}"""),
    };

    private static JsonNode? ResultOf(string step) =>
        Results.TryGetValue(step, out var r) ? r : throw new ToolException($"Step '{step}' has no result yet.");

    private static JsonObject Resolve(string json) => Placeholders.Resolve((JsonObject)JsonNode.Parse(json)!, ResultOf);

    [Fact]
    public void Whole_string_placeholder_keeps_the_type() => Assert.Equal(7, Resolve("""{"list": "{{plan.list.id}}"}""")["list"]!.GetValue<int>());

    [Fact]
    public void Paths_go_into_arrays_and_ignore_case() => Assert.Equal(9, Resolve("""{"x": "{{plan.Items.1.ID}}"}""")["x"]!.GetValue<int>());

    [Fact]
    public void Embedded_placeholders_become_text() =>
        Assert.Equal("Counted 20 s for Tacos", Resolve("""{"text": "Counted {{count.seconds}} s for {{plan.list.name}}"}""")["text"]!.GetValue<string>());

    [Fact]
    public void Objects_embedded_in_text_become_json() =>
        Assert.Equal("""list={"id":7,"name":"Tacos"}""", Resolve("""{"t": "list={{plan.list}}"}""")["t"]!.GetValue<string>());

    [Fact]
    public void Nested_objects_and_arrays_are_resolved() =>
        Assert.Equal(20, Resolve("""{"a": [{"b": "{{count.seconds}}"}]}""")["a"]![0]!["b"]!.GetValue<int>());

    [Fact]
    public void Strings_without_placeholders_are_untouched() => Assert.Equal("{{ not one", Resolve("""{"t": "{{ not one"}""")["t"]!.GetValue<string>());

    [Fact]
    public void Missing_paths_and_steps_are_errors()
    {
        Assert.Contains("nope", Assert.Throws<ToolException>(() => Resolve("""{"x": "{{plan.nope}}"}""")).Message);
        Assert.Contains("later", Assert.Throws<ToolException>(() => Resolve("""{"x": "{{later.id}}"}""")).Message);
    }

    [Fact]
    public void The_input_is_not_modified()
    {
        var input = (JsonObject)JsonNode.Parse("""{"x": "{{count.seconds}}"}""")!;
        Placeholders.Resolve(input, ResultOf);
        Assert.Equal("{{count.seconds}}", input["x"]!.GetValue<string>());
    }
}
