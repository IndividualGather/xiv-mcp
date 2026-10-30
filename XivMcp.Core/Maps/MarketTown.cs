using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Maps;

/// <summary>A town whose map shows a market board.</summary>
public sealed record MarketTown(uint Territory, string Name, uint Region, uint ExVersion)
{
    /// <summary>
    /// Where to buy when the zone the player is in has no market board: a town in the same region if there is one (a short trip),
    /// otherwise the newest expansion's town, whose board tends to be busy and easy to reach.
    /// </summary>
    public static MarketTown? Pick(IEnumerable<MarketTown> towns, uint currentTerritory, uint currentRegion) =>
        towns.Where(t => t.Territory != currentTerritory)
            .OrderByDescending(t => t.Region == currentRegion)
            .ThenByDescending(t => t.ExVersion)
            .ThenBy(t => t.Territory)
            .FirstOrDefault();
}
