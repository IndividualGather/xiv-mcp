using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Connect;

/// <summary>
/// Whether XIV MCP is already set up in an AI app, read from the app's own configuration (so the Connect tab can say "Set up" and
/// stop recommending an app once one is connected). Every check is forgiving: a file that can't be read counts as not set up.
/// </summary>
public static class SetupStatus
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonObject? Parse(string json)
    {
        try { return JsonNode.Parse(json, documentOptions: Lenient) as JsonObject; }
        catch { return null; }
    }

    /// <summary>A JSON config with XIV MCP under <paramref name="key"/> ("servers" for VS Code, "mcpServers" for Cursor and LM Studio).</summary>
    public static bool HasServer(string json, string key) =>
        Parse(json)?[key] is JsonObject servers && servers.ContainsKey(ClientSetup.ServerName);

    /// <summary>LM Studio's Bionic app (ng-mcp.json): a server named like XIV MCP's that is switched on.</summary>
    public static bool BionicHasServer(string json) =>
        Parse(json)?["servers"] is JsonArray servers &&
        servers.OfType<JsonObject>().Any(s => s["name"]?.GetValue<string>() == ClientSetup.ServerName && s["enabled"]?.GetValue<bool>() != false);

    /// <summary>Claude Code's ~/.claude.json: XIV MCP for all projects (top-level mcpServers) or for any one project.</summary>
    public static bool ClaudeCodeHasServer(string json)
    {
        if (Parse(json) is not { } root) return false;
        if (root["mcpServers"] is JsonObject user && user.ContainsKey(ClientSetup.ServerName)) return true;
        return root["projects"] is JsonObject projects && projects.Select(p => p.Value?["mcpServers"]).OfType<JsonObject>().Any(s => s.ContainsKey(ClientSetup.ServerName));
    }

    /// <summary>The project folders in ~/.claude.json that have their own XIV MCP entry (added with the local scope).</summary>
    public static IReadOnlyList<string> ClaudeCodeProjects(string json) =>
        Parse(json)?["projects"] is JsonObject projects
            ? projects.Where(p => p.Value?["mcpServers"] is JsonObject s && s.ContainsKey(ClientSetup.ServerName)).Select(p => p.Key).ToList()
            : [];

    /// <summary>The extension's folder name ends in its manifest name (Claude Desktop names it local.mcpb.&lt;author&gt;.&lt;name&gt;).</summary>
    public const string ExtensionSuffix = ".xiv-mcp";

    /// <summary>
    /// Claude Desktop: the XIV MCP extension is among the installed extension folders and isn't switched off in its settings
    /// (<paramref name="settingsJson"/>, if there is a settings file).
    /// </summary>
    public static bool ClaudeExtensionEnabled(IEnumerable<string> extensionFolders, string? settingsJson)
    {
        if (!extensionFolders.Any(f => Path.GetFileName(f).EndsWith(ExtensionSuffix, StringComparison.OrdinalIgnoreCase))) return false;
        return settingsJson is null || Parse(settingsJson)?["isEnabled"]?.GetValue<bool>() != false;
    }
}
