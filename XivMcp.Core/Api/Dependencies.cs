using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using XivMcp.Integrations;
using XivMcp.Mcp;

namespace XivMcp.Api;

/// <summary>
/// A tool a third-party plugin's jobs use, as declared through XivMcp.DeclareDependencies. A built-in tool (XIV MCP's own or one of
/// its integrations) is named alone; another plugin's tool also names that plugin, where to install it and the version it needs.
/// </summary>
public sealed record ToolDependency(string Tool, string? Plugin = null, string? PluginName = null, string? Repo = null, Version? MinVersion = null)
{
    public bool BuiltIn => Plugin is null;
}

public static partial class DependencyParser
{
    public const int MaxDependencies = 64;

    [GeneratedRegex("^[a-z][a-z0-9_]{2,63}$")]
    private static partial Regex ToolPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex PluginPattern();

    /// <summary>
    /// Parses {"tools": [{"tool": "navigate_to"}, {"tool": "x_scan", "plugin": "X", "name": "X", "repo": "https://…/repo.json",
    /// "minVersion": "1.2.0"}]}; throws ToolException with a message for the plugin developer. An empty list clears the declaration.
    /// </summary>
    public static IReadOnlyList<ToolDependency> Parse(string json, Func<string, bool> isBuiltIn)
    {
        JsonObject root;
        try { root = JsonNode.Parse(json) as JsonObject ?? throw new ToolException("The dependencies must be a JSON object { \"tools\": [ … ] }."); }
        catch (JsonException ex) { throw new ToolException($"The dependencies are not valid JSON: {ex.Message}"); }
        if (root["tools"] is not JsonArray tools) throw new ToolException("The dependencies need a 'tools' array.");
        if (tools.Count > MaxDependencies) throw new ToolException($"Declare at most {MaxDependencies} tools.");

        var result = new List<ToolDependency>();
        foreach (var node in tools)
        {
            if (node is not JsonObject o) throw new ToolException("Each dependency must be an object with at least 'tool'.");
            var tool = Text(o, "tool") ?? throw new ToolException("Each dependency needs 'tool'.");
            if (!ToolPattern().IsMatch(tool)) throw new ToolException($"'{tool}' is not a valid tool name.");
            if (result.Any(d => d.Tool == tool)) throw new ToolException($"'{tool}' is declared twice.");

            var claimsPlugin = o.ContainsKey("plugin") || o.ContainsKey("repo") || o.ContainsKey("minVersion") || o.ContainsKey("name");
            if (isBuiltIn(tool))
            {
                if (claimsPlugin)
                    throw new ToolException($"'{tool}' is built into XIV MCP; declare it with 'tool' alone. XIV MCP knows which plugins it needs.");
                result.Add(new ToolDependency(tool));
                continue;
            }

            string Required(string key) => Text(o, key) is { Length: > 0 } s ? s
                : throw new ToolException($"'{tool}' is not an XIV MCP tool. For another plugin's tool, give 'plugin' (its internal name), " +
                                          $"'name', 'repo' (its repo.json URL, or \"official\") and 'minVersion'; '{key}' is missing.");

            var plugin = Required("plugin");
            if (!PluginPattern().IsMatch(plugin)) throw new ToolException($"'{plugin}' is not a valid plugin internal name.");
            var name = Required("name");
            var repo = Required("repo");
            if (repo != PluginCatalog.Official && !(Uri.TryCreate(repo, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps))
                throw new ToolException($"'repo' of '{tool}' must be the https URL of the plugin's repo.json, or \"official\" for Dalamud's main repository.");
            var versionText = Required("minVersion");
            if (!Version.TryParse(versionText, out var version))
                throw new ToolException($"'minVersion' of '{tool}' must be a version like \"1.2.0\", not '{versionText}'.");
            result.Add(new ToolDependency(tool, plugin, name.Length > 64 ? name[..64] : name, repo, version));
        }
        return result;
    }

    private static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null;
}

/// <summary>An installed plugin, as Dalamud reports it.</summary>
public sealed record PluginPresence(string InternalName, string Name, Version? Version, bool Loaded);

/// <summary>Where a dependency stands. Everything except <see cref="Ok"/> keeps the job step from running.</summary>
public enum DependencyState
{
    Ok,
    /// <summary>An integration tool needs its plugin, which isn't installed or not enabled in Dalamud. (Core tools work without plugins.)</summary>
    NeedsPlugin,
    /// <summary>Another plugin's tool, and that plugin isn't installed.</summary>
    PluginMissing,
    /// <summary>Installed, but disabled in Dalamud.</summary>
    PluginNotLoaded,
    /// <summary>Installed, but older than the declared minimum version.</summary>
    PluginOutdated,
    /// <summary>The plugin is loaded but hasn't registered the tool (or someone else registered that name).</summary>
    ToolMissing,
    /// <summary>The tool is there, but the player hasn't enabled its plugin in XIV MCP.</summary>
    NotEnabled,
}

/// <summary>A plugin to install, enable or update for a dependency. <see cref="Needed"/> is false for plugins that only improve a tool.</summary>
public sealed record PluginToInstall(string InternalName, string Name, string Repo, Version? MinVersion, string Reason, bool Needed, bool Installed, bool Outdated,
                                     string? Without = null);

public sealed record DependencyStatus(ToolDependency Dependency, DependencyState State, string Message, IReadOnlyList<PluginToInstall> Install)
{
    public bool IsError => State != DependencyState.Ok;
}

public static class DependencyCheck
{
    /// <param name="installed">The installed plugin with that internal name, or null.</param>
    /// <param name="toolOwner">The plugin (provider id) that registered a tool, or null.</param>
    /// <param name="enabledInXivMcp">Whether the player enabled a third-party plugin in XIV MCP.</param>
    public static DependencyStatus Check(ToolDependency d, Func<string, PluginPresence?> installed, Func<string, string?> toolOwner,
                                         Func<string, bool> enabledInXivMcp) =>
        d.BuiltIn ? CheckBuiltIn(d, installed) : CheckOther(d, installed, toolOwner, enabledInXivMcp);

