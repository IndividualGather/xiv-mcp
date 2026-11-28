using System;

namespace XivMcp.Quests;

/// <summary>
/// Questionable's "Craft" steps first ask Artisan to run its list for the whole quest and, when that starts, craft every turn-in
/// (intermediates included) without checking what the bags already hold. That re-crafts items already made and uses up materials
/// meant for the rest. A step whose items are already owned is done.
/// </summary>
public static class CraftStep
{
    public const string Craft = "Craft";

    /// <summary>Questionable's shop step; it can hang in the open shop when the bags already hold the item.</summary>
    public const string PurchaseItem = "PurchaseItem";

    /// <summary>Whether XIV MCP checks this kind of step against the bags.</summary>
    public static bool Covers(string? interactionType) =>
        string.Equals(interactionType, Craft, StringComparison.Ordinal) || string.Equals(interactionType, PurchaseItem, StringComparison.Ordinal);

    /// <param name="quality">The step's ItemQuality: "NQ", "HQ" or "Any" (null = Any).</param>
    public static bool IsDone(string? interactionType, int? itemCount, int nqOwned, int hqOwned, string? quality)
    {
        if (!Covers(interactionType) || itemCount is not > 0) return false;
        var owned = quality switch
        {
            "NQ" => nqOwned,
            "HQ" => hqOwned,
            _ => nqOwned + hqOwned,
        };
        return owned >= itemCount;
    }
}

/// <summary>What XIV MCP does at a craft or purchase step, once its items are in the bags (<see cref="OwnedAtStart"/>).</summary>
public enum OwnedItemsAction
{
    /// <summary>The step still needs its items.</summary>
    None,

    /// <summary>
    /// The items arrived during the step, made or bought by Questionable: the step is its own, and it finishes it. Only the Artisan
    /// list it started for a craft is stopped, as the list would go on to craft the quest's other turn-ins.
    /// </summary>
    StopList,

    /// <summary>The items were owned before the step: the Artisan list is stopped and the step skipped.</summary>
    Skip,
}

/// <summary>
/// Whether a craft or purchase step's items were owned before Questionable worked on it: only then is the step skipped. The first look
/// at a step decides. Items Questionable crafted or bought for the step itself show up in the bags as the step ends; Questionable
/// finishes such a step on its own once nothing is crafting (Craft.cs, DoCraft), so a skip then only stops it. That happened on Chief
/// Concerns and two Dwarves dailies on 2026-11-25, each costing a restart. One per run of quests, like the other watches.
/// </summary>
public sealed class OwnedAtStart
{
    private string? step;
    private bool doneAtStart;

    /// <param name="stepKey">The step's quest, sequence and step index.</param>
    /// <param name="doneNow">The bags hold what the step asks for now (<see cref="CraftStep.IsDone"/>).</param>
    public OwnedItemsAction Next(string stepKey, bool doneNow)
    {
        if (stepKey != step)
        {
            step = stepKey;
            doneAtStart = doneNow;
        }
        return !doneNow ? OwnedItemsAction.None : doneAtStart ? OwnedItemsAction.Skip : OwnedItemsAction.StopList;
    }
}
