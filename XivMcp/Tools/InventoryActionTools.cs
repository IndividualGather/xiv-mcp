using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Tools that change the inventory: the game's own /itemsort, and moving single items like a drag &amp; drop.</summary>
internal static class InventoryActionTools
{
    private static readonly string[] SortCategories =
        ["inventory", "retainer", "armoury", "saddlebag", "rightsaddlebag", "mh", "oh", "head", "body", "hands", "legs", "feet", "neck", "ears", "wrists", "rings", "soul"];

    private static readonly string[] ArmorySlotCategories = ["mh", "oh", "head", "body", "hands", "legs", "feet", "neck", "ears", "wrists", "rings", "soul"];

    private static readonly string[] SortConditions =
        ["id", "spiritbond", "category", "lv", "ilv", "stack", "hq", "materia", "pdamage", "mdamage", "delay", "autoattack",
         "blockrate", "blockstrength", "defense", "mdefense", "str", "dex", "vit", "int", "mnd", "craftsmanship", "control", "gathering", "perception"];

    private static readonly string[] BusyFlags =
        ["InCombat", "Crafting", "PreparingToCraft", "ExecutingCraftingAction", "Gathering", "ExecutingGatheringAction", "TradeOpen",
         "BetweenAreas", "BetweenAreas51", "WatchingCutscene", "WatchingCutscene78", "OccupiedInCutSceneEvent", "Unconscious", "Casting"];

    private static readonly string[] SaddlebagAddons = ["InventoryBuddy", "InventoryBuddy2"];
    private static readonly string[] RetainerAddons = ["InventoryRetainer", "InventoryRetainerLarge"];

