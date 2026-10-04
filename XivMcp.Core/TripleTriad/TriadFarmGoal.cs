using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.TripleTriad;

/// <summary>
/// What a card farm against one Triple Triad opponent is after: one of their cards, or all of them. A card counts as obtained once it
/// is in the collection or waiting in the bags (dropped cards arrive as items that still have to be registered).
/// </summary>
public sealed class TriadFarmGoal
{
    /// <summary>The cards that must be obtained.</summary>
    public IReadOnlyList<int> Cards { get; }

    private TriadFarmGoal(IReadOnlyList<int> cards) => Cards = cards;

    public static TriadFarmGoal ForCard(int card, IReadOnlyCollection<int> rewardCards) =>
        rewardCards.Contains(card) ? new([card]) : throw new ArgumentException("This opponent doesn't give that card.");

    public static TriadFarmGoal AllCards(IReadOnlyCollection<int> rewardCards) =>
        rewardCards.Count > 0 ? new(rewardCards.Distinct().ToList()) : throw new ArgumentException("This opponent gives no cards.");

    public IReadOnlyList<int> Missing(IReadOnlyCollection<int> owned, IReadOnlyCollection<int> inBags) =>
        Cards.Where(c => !owned.Contains(c) && !inBags.Contains(c)).ToList();

    public bool IsMet(IReadOnlyCollection<int> owned, IReadOnlyCollection<int> inBags) => Missing(owned, inBags).Count == 0;
}
