using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XivMcp.Api;
using XivMcp.Permissions;
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
}

/// <summary>
/// The "Third-party plugins" tab: per plugin the on/off switch, a suspension notice, the policy for each declared capability, its tools,
/// session approvals and the audit log of its recent calls.
/// </summary>
internal sealed class ThirdPartyPanel(IThirdPartyHost host)
{
    private static readonly string[] ModeLabels = ["Allow", "Ask", "Deny"];

    /// <summary>A plugin to expand and scroll to on the next frame.</summary>
    private string? focus;

    public void Focus(string pluginId) => focus = pluginId;

    public string TabLabel()
    {
        var plugins = host.Plugins();
        var suspended = plugins.Count(p => p.Policy.Suspended);
        return plugins.Count == 0 ? "Third-party plugins" : suspended > 0 ? $"Third-party plugins ({suspended} suspended)" : $"Third-party plugins ({plugins.Count})";
    }

    public void Draw()
    {
        var plugins = host.Plugins();
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted,
            "Other plugins can offer tools to your assistant through XIV MCP. They run that plugin's own code, so XIV MCP can't sandbox them, but it " +
            "controls when they run: each plugin is off until you enable it, every capability it declares is allowed, asked or denied as you set it " +
            "here, and XIV MCP checks what each call actually changed. A plugin whose call does something it didn't declare is suspended.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        if (plugins.Count == 0)
        {
            IconText(FontAwesomeIcon.InfoCircle, Muted);
            ImGui.SameLine();
            ImGui.TextColored(Muted, "No third-party plugin has registered tools yet.");
            return;
        }

        foreach (var p in plugins) DrawPlugin(p);
        ImGui.Spacing();
        ImGui.TextColored(Muted, "Full audit log: pluginConfigs/XivMcp/audit.jsonl");
    }

