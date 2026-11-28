using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XivMcp.Caching;
using XivMcp.Fashion;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// The Fashion Report planner: from the week's hints, the items fashionreportxiv.com lists for each, the week's dyes and what the
/// player has, the cheapest outfit that reaches the score asked for (<see cref="FashionPlanner"/>), and the job that gets it on.
/// </summary>
internal static partial class FashionTools
{
    private const string HintUrl = "https://fashionreportxiv.com/api/hint";

    /// <summary>The items the site lists per hint, kept like the report.</summary>
    private static readonly TimedCache<string, IReadOnlyList<string>> HintLists = new(TimeSpan.FromMinutes(5));

    /// <summary>A planned outfit, and what the steps need to know about it.</summary>
    private sealed record Planned(FashionOutfit Outfit, int Target, IReadOnlyDictionary<int, (uint Stain, string Name, List<uint> DyeItems)> Wanted,
                                  Dictionary<uint, (uint TerritoryType, string Name, string Mode)> Duties, bool DyesKnown, long Gil)
    {
        public IEnumerable<FashionChoice> Changes => Outfit.Choices.Where(c => c.Piece is not null);

        /// <summary>What changes, as lines for the player, and what it costs.</summary>
        public IReadOnlyList<string> Lines() =>
            [.. FashionOutfitText.Lines(Outfit, slot => Wanted.TryGetValue(slot, out var w) ? w.Name : null),
             .. FashionOutfitText.CostLine(Gil) is { } cost ? [cost] : Array.Empty<string>()];

        public object Describe() => new
        {
            target = Target, expectedScore = Outfit.Score, reachesTarget = Outfit.ReachesTarget, dyesKnown = DyesKnown, gil = Gil,
            slots = Outfit.Choices.Select(c => new
            {
                slot = GearSlots.Name(c.Slot), item = c.Piece?.Name, source = c.Piece?.Source.ToString() ?? "Empty", retainer = c.Piece?.Retainer,
                matchesHint = c.Piece?.Matches, dye = c.Dye == DyeChange.None ? null : c.Dye.ToString(),
                color = c.Dye == DyeChange.None ? null : Wanted.GetValueOrDefault(c.Slot).Name,
                channel = c.Dye is DyeChange.Dye or DyeChange.Replace ? c.Channel : (int?)null, points = c.Points,
            }),
            note = "Expected scores follow the rules players have worked out (not game data): 10 points for the weapon and each armour " +
                   "slot, 8 for each accessory, only 2 in a hinted slot without a matching piece, 0 for an empty slot, and 2 more for the " +
                   "week's exact dye on the weapon and armour. Dyes of the right color family (1 point in game) are not counted.",
        };
    }

