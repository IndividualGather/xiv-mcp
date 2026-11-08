using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Gear;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Melding materia into gear, overmelding included, through the game's Materia Melding window: its agent gets the same events
/// the window sends when the player picks the item and the materia, then the meld dialog is confirmed. Which materia goes into
/// which slot and the overmeld chances come from <see cref="MeldPlanner"/> and the MateriaGrade sheet.
/// </summary>
internal static class MeldTools
{
    private const uint MeldingAction = 12, AdvancedMeldingAction = 13; // GeneralAction rows
    private const uint MeldingUnlock = 11, AdvancedMeldingUnlock = 12;  // UnlockLink rows (quests "Waking the Spirit", "Melding Materia Muchly")

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
            Name = "meld_materia",
            Description = "Melds materia from the bags into a piece of gear (equipped, in the bags or the armoury chest), like the game's Materia " +
                          "Melding window, overmelding included: 'materia' lists the materia to add (names or item ids, one entry per materia, e.g. " +
                          "[\"Craftsman's Control Materia X\", \"Craftsman's Control Materia X\"]). Fills the free slots after the materia already " +
                          "melded; harder materia go first, into the guaranteed slots and then the overmeld slots with the best chances. An overmeld " +
                          "uses the materia up when it fails; it is tried again while that materia is left and it failed fewer than 'max_failures' " +
                          "times per slot. 'dry_run' only shows the plan (slots, chances, materia expected to be used). Overmelding needs Advanced " +
                          "Materia Melding unlocked and gear that allows it. Out of combat. Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": ["string", "integer"], "description": "The gear's name or item id." },
                    "slot": { "type": "string", "description": "Instead of 'item': the piece worn in this slot, e.g. \"ring\" or \"main hand\"." },
                    "materia": { "type": "array", "items": { "type": ["string", "integer"] }, "description": "The materia to meld, one entry per materia." },
                    "max_failures": { "type": "integer", "minimum": 0, "maximum": 100, "description": "Failed overmeld tries allowed per slot before giving up (default 10)." },
                    "min_chance": { "type": "integer", "minimum": 1, "maximum": 100, "description": "Don't try overmelds with a lower success chance in percent (default 1)." },
                    "dry_run": { "type": "boolean", "description": "Only plan; meld nothing (default false)." }
                  },
                  "required": ["materia"]
                }
                """,
            ReadOnly = false,
            Handler = (args, ct) => Meld(
                args.Node("item")?.ToString(), args.String("slot"),
                (args.Node("materia") as System.Text.Json.Nodes.JsonArray ?? throw new ToolException("'materia' is required (a list).")).Select(n => n?.ToString() ?? "").ToList(),
                args.Int("max_failures", 10, 0, 100), args.Int("min_chance", 1, 1, 100), args.Bool("dry_run", false), ct),
        };
    }

    private sealed record Target(InventoryType Container, int Slot, uint ItemId, string Name, bool Hq, int GuaranteedSlots, bool Advanced, int Filled, int ItemLevel);

    private static async Task<object?> Meld(string? itemQuery, string? slotName, List<string> materiaQueries, int maxFailures, int minChance, bool dryRun, CancellationToken ct)
    {
        if (itemQuery is null == slotName is null) throw new ToolException("Give either 'item' or 'slot'.");
        if (materiaQueries.Count == 0) throw new ToolException("'materia' is empty.");
        int? equipSlot = null;
        if (slotName is not null)
        {
            try { equipSlot = XivMcp.Fashion.GearSlots.FromName(slotName); }
            catch (ArgumentException ex) { throw new ToolException(ex.Message); }
        }

        var (target, picks, plan, advancedUnlocked) = await Game.RunLoggedIn(() =>
        {
            var t = FindTarget(itemQuery, equipSlot);
            var p = materiaQueries.Select(ResolveMateria).ToList();
            foreach (var m in p)
            {
                var level = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(m.ItemId)?.LevelItem.RowId ?? 0;
                if (level > t.ItemLevel) throw new ToolException($"{m.Name} needs gear of item level {level} or higher; {t.Name} is item level {t.ItemLevel}.");
            }
            unsafe
            {
                var ui = UIState.Instance();
                if (!ui->IsUnlockLinkUnlocked(MeldingUnlock)) throw new ToolException("This character hasn't unlocked Materia Melding (quest \"Waking the Spirit\").");
                var advanced = ui->IsUnlockLinkUnlocked(AdvancedMeldingUnlock);
                var hq = t.Hq;
                List<MeldStep> steps;
                try { steps = MeldPlanner.Plan(new MeldItem(t.Name, t.GuaranteedSlots, t.Advanced, t.Filled), p, (g, i, _) => OvermeldChance(g, i, hq), advanced); }
                catch (MeldPlanException ex) { throw new ToolException(ex.Message); }
                return (t, p, steps, advanced);
            }
        }).ConfigureAwait(false);

        var tooUnlikely = plan.FirstOrDefault(s => s.ChancePercent < minChance);
        var planView = plan.Select(s => new { slot = s.Slot + 1, materia = s.Materia.Name, chance = s.ChancePercent, overmeld = s.Overmeld }).ToList();
        if (dryRun || tooUnlikely is not null)
        {
            var have = await Game.Run(() => picks.Select(m => m.ItemId).Distinct().ToDictionary(id => id, BagCount)).ConfigureAwait(false);
            return new
            {
                item = target.Name, hq = target.Hq, guaranteedSlots = target.GuaranteedSlots, alreadyMelded = target.Filled, plan = planView,
                expectedMateria = Math.Round(MeldPlanner.ExpectedMateria(plan), 1),
                inBags = have.ToDictionary(kv => picks.First(m => m.ItemId == kv.Key).Name, kv => kv.Value),
                melded = false,
                note = tooUnlikely is not null ? $"Slot {tooUnlikely.Slot + 1} ({tooUnlikely.Materia.Name}) succeeds only {tooUnlikely.ChancePercent}%, below min_chance {minChance}; nothing melded." : "Dry run: nothing melded.",
            };
        }

        await Game.Run(() =>
        {
            var c = Svc.Condition;
            if (c[ConditionFlag.InCombat]) throw new ToolException("Can't meld during combat.");
            if (!c[ConditionFlag.NormalConditions] || c[ConditionFlag.Occupied39] || c[ConditionFlag.Jumping] || c[ConditionFlag.Crafting] || c[ConditionFlag.Gathering])
                throw new ToolException("Can't meld right now (busy: crafting, gathering, talking or in a cutscene).");
            return true;
        }).ConfigureAwait(false);

        var results = new List<object>();
        var used = 0;
        try
        {
            await OpenWindow(advancedUnlocked, ct).ConfigureAwait(false);
            foreach (var step in plan)
            {
                var failures = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var left = await Game.Run(() => BagCount(step.Materia.ItemId)).ConfigureAwait(false);
                    if (left == 0)
                        throw new ToolException($"No {step.Materia.Name} left in the bags for slot {step.Slot + 1}. Done so far: {Describe(results)}");
                    var (success, rate) = await MeldOnce(target, step, ct).ConfigureAwait(false);
                    used++;
                    if (success)
                    {
                        results.Add(new { slot = step.Slot + 1, materia = step.Materia.Name, chance = rate, tries = failures + 1 });
                        break;
                    }
                    failures++;
                    if (!MeldPlanner.TryAgain(failures, maxFailures, left - 1))
                        throw new ToolException($"Overmelding {step.Materia.Name} into slot {step.Slot + 1} failed {failures} time(s) " +
                                                $"({(left - 1 == 0 ? "none left" : $"max_failures {maxFailures}")}). Done so far: {Describe(results)}");
                }
            }
        }
        finally
        {
            await Game.Run(() => { unsafe { var a = AgentMateriaAttach.Instance(); if (a->IsAgentActive()) a->Hide(); } return true; }).ConfigureAwait(false);
        }
        return new { item = target.Name, melded = results, materiaUsed = used };
    }

    private static string Describe(List<object> results) => results.Count == 0 ? "nothing melded." : System.Text.Json.JsonSerializer.Serialize(results);

    /// <summary>The overmeld chance from the MateriaGrade sheet (row = 0-based grade; NQ or HQ gear). Framework thread.</summary>
    private static int OvermeldChance(int grade, int overmeldIndex, bool hq)
    {
        if (overmeldIndex is < 0 or > 3) return 0;
        if (Svc.Data.GetExcelSheet<MateriaGrade>().GetRowOrDefault((uint)grade) is not { } row) return 0;
        return hq ? row.OvermeldHQPercent[overmeldIndex] : row.OvermeldNQPercent[overmeldIndex];
    }

    /// <summary>A materia by name or item id, with its grade from the Materia sheet. Framework thread.</summary>
    private static MateriaPick ResolveMateria(string query)
    {
        var items = Svc.Data.GetExcelSheet<Item>();
        uint itemId;
        if (uint.TryParse(query.Trim(), out var id)) itemId = id;
        else
        {
            var match = items.Where(i => i.ItemUICategory.RowId == 58 && i.Name.ExtractText().Equals(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0) throw new ToolException($"No materia is called '{query}' (use the full name, e.g. \"Craftsman's Control Materia X\").");
            itemId = match[0].RowId;
        }
        foreach (var row in Svc.Data.GetExcelSheet<Materia>())
            for (var g = 0; g < row.Item.Count; g++)
                if (row.Item[g].RowId == itemId)
                {
                    var p = row.BaseParam.RowId;
                    return new MateriaPick(itemId, items.GetRowOrDefault(itemId)?.Name.ExtractText() ?? query, g, p is 10 or 11 or 70 or 71 or 72 or 73);
                }
        throw new ToolException($"'{query}' isn't a materia.");
    }

    /// <summary>The gear to meld: by slot worn, or by name/id (equipped first, then bags, then armoury). Framework thread.</summary>
    private static unsafe Target FindTarget(string? itemQuery, int? equipSlot)
    {
        var items = Svc.Data.GetExcelSheet<Item>();
        var im = InventoryManager.Instance();
        foreach (var type in equipSlot is null ? GearContainers : [InventoryType.EquippedItems])
        {
            var container = im->GetInventoryContainer(type);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                if (equipSlot is { } es && i != es) continue;
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || items.GetRowOrDefault(slot->ItemId) is not { } row) continue;
                if (equipSlot is null && !(uint.TryParse(itemQuery!, out var id) ? row.RowId == id : row.Name.ExtractText().Equals(itemQuery!.Trim(), StringComparison.OrdinalIgnoreCase)))
                    continue;
                var name = row.Name.ExtractText();
                if (row.MateriaSlotCount == 0 && !row.IsAdvancedMeldingPermitted) throw new ToolException($"{name} can't take materia.");
                return new Target(type, i, row.RowId, name, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), row.MateriaSlotCount,
                    row.IsAdvancedMeldingPermitted, slot->GetMateriaCount(), (int)row.LevelItem.RowId);
            }
        }
        throw new ToolException(equipSlot is null ? $"You don't have '{itemQuery}' equipped, in your bags or in the armoury chest." : "Nothing is worn in that slot.");
    }

    private static unsafe int BagCount(uint itemId) => InventoryManager.Instance()->GetInventoryItemCount(itemId);

    private static AgentMateriaAttach.FilterCategory CategoryOf(InventoryType type) => type switch
    {
        InventoryType.Inventory1 or InventoryType.Inventory2 or InventoryType.Inventory3 or InventoryType.Inventory4 => AgentMateriaAttach.FilterCategory.Inventory,
        InventoryType.ArmoryMainHand or InventoryType.ArmoryOffHand => AgentMateriaAttach.FilterCategory.ArmouryWeapon,
        InventoryType.ArmoryHead or InventoryType.ArmoryBody or InventoryType.ArmoryHands => AgentMateriaAttach.FilterCategory.ArmouryHeadBodyHands,
        InventoryType.ArmoryLegs or InventoryType.ArmoryFeets => AgentMateriaAttach.FilterCategory.ArmouryLegsFeet,
        InventoryType.ArmoryEar or InventoryType.ArmoryNeck => AgentMateriaAttach.FilterCategory.ArmouryNeckEars,
        InventoryType.ArmoryWrist or InventoryType.ArmoryRings => AgentMateriaAttach.FilterCategory.ArmouryWristRing,
        InventoryType.EquippedItems => AgentMateriaAttach.FilterCategory.Equipped,
        _ => AgentMateriaAttach.FilterCategory.None,
    };

    /// <summary>Opens the Materia Melding window (the advanced action replaces the plain one once unlocked).</summary>
    private static async Task OpenWindow(bool advanced, CancellationToken ct)
    {
        if (await Game.Run(() => { unsafe { return AgentMateriaAttach.Instance()->IsAgentActive(); } }).ConfigureAwait(false)) return;
        foreach (var action in advanced ? new[] { AdvancedMeldingAction, MeldingAction } : [MeldingAction])
        {
            await Game.Run(() => { unsafe { return ActionManager.Instance()->UseAction(ActionType.GeneralAction, action); } }).ConfigureAwait(false);
            if (await WaitFor(() => { unsafe { var a = AgentMateriaAttach.Instance(); return a->IsAgentActive() && a->UpdateState == 0; } }, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false))
                return;
        }
        throw new ToolException("The Materia Melding window did not open.");
    }

    /// <summary>One meld: pick the item and the materia in the window, confirm the dialog (and the overmeld question), and read the slot after.</summary>
    private static async Task<(bool Success, int Rate)> MeldOnce(Target target, MeldStep step, CancellationToken ct)
    {
        var category = CategoryOf(target.Container);
        if (category == AgentMateriaAttach.FilterCategory.None) throw new ToolException($"{target.Name} isn't where melding can reach it.");

        // The category tab, then the item (matched by its inventory slot, which tells identical items apart), then the materia.
        await Game.Run(() => { unsafe { var a = AgentMateriaAttach.Instance(); if (a->Category != category) Send(0, 0, (int)category); } return true; }).ConfigureAwait(false);
        await WaitIdle(ct).ConfigureAwait(false);
        var itemIndex = await Game.Run(() =>
        {
            unsafe
            {
                var a = AgentMateriaAttach.Instance();
                var wanted = InventoryManager.Instance()->GetInventoryContainer(target.Container)->GetInventorySlot(target.Slot);
                for (var i = 0; i < a->ItemCount && i < a->Data->ItemsSorted.Length; i++)
                    if (a->Data->ItemsSorted[i].Value != null && a->Data->ItemsSorted[i].Value->Item == wanted) return i;
                return -1;
            }
        }).ConfigureAwait(false);
        if (itemIndex < 0) throw new ToolException($"{target.Name} isn't in the Materia Melding list (it may be full, or no crafter is high enough to meld it).");
        await Game.Run(() => { Send(0, 1, itemIndex, 1, 0); return true; }).ConfigureAwait(false);
        await WaitIdle(ct).ConfigureAwait(false);
        var materiaIndex = await Game.Run(() =>
        {
            unsafe
            {
                var a = AgentMateriaAttach.Instance();
                for (var i = 0; i < a->MateriaCount && i < a->Data->MateriaSorted.Length; i++)
                    if (a->Data->MateriaSorted[i].Value != null && a->Data->MateriaSorted[i].Value->Item != null && a->Data->MateriaSorted[i].Value->Item->ItemId == step.Materia.ItemId) return i;
                return -1;
            }
        }).ConfigureAwait(false);
        if (materiaIndex < 0) throw new ToolException($"The Materia Melding window doesn't offer {step.Materia.Name} for {target.Name}.");
        var before = await Game.Run(() => MateriaCountNow(target)).ConfigureAwait(false);
        await Game.Run(() => { Send(0, 2, materiaIndex, 1, 0); return true; }).ConfigureAwait(false);

        // The meld dialog: read the real chance, then Meld.
        if (!await WaitFor(() => GameWindows.Ready("MateriaAttachDialog") && Svc.Condition[ConditionFlag.MeldingMateria], TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException("The meld dialog did not open.");
        var rate = await Game.Run(() => { unsafe { var d = (AtkUnitBase*)RetainerUi.Ptr("MateriaAttachDialog").Address; return d != null && d->AtkValuesCount > 41 ? d->AtkValues[41].Int : step.ChancePercent; } }).ConfigureAwait(false);
        await Game.Run(() => { unsafe { RetainerUi.Fire((AtkUnitBase*)RetainerUi.Ptr("MateriaAttachDialog").Address, true, 0, 0, 1); } return true; }).ConfigureAwait(false);

        // An overmeld asks once more ("…may be lost…").
        if (await WaitFor(() => GameWindows.YesNoPrompt() is not null, TimeSpan.FromSeconds(step.Overmeld ? 2 : 0.6), ct).ConfigureAwait(false))
            await Game.Run(() => { GameWindows.AnswerYesNo(true); return true; }).ConfigureAwait(false);

        // Done when the melding state ends; success shows as one more materia on the item.
        await WaitFor(() => !Svc.Condition[ConditionFlag.MeldingMateria], TimeSpan.FromSeconds(12), ct).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);
        var after = await Game.Run(() => MateriaCountNow(target)).ConfigureAwait(false);
        return (after > before, rate);
    }

    private static unsafe int MateriaCountNow(Target t)
    {
        var slot = InventoryManager.Instance()->GetInventoryContainer(t.Container)->GetInventorySlot(t.Slot);
        return slot == null || slot->ItemId != t.ItemId ? -1 : slot->GetMateriaCount();
    }

    /// <summary>Sends an event to the melding agent, as the window does (all values Int). Framework thread.</summary>
    private static unsafe void Send(ulong kind, params int[] values)
    {
        var a = AgentMateriaAttach.Instance();
        var args = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++) { args[i] = default; args[i].Type = AtkValueType.Int; args[i].Int = values[i]; }
        var ret = stackalloc AtkValue[1];
        ret[0] = default;
        a->ReceiveEvent(ret, args, (uint)values.Length, kind);
    }

    private static Task WaitIdle(CancellationToken ct) =>
        WaitFor(() => { unsafe { return AgentMateriaAttach.Instance()->UpdateState == 0; } }, TimeSpan.FromSeconds(3), ct);

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        await Task.Delay(150, ct).ConfigureAwait(false);
        while (true)
        {
            if (await Game.Run(condition).ConfigureAwait(false)) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
    }
}
