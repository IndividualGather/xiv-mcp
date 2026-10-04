using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.TripleTriad;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Dyeing gear: the game's dye window, driven like a player does it (item context menu → Dye → channel → color → Apply →
/// confirm). The commands were recorded with capture_ui_events; the game has no function for it.
/// </summary>
internal static class DyeTools
{
    /// <summary>Addon sheet rows whose text is the context menu's "Dye" entry (read in the game's language).</summary>
    private static readonly uint[] DyeMenuTexts = [4695, 4706];

    /// <summary>Where gear can be: equipped first, then the bags and the armoury chest.</summary>
    private static readonly InventoryType[] GearContainers =
    [
        InventoryType.EquippedItems, InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];

    public static IEnumerable<McpTool> Create()
    {
        yield return new McpTool
        {
            Name = "dye_item",
            Description = "Dyes a piece of gear the player has (equipped, in the bags or the armoury chest) with a color, like the Dye entry of " +
                          "its context menu: 'dye' is the color's name as the game calls it (e.g. \"Soot Black\", \"Snow White\", \"Metallic Silver\"), " +
                          "'channel' 1 or 2 for gear with two dye channels. Uses one dye item from the bags: for most colors \"Standard Spectrum " +
                          "Dye\" (the result names it); fails if it is missing. Out of combat only. Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": ["string", "integer"], "description": "The gear's name or item id." },
                    "dye": { "type": "string", "description": "The color, e.g. \"Soot Black\"." },
                    "channel": { "type": "integer", "minimum": 1, "maximum": 2, "description": "Which dye channel (default 1)." }
                  },
                  "required": ["item", "dye"]
                }
                """,
            ReadOnly = false,
            Handler = (args, ct) => Dye(
                args.Node("item")?.ToString() ?? throw new ToolException("'item' is required."),
                args.String("dye") ?? throw new ToolException("'dye' is required."),
                args.Int("channel", 1, 1, 2), ct),
        };
    }

    internal static async Task<object?> Dye(string itemQuery, string dyeQuery, int channel, CancellationToken ct)
    {
        var plan = await Game.RunLoggedIn(() => Plan(itemQuery, dyeQuery, channel)).ConfigureAwait(false);
        if (plan.AlreadyDyed) return new { item = plan.ItemName, dye = plan.StainName, channel, changed = false, note = "It already has this color." };

        if (await Game.Run(() => Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]).ConfigureAwait(false))
            throw new ToolException("Can't dye during combat.");

        // 1. The item's context menu, then its Dye entry.
        await Game.Run(() => { OpenContextMenu(plan.Container, plan.Slot); return true; }).ConfigureAwait(false);
        if (!await WaitFor(() => RetainerUi.Ready("ContextMenu"), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            throw new ToolException("The item's context menu did not open.");
        await Game.Run(() => { PickDyeEntry(); return true; }).ConfigureAwait(false);
        if (!await WaitFor(() => RetainerUi.Ready("ColorantColoring"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException("The dye window did not open.");

        // 2. The channel, the color, Apply; then the confirmation.
        await Game.Run(() => { Fire("ColorantColoring", channel == 2 ? [3] : [4, 0]); return true; }).ConfigureAwait(false);
        await Task.Delay(400, ct).ConfigureAwait(false);
        await Game.Run(() => { Fire("ColorantColoring", [5, (int)plan.StainId, (int)plan.StainId, 0]); return true; }).ConfigureAwait(false);
        await Task.Delay(400, ct).ConfigureAwait(false);
        await Game.Run(() => { Fire("ColorantColoring", [0]); return true; }).ConfigureAwait(false);
        if (!await WaitFor(() => RetainerUi.Ready("MiragePrismMiragePlateConfirm"), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
        {
            await Game.Run(CloseDyeWindow).ConfigureAwait(false);
            throw new ToolException("The dye window did not ask for confirmation; nothing was dyed.");
        }
        await Game.Run(() => { Fire("MiragePrismMiragePlateConfirm", [0]); return true; }).ConfigureAwait(false);

        // 3. Done once the item carries the color.
        var dyed = await WaitFor(() => CurrentStain(plan.Container, plan.Slot, plan.ItemId, channel) == plan.StainId, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        await Game.Run(CloseDyeWindow).ConfigureAwait(false);
        if (!dyed) throw new ToolException($"{plan.ItemName} was not dyed (the game may have refused it).");
        return new { item = plan.ItemName, dye = plan.StainName, channel, changed = true, used = plan.DyeItemName };
    }

    private sealed record DyePlan(InventoryType Container, int Slot, uint ItemId, string ItemName, uint StainId, string StainName, string DyeItemName, bool AlreadyDyed);

    /// <summary>Finds the item and the color, and checks the channel and the dye item. Framework thread.</summary>
    private static unsafe DyePlan Plan(string itemQuery, string dyeQuery, int channel)
    {
        var items = Svc.Data.GetExcelSheet<Item>();
        var im = InventoryManager.Instance();
        var candidates = new List<(InventoryType Container, int Slot, Item Row)>();
        foreach (var type in GearContainers)
        {
            var container = im->GetInventoryContainer(type);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || items.GetRowOrDefault(slot->ItemId) is not { } row) continue;
                var matches = uint.TryParse(itemQuery, out var id) ? row.RowId == id : row.Name.ExtractText().Equals(itemQuery.Trim(), StringComparison.OrdinalIgnoreCase);
                if (matches) candidates.Add((type, i, row));
            }
        }
        if (candidates.Count == 0) throw new ToolException($"You don't have '{itemQuery}' equipped, in your bags or in the armoury chest.");
        var (where, index, item) = candidates[0];
        var name = item.Name.ExtractText();
        if (item.DyeCount == 0) throw new ToolException($"{name} can't be dyed.");
        if (channel > item.DyeCount) throw new ToolException($"{name} has only one dye channel.");

        Stain stain;
        try { stain = NameMatch.Single(Svc.Data.GetExcelSheet<Stain>().Where(s => s.RowId != 0 && !s.Name.IsEmpty), dyeQuery, s => s.Name.ExtractText()); }
        catch (ArgumentException ex) { throw new ToolException($"{ex.Message} (dye colors as the game names them, e.g. Soot Black)"); }
        var stainName = stain.Name.ExtractText();
        var current = im->GetInventoryContainer(where)->GetInventorySlot(index)->GetStain(channel - 1);
        var dyeItems = stain.Item.Where(i => i.RowId != 0).Select(i => i.RowId).ToList();
        var have = dyeItems.FirstOrDefault(i => im->GetInventoryItemCount(i) > 0);
        if (current != stain.RowId && have == 0)
        {
            var needed = dyeItems.Count > 0 ? items.GetRowOrDefault(dyeItems[0])?.Name.ExtractText() : null;
            throw new ToolException($"Dyeing {stainName} needs {(needed ?? "a dye")} in your bags.");
        }
        var dyeName = have != 0 ? items.GetRowOrDefault(have)?.Name.ExtractText() ?? "" : "";
        return new DyePlan(where, index, item.RowId, name, stain.RowId, stainName, dyeName, current == stain.RowId);
    }

    private static unsafe byte CurrentStain(InventoryType container, int slot, uint itemId, int channel)
    {
        var item = InventoryManager.Instance()->GetInventoryContainer(container)->GetInventorySlot(slot);
        return item != null && item->ItemId == itemId ? item->GetStain(channel - 1) : (byte)0;
    }

    private static unsafe void OpenContextMenu(InventoryType container, int slot)
    {
        var owner = RetainerUi.Ptr(container == InventoryType.EquippedItems ? "Character" : "Inventory");
        var ownerId = owner.IsNull ? 0u : (uint)((AtkUnitBase*)owner.Address)->Id;
        AgentInventoryContext.Instance()->OpenForItemSlot(container, slot, 0, ownerId);
    }

    /// <summary>Picks the "Dye" entry of the open context menu by its text (in the game's language).</summary>
    private static unsafe void PickDyeEntry()
    {
        var texts = DyeMenuTexts.Select(r => Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(r)?.Text.ExtractText()).Where(t => !string.IsNullOrEmpty(t)).ToHashSet();
        var menu = (AtkUnitBase*)RetainerUi.Ptr("ContextMenu").Address;
        // ContextMenu values: [0] = entry count, entry texts from [8] on (as ECommons' tested ContextMenu reads them). Only values
        // that are text are read: reading another value as a text pointer crashes the game.
        var count = (int)menu->AtkValues[0].UInt;
        for (var i = 0; i < count && 8 + i < menu->AtkValuesCount; i++)
        {
            var v = menu->AtkValues[8 + i];
            if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8) || v.String.Value == null) continue;
            var text = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue;
            if (texts.Contains(text))
            {
                RetainerUi.Fire(menu, true, 0, i, 0u);
                return;
            }
        }
        RetainerUi.Fire(menu, true, -1);
        throw new ToolException("This item's context menu has no Dye entry (it may not be dyeable here).");
    }

    private static unsafe void Fire(string addon, int[] values)
    {
        var ptr = RetainerUi.Ptr(addon);
        if (ptr.IsNull) throw new ToolException($"The {addon} window closed unexpectedly.");
        RetainerUi.Fire((AtkUnitBase*)ptr.Address, true, values.Cast<object?>().ToArray());
    }

    private static unsafe bool CloseDyeWindow()
    {
        var ptr = RetainerUi.Ptr("ColorantColoring");
        if (!ptr.IsNull && ptr.IsVisible) RetainerUi.Fire((AtkUnitBase*)ptr.Address, true, -1);
        return true;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await Game.Run(condition).ConfigureAwait(false)) return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        return false;
    }
}
