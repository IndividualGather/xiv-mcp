using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;
using XivMcp.Voyages;

namespace XivMcp.Tools;

/// <summary>
/// Repairing and deploying the free company's submersibles at the voyage control panel in the FC workshop, through the panel's own
/// menus and windows: Submersible Management → the vessel → Repair (CompanyCraftSupply), View previous voyage log → Redeploy, or
/// Deploy on a subaquatic voyage → the sea (SubmarineExplorationMapSelect) → the points (AirShipExploration) → the voyage details
/// (AirShipExplorationDetail) → Deploy. The window steps follow what AutoRetainer's voyage automation does with the same windows.
/// </summary>
internal static class SubmarineTools
{
    private const uint RepairMaterials = 10373, Ceruleum = 10155;

    // The panel's menu entries. They come from the event script, not a text sheet, so they are listed per client language
    // (English, Japanese, Chinese, German, French, Korean).
    private static readonly string[] SubmersibleManagement = ["Submersible Management", "潜水艦の管理", "管理潜水艇", "管理潛水艇", "Tauchboot verwalten", "Contrôle sous-marin", "잠수함 관리"];
    private static readonly string[] Repair = ["Repair submersible components", "パーツの修理", "Bauteile reparieren", "Réparer des éléments", "修理配件", "부품 수리"];
    private static readonly string[] PreviousLog = ["View previous voyage log", "前回のボイジャー報告", "上次的远航报告", "上次的遠航報告", "Bericht der letzten Erkundung", "Consulter le journal de la précédente expédition", "이전 탐사 보고서"];
    private static readonly string[] DeployNew = ["Deploy submersible on subaquatic voyage", "ボイジャー出港", "出发", "出發", "Auf Erkundung gehen", "Expédier le sous-marin", "탐사 출항"];
    // Only the English text is known for sure; elsewhere the entry is found by its place (first, while the vessel is out).
    private static readonly string[] Recall = ["Recall submersible"];
    private static readonly string[] QuitVessel = ["Quit", "やめる", "取消", "Beenden", "Annuler", "그만두기"];
    private static readonly string[] Nothing = ["Nothing.", "やめる", "取消", "Nichts", "Annuler", "그만두기"];
    private static readonly string[] Cancel = ["Cancel", "キャンセル", "取消", "Abbrechen", "Annuler", "취소"];

