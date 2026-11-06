using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using XivMcp.Mcp;

namespace XivMcp.Crafting;

/// <summary>A recipe and a number of crafts, as in a crafting list.</summary>
public sealed record RecipeCount(uint RecipeId, int Crafts);

/// <summary>
/// What a crafting-list job step remembers between runs: the list as it was first run (<paramref name="Original"/>), what the step
/// last wrote into the crafting plugin's list (<paramref name="Written"/>; the original until it shrinks it), and the crafts made
/// so far per recipe id.
/// </summary>
public sealed record CraftListMemory(int ListId, List<RecipeCount> Original, List<RecipeCount> Written, Dictionary<uint, int> Done)
{
    public JsonObject ToJson() => new()
    {
        ["listId"] = ListId,
        ["original"] = Counts(Original),
        ["written"] = Counts(Written),
        ["done"] = new JsonObject(Done.Select(d => KeyValuePair.Create(d.Key.ToString(), (JsonNode?)d.Value))),
    };

    public static CraftListMemory? FromJson(JsonObject? json)
    {
        if (json is null || JsonNumber.Int(json["listId"]) is not { } listId || json["original"] is not JsonArray original) return null;
        var done = new Dictionary<uint, int>();
        foreach (var (key, value) in json["done"] as JsonObject ?? [])
            if (uint.TryParse(key, out var id) && JsonNumber.Int(value) is { } n) done[id] = n;
        var orig = Read(original);
        return new CraftListMemory(listId, orig, json["written"] is JsonArray w ? Read(w) : orig, done);
    }

    private static JsonArray Counts(List<RecipeCount> list) =>
        new(list.Select(r => (JsonNode?)new JsonObject { ["recipe"] = r.RecipeId, ["crafts"] = r.Crafts }).ToArray());

    private static List<RecipeCount> Read(JsonArray array) =>
        array.OfType<JsonObject>().Select(o => new RecipeCount((uint)Math.Max(0, JsonNumber.Int(o["recipe"]) ?? 0), JsonNumber.Int(o["crafts"]) ?? 0))
             .Where(r => r.RecipeId != 0).ToList();
}

/// <param name="Original">The list the report and progress refer to.</param>
/// <param name="Done">Crafts made by earlier runs, per recipe id.</param>
/// <param name="Shrink">The list to write before starting (only what is left), or null to run the list as it is.</param>
/// <param name="AllDone">Earlier runs made everything: nothing to start.</param>
public sealed record CraftListResumePlan(List<RecipeCount> Original, Dictionary<uint, int> Done, List<RecipeCount>? Shrink, bool AllDone);

/// <summary>
/// Running a crafting list again after it was interrupted: the crafting plugin would start the list from the top and craft
/// everything again, so the list is shrunk to what earlier runs haven't made yet.
/// </summary>
public static class CraftListResume
{
    public static CraftListResumePlan Plan(CraftListMemory? memory, int listId, IReadOnlyList<RecipeCount> current)
    {
        // No memory, another list, or the list was edited since: start over with the list as it is.
        if (memory is null || memory.ListId != listId || !(Same(current, memory.Original) || Same(current, memory.Written)))
            return new CraftListResumePlan(current.ToList(), [], null, false);

        var left = Remaining(memory.Original, memory.Done);
        return new CraftListResumePlan(memory.Original, new Dictionary<uint, int>(memory.Done), Same(left, current) ? null : left, left.Count == 0);
    }

    /// <summary>The list minus the crafts already made, in list order, without finished entries.</summary>
    public static List<RecipeCount> Remaining(IReadOnlyList<RecipeCount> original, IReadOnlyDictionary<uint, int> done)
    {
        var used = new Dictionary<uint, int>(done);
        var result = new List<RecipeCount>();
        foreach (var r in original)
        {
            var credit = Math.Min(r.Crafts, used.GetValueOrDefault(r.RecipeId));
            used[r.RecipeId] = used.GetValueOrDefault(r.RecipeId) - credit;
            if (r.Crafts - credit > 0) result.Add(r with { Crafts = r.Crafts - credit });
        }
        return result;
    }

    private static bool Same(IReadOnlyList<RecipeCount> a, IReadOnlyList<RecipeCount> b) => a.SequenceEqual(b);
}