    private void DrawPlugin(PluginApi.PluginInfo p)
    {
        using var id = ImRaii.PushId($"tp-{p.InternalName}");
        var policy = p.Policy;
        var flagged = host.Gate.Audit.Recent(p.InternalName, 200).Count(e => e.Flagged && !e.BuiltIn);

        var pending = host.Pending(p.InternalName);
        var enabled = policy.Enabled && !policy.AwaitingConsent;
        if (ImGuiComponents.ToggleButton("##enabled", ref enabled)) host.Decide(p.InternalName, enabled);
        Tooltip(enabled ? "Enabled: its tools are offered to your assistant."
                : policy.AwaitingConsent ? "Paused: it changed its registration and waits for your consent."
                : "Off: its tools are hidden and refused.");
        ImGui.SameLine();
        var focused = string.Equals(focus, p.InternalName, StringComparison.OrdinalIgnoreCase);
        if (focused) ImGui.SetNextItemOpen(true);
        var open = ImGui.CollapsingHeader($"{p.DisplayName}###hdr");
        if (focused)
        {
            ImGui.SetScrollHereY(0);
            focus = null;
        }
        ImGui.SameLine();
        if (policy.Suspended) ImGui.TextColored(Red, "suspended");
        else if (policy.AwaitingConsent) ImGui.TextColored(Gold, "changed: waiting for your consent");
        else if (!p.Loaded) ImGui.TextColored(Muted, "not loaded");
        else ImGui.TextColored(enabled ? Green : Muted, $"{p.Tools.Count} tool{(p.Tools.Count == 1 ? "" : "s")}{(enabled ? "" : " · off")}");
        if (flagged > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(Amber, $"{flagged} flagged");
        }

        if (policy.Suspended) DrawSuspension(policy);
        if (pending.What != RegistrationReview.Kind.None && p.Tools.Count > 0) DrawDecision(p, policy, pending);
        if (!open) return;

        using var indent = ImRaii.PushIndent();
        DrawCapabilityPolicies(p, policy);
        ImGui.Spacing();
        DrawTools(p);
        ImGui.Spacing();
        var sessions = host.Gate.Sessions.Count(p.InternalName);
        ImGui.TextColored(Muted, sessions == 0 ? "No session approvals." : $"{sessions} approval(s) for this session.");
        if (sessions > 0)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Clear")) host.Gate.Sessions.Clear(p.InternalName);
        }
        ImGui.Spacing();
        DrawAudit(p.InternalName);
        ImGui.Spacing();
    }

    /// <summary>A registration that needs the player's decision: enable it, or keep it disabled (remembered until it changes again).</summary>
    private void DrawDecision(PluginApi.PluginInfo p, PluginPolicy policy, RegistrationReview.Result pending)
    {
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.89f, 0.75f, 0.48f, 0.12f));
        using var child = ImRaii.Child("##decision", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 3.6f + 12 * Ui.Scale), true);
        IconText(FontAwesomeIcon.QuestionCircle, Gold);
        ImGui.SameLine();
        ImGui.TextColored(Gold, pending.What == RegistrationReview.Kind.NewPlugin ? $"{p.DisplayName} wants to register with XIV MCP"
                               : policy.AwaitingConsent ? $"{p.DisplayName} changed its registration; its tools are paused" : $"{p.DisplayName} changed its registration");
        ImGui.PushTextWrapPos();
        var parts = new List<string>();
        if (pending.NewTools.Count > 0) parts.Add($"{(pending.What == RegistrationReview.Kind.NewPlugin ? "Tools" : "New tools")}: {string.Join(", ", pending.NewTools)}.");
        if (pending.New.Count > 0) parts.Add($"{(pending.What == RegistrationReview.Kind.NewPlugin ? "It asks to" : "Newly asks to")}: {string.Join(", ", pending.New.Select(c => c.Title.ToLowerInvariant()))}.");
        ImGui.TextUnformatted(string.Join(" ", parts));
        ImGui.PopTextWrapPos();
        if (ImGui.SmallButton("Enable")) host.Decide(p.InternalName, true);
        Tooltip("Offer its tools to your assistant. Each capability then follows the policy below.");
        ImGui.SameLine();
        if (ImGui.SmallButton("Keep disabled")) host.Decide(p.InternalName, false);
        Tooltip("Keep it off. XIV MCP won't ask again unless its registration changes.");
    }

    private void DrawSuspension(PluginPolicy policy)
    {
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0.6f, 0.15f, 0.15f, 0.22f));
        using var child = ImRaii.Child("##suspended", new Vector2(-1, ImGui.GetTextLineHeightWithSpacing() * 3.4f + 12 * Ui.Scale), true);
        IconText(FontAwesomeIcon.ExclamationTriangle, Red);
        ImGui.SameLine();
        ImGui.TextColored(Red, $"Suspended {(policy.SuspendedUtc is { } t ? t.ToLocalTime().ToString("g") : "")}");
        ImGui.PushTextWrapPos();
        ImGui.TextUnformatted(policy.SuspendReason ?? "It did something it didn't declare.");
        ImGui.PopTextWrapPos();
        if (ImGui.SmallButton("Lift suspension"))
        {
            policy.Lift();
            host.Policies.Save();
        }
        Tooltip("Only lift it if you know why it happened, e.g. you spent gil yourself while the call ran.");
    }

    private void DrawCapabilityPolicies(PluginApi.PluginInfo p, PluginPolicy policy)
    {
        var declared = p.DeclaredCapabilities.Select(Capabilities.Find).OfType<Capability>().OrderByDescending(c => c.Risk).ThenBy(c => c.Title).ToList();
        if (declared.Count == 0) return;
        using var table = ImRaii.Table("##caps", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;
        ImGui.TableSetupColumn("What it may do", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Risk", ImGuiTableColumnFlags.WidthStretch, 0.7f);
        ImGui.TableSetupColumn("Policy", ImGuiTableColumnFlags.WidthStretch, 1.1f);
        ImGui.TableHeadersRow();
        foreach (var cap in declared)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(cap.Title);
            Tooltip($"{cap.Description}\n\nUsed by: {string.Join(", ", p.Tools.Where(t => t.Capabilities.Contains(cap.Id)).Select(t => t.Name))}");
            ImGui.TableNextColumn();
            ImGui.TextColored(RiskColor(cap.Risk), cap.Risk.ToString().ToLowerInvariant());
            ImGui.TableNextColumn();
            var mode = policy.ModeFor(cap.Id);
            ImGui.SetNextItemWidth(-1);
            using var combo = ImRaii.Combo($"##{cap.Id}", ModeLabels[(int)mode]);
            if (!combo) continue;
            foreach (var m in Enum.GetValues<PolicyMode>())
            {
                if (!Capabilities.IsModeAllowed(cap.Risk, m)) continue;
                if (!ImGui.Selectable(ModeLabels[(int)m], m == mode)) continue;
                policy.Modes[cap.Id] = m;
                if (m != PolicyMode.Ask) host.Gate.Sessions.Clear(p.InternalName);
                host.Policies.Save();
            }
        }
    }

    private static void DrawTools(PluginApi.PluginInfo p)
    {
        if (p.Tools.Count == 0)
        {
            ImGui.TextColored(Muted, p.Loaded ? "No tools registered right now." : "Not loaded: no tools.");
            return;
        }
        foreach (var t in p.Tools)
        {
            using (Ui.MonoFont()) ImGui.TextUnformatted(t.Name);
            ImGui.SameLine();
            ImGui.TextColored(t.ReadOnly ? Green : t.Destructive ? Red : Amber, t.ReadOnly ? "reads" : t.Destructive ? "hard to undo" : "acts");
            DescriptionTooltip(t.Description, $"Declares: {string.Join(", ", t.Capabilities.Select(c => Capabilities.Find(c)?.Title ?? c))}");
        }
    }

    private void DrawAudit(string pluginId) => DrawAuditTable(host.Gate.Audit.Recent(pluginId, 300).Where(e => !e.BuiltIn).Take(25).ToList(), showSource: false);

    /// <summary>Audit entries as a table: time, tool (and source), decision, result; details as tooltip.</summary>
    public static void DrawAuditTable(IReadOnlyList<AuditEntry> entries, bool showSource)
    {
        if (entries.Count == 0)
        {
            ImGui.TextColored(Muted, "No calls yet.");
            return;
        }
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
            ImGui.TextColored(e.Decision is "denied" or "blocked" or "refused" ? Red : e.Decision == "allowed" ? Muted : Gold, e.Decision.Replace('_', ' '));
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
        RiskLevel.Low => Green,
        RiskLevel.Medium => Amber,
        _ => Red,
    };
}
