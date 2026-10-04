using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using XivMcp.Connect;
using XivMcp.Util;

namespace XivMcp.Windows;

/// <summary>
/// The Connect tab: pick your AI app, then one button installs XIV MCP in it (Claude Desktop extension, Codex config for ChatGPT, install links),
/// with copyable commands where there is no button. Below: whether it worked, and the server address and access token.
/// </summary>
internal sealed partial class ConfigWindow
{
    /// <summary>An AI app XIV MCP can connect to. <see cref="Detect"/> says whether it seems installed (null: can't tell).</summary>
    private sealed record ClientApp(string Id, string Name, FontAwesomeIcon Icon, Func<bool?> Detect, Func<string, bool> Matches);

    private static readonly ClientApp[] Clients =
    [
        // Codex lives in the ChatGPT desktop app (since July 2026); the id stays "codex" for saved settings.
        new("codex", "ChatGPT", FontAwesomeIcon.Code, () => ClientInstaller.CodexInstalled || AppIcons.StorePackage("OpenAI.Codex_") is not null || AppIcons.StorePackage("OpenAI.ChatGPT") is not null, n => n.Contains("codex", StringComparison.OrdinalIgnoreCase)),
        new("claude-desktop", "Claude Desktop", FontAwesomeIcon.Desktop, () => ClientInstaller.ClaudeDesktopInstalled,
            n => n.Contains("claude", StringComparison.OrdinalIgnoreCase) && !n.Contains("claude-code", StringComparison.OrdinalIgnoreCase)),
        new("claude", "Claude Code", FontAwesomeIcon.Terminal, () => ClientInstaller.FindClaudeCli() is not null, n => n.Contains("claude-code", StringComparison.OrdinalIgnoreCase)),
        new("vscode", "VS Code", FontAwesomeIcon.FileCode, () => ClientInstaller.IsRegistered("vscode"),
            n => n.Contains("visual studio code", StringComparison.OrdinalIgnoreCase) || n.Contains("vscode", StringComparison.OrdinalIgnoreCase)),
        new("cursor", "Cursor", FontAwesomeIcon.MousePointer, () => ClientInstaller.IsRegistered("cursor"), n => n.Contains("cursor", StringComparison.OrdinalIgnoreCase)),
        new("lmstudio", "LM Studio", FontAwesomeIcon.Microchip, () => ClientInstaller.IsRegistered("lmstudio"),
            n => n.Contains("lm studio", StringComparison.OrdinalIgnoreCase) || n.Contains("lmstudio", StringComparison.OrdinalIgnoreCase)),
        new("other", "Other app", FontAwesomeIcon.EllipsisH, () => null, _ => false),
    ];

    /// <summary>Installed or not, per app; read when the tab opens and every few seconds (registry reads).</summary>
    private bool?[] detected = new bool?[Clients.Length];

    /// <summary>Per app: XIV MCP is set up in it (its config lists the server), refreshed with <see cref="detected"/>.</summary>
    private bool[] connected = new bool[Clients.Length];
    private DateTime nextDetect = DateTime.MinValue;

    /// <summary>The outcome of the last button press, per app id.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ClientInstaller.Result> results = new();

    /// <summary>An action running in the background (the claude CLI), by app id.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> busy = new();

