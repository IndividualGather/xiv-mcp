using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.CustomDeliveries;

/// <summary>The three requests a custom delivery client makes each week, in the game's slot order.</summary>
public enum DeliveryKind { Craft = 0, Gather = 1, Fish = 2 }

/// <summary>
/// A custom delivery client as far as planning goes: how many deliveries each kind of request still takes (deliveries left this
/// week, fewer when the satisfaction cap comes first) and which requests carry the bonus. <see cref="Left"/> is how many deliveries
/// the client takes this week in all (default: as many as its largest request); <see cref="GainsRank"/> marks a client below its top
/// rank, whose deliveries still raise it; <see cref="Rewards"/> are the capped currencies each kind pays out (by kind; none: no cap);
/// <see cref="AchievementLeft"/> is how many deliveries the client's achievements still need (unknown: none).
/// </summary>
public sealed record DeliveryNpc(string Name, bool Unlocked, int[] Remaining, IReadOnlySet<DeliveryKind> Bonus)
{
    public int? Left { get; init; }
    public bool GainsRank { get; init; }
    public IReadOnlyList<CappedReward>[]? Rewards { get; init; }
    public int? AchievementLeft { get; init; }

    public int RemainingFor(DeliveryKind kind) => Remaining[(int)kind];
}

/// <summary>One run: deliver <see cref="Count"/> items of one kind to one client.</summary>
public sealed record DeliveryRun(DeliveryNpc Npc, DeliveryKind Kind, int Count);

/// <summary>
/// Which deliveries to make this week, within the weekly allowance shared by all clients. Clients that still rank up first (their
/// deliveries raise their rank), then those with a bonus on a request that can be delivered, then those whose achievements need the
/// most deliveries, the others in the game's order. Per client the bonus request first, then crafting (crafters' scrips buy the more
/// valuable materia), gathering for what crafting leaves (a request that takes fewer than the client does, or scrips at their cap);
/// fishing only when asked for.
/// </summary>
public static class DeliveryPlan
{
    private static readonly DeliveryKind[] Order = [DeliveryKind.Craft, DeliveryKind.Gather];

    /// <param name="possible">The kinds that can be done (crafting needs Artisan, gathering Questionable, fishing AutoHook).</param>
    /// <param name="allowances">Deliveries left this week, for all clients together.</param>
    /// <param name="kind">Use this kind for everyone (the only way to fish); otherwise gathering, else crafting.</param>
    /// <param name="only">Only these clients (names, any case).</param>
    public static List<DeliveryRun> Choose(IEnumerable<DeliveryNpc> npcs, IReadOnlySet<DeliveryKind> possible, int allowances,
                                           DeliveryKind? kind = null, IReadOnlyCollection<string>? only = null)
    {
        var runs = new List<DeliveryRun>();
        // Scrips a delivery earlier in the plan already takes, by currency: the cap is shared by every client.
        var earned = new Dictionary<string, long>();
        var kinds = kind is { } k ? (possible.Contains(k) ? [k] : []) : Order.Where(possible.Contains).ToArray();
        bool HasBonus(DeliveryNpc n) => kinds.Any(c => n.Bonus.Contains(c) && n.RemainingFor(c) > 0);
        // The sorts are stable: the others keep the game's order.
        var ordered = npcs.OrderByDescending(n => n.GainsRank).ThenByDescending(HasBonus).ThenByDescending(n => n.AchievementLeft ?? 0);
        foreach (var npc in ordered)
        {
            if (allowances <= 0) break;
            if (!npc.Unlocked) continue;
            if (only is { Count: > 0 } && !only.Any(n => n.Equals(npc.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var left = npc.Left ?? npc.Remaining.Max();
            foreach (var chosen in kinds.OrderByDescending(npc.Bonus.Contains))
            {
                var count = Math.Min(Math.Min(npc.RemainingFor(chosen), left), allowances);
                var rewards = (npc.Rewards?[(int)chosen] ?? []).Select(r => r with { Have = r.Have + earned.GetValueOrDefault(r.Currency) }).ToList();
                count = ScripCap.DeliveriesThatFit(rewards, count);
                if (count <= 0) continue;
                runs.Add(new DeliveryRun(npc, chosen, count));
                foreach (var r in rewards) earned[r.Currency] = earned.GetValueOrDefault(r.Currency) + (long)count * r.PerDelivery;
                left -= count;
                allowances -= count;
                if (left <= 0 || allowances <= 0) break;
            }
        }
        return runs;
    }
}
