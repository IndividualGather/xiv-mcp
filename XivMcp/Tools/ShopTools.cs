using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Maps;
using XivMcp.Mcp;
using XivMcp.Permissions;
using XivMcp.Shops;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Buying from NPC vendors. Which NPCs sell an item and where they stand comes from the Item Vendor Location plugin (its public
/// IPC); XIV MCP travels there, opens the shop through the NPC's menu and buys in batches of up to 99 like a player would. Without
/// that plugin, the player opens a shop that sells the item, and XIV MCP buys from it.
/// Whether a purchase costs gil is decided by the shop window that actually opens: gil shops run directly, every other shop
/// (tomestones, scrips, seals, items) only buys after the player approved it in game — with that shop window open in front of them.
/// </summary>
internal static class ShopTools
{
    public const string ItemVendorLocation = "ItemVendorLocation";
    public static bool ItemVendorLocationLoaded => PluginCompat.IsLoaded(ItemVendorLocation);

    private const int MaxBatch = 99;
    private static readonly TimeSpan ConsentTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long a standing approval question stays up in game (the call that asks returns long before).</summary>
    private static readonly TimeSpan ApprovalQuestionTimeout = TimeSpan.FromMinutes(5);
    private static readonly string[] ShopAddons = ["Shop", "ShopExchangeItem", "ShopExchangeCurrency", "InclusionShop"];
    private static readonly string[] ConfirmAddons = ["ShopExchangeItemDialog", "ShopExchangeCurrencyDialog"];
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>A vendor of the item. GilPrice is set when one of the NPC's own gil shops lists the item.</summary>
    private sealed record Offer(uint NpcId, string Npc, uint Territory, (float X, float Y)? Coords, int? GilPrice, List<string> ShopNames)
    {
        public int UnitsPerTrade => 1;
    }

    public static IEnumerable<McpTool> Create(Configuration config, PluginCompat compat)
    {
        void RequireEnabled()
        {
            if (!config.AllowMarketPurchases)
                throw new ToolException("Buying is disabled. Enable \"Market & purchases\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        yield return new McpTool
        {
            Name = "find_vendors",
            Description = "Lists NPCs that sell an item, with zone and map coordinates, from the Item Vendor Location plugin (required). Gil vendors " +
                          "show their price; other vendors are exchanges (tomestones, scrips, seals, items) whose cost the shop window shows.",
            InputSchema = """
                { "type": "object", "properties": { "item": { "type": "string", "description": "Item name or id." } }, "required": ["item"] }
                """,
            Handler = async (args, _) =>
            {
                var item = await Game.Run(() => Items.Resolve(args.String("item") ?? throw new ToolException("'item' is required."))).ConfigureAwait(false);
                var offers = await Game.Run(() => Offers(item.RowId)).ConfigureAwait(false);
                return await Game.Run(() => (object?)new
                {
                    item = new { id = item.RowId, name = item.Name.ExtractText() },
                    offers = offers.Take(25).Select(Describe).ToList(),
                    none = offers.Count == 0 ? "No NPC vendor sells this item (according to Item Vendor Location)." : null,
                }).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "buy_item",
            Description = "Buys an item from an NPC vendor (needs a vendor-location plugin to know where vendors stand): picks a vendor (gil vendors " +
                          "in a city first, then the current zone, or the one you name), travels there if needed (with the navigation plugins installed, needs 'Game & navigation'), " +
                          "opens the shop through the NPC's menu and buys the quantity in batches of up to 99, confirming like a player. Checks free " +
                          "bag slots (and gil for gil shops) first and verifies what arrived. Gil shops buy directly. Any other shop (tomestones, " +
                          "scrips, seals, items) shows an approval popup in game while its window is open, and only buys when the player clicks " +
                          "Approve. Supported shop windows: gil shops and the common exchange windows (not grand company or multi-page \"inclusion\" " +
                          "shops). Requires 'Market & purchases' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string", "description": "Item name or id." },
                    "quantity": { "type": "integer", "description": "How many to buy." },
                    "npc": { "type": "string", "description": "Vendor NPC name or id (from find_vendors); default: a gil vendor, in a city first, the current zone first." },
                    "menu_option": { "type": "string", "description": "Text of the NPC menu entry that opens the right shop, if it can't be found automatically." },
                    "approval": { "type": "string", "description": "Id of a standing approval (request_spending_approval) to use instead of asking per purchase; it caps what can be spent." }
                  },
                  "required": ["item", "quantity"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                RequireEnabled();
                var quantity = args.Int("quantity", 0, 0, 9999);
                if (quantity <= 0) throw new ToolException("'quantity' must be at least 1.");
                if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new ToolException("Another purchase is running.");
                var steps = new List<string>();
                try
                {
                    var item = await Game.RunLoggedIn(() => Items.Resolve(args.String("item") ?? throw new ToolException("'item' is required."))).ConfigureAwait(false);
                    var offer = ItemVendorLocationLoaded
                        ? await Game.Run(() => PickOffer(item.RowId, args.String("npc"))).ConfigureAwait(false)
                        : await AskForShop(item, steps, ct).ConfigureAwait(false);
                    var name = item.Name.ExtractText();

                    // Does it fit, and (for a known gil price) can we afford it?
                    var check = await Game.RunLoggedIn(() =>
                    {
                        InventoryActionTools.EnsureNotBusy();
                        var slots = Items.SlotsNeeded(item.RowId, quantity);
                        if (slots > Items.FreeBagSlots()) throw new ToolException($"{quantity}x {name} needs {slots} free bag slots; only {Items.FreeBagSlots()} are free.");
                        var gil = Gil();
                        if (offer.GilPrice is { } p && (long)p * quantity > gil) throw new ToolException($"Not enough gil: about {(long)p * quantity:N0} needed, {gil:N0} owned.");
                        return (Gil: gil, Before: Items.CountInBags(item.RowId));
                    }).ConfigureAwait(false);

                    // Get to the vendor.
                    var near = await Game.Run(() => AnyVisible(ShopAddons) is not null ||
                                                    Svc.Objects.Any(o => o.BaseId == offer.NpcId && Game.DistanceToPlayer(o.Position) < 4)).ConfigureAwait(false);
                    if (!near)
                    {
                        if (!config.AllowGameNavigation) throw new ToolException($"{offer.Npc} is not nearby and 'Game & navigation' is off; go there first ({Location(offer)}).");
                        var spot = await Game.Run(() => offer.Coords is { } c ? NavigationTools.FindNpc(offer.NpcId, offer.Territory, c.X, c.Y) : null).ConfigureAwait(false)
                                   ?? throw new ToolException($"Item Vendor Location doesn't know where {offer.Npc} stands; go there first.");
                        await NavigationTools.GoToNpc(spot, steps, ct).ConfigureAwait(false);
                    }

                    compat.PauseClickers();
                    try
                    {
                        var window = await OpenShop(offer, args.String("menu_option"), steps, ct).ConfigureAwait(false);

                        // Spending anything but gil is the player's decision; the shop window with the price is open right now.
                        var isGil = window == "Shop";
                        // A standing approval (request_spending_approval) replaces the popup for non-gil purchases; it is checked again per batch.
                        Approvals.Approval? approval = null;
                        if (!isGil && args.String("approval") is { } approvalId)
                        {
                            try { approval = Approvals.Get(approvalId, item.RowId, args.Caller?.ApprovalOwner ?? "assistant"); }
                            catch { await CloseShop(ct).ConfigureAwait(false); throw; }
                            steps.Add($"Using standing approval {approval.Id} ({approval.Remaining:N0} {Items.Name(approval.CurrencyId)} left).");
                        }
                        if (approval is null && (!isGil || (config.AskAboveGil > 0 && (long)(offer.GilPrice ?? 0) * quantity > config.AskAboveGil)))
                        {
                            var details = new List<string>
                            {
                                $"Buy {quantity}x {name} from {offer.Npc} ({Location(offer)})",
                                isGil ? $"About {(long)(offer.GilPrice ?? 0) * quantity:N0} gil (you have {check.Gil:N0})"
                                      : "This shop is not paid with gil: the price is shown in the shop window that is open now.",
                            };
                            steps.Add("Waiting for approval in game.");
                            try
                            {
                                await Consent.Require(isGil ? $"Buy {name} for gil?" : $"Buy {name} with a non-gil currency?", details, ConsentTimeout, ct).ConfigureAwait(false);
                            }
                            catch
                            {
                                await CloseShop(ct).ConfigureAwait(false);
                                throw;
                            }
                            steps.Add("Approved in game.");
                        }

                        var gilBefore = await Game.Run(Gil).ConfigureAwait(false);
                        if (window == "InclusionShop") await ShowInInclusionShop(item.RowId, steps, ct).ConfigureAwait(false);
                        var bought = await Purchase(item.RowId, quantity, offer, check.Before, steps, ct, approval).ConfigureAwait(false);
                        await CloseShop(ct).ConfigureAwait(false);
                        var after = await Game.Run(() => (Count: Items.CountInBags(item.RowId), Gil: Gil())).ConfigureAwait(false);
                        return new
                        {
                            bought,
                            requested = quantity,
                            item = name,
                            from = offer.Npc,
                            paidWith = isGil ? "gil" : "non-gil currency (approved in game)",
                            gilSpent = isGil ? gilBefore - after.Gil : (long?)null,
                            nowInBags = after.Count,
                            steps,
                        };
                    }
                    finally
                    {
                        compat.ResumeClickers();
                    }
                }
                finally
                {
                    Gate.Release();
                }
            },
        };
    }

    /// <summary>Tools for standing approvals (asking once in game for a budget of one currency).</summary>
    public static IEnumerable<McpTool> ApprovalTools(Configuration config)
    {
        yield return new McpTool
        {
            Name = "request_spending_approval",
            Description = "Asks the player once, in game, to approve spending up to an amount of a currency (e.g. 4,000 Orange Crafters' Scrip), " +
                          "optionally only on given items, for a while (default 24 h) — a standing approval for a job that buys repeatedly. Returns " +
                          "within 20 seconds: approved, declined, or still waiting (the question stays up in game for 5 minutes; list_approvals " +
                          "shows the answer later). Pass the id of an approved one as 'approval' to buy_item: purchases then don't ask again but " +
                          "never spend more than approved (each purchase's real cost is counted). The player can revoke it in /xivmcp → Jobs. " +
                          "Requires 'Market & purchases'.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "currency": { "type": "string", "description": "Currency name or item id, e.g. \"Orange Crafters' Scrip\"." },
                    "max_amount": { "type": "integer", "description": "Most that may be spent in total." },
                    "items": { "type": "array", "items": { "type": "string" }, "description": "Only these items may be bought (optional)." },
                    "purpose": { "type": "string", "description": "Shown to the player, e.g. \"Tacos scrip farm: buy materia\"." },
                    "valid_hours": { "type": "integer", "description": "Default 24." }
                  },
                  "required": ["currency", "max_amount"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (!config.AllowMarketPurchases)
                    throw new ToolException("Buying is disabled. Enable \"Market & purchases\" in the XIV MCP settings window (/xivmcp) in game.");
                var max = args.Int("max_amount", 0, 0, int.MaxValue);
                if (max <= 0) throw new ToolException("'max_amount' must be positive.");
                var hours = args.Int("valid_hours", 24, 1, 24 * 14);
                var (currency, items) = await Game.Run(() => (Items.Resolve(args.String("currency") ?? throw new ToolException("'currency' is required.")).RowId,
                                                               args.StringList("items").Select(i => Items.Resolve(i).RowId).ToList())).ConfigureAwait(false);
                var purpose = args.String("purpose") ?? "Purchases by your assistant";
                var details = new List<string>
                {
                    purpose,
                    $"Spend up to {max:N0} {Items.Name(currency)} in total (you have {await Game.Run(() => CurrencyCount(currency)).ConfigureAwait(false):N0})",
                    items.Count == 0 ? "On any item" : "Only on: " + string.Join(", ", items.Select(Items.Name)),
                    $"Valid for {hours} hours; you can revoke it any time in /xivmcp → Jobs.",
                };
                // The question stays up for minutes, longer than a client waits for a tool's answer: ask in the background, wait a
                // little, and return either the answer or the waiting request (list_approvals shows how it was answered).
                var validFor = TimeSpan.FromHours(hours);
                var approval = Approvals.Add(purpose, currency, max, items, validFor, args.Caller?.ApprovalOwner ?? "assistant");
                var request = new Consent.Request($"Approve spending {Items.Name(currency)}?", details) { Deadline = DateTime.UtcNow + ApprovalQuestionTimeout };
                var answered = Task.Run(async () =>
                {
                    var decision = await Consent.Ask(request, ApprovalQuestionTimeout, CancellationToken.None).ConfigureAwait(false);
                    Approvals.SetAnswer(approval.Id, decision != ApprovalDecision.Denied ? ApprovalAnswer.Approved
                        : DateTime.UtcNow >= request.Deadline ? ApprovalAnswer.Unanswered : ApprovalAnswer.Declined, validFor);
                });
                await Task.WhenAny(answered, Task.Delay(TimeSpan.FromSeconds(20), ct)).ConfigureAwait(false);
                var now = Approvals.Find(approval.Id) ?? approval;
                return new
                {
                    approval = Approvals.Describe(now),
                    next = now.Status switch
                    {
                        ApprovalStatus.Waiting => $"The player hasn't answered yet; the question stays up in game for {ApprovalQuestionTimeout.TotalMinutes:0} minutes. " +
                                                  "Check list_approvals before buying with it.",
                        ApprovalStatus.Active => $"Approved: pass approval=\"{now.Id}\" to buy_item.",
                        _ => "Not approved; nothing can be bought with it.",
                    },
                };
            },
        };

        yield return new McpTool
        {
            Name = "list_approvals",
            Description = "Standing spending approvals: currency, approved and spent amount, items, expiry and state (active, used up, expired, revoked).",
            Handler = (_, _) => Task.FromResult<object?>(Approvals.All().Select(Approvals.Describe).ToList()),
        };

        yield return new McpTool
        {
            Name = "revoke_approval",
            Description = "Revokes a standing spending approval right away.",
            InputSchema = """{ "type": "object", "properties": { "id": { "type": "string" } }, "required": ["id"] }""",
            ReadOnly = false,
            Handler = (args, _) =>
            {
                var id = args.String("id") ?? throw new ToolException("'id' is required.");
                Approvals.Revoke(id);
                return Task.FromResult<object?>(new { revoked = id });
            },
        };
    }

    private static unsafe long CurrencyCount(uint id)
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : id == 1 ? im->GetGil() : im->GetInventoryItemCount(id);
    }

    /// <summary>
    /// Without a vendor plugin: asks the player to open the shop of a vendor that sells the item (unless one is open already), waits
    /// up to ten minutes for a shop window, and returns that vendor as the offer. The purchase then checks that the shop lists the item.
    /// </summary>
    private static async Task<Offer> AskForShop(Item item, List<string> steps, CancellationToken ct)
    {
        var name = item.Name.ExtractText();
        if (await Game.Run(() => AnyVisible(ShopAddons)).ConfigureAwait(false) is null)
        {
            PlayerGuide.Ask(PlayerGuidance.OpenShop(name));
            steps.Add($"Asked you to open the shop of a vendor that sells {name}.");
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                while (await Game.Run(() => AnyVisible(ShopAddons)).ConfigureAwait(false) is null)
                    await Task.Delay(500, wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ToolException($"No shop was opened within 10 minutes, so nothing was bought. Ask the player to open a shop that sells {name}, then try again.");
            }
            finally { PlayerGuide.Done(); }
            steps.Add("You opened a shop.");
        }
        return await Game.Run(() =>
        {
            var window = AnyVisible(ShopAddons);
            var npc = Svc.Targets.Target;
            return new Offer(npc?.BaseId ?? 0, npc?.Name.TextValue is { Length: > 0 } n ? n : "the vendor you opened", Svc.ClientState.TerritoryType, null,
                             window == "Shop" ? (int)item.PriceMid : null, []);
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ vendors (Item Vendor Location)

    /// <summary>Whether Item Vendor Location knows a vendor for the item (null when the plugin is missing). Framework thread.</summary>
    internal static bool? SoldByVendor(uint itemId)
    {
        if (!ItemVendorLocationLoaded) return null;
        try { return Svc.PluginInterface.GetIpcSubscriber<uint, bool, HashSet<(uint, uint, (float, float))>?>("ItemVendorLocation.GetItemVendors").InvokeFunc(itemId, false) is { Count: > 0 }; }
        catch { return null; }
    }

    private static void RequireVendorPlugin()
    {
        if (!ItemVendorLocationLoaded)
            throw new ToolException("Finding vendors needs a vendor-location plugin, which isn't installed or enabled.");
    }

    /// <summary>Vendors of an item from Item Vendor Location, the best first (see <see cref="VendorRanking"/>). Framework thread.</summary>
    private static List<Offer> Offers(uint itemId)
    {
        RequireVendorPlugin();
        HashSet<(uint, uint, (float, float))>? vendors;
        try
        {
            vendors = Svc.PluginInterface.GetIpcSubscriber<uint, bool, HashSet<(uint, uint, (float, float))>?>("ItemVendorLocation.GetItemVendors").InvokeFunc(itemId, false);
        }
        catch (Exception ex)
        {
            throw new ToolException($"Item Vendor Location did not answer ({ex.GetType().Name}). Is it up to date?");
        }
        if (vendors is null) return [];

        var price = (int)(Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.PriceMid ?? 0);
        var here = Svc.ClientState.TerritoryType;
        var offers = vendors.Select(v => v.Item1).Distinct().Select(npc =>
            {
                // GetItemVendors repeats the X coordinate; GetVendorLocation has the correct pair.
                (uint, (float, float))? location = null;
                try { location = Svc.PluginInterface.GetIpcSubscriber<uint, (uint, (float, float))?>("ItemVendorLocation.GetVendorLocation").InvokeFunc(npc); }
                catch { /* unknown location */ }
                var shops = ShopNamesFor(npc, itemId);
                return new Offer(npc, ItemSourceTools.NpcName(npc), location?.Item1 ?? 0, location is { Item1: not 0 } l ? l.Item2 : null,
                                 shops.Gil ? price : null, shops.Names);
            })
            .ToList();
        // Gil vendors in a city first (quick to reach by aetheryte), then the field; the current zone first within each.
        var territories = Svc.Data.GetExcelSheet<TerritoryType>();
        return VendorRanking.Order(offers, o => new VendorSpot(o.Npc, o.GilPrice is not null,
                                                            territories.GetRowOrDefault(o.Territory)?.TerritoryIntendedUse.RowId == 0 && o.Territory != 0,
                                                            o.Territory == here, o.Coords is not null)).ToList();
    }

    private static Offer PickOffer(uint itemId, string? npc)
    {
        var offers = Offers(itemId);
        if (offers.Count == 0) throw new ToolException("No NPC vendor sells this item (according to Item Vendor Location).");
        if (npc is not null)
        {
            offers = offers.Where(o => uint.TryParse(npc, out var id) ? o.NpcId == id : Game.Matches(o.Npc, npc)).ToList();
            if (offers.Count == 0) throw new ToolException($"'{npc}' doesn't sell this item. Use find_vendors.");
        }
        return offers.FirstOrDefault(o => o.Coords is not null) ?? offers[0];
    }

    /// <summary>
    /// Names of the NPC's shops that list the item (the menu entries to pick), and whether one of them is a gil shop. Only used to
    /// choose the right menu entry; which NPC sells what comes from Item Vendor Location. Shops behind a sub-menu (TopicSelect, as
    /// the city merchants have) or a greeting (PreHandler) are followed; the sub-menu's name comes before the shop's.
    /// </summary>
    private static (List<string> Names, bool Gil) ShopNamesFor(uint npcId, uint itemId)
    {
        var names = new List<string>();
        var gil = false;
        if (Svc.Data.GetExcelSheet<ENpcBase>().GetRowOrDefault(npcId) is not { } npc) return (names, gil);
        foreach (var data in npc.ENpcData) Visit(data, 0);
        return (names, gil);

        // True when the entry (or something behind it) lists the item.
        bool Visit(Lumina.Excel.RowRef entry, int depth)
        {
            var id = entry.RowId;
            if (id == 0 || depth > 3) return false;
            if (id is >= 262144 and < 327680)
            {
                if (!Svc.Data.GetSubrowExcelSheet<GilShopItem>().TryGetRow(id, out var rows) || !rows.Any(r => r.Item.RowId == itemId)) return false;
                gil = true;
                if (Svc.Data.GetExcelSheet<GilShop>().GetRowOrDefault(id)?.Name.ExtractText() is { Length: > 0 } n) names.Add(n);
                return true;
            }
            if (id is >= 1769472 and < 1835008 && Svc.Data.GetExcelSheet<SpecialShop>().GetRowOrDefault(id) is { } special)
            {
                if (!special.Item.Any(e => e.ReceiveItems.Any(i => i.Item.RowId == itemId))) return false;
                if (special.Name.ExtractText() is { Length: > 0 } sn) names.Add(sn);
                return true;
            }
            if (entry.Is<PreHandler>() && entry.GetValueOrDefault<PreHandler>() is { } pre) return Visit(pre.Target, depth + 1);
            if (entry.Is<TopicSelect>() && entry.GetValueOrDefault<TopicSelect>() is { } topic)
            {
                var at = names.Count;
                var any = false;
                foreach (var shop in topic.Shop) any |= Visit(shop, depth + 1);
                if (any && topic.Name.ExtractText() is { Length: > 0 } tn) names.Insert(at, tn);
                return any;
            }
            return false;
        }
    }

    private static object Describe(Offer o) => new
    {
        npc = o.Npc,
        npcId = o.NpcId,
        zone = o.Territory == 0 ? null : NavigationTools.TerritoryName(o.Territory),
        map = o.Coords is { } c ? new { x = MathF.Round(c.X, 1), y = MathF.Round(c.Y, 1) } : null,
        sells = o.GilPrice is { } p ? $"{p:N0} gil" : "price not listed (gil shops buy directly; any other currency asks in game first)",
        shops = o.ShopNames.Count > 0 ? o.ShopNames : null,
    };

    private static string Location(Offer o) =>
        o.Territory == 0 ? "location unknown" : o.Coords is { } c ? $"{NavigationTools.TerritoryName(o.Territory)} ({c.X:0.0}, {c.Y:0.0})" : NavigationTools.TerritoryName(o.Territory);

    private static unsafe long Gil()
    {
        var im = InventoryManager.Instance();
        return im == null ? 0 : im->GetGil();
    }

    // ------------------------------------------------------------------ the shop windows

    /// <summary>Talks to the vendor and opens the shop. Returns the shop window that opened ("Shop" = gil shop).</summary>
    private static async Task<string> OpenShop(Offer offer, string? menuOption, List<string> steps, CancellationToken ct)
    {
        if (await Game.Run(() => AnyVisible(ShopAddons)).ConfigureAwait(false) is { } already) return already;
        await Game.RunLoggedIn(() =>
        {
            var npc = Svc.Objects.Where(o => o.BaseId == offer.NpcId && o.IsTargetable).OrderBy(o => Game.DistanceToPlayer(o.Position)).FirstOrDefault()
                      ?? throw new ToolException($"{offer.Npc} is not nearby.");
            unsafe
            {
                var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address;
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->SetHardTarget(native, false, false, 0);
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(native, true);
            }
            return true;
        }).ConfigureAwait(false);
        steps.Add($"Talking to {offer.Npc}.");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        var lastAction = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct).ConfigureAwait(false);
            if (DateTime.UtcNow - lastAction < TimeSpan.FromMilliseconds(600)) continue;
            var outcome = await Game.Run(() =>
            {
                if (AnyVisible(ShopAddons) is { } shop) return $"open:{shop}";
                if (RetainerUi.Ready("Talk")) { RetainerUi.ClickTalk(); return "talk"; }
                var entries = MenuEntries(out var addon);
                if (entries is null) return null;
                var index = PickMenuEntry(entries, menuOption is not null ? [menuOption] : offer.ShopNames);
                if (index < 0) return "menu:" + string.Join(" | ", entries);
                FireMenu(addon, index);
                return "selected:" + entries[index];
            }).ConfigureAwait(false);
            if (outcome is null) continue;
            if (outcome.StartsWith("open:")) { steps.Add($"Shop open ({outcome[5..]})."); await Task.Delay(400, ct).ConfigureAwait(false); return outcome[5..]; }
            if (outcome.StartsWith("menu:"))
                throw new ToolException($"Couldn't tell which menu entry opens the shop. Entries: {outcome[5..]}. Retry with menu_option.");
            if (outcome.StartsWith("selected:")) steps.Add($"Menu: {outcome[9..]}.");
            lastAction = DateTime.UtcNow;
        }
        throw new ToolException($"The shop window didn't open after talking to {offer.Npc}. Windows open: {await Game.Run(OpenWindows).ConfigureAwait(false)}.");
    }

    /// <summary>The menu entry that opens the wanted shop: exact/prefix match on the shop name, else the only "purchase"-like entry.</summary>
    private static int PickMenuEntry(List<string> entries, List<string> shopNames)
    {
        foreach (var shopName in shopNames.Where(n => n.Length > 0))
        {
            var exact = entries.FindIndex(e => e.Equals(shopName, StringComparison.OrdinalIgnoreCase));
            if (exact >= 0) return exact;
            var prefix = entries.FindIndex(e => e.StartsWith(shopName, StringComparison.OrdinalIgnoreCase) || shopName.StartsWith(e, StringComparison.OrdinalIgnoreCase) && e.Length > 4);
            if (prefix >= 0) return prefix;
            var contains = entries.FindIndex(e => e.Contains(shopName, StringComparison.OrdinalIgnoreCase));
            if (contains >= 0) return contains;
        }
        var buying = entries.Select((e, i) => (e, i)).Where(x => x.e.Contains("Purchase", StringComparison.OrdinalIgnoreCase) || x.e.Contains("Buy", StringComparison.OrdinalIgnoreCase) ||
                                                              x.e.Contains("Exchange", StringComparison.OrdinalIgnoreCase) || x.e.Contains("Trade", StringComparison.OrdinalIgnoreCase)).ToList();
        return buying.Count == 1 ? buying[0].i : -1;
    }

    /// <summary>
    /// Buys in batches until the bags hold the wanted quantity. Under a standing approval it first buys one, learns the real unit cost
    /// from the approved currency's drop, and then never buys more than the remaining approved amount covers.
    /// </summary>
    private static async Task<int> Purchase(uint itemId, int quantity, Offer offer, int before, List<string> steps, CancellationToken ct,
                                            Approvals.Approval? approval = null)
    {
        var target = before + quantity;
        var failures = 0;
        long? unitCost = null;
        var budget = approval?.Remaining ?? 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var have = await Game.Run(() => Items.CountInBags(itemId)).ConfigureAwait(false);
            if (have >= target) return have - before;
            var remainingUnits = (int)Math.Ceiling((target - have) / (double)offer.UnitsPerTrade);
            var batch = Math.Min(MaxBatch, remainingUnits);
            if (approval is not null)
            {
                batch = unitCost is { } cost ? (int)Math.Min(batch, budget / Math.Max(1, cost)) : 1;
                if (batch <= 0)
                {
                    steps.Add($"Approval {approval.Id} has {budget:N0} {Items.Name(approval.CurrencyId)} left, not enough for another one.");
                    return have - before;
                }
            }
            var currencyBefore = approval is null ? 0 : await Game.Run(() => CurrencyCount(approval.CurrencyId)).ConfigureAwait(false);

            var fired = await Game.Run(() => BuyBatch(itemId, batch)).ConfigureAwait(false);
            if (fired is not null) throw new ToolException(fired);
            steps.Add($"Buying {batch * offer.UnitsPerTrade}.");

            // Confirm dialogs and wait for the items to arrive.
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var arrived = false;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                var now = await Game.Run(() => { ConfirmDialogs(); return Items.CountInBags(itemId); }).ConfigureAwait(false);
                if (now > have)
                {
                    arrived = true;
                    if (approval is not null)
                    {
                        await Task.Delay(500, ct).ConfigureAwait(false); // the currency update can trail the item
                        var spent = currencyBefore - await Game.Run(() => CurrencyCount(approval.CurrencyId)).ConfigureAwait(false);
                        if (spent <= 0)
                            throw new ToolException($"That purchase was not paid with {Items.Name(approval.CurrencyId)}, so approval {approval.Id} doesn't cover it; stopped.");
                        Approvals.Spend(approval.Id, spent);
                        budget -= spent;
                        var got = await Game.Run(() => Items.CountInBags(itemId)).ConfigureAwait(false) - have;
                        unitCost = Math.Max(1, spent / Math.Max(1, got));
                        steps.Add($"Paid {spent:N0} {Items.Name(approval.CurrencyId)} ({budget:N0} approved left).");
                    }
                    break;
                }
            }
            if (!arrived && ++failures >= 2)
                throw new ToolException($"The purchase didn't go through (bought {have - before} of {quantity}). The shop may have refused (stock, rank or currency).");
            await Task.Delay(Random.Shared.Next(400, 700), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fires the buy callback in the open shop window. Returns an error text, or null when sent.</summary>
    private static unsafe string? BuyBatch(uint itemId, int quantity)
    {
        if (Addon("Shop") is var shop && shop != null && shop->IsVisible)
        {
            var count = (int)Value(shop, 2);
            for (var i = 0; i < count; i++)
                if (Value(shop, 14 + 427 + i) == itemId)
                {
                    Fire(shop, true, 0, i, quantity, 0);
                    return null;
                }
            return "The item is not in this gil shop's list (wrong vendor or the item isn't unlocked for you yet).";
        }
        if (Addon("InclusionShop") is var inclusion && inclusion != null && inclusion->IsVisible)
        {
            var index = InclusionIndex(inclusion, itemId);
            if (index < 0) return "The item is not on the exchange page that is shown.";
            FireMixed(inclusion, 14, (uint)index, (uint)quantity);
            return null;
        }
        foreach (var name in new[] { "ShopExchangeItem", "ShopExchangeCurrency" })
        {
            var addon = Addon(name);
            if (addon == null || !addon->IsVisible) continue;
            var agent = (AgentShop*)AgentModule.Instance()->GetAgentByInternalId(AgentId.Shop);
            var index = -1;
            if (agent != null && agent->ItemReceive != null)
            {
                var rows = agent->ItemReceiveSpan;
                for (var i = 0; i < rows.Length; i++)
                    if (rows[i].ItemId == itemId) { index = i; break; }
            }
            if (index < 0) return $"The item is not in this exchange ({name}).";
            Fire(addon, true, 0, index, quantity, 0);
            return null;
        }
        return "No shop window is open.";
    }

    private static unsafe void ConfirmDialogs()
    {
        foreach (var name in ConfirmAddons)
        {
            var dialog = Addon(name);
            if (dialog != null && dialog->IsVisible) { Fire(dialog, true, 0); return; }
        }
        var yesno = (AddonSelectYesno*)Addon("SelectYesno");
        if (yesno != null && yesno->AtkUnitBase.IsVisible && yesno->YesButton != null && yesno->YesButton->IsEnabled)
            Fire(&yesno->AtkUnitBase, true, 0);
    }

    private static async Task CloseShop(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var open = await Game.Run(() =>
            {
                unsafe
                {
                    if (AnyVisible(ShopAddons) is not { } name) return false;
                    var addon = Addon(name);
                    if (name == "Shop") Fire(addon, true, -1);
                    else addon->Close(true);
                    return true;
                }
            }).ConfigureAwait(false);
            if (!open) break;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        // Some vendors return to their menu afterwards; leave it.
        await Task.Delay(300, ct).ConfigureAwait(false);
        await Game.Run(() =>
        {
            unsafe
            {
                if (MenuEntries(out var menu) is { } entries && menu != 0)
                {
                    var quit = entries.FindIndex(e => e.StartsWith("Cancel", StringComparison.OrdinalIgnoreCase) || e.StartsWith("Nothing", StringComparison.OrdinalIgnoreCase) || e.StartsWith("Quit", StringComparison.OrdinalIgnoreCase));
                    if (quit >= 0) FireMenu(menu, quit); else ((AtkUnitBase*)menu)->Close(true);
                }
            }
            return true;
        }).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ multi-page exchanges (scrip exchange: InclusionShop)

    /// <summary>
    /// Pages (categories) and sub-pages where an item is offered in multi-page exchanges: InclusionShop → Category[page] → its series'
    /// sub-rows (sub-page = index + 1) → the SpecialShop that lists the item.
    /// </summary>
    private static List<(int Page, int SubPage)> InclusionRoutes(uint itemId)
    {
        var specials = Svc.Data.GetExcelSheet<SpecialShop>()
            .Where(s => s.Item.Any(i => i.ReceiveItems.Any(r => r.Item.RowId == itemId))).Select(s => s.RowId).ToHashSet();
        var series = Svc.Data.GetSubrowExcelSheet<InclusionShopSeries>();
        var routes = new List<(int, int)>();
        foreach (var shop in Svc.Data.GetExcelSheet<InclusionShop>())
            for (var page = 0; page < shop.Category.Count; page++)
            {
                if (shop.Category[page].ValueNullable is not { } category || category.InclusionShopSeries.RowId == 0) continue;
                if (!series.TryGetRow(category.InclusionShopSeries.RowId, out var subs)) continue;
                for (var j = 0; j < subs.Count; j++)
                    if (specials.Contains(subs[j].SpecialShop.RowId)) routes.Add((page, j + 1));
            }
        return routes.Distinct().ToList();
    }

    /// <summary>Selects the page and sub-page of the open exchange that show the item.</summary>
    private static async Task ShowInInclusionShop(uint itemId, List<string> steps, CancellationToken ct)
    {
        bool Visible() { unsafe { var a = Addon("InclusionShop"); return a != null && InclusionIndex(a, itemId) >= 0; } }
        if (await Game.Run(Visible).ConfigureAwait(false)) return;
        var routes = await Game.Run(() => InclusionRoutes(itemId)).ConfigureAwait(false);
        foreach (var (page, sub) in routes)
        {
            await Game.Run(() =>
            {
                unsafe
                {
                    var a = Addon("InclusionShop");
                    if (a == null) throw new ToolException("The exchange window closed.");
                    // Keep the page dropdown in step with the selection, like a click on it.
                    for (var i = 0; i < a->UldManager.NodeListCount; i++)
                    {
                        var node = a->UldManager.NodeList[i];
                        if (node != null && (int)node->Type == 1015 && node->NodeId == 7 && node->GetAsAtkComponentNode()->Component != null)
                            ((AtkComponentDropDownList*)node->GetAsAtkComponentNode()->Component)->SelectItem(page);
                    }
                    FireMixed(a, 12, (uint)page);
                }
                return true;
            }).ConfigureAwait(false);
            await Task.Delay(700, ct).ConfigureAwait(false);
            await Game.Run(() => { unsafe { FireMixed(Addon("InclusionShop"), 13, (uint)sub); } return true; }).ConfigureAwait(false);
            await Task.Delay(700, ct).ConfigureAwait(false);
            if (await Game.Run(Visible).ConfigureAwait(false))
            {
                steps.Add($"Exchange page {page + 1}, sub-page {sub}.");
                return;
            }
        }
        throw new ToolException($"{Items.Name(itemId)} is not offered on any page of this exchange.");
    }

    /// <summary>Row of an item among the exchange's visible items (AtkValues: count at 298, item ids from 300 in steps of 18).</summary>
    private static unsafe int InclusionIndex(AtkUnitBase* addon, uint itemId)
    {
        if (addon == null) return -1;
        var count = (int)Value(addon, 298);
        for (var i = 0; i < count; i++)
            if (Value(addon, 300 + i * 18) == itemId) return i;
        return -1;
    }

    /// <summary>Fires a callback with an int command followed by uint arguments (the form these exchange windows use).</summary>
    private static unsafe void FireMixed(AtkUnitBase* addon, int command, params uint[] values)
    {
        if (addon == null) throw new ToolException("Window disappeared.");
        var atk = stackalloc AtkValue[values.Length + 1];
        atk[0] = default; atk[0].Type = AtkValueType.Int; atk[0].Int = command;
        for (var i = 0; i < values.Length; i++) { atk[i + 1] = default; atk[i + 1].Type = AtkValueType.UInt; atk[i + 1].UInt = values[i]; }
        addon->FireCallback((uint)values.Length + 1, atk, false);
    }

    // ------------------------------------------------------------------ low-level helpers

    private static unsafe AtkUnitBase* Addon(string name) => Svc.GameGui.GetAddonByName<AtkUnitBase>(name, 1);

    private static unsafe string? AnyVisible(string[] names) => names.FirstOrDefault(n => Addon(n) is var a && a != null && a->IsVisible);

    private static string OpenWindows() => string.Join(", ", new[] { "Talk", "SelectString", "SelectIconString", "SelectYesno", "Shop", "ShopExchangeItem", "ShopExchangeCurrency", "InclusionShop", "GrandCompanyExchange" }
        .Where(RetainerUi.Ready)) is { Length: > 0 } s ? s : "none";

    private static unsafe uint Value(AtkUnitBase* addon, int index)
    {
        if (index >= addon->AtkValuesCount) return 0;
        var v = addon->AtkValues[index];
        return v.Type switch { AtkValueType.UInt => v.UInt, AtkValueType.Int => (uint)v.Int, _ => 0 };
    }

    /// <summary>Entries of an open SelectString or SelectIconString menu.</summary>
    internal static unsafe List<string>? MenuEntries(out nint addon)
    {
        addon = 0;
        foreach (var name in new[] { "SelectIconString", "SelectString" })
        {
            if (!RetainerUi.Ready(name)) continue;
            var a = Addon(name);
            var popup = name == "SelectString" ? &((AddonSelectString*)a)->PopupMenu.PopupMenu : &((AddonSelectIconString*)a)->PopupMenu.PopupMenu;
            var list = new List<string>();
            for (var i = 0; i < popup->EntryCount; i++)
                list.Add(MemoryHelper.ReadSeStringNullTerminated((nint)popup->EntryNames[i].Value).TextValue.Trim());
            addon = (nint)a;
            return list;
        }
        return null;
    }

    internal static unsafe void FireMenu(nint addon, int index) => Fire((AtkUnitBase*)addon, true, index);

    private static unsafe void Fire(AtkUnitBase* addon, bool updateState, params int[] values)
    {
        if (addon == null) throw new ToolException("Window disappeared.");
        var atk = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atk[i] = default;
            atk[i].Type = AtkValueType.Int;
            atk[i].Int = values[i];
        }
        addon->FireCallback((uint)values.Length, atk, updateState);
    }
}