    private void DrawConnect()
    {
        var config = plugin.Config;
        var server = plugin.Server;
        var endpoint = server.Endpoint;
        var token = config.RequireToken ? config.Token : null;
        if (DateTime.UtcNow >= nextDetect)
        {
            detected = Clients.Select(c => c.Detect()).ToArray();
            connected = Clients.Select(c => ClientInstaller.IsConnected(c.Id)).ToArray();
            nextDetect = DateTime.UtcNow.AddSeconds(5);
        }
        var chosen = Array.FindIndex(Clients, c => c.Id == config.ConnectClient);
        if (chosen < 0) chosen = Array.FindIndex(Clients, c => c.Id == "claude-desktop");

        // Text runs at most this wide (about 80 characters), so lines stay easy to follow on a wide window.
        textLeft = ImGui.GetCursorPosX();
        textWidth = Math.Min(ImGui.GetContentRegionAvail().X, 620 * Ui.Scale);

        // 1. Which app?
        ImGui.TextColored(Muted, "Which AI app do you use?");
        Gap(4);
        DrawClientTiles(chosen);
        Gap(14);

        // 2. Connect it: what it does in one line, the button, then what to do after clicking.
        var app = Clients[chosen];
        using (ImRaii.PushId(app.Id))
        {
            ImGui.SetWindowFontScale(1.25f);
            ImGui.TextColored(Accent, app.Name);
            ImGui.SetWindowFontScale(1f);
            Gap(2);
            switch (app.Id)
            {
                case "claude-desktop": DrawClaudeDesktop(endpoint, token, detected[chosen]); break;
                case "claude": DrawClaudeCode(endpoint, token, detected[chosen]); break;
                case "codex": DrawCodex(endpoint, token, detected[chosen]); break;
                case "vscode":
                    DrawLinkApp(app, detected[chosen], ClientSetup.VsCodeLink(endpoint, token), "https://code.visualstudio.com/",
                        "Adds XIV MCP to GitHub Copilot in VS Code, for all your projects.",
                        ["When VS Code asks to install the server, click Install.", "Open Copilot Chat in Agent mode and ask about your character."],
                        "VS Code keeps the address and token in your user mcp.json.");
                    break;
                case "cursor":
                    DrawLinkApp(app, detected[chosen], ClientSetup.CursorLink(endpoint, token), "https://cursor.com/",
                        "Adds XIV MCP to Cursor.",
                        ["When Cursor asks to install the server, click Install.", "Ask the agent about your character."], null);
                    break;
                case "lmstudio":
                    DrawLinkApp(app, detected[chosen], ClientSetup.LmStudioLink(endpoint, token), "https://lmstudio.ai/",
                        "Adds XIV MCP to LM Studio, for models running on your PC.",
                        ["When LM Studio asks to install the server, click Install.", "Load a model that supports tool use and ask about your character."],
                        "Small models often struggle with this many tools. Larger ones work better.");
                    break;
                default: DrawOtherApp(endpoint, token); break;
            }
        }

        // 3. Did it work?
        Gap(14);
        var client = server.Clients().FirstOrDefault(c => app.Id == "other"
            ? !Clients.Any(k => k.Matches(c.Name))
            : app.Matches(c.Name));
        if (!server.IsRunning)
            Status(FontAwesomeIcon.ExclamationTriangle, Amber, "The server is switched off (top right), so no app can connect.");
        else if (client is { } c)
            Status(FontAwesomeIcon.Check, Green, $"Connected: {c.Name}, last request {Ago(c.LastSeenUtc)}");
        else if (connected[chosen])
            Status(FontAwesomeIcon.Check, Green, $"Set up in {app.Name}. It connects whenever {app.Name} is running.");
        else
            Status(FontAwesomeIcon.Hourglass, Muted, "Not connected yet. Once it's set up, ask your assistant something about your character.");

        // 4. Taking it out again, and what using an AI app costs.
        Gap(10);
        using (ImRaii.PushId(app.Id)) DrawRemoval(app);
        Gap(14);
        DrawCosts();

        // 5. Rarely needed details.
        Gap(10);
        ImGui.Separator();
        DrawServerAndToken(endpoint);
    }

    private float textLeft, textWidth;

    private static int Index(string appId) => Array.FindIndex(Clients, c => c.Id == appId);

