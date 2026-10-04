using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Fishing;

/// <summary>One cast of a catch: cast with <see cref="Bait"/> (an item, or a fish to mooch with) and hook <see cref="Fish"/>.</summary>
public sealed record CatchStep(uint Bait, uint Fish, Tug? Tug, Hookset? Hookset, BiteWindow? Bite)
{
    /// <summary>True when <see cref="Bait"/> is a fish just caught, used with Mooch.</summary>
    public bool Mooch { get; init; }
}

/// <summary>Another fish that bites at the same spot with the same bait.</summary>
public sealed record Rival(uint Fish, Tug? Tug, BiteWindow? Bite);

/// <summary>Everything about catching one fish at one spot: the casts, when it bites, what it needs, and what else bites there.</summary>
public sealed record FishGuide(
    uint Fish, uint Spot, IReadOnlyList<CatchStep> Path, FishConditions Conditions, IReadOnlyList<(uint Fish, int Count)> Predators,
    int? IntuitionSeconds, uint? Folklore, bool FishEyes, bool BigFish, bool Snagging, string? Lure, bool Collectable,
    IReadOnlyList<Rival> Rivals, bool FromTracker)
{
    /// <summary>
    /// How the fish for Fisher's Intuition (<see cref="Predators"/>) are caught at this spot, where the data knows it. They have to be
    /// caught before the fish bites.
    /// </summary>
    public IReadOnlyList<CatchStep> PredatorCasts { get; init; } = [];

    public CatchStep Last => Path[^1];
    public uint FirstBait => Path[0].Bait;
    public IReadOnlyList<uint> Mooches => Path.Skip(1).Select(s => s.Bait).ToList();
}

public static class FishGuides
{
    /// <summary>The spots a fish bites at: the tracker's spot first, then Teamcraft's.</summary>
    public static IReadOnlyList<uint> Spots(uint fish, IReadOnlyDictionary<uint, TrackerFish> tracker, IReadOnlyDictionary<uint, IReadOnlyList<FishSource>> sources)
    {
        var spots = new List<uint>();
        if (tracker.TryGetValue(fish, out var t) && t.Spot != 0) spots.Add(t.Spot);
        if (sources.TryGetValue(fish, out var s)) spots.AddRange(s.Select(x => x.Spot).Where(x => x != 0));
        return spots.Distinct().ToList();
    }

    /// <summary>
    /// The guide for a fish, at <paramref name="spot"/> or its best spot, or null if neither source knows how it is caught there.
    /// <paramref name="bite"/> looks up bite times (fish, spot, bait); <paramref name="spots"/> lists what else bites at a spot.
    /// </summary>
    public static FishGuide? Build(
        uint fish, uint? spot, IReadOnlyDictionary<uint, TrackerFish> tracker, IReadOnlyDictionary<uint, IReadOnlyList<FishSource>> sources,
        IReadOnlyDictionary<uint, FishingSpotInfo> spots, Func<uint, uint, uint, BiteWindow?> bite)
    {
        tracker.TryGetValue(fish, out var t);
        var at = spot ?? Spots(fish, tracker, sources).FirstOrDefault();
        if (at == 0) return null;

        // The tracker's best path holds for its own spot; elsewhere (or for fish it does not track), follow Teamcraft's baits.
        IReadOnlyList<uint>? path = t is not null && t.Spot == at && t.CatchPath.Count > 0 ? [.. t.CatchPath, fish] : TeamcraftPath(fish, at, sources);
        if (path is null || path.Count < 2) return null;

        var steps = new List<CatchStep>();
        for (var i = 0; i + 1 < path.Count; i++)
        {
            var (castWith, hooked) = (path[i], path[i + 1]);
            var source = SourceAt(sources, hooked, at, castWith);
            var isTarget = hooked == fish;
            steps.Add(new CatchStep(castWith, hooked,
                (isTarget ? t?.Tug : null) ?? source?.Tug,
                (isTarget ? t?.Hookset : null) ?? source?.Hookset,
                bite(hooked, at, castWith)) { Mooch = i > 0 });
        }

        var last = steps[^1];
        var rivals = (spots.TryGetValue(at, out var here) ? here.Fish : [])
            .Where(f => f != fish)
            .Select(f => (Fish: f, Source: SourceAt(sources, f, at, last.Bait)))
            .Where(r => r.Source is not null && r.Source.Bait == last.Bait)
            .Select(r => new Rival(r.Fish, r.Source!.Tug, bite(r.Fish, at, last.Bait)))
            .ToList();

        var snag = t?.Snagging ?? SourceAt(sources, fish, at, last.Bait)?.Snagging ?? false;
        var predatorCasts = (t?.Predators ?? [])
            .Select(p => (p.Fish, Source: SourceAt(sources, p.Fish, at, null)))
            .Where(p => p.Source is not null && p.Source.Bait != 0)
            .Select(p => new CatchStep(p.Source!.Bait, p.Fish, p.Source.Tug, p.Source.Hookset, bite(p.Fish, at, p.Source.Bait)) { Mooch = sources.ContainsKey(p.Source.Bait) })
            .ToList();
        return new FishGuide(fish, at, steps, t?.Conditions ?? FishConditions.Always, t?.Predators ?? [], t?.IntuitionSeconds, t?.Folklore,
            t?.FishEyes ?? false, t?.BigFish ?? false, snag, t?.Lure, t?.Collectable ?? false, rivals, t is not null) { PredatorCasts = predatorCasts };
    }

    /// <summary>Bait first, the fish last: Teamcraft's bait for the fish at the spot, and for a mooch, how that fish is caught.</summary>
    private static List<uint>? TeamcraftPath(uint fish, uint spot, IReadOnlyDictionary<uint, IReadOnlyList<FishSource>> sources)
    {
        var path = new List<uint> { fish };
        var current = fish;
        for (var depth = 0; depth < 5; depth++)
        {
            var source = SourceAt(sources, current, spot, null);
            if (source is null || source.Bait == 0) return depth == 0 ? null : path;
            path.Insert(0, source.Bait);
            if (!sources.ContainsKey(source.Bait) || path.Count(x => x == source.Bait) > 1) return path; // a bait item, not a fish
            current = source.Bait;
        }
        return path;
    }

    private static FishSource? SourceAt(IReadOnlyDictionary<uint, IReadOnlyList<FishSource>> sources, uint fish, uint spot, uint? bait) =>
        sources.TryGetValue(fish, out var list)
            ? list.FirstOrDefault(s => s.Spot == spot && (bait is null || s.Bait == bait)) ?? list.FirstOrDefault(s => s.Spot == spot)
            : null;
}
