using System.Collections.Generic;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace XivMcp.Util;

/// <summary>
/// The game's sort (/itemsort, the Sort button) does not move items between slots: it stores a display order per container
/// group in ItemOrderModule. This maps physical slots to the position the player actually sees.
/// Each sorter entry at display index i points at a physical (page, slot); page is the container within the group
/// (Inventory1-4, SaddleBag1-2, RetainerPage1-7) and is 0 for armory sections.
/// </summary>
internal static unsafe class ItemOrder
{
    public readonly record struct DisplayPosition(int Page, int Slot, int Index);

    /// <summary>Physical (container, slot) → displayed position, for the group the container belongs to. Empty if unknown.</summary>
    public static Dictionary<(GameInventoryType Container, int Slot), DisplayPosition> For(GameInventoryType container)
    {
        var result = new Dictionary<(GameInventoryType, int), DisplayPosition>();
        var module = ItemOrderModule.Instance();
        if (module == null) return result;

        var group = Group(container);
        var sorter = Sorter(module, container);
        if (sorter == null || group.Length == 0) return result;

        var perPage = sorter->ItemsPerPage > 0 ? sorter->ItemsPerPage : 1;
        var count = sorter->Items.Count;
        for (var i = 0; i < count; i++)
        {
            var entry = sorter->Items[i].Value;
            if (entry == null || entry->Page >= group.Length) continue;
            result[(group[entry->Page], entry->Slot)] = new DisplayPosition(i / perPage, i % perPage, i);
        }
        return result;
    }

    /// <summary>The containers a sorter's "page" index refers to.</summary>
    private static GameInventoryType[] Group(GameInventoryType c)
    {
        var name = c.ToString();
        if (name.StartsWith("Inventory", System.StringComparison.Ordinal))
            return [GameInventoryType.Inventory1, GameInventoryType.Inventory2, GameInventoryType.Inventory3, GameInventoryType.Inventory4];
        if (name.StartsWith("SaddleBag", System.StringComparison.Ordinal)) return [GameInventoryType.SaddleBag1, GameInventoryType.SaddleBag2];
        if (name.StartsWith("PremiumSaddleBag", System.StringComparison.Ordinal)) return [GameInventoryType.PremiumSaddleBag1, GameInventoryType.PremiumSaddleBag2];
        if (name.StartsWith("RetainerPage", System.StringComparison.Ordinal))
            return [GameInventoryType.RetainerPage1, GameInventoryType.RetainerPage2, GameInventoryType.RetainerPage3, GameInventoryType.RetainerPage4,
                    GameInventoryType.RetainerPage5, GameInventoryType.RetainerPage6, GameInventoryType.RetainerPage7];
        return name.StartsWith("Armory", System.StringComparison.Ordinal) ? [c] : [];
    }

    private static ItemOrderModuleSorter* Sorter(ItemOrderModule* m, GameInventoryType c) => c switch
    {
        GameInventoryType.Inventory1 or GameInventoryType.Inventory2 or GameInventoryType.Inventory3 or GameInventoryType.Inventory4 =>
            m->InventorySorter,
        GameInventoryType.SaddleBag1 or GameInventoryType.SaddleBag2 =>
            m->SaddleBagSorter,
        GameInventoryType.PremiumSaddleBag1 or GameInventoryType.PremiumSaddleBag2 =>
            m->PremiumSaddleBagSorter,
        GameInventoryType.RetainerPage1 or GameInventoryType.RetainerPage2 or GameInventoryType.RetainerPage3 or GameInventoryType.RetainerPage4 or
        GameInventoryType.RetainerPage5 or GameInventoryType.RetainerPage6 or GameInventoryType.RetainerPage7 =>
            m->GetActiveRetainerSorter(),
        GameInventoryType.ArmoryMainHand => m->ArmouryMainHandSorter,
        GameInventoryType.ArmoryOffHand => m->ArmouryOffHandSorter,
        GameInventoryType.ArmoryHead => m->ArmouryHeadSorter,
        GameInventoryType.ArmoryBody => m->ArmouryBodySorter,
        GameInventoryType.ArmoryHands => m->ArmouryHandsSorter,
        GameInventoryType.ArmoryWaist => m->ArmouryWaistSorter,
        GameInventoryType.ArmoryLegs => m->ArmouryLegsSorter,
        GameInventoryType.ArmoryFeets => m->ArmouryFeetSorter,
        GameInventoryType.ArmoryEar => m->ArmouryEarsSorter,
        GameInventoryType.ArmoryNeck => m->ArmouryNeckSorter,
        GameInventoryType.ArmoryWrist => m->ArmouryWristsSorter,
        GameInventoryType.ArmoryRings => m->ArmouryRingsSorter,
        GameInventoryType.ArmorySoulCrystal => m->ArmourySoulCrystalSorter,
        _ => null,
    };
}
