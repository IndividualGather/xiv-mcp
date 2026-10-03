using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Plans a crafting project: resolves the recipe tree, decides what to craft and what to obtain, nets everything against what
/// the character already has (bags, and optionally the cached retainer inventories), orders the crafts so every intermediate is
/// made before it is needed, and simulates bag space through the whole project to split it into batches when it would not fit.
/// Pure game-data computation; call on the framework thread.
/// </summary>
internal static class CraftPlanner
{
    public sealed record Target(uint ItemId, int Quantity);

    public sealed record Options(bool CraftIntermediates = true, bool UseRetainers = true, bool CountExistingTargets = false, int? FreeSlotsOverride = null);

    public sealed record Step(uint RecipeId, uint ItemId, string Item, string Job, int JobLevelNeeded, int PlayerJobLevel, int Crafts, int Yield,
                              int Produces, int Depth, bool IsTarget, List<(uint ItemId, int Amount)> Ingredients, string? Problem);

    public sealed record Material(uint ItemId, string Item, int Needed, int InBags, int OnRetainers, List<(string Retainer, int Quantity)> RetainerStock,
                                  int Missing, bool Crystal, string? Note);

    public sealed record Batch(int Index, List<Target> Targets, int PeakSlotsNeeded);

    public sealed record Plan(List<Step> Steps, List<Material> Materials, int FreeSlots, int PeakSlotsNeeded, List<Batch> Batches,
                              List<string> Warnings, DateTime? RetainerDataFrom);

