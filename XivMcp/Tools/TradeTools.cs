using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMcp.Mcp;
using XivMcp.Permissions;
using XivMcp.Transfers;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Trading items and gil with another player (module "Trading"). XIV MCP only starts trades the assistant was asked for, with a
/// player standing nearby, and never accepts trades others start. Before the trade is completed, the player approves it in game
/// with what the trade window really holds on both sides; that approval is asked every time and cannot be allowed permanently.
/// Right before the final confirmation the window is checked again, and anything that changed stops the trade.
/// </summary>
internal static class TradeTools
{
    /// <summary>Addon rows that read "Trade" (the context menu entry and the trade window's button), and "Complete trade?".</summary>
    private static readonly uint[] TradeTexts = [53, 95, 201, 203, 531, 3411, 8890, 10375, 11423, 13788];
    private const uint CompleteTradePrompt = 102223, HighQualityPrompt = 102434;

    private const float TradeRange = 4f;
    private static readonly string[] InventoryAddons = ["InventoryExpansion", "InventoryLarge", "Inventory"];

    public static IEnumerable<McpTool> Create()
    {
        yield return new McpTool
        {
            Name = "get_trade",
            Description = "The trade window, if one is open: the other player, what each side offers (items and gil), and whether each side " +
                          "has pressed Trade.",
            Handler = (_, _) => Game.Run<object?>(() => Describe(Read())),
        };

        yield return new McpTool
        {
            Name = "trade_with_player",
            Description = "Trades items and gil with a player standing nearby: sends the trade request, waits for them to accept, puts " +
                          "'give' into the trade window, waits until they have put in their side and pressed Trade, then asks the player " +
                          "in game to approve the trade with exactly what both sides hold, and completes it. With 'receive', the trade is " +
                          "stopped unless the other side holds at least that. The window is checked again right before the final " +
                          "confirmation; anything that changed stops the trade. Every trade asks in game; that cannot be turned off. " +
                          "At most 5 stacks and 1,000,000 gil per side. Requires 'Trading' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "player": { "type": "string", "description": "The other player's name, as shown over their head; they must be within a few yalms." },
                    "give": { "$ref": "#/$defs/offer" },
                    "receive": { "$ref": "#/$defs/offer", "description": "What you expect in return (at least). Leave out for a gift." },
                    "timeout_seconds": { "type": "integer", "minimum": 30, "maximum": 600, "description": "How long to wait for the other player at each step (default 120)." }
                  },
                  "required": ["player"],
                  "$defs": {
                    "offer": {
                      "type": "object",
                      "properties": {
                        "items": { "type": "array", "items": { "type": "object", "properties": {
                          "item": { "type": "string", "description": "Name or item id." },
                          "quantity": { "type": "integer", "minimum": 1 },
                          "hq": { "type": "boolean", "description": "High quality (default: normal quality)." } }, "required": ["item"] } },
                        "gil": { "type": "integer", "minimum": 0, "maximum": 1000000 }
                      }
                    }
                  }
                }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = async (args, ct) =>
            {
                var playerName = args.String("player") ?? throw new ToolException("'player' is required.");
                var timeout = TimeSpan.FromSeconds(args.Int("timeout_seconds", 120, 30, 600));
                var give = await Game.Run(() => ParseOffer(args.Node("give") as JsonObject)).ConfigureAwait(false) ?? TradeOffer.Empty;
                var expect = await Game.Run(() => ParseOffer(args.Node("receive") as JsonObject)).ConfigureAwait(false);
                return await Trade(playerName, give, expect, timeout, ct).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "cancel_trade",
            Description = "Cancels the open trade window or trade request. Nothing changes hands.",
            ReadOnly = false,
            Handler = async (_, _) =>
            {
                var was = await Game.Run(() => Read().Trading).ConfigureAwait(false);
                if (was) await Game.Run(Refuse).ConfigureAwait(false);
                return new { cancelled = was };
            },
        };
    }

    // ------------------------------------------------------------------ the trade

    private static async Task<object> Trade(string playerName, TradeOffer give, TradeOffer? expect, TimeSpan timeout, CancellationToken ct)
    {
        if (TradeCheck.PlanProblem(give, expect) is { } planProblem) throw new ToolException(planProblem);
        var stacks = await Game.RunLoggedIn(() => PlanStacks(give)).ConfigureAwait(false);
        var countsBefore = await Game.Run(() => give.Totals().Keys.Select(k => k.ItemId).Distinct().ToDictionary(id => id, Items.CountInBags)).ConfigureAwait(false);
        var partner = await Game.RunLoggedIn(() => FindPlayer(playerName)).ConfigureAwait(false);
        var started = false;
        var completed = false;
        try
        {
            // 1. Close enough, then the request.
            await Approach(partner, ct).ConfigureAwait(false);
            await Game.Run(() => Request(partner.EntityId)).ConfigureAwait(false);
            started = true;
            if (!await GameWindows.WaitFor(() => GameWindows.Ready("Trade"), timeout, ct).ConfigureAwait(false))
                throw new ToolException($"{partner.Name} did not accept the trade request.");

            // 2. Our side.
            foreach (var stack in stacks) await AddStack(stack, ct).ConfigureAwait(false);
            if (give.Gil > 0)
            {
                await Game.Run(() => SetGil(give.Gil)).ConfigureAwait(false);
                if (!await GameWindows.WaitFor(() => Read().Give.Gil == give.Gil, TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false))
                {
                    // As a player does it: the gil field (trade window callback 2) opens the number box, which takes the amount.
                    await Game.Run(OpenGilInput).ConfigureAwait(false);
                    if (await GameWindows.WaitFor(() => GameWindows.Ready("InputNumeric"), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
                        await Game.Run(() => GameWindows.EnterNumber((int)give.Gil)).ConfigureAwait(false);
                }
                if (!await GameWindows.WaitFor(() => Read().Give.Gil == give.Gil, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false))
                    throw new ToolException("The gil did not show up in the trade window.");
            }

            // 3. Their side: wait until they have pressed Trade, so their offer is final.
            if (!await GameWindows.WaitFor(() => Read() is { Trading: false } or { RemoteLocked: true }, timeout, ct).ConfigureAwait(false))
                throw new ToolException($"{partner.Name} did not press Trade in time.");
            var window = await Game.Run(Read).ConfigureAwait(false);
            if (!window.Trading) throw new ToolException($"{partner.Name} cancelled the trade.");
            Check(give, expect, window);

            // 4. The player approves what the window holds.
            await Approve(partner.Name, window, expect).ConfigureAwait(false);
            Check(give, expect, await Game.Run(Read).ConfigureAwait(false));

            // 5. Trade, then the final confirmation, checked once more.
            if (!await Game.Run(PressTrade).ConfigureAwait(false)) throw new ToolException("The trade window's Trade button could not be pressed.");
            if (!await GameWindows.WaitFor(() => Prompt() is not null || !Read().Trading, timeout, ct).ConfigureAwait(false))
                throw new ToolException("The final trade confirmation did not appear.");
            for (var i = 0; i < 3 && await Game.Run(Prompt).ConfigureAwait(false) is { } prompt; i++)
            {
                var now = await Game.Run(Read).ConfigureAwait(false);
                Check(give, expect, now);
                if (prompt == HighQualityPrompt && give.Items.All(x => !x.Hq)) throw new ToolException("The game warns about giving a high-quality item that was not planned.");
                await Game.Run(() => Answer(yes: true)).ConfigureAwait(false);
                await Task.Delay(800, ct).ConfigureAwait(false);
            }

            // 6. Done once the window is gone and the items moved.
            var before = window;
            if (!await GameWindows.WaitFor(() => !Read().Trading, timeout, ct).ConfigureAwait(false))
                throw new ToolException($"{partner.Name} did not complete the trade in time.");
            completed = await Game.Run(() => GaveAway(give, countsBefore, before.PlayerGil)).ConfigureAwait(false);
            if (!completed) throw new ToolException($"The trade with {partner.Name} was cancelled before it completed; nothing changed hands.");
            return new
            {
                traded = true,
                with = partner.Name,
                gave = before.Give.Describe(Items.Name),
                received = before.Receive.Describe(Items.Name),
            };
        }
        finally
        {
            if (started && !completed) await Game.Run(() => { if (Read().Trading) Refuse(); return true; }).ConfigureAwait(false);
        }
    }

    private static void Check(TradeOffer give, TradeOffer? expect, Window window)
    {
        var problems = TradeCheck.Problems(give, window.Give, expect, window.Receive, Items.Name);
        if (problems.Count > 0) throw new ToolException($"The trade was stopped: {string.Join(" ", problems)}");
    }

    /// <summary>Asks in game, every time, with what both sides hold. Declining stops the trade.</summary>
    private static async Task Approve(string partner, Window window, TradeOffer? expect)
    {
        var details = new List<string>
        {
            $"You give: {window.Give.Describe(Items.Name)}.",
            $"You receive: {window.Receive.Describe(Items.Name)}.",
        };
        if (expect is null && !window.Receive.IsEmpty) details.Add("Your assistant did not say what you get in return; check it.");
        if (expect is null && window.Receive.IsEmpty) details.Add("You get nothing in return: this is a gift.");
        var request = new Consent.Request($"Complete the trade with {partner}?", details)
        {
            Deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2),
            Risk = RiskLevel.Critical,
            Warning = "Trades cannot be undone.",
        };
        if (await Consent.Ask(request, TimeSpan.FromMinutes(2), CancellationToken.None).ConfigureAwait(false) == ApprovalDecision.Denied)
            throw new ToolException("The player did not approve the trade; it was cancelled and nothing changed hands.");
    }

    // ------------------------------------------------------------------ game access (framework thread)

    /// <summary>A stack in the bags to put into the trade window, and how many of it.</summary>
    private sealed record Stack(InventoryType Container, int Slot, uint ItemId, bool Hq, int Quantity);

    private sealed record Window(bool Trading, string? Partner, TradeOffer Give, TradeOffer Receive, bool LocalLocked, bool RemoteLocked, long PlayerGil);

    private static TradeOffer? ParseOffer(JsonObject? o)
    {
        if (o is null) return null;
        var items = new List<TradeItem>();
        foreach (var entry in (o["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var a = new ToolArgs(entry);
            var item = Items.Resolve(a.String("item") ?? throw new ToolException("Each item needs 'item'."));
            if (item.IsUntradable) throw new ToolException($"{item.Name.ExtractText()} cannot be traded.");
            items.Add(new TradeItem(item.RowId, a.Bool("hq", false), a.Int("quantity", 1, 1, 999_999)));
        }
        var gil = new ToolArgs(o).Int("gil", 0, 0, (int)TradeOffer.MaxGil);
        return new TradeOffer(items, gil);
    }

    /// <summary>The bag stacks that make up what is given: fewest slots first, at most five.</summary>
    private static unsafe List<Stack> PlanStacks(TradeOffer give)
    {
        var im = InventoryManager.Instance();
        if (give.Gil > im->GetGil()) throw new ToolException($"You have only {im->GetGil():N0} gil.");
        var plan = new List<Stack>();
        foreach (var ((itemId, hq), wanted) in give.Totals())
        {
            var stacks = new List<(InventoryType, int, int)>();
            foreach (var container in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
            {
                var c = im->GetInventoryContainer(container);
                if (c == null) continue;
                for (var i = 0; i < c->Size; i++)
                {
                    var slot = c->GetInventorySlot(i);
                    if (slot == null || slot->ItemId != itemId || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) != hq) continue;
                    stacks.Add((container, i, (int)slot->Quantity));
                }
            }
            var left = wanted;
            foreach (var (container, slot, quantity) in stacks.OrderByDescending(s => s.Item3))
            {
                if (left <= 0) break;
                var take = Math.Min(left, quantity);
                plan.Add(new Stack(container, slot, itemId, hq, take));
                left -= take;
            }
            if (left > 0) throw new ToolException($"You have only {wanted - left} {Label(itemId, hq)} in your bags, not {wanted}.");
        }
        if (plan.Count > TradeOffer.MaxItems)
            throw new ToolException($"That takes {plan.Count} stacks from your bags; a trade holds {TradeOffer.MaxItems}. Merge stacks first (sort_inventory) or give less.");
        return plan;
    }

    /// <summary>The other player, as plain values: game objects may only be read on the framework thread.</summary>
    private sealed record Partner(string Name, uint EntityId);

    private static Partner FindPlayer(string name)
    {
        var self = Svc.Objects.LocalPlayer!;
        var players = Svc.Objects.OfType<IPlayerCharacter>().Where(p => p.Address != self.Address).ToList();
        var match = players.FirstOrDefault(p => p.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? (players.Where(p => Game.Matches(p.Name.TextValue, name)).ToList() is { Count: 1 } one ? one[0] : null);
        return match is null ? throw new ToolException($"No player named '{name}' is nearby.") : new Partner(match.Name.TextValue, match.EntityId);
    }

    /// <summary>Where the other player stands now, or null if they left. Framework thread.</summary>
    private static System.Numerics.Vector3? PositionOf(Partner partner) =>
        Svc.Objects.FirstOrDefault(o => o.EntityId == partner.EntityId)?.Position;

    private static bool InRange(Partner partner) => PositionOf(partner) is { } p && Game.DistanceToPlayer(p) <= TradeRange;

    /// <summary>Walks up to the other player with vnavmesh, or asks the player to.</summary>
    private static async Task Approach(Partner partner, CancellationToken ct)
    {
        if (await Game.Run(() => InRange(partner)).ConfigureAwait(false)) return;
        if (!await Game.Run(() => Navigation.VnavmeshLoaded).ConfigureAwait(false))
            throw new ToolException($"{partner.Name} is too far away to trade. Walk up to them first.");
        await Game.Run(() => PositionOf(partner) is { } p ? Navigation.MoveCloseTo(p, TradeRange - 1.5f)
                                                          : throw new ToolException($"{partner.Name} is no longer nearby.")).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => InRange(partner), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
            throw new ToolException($"Could not get close enough to {partner.Name} to trade.");
        await Game.Run(() => { Navigation.StopMoving(); return true; }).ConfigureAwait(false);
    }

    private static unsafe bool Request(uint entityId)
    {
        InventoryManager.Instance()->SendTradeRequest(entityId);
        return true;
    }

    private static unsafe bool SetGil(long gil)
    {
        InventoryManager.Instance()->SetTradeGilAmount((uint)gil);
        return true;
    }

    private static unsafe bool Refuse()
    {
        InventoryManager.Instance()->RefuseTrade();
        return true;
    }

    /// <summary>Puts one stack into the trade window: its context menu's Trade entry, then the quantity.</summary>
    private static async Task AddStack(Stack stack, CancellationToken ct)
    {
        var before = await Game.Run(() => Read().Give).ConfigureAwait(false);
        await Game.Run(() => OpenContextMenu(stack)).ConfigureAwait(false);
        if (!await GameWindows.WaitFor(() => GameWindows.Ready("ContextMenu"), TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            throw new ToolException($"The context menu of {Label(stack.ItemId, stack.Hq)} did not open.");
        await Game.Run(PickTradeEntry).ConfigureAwait(false);
        // Stacks of more than one ask how many.
        if (await GameWindows.WaitFor(() => GameWindows.Ready("InputNumeric"), TimeSpan.FromSeconds(1.5), ct).ConfigureAwait(false))
            await Game.Run(() => GameWindows.EnterNumber(stack.Quantity)).ConfigureAwait(false);
        var wanted = before.Totals().GetValueOrDefault((stack.ItemId, stack.Hq)) + stack.Quantity;
        if (!await GameWindows.WaitFor(() => Read().Give.Totals().GetValueOrDefault((stack.ItemId, stack.Hq)) == wanted, TimeSpan.FromSeconds(4), ct).ConfigureAwait(false))
            throw new ToolException($"{stack.Quantity} {Label(stack.ItemId, stack.Hq)} did not show up in the trade window.");
    }

    private static unsafe bool OpenContextMenu(Stack stack)
    {
        AtkUnitBase* owner = null;
        foreach (var name in InventoryAddons)
        {
            var a = GameWindows.Addon(name);
            if (a != null && a->IsVisible) { owner = a; break; }
        }
        AgentInventoryContext.Instance()->OpenForItemSlot(stack.Container, stack.Slot, 0, owner == null ? 0u : (uint)owner->Id);
        return true;
    }

    /// <summary>Picks the context menu's Trade entry by its text, as the dye tool picks Dye.</summary>
    private static unsafe bool PickTradeEntry()
    {
        var texts = GameWindows.Texts(TradeTexts);
        var menu = GameWindows.Addon("ContextMenu");
        var count = (int)menu->AtkValues[0].UInt;
        for (var i = 0; i < count && 8 + i < menu->AtkValuesCount; i++)
        {
            var v = menu->AtkValues[8 + i];
            if (v.Type is not (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.String8) || v.String.Value == null) continue;
            if (texts.Contains(Dalamud.Memory.MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue.Trim()))
            {
                RetainerUi.Fire(menu, true, 0, i, 0u);
                return true;
            }
        }
        RetainerUi.Fire(menu, true, -1);
        throw new ToolException("The item's context menu has no Trade entry (it may not be tradable).");
    }

    /// <summary>Opens the number box for the gil to give, as clicking the trade window's gil field does (recorded: callback 2).</summary>
    private static unsafe bool OpenGilInput()
    {
        var trade = GameWindows.Addon("Trade");
        if (trade == null) throw new ToolException("The trade window closed.");
        RetainerUi.Fire(trade, true, 2, null);
        return true;
    }

    private static unsafe bool PressTrade() => GameWindows.Press(GameWindows.Addon("Trade"), GameWindows.Texts(TradeTexts));

    /// <summary>The trade confirmation that is showing ("Complete trade?" or the high-quality warning), or null.</summary>
    private static unsafe uint? Prompt()
    {
        if (!GameWindows.Ready("SelectYesno")) return null;
        var texts = GameWindows.AllTexts(GameWindows.Addon("SelectYesno"));
        if (texts.Any(t => GameWindows.Texts(CompleteTradePrompt).Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase)))) return CompleteTradePrompt;
        if (texts.Any(t => GameWindows.Texts(HighQualityPrompt).Any(p => t.StartsWith(p, StringComparison.OrdinalIgnoreCase)))) return HighQualityPrompt;
        return null;
    }

    private static unsafe bool Answer(bool yes)
    {
        var yesno = (AddonSelectYesno*)GameWindows.Addon("SelectYesno");
        if (yesno == null || !yesno->AtkUnitBase.IsVisible) return false;
        RetainerUi.Fire(&yesno->AtkUnitBase, true, yes ? 0 : 1);
        return true;
    }

    /// <summary>The trade window as the game holds it: five item slots and the gil (sixth slot) of each side.</summary>
    private static unsafe Window Read()
    {
        var im = InventoryManager.Instance();
        var trading = im->TradeLocalState is not (0 or TradeState.NotTrading) && GameWindows.Ready("Trade");
        // The gil each side offers is not in the trade's item data; the window shows it: ours in the gil field (a component),
        // the other player's as plain text.
        var (ownGil, theirGil) = trading ? GameWindows.TradeGil(GameWindows.Addon("Trade")) : (0, 0);
        return new Window(
            trading,
            trading ? im->TradePartnerNameString : null,
            Side(im->TradeItemsLocal) with { Gil = ownGil },
            Side(im->TradeItemsRemote) with { Gil = theirGil },
            im->TradeLocalState is TradeState.LockedIn or TradeState.WaitingForConfirmation or TradeState.Confirmed,
            im->TradeRemoteState is TradeState.LockedIn or TradeState.WaitingForConfirmation or TradeState.Confirmed,
            im->GetGil());
    }

    private static TradeOffer Side(Span<InventoryItem> slots)
    {
        var items = new List<TradeItem>();
        for (var i = 0; i < Math.Min(TradeOffer.MaxItems, slots.Length); i++)
            if (slots[i].ItemId != 0 && slots[i].Quantity > 0)
                items.Add(new TradeItem(slots[i].ItemId, slots[i].Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), (int)slots[i].Quantity));
        var gil = slots.Length > TradeOffer.MaxItems ? slots[TradeOffer.MaxItems].Quantity : 0;
        return new TradeOffer(items, gil);
    }

    /// <summary>
    /// Whether the trade went through: the given gil and items have left the bags. A trade that gives nothing counts as done once
    /// the window closed without being cancelled, which cannot be told apart here, so it is taken as done.
    /// </summary>
    private static unsafe bool GaveAway(TradeOffer give, IReadOnlyDictionary<uint, int> countsBefore, long gilBefore)
    {
        if (give.Gil > 0) return InventoryManager.Instance()->GetGil() <= gilBefore - give.Gil;
        foreach (var (id, before) in countsBefore)
        {
            var given = give.Totals().Where(t => t.Key.ItemId == id).Sum(t => t.Value);
            if (Items.CountInBags(id) > before - given) return false;
        }
        return true;
    }

    private static object Describe(Window w) => w.Trading
        ? new
        {
            trading = true,
            with = w.Partner,
            give = w.Give.Describe(Items.Name),
            receive = w.Receive.Describe(Items.Name),
            youPressedTrade = w.LocalLocked,
            theyPressedTrade = w.RemoteLocked,
        }
        : new { trading = false, with = (string?)null, give = (string?)null, receive = (string?)null, youPressedTrade = false, theyPressedTrade = false };

    private static string Label(uint itemId, bool hq) => hq ? $"{Items.Name(itemId)} (HQ)" : Items.Name(itemId);
}
