using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using XivMcp.Mcp;
using XivMcp.Permissions;
using static XivMcp.Windows.ConfigWindow;

namespace XivMcp.Windows;

/// <summary>What the permission cards need (kept separate from the plugin so the panel only depends on what it draws).</summary>
internal interface IPermissionsHost
{
    IReadOnlyCollection<McpTool> Tools { get; }
    CorePolicy Policy { get; }
    void Save();
    bool IsInstalled(string pluginId);
    bool IsLoaded(string pluginId);

    /// <summary>Extra options shown in a group's card while its changes aren't denied (bell location, move delay, gil limit).</summary>
    Action? Options(string groupId);

    /// <summary>A group was turned on or off: tell clients the tool list changed.</summary>
    void ToolsChanged();

    /// <summary>Whether press_xivmcp_control asked to press this control (call it every frame the control is drawn).</summary>
    bool Press(string controlId);
}

/// <summary>
/// The permission groups of XIV MCP's own tools as cards in a two-column grid. Each card: a large icon, the name, a one-line summary of
/// what's allowed, a short description, and a segmented Allow / Ask / Deny control for reading and for changes.
/// </summary>
internal sealed class PermissionsPanel(IPermissionsHost host)
{
    private static readonly Vector4 AllowColor = new(0.30f, 0.62f, 0.34f, 1);
    private static readonly Vector4 AskColor = new(0.72f, 0.56f, 0.24f, 1);
    private static readonly Vector4 DenyColor = new(0.62f, 0.27f, 0.27f, 1);
    private static readonly Vector4 NeutralAccent = new(0.45f, 0.46f, 0.50f, 1);

    private static FontAwesomeIcon GroupIcon(string id) => id switch
    {
        "game_data" => FontAwesomeIcon.Eye,
        "game_navigation" => FontAwesomeIcon.Route,
        "items_retainers" => FontAwesomeIcon.Boxes,
        "market" => FontAwesomeIcon.Store,
        "ui_editing" => FontAwesomeIcon.Terminal,
        "online" => FontAwesomeIcon.Globe,
        "plugin_management" => FontAwesomeIcon.PuzzlePiece,
        "jobs" => FontAwesomeIcon.Tasks,
        "autoduty" => FontAwesomeIcon.Dungeon,
        "saucy" => FontAwesomeIcon.Dice,
        "artisan" => FontAwesomeIcon.Hammer,
        "gatherbuddyreborn" => FontAwesomeIcon.Leaf,
        "lifestream" => FontAwesomeIcon.PlaneDeparture,
        "itemvendorlocation" => FontAwesomeIcon.MapMarkedAlt,
        "fcch" => FontAwesomeIcon.Archive,
        _ => FontAwesomeIcon.ShieldAlt,
    };

    public void Draw()
    {
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "Each group has a setting for reading and one for changes. Allow runs the tool, Ask shows an approval window first, Deny blocks it.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var byGroup = host.Tools.Where(t => t.Provider.Trust != ProviderTrust.ThirdParty)
                          .GroupBy(t => PermissionCatalog.GroupOf(t)?.Id ?? "")
                          .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());

        // Search: filters sections and tool calls as you type.
        DrawSearch();
        var found = PermissionCatalog.Groups.ToDictionary(g => g.Id, g => ModuleSearch.Match(search, g.Title, g.Description,
            (byGroup.GetValueOrDefault(g.Id) ?? []).Select(t => new ModuleSearch.Tool(t.Name, ToolSummaries.For(t.Name) ?? ShortDescription(t.Description))).ToList()));
        void Card(PermissionGroup g) => DrawCard(g, byGroup.GetValueOrDefault(g.Id) ?? [], found[g.Id].Tools);