    /// <summary>
    /// Plans the outfit for <paramref name="target"/> points. Pieces that would replace a dye of another color are only planned when
    /// <paramref name="allowReplace"/>. Goes online for the hint lists and the market prices of pieces to buy there.
    /// </summary>
    private static async Task<Planned> PlanOutfit(WeeklyReport report, FashionSet set, int target, RetainerTracker retainers, int maxMarketPrice,
                                                  bool allowReplace, CancellationToken ct)
    {
        if (report.Hints.Count == 0) throw new ToolException("fashionreportxiv.com has no hints for this week yet.");
        var lists = new Dictionary<FashionHint, IReadOnlyList<string>>();
        foreach (var hint in report.Hints) lists[hint] = await HintItemNames(hint, ct).ConfigureAwait(false);

        // Until the site has found this week's dyes, its dyes are last week's; when the whole guide is an earlier week's, they go with it.
        var dyesKnown = report.DyesFresh || report.Week != FashionSchedule.WeekAt(DateTime.UtcNow);
        var (slots, wanted, market) = await Game.RunLoggedIn(() => Candidates(report, set, lists, retainers, dyesKnown)).ConfigureAwait(false);

        // Pieces only on the market board: their lowest listing on the home world, so the plan compares real prices.
        var prices = new Dictionary<uint, long>();
        if (market.Count > 0)
        {
            try
            {
                var world = await Game.Run(() => Universalis.HomeScopes().World).ConfigureAwait(false);
                foreach (var (id, m) in await Universalis.Get(market, world, 5, 0, ct).ConfigureAwait(false))
                    if (m.Listings.Count > 0) prices[id] = m.Listings.Min(l => l.PricePerUnit);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Svc.Log.Warning($"[MCP] Fashion Report market prices: {ex.Message}"); }
        }
        slots = slots.Select(s => s with
        {
            Candidates = s.Candidates
                .Select(c => c.Source == PieceSource.Market ? c with { Price = prices.TryGetValue(c.ItemId, out var p) ? p : maxMarketPrice } : c)
                .Where(c => c.Source != PieceSource.Market || c.Price <= maxMarketPrice).ToList(),
        }).ToList();

        // Pieces with no other way: a dungeon that drops them, if AutoDuty can run one. Only for the site's own pieces, as before.
        var duties = new Dictionary<uint, (uint TerritoryType, string Name, string Mode)>();
        var unreachable = await Game.Run(() => set.Pieces.Select(p => TryResolve(p.Item)).Where(id => id is { } i && !slots.Any(s => s.Candidates.Any(c => c.ItemId == i)))
            .Select(id => id!.Value).Distinct().ToList()).ConfigureAwait(false);
        foreach (var id in unreachable)
            if (await DutyTools.FindFarmDuty(id, ct).ConfigureAwait(false) is { } duty) duties[id] = duty;
        if (duties.Count > 0)
            slots = await Game.Run(() => slots.Select(s => s with
            {
                Candidates = s.Candidates.Concat(duties.Keys.Where(id => s.Hinted && Fits(id, s.Slot)).Select(id =>
                    new FashionCandidate(id, Items.Name(id), PieceSource.Duty, true, 0, Channels(id)))).ToList(),
            }).ToList()).ConfigureAwait(false);

        var dyeCost = await Game.Run(() => DyeCosts(wanted)).ConfigureAwait(false);
        var outfit = FashionPlanner.Plan(slots, target, stain => dyeCost.GetValueOrDefault(stain, FashionPlanner.Buy), allowReplace);
        var gil = FashionOutfitText.PiecesGil(outfit) + await Game.Run(() => DyesGil(outfit, wanted)).ConfigureAwait(false);
        return new Planned(outfit, target, wanted, duties, dyesKnown, gil);
    }

    private static async Task<IReadOnlyList<string>> HintItemNames(FashionHint hint, CancellationToken ct)
    {
        var url = $"{HintUrl}?hint={Uri.EscapeDataString(hint.Hint)}&slot={Uri.EscapeDataString(hint.Slot)}";
        try
        {
            return (await HintLists.Get(url, async () =>
            {
                using var timeout = new CancellationTokenSource(ReportTimeout);
                return WeeklyReport.HintItems(await Teamcraft.Http.GetStringAsync(url, timeout.Token).ConfigureAwait(false));
            }, ct).ConfigureAwait(false)).Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Without the list the hint's slot still has the site's own piece.
            Svc.Log.Warning($"[MCP] Fashion Report hint list for '{hint.Hint}': {ex.Message}");
            return [];
        }
    }

