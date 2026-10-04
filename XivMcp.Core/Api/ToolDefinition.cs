using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Api;

/// <summary>A third-party tool as registered through XivMcp.RegisterTool, validated.</summary>
public sealed record ToolDefinition(string Name, string Description, JsonObject InputSchema, bool ReadOnly, bool Destructive, IReadOnlyList<string> Capabilities);

public static partial class ToolDefinitionParser
{
    [GeneratedRegex("^[a-z][a-z0-9_]{2,63}$")]
    private static partial Regex NamePattern();

    /// <summary>Parses and validates a definition; throws ToolException with a message for the plugin developer.</summary>
    public static ToolDefinition Parse(string json)
    {
        JsonObject def;
        try { def = JsonNode.Parse(json) as JsonObject ?? throw new ToolException("The tool definition must be a JSON object."); }
        catch (JsonException ex) { throw new ToolException($"The tool definition is not valid JSON: {ex.Message}"); }

        var name = Text(def, "name") ?? throw new ToolException("The tool definition needs 'name'.");
        if (!NamePattern().IsMatch(name))
            throw new ToolException($"Invalid tool name '{name}': use 3-64 characters a-z, 0-9 and _, starting with a letter (e.g. \"myplugin_do_thing\").");
        var description = Text(def, "description");
        if (string.IsNullOrWhiteSpace(description))
            throw new ToolException("The tool definition needs a 'description' (it is what the assistant reads to decide when to use the tool).");

        var schema = def["inputSchema"] ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        if (schema is not JsonObject so || so["type"] is not JsonValue t || !t.TryGetValue<string>(out var type) || type != "object")
            throw new ToolException("'inputSchema' must be a JSON schema object with \"type\": \"object\".");

        var readOnly = Flag(def, "readOnly");
        var destructive = Flag(def, "destructive");
        if (readOnly && destructive) throw new ToolException("A tool can't be both readOnly and destructive.");

        var declared = new List<string>();
        if (def["capabilities"] is JsonArray arr)
            foreach (var node in arr)
            {
                var id = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (id is null || XivMcp.Permissions.Capabilities.Find(id) is null)
                    throw new ToolException($"Unknown capability '{node?.ToJsonString()}'. Valid: {string.Join(", ", XivMcp.Permissions.Capabilities.All.Select(c => c.Id))}.");
                if (!declared.Contains(id)) declared.Add(id);
            }
        else if (def["capabilities"] is not null) throw new ToolException("'capabilities' must be an array of capability ids.");

        var acting = declared.Where(c => c != XivMcp.Permissions.Capabilities.ReadGame).ToList();
        if (readOnly && acting.Count > 0)
            throw new ToolException($"A read-only tool can't declare {string.Join(", ", acting)}; drop readOnly or the capabilities.");
        if (!readOnly && acting.Count == 0)
            throw new ToolException("A tool that isn't readOnly must declare its 'capabilities' (what it does, e.g. [\"move_character\", \"spend_gil\"]), " +
                                    "so the player can decide what to allow.");

        return new ToolDefinition(name, description.Trim(), (JsonObject)so.DeepClone(), readOnly, destructive,
            [XivMcp.Permissions.Capabilities.ReadGame, .. acting]);
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Flag(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}

/// <summary>A plugin's reply to XIV MCP's Invoke: a result, an error, or "pending" (finished later with CompleteCall / FailCall).</summary>
public abstract record PluginReply
{
    public sealed record Result(JsonNode? Value) : PluginReply;
    public sealed record Failure(string Message) : PluginReply;
    public sealed record Pending : PluginReply;

    public static PluginReply Parse(string? reply, string plugin)
    {
        if (string.IsNullOrWhiteSpace(reply)) return new Result(null);
        JsonObject o;
        try
        {
            o = JsonNode.Parse(reply) as JsonObject
                ?? throw new ToolException($"{plugin} returned an invalid reply: expected {{\"result\": …}}, {{\"error\": \"…\"}} or {{\"pending\": true}}.");
        }
        catch (JsonException ex) { throw new ToolException($"{plugin} returned invalid JSON: {ex.Message}"); }

        if (o.ContainsKey("error")) return new Failure(o["error"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : o["error"]?.ToJsonString() ?? "Failed.");
        if (o["pending"] is JsonValue p && p.TryGetValue<bool>(out var pending) && pending) return new Pending();
        if (o.ContainsKey("result")) return new Result(o["result"]?.DeepClone());
        throw new ToolException($"{plugin} returned an invalid reply: expected {{\"result\": …}}, {{\"error\": \"…\"}} or {{\"pending\": true}}.");
    }
}
