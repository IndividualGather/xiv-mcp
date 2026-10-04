using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Network.Structures;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Buying on the market board: XIV MCP walks to a market board in the current zone and opens it, asks for the item's listings
/// (InfoProxyItemSearch.RequestData, as the result window does), and buys the cheapest fitting listings with the game's purchase
/// request (SetLastPurchasedItem + SendPurchaseRequestPacket); Dalamud reports each finished purchase. No window clicks needed.
/// </summary>
internal static class MarketBoardTools
{
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(2);

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        yield return new McpTool
        {
            Name = "buy_from_market_board",
            Description = "Buys an item on the market board of the current world: walks to a market board in the current zone (be in a city with " +
                          "one: Limsa Lominsa, Gridania, Ul'dah or a later hub), opens it, reads the listings and buys the cheapest ones until " +
                          "'quantity' is reached, never above 'max_unit_price' gil per item. Listings are bought whole, so it may buy a few more. " +
                          "Gil purchases above the limit in /xivmcp ask in game first (or use a standing 'approval'). Requires 'Market & " +
                          "purchases' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": ["string", "integer"], "description": "Item name or id." },
                    "quantity": { "type": "integer", "minimum": 1, "maximum": 9999, "description": "How many (default 1)." },
                    "max_unit_price": { "type": "integer", "minimum": 1, "description": "Highest gil per item to pay." },
                    "hq": { "type": "boolean", "description": "Only HQ (true) or only NQ (false) listings; both when left out." },
                    "approval": { "type": "string", "description": "Id of a standing gil approval (request_spending_approval) to use instead of asking." }
                  },
                  "required": ["item", "max_unit_price"]
                }
                """,
            ReadOnly = false,
            Handler = (args, ct) => Buy(config, args, ct),
        };
    }

    private sealed record Listing(ulong ListingId, uint UnitPrice, uint Quantity, uint Tax, bool Hq, int Index);

    private static async Task<object?> Buy(Configuration config, ToolArgs args, CancellationToken ct)
    {
        var query = args.Node("item")?.ToString() ?? throw new ToolException("'item' is required.");
        var quantity = args.Int("quantity", 1, 1, 9999);
        var maxPrice = args.Int("max_unit_price", 0, 1, int.MaxValue);
        if (maxPrice == 0) throw new ToolException("'max_unit_price' is required: the most gil to pay per item.");
        bool? hq = args.Node("hq") is { } h ? h.GetValue<bool>() : null;

        var item = await Game.Run(() => ResolveTradable(query)).ConfigureAwait(false);
        var name = item.Name.ExtractText();
        var steps = new List<string>();

        // 1. A market board here, opened.
        await OpenMarketBoard(steps, ct).ConfigureAwait(false);

        // 2. The listings for the item.
        var listings = await RequestListings(item.RowId, ct).ConfigureAwait(false);
        var fitting = listings.Where(l => l.UnitPrice <= maxPrice && (hq is null || l.Hq == hq)).OrderBy(l => l.UnitPrice).ToList();
        var plan = new List<Listing>();
        foreach (var l in fitting)
        {
            if (plan.Sum(p => p.Quantity) >= quantity) break;
            plan.Add(l);
        }
        if (plan.Count == 0)
        {
            await Game.Run(CloseMarketBoard).ConfigureAwait(false);
            var cheapest = listings.Where(l => hq is null || l.Hq == hq).OrderBy(l => l.UnitPrice).FirstOrDefault();
            throw new ToolException(cheapest is null
                ? $"Nobody sells {name}{(hq is null ? "" : hq == true ? " HQ" : " NQ")} on this world right now."
                : $"The cheapest {name} costs {cheapest.UnitPrice:N0} gil each, above your limit of {maxPrice:N0}.");
        }
        var total = plan.Sum(p => (long)p.UnitPrice * p.Quantity + p.Tax);

        // 3. Approval, like buy_item: a standing approval, or ask above the gil limit.
        Approvals.Approval? approval = null;
        if (args.String("approval") is { } approvalId)
        {
            try { approval = Approvals.Get(approvalId, item.RowId); }
            catch (ToolException) { await Game.Run(CloseMarketBoard).ConfigureAwait(false); throw; }
        }
        if (approval is null && config.AskAboveGil > 0 && total > config.AskAboveGil)
            await Consent.Require($"Buy {name} on the market board?",
                [$"{plan.Sum(p => p.Quantity)} × {name} for {total:N0} gil in total ({string.Join(", ", plan.Select(p => $"{p.Quantity} at {p.UnitPrice:N0}"))})."],
                ConsentTimeout, ct).ConfigureAwait(false);

        // 4. Buy listing by listing; Dalamud reports each finished purchase.
        var bought = new List<object>();
        long spent = 0;
        foreach (var listing in plan)
        {
            ct.ThrowIfCancellationRequested();
            var done = await Purchase(item.RowId, listing, ct).ConfigureAwait(false);
            if (!done)
            {
                steps.Add($"A listing of {listing.Quantity} at {listing.UnitPrice:N0} was gone or refused.");
                continue;
            }
            var cost = (long)listing.UnitPrice * listing.Quantity + listing.Tax;
            spent += cost;
            bought.Add(new { quantity = listing.Quantity, unitPrice = listing.UnitPrice, hq = listing.Hq, gil = cost });
            if (approval is not null) Approvals.Spend(approval.Id, cost);
            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
        await Game.Run(CloseMarketBoard).ConfigureAwait(false);
        if (bought.Count == 0) throw new ToolException($"No {name} could be bought: the listings changed. Try again.");
        return new { item = name, bought, total = bought.Count, gilSpent = spent, steps };
    }

    // ---------------------------------------------------------------- market board

    private static readonly Lazy<HashSet<uint>> MarketBoardIds = new(() =>
        Svc.Data.GetExcelSheet<EObjName>(Dalamud.Game.ClientLanguage.English)
           .Where(n => n.Singular.ExtractText().Equals("market board", StringComparison.OrdinalIgnoreCase)).Select(n => n.RowId).ToHashSet());

    private static async Task OpenMarketBoard(List<string> steps, CancellationToken ct)
    {
        if (await Game.Run(() => RetainerUi.Ready("ItemSearch")).ConfigureAwait(false)) return;
        var ids = await Task.Run(() => MarketBoardIds.Value, ct).ConfigureAwait(false);
        if (await NavigationTools.WalkTo("Market Board", o => ids.Contains(o.BaseId), steps, ct).ConfigureAwait(false) is null)
            throw new ToolException("No market board in this zone: go to a city with one (Limsa Lominsa, Gridania, Ul'dah or a later hub).");
        await Game.Run(() =>
        {
            var board = Svc.Objects.Where(o => ids.Contains(o.BaseId)).OrderBy(o => Game.DistanceToPlayer(o.Position) ?? float.MaxValue).First();
            unsafe
            {
                var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)board.Address;
                TargetSystem.Instance()->InteractWithObject(native, true);
            }
            return true;
        }).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (await Game.Run(() => RetainerUi.Ready("ItemSearch")).ConfigureAwait(false)) { steps.Add("Opened the market board."); return; }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        throw new ToolException("The market board did not open.");
    }

    /// <summary>Asks the server for the item's listings (as the result window does) and waits until they are in.</summary>
    private static async Task<List<Listing>> RequestListings(uint itemId, CancellationToken ct)
    {
        await Game.Run(() =>
        {
            unsafe
            {
                var proxy = InfoProxyItemSearch.Instance();
                if (proxy == null) throw new ToolException("The market board's listings are not available.");
                proxy->SearchItemId = itemId;
                proxy->VirtualTable->RequestData(proxy);
            }
            return true;
        }).ConfigureAwait(false);
        await Task.Delay(500, ct).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var listings = await Game.Run(() =>
            {
                unsafe
                {
                    var proxy = InfoProxyItemSearch.Instance();
                    if (proxy->WaitingForListings || proxy->SearchItemId != itemId) return null;
                    var result = new List<Listing>();
                    for (var i = 0; i < proxy->ListingCount && i < proxy->Listings.Length; i++)
                    {
                        var l = proxy->Listings[i];
                        if (l.ItemId == itemId) result.Add(new Listing(l.ListingId, l.UnitPrice, l.Quantity, l.TotalTax, l.IsHqItem, i));
                    }
                    return result;
                }
            }).ConfigureAwait(false);
            if (listings is not null) return listings;
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        throw new ToolException("The market board did not send the listings (it may be busy; try again in a moment).");
    }

    /// <summary>Buys one listing: it must still be the same listing; done when Dalamud reports the purchase.</summary>
    private static async Task<bool> Purchase(uint itemId, Listing listing, CancellationToken ct)
    {
        var purchased = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPurchased(IMarketBoardPurchase p)
        {
            if (p.CatalogId == itemId) purchased.TrySetResult(true);
        }
        Svc.MarketBoard.ItemPurchased += OnPurchased;
        try
        {
            var sent = await Game.Run(() =>
            {
                unsafe
                {
                    var proxy = InfoProxyItemSearch.Instance();
                    if (listing.Index >= proxy->ListingCount) return false;
                    var entry = (MarketBoardListing*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref proxy->Listings[listing.Index]);
                    if (entry->ListingId != listing.ListingId) return false; // the list changed under us
                    return proxy->SetLastPurchasedItem(entry) && proxy->SendPurchaseRequestPacket();
                }
            }).ConfigureAwait(false);
            if (!sent) return false;
            var done = await Task.WhenAny(purchased.Task, Task.Delay(TimeSpan.FromSeconds(10), ct)).ConfigureAwait(false);
            return done == purchased.Task;
        }
        finally { Svc.MarketBoard.ItemPurchased -= OnPurchased; }
    }

    private static unsafe bool CloseMarketBoard()
    {
        foreach (var window in new[] { "ItemSearchResult", "ItemSearch" })
        {
            var ptr = RetainerUi.Ptr(window);
            if (!ptr.IsNull && ptr.IsVisible) RetainerUi.Fire((AtkUnitBase*)ptr.Address, true, -1);
        }
        return true;
    }

    private static Item ResolveTradable(string query)
    {
        var q = query.Trim();
        var sheet = Svc.Data.GetExcelSheet<Item>();
        var item = uint.TryParse(q, out var id) ? sheet.GetRowOrDefault(id)
                 : sheet.FirstOrDefault(i => i.Name.ExtractText().Equals(q, StringComparison.OrdinalIgnoreCase)) is { RowId: > 0 } byName ? byName
                 : Svc.Data.GetExcelSheet<Item>(Dalamud.Game.ClientLanguage.English).FirstOrDefault(i => i.Name.ExtractText().Equals(q, StringComparison.OrdinalIgnoreCase)) is { RowId: > 0 } english
                    ? sheet.GetRow(english.RowId) : (Item?)null;
        if (item is not { } found) throw new ToolException($"There is no item called '{query}'.");
        if (found.IsUntradable || found.ItemSearchCategory.RowId == 0) throw new ToolException($"{found.Name.ExtractText()} can't be sold on the market board.");
        return found;
    }
}
