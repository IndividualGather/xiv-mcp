using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivMcp.Fishing;

/// <summary>How hard the line moves at a bite: "!", "!!" or "!!!" over the character's head.</summary>
public enum Tug { Light, Medium, Heavy }

/// <summary>The hookset a fish needs while Patience is active.</summary>
public enum Hookset { Precision, Powerful }

public static class Tugs
{
    public static string Signs(Tug t) => t switch { Tug.Light => "!", Tug.Medium => "!!", _ => "!!!" };

    public static string Describe(Tug t) => $"{t.ToString().ToLowerInvariant()} ({Signs(t)})";

    /// <summary>Teamcraft's numbering: 0 medium, 1 heavy, 2 light.</summary>
    public static Tug? FromTeamcraft(int? value) => value switch { 0 => Tug.Medium, 1 => Tug.Heavy, 2 => Tug.Light, _ => null };

    public static Tug? FromName(string? name) => name?.ToLowerInvariant() switch
    {
        "light" => Tug.Light, "medium" => Tug.Medium, "heavy" => Tug.Heavy, _ => null,
    };

    /// <summary>Teamcraft's numbering: 1 powerful, 2 precision.</summary>
    public static Hookset? HooksetFromTeamcraft(int? value) => value switch { 1 => Hookset.Powerful, 2 => Hookset.Precision, _ => null };

    public static Hookset? HooksetFromName(string? name) => name?.ToLowerInvariant() switch
    {
        "precision" => Hookset.Precision, "powerful" => Hookset.Powerful, _ => null,
    };
}

/// <summary>
/// A fish as the community fish tracker (Carbuncle Plushy's FFX|V Fish Tracker) knows it: when it bites, the best way to catch it,
/// and what it needs. It covers the fish worth tracking (timed, weathered, big fish); <see cref="FishSource"/> covers all of them.
/// </summary>
public sealed record TrackerFish(
    uint Id, uint Spot, FishConditions Conditions, IReadOnlyList<uint> CatchPath, IReadOnlyList<(uint Fish, int Count)> Predators,
    int? IntuitionSeconds, uint? Folklore, bool FishEyes, bool BigFish, bool? Snagging, string? Lure, Hookset? Hookset, Tug? Tug,
    bool Collectable, double Patch);

/// <summary>One way to catch a fish, from Teamcraft's data: at a spot, with a bait (an item or a fish to mooch with).</summary>
public sealed record FishSource(uint Spot, uint Bait, Tug? Tug, Hookset? Hookset, bool Snagging, int MinGathering);

public static class FishTrackerData
{
    /// <summary>The fish of the tracker's data file (a JavaScript file, <c>const DATA = { FISH: {...}, ... }</c>).</summary>
    public static IReadOnlyDictionary<uint, TrackerFish> ParseFish(string js)
    {
        var fish = new Dictionary<uint, TrackerFish>();
        if (Section(js, "FISH") is not JsonObject all) return fish;
        foreach (var (key, node) in all)
        {
            if (!uint.TryParse(key, out var id) || node is not JsonObject f) continue;
            var start = Num(f["startHour"]) ?? 0;
            var end = Num(f["endHour"]) ?? 24;
            fish[id] = new TrackerFish(
                id,
                (uint)(Num(f["location"]) ?? 0),
                new FishConditions(start, end, Ids(f["previousWeatherSet"]), Ids(f["weatherSet"])),
                Ids(f["bestCatchPath"]),
                (f["predators"] as JsonArray ?? []).OfType<JsonArray>()
                    .Where(p => p.Count >= 2).Select(p => ((uint)(Num(p[0]) ?? 0), (int)(Num(p[1]) ?? 0))).ToList(),
                Num(f["intuitionLength"]) is { } s ? (int)s : null,
                Num(f["folklore"]) is { } folk ? (uint)folk : null,
                Bool(f["fishEyes"]) == true,
                Bool(f["bigFish"]) == true,
                Bool(f["snagging"]),
                Text(f["lure"]),
                Tugs.HooksetFromName(Text(f["hookset"])),
                Tugs.FromName(Text(f["tug"])),
                f["collectable"] is not null && f["collectable"]!.GetValueKind() != JsonValueKind.Null,
                Num(f["patch"]) ?? 0);
        }
        return fish;
    }