    private static readonly string[] Parts = ["hull", "stern", "bow", "bridge"];

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        yield return new McpTool
        {
            Name = "recall_submersible",
            Description = "Recalls a submersible from its voyage at the voyage control panel in the FC workshop, as the panel's \"Recall " +
                          "submersible\" does: the voyage is cancelled and the submersible is back at once, with no loot; the Ceruleum " +
                          "Tanks used for it are not refunded. Then send it out again with deploy_submersible (a new route, or it repeats " +
                          "the cancelled one). The character must stand at the voyage control panel (navigate_to destination workshop). " +
                          "Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "submersible": { "type": "string", "description": "The submersible's name." }
                  },
                  "required": ["submersible"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled(config);
                var name = args.String("submersible") ?? throw new ToolException("'submersible' is required.");
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await LoadVessels(ct).ConfigureAwait(false);
                    var vessel = await Game.Run(() => FindVessel(name)).ConfigureAwait(false);
                    if (!vessel.OnVoyage) throw new ToolException($"{vessel.Name} is not out on a voyage.");
                    var log = new List<string>();
                    await RecallVessel(vessel, log, ct).ConfigureAwait(false);
                    return new { recalled = vessel.Name, log, next = "deploy_submersible with a route to send it out again" };
                }
                finally
                {
                    await Leave(ct).ConfigureAwait(false);
                    InventoryActionTools.Gate.Release();
                }
            },
        };

        yield return new McpTool
        {
            Name = "repair_submersible",
            Description = "Repairs a submersible's parts at the voyage control panel in the FC workshop, with Magitek Repair Materials from " +
                          "the bags: every part below 'below_percent' (default 100: every worn part). get_submersibles shows each part's " +
                          "condition. The character must stand at the voyage control panel (navigate_to destination workshop). A " +
                          "submersible that is out on a voyage cannot be repaired. Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "submersible": { "type": "string", "description": "The submersible's name." },
                    "below_percent": { "type": "integer", "minimum": 1, "maximum": 100, "description": "Repair parts below this condition (default 100)." }
                  },
                  "required": ["submersible"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled(config);
                var name = args.String("submersible") ?? throw new ToolException("'submersible' is required.");
                var below = args.Int("below_percent", 100, 1, 100);
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await LoadVessels(ct).ConfigureAwait(false);
                    var vessel = await Game.Run(() => FindVessel(name)).ConfigureAwait(false);
                    if (vessel.OnVoyage) throw new ToolException($"{vessel.Name} is out on a voyage until {vessel.Returns:HH:mm}; repair it when it is back.");
                    var log = new List<string>();
                    await OpenVessel(vessel, log, ct).ConfigureAwait(false);
                    var repaired = await RepairParts(vessel, below, log, ct).ConfigureAwait(false);
                    await Leave(ct).ConfigureAwait(false);
                    return new { submersible = vessel.Name, repaired, condition = Condition(vessel), log };
                }
                finally { InventoryActionTools.Gate.Release(); }
            },
        };

        yield return new McpTool
        {
            Name = "collect_submersible",
            Description = "Finishes the voyages of submersibles that are back, at the voyage control panel in the FC workshop, without " +
                          "sending them out again: the loot is collected, and only then does the game raise their rank and unlock the " +
                          "sectors they found. Returns the rank-ups, the sectors newly unlocked and explored, and per submersible its rank, " +
                          "range and the sectors it can visit now (unexplored ones first: exploring them unlocks more). Choose the next " +
                          "route from that, then deploy_submersible. The character must stand at the voyage control panel (navigate_to " +
                          "destination workshop). Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "submersible": { "type": "string", "description": "The submersible's name (default: every one that is back)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled(config);
                var name = args.String("submersible");
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await LoadVessels(ct).ConfigureAwait(false);
                    var vessels = await Game.Run(() => name is null ? Returned() : [FindVessel(name)]).ConfigureAwait(false);
                    if (vessels.Count == 0) throw new ToolException("No submersible is back from its voyage.");
                    var before = await Game.Run(Fleet).ConfigureAwait(false);
                    var log = new List<string>();
                    foreach (var vessel in vessels)
                    {
                        if (vessel.OnVoyage) throw new ToolException($"{vessel.Name} is still out on a voyage until {vessel.Returns:HH:mm}.");
                        await OpenVessel(vessel, log, ct).ConfigureAwait(false);
                        await Leave(ct).ConfigureAwait(false);
                        await Task.Delay(800, ct).ConfigureAwait(false);
                    }
                    return await Game.Run(() => new
                    {
                        collected = vessels.Select(v => v.Name),
                        progress = DescribeProgress(VoyageProgress.Between(before, Fleet())),
                        submersibles = vessels.Select(v => Options(FindVessel(v.Name))),
                        log,
                        next = "deploy_submersible with a route (or route \"previous\")",
                    }).ConfigureAwait(false);
                }
                catch
                {
                    await Leave(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                finally { InventoryActionTools.Gate.Release(); }
            },
        };

        yield return new McpTool
        {
            Name = "deploy_submersible",
            Description = "Sends a submersible on a voyage from the voyage control panel in the FC workshop. route: \"previous\" (the default: " +
                          "the route of its last voyage) or the points to visit, in order, by name, map letter (every sea uses A, B, …: letters " +
                          "are read on the sea of its last voyage, so name a point to change seas) or id (up to 5, one sea, " +
                          "unlocked and within its rank; get_submersibles lists the sectors). A submersible that is back is first " +
                          "finalized (its loot is collected), and parts below 'repair_below' percent are repaired first (default 20; 0: " +
                          "never). Without 'submersible', every submersible that is back is redeployed on its previous route. Uses " +
                          "Ceruleum Tanks as fuel. The character must stand at the voyage control panel (navigate_to destination " +
                          "workshop). The route is checked after the voyage is finished, against the rank and sectors it brought; the result " +
                          "lists rank-ups and new sectors. To choose a new route with those in mind, run collect_submersible first. " +
                          "Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "submersible": { "type": "string", "description": "The submersible's name (default: every one that is back)." },
                    "route": {
                      "description": "\"previous\", or the points of a new route.",
                      "oneOf": [ { "type": "string", "enum": ["previous"] }, { "type": "array", "items": { "type": "string" }, "minItems": 1, "maxItems": 5 } ]
                    },
                    "repair_below": { "type": "integer", "minimum": 0, "maximum": 100, "description": "Repair parts below this condition first (default 20, 0: never)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled(config);
                var repairBelow = args.Int("repair_below", 20, 0, 100);
                var points = (args.Node("route") as JsonArray)?.Select(n => n?.ToString() ?? "").ToList();
                var name = args.String("submersible");
                if (points is not null && name is null) throw new ToolException("A new route needs 'submersible': name the one to send.");

                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await LoadVessels(ct).ConfigureAwait(false);
                    var vessels = await Game.Run(() => name is null ? Returned() : [FindVessel(name)]).ConfigureAwait(false);
                    if (vessels.Count == 0) throw new ToolException("No submersible is back from its voyage.");
                    var before = await Game.Run(Fleet).ConfigureAwait(false);
                    var results = new List<object>();
                    foreach (var vessel in vessels)
                    {
                        if (vessel.OnVoyage) throw new ToolException($"{vessel.Name} is still out on a voyage until {vessel.Returns:HH:mm}.");
                        var log = new List<string>();
                        await OpenVessel(vessel, log, ct).ConfigureAwait(false);
                        // Finishing the voyage can raise its rank and unlock sectors: check the route against what it has now.
                        var route = points is null ? null : await Game.Run(() => ResolveRoute(points, FindVessel(vessel.Name))).ConfigureAwait(false);
                        var repaired = repairBelow > 0 ? await RepairParts(vessel, repairBelow, log, ct).ConfigureAwait(false) : [];
                        if (route is null) await DeployPrevious(vessel, log, ct).ConfigureAwait(false);
                        else await DeployRoute(route, log, ct).ConfigureAwait(false);
                        var after = await Game.Run(() => FindVessel(vessel.Name)).ConfigureAwait(false);
                        results.Add(new
                        {
                            submersible = vessel.Name,
                            route = route is null ? "previous" : string.Join(" → ", route.Select(p => p.Name)),
                            repaired,
                            returns = after.OnVoyage ? after.Returns.ToString("yyyy-MM-dd HH:mm") : null,
                            log,
                        });
                    }
                    await Leave(ct).ConfigureAwait(false);
                    var progress = await Game.Run(() => DescribeProgress(VoyageProgress.Between(before, Fleet()))).ConfigureAwait(false);
                    return new { deployed = results.Count, submersibles = results, progress, ceruleumLeft = await Game.Run(() => Items.CountInBags(Ceruleum)).ConfigureAwait(false) };
                }
                catch
                {
                    await Leave(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                finally { InventoryActionTools.Gate.Release(); }
            },
        };
    }

    private static void RequireEnabled(Configuration config)
    {
        if (!config.AllowItemsRetainers)
            throw new ToolException("Submersible tools are disabled. Enable \"Items & retainers\" in the XIV MCP settings window (/xivmcp) in game.");
    }

    // ------------------------------------------------------------------ the vessels

    /// <summary>A submersible as the workshop holds it right now.</summary>
    private sealed record Vessel(int Index, string Name, int Rank, DateTime Returns, uint[] Points)
    {
        public bool OnVoyage => Returns > DateTime.Now;
    }

    private static unsafe List<Vessel> Vessels()
    {
        var ws = HousingManager.Instance()->WorkshopTerritory;
        if (ws == null) throw new ToolException("You are not in the free company workshop. Go there first (navigate_to destination workshop).");
        var list = new List<Vessel>();
        var i = -1;
        foreach (ref var s in ws->Submersible.Data)
        {
            i++;
            if (s.RankId == 0 || string.IsNullOrEmpty(s.NameString)) continue;
            var returns = s.ReturnTime == 0 ? DateTime.MinValue : DateTimeOffset.FromUnixTimeSeconds(s.ReturnTime).LocalDateTime;
            list.Add(new Vessel(i, s.NameString, s.RankId, returns, s.CurrentExplorationPoints.ToArray().Where(p => p != 0).Select(p => (uint)p).ToArray()));
        }
        return list;
    }

    /// <summary>
    /// Windows an earlier, interrupted attempt can leave open in front of the panel: a yes/no (answered No), the voyage details
    /// (Cancel) and the voyage map (closed). Clears them so the panel's menu can open.
    /// </summary>
    private static async Task ClearLeftovers(CancellationToken ct)
    {
        for (var i = 0; i < 6; i++)
        {
            var cleared = await Game.Run(() =>
            {
                if (GameWindows.Ready("SelectYesno")) return Fire("SelectYesno", 1);
                if (GameWindows.Ready("AirShipExplorationDetail")) return Fire("AirShipExplorationDetail", -1);
                if (GameWindows.Ready("AirShipExploration")) return Fire("AirShipExploration", -1);
                if (GameWindows.Ready("SubmarineExplorationMapSelect")) return Fire("SubmarineExplorationMapSelect", -1);
                return false;
            }).ConfigureAwait(false);
            if (!cleared) return;
            await Task.Delay(600, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The game fills the workshop's submersible list only once the voyage control panel was opened after logging in: open its menu
    /// when the list is still empty, and wait for it (the menu stays open for the next step).
    /// </summary>
    private static async Task LoadVessels(CancellationToken ct)
    {
        await ClearLeftovers(ct).ConfigureAwait(false);
        if (await Game.Run(() => Vessels().Count > 0).ConfigureAwait(false)) return;
        if (!await Game.Run(() => MenuHas(SubmersibleManagement)).ConfigureAwait(false))
        {
            await Game.Run(InteractWithPanel).ConfigureAwait(false);
            if (!await GameWindows.WaitFor(() => MenuHas(SubmersibleManagement), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
                throw new ToolException("The voyage control panel's menu did not open.");
        }
        // Choosing "Submersible management" makes the game load the list.
        await Game.Run(() => Pick(SubmersibleManagement)).ConfigureAwait(false);
        var loaded = await GameWindows.WaitFor(() => Vessels().Count > 0, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        await Leave(ct).ConfigureAwait(false);
        if (!loaded) throw new ToolException("The voyage control panel shows no submersibles: the free company may have none registered yet.");
        await Task.Delay(800, ct).ConfigureAwait(false);
    }

    private static Vessel FindVessel(string name)
    {
        var all = Vessels();
        return all.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? (all.Where(v => v.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList() is { Count: 1 } one ? one[0] : null)
               ?? throw new ToolException($"No submersible named '{name}'. The free company has: {string.Join(", ", all.Select(v => v.Name))}.");
    }

    /// <summary>The submersibles that are back and have been out (a new one without a previous route is left alone).</summary>
    private static List<Vessel> Returned() => Vessels().Where(v => !v.OnVoyage && v.Returns != DateTime.MinValue).ToList();

    private static int[] Condition(Vessel v) => WorkshopTracker.PartConditions(v.Index);

    /// <summary>The sectors submersibles can explore (the SubmarineExploration sheet), with whether the free company has unlocked each.</summary>
    private static List<SubmarinePoint> AllPoints() => Svc.Data.GetExcelSheet<SubmarineExploration>()
        .Where(r => r.RowId is > 0 and <= byte.MaxValue && !r.StartingPoint && r.Map.RowId != 0)
        .Select(r => new SubmarinePoint(r.RowId, r.Map.RowId, r.Destination.ExtractText().Trim(), r.Location.ExtractText().Trim(), r.RankReq,
                                        HousingManager.IsSubmarineExplorationUnlocked((byte)r.RowId)))
        .ToList();

    private static string? SeaName(uint map) =>
        Svc.Data.GetExcelSheet<SubmarineExploration>().FirstOrDefault(r => r.Map.RowId == map) is { RowId: > 0 } row ? Excel.Name(row.Map) : null;

    /// <summary>Every submersible's rank, and the sectors unlocked and explored. Workshop, framework thread.</summary>
    private static FleetState Fleet()
    {
        var points = AllPoints();
        return new FleetState(Vessels().ToDictionary(v => v.Name, v => v.Rank),
                              points.Where(p => p.Unlocked).Select(p => p.Id).ToHashSet(),
                              points.Where(p => HousingManager.IsSubmarineExplorationExplored((byte)p.Id)).Select(p => p.Id).ToHashSet());
    }

    private static object DescribeProgress(VoyageProgress progress)
    {
        var points = AllPoints().ToDictionary(p => p.Id);
        object Point(uint id) => points.TryGetValue(id, out var p)
            ? new { id, name = p.Name, letter = p.Code, sea = SeaName(p.Map), rankRequired = p.RankRequired }
            : new { id, name = id.ToString(), letter = "", sea = (string?)null, rankRequired = 0 };
        return new
        {
            rankUps = progress.RankUps.Select(r => new { submersible = r.Vessel, from = r.From, to = r.To }),
            newlyUnlocked = progress.NewlyUnlocked.Select(Point),
            newlyExplored = progress.NewlyExplored.Select(Point),
            summary = progress.Any ? null : "No rank-ups and no new sectors.",
        };
    }

    /// <summary>What a submersible can do next: its rank and the unlocked sectors within it, unexplored ones first, by sea.</summary>
    private static object Options(Vessel vessel)
    {
        var open = AllPoints().Where(p => p.Unlocked && p.RankRequired <= vessel.Rank)
                              .Select(p => (Point: p, Explored: HousingManager.IsSubmarineExplorationExplored((byte)p.Id)))
                              .OrderBy(x => x.Explored).ThenBy(x => x.Point.Map).ThenBy(x => x.Point.Code);
        return new
        {
            submersible = vessel.Name,
            rank = vessel.Rank,
            lastRoute = string.Join("", AllPoints().Where(p => vessel.Points.Contains(p.Id)).OrderBy(p => Array.IndexOf(vessel.Points, p.Id)).Select(p => p.Code)),
            sectors = open.Select(x => new { id = x.Point.Id, name = x.Point.Name, letter = x.Point.Code, sea = SeaName(x.Point.Map),
                                             rankRequired = x.Point.RankRequired, explored = x.Explored }),
        };
    }

    private static IReadOnlyList<SubmarinePoint> ResolveRoute(IReadOnlyList<string> points, Vessel vessel)
    {
        var all = AllPoints();
        // Letters are read on the sea of the submersible's last voyage.
        uint? sea = vessel.Points.Length > 0 ? all.FirstOrDefault(p => p.Id == vessel.Points[0])?.Map : null;
        try { return SubmarineRoutes.Resolve(points, all, vessel.Rank, sea); }
        catch (FormatException ex) { throw new ToolException(ex.Message); }
    }

    // ------------------------------------------------------------------ the panel's menus

    /// <summary>Picks the entry of the open menu that starts with one of <paramref name="texts"/> (or contains <paramref name="name"/>).</summary>
    private static bool Pick(string[]? texts, string? name = null)
    {
        var entries = RetainerUi.MenuEntries();
        if (entries is null) return false;
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (texts is not null && texts.Any(t => e.StartsWith(t, StringComparison.OrdinalIgnoreCase)) || name is not null && e.Contains(name, StringComparison.Ordinal))
            {
                RetainerUi.SelectMenuIndex(i);
                return true;
            }
        }
        return false;
    }

    private static bool MenuHas(string[] texts) => RetainerUi.MenuEntries()?.Any(e => texts.Any(t => e.StartsWith(t, StringComparison.OrdinalIgnoreCase))) == true;

    /// <summary>Interacts with the panel if needed, then opens Submersible Management and the vessel, stopping at its menu.</summary>
    private static async Task OpenVessel(Vessel vessel, List<string> log, CancellationToken ct)
    {
        await Game.RunLoggedIn(() => { InventoryActionTools.EnsureNotBusy(); return true; }).ConfigureAwait(false);
        // The list of submersibles may still be open (opened by hand, or by an earlier step): pick from it right away.
        var listOpen = await Game.Run(() => RetainerUi.MenuEntries()?.Any(e => e.Contains(vessel.Name, StringComparison.Ordinal)) == true).ConfigureAwait(false);
        if (!listOpen && !await Game.Run(() => MenuHas(SubmersibleManagement)).ConfigureAwait(false))
        {
            await Game.Run(InteractWithPanel).ConfigureAwait(false);
            if (!await GameWindows.WaitFor(() => MenuHas(SubmersibleManagement), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
                throw new ToolException("The voyage control panel's menu did not open.");
        }
        if (!listOpen) await Game.Run(() => Pick(SubmersibleManagement)).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => RetainerUi.MenuEntries()?.Any(e => e.Contains(vessel.Name, StringComparison.Ordinal)) == true, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException($"{vessel.Name} is not in the panel's list of submersibles.");
        await Game.Run(() => Pick(null, vessel.Name)).ConfigureAwait(false);

        // A submersible that is back shows its voyage log first: finalizing it collects the loot and opens its menu.
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("AirShipExplorationResult") || MenuHas(Repair), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
            throw new ToolException($"{vessel.Name}'s menu did not open.");
        if (await Game.Run(() => GameWindows.Ready("AirShipExplorationResult")).ConfigureAwait(false))
        {
            await Game.Run(() => Fire("AirShipExplorationResult", 0)).ConfigureAwait(false);
            log.Add("Finalized the voyage and collected the loot.");
            if (!await GameWindows.WaitFor(() => MenuHas(Repair), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
                throw new ToolException($"{vessel.Name}'s menu did not open after the voyage log.");
        }
    }

    /// <summary>Picks the vessel from the panel's list, chooses Recall and confirms, and waits until it is back.</summary>
    private static async Task RecallVessel(Vessel vessel, List<string> log, CancellationToken ct)
    {
        // The confirmation may still be open (an earlier attempt): go straight to it.
        if (!await Game.Run(() => GameWindows.Ready("AirShipExplorationDetail")).ConfigureAwait(false))
            await ChooseRecall(vessel, ct).ConfigureAwait(false);
        await ConfirmRecall(vessel, log, ct).ConfigureAwait(false);
    }

    private static async Task ChooseRecall(Vessel vessel, CancellationToken ct)
    {
        await Game.RunLoggedIn(() => { InventoryActionTools.EnsureNotBusy(); return true; }).ConfigureAwait(false);
        var inList = await Game.Run(() => RetainerUi.MenuEntries()?.Any(e => e.Contains(vessel.Name, StringComparison.Ordinal)) == true).ConfigureAwait(false);
        if (!inList)
        {
            if (!await Game.Run(() => MenuHas(SubmersibleManagement)).ConfigureAwait(false))
            {
                await Game.Run(InteractWithPanel).ConfigureAwait(false);
                if (!await GameWindows.WaitFor(() => MenuHas(SubmersibleManagement), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
                    throw new ToolException("The voyage control panel's menu did not open.");
            }
            await Game.Run(() => Pick(SubmersibleManagement)).ConfigureAwait(false);
            if (!await GameWindows.WaitFor(() => RetainerUi.MenuEntries()?.Any(e => e.Contains(vessel.Name, StringComparison.Ordinal)) == true, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
                throw new ToolException($"{vessel.Name} is not in the panel's list of submersibles.");
        }
        await Game.Run(() => Pick(null, vessel.Name)).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => MenuHas(PreviousLog), TimeSpan.FromSeconds(8), ct).ConfigureAwait(false))
            throw new ToolException($"{vessel.Name}'s menu did not open.");
        var picked = await Game.Run(() =>
        {
            if (Pick(Recall)) return true;
            // Other languages: while out on a voyage, Recall is the menu's first entry.
            if (RetainerUi.MenuEntries() is { Count: > 0 }) { RetainerUi.SelectMenuIndex(0); return true; }
            return false;
        }).ConfigureAwait(false);
        if (!picked) throw new ToolException($"{vessel.Name}'s menu has no Recall entry.");
    }

    /// <summary>
    /// The voyage details window asks "Recalling your vessel will cancel the current voyage… Proceed?" with Recall (its first button,
    /// as Deploy is when sending out) and Cancel; a yes/no may follow. Confirm until the vessel is back.
    /// </summary>
    private static async Task ConfirmRecall(Vessel vessel, List<string> log, CancellationToken ct)
    {
        // The yes/no ("Recall the voyage?") opens on top of the details window, which stays open behind it: answer it first, and
        // press Recall in the details window once.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var recallPressed = false;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var window = await Game.Run(() => GameWindows.Ready("SelectYesno") ? "SelectYesno"
                                            : !recallPressed && GameWindows.Ready("AirShipExplorationDetail") ? "AirShipExplorationDetail" : null).ConfigureAwait(false);
            if (window is not null)
            {
                await Game.Run(() => Fire(window, 0)).ConfigureAwait(false);
                if (window == "AirShipExplorationDetail") { recallPressed = true; await Task.Delay(800, ct).ConfigureAwait(false); continue; }
                // The yes/no was the last question: the game recalls on it (its return time only updates when the panel reloads).
                log.Add("Confirmed the recall.");
                await Task.Delay(1200, ct).ConfigureAwait(false);
                log.Add($"{vessel.Name} is back.");
                return;
            }
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        throw new ToolException($"{vessel.Name} was not recalled: the confirmation did not come, or the game refused.");
    }

    private static unsafe bool InteractWithPanel()
    {
        var panel = Svc.Objects.Where(o => NavigationTools.VoyagePanelIds.Value.Contains(o.BaseId))
            .OrderBy(o => Game.DistanceToPlayer(o.Position)).FirstOrDefault();
        if (panel is null || Game.DistanceToPlayer(panel.Position) > 6)
            throw new ToolException("You are not at the voyage control panel. Go there first (navigate_to destination workshop).");
        TargetSystem.Instance()->InteractWithObject((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)panel.Address, true);
        return true;
    }

    /// <summary>Repairs the parts below <paramref name="belowPercent"/> from the vessel's menu, and comes back to it.</summary>
    private static async Task<List<string>> RepairParts(Vessel vessel, int belowPercent, List<string> log, CancellationToken ct)
    {
        var before = await Game.Run(() => Condition(vessel)).ConfigureAwait(false);
        var slots = VesselRepair.SlotsToRepair(before, belowPercent);
        if (slots.Count == 0) return [];
        if (await Game.Run(() => Items.CountInBags(RepairMaterials)).ConfigureAwait(false) == 0)
            throw new ToolException($"{vessel.Name} needs repairs ({string.Join(", ", slots.Select(s => $"{Parts[s]} {before[s]}%"))}), but you have no Magitek Repair Materials.");

        await Game.Run(() => Pick(Repair)).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("CompanyCraftSupply"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException("The repair window did not open.");
        var repaired = new List<string>();
        foreach (var slot in slots)
        {
            await Game.Run(() => Fire("CompanyCraftSupply", 3, null, slot, null, null, null)).ConfigureAwait(false);
            if (!await GameWindows.WaitFor(() => GameWindows.Ready("SelectYesno"), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
                throw new ToolException($"The game did not offer to repair the {Parts[slot]} (not enough Magitek Repair Materials?).");
            await Game.Run(() => Fire("SelectYesno", 0)).ConfigureAwait(false);
            if (!await GameWindows.WaitFor(() => Condition(vessel)[slot] > before[slot], TimeSpan.FromSeconds(6), ct).ConfigureAwait(false))
                throw new ToolException($"The {Parts[slot]} was not repaired.");
            repaired.Add(Parts[slot]);
            log.Add($"Repaired the {Parts[slot]} ({before[slot]}% → 100%).");
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        await Game.Run(() => Fire("CompanyCraftSupply", 5)).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => MenuHas(Repair), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException($"{vessel.Name}'s menu did not come back after repairing.");
        return repaired;
    }

    private static async Task DeployPrevious(Vessel vessel, List<string> log, CancellationToken ct)
    {
        if (!await Game.Run(() => Pick(PreviousLog)).ConfigureAwait(false))
            throw new ToolException($"{vessel.Name} has no previous voyage to repeat; give a route.");
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("AirShipExplorationResult"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException("The previous voyage log did not open.");
        await Game.Run(() => Fire("AirShipExplorationResult", 1)).ConfigureAwait(false); // Redeploy
        await Deploy(log, ct).ConfigureAwait(false);
        log.Add("Redeployed on the previous route.");
    }

    private static async Task DeployRoute(IReadOnlyList<SubmarinePoint> route, List<string> log, CancellationToken ct)
    {
        await Game.Run(() => Pick(DeployNew)).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("SubmarineExplorationMapSelect"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException("The sea selection did not open.");
        await Game.Run(() => Fire("SubmarineExplorationMapSelect", 2, null, route[0].Map)).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("AirShipExploration"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException("The voyage map did not open.");
        await Task.Delay(500, ct).ConfigureAwait(false);
        foreach (var point in route)
        {
            await Game.Run(() => SelectPoint(point)).ConfigureAwait(false);
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        await Game.Run(() => Fire("AirShipExploration", 0)).ConfigureAwait(false); // Deploy
        await Deploy(log, ct).ConfigureAwait(false);
        log.Add($"Deployed: {string.Join(" → ", route.Select(p => p.Name))}.");
    }

    /// <summary>The voyage details: checks fuel and distance, deploys, and waits through the departure scene.</summary>
    private static async Task Deploy(List<string> log, CancellationToken ct)
    {
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("AirShipExplorationDetail"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException("The voyage details did not open.");
        var (fuel, distance) = await Game.Run(DetailNumbers).ConfigureAwait(false);
        if (fuel is { } f && f.Used > f.Available)
        {
            await Game.Run(() => Fire("AirShipExplorationDetail", -1)).ConfigureAwait(false);
            throw new ToolException($"Not enough Ceruleum Tanks: the voyage needs {f.Used}, you have {f.Available}.");
        }
        if (distance is { } d && d.Used > d.Available)
        {
            await Game.Run(() => Fire("AirShipExplorationDetail", -1)).ConfigureAwait(false);
            throw new ToolException($"The route is too long for this submersible ({d.Used} of {d.Available} distance).");
        }
        await Game.Run(() => Fire("AirShipExplorationDetail", 0)).ConfigureAwait(false);
        if (fuel is { } used) log.Add($"Used {used.Used} Ceruleum Tanks.");
        // The departure scene plays, then the panel's list of submersibles comes back.
        await GameWindows.WaitFor(() => Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Svc.Condition[ConditionFlag.WatchingCutscene78], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        await GameWindows.WaitFor(() => !Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent] && !Svc.Condition[ConditionFlag.WatchingCutscene78], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        await GameWindows.WaitFor(() => RetainerUi.MenuEntries() is not null, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
    }

    /// <summary>Fuel ("needed/have") and distance ("route/range") as the voyage details show them.</summary>
    private static unsafe ((int Used, int Available)? Fuel, (int Used, int Available)? Distance) DetailNumbers()
    {
        var addon = GameWindows.Addon("AirShipExplorationDetail");
        string? Text(int i) => addon != null && i < addon->AtkValuesCount && addon->AtkValues[i].Type is AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8
            && addon->AtkValues[i].String.Value != null ? Dalamud.Memory.MemoryHelper.ReadStringNullTerminated((nint)addon->AtkValues[i].String.Value) : null;
        return (Text(1) is { } f ? VesselRepair.Fraction(f) : null, Text(2) is { } d ? VesselRepair.Fraction(d) : null);
    }

    /// <summary>
    /// Selects a point on the voyage map, by its place in the map's list (values from 13 on, seven per point: full name at +1,
    /// letter at +2, state at +6, where 0 and 1 can be selected). The map takes a click event (type 35) with the point's index.
    /// </summary>
    private static unsafe bool SelectPoint(SubmarinePoint point)
    {
        var addon = GameWindows.Addon("AirShipExploration");
        if (addon == null) throw new ToolException("The voyage map closed.");
        for (var i = 0; 13 + i * 7 + 6 < addon->AtkValuesCount && i < 74; i++)
        {
            var name = ReadValue(addon, 13 + i * 7 + 1);
            var code = ReadValue(addon, 13 + i * 7 + 2);
            if (!point.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !point.Code.Equals(code, StringComparison.OrdinalIgnoreCase)) continue;
            var state = addon->AtkValues[13 + i * 7 + 6].UInt;
            if (state is not (0 or 1)) throw new ToolException($"{point.Name} cannot be selected on the voyage map.");
            ClickPoint(addon, i);
            return true;
        }
        throw new ToolException($"{point.Name} is not on the voyage map.");
    }

    private static unsafe string ReadValue(AtkUnitBase* addon, int index)
    {
        var v = addon->AtkValues[index];
        if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8) || v.String.Value == null) return "";
        return Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue.Trim();
    }

    /// <summary>The click on a map point: event data whose first pointer leads (at +168) to a block holding 0x0FFFFFFF at +172, the index at +16.</summary>
    private static unsafe void ClickPoint(AtkUnitBase* addon, int index)
    {
        var evt = stackalloc byte[sizeof(AtkEvent)];
        new Span<byte>(evt, sizeof(AtkEvent)).Clear();
        var inner = stackalloc byte[256];
        new Span<byte>(inner, 256).Clear();
        *(int*)(inner + 172) = 0x0FFFFFFF;
        var middle = stackalloc byte[256];
        new Span<byte>(middle, 256).Clear();
        *(byte**)(middle + 168) = inner;
        var data = stackalloc byte[64];
        new Span<byte>(data, 64).Clear();
        *(byte**)data = middle;
        *(int*)(data + 16) = index;
        addon->ReceiveEvent((AtkEventType)35, 0, (AtkEvent*)evt, (AtkEventData*)data);
    }

    private static unsafe bool Fire(string addon, params object?[] values)
    {
        var a = GameWindows.Addon(addon);
        if (a == null) throw new ToolException($"The {addon} window closed unexpectedly.");
        RetainerUi.Fire(a, true, values);
        return true;
    }

    /// <summary>Backs out of the panel's menus: "Quit" from a vessel, "Nothing." from the list, "Cancel" from the panel.</summary>
    private static async Task Leave(CancellationToken ct)
    {
        for (var i = 0; i < 6; i++)
        {
            var picked = await Game.Run(() => Pick(QuitVessel) || Pick(Nothing) || Pick(Cancel)).ConfigureAwait(false);
            if (!picked) return;
            await Task.Delay(600, ct).ConfigureAwait(false);
        }
    }
}
