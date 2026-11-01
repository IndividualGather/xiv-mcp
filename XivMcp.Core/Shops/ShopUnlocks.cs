using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Shops;

/// <summary>
/// Exchanges (SpecialShop) can require a quest: until it is done, the exchange window leaves their page out. An item that only such
/// exchanges sell can't be bought yet, and the quest is what the player needs to hear about.
/// </summary>
public static class ShopUnlocks
{
    /// <summary>
    /// The quest the player still has to complete before any exchange sells the item, or null when one already does (or a gil shop
    /// does, or no exchange sells it at all). <paramref name="shopQuests"/> holds each selling exchange's required quest (0 = none).
    /// </summary>
    public static uint? MissingQuest(IEnumerable<uint> shopQuests, bool soldForGil, Func<uint, bool> completed)
    {
        if (soldForGil) return null;
        var quests = shopQuests.ToList();
        if (quests.Count == 0 || quests.Any(q => q == 0 || completed(q))) return null;
        return quests[0];
    }
}
