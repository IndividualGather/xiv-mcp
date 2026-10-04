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

    // ---------------------------------------------------------------- set up?

    /// <summary>
    /// Whether XIV MCP is set up in the app (its own configuration lists it), so the app connects whenever it runs. Read from each app's
    /// config files; anything unreadable counts as not set up.
    /// </summary>
    public static bool IsConnected(string appId)
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
                "vscode" => Read(Path.Combine(appData, "Code", "User", "mcp.json")) is { } v && SetupStatus.HasServer(v, "servers"),
                "cursor" => Read(Path.Combine(home, ".cursor", "mcp.json")) is { } c && SetupStatus.HasServer(c, "mcpServers"),
                "lmstudio" => Read(Path.Combine(home, ".lmstudio", "mcp.json")) is { } l && SetupStatus.HasServer(l, "mcpServers"),
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
