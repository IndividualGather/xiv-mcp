using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XivMcp.Util;

namespace XivMcp.Windows;

internal sealed partial class ConfigWindow : Window
{
    /// <summary>XIV MCP's accent: the middle of the icon's teal-to-blue gradient, a little brighter so it reads as text.</summary>
    internal static readonly Vector4 Accent = new(0.33f, 0.78f, 0.95f, 1);
    internal static readonly Vector4 Green = new(0.42f, 0.84f, 0.42f, 1);
    internal static readonly Vector4 Red = new(0.88f, 0.42f, 0.42f, 1);
    internal static readonly Vector4 Amber = new(0.91f, 0.70f, 0.29f, 1);
    internal static readonly Vector4 Cyan = new(0.50f, 0.91f, 1.00f, 1);
    internal static readonly Vector4 Muted = new(0.62f, 0.64f, 0.70f, 1);

    private readonly Plugin plugin;
    private readonly string iconPath;
    private int portInput;
    private bool showToken;
    private string toolFilter = "";

    // Snapshots refreshed once per second (they involve IPC / file data).
    private DateTime nextRefresh = DateTime.MinValue;
    private PluginCompat.Info? compat;
    private List<(string Cache, string Character, string Entry, DateTime? Captured, bool Live)> cacheRows = [];

