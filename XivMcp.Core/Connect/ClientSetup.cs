using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace XivMcp.Connect;

/// <summary>
/// What each AI client needs to connect to XIV MCP: a Claude Desktop extension (.mcpb), install links for VS Code, Cursor and
/// LM Studio, and the entry in Codex's config.toml. The server is always added under the name <see cref="ServerName"/>.
/// </summary>
public static class ClientSetup
{
    public const string ServerName = "ffxiv";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // ---------------------------------------------------------------- Claude Desktop

    /// <summary>
    /// The manifest of the Claude Desktop extension. Extensions run a local program over stdio, so the bundle carries a small bridge
    /// (<c>server/index.js</c>, run by the Node.js that ships with Claude Desktop) that forwards to the server's address.
    /// </summary>
    public static string McpbManifest(string version, string endpoint, string? token, bool icon = true)
    {
        var env = new JsonObject { ["XIVMCP_URL"] = endpoint };
        if (token is not null) env["XIVMCP_TOKEN"] = token;
        var manifest = new JsonObject
        {
            ["manifest_version"] = "0.3",
            ["name"] = "xiv-mcp",
            ["display_name"] = "Final Fantasy XIV (XIV MCP)",
            ["version"] = version,
            ["description"] = "Your character, inventory, retainers and the game world, live from Final Fantasy XIV.",
            ["long_description"] = "Connects Claude to the XIV MCP plugin running in Final Fantasy XIV (Dalamud). Claude can read your character, " +
                                   "inventory, retainers, quests and game data, and, if you allow it in /xivmcp in game, act for you. The game must be " +
                                   "running; while it isn't, the extension stays connected without tools and picks the game up when it starts.",
            ["author"] = new JsonObject { ["name"] = "IndividualGather" },
            ["server"] = new JsonObject
            {
                ["type"] = "node",
                ["entry_point"] = "server/index.js",
                ["mcp_config"] = new JsonObject
                {
                    ["command"] = "node",
                    ["args"] = new JsonArray("${__dirname}/server/index.js"),
                    ["env"] = env,
                },
            },
            // The tools come from the game and depend on its settings, so none are listed here.
            ["tools_generated"] = true,
            ["keywords"] = new JsonArray("ffxiv", "final fantasy xiv", "dalamud", "game"),
            ["compatibility"] = new JsonObject
            {
                ["platforms"] = new JsonArray("win32", "darwin"),
                ["runtimes"] = new JsonObject { ["node"] = ">=16.0.0" },
            },
        };
        if (icon) manifest["icon"] = "icon.png";
        return manifest.ToJsonString(Indented);
    }

