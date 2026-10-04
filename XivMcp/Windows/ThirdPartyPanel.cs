using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using XivMcp.Api;
using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Permissions;
using XivMcp.Util;
using static XivMcp.Windows.ConfigWindow;

namespace XivMcp.Windows;

/// <summary>What the third-party panel needs (kept separate from the plugin so the panel only depends on what it draws).</summary>
internal interface IThirdPartyHost
{
    List<PluginApi.PluginInfo> Plugins();
    ToolGate Gate { get; }
    IPolicyStore Policies { get; }

    /// <summary>A plugin was enabled or disabled: tell clients the tool list changed.</summary>
    void ToolsChanged();

    /// <summary>Whether the plugin's current registration still needs the player's decision.</summary>
    RegistrationReview.Result Pending(string pluginId);

    /// <summary>Enable (consent to the current registration) or keep disabled (remembered until the registration changes).</summary>
    void Decide(string pluginId, bool enable);

    /// <summary>The custom repositories added in Dalamud's settings, or null when they can't be read.</summary>
    IReadOnlyList<(string Url, bool Enabled)>? CustomRepositories();
}

/// <summary>
/// The "Third-party plugins" tab, laid out like the Modules cards but marked as external: a violet accent, a THIRD-PARTY badge and
/// a plug icon. Per plugin: an on/off switch, the decision or suspension notice when there is one, one row per declared capability
/// (risk, Allow / Ask / Deny, expandable to the tools using it), session approvals and recent activity.
/// </summary>
internal sealed class ThirdPartyPanel(IThirdPartyHost host)
{
    private static readonly Vector4 External = new(0.62f, 0.48f, 0.90f, 1);   // violet: never used by XIV MCP's own cards
    private static readonly Vector4 Waiting = new(0.72f, 0.56f, 0.24f, 1);
    private static readonly Vector4 Problem = new(0.72f, 0.30f, 0.30f, 1);
    private static readonly Vector4 Off = new(0.45f, 0.46f, 0.50f, 1);

    /// <summary>A plugin to scroll to on the next frame (from the registration notification).</summary>
    private string? focus;

    /// <summary>Expanded rows: "plugin:capability" for tool lists, "plugin:activity" for the activity list.</summary>
    private readonly HashSet<string> expanded = [];

    public void Focus(string pluginId) => focus = pluginId;

    public string TabLabel()
    {
        var plugins = host.Plugins();
        var suspended = plugins.Count(p => p.Policy.Suspended);
        var missing = plugins.Count(p => p.Dependencies.Any(d => d.IsError));
        return plugins.Count == 0 ? "Third-party plugins"
             : suspended > 0 ? $"Third-party plugins ({suspended} suspended)"
             : missing > 0 ? $"Third-party plugins ({plugins.Count}, {missing} missing something)"
             : $"Third-party plugins ({plugins.Count})";
    }

    public void Draw()
    {
        DrawTrustBanner();
        ImGui.Spacing();

        var plugins = host.Plugins();
        if (plugins.Count == 0)
        {
            IconText(FontAwesomeIcon.InfoCircle, Muted);
            ImGui.SameLine();
            ImGui.TextColored(Muted, "No third-party plugin has registered tools yet.");
            return;
        }
        PermissionsPanel.CardGrid("thirdparty", plugins, DrawCard);
        ImGui.Spacing();
        ImGui.TextColored(Muted, "Full audit log: pluginConfigs/XivMcp/audit.jsonl");
    }