    /// <summary>One tile per app, wrapping to the next line when the row is full. The chosen one is highlighted.</summary>
    private void DrawClientTiles(int chosen)
    {
        var scale = Ui.Scale;
        var size = new Vector2(144 * scale, 62 * scale);
        var gap = ImGui.GetStyle().ItemSpacing.X;
        var right = ImGui.GetContentRegionAvail().X;
        var x = 0f;
        for (var i = 0; i < Clients.Length; i++)
        {
            var app = Clients[i];
            if (i > 0)
            {
                if (x + gap + size.X <= right) ImGui.SameLine(0, gap);
                else x = 0;
            }
            x += (x > 0 ? gap : 0) + size.X;

            using var id = ImRaii.PushId(app.Id);
            var start = ImGui.GetCursorScreenPos();
            var selected = i == chosen;
            if (ImGui.InvisibleButton("##tile", size) && !selected)
            {
                plugin.Config.ConnectClient = app.Id;
                plugin.Config.Save();
            }
            var hovered = ImGui.IsItemHovered();
            var dl = ImGui.GetWindowDrawList();
            var accent = selected ? Accent : hovered ? Accent with { W = 0.6f } : new Vector4(1, 1, 1, 0.12f);
            dl.AddRectFilled(start, start + size, ImGui.GetColorU32(selected ? Accent with { W = 0.10f } : new Vector4(1, 1, 1, hovered ? 0.06f : 0.03f)), 6 * scale);
            dl.AddRect(start, start + size, ImGui.GetColorU32(accent), 6 * scale, ImDrawFlags.None, selected ? 2 * scale : 1 * scale);

            var textColor = ImGui.GetColorU32(selected ? Accent : ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
            // The app's own icon when it's installed; a generic symbol otherwise.
            var iconSize = 26 * scale;
            if (AppIcons.Get(app.Id) is { } logo)
            {
                var at = start + new Vector2(10 * scale, (size.Y - iconSize) / 2);
                dl.AddImage(logo.Handle, at, at + new Vector2(iconSize));
            }
            else
                using (Ui.IconFont())
                {
                    var icon = app.Icon.ToIconString();
                    var iconWidth = ImGui.CalcTextSize(icon);
                    dl.AddText(start + new Vector2(10 * scale + (iconSize - iconWidth.X) / 2, (size.Y - iconWidth.Y) / 2), textColor, icon);
                }
            var textX = start.X + 46 * scale;
            var line = ImGui.GetTextLineHeight();
            var top = start.Y + (size.Y - 2 * line) / 2;
            dl.AddText(new Vector2(textX, top), textColor, app.Name);
            // Connected: XIV MCP is set up in the app. Recommended only until any app is connected.
            var recommended = app.Id == "claude-desktop" && !connected.Any(x => x) && detected[i] != false;
            var (note, noteColor) = connected[i] ? ("Connected", Green) : recommended ? ("Recommended", Accent)
                : detected[i] == false ? ("Not found", Muted) : detected[i] == true ? ("Installed", Muted) : ("", Muted);
            if (note.Length > 0) dl.AddText(new Vector2(textX, top + line), ImGui.GetColorU32(noteColor), note);
            if (hovered) Tooltip(connected[i] ? $"XIV MCP is set up in {app.Name}." : detected[i] == false ? $"{app.Name} doesn't seem to be installed on this PC." : $"Connect {app.Name}");
        }
    }

    private void DrawClaudeDesktop(string endpoint, string? token, bool? installed)
    {
        Paragraph("Installs XIV MCP as a Claude Desktop extension.");
        if (installed == false) NotInstalled("Claude Desktop", "https://claude.ai/download");
        if (PrimaryButton(FontAwesomeIcon.Download, connected[Index("claude-desktop")] ? "Install again in Claude Desktop" : "Install in Claude Desktop"))
            results["claude-desktop"] = ClientInstaller.InstallClaudeDesktop(endpoint, token, iconPath);
        ImGui.SameLine();
        if (SecondaryButton(FontAwesomeIcon.FolderOpen, "Show file")) ClientInstaller.ShowMcpb(endpoint, token, iconPath);
        Tooltip("The extension file (xiv-mcp.mcpb). You can also install it in Claude Desktop under Settings → Extensions → Install Extension.");
        ResultLine("claude-desktop");
        Steps(installed != false && !ClientInstaller.OpensInstallScreen
            ? ["Drag xiv-mcp.mcpb from the folder that opens into the Claude Desktop window.", "Click Install.", "Start a new chat and ask about your character."]
            : ["Switch to Claude Desktop, which shows the extension, and click Install.", "Start a new chat and ask about your character."]);
        Hint("Claude stays connected while the game is closed. The game's tools appear as soon as you start it.");
        if (token is not null) Hint("The extension contains your access token. If you generate a new token, install it again.");
    }

    private void DrawClaudeCode(string endpoint, string? token, bool? installed)
    {
        Paragraph("Adds XIV MCP to Claude Code, for all your projects.");
        if (installed == false) NotInstalled("Claude Code", "https://claude.com/product/claude-code");
        var running = busy.ContainsKey("claude");
        using (ImRaii.Disabled(running))
            if (PrimaryButton(FontAwesomeIcon.Terminal, running ? "Adding…" : connected[Index("claude")] ? "Update in Claude Code" : "Add to Claude Code"))
            {
                busy["claude"] = true;
                results.TryRemove("claude", out _);
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { results["claude"] = await ClientInstaller.AddToClaudeCodeAsync(endpoint, token); }
                    finally { busy.TryRemove("claude", out _); }
                });
            }
        Tooltip("Runs the claude mcp add command for you, without a console window. An earlier XIV MCP entry for all projects is replaced.");
        ResultLine("claude");
        Steps(["Start a new Claude Code session.", "Ask about your character."]);
        Gap(4);
        if (ImGui.TreeNode("Prefer to run the command yourself?"))
        {
            CommandBox("claude", ClaudeCommand(endpoint, token is null ? null : Masked), ClaudeCommand(endpoint, token), "Copy command");
            ImGui.TreePop();
        }
    }

    private void DrawCodex(string endpoint, string? token, bool? installed)
    {
        Paragraph("Adds XIV MCP to Codex in the ChatGPT app, and to the Codex CLI and IDE extension. They share one configuration.");
        if (installed == false) NotInstalled("ChatGPT", "https://chatgpt.com/download");
        var added = ClientInstaller.CodexHasEntry();
        if (PrimaryButton(FontAwesomeIcon.Plus, added ? "Update in ChatGPT" : "Add to ChatGPT"))
            results["codex"] = ClientInstaller.AddToCodex(endpoint, token);
        Tooltip($"Writes the entry to {ClientInstaller.CodexConfigPath}, Codex's configuration. Everything else in the file is kept, and the old file is backed up.");
        if (added && !results.ContainsKey("codex"))
        {
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Green, "Already set up.");
        }
        ResultLine("codex");
        Steps(["Restart ChatGPT.", "Switch to Codex and ask about your character."]);
        Hint("XIV MCP shows up in Codex. ChatGPT's Chat mode only connects to servers on the internet, so it can't reach the game.");
        Gap(4);
        if (ImGui.TreeNode("Prefer to do it by hand?"))
        {
            Hint("Run these in a terminal. The first line stores the token as an environment variable.");
            CommandBox("codex", CodexCommand(endpoint, token is null ? null : Masked), CodexCommand(endpoint, token), "Copy commands");
            if (ImGui.SmallButton("Copy config.toml entry")) ImGui.SetClipboardText(CodexToml(endpoint, token));
            ImGui.TreePop();
        }
    }

    private void DrawLinkApp(ClientApp app, bool? installed, string link, string download, string what, string[] steps, string? note)
    {
        Paragraph(what);
        if (installed == false) NotInstalled(app.Name, download);
        var appName = app.Name.Split(' ')[0];
        if (PrimaryButton(FontAwesomeIcon.ExternalLinkAlt, $"Open in {appName}"))
            results[app.Id] = ClientInstaller.OpenLink(link, appName);
        ImGui.SameLine();
        if (SecondaryButton(FontAwesomeIcon.Copy, "Copy link")) ImGui.SetClipboardText(link);
        Tooltip("The install link, with your token. Paste it into a browser if the button does nothing.");
        ResultLine(app.Id);
        Steps(steps);
        if (note is not null) Hint(note);
    }

    /// <summary>How to take XIV MCP out of the app again: a button where XIV MCP can do it safely, otherwise the app's own steps.</summary>
    private void DrawRemoval(ClientApp app)
    {
        if (!ImGui.TreeNode($"Remove XIV MCP from {app.Name}")) return;
        Gap(4);
        switch (app.Id)
        {
            case "claude-desktop":
                RemovalSteps(["In Claude Desktop, open Settings → Extensions.", "Select Final Fantasy XIV (XIV MCP) and click Uninstall. To keep it but stop using it, switch it off there instead."]);
                break;
            case "claude":
            {
                var running = busy.ContainsKey("claude-remove");
                using (ImRaii.Disabled(running))
                    if (SecondaryButton(FontAwesomeIcon.Trash, running ? "Removing…" : "Remove from Claude Code"))
                    {
                        busy["claude-remove"] = true;
                        results.TryRemove("claude-remove", out _);
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            try { results["claude-remove"] = await ClientInstaller.RemoveFromClaudeCodeAsync(); }
                            finally { busy.TryRemove("claude-remove", out _); }
                        });
                    }
                Tooltip("Runs claude mcp remove for the entry for all your projects, and for every project that has its own entry.");
                ResultLine("claude-remove");
                break;
            }
            case "codex":
                if (SecondaryButton(FontAwesomeIcon.Trash, "Remove from ChatGPT")) results["codex-remove"] = ClientInstaller.RemoveFromCodex();
                Tooltip($"Deletes the XIV MCP entry from {ClientInstaller.CodexConfigPath}. Everything else in the file is kept, and the old file is backed up.");
                ResultLine("codex-remove");
                break;
            case "vscode":
                RemovalSteps(["In VS Code, open the Extensions view (the blocks icon on the left).", "Under MCP Servers – Installed, right-click ffxiv and choose Uninstall."]);
                break;
            case "cursor":
                RemovalSteps(["Open ~/.cursor/mcp.json in your user folder. Cursor's settings also list it under MCP.", "Delete the ffxiv entry and save the file."]);
                break;
            case "lmstudio":
                RemovalSteps(["In LM Studio, open the Program tab, then Install → Edit mcp.json.", "Delete the ffxiv entry and save the file."]);
                break;
            default:
                RemovalSteps(["Delete the ffxiv entry from the app's MCP server settings."]);
                break;
        }
        Gap(6);
        Hint("To cut off every app at once, switch off the server (top right) or generate a new access token below.");
        ImGui.TreePop();
    }

    private void RemovalSteps(string[] steps)
    {
        for (var i = 0; i < steps.Length; i++)
        {
            if (i > 0) Gap(3);
            ImGui.TextColored(Accent, $"{i + 1}.");
            ImGui.SameLine();
            using (Wrap()) ImGui.TextUnformatted(steps[i]);
        }
    }

    /// <summary>What using an AI app with XIV MCP costs: tokens, and with them the provider's plan or usage billing.</summary>
    private void DrawCosts()
    {
        Status(FontAwesomeIcon.Coins, Amber, "Costs");
        Gap(4);
        using (ImRaii.PushIndent(ImGui.GetFrameHeight(), false))
        {
            Hint("Every time your assistant reads the game or acts in it, the AI model handles a request, and that uses tokens. Controlling " +
                 "the game uses many: each tool call is a request, and a job such as a dungeon run can make hundreds of them.");
            Gap(4);
            Hint("Claude, ChatGPT, GitHub Copilot and Cursor include a small amount of use on their free plans, and game control uses it up " +
                 "quickly. For regular use you need a paid plan. With an API key, every token is billed, with no limit unless you set one with the provider.");
            Gap(4);
            Hint("LM Studio runs models on your own PC and costs nothing extra, but small models handle this many tools poorly.");
            Gap(4);
            Hint("XIV MCP itself is free. It cannot see how many tokens your assistant uses, so check the usage page of your plan.");
        }
    }

    private void DrawOtherApp(string endpoint, string? token)
    {
        Paragraph("Any app that supports MCP servers over HTTP can connect.");
        CommandBox("json", JsonConfig(endpoint, token is null ? null : Masked), JsonConfig(endpoint, token), "Copy JSON");
        Steps(["Add the JSON to the app's MCP server settings.", "Restart the app and ask about your character."]);
        Hint("Apps that can only start programs on your PC (stdio) can't use this. Claude Desktop is one of them: use its button instead.");
    }

    private void DrawServerAndToken(string endpoint)
    {
        var config = plugin.Config;
        var server = plugin.Server;
        if (!ImGui.CollapsingHeader($"{FontAwesomeIcon.Key.ToIconString()}  Server & access token")) return;
        ImGui.Indent();
        ImGui.SetNextItemWidth(120 * Ui.Scale);
        ImGui.InputInt("Port", ref portInput);
        portInput = Math.Clamp(portInput, 1024, 65535);
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Redo, portInput == config.Port ? "Restart server" : "Apply & restart"))
        {
            config.Port = portInput;
            config.Save();
            if (config.ServerEnabled) server.Start();
        }
        ImGui.TextColored(Muted, $"Address: {endpoint}  ·  only reachable from this PC.");
        if (portInput != config.Port) ImGui.TextColored(Amber, "Changing the port means setting up your apps again.");

        ImGui.Spacing();
        var requireToken = config.RequireToken;
        if (ImGuiComponents.ToggleButton("##token", ref requireToken))
        {
            config.RequireToken = requireToken;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Require access token");
        if (!config.RequireToken)
            ImGui.TextColored(Amber, "Without a token, any program on this PC can use the server.");
        else
        {
            using (Ui.MonoFont())
                ImGui.TextUnformatted(showToken ? config.Token : config.Token[..6] + new string('•', 18));
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(1, showToken ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye)) showToken = !showToken;
            Tooltip(showToken ? "Hide" : "Show");
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(2, FontAwesomeIcon.Copy)) ImGui.SetClipboardText(config.Token);
            Tooltip("Copy token");
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(3, FontAwesomeIcon.Sync))
            {
                config.Token = Configuration.NewToken();
                config.Save();
            }
            Tooltip("Generate a new token. Connected apps must then be set up again.");
            ImGui.TextColored(Muted, "The token stays the same across game restarts until you generate a new one.");
        }
        ImGui.Unindent();
    }

    // ---------------------------------------------------------------- pieces

    private const string Masked = "••••••";

    /// <summary>Vertical space, in unscaled pixels.</summary>
    private static void Gap(float pixels) => ImGui.Dummy(new Vector2(0, pixels * Ui.Scale));

    /// <summary>Wraps at the reading width (see <see cref="textWidth"/>), counted from the left edge of the tab.</summary>
    private IDisposable Wrap()
    {
        ImGui.PushTextWrapPos(textLeft + textWidth);
        return new Ui.Popper(ImGui.PopTextWrapPos);
    }

    private void Paragraph(string text)
    {
        using (Wrap()) ImGui.TextUnformatted(text);
        Gap(8);
    }

    private void Hint(string text)
    {
        using (Wrap()) ImGui.TextColored(Muted, text);
        Gap(2);
    }

    /// <summary>An icon and a line of text; wrapped lines stay indented under the text.</summary>
    private void Status(FontAwesomeIcon icon, Vector4 color, string text)
    {
        IconText(icon, color);
        ImGui.SameLine();
        using (Wrap()) ImGui.TextColored(color, text);
    }

    private void NotInstalled(string app, string download)
    {
        Status(FontAwesomeIcon.ExclamationTriangle, Amber, $"{app} doesn't seem to be installed on this PC.");
        ImGui.SameLine();
        if (ImGui.SmallButton($"Get {app}")) Dalamud.Utility.Util.OpenLink(download);
        Gap(6);
    }

    /// <summary>The main action of a section: a larger button in the accent colour.</summary>
    private static bool PrimaryButton(FontAwesomeIcon icon, string label)
    {
        using var colors = ImRaii.PushColor(ImGuiCol.Button, Accent with { W = 0.85f })
                                 .Push(ImGuiCol.ButtonHovered, Accent)
                                 .Push(ImGuiCol.ButtonActive, Accent with { W = 0.7f })
                                 .Push(ImGuiCol.Text, new Vector4(0.04f, 0.08f, 0.10f, 1));
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(14, 7) * Ui.Scale);
        return ImGuiComponents.IconButtonWithText(icon, label);
    }

    /// <summary>A button next to a <see cref="PrimaryButton"/>: same height, normal colours.</summary>
    private static bool SecondaryButton(FontAwesomeIcon icon, string label)
    {
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(14, 7) * Ui.Scale);
        return ImGuiComponents.IconButtonWithText(icon, label);
    }

    private void ResultLine(string id)
    {
        if (!results.TryGetValue(id, out var r)) return;
        Gap(4);
        Status(r.Ok ? FontAwesomeIcon.Check : FontAwesomeIcon.ExclamationTriangle, r.Ok ? Green : Red, r.Message);
    }

    /// <summary>What to do next, numbered, with room between the steps. Wrapped lines stay indented under their step.</summary>
    private void Steps(string[] steps)
    {
        Gap(12);
        ImGui.TextColored(Muted, "Then:");
        Gap(2);
        for (var i = 0; i < steps.Length; i++)
        {
            if (i > 0) Gap(3);
            ImGui.TextColored(Accent, $"{i + 1}.");
            ImGui.SameLine();
            using (Wrap()) ImGui.TextUnformatted(steps[i]);
        }
        Gap(10);
    }

    private void CommandBox(string id, string preview, string copy, string buttonLabel)
    {
        var lines = preview.Count(ch => ch == '\n') + 1;
        // Long commands wrap inside the box; leave room for that.
        using (Ui.MonoFont())
            lines = Math.Max(lines, (int)Math.Ceiling(ImGui.CalcTextSize(preview, false, textWidth - 20 * Ui.Scale).Y / ImGui.GetTextLineHeight()));
        using (ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0.25f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.WindowPadding, new Vector2(10, 8) * Ui.Scale))
        using (ImRaii.Child($"##cmd-{id}", new Vector2(textWidth, ImGui.GetTextLineHeightWithSpacing() * lines + 18 * Ui.Scale), true))
        using (Ui.MonoFont())
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Cyan, preview);
            ImGui.PopTextWrapPos();
        }
        Gap(4);
        if (PrimaryButton(FontAwesomeIcon.Copy, $"{buttonLabel}##{id}")) ImGui.SetClipboardText(copy);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Muted, "Your token is filled in when copying.");
    }

    private static string ClaudeCommand(string endpoint, string? token) =>
        $"claude mcp add --scope user --transport http {ClientSetup.ServerName} {endpoint}" + (token is null ? "" : $" --header \"Authorization: Bearer {token}\"");

    private static string CodexCommand(string endpoint, string? token) => token is null
        ? $"codex mcp add {ClientSetup.ServerName} --url {endpoint}"
        : $"setx XIVMCP_TOKEN \"{token}\"\ncodex mcp add {ClientSetup.ServerName} --url {endpoint} --bearer-token-env-var XIVMCP_TOKEN";

    private static string CodexToml(string endpoint, string? token) => ClientSetup.MergeCodexConfig("", endpoint, token);

    private static string JsonConfig(string endpoint, string? token)
    {
        var headers = token is null ? "" : $",\n      \"headers\": {{ \"Authorization\": \"Bearer {token}\" }}";
        return $$"""
            {
              "mcpServers": {
                "{{ClientSetup.ServerName}}": {
                  "type": "http",
                  "url": "{{endpoint}}"{{headers}}
                }
              }
            }
            """;
    }
}
