using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Inventory;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Reading cached retainer inventories, opening/closing retainers and moving items between them.</summary>
internal static class RetainerTools
{
    private const string Player = "player";

    private static readonly GameInventoryType[] Bags =
        [GameInventoryType.Inventory1, GameInventoryType.Inventory2, GameInventoryType.Inventory3, GameInventoryType.Inventory4];

    private static readonly GameInventoryType[] RetainerPages =
        [GameInventoryType.RetainerPage1, GameInventoryType.RetainerPage2, GameInventoryType.RetainerPage3, GameInventoryType.RetainerPage4,
         GameInventoryType.RetainerPage5, GameInventoryType.RetainerPage6, GameInventoryType.RetainerPage7];

    public static IEnumerable<McpTool> Create(Configuration config, RetainerTracker tracker)
    {
        void RequireEnabled()
        {
            if (!config.AllowInventoryActions)
                throw new ToolException("Inventory actions are disabled. Enable \"Allow inventory actions\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "get_retainer_inventories",
            Description = "Inventories of the character's retainers — items, equipped gear, crystals and market listings (with prices) — from the retainer cache. " +
                          "The game only sends a retainer's inventory while that retainer is open, so every retainer has its own snapshot age; " +
                          "each result carries a 'cache' block (age, stale flag, how to refresh). Filter by retainer name and/or item name. " +
                          "Use wait_for_cache_refresh(cache=\"retainers\") to wait for the user to open a retainer.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "retainer": { "type": "string", "description": "Retainer name (default: all retainers)." },
                    "query": { "type": "string", "description": "Only items whose name contains this text." },
                    "include_market": { "type": "boolean", "description": "Include market board listings (default true)." },
                    "include_equipment": { "type": "boolean", "description": "Include retainer gear and crystals (default false)." },
                    "all_characters": { "type": "boolean", "description": "Include retainers of all characters seen by the plugin (default false)." }
                  }
                }
                """,
            Handler = (args, _) => Svc.Framework.RunOnFrameworkThread<object?>(() =>
            {
                var retainerFilter = args.String("retainer");
                var query = args.String("query");
                var includeMarket = args.Bool("include_market", true);
                var includeEquip = args.Bool("include_equipment", false);
                var characters = SelectCharacters(tracker, args.Bool("all_characters", false));

                return characters.Select(c => new
                {
                    character = c.Label,
                    retainerList = CacheFreshness.Describe(c.ListCapturedUtc, IsLive(c), RetainerTracker.ListRefreshHint),
                    retainers = c.Retainers
                        .Where(r => retainerFilter is null || r.Name.Equals(retainerFilter, StringComparison.OrdinalIgnoreCase) || Game.Matches(r.Name, retainerFilter))
                        .Select(r => new
                        {
                            name = r.Name,
                            gil = r.Gil,
                            itemCount = r.ItemCount,
                            cache = r.InventoryCapturedUtc is { } t
                                ? CacheFreshness.Describe(t, IsLive(c) && RetainerUi.InventoryOpenFor(r.Name), RetainerTracker.InventoryRefreshHint(r.Name))
                                : new { notCaptured = true, suggestion = "No inventory snapshot yet. To capture it: " + RetainerTracker.InventoryRefreshHint(r.Name) },
                            items = Items(r.Items, query),
                            market = includeMarket ? Items(r.Market, query) : null,
                            equipment = includeEquip ? Items(r.Equipped, query) : null,
                            crystals = includeEquip ? Items(r.Crystals, query) : null,
                        }).ToList(),
                }).ToList();
            }),
        };

        yield return new McpTool
        {
            Name = "open_retainer",
            Description = "Opens a retainer's inventory at the summoning bell (selects it in the retainer list, clicks through the greeting and chooses " +
                          "\"Entrust or withdraw items\"). The retainer list must already be open (interact with a summoning bell), or another retainer — " +
                          "which is closed first. Opening refreshes that retainer's cached inventory. Requires 'Allow inventory actions' in /xivmcp.",
            InputSchema = """
                { "type": "object", "properties": { "retainer": { "type": "string", "description": "Retainer name." } }, "required": ["retainer"] }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var name = args.String("retainer") ?? throw new ToolException("'retainer' is required.");
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Game.RunLoggedIn(() => { InventoryActionTools.EnsureNotBusy(); return true; }).ConfigureAwait(false);
                    await RetainerUi.Open(name, ct).ConfigureAwait(false);
                    await Task.Delay(1200, ct).ConfigureAwait(false); // give the tracker a poll to capture the inventory
                    return new { opened = await Svc.Framework.RunOnFrameworkThread(() => RetainerUi.ActiveRetainerName).ConfigureAwait(false) };
                }
                finally { InventoryActionTools.Gate.Release(); }
            },
        };

        yield return new McpTool
        {
            Name = "close_retainer",
            Description = "Closes the currently open retainer (inventory window, then \"Quit\" in the retainer menu) and returns to the retainer list. " +
                          "Requires 'Allow inventory actions' in /xivmcp.",
            ReadOnly = false,
            Handler = async (_, ct) =>
            {
                RequireEnabled();
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var name = await Svc.Framework.RunOnFrameworkThread(() => RetainerUi.ActiveRetainerName).ConfigureAwait(false);
                    await RetainerUi.Close(ct).ConfigureAwait(false);
                    return new { closed = name };
                }
                finally { InventoryActionTools.Gate.Release(); }
            },
        };

        yield return new McpTool
        {
            Name = "transfer_retainer_items",
            Description = "Moves whole item stacks between retainers, or between a retainer and the player's bags, at a summoning bell. " +
                          "Each transfer is { item_id, from, to, stacks?, hq? } where from/to is a retainer name or \"player\". " +
                          "Retainer-to-retainer goes through the player's bags (the game has no direct transfer): open source retainer → withdraw " +
                          "→ close → open target retainer → entrust. Needs enough free bag slots for the stacks in flight and the retainer list open " +
                          "at a summoning bell. Every move is confirmed by the server before the next one. Requires 'Allow inventory actions' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "transfers": {
                      "type": "array",
                      "description": "Transfers, executed in order (max 50).",
                      "items": {
                        "type": "object",
                        "properties": {
                          "item_id": { "type": "integer" },
                          "from": { "type": "string", "description": "Retainer name or \"player\"." },
                          "to": { "type": "string", "description": "Retainer name or \"player\"." },
                          "stacks": { "type": "integer", "description": "Number of stacks to move (default: all stacks of that item)." },
                          "hq": { "type": "boolean", "description": "Only HQ (true) or only NQ (false) stacks." }
                        },
                        "required": ["item_id", "from", "to"]
                      }
                    },
                    "close_when_done": { "type": "boolean", "description": "Close the last opened retainer at the end (default true)." }
                  },
                  "required": ["transfers"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                if (args.Node("transfers") is not JsonArray list || list.Count == 0) throw new ToolException("'transfers' must be a non-empty array.");
                if (list.Count > 50) throw new ToolException("At most 50 transfers per call.");
                var transfers = list.Select((n, i) => Transfer.Parse(n as JsonObject, i)).ToList();
                var closeWhenDone = args.Bool("close_when_done", true);

                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Game.RunLoggedIn(() =>
                    {
                        InventoryActionTools.EnsureNotBusy();
                        if (!RetainerUi.RetainerListOpen && RetainerUi.ActiveRetainerName is null)
                            throw new ToolException("Open the retainer list at a summoning bell first.");
                        return true;
                    }).ConfigureAwait(false);

                    var results = new List<object>();
                    foreach (var t in transfers)
                    {
                        var result = await RunTransfer(t, config, ct).ConfigureAwait(false);
                        results.Add(result);
                        if (!result.Ok) break;
                    }
                    if (closeWhenDone) await RetainerUi.Close(ct).ConfigureAwait(false);
                    return new { completed = results.Cast<TransferResult>().Count(r => r.Ok), requested = transfers.Count, results };
                }
                finally { InventoryActionTools.Gate.Release(); }
            },
        };
    }

    private sealed record Transfer(int Index, uint ItemId, string From, string To, int? Stacks, bool? Hq)
    {
        public static Transfer Parse(JsonObject? o, int index)
        {
            if (o is null) throw new ToolException($"Transfer #{index}: must be an object.");
            var a = new ToolArgs(o);
            var item = a.UInt("item_id") ?? throw new ToolException($"Transfer #{index}: 'item_id' is required.");
            var from = a.String("from") ?? throw new ToolException($"Transfer #{index}: 'from' is required.");
            var to = a.String("to") ?? throw new ToolException($"Transfer #{index}: 'to' is required.");
            if (from.Equals(to, StringComparison.OrdinalIgnoreCase)) throw new ToolException($"Transfer #{index}: from and to are the same.");
            return new Transfer(index, item, from, to, o.ContainsKey("stacks") ? a.Int("stacks", 1, 1, 175) : null, o.ContainsKey("hq") ? a.Bool("hq", false) : null);
        }
    }

    private sealed record TransferResult(int Index, bool Ok, string Item, string From, string To, int StacksMoved, int QuantityMoved, string? Error, List<InventoryActionTools.MoveResult> Moves);

    private static async Task<TransferResult> RunTransfer(Transfer t, Configuration config, CancellationToken ct)
    {
        var itemName = InventoryTools.ItemName(t.ItemId) ?? t.ItemId.ToString();
        var moves = new List<InventoryActionTools.MoveResult>();
        TransferResult Fail(string error, int stacks = 0, int qty = 0) => new(t.Index, false, itemName, t.From, t.To, stacks, qty, error, moves);

        var fromPlayer = t.From.Equals(Player, StringComparison.OrdinalIgnoreCase);
        var toPlayer = t.To.Equals(Player, StringComparison.OrdinalIgnoreCase);
        var delay = TimeSpan.FromMilliseconds(Math.Clamp(config.MoveDelayMs, 200, 5000));

        try
        {
            // 1) Get the stacks into the player's bags (withdraw from the source retainer if needed).
            List<(GameInventoryType Container, int Slot, int Quantity)> inBags;
            if (fromPlayer)
            {
                inBags = await Svc.Framework.RunOnFrameworkThread(() => FindStacks(Bags, t.ItemId, t.Hq, t.Stacks)).ConfigureAwait(false);
                if (inBags.Count == 0) return Fail($"{itemName} is not in your bags.");
                if (toPlayer) return Fail("from and to are both the player.");
            }
            else
            {
                await RetainerUi.Open(t.From, ct).ConfigureAwait(false);
                var stacks = await Svc.Framework.RunOnFrameworkThread(() => FindStacks(RetainerPages, t.ItemId, t.Hq, t.Stacks)).ConfigureAwait(false);
                if (stacks.Count == 0) return Fail($"{t.From} has no {itemName}.");
                var free = await Svc.Framework.RunOnFrameworkThread(() => CountEmpty(Bags)).ConfigureAwait(false);
                if (free < stacks.Count) return Fail($"Need {stacks.Count} free bag slots, you have {free}.");

                inBags = [];
                foreach (var s in stacks)
                {
                    var target = await Svc.Framework.RunOnFrameworkThread(() => FirstEmpty(Bags)).ConfigureAwait(false);
                    if (target is null) return Fail("Bags are full.", inBags.Count, inBags.Sum(b => b.Quantity));
                    var r = await InventoryActionTools.ExecuteMove(
                        new InventoryActionTools.MoveRequest(moves.Count, s.Container, s.Slot, null, null, target.Value.Container, target.Value.Slot), ct).ConfigureAwait(false);
                    moves.Add(r);
                    if (!r.Ok) return Fail($"Withdrawing failed: {r.Reason}", inBags.Count, inBags.Sum(b => b.Quantity));
                    inBags.Add((target.Value.Container, target.Value.Slot, s.Quantity));
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                if (toPlayer) return new TransferResult(t.Index, true, itemName, t.From, t.To, inBags.Count, inBags.Sum(b => b.Quantity), null, moves);
            }

            // 2) Entrust the stacks to the target retainer.
            await RetainerUi.Open(t.To, ct).ConfigureAwait(false);
            var entrusted = 0;
            var quantity = 0;
            foreach (var b in inBags)
            {
                var target = await Svc.Framework.RunOnFrameworkThread(() => FirstEmpty(RetainerPages)).ConfigureAwait(false);
                if (target is null)
                    return Fail($"{t.To}'s inventory is full; {inBags.Count - entrusted} stack(s) stayed in your bags.", entrusted, quantity);
                var r = await InventoryActionTools.ExecuteMove(
                    new InventoryActionTools.MoveRequest(moves.Count, b.Container, b.Slot, null, null, target.Value.Container, target.Value.Slot), ct).ConfigureAwait(false);
                moves.Add(r);
                if (!r.Ok) return Fail($"Entrusting failed: {r.Reason}. {inBags.Count - entrusted} stack(s) are in your bags.", entrusted, quantity);
                entrusted++;
                quantity += b.Quantity;
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            return new TransferResult(t.Index, true, itemName, t.From, t.To, entrusted, quantity, null, moves);
        }
        catch (ToolException ex)
        {
            return Fail(ex.Message);
        }
    }

    private static List<(GameInventoryType Container, int Slot, int Quantity)> FindStacks(GameInventoryType[] containers, uint itemId, bool? hq, int? max)
    {
        var result = new List<(GameInventoryType, int, int)>();
        foreach (var c in containers)
            foreach (var item in Svc.Inventory.GetInventoryItems(c))
            {
                if (item.IsEmpty || item.BaseItemId != itemId || (hq is { } h && item.IsHq != h)) continue;
                result.Add((c, (int)item.InventorySlot, item.Quantity));
                if (max is { } m && result.Count >= m) return result;
            }
        return result;
    }

    private static (GameInventoryType Container, int Slot)? FirstEmpty(GameInventoryType[] containers)
    {
        foreach (var c in containers)
            if (InventoryActionTools.FirstEmptySlot(Svc.Inventory.GetInventoryItems(c)) is { } slot) return (c, slot);
        return null;
    }

    private static int CountEmpty(GameInventoryType[] containers)
    {
        var n = 0;
        foreach (var c in containers)
            foreach (var item in Svc.Inventory.GetInventoryItems(c))
                if (item.IsEmpty) n++;
        return n;
    }

    private static List<CharacterRetainers> SelectCharacters(RetainerTracker tracker, bool all)
    {
        if (all) return tracker.All();
        if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded)
            throw new ToolException("No character is logged in. Use all_characters=true to see saved retainer data.");
        return tracker.Get(Svc.PlayerState.ContentId) is { } c
            ? [c]
            : throw new ToolException("No retainer data recorded yet for this character. To capture it: " + RetainerTracker.ListRefreshHint +
                                      " Each retainer's inventory is captured when that retainer is opened.");
    }

    private static bool IsLive(CharacterRetainers c) =>
        Svc.ClientState.IsLoggedIn && Svc.PlayerState.ContentId == c.ContentId && (RetainerUi.RetainerListOpen || RetainerUi.InventoryOpen);

    private static List<object> Items(List<RetainerItem> items, string? query) =>
        items.Select(i => (i, name: InventoryTools.ItemName(i.ItemId)))
             .Where(x => Game.Matches(x.name, query))
             .Select(x => (object)new
             {
                 container = x.i.Container,
                 slot = x.i.Slot,
                 itemId = x.i.ItemId,
                 name = x.name,
                 quantity = x.i.Quantity,
                 hq = x.i.Hq ? true : (bool?)null,
                 price = x.i.Price,
             }).ToList();
}
