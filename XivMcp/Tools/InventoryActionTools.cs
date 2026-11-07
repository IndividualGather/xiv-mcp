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
                          "SaddleBag1/2, PremiumSaddleBag1/2, RetainerPage1-7, FreeCompanyPage1-5, and for crystals Crystals, RetainerCrystals and " +
                          "FreeCompanyCrystals). Saddlebag/retainer/company chest windows must be open; the chest's item tabs a move needs are selected " +
                          "automatically (the game only sends a tab once it is selected). 'quantity' moves part of a stack: it is split off " +
                          "first (needs an empty slot in the source container). Crystals move between crystal containers only, into the " +
                          "slot of their kind; a slot holds at most 9,999, so only what fits moves (the result says how many). Part of a crystal " +
                          "stack (a 'quantity', or what fits) moves between your crystals and a retainer's through the crystal's Entrust to " +
                          "Retainer / Retrieve from Retainer menu; the company chest takes whole stacks. " +
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
                          "quantity": { "type": "integer", "minimum": 1, "description": "Move only this many of the stack (default: the whole stack). Crystals: between your crystals and a retainer's." },
                          "hq": { "type": "boolean", "description": "With item_id: only HQ (true) or only NQ (false) stacks." },
                          "if_at_least": { "type": "integer", "minimum": 1, "description": "Only move when the source stack holds at least this many; otherwise the move is skipped (not failed). E.g. crystals near the cap, as a job step." },
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
                    var moved = results.Cast<MoveResult>().Count(r => r.Ok && r.Status != "skipped");
                    Svc.Log.Information($"[MCP] Moved {moved}/{requests.Count} item(s)");
                    return new
                    {
                        requested = requests.Count,
                        moved,
                        failed = results.Cast<MoveResult>().Count(r => !r.Ok),
                        skipped = requests.Count - results.Count + results.Cast<MoveResult>().Count(r => r.Status == "skipped"),
                        seconds = Math.Round(sw.Elapsed.TotalSeconds, 1),
                        pauseBetweenMoves = config.MoveDelayDescription,
                        pausesMs = pauses,
                        results,
                    };
                }
                finally { Gate.Release(); }
            },
        };

        yield return new McpTool
        {
            Name = "use_item",
            Description = "Uses an item from the bags like from a hotbar or its context menu's Use: reads a recipe book, folklore tome or orchestrion " +
                          "roll, uses a manual, food, potion or a mount/minion item. A Yes/No question the game asks about it is answered Yes. " +
                          "Uses one at a time ('quantity' for several) and only counts it when one left the bags. Out of combat, not while crafting " +
                          "or gathering. Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string", "description": "Item name or id." },
                    "quantity": { "type": "integer", "minimum": 1, "maximum": 99, "description": "How many to use, one after another (default 1)." },
                    "hq": { "type": "boolean", "description": "Use the HQ one (default: HQ if the bags hold one)." }
                  },
                  "required": ["item"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var quantity = args.Int("quantity", 1, 1, 99);
                var item = await Game.RunLoggedIn(() => Items.Resolve(args.String("item") ?? throw new ToolException("'item' is required."))).ConfigureAwait(false);
                var name = item.Name.ExtractText();
                if (item.ItemAction.RowId == 0) throw new ToolException($"{name} can't be used.");
                await Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var used = 0;
                    var messages = new List<string>();
                    for (var n = 0; n < quantity; n++)
                    {
                        var (nq, hq) = await Game.RunLoggedIn(() => CountNqHq(item.RowId)).ConfigureAwait(false);
                        if (nq + hq == 0)
                        {
                            if (n == 0) throw new ToolException($"No {name} in the bags.");
                            messages.Add("None left in the bags.");
                            break;
                        }
                        var useHq = args.Node("hq") is null ? hq > 0 : args.Bool("hq", false);
                        if (useHq ? hq == 0 : nq == 0) throw new ToolException($"No {(useHq ? "HQ" : "NQ")} {name} in the bags.");
                        var actionId = item.RowId + (useHq ? 1_000_000u : 0);
                        var before = nq + hq;
                        // Right after another item the game can swallow a use without a message, so a use that shows no effect is sent once more.
                        var gone = false;
                        for (var attempt = 0; attempt < 2 && !gone; attempt++)
                        {
                            if (await UseOnce(actionId, ct).ConfigureAwait(false) is { } refused)
                            {
                                if (used == 0) throw new ToolException($"{name} wasn't used: {refused}");
                                messages.Add(refused);
                                break;
                            }
                            // The game may ask first (reading a book, a one-time unlock): this tool was called to use it, so answer Yes.
                            gone = await GameWindows.WaitFor(() =>
                            {
                                if (GameWindows.YesNoPrompt() is { } prompt)
                                {
                                    messages.Add($"Answered Yes: {prompt}");
                                    GameWindows.AnswerYesNo(true);
                                    return false;
                                }
                                return Items.CountInBags(item.RowId) < before;
                            }, TimeSpan.FromSeconds(attempt == 0 ? 6 : 15), ct).ConfigureAwait(false);
                        }
                        if (!gone)
                        {
                            if (used == 0) throw new ToolException($"The game didn't use {name} (it is still in the bags).");
                            messages.Add("The game didn't use it (it is still in the bags).");
                            break;
                        }
                        used++;
                        await Task.Delay(1500, ct).ConfigureAwait(false);
                    }
                    Svc.Log.Information($"[MCP] Used {used}x {name}");
                    return new
                    {
                        item = name,
                        used,
                        requested = quantity,
                        leftInBags = await Game.Run(() => Items.CountInBags(item.RowId)).ConfigureAwait(false),
                        messages,
                    };
                }
                finally { Gate.Release(); }
            },
        };
    }

    /// <summary>NQ and HQ count of an item in the bags, as the game counts them. Framework thread.</summary>
    private static unsafe (int Nq, int Hq) CountNqHq(uint itemId)
    {
        var im = InventoryManager.Instance();
        if (im == null) return (0, 0);
        return (im->GetInventoryItemCount(itemId, false, false, false), im->GetInventoryItemCount(itemId, true, false, false));
    }

    /// <summary>
    /// Uses an item action like a hotbar does, once the game allows it (waits up to 10 s, e.g. for a cast or teleport to end). Returns why
    /// the game refused it, or null when it was sent.
    /// </summary>
    private static async Task<string?> UseOnce(uint actionId, CancellationToken ct)
    {
        await Game.RunLoggedIn(() => { EnsureNotBusy(); return true; }).ConfigureAwait(false);
        var until = DateTime.UtcNow.AddSeconds(10);
        uint status;
        while (true)
        {
            status = await Game.Run(() =>
            {
                unsafe
                {
                    var am = ActionManager.Instance();
                    var s = am->GetActionStatus(ActionType.Item, actionId);
                    if (s == 0) am->UseAction(ActionType.Item, actionId, 0xE0000000, 65535);
                    return s;
                }
            }).ConfigureAwait(false);
            if (status == 0 || DateTime.UtcNow > until) break;
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
        if (status == 0) return null;
        var why = await Game.Run(() => Svc.Data.GetExcelSheet<LogMessage>().GetRowOrDefault(status)?.Text.ExtractText()).ConfigureAwait(false);
        return string.IsNullOrEmpty(why) ? $"the game refused it (action status {status})" : why;
    }

    /// <summary>A move whose 'if_at_least' isn't met: reported as skipped, not as a failure.</summary>
    private sealed class SkipMove(string message) : Exception(message);

    internal sealed record MoveRequest(int Index, GameInventoryType? From, int? FromSlot, uint? ItemId, bool? Hq, GameInventoryType To, int? ToSlot, int? Quantity = null, int? IfAtLeast = null)
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
            var quantity = m.ContainsKey("quantity") ? a.Int("quantity", 1, 1, 999_999) : (int?)null;
            var ifAtLeast = m.ContainsKey("if_at_least") ? a.Int("if_at_least", 1, 1, 999_999) : (int?)null;
            return new MoveRequest(index, from, fromSlot, itemId, hq, to, toSlot, quantity, ifAtLeast);
        }
    }

    internal sealed record MoveResult(int Index, bool Ok, string Status, string? Item, string? From, string? To, string? Reason)
    {
        public static MoveResult Fail(int index, string reason, string? item = null, string? from = null, string? to = null) =>
            new(index, false, "failed", item, from, to, reason);
    }

    /// <summary>True while the game still waits for the server to answer an earlier inventory operation.</summary>
    internal static unsafe bool HasPendingOperation()
    {
        var im = InventoryManager.Instance();
        if (im == null) return false;
        foreach (ref var op in im->PendingOperations)
            if (!op.IsEmpty) return true;
        return false;
    }

    internal static async Task<bool> WaitForNoPendingOperation(TimeSpan timeout, CancellationToken ct)
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
        if (TouchesChest(req))
        {
            try { await PluginCompat.WaitForFcch(TimeSpan.FromMinutes(2), ct).ConfigureAwait(false); }
            catch (ToolException ex) { return MoveResult.Fail(req.Index, ex.Message); }
        }
        // The game only sends a chest tab once it is selected: select the ones this move takes from or puts into (all of them when the
        // item is looked for everywhere). Nothing happens while the chest window is closed.
        var pages = Transfers.ChestTabs.PagesFor(req.From?.ToString(), req.To.ToString(), searchesAll: req.ItemId is not null && req.From is null);
        if (pages.Count > 0)
        {
            var unloaded = await FcChestTools.LoadPages(pages, ct).ConfigureAwait(false);
            if (unloaded.Count > 0 && TouchesChest(req))
                return MoveResult.Fail(req.Index, $"The company chest's tab {string.Join(", ", unloaded)} isn't shown in the chest window (the company rank gives no access to it).");
        }

        // Part of a stack: split it off first, then move the new stack. Crystals can't be split; they take a quantity in the move.
        if (req.Quantity is not null && !IsCrystalContainer(req.To))
        {
            try { req = await SplitOff(req, ct).ConfigureAwait(false); }
            catch (ToolException ex) { return MoveResult.Fail(req.Index, ex.Message); }
        }

        // Resolve and validate against the current state, then issue the move — all in one framework tick.
        (GameInventoryType From, int FromSlot, GameInventoryType To, int ToSlot, uint ItemId, int Quantity, uint DstItemId, int DstQuantity, string? Name,
            int Amount, bool Partial, string? Note)? plan;
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
                if (req.IfAtLeast is { } least && (src.IsEmpty || src.Quantity < least))
                    throw new SkipMove($"{(src.IsEmpty ? 0 : src.Quantity)} there, fewer than {least}");
                if (src.IsEmpty) throw new ToolException($"{from}[{fromSlot}] is empty.");

                var dstItems = Svc.Inventory.GetInventoryItems(to);
                if (IsCrystalContainer(from) != IsCrystalContainer(to))
                    throw new ToolException("Crystals only move between crystal containers (Crystals, RetainerCrystals, FreeCompanyCrystals).");
                // Crystal containers have one slot per kind of crystal: the stack goes to the same slot on the other side.
                var toSlot = IsCrystalContainer(to) ? fromSlot : req.ToSlot ?? FirstEmptySlot(dstItems) ?? throw new ToolException($"{to} has no empty slot.");
                if (toSlot < 0 || toSlot >= dstItems.Length) throw new ToolException($"{to} has slots 0-{dstItems.Length - 1}.");
                if (from == to && fromSlot == toSlot) throw new ToolException("Source and target are the same slot.");
                var dst = dstItems[toSlot];

                EnsureFitsArmory(src.BaseItemId, to);
                if (!dst.IsEmpty) EnsureFitsArmory(dst.BaseItemId, from); // the swapped item lands in the source container

                // A crystal slot holds at most 9,999 and the game rejects a move over that: only what fits moves, and part of a stack
                // goes through the context menu's quantity entry.
                var crystals = IsCrystalContainer(to)
                    ? Transfers.CrystalMove.For(req.Quantity, (int)src.Quantity, dst.IsEmpty || dst.ItemId != src.ItemId ? 0 : (int)dst.Quantity)
                    : new Transfers.CrystalMove.Plan((int)src.Quantity, null, null);
                if (crystals.Problem is { } problem) throw new ToolException(problem);
                var partial = crystals.Partial((int)src.Quantity);

                if (!partial)
                    unsafe
                    {
                        var rc = InventoryManager.Instance()->MoveItemSlot((InventoryType)from, (ushort)fromSlot, (InventoryType)to, (ushort)toSlot, true);
                        if (rc != 0) Svc.Log.Debug($"MoveItemSlot returned {rc}");
                    }
                return ((GameInventoryType, int, GameInventoryType, int, uint, int, uint, int, string?, int, bool, string?)?)
                    (from, fromSlot, to, toSlot, src.ItemId, (int)src.Quantity, dst.ItemId, (int)dst.Quantity, InventoryTools.ItemName(src.BaseItemId),
                     crystals.Amount, partial, crystals.Note);
            }).ConfigureAwait(false);
        }
        catch (SkipMove skip)
        {
            return new MoveResult(req.Index, true, "skipped", null, null, null, skip.Message);
        }
        catch (ToolException ex)
        {
            return MoveResult.Fail(req.Index, ex.Message);
        }

        var p = plan!.Value;
        var fromText = $"{p.From}[{p.FromSlot}]";
        var toText = $"{p.To}[{p.ToSlot}]";

        if (p.Partial)
        {
            try { await MoveCrystalQuantity(p.From, p.FromSlot, p.To, p.Amount, ct).ConfigureAwait(false); }
            catch (ToolException ex) { return MoveResult.Fail(req.Index, ex.Message, p.Name, fromText, toText); }
        }
        var note = p.Partial || p.Note is not null ? $"{p.Amount:N0} moved{(p.Note is null ? "" : $": {p.Note}")}" : null;

        // Wait for the server to confirm: the target slot must now hold the moved item (or, for part of a crystal stack, that many more).
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            var confirmed = await Svc.Framework.RunOnFrameworkThread(() =>
            {
                if (HasPendingOperation()) return false;
                // The source must have changed too: a container the game loads late (the company chest) can show the same item in the
                // target without the move having happened.
                var src = Slot(p.From, p.FromSlot);
                if (!src.IsEmpty && src.ItemId == p.ItemId && src.Quantity == p.Quantity) return false;
                var dst = Slot(p.To, p.ToSlot);
                if (p.Partial) return dst.ItemId == p.ItemId && dst.Quantity == (p.DstItemId == p.ItemId ? p.DstQuantity : 0) + p.Amount;
                var merged = p.DstItemId == p.ItemId && dst.ItemId == p.ItemId && dst.Quantity != p.DstQuantity;
                return (dst.ItemId == p.ItemId && dst.ItemId != p.DstItemId) || merged;
            }).ConfigureAwait(false);
            if (confirmed)
                return new MoveResult(req.Index, true, p.DstItemId != 0 && p.DstItemId != p.ItemId ? "swapped" : "moved", p.Name, fromText, toText, note);
        }
        return MoveResult.Fail(req.Index, "The game did not confirm the move within 4 seconds (it may have been rejected, e.g. item not allowed there).", p.Name, fromText, toText);
    }

    private static readonly string[] PlayerInventoryAddons = ["InventoryExpansion", "InventoryLarge", "Inventory"];
    private static readonly string[] RetainerInventoryAddons = ["InventoryRetainerLarge", "InventoryRetainer"];
    // Items offer "Entrust/Retrieve Quantity"; crystals only "Entrust to Retainer" / "Retrieve from Retainer", which asks how many.
    private static readonly uint[] EntrustTexts = [772, 97], RetrieveTexts = [773, 98];

    /// <summary>
    /// Moves part of a crystal stack between your crystals and a retainer's, as the player would: the crystal's context menu,
    /// "Entrust to Retainer" or "Retrieve from Retainer" (or an item's "... Quantity"), then the number the game asks for.
    /// </summary>
    private static async Task MoveCrystalQuantity(GameInventoryType from, int fromSlot, GameInventoryType to, int amount, CancellationToken ct)
    {
        var entry = (from, to) switch
        {
            (GameInventoryType.Crystals, GameInventoryType.RetainerCrystals) => EntrustTexts,
            (GameInventoryType.RetainerCrystals, GameInventoryType.Crystals) => RetrieveTexts,
            _ => throw new ToolException("Part of a crystal stack only moves between your crystals and a retainer's; the company chest takes whole stacks."),
        };
        await Game.Run(() => OpenItemContextMenu(from, fromSlot)).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("ContextMenu"), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            throw new ToolException("The crystal's context menu did not open.");
        await Game.Run(() => PickContextEntry(GameWindows.Texts(entry))).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("InputNumeric"), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            throw new ToolException("The game did not ask how many crystals to move.");
        await Game.Run(() => GameWindows.EnterNumber(amount)).ConfigureAwait(false);
    }

    private static unsafe bool OpenItemContextMenu(GameInventoryType container, int slot)
    {
        var names = container == GameInventoryType.RetainerCrystals ? RetainerInventoryAddons : PlayerInventoryAddons;
        uint ownerId = 0;
        foreach (var name in names)
        {
            var a = GameWindows.Addon(name);
            if (a != null && a->IsVisible) { ownerId = a->Id; break; }
        }
        if (ownerId == 0) throw new ToolException("The inventory window the crystals are in isn't open.");
        FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance()->OpenForItemSlot((InventoryType)container, slot, 0, ownerId);
        return true;
    }

    /// <summary>Picks a context menu entry by its text; closes the menu and names its entries when none matches.</summary>
    private static unsafe bool PickContextEntry(IReadOnlySet<string> texts)
    {
        var menu = GameWindows.Addon("ContextMenu");
        var count = (int)menu->AtkValues[0].UInt;
        var seen = new List<string>();
        for (var i = 0; i < count && 8 + i < menu->AtkValuesCount; i++)
        {
            var v = menu->AtkValues[8 + i];
            if (v.Type is not (FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String or FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.ManagedString
                or FFXIVClientStructs.FFXIV.Component.GUI.AtkValueType.String8) || v.String.Value == null) continue;
            var text = Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue.Trim();
            seen.Add(text);
            if (texts.Contains(text))
            {
                RetainerUi.Fire(menu, true, 0, i, 0u);
                return true;
            }
        }
        RetainerUi.Fire(menu, true, -1);
        throw new ToolException($"The context menu has no \"{string.Join("\" or \"", texts)}\" entry (it has: {string.Join(", ", seen)}).");
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
               n.StartsWith("RetainerPage", StringComparison.Ordinal) || n.StartsWith("FreeCompanyPage", StringComparison.Ordinal) ||
               IsCrystalContainer(t);
    });

    private static bool TouchesChest(MoveRequest req) =>
        req.To.ToString().StartsWith("FreeCompany", StringComparison.Ordinal) || req.From?.ToString().StartsWith("FreeCompany", StringComparison.Ordinal) == true;

    internal static bool IsCrystalContainer(GameInventoryType t) =>
        t is GameInventoryType.Crystals or GameInventoryType.RetainerCrystals or GameInventoryType.FreeCompanyCrystals;

    /// <summary>
    /// Splits <see cref="MoveRequest.Quantity"/> off the source stack (the game puts it into an empty slot of the same container) and
    /// returns the request for moving the new stack. The whole stack, or more, needs no split.
    /// </summary>
    private static async Task<MoveRequest> SplitOff(MoveRequest req, CancellationToken ct)
    {
        var plan = await Game.RunLoggedIn(() =>
        {
            EnsureNotBusy();
            var (from, fromSlot) = ResolveSource(req);
            EnsureContainerUsable(from);
            var src = Slot(from, fromSlot);
            if (src.IsEmpty) throw new ToolException($"{from}[{fromSlot}] is empty.");
            if (req.Quantity >= src.Quantity) return ((List<(GameInventoryType Container, int Slot)> empty, uint ItemId)?)null;
            if (IsCrystalContainer(from)) throw new ToolException("Crystals only move between crystal containers (Crystals, RetainerCrystals, FreeCompanyCrystals).");
            // Check the target first, so a failed move does not leave a split stack behind.
            EnsureContainerUsable(req.To);
            if (req.ToSlot is null && FirstEmptySlot(Svc.Inventory.GetInventoryItems(req.To)) is null)
                throw new ToolException($"{req.To} has no empty slot.");
            // The split stack goes to an empty slot of the same inventory: for the bags, any of the four pages.
            var pages = BagPages.Contains(from) ? BagPages : [from];
            var empty = new List<(GameInventoryType Container, int Slot)>();
            foreach (var page in pages)
            {
                var items = Svc.Inventory.GetInventoryItems(page);
                for (var i = 0; i < items.Length; i++)
                    if (items[i].IsEmpty) empty.Add((page, i));
            }
            if (empty.Count == 0) throw new ToolException($"Splitting needs an empty slot in {(pages.Length > 1 ? "your bags" : from.ToString())}.");
            unsafe
            {
                var rc = InventoryManager.Instance()->SplitItem((InventoryType)from, (ushort)fromSlot, req.Quantity!.Value);
                if (rc != 0) Svc.Log.Debug($"SplitItem returned {rc}");
            }
            return (empty, src.ItemId);
        }).ConfigureAwait(false);
        if (plan is not { } p) return req with { Quantity = null };

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
            var found = await Svc.Framework.RunOnFrameworkThread<(GameInventoryType, int)?>(() =>
            {
                if (HasPendingOperation()) return null;
                foreach (var (container, i) in p.empty)
                {
                    var item = Slot(container, i);
                    if (!item.IsEmpty && item.ItemId == p.ItemId && item.Quantity == req.Quantity) return (container, i);
                }
                return null;
            }).ConfigureAwait(false);
            if (found is { } at) return req with { From = at.Item1, FromSlot = at.Item2, ItemId = null, Hq = null, Quantity = null };
        }
        throw new ToolException("The game did not confirm splitting the stack within 4 seconds.");
    }

    private static readonly GameInventoryType[] BagPages =
        [GameInventoryType.Inventory1, GameInventoryType.Inventory2, GameInventoryType.Inventory3, GameInventoryType.Inventory4];

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
        else if (n.StartsWith("FreeCompany", StringComparison.Ordinal)) EnsureWindowOpen(["FreeCompanyChest"], "company chest");
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
