using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Managing other Dalamud plugins: list, enable/disable/reload, read and edit their configuration files.</summary>
internal static class PluginTools
{
    private const int MaxBackupsPerFile = 10;
    private static readonly string[] TextExtensions = [".json", ".yaml", ".yml", ".txt", ".ini", ".toml", ".xml", ".cfg", ".conf", ".csv"];

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Serializes enable/disable/reload/config writes so two requests can't interleave unload/load of the same plugin.</summary>
    private static readonly System.Threading.SemaphoreSlim Gate = new(1, 1);

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        void RequireEnabled()
        {
            if (!config.AllowPluginManagement)
                throw new ToolException("Plugin management is disabled. Enable \"Allow plugin management\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "list_plugins",
            Description = "Lists all installed Dalamud plugins with name, internal name, version, load state (Loaded/Unloaded/LoadError/...) and flags " +
                          "(dev, testing, outdated, banned, in default collection). Use the internal name with the other plugin tools.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Case-insensitive filter on name or internal name." },
                    "loaded_only": { "type": "boolean", "description": "Only list loaded plugins (default false)." }
                  }
                }
                """,
            Handler = (args, _) => Task.Run<object?>(() =>
            {
                var query = args.String("query");
                var loadedOnly = args.Bool("loaded_only", false);
                var plugins = DalamudInternals.InstalledPlugins()
                    .Where(p => query is null || Game.Matches(DalamudInternals.Name(p), query) || Game.Matches(DalamudInternals.InternalName(p), query))
                    .Where(p => !loadedOnly || DalamudInternals.IsLoaded(p))
                    .Select(DalamudInternals.Describe)
                    .OrderBy(d => (string)d["name"]!, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new { managementEnabled = config.AllowPluginManagement, count = plugins.Count, plugins };
            }),
        };

        yield return new McpTool
        {
            Name = "set_plugin_enabled",
            Description = "Enables (loads) or disables (unloads) an installed Dalamud plugin, exactly like the toggle in the plugin installer. " +
                          "The state is saved in the default collection so it persists across restarts. Requires 'Allow plugin management' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "plugin": { "type": "string", "description": "Internal name (preferred) or display name of the plugin." },
                    "enabled": { "type": "boolean", "description": "true = enable/load, false = disable/unload." }
                  },
                  "required": ["plugin", "enabled"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, _) =>
            {
                RequireEnabled();
                var enabled = args.Bool("enabled", true);
                var plugin = DalamudInternals.Find(args.String("plugin") ?? throw new ToolException("'plugin' is required."));
                DalamudInternals.EnsureNotSelf(plugin);

                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var before = DalamudInternals.State(plugin);
                    bool persisted;
                    if (enabled)
                    {
                        persisted = await DalamudInternals.PersistWantState(plugin, true).ConfigureAwait(false);
                        await DalamudInternals.Load(plugin).ConfigureAwait(false);
                    }
                    else
                    {
                        await DalamudInternals.Unload(plugin).ConfigureAwait(false);
                        persisted = await DalamudInternals.PersistWantState(plugin, false).ConfigureAwait(false);
                    }
                    Svc.Log.Information($"[MCP] {(enabled ? "Enabled" : "Disabled")} plugin {DalamudInternals.InternalName(plugin)}");
                    return new
                    {
                        plugin = DalamudInternals.InternalName(plugin),
                        stateBefore = before,
                        stateAfter = DalamudInternals.State(plugin),
                        persisted,
                        note = persisted ? null : "The plugin is only managed by a non-default collection; the change applies now but the collection decides its state on the next start.",
                    };
                }
                finally { Gate.Release(); }
            },
        };

        yield return new McpTool
        {
            Name = "reload_plugin",
            Description = "Reloads a loaded Dalamud plugin (unload, then load again) — e.g. to make it pick up a changed config file or recover from a bad state. " +
                          "If the plugin is not loaded, it is simply loaded. Requires 'Allow plugin management' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": { "plugin": { "type": "string", "description": "Internal name (preferred) or display name of the plugin." } },
                  "required": ["plugin"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, _) =>
            {
                RequireEnabled();
                var plugin = DalamudInternals.Find(args.String("plugin") ?? throw new ToolException("'plugin' is required."));
                DalamudInternals.EnsureNotSelf(plugin);

                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var before = DalamudInternals.State(plugin);
                    await DalamudInternals.Unload(plugin).ConfigureAwait(false);
                    await DalamudInternals.Load(plugin).ConfigureAwait(false);
                    Svc.Log.Information($"[MCP] Reloaded plugin {DalamudInternals.InternalName(plugin)}");
                    return new { plugin = DalamudInternals.InternalName(plugin), stateBefore = before, stateAfter = DalamudInternals.State(plugin) };
                }
                finally { Gate.Release(); }
            },
        };

        yield return new McpTool
        {
            Name = "list_plugin_config_files",
            Description = "Lists the configuration files of a Dalamud plugin: its main <InternalName>.json and everything in its config directory, " +
                          "with relative path, size and last modification time. Requires 'Allow plugin management' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": { "plugin": { "type": "string", "description": "Internal name of the plugin (it does not need to be installed or loaded)." } },
                  "required": ["plugin"]
                }
                """,
            Handler = (args, _) => Task.Run<object?>(() =>
            {
                RequireEnabled();
                var internalName = ResolveInternalName(args.String("plugin"));
                var root = ConfigRoot();
                var files = new List<object>();
                var main = new FileInfo(Path.Combine(root, internalName + ".json"));
                if (main.Exists) files.Add(FileEntry(main, main.Name, isMain: true));
                var dir = new DirectoryInfo(Path.Combine(root, internalName));
                if (dir.Exists)
                    foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories).OrderBy(f => f.FullName).Take(300))
                        files.Add(FileEntry(f, Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), isMain: false));
                return new { plugin = internalName, configRoot = root, files };
            }),
        };

        yield return new McpTool
        {
            Name = "get_plugin_config",
            Description = "Reads a Dalamud plugin's configuration file (default: its main <InternalName>.json). For JSON you can select a sub-tree with 'path' " +
                          "(e.g. \"Profiles[0].Name\" or [\"key.with.dots\"]) and limit nesting with 'depth' to explore large configs. " +
                          "Note: this is the saved file — a loaded plugin may hold unsaved changes in memory. Requires 'Allow plugin management' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "plugin": { "type": "string", "description": "Internal name of the plugin." },
                    "file": { "type": "string", "description": "Relative file path from list_plugin_config_files (default: the main <InternalName>.json)." },
                    "path": { "type": "string", "description": "JSON path inside the file, e.g. Settings.Enabled or Items[3]." },
                    "depth": { "type": "integer", "description": "Collapse objects/arrays below this depth (default: unlimited)." },
                    "max_chars": { "type": "integer", "description": "Truncate the output to this many characters (default 30000, max 200000)." }
                  },
                  "required": ["plugin"]
                }
                """,
            Handler = (args, _) => Task.Run<object?>(async () =>
            {
                RequireEnabled();
                var internalName = ResolveInternalName(args.String("plugin"));
                var file = ResolveConfigFile(internalName, args.String("file"));
                var maxChars = args.Int("max_chars", 30000, 200, 200000);
                var text = await File.ReadAllTextAsync(file.FullName).ConfigureAwait(false);

                if (!file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TextExtensions.Contains(file.Extension.ToLowerInvariant()))
                        throw new ToolException($"'{file.Name}' does not look like a text file.");
                    return Truncate(text, maxChars);
                }

                var root = ParseJson(text, file.Name);
                var segments = JsonPath.Parse(args.String("path"));
                var node = JsonPath.Get(root, segments);
                var depth = args.Int("depth", -1, -1, 50);
                if (depth >= 0) node = JsonPath.Summarize(node, depth);
                var json = node?.ToJsonString(WriteOptions) ?? "null";
                return $"// {RelativeName(file)}  path: {JsonPath.Format(segments)}  type: {JsonPath.Kind(node)}\n" + Truncate(json, maxChars);
            }),
        };

        yield return new McpTool
        {
            Name = "set_plugin_config",
            Description = "Changes values in a Dalamud plugin's JSON configuration file. Each change is { path, value } where value is any JSON value. " +
                          "Because plugins keep their config in memory (and often save it on shutdown), a loaded plugin is unloaded first, the file is written, " +
                          "and the plugin is loaded again (reload=true, default). Existing keys keep their JSON type unless allow_type_change=true; " +
                          "new keys need create_missing=true. A backup of the previous file is kept. Read the config with get_plugin_config first. " +
                          "Requires 'Allow plugin management' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "plugin": { "type": "string", "description": "Internal name of the plugin." },
                    "file": { "type": "string", "description": "Relative JSON file path (default: the main <InternalName>.json)." },
                    "changes": {
                      "type": "array",
                      "description": "Values to set.",
                      "items": {
                        "type": "object",
                        "properties": {
                          "path": { "type": "string", "description": "JSON path, e.g. Settings.Volume or Items[2].Enabled." },
                          "value": { "description": "New JSON value (string, number, boolean, null, object or array)." }
                        },
                        "required": ["path", "value"]
                      }
                    },
                    "reload": { "type": "boolean", "description": "Unload the plugin before writing and load it again afterwards (default true). Ignored if the plugin is not loaded." },
                    "create_missing": { "type": "boolean", "description": "Allow adding keys / appending array items that do not exist yet (default false)." },
                    "allow_type_change": { "type": "boolean", "description": "Allow replacing a value with a different JSON type (default false)." },
                    "dry_run": { "type": "boolean", "description": "Validate and show the before/after values without writing anything (default false)." }
                  },
                  "required": ["plugin", "changes"]
                }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = async (args, _) =>
            {
                RequireEnabled();
                var internalName = ResolveInternalName(args.String("plugin"));
                var file = ResolveConfigFile(internalName, args.String("file"));
                if (!file.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
                    throw new ToolException("Only JSON config files can be edited.");
                if (args.Node("changes") is not JsonArray changes || changes.Count == 0)
                    throw new ToolException("'changes' must be a non-empty array of { path, value }.");

                var createMissing = args.Bool("create_missing", false);
                var allowTypeChange = args.Bool("allow_type_change", false);
                var dryRun = args.Bool("dry_run", false);
                var reload = args.Bool("reload", true);

                var plugin = DalamudInternals.InstalledPlugins().FirstOrDefault(p => DalamudInternals.InternalName(p).Equals(internalName, StringComparison.OrdinalIgnoreCase));
                if (plugin is not null) DalamudInternals.EnsureNotSelf(plugin);
                var wasLoaded = plugin is not null && DalamudInternals.IsLoaded(plugin);

                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Validate against the current file before touching the plugin, so a bad request never unloads anything.
                    var applied = ApplyChanges(ParseJson(await File.ReadAllTextAsync(file.FullName).ConfigureAwait(false), file.Name),
                                               changes, createMissing, allowTypeChange, out var _validated);
                    if (dryRun) return new { dryRun = true, file = RelativeName(file), pluginLoaded = wasLoaded, changes = applied };

                    var unloaded = false;
                    if (wasLoaded && reload)
                    {
                        await DalamudInternals.Unload(plugin!).ConfigureAwait(false);
                        unloaded = true;
                    }

                    string backup;
                    try
                    {
                        // Re-read after unloading: the plugin may have saved its config while shutting down.
                        var original = await File.ReadAllTextAsync(file.FullName).ConfigureAwait(false);
                        var root = ParseJson(original, file.Name);
                        applied = ApplyChanges(root, changes, createMissing, allowTypeChange, out var serialized);
                        backup = Backup(internalName, file, original);
                        var workingId = plugin is not null ? DalamudInternals.WorkingId(plugin) : Guid.Empty;
                        await DalamudInternals.WriteConfigFile(file.FullName, serialized, workingId).ConfigureAwait(false);
                        Svc.Log.Information($"[MCP] Wrote {applied.Count} change(s) to {RelativeName(file)} (backup: {backup})");
                    }
                    catch
                    {
                        if (unloaded) await DalamudInternals.Load(plugin!).ConfigureAwait(false); // restore the previous state
                        throw;
                    }

                    string? reloadError = null;
                    if (unloaded)
                    {
                        try { await DalamudInternals.Load(plugin!).ConfigureAwait(false); }
                        catch (Exception ex) { reloadError = $"The config was written, but loading the plugin again failed: {ex.GetBaseException().Message}. Restore the backup if the new values broke it."; }
                    }

                    return new
                    {
                        file = RelativeName(file),
                        changes = applied,
                        backup,
                        pluginWasLoaded = wasLoaded,
                        reloaded = unloaded && reloadError is null,
                        stateAfter = plugin is not null ? DalamudInternals.State(plugin) : null,
                        warning = reloadError ?? (wasLoaded && !reload
                            ? "The plugin is loaded and was not reloaded: it will not see these changes and may overwrite them when it next saves. Use reload_plugin."
                            : null),
                    };
                }
                finally { Gate.Release(); }
            },
        };
    }

    private static List<object> ApplyChanges(JsonNode root, JsonArray changes, bool createMissing, bool allowTypeChange, out string serialized)
    {
        var applied = new List<object>();
        foreach (var change in changes)
        {
            if (change is not JsonObject c || c["path"]?.GetValue<string>() is not { } path || !c.ContainsKey("value"))
                throw new ToolException("Each change must be an object with 'path' and 'value'.");
            var segments = JsonPath.Parse(path);
            var value = c["value"]?.DeepClone();

            JsonNode? existing = null;
            var exists = true;
            try { existing = JsonPath.Get(root, segments); }
            catch (ToolException) { exists = false; }

            if (exists && !allowTypeChange && existing is not null && value is not null && !SameKind(existing, value))
                throw new ToolException($"'{path}' is a {JsonPath.Kind(existing)} but the new value is a {JsonPath.Kind(value)}. Pass allow_type_change=true if this is intended.");

            var old = JsonPath.Set(root, segments, value, createMissing);
            applied.Add(new { path = JsonPath.Format(segments), before = old, after = value, added = !exists });
        }
        serialized = root.ToJsonString(WriteOptions);
        return applied;
    }

    private static bool SameKind(JsonNode a, JsonNode b)
    {
        static string Norm(JsonValueKind k) => k is JsonValueKind.True or JsonValueKind.False ? "bool" : k.ToString();
        return Norm(a.GetValueKind()) == Norm(b.GetValueKind());
    }

    private static JsonNode ParseJson(string text, string name)
    {
        try { return JsonNode.Parse(text, documentOptions: ReadOptions) ?? throw new ToolException($"'{name}' is empty."); }
        catch (JsonException ex) { throw new ToolException($"'{name}' is not valid JSON: {ex.Message}"); }
    }

    private static string ConfigRoot() =>
        Svc.PluginInterface.ConfigFile.Directory?.FullName ?? throw new ToolException("Could not determine the pluginConfigs directory.");

    /// <summary>Accepts an installed plugin's name/internal name, or any internal name that has config files.</summary>
    private static string ResolveInternalName(string? plugin)
    {
        var name = ResolveInternalNameUnchecked(plugin);
        if (name.Equals(Svc.PluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("XIV MCP's own configuration (which holds the access token) is not available through these tools. Use /xivmcp in game.");
        return name;
    }

    private static string ResolveInternalNameUnchecked(string? plugin)
    {
        if (plugin is null) throw new ToolException("'plugin' is required.");
        try { return DalamudInternals.InternalName(DalamudInternals.Find(plugin)); }
        catch (ToolException)
        {
            var root = ConfigRoot();
            if (plugin.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                (File.Exists(Path.Combine(root, plugin + ".json")) || Directory.Exists(Path.Combine(root, plugin))))
                return plugin;
            throw;
        }
    }

    /// <summary>Resolves a config file and makes sure it stays inside the plugin's own config area.</summary>
    private static FileInfo ResolveConfigFile(string internalName, string? relative)
    {
        var root = Path.GetFullPath(ConfigRoot());
        var mainFile = Path.Combine(root, internalName + ".json");
        var pluginDir = Path.GetFullPath(Path.Combine(root, internalName)) + Path.DirectorySeparatorChar;

        string full;
        if (relative is null || relative.Equals(internalName + ".json", StringComparison.OrdinalIgnoreCase))
            full = mainFile;
        else
        {
            var rel = relative.Replace('/', Path.DirectorySeparatorChar);
            full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(pluginDir, StringComparison.OrdinalIgnoreCase))
                full = Path.GetFullPath(Path.Combine(pluginDir, rel)); // allow paths relative to the plugin's own folder
            if (!full.StartsWith(pluginDir, StringComparison.OrdinalIgnoreCase))
                throw new ToolException($"'{relative}' is outside of {internalName}'s config files.");
        }

        var info = new FileInfo(full);
        if (!info.Exists) throw new ToolException($"Config file '{Path.GetRelativePath(root, full)}' does not exist. Use list_plugin_config_files.");
        return info;
    }

    private static string Backup(string internalName, FileInfo file, string contents)
    {
        var dir = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "backups", internalName);
        Directory.CreateDirectory(dir);
        var safeName = RelativeName(file).Replace('/', '_').Replace('\\', '_');
        var path = Path.Combine(dir, $"{safeName}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
        File.WriteAllText(path, contents);
        foreach (var old in new DirectoryInfo(dir).GetFiles(safeName + ".*.bak").OrderByDescending(f => f.Name).Skip(MaxBackupsPerFile))
            old.Delete();
        return path;
    }

    private static string RelativeName(FileInfo file) => Path.GetRelativePath(ConfigRoot(), file.FullName).Replace('\\', '/');

    private static object FileEntry(FileInfo f, string relative, bool isMain) => new
    {
        file = relative,
        main = isMain,
        sizeBytes = f.Length,
        modified = f.LastWriteTime,
        editable = f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase),
    };

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + $"\n... [truncated, {s.Length - max} more characters — use 'path' or 'depth']";
}
