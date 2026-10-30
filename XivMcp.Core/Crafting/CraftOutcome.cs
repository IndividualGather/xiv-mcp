using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Crafting;

/// <summary>
/// What a prepared crafting macro achieves with the stats it will run with, simulated step by step under normal conditions:
/// whether it finishes the craft, the quality it ends with, and the recipe's collectability breakpoints in quality
/// (collectability × 10, lowest first; 0 = unused).
/// </summary>
public sealed record CraftOutcome(bool Finishes, int Quality, IReadOnlyList<int> TierQuality)
{
    private IReadOnlyList<int> Tiers => TierQuality.Where(q => q > 0).ToList();

    public int Collectability => Quality / 10;

    /// <summary>The highest collectability tier reached (0 = none, or the craft doesn't finish).</summary>
    public int Tier => Finishes ? Tiers.Count(q => Quality >= q) : 0;

    public string Describe() => Tiers.Count == 0 ? $"quality {Quality}" : $"tier {Tier} of {Tiers.Count} (collectability {Collectability})";

    /// <summary>Why crafting with this macro would waste the materials, or null when it is fine.</summary>
    public string? Problem(bool collectable)
    {
        if (!Finishes)
            return "With these stats the macro doesn't finish the craft (progress falls short): better gear, food or a potion is needed.";
        if (collectable && Tiers.Count > 0 && Tier == 0)
            return $"With these stats the macro reaches collectability {Collectability}, below the lowest tier ({Tiers[0] / 10}): " +
                   "the collectables would not be accepted. Better gear, food or a potion is needed.";
        return null;
    }
}
