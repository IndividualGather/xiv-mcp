using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace XivMcp.OceanFishing;

/// <summary>Time of day at an ocean fishing stop (IKDTimeDefine rows 1-3).</summary>
public enum OceanTime { Unknown = 0, Day = 1, Sunset = 2, Night = 3 }

/// <summary>A fish at an ocean fishing stop, as Distant Seas' community data describes it.</summary>
/// <param name="BestBait">The bait it bites best on (item id); null for fish caught by mooching or under intuition only.</param>
/// <param name="Points">Average points per catch.</param>
/// <param name="TriggersSpectral">Catching it can start a spectral current.</param>
/// <param name="Intuition">It needs Fisher's Intuition (the rare fish of the stop).</param>
/// <param name="Times">The times of day it bites at (empty: any).</param>
public sealed record OceanFish(uint ItemId, uint? BestBait, int Points, int Stars, bool TriggersSpectral, bool Intuition, IReadOnlySet<OceanTime> Times)
{
    public bool BitesAt(OceanTime time) => Times.Count == 0 || time == OceanTime.Unknown || Times.Contains(time);
}

/// <summary>One stop's water, normal or during a spectral current; <paramref name="SpotId"/> is the game's IKDSpot row.</summary>
public sealed record OceanSpotData(uint SpotId, bool Spectral, IReadOnlyList<OceanFish> Fish);

/// <summary>What is worth going for at a stop: the normal water's best fish (spectral triggers first) and the spectral current's.</summary>
public sealed record OceanHighlights(IReadOnlyList<OceanFish> Normal, IReadOnlyList<OceanFish> Spectral);

/// <summary>
/// Reads Distant Seas' route data (Data/indigo.json and Data/ruby.json next to its plugin), generated from the community ocean
/// fishing spreadsheet. Its spot names are the game's IKDSpot rows in order.
/// </summary>
public static class OceanFishData
{
    private static readonly string[] SpotNames =
    [
        "Unknown", "GaladionBay", "SouthernMerlthor", "NorthernMerlthor", "RhotanoSea", "CieldalaesMargin", "BloodbrineSea", "RothlytSound",
        "SirensongSea", "KuganeCoast", "RubySea", "OneRiver", "UnnamedIsland", "Thavnair",
    ];

    public static IReadOnlyList<OceanSpotData> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var spots = new List<OceanSpotData>();
        foreach (var spot in doc.RootElement.EnumerateArray())
        {
            var id = Array.IndexOf(SpotNames, spot.GetProperty("Type").GetString());
            if (id <= 0) continue;
            var fish = spot.GetProperty("Fish").EnumerateArray().Select(ParseFish).ToList();
            spots.Add(new OceanSpotData((uint)id, spot.GetProperty("IsSpectral").GetBoolean(), fish));
        }
        return spots;
    }

    private static OceanFish ParseFish(JsonElement f)
    {
        uint? best = null;
        if (f.TryGetProperty("BiteTimes", out var bites) && bites.ValueKind == JsonValueKind.Object)
            foreach (var bite in bites.EnumerateObject())
                if (bite.Value.TryGetProperty("CellType", out var cell) && cell.GetString() == "BestOrRequired" && uint.TryParse(bite.Name, out var bait))
                {
                    best = bait;
                    break;
                }
        var times = new HashSet<OceanTime>();
        if (f.TryGetProperty("TimeAvailability", out var availability) && availability.ValueKind == JsonValueKind.Object)
        {
            foreach (var t in availability.EnumerateObject())
                if (t.Value.ValueKind == JsonValueKind.True && Enum.TryParse<OceanTime>(t.Name, out var time)) times.Add(time);
        }
        var intuition = f.TryGetProperty("Intuition", out var i) && i.ValueKind == JsonValueKind.Object
                        || f.TryGetProperty("CellType", out var c) && c.GetString() == "Intuition";
        return new OceanFish(
            f.GetProperty("ItemId").GetUInt32(), best,
            f.TryGetProperty("AveragePoints", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0,
            f.TryGetProperty("Stars", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0,
            f.TryGetProperty("CanCauseSpectral", out var sp) && sp.ValueKind == JsonValueKind.True,
            intuition, times);
    }

    /// <summary>
    /// The fish worth going for at a stop at a time of day: in the normal water the spectral triggers, then the best scorers; in the
    /// spectral current the best scorers. At most <paramref name="top"/> each, beyond the triggers.
    /// </summary>
    public static OceanHighlights Highlights(IReadOnlyList<OceanSpotData> spots, uint spotId, OceanTime time, int top)
    {
        IEnumerable<OceanFish> At(bool spectral) =>
            spots.Where(s => s.SpotId == spotId && s.Spectral == spectral).SelectMany(s => s.Fish).Where(f => f.BitesAt(time));
        var normal = At(false).Where(f => f.TriggersSpectral)
            .Concat(At(false).Where(f => !f.TriggersSpectral).OrderByDescending(f => f.Points).Take(top))
            .ToList();
        var spectral = At(true).OrderByDescending(f => f.Points).Take(top).ToList();
        return new OceanHighlights(normal, spectral);
    }
}
