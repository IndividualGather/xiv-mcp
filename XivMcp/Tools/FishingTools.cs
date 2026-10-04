using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using XivMcp.Fishing;
using WeatherRate = XivMcp.Fishing.WeatherRate;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Fishing without any plugin: where, when and how a fish bites (from the community fish tracker and FFXIV Teamcraft, module
/// "Online lookups"), and the weather forecast (computed from the game's own data, module "Game data").
/// </summary>
internal static class FishingTools
{
    private const string Credits = "Fish data: FFX|V Fish Tracker (Carbuncle Plushy) and FFXIV Teamcraft, including the bite times players report to Teamcraft.";

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        yield return new McpTool
        {
            Name = "find_fish",
            Description = "How to catch a fish: where it bites (fishing spot, zone, map coordinates, level), the bait and any fish to mooch " +
                          "with on the way, the tug (! / !! / !!!) and hookset of each cast, how many seconds after casting it bites, " +
                          "the Eorzean time and the weather it needs (and the weather before), its next windows in real time, Fisher's " +
                          "Intuition, folklore, lures and snagging, other fish that bite at the spot with the same bait, and step-by-step " +
                          "advice with the abilities to use and a counting macro when timing tells the bites apart. Bait is bought with " +
                          "buy_item; get_weather_forecast shows a zone's weather. Requires 'Online lookups' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "fish": { "type": "string", "description": "The fish's name or item id." },
                    "spot": { "type": "string", "description": "A fishing spot's name or id, if the fish bites at several (default: the best one)." },
                    "windows": { "type": "integer", "minimum": 1, "maximum": 10, "description": "How many of its next windows to list (default 3)." }
                  },
                  "required": ["fish"]
                }
                """,
            Handler = async (args, ct) =>
            {
                ItemSourceTools.RequireOnline(config);
                var fish = await Task.Run(() => ResolveFish(args.String("fish") ?? throw new ToolException("'fish' is required.")), ct).ConfigureAwait(false);
                var data = await FishingSources.Get(ct).ConfigureAwait(false);
                var spotArg = args.String("spot");
                var spot = spotArg is null ? null : (uint?)await Task.Run(() => ResolveSpot(spotArg, fish, data), ct).ConfigureAwait(false);
                var guide = await Guide(fish, spot, data, ct).ConfigureAwait(false);
                var baits = Baits(guide);
                var bags = await Game.Run(() => baits.ToDictionary(b => b, Items.CountInBags)).ConfigureAwait(false);
                return await Task.Run(() => Describe(guide, data, args.Int("windows", 3, 1, 10), bags), ct).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "get_weather_forecast",
            Description = "The weather forecast of a zone (default: the current one), computed from the game's own data like the game does: " +
                          "the current Eorzean time, and the next weather periods (each lasts 8 Eorzean hours, about 23 real minutes) with " +
                          "their real start time and the weather before. With 'weather', also when that weather comes next. For a fish's " +
                          "windows (time and weather together), use find_fish.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "zone": { "type": "string", "description": "Zone name or territory id (default: the current zone)." },
                    "count": { "type": "integer", "minimum": 1, "maximum": 24, "description": "How many periods to list (default 6)." },
                    "weather": { "type": "string", "description": "A weather to look for, such as \"Rain\": lists its next periods." }
                  }
                }
                """,
            Handler = (args, _) => Game.Run<object?>(() =>
            {
                var (territory, _) = MapTools.ResolveZone(args.String("zone"));
                var rates = Rates(territory.RowId);
                if (rates.Count == 0) throw new ToolException($"{Excel.Name(territory.PlaceName)} has no weather of its own.");
                var now = DateTimeOffset.UtcNow;
                var periods = WeatherForecast.Periods(rates, now, args.Int("count", 6, 1, 24));
                object? next = null;
                if (args.String("weather") is { } wanted)
                {
                    var match = rates.Select(r => r.Weather).Distinct().FirstOrDefault(w => Game.Matches(WeatherName(w), wanted));
                    if (match == 0)
                        throw new ToolException($"{Excel.Name(territory.PlaceName)} never has '{wanted}'. Its weathers: {string.Join(", ", rates.Select(r => WeatherName(r.Weather)).Distinct())}.");
                    next = WeatherForecast.Periods(rates, now, 300).Where(p => p.Weather == match).Take(3).Select(p => Period(p, now)).ToList();
                }
                return new
                {
                    zone = new { id = territory.RowId, name = Excel.Name(territory.PlaceName) },
                    eorzeaTime = EorzeaTime.Format(EorzeaTime.Hour(now)),
                    chances = rates.Where(r => r.Rate > 0).Select(r => new { weather = WeatherName(r.Weather), percent = r.Rate }),
                    periods = periods.Select(p => Period(p, now)),
                    next,
                };
            }),
        };
    }

    // ------------------------------------------------------------------ building a guide

    /// <summary>The guide for a fish at a spot, with the bite times of every cast on the way.</summary>
    internal static async Task<FishGuide> Guide(uint fish, uint? spot, FishingSources.Data data, CancellationToken ct)
    {
        var first = FishGuides.Build(fish, spot, data.Tracker, data.Sources, data.Spots, (_, _, _) => null)
                    ?? throw new ToolException(spot is null
                        ? $"The fishing data does not say how {Items.Name(fish)} is caught. It may be caught by spearfishing or only in ocean fishing."
                        : $"{Items.Name(fish)} is not known to bite at {SpotName(spot.Value)}.");
        var bites = new Dictionary<(uint Fish, uint Bait), BiteWindow>();
        foreach (var bait in first.Path.Select(s => s.Bait).Distinct())
            foreach (var (f, w) in await FishingSources.BiteTimes(first.Spot, bait, ct).ConfigureAwait(false))
                bites[(f, bait)] = w;
        return FishGuides.Build(fish, first.Spot, data.Tracker, data.Sources, data.Spots, (f, _, bait) => bites.GetValueOrDefault((f, bait)))!;
    }

    /// <summary>The bait items a guide casts with (not the fish it mooches with), the fish for Fisher's Intuition included.</summary>
    internal static IReadOnlyList<uint> Baits(FishGuide g) =>
        g.Path.Concat(g.PredatorCasts).Where(s => !s.Mooch).Select(s => s.Bait).Distinct().ToList();

    internal static object Describe(FishGuide g, FishingSources.Data data, int windows, IReadOnlyDictionary<uint, int> bags)
    {
        var spot = Svc.Data.GetExcelSheet<FishingSpot>().GetRowOrDefault(g.Spot);
        var territory = spot?.TerritoryType.RowId ?? 0;
        data.Spots.TryGetValue(g.Spot, out var info);
        var rates = Rates(territory);
        var now = DateTimeOffset.UtcNow;
        var next = rates.Count == 0 ? [] : FishWindows.Next(g.Conditions, rates, now, windows, TimeSpan.FromDays(30));
        var advice = FishingAdvice.For(g, Items.Name, WeatherName, f => data.Folklore.GetValueOrDefault(f) ?? Items.Name(f));
        var tracker = data.Tracker.GetValueOrDefault(g.Fish);

        return new
        {
            fish = new { id = g.Fish, name = Items.Name(g.Fish), bigFish = g.BigFish, patch = tracker?.Patch },
            spot = new
            {
                id = g.Spot,
                name = SpotName(g.Spot),
                zone = territory == 0 ? null : new { id = territory, name = NavigationTools.TerritoryName(territory) },
                map = info is null ? null : new { x = info.MapX, y = info.MapY },
                level = info?.Level,
            },
            otherSpots = FishGuides.Spots(g.Fish, data.Tracker, data.Sources).Where(s => s != g.Spot)
                .Select(s => new { id = s, name = SpotName(s), zone = Zone(s) }),
            bait = Baits(g).Select(b => new
            {
                name = Items.Name(b),
                inBags = bags.GetValueOrDefault(b),
                soldForGil = GilShopItems.Value.Contains(b),
                hint = GilShopItems.Value.Contains(b) ? "buy_item buys it from a vendor." : "Not sold for gil; see get_item_sources.",
            }),
            casts = g.Path.Select(s => new
            {
                castWith = Items.Name(s.Bait),
                mooch = s.Mooch,
                catches = Items.Name(s.Fish),
                tug = s.Tug is { } t ? Tugs.Describe(t) : null,
                hookset = s.Hookset?.ToString(),
                bitesAfterSeconds = s.Bite is { } b ? new { from = b.Min, to = b.Max, usually = b.Median, reports = b.Samples } : null,
            }),
            when = FishingAdvice.When(g.Conditions, WeatherName) ?? "any time, in any weather",
            nextWindows = g.Conditions.AnyTime ? null : next.Select(w => Window(w, now)),
            needs = new
            {
                folklore = g.Folklore is { } f ? data.Folklore.GetValueOrDefault(f) : null,
                fishersIntuition = g.Predators.Count == 0 ? null : new
                {
                    catchFirst = g.Predators.Select(p => new { fish = Items.Name(p.Fish), count = p.Count }),
                    seconds = g.IntuitionSeconds,
                    casts = g.PredatorCasts.Select(p => new
                    {
                        fish = Items.Name(p.Fish), castWith = Items.Name(p.Bait), mooch = p.Mooch,
                        tug = p.Tug is { } t ? Tugs.Describe(t) : null, hookset = p.Hookset?.ToString(),
                    }),
                },
                lure = g.Lure is { } l ? $"{l} Lure" : null,
                snagging = g.Snagging,
                collectable = g.Collectable,
            },
            alsoBitesHere = g.Rivals.Select(r => new
            {
                fish = Items.Name(r.Fish),
                tug = r.Tug is { } t ? Tugs.Describe(t) : null,
                bitesAfterSeconds = r.Bite is { } b ? new { from = b.Min, to = b.Max } : null,
            }),
            advice = advice.Steps,
            abilities = advice.Abilities,
            countingMacro = advice.CountingMacro,
            source = g.FromTracker ? Credits : Credits + " This fish is not in the fish tracker, so its time and weather are not known; most such fish bite at any time.",
        };
    }

    // ------------------------------------------------------------------ game data

    internal static bool SoldForGil(uint item) => GilShopItems.Value.Contains(item);

    /// <summary>Every item some gil vendor sells.</summary>
    private static readonly Lazy<HashSet<uint>> GilShopItems = new(() =>
        Svc.Data.GetSubrowExcelSheet<GilShopItem>().SelectMany(rows => rows).Select(r => r.Item.RowId).Where(id => id != 0).ToHashSet());

    /// <summary>A fish by name or item id: fish from the fishing log first.</summary>
    internal static uint ResolveFish(string query)
    {
        var fish = Svc.Data.GetExcelSheet<FishParameter>().Select(f => f.Item.RowId).Where(id => id != 0).ToHashSet();
        if (uint.TryParse(query, out var id)) return fish.Contains(id) ? id : throw new ToolException($"Item {id} is not a fish.");
        var names = fish.Select(f => (Id: f, Name: Items.Name(f))).ToList();
        var exact = names.FirstOrDefault(n => n.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact.Id != 0) return exact.Id;
        var partial = names.Where(n => Game.Matches(n.Name, query)).Take(9).ToList();
        return partial.Count switch
        {
            1 => partial[0].Id,
            0 => throw new ToolException($"No fish matches '{query}'."),
            _ => throw new ToolException($"'{query}' matches several fish: {string.Join(", ", partial.Take(8).Select(p => p.Name))}. Be more specific."),
        };
    }

    private static uint ResolveSpot(string query, uint fish, FishingSources.Data data)
    {
        var spots = FishGuides.Spots(fish, data.Tracker, data.Sources);
        if (uint.TryParse(query, out var id)) return id;
        return spots.FirstOrDefault(s => Game.Matches(SpotName(s), query)) is var match && match != 0
            ? match
            : throw new ToolException($"{Items.Name(fish)} does not bite at '{query}'. It bites at: {string.Join(", ", spots.Select(SpotName))}.");
    }

    /// <summary>A fishing spot by id or name (exact first, then a unique partial match).</summary>
    internal static uint ResolveSpotName(string query)
    {
        var sheet = Svc.Data.GetExcelSheet<FishingSpot>();
        if (uint.TryParse(query, out var id)) return sheet.GetRowOrDefault(id) is not null ? id : throw new ToolException($"No fishing spot {id}.");
        var spots = sheet.Where(s => s.TerritoryType.RowId != 0).Select(s => (s.RowId, Name: Excel.Name(s.PlaceName) ?? "")).Where(s => s.Name.Length > 0).ToList();
        var exact = spots.FirstOrDefault(s => s.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact.RowId != 0) return exact.RowId;
        var partial = spots.Where(s => Game.Matches(s.Name, query)).Take(9).ToList();
        return partial.Count switch
        {
            1 => partial[0].RowId,
            0 => throw new ToolException($"No fishing spot matches '{query}'."),
            _ => throw new ToolException($"'{query}' matches several fishing spots: {string.Join(", ", partial.Take(8).Select(p => p.Name))}. Be more specific, or use the id from find_fish."),
        };
    }

    internal static string SpotName(uint spot) =>
        Svc.Data.GetExcelSheet<FishingSpot>().GetRowOrDefault(spot) is { } s ? Excel.Name(s.PlaceName) ?? $"spot {spot}" : $"spot {spot}";

    private static string? Zone(uint spot) =>
        Svc.Data.GetExcelSheet<FishingSpot>().GetRowOrDefault(spot) is { } s ? NavigationTools.TerritoryName(s.TerritoryType.RowId) : null;

    /// <summary>A zone's weather chances, from the WeatherRate sheet.</summary>
    internal static IReadOnlyList<WeatherRate> Rates(uint territory)
    {
        if (Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territory) is not { } t) return [];
        if (Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.WeatherRate>().GetRowOrDefault(t.WeatherRate.RowId) is not { } row) return [];
        var list = new List<WeatherRate>();
        for (var i = 0; i < row.Weather.Count; i++)
            if (row.Rate[i] > 0) list.Add(new WeatherRate(row.Weather[i].RowId, row.Rate[i]));
        return list;
    }

    internal static string WeatherName(uint weather) => Excel.NameOf<Weather>(weather) ?? $"weather {weather}";

    private static object Period(WeatherPeriod p, DateTimeOffset now) => new
    {
        starts = p.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        inMinutes = Math.Max(0, (int)Math.Round((p.Start - now).TotalMinutes)),
        eorzeaTime = EorzeaTime.Format(EorzeaTime.Hour(p.Start)),
        weather = WeatherName(p.Weather),
        after = WeatherName(p.Previous),
    };

    private static object Window(FishWindow w, DateTimeOffset now) => new
    {
        starts = w.Start.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        ends = w.End.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
        open = w.Start <= now,
        inMinutes = Math.Max(0, (int)Math.Round((w.Start - now).TotalMinutes)),
        minutes = Math.Round(w.Length.TotalMinutes, 1),
    };
}