    /// <summary>Every judged slot with the pieces it may take. Framework thread.</summary>
    private static unsafe (List<FashionSlot> Slots, Dictionary<int, (uint Stain, string Name, List<uint> DyeItems)> Wanted, List<uint> Market)
        Candidates(WeeklyReport report, FashionSet set, Dictionary<FashionHint, IReadOnlyList<string>> lists, RetainerTracker retainers, bool dyesKnown)
    {
        var wanted = new Dictionary<int, (uint, string, List<uint>)>();
        foreach (var (slotName, dye) in report.WantedDyes(set, dyesKnown))
        {
            int slot;
            try { slot = GearSlots.FromName(slotName); }
            catch (ArgumentException) { continue; }
            if (!FashionScore.DyeCounts(slot)) continue;
            var (stain, items) = DyeItemsOf(dye);
            wanted[slot] = (stain, dye, items);
        }

        var hinted = new Dictionary<int, HashSet<uint>>();
        foreach (var (hint, names) in lists)
        {
            int slot;
            try { slot = GearSlots.FromName(hint.Slot); }
            catch (ArgumentException) { continue; }
            hinted[slot] = names.Select(TryResolve).OfType<uint>().ToHashSet();
        }
        // The site's own piece for a slot is a candidate even when the hint list lacks it, and is the only one that may come out of the dresser.
        var picks = new Dictionary<int, uint>();
        foreach (var piece in set.Pieces)
        {
            if (TryResolve(piece.Item) is not { } id) continue;
            int slot;
            try { slot = GearSlots.FromName(piece.Slot); }
            catch (ArgumentException) { continue; }
            picks[slot] = id;
            if (hinted.TryGetValue(slot, out var ids)) ids.Add(id);
        }

        var im = InventoryManager.Instance();
        var equipped = im->GetInventoryContainer(InventoryType.EquippedItems);
        var carried = new List<(uint ItemId, byte Stain, byte Stain2)>();
        foreach (var type in Carried)
        {
            var c = im->GetInventoryContainer(type);
            for (var i = 0; c != null && i < c->Size; i++)
                if (c->GetInventorySlot(i) is var s && s != null && s->ItemId != 0) carried.Add((s->ItemId, s->GetStain(0), s->GetStain(1)));
        }
        var stored = retainers.Get(Svc.PlayerState.ContentId)?.Retainers.SelectMany(r => r.Items.Select(i => (r.Name, Item: i))).ToList() ?? [];

        var market = new List<uint>();
        var slots = new List<FashionSlot>();
        foreach (var slot in FashionScore.Judged)
        {
            var isHinted = hinted.TryGetValue(slot, out var hintIds);
            hintIds ??= [];
            var stain = wanted.TryGetValue(slot, out var w) ? w.Item1 : (uint?)null;
            var list = new List<FashionCandidate>();

            var wornSlot = equipped->GetInventorySlot(slot);
            if (wornSlot->ItemId != 0 && !Blocks(wornSlot->ItemId))
            {
                var look = wornSlot->GlamourId != 0 ? wornSlot->GlamourId : wornSlot->ItemId;
                list.Add(new FashionCandidate(wornSlot->ItemId, Items.Name(look), PieceSource.Worn, hintIds.Contains(look), wornSlot->GetStain(0), Channels(look),
                    Stain2: wornSlot->GetStain(1)));
            }
            // The weapon goes with the job: never changed.
            if (slot == GearSlots.MainHand) { slots.Add(new FashionSlot(slot, isHinted, stain, list)); continue; }

            bool Taken(uint id) => list.Any(c => c.ItemId == id);
            foreach (var id in hintIds.Where(id => !Taken(id) && Fits(id, slot) && Wearable(id)))
            {
                var copies = carried.Where(c => c.ItemId == id).ToList();
                var retainer = stored.FirstOrDefault(s => s.Item.ItemId == id);
                var item = Svc.Data.GetExcelSheet<Item>().GetRow(id);
                var source =
                    copies.Count > 0 ? PieceSource.Carried
                    : retainer.Name is not null ? PieceSource.Retainer
                    : picks.GetValueOrDefault(slot) == id && InDresser(id) ? PieceSource.Dresser
                    : InArmoire(id) ? PieceSource.Armoire
                    : BuyableAtVendor(id) ? PieceSource.Vendor
                    : !item.IsUntradable && item.ItemSearchCategory.RowId != 0 ? PieceSource.Market
                    : PieceSource.Empty;
                if (source == PieceSource.Empty) continue;
                if (source == PieceSource.Market) market.Add(id);
                var copy = source == PieceSource.Carried ? copies.FirstOrDefault(c => c.Stain == stain || c.Stain2 == stain, copies[0]) : default;
                (byte? dye, byte? dye2) = source switch
                {
                    PieceSource.Carried => (copy.Stain, copy.Stain2),
                    PieceSource.Retainer => (retainer.Item.Stain, retainer.Item.Stain2),
                    PieceSource.Dresser => (null, null), // the dresser's copy may be dyed; not changed here
                    _ => ((byte?)0, (byte?)0),
                };
                list.Add(new FashionCandidate(id, Items.Name(id), source, true, dye, Channels(id),
                    source == PieceSource.Vendor ? item.PriceMid : null, retainer.Name, source == PieceSource.Carried ? copies.Count : 1, dye2));
            }

            // Other pieces for the slot: one already in the week's color, one undyed to dye, one of any kind to fill it.
            var fitting = carried.Where(c => !Taken(c.ItemId) && !hintIds.Contains(c.ItemId) && Fits(c.ItemId, slot) && Wearable(c.ItemId)).ToList();
            var others = new[]
            {
                stain is { } s ? fitting.FirstOrDefault(c => c.Stain == s || c.Stain2 == s && Channels(c.ItemId) > 1) : default,
                stain is not null ? fitting.FirstOrDefault(c => Channels(c.ItemId) > 0 && (c.Stain == 0 || c.Stain2 == 0 && Channels(c.ItemId) > 1)) : default,
                fitting.FirstOrDefault(),
            };
            foreach (var (id, dye, dye2) in others.Where(o => o.ItemId != 0).Distinct())
                list.Add(new FashionCandidate(id, Items.Name(id), PieceSource.Carried, false, dye, Channels(id), Copies: fitting.Count(c => c.ItemId == id), Stain2: dye2));
            // A piece a retainer holds that already has the week's color: the dye without dyeing anything.
            if (stain is { } wantedStain && !list.Any(c => c.Stain == wantedStain || c.Channels > 1 && c.Stain2 == wantedStain)
                && stored.FirstOrDefault(s => !Taken(s.Item.ItemId) && !hintIds.Contains(s.Item.ItemId) && Channels(s.Item.ItemId) > 0
                                              && (s.Item.Stain == wantedStain || Channels(s.Item.ItemId) > 1 && s.Item.Stain2 == wantedStain)
                                              && Fits(s.Item.ItemId, slot) && Wearable(s.Item.ItemId)) is { Name: not null } kept)
                list.Add(new FashionCandidate(kept.Item.ItemId, Items.Name(kept.Item.ItemId), PieceSource.Retainer, false, kept.Item.Stain,
                    Channels(kept.Item.ItemId), Retainer: kept.Name, Stain2: kept.Item.Stain2));
            foreach (var id in new[] { CheapestAtVendor(slot, false), stain is not null ? CheapestAtVendor(slot, true) : null }.OfType<uint>().Distinct())
                if (!Taken(id)) list.Add(new FashionCandidate(id, Items.Name(id), PieceSource.Vendor, hintIds.Contains(id), 0, Channels(id), Svc.Data.GetExcelSheet<Item>().GetRow(id).PriceMid));

            slots.Add(new FashionSlot(slot, isHinted, stain, list));
        }
        return (slots, wanted, market.Distinct().ToList());
    }

