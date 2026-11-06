using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace XivMcp.Mcp;

/// <summary>
/// Lenient number reading from JSON that XIV MCP doesn't control (Teamcraft data, plugin configs). <c>GetValue&lt;int&gt;</c> throws
/// on 1.5, 12.0 or 3000000000; these helpers round and clamp instead, and return null for anything that isn't a number.
/// </summary>
public static class JsonNumber
{
    public static int? Int(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return FromDouble(d);
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return FromDouble(p);
        return null;
    }

    private static int? FromDouble(double d) =>
        double.IsNaN(d) ? null : (int)Math.Clamp(Math.Round(d, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
}
