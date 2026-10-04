using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Transfers;

/// <summary>One stack in a trade: an item, whether it is high quality, and how many.</summary>
public sealed record TradeItem(uint ItemId, bool Hq, int Quantity);

/// <summary>One side of a trade: up to five stacks and some gil.</summary>
public sealed record TradeOffer(IReadOnlyList<TradeItem> Items, long Gil)
{
    /// <summary>The trade window's limits: five item slots and 1,000,000 gil per side.</summary>
    public const int MaxItems = 5;
    public const long MaxGil = 1_000_000;

    public static TradeOffer Empty { get; } = new([], 0);

    public bool IsEmpty => Items.Count == 0 && Gil == 0;

    /// <summary>What a side cannot hold, or null.</summary>
    public static string? Problem(TradeOffer offer) =>
        offer.Items.Count > MaxItems ? $"A trade holds at most {MaxItems} items (stacks) per side, not {offer.Items.Count}."
        : offer.Gil > MaxGil ? $"A trade holds at most {MaxGil:N0} gil per side."
        : offer.Gil < 0 ? "Gil cannot be negative."
        : null;

    /// <summary>Totals per item and quality, so the same items in other stacks compare equal.</summary>
    public IReadOnlyDictionary<(uint ItemId, bool Hq), int> Totals() =>
        Items.GroupBy(i => (i.ItemId, i.Hq)).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));

    /// <summary>"3 Potion, 1 Ether (HQ) and 1,000 gil".</summary>
    public string Describe(Func<uint, string> name)
    {
        var parts = Totals().Select(kv => $"{kv.Value} {Label(kv.Key, name)}").ToList();
        if (Gil > 0) parts.Add($"{Gil:N0} gil");
        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
        };
    }

    internal static string Label((uint ItemId, bool Hq) key, Func<uint, string> name) => key.Hq ? $"{name(key.ItemId)} (HQ)" : name(key.ItemId);
}

/// <summary>
/// The check before a trade is completed: the window has to hold exactly what the player approved to give, and at least what they
/// expect to receive. Anything else stops the trade.
/// </summary>
public static class TradeCheck
{
    public static IReadOnlyList<string> Problems(TradeOffer give, TradeOffer actualGive, TradeOffer? expect, TradeOffer actualReceive, Func<uint, string> name)
    {
        var problems = new List<string>();
        var want = give.Totals();
        var have = actualGive.Totals();
        foreach (var key in want.Keys.Union(have.Keys))
        {
            var w = want.GetValueOrDefault(key);
            var h = have.GetValueOrDefault(key);
            if (w != h) problems.Add($"You would give {h} {TradeOffer.Label(key, name)} instead of {w}.");
        }
        if (give.Gil != actualGive.Gil) problems.Add($"You would give {actualGive.Gil:N0} gil instead of {give.Gil:N0}.");

        if (expect is null) return problems;
        var got = actualReceive.Totals();
        foreach (var (key, count) in expect.Totals())
            if (got.GetValueOrDefault(key) < count)
                problems.Add($"You would receive {got.GetValueOrDefault(key)} of {count} {TradeOffer.Label(key, name)}.");
        if (actualReceive.Gil < expect.Gil) problems.Add($"You would receive {actualReceive.Gil:N0} gil instead of {expect.Gil:N0}.");
        return problems;
    }
}
