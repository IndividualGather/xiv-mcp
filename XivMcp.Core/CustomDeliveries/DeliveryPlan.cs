using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.CustomDeliveries;

/// <summary>The three requests a custom delivery client makes each week, in the game's slot order.</summary>
public enum DeliveryKind { Craft = 0, Gather = 1, Fish = 2 }

/// <summary>
/// A custom delivery client as far as planning goes: how many deliveries each kind of request still takes (deliveries left this
/// week, fewer when the satisfaction cap comes first) and which requests carry the bonus.
/// </summary>
public sealed record DeliveryNpc(string Name, bool Unlocked, int[] Remaining, IReadOnlySet<DeliveryKind> Bonus)
{
    public int RemainingFor(DeliveryKind kind) => Remaining[(int)kind];
}

/// <summary>One run: deliver <see cref="Count"/> items of one kind to one client.</summary>
public sealed record DeliveryRun(DeliveryNpc Npc, DeliveryKind Kind, int Count);

/// <summary>Which deliveries to make this week, within the weekly allowance shared by all clients.</summary>
public static class DeliveryPlan
{
    private static readonly DeliveryKind[] Order = [DeliveryKind.Craft, DeliveryKind.Gather, DeliveryKind.Fish];

    /// <param name="possible">The kinds that can be done (crafting needs Artisan, gathering Questionable, fishing AutoHook).</param>
    /// <param name="allowances">Deliveries left this week, for all clients together.</param>
    /// <param name="kind">Use this kind for everyone; otherwise a bonus request first, then crafting, gathering, fishing.</param>
    /// <param name="only">Only these clients (names, any case).</param>
    public static List<DeliveryRun> Choose(IEnumerable<DeliveryNpc> npcs, IReadOnlySet<DeliveryKind> possible, int allowances,
                                           DeliveryKind? kind = null, IReadOnlyCollection<string>? only = null)
    {
        var runs = new List<DeliveryRun>();
        foreach (var npc in npcs)
        {
            if (allowances <= 0) break;
            if (!npc.Unlocked) continue;
            if (only is { Count: > 0 } && !only.Any(n => n.Equals(npc.Name, StringComparison.OrdinalIgnoreCase))) continue;
            var choice = kind is { } k ? (possible.Contains(k) ? k : (DeliveryKind?)null) : Pick(npc, possible);
            if (choice is not { } chosen) continue;
            var count = Math.Min(npc.RemainingFor(chosen), allowances);
            if (count <= 0) continue;
            runs.Add(new DeliveryRun(npc, chosen, count));
            allowances -= count;
        }
        return runs;
    }

    private static DeliveryKind? Pick(DeliveryNpc npc, IReadOnlySet<DeliveryKind> possible)
    {
        var doable = Order.Where(k => possible.Contains(k) && npc.RemainingFor(k) > 0).ToList();
        return doable.Where(npc.Bonus.Contains).Cast<DeliveryKind?>().FirstOrDefault() ?? doable.Cast<DeliveryKind?>().FirstOrDefault();
    }
}
