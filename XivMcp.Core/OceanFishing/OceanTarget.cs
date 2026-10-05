using System.Collections.Generic;

namespace XivMcp.OceanFishing;

/// <summary>What has been done so far: voyages finished, points scored over all of them, and fish caught by item id.</summary>
public sealed record OceanProgress(int Voyages, int Points, IReadOnlyDictionary<uint, int> Caught);

/// <summary>
/// When an ocean fishing job stops: after a number of voyages, a points total, or a number of one fish, whichever comes first.
/// <paramref name="MaxVoyages"/> caps a hunt that may take long (a rare fish). Without any target, one voyage.
/// </summary>
public sealed record OceanTarget(int? Voyages = null, int? Points = null, uint? Fish = null, int FishCount = 1, int? MaxVoyages = null)
{
    public bool Reached(OceanProgress p)
    {
        if (Voyages is null && Points is null && Fish is null) return p.Voyages >= 1;
        if (MaxVoyages is { } max && p.Voyages >= max) return true;
        if (Voyages is { } v && p.Voyages >= v) return true;
        if (Points is { } points && p.Points >= points) return true;
        return Fish is { } fish && p.Caught.TryGetValue(fish, out var n) && n >= FishCount;
    }
}
