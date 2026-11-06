using System;

namespace XivMcp.CustomDeliveries;

/// <summary>
/// Scrips and other "special" currencies. The game keeps two counts: the inventory manager's, which changes the moment scrips are
/// earned or spent (purchases are checked against it), and the currency manager's bucket, which a server message fills and which
/// can trail behind for minutes (0 scrips shown after two turn-ins of 198). The currency manager still knows the caps.
/// </summary>
public static class SpecialCurrency
{
    /// <summary>How many the player has: the inventory's count, or the currency manager's for a currency the inventory doesn't count.</summary>
    public static long Held(long inventoryCount, long managerCount) => inventoryCount > 0 ? inventoryCount : Math.Max(0, managerCount);

    /// <summary>How many more fit under the cap (<paramref name="max"/> 0: no cap, unlimited room).</summary>
    public static long Room(long have, long max) => max > 0 ? Math.Max(0, max - have) : long.MaxValue;
}