        var core = PermissionCatalog.Groups.Where(g => g.PluginId is null && found[g.Id].Visible).ToList();
        var installed = PermissionCatalog.Groups.Where(g => g.PluginId is not null && (g.Standalone || g.Plugins.Any(host.IsInstalled)) && found[g.Id].Visible).ToList();
        var soon = Upcoming.Where(u => ModuleSearch.Match(search, u.Title, u.Description, []).Visible).ToList();
        if (core.Count + installed.Count + soon.Count == 0)
        {
            ImGui.TextColored(Muted, $"Nothing matches \"{search.Trim()}\".");
            return;
        }
        if (core.Count > 0)
        {
            Section(FontAwesomeIcon.ShieldAlt, "XIV MCP");
            CardGrid("core", core, Card);
        }
        if (installed.Count > 0)
        {
            ImGui.Spacing();
            Section(FontAwesomeIcon.Link, "Integrations maintained by XIV MCP");
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Muted, "XIV MCP's own tools for these plugins, only while the plugin is loaded. Independent of the groups above.");
            ImGui.PopTextWrapPos();
            ImGui.Spacing();
            CardGrid("integrations", installed, Card);
        }

        if (soon.Count == 0) return;
        ImGui.Spacing();
        Section(FontAwesomeIcon.Hourglass, "Coming soon");
        CardGrid("soon", soon, DrawUpcoming);
    }

    /// <summary>A feature that is planned but not built yet: shown as a card so players see what's next. Nothing to set.</summary>
    private sealed record Feature(string Id, string Title, string Description, FontAwesomeIcon Icon);

    private static readonly Feature[] Upcoming =
    [
    ];

    /// <summary>A "Coming soon" card: dimmed, with the feature's icon, title, description and a label instead of a switch.</summary>
    private static void DrawUpcoming(Feature f)
    {
        using var id = ImRaii.PushId($"soon-{f.Id}");
        var scale = Ui.Scale;
        var pad = 10 * scale;
        var bar = 4 * scale;
        var width = ImGui.GetContentRegionAvail().X - 2 * scale;
        var inner = width - 2 * pad - bar;
        var start = ImGui.GetCursorScreenPos();
        ImGui.Indent(pad + bar);
        ImGui.Dummy(new Vector2(0, pad - ImGui.GetStyle().ItemSpacing.Y));
        var left = ImGui.GetCursorPosX();
        var headerY = ImGui.GetCursorPosY();
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, 0.6f))
        {
            using (Ui.IconFont())
            {
                ImGui.SetWindowFontScale(1.45f);
                ImGui.TextColored(NeutralAccent, f.Icon.ToIconString());
                ImGui.SetWindowFontScale(1f);
            }
            ImGui.SameLine(0, 10 * scale);
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(f.Title);
            ImGui.PushTextWrapPos(left + inner);
            ImGui.TextColored(Muted, f.Description);
            ImGui.PopTextWrapPos();
        }
        var contentBottom = ImGui.GetItemRectMax().Y;

        // "Coming soon" label where the other cards have their switch.
        var after = ImGui.GetCursorPos();
        ImGui.SetWindowFontScale(0.8f);
        var label = ImGui.CalcTextSize("COMING SOON");
        ImGui.SetCursorPos(new Vector2(left + inner - label.X - 8 * scale, headerY + 4 * scale));
        var at = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddRect(at - new Vector2(5, 2) * scale, at + label + new Vector2(5, 2) * scale, ImGui.GetColorU32(Accent with { W = 0.6f }), 3 * scale);
        ImGui.TextColored(Accent, "COMING SOON");
        ImGui.SetWindowFontScale(1f);
        ImGui.SetCursorPos(after);
        ImGui.Unindent(pad + bar);

        var end = new Vector2(start.X + width, contentBottom + pad);
        var dl = ImGui.GetWindowDrawList();
        var rounding = 6 * scale;
        dl.AddRectFilled(start, end, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.02f)), rounding);
        dl.AddRect(start, end, ImGui.GetColorU32(NeutralAccent with { W = 0.35f }), rounding);
        dl.AddRectFilled(start, new Vector2(start.X + bar, end.Y), ImGui.GetColorU32(NeutralAccent with { W = 0.6f }), rounding, ImDrawFlags.RoundCornersLeft);
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 8 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    private string search = "";

    /// <summary>The search field: a magnifier, the input, and a clear button while there is text.</summary>
    private void DrawSearch()
    {
        var scale = Ui.Scale;
        IconText(FontAwesomeIcon.Search, Muted);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(ImGui.GetContentRegionAvail().X - 40 * scale, 360 * scale));
        ImGui.InputTextWithHint("##modules-search", "Search sections and tool calls", ref search, 100);
        if (search.Length > 0)
        {
            ImGui.SameLine();
            if (ImGuiComponents.IconButton("##clear-search", FontAwesomeIcon.Times)) search = "";
            Tooltip("Clear the search");
        }
        ImGui.Spacing();
    }

    /// <summary>Two columns when there's room (one in a narrow window).</summary>
    internal static void CardGrid<T>(string id, List<T> groups, Action<T> draw)
    {
        var columns = ImGui.GetContentRegionAvail().X >= 540 * Ui.Scale ? 2 : 1;
        using var table = ImRaii.Table($"##{id}", columns, ImGuiTableFlags.SizingStretchSame);
        if (!table) return;
        foreach (var g in groups)
        {
            ImGui.TableNextColumn();
            draw(g);
        }
    }

    /// <param name="only">The tool calls the search matched (their rows open, other tools hidden); null shows the card as usual.</param>
    private void DrawCard(PermissionGroup g, List<McpTool> tools, IReadOnlyList<string>? only = null)
    {
        using var id = ImRaii.PushId($"card-{g.Id}");
        var scale = Ui.Scale;
        var read = g.HasRead ? host.Policy.ModeFor(g.Id, Access.Read) : (PolicyMode?)null;
        var write = g.HasWrite ? host.Policy.ModeFor(g.Id, Access.Write) : (PolicyMode?)null;
        var loaded = g.PluginId is null || g.Standalone || g.Plugins.Any(host.IsLoaded);
        // Only a plugin that offers some of the tools (TriadBuddy for the Gold Saucer): say which ones work.
        var partly = g.PluginId is { } own && loaded && !host.IsLoaded(own);
        var on = host.Policy.IsGroupOn(g.Id);
        // How open the group is decides the card's accent: its changes if it has any, else its reading.
        var openness = write ?? read ?? PolicyMode.Deny;
        var accent = !on ? NeutralAccent : openness switch { PolicyMode.Allow => AllowColor, PolicyMode.Ask => AskColor, _ => NeutralAccent };

        var pad = 10 * scale;
        var bar = 4 * scale;
        var width = ImGui.GetContentRegionAvail().X - 2 * scale; // keep the border inside the cell
        var inner = width - 2 * pad - bar;
        var start = ImGui.GetCursorScreenPos();

        // Everything inside the card is indented past the accent bar, so new lines start at its inner edge.
        ImGui.Indent(pad + bar);
        ImGui.Dummy(new Vector2(0, pad - ImGui.GetStyle().ItemSpacing.Y));
        var left = ImGui.GetCursorPosX();
        var headerY = ImGui.GetCursorPosY();
        using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, loaded && on ? 1f : 0.55f))
        {
            // Header: a larger icon, the name and a one-line summary.
            using (Ui.IconFont())
            {
                ImGui.SetWindowFontScale(1.45f);
                ImGui.TextColored(accent, GroupIcon(g.Id).ToIconString());
                ImGui.SetWindowFontScale(1f);
            }
            ImGui.SameLine(0, 10 * scale);
            using (ImRaii.Group())
            {
                ImGui.TextUnformatted(g.Title);
                var individual = tools.Count(t => host.Policy.ToolMode(t.Name) is not null);
                ImGui.TextColored(Muted, !on ? "Turned off: its tools are not offered to your assistant" : !loaded ? "Not loaded: its tools are hidden"
                    : partly ? $"Only some tools: the others need {XivMcp.Integrations.PluginCatalog.Find(g.PluginId!)?.Name ?? g.PluginId}"
                    : Summary(read, write) + (individual > 0 ? $" · {individual} set individually" : ""));
            }
            Tooltip(ToolList(tools, loaded));

            ImGui.PushTextWrapPos(left + inner);
            ImGui.TextColored(Muted, g.Description);
            ImGui.PopTextWrapPos();

            // A turned-off group shows nothing to set: its tools aren't offered at all.
            if (on)
            {
                Divider(inner, Lines);
                var labelWidth = ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X + Math.Max(ImGui.CalcTextSize("Reading").X, ImGui.CalcTextSize("Changes").X) + 12 * scale;
                Segmented(g, Access.Read, "Reading", tools, left, labelWidth, inner, only);
                Divider(inner, Lines);
                Segmented(g, Access.Write, "Changes", tools, left, labelWidth, inner, only);

                if (host.Options(g.Id) is { } options && write is not null and not PolicyMode.Deny)
                {
                    Divider(inner, Lines);
                    ImGui.TextColored(Muted, "Options");
                    ImGui.PushTextWrapPos(left + inner);
                    options();
                    ImGui.PopTextWrapPos();
                }
            }
        }
        var contentBottom = ImGui.GetItemRectMax().Y;

        // The on/off switch, right-aligned in the header and never dimmed.
        var afterContent = ImGui.GetCursorPos();
        var switchWidth = ImGui.GetFrameHeight() * 1.55f;
        ImGui.SetCursorPos(new Vector2(left + inner - switchWidth, headerY));
        var isOn = on;
        if (ImGuiComponents.ToggleButton("##on", ref isOn))
        {
            host.Policy.SetGroupOn(g.Id, isOn);
            host.Save();
            host.ToolsChanged();
        }
        Tooltip(isOn ? "On: its tools are offered to your assistant. Turn off to remove them." : "Off: its tools are not offered to your assistant at all.");
        ImGui.SetCursorPos(afterContent);
        ImGui.Unindent(pad + bar);

        // Frame: a faint tint, a border and an accent bar on the left, drawn after the content (no channel splitting inside the table).
        var end = new Vector2(start.X + width, contentBottom + pad);
        var drawList = ImGui.GetWindowDrawList();
        var rounding = 6 * scale;
        drawList.AddRectFilled(start, end, ImGui.GetColorU32(openness == PolicyMode.Deny ? new Vector4(1, 1, 1, 0.025f) : accent with { W = 0.07f }), rounding);
        drawList.AddRect(start, end, ImGui.GetColorU32(accent with { W = 0.45f }), rounding);
        drawList.AddRectFilled(start, new Vector2(start.X + bar, end.Y), ImGui.GetColorU32(accent), rounding, ImDrawFlags.RoundCornersLeft);

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 8 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>Rows (group id + access) whose tool list is expanded.</summary>
    private readonly HashSet<string> expanded = [];

    /// <summary>
    /// "▸ Reading  [Allow][Ask][Deny]": an arrow that expands the row's tools, the label in a fixed column, then three equal buttons
    /// filling the rest of the card. Expanded, every tool of the row is listed with a short description and its own setting.
    /// </summary>
    private void Segmented(PermissionGroup g, Access access, string label, List<McpTool> tools, float left, float labelWidth, float inner, IReadOnlyList<string>? only = null)
    {
        using var id = ImRaii.PushId(label);
        var key = $"{g.Id}:{access}";
        var mine = tools.Where(t => PermissionCatalog.AccessOf(t) == access && (only is null || only.Contains(t.Name))).ToList();
        // While searching, rows with matching tools are open; the others stay closed and show no tools.
        var open = only is null ? expanded.Contains(key) : mine.Count > 0;
        var frame = ImGui.GetFrameHeight();
        var rowStart = ImGui.GetCursorPos();
        var hovered = false;
        if (mine.Count > 0)
        {
            // Arrow and label are one clickable area: hovering either turns both gold, clicking either toggles the tool list.
            // (No cursor change: Dalamud draws ImGui cursors as an extra icon next to the game's own.)
            var labelEnd = frame + ImGui.GetStyle().ItemSpacing.X + ImGui.CalcTextSize(label).X;
            // Pressable as module:<group>:read|write, to show a module's tools (only the list; settings can't be pressed).
            if ((ImGui.InvisibleButton("##toggle", new Vector2(labelEnd, frame)) | host.Press($"module:{g.Id}:{(access == Access.Read ? "read" : "write")}"))
                && !expanded.Remove(key)) expanded.Add(key);
            hovered = ImGui.IsItemHovered();
            Tooltip(open ? "Hide the tools" : $"Set the {mine.Count} tool{(mine.Count == 1 ? "" : "s")} one by one");
            ImGui.SetCursorPos(rowStart);
        }
        var color = hovered ? Accent : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        if (mine.Count > 0)
        {
            using (Ui.IconFont())
            {
                var icon = (open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight).ToIconString();
                // Centre the caret in a frame-sized square, where the arrow button used to be.
                var size = ImGui.CalcTextSize(icon);
                ImGui.SetCursorPos(rowStart + new Vector2((frame - size.X) / 2, (frame - size.Y) / 2));
                ImGui.TextColored(color, icon);
            }
        }
        ImGui.SetCursorPos(rowStart + new Vector2(frame + ImGui.GetStyle().ItemSpacing.X, 0));
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(color, label);
        ImGui.SameLine();
        ImGui.SetCursorPosX(left + labelWidth);
        if (!g.Has(access))
        {
            ImGui.TextColored(Muted, access == Access.Write ? "Only reads" : "Nothing to read");
            return;
        }
        var current = host.Policy.ModeFor(g.Id, access);
        ModeButtons(inner - labelWidth, [(null, PolicyMode.Allow), (null, PolicyMode.Ask), (null, PolicyMode.Deny)], current, mode =>
        {
            host.Policy.Set(g.Id, access, mode!.Value);
            host.Save();
        });

        if (!open || mine.Count == 0) return;
        using var indent = ImRaii.PushIndent(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X, false);
        var toolLeft = ImGui.GetCursorPosX();
        var toolInner = inner - (toolLeft - left);
        var top = ImGui.GetCursorScreenPos();
        for (var i = 0; i < mine.Count; i++)
        {
            if (i > 0) ImGui.Dummy(new Vector2(0, 4 * Ui.Scale));
            DrawTool(mine[i], current, toolLeft, toolInner);
        }
        GuideLine(top, ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X, Lines with { W = 0.3f });
        ImGui.Dummy(new Vector2(0, 2 * Ui.Scale));
    }

    /// <summary>Built-in cards draw their dividers and guide lines in neutral grey; third-party cards use their violet.</summary>
    private static readonly Vector4 Lines = new(1, 1, 1, 0.13f);

    /// <summary>Space, a faint line across the card, and space: keeps the sections of a card apart.</summary>
    internal static void Divider(float inner, Vector4 color)
    {
        var scale = Ui.Scale;
        ImGui.Dummy(new Vector2(0, 5 * scale));
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(inner, 0), ImGui.GetColorU32(color), 1.5f * scale);
        ImGui.Dummy(new Vector2(0, 6 * scale));
    }

    /// <summary>A vertical line from <paramref name="top"/> to the last item, half an indent to its left: marks an expanded tool list as part of its row.</summary>
    internal static void GuideLine(Vector2 top, float indent, Vector4 color)
    {
        var x = top.X - indent / 2;
        ImGui.GetWindowDrawList().AddLine(new Vector2(x, top.Y + 2 * Ui.Scale), new Vector2(x, ImGui.GetItemRectMax().Y), ImGui.GetColorU32(color), 2 * Ui.Scale);
    }

    /// <summary>One tool: its name, a short description, and Group / Allow / Ask / Deny (Group = follow the group's setting).</summary>
    private void DrawTool(McpTool t, PolicyMode groupMode, float left, float inner)
    {
        using var id = ImRaii.PushId(t.Name);
        ImGui.Dummy(new Vector2(0, 2 * Ui.Scale));
        var own = host.Policy.ToolMode(t.Name);
        using (Ui.MonoFont()) ImGui.TextUnformatted(t.Name);
        if (own is { } o)
        {
            ImGui.SameLine();
            ImGui.TextColored(ModeColor(o), $"set to {o}");
        }
        ImGui.PushTextWrapPos(left + inner);
        // The player-facing text for XIV MCP's tools; third-party tools only have their own description.
        var plain = ToolSummaries.For(t.Name);
        ImGui.TextColored(Muted, plain ?? ShortDescription(t.Description));
        ImGui.PopTextWrapPos();
        if (plain is not null) DescriptionTooltip(t.Description, intro: "What your assistant reads:");
        else if (ShortDescription(t.Description) != t.Description.Trim()) DescriptionTooltip(t.Description); // the full text when the short one leaves anything out
        ImGui.SetCursorPosX(left);
        ModeButtons(inner, [("Group", null), (null, PolicyMode.Allow), (null, PolicyMode.Ask), (null, PolicyMode.Deny)], own, mode =>
        {
            host.Policy.SetTool(t.Name, mode);
            host.Save();
        }, groupMode, small: true);
    }

    /// <summary>
    /// A row of equal buttons, the active one filled in its colour. A null option means "follow the group": it is filled in the group's
    /// colour, dimmed, when active.
    /// </summary>
    internal static void ModeButtons(float width, (string? Label, PolicyMode? Mode)[] options, PolicyMode? current, Action<PolicyMode?> set,
                                    PolicyMode? inherited = null, bool small = false, string followTip = "Follow the group's setting (now {0}).")
    {
        var gap = 2 * Ui.Scale;
        var buttonWidth = MathF.Floor((width - (options.Length - 1) * gap) / options.Length);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap, ImGui.GetStyle().ItemSpacing.Y));
        using var padding = ImRaii.PushStyle(ImGuiStyleVar.FramePadding, ImGui.GetStyle().FramePadding with { Y = 1 * Ui.Scale }, small);
        for (var i = 0; i < options.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            var (label, mode) = options[i];
            var active = mode == current;
            var color = mode is { } m ? ModeColor(m) : ModeColor(inherited ?? PolicyMode.Deny) with { W = 0.55f };
            using var colors = ImRaii.PushColor(ImGuiCol.Button, active ? color : new Vector4(1, 1, 1, 0.06f))
                                     .Push(ImGuiCol.ButtonHovered, active ? color : color with { W = 0.45f })
                                     .Push(ImGuiCol.ButtonActive, color)
                                     .Push(ImGuiCol.Text, active ? new Vector4(1, 1, 1, 1) : Muted);
            if (ImGui.Button(label ?? mode.ToString(), new Vector2(buttonWidth, 0)) && !active) set(mode);
            if (mode is null && inherited is { } inh) Tooltip(string.Format(followTip, inh));
        }
    }

    internal static Vector4 ModeColor(PolicyMode m) => m switch { PolicyMode.Allow => AllowColor, PolicyMode.Ask => AskColor, _ => DenyColor };

    /// <summary>The first sentence of a tool's description, cut at a word after ~110 characters; the full text is the tooltip.</summary>
    private static string ShortDescription(string description) => ToolText.Truncate(ToolText.FirstSentence(description), 110);

    private static string Summary(PolicyMode? read, PolicyMode? write)
    {
        static string Verb(PolicyMode m, string allowed, string asked, string denied) => m switch
        {
            PolicyMode.Allow => allowed,
            PolicyMode.Ask => asked,
            _ => denied,
        };
        var parts = new List<string>();
        if (read is { } r) parts.Add(Verb(r, "reads freely", "asks before reading", "can't read"));
        if (write is { } w) parts.Add(Verb(w, "changes freely", "asks before changes", "no changes"));
        var text = string.Join(" · ", parts);
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string ToolList(List<McpTool> tools, bool loaded)
    {
        if (tools.Count == 0) return loaded ? "No tools right now." : "Not loaded: its tools are hidden.";
        var reads = tools.Where(t => t.ReadOnly).Select(t => t.Name).ToList();
        var changes = tools.Where(t => !t.ReadOnly).Select(t => t.Name).ToList();
        return string.Join("\n\n", new[]
        {
            reads.Count > 0 ? "Reading:\n  " + string.Join("\n  ", reads) : null,
            changes.Count > 0 ? "Changes:\n  " + string.Join("\n  ", changes) : null,
        }.Where(s => s is not null));
    }
}
