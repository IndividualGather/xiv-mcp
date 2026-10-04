using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using XivMcp.Ui;

namespace XivMcp.Windows;

/// <summary>The Overlay tab: settings of the activity overlay (<see cref="OverlayWindow"/>).</summary>
internal sealed partial class ConfigWindow
{
    // The preview only makes sense while the settings are in view.
    public override void OnClose()
    {
        if (plugin.Overlay is { } overlay) overlay.Preview = false;
    }

    private void DrawOverlaySettings()
    {
        var s = plugin.Config.Overlay;
        var overlay = plugin.Overlay;
        var changed = false;
        var scale = Ui.Scale;
        // XIV MCP's accent rather than Dalamud's violet, like the tabs.
        using var accent = ImRaii.PushColor(ImGuiCol.CheckMark, Accent)
                                 .Push(ImGuiCol.SliderGrab, Accent with { W = 0.8f })
                                 .Push(ImGuiCol.SliderGrabActive, Accent);

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "A small window on your screen that shows the tool calls and background jobs running right now. " +
                                 "It only appears while something runs, and closes a few seconds after.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var enabled = s.Enabled;
        if (ImGuiComponents.ToggleButton("##overlay-on", ref enabled)) { s.Enabled = enabled; changed = true; }
        ImGui.SameLine();
        ImGui.TextUnformatted(enabled ? "Show the overlay while something runs" : "The overlay is off");

        // Placing it: it is invisible while idle, so the preview shows it with sample content.
        ImGui.Spacing();
        var preview = overlay.Preview;
        using (ImRaii.PushColor(ImGuiCol.Button, Accent with { W = preview ? 0.55f : 0.18f }))
            if (ImGuiComponents.IconButtonWithText(preview ? FontAwesomeIcon.EyeSlash : FontAwesomeIcon.Eye, preview ? "Hide preview" : "Show preview")
                | Controls.Consume("overlay:preview"))
                overlay.Preview = !preview;
        Tooltip("Shows the overlay with sample content, so you can place it and try these settings. Hidden again when this window closes.");
        ImGui.SameLine();
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Undo, "Reset position") | Controls.Consume("overlay:reset-position")) overlay.ResetPosition();
        Tooltip("Moves the overlay back to where it starts, on the left of the screen.");

        // Layout and size.
        ImGui.Spacing();
        Section(FontAwesomeIcon.Columns, "Layout");
        changed |= LayoutButtons(s);
        ImGui.TextColored(Muted, s.Layout == OverlayLayout.Full
            ? "Full: a card per job with its progress bar, current step and progress, and each tool call with what it does."
            : "Minimal: one line per job or tool call, with its progress and time.");

        var percent = (int)MathF.Round(s.Scale * 100);
        ImGui.SetNextItemWidth(260 * scale);
        if (ImGui.SliderInt("Size##overlay-scale", ref percent, (int)(OverlaySettings.MinScale * 100), (int)(OverlaySettings.MaxScale * 100), "%d%%"))
        { s.Scale = percent / 100f; changed = true; }
        Tooltip("Size of the text, icons and spacing.");
        var opacity = (int)MathF.Round(s.Opacity * 100);
        ImGui.SetNextItemWidth(260 * scale);
        if (ImGui.SliderInt("Background##overlay-opacity", ref opacity, (int)(OverlaySettings.MinOpacity * 100), 100, "%d%%"))
        { s.Opacity = opacity / 100f; changed = true; }
        Tooltip("How opaque the overlay's background is. The text stays fully visible.");

        // Behaviour.
        ImGui.Spacing();
        Section(FontAwesomeIcon.HandPointer, "Interaction");
        changed |= Check("Lock position", s.Locked, v => s.Locked = v, "The overlay can't be dragged. Its buttons still work.");
        changed |= Check("Click-through", s.ClickThrough, v => s.ClickThrough = v,
            "Clicks go through the overlay to the game, as if it weren't there. It can't be moved, and its buttons don't work.");
        using (ImRaii.Disabled(s.ClickThrough))
            changed |= Check("Pause and cancel buttons", s.Interactive, v => s.Interactive = v,
                "Shows buttons to pause, resume or cancel a job on the overlay. Cancel asks for a second click. Off: the overlay only shows.");

        // What it shows.
        ImGui.Spacing();
        Section(FontAwesomeIcon.ListUl, "Content");
        changed |= Check("Tool calls", s.ShowToolCalls, v => s.ShowToolCalls = v, "Tool calls your assistant is making right now.");
        changed |= Check("Background jobs", s.ShowJobs, v => s.ShowJobs = v, "Jobs that are running, or waiting for their turn.");
        var after = s.ShowAfterMs;
        ImGui.SetNextItemWidth(260 * scale);
        if (ImGui.SliderInt("Show calls after##overlay-after", ref after, 0, 3000, "%d ms")) { s.ShowAfterMs = after; changed = true; }
        Tooltip("A tool call appears once it has run this long. Most calls only read and take a moment; this keeps the overlay from flickering.");
        var linger = s.LingerSeconds;
        ImGui.SetNextItemWidth(260 * scale);
        if (ImGui.SliderInt("Keep finished items##overlay-linger", ref linger, 0, 30, "%d s")) { s.LingerSeconds = linger; changed = true; }
        Tooltip("How long a finished call or job stays on the overlay, so you can see how it ended.");
        var max = s.MaxItems;
        ImGui.SetNextItemWidth(260 * scale);
        if (ImGui.SliderInt("At most##overlay-max", ref max, 1, 10, "%d items")) { s.MaxItems = max; changed = true; }
        Tooltip("More calls and jobs than this are counted as \"+N more\".");

        if (changed)
        {
            s.Normalize();
            plugin.Config.Save();
        }
    }

    /// <summary>Minimal | Full as two buttons, the active one filled.</summary>
    private bool LayoutButtons(OverlaySettings s)
    {
        var changed = false;
        var width = 130 * Ui.Scale;
        foreach (var (layout, label) in new[] { (OverlayLayout.Minimal, "Minimal"), (OverlayLayout.Full, "Full") })
        {
            if (layout != OverlayLayout.Minimal) ImGui.SameLine(0, 2 * Ui.Scale);
            var active = s.Layout == layout;
            using var colors = ImRaii.PushColor(ImGuiCol.Button, active ? Accent with { W = 0.6f } : new Vector4(1, 1, 1, 0.06f))
                                     .Push(ImGuiCol.ButtonHovered, Accent with { W = active ? 0.6f : 0.3f })
                                     .Push(ImGuiCol.Text, active ? new Vector4(1, 1, 1, 1) : Muted);
            if ((ImGui.Button(label, new Vector2(width, 0)) | Controls.Consume($"overlay:layout:{label.ToLowerInvariant()}")) && !active)
            {
                s.Layout = layout;
                changed = true;
            }
        }
        return changed;
    }

    private static bool Check(string label, bool value, Action<bool> set, string tip)
    {
        var v = value;
        var changed = ImGui.Checkbox(label, ref v);
        Tooltip(tip);
        ImGui.SameLine();
        ImGui.TextColored(Muted, "(?)");
        Tooltip(tip);
        if (changed) set(v);
        return changed;
    }
}
