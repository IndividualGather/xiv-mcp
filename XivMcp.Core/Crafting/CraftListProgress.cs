using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Crafting;

/// <summary>One entry of a crafting list: the recipe, the item it makes, how many crafts, items per craft, and equipment ingredients (by name).</summary>
public sealed record ListRecipe(uint RecipeId, string Item, uint ItemId, int Crafts, int Yield, IReadOnlyList<string> GearIngredients);

/// <summary>
/// Follows a crafting list from bag counts: every increase of a recipe's product counts as items made by that recipe (in list
/// order when two entries make the same item), so intermediates count even after later crafts use them up. Any count change
/// (products or ingredients) is activity; no activity for a while means the crafting plugin is stuck.
/// </summary>
public sealed class CraftListProgress
{
    private readonly IReadOnlyList<ListRecipe> recipes;
    private readonly Dictionary<uint, int> produced = []; // recipe id → items made
    private Dictionary<uint, int> last;
    private DateTime lastActivity;

    /// <param name="startCounts">Bag counts now, of every product and ingredient worth watching.</param>
    /// <param name="doneCrafts">Crafts already made per recipe id by an earlier run of the same list.</param>
    public CraftListProgress(IReadOnlyList<ListRecipe> recipes, IReadOnlyDictionary<uint, int> startCounts, DateTime now, IReadOnlyDictionary<uint, int>? doneCrafts = null)
    {
        this.recipes = recipes;
        last = new Dictionary<uint, int>(startCounts);
        lastActivity = now;
        foreach (var r in recipes)
            produced[r.RecipeId] = Math.Min(r.Crafts, doneCrafts?.GetValueOrDefault(r.RecipeId) ?? 0) * Math.Max(1, r.Yield);
        Initial = Done();
    }

    private IReadOnlyDictionary<uint, int> Initial { get; }

    public int CraftsDone(uint recipeId) =>
        recipes.FirstOrDefault(r => r.RecipeId == recipeId) is { } r ? Math.Min(r.Crafts, produced.GetValueOrDefault(recipeId) / Math.Max(1, r.Yield)) : 0;

    /// <summary>Crafts done per recipe id.</summary>
    public Dictionary<uint, int> Done() => recipes.Select(r => r.RecipeId).Distinct().ToDictionary(id => id, CraftsDone);

    /// <summary>Items made per recipe id (for the report).</summary>
    public int ItemsMade(uint recipeId) => produced.GetValueOrDefault(recipeId);

    /// <summary>The entry being worked on: the first one in list order that isn't done.</summary>
    public ListRecipe? Current => recipes.FirstOrDefault(r => CraftsDone(r.RecipeId) < r.Crafts);

    public bool Finished => Current is null;

    /// <summary>True when this run made at least one craft (earlier runs don't count).</summary>
    public bool MadeProgressThisRun => Done().Any(d => d.Value > Initial.GetValueOrDefault(d.Key));

    /// <summary>Takes new bag counts; returns true when any watched count changed.</summary>
    public bool Observe(IReadOnlyDictionary<uint, int> counts, DateTime now)
    {
        var changed = false;
        foreach (var (item, count) in counts)
        {
            var before = last.GetValueOrDefault(item);
            if (count == before) continue;
            changed = true;
            if (count > before) Credit(item, count - before);
        }
        last = new Dictionary<uint, int>(counts);
        if (changed) lastActivity = now;
        return changed;
    }

    /// <summary>Something else shows the crafting plugin is working (e.g. its solver is busy).</summary>
    public void Touch(DateTime now) => lastActivity = now;

    /// <summary>No activity for <paramref name="threshold"/>; a zero threshold turns the check off.</summary>
    public bool Stalled(DateTime now, TimeSpan threshold) => threshold > TimeSpan.Zero && now - lastActivity >= threshold;

    public TimeSpan Idle(DateTime now) => now - lastActivity;

    private void Credit(uint item, int amount)
    {
        var makers = recipes.Where(r => r.ItemId == item).ToList();
        if (makers.Count == 0) return;
        foreach (var r in makers)
        {
            if (amount <= 0) return;
            var room = r.Crafts * Math.Max(1, r.Yield) - produced[r.RecipeId];
            var take = Math.Min(room, amount);
            if (take <= 0) continue;
            produced[r.RecipeId] += take;
            amount -= take;
        }
        if (amount > 0) produced[makers[^1].RecipeId] += amount; // more than planned (e.g. bought meanwhile): the last entry keeps it
    }

    /// <summary>Why a stalled list is stuck, naming the entry it stopped at.</summary>
    public string StallMessage(string listName, TimeSpan idle)
    {
        var minutes = Math.Max(1, (int)Math.Round(idle.TotalMinutes));
        var current = Current;
        if (current is null)
            return $"Artisan made no progress on '{listName}' for {minutes} minutes although every craft seems done; it was stopped.";
        var head = $"Artisan made no progress on '{listName}' for {minutes} minutes: it is stuck on {current.Item} (recipe {current.RecipeId}, " +
                   $"{CraftsDone(current.RecipeId)} of {current.Crafts} crafts done). Artisan was stopped. ";
        return head + (GearIngredients.Note(current.GearIngredients)
                       ?? "Likely causes: an ingredient it can't select in the crafting log, missing materials (or the wrong HQ/NQ ones), or a window in the way.");
    }
}

/// <summary>Recipes that use equipment (an item with an equip slot) as an ingredient: Artisan can't select those in the crafting log.</summary>
public static class GearIngredients
{
    public static bool IsGear(uint equipSlotCategory) => equipSlotCategory != 0;

    public static string? Note(IReadOnlyCollection<string> gearIngredients) => gearIngredients.Count == 0 ? null :
        $"This recipe uses equipment as an ingredient ({string.Join(", ", gearIngredients)}). Artisan can't select equipment ingredients in the " +
        "crafting log (they stay \"Unselected\"), so it never starts the synthesis and its list stalls here. Craft it by hand: open the recipe, " +
        "select the ingredient and start the synthesis yourself (or leave it out of the Artisan list).";
}