    public static Plan Build(IReadOnlyList<Target> targets, Options options, RetainerTracker? retainers)
    {
        var warnings = new List<string>();
        var recipes = Svc.Data.GetExcelSheet<Recipe>();
        var recipesByItem = recipes.Where(r => r.ItemResult.RowId != 0).GroupBy(r => r.ItemResult.RowId).ToDictionary(g => g.Key, g => g.ToList());
        var targetIds = targets.Select(t => t.ItemId).ToHashSet();

        // 1. Pick a recipe per craftable item and find each item's depth (longest path from a target), so crafts can be ordered.
        var chosen = new Dictionary<uint, Recipe>();
        var depth = new Dictionary<uint, int>();
        void Visit(uint item, int d, HashSet<uint> path)
        {
            if (depth.TryGetValue(item, out var known) && known >= d) return;
            if (!path.Add(item)) return; // cycles don't exist in FFXIV recipes, but be safe
            depth[item] = d;
            var craft = targetIds.Contains(item) || options.CraftIntermediates;
            if (craft && ChooseRecipe(item, recipesByItem) is { } recipe)
            {
                chosen[item] = recipe;
                foreach (var (ing, _) in Ingredients(recipe)) Visit(ing, d + 1, path);
            }
            path.Remove(item);
        }
        foreach (var t in targets)
        {
            if (!recipesByItem.ContainsKey(t.ItemId)) throw new ToolException($"{Items.Name(t.ItemId)} has no crafting recipe.");
            Visit(t.ItemId, 0, []);
        }

        // 2. Stock: bags (live) and retainers (cached).
        var retainerStock = new Dictionary<uint, List<(string, int)>>();
        DateTime? retainerTime = null;
        if (options.UseRetainers && retainers?.Get(Svc.PlayerState.ContentId) is { } character)
        {
            foreach (var r in character.Retainers)
            {
                if (r.InventoryCapturedUtc is { } at) retainerTime = retainerTime is null || at < retainerTime ? at : retainerTime;
                foreach (var it in r.Items.Concat(r.Crystals).Where(i => depth.ContainsKey(i.ItemId)).GroupBy(i => i.ItemId))
                {
                    if (!retainerStock.TryGetValue(it.Key, out var list)) retainerStock[it.Key] = list = [];
                    list.Add((r.Name, it.Sum(i => i.Quantity)));
                }
            }
        }
        var bagStock = depth.Keys.ToDictionary(id => id, Items.CountInBags);

        // 3. Walk items from the targets down: each item's total demand is known once all items above it are processed.
        var demand = new Dictionary<uint, int>();
        foreach (var t in targets) demand[t.ItemId] = demand.GetValueOrDefault(t.ItemId) + t.Quantity;
        var fromBags = new Dictionary<uint, int>();
        var fromRetainers = new Dictionary<uint, int>();
        var crafts = new Dictionary<uint, int>();
        foreach (var item in depth.OrderBy(kv => kv.Value).Select(kv => kv.Key))
        {
            var need = demand.GetValueOrDefault(item);
            if (need <= 0) continue;
            var useStock = !targetIds.Contains(item) || options.CountExistingTargets;
            var bag = useStock ? Math.Min(need, bagStock[item]) : 0;
            var rest = need - bag;
            // Retainer stock counts for materials and intermediates alike (cheaper than crafting them again).
            var ret = useStock ? Math.Min(rest, retainerStock.GetValueOrDefault(item)?.Sum(x => x.Item2) ?? 0) : 0;
            rest -= ret;
            fromBags[item] = bag;
            fromRetainers[item] = ret;
            if (rest > 0 && chosen.TryGetValue(item, out var recipe))
            {
                var y = Math.Max(1, (int)recipe.AmountResult);
                var n = (int)Math.Ceiling(rest / (double)y);
                crafts[item] = n;
                foreach (var (ing, amount) in Ingredients(recipe)) demand[ing] = demand.GetValueOrDefault(ing) + amount * n;
            }
        }

        // 4. Steps in craft order (deepest first) and the materials that must come from outside.
        var jobs = Svc.Data.GetExcelSheet<ClassJob>();
        var steps = crafts.Where(c => c.Value > 0).OrderByDescending(c => depth[c.Key]).ThenBy(c => chosen[c.Key].CraftType.RowId).Select(c =>
        {
            var r = chosen[c.Key];
            var job = jobs.GetRow(r.CraftType.RowId + 8);
            var needed = r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0;
            var have = Svc.PlayerState.GetClassJobLevel(job);
            string? problem = null;
            if (have < needed) problem = $"{job.Abbreviation.ExtractText()} is level {have}, the recipe needs {needed}.";
            else if (r.SecretRecipeBook.RowId != 0 && !BookUnlocked(r.SecretRecipeBook.RowId))
                problem = $"Needs the master recipe book {Excel.Name(r.SecretRecipeBook.Value.Item)}.";
            else if (r.IsSpecializationRequired && !IsSpecialist(job.RowId))
                problem = "Needs a crafter's soul (specialist) for this job.";
            var y = Math.Max(1, (int)r.AmountResult);
            return new Step(r.RowId, c.Key, Items.Name(c.Key), job.Abbreviation.ExtractText(), needed, have, c.Value, y, c.Value * y, depth[c.Key],
                            targetIds.Contains(c.Key), Ingredients(r).Select(i => (i.Item, i.Amount * c.Value)).ToList(), problem);
        }).ToList();
        foreach (var s in steps.Where(s => s.Problem is not null)) warnings.Add($"{s.Item}: {s.Problem}");

        var materials = demand.Where(d => !crafts.ContainsKey(d.Key) && !targetIds.Contains(d.Key)).Select(d =>
        {
            var bag = fromBags.GetValueOrDefault(d.Key);
            var ret = fromRetainers.GetValueOrDefault(d.Key);
            var stock = retainerStock.GetValueOrDefault(d.Key) ?? [];
            var note = chosen.ContainsKey(d.Key) ? null : recipesByItem.ContainsKey(d.Key) && !options.CraftIntermediates ? "craftable, but craft_intermediates is off" : null;
            return new Material(d.Key, Items.Name(d.Key), d.Value, bag, ret, AllocateRetainers(stock, ret), Math.Max(0, d.Value - bag - ret), Items.IsCrystal(d.Key), note);
        }).OrderBy(m => m.Crystal).ThenByDescending(m => m.Missing).ToList();
        // Intermediates partly covered by stock are not "materials", but report what stock they use.
        foreach (var (item, ret) in fromRetainers.Where(f => f.Value > 0 && crafts.ContainsKey(f.Key)))
            warnings.Add($"{Items.Name(item)}: {ret} taken from retainers, the rest is crafted.");

        // 5. Bag space: simulate the project and split into batches if it doesn't fit.
        var free = options.FreeSlotsOverride ?? Items.FreeBagSlots();
        var peak = PeakSlots(steps, materials, targets, fromBags, fromRetainers, bagStock);
        var batches = new List<Batch>();
        if (peak <= free) batches.Add(new Batch(1, targets.ToList(), peak));
        else
        {
            var split = 2;
            for (; split <= 50; split++)
            {
                var part = targets.Select(t => new Target(t.ItemId, (int)Math.Ceiling(t.Quantity / (double)split))).ToList();
                var sub = Build(part, options with { FreeSlotsOverride = int.MaxValue }, retainers);
                if (sub.PeakSlotsNeeded <= free) break;
            }
            if (split > 50) warnings.Add($"Even a small part of this project needs more than the {free} free bag slots; free up space first.");
            else
            {
                var remaining = targets.ToDictionary(t => t.ItemId, t => t.Quantity);
                for (var i = 1; i <= split; i++)
                {
                    var part = remaining.Where(r => r.Value > 0).Select(r => new Target(r.Key, Math.Min(r.Value, (int)Math.Ceiling(targets.First(t => t.ItemId == r.Key).Quantity / (double)split)))).ToList();
                    foreach (var p in part) remaining[p.ItemId] -= p.Quantity;
                    if (part.Count == 0) break;
                    var sub = Build(part, options with { FreeSlotsOverride = int.MaxValue }, retainers);
                    batches.Add(new Batch(i, part, sub.PeakSlotsNeeded));
                }
                warnings.Add($"The whole project needs about {peak} more bag slots at its peak but only {free} are free: split into {batches.Count} batches " +
                             "(or move things to retainers / the saddlebag first).");
            }
        }

        if (options.UseRetainers && retainerTime is null && retainers is not null)
            warnings.Add("No retainer inventories cached yet; retainer stock was not counted (refresh_retainer_inventories at a bell).");

        return new Plan(steps, materials, free, peak, batches, warnings, retainerTime);
    }