    /// <summary>Only one inventory action at a time — interleaved moves would confuse the server and each other.</summary>
    internal static readonly SemaphoreSlim Gate = new(1, 1);

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        void RequireEnabled()
        {
            if (!config.AllowItemsRetainers)
                throw new ToolException("Inventory actions are disabled. Enable \"Items & retainers\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "sort_inventory",
            Description = "Sorts inventory containers with the game's own /itemsort command (the same as typing it): sets the given sort conditions " +
                          "and executes the sort. Categories: inventory (main bags), armoury (whole armory chest) or a single armory slot " +
                          "(mh, oh, head, body, hands, legs, feet, neck, ears, wrists, rings, soul), saddlebag, rightsaddlebag (premium), retainer. " +
                          "Saddlebag and retainer need their window open in game. Use category \"armoury_slots\" to sort every armory slot with the same conditions. " +
                          "Conditions are applied in order (first = primary). Requires 'Items & retainers' in /xivmcp.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    "categories": { "type": "array", "items": { "type": "string", "enum": [{{Quote(SortCategories.Append("armoury_slots"))}}] },
                                    "description": "What to sort, e.g. [\"inventory\"] or [\"armoury_slots\"]." },
                    "conditions": {
                      "type": "array",
                      "description": "Sort keys in priority order, e.g. [{\"by\":\"ilv\",\"order\":\"des\"},{\"by\":\"id\"}].",
                      "items": {
                        "type": "object",
                        "properties": {
                          "by": { "type": "string", "enum": [{{Quote(SortConditions)}}] },
                          "order": { "type": "string", "enum": ["asc", "des"], "description": "Default asc." }
                        },
                        "required": ["by"]
                      }
                    }
                  },
                  "required": ["categories", "conditions"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var categories = args.StringList("categories").Select(c => c.ToLowerInvariant())
                    .SelectMany(c => c == "armoury_slots" ? ArmorySlotCategories : [c == "armory" ? "armoury" : c])
                    .Distinct().ToList();
                if (categories.Count == 0) throw new ToolException("'categories' is required.");
                foreach (var c in categories.Where(c => !SortCategories.Contains(c)))
                    throw new ToolException($"Unknown category '{c}'. Valid: {string.Join(", ", SortCategories)}, armoury_slots");

                if (args.Node("conditions") is not JsonArray condArray || condArray.Count == 0)
                    throw new ToolException("'conditions' must be a non-empty array of { by, order }.");
                if (condArray.Count > 5) throw new ToolException("At most 5 sort conditions.");
                var conditions = condArray.Select(n =>
                {
                    var by = n?["by"]?.GetValue<string>().ToLowerInvariant() ?? throw new ToolException("Each condition needs 'by'.");
                    var order = n["order"]?.GetValue<string>().ToLowerInvariant() ?? "asc";
                    if (order == "desc") order = "des";
                    if (!SortConditions.Contains(by)) throw new ToolException($"Unknown condition '{by}'. Valid: {string.Join(", ", SortConditions)}");
                    if (order is not ("asc" or "des")) throw new ToolException("order must be 'asc' or 'des'.");
                    return (by, order);
                }).ToList();

                await Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Game.RunLoggedIn(() =>
                    {
                        EnsureNotBusy();
                        if (categories.Any(c => c is "saddlebag" or "rightsaddlebag")) EnsureWindowOpen(SaddlebagAddons, "saddlebag");
                        if (categories.Contains("retainer")) EnsureRetainerOpen();
                        return true;
                    }).ConfigureAwait(false);

                    var sent = new List<string>();
                    using var feedback = new GameCommands.Feedback();
                    foreach (var category in categories)
                    {
                        var commands = new List<string> { $"/itemsort clear {category}" };
                        commands.AddRange(conditions.Select(c => $"/itemsort condition {category} {c.by} {c.order}"));
                        commands.Add($"/itemsort execute {category}");
                        foreach (var cmd in commands)
                        {
                            ct.ThrowIfCancellationRequested();
                            await Svc.Framework.RunOnFrameworkThread(() => GameCommands.Execute(cmd)).ConfigureAwait(false);
                            sent.Add(cmd);
                            await Task.Delay(120, ct).ConfigureAwait(false);
                        }
                        await Task.Delay(400, ct).ConfigureAwait(false); // let the sort's item moves go out before the next category
                    }
                    await Task.Delay(500, ct).ConfigureAwait(false);
                    Svc.Log.Information($"[MCP] Sorted {string.Join(", ", categories)}");
                    return new
                    {
                        sorted = categories,
                        commands = sent,
                        gameMessages = feedback.Messages,
                        note = "The game applies the sort itself; check get_inventory to see the result. Game error messages, if any, are listed in gameMessages.",
                    };
                }
                finally { Gate.Release(); }
            },
        };

        yield return new McpTool
        {
            Name = "move_items",
            Description = "Moves items between slots and containers exactly like dragging them in the inventory window: rearrange bag slots, bags <-> armory chest, " +
                          "bags <-> saddlebag, bags <-> retainer. Dropping onto an occupied slot swaps the items (or merges stacks of the same item). " +
                          "Each move is sent separately, the plugin waits until the server confirmed it, then pauses before the next one (by default a random 500-800 ms, configurable in /xivmcp). " +
                          "Give the source either as from_container+from_slot or as item_id (first matching stack, optionally limited to from_container); " +
                          "omit to_slot to use the first empty slot of to_container. Container names come from get_inventory (Inventory1-4, ArmoryHead, ..., " +
                          "SaddleBag1/2, PremiumSaddleBag1/2, RetainerPage1-7, FreeCompanyPage1-5). Saddlebag/retainer/company chest windows must be open. " +
                          "Armory containers only accept matching gear. " +
                          "Stops at the first failed move unless continue_on_error=true. Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "moves": {
                      "type": "array",
                      "description": "Moves, executed in order (max 200).",
                      "items": {
                        "type": "object",
                        "properties": {
                          "from_container": { "type": "string" },
                          "from_slot": { "type": "integer", "description": "0-based slot index." },
                          "item_id": { "type": "integer", "description": "Alternative to from_slot: move the first stack of this item." },
                          "hq": { "type": "boolean", "description": "With item_id: only HQ (true) or only NQ (false) stacks." },
                          "to_container": { "type": "string" },
                          "to_slot": { "type": "integer", "description": "0-based target slot; omit for the first empty slot." }
                        },
                        "required": ["to_container"]
                      }
                    },
                    "continue_on_error": { "type": "boolean", "description": "Keep going after a failed move (default false)." }
                  },
                  "required": ["moves"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                if (args.Node("moves") is not JsonArray moves || moves.Count == 0)
                    throw new ToolException("'moves' must be a non-empty array.");
                if (moves.Count > 200) throw new ToolException("At most 200 moves per call.");
                var continueOnError = args.Bool("continue_on_error", false);
                var requests = moves.Select((m, i) => MoveRequest.Parse(m as JsonObject, i)).ToList();

                await Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var results = new List<object>();
                    var sw = Stopwatch.StartNew();
                    var pauses = new List<int>();
                    foreach (var request in requests)
                    {
                        ct.ThrowIfCancellationRequested();
                        var result = await ExecuteMove(request, ct).ConfigureAwait(false);
                        results.Add(result);
                        if (!result.Ok && !continueOnError) break;
                        if (result.Ok && result.Index < requests.Count - 1)
                        {
                            var pause = config.NextMoveDelay();
                            pauses.Add((int)pause.TotalMilliseconds);
                            await Task.Delay(pause, ct).ConfigureAwait(false);
                        }
                    }
                    var moved = results.Cast<MoveResult>().Count(r => r.Ok);
                    Svc.Log.Information($"[MCP] Moved {moved}/{requests.Count} item(s)");
                    return new
                    {
                        requested = requests.Count,
                        moved,
                        failed = results.Cast<MoveResult>().Count(r => !r.Ok),
                        skipped = requests.Count - results.Count,
                        seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                        pauseBetweenMoves = config.MoveDelayDescription,
                        pausesMs = pauses,
                        results,
                    };
                }
                finally { Gate.Release(); }
            },
        };
    }

    internal sealed record MoveRequest(int Index, GameInventoryType? From, int? FromSlot, uint? ItemId, bool? Hq, GameInventoryType To, int? ToSlot)
    {
        public static MoveRequest Parse(JsonObject? m, int index)
        {
            if (m is null) throw new ToolException($"Move #{index}: must be an object.");
            var a = new ToolArgs(m);
            var from = a.String("from_container") is { } f ? ParseContainer(f, index) : (GameInventoryType?)null;
            var to = ParseContainer(a.String("to_container") ?? throw new ToolException($"Move #{index}: 'to_container' is required."), index);
            var fromSlot = m.ContainsKey("from_slot") ? a.Int("from_slot", -1) : (int?)null;
            var itemId = a.UInt("item_id");
            if (itemId is null && (from is null || fromSlot is null))
                throw new ToolException($"Move #{index}: give from_container + from_slot, or item_id.");
            var hq = m.ContainsKey("hq") ? a.Bool("hq", false) : (bool?)null;
            var toSlot = m.ContainsKey("to_slot") ? a.Int("to_slot", -1) : (int?)null;
            return new MoveRequest(index, from, fromSlot, itemId, hq, to, toSlot);
        }
    }

    internal sealed record MoveResult(int Index, bool Ok, string Status, string? Item, string? From, string? To, string? Reason)
    {
        public static MoveResult Fail(int index, string reason, string? item = null, string? from = null, string? to = null) =>
            new(index, false, "failed", item, from, to, reason);
    }

    /// <summary>True while the game still waits for the server to answer an earlier inventory operation.</summary>
    private static unsafe bool HasPendingOperation()
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        foreach (ref var op in im->PendingOperations)
            if (!op.IsEmpty) return true;
        return false;
    }

    private static async Task<bool> WaitForNoPendingOperation(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (await Svc.Framework.RunOnFrameworkThread(HasPendingOperation).ConfigureAwait(false))
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        return true;
    }

    internal static async Task<MoveResult> ExecuteMove(MoveRequest req, CancellationToken ct)
    {
        if (!await WaitForNoPendingOperation(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            return MoveResult.Fail(req.Index, "The game is still processing an earlier inventory operation; try again in a moment.");

        // Resolve and validate against the current state, then issue the move — all in one framework tick.
        (GameInventoryType From, int FromSlot, GameInventoryType To, int ToSlot, uint ItemId, int Quantity, uint DstItemId, int DstQuantity, string? Name)? plan;
        try
        {
            plan = await Game.RunLoggedIn(() =>
            {
                EnsureNotBusy();
                var (from, fromSlot) = ResolveSource(req);
                var to = req.To;
                EnsureContainerUsable(from);
                EnsureContainerUsable(to);

                var src = Slot(from, fromSlot);
                if (src.IsEmpty) throw new ToolException($"{from}[{fromSlot}] is empty.");

                var dstItems = Svc.Inventory.GetInventoryItems(to);
                var toSlot = req.ToSlot ?? FirstEmptySlot(dstItems) ?? throw new ToolException($"{to} has no empty slot.");
                if (toSlot < 0 || toSlot >= dstItems.Length) throw new ToolException($"{to} has slots 0-{dstItems.Length - 1}.");
                if (from == to && fromSlot == toSlot) throw new ToolException("Source and target are the same slot.");
                var dst = dstItems[toSlot];

                EnsureFitsArmory(src.BaseItemId, to);
                if (!dst.IsEmpty) EnsureFitsArmory(dst.BaseItemId, from); // the swapped item lands in the source container

                unsafe
                {
                    var rc = InventoryManager.Instance()->MoveItemSlot((InventoryType)from, (ushort)fromSlot, (InventoryType)to, (ushort)toSlot, true);
                    if (rc != 0) Svc.Log.Debug($"MoveItemSlot returned {rc}");
                }
                return ((GameInventoryType, int, GameInventoryType, int, uint, int, uint, int, string?)?)
                    (from, fromSlot, to, toSlot, src.ItemId, src.Quantity, dst.ItemId, dst.Quantity, InventoryTools.ItemName(src.BaseItemId));
            }).ConfigureAwait(false);
        }
        catch (ToolException ex)
        {
            return MoveResult.Fail(req.Index, ex.Message);
        }

        var p = plan!.Value;
        var fromText = $"{p.From}[{p.FromSlot}]";
        var toText = $"{p.To}[{p.ToSlot}]";

        // Wait for the server to confirm: the target slot must now hold the moved item.
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            var confirmed = await Svc.Framework.RunOnFrameworkThread(() =>
            {
                if (HasPendingOperation()) return false;
                var dst = Slot(p.To, p.ToSlot);
                var merged = p.DstItemId == p.ItemId && dst.ItemId == p.ItemId && dst.Quantity != p.DstQuantity;
                return (dst.ItemId == p.ItemId && dst.ItemId != p.DstItemId) || merged;
            }).ConfigureAwait(false);
            if (confirmed)
                return new MoveResult(req.Index, true, p.DstItemId != 0 && p.DstItemId != p.ItemId ? "swapped" : "moved", p.Name, fromText, toText, null);
        }
        return MoveResult.Fail(req.Index, "The game did not confirm the move within 4 seconds (it may have been rejected, e.g. item not allowed there).", p.Name, fromText, toText);
    }

    private static (GameInventoryType Container, int Slot) ResolveSource(MoveRequest req)
    {
        if (req.ItemId is null)
        {
            var items = Svc.Inventory.GetInventoryItems(req.From!.Value);
            if (req.FromSlot < 0 || req.FromSlot >= items.Length) throw new ToolException($"{req.From} has slots 0-{items.Length - 1}.");
            return (req.From.Value, req.FromSlot!.Value);
        }

        IEnumerable<GameInventoryType> containers = req.From is { } only ? [only] : MovableContainers().Where(c => c != req.To);
        foreach (var container in containers)
        {
            foreach (var item in Svc.Inventory.GetInventoryItems(container))
            {
                if (item.IsEmpty || item.BaseItemId != req.ItemId) continue;
                if (req.Hq is { } hq && item.IsHq != hq) continue;
                if (req.From is null && !IsContainerUsable(container)) continue;
                return (container, (int)item.InventorySlot);
            }
        }
        throw new ToolException($"Item {req.ItemId} ({InventoryTools.ItemName(req.ItemId.Value)}) not found{(req.From is { } c ? $" in {c}" : "")}.");
    }

    private static GameInventoryItem Slot(GameInventoryType container, int slot)
    {
        var items = Svc.Inventory.GetInventoryItems(container);
        return slot >= 0 && slot < items.Length ? items[slot] : default;
    }

    internal static int? FirstEmptySlot(ReadOnlySpan<GameInventoryItem> items)
    {
        for (var i = 0; i < items.Length; i++)
            if (items[i].IsEmpty) return i;
        return null;
    }

    private static GameInventoryType ParseContainer(string name, int index)
    {
        if (!Enum.TryParse<GameInventoryType>(name, true, out var t) || !MovableContainers().Contains(t))
            throw new ToolException($"Move #{index}: '{name}' is not a container items can be moved to/from. Valid: {string.Join(", ", MovableContainers())}");
        return t;
    }

    private static IEnumerable<GameInventoryType> MovableContainers() => Enum.GetValues<GameInventoryType>().Where(t =>
    {
        var n = t.ToString();
        return n.StartsWith("Inventory", StringComparison.Ordinal) || n.StartsWith("Armory", StringComparison.Ordinal) ||
               n.StartsWith("SaddleBag", StringComparison.Ordinal) || n.StartsWith("PremiumSaddleBag", StringComparison.Ordinal) ||
               n.StartsWith("RetainerPage", StringComparison.Ordinal) || n.StartsWith("FreeCompanyPage", StringComparison.Ordinal);
    });

    private static bool IsContainerUsable(GameInventoryType t)
    {
        try { EnsureContainerUsable(t); return true; }
        catch (ToolException) { return false; }
    }

    private static void EnsureContainerUsable(GameInventoryType t)
    {
        var n = t.ToString();
        if (n.Contains("SaddleBag", StringComparison.Ordinal)) EnsureWindowOpen(SaddlebagAddons, "saddlebag");
        else if (n.StartsWith("Retainer", StringComparison.Ordinal)) EnsureRetainerOpen();
        else if (n.StartsWith("FreeCompanyPage", StringComparison.Ordinal)) EnsureWindowOpen(["FreeCompanyChest"], "company chest");
    }

    private static void EnsureWindowOpen(string[] addons, string what)
    {
        if (!addons.Any(a => Svc.GameGui.GetAddonByName(a, 1) is { IsNull: false, IsVisible: true }))
            throw new ToolException($"The {what} window must be open in game.");
    }

    private static unsafe void EnsureRetainerOpen()
    {
        var rm = RetainerManager.Instance();
        if (rm == null || rm->GetActiveRetainer() == null) throw new ToolException("No retainer is currently open (talk to a summoning bell and select one).");
        EnsureWindowOpen(RetainerAddons, "retainer inventory");
    }

    internal static void EnsureNotBusy()
    {
        foreach (var name in BusyFlags)
            if (Enum.TryParse<ConditionFlag>(name, out var flag) && Svc.Condition[flag])
                throw new ToolException($"Can't change the inventory right now (character state: {name}).");
        PluginCompat.EnsureFcchIdle();
    }

    /// <summary>Armory chest sections only accept gear for that slot.</summary>
    private static void EnsureFitsArmory(uint itemId, GameInventoryType container)
    {
        var name = container.ToString();
        if (!name.StartsWith("Armory", StringComparison.Ordinal)) return;
        var item = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId);
        var cat = item?.EquipSlotCategory.ValueNullable;
        bool fits = cat is { } c && name switch
        {
            "ArmoryMainHand" => c.MainHand == 1,
            "ArmoryOffHand" => c.OffHand == 1,
            "ArmoryHead" => c.Head == 1,
            "ArmoryBody" => c.Body == 1,
            "ArmoryHands" => c.Gloves == 1,
            "ArmoryWaist" => c.Waist == 1,
            "ArmoryLegs" => c.Legs == 1,
            "ArmoryFeets" => c.Feet == 1,
            "ArmoryEar" => c.Ears == 1,
            "ArmoryNeck" => c.Neck == 1,
            "ArmoryWrist" => c.Wrists == 1,
            "ArmoryRings" => c.FingerL == 1 || c.FingerR == 1,
            "ArmorySoulCrystal" => c.SoulCrystal == 1,
            _ => false,
        };
        if (!fits) throw new ToolException($"{InventoryTools.ItemName(itemId) ?? itemId.ToString()} does not belong in {container}.");
    }

    private static string Quote(IEnumerable<string> values) => string.Join(", ", values.Select(v => $"\"{v}\""));
}
