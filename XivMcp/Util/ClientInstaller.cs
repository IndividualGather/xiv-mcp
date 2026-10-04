using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using XivMcp.Connect;

namespace XivMcp.Util;

/// <summary>
/// Connects AI apps on this PC to XIV MCP: writes and opens the Claude Desktop extension, opens install links, and adds the entry to
/// Codex's config.toml. The formats themselves live in <see cref="ClientSetup"/>.
/// </summary>
internal static class ClientInstaller
{
    /// <summary>The outcome of an action, shown under its button.</summary>
    public sealed record Result(bool Ok, string Message);

    // ---------------------------------------------------------------- detection

    /// <summary>Whether Windows knows how to open the file type or link scheme (an app registered it), e.g. ".mcpb" or "vscode".</summary>
    public static bool IsRegistered(string extensionOrScheme)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(extensionOrScheme);
            return key is not null;
        }
        catch { return false; }
    }

    public static string CodexConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

    public static bool CodexInstalled => Directory.Exists(Path.GetDirectoryName(CodexConfigPath));

    public static bool CodexHasEntry()
    {
        try { return File.Exists(CodexConfigPath) && ClientSetup.HasCodexEntry(File.ReadAllText(CodexConfigPath)); }
        catch { return false; }
    }

    // ---------------------------------------------------------------- actions

    public static string McpbPath => Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "xiv-mcp.mcpb");

    /// <summary>Claude Desktop is installed: it registered .mcpb files, or (the Microsoft Store build) only its claude: links.</summary>
    public static bool ClaudeDesktopInstalled => IsRegistered(".mcpb") || IsRegistered("claude") || File.Exists(ClaudeAlias);

    /// <summary>
    /// The command-line alias of the Microsoft Store build of Claude Desktop. That build doesn't register .mcpb files, but passing the
    /// file to the alias opens Claude's install screen all the same.
    /// </summary>
    private static string ClaudeAlias => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "claude-desktop.exe");

    /// <summary>Whether the button can open Claude Desktop's install screen directly (.mcpb registered, or the Store build's alias).</summary>
    public static bool OpensInstallScreen => IsRegistered(".mcpb") || File.Exists(ClaudeAlias);

    /// <summary>
    /// Writes the extension with the current address and token and hands it to Claude Desktop, which shows its install screen: by
    /// opening the file where .mcpb is registered, else through the Store build's alias. Failing both, Claude Desktop and an Explorer
    /// window with the file selected, to drag in.
    /// </summary>
    public static Result InstallClaudeDesktop(string endpoint, string? token, string iconPath)
    {
        try
        {
            WriteMcpb(endpoint, token, iconPath);
            const string done = "Claude Desktop is showing the extension: switch to it and click Install.";
            if (IsRegistered(".mcpb"))
            {
                Open(McpbPath);
                return new Result(true, done);
            }
            if (File.Exists(ClaudeAlias))
            {
                Process.Start(new ProcessStartInfo(ClaudeAlias, $"\"{McpbPath}\"") { UseShellExecute = true });
                return new Result(true, done);
            }
            Process.Start("explorer.exe", $"/select,\"{McpbPath}\"");
            if (IsRegistered("claude")) Open("claude://");
            return new Result(true, "Drag xiv-mcp.mcpb from the Explorer window into Claude Desktop, then click Install.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[MCP] Could not open the Claude Desktop extension.");
            return new Result(false, $"Couldn't open the extension: {ex.Message}. Use Show file and drag it into Claude Desktop.");
        }
    }

    /// <summary>Opens Explorer with the extension file selected (writing it first if needed).</summary>
    public static void ShowMcpb(string endpoint, string? token, string iconPath)
    {
        WriteMcpb(endpoint, token, iconPath); // always current: the address or token may have changed since
        Process.Start("explorer.exe", $"/select,\"{McpbPath}\"");
    }

    private static void WriteMcpb(string endpoint, string? token, string iconPath)
    {
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var icon = File.Exists(iconPath) ? File.ReadAllBytes(iconPath) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(McpbPath)!);
        using var file = File.Create(McpbPath);
        ClientSetup.WriteMcpb(file, version, endpoint, token, icon);
    }

    /// <summary>Opens an install link (vscode:, cursor:, lmstudio:); the app asks the player to confirm.</summary>
    public static Result OpenLink(string link, string app)
    {
        try
        {
            Open(link);
            return new Result(true, $"{app} should now ask you to install the server. Confirm it there.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[MCP] Could not open the {app} install link.");
            return new Result(false, $"Couldn't open {app}: {ex.Message}");
        }
    }

    /// <summary>Adds (or updates) XIV MCP in ~/.codex/config.toml, keeping a backup of the previous file.</summary>
    public static Result AddToCodex(string endpoint, string? token)
    {
        try
        {
            var path = CodexConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var merged = ClientSetup.MergeCodexConfig(existing, endpoint, token);
            if (merged == existing) return new Result(true, "Already set up with this address and token.");
            if (existing.Length > 0) File.Copy(path, path + ".xivmcp-backup", overwrite: true);
            File.WriteAllText(path, merged);
            return new Result(true, existing.Length > 0
                ? "Added (previous config saved as config.toml.xivmcp-backup). Restart ChatGPT to load it."
                : "Added. Restart ChatGPT to load it.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[MCP] Could not update Codex's config.toml.");
            return new Result(false, $"Couldn't update config.toml: {ex.Message}");
        }
    }

    /// <summary>Removes XIV MCP from ~/.codex/config.toml (keeping a backup); everything else in the file stays.</summary>
    public static Result RemoveFromCodex()
    {
        try
        {
            var path = CodexConfigPath;
            if (!File.Exists(path)) return new Result(true, "Not set up: there is no Codex configuration.");
            var existing = File.ReadAllText(path);
            var removed = ClientSetup.RemoveCodexEntry(existing);
            if (removed == existing) return new Result(true, "Not set up: XIV MCP isn't in the Codex configuration.");
            File.Copy(path, path + ".xivmcp-backup", overwrite: true);
            File.WriteAllText(path, removed);
            return new Result(true, "Removed (previous config saved as config.toml.xivmcp-backup). Restart ChatGPT to apply it.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[MCP] Could not update Codex's config.toml.");
            return new Result(false, $"Couldn't update config.toml: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- LM Studio

    /// <summary>LM Studio's MCP config. Its desktop app is now called Bionic and keeps its data in the same folder.</summary>
    public static string LmStudioConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "mcp.json");

    /// <summary>LM Studio or its successor Bionic: a registered link scheme, or its data folder.</summary>
    public static bool LmStudioInstalled => IsRegistered("lmstudio") || IsBionic || Directory.Exists(Path.GetDirectoryName(LmStudioConfigPath));

    /// <summary>
    /// The current LM Studio desktop app, Bionic. It adds MCP servers in its own settings (Integrations → MCP) and doesn't read the old
    /// app's mcp.json.
    /// </summary>
    public static bool IsBionic => IsRegistered("bionic") || File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Bionic", "Bionic.exe"));

    /// <summary>
    /// Where Bionic may keep the servers added in its settings. Not documented: these are the places its own code names (ng-mcp.json);
    /// a file there that mentions XIV MCP's address counts as set up.
    /// </summary>
    private static System.Collections.Generic.IEnumerable<string> BionicConfigCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".lmstudio", "ng-mcp.json");
        yield return Path.Combine(home, ".lmstudio", ".internal", "ng-mcp.json");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bionic", "ng-mcp.json");
    }

    private static bool BionicHasServer(string endpoint) =>
        BionicConfigCandidates().Select(Read).Any(text => text is not null && text.Contains(endpoint.Replace("http://", ""), StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds (or updates) XIV MCP in LM Studio's mcp.json, keeping a backup of the previous file.</summary>
    public static Result AddToLmStudio(string endpoint, string? token) => EditJsonConfig(LmStudioConfigPath, "mcpServers",
        existing => ClientSetup.MergeJsonServer(existing, "mcpServers", endpoint, token),
        "Added to LM Studio. If it doesn't show up under Integrations → MCP, restart LM Studio.", "Already set up with this address and token.");

    public static Result RemoveFromLmStudio() => EditJsonConfig(LmStudioConfigPath, "mcpServers",
        existing => ClientSetup.RemoveJsonServer(existing, "mcpServers"), "Removed from LM Studio.", "Not set up: XIV MCP isn't in LM Studio's mcp.json.");

    private static Result EditJsonConfig(string path, string key, Func<string, string> edit, string done, string unchanged)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var edited = edit(existing);
            if (SetupStatus.HasServer(existing, key) == SetupStatus.HasServer(edited, key) && Normalize(existing) == Normalize(edited)) return new Result(true, unchanged);
            if (existing.Length > 0) File.Copy(path, path + ".xivmcp-backup", overwrite: true);
            File.WriteAllText(path, edited);
            return new Result(true, existing.Length > 0 ? $"{done} (Previous file saved as {Path.GetFileName(path)}.xivmcp-backup.)" : done);
        }
        catch (FormatException ex) { return new Result(false, $"{Path.GetFileName(path)} couldn't be read, so it was left alone: {ex.Message}"); }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[MCP] Could not update {path}.");
            return new Result(false, $"Couldn't update {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    private static string Normalize(string json)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString() ?? ""; }
        catch { return json; }
    }

    // ---------------------------------------------------------------- GitHub Copilot and Grok Build

    /// <summary>GitHub Copilot's MCP config, read by the Copilot CLI and (by its docs) the GitHub Copilot app.</summary>
    public static string CopilotConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "mcp-config.json");

    public static bool CopilotInstalled => InstalledApp("GitHub Copilot") is not null || OnPath("copilot") || File.Exists(CopilotConfigPath);

    public static Result AddToCopilot(string endpoint, string? token) => EditJsonConfig(CopilotConfigPath, "mcpServers",
        existing => ClientSetup.MergeCopilotConfig(existing, endpoint, token),
        "Added to GitHub Copilot. Restart the Copilot app (or start a new Copilot CLI session) to load it.", "Already set up with this address and token.");

    public static Result RemoveFromCopilot() => EditJsonConfig(CopilotConfigPath, "mcpServers",
        existing => ClientSetup.RemoveJsonServer(existing, "mcpServers"), "Removed from GitHub Copilot.", "Not set up: XIV MCP isn't in Copilot's mcp-config.json.");

    /// <summary>Grok Build's user config. (grok.com only reaches servers on the internet; Grok Build runs on this PC.)</summary>
    public static string GrokConfigPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "config.toml");

    public static bool GrokInstalled => OnPath("grok") || Directory.Exists(Path.GetDirectoryName(GrokConfigPath));

    public static Result AddToGrok(string endpoint, string? token) => EditTomlConfig(GrokConfigPath,
        existing => ClientSetup.MergeGrokConfig(existing, endpoint, token), "Added to Grok Build. Start a new grok session to use it.");

    public static Result RemoveFromGrok() => EditTomlConfig(GrokConfigPath, ClientSetup.RemoveCodexEntry, "Removed from Grok Build.");

    private static Result EditTomlConfig(string path, Func<string, string> edit, string done)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var existing = File.Exists(path) ? File.ReadAllText(path) : "";
            var edited = edit(existing);
            if (edited == existing) return new Result(true, ClientSetup.HasCodexEntry(existing) ? "Already set up with this address and token." : "Not set up: nothing to remove.");
            if (existing.Length > 0) File.Copy(path, path + ".xivmcp-backup", overwrite: true);
            File.WriteAllText(path, edited);
            return new Result(true, existing.Length > 0 ? $"{done} (Previous file saved as {Path.GetFileName(path)}.xivmcp-backup.)" : done);
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, $"[MCP] Could not update {path}.");
            return new Result(false, $"Couldn't update {Path.GetFileName(path)}: {ex.Message}");
        }
    }

    /// <summary>A program on PATH (exe or cmd), e.g. a CLI installed with npm or winget.</summary>
    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(d => { try { return File.Exists(Path.Combine(d.Trim('"'), name + ".exe")) || File.Exists(Path.Combine(d.Trim('"'), name + ".cmd")); } catch { return false; } });

    /// <summary>
    /// An app in Windows' list of installed programs whose name contains <paramref name="name"/>: its icon file (the program, usually),
    /// or null if it isn't installed.
    /// </summary>
    public static string? InstalledApp(string name)
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var uninstall = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;
            foreach (var sub in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(sub);
                if (key?.GetValue("DisplayName") is not string display || !display.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
                var icon = (key.GetValue("DisplayIcon") as string)?.Split(',')[0].Trim('"');
                return icon is not null && File.Exists(icon) ? icon : "";
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- set up?

    /// <summary>
    /// Whether XIV MCP is set up in the app (its own configuration lists it), so the app connects whenever it runs. Read from each app's
    /// config files; anything unreadable counts as not set up.
    /// </summary>
    public static bool IsConnected(string appId, string endpoint)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        try
        {
            return appId switch
            {
                "claude-desktop" => ClaudeDesktopRoots().Any(ClaudeExtensionEnabled),
                "claude" => Read(Path.Combine(home, ".claude.json")) is { } j && SetupStatus.ClaudeCodeHasServer(j),
                "codex" => CodexHasEntry(),
                "copilot" => Read(CopilotConfigPath) is { } cp && SetupStatus.HasServer(cp, "mcpServers"),
                "grok" => Read(GrokConfigPath) is { } gk && ClientSetup.HasCodexEntry(gk),
                "vscode" => Read(Path.Combine(appData, "Code", "User", "mcp.json")) is { } v && SetupStatus.HasServer(v, "servers"),
                "cursor" => Read(Path.Combine(home, ".cursor", "mcp.json")) is { } c && SetupStatus.HasServer(c, "mcpServers"),
                // Bionic keeps its servers in its own settings; only the old LM Studio app reads mcp.json.
                "lmstudio" => IsBionic ? BionicHasServer(endpoint) : Read(LmStudioConfigPath) is { } l && SetupStatus.HasServer(l, "mcpServers"),
                _ => false,
            };
        }
        catch { return false; }
    }

    /// <summary>Claude Desktop's data folders: the regular install's, and the Microsoft Store build's (kept inside its package data).</summary>
    private static System.Collections.Generic.IEnumerable<string> ClaudeDesktopRoots()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
        var packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (Directory.Exists(packages))
            foreach (var dir in Directory.EnumerateDirectories(packages, "Claude_*"))
                yield return Path.Combine(dir, "LocalCache", "Roaming", "Claude");
    }

    private static bool ClaudeExtensionEnabled(string root)
    {
        var extensions = Path.Combine(root, "Claude Extensions");
        if (!Directory.Exists(extensions)) return false;
        var folders = Directory.EnumerateDirectories(extensions).ToList();
        var mine = folders.FirstOrDefault(f => Path.GetFileName(f).EndsWith(SetupStatus.ExtensionSuffix, StringComparison.OrdinalIgnoreCase));
        var settings = mine is null ? null : Read(Path.Combine(root, "Claude Extensions Settings", Path.GetFileName(mine) + ".json"));
        return SetupStatus.ClaudeExtensionEnabled(folders, settings);
    }

    /// <summary>Reads a config file, or null. Files that haven't changed since the last read come from memory (~/.claude.json can be large).</summary>
    private static string? Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        lock (ReadCache)
        {
            if (ReadCache.TryGetValue(path, out var hit) && hit.Written == info.LastWriteTimeUtc && hit.Length == info.Length) return hit.Text;
            var text = File.ReadAllText(path);
            ReadCache[path] = (info.LastWriteTimeUtc, info.Length, text);
            return text;
        }
    }

    private static readonly System.Collections.Generic.Dictionary<string, (DateTime Written, long Length, string Text)> ReadCache = new();

    // ---------------------------------------------------------------- Claude Code

    /// <summary>
    /// The claude CLI: on PATH, where the native installer puts it (~/.local/bin), or npm's global folder. The game may have been
    /// started before Claude Code was installed, so PATH alone isn't enough.
    /// </summary>
    public static string? FindClaudeCli()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                   .Append(Path.Combine(home, ".local", "bin")).Append(Path.Combine(appData, "npm"));
        foreach (var dir in dirs)
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    var path = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(path)) return path;
                }
                catch { /* a malformed PATH entry */ }
            }
        return null;
    }

    /// <summary>Adds (or updates) XIV MCP in Claude Code for all projects, by running the claude CLI without a console window.</summary>
    public static async System.Threading.Tasks.Task<Result> AddToClaudeCodeAsync(string endpoint, string? token)
    {
        if (FindClaudeCli() is not { } cli) return new Result(false, "Couldn't find Claude Code (the claude command). Install it, or copy the command and run it yourself.");
        try
        {
            await Run(cli, ClientSetup.ClaudeCodeRemoveArgs()); // fails harmlessly when there is no earlier entry
            var (code, output) = await Run(cli, ClientSetup.ClaudeCodeAddArgs(endpoint, token));
            return code == 0
                ? new Result(true, "Added to Claude Code for all your projects. Start a new session to use it.")
                : new Result(false, $"Claude Code said: {(output.Length > 0 ? output : $"exit code {code}")}");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[MCP] Could not run the claude CLI.");
            return new Result(false, $"Couldn't run Claude Code: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes XIV MCP from Claude Code: the entry for all projects, and every project's own entry (found in ~/.claude.json; the claude
    /// CLI removes those when run in the project's folder).
    /// </summary>
    public static async System.Threading.Tasks.Task<Result> RemoveFromClaudeCodeAsync()
    {
        if (FindClaudeCli() is not { } cli) return new Result(false, "Couldn't find Claude Code (the claude command).");
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var json = Read(Path.Combine(home, ".claude.json"));
            var projects = json is null ? [] : SetupStatus.ClaudeCodeProjects(json);
            var (userCode, _) = await Run(cli, ClientSetup.ClaudeCodeRemoveArgs("user"));
            var removed = userCode == 0 ? 1 : 0;
            var failed = new System.Collections.Generic.List<string>();
            foreach (var project in projects)
            {
                if (!Directory.Exists(project)) { failed.Add(project); continue; }
                var (code, _) = await Run(cli, ClientSetup.ClaudeCodeRemoveArgs("local"), project);
                if (code == 0) removed++; else failed.Add(project);
            }
            if (failed.Count > 0)
                return new Result(false, $"Removed {removed} entr{(removed == 1 ? "y" : "ies")}, but not the one for {string.Join(", ", failed)}. Run \"claude mcp remove ffxiv\" in that folder.");
            return removed == 0
                ? new Result(true, "Not set up: Claude Code has no XIV MCP entry.")
                : new Result(true, $"Removed from Claude Code ({removed} entr{(removed == 1 ? "y" : "ies")}). Running sessions keep it until they restart.");
        }
        catch (Exception ex)
        {
            Svc.Log.Warning(ex, "[MCP] Could not run the claude CLI.");
            return new Result(false, $"Couldn't run Claude Code: {ex.Message}");
        }
    }

    private static async System.Threading.Tasks.Task<(int Code, string Output)> Run(string cli, string[] args, string? folder = null)
    {
        // claude.cmd (npm) runs through cmd; claude.exe directly.
        var isCmd = cli.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(isCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : cli)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (isCmd) { start.ArgumentList.Add("/c"); start.ArgumentList.Add(cli); }
        foreach (var a in args) start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        var output = ((await stdout) + "\n" + (await stderr)).Trim();
        return (process.ExitCode, output.Length > 300 ? output[..297] + "..." : output);
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}
