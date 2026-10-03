using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Turning in collectables at a Collectable Appraiser for scrips: select the job tab and the item in the collectables window and trade
/// them one at a time. When a trade would go over the scrip cap the game asks first — that is answered "No" and the turn-in stops, so
/// nothing is lost. The window callbacks are the ones the window uses itself (the same values GatherBuddy Reborn uses).
/// </summary>
internal static class CollectableTools
{
    private const string Window = "CollectablesShop";

    // Scrips the appraisers pay (crafters' and gatherers' scrips).
    private static readonly uint[] Scrips = [33913, 33914, 41784, 41785];

    public static IEnumerable<McpTool> Create(Configuration config, PluginCompat compat)
    {
        yield return new McpTool
        {
            Name = "turn_in_collectables",
            Description = "Turns in collectables from your bags at a Collectable Appraiser for scrips, one by one, like clicking Trade. Goes to an " +
                          "appraiser first if none is nearby (needs Item Vendor Location for the NPC's position and 'Game & navigation'). " +
                          "Stops before the scrip cap: when the game warns that a trade would overcap a currency, it answers No and stops — " +
                          "nothing is lost. Without 'items' it turns in every collectable the appraiser accepts. Reports scrips before/after and " +
                          "what is left. Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "items": { "type": "array", "items": { "type": "string" }, "description": "Only these collectables (names or ids)." },
                    "max": { "type": "integer", "description": "Turn in at most this many (default all)." },
                    "npc": { "type": "string", "description": "Appraiser NPC id, if a specific one should be used." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (!config.AllowItemsRetainers)
                    throw new ToolException("Turning in collectables is disabled. Enable \"Items & retainers\" in the XIV MCP settings window (/xivmcp) in game.");
                var max = args.Int("max", int.MaxValue, 1, int.MaxValue);
                var steps = new List<string>();

                var plan = await Game.RunLoggedIn(() =>
                {
                    InventoryActionTools.EnsureNotBusy();
                    var only = args.StringList("items").Select(i => Items.Resolve(i).RowId).ToHashSet();
                    var bags = CollectablesInBags().Where(c => only.Count == 0 || only.Contains(c.ItemId)).ToList();
                    if (bags.Count == 0) throw new ToolException(only.Count == 0 ? "No collectables in your bags that an appraiser accepts." : "None of those collectables are in your bags.");
                    var npcs = AppraisersFor(bags.Select(b => b.ItemId));
                    if (npcs.Count == 0) throw new ToolException("No NPC accepts these collectables.");
                    return (Bags: bags, Scrips: ScripCounts(), Npcs: npcs);
                }).ConfigureAwait(false);

                // Get to an appraiser.
                var npc = await Game.Run(() => NearbyAppraiser(plan.Npcs)).ConfigureAwait(false);
                if (npc is null)
                {
                    if (!config.AllowGameNavigation) throw new ToolException("No Collectable Appraiser nearby and 'Game & navigation' is off; go to one first.");
                    var spot = await Game.Run(() => AppraiserSpot(args.UInt("npc"), plan.Npcs)).ConfigureAwait(false)
                               ?? throw new ToolException("Don't know where a Collectable Appraiser stands (needs the Item Vendor Location plugin); go to one first.");
                    await NavigationTools.GoToNpc(spot, steps, ct).ConfigureAwait(false);
                    npc = await Game.Run(() => NearbyAppraiser(plan.Npcs)).ConfigureAwait(false) ?? throw new ToolException("Arrived, but no appraiser is in reach.");
                }

                compat.PauseClickers();
                var traded = new Dictionary<uint, int>();
                string? stopped = null;
                try
                {
                    await OpenWindow(npc.Value, steps, ct).ConfigureAwait(false);
                    var done = 0;
                    foreach (var group in plan.Bags.GroupBy(c => c.ItemId))
                    {
                        var itemName = Items.Name(group.Key);
                        var job = JobTab(group.Key);
                        if (job is null) { steps.Add($"{itemName}: no crafting/gathering job known; skipped."); continue; }
                        await Act(() => { Fire(14, (uint)job.Value); return $"Job tab {job}."; }, ct).ConfigureAwait(false);
                        await Task.Delay(600, ct).ConfigureAwait(false);
                        var index = await Game.Run(() => ItemIndex(itemName)).ConfigureAwait(false);
                        if (index < 0) { steps.Add($"{itemName}: not in this appraiser's list; skipped."); continue; }
                        await Act(() => { Fire(12, (uint)index); return $"Selected {itemName}."; }, ct).ConfigureAwait(false);
                        await Task.Delay(600, ct).ConfigureAwait(false);

                        while (done < max)
                        {
                            ct.ThrowIfCancellationRequested();
                            var before = await Game.Run(() => CountCollectable(group.Key)).ConfigureAwait(false);
                            if (before == 0) break;
                            await Game.Run(() => { Fire(15, 0u, updateState: true); return true; }).ConfigureAwait(false);
                            var outcome = await WaitForTrade(group.Key, before, ct).ConfigureAwait(false);
                            if (outcome == TradeOutcome.Overcap) { stopped = "The next trade would go over the scrip cap (answered No)."; break; }
                            if (outcome == TradeOutcome.NoChange) { stopped = $"The trade of {itemName} didn't go through (collectability too low for this appraiser?)."; break; }
                            traded[group.Key] = traded.GetValueOrDefault(group.Key) + 1;
                            done++;
                            await Task.Delay(Random.Shared.Next(500, 800), ct).ConfigureAwait(false);
                        }
                        if (stopped is not null || done >= max) break;
                    }
                }
                finally
                {
                    await Game.Run(() => { CloseWindow(); return true; }).ConfigureAwait(false);
                    compat.ResumeClickers();
                }

                var after = await Game.Run(() => (Scrips: ScripCounts(), Left: CollectablesInBags())).ConfigureAwait(false);
                return new
                {
                    turnedIn = traded.Select(t => new { item = Items.Name(t.Key), count = t.Value }).ToList(),
                    scrips = Scrips.Where(s => plan.Scrips[s] != after.Scrips[s] || after.Scrips[s] > 0)
                        .Select(s => new { currency = Items.Name(s), before = plan.Scrips[s], after = after.Scrips[s], gained = after.Scrips[s] - plan.Scrips[s] }).ToList(),
                    stopped,
                    left = after.Left.GroupBy(c => c.ItemId).Select(g => new { item = Items.Name(g.Key), count = g.Count() }).ToList(),
                    steps,
                };
            },
        };
    }