    /// <summary>The names of the folklore tomes, by id.</summary>
    public static IReadOnlyDictionary<uint, string> ParseFolklore(string js) =>
        Section(js, "FOLKLORE") is JsonObject all
            ? all.Where(kv => uint.TryParse(kv.Key, out _) && kv.Value is JsonObject)
                 .ToDictionary(kv => uint.Parse(kv.Key), kv => Text(kv.Value!["name_en"]) ?? $"Folklore {kv.Key}")
            : new Dictionary<uint, string>();

    /// <summary>One top-level table of the data file, such as <c>FISH</c>, as JSON.</summary>
    internal static JsonNode? Section(string js, string name)
    {
        var at = js.IndexOf($"{name}:", StringComparison.Ordinal);
        if (at < 0) return null;
        var open = js.IndexOf('{', at);
        if (open < 0) return null;
        // Find the matching brace, skipping strings.
        var depth = 0;
        var inString = false;
        for (var i = open; i < js.Length; i++)
        {
            var c = js[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return JsonNode.Parse(js.AsSpan(open, i - open + 1).ToString());
        }
        return null;
    }

    internal static double? Num(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    internal static bool? Bool(JsonNode? n) => n is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : null;

    internal static string? Text(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    private static uint[] Ids(JsonNode? n) => (n as JsonArray ?? []).Select(Num).Where(x => x is not null).Select(x => (uint)x!.Value).ToArray();
}

/// <summary>A fishing spot from Teamcraft's data: its map coordinates (as the in-game map shows them), level and fish.</summary>
public sealed record FishingSpotInfo(uint Id, double MapX, double MapY, int Level, IReadOnlyList<uint> Fish);

public static class TeamcraftFishData
{
    /// <summary>Teamcraft's <c>fishing-sources.json</c>: per fish, the spots and baits it is caught with.</summary>
    public static IReadOnlyDictionary<uint, IReadOnlyList<FishSource>> ParseSources(string json)
    {
        var result = new Dictionary<uint, IReadOnlyList<FishSource>>();
        if (JsonNode.Parse(json) is not JsonObject all) return result;
        foreach (var (key, node) in all)
        {
            if (!uint.TryParse(key, out var id) || node is not JsonArray list) continue;
            result[id] = list.OfType<JsonObject>().Select(s => new FishSource(
                (uint)(FishTrackerData.Num(s["spot"]) ?? 0),
                (uint)(FishTrackerData.Num(s["bait"]) ?? 0),
                Tugs.FromTeamcraft((int?)FishTrackerData.Num(s["tug"])),
                Tugs.HooksetFromTeamcraft((int?)FishTrackerData.Num(s["hookset"])),
                FishTrackerData.Bool(s["snagging"]) == true,
                (int)(FishTrackerData.Num(s["minGathering"]) ?? 0))).ToList();
        }
        return result;
    }

    /// <summary>Teamcraft's <c>fishing-spots.json</c>: where each spot is on its map, and the fish that bite there.</summary>
    public static IReadOnlyDictionary<uint, FishingSpotInfo> ParseSpots(string json)
    {
        var result = new Dictionary<uint, FishingSpotInfo>();
        if (JsonNode.Parse(json) is not JsonArray all) return result;
        foreach (var s in all.OfType<JsonObject>())
            if (FishTrackerData.Num(s["id"]) is { } id)
                result[(uint)id] = new FishingSpotInfo((uint)id,
                    FishTrackerData.Num(s["coords"]?["x"]) ?? 0, FishTrackerData.Num(s["coords"]?["y"]) ?? 0,
                    (int)(FishTrackerData.Num(s["level"]) ?? 0),
                    (s["fishes"] as JsonArray ?? []).Select(FishTrackerData.Num).Where(x => x is not null).Select(x => (uint)x!.Value).ToList());
        return result;
    }
}
