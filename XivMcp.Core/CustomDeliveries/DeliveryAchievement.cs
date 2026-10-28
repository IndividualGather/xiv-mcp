using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.CustomDeliveries;

/// <summary>
/// One of the "The Customer Is Always Right: &lt;client&gt;" achievements, for delivering <see cref="Count"/> collectables to a custom
/// delivery client. A client's tiers (I to VI) count the same deliveries, so the last tier's progress tells how many the series still needs.
/// </summary>
public sealed record DeliveryAchievement(uint Id, string Name, int Count)
{
    private const string Prefix = "The Customer Is Always Right: ";

    /// <summary>The client's tier that takes the most deliveries, or null when the client has no such achievements.</summary>
    public static DeliveryAchievement? LastTier(string client, IEnumerable<DeliveryAchievement> achievements) =>
        achievements.Where(a => a.Name.StartsWith(Prefix + client + " ", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.Count).FirstOrDefault();

    /// <summary>The deliveries the counter still lacks.</summary>
    public static int Left(uint current, uint max) => (int)Math.Max(0, (long)max - current);
}
