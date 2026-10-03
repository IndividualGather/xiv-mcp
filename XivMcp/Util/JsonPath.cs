using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Tiny path syntax for navigating JSON documents: <c>Foo.Bar[2].Baz</c>, with <c>["key.with.dots"]</c> for awkward keys.
/// </summary>
internal static class JsonPath
{
    public static List<object> Parse(string? path)
    {
        var segments = new List<object>();
        if (string.IsNullOrWhiteSpace(path) || path is "$" or ".") return segments;
        var p = path.StartsWith("$", StringComparison.Ordinal) ? path[1..] : path;
        var i = 0;
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0) segments.Add(current.ToString());
            current.Clear();
        }

        while (i < p.Length)
        {
            var c = p[i];
            if (c == '.') { Flush(); i++; }
            else if (c == '[')
            {
                Flush();
                var end = p.IndexOf(']', i);
                if (end < 0) throw new ToolException($"Unclosed '[' in path '{path}'.");
                var inner = p[(i + 1)..end].Trim();
                if (inner.Length >= 2 && (inner[0] is '"' or '\'') && inner[^1] == inner[0])
                {
                    // quoted key may contain ']' — find the matching quote + ']'
                    var quote = inner[0];
                    var close = p.IndexOf(quote + "]", i + 2, StringComparison.Ordinal);
                    segments.Add(p[(i + 2)..close]);
                    end = close + 1;
                }
                else if (int.TryParse(inner, out var index)) segments.Add(index);
                else segments.Add(inner);
                i = end + 1;
            }
            else { current.Append(c); i++; }
        }
        Flush();
        return segments;
    }

    public static string Format(IEnumerable<object> segments)
    {
        var sb = new StringBuilder();
        foreach (var s in segments)
        {
            if (s is int i) sb.Append('[').Append(i).Append(']');
            else if (((string)s).IndexOfAny(['.', '[', ']']) >= 0) sb.Append("[\"").Append(s).Append("\"]");
            else { if (sb.Length > 0) sb.Append('.'); sb.Append(s); }
        }
        return sb.Length == 0 ? "$" : sb.ToString();
    }

    public static JsonNode? Get(JsonNode? root, List<object> segments)
    {
        var node = root;
        for (var i = 0; i < segments.Count; i++)
        {
            node = Step(node, segments[i], out var error);
            if (error is not null) throw new ToolException($"Path '{Format(segments.Take(i + 1))}': {error}");
        }
        return node;
    }

    /// <summary>Sets a value; returns the previous value (null if it did not exist).</summary>
    public static JsonNode? Set(JsonNode root, List<object> segments, JsonNode? value, bool createMissing)
    {
        if (segments.Count == 0) throw new ToolException("Cannot replace the whole document; give a path.");
        var parent = Get(root, segments.Take(segments.Count - 1).ToList());
        var last = segments[^1];
        var where = Format(segments);

        switch (parent)
        {
            case JsonObject obj:
            {
                var key = last.ToString()!;
                var actualKey = obj.Select(kv => kv.Key).FirstOrDefault(k => k == key)
                                ?? obj.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (actualKey is null && !createMissing)
                    throw new ToolException($"'{where}' does not exist. Pass create_missing=true to add it. Existing keys: {string.Join(", ", obj.Select(kv => kv.Key).Take(40))}");
                actualKey ??= key;
                var old = obj[actualKey]?.DeepClone();
                obj[actualKey] = value;
                return old;
            }
            case JsonArray arr when last is int index:
            {
                if (index == arr.Count && createMissing) { arr.Add(value); return null; }
                if (index < 0 || index >= arr.Count) throw new ToolException($"'{where}': index out of range (array has {arr.Count} items).");
                var old = arr[index]?.DeepClone();
                arr[index] = value;
                return old;
            }
            default:
                throw new ToolException($"'{Format(segments.Take(segments.Count - 1))}' is not an object or array.");
        }
    }

    private static JsonNode? Step(JsonNode? node, object segment, out string? error)
    {
        error = null;
        switch (node)
        {
            case JsonObject obj:
            {
                var key = segment.ToString()!;
                if (obj.TryGetPropertyValue(key, out var v)) return v;
                var ci = obj.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (ci.Key is not null) return ci.Value;
                error = $"key not found. Keys here: {string.Join(", ", obj.Select(kv => kv.Key).Take(40))}";
                return null;
            }
            case JsonArray arr when segment is int i:
                if (i >= 0 && i < arr.Count) return arr[i];
                error = $"index out of range (array has {arr.Count} items)";
                return null;
            default:
                error = node is null ? "value is null" : $"cannot index into a {node.GetValueKind()}";
                return null;
        }
    }

    /// <summary>Copy of a node with everything below <paramref name="depth"/> collapsed into short placeholders.</summary>
    public static JsonNode? Summarize(JsonNode? node, int depth) => node switch
    {
        JsonObject obj when depth <= 0 => JsonValue.Create($"{{object with {obj.Count} keys}}"),
        JsonArray arr when depth <= 0 => JsonValue.Create($"[array with {arr.Count} items]"),
        JsonObject obj => new JsonObject(obj.Select(kv => KeyValuePair.Create(kv.Key, Summarize(kv.Value, depth - 1)))),
        JsonArray arr => new JsonArray(arr.Select(n => Summarize(n, depth - 1)).ToArray()),
        _ => node?.DeepClone(),
    };

    public static string Kind(JsonNode? n) => n?.GetValueKind() switch
    {
        null => "Null",
        System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "Boolean",
        var k => k.Value.ToString(),
    };
}