    /// <summary>A violet strip that says what these plugins are and how far XIV MCP can protect you.</summary>
    private static void DrawTrustBanner()
    {
        var scale = Ui.Scale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        ImGui.Indent(12 * scale);
        ImGui.Dummy(new Vector2(0, 4 * scale));
        IconText(FontAwesomeIcon.Plug, External);
        ImGui.SameLine();
        ImGui.TextColored(External, "External plugins");
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - 24 * scale);
        ImGui.TextColored(Muted, "These plugins are made by others and run their own code. XIV MCP can't sandbox them, but it decides when they run: " +
                                 "each one is off until you enable it, each capability (or single tool) is allowed, asked or denied as you set it, and a plugin that does " +
                                 "something it didn't declare is suspended. Only enable plugins you trust.");
        ImGui.PopTextWrapPos();
        ImGui.Unindent(12 * scale);
        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + 6 * scale);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(start, end, ImGui.GetColorU32(External with { W = 0.08f }), 6 * scale);
        dl.AddRectFilled(start, new Vector2(start.X + 4 * scale, end.Y), ImGui.GetColorU32(External), 6 * scale, ImDrawFlags.RoundCornersLeft);
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 4 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawCard(PluginApi.PluginInfo p)
    {
        using var id = ImRaii.PushId($"tp-{p.InternalName}");
        var scale = Ui.Scale;
        var policy = p.Policy;
        var status = PluginStatus.Of(policy);
        var pending = host.Pending(p.InternalName);
        var accent = status.State switch
        {
            PluginState.Suspended => Problem,
            PluginState.AwaitingConsent or PluginState.Undecided => Waiting,
            PluginState.Enabled => External,
            _ => Off,
        };

        var pad = 10 * scale;
        var bar = 4 * scale;
        var width = ImGui.GetContentRegionAvail().X - 2 * scale;
        var inner = width - 2 * pad - bar;
        var start = ImGui.GetCursorScreenPos();
        if (string.Equals(focus, p.InternalName, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.SetScrollHereY(0);
            focus = null;
        }

        ImGui.Indent(pad + bar);
        ImGui.Dummy(new Vector2(0, pad - ImGui.GetStyle().ItemSpacing.Y));
        var left = ImGui.GetCursorPosX();
        var headerY = ImGui.GetCursorPosY();
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, p.Loaded && status.State != PluginState.KeptDisabled ? 1f : 0.6f))
        {
            // Header: plug icon, name with the THIRD-PARTY badge, and a one-line summary.
            using (Ui.IconFont())
            {
                ImGui.SetWindowFontScale(1.45f);
                ImGui.TextColored(accent, FontAwesomeIcon.Plug.ToIconString());
                ImGui.SetWindowFontScale(1f);
            }
            ImGui.SameLine(0, 10 * scale);
            using (ImRaii.Group())
            {
                ImGui.TextUnformatted(p.DisplayName);
                ImGui.SameLine();
                Badge("THIRD-PARTY", External);
                ImGui.TextColored(Muted, Summary(p, status));
            }

            if (status.State == PluginState.Suspended) Notice(FontAwesomeIcon.ExclamationTriangle, Problem, $"Suspended: {policy.SuspendReason ?? "it did something it didn't declare."}",
                left, inner, ("Lift suspension", () => { policy.Lift(); host.Policies.Save(); }, "Only lift it if you know why it happened, e.g. you spent gil yourself while the call ran."));
            if (pending.What != RegistrationReview.Kind.None && p.Tools.Count > 0)
                Notice(FontAwesomeIcon.QuestionCircle, Waiting, DecisionText(p, policy, pending), left, inner,
                    ("Enable", () => host.Decide(p.InternalName, true), "Offer its tools to your assistant, under the settings below."),
                    ("Keep disabled", () => host.Decide(p.InternalName, false), "Keep it off. You won't be asked again unless its registration changes."));

            // One section per declared capability, riskiest first, and reading (for tools that only read) last.
            var sections = p.Tools.SelectMany(PluginPolicy.Sections).Distinct().Select(Capabilities.Find).OfType<Capability>()
                            .OrderBy(c => c.Id == Capabilities.ReadGame).ThenByDescending(c => c.Risk).ThenBy(c => c.Title).ToList();
            ImGui.Dummy(new Vector2(0, 2 * scale));
            for (var i = 0; i < sections.Count; i++)
            {
                if (i > 0) PermissionsPanel.Divider(inner, External with { W = 0.28f });
                CapabilityRow(p, policy, sections[i], left, inner);
            }

            // The tools its jobs use, with what is missing and how to install it.
            if (p.Dependencies.Count > 0)
            {
                PermissionsPanel.Divider(inner, External with { W = 0.28f });
                DrawDependencies(p, left, inner);
            }

            // Footer: session approvals and recent activity.
            PermissionsPanel.Divider(inner, External with { W = 0.28f });
            var sessions = host.Gate.Sessions.Count(p.InternalName);
            ImGui.TextColored(Muted, sessions == 0 ? "No approvals for this session." : $"{sessions} approval(s) for this session.");
            if (sessions > 0)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Clear")) host.Gate.Sessions.Clear(p.InternalName);
            }
            var activity = host.Gate.Audit.Recent(p.InternalName, 300).Where(e => !e.BuiltIn).Take(25).ToList();
            var flagged = activity.Count(e => e.Flagged);
            if (Toggle($"{p.InternalName}:activity", activity.Count == 0 ? "Activity: no calls yet" : $"Activity ({activity.Count}{(flagged > 0 ? $", {flagged} flagged" : "")})",
                       activity.Count > 0, flagged > 0 ? Problem : null))
                DrawAuditTable(activity, showSource: false);
        }
        var contentBottom = ImGui.GetItemRectMax().Y;

        // The on/off switch: enabling consents to the current registration, off keeps it disabled.
        var afterContent = ImGui.GetCursorPos();
        ImGui.SetCursorPos(new Vector2(left + inner - ImGui.GetFrameHeight() * 1.55f, headerY));
        var isOn = policy.Enabled && !policy.AwaitingConsent;
        if (ImGuiComponents.ToggleButton("##enabled", ref isOn)) host.Decide(p.InternalName, isOn);
        Tooltip(isOn ? "Enabled: its tools are offered to your assistant." : policy.AwaitingConsent ? "Paused: it changed its tools and waits for your consent." : "Off: its tools are not offered.");
        ImGui.SetCursorPos(afterContent);
        ImGui.Unindent(pad + bar);

        // Frame: violet (or state) tint, border and accent bar, plus a thin top line that built-in cards don't have.
        var end = new Vector2(start.X + width, contentBottom + pad);
        var dl = ImGui.GetWindowDrawList();
        var rounding = 6 * scale;
        dl.AddRectFilled(start, end, ImGui.GetColorU32(accent with { W = 0.07f }), rounding);
        dl.AddRect(start, end, ImGui.GetColorU32(accent with { W = 0.5f }), rounding);
        dl.AddRectFilled(start, new Vector2(start.X + bar, end.Y), ImGui.GetColorU32(accent), rounding, ImDrawFlags.RoundCornersLeft);
        dl.AddLine(new Vector2(start.X + rounding, start.Y + 1), new Vector2(end.X - rounding, start.Y + 1), ImGui.GetColorU32(accent with { W = 0.8f }), 2 * scale);
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 8 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>One capability: caret, title and risk; Allow / Ask / Deny (critical ones can't be always allowed); expandable to its tools.</summary>
    private void CapabilityRow(PluginApi.PluginInfo p, PluginPolicy policy, Capability cap, float left, float inner)
    {
        using var id = ImRaii.PushId(cap.Id);
        var key = $"{p.InternalName}:{cap.Id}";
        var tools = p.Tools.Where(t => PluginPolicy.Sections(t).Contains(cap.Id)).OrderBy(t => t.Name).ToList();
        var reading = cap.Id == Capabilities.ReadGame;
        var label = reading ? "Reading" : cap.Title;
        var owned = tools.Count(t => policy.ToolMode(t.Name) is not null);
        var open = Toggle(key, owned > 0 ? $"{label} · {owned} set on their own" : label, tools.Count > 0, null, () =>
        {
            ImGui.SameLine();
            Badge(cap.Risk.ToString().ToUpperInvariant(), RiskColor(cap.Risk));
        }, reading ? "Tools that only read game state: your character, inventory, zone and other game data. They change nothing." : cap.Description);

        var mode = policy.ModeFor(cap.Id);
        ImGui.SetCursorPosX(left);
        var options = Capabilities.IsModeAllowed(cap.Risk, PolicyMode.Allow)
            ? new (string?, PolicyMode?)[] { (null, PolicyMode.Allow), (null, PolicyMode.Ask), (null, PolicyMode.Deny) }
            : [("Always ask", PolicyMode.Ask), (null, PolicyMode.Deny)];
        PermissionsPanel.ModeButtons(inner, options, mode, m =>
        {
            policy.Modes[cap.Id] = m!.Value;
            if (m != PolicyMode.Ask) host.Gate.Sessions.Clear(p.InternalName);
            host.Policies.Save();
        });

        if (!open) return;
        var step = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X;
        using var indent = ImRaii.PushIndent(step, false);
        var top = ImGui.GetCursorScreenPos();
        for (var i = 0; i < tools.Count; i++)
        {
            if (i > 0) ImGui.Dummy(new Vector2(0, 4 * Ui.Scale));
            DrawTool(p, policy, tools[i], cap.Id, left + step, inner - step);
        }
        PermissionsPanel.GuideLine(top, step, External with { W = 0.4f });
        ImGui.Dummy(new Vector2(0, 2 * Ui.Scale));
    }

    /// <summary>
    /// One tool: name, its own setting if any, the other sections it is listed under, a short description (full text on hover) and
    /// Follow / Allow / Ask / Deny. Tools with a critical capability can't be set to Allow.
    /// </summary>
    private void DrawTool(PluginApi.PluginInfo p, PluginPolicy policy, McpTool t, string section, float left, float inner)
    {
        using var id = ImRaii.PushId(t.Name);
        ImGui.Dummy(new Vector2(0, 2 * Ui.Scale));
        var own = policy.ToolMode(t.Name);
        using (Ui.MonoFont()) ImGui.TextUnformatted(t.Name);
        ImGui.SameLine();
        ImGui.TextColored(t.ReadOnly ? PermissionsPanel.ModeColor(PolicyMode.Allow) : Muted, t.ReadOnly ? "reads" : t.Destructive ? "hard to undo" : "acts");
        if (own is { } o)
        {
            ImGui.SameLine();
            ImGui.TextColored(PermissionsPanel.ModeColor(o), $"set to {o}");
        }
        var others = PluginPolicy.Sections(t).Where(c => c != section).Select(c => Capabilities.Find(c)?.Title ?? c).ToList();
        ImGui.PushTextWrapPos(left + inner);
        if (others.Count > 0) ImGui.TextColored(Muted, $"Also listed under: {string.Join(", ", others)} (one setting for all)");
        ImGui.TextColored(Muted, ToolText.Truncate(ToolText.FirstSentence(t.Description), 110));
        ImGui.PopTextWrapPos();
        DescriptionTooltip(t.Description, $"Declares: {string.Join(", ", PluginPolicy.Sections(t).Select(c => Capabilities.Find(c)?.Title ?? c))}");

        var critical = PluginPolicy.IsCritical(t);
        (string?, PolicyMode?)[] options = critical
            ? [("Follow", null), ("Always ask", PolicyMode.Ask), (null, PolicyMode.Deny)]
            : [("Follow", null), (null, PolicyMode.Allow), (null, PolicyMode.Ask), (null, PolicyMode.Deny)];
        ImGui.SetCursorPosX(left);
        PermissionsPanel.ModeButtons(inner, options, own, mode =>
        {
            policy.SetTool(t.Name, mode);
            if (mode != PolicyMode.Ask) host.Gate.Sessions.Clear(p.InternalName);
            host.Policies.Save();
        }, policy.SectionMode(t), small: true,
           followTip: others.Count > 0 ? "Follow the strictest of its sections (now {0})." : "Follow this section's setting (now {0}).");
    }

    /// <summary>
    /// "Tools its jobs use": one row per declared tool, with where it comes from and whether it can run. A tool that can't is shown
    /// as an error, with a way to get each plugin it needs: install, enable or update it, or add its repository first.
    /// </summary>
    private void DrawDependencies(PluginApi.PluginInfo p, float left, float inner)
    {
        var errors = p.Dependencies.Count(d => d.IsError);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Tools its jobs use");
        ImGui.SameLine();
        ImGui.TextColored(errors > 0 ? Red : Muted, errors > 0 ? $"{errors} of {p.Dependencies.Count} can't run" : $"{p.Dependencies.Count}, all ready");
        Tooltip($"{p.DisplayName} declared these tools for its background jobs. A job with a tool that is not available is refused when it starts. XIV MCP's own tools still run without the plugins they can use; they ask you to do that part instead.");

        foreach (var d in p.Dependencies)
        {
            using var id = ImRaii.PushId($"dep-{d.Dependency.Tool}");
            ImGui.Dummy(new Vector2(0, 2 * Ui.Scale));
            IconText(d.IsError ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.CheckCircle, d.IsError ? Red : Green);
            ImGui.SameLine();
            using (Ui.MonoFont()) ImGui.TextUnformatted(d.Dependency.Tool);
            ImGui.SameLine();
            ImGui.TextColored(Muted, d.Dependency.BuiltIn ? "XIV MCP" : $"{d.Dependency.PluginName} {d.Dependency.MinVersion} or newer");
            if (!d.Dependency.BuiltIn) Tooltip($"Internal name: {d.Dependency.Plugin}\nRepository: {RepoLabel(d.Dependency.Repo)}");

            var indent = ImGui.GetFrameHeight();
            using var _ = ImRaii.PushIndent(indent, false);
            ImGui.PushTextWrapPos(left + inner);
            if (d.IsError || d.Install.Count > 0) ImGui.TextColored(d.IsError ? Red : Muted, d.Message);
            if (d.State == DependencyState.ToolMissing)
                ImGui.TextColored(Muted, $"Update {d.Dependency.PluginName}, or check that it is set up to offer this tool.");
            ImGui.PopTextWrapPos();

            if (d.State == DependencyState.NotEnabled && ImGui.SmallButton($"Show {d.Dependency.PluginName}"))
                Focus(d.Dependency.Plugin!);
            foreach (var plugin in d.Install) DrawInstall(plugin, left + indent, inner - indent);
        }
    }

    /// <summary>One plugin to get: what it is for, and the buttons that get it (install, enable, update, or add its repository first).</summary>
    private void DrawInstall(PluginToInstall plugin, float left, float inner)
    {
        using var id = ImRaii.PushId($"inst-{plugin.InternalName}");
        ImGui.PushTextWrapPos(left + inner);
        ImGui.TextColored(plugin.Needed ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : Muted, $"{plugin.Name}: {plugin.Reason}");
        ImGui.PopTextWrapPos();

        var name = plugin.Name;
        if (plugin.Outdated)
        {
            if (ImGui.SmallButton($"Update {name}")) Svc.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.UpdateablePlugins, name);
            Tooltip($"Opens the plugin installer's updates, at {name}. It needs {plugin.MinVersion} or newer.");
            return;
        }
        if (plugin.Installed)
        {
            if (ImGui.SmallButton($"Enable {name}")) Svc.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.InstalledPlugins, name);
            Tooltip($"{name} is installed but turned off. Opens your installed plugins at {name}.");
            return;
        }

        var official = plugin.Repo == PluginCatalog.Official;
        var repoAdded = official || host.CustomRepositories()?.Any(r => r.Enabled && SameUrl(r.Url, plugin.Repo)) == true;
        if (!repoAdded)
        {
            ImGui.PushTextWrapPos(left + inner);
            ImGui.TextColored(Muted, $"{name} is not in Dalamud's main repository. Add its repository first: Dalamud settings → Experimental → " +
                                     "Custom Plugin Repositories, paste the URL, tick Enabled and save. Only add repositories you trust.");
            ImGui.PopTextWrapPos();
            using (Ui.MonoFont()) ImGui.TextColored(Muted, plugin.Repo);
            if (ImGui.SmallButton("Copy repository URL")) ImGui.SetClipboardText(plugin.Repo);
            ImGui.SameLine();
            if (ImGui.SmallButton("Open Dalamud settings")) Svc.PluginInterface.OpenDalamudSettingsTo(SettingsOpenKind.Experimental, "");
            Tooltip("Opens the Experimental tab, where custom plugin repositories are added.");
            ImGui.SameLine();
        }
        if (ImGui.SmallButton($"Install {name}")) Svc.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.AllPlugins, name);
        Tooltip(repoAdded ? $"Opens the plugin installer at {name}." : $"Opens the plugin installer at {name}, once its repository is added.");
    }

    private static string RepoLabel(string? repo) => repo == PluginCatalog.Official ? "Dalamud's main repository" : repo ?? "";

    private static bool SameUrl(string a, string b) => string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A caret plus label as one clickable area (hover turns both gold). Returns whether the row is expanded. <paramref name="after"/>
    /// draws extra items on the same line (a badge).
    /// </summary>
    private bool Toggle(string key, string label, bool expandable, Vector4? labelColor = null, Action? after = null, string? tooltip = null)
    {
        var open = expandable && expanded.Contains(key);
        var frame = ImGui.GetFrameHeight();
        var rowStart = ImGui.GetCursorPos();
        var hovered = false;
        if (expandable)
        {
            var width = frame + ImGui.GetStyle().ItemSpacing.X + ImGui.CalcTextSize(label).X;
            if (ImGui.InvisibleButton($"##t-{key}", new Vector2(width, frame)) && !expanded.Remove(key)) expanded.Add(key);
            hovered = ImGui.IsItemHovered();
            if (tooltip is not null) Tooltip(tooltip);
            ImGui.SetCursorPos(rowStart);
        }
        var color = hovered ? Accent : labelColor ?? ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        if (expandable)
            using (Ui.IconFont())
            {
                var icon = (open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight).ToIconString();
                var size = ImGui.CalcTextSize(icon);
                ImGui.SetCursorPos(rowStart + new Vector2((frame - size.X) / 2, (frame - size.Y) / 2));
                ImGui.TextColored(color, icon);
            }
        ImGui.SetCursorPos(rowStart + new Vector2(frame + ImGui.GetStyle().ItemSpacing.X, 0));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, label);
        if (!expandable && tooltip is not null) Tooltip(tooltip);
        after?.Invoke();
        return open;
    }

    /// <summary>A small filled label, e.g. THIRD-PARTY or HIGH.</summary>
    private static void Badge(string text, Vector4 color)
    {
        var scale = Ui.Scale;
        var size = ImGui.CalcTextSize(text) * 0.8f;
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetTextLineHeight() - size.Y) / 2);
        var padding = new Vector2(5 * scale, 1 * scale);
        ImGui.GetWindowDrawList().AddRectFilled(pos - new Vector2(0, padding.Y), pos + size + padding * 2 - new Vector2(0, padding.Y),
            ImGui.GetColorU32(color with { W = 0.85f }), 3 * scale);
        ImGui.SetWindowFontScale(0.8f);
        ImGui.SetCursorScreenPos(pos + new Vector2(padding.X, 0));
        ImGui.TextColored(new Vector4(1, 1, 1, 1), text);
        ImGui.SetWindowFontScale(1f);
        ImGui.SameLine(0, padding.X + 4 * scale);
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>A coloured notice inside the card with up to two buttons.</summary>
    private static void Notice(FontAwesomeIcon icon, Vector4 color, string text, float left, float inner, params (string Label, Action Click, string Tip)[] buttons)
    {
        ImGui.Dummy(new Vector2(0, 2 * Ui.Scale));
        IconText(icon, color);
        ImGui.SameLine();
        ImGui.PushTextWrapPos(left + inner);
        ImGui.TextColored(color, text);
        ImGui.PopTextWrapPos();
        for (var i = 0; i < buttons.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (ImGui.SmallButton(buttons[i].Label)) buttons[i].Click();
            Tooltip(buttons[i].Tip);
        }
    }

    private static string DecisionText(PluginApi.PluginInfo p, PluginPolicy policy, RegistrationReview.Result pending)
    {
        var parts = new List<string>();
        if (pending.NewTools.Count > 0) parts.Add($"{(pending.What == RegistrationReview.Kind.NewPlugin ? "Tools" : "New tools")}: {string.Join(", ", pending.NewTools)}.");
        if (pending.New.Count > 0) parts.Add($"{(pending.What == RegistrationReview.Kind.NewPlugin ? "It asks to" : "Newly asks to")}: {string.Join(", ", pending.New.Select(c => c.Title.ToLowerInvariant()))}.");
        var head = pending.What == RegistrationReview.Kind.NewPlugin ? "Wants to register with XIV MCP."
                 : policy.AwaitingConsent ? "Changed its registration; its tools are paused." : "Changed its registration.";
        return $"{head} {string.Join(" ", parts)}";
    }

    private string Summary(PluginApi.PluginInfo p, PluginStatus status)
    {
        if (!p.Loaded) return "Not loaded";
        var missing = p.Dependencies.Count(d => d.IsError);
        var tools = $"{p.Tools.Count} tool{(p.Tools.Count == 1 ? "" : "s")}" + (missing > 0 ? $" · its jobs miss {missing} tool{(missing == 1 ? "" : "s")}" : "");
        return status.State switch
        {
            PluginState.Undecided => $"Waiting for your decision · {tools}",
            PluginState.KeptDisabled => $"Kept disabled · {tools}",
            PluginState.AwaitingConsent => $"Paused: waiting for your consent · {tools}",
            PluginState.Suspended => $"Suspended · {tools}",
            _ => $"Enabled · {tools}{AskSummary(p)}",
        };
    }

    private static string AskSummary(PluginApi.PluginInfo p)
    {
        var asks = p.Tools.Count(t => p.Policy.ModeForTool(t) == PolicyMode.Ask);
        var denied = p.Tools.Count(t => p.Policy.ModeForTool(t) == PolicyMode.Deny);
        return (asks > 0 ? $" · {asks} ask first" : "") + (denied > 0 ? $" · {denied} denied" : "");
    }

    /// <summary>Audit entries as a table: time, tool (and source), decision, result; details as tooltip.</summary>
    public static void DrawAuditTable(IReadOnlyList<AuditEntry> entries, bool showSource)
    {
        using var table = ImRaii.Table("##audit", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;
        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Tool", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Decision", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("Result", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableHeadersRow();
        foreach (var e in entries)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(Muted, e.Utc.ToLocalTime().ToString("t"));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted((e.Kind == "approval" ? $"{e.Tool} (asked)" : e.Tool) + (showSource && e.ProviderId != "XivMcp" ? $" · {e.ProviderName}" : ""));
            ImGui.TableNextColumn();
            ImGui.TextColored(e.Decision is "denied" or "blocked" or "refused" ? Red : e.Decision == "allowed" ? Muted : Accent, e.Decision.Replace('_', ' '));
            ImGui.TableNextColumn();
            if (e.Flagged)
            {
                IconText(FontAwesomeIcon.ExclamationTriangle, e.SuspendedPlugin ? Red : Amber);
                ImGui.SameLine();
            }
            var result = e.Flagged ? string.Join(" ", e.SideEffects.Where(s => s.Undeclared).Select(s => s.Detail).DefaultIfEmpty(e.Error ?? "undeclared request"))
                       : e.Outcome is null ? e.Error ?? ""
                       : e.Outcome == "ok" ? (e.SideEffects.Count > 0 ? string.Join(" ", e.SideEffects.Select(s => s.Detail)) : "ok")
                       : $"{e.Outcome}: {e.Error}";
            ImGui.TextColored(e.Flagged ? (e.SuspendedPlugin ? Red : Amber) : Muted, result.Length > 60 ? result[..57] + "..." : result);
            Tooltip(string.Join("\n", new[]
            {
                e.Summary, e.ArgsPreview is { Length: > 2 } a ? $"Arguments: {a}" : null, $"Declared: {string.Join(", ", e.Capabilities)}",
                e.DurationMs > 0 ? $"Took {TimeSpan.FromMilliseconds(e.DurationMs):g}" : null, e.InJob ? "Run as a job step." : null,
                e.SideEffects.Count > 0 ? "Observed: " + string.Join(" ", e.SideEffects.Select(s => s.Detail + (s.Undeclared ? " (undeclared)" : ""))) : null,
                e.SuspendedPlugin ? "This call suspended the plugin." : null,
            }.Where(s => !string.IsNullOrEmpty(s))));
        }
    }

    private static Vector4 RiskColor(RiskLevel r) => r switch
    {
        RiskLevel.Low => PermissionsPanel.ModeColor(PolicyMode.Allow),
        RiskLevel.Medium => Waiting,
        RiskLevel.High => Problem,
        _ => new Vector4(0.55f, 0.15f, 0.15f, 1),
    };
}
