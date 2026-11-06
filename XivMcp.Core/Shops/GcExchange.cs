using System;
using System.Collections.Generic;

namespace XivMcp.Shops;

/// <summary>
/// The Grand Company seal exchange window (GrandCompanyExchange), as AutoRetainer reads and drives it: its AtkValues hold the item
/// count at 1, the shown rank tab at 2, and per item i, from 17 + i: name (+0), seals (+50), bag count (+100), icon (+150),
/// stackable (+250), item id (+300), rank needed (+400) and whether buying opens the currency dialog (+450, the Venture-style items).
/// The window has three rank tabs (radio buttons, node 37 + tab) and five category tabs (node 44 + tab).
/// </summary>
public static class GcExchange
{
    public const int CountIndex = 1;
    public const int RankTabIndex = 2;
    public const int ItemsStart = 17;
    public const int RankTabs = 3;
    public const int RankTabNode = 37;
    public const int CategoryTabNode = 44;

    // Category tabs as the window orders them (AutoRetainer's GetTab): Weapons 0, Armor 1, Materiel 2, Materials 3, and a fifth.
    public const int MaterialsTab = 3;
    private static readonly int[] CategoryOrder = [MaterialsTab, 2, 4, 0, 1];

    /// <summary>A row of the item list that is shown.</summary>
    public sealed record Row(int Index, uint ItemId, uint Seals, uint RankNeeded, bool Stackable, bool CurrencyDialog);

    /// <summary>The row of the item on the shown page, or null. <paramref name="value"/> reads an AtkValue as a number (bools as 0/1).</summary>
    public static Row? Find(Func<int, uint> value, uint itemId)
    {
        var count = (int)Math.Min(value(CountIndex), 50);
        for (var i = 0; i < count; i++)
        {
            var at = ItemsStart + i;
            if (value(at + 300) != itemId) continue;
            return new Row(i, itemId, value(at + 50), value(at + 400), value(at + 250) != 0, value(at + 450) != 0);
        }
        return null;
    }

    /// <summary>The pages to look through for an item: the Materials category on every rank tab first, then the other categories.</summary>
    public static IEnumerable<(int RankTab, int Category)> Pages()
    {
        for (var rank = 0; rank < RankTabs; rank++) yield return (rank, MaterialsTab);
        for (var rank = 0; rank < RankTabs; rank++)
            foreach (var category in CategoryOrder)
                if (category != MaterialsTab) yield return (rank, category);
    }

    /// <summary>The Grand Company (1 Maelstrom, 2 Twin Adder, 3 Immortal Flames) whose seal exchange the NPC runs, or null.</summary>
    public static int? GrandCompanyOf(uint npcId) => npcId switch
    {
        1002387 => 1, // Storm Quartermaster
        1002393 => 2, // Serpent Quartermaster
        1002390 => 3, // Flame Quartermaster
        _ => null,
    };
}