    /// <summary>
    /// What a dye of each wanted color costs the plan: one in the bags costs what replacing it would (its vendor price; one no vendor
    /// sells costs more than buying a vendor's, so a rarer dye is not spent while a cheaper one would do); otherwise buying one.
    /// Framework thread.
    /// </summary>
    private static unsafe Dictionary<uint, long> DyeCosts(IReadOnlyDictionary<int, (uint Stain, string Name, List<uint> DyeItems)> wanted)
    {
        var im = InventoryManager.Instance();
        var items = Svc.Data.GetExcelSheet<Item>();
        long Price(uint dye) => SoldByVendor(dye) ? items.GetRowOrDefault(dye)?.PriceMid ?? 0 : 2 * FashionPlanner.Buy;
        return wanted.Values.DistinctBy(w => w.Stain).ToDictionary(w => w.Stain, w =>
            w.DyeItems.FirstOrDefault(i => im->GetInventoryItemCount(i) > 0) is var owned && owned != 0 ? FashionPlanner.DyeOwned + Price(owned)
            : FashionPlanner.Buy + (w.DyeItems.Count > 0 ? Price(w.DyeItems[0]) : 0));
    }

    private static uint? TryResolve(string name)
    {
        try { return ResolveItem(name, english: true).RowId; }
        catch (ToolException) { return null; }
    }

    /// <summary>How many dye channels the item has (0 = it takes no dye).</summary>
    private static int Channels(uint itemId) => Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.DyeCount ?? 0;

