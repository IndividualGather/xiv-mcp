using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Voyages;

/// <summary>What the free company's submersibles have reached: each one's rank, and the sectors unlocked and explored.</summary>
public sealed record FleetState(IReadOnlyDictionary<string, int> Ranks, IReadOnlySet<uint> Unlocked, IReadOnlySet<uint> Explored);

/// <summary>A submersible that went up in rank.</summary>
public sealed record RankUp(string Vessel, int From, int To);

/// <summary>
/// What finishing voyages brought: rank-ups and sectors newly unlocked or explored. Only known once the voyage is finished at the
/// panel, so the next route is chosen after that, with the new rank and sectors.
/// </summary>
public sealed record VoyageProgress(IReadOnlyList<RankUp> RankUps, IReadOnlyList<uint> NewlyUnlocked, IReadOnlyList<uint> NewlyExplored)
{
    public bool Any => RankUps.Count > 0 || NewlyUnlocked.Count > 0 || NewlyExplored.Count > 0;

    public static VoyageProgress Between(FleetState before, FleetState after) => new(
        after.Ranks.Where(kv => before.Ranks.TryGetValue(kv.Key, out var was) && kv.Value > was)
                   .Select(kv => new RankUp(kv.Key, before.Ranks[kv.Key], kv.Value)).ToList(),
        after.Unlocked.Except(before.Unlocked).Order().ToList(),
        after.Explored.Except(before.Explored).Order().ToList());
}