    private static DependencyStatus CheckBuiltIn(ToolDependency d, Func<string, PluginPresence?> installed)
    {
        var install = new List<PluginToInstall>();
        var missingNames = new List<string>();
        foreach (var r in ToolRequirements.For(d.Tool))
        {
            // Any one of a plugin and its alternatives will do (Saucy or TriadBuddy).
            if (new[] { r.PluginId }.Concat(r.Alternatives).Any(id => installed(id) is { Loaded: true })) continue;
            var p = installed(r.PluginId);
            var known = PluginCatalog.Find(r.PluginId);
            install.Add(new PluginToInstall(r.PluginId, known?.Name ?? r.PluginId, known?.Repo ?? PluginCatalog.Official, null, r.Purpose,
                r.Need == Need.Needed, p is not null, false, r.Without));
            if (r.Need == Need.Needed) missingNames.Add(r.Names(id => PluginCatalog.Find(id)?.Name ?? id));
        }
        if (missingNames.Count == 0)
            return new DependencyStatus(d, DependencyState.Ok,
                install.Count == 0 ? "Ready." : "Ready. " + string.Join(" ", install.Select(p => $"Without {p.Name}: {p.Without ?? "it works, just not as well."}")), install);
        return new DependencyStatus(d, DependencyState.NeedsPlugin,
            $"Needs {string.Join(" and ", missingNames)}, which {(missingNames.Count == 1 ? "is" : "are")} not installed or not enabled.", install);
    }

    private static DependencyStatus CheckOther(ToolDependency d, Func<string, PluginPresence?> installed, Func<string, string?> toolOwner,
                                               Func<string, bool> enabledInXivMcp)
    {
        var plugin = d.Plugin!;
        var p = installed(plugin);
        PluginToInstall Install(bool outdated) =>
            new(plugin, d.PluginName ?? plugin, d.Repo ?? PluginCatalog.Official, d.MinVersion, $"Provides {d.Tool}.", true, p is not null, outdated);

        if (p is null)
            return new DependencyStatus(d, DependencyState.PluginMissing, $"Needs {d.PluginName} {d.MinVersion} or newer, which is not installed.", [Install(false)]);
        if (d.MinVersion is { } min && p.Version is { } have && Full(have) < Full(min))
            return new DependencyStatus(d, DependencyState.PluginOutdated, $"Needs {d.PluginName} {min} or newer; {have} is installed.", [Install(true)]);
        if (!p.Loaded)
            return new DependencyStatus(d, DependencyState.PluginNotLoaded, $"{d.PluginName} is installed but not enabled in Dalamud.", [Install(false)]);
        if (!string.Equals(toolOwner(d.Tool), plugin, StringComparison.OrdinalIgnoreCase))
            return new DependencyStatus(d, DependencyState.ToolMissing, $"{d.PluginName} is loaded but has not registered {d.Tool} with XIV MCP.", []);
        if (!enabledInXivMcp(plugin))
            return new DependencyStatus(d, DependencyState.NotEnabled, $"{d.PluginName} is not enabled in XIV MCP → Third-party plugins.", []);
        return new DependencyStatus(d, DependencyState.Ok, "Ready.", []);
    }

    private static string Names(IEnumerable<PluginToInstall> plugins) => string.Join(" and ", plugins.Select(p => p.Name));

    /// <summary>Missing parts count as 0, so 1.2 equals 1.2.0 (System.Version ranks 1.2 below 1.2.0).</summary>
    private static Version Full(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
