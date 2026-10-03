using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>Item lookups shared by the planning, shop and venture tools.</summary>
internal static class Items
{
    /// <summary>An item by id or name: exact name first, then a unique partial match.</summary>
    public static Item Resolve(string query)
    {
        var sheet = Svc.Data.GetExcelSheet<Item>();
        if (uint.TryParse(query, out var id))
            return sheet.GetRowOrDefault(id) is { } byId && !byId.Name.IsEmpty ? byId : throw new ToolException($"No item {id}.");
        var exact = sheet.FirstOrDefault(i => i.Name.ExtractText().Equals(query, StringComparison.OrdinalIgnoreCase));
        if (exact.RowId != 0) return exact;
        var partial = sheet.Where(i => !i.Name.IsEmpty && Game.Matches(i.Name.ExtractText(), query)).Take(9).ToList();
        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new ToolException($"No item matches '{query}'."),
            _ => throw new ToolException($"'{query}' matches several items: {string.Join(", ", partial.Take(8).Select(i => i.Name.ExtractText()))}. Be more specific."),
        };
    }

    public static string Name(uint itemId) => Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.Name.ExtractText() ?? $"Item {itemId}";

    public static bool IsCrystal(uint itemId) => itemId is >= 2 and <= 19;

    public static uint StackSize(uint itemId) => Math.Max(1, Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.StackSize ?? 1);

    /// <summary>Gil, the currencies shown in the currency window and other "items" that never take an inventory slot.</summary>
    public static bool IsCurrency(uint itemId) => itemId < 100 || Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.ItemUICategory.RowId == 100;

    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    /// <summary>How many of an item are in the bags (+ crystal bag), NQ and HQ together. Framework thread.</summary>
    public static unsafe int CountInBags(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        // Crystals live in their own container; everything else is counted slot by slot, because the game's item count
        // leaves collectables out.
        if (IsCrystal(itemId)) return im->GetInventoryItemCount(itemId, false, false, false);
        var count = 0;
        foreach (var bag in Bags)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId) count += slot->Quantity;
            }
        }
        return count;
    }

    /// <summary>Free slots in the four inventory bags. Framework thread.</summary>
    public static unsafe int FreeBagSlots()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : (int)im->GetEmptySlotsInBag();
    }

    /// <summary>For an item already in the bags: how much more fits into its partly filled stacks. Framework thread.</summary>
    public static unsafe int RoomInExistingStacks(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return 0;
        var stack = (int)StackSize(itemId);
        var room = 0;
        foreach (var bag in Bags)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId && !slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality))
                    room += Math.Max(0, stack - slot->Quantity);
            }
        }
        return room;
    }

    /// <summary>Bag slots needed to receive <paramref name="quantity"/> more of an item (crystals and currencies need none). Framework thread.</summary>
    public static int SlotsNeeded(uint itemId, int quantity)
    {
        if (quantity <= 0 || IsCrystal(itemId) || IsCurrency(itemId)) return 0;
        if (Collectables.IsCollectable(itemId)) return quantity; // collectables never stack
        var rest = quantity - RoomInExistingStacks(itemId);
        return rest <= 0 ? 0 : (int)Math.Ceiling(rest / (double)StackSize(itemId));
    }

    /// <summary>Bag slots needed without looking at the current bags (for planning items that are not there yet).</summary>
    public static int SlotsFor(uint itemId, int quantity) =>
        quantity <= 0 || IsCrystal(itemId) || IsCurrency(itemId) ? 0
        : Collectables.IsCollectable(itemId) ? quantity // collectables never stack
        : (int)Math.Ceiling(quantity / (double)StackSize(itemId));
}