    // ------------------------------------------------------------------ inventory and data

    private sealed record Collectable(uint ItemId, int Collectability);

    private static readonly Lazy<HashSet<uint>> AcceptedItems = new(() =>
        Svc.Data.GetSubrowExcelSheet<CollectablesShopItem>().SelectMany(s => s).Select(r => r.Item.RowId).Where(i => i != 0).ToHashSet());

    private static readonly InventoryType[] Bags = [InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4];

    private static unsafe List<Collectable> CollectablesInBags()
    {
        var im = InventoryManager.Instance();
        var list = new List<Collectable>();
        foreach (var bag in Bags)
        {
            var c = im->GetInventoryContainer(bag);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->ItemId == 0 || !s->Flags.HasFlag(InventoryItem.ItemFlags.Collectable) || !AcceptedItems.Value.Contains(s->ItemId)) continue;
                list.Add(new Collectable(s->ItemId, s->SpiritbondOrCollectability));
            }
        }
        return list;
    }

    private static int CountCollectable(uint itemId) => CollectablesInBags().Count(c => c.ItemId == itemId);

    private static unsafe Dictionary<uint, long> ScripCounts()
    {
        var im = InventoryManager.Instance();
        return Scrips.ToDictionary(s => s, s => (long)im->GetInventoryItemCount(s));
    }

    /// <summary>The window's job tab for an item: its recipe's craft type (0-7 = CRP..CUL), or 8/9/10 for MIN/BTN/FSH items.</summary>
    private static int? JobTab(uint itemId)
    {
        var recipe = Svc.Data.GetExcelSheet<Recipe>().FirstOrDefault(r => r.ItemResult.RowId == itemId);
        if (recipe.RowId != 0) return (int)recipe.CraftType.RowId;
        if (Svc.Data.GetExcelSheet<FishParameter>().Any(f => f.Item.RowId == itemId)) return 10;
        var gathering = Svc.Data.GetExcelSheet<GatheringItem>().FirstOrDefault(g => g.Item.RowId == itemId);
        if (gathering.RowId == 0) return null;
        foreach (var point in Svc.Data.GetExcelSheet<GatheringPointBase>())
            if (point.Item.Any(i => i.RowId == gathering.RowId))
                return point.GatheringType.RowId switch { 0 or 1 or 6 => 8, 2 or 3 or 5 => 9, 4 or 7 => 10, _ => null };
        return null;
    }

    // ------------------------------------------------------------------ the appraiser

    /// <summary>Items each collectables shop accepts (its ShopItems groups).</summary>
    private static readonly Lazy<Dictionary<uint, HashSet<uint>>> ShopItems = new(() =>
    {
        var groups = Svc.Data.GetSubrowExcelSheet<CollectablesShopItem>();
        return Svc.Data.GetExcelSheet<CollectablesShop>().ToDictionary(s => s.RowId, s => s.ShopItems
            .Where(g => g.RowId != 0 && groups.HasRow(g.RowId))
            .SelectMany(g => groups.GetRow(g.RowId)).Select(r => r.Item.RowId).Where(i => i != 0).ToHashSet());
    });

    /// <summary>
    /// Collectables shops of every NPC. NPCs reach their shops directly or through talk scripts (CustomTalk), pre-handlers and topic
    /// menus, so those are followed — the same resolution GatherBuddy Reborn does.
    /// </summary>
    private static readonly Lazy<Dictionary<uint, HashSet<uint>>> NpcShops = new(() =>
    {
        var shops = ShopItems.Value;
        var result = new Dictionary<uint, HashSet<uint>>();
        foreach (var npc in Svc.Data.GetExcelSheet<ENpcBase>())
        {
            var found = new HashSet<uint>();
            foreach (var entry in npc.ENpcData) Collect(entry, shops, found, 0);
            if (found.Count > 0) result[npc.RowId] = found;
        }
        return result;
    });

    private static void Collect(Lumina.Excel.RowRef entry, Dictionary<uint, HashSet<uint>> shops, HashSet<uint> found, int depth)
    {
        if (entry.RowId == 0 || depth > 4) return;
        if (entry.Is<CollectablesShop>()) { if (shops.ContainsKey(entry.RowId)) found.Add(entry.RowId); return; }
        if (entry.Is<CustomTalk>() && entry.GetValueOrDefault<CustomTalk>() is { } talk)
        {
            Collect(talk.SpecialLinks, shops, found, depth + 1); // e.g. the appraiser's collectables shop
            foreach (var s in talk.Script) if (shops.ContainsKey(s.ScriptArg)) found.Add(s.ScriptArg);
            return;
        }
        if (entry.Is<PreHandler>() && entry.GetValueOrDefault<PreHandler>() is { } pre) { Collect(pre.Target, shops, found, depth + 1); return; }
        if (entry.Is<TopicSelect>() && entry.GetValueOrDefault<TopicSelect>() is { } topic)
            foreach (var s in topic.Shop) Collect(s, shops, found, depth + 1);
    }

    /// <summary>NPCs with a collectables shop that accepts any of the items.</summary>
    private static HashSet<uint> AppraisersFor(IEnumerable<uint> items)
    {
        var wanted = items.ToHashSet();
        return NpcShops.Value.Where(n => n.Value.Any(s => ShopItems.Value[s].Overlaps(wanted))).Select(n => n.Key).ToHashSet();
    }

    private static (uint NpcId, ulong ObjectId)? NearbyAppraiser(HashSet<uint> npcs)
    {
        var obj = Svc.Objects.Where(o => o.IsTargetable && npcs.Contains(o.BaseId) && Game.DistanceToPlayer(o.Position) < 40)
            .OrderBy(o => Game.DistanceToPlayer(o.Position)).FirstOrDefault();
        return obj is null ? null : (obj.BaseId, obj.GameObjectId);
    }

    /// <summary>Where an appraiser stands (Item Vendor Location knows NPC positions): the requested one, else the current zone's, else any.</summary>
    private static NavigationTools.NpcSpot? AppraiserSpot(uint? wanted, HashSet<uint> npcs)
    {
        if (!ShopTools.ItemVendorLocationLoaded) return null;
        var here = Svc.ClientState.TerritoryType;
        NavigationTools.NpcSpot? fallback = null;
        foreach (var id in wanted is { } w ? [w] : npcs.ToArray())
        {
            (uint, (float, float))? loc = null;
            try { loc = Svc.PluginInterface.GetIpcSubscriber<uint, (uint, (float, float))?>("ItemVendorLocation.GetVendorLocation").InvokeFunc(id); }
            catch { /* unknown */ }
            if (loc is not { Item1: not 0 } l) continue;
            var spot = NavigationTools.FindNpc(id, l.Item1, l.Item2.Item1, l.Item2.Item2);
            if (spot is null) continue;
            if (spot.Territory == here) return spot;
            fallback ??= spot;
        }
        return fallback;
    }

    // ------------------------------------------------------------------ the window

    private static async Task OpenWindow((uint NpcId, ulong ObjectId) npc, List<string> steps, CancellationToken ct)
    {
        if (await Game.Run(() => RetainerUi.Ready(Window)).ConfigureAwait(false)) return;
        await Game.Run(() =>
        {
            var obj = Svc.Objects.FirstOrDefault(o => o.GameObjectId == npc.ObjectId) ?? throw new ToolException("The appraiser is gone.");
            unsafe
            {
                var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->SetHardTarget(native, false, false, 0);
                FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance()->InteractWithObject(native, true);
            }
            return true;
        }).ConfigureAwait(false);
        steps.Add($"Talking to {ItemSourceTools.NpcName(npc.NpcId)}.");

        var deadline = DateTime.UtcNow.AddSeconds(15);
        var lastAction = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            var state = await Game.Run(() =>
            {
                if (RetainerUi.Ready(Window)) return "open";
                if (DateTime.UtcNow - lastAction < TimeSpan.FromMilliseconds(800)) return null;
                if (RetainerUi.Ready("Talk")) { RetainerUi.ClickTalk(); return "talk"; }
                // Some appraisers show a menu first: pick the collectables entry.
                if (ShopTools.MenuEntries(out var menu) is { } entries)
                {
                    var i = entries.FindIndex(e => e.Contains("ollectable", StringComparison.OrdinalIgnoreCase) || e.Contains("Trade", StringComparison.OrdinalIgnoreCase));
                    if (i < 0) throw new ToolException($"Unexpected menu: {string.Join(" | ", entries)}");
                    ShopTools.FireMenu(menu, i);
                    return "menu";
                }
                return null;
            }).ConfigureAwait(false);
            if (state == "open") { steps.Add("Collectables window open."); await Task.Delay(500, ct).ConfigureAwait(false); return; }
            if (state is not null) lastAction = DateTime.UtcNow;
        }
        throw new ToolException("The collectables window didn't open.");
    }

    /// <summary>Position of an item among the window's entries (group headers skipped), by its label; -1 if missing.</summary>
    private static unsafe int ItemIndex(string itemName)
    {
        var addon = Addon();
        if (addon == null) return -1;
        AtkComponentTreeList* list = null;
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (node != null && (int)node->Type == 1028 && node->NodeId == 28 && node->GetAsAtkComponentNode()->Component != null)
                list = (AtkComponentTreeList*)node->GetAsAtkComponentNode()->Component;
        }
        if (list == null) return -1;
        var index = 0;
        for (var i = 0; i < list->Items.Count; i++)
        {
            var item = list->Items[i].Value;
            if (item == null) continue;
            var type = item->UIntValues.Count > 0 ? item->UIntValues[0] & 0xF : 0;
            if (type is 2 or 4) continue; // group headers
            var label = item->StringValues.Count > 0 ? SeString.Parse(item->StringValues[0].Value).TextValue : "";
            if (label.Contains(itemName, StringComparison.OrdinalIgnoreCase)) return index;
            index++;
        }
        return -1;
    }

    private enum TradeOutcome { Traded, Overcap, NoChange }

    /// <summary>After Trade: the collectable leaves the bags, or the game warns about overcapping (answered No).</summary>
    private static async Task<TradeOutcome> WaitForTrade(uint itemId, int before, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            var result = await Game.Run(() =>
            {
                unsafe
                {
                    var yesno = Svc.GameGui.GetAddonByName<AtkUnitBase>("SelectYesno", 1);
                    if (yesno != null && yesno->IsVisible && yesno->IsReady)
                    {
                        Fire(yesno, 1); // "No": never trade into the cap
                        return (TradeOutcome?)TradeOutcome.Overcap;
                    }
                }
                return CountCollectable(itemId) < before ? TradeOutcome.Traded : null;
            }).ConfigureAwait(false);
            if (result is { } r) return r;
        }
        return TradeOutcome.NoChange;
    }

    private static async Task Act(Func<string> action, CancellationToken ct)
    {
        await Game.Run(() =>
        {
            if (!RetainerUi.Ready(Window)) throw new ToolException("The collectables window closed.");
            return action();
        }).ConfigureAwait(false);
        await Task.Delay(200, ct).ConfigureAwait(false);
    }

    private static unsafe AtkUnitBase* Addon() => Svc.GameGui.GetAddonByName<AtkUnitBase>(Window, 1);

    private static unsafe void CloseWindow()
    {
        var addon = Addon();
        if (addon == null || !addon->IsVisible) return;
        Fire(addon, -1);
        addon->Close(true);
    }

    /// <summary>Fires the collectables window callback (an int command and a uint argument), like its own buttons do.</summary>
    private static unsafe void Fire(int command, uint value, bool updateState = false)
    {
        var addon = Addon();
        if (addon == null) throw new ToolException("The collectables window closed.");
        var atk = stackalloc AtkValue[2];
        atk[0] = default; atk[0].Type = AtkValueType.Int; atk[0].Int = command;
        atk[1] = default; atk[1].Type = AtkValueType.UInt; atk[1].UInt = value;
        addon->FireCallback(2, atk, updateState);
    }

    private static unsafe void Fire(AtkUnitBase* addon, int value)
    {
        var atk = stackalloc AtkValue[1];
        atk[0] = default; atk[0].Type = AtkValueType.Int; atk[0].Int = value;
        addon->FireCallback(1, atk, true);
    }
}