    public ConfigWindow(Plugin plugin) : base("XIV MCP###XivMcpConfig")
    {
        this.plugin = plugin;
        portInput = plugin.Config.Port;
        iconPath = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName ?? "", "images", "icon.png");
        Size = new Vector2(880, 640);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(560, 440), MaximumSize = new Vector2(1600, 1400) };
    }

    public override void Draw()
    {
        Refresh();
        DrawHeader();
        ImGui.Spacing();

        // Tabs in XIV MCP's accent rather than Dalamud's theme colour (which is close to the violet of third-party cards).
        using var tabColors = ImRaii.PushColor(ImGuiCol.Tab, Accent with { W = 0.16f })
                                    .Push(ImGuiCol.TabHovered, Accent with { W = 0.55f })
                                    .Push(ImGuiCol.TabActive, Accent with { W = 0.42f });
        using var tabs = ImRaii.TabBar("##xivmcp-tabs");
        if (!tabs) return;
        Tab(FontAwesomeIcon.Plug, "Connect", DrawConnect);
        Tab(FontAwesomeIcon.Cubes, "Modules", DrawPermissions);
        Tab(FontAwesomeIcon.UserShield, ThirdPartyTabLabel(), DrawThirdParty);
        var activeJobs = plugin.Jobs?.All().Count(j => !j.Finished) ?? 0;
        Tab(FontAwesomeIcon.Tasks, activeJobs > 0 ? $"Jobs ({activeJobs})" : "Jobs", DrawJobs);
        Tab(FontAwesomeIcon.InfoCircle, "Info", DrawInfo);
    }

    /// <summary>A tab to bring to the front on the next frame (its first label word, e.g. "Third-party").</summary>
    private string? selectTab;

    private string? currentTab;

    // Dev builds reload on every build: remember whether the window was open and on which tab, and come back the same way.
    private static string ReopenFile => System.IO.Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "dev-window.txt");

    /// <summary>Dev plugin only: after a hot reload, reopen on the tab that was showing.</summary>
    public void RestoreAfterReload()
    {
        try
        {
            if (!Svc.PluginInterface.IsDev || !File.Exists(ReopenFile)) return;
            selectTab = File.ReadAllText(ReopenFile).Trim();
            IsOpen = true;
        }
        catch { /* only a convenience */ }
    }

    /// <summary>Dev plugin only: called on unload, remembers the open tab (or that the window was closed).</summary>
    public void RememberForReload()
    {
        try
        {
            if (!Svc.PluginInterface.IsDev) return;
            if (IsOpen && currentTab is not null) File.WriteAllText(ReopenFile, currentTab);
            else if (File.Exists(ReopenFile)) File.Delete(ReopenFile);
        }
        catch { /* only a convenience */ }
    }

    private void Tab(FontAwesomeIcon icon, string label, Action draw)
    {
        var key = label.Split(' ')[0];
        var flags = selectTab == key ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        if (flags != ImGuiTabItemFlags.None) selectTab = null;
        using var tab = ImRaii.TabItem($"{icon.ToIconString()}  {label}###{key}", flags);
        if (!tab) return;
        currentTab = key;
        ImGui.Spacing();
        using var child = ImRaii.Child($"##{key}", new Vector2(-1, -1), false);
        draw();
    }

    /// <summary>Opens the window on the Third-party plugins tab with that plugin expanded (e.g. from the "new plugin" notification).</summary>
    public void ShowThirdParty(string pluginId)
    {
        IsOpen = true;
        BringToFront();
        selectTab = "Third-party";
        ThirdParty.Focus(pluginId);
    }

    // ---------------------------------------------------------------- header

    private void DrawHeader()
    {
        var server = plugin.Server;
        var config = plugin.Config;
        var iconSize = 64 * Ui.Scale;

        if (File.Exists(iconPath))
        {
            ImGui.Image(Svc.Textures.GetFromFile(iconPath).GetWrapOrEmpty().Handle, new Vector2(iconSize));
            ImGui.SameLine(0, 12 * Ui.Scale);
        }

        using (ImRaii.Group())
        {
            ImGui.TextColored(Accent, "XIV MCP");
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"v{typeof(Plugin).Assembly.GetName().Version?.ToString(3)}  ·  Model Context Protocol server");

            var (color, text) = server.IsRunning
                ? (Green, $"Running  ·  {server.Endpoint}")
                : config.ServerEnabled ? (Red, "Not running" + (server.LastError is { } e ? $": {e}" : "")) : (Muted, "Stopped");
            IconText(FontAwesomeIcon.Circle, color);
            ImGui.SameLine();
            ImGui.TextColored(color, text);

            // Clients are tracked since the server (re)started — e.g. after a plugin reload clients show up again on their next request.
            var clients = server.Clients();
            var since = server.StartedUtc is { } s ? s.ToLocalTime().ToString("HH:mm") : "-";
            if (clients.Count == 0)
            {
                ImGui.TextColored(Muted, $"No requests since the server started at {since}");
            }
            else
            {
                var recent = clients.Where(c => DateTime.UtcNow - c.LastSeenUtc < TimeSpan.FromMinutes(10)).ToList();
                var shown = recent.Count > 0 ? recent : clients.Take(1).ToList();
                var clientText = string.Join(", ", shown.Take(2).Select(c => $"{c.Name} ({Ago(c.LastSeenUtc)})")) + (shown.Count > 2 ? $" +{shown.Count - 2}" : "");
                IconText(FontAwesomeIcon.Link, recent.Count > 0 ? Cyan : Muted);
                ImGui.SameLine();
                ImGui.TextColored(recent.Count > 0 ? Cyan : Muted, clientText);
                Tooltip($"{server.RequestCount} requests since {since}\n\n" +
                        string.Join("\n", clients.Select(c => $"{c.Name}: last request {c.LastSeenUtc.ToLocalTime():HH:mm:ss}" + (c.EventStream ? ", listening for notifications" : ""))));
            }
        }

        // Server switch, right-aligned
        var toggleWidth = ImGui.CalcTextSize("Server").X + 50 * Ui.Scale;
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - toggleWidth);
        using (ImRaii.Group())
        {
            ImGui.TextColored(Muted, "Server");
            ImGui.SameLine();
            var enabled = config.ServerEnabled;
            if (ImGuiComponents.ToggleButton("##server", ref enabled))
            {
                config.ServerEnabled = enabled;
                config.Save();
                if (enabled) server.Start(); else server.Stop();
            }
        }
        ImGui.Separator();
    }

    // ---------------------------------------------------------------- permissions

    private void DrawPermissions() => Permissions.Draw();

    // ---------------------------------------------------------------- info

    /// <summary>The Info tab: recent activity, compatibility with other plugins, the tool list and the caches, each as a sub-tab.</summary>
    private void DrawInfo()
    {
        using var tabs = ImRaii.TabBar("##info-tabs");
        if (!tabs) return;
        SubTab(FontAwesomeIcon.History, "Recent activity", DrawActivity);
        SubTab(FontAwesomeIcon.Robot, "Compatibility", DrawCompatibility);
        SubTab(FontAwesomeIcon.Wrench, $"Tools ({plugin.Server.Tools.Count})", DrawTools);
        SubTab(FontAwesomeIcon.Database, "Caches", DrawCaches);
    }

    private static void SubTab(FontAwesomeIcon icon, string label, Action draw)
    {
        using var tab = ImRaii.TabItem($"{icon.ToIconString()}  {label}###info-{label.Split(' ')[0]}");
        if (!tab) return;
        ImGui.Spacing();
        using var child = ImRaii.Child($"##info-{label.Split(' ')[0]}", new Vector2(-1, -1), false);
        draw();
    }

    /// <summary>What XIV MCP's own tools changed recently, and every call that was asked or blocked.</summary>
    private void DrawActivity()
    {
        var activity = plugin.Gate.Audit.Recent(max: 500).Where(e => e.BuiltIn).Take(50).ToList();
        if (activity.Count == 0) ImGui.TextColored(Muted, "No changes or approvals yet. Reading that's allowed isn't listed.");
        else ThirdPartyPanel.DrawAuditTable(activity, showSource: true);
        ImGui.Spacing();
        ImGui.TextColored(Muted, "Full log: pluginConfigs/XivMcp/audit.jsonl. Third-party plugins' activity is on their cards in Third-party plugins.");
    }

    /// <summary>How XIV MCP works with the other plugins installed. Plugins that aren't installed aren't mentioned.</summary>
    private void DrawCompatibility()
    {
        if (compat is not { } c || !(c.AutoRetainer || c.YesAlready || c.TextAdvance || c.Fcch || c.WaymarkPresetPlugin || c.Vnavmesh || c.Lifestream || c.Artisan || c.GatherBuddy || c.ItemVendorLocation))
        {
            ImGui.TextColored(Muted, "None of the plugins XIV MCP works with are installed.");
            return;
        }
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "How XIV MCP works with the other plugins you have installed.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        if (c.AutoRetainer)
            CompatRow("AutoRetainer",
                c.SuppressedByUs ? "paused while XIV MCP uses the summoning bell"
                : c.AutoRetainerBusy == true ? "busy, XIV MCP waits for it"
                : "paused automatically when XIV MCP uses the summoning bell");
        if (c.YesAlready)
            CompatRow("YesAlready", c.ClickersPausedByUs ? "paused by XIV MCP right now" : "paused automatically while XIV MCP uses retainer windows");
        if (c.TextAdvance)
            CompatRow("TextAdvance", c.ClickersPausedByUs ? "paused by XIV MCP right now" : "paused automatically while XIV MCP uses retainer windows");
        if (c.Fcch)
            CompatRow("FCCH", c.FcchBusy == true ? "busy, XIV MCP waits with item moves" : "used for free company chest transfers; item moves wait while it works");
        if (c.WaymarkPresetPlugin)
            CompatRow("WaymarkPresetPlugin", "its preset library is available, library presets are placed through it");
        if (c.Vnavmesh)
            CompatRow("vnavmesh", "used by Navigation to walk to objects");
        if (c.Lifestream)
            CompatRow("Lifestream", "used by Navigation for teleports, houses, inns and the workshop (its property priority is respected)");
        if (c.Artisan)
            CompatRow("Artisan", "crafts and crafting lists can be started and lists edited");
        if (c.GatherBuddy)
            CompatRow("GatherBuddy Reborn", "auto-gather can be started and its lists edited");
        if (c.ItemVendorLocation)
            CompatRow("Item Vendor Location", "used by Market & purchases to find vendors and where they stand");
    }

    private static readonly (string Id, string Label)[] BellLocations =
    [
        ("lifestream", "Lifestream's preferred property"), ("inn", "Inn room"), ("fc", "Free company house"), ("home", "Private house"), ("apartment", "Apartment"),
    ];

    /// <summary>Where navigate_to goes for a summoning bell when none is nearby.</summary>
    private void DrawBellPreference()
    {
        var config = plugin.Config;
        ImGui.Spacing();
        IconText(FontAwesomeIcon.Bell, Muted);
        ImGui.SameLine();
        ImGui.TextUnformatted("Summoning bell location");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(230 * Ui.Scale, ImGui.GetContentRegionAvail().X));
        var current = BellLocations.FirstOrDefault(b => b.Id == config.PreferredBellLocation).Label ?? BellLocations[0].Label;
        using var combo = ImRaii.Combo("##bell-location", current);
        if (!combo) return;
        foreach (var (id, label) in BellLocations)
            if (ImGui.Selectable(label, id == config.PreferredBellLocation))
            {
                config.PreferredBellLocation = id;
                config.Save();
            }
    }

    /// <summary>Purchases find vendors through Item Vendor Location; offers to install it when it is missing.</summary>
    private static void DrawVendorPluginMissing()
    {
        IconText(FontAwesomeIcon.InfoCircle, Cyan);
        ImGui.SameLine();
        ImGui.TextColored(Cyan, "Requires the Item Vendor Location plugin (finds the vendors).");
        ImGui.SameLine();
        if (ImGui.SmallButton("Install Item Vendor Location"))
            Svc.PluginInterface.OpenPluginInstallerTo(Dalamud.Interface.PluginInstallerOpenKind.AllPlugins, "Item Vendor Location");
        Tooltip("Opens the Dalamud plugin installer filtered to Item Vendor Location; install it from there.");
    }

    /// <summary>Optional gil limit above which a gil purchase asks too.</summary>
    private void DrawGilLimit()
    {
        var config = plugin.Config;
        ImGui.Spacing();
        IconText(FontAwesomeIcon.Coins, Muted);
        ImGui.SameLine();
        ImGui.TextUnformatted("Ask for gil purchases above");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Min(140 * Ui.Scale, ImGui.GetContentRegionAvail().X));
        var limit = config.AskAboveGil;
        if (ImGui.InputInt("gil##ask-gil", ref limit, 1000, 10000))
        {
            config.AskAboveGil = Math.Clamp(limit, 0, 999_999_999);
            config.Save();
        }
        ImGui.TextColored(Muted, config.AskAboveGil == 0 ? "0: gil purchases never ask." : $"Gil purchases above {config.AskAboveGil:N0} gil ask first.");
    }

    /// <summary>Pause between item moves: random within a range (default 500-800 ms) or an exact value.</summary>
    private void DrawMoveDelay()
    {
        var config = plugin.Config;
        ImGui.Spacing();
        IconText(FontAwesomeIcon.Hourglass, Muted);
        ImGui.SameLine();
        ImGui.TextUnformatted("Pause between moves");
        ImGui.SameLine();
        var random = config.MoveDelayRandom;
        if (ImGuiComponents.ToggleButton("##delay-random", ref random))
        {
            config.MoveDelayRandom = random;
            config.Save();
        }
        ImGui.SameLine();
        ImGui.TextColored(Muted, random ? "random" : "fixed");
        Tooltip("Random: every pause is picked anew between the two values, so moves don't follow a fixed rhythm.\nFixed: always exactly the same pause.");

        ImGui.SetNextItemWidth(Math.Min(260 * Ui.Scale, ImGui.GetContentRegionAvail().X));
        if (random)
        {
            int min = config.MoveDelayMinMs, max = config.MoveDelayMaxMs;
            if (ImGui.DragIntRange2("ms##delay-range", ref min, ref max, 5f, Configuration.MoveDelayLimitMin, Configuration.MoveDelayLimitMax, "min %d", "max %d"))
            {
                config.MoveDelayMinMs = Math.Clamp(Math.Min(min, max), Configuration.MoveDelayLimitMin, Configuration.MoveDelayLimitMax);
                config.MoveDelayMaxMs = Math.Clamp(Math.Max(min, max), Configuration.MoveDelayLimitMin, Configuration.MoveDelayLimitMax);
                config.Save();
            }
        }
        else
        {
            var exact = config.MoveDelayMs;
            if (ImGui.InputInt("ms##delay-exact", ref exact, 10, 100))
            {
                config.MoveDelayMs = Math.Clamp(exact, Configuration.MoveDelayLimitMin, Configuration.MoveDelayLimitMax);
                config.Save();
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Default")) { config.MoveDelayRandom = true; config.MoveDelayMinMs = 500; config.MoveDelayMaxMs = 800; config.Save(); }
        Tooltip("Random between 500 and 800 ms");
        ImGui.TextColored(Muted, $"Drag or Ctrl+click to type exact values ({Configuration.MoveDelayLimitMin}-{Configuration.MoveDelayLimitMax} ms).");
    }

    private static void CompatRow(string name, string state)
    {
        IconText(FontAwesomeIcon.Check, Green);
        ImGui.SameLine();
        ImGui.TextUnformatted(name);
        ImGui.SameLine(150 * Ui.Scale);
        ImGui.TextColored(Muted, state);
    }

    // ---------------------------------------------------------------- caches

    // ---------------------------------------------------------------- jobs

    private void DrawJobs()
    {
        var manager = plugin.Jobs;
        if (manager is null) return;
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "Background jobs started by your assistant: queues of tool calls that run here in game for as long as they take. " +
                                 "A job whose step failed or was stopped waits as \"pending\" until the assistant fixes it, or you cancel it.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        var all = manager.All();
        var active = all.Where(j => !j.Finished).ToList();
        if (active.Count == 0) ImGui.TextColored(Muted, "No active jobs.");
        foreach (var job in active) DrawJob(manager, job);
        var approvals = XivMcp.Util.Approvals.All().Where(a => a.Active).ToList();
        if (approvals.Count > 0)
        {
            ImGui.Spacing();
            Section(FontAwesomeIcon.CheckCircle, "Standing spending approvals");
            foreach (var a in approvals)
            {
                using var aid = ImRaii.PushId(a.Id);
                ImGui.TextColored(Accent, $"{a.Remaining:N0} / {a.MaxAmount:N0} {XivMcp.Util.Items.Name(a.CurrencyId)}");
                ImGui.SameLine();
                ImGui.TextColored(Muted, $"{a.Purpose} — until {a.ExpiresUtc.ToLocalTime():g}");
                ImGui.SameLine();
                if (ImGui.SmallButton("Revoke")) XivMcp.Util.Approvals.Revoke(a.Id);
            }
            ImGui.Spacing();
        }
        var finished = all.Where(j => j.Finished).ToList();
        if (finished.Count > 0 && ImGui.CollapsingHeader($"Finished ({finished.Count})##finished-jobs"))
            foreach (var job in finished) DrawJob(manager, job);
    }

    private static readonly Dictionary<XivMcp.Util.JobManager.JobState, Vector4> JobColors = new()
    {
        [XivMcp.Util.JobManager.JobState.Running] = Green, [XivMcp.Util.JobManager.JobState.Queued] = Cyan, [XivMcp.Util.JobManager.JobState.Paused] = Amber,
        [XivMcp.Util.JobManager.JobState.Pending] = Amber, [XivMcp.Util.JobManager.JobState.Completed] = Muted, [XivMcp.Util.JobManager.JobState.Failed] = Red,
        [XivMcp.Util.JobManager.JobState.Cancelled] = Muted,
    };

    private void DrawJob(XivMcp.Util.JobManager manager, XivMcp.Util.JobManager.Job job)
    {
        using var id = ImRaii.PushId(job.Id);
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(1, 1, 1, 0.04f));
        var done = job.Steps.Count(s => s.State is XivMcp.Util.JobManager.StepState.Done or XivMcp.Util.JobManager.StepState.Skipped);
        ImGui.TextColored(JobColors[job.State], job.State.ToString().ToLowerInvariant());
        ImGui.SameLine();
        ImGui.TextColored(Accent, job.Name);
        ImGui.SameLine();
        ImGui.TextColored(Muted, $"{done}/{job.Steps.Count} steps");
        if (!job.Finished)
        {
            ImGui.SameLine();
            try
            {
                if (job.State is XivMcp.Util.JobManager.JobState.Running or XivMcp.Util.JobManager.JobState.Queued)
                {
                    if (ImGui.SmallButton("Pause")) manager.Pause(job.Id, "Paused in game.");
                }
                else if (job.Current?.State != XivMcp.Util.JobManager.StepState.Failed && ImGui.SmallButton("Resume")) manager.Resume(job.Id);
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel")) manager.Cancel(job.Id);
            }
            catch (XivMcp.Mcp.ToolException ex) { Svc.Log.Warning($"[MCP] {ex.Message}"); }
        }
        if (job.Current is { } cur)
        {
            var since = cur.StartedUtc is { } s && cur.State == XivMcp.Util.JobManager.StepState.Running ? $" for {CacheFreshness.FormatAge(DateTime.UtcNow - s)}" : "";
            ImGui.TextColored(Muted, $"Step {cur.Id}: {cur.Tool} ({cur.State.ToString().ToLowerInvariant()}{since})");
        }
        if (job.Reason is { } reason)
        {
            ImGui.PushTextWrapPos();
            ImGui.TextColored(job.State == XivMcp.Util.JobManager.JobState.Pending ? Amber : Muted, reason);
            ImGui.PopTextWrapPos();
        }
        if (ImGui.TreeNode($"Steps and log##{job.Id}"))
        {
            foreach (var step in job.Steps)
            {
                ImGui.TextUnformatted($"{step.Id}  {step.Tool}  —  {step.State.ToString().ToLowerInvariant()}{(step.Attempts > 1 ? $" (attempt {step.Attempts})" : "")}");
                if (step.Error is { } e) { ImGui.PushTextWrapPos(); ImGui.TextColored(Red, e); ImGui.PopTextWrapPos(); }
            }
            ImGui.Separator();
            foreach (var line in job.Log.TakeLast(12)) ImGui.TextColored(Muted, line);
            ImGui.TreePop();
        }
        ImGui.Separator();
    }

    private void DrawCaches()
    {
        var config = plugin.Config;
        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "Some data is only sent by the game in certain places. XIV MCP remembers it so assistants can use it anywhere, " +
                                 "and tells them how old it is and how to refresh it.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();

        var hours = config.CacheStaleHours;
        ImGui.SetNextItemWidth(220 * Ui.Scale);
        if (ImGui.SliderInt("Suggest refresh after (hours)", ref hours, 1, 168))
        {
            config.CacheStaleHours = hours;
            config.Save();
        }
        ImGui.Spacing();

        if (cacheRows.Count == 0)
        {
            ImGui.TextColored(Muted, "Nothing cached yet. Open the voyage control panel in your FC workshop, or a retainer at a summoning bell.");
            return;
        }

        using var table = ImRaii.Table("##caches", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;
        ImGui.TableSetupColumn("Data", ImGuiTableColumnFlags.WidthStretch, 2.2f);
        ImGui.TableSetupColumn("Character", ImGuiTableColumnFlags.WidthStretch, 1.6f);
        ImGui.TableSetupColumn("Age", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 0.9f);
        ImGui.TableHeadersRow();
        foreach (var row in cacheRows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            IconText(row.Cache switch { "submersibles" => FontAwesomeIcon.Ship, "retainers" => FontAwesomeIcon.Bell, "glamour" => FontAwesomeIcon.Tshirt, "progress" => FontAwesomeIcon.Trophy, _ => FontAwesomeIcon.Archive }, Muted);
            ImGui.SameLine();
            ImGui.TextUnformatted(row.Entry);
            ImGui.TableNextColumn();
            ImGui.TextColored(Muted, row.Character);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Live ? "—" : row.Captured is { } t ? CacheFreshness.FormatAge(DateTime.UtcNow - t) : "—");
            ImGui.TableNextColumn();
            var stale = row.Captured is not { } c || DateTime.UtcNow - c > CacheFreshness.StaleAfter;
            if (row.Live) ImGui.TextColored(Cyan, "live from game");
            else ImGui.TextColored(row.Captured is null ? Muted : stale ? Amber : Green, row.Captured is null ? "not captured" : stale ? "stale" : "snapshot");
        }
    }

    // ---------------------------------------------------------------- tools

    private void DrawTools()
    {
        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##filter", "Filter tools…", ref toolFilter, 64);
        ImGui.Spacing();

        using var table = ImRaii.Table("##tools", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp);
        if (!table) return;
        ImGui.TableSetupColumn("Tool", ImGuiTableColumnFlags.WidthStretch, 1.4f);
        ImGui.TableSetupColumn("Access", ImGuiTableColumnFlags.WidthStretch, 0.8f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 1.0f);
        ImGui.TableSetupColumn("What it does", ImGuiTableColumnFlags.WidthStretch, 3.2f);
        ImGui.TableHeadersRow();

        foreach (var tool in plugin.Server.Tools.OrderBy(t => t.ReadOnly ? 0 : 1).ThenBy(t => t.Name))
        {
            if (toolFilter.Length > 0 && !tool.Name.Contains(toolFilter, StringComparison.OrdinalIgnoreCase) &&
                !tool.Description.Contains(toolFilter, StringComparison.OrdinalIgnoreCase)) continue;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            using (Ui.MonoFont()) ImGui.TextUnformatted(tool.Name);
            ImGui.TableNextColumn();
            if (tool.ReadOnly) ImGui.TextColored(Green, "read");
            else ImGui.TextColored(tool.Destructive ? Red : Amber, tool.Destructive ? "writes files" : "acts in game");
            ImGui.TableNextColumn();
            ImGui.TextColored(tool.Provider.Trust == XivMcp.Mcp.ProviderTrust.ThirdParty ? Amber : Muted, SourceOf(tool));
            ImGui.TableNextColumn();
            var summary = XivMcp.Mcp.ToolSummaries.For(tool.Name) ?? XivMcp.Mcp.ToolText.FirstSentence(tool.Description);
            ImGui.PushTextWrapPos();
            ImGui.TextColored(Muted, summary);
            ImGui.PopTextWrapPos();
            DescriptionTooltip(tool.Description);
        }
    }

    // ---------------------------------------------------------------- helpers

    private void Refresh()
    {
        if (DateTime.UtcNow < nextRefresh) return;
        nextRefresh = DateTime.UtcNow.AddSeconds(1);
        try { compat = plugin.Compat.GetInfo(); } catch { compat = null; }
        cacheRows = plugin.Caches.All
            .SelectMany(c => c.Entries().Select(e => (c.Id, e.Character, e.Entry ?? c.Title, e.CapturedUtc, e.Live)))
            .OrderBy(r => r.Character).ThenBy(r => r.Item3)
            .ToList();
    }

    private static string Ago(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalSeconds < 60 ? "just now" : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago" : $"{(int)age.TotalHours} h ago";
    }

    internal static void Section(FontAwesomeIcon icon, string title)
    {
        IconText(icon, Accent);
        ImGui.SameLine();
        ImGui.TextColored(Accent, title);
    }

    internal static void IconText(FontAwesomeIcon icon, Vector4 color)
    {
        using var font = Ui.IconFont();
        ImGui.TextColored(color, icon.ToIconString());
    }

    /// <summary>
    /// A tool description as a formatted tooltip: the summary first, then the details, bullets, and requirements set apart in muted
    /// text (see <see cref="XivMcp.Mcp.ToolText.Paragraphs"/>). <paramref name="footer"/> is added as a last muted paragraph.
    /// </summary>
    internal static void DescriptionTooltip(string description, string? footer = null, string? intro = null)
    {
        if (!ImGui.IsItemHovered()) return;
        using var tt = ImRaii.Tooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
        var previous = (XivMcp.Mcp.ToolText.BlockKind?)null;
        if (intro is not null)
        {
            ImGui.TextColored(Muted, intro);
            ImGui.Dummy(new Vector2(0, ImGui.GetFontSize() * 0.2f));
        }
        foreach (var block in XivMcp.Mcp.ToolText.Paragraphs(description))
        {
            // A gap between paragraphs; bullets that follow each other stay together.
            if (previous is not null && !(previous == XivMcp.Mcp.ToolText.BlockKind.Bullet && block.Kind == XivMcp.Mcp.ToolText.BlockKind.Bullet))
                ImGui.Dummy(new Vector2(0, ImGui.GetFontSize() * 0.35f));
            switch (block.Kind)
            {
                case XivMcp.Mcp.ToolText.BlockKind.Summary:
                    ImGui.TextColored(Accent, block.Text);
                    break;
                case XivMcp.Mcp.ToolText.BlockKind.Bullet:
                    ImGui.Bullet();
                    ImGui.SameLine();
                    ImGui.TextUnformatted(block.Text);
                    break;
                case XivMcp.Mcp.ToolText.BlockKind.Requirement:
                    ImGui.TextColored(Muted, block.Text);
                    break;
                default:
                    ImGui.TextUnformatted(block.Text);
                    break;
            }
            previous = block.Kind;
        }
        if (footer is not null)
        {
            ImGui.Dummy(new Vector2(0, ImGui.GetFontSize() * 0.35f));
            ImGui.TextColored(Muted, footer);
        }
        ImGui.PopTextWrapPos();
    }

    internal static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered()) return;
        using var tt = ImRaii.Tooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

}
