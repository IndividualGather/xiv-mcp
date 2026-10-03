using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

internal static class InventoryTools
{
    private static readonly string[] EquipSlots =
        ["MainHand", "OffHand", "Head", "Body", "Hands", "Waist", "Legs", "Feet", "Ears", "Neck", "Wrists", "RightRing", "LeftRing", "SoulCrystal"];

    /// <summary>Named groups of containers; raw GameInventoryType names are accepted as well.</summary>
    private static readonly Dictionary<string, GameInventoryType[]> Groups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bags"] = [GameInventoryType.Inventory1, GameInventoryType.Inventory2, GameInventoryType.Inventory3, GameInventoryType.Inventory4],
        ["equipped"] = [GameInventoryType.EquippedItems],
        ["armory"] = ByPrefix("Armory"),
        ["currency"] = [GameInventoryType.Currency],
        ["crystals"] = [GameInventoryType.Crystals],
        ["key_items"] = [GameInventoryType.KeyItems],
        ["saddlebag"] = [GameInventoryType.SaddleBag1, GameInventoryType.SaddleBag2, GameInventoryType.PremiumSaddleBag1, GameInventoryType.PremiumSaddleBag2],
        ["retainer"] = ByPrefix("Retainer"),
        ["free_company"] = ByPrefix("FreeCompany"),
        ["housing"] = ByPrefix("Housing"),
    };

    private static readonly string[] DefaultSearchGroups = ["bags", "equipped", "armory", "crystals", "key_items", "saddlebag"];

    private static GameInventoryType[] ByPrefix(string prefix) =>
        Enum.GetValues<GameInventoryType>().Where(t => t.ToString().StartsWith(prefix, StringComparison.Ordinal)).ToArray();

    private static readonly string ContainerHelp =
        $"Groups: {string.Join(", ", Groups.Keys)}. Raw container names are also accepted, e.g. {string.Join(", ", Enum.GetNames<GameInventoryType>().Take(6))}, ...";

    public static IEnumerable<McpTool> Create(RetainerTracker retainers)
    {
        yield return new McpTool
        {
            Name = "get_inventory",
            Description = "Lists the items in one or more inventory containers of the logged-in character (default: the four main bags), " +
                          "with item names, quantities, HQ/collectable flags, spiritbond, condition, materia and dyes. Also reports free bag slots. " +
                          "Saddlebag requires the saddlebag to have been opened once this session; retainer containers only reflect the retainer that was last opened. " +
                          ContainerHelp,
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "containers": { "type": "array", "items": { "type": "string" }, "description": "Container groups or raw container names. Default: [\"bags\"]." },
                    "include_details": { "type": "boolean", "description": "Include materia, dyes, spiritbond and condition (default false)." }
                  }
                }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                var containers = ResolveContainers(args.StringList("containers"), ["bags"]);
                var details = args.Bool("include_details", false);
                var result = containers.Select(c =>
                {
                    var items = Svc.Inventory.GetInventoryItems(c);
                    return new
                    {
                        container = c.ToString(),
                        slots = items.Length,
                        used = items.ToArray().Count(i => !i.IsEmpty),
                        // Listed in the order the player sees (the game's sort only changes the displayed order, not the slots).
                        items = WithDisplayOrder(c, items.ToArray().Where(i => !i.IsEmpty), details),
                    };
                }).ToList();
                unsafe
                {
                    return new { freeBagSlots = InventoryManager.Instance()->GetEmptySlotsInBag(), containers = result };
                }
            }),
        };

        yield return new McpTool
        {
            Name = "search_inventory",
            Description = "Finds items by (partial) name or item id across the character's containers — by default bags, equipped gear, armory chest, " +
                          "crystals, key items and saddlebag (live) plus every retainer (from the retainer cache, each with its snapshot age) — " +
                          "and reports where each stack is and the total count. " + ContainerHelp,
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Case-insensitive part of the item name." },
                    "item_id": { "type": "integer", "description": "Exact item id (alternative to query)." },
                    "containers": { "type": "array", "items": { "type": "string" }, "description": "Restrict the live search to these container groups / names." },
                    "include_retainers": { "type": "boolean", "description": "Also search all retainers' cached inventories (default true)." }
                  }
                }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                var query = args.String("query");
                var itemId = args.UInt("item_id");
                if (query is null && itemId is null) throw new ToolException("Provide 'query' or 'item_id'.");
                bool Wanted(uint baseId, out string? name)
                {
                    name = null;
                    if (itemId is { } id && baseId != id) return false;
                    name = ItemName(baseId);
                    return query is null || Game.Matches(name, query);
                }

                var hits = new List<(uint ItemId, string? Name, int Quantity, object Location)>();
                foreach (var c in ResolveContainers(args.StringList("containers"), DefaultSearchGroups))
                {
                    foreach (var item in Svc.Inventory.GetInventoryItems(c))
                    {
                        if (item.IsEmpty || !Wanted(item.BaseItemId, out var name)) continue;
                        hits.Add((item.BaseItemId, name, item.Quantity,
                            new { container = item.ContainerType.ToString(), slot = item.InventorySlot, quantity = item.Quantity, hq = item.IsHq }));
                    }
                }

                // Saddlebag and FC chest: live when the game has them loaded, otherwise the last snapshot.
                var storageNotes = new List<object>();
                foreach (var (key, group) in args.StringList("containers").Count == 0 ? StorageTracker.Groups : [])
                {
                    if (StorageTracker.IsLoaded(key))
                    {
                        if (key == "saddlebag") continue; // already part of the live search above
                        foreach (var c in group.Containers)
                            foreach (var item in Svc.Inventory.GetInventoryItems(c))
                            {
                                if (item.IsEmpty || !Wanted(item.BaseItemId, out var name)) continue;
                                hits.Add((item.BaseItemId, name, item.Quantity, new { container = c.ToString(), slot = item.InventorySlot, quantity = item.Quantity, hq = item.IsHq }));
                            }
                        continue;
                    }
                    if (StorageTracker.Instance?.Get(key) is not { } stored) continue;
                    var found = false;
                    foreach (var i in stored.Items)
                    {
                        if (!Wanted(i.ItemId, out var name)) continue;
                        found = true;
                        hits.Add((i.ItemId, name, i.Quantity, new { container = i.Container, slot = i.Slot, quantity = i.Quantity, hq = i.Hq, cachedAt = stored.CapturedUtc }));
                    }
                    if (found) storageNotes.Add(new { storage = group.Title, cache = CacheFreshness.Describe(stored.CapturedUtc, false, group.Hint) });
                }

                var retainerNotes = new List<object>();
                if (args.Bool("include_retainers", true) && retainers.Get(Svc.PlayerState.ContentId) is { } cached)
                {
                    foreach (var r in cached.Retainers)
                    {
                        if (r.InventoryCapturedUtc is not { } captured)
                        {
                            retainerNotes.Add(new { retainer = r.Name, notCaptured = true, suggestion = RetainerTracker.InventoryRefreshHint(r.Name) });
                            continue;
                        }
                        var any = false;
                        foreach (var i in r.Items.Concat(r.Market).Concat(r.Crystals))
                        {
                            if (!Wanted(i.ItemId, out var name)) continue;
                            any = true;
                            hits.Add((i.ItemId, name, i.Quantity, new { retainer = r.Name, container = i.Container, slot = i.Slot, quantity = i.Quantity, hq = i.Hq, cachedAt = captured }));
                        }
                        if (any) retainerNotes.Add(new { retainer = r.Name, cache = CacheFreshness.Describe(captured, RetainerUi.InventoryOpenFor(r.Name), RetainerTracker.InventoryRefreshHint(r.Name)) });
                    }
                }

                return new
                {
                    items = hits.GroupBy(h => h.ItemId).Select(g => new
                    {
                        itemId = g.Key,
                        name = g.First().Name,
                        totalQuantity = g.Sum(h => h.Quantity),
                        locations = g.Select(h => h.Location).ToList(),
                    }).ToList(),
                    retainerCaches = retainerNotes.Count > 0 ? retainerNotes : null,
                    storageCaches = storageNotes.Count > 0 ? storageNotes : null,
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_equipment",
            Description = "The gear currently equipped by the logged-in character, per slot, with item level, equip level, materia, dyes, glamour and " +
                          "spiritbond/condition, plus the average item level.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
            {
                var items = Svc.Inventory.GetInventoryItems(GameInventoryType.EquippedItems).ToArray();
                var slots = new List<object>();
                var ilvls = new List<uint>();
                foreach (var item in items)
                {
                    var slot = item.InventorySlot < EquipSlots.Length ? EquipSlots[item.InventorySlot] : item.InventorySlot.ToString();
                    if (item.IsEmpty) { slots.Add(new { slot, empty = true }); continue; }
                    var row = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(item.BaseItemId);
                    var desc = DescribeItem(item, true);
                    desc["slot"] = slot;
                    if (row is { } r)
                    {
                        desc["itemLevel"] = r.LevelItem.RowId;
                        desc["equipLevel"] = r.LevelEquip;
                        if (slot is not ("SoulCrystal" or "Waist")) ilvls.Add(r.LevelItem.RowId);
                    }
                    slots.Add(desc);
                }
                return new { averageItemLevel = ilvls.Count > 0 ? Math.Round(ilvls.Average(l => (double)l), 1) : 0, slots };
            }),
        };

        yield return new McpTool
        {
            Name = "get_currencies",
            Description = "All currencies of the logged-in character: gil, MGP, Wolf Marks, Allied Seals, Grand Company seals (with cap), " +
                          "every allagan tomestone type, the weekly tomestone cap progress, retainer gil, plus the raw contents of the currency container (scrips, etc.).",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
            {
                unsafe
                {
                    var im = InventoryManager.Instance();
                    var gcSeals = Svc.Data.GetExcelSheet<GrandCompany>().Where(gc => gc.RowId != 0).Select(gc => new
                    {
                        grandCompany = gc.Name.ExtractText(),
                        seals = im->GetCompanySeals((byte)gc.RowId),
                        max = im->GetMaxCompanySeals((byte)gc.RowId),
                    }).ToList();

                    var tomestones = Svc.Data.GetExcelSheet<TomestonesItem>()
                        .Where(t => t.Item.RowId != 0)
                        .Select(t => new { itemId = t.Item.RowId, name = Excel.Name(t.Item), count = im->GetTomestoneCount(t.Item.RowId) })
                        .Where(t => !string.IsNullOrEmpty(t.name))
                        .ToList();

                    var container = Svc.Inventory.GetInventoryItems(GameInventoryType.Currency).ToArray()
                        .Where(i => !i.IsEmpty)
                        .Select(i => new { itemId = i.BaseItemId, name = ItemName(i.BaseItemId), quantity = i.Quantity })
                        .ToList();

                    return new
                    {
                        gil = im->GetGil(),
                        retainerGil = im->GetRetainerGil(),
                        mgp = im->GetGoldSaucerCoin(),
                        wolfMarks = im->GetWolfMarks(),
                        alliedSeals = im->GetAlliedSeals(),
                        grandCompanySeals = gcSeals,
                        tomestones,
                        weeklyTomestones = new { acquired = im->GetWeeklyAcquiredTomestoneCount(), limit = InventoryManager.GetLimitedTomestoneWeeklyLimit() },
                        currencyContainer = container,
                    };
                }
            }),
        };
    }

    private static List<GameInventoryType> ResolveContainers(List<string> requested, string[] fallback)
    {
        var names = requested.Count > 0 ? requested : fallback.ToList();
        var result = new List<GameInventoryType>();
        foreach (var name in names)
        {
            if (Groups.TryGetValue(name, out var group)) result.AddRange(group);
            else if (Enum.TryParse<GameInventoryType>(name, true, out var t)) result.Add(t);
            else throw new ToolException($"Unknown container '{name}'. {ContainerHelp}");
        }
        return result.Distinct().ToList();
    }

    /// <summary>
    /// Adds "shown" (page/slot as displayed in game, 0-based) and orders by it. "slot" stays the physical slot that move_items uses.
    /// </summary>
    private static List<Dictionary<string, object?>> WithDisplayOrder(GameInventoryType container, IEnumerable<GameInventoryItem> items, bool details)
    {
        var order = ItemOrder.For(container);
        return items.Select(i =>
            {
                var d = DescribeItem(i, details);
                if (order.TryGetValue((container, (int)i.InventorySlot), out var pos)) d["shown"] = new { page = pos.Page, slot = pos.Slot };
                return (d, index: order.TryGetValue((container, (int)i.InventorySlot), out var p) ? p.Index : int.MaxValue);
            })
            .OrderBy(x => x.index)
            .Select(x => x.d)
            .ToList();
    }

    public static string? ItemName(uint itemId)
    {
        if (Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is { } item) return item.Name.ExtractText();
        if (Svc.Data.GetExcelSheet<EventItem>().GetRowOrDefault(itemId) is { } ev) return ev.Name.ExtractText();
        return null;
    }

    private static Dictionary<string, object?> DescribeItem(GameInventoryItem i, bool details)
    {
        var d = new Dictionary<string, object?>
        {
            ["slot"] = i.InventorySlot,
            ["itemId"] = i.BaseItemId,
            ["name"] = ItemName(i.BaseItemId),
            ["quantity"] = i.Quantity,
        };
        if (i.IsHq) d["hq"] = true;
        if (i.IsCollectable) d["collectability"] = i.SpiritbondOrCollectability;
        if (!details) return d;

        if (!i.IsCollectable) d["spiritbondPercent"] = Math.Round(i.SpiritbondOrCollectability / 100.0, 2);
        d["conditionPercent"] = Math.Round(i.Condition / 300.0, 1);
        var materia = i.MateriaEntries
            .Where(m => m.Type.RowId != 0)
            .Select(m => m.Type.ValueNullable is { } row && m.Grade.RowId < row.Item.Count
                ? Excel.Name(row.Item[(int)m.Grade.RowId])
                : $"materia {m.Type.RowId}/{m.Grade.RowId}")
            .ToList();
        if (materia.Count > 0) d["materia"] = materia;
        var stains = i.Stains.ToArray().Where(s => s != 0).Select(s => Excel.Ref<Stain>(s)).ToList();
        if (stains.Count > 0) d["dyes"] = stains;
        if (i.GlamourId != 0) d["glamour"] = new { itemId = i.GlamourId, name = ItemName(i.GlamourId) };
        return d;
    }
}