    /// <summary>Writes the .mcpb bundle (a zip): manifest.json, server/index.js and, if given, icon.png.</summary>
    public static void WriteMcpb(Stream output, string version, string endpoint, string? token, byte[]? icon)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        Add(zip, "manifest.json", Encoding.UTF8.GetBytes(McpbManifest(version, endpoint, token, icon is not null)));
        Add(zip, "server/index.js", Encoding.UTF8.GetBytes(Bridge));
        if (icon is not null) Add(zip, "icon.png", icon);
    }

    private static void Add(ZipArchive zip, string name, byte[] content)
    {
        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        entry.Write(content);
    }

    /// <summary>The stdio-to-HTTP bridge script (embedded in this assembly).</summary>
    public static string Bridge { get; } = ReadResource("bridge.js");

    private static string ReadResource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return reader.ReadToEnd();
    }

    // ---------------------------------------------------------------- install links

    private static JsonObject HttpServer(string endpoint, string? token)
    {
        var server = new JsonObject { ["url"] = endpoint };
        if (token is not null) server["headers"] = new JsonObject { ["Authorization"] = $"Bearer {token}" };
        return server;
    }

    /// <summary>VS Code (GitHub Copilot): opens VS Code, which asks to add the server to the user's MCP configuration.</summary>
    public static string VsCodeLink(string endpoint, string? token)
    {
        var config = new JsonObject { ["name"] = ServerName, ["type"] = "http" };
        foreach (var (k, v) in HttpServer(endpoint, token)) config[k] = v?.DeepClone();
        return "vscode:mcp/install?" + Uri.EscapeDataString(config.ToJsonString());
    }

    /// <summary>Cursor: opens Cursor's install prompt.</summary>
    public static string CursorLink(string endpoint, string? token) =>
        $"cursor://anysphere.cursor-deeplink/mcp/install?name={ServerName}&config={Base64(HttpServer(endpoint, token))}";

    /// <summary>LM Studio: opens LM Studio's install prompt.</summary>
    public static string LmStudioLink(string endpoint, string? token) =>
        $"lmstudio://add_mcp?name={ServerName}&config={Base64(HttpServer(endpoint, token))}";

    private static string Base64(JsonObject json) => Uri.EscapeDataString(Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())));

    // ---------------------------------------------------------------- Claude Code

    /// <summary>Arguments for <c>claude</c> that add XIV MCP for all the player's projects (user scope).</summary>
    public static string[] ClaudeCodeAddArgs(string endpoint, string? token) =>
        token is null
            ? ["mcp", "add", "--scope", "user", "--transport", "http", ServerName, endpoint]
            : ["mcp", "add", "--scope", "user", "--transport", "http", ServerName, endpoint, "--header", $"Authorization: Bearer {token}"];

    /// <summary>Removes an earlier user-scope entry, so adding again updates it. Project entries are left alone.</summary>
    public static string[] ClaudeCodeRemoveArgs(string scope = "user") => ["mcp", "remove", "--scope", scope, ServerName];

    // ---------------------------------------------------------------- Codex

    /// <summary>The section header of the entry, and of its sub-tables (e.g. [mcp_servers.ffxiv.env]).</summary>
    private static readonly Regex OwnHeader = new($@"^\s*\[\s*mcp_servers\.{ServerName}\s*(\.[^\]]*)?\]\s*$");
    private static readonly Regex AnyHeader = new(@"^\s*\[");

    public static bool HasCodexEntry(string toml) => SplitLines(toml).Any(l => OwnHeader.IsMatch(l));

    /// <summary>
    /// Codex's config.toml (shared by the Codex CLI, IDE extension and app) with the XIV MCP entry added or replaced in place. Every
    /// other line is kept as it was, and the file's line endings are kept.
    /// </summary>
    public static string MergeCodexConfig(string existing, string endpoint, string? token)
    {
        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var entry = new StringBuilder()
            .Append($"[mcp_servers.{ServerName}]").Append(newline)
            .Append($"url = {Quote(endpoint)}").Append(newline);
        if (token is not null) entry.Append($"http_headers = {{ \"Authorization\" = {Quote("Bearer " + token)} }}").Append(newline);

        var lines = SplitLines(existing);
        var start = lines.FindIndex(l => OwnHeader.IsMatch(l));
        if (start < 0)
        {
            var text = existing.Length == 0 || existing.EndsWith('\n') ? existing : existing + newline;
            if (text.Length > 0 && !text.EndsWith(newline + newline)) text += newline;
            return text + entry;
        }

        // Replace from the entry's header up to the next table that isn't one of its own sub-tables.
        var end = start + 1;
        while (end < lines.Count && !(AnyHeader.IsMatch(lines[end]) && !OwnHeader.IsMatch(lines[end]))) end++;
        var before = string.Concat(lines.Take(start).Select(l => l + newline));
        var after = string.Concat(lines.Skip(end).Select(l => l + newline));
        if (after.Length > 0) entry.Append(newline);
        var merged = before + entry + after;
        // SplitLines adds a final empty line when the file ended with a newline; don't double it.
        return existing.EndsWith('\n') || merged.Length == 0 ? merged : merged.TrimEnd('\r', '\n');
    }

    /// <summary>
    /// Codex's config.toml without the XIV MCP entry (and its sub-tables). The blank lines that separated it go with it, so removing
    /// an entry <see cref="MergeCodexConfig"/> added gives back the file as it was. Without an entry, the file is returned unchanged.
    /// </summary>
    public static string RemoveCodexEntry(string existing)
    {
        var lines = SplitLines(existing);
        var start = lines.FindIndex(l => OwnHeader.IsMatch(l));
        if (start < 0) return existing;
        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var end = start + 1;
        while (end < lines.Count && !(AnyHeader.IsMatch(lines[end]) && !OwnHeader.IsMatch(lines[end]))) end++;
        // At the end of the file, the blank lines before the entry belonged to it too.
        if (end == lines.Count) while (start > 0 && lines[start - 1].Trim().Length == 0) start--;
        var kept = lines.Take(start).Concat(lines.Skip(end)).ToList();
        return string.Concat(kept.Select(l => l + newline));
    }

    private static System.Collections.Generic.List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
