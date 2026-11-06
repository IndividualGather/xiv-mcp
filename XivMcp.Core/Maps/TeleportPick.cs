using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Maps;

/// <param name="Id">Aetheryte row.</param>
/// <param name="ToTarget">Flat distance from it to the target, if its position is known.</param>
/// <param name="ToPlayer">Flat distance from it to the player, if the player is in its zone and its position is known.</param>
public readonly record struct TeleportOption(uint Id, bool IsAetheryte, float? ToTarget, float? ToPlayer);

/// <summary>
/// Which aetheryte or aethernet shard gets the player near a target. Map positions carry no height, so the nearest shard can sit
/// on a level the target can't be walked to from (Limsa's Airship Landing, reached only by lift). When the walk from there runs
/// into an obstacle, travel is planned again as a retry: away from the shards already tried and from the one the player stands
/// at, and never "just walk".
/// </summary>
public static class TeleportPick
{
    /// <summary>A shard this close to the player is where the player already is.</summary>
    public const float StandingAt = 40f;

    /// <summary>Walking is preferred when the player is at most this much farther from the target than the best option.</summary>
    public const float WalkSlack = 30f;

    /// <param name="playerToTarget">Flat distance from the player to the target, or null when the player is in another zone.</param>
    /// <returns>The option to travel to, or null to walk (on a retry: nothing else is left).</returns>
    public static TeleportOption? Choose(IReadOnlyList<TeleportOption> options, float? playerToTarget, bool retry, ICollection<uint> tried)
    {
        var candidates = options.Where(o => !tried.Contains(o.Id) && !(retry && o.ToPlayer < StandingAt)).ToList();
        if (candidates.Count == 0) return null;
        var best = candidates.OrderBy(o => o.ToTarget ?? float.MaxValue).ThenByDescending(o => o.IsAetheryte).First();
        if (!retry && playerToTarget is { } walk && walk <= (best.ToTarget ?? float.MaxValue) + WalkSlack) return null;
        return best;
    }
}