    /// <summary>
    /// Extra bag slots the project needs at its fullest moment, compared to now: materials arrive first (withdrawn/bought/gathered),
    /// then each craft step consumes its ingredients and adds its product.
    /// </summary>
    private static int PeakSlots(List<Step> steps, List<Material> materials, IReadOnlyList<Target> targets,
                                 Dictionary<uint, int> fromBags, Dictionary<uint, int> fromRetainers, Dictionary<uint, int> bagStock)
    {
        var qty = new Dictionary<uint, int>(bagStock);
        int Slots() => qty.Sum(kv => Items.SlotsFor(kv.Key, kv.Value));
        var baseline = Slots();
        foreach (var m in materials) qty[m.ItemId] = qty.GetValueOrDefault(m.ItemId) + m.OnRetainers + m.Missing;
        foreach (var (item, ret) in fromRetainers.Where(f => f.Value > 0 && materials.All(m => m.ItemId != f.Key)))
            qty[item] = qty.GetValueOrDefault(item) + ret;
        var peak = Slots();
        foreach (var s in steps)
        {
            foreach (var (ing, amount) in s.Ingredients) qty[ing] = Math.Max(0, qty.GetValueOrDefault(ing) - amount);
            qty[s.ItemId] = qty.GetValueOrDefault(s.ItemId) + s.Produces;
            peak = Math.Max(peak, Slots());
        }
        return Math.Max(0, peak - baseline);
    }

    private static List<(string, int)> AllocateRetainers(List<(string Retainer, int Quantity)> stock, int amount)
    {
        var result = new List<(string, int)>();
        foreach (var (retainer, quantity) in stock.OrderByDescending(s => s.Quantity))
        {
            if (amount <= 0) break;
            var take = Math.Min(amount, quantity);
            result.Add((retainer, take));
            amount -= take;
        }
        return result;
    }

    /// <summary>The recipe to use for an item: one the character can craft (highest job level first), non-specialist and unlocked preferred.</summary>
    public static Recipe? ChooseRecipe(uint itemId, Dictionary<uint, List<Recipe>> recipesByItem)
    {
        if (!recipesByItem.TryGetValue(itemId, out var candidates)) return null;
        var jobs = Svc.Data.GetExcelSheet<ClassJob>();
        return candidates
            .Select(r => (Recipe: r, Have: Svc.PlayerState.GetClassJobLevel(jobs.GetRow(r.CraftType.RowId + 8)), Need: r.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0))
            .OrderBy(x => x.Have >= x.Need ? 0 : 1)
            .ThenBy(x => x.Recipe.SecretRecipeBook.RowId == 0 || BookUnlocked(x.Recipe.SecretRecipeBook.RowId) ? 0 : 1)
            .ThenBy(x => x.Recipe.IsSpecializationRequired ? 1 : 0)
            .ThenBy(x => x.Recipe.IsExpert ? 1 : 0)
            .ThenByDescending(x => x.Have - x.Need)
            .Select(x => (Recipe?)x.Recipe)
            .FirstOrDefault();
    }

    public static IEnumerable<(uint Item, int Amount)> Ingredients(Recipe r)
    {
        for (var i = 0; i < r.Ingredient.Count; i++)
        {
            var id = r.Ingredient[i].RowId;
            var amount = r.AmountIngredient[i];
            if (id != 0 && amount > 0) yield return (id, amount);
        }
    }

    private static unsafe bool BookUnlocked(uint book) => PlayerState.Instance()->IsSecretRecipeBookUnlocked(book);

    private static unsafe bool IsSpecialist(uint classJob)
    {
        var im = FFXIVClientStructs.FFXIV.Client.Game.InventoryManager.Instance();
        if (im == null) return false;
        // The soul crystal sits in the equipped soul slot only while that job is active; otherwise assume yes and let Artisan check.
        return Svc.PlayerState.ClassJob.RowId != classJob || im->GetInventorySlot(FFXIVClientStructs.FFXIV.Client.Game.InventoryType.EquippedItems, 13)->ItemId != 0;
    }
}