    private static sbyte[] SlotFlags(uint itemId)
    {
        if (Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.EquipSlotCategory.ValueNullable is not { } c) return [];
        return [c.MainHand, c.OffHand, c.Head, c.Body, c.Gloves, c.Waist, c.Legs, c.Feet, c.Ears, c.Neck, c.Wrists, c.FingerL, c.FingerR, c.SoulCrystal];
    }

    /// <summary>Whether the item goes in this slot (a ring in either hand).</summary>
    private static bool Fits(uint itemId, int slot)
    {
        var flags = SlotFlags(itemId);
        if (flags.Length == 0 || GearSlots.Target(flags) is not { } target) return false;
        return target == slot || (target is GearSlots.RingRight or GearSlots.RingLeft && slot is GearSlots.RingRight or GearSlots.RingLeft);
    }

    /// <summary>Pieces that cover more than one slot (a robe without legs) are left out: the slot they hide would be judged empty.</summary>
    private static bool Blocks(uint itemId) => SlotFlags(itemId).Skip(2).Any(f => f == -1);

    /// <summary>Whether the character can wear it: the current job and level, and the race and gender some gear is made for. Framework thread.</summary>
    private static bool Wearable(uint itemId)
    {
        if (Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId) is not { } item || Blocks(itemId)) return false;
        var player = Svc.Objects.LocalPlayer;
        if (player is null || item.LevelEquip > player.Level || !FitsBody(item, player.Customize.ToArray())) return false;
        return item.ClassJobCategory.ValueNullable is not { } jobs || player.ClassJob.ValueNullable is not { } job
            || typeof(ClassJobCategory).GetProperty(job.Abbreviation.ExtractText())?.GetValue(jobs) is not false;
    }

    /// <summary>Gear made for one gender or some races only (Hempen Underpants, racial starter gear). Customize: [0] race 1-8, [1] 0 male, 1 female.</summary>
    private static bool FitsBody(Item item, IReadOnlyList<byte> customize)
    {
        if (item.EquipRestriction.ValueNullable is not { } r || customize.Count < 2) return true;
        var gender = customize[1] == 0 ? r.Male : r.Female;
        var race = customize[0] switch
        {
            1 => r.Hyur, 2 => r.Elezen, 3 => r.Lalafell, 4 => r.Miqote, 5 => r.Roegadyn, 6 => r.AuRa, 7 => r.Hrothgar, 8 => r.Viera, _ => true,
        };
        return gender && race;
    }

    /// <summary>Gear sold for gil, by the slot it goes in: for filling a slot when the player has nothing that fits.</summary>
    private static readonly Lazy<List<(uint ItemId, int Slot, uint Price, bool Dyeable)>> VendorGear = new(() =>
        VendorItems.Value.Select(id => Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(id)).OfType<Item>()
            .Where(i => i.EquipSlotCategory.RowId != 0 && i.PriceMid > 0 && !Blocks(i.RowId))
            .Select(i => (i.RowId, Slot: GearSlots.Target(SlotFlags(i.RowId)) ?? -1, i.PriceMid, i.DyeCount > 0))
            .Where(i => i.Slot > GearSlots.OffHand).OrderBy(i => i.PriceMid).ToList());

    private static uint? CheapestAtVendor(int slot, bool dyeable)
    {
        var target = slot == GearSlots.RingLeft ? GearSlots.RingRight : slot;
        foreach (var g in VendorGear.Value)
            if (g.Slot == target && (!dyeable || g.Dyeable) && Wearable(g.ItemId) && BuyableAtVendor(g.ItemId)) return g.ItemId;
        return null;
    }

    /// <summary>Each gil shop entry of an item: the shop, and what it asks of the player first (quests done, an achievement).</summary>
    private static readonly Lazy<ILookup<uint, (uint Shop, uint[] Quests, uint Achievement)>> VendorEntries = new(() =>
        Svc.Data.GetSubrowExcelSheet<GilShopItem>()
            .SelectMany(rows => rows.Where(i => i.Item.RowId != 0).Select(i => (Shop: rows.RowId, Entry: i)))
            .ToLookup(e => e.Entry.Item.RowId, e => (e.Shop, e.Entry.QuestRequired.Where(q => q.RowId != 0).Select(q => q.RowId).ToArray(), e.Entry.AchievementRequired.RowId)));

    /// <summary>The NPCs that carry each gil shop: directly, or behind a greeting or a sub-menu, as buy_item follows them.</summary>
    private static readonly Lazy<ILookup<uint, uint>> ShopNpcs = new(() =>
    {
        var pairs = new List<(uint Shop, uint Npc)>();
        foreach (var npc in Svc.Data.GetExcelSheet<ENpcBase>())
            foreach (var data in npc.ENpcData) Visit(npc.RowId, data, 0);
        return pairs.Distinct().ToLookup(p => p.Shop, p => p.Npc);

        void Visit(uint npc, Lumina.Excel.RowRef entry, int depth)
        {
            if (entry.RowId == 0 || depth > 3) return;
            if (entry.RowId is >= 262144 and < 327680) pairs.Add((entry.RowId, npc));
            else if (entry.Is<PreHandler>() && entry.GetValueOrDefault<PreHandler>() is { } pre) Visit(npc, pre.Target, depth + 1);
            else if (entry.Is<TopicSelect>() && entry.GetValueOrDefault<TopicSelect>() is { } topic)
                foreach (var shop in topic.Shop) Visit(npc, shop, depth + 1);
        }
    });

    /// <summary>
    /// Whether a gil shop sells the item to this player at an NPC buy_item can go to. The shop's entry must ask for nothing, or for
    /// quests the player has done and an achievement they have (seasonal event shops only list an event's items to players who did
    /// it; an achievement only counts once the game has sent them). And one of the NPCs carrying that shop must stand outside a house
    /// (NPCs players hire into their homes, like the Journeyman Salvager, carry shops no one else does). Without Item Vendor
    /// Location the NPCs' places are unknown, and any NPC counts. Framework thread.
    /// </summary>
    private static unsafe bool BuyableAtVendor(uint itemId)
    {
        var achievements = FFXIVClientStructs.FFXIV.Client.Game.UI.Achievement.Instance();
        var territories = Svc.Data.GetExcelSheet<TerritoryType>();
        bool Reachable(uint npc) =>
            !ShopTools.ItemVendorLocationLoaded
            || ShopTools.VendorTerritory(npc) is { } t && territories.GetRowOrDefault(t)?.TerritoryIntendedUse.RowId != HouseInterior;
        return VendorEntries.Value[itemId].Any(e =>
            e.Quests.All(q => QuestManager.IsQuestComplete(q))
            && (e.Achievement == 0 || achievements != null && achievements->IsLoaded() && achievements->IsComplete((int)e.Achievement))
            && ShopNpcs.Value[e.Shop].Any(Reachable));
    }

    /// <summary>TerritoryIntendedUse of house, apartment and company workshop interiors.</summary>
    private const uint HouseInterior = 14;

    /// <summary>The job's steps for a planned outfit: get the pieces (by place), buy the dyes, dye, and present when judging is open.</summary>
    private static List<JobManager.Step> Steps(Planned plan, int maxMarketPrice, bool judging, IReadOnlyDictionary<uint, int> dyesOwned)
    {
        var steps = new List<JobManager.Step>();
        void Add(string tool, JsonObject args, string note) => steps.Add(new JobManager.Step { Id = $"s{steps.Count + 1}", Tool = tool, Args = args, Note = note });
        var pieces = plan.Changes.ToList();
        bool From(FashionChoice c, PieceSource s) => c.Piece!.Source == s;

        var fromRetainers = pieces.Where(c => From(c, PieceSource.Retainer)).GroupBy(c => c.Piece!.Retainer).ToList();
        if (fromRetainers.Count > 0)
        {
            // transfer_retainer_items needs the retainer list open; closing it afterwards lets a paused retainer plugin go on.
            Add("navigate_to", new JsonObject { ["destination"] = "summoning_bell" }, "Goes to a summoning bell");
            Add("interact_with_object", new JsonObject { ["name"] = "Summoning Bell" }, "Opens the retainer list");
            foreach (var group in fromRetainers)
            {
                var transfers = new JsonArray(group.Select(c => (JsonNode)new JsonObject { ["item_id"] = c.Piece!.ItemId, ["from"] = group.Key, ["to"] = "player", ["stacks"] = 1 }).ToArray());
                Add("transfer_retainer_items", new JsonObject { ["transfers"] = transfers }, $"Takes {string.Join(", ", group.Select(c => c.Piece!.Name))} from {group.Key}");
            }
            Add("close_retainer", new JsonObject { ["close_list"] = true }, "Closes the retainer list");
        }
        var stored = pieces.Where(c => From(c, PieceSource.Armoire) || From(c, PieceSource.Dresser)).ToList();
        if (stored.Count > 0)
        {
            Add("navigate_to", new JsonObject { ["destination"] = "inn" }, "Goes to the inn room");
            foreach (var c in stored)
                Add("retrieve_glamour_item", new JsonObject { ["item"] = c.Piece!.ItemId, ["from"] = From(c, PieceSource.Armoire) ? "armoire" : "dresser" },
                    $"Takes {c.Piece.Name} out of the {(From(c, PieceSource.Armoire) ? "armoire" : "glamour dresser")}");
        }
        foreach (var c in pieces.Where(c => From(c, PieceSource.Vendor)))
            Add("buy_item", new JsonObject { ["item"] = c.Piece!.ItemId, ["quantity"] = 1 }, $"Buys {c.Piece.Name}");
        foreach (var c in pieces.Where(c => From(c, PieceSource.Duty)))
        {
            var duty = plan.Duties[c.Piece!.ItemId];
            Add("run_duty", new JsonObject
            {
                ["duty"] = duty.TerritoryType.ToString(), ["mode"] = duty.Mode, ["loops"] = 99,
                ["until"] = new JsonArray(new JsonObject { ["item"] = c.Piece.ItemId.ToString(), ["quantity"] = 1 }),
            }, $"Runs {duty.Name} ({duty.Mode}) until {c.Piece.Name} drops");
        }
        foreach (var c in pieces.Where(c => From(c, PieceSource.Market)))
            Add("buy_from_market_board", new JsonObject { ["item"] = c.Piece!.ItemId, ["quantity"] = 1, ["max_unit_price"] = maxMarketPrice },
                $"Buys {c.Piece.Name} on the market board (at most {maxMarketPrice:N0} gil)");

        var dyed = pieces.Where(c => c.Dye is DyeChange.Dye or DyeChange.Replace).ToList();
        var uses = dyed.Select(c => (IReadOnlyList<uint>)plan.Wanted[c.Slot].DyeItems).ToList();
        foreach (var (dyeId, count) in FashionDyes.ToBuy(uses, item => dyesOwned.GetValueOrDefault(item)))
        {
            var dyeItem = Items.Name(dyeId);
            if (SoldByVendor(dyeId)) Add("buy_item", new JsonObject { ["item"] = dyeId, ["quantity"] = count }, $"Buys {count} {dyeItem}");
            else Add("buy_from_market_board", new JsonObject { ["item"] = dyeId, ["quantity"] = count, ["max_unit_price"] = maxMarketPrice },
                     $"Buys {count} {dyeItem} on the market board");
        }
        foreach (var c in dyed)
        {
            var color = plan.Wanted[c.Slot].Name;
            var args = From(c, PieceSource.Worn)
                ? new JsonObject { ["slot"] = DyeSlotName(c.Slot), ["dye"] = color }
                : new JsonObject { ["item"] = c.Piece!.ItemId, ["dye"] = color };
            if (c.Channel != 1) args["channel"] = c.Channel;
            Add("dye_item", args, $"Dyes {c.Piece!.Name}{(From(c, PieceSource.Worn) ? " (worn)" : "")} {color}{(c.Channel != 1 ? $" (dye channel {c.Channel})" : "")}");
        }

        foreach (var c in pieces.Where(c => From(c, PieceSource.Vendor) || From(c, PieceSource.Market))) BoughtForReport[c.Piece!.ItemId] = 0;

        if (judging)
        {
            var put = pieces.Where(c => !From(c, PieceSource.Worn)).ToList();
            var args = new JsonObject { ["items"] = new JsonArray(put.Where(c => c.Slot != GearSlots.RingLeft).Select(c => (JsonNode)c.Piece!.ItemId).ToArray()) };
            if (put.FirstOrDefault(c => c.Slot == GearSlots.RingLeft) is { } left) args["left_ring"] = left.Piece!.ItemId;
            Add("present_fashion_report", args, "Puts on the outfit, presents it to the Masked Rose, and throws away cheap pieces it bought");
        }
        return steps;
    }

    /// <summary>What buying the dyes the bags don't cover costs, at vendor prices (a dye no vendor sells counts as 0). Framework thread.</summary>
    private static unsafe long DyesGil(FashionOutfit outfit, IReadOnlyDictionary<int, (uint Stain, string Name, List<uint> DyeItems)> wanted)
    {
        var im = InventoryManager.Instance();
        var uses = outfit.Choices.Where(c => c.Dye is DyeChange.Dye or DyeChange.Replace && wanted.ContainsKey(c.Slot))
            .Select(c => (IReadOnlyList<uint>)wanted[c.Slot].DyeItems).ToList();
        return FashionDyes.ToBuy(uses, item => im->GetInventoryItemCount(item))
            .Sum(d => SoldByVendor(d.Key) ? (long)(Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(d.Key)?.PriceMid ?? 0) * d.Value : 0);
    }

    /// <summary>Whether the player has the "Fashion Leader" title 100 points give; null while the game hasn't sent the titles. Framework thread.</summary>
    private static bool? HasLeaderTitle() =>
        Svc.Unlocks.IsTitleListLoaded && Svc.Data.GetExcelSheet<Title>().GetRowOrDefault(FashionTarget.LeaderTitle) is { } title
            ? Svc.Unlocks.IsTitleUnlocked(title) : null;

    /// <summary>The score asked for, from 'target' (or the older 'set').</summary>
    private static int TargetArg(ToolArgs args)
    {
        try { return FashionTarget.Of(args.Node("target") is { } t ? args.Int("target", 80, 0, 1000) : null, args.String("set")); }
        catch (ArgumentException ex) { throw new ToolException(ex.Message); }
    }

    /// <summary>A note for aiming at 100 when the title is already the player's: 80 gives the same MGP.</summary>
    private static string? TitleNote(int target, bool? hasTitle) =>
        target == 100 && hasTitle == true ? "You already have the Fashion Leader title: 80 points give the same MGP, often for less (target 80)." : null;

    /// <summary>How many of each dye item of the planned colors the bags hold. Framework thread.</summary>
    private static unsafe Dictionary<uint, int> DyesOwned(Planned plan)
    {
        var im = InventoryManager.Instance();
        return plan.Wanted.Values.SelectMany(w => w.DyeItems).Distinct().ToDictionary(i => i, i => im->GetInventoryItemCount(i));
    }

    private static string DyeSlotName(int slot) => slot switch
    {
        GearSlots.MainHand => "weapon", GearSlots.Head => "head", GearSlots.Body => "body", GearSlots.Hands => "hands",
        GearSlots.Legs => "legs", GearSlots.Feet => "feet", _ => GearSlots.Name(slot),
    };

    /// <summary>
    /// The in-game question before replacing dyes the player put on pieces, asked only when no outfit reaches the score without it:
    /// each piece with its old and new color.
    /// </summary>
    private static async Task<bool> ApproveDyeReplacements(Planned plan, Permissions.Caller? caller, CancellationToken ct)
    {
        var replaced = plan.Changes.Where(c => c.Dye == DyeChange.Replace).ToList();
        if (replaced.Count == 0) return true;
        var lines = await Game.Run(() => replaced.Select(c =>
            $"{c.Piece!.Name}: {Svc.Data.GetExcelSheet<Stain>().GetRowOrDefault(c.Piece.Stain ?? 0)?.Name.ExtractText() ?? "?"} → {plan.Wanted[c.Slot].Name}").ToList()).ConfigureAwait(false);
        lines.Add("Without this, nothing is done: no outfit reaches the score without changing these dyes.");
        var request = new Consent.Request($"Replace the dye on {replaced.Count} piece{(replaced.Count == 1 ? "" : "s")} for the Fashion Report?", lines)
        {
            Deadline = DateTime.UtcNow + ConsentTimeout,
            Source = caller?.Name ?? "your AI assistant",
        };
        return await Consent.Ask(request, ConsentTimeout, ct).ConfigureAwait(false) != Permissions.ApprovalDecision.Denied;
    }
}
