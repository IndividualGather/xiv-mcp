using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XivMcp.Mcp;

namespace XivMcp.Jobs;

/// <summary>
/// Job step arguments can use earlier results: a string that is exactly "{{stepId.path}}" becomes that value (keeping its type); inside a
/// longer string each placeholder is replaced by the value as text. Paths walk objects (case-insensitive) and arrays (index).
/// </summary>
public static partial class Placeholders
{
    [GeneratedRegex(@"^\{\{([A-Za-z0-9_-]+)\.([^}]+)\}\}$")]
    private static partial Regex Whole();

    [GeneratedRegex(@"\{\{([A-Za-z0-9_-]+)\.([^}]+)\}\}")]
    private static partial Regex Embedded();

    /// <summary>A resolved copy of <paramref name="args"/>. <paramref name="resultOf"/> returns a step's result or throws ToolException.</summary>
    public static JsonObject Resolve(JsonObject args, Func<string, JsonNode?> resultOf)
    {
        var copy = (JsonObject)args.DeepClone();
        Walk(copy, resultOf);
        return copy;
    }

    private static void Walk(JsonNode? node, Func<string, JsonNode?> resultOf)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                    if (o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("{{")) o[key] = Substitute(s, resultOf);
                    else Walk(o[key], resultOf);
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains("{{")) a[i] = Substitute(s, resultOf);
                    else Walk(a[i], resultOf);
                break;
        }
    }

    private static JsonNode? Substitute(string s, Func<string, JsonNode?> resultOf)
    {
        if (Whole().Match(s) is { Success: true } whole) return Lookup(whole.Groups[1].Value, whole.Groups[2].Value, resultOf);
        if (!Embedded().IsMatch(s)) return JsonValue.Create(s);
        return JsonValue.Create(Embedded().Replace(s, m => Lookup(m.Groups[1].Value, m.Groups[2].Value, resultOf) switch
        {
            JsonValue v when v.TryGetValue<string>(out var text) => text,
            null => "",
            var n => n.ToJsonString(),
        }));
    }

    private static JsonNode? Lookup(string stepId, string path, Func<string, JsonNode?> resultOf)
    {
        var node = resultOf(stepId);
        foreach (var part in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o.FirstOrDefault(kv => kv.Key.Equals(part, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray a when int.TryParse(part, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null,
            };
            if (node is null) throw new ToolException($"Step '{stepId}' result has no '{path}'.");
        }
        return node?.DeepClone();
    }
}
