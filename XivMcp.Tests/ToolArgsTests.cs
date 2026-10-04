using System.Text.Json.Nodes;
using XivMcp.Mcp;

namespace XivMcp.Tests;

public class ToolArgsTests
{
    private static ToolArgs Args(string json) => new(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void An_array_is_read_as_sent()
    {
        Assert.Equal([3800, 3801], Args("""{ "items": [3800, 3801] }""").Array("items")!.Select(n => n!.GetValue<int>()));
    }

    [Fact]
    public void An_array_sent_as_a_JSON_string_is_read_too()
    {
        // Some clients send arrays as text when their copy of the schema predates the parameter.
        Assert.Equal([3800], Args("""{ "items": "[3800]" }""").Array("items")!.Select(n => n!.GetValue<int>()));
    }

    [Fact]
    public void A_missing_or_unreadable_value_is_null()
    {
        Assert.Null(Args("{}").Array("items"));
        Assert.Null(Args("""{ "items": "shoes" }""").Array("items"));
    }
}
