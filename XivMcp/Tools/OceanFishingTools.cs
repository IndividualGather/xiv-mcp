using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using XivMcp.Jobs;
using XivMcp.Mcp;
using XivMcp.OceanFishing;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Ocean fishing: the voyage schedule (the game's IKDRouteTable), boarding at Dryskthota in Limsa Lominsa, the state of a voyage, and
/// a job that fishes voyages with AutoHook's ocean fishing mode until a target. Distant Seas adds its fish data, overlay and alarm.
/// </summary>
internal static class OceanFishingTools
{
    private const uint Dryskthota = 1005421, MerchantMender = 1005422;
    private const uint UnlockQuest = 69379, RubyQuest = 68089;
    private const uint BoatUse = 46; // TerritoryIntendedUse of the boats
    private const uint FisherJob = 18;
    private static readonly uint[] Baits = [29714, 29715, 29716]; // Ragworm, Krill, Plump Worm
    private const string EntranceSheet = "custom/006/CtsIkdEntrance_00663";

    public static IEnumerable<McpTool> Create(Configuration config, Func<JobManager> jobs, Func<string?> client)
    {
        yield return new McpTool
        {
            Name = "get_ocean_fishing_schedule",
            Description = "The next ocean fishing voyages from Limsa Lominsa's ferry docks (boarding opens at every even UTC hour for 15 " +
                          "minutes): when boarding opens, whether it is open now, the route (Indigo, and Ruby once unlocked) with its three " +
                          "stops and their time of day, and, with Distant Seas, the fish worth going for at each stop (spectral current " +
                          "triggers, the best scorers, and the spectral current's best fish) with their bait.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "count": { "type": "integer", "minimum": 1, "maximum": 24, "description": "How many voyages (default 4)." },
                    "route": { "type": "string", "enum": ["indigo", "ruby", "both"], "description": "Which route (default: both once the Ruby route is unlocked)." },
                    "fish": { "type": "boolean", "description": "Name the fish worth going for (default true; needs Distant Seas)." }
                  }
                }
                """,
            ReadOnly = true,
            Handler = (args, ct) => Game.Run(() => Schedule(args.Int("count", 4, 1, 24), args.String("route"), args.Node("fish") is not { } f || f.GetValue<bool>())),
        };

        yield return new McpTool
        {
            Name = "get_ocean_fishing_status",
            Description = "The ocean fishing voyage the player is on: the route, the stop (1-3) and its time of day, the seconds left at the " +
                          "stop, whether a spectral current is on, the voyage missions and their progress, the fish caught with their points, " +
                          "and the results once the voyage is over. Not on a boat: whether boarding is open and when the next voyage leaves.",
            InputSchema = """{ "type": "object", "properties": {} }""",
            ReadOnly = true,
            Handler = (_, ct) => Game.Run(Status),
        };

        yield return new McpTool
        {
            Name = "board_ocean_fishing",
            Description = "Boards the ocean fishing voyage that is boarding now (every even UTC hour, for 15 minutes): travels to Dryskthota " +
                          "at Limsa Lominsa's ferry docks, registers, picks the route (Ruby only once unlocked), confirms and accepts the " +
                          "duty. Returns once on the boat. Needs ocean fishing unlocked. Requires 'Ocean fishing' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "route": { "type": "string", "enum": ["indigo", "ruby"], "description": "Which route (default indigo)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var steps = new List<string>();
                await Board(Route(args), steps, ct).ConfigureAwait(false);
                return new { boarded = true, steps };
            },
        };

        yield return new McpTool
        {
            Name = "set_ocean_fishing_alarm",
            Description = "Turns Distant Seas' departure alarm on or off: a chat message (and a sound, if set) some minutes before boarding " +
                          "opens. Saved in Distant Seas' settings. Requires 'Ocean fishing' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "on": { "type": "boolean" },
                    "minutes": { "type": "integer", "minimum": 1, "maximum": 60, "description": "Minutes before boarding opens (default: keep)." },
                    "sound": { "type": "boolean", "description": "Also play a sound (default: keep)." }
                  },
                  "required": ["on"]
                }
                """,
            ReadOnly = false,
            Handler = (args, ct) => Game.Run(() =>
            {
                var on = args.Node("on")?.GetValue<bool>() ?? true;
                DistantSeasBridge.SetAlarm(on, args.Node("minutes") is null ? null : args.Int("minutes", 5, 1, 60), args.Node("sound")?.GetValue<bool>());
                var (enabled, minutes) = DistantSeasBridge.Alarm();
                return (object)new { alarm = enabled, minutes };
            }),
        };

        const string TargetProperties = """
                    "voyages": { "type": "integer", "minimum": 1, "maximum": 50, "description": "Stop after this many voyages." },
                    "points": { "type": "integer", "minimum": 1, "description": "Stop once the voyages scored this many points in total." },
                    "fish": { "type": "string", "description": "Stop once this fish (name or item id) was caught 'fish_count' times." },
                    "fish_count": { "type": "integer", "minimum": 1, "maximum": 99, "description": "Default 1." },
                    "max_voyages": { "type": "integer", "minimum": 1, "maximum": 50, "description": "Stop after this many voyages at the latest (for a fish or points target)." },
                    "goal": { "type": "string", "description": "What AutoHook aims for on each voyage: Points (default), Legendary, Achievement or Levelling." },
                    "route": { "type": "string", "enum": ["indigo", "ruby"], "description": "Which route (default indigo)." }
            """;

        yield return new McpTool
        {
            Name = "go_ocean_fishing",
            Description = "Starts a background job that fishes ocean voyages with AutoHook's ocean fishing mode until a target: a number of " +
                          "voyages, a points total, or a fish (with 'max_voyages' as a cap); without a target, one voyage. It switches to " +
                          "Fisher, buys the ocean baits (Ragworm, Krill, Plump Worm) when fewer than 30 are left, then waits for each boarding " +
                          "window (every 2 hours), boards, lets AutoHook fish (moving to the railing, switching bait per stop and spectral " +
                          "current), and collects the results. Distant Seas' overlay is shown during voyages. Follow it with get_job. " +
                          "Requires 'Ocean fishing' in /xivmcp; buying bait follows 'Market & purchases'.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    {{TargetProperties}}
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var (isFisher, low) = await Game.Run(() => (Svc.Objects.LocalPlayer?.ClassJob.RowId == FisherJob,
                                                            Baits.Where(b => Items.CountInBags(b) < 30).ToList())).ConfigureAwait(false);
                var steps = new List<JobManager.Step>();
                if (!isFisher) steps.Add(new() { Id = "job", Tool = "switch_gearset", Args = new JsonObject { ["gearset"] = "FSH" }, Note = "Switch to Fisher" });
                foreach (var bait in low)
                    steps.Add(new()
                    {
                        Id = $"bait{bait}", Tool = "buy_item", Args = new JsonObject { ["item"] = bait.ToString(), ["quantity"] = 99, ["npc"] = MerchantMender.ToString() },
                        Note = $"Buy 99 {Items.Name(bait)}",
                    });
                var voyageArgs = new JsonObject();
                foreach (var key in new[] { "voyages", "points", "fish", "fish_count", "max_voyages", "goal", "route" })
                    if (args.Node(key) is { } v) voyageArgs[key] = v.DeepClone();
                var target = Target(args);
                steps.Add(new() { Id = "voyages", Tool = "fish_ocean_voyages", Args = voyageArgs, Note = $"Fish ocean voyages until {Describe(target)}" });
                var job = jobs().Start($"Ocean fishing: {Describe(target)}", steps, client());
                return new { started = JobManager.Describe(job), next = await Game.Run(() => NextVoyage()).ConfigureAwait(false) };
            },
        };

        yield return new McpTool
        {
            Name = "fish_ocean_voyages",
            Description = "Fishes ocean voyages one after another until a target (a step of go_ocean_fishing's job; it can take many hours): " +
                          "waits for each boarding window, boards (board_ocean_fishing), walks to the free spot of the railing furthest from other players and faces the ocean, turns on AutoHook's ocean fishing mode with the goal " +
                          "for the voyage (put back afterwards), shows Distant Seas' overlay, waits until the voyage is over, records the " +
                          "points and fish, and closes the results. The player must be a Fisher. Requires 'Ocean fishing' in /xivmcp.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    {{TargetProperties}}
                  }
                }
                """,
            ReadOnly = false,
            Handler = (args, ct) => Voyages(args, ct),
        };
    }

    // ---------------------------------------------------------------- schedule and routes

    private sealed record Stop(uint SpotId, string Place, OceanTime Time);

    private static (string Name, List<Stop> Stops) RouteInfo(uint routeId)
    {
        var route = Svc.Data.GetExcelSheet<IKDRoute>().GetRow(routeId);
        var stops = new List<Stop>();
        for (var i = 0; i < route.Spot.Count; i++)
        {
            var spot = route.Spot[i];
            if (spot.RowId == 0) continue;
            var time = i < route.Time.Count ? (OceanTime)route.Time[i].RowId : OceanTime.Unknown;
            stops.Add(new Stop(spot.RowId, spot.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText() ?? $"Spot {spot.RowId}", time));
        }
        return (route.Name.ExtractText(), stops);
    }

    /// <summary>The IKDRoute rows of a route table row: the Indigo route and the Ruby route.</summary>
    private static (uint Indigo, uint Ruby) Routes(int row)
    {
        var r = Svc.Data.GetExcelSheet<IKDRouteTable>().GetRow((uint)row);
        return (r.Route.RowId, Convert.ToUInt32(r.Unknown0));
    }

    private static int TableSize => Svc.Data.GetExcelSheet<IKDRouteTable>().Count;

    private static unsafe bool RubyUnlocked => QuestManager.IsQuestComplete(RubyQuest);

    private static object Schedule(int count, string? which, bool withFish)
    {
        var ruby = RubyUnlocked;
        which ??= ruby ? "both" : "indigo";
        var data = withFish ? DistantSeasBridge.Spots() : null;
        var now = DateTimeOffset.UtcNow;
        return new
        {
            boardingOpen = OceanSchedule.BoardingOpen(now),
            rubyUnlocked = ruby,
            fishData = withFish && data is null ? "Install Distant Seas to see the fish worth going for." : null,
            voyages = OceanSchedule.Upcoming(now, count, TableSize).Select(v =>
            {
                var (indigo, rubyRoute) = Routes(v.Row);
                return new
                {
                    boarding = v.Boarding.ToString("u"),
                    local = v.Boarding.ToLocalTime().ToString("ddd HH:mm"),
                    inMinutes = v.Open ? 0 : (int)Math.Ceiling((v.Boarding - now).TotalMinutes),
                    open = v.Open,
                    indigo = which is "indigo" or "both" ? DescribeRoute(indigo, data) : null,
                    ruby = which is "ruby" or "both" ? DescribeRoute(rubyRoute, data) : null,
                };
            }),
        };
    }

    private static object? DescribeRoute(uint routeId, IReadOnlyList<OceanSpotData>? data)
    {
        if (routeId == 0) return null;
        var (name, stops) = RouteInfo(routeId);
        return new
        {
            route = name,
            stops = stops.Select(s =>
            {
                var h = data is null ? null : OceanFishData.Highlights(data, s.SpotId, s.Time, 3);
                return new
                {
                    place = s.Place,
                    time = s.Time.ToString(),
                    spectralTriggers = h?.Normal.Where(f => f.TriggersSpectral).Select(FishText),
                    bestFish = h?.Normal.Where(f => !f.TriggersSpectral).Select(FishText),
                    spectralCurrent = h?.Spectral.Select(FishText),
                };
            }),
        };
    }

    private static string FishText(OceanFish f) =>
        $"{Items.Name(f.ItemId)} ({f.Points} pts{(f.BestBait is { } b ? $", {Items.Name(b)}" : f.Intuition ? ", needs intuition" : "")})";

    private static object NextVoyage()
    {
        var next = OceanSchedule.Upcoming(DateTimeOffset.UtcNow, 1, TableSize)[0];
        var (indigo, _) = Routes(next.Row);
        return new { boarding = next.Boarding.ToString("u"), open = next.Open, route = RouteInfo(indigo).Name };
    }

    // ---------------------------------------------------------------- voyage state

    private static unsafe InstanceContentOceanFishing* Voyage()
    {
        var ef = EventFramework.Instance();
        return ef == null ? null : ef->GetInstanceContentOceanFishing();
    }

    private static bool OnBoat =>
        Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(Svc.ClientState.TerritoryType)?.TerritoryIntendedUse.RowId == BoatUse;

    private static unsafe object Status()
    {
        var oc = Voyage();
        if (oc == null || !OnBoat)
            return new { onVoyage = false, boardingOpen = OceanSchedule.BoardingOpen(DateTimeOffset.UtcNow), next = NextVoyage() };
        var (name, stops) = RouteInfo(oc->CurrentRoute);
        var zone = (int)oc->CurrentZone;
        var stop = zone < stops.Count ? stops[zone] : null;
        var caught = Caught(oc);
        var missions = new List<object>();
        var sheet = Svc.Data.GetExcelSheet<IKDPlayerMissionCondition>();
        foreach (var (type, progress) in new[] { (oc->Mission1Type, oc->Mission1Progress), (oc->Mission2Type, oc->Mission2Progress), (oc->Mission3Type, oc->Mission3Progress) })
        {
            if (type == 0) continue;
            var row = sheet.GetRowOrDefault(type);
            missions.Add(new { mission = row?.Unknown0.ExtractText(), progress, of = row?.Unknown1 });
        }
        var finished = oc->Status == InstanceContentOceanFishing.OceanFishingStatus.Finished;
        return new
        {
            onVoyage = true,
            route = name,
            status = oc->Status.ToString(),
            stop = zone + 1,
            place = stop?.Place,
            time = stop?.Time.ToString(),
            secondsLeft = SecondsLeft(oc),
            spectralCurrent = oc->SpectralCurrentActive,
            missions,
            caught = caught.Select(c => new { fish = Items.Name(c.Key), count = c.Value.Count, points = c.Value.Points }),
            points = caught.Values.Sum(c => c.Points),
            result = finished ? new { totalPoints = oc->IndividualResult.TotalPoints } : null,
        };
    }

    private static unsafe int? SecondsLeft(InstanceContentOceanFishing* oc)
    {
        var left = oc->InstanceContentDirector.ContentDirector.ContentTimeLeft - oc->TimeOffset;
        return left > 0 ? (int)left : null;
    }

    private static unsafe Dictionary<uint, (int Count, int Points)> Caught(InstanceContentOceanFishing* oc)
    {
        var caught = new Dictionary<uint, (int Count, int Points)>();
        foreach (var f in oc->FishData)
        {
            if (f.ItemId == 0 || f.NqAmount + f.HqAmount == 0) continue; // the route's fish list holds every fish, caught or not
            caught.TryGetValue(f.ItemId, out var had);
            caught[f.ItemId] = (had.Count + f.NqAmount + f.HqAmount, had.Points + (int)f.TotalPoints);
        }
        return caught;
    }

    // ---------------------------------------------------------------- boarding

    private static bool Route(ToolArgs args) => args.String("route") is { } r && r.Equals("ruby", StringComparison.OrdinalIgnoreCase);

    private static string EntranceText(uint row)
    {
        try { return Svc.Data.GetExcelSheet<RawRow>(name: EntranceSheet).GetRow(row).ReadStringColumn(1).ExtractText(); }
        catch { return ""; }
    }

    private static async Task Board(bool ruby, List<string> steps, CancellationToken ct)
    {
        if (await Game.Run(() => OnBoat).ConfigureAwait(false)) { steps.Add("Already on the boat."); return; }
        var check = await Game.RunLoggedIn(() =>
        {
            unsafe
            {
                if (!QuestManager.IsQuestComplete(UnlockQuest)) return "Ocean fishing is not unlocked yet (the quest \"Fishing for Friendship\"?).";
                if (ruby && !RubyUnlocked) return "The Ruby route is not unlocked yet.";
            }
            return null;
        }).ConfigureAwait(false);
        if (check is not null) throw new ToolException(check);
        if (!OceanSchedule.BoardingOpen(DateTimeOffset.UtcNow))
        {
            var next = OceanSchedule.Upcoming(DateTimeOffset.UtcNow, 1, await Game.Run(() => TableSize).ConfigureAwait(false))[0];
            throw new ToolException($"Boarding is closed. It opens at {next.Boarding:HH:mm} UTC (in {(int)Math.Ceiling((next.Boarding - DateTimeOffset.UtcNow).TotalMinutes)} minutes).");
        }

        var npc = await Game.Run(() => TriadData.Locate(Dryskthota, "Dryskthota")).ConfigureAwait(false)
                  ?? throw new ToolException("Dryskthota was not found in the game data.");
        await NavigationTools.GoToNpc(npc, steps, ct).ConfigureAwait(false);

        var (boardText, routeNames) = await Game.Run(() =>
        {
            var (indigo, rubyRoute) = Routes(OceanSchedule.Row(DateTimeOffset.UtcNow, TableSize));
            return (EntranceText(4), (Indigo: RouteInfo(indigo).Name, Ruby: rubyRoute == 0 ? "" : RouteInfo(rubyRoute).Name));
        }).ConfigureAwait(false);

        await Game.Run(() => { Interact(Dryskthota); return true; }).ConfigureAwait(false);
        steps.Add("Talking to Dryskthota.");
        var registered = false;
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(400, ct).ConfigureAwait(false);
            var done = await Game.Run(() =>
            {
                if (OnBoat || Svc.Condition[ConditionFlag.BoundByDuty]) return "aboard";
                if (RetainerUi.Ready("ContentsFinderConfirm")) { FireIn("ContentsFinderConfirm", 8); return "accepted"; }
                if (RetainerUi.Ready("SelectYesno")) { FireIn("SelectYesno", 0); return "embark"; }
                if (RetainerUi.Ready("SelectString") && RetainerUi.MenuEntries() is { } entries)
                {
                    if (!registered)
                    {
                        var i = entries.FindIndex(e => boardText.Length > 0 && e.Contains(boardText, StringComparison.OrdinalIgnoreCase));
                        if (i < 0) i = entries.FindIndex(e => e.Contains("board", StringComparison.OrdinalIgnoreCase) || e.Contains("ocean fishing", StringComparison.OrdinalIgnoreCase));
                        if (i < 0) throw new ToolException($"Dryskthota's menu has no entry to board. Entries: {string.Join(" | ", entries)}.");
                        RetainerUi.SelectMenuIndex(i);
                        registered = true;
                        return "menu:" + entries[i];
                    }
                    // The route choice (Ruby route unlocked): pick the route by its name, else by its place in the list.
                    var name = ruby ? routeNames.Ruby : routeNames.Indigo;
                    var pick = name.Length > 0 ? entries.FindIndex(e => e.Contains(name, StringComparison.OrdinalIgnoreCase)) : -1;
                    if (pick < 0) pick = ruby ? Math.Min(1, entries.Count - 1) : 0;
                    RetainerUi.SelectMenuIndex(pick);
                    return "route:" + entries[pick];
                }
                if (RetainerUi.Ready("Talk")) { RetainerUi.ClickTalk(); return "talk"; }
                return null;
            }).ConfigureAwait(false);
            if (done is null or "talk") continue;
            if (done == "aboard") break;
            steps.Add(done switch
            {
                "accepted" => "Accepted the duty.",
                "embark" => "Confirmed boarding.",
                _ when done.StartsWith("menu:") => $"Menu: {done[5..]}.",
                _ => $"Route: {done[6..]}.",
            });
        }
        if (!await WaitFor(() => OnBoat, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false))
            throw new ToolException("Did not get onto the boat. The duty may not have popped, or boarding closed.");
        steps.Add("On the boat.");
    }

    private static unsafe void Interact(uint baseId)
    {
        var obj = Svc.Objects.Where(o => o.BaseId == baseId && o.IsTargetable).OrderBy(o => Game.DistanceToPlayer(o.Position)).FirstOrDefault()
                  ?? throw new ToolException("Dryskthota is not nearby.");
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        TargetSystem.Instance()->SetHardTarget(native, false, false, 0);
        TargetSystem.Instance()->InteractWithObject(native, true);
    }

    private static unsafe void FireIn(string window, int value)
    {
        var ptr = RetainerUi.Ptr(window);
        if (!ptr.IsNull) RetainerUi.Fire((AtkUnitBase*)ptr.Address, true, value);
    }

    // ---------------------------------------------------------------- voyages until a target

    private static OceanTarget Target(ToolArgs args)
    {
        uint? fish = null;
        if (args.String("fish") is { } f) fish = uint.TryParse(f, out var id) ? id : null;
        return new OceanTarget(
            args.Node("voyages") is null ? null : args.Int("voyages", 1, 1, 50),
            args.Node("points") is null ? null : args.Int("points", 1, 1, int.MaxValue),
            fish, args.Int("fish_count", 1, 1, 99),
            args.Node("max_voyages") is null ? null : args.Int("max_voyages", 1, 1, 50));
    }

    private static string Describe(OceanTarget t)
    {
        var parts = new List<string>();
        if (t.Voyages is { } v) parts.Add($"{v} voyage{(v == 1 ? "" : "s")}");
        if (t.Points is { } p) parts.Add($"{p:N0} points");
        if (t.Fish is { } f) parts.Add($"{t.FishCount} {Items.Name(f)}");
        if (t.MaxVoyages is { } m) parts.Add($"at most {m} voyages");
        return parts.Count == 0 ? "one voyage" : string.Join(" or ", parts);
    }

    private static async Task<object> Voyages(ToolArgs args, CancellationToken ct)
    {
        if (!AutoHookTools.Loaded) throw new ToolException("AutoHook is not loaded; it does the fishing.");
        var goal = args.String("goal") ?? "Points";
        var ruby = Route(args);
        // A fish given by name: resolve it to an item id once.
        if (args.String("fish") is { } fishArg && !uint.TryParse(fishArg, out _))
            args.Raw["fish"] = (await Game.Run(() => Items.Resolve(fishArg)).ConfigureAwait(false)).RowId.ToString();
        var target = Target(args);
        if (await Game.Run(() => Svc.Objects.LocalPlayer?.ClassJob.RowId != FisherJob && !OnBoat).ConfigureAwait(false))
            throw new ToolException("Switch to Fisher first (switch_gearset FSH).");

        var log = new List<object>();
        var caughtTotal = new Dictionary<uint, int>();
        var points = 0;
        var progress = new OceanProgress(0, 0, caughtTotal);
        while (!target.Reached(progress))
        {
            var steps = new List<string>();
            // Wait for boarding to open (unless already aboard).
            while (!OceanSchedule.BoardingOpen(DateTimeOffset.UtcNow) && !await Game.Run(() => OnBoat).ConfigureAwait(false))
            {
                var next = OceanSchedule.Upcoming(DateTimeOffset.UtcNow, 1, 1)[0];
                var wait = next.Boarding - DateTimeOffset.UtcNow;
                await Task.Delay(wait > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait < TimeSpan.Zero ? TimeSpan.FromSeconds(1) : wait, ct).ConfigureAwait(false);
            }
            await Board(ruby, steps, ct).ConfigureAwait(false);

            int voyagePoints;
            Dictionary<uint, (int Count, int Points)> caught;
            // XIV MCP takes the railing spot itself when it can walk on the boat; otherwise AutoHook walks there.
            var walkOurselves = Navigation.VnavmeshLoaded;
            var autoHook = await Game.Run(() => AutoHookOcean.Enable(goal, walkToRailing: !walkOurselves)).ConfigureAwait(false);
            var overlay = await Game.Run(() => DistantSeasBridge.Loaded ? DistantSeasBridge.ShowOverlay() : null).ConfigureAwait(false);
            try
            {
                await Game.Run(() => { AutoHookTools.SetAutoHook(true); return true; }).ConfigureAwait(false);
                steps.Add($"AutoHook fishes the voyage (goal: {goal}).");
                if (walkOurselves) await TakeRailSpot(steps, ct).ConfigureAwait(false);
                var idleSince = (DateTime?)null;
                // The voyage: three stops of 7 minutes, plus the sailing between them.
                var until = DateTime.UtcNow.AddMinutes(40);
                while (DateTime.UtcNow < until)
                {
                    var state = await Game.Run(() => { unsafe { var oc = Voyage(); return oc == null ? (InstanceContentOceanFishing.OceanFishingStatus?)null : oc->Status; } }).ConfigureAwait(false);
                    if (state == InstanceContentOceanFishing.OceanFishingStatus.Finished || !await Game.Run(() => OnBoat).ConfigureAwait(false)) break;
                    var idle = await Game.Run(() => { unsafe { var oc = Voyage(); return oc != null && oc->Status == InstanceContentOceanFishing.OceanFishingStatus.Fishing && (SecondsLeft(oc) ?? 0) > 35 && !Fishing; } }).ConfigureAwait(false);
                    idleSince = idle ? idleSince ?? DateTime.UtcNow : null;
                    if (idleSince is { } since && DateTime.UtcNow - since > TimeSpan.FromSeconds(15))
                    {
                        await StartFishing(steps, ct).ConfigureAwait(false);
                        idleSince = null;
                    }
                    await Task.Delay(2000, ct).ConfigureAwait(false);
                }
                (voyagePoints, caught) = await Game.Run(() =>
                {
                    unsafe
                    {
                        var oc = Voyage();
                        if (oc == null) return (0, new Dictionary<uint, (int Count, int Points)>());
                        var c = Caught(oc);
                        var total = oc->Status == InstanceContentOceanFishing.OceanFishingStatus.Finished && oc->IndividualResult.TotalPoints > 0
                            ? (int)oc->IndividualResult.TotalPoints : c.Values.Sum(x => x.Points);
                        return (total, c);
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                await Game.Run(() =>
                {
                    AutoHookTools.SetAutoHook(false);
                    autoHook.Dispose();
                    overlay?.Dispose();
                    return true;
                }).ConfigureAwait(false);
            }

            // Close the results, which takes the player back to Limsa Lominsa.
            var leaveBy = DateTime.UtcNow.AddMinutes(3);
            while (await Game.Run(() => OnBoat).ConfigureAwait(false) && DateTime.UtcNow < leaveBy)
            {
                await Game.Run(() => { if (RetainerUi.Ready("IKDResult")) FireIn("IKDResult", 0); return true; }).ConfigureAwait(false);
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            steps.Add("Back in Limsa Lominsa.");

            points += voyagePoints;
            foreach (var (fish, c) in caught) caughtTotal[fish] = caughtTotal.GetValueOrDefault(fish) + c.Count;
            progress = new OceanProgress(progress.Voyages + 1, points, caughtTotal);
            log.Add(new { voyage = progress.Voyages, points = voyagePoints, caught = caught.Select(c => $"{c.Value.Count} {Items.Name(c.Key)}"), steps });
            JobManager.Instance?.StepProgress("fish_ocean_voyages", $"{progress.Voyages} voyage{(progress.Voyages == 1 ? "" : "s")}, {points:N0} points");
        }
        return new
        {
            voyages = progress.Voyages,
            points,
            target = Describe(target),
            fish = target.Fish is { } f ? new { fish = Items.Name(f), caught = caughtTotal.GetValueOrDefault(f) } : null,
            log,
        };
    }

    /// <summary>
    /// After the loading screen: walks to the free spot of the railing furthest from the other players and faces the ocean, checking
    /// again once there in case someone took it meanwhile. False when it can't (no vnavmesh, or no path on the boat): AutoHook then
    /// walks to the railing itself.
    /// </summary>
    private static async Task<bool> TakeRailSpot(List<string> steps, CancellationToken ct)
    {
        if (!await WaitFor(() => OnBoat && Svc.Objects.LocalPlayer is not null && !Svc.Condition[ConditionFlag.BetweenAreas], TimeSpan.FromSeconds(60), ct).ConfigureAwait(false))
            return false;
        // Until the boat leaves, the deck is fenced off: wait for the first stop to begin.
        await WaitFor(() => { unsafe { var oc = Voyage(); return oc != null && oc->Status == InstanceContentOceanFishing.OceanFishingStatus.Fishing; } },
                      TimeSpan.FromMinutes(16), ct).ConfigureAwait(false);
        await Task.Delay(2000, ct).ConfigureAwait(false);
        if (await Game.Run(() => Fishing).ConfigureAwait(false)) return true; // AutoHook got there first
        if (!Navigation.VnavmeshLoaded || !await WaitFor(() => Navigation.NavReady, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
        {
            steps.Add("No path on the boat: AutoHook walks to the railing.");
            await StartFishing(steps, ct).ConfigureAwait(false);
            return false;
        }
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var spot = await Game.Run(() =>
            {
                var me = Svc.Objects.LocalPlayer!;
                var others = Svc.Objects.OfType<Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter>()
                    .Where(p => p.GameObjectId != me.GameObjectId).Select(p => p.Position);
                return Railing.FreeSpot(me.Position, others);
            }).ConfigureAwait(false);
            if (await Game.Run(() => Game.DistanceToPlayer(spot.Position) ?? 99).ConfigureAwait(false) > 0.4f)
            {
                if (!await Game.Run(() => Navigation.MoveCloseTo(spot.Position, 0.2f)).ConfigureAwait(false))
                {
                    steps.Add("No path on the boat: AutoHook walks to the railing.");
                    await StartFishing(steps, ct).ConfigureAwait(false);
                    return false;
                }
                await Task.Delay(500, ct).ConfigureAwait(false);
                await WaitFor(() => !Navigation.PathRunning, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            }
            await Game.Run(() => { unsafe { ((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)Svc.Objects.LocalPlayer!.Address)->SetRotation(spot.Facing); } return true; }).ConfigureAwait(false);
            await Task.Delay(1000, ct).ConfigureAwait(false);
            // Someone may have walked up to the same spot meanwhile: if so, pick again.
            var crowded = await Game.Run(() =>
            {
                var me = Svc.Objects.LocalPlayer!;
                return Svc.Objects.OfType<Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter>()
                    .Any(p => p.GameObjectId != me.GameObjectId && System.Numerics.Vector3.Distance(p.Position, me.Position) < 0.8f);
            }).ConfigureAwait(false);
            if (!crowded)
            {
                steps.Add(spot.Gap == float.MaxValue ? "At the railing, facing the ocean (nobody else around)."
                                                     : $"At the railing, facing the ocean, {spot.Gap:0.#} yalms from the nearest player.");
                await StartFishing(steps, ct).ConfigureAwait(false);
                return true;
            }
        }
        steps.Add("The railing is crowded: stayed at the freest spot found.");
        await StartFishing(steps, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Puts on a bait when none is chosen, or the chosen one ran out: during a spectral current the bait of its best fish, otherwise
    /// the bait of the stop's spectral trigger (Distant Seas' data), else any ocean bait in the bags. Returns its name when it switched.
    /// </summary>
    private static unsafe string? ChooseBait()
    {
        var current = PlayerState.Instance()->FishingBait;
        if (current != 0 && Items.CountInBags(current) > 0) return null;
        var oc = Voyage();
        var wanted = new List<uint>();
        if (oc != null && DistantSeasBridge.Spots() is { } data)
        {
            var (_, stops) = RouteInfo(oc->CurrentRoute);
            if (oc->CurrentZone < stops.Count)
            {
                var stop = stops[(int)oc->CurrentZone];
                var h = OceanFishData.Highlights(data, stop.SpotId, stop.Time, 3);
                var fish = oc->SpectralCurrentActive ? h.Spectral : h.Normal.Where(f => f.TriggersSpectral).Concat(h.Normal);
                wanted.AddRange(fish.Where(f => f.BestBait is not null).Select(f => f.BestBait!.Value));
            }
        }
        wanted.AddRange(Baits);
        var bait = wanted.FirstOrDefault(b => Items.CountInBags(b) > 0);
        if (bait == 0) return null;
        Svc.PluginInterface.GetIpcSubscriber<uint, bool>("AutoHook.SwapBaitById").InvokeFunc(bait);
        return Items.Name(bait);
    }

    private static bool Fishing => Svc.Condition[ConditionFlag.Fishing] || Svc.Condition[ConditionFlag.Gathering];

    /// <summary>Gets AutoHook fishing when the line isn't out: its start command, then a cast (AutoHook hooks and recasts from there).</summary>
    private static async Task StartFishing(List<string> steps, CancellationToken ct)
    {
        if (await WaitFor(() => Fishing, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false)) return;
        if (await Game.Run(ChooseBait).ConfigureAwait(false) is { } bait) steps.Add($"Bait: {bait}.");
        await Game.Run(() => { AutoHookTools.SetAutoHook(true); Svc.Commands.ProcessCommand("/ahstart"); return true; }).ConfigureAwait(false);
        if (await WaitFor(() => Fishing, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false)) { steps.Add("Started fishing (AutoHook)."); return; }
        await Game.Run(() => { unsafe { return ActionManager.Instance()->UseAction(ActionType.Action, 289); } }).ConfigureAwait(false);
        if (await WaitFor(() => Fishing, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false)) steps.Add("Started fishing (cast).");
        else steps.Add("Could not start fishing here.");
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await Game.Run(condition).ConfigureAwait(false)) return true;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        return false;
    }
}
