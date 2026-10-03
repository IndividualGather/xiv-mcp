using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace XivMcp.Mcp;

/// <summary>A single MCP tool: metadata plus the handler that produces its (JSON-serializable) result.</summary>
public sealed class McpTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>JSON schema of the arguments, as raw JSON. Defaults to an empty object schema.</summary>
    public string InputSchema { get; init; } = """{ "type": "object", "properties": {} }""";

    public required Func<ToolArgs, CancellationToken, Task<object?>> Handler { get; init; }

    /// <summary>False for tools that change state (plugin management).</summary>
    public bool ReadOnly { get; init; } = true;

    /// <summary>True for tools whose changes may be hard to undo (e.g. overwriting config files).</summary>
    public bool Destructive { get; init; }

    /// <summary>When set, the tool is only listed (and callable) while this returns true, e.g. while the plugin it drives is loaded.</summary>
    public Func<bool>? Available { get; init; }

    public bool IsAvailable
    {
        get
        {
            try { return Available?.Invoke() ?? true; }
            catch { return false; }
        }
    }

    /// <summary>The input schema; a broken one is logged and replaced by an empty schema, so one tool can't break tools/list for all.</summary>
    public JsonNode ParsedSchema()
    {
        try { return JsonNode.Parse(InputSchema) ?? new JsonObject { ["type"] = "object" }; }
        catch (System.Text.Json.JsonException ex)
        {
            XivMcp.Svc.Log.Error($"[MCP] Tool {Name} has an invalid input schema: {ex.Message}");
            return new JsonObject { ["type"] = "object" };
        }
    }

    public JsonObject ToListEntry() => new()
    {
        ["name"] = Name,
        ["description"] = Description,
        ["inputSchema"] = ParsedSchema(),
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = ReadOnly,
            ["destructiveHint"] = Destructive,
            ["openWorldHint"] = false,
        },
    };
}

/// <summary>A PNG returned as an MCP image content block, optionally preceded by a caption text block.</summary>
public sealed record ToolImage(byte[] Png, string? Caption = null);

/// <summary>Tool result that carries images besides its JSON data.</summary>
public sealed record ToolResultWithImages(object? Data, IReadOnlyList<ToolImage> Images);

/// <summary>Thrown by tool handlers for user-facing errors (reported as an MCP tool error, not a protocol error).</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>Typed access to the "arguments" object of a tools/call request.</summary>
public sealed class ToolArgs(JsonObject? args)
{
    private readonly JsonObject args = args ?? [];

    public JsonNode? Node(string name) => args[name];

    public string? String(string name)
    {
        var node = args[name];
        if (node is null) return null;
        var s = node is JsonValue v && v.TryGetValue<string>(out var str) ? str : node.ToJsonString();
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    public int Int(string name, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        var node = args[name];
        int value = fallback;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) value = i;
            else if (v.TryGetValue<double>(out var d)) value = (int)d;
            else if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var p)) value = p;
        }
        return Math.Clamp(value, min, max);
    }

    public uint? UInt(string name)
    {
        var node = args[name];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<uint>(out var u)) return u;
        if (v.TryGetValue<long>(out var l) && l >= 0) return (uint)l;
        if (v.TryGetValue<double>(out var d) && d >= 0) return (uint)d;
        if (v.TryGetValue<string>(out var s) && uint.TryParse(s, out var p)) return p;
        return null;
    }

    public float? Float(string name)
    {
        var node = args[name];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return (float)d;
        if (v.TryGetValue<string>(out var s) && float.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p)) return p;
        return null;
    }

    public bool Bool(string name, bool fallback)
    {
        var node = args[name];
        if (node is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p)) return p;
        }
        return fallback;
    }

    /// <summary>Accepts either a JSON array of strings or a comma separated string.</summary>
    public List<string> StringList(string name)
    {
        var node = args[name];
        if (node is JsonArray arr)
            return arr.Select(n => n?.ToString().Trim() ?? "").Where(s => s.Length > 0).ToList();
        var single = String(name);
        return single is null ? [] : single.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    /// <summary>A JSON array of strings exactly as given (no trimming, empty entries kept, no comma splitting) — e.g. macro lines.</summary>
    public List<string> StringListRaw(string name)
    {
        if (args[name] is not JsonArray arr) throw new ToolException($"'{name}' must be an array of strings.");
        return arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "").ToList();
    }

    /// <summary>Accepts either a JSON array of numbers or a comma separated string.</summary>
    public List<uint> UIntList(string name)
    {
        var node = args[name];
        if (node is JsonArray arr)
            return arr.Select(n => n is JsonValue v && v.TryGetValue<long>(out var l) ? (uint?)l : uint.TryParse(n?.ToString(), out var p) ? p : null)
                      .Where(u => u.HasValue).Select(u => u!.Value).ToList();
        if (node is JsonValue single && single.TryGetValue<long>(out var one)) return [(uint)one];
        return StringList(name).Select(s => uint.TryParse(s, out var p) ? (uint?)p : null).Where(u => u.HasValue).Select(u => u!.Value).ToList();
    }
}
