using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Network.Structures;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// The market board through retainers: prices (Universalis), sale notices, the retainer sale history, putting items up for sale and
/// repricing (undercutting) listings. Live competing listings come from the game's own "Compare prices" (Dalamud's market board
/// event, as Penny Pincher uses); the undercut rules are Penny Pincher's settings when it is installed.
/// </summary>
internal static class MarketTools
{
    private const uint AddonSellFromBags = 2380;      // "Sell items in your inventory on the market."
    private const uint AddonSellFromRetainer = 2381;  // "Sell items in your retainer's inventory on the market."
    private const uint AddonSaleHistory = 2382;       // "View sale history."
    private const int MaxListings = 20;

    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];
    private static readonly InventoryType[] RetainerPages =
        [InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
         InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7];

    private static DateTime lastCompare = DateTime.MinValue;

    public static IEnumerable<McpTool> Create(Configuration config, RetainerTracker tracker, SalesTracker sales, PluginCompat compat)
    {
        void RequireMarket()
        {
            if (!config.AllowMarketPurchases)
                throw new ToolException("Market actions are disabled. Enable \"Market & purchases\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "get_market_prices",
            Description = "Market prices from universalis.app for items: current lowest listings (NQ/HQ, retainer, world), recent sales, average " +
                          "price and sales per day, for your home world (default), data center or region. Universalis data is crowd-sourced and " +
                          "may lag behind the live market board. Requires 'Online lookups' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "items": { "type": "array", "items": { "type": "string" }, "description": "Item names or ids." },
                    "scope": { "type": "string", "enum": ["world", "datacenter", "region"], "description": "Default world (your home world)." },
                    "listings": { "type": "integer", "description": "Listings per item (default 5)." },
                    "sales": { "type": "integer", "description": "Recent sales per item (default 5)." }
                  },
                  "required": ["items"]
                }
                """,
            Handler = async (args, ct) =>
            {
                ItemSourceTools.RequireOnline(config);
                var (ids, scope) = await Game.RunLoggedIn(() =>
                {
                    var items = args.StringList("items").Select(i => Items.Resolve(i).RowId).ToList();
                    if (items.Count == 0) throw new ToolException("'items' is required.");
                    var s = Universalis.HomeScopes();
                    return (items, args.String("scope") switch { "datacenter" => s.DataCenter, "region" => s.Region, _ => s.World });
                }).ConfigureAwait(false);
                var data = await Universalis.Get(ids, scope, args.Int("listings", 5, 0, 50), args.Int("sales", 5, 0, 50), ct).ConfigureAwait(false);
                return ids.Select(id => data.TryGetValue(id, out var m) ? DescribeMarket(m) : new { item = Items.Name(id), untradeable = true }).ToList();
            },
        };

        yield return new McpTool
        {
            Name = "get_market_listings",
            Description = "Your retainers' current market listings (item, quantity, price per unit, HQ) from the retainer cache, with when each " +
                          "retainer was last seen. compare=true adds Universalis' lowest competing price per item (needs 'Online lookups') and flags " +
                          "listings that have been undercut. To check live prices and reprice, use reprice_listings at a summoning bell.",
            InputSchema = """
                { "type": "object", "properties": { "retainer": { "type": "string" }, "compare": { "type": "boolean", "description": "Compare with Universalis (default false)." } } }
                """,
            Handler = async (args, ct) =>
            {
                var filter = args.String("retainer");
                var (character, own) = await Game.RunLoggedIn(() => (tracker.Get(Svc.PlayerState.ContentId), OwnRetainerNames())).ConfigureAwait(false);
                if (character is null) throw new ToolException("No retainer data cached yet; open the retainer list at a summoning bell once.");
                var retainers = character.Retainers.Where(r => filter is null || Game.Matches(r.Name, filter)).ToList();
                Dictionary<uint, Universalis.ItemMarket>? market = null;
                if (args.Bool("compare", false))
                {
                    ItemSourceTools.RequireOnline(config);
                    var world = await Game.Run(() => Universalis.HomeScopes().World).ConfigureAwait(false);
                    market = await Universalis.Get(retainers.SelectMany(r => r.Market).Select(m => m.ItemId).ToList(), world, 10, 0, ct).ConfigureAwait(false);
                }
                return await Game.Run(() => (object?)retainers.Where(r => r.Market.Count > 0 || r.MarketItemCount > 0).Select(r => new
                {
                    retainer = r.Name,
                    listings = r.Market.Select(m =>
                    {
                        Universalis.Listing? lowest = null;
                        if (market?.TryGetValue(m.ItemId, out var mk) == true)
                            lowest = mk.Listings.Where(l => (!m.Hq || l.Hq) && !own.Contains(l.Retainer ?? "")).OrderBy(l => l.PricePerUnit).FirstOrDefault();
                        return new
                        {
                            item = Items.Name(m.ItemId),
                            hq = m.Hq ? true : (bool?)null,
                            quantity = m.Quantity,
                            pricePerUnit = m.Price,
                            lowestCompetitor = lowest is null ? null : new { price = lowest.PricePerUnit, retainer = lowest.Retainer, seen = lowest.Reviewed },
                            undercut = lowest is not null && m.Price is { } p ? lowest.PricePerUnit < (long)p : (bool?)null,
                        };
                    }).ToList(),
                    listingCount = r.MarketItemCount,
                    expires = r.MarketExpire == 0 ? (DateTime?)null : DateTimeOffset.FromUnixTimeSeconds(r.MarketExpire).UtcDateTime,
                    seen = r.InventoryCapturedUtc,
                }).ToList()).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "get_sales",
            Description = "Items that sold: the game's retainer sale notices recorded while XIV MCP was running (item, quantity, gil after fees, " +
                          "time — including the notices shown at login), plus listings that disappeared since a retainer was last seen. For the full " +
                          "per-retainer record (buyer, date, price) use get_sale_history at a summoning bell.",
            InputSchema = """
                { "type": "object", "properties": { "since_hours": { "type": "integer", "description": "Only notices from the last N hours (default 168)." } } }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                var since = DateTime.UtcNow.AddHours(-args.Int("since_hours", 168, 1, 24 * 365));
                var notices = sales.For(Svc.PlayerState.ContentId, since);
                return new
                {
                    notices = notices.Select(n => new { when = n.Utc, item = n.Item, hq = n.Hq ? true : (bool?)null, quantity = n.Quantity, gil = n.Gil }).ToList(),
                    totalGil = notices.Sum(n => n.Gil ?? 0),
                    note = notices.Count == 0 ? "No sale notices recorded in that time (notices are only seen while the game and XIV MCP run)." : null,
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_sale_history",
            Description = "Reads a retainer's sale history window (the last 20 sales: item, quantity, total price, buyer, date) at the summoning bell. " +
                          "The retainer list must be open. Requires 'Market & purchases' in /xivmcp.",
            InputSchema = """
                { "type": "object", "properties": { "retainer": { "type": "string" } }, "required": ["retainer"] }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireMarket();
                var name = args.String("retainer") ?? throw new ToolException("'retainer' is required.");
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var retainer = await Game.RunLoggedIn(() => { RequireBell(); compat.AcquireBell(); return ResolveRetainer(name); }).ConfigureAwait(false);
                    await RetainerUi.OpenMenu(retainer, ct).ConfigureAwait(false);
                    await SelectAndWait(AddonSaleHistory, "RetainerHistory", ct).ConfigureAwait(false);
                    await Task.Delay(800, ct).ConfigureAwait(false); // rows arrive from the server
                    var rows = await Game.Run(ReadHistory).ConfigureAwait(false);
                    await Game.Run(() => { CloseAddon("RetainerHistory"); return true; }).ConfigureAwait(false);
                    await RetainerUi.Close(ct).ConfigureAwait(false);
                    return new { retainer, sales = rows, count = rows.Count };
                }
                finally
                {
                    InventoryActionTools.Gate.Release();
                }
            },
        };

        yield return new McpTool
        {
            Name = "sell_item",
            Description = "Puts an item up for sale on the market board through a retainer, from your bags or that retainer's inventory (one stack " +
                          "= one listing). Without 'price' it checks the live market board (\"Compare prices\") and undercuts the cheapest competing " +
                          "listing — never your own retainers — using Penny Pincher's settings if installed (undercut amount, rounding, minimum), " +
                          "else 1 gil. Refuses a price far below the item's recent Universalis average unless allowed. The retainer list must be " +
                          "open. Requires 'Market & purchases' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "retainer": { "type": "string" },
                    "item": { "type": "string", "description": "Item name or id." },
                    "hq": { "type": "boolean", "description": "Sell the HQ stack (default: HQ if you have one)." },
                    "quantity": { "type": "integer", "description": "How many from the stack (default the whole stack)." },
                    "price": { "type": "integer", "description": "Price per unit; omit to undercut the live market." },
                    "allow_low_price": { "type": "boolean", "description": "Allow a price under half the recent Universalis average (default false)." }
                  },
                  "required": ["retainer", "item"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireMarket();
                var name = args.String("retainer") ?? throw new ToolException("'retainer' is required.");
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                var steps = new List<string>();
                try
                {
                    var plan = await Game.RunLoggedIn(() =>
                    {
                        RequireBell();
                        InventoryActionTools.EnsureNotBusy();
                        var item = Items.Resolve(args.String("item") ?? throw new ToolException("'item' is required."));
                        if (item.IsUntradable) throw new ToolException($"{item.Name.ExtractText()} can't be sold on the market board.");
                        var retainer = ResolveRetainer(name);
                        if (RetainerListingCount(retainer) >= MaxListings) throw new ToolException($"{retainer} already has {MaxListings} listings.");
                        compat.AcquireBell();
                        return (Item: item.RowId, ItemName: item.Name.ExtractText(), Retainer: retainer);
                    }).ConfigureAwait(false);

                    await RetainerUi.OpenMenu(plan.Retainer, ct).ConfigureAwait(false);
                    steps.Add($"Opened {plan.Retainer}.");
                    // Where is the stack? Bags first, then this retainer's inventory (loaded once the retainer is open).
                    var stack = await Game.Run(() => FindStack(plan.Item, args.Node("hq") is null ? null : args.Bool("hq", false))).ConfigureAwait(false)
                                ?? throw new ToolException($"No {plan.ItemName} in your bags or {plan.Retainer}'s inventory.");
                    var quantity = Math.Clamp(args.Int("quantity", stack.Quantity, 1, 9999), 1, stack.Quantity);
                    await SelectAndWait(stack.FromBags ? AddonSellFromBags : AddonSellFromRetainer, "RetainerSellList", ct).ConfigureAwait(false);
                    await Game.Run(() => { unsafe { AgentRetainer.Instance()->OpenRetainerSell(stack.Container, (ushort)stack.Slot); } return true; }).ConfigureAwait(false);
                    await WaitFor("RetainerSell", ct).ConfigureAwait(false);
                    steps.Add($"Selling {quantity}x {plan.ItemName}{(stack.Hq ? " (HQ)" : "")} from {(stack.FromBags ? "your bags" : "the retainer")}.");

                    var outcome = await PriceAndConfirm(plan.Item, stack.Hq, quantity, args.Node("price") is null ? null : args.Int("price", 0, 1, 999_999_999),
                                                        currentPrice: null, args.Bool("allow_low_price", false), maxDropPercent: null, dryRun: false, config, steps, ct).ConfigureAwait(false);
                    await Game.Run(() => { CloseAddon("RetainerSellList"); return true; }).ConfigureAwait(false);
                    await RetainerUi.Close(ct).ConfigureAwait(false);
                    return new { retainer = plan.Retainer, item = plan.ItemName, quantity, outcome, steps };
                }
                finally
                {
                    // However the sale ended: no sell window or market list is left in front of the retainer's menu.
                    await Game.Run(() => { CloseAddon("RetainerSell"); CloseAddon("ItemSearchResult"); CloseAddon("RetainerSellList"); return true; }).ConfigureAwait(false);
                    InventoryActionTools.Gate.Release();
                }
            },
        };

        yield return new McpTool
        {
            Name = "reprice_listings",
            Description = "Checks your retainers' listings against the live market board and undercuts competing offers: for each listing it opens " +
                          "\"Adjust price\", runs \"Compare prices\" and, if someone else is cheaper, sets your price just below them (Penny Pincher's " +
                          "settings if installed, else 1 gil; your own retainers are never undercut). Listings that are already cheapest are left " +
                          "alone. A cut of more than max_drop_percent (default 30) is skipped and reported instead, so a single dumped listing can't " +
                          "crash your price. mode=set sets an exact price for one item. dry_run=true only reports. The retainer list must be open. " +
                          "Requires 'Market & purchases' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "retainer": { "type": "string", "description": "Only this retainer (default: every retainer with listings)." },
                    "item": { "type": "string", "description": "Only listings of this item." },
                    "mode": { "type": "string", "enum": ["undercut", "set"], "description": "Default undercut." },
                    "price": { "type": "integer", "description": "For mode=set: the new price per unit." },
                    "max_drop_percent": { "type": "integer", "description": "Skip cuts larger than this (default 30)." },
                    "dry_run": { "type": "boolean", "description": "Only report what would change (default false)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireMarket();
                var mode = args.String("mode") ?? "undercut";
                var setPrice = args.Node("price") is null ? (int?)null : args.Int("price", 0, 1, 999_999_999);
                if (mode == "set" && (setPrice is null || args.String("item") is null)) throw new ToolException("mode=set needs 'item' and 'price'.");
                var dryRun = args.Bool("dry_run", false);
                var maxDrop = args.Int("max_drop_percent", 30, 1, 99);
                await InventoryActionTools.Gate.WaitAsync(ct).ConfigureAwait(false);
                var results = new List<object>();
                var steps = new List<string>();
                try
                {
                    var targets = await Game.RunLoggedIn(() =>
                    {
                        RequireBell();
                        InventoryActionTools.EnsureNotBusy();
                        compat.AcquireBell();
                        var only = args.String("retainer") is { } r ? ResolveRetainer(r) : null;
                        var itemFilter = args.String("item") is { } i ? Items.Resolve(i).RowId : (uint?)null;
                        return (Retainers: RetainersWithListings().Where(n => only is null || n == only).ToList(), Item: itemFilter);
                    }).ConfigureAwait(false);
                    if (targets.Retainers.Count == 0) throw new ToolException("No retainer with market listings (or the named retainer has none).");

                    foreach (var retainer in targets.Retainers)
                    {
                        ct.ThrowIfCancellationRequested();
                        await RetainerUi.OpenMenu(retainer, ct).ConfigureAwait(false);
                        await SelectAndWait(AddonSellFromBags, "RetainerSellList", ct).ConfigureAwait(false);
                        await Task.Delay(500, ct).ConfigureAwait(false);
                        var listings = await Game.Run(ReadListings).ConfigureAwait(false);
                        foreach (var listing in listings.Where(l => targets.Item is null || l.ItemId == targets.Item))
                        {
                            ct.ThrowIfCancellationRequested();
                            var itemName = Items.Name(listing.ItemId);
                            if (!await OpenAdjust(listing.Slot, ct).ConfigureAwait(false))
                            {
                                results.Add(new { retainer, item = itemName, skipped = "Could not open \"Adjust price\" for this listing." });
                                continue;
                            }
                            var outcome = await PriceAndConfirm(listing.ItemId, listing.Hq, null, mode == "set" ? setPrice : null, listing.Price,
                                                                allowLow: mode == "set", maxDropPercent: mode == "set" ? null : maxDrop, dryRun, config, steps, ct).ConfigureAwait(false);
                            results.Add(new { retainer, item = itemName, hq = listing.Hq ? true : (bool?)null, quantity = listing.Quantity, outcome });
                            await Game.Run(() => { CloseAddon("RetainerSell"); return true; }).ConfigureAwait(false);
                            await Task.Delay(400, ct).ConfigureAwait(false);
                        }
                        await Game.Run(() => { CloseAddon("RetainerSellList"); return true; }).ConfigureAwait(false);
                        await RetainerUi.Close(ct).ConfigureAwait(false);
                    }
                    return new { dryRun, rules = await Game.Run(() => Rules().Describe).ConfigureAwait(false), results };
                }
                finally
                {
                    await Game.Run(() => { CloseAddon("RetainerSell"); return true; }).ConfigureAwait(false);
                    InventoryActionTools.Gate.Release();
                }
            },
        };
    }

    // ------------------------------------------------------------------ pricing

    /// <summary>Undercut rules: Penny Pincher's settings when installed, else 1 gil below, own retainers excluded.</summary>
    private sealed record UndercutRules(int Delta, int Min, int Mod, int Multiple, bool UndercutSelf, bool HqOnlyForHq, string Source)
    {
        public long Apply(long lowest)
        {
            var price = lowest - lowest % Math.Max(1, Mod) - Delta;
            price -= price % Math.Max(1, Multiple);
            return Math.Max(price, Min);
        }

        public string Describe => $"{Source}: {Delta} gil below the cheapest competitor" + (Mod > 1 ? $", modulo {Mod}" : "") + (Multiple > 1 ? $", multiple of {Multiple}" : "") +
                                  $", minimum {Min}, {(UndercutSelf ? "including" : "never")} your own retainers, HQ listings compared with {(HqOnlyForHq ? "HQ only" : "all")}";
    }

    private static UndercutRules Rules()
    {
        if (PluginCompat.IsLoaded("PennyPincher"))
        {
            try
            {
                var c = PluginTools.ReadPluginJson("PennyPincher", null);
                return new UndercutRules(c["delta"]?.GetValue<int>() ?? 1, c["min"]?.GetValue<int>() ?? 1, c["mod"]?.GetValue<int>() ?? 1, c["multiple"]?.GetValue<int>() ?? 1,
                                         c["undercutSelf"]?.GetValue<bool>() ?? false, c["hq"]?.GetValue<bool>() ?? true, "Penny Pincher settings");
            }
            catch (Exception ex) { Svc.Log.Debug($"[MCP] Penny Pincher config unreadable: {ex.Message}"); }
        }
        return new UndercutRules(1, 1, 1, 1, false, true, "default");
    }

    /// <summary>
    /// With the RetainerSell window open: decide the price (given, or undercut from a live "Compare prices"), check it against the
    /// guards, set price/quantity and confirm. Returns a description of what happened.
    /// </summary>
    private static async Task<object> PriceAndConfirm(uint itemId, bool hq, int? quantity, int? price, long? currentPrice, bool allowLow, int? maxDropPercent,
                                                      bool dryRun, Configuration config, List<string> steps, CancellationToken ct)
    {
        var rules = await Game.Run(Rules).ConfigureAwait(false);
        long? cheapest = null;
        string? cheapestRetainer = null;
        long newPrice;
        if (price is { } p) newPrice = p;
        else
        {
            var offers = await ComparePrices(itemId, ct).ConfigureAwait(false);
            var own = await Game.Run(OwnRetainerIds).ConfigureAwait(false);
            var competitor = offers
                .Where(o => rules.UndercutSelf || !own.Contains(o.RetainerId))
                .Where(o => !(hq && rules.HqOnlyForHq) || o.IsHq)
                .OrderBy(o => o.PricePerUnit).FirstOrDefault();
            if (competitor is null)
            {
                if (currentPrice is { } keep) return new { action = "kept", price = keep, reason = "no competing listing" };
                // Nobody else sells it: the recent average sale price, when online lookups are allowed.
                var average = config.AllowOnlineData ? await RecentAverage(itemId, hq, ct).ConfigureAwait(false) : null;
                if (average is not { } avgPrice || avgPrice < 1)
                    throw new ToolException("No competing listing on the market board to undercut, and no recent sales to go by; give 'price'.");
                newPrice = (long)Math.Round(avgPrice);
                steps.Add($"Nobody else lists it; using the recent average sale price, {newPrice:N0} gil.");
            }
            else
            {
                cheapest = competitor.PricePerUnit;
                cheapestRetainer = competitor.RetainerName;
                if (currentPrice is { } cur && cur <= cheapest && !rules.UndercutSelf)
                    return new { action = "kept", price = cur, reason = $"already the cheapest (next: {cheapest:N0} by {cheapestRetainer})" };
                newPrice = rules.Apply(competitor.PricePerUnit);
            }
        }

        // Guards against dumping.
        if (maxDropPercent is { } drop && currentPrice is { } before && newPrice < before * (100 - drop) / 100)
            return new { action = "skipped", price = before, wouldBe = newPrice, cheapest, cheapestRetainer,
                         reason = $"cut of {100 - newPrice * 100 / before}% exceeds max_drop_percent {drop}; rerun with a higher limit or mode=set if intended" };
        if (!allowLow && price is null && config.AllowOnlineData)
        {
            var world = await Game.Run(() => Universalis.HomeScopes().World).ConfigureAwait(false);
            var market = await Universalis.Get([itemId], world, 0, 10, ct).ConfigureAwait(false);
            if (market.TryGetValue(itemId, out var m) && (hq ? m.AveragePriceHq : m.AveragePriceNq) is { } avg && newPrice < avg / 2)
                return new { action = "skipped", wouldBe = newPrice, recentAverage = avg, reason = "below half the recent average sale price; set allow_low_price or give 'price'" };
        }

        if (dryRun) return new { action = "would set", from = currentPrice, to = newPrice, cheapest, cheapestRetainer };

        await Game.Run(() =>
        {
            unsafe
            {
                var sell = (AddonRetainerSell*)Svc.GameGui.GetAddonByName<AtkUnitBase>("RetainerSell", 1);
                if (sell == null) throw new ToolException("The sell window closed.");
                RetainerUi.Fire(&sell->AtkUnitBase, true, 2, (int)newPrice);
                if (quantity is { } q) RetainerUi.Fire(&sell->AtkUnitBase, true, 3, q);
            }
            return true;
        }).ConfigureAwait(false);
        await Task.Delay(Random.Shared.Next(400, 700), ct).ConfigureAwait(false);
        var confirmed = await Game.Run(() =>
        {
            unsafe
            {
                var sell = (AddonRetainerSell*)Svc.GameGui.GetAddonByName<AtkUnitBase>("RetainerSell", 1);
                if (sell == null) return false;
                if (sell->AskingPrice != null && sell->AskingPrice->Value != newPrice) throw new ToolException($"The price field shows {sell->AskingPrice->Value}, not {newPrice}; nothing confirmed.");
                return VentureTools.Click(&sell->AtkUnitBase, sell->Confirm);
            }
        }).ConfigureAwait(false);
        if (!confirmed) throw new ToolException("Could not click Confirm in the sell window.");
        if (!await WaitGone("RetainerSell", ct).ConfigureAwait(false)) throw new ToolException("The sell window did not close after Confirm.");
        steps.Add($"Set {Items.Name(itemId)} to {newPrice:N0} gil.");
        return new { action = currentPrice is null ? "listed" : "repriced", from = currentPrice, to = newPrice, cheapest, cheapestRetainer };
    }

    private static async Task<double?> RecentAverage(uint itemId, bool hq, CancellationToken ct)
    {
        var world = await Game.Run(() => Universalis.HomeScopes().World).ConfigureAwait(false);
        var market = await Universalis.Get([itemId], world, 0, 10, ct).ConfigureAwait(false);
        return market.TryGetValue(itemId, out var m) ? (hq ? m.AveragePriceHq : m.AveragePriceNq) : null;
    }

    /// <summary>Clicks "Compare prices" in the open sell window and waits for the market board listings the server sends.</summary>
    private static async Task<List<IMarketBoardItemListing>> ComparePrices(uint itemId, CancellationToken ct)
    {
        // The server refuses market searches that come too fast ("Please wait and try your search again").
        var wait = TimeSpan.FromSeconds(3) - (DateTime.UtcNow - lastCompare);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);

        var received = new TaskCompletionSource<List<IMarketBoardItemListing>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(IMarketBoardCurrentOfferings offers)
        {
            if (offers.ItemListings.Count == 0 || offers.ItemListings[0].ItemId == itemId) received.TrySetResult(offers.ItemListings.ToList());
        }
        Svc.MarketBoard.OfferingsReceived += Handler;
        using var feedback = new GameCommands.Feedback();
        try
        {
            var clicked = await Game.Run(() =>
            {
                unsafe
                {
                    var sell = (AddonRetainerSell*)Svc.GameGui.GetAddonByName<AtkUnitBase>("RetainerSell", 1);
                    return sell != null && VentureTools.Click(&sell->AtkUnitBase, sell->ComparePrices);
                }
            }).ConfigureAwait(false);
            if (!clicked) throw new ToolException("Could not click \"Compare prices\".");
            lastCompare = DateTime.UtcNow;
            // Nobody selling: the server sends no offerings, the search window just shows no results. Tell that from a throttled search
            // (the game says "Please wait and try your search again").
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            DateTime? windowSince = null;
            while (DateTime.UtcNow < deadline)
            {
                if (received.Task.IsCompleted) return received.Task.Result;
                if (feedback.Messages.Any(m => m.Contains("Please wait", StringComparison.OrdinalIgnoreCase)))
                    throw new ToolException("The market board is throttling searches (\"Please wait and try your search again\"); retry shortly.");
                windowSince = await Game.Run(() => GameWindows.Ready("ItemSearchResult")).ConfigureAwait(false) ? windowSince ?? DateTime.UtcNow : null;
                if (DateTime.UtcNow - windowSince > TimeSpan.FromSeconds(3)) return [];
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            if (received.Task.IsCompleted) return received.Task.Result;
            throw new ToolException("The market board did not answer \"Compare prices\" (the server may be throttling searches; retry shortly).");
        }
        finally
        {
            Svc.MarketBoard.OfferingsReceived -= Handler;
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            await Game.Run(() => { CloseAddon("ItemSearchResult"); return true; }).ConfigureAwait(false);
            await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------ listings and stacks

    private sealed record LiveListing(int Slot, uint ItemId, bool Hq, int Quantity, long Price);

    private static unsafe List<LiveListing> ReadListings()
    {
        var im = InventoryManager.Instance();
        var market = im->GetInventoryContainer(InventoryType.RetainerMarket);
        if (market == null || !market->IsLoaded) throw new ToolException("The retainer's listings are not loaded.");
        var prices = im->RetainerMarketPrices;
        var list = new List<LiveListing>();
        for (var i = 0; i < market->Size; i++)
        {
            var slot = market->GetInventorySlot(i);
            if (slot == null || slot->ItemId == 0) continue;
            list.Add(new LiveListing(i, slot->ItemId, slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), slot->Quantity, i < prices.Length ? (long)prices[i] : 0));
        }
        return list;
    }

    private sealed record Stack(InventoryType Container, int Slot, int Quantity, bool Hq, bool FromBags);

    /// <summary>A stack of the item: bags first, then the open retainer's pages; HQ preferred unless hq=false. Framework thread.</summary>
    private static unsafe Stack? FindStack(uint itemId, bool? hq)
    {
        var im = InventoryManager.Instance();
        var found = new List<Stack>();
        foreach (var (types, fromBags) in new[] { (Bags, true), (RetainerPages, false) })
            foreach (var type in types)
            {
                var c = im->GetInventoryContainer(type);
                if (c == null || !c->IsLoaded) continue;
                for (var i = 0; i < c->Size; i++)
                {
                    var s = c->GetInventorySlot(i);
                    if (s == null || s->ItemId != itemId) continue;
                    found.Add(new Stack(type, i, s->Quantity, s->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality), fromBags));
                }
            }
        return found.Where(s => hq is null || s.Hq == hq).OrderByDescending(s => s.FromBags).ThenByDescending(s => hq is null && s.Hq).ThenByDescending(s => s.Quantity).FirstOrDefault();
    }

    /// <summary>Opens the price adjustment for a listing (slot in the retainer's market list).</summary>
    private static async Task<bool> OpenAdjust(int slot, CancellationToken ct)
    {
        await Game.Run(() => { unsafe { AgentRetainer.Instance()->OpenRetainerSell(InventoryType.RetainerMarket, (ushort)slot); } return true; }).ConfigureAwait(false);
        return await WaitFor("RetainerSell", ct, throwOnTimeout: false).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ sale history

    private static unsafe List<object> ReadHistory()
    {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>("RetainerHistory", 1);
        if (addon == null) throw new ToolException("The sale history window is not open.");
        var rows = new List<object>();
        var listNode = addon->GetNodeById(10);
        if (listNode == null || (int)listNode->Type < 1000) return rows;
        var list = ((AtkComponentNode*)listNode)->Component;
        for (var i = 0; i < list->UldManager.NodeListCount; i++)
        {
            var rowNode = list->UldManager.NodeList[i];
            if (rowNode == null || (int)rowNode->Type < 1000 || !rowNode->IsVisible()) continue;
            var row = ((AtkComponentNode*)rowNode)->Component;
            string? Text(uint id)
            {
                var n = row->UldManager.SearchNodeById(id);
                return n == null || n->Type != NodeType.Text ? null : Dalamud.Game.Text.SeStringHandling.SeString.Parse(((AtkTextNode*)n)->NodeText).TextValue.Trim();
            }
            var date = Text(8);
            var name = Text(3);
            if (string.IsNullOrEmpty(date) || string.IsNullOrEmpty(name)) continue;
            int quantity = 1;
            uint iconId = 0;
            var icon = row->UldManager.SearchNodeById(2);
            if (icon != null && (int)icon->Type >= 1000)
            {
                iconId = ((AtkComponentIcon*)((AtkComponentNode*)icon)->Component)->IconId;
                var qtyNode = ((AtkComponentNode*)icon)->Component->UldManager.SearchNodeById(7);
                if (qtyNode != null && qtyNode->Type == NodeType.Text && qtyNode->IsVisible() &&
                    int.TryParse(((AtkTextNode*)qtyNode)->NodeText.ToString(), out var q)) quantity = q;
            }
            var hq = name.Contains('');
            var clean = name.Replace("", "").Trim();
            rows.Add(new
            {
                item = clean.EndsWith("...") ? ResolveTruncated(clean, iconId) ?? clean : clean,
                hq = hq ? true : (bool?)null,
                quantity,
                priceTotal = ParseGil(Text(6)),
                buyer = Text(7),
                when = date,
            });
        }
        return rows;
    }

    /// <summary>Long item names are cut in the history list ("Grade 4 Gemdraught of..."); find the item if the prefix is unique.</summary>
    private static string? ResolveTruncated(string shown, uint iconId)
    {
        var prefix = shown[..^3].TrimEnd();
        var icon = iconId % 1_000_000; // HQ icons are offset by 1,000,000
        var candidates = Svc.Data.GetExcelSheet<Item>().Where(i => !i.IsUntradable && i.Name.ExtractText().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (icon != 0 && candidates.Count(i => i.Icon == icon) is 1) return candidates.First(i => i.Icon == icon).Name.ExtractText();
        var matches = candidates
            .Select(i => i.Name.ExtractText()).Distinct().Take(3).ToList();
        return matches.Count == 1 ? matches[0] : matches.Count > 1 ? $"{shown} (one of: {string.Join(", ", matches)})" : null;
    }

    private static long? ParseGil(string? s) =>
        s is null ? null : long.TryParse(Regex.Replace(s, @"[^\d]", ""), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;

    // ------------------------------------------------------------------ helpers

    private static void RequireBell()
    {
        if (!RetainerUi.RetainerListOpen && RetainerUi.ActiveRetainerName is null)
            throw new ToolException("The retainer list is not open. Use a summoning bell first (navigate_to summoning_bell, then interact_with_object).");
    }

    private static unsafe string ResolveRetainer(string name)
    {
        var rm = RetainerManager.Instance();
        if (rm == null || !rm->IsReady) throw new ToolException("Retainer data is not loaded. Open the retainer list at a summoning bell.");
        string? fuzzy = null;
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId == 0) continue;
            if (r->NameString.Equals(name, StringComparison.OrdinalIgnoreCase)) return r->NameString;
            if (r->NameString.StartsWith(name, StringComparison.OrdinalIgnoreCase)) fuzzy ??= r->NameString;
        }
        return fuzzy ?? throw new ToolException($"No retainer named '{name}'.");
    }

    private static unsafe int RetainerListingCount(string name)
    {
        var rm = RetainerManager.Instance();
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->NameString == name) return r->MarketItemCount;
        }
        return 0;
    }

    private static unsafe List<string> RetainersWithListings()
    {
        var rm = RetainerManager.Instance();
        var list = new List<string>();
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0 && r->MarketItemCount > 0) list.Add(r->NameString);
        }
        return list;
    }

    private static unsafe HashSet<ulong> OwnRetainerIds()
    {
        var rm = RetainerManager.Instance();
        var ids = new HashSet<ulong>();
        if (rm == null) return ids;
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0) ids.Add(r->RetainerId);
        }
        return ids;
    }

    private static unsafe HashSet<string> OwnRetainerNames()
    {
        var rm = RetainerManager.Instance();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (rm == null) return names;
        for (var i = 0u; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r != null && r->RetainerId != 0) names.Add(r->NameString);
        }
        return names;
    }

    private static object DescribeMarket(Universalis.ItemMarket m) => new
    {
        item = Items.Name(m.ItemId),
        scope = m.Scope,
        lowestNq = m.Listings.Where(l => !l.Hq).OrderBy(l => l.PricePerUnit).Select(l => (long?)l.PricePerUnit).FirstOrDefault(),
        lowestHq = m.Listings.Where(l => l.Hq).OrderBy(l => l.PricePerUnit).Select(l => (long?)l.PricePerUnit).FirstOrDefault(),
        listings = m.Listings.Select(l => new { l.PricePerUnit, l.Quantity, hq = l.Hq ? true : (bool?)null, l.Retainer, l.World }).ToList(),
        recentSales = m.RecentSales.Select(s => new { s.PricePerUnit, s.Quantity, hq = s.Hq ? true : (bool?)null, s.When, s.World }).ToList(),
        averagePrice = m.AveragePrice,
        averageNq = m.AveragePriceNq,
        averageHq = m.AveragePriceHq,
        salesPerDay = m.SalesPerDay,
        totalListings = m.ListingCount,
        updated = m.LastUpload,
    };

    private static async Task SelectAndWait(uint menuRow, string window, CancellationToken ct)
    {
        await Game.Run(() =>
        {
            if (!RetainerUi.SelectMenuEntry(menuRow)) throw new ToolException($"The retainer menu has no \"{Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRow(menuRow).Text.ExtractText()}\" entry.");
            return true;
        }).ConfigureAwait(false);
        await WaitFor(window, ct).ConfigureAwait(false);
    }

    private static async Task<bool> WaitFor(string window, CancellationToken ct, bool throwOnTimeout = true)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (await Game.Run(() => RetainerUi.Ready(window)).ConfigureAwait(false)) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return throwOnTimeout ? throw new ToolException($"The {window} window did not open.") : false;
    }

    private static async Task<bool> WaitGone(string window, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (!await Game.Run(() => RetainerUi.Ready(window)).ConfigureAwait(false)) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static unsafe void CloseAddon(string name)
    {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
        if (addon != null && addon->IsVisible) addon->Close(true);
    }
}
