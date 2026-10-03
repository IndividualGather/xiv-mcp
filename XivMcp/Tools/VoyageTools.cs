using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Free company submersibles and airships, from <see cref="WorkshopTracker"/> snapshots.</summary>
internal static class VoyageTools
{
    private static readonly string[] StatNames = ["surveillance", "retrieval", "speed", "range", "favor"];

    /// <summary>SubmarinePart.Class → community build notation (hull, stern, bow, bridge — e.g. "SSUC").</summary>
    private static readonly Dictionary<ushort, string> ClassLetters = new()
    {
        [3] = "S", [2] = "U", [1] = "W", [4] = "C", [5] = "Y",
        [8] = "S+", [7] = "U+", [6] = "W+", [9] = "C+", [10] = "Y+",
    };

    private static readonly Lazy<Dictionary<uint, Item>> PartItems = new(() =>
        Svc.Data.GetExcelSheet<Item>()
            .Where(i => i.FilterGroup == 36 && i.AdditionalData.RowId != 0) // filter group 36 = submersible parts; AdditionalData = SubmarinePart row
            .GroupBy(i => i.AdditionalData.RowId)
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.RowId).First()));

    public static IEnumerable<McpTool> Create(WorkshopTracker tracker)
    {
        yield return new McpTool
        {
            Name = "get_submersibles",
            Description = "Free company submersibles (and airships) of the logged-in character: name, rank, EXP to next rank, parts and build code " +
                          "(hull-stern-bow-bridge, e.g. SSUC), surveillance/retrieval/speed/range/favor (base + bonus), current voyage route with " +
                          "sector names, return time / time remaining / ready state, loot of the last voyage, and unlocked/explored sectors per sea. " +
                          "The game only provides this data inside the FC workshop; the plugin snapshots it each time you are there, so the result " +
                          "may be from an earlier visit (see capturedAt). Voyage return times stay accurate regardless.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "all_characters": { "type": "boolean", "description": "Return the saved snapshots of all characters instead of only the logged-in one (default false)." },
                    "include_airships": { "type": "boolean", "description": "Include airships (default true)." },
                    "include_loot": { "type": "boolean", "description": "Include last voyage loot (default true)." },
                    "include_sectors": { "type": "boolean", "description": "Include per-sea unlocked/explored sector lists (default false; counts are always included)." }
                  }
                }
                """,
            Handler = (args, _) => Task.Run<object?>(() =>
            {
                var all = args.Bool("all_characters", false);
                var options = (Airships: args.Bool("include_airships", true), Loot: args.Bool("include_loot", true), Sectors: args.Bool("include_sectors", false));

                if (all)
                {
                    var list = tracker.All();
                    if (list.Count == 0) throw new ToolException(NoDataMessage);
                    return list.Select(s => Describe(s, tracker, options)).ToList();
                }

                if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded)
                    throw new ToolException("No character is logged in. Use all_characters=true to see saved snapshots.");
                var snapshot = tracker.Get(Svc.PlayerState.ContentId) ?? throw new ToolException(NoDataMessage);
                return Describe(snapshot, tracker, options);
            }),
        };
    }

    private const string NoDataMessage =
        "No submersible data recorded yet. Enter your free company's workshop once (the data is only sent by the game there); " +
        "the plugin saves it automatically. If you are already inside, open the voyage control panel and retry.";

    private static object Describe(WorkshopSnapshot s, WorkshopTracker tracker, (bool Airships, bool Loot, bool Sectors) options)
    {
        var now = DateTimeOffset.UtcNow;
        var live = tracker.IsInWorkshop && Svc.ClientState.IsLoggedIn && Svc.PlayerState.ContentId == s.ContentId;
        return new
        {
            character = s.Character,
            world = s.World,
            capturedAt = s.CapturedUtc,
            live,
            snapshotAgeMinutes = Math.Round((DateTime.UtcNow - s.CapturedUtc).TotalMinutes, 1),
            submersibles = s.Submersibles.Select(v => DescribeSubmersible(v, now, options.Loot)).ToList(),
            airships = options.Airships ? s.Airships.Select(v => DescribeAirship(v, now)).ToList() : null,
            sectors = DescribeSectors(s, options.Sectors),
        };
    }

    private static Dictionary<string, object?> DescribeSubmersible(VesselSnapshot v, DateTimeOffset now, bool includeLoot)
    {
        var exploration = Svc.Data.GetExcelSheet<SubmarineExploration>();
        var parts = new[] { ("hull", v.Hull), ("stern", v.Stern), ("bow", v.Bow), ("bridge", v.Bridge) }
            .Select(p => (Slot: p.Item1, Part: ResolvePart(p.Item2), RawId: p.Item2))
            .ToList();

        var build = parts.All(p => p.Part is not null && ClassLetters.ContainsKey(p.Part.Value.Row.Class))
            ? string.Concat(parts.Select(p => ClassLetters[p.Part!.Value.Row.Class]))
            : null;

        var route = v.Points.Select(p => exploration.GetRowOrDefault(p)).ToList();
        var d = new Dictionary<string, object?>
        {
            ["name"] = v.Name,
            ["rank"] = v.Rank,
            ["exp"] = v.Exp,
            ["nextRankExp"] = v.NextLevelExp,
            ["expToNextRank"] = v.NextLevelExp > v.Exp ? v.NextLevelExp - v.Exp : 0,
            ["build"] = build,
            ["parts"] = parts.ToDictionary(p => p.Slot, p => (object?)(p.Part is { } part
                ? new { partId = part.Row.RowId, itemId = part.ItemId, name = part.Name, partRank = part.Row.Rank }
                : new { partId = (uint)p.RawId, itemId = (uint?)null, name = (string?)null, partRank = (byte?)null })),
            ["stats"] = Stats(v),
            ["voyage"] = Voyage(v, now, route.Count == 0 ? null : new
            {
                sea = route.FirstOrDefault(r => r is not null) is { } first ? Excel.Name(first.Map) : null,
                route = string.Concat(route.Select(r => r?.Location.ExtractText() ?? "?")),
                sectors = route.Select((r, i) => new
                {
                    point = v.Points[i],
                    code = r?.Location.ExtractText(),
                    name = r?.Destination.ExtractText(),
                    rankRequired = r?.RankReq,
                    stars = r?.Stars,
                    exp = r?.ExpReward,
                }).ToList(),
                totalSectorExp = route.Sum(r => (long)(r?.ExpReward ?? 0)),
            }),
            ["registered"] = v.RegisterTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(v.RegisterTime) : null,
        };

        if (includeLoot && v.Loot.Count > 0)
        {
            d["lastVoyageLoot"] = v.Loot.Select(l => new
            {
                sector = exploration.GetRowOrDefault(l.Point) is { } r ? $"{r.Location.ExtractText()}: {r.Destination.ExtractText()}" : l.Point.ToString(),
                exp = l.ExpGained,
                primary = l.ItemPrimary != 0 ? new { itemId = l.ItemPrimary, name = InventoryTools.ItemName(l.ItemPrimary), quantity = l.CountPrimary, hq = l.HqPrimary } : null,
                additional = l.ItemAdditional != 0 ? new { itemId = l.ItemAdditional, name = InventoryTools.ItemName(l.ItemAdditional), quantity = l.CountAdditional, hq = l.HqAdditional } : null,
                doubleDip = l.DoubleDip,
                firstExploration = l.FirstExploration,
                unlockedSector = l.UnlockedPoint != 0 && exploration.GetRowOrDefault(l.UnlockedPoint) is { } u ? u.Destination.ExtractText() : null,
            }).ToList();
        }
        return d;
    }

    private static object DescribeAirship(VesselSnapshot v, DateTimeOffset now)
    {
        return new
        {
            name = v.Name,
            rank = v.Rank,
            exp = v.Exp,
            nextRankExp = v.NextLevelExp,
            expToNextRank = v.NextLevelExp > v.Exp ? v.NextLevelExp - v.Exp : 0,
            partIds = new { hull = v.Hull, stern = v.Stern, bow = v.Bow, bridge = v.Bridge },
            stats = StatNames.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => (int)(x.i < v.Base.Length ? v.Base[x.i] : 0)),
            voyage = Voyage(v, now, null),
        };
    }

    private static object Voyage(VesselSnapshot v, DateTimeOffset now, object? route)
    {
        if (v.ReturnTime == 0) return new { status = "Idle" };
        var ret = DateTimeOffset.FromUnixTimeSeconds(v.ReturnTime);
        var remaining = ret - now;
        return new
        {
            status = remaining > TimeSpan.Zero ? "Voyaging" : "Returned",
            returnsAt = ret,
            returnsAtLocal = ret.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            remaining = remaining > TimeSpan.Zero ? $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m" : null,
            readyToCollect = remaining <= TimeSpan.Zero,
            current = route,
        };
    }

    private static Dictionary<string, object> Stats(VesselSnapshot v)
    {
        var stats = new Dictionary<string, object>();
        for (var i = 0; i < StatNames.Length; i++)
        {
            var b = i < v.Base.Length ? v.Base[i] : 0;
            var bonus = i < v.Bonus.Length ? v.Bonus[i] : 0;
            stats[StatNames[i]] = new { total = b + bonus, @base = b, bonus };
        }
        return stats;
    }

    private static object DescribeSectors(WorkshopSnapshot s, bool listSectors)
    {
        var exploration = Svc.Data.GetExcelSheet<SubmarineExploration>();
        var unlocked = s.UnlockedSectors.ToHashSet();
        var explored = s.ExploredSectors.ToHashSet();
        return exploration
            .Where(r => r.RowId != 0 && !r.StartingPoint && r.Map.RowId != 0)
            .GroupBy(r => r.Map.RowId)
            .OrderBy(g => g.Key)
            .Select(g => new
            {
                sea = Excel.Name(g.First().Map),
                total = g.Count(),
                unlocked = g.Count(r => unlocked.Contains((byte)r.RowId)),
                explored = g.Count(r => explored.Contains((byte)r.RowId)),
                sectors = listSectors
                    ? g.Select(r => new
                    {
                        point = r.RowId,
                        code = r.Location.ExtractText(),
                        name = r.Destination.ExtractText(),
                        rankRequired = r.RankReq,
                        unlocked = unlocked.Contains((byte)r.RowId),
                        explored = explored.Contains((byte)r.RowId),
                    }).ToList()
                    : null,
            })
            .ToList();
    }

    private readonly record struct PartInfo(SubmarinePart Row, uint? ItemId, string? Name);

    /// <summary>The workshop stores SubmarinePart row ids; accept item ids too, just in case.</summary>
    private static PartInfo? ResolvePart(ushort id)
    {
        if (id == 0) return null;
        var parts = Svc.Data.GetExcelSheet<SubmarinePart>();
        if (parts.GetRowOrDefault(id) is { } row && row.Class != 0)
        {
            var item = PartItems.Value.TryGetValue(id, out var it) ? it : (Item?)null;
            return new PartInfo(row, item?.RowId, item?.Name.ExtractText());
        }
        if (Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(id) is { } asItem && parts.GetRowOrDefault(asItem.AdditionalData.RowId) is { } viaItem)
            return new PartInfo(viaItem, asItem.RowId, asItem.Name.ExtractText());
        return null;
    }
}
