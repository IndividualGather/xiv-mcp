using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.CustomDeliveries;

/// <summary>A currency a delivery pays out: how much the player has, its cap (0: none) and the most one delivery gives.</summary>
public sealed record CappedReward(string Currency, long Have, long Max, int PerDelivery);

/// <summary>
/// How many deliveries fit before a reward currency (scrips) would go over its cap. The game asks before a turn-in that would
/// overcap, a question neither Satisfier nor Questionable answers, so deliveries stop short of it instead.
/// </summary>
public static class ScripCap
{
    public static int DeliveriesThatFit(IEnumerable<CappedReward> rewards, int wanted) =>
        Math.Max(0, rewards.Select(r => Fit(r, wanted)).DefaultIfEmpty(wanted).Min());

    /// <summary>The currency that allows the fewest deliveries, if it allows fewer than <paramref name="wanted"/>.</summary>
    public static CappedReward? Limiting(IEnumerable<CappedReward> rewards, int wanted) =>
        rewards.Select(r => (Reward: r, Fit: Fit(r, wanted))).Where(x => x.Fit < wanted).OrderBy(x => x.Fit).Select(x => x.Reward).FirstOrDefault();

    private static int Fit(CappedReward r, int wanted) =>
        r.Max <= 0 || r.PerDelivery <= 0 ? wanted : (int)Math.Min(wanted, Math.Max(0, r.Max - r.Have) / r.PerDelivery);
}
