using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Shops;

/// <summary>A vendor of an item, as far as choosing one goes.</summary>
/// <param name="Gil">It sells the item for gil (otherwise it is an exchange for another currency).</param>
/// <param name="InCity">It stands in a city (a town zone: the city-states and the expansion hubs).</param>
/// <param name="Here">It stands in the zone the player is in.</param>
/// <param name="Located">Where it stands is known.</param>
public sealed record VendorSpot(string Name, bool Gil, bool InCity, bool Here, bool Located);

/// <summary>Which vendor to buy from: gil vendors in a city first (cities have aetherytes and are quick to walk), then the rest.</summary>
public static class VendorRanking
{
    /// <summary>Gil before exchanges, located before unknown, cities before the field, the current zone first within each.</summary>
    public static IEnumerable<VendorSpot> Order(IEnumerable<VendorSpot> vendors) => Order(vendors, v => v);

    /// <inheritdoc cref="Order(IEnumerable{VendorSpot})"/>
    public static IEnumerable<T> Order<T>(IEnumerable<T> vendors, Func<T, VendorSpot> spot) =>
        vendors.Select(v => (Vendor: v, Spot: spot(v)))
               .OrderBy(v => v.Spot.Gil ? 0 : 1)
               .ThenBy(v => v.Spot.Located ? 0 : 1)
               .ThenBy(v => v.Spot.InCity ? 0 : 1)
               .ThenBy(v => v.Spot.Here ? 0 : 1)
               .Select(v => v.Vendor);
}
