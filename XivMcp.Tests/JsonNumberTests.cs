using System.Text.Json.Nodes;
using XivMcp.Mcp;

namespace XivMcp.Tests;

public class JsonNumberTests
{
    private static JsonNode? Value(string json) => JsonNode.Parse($$"""{ "v": {{json}} }""")!["v"];

    [Fact]
    public void Whole_numbers_are_read_as_they_are()
    {
        Assert.Equal(12, JsonNumber.Int(Value("12")));
        Assert.Equal(-5, JsonNumber.Int(Value("-5")));
    }

    [Fact]
    public void Fractional_numbers_do_not_throw()
    {
        // Regression: Teamcraft's timed-node data has durations like 1.5 (hours); GetValue<int> threw
        // "An element of type 'Number' cannot be converted to a 'System.Int32'" and broke plan_craft for big projects.
        Assert.Throws<InvalidOperationException>(() => Value("1.5")!.GetValue<int>());
        Assert.Equal(2, JsonNumber.Int(Value("1.5")));
        Assert.Equal(12, JsonNumber.Int(Value("12.0")));
        Assert.Equal(3, JsonNumber.Int(Value("3.4")));
    }

    [Fact]
    public void Numbers_outside_the_int_range_are_clamped()
    {
        Assert.Equal(int.MaxValue, JsonNumber.Int(Value("3000000000")));
        Assert.Equal(int.MinValue, JsonNumber.Int(Value("-3000000000")));
        Assert.Equal(int.MaxValue, JsonNumber.Int(Value("1e30")));
    }

    [Fact]
    public void Numeric_strings_are_read_and_anything_else_is_null()
    {
        Assert.Equal(7, JsonNumber.Int(Value("\"7\"")));
        Assert.Null(JsonNumber.Int(Value("\"seven\"")));
        Assert.Null(JsonNumber.Int(Value("true")));
        Assert.Null(JsonNumber.Int(Value("{}")));
        Assert.Null(JsonNumber.Int(null));
    }

    [Fact]
    public void Created_values_work_too()
    {
        Assert.Equal(4, JsonNumber.Int(JsonValue.Create(4.0)));
        Assert.Equal(9, JsonNumber.Int(JsonValue.Create(9L)));
    }
}
