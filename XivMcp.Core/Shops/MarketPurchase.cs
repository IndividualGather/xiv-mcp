using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Shops;

/// <summary>A market board listing: it can only be bought whole (all <see cref="Quantity"/> at once, plus <see cref="Tax"/>).</summary>
public sealed record MarketOffer(ulong Id, uint UnitPrice, uint Quantity, uint Tax)
{
    public long Cost => (long)UnitPrice * Quantity + Tax;
}

/// <summary>Which listings a market board purchase buys, and what one that fell short says.</summary>
public static class MarketPurchase
{
    /// <summary>
    /// The listings that get at least <paramref name="wanted"/> items for the least gil in total. Listings are bought whole,
    /// so the cheapest unit price isn't always cheapest: for 2 items a pair at 2,000 beats a stack of 96 at 1,897.
    /// When fewer than <paramref name="wanted"/> are listed, all of them.
    /// </summary>
    public static IReadOnlyList<MarketOffer> Cheapest(IReadOnlyList<MarketOffer> offers, int wanted)
    {
        var listed = offers.Where(o => o.Quantity > 0).ToList();
        if (listed.Count == 0 || wanted <= 0) return [];
        if (listed.Sum(o => (long)o.Quantity) <= wanted) return listed;

        // 0/1 knapsack over the covered quantity, capped at 'wanted': best[i, q] = least gil to have at least q from the first i listings.
        var n = listed.Count;
        var best = new long[n + 1, wanted + 1];
        for (var q = 1; q <= wanted; q++) best[0, q] = long.MaxValue;
        for (var i = 1; i <= n; i++)
        {
            var offer = listed[i - 1];
            for (var q = 0; q <= wanted; q++) best[i, q] = best[i - 1, q];
            for (var q = 0; q <= wanted; q++)
            {
                if (best[i - 1, q] == long.MaxValue) continue;
                var to = (int)Math.Min(wanted, q + (long)offer.Quantity);
                var cost = best[i - 1, q] + offer.Cost;
                if (cost < best[i, to]) best[i, to] = cost;
            }
        }

        // Walk back from 'wanted': a listing was taken where the cost differs from leaving it out.
        var chosen = new List<MarketOffer>();
        var left = wanted;
        for (var i = n; i >= 1 && left > 0; i--)
        {
            if (best[i, left] == best[i - 1, left]) continue;
            var offer = listed[i - 1];
            chosen.Add(offer);
            // The state before this listing: the q it was added to (q + quantity capped at wanted reaches 'left').
            var from = -1;
            for (var q = 0; q <= wanted; q++)
            {
                if (best[i - 1, q] == long.MaxValue) continue;
                if ((int)Math.Min(wanted, q + (long)offer.Quantity) == left && best[i - 1, q] + offer.Cost == best[i, left]) { from = q; break; }
            }
            left = Math.Max(0, from);
        }
        chosen.Reverse();
        return chosen;
    }

    /// <summary>
    /// Null when at least <paramref name="wanted"/> were bought. Otherwise what was bought and why the rest wasn't:
    /// <paramref name="nextPrice"/> is the cheapest listing left (above the limit), null when nothing else is listed.
    /// </summary>
    public static string? Shortfall(string name, int wanted, int got, long spent, uint? nextPrice, int maxPrice)
    {
        if (got >= wanted) return null;
        var why = nextPrice is { } price
            ? $"the next listing costs {price:N0} gil each, above your limit of {maxPrice:N0}"
            : "nobody sells more on this world right now";
        var missing = wanted - got;
        return $"Bought only {got} of {wanted} {name} ({spent:N0} gil): {why}. {missing} {(missing == 1 ? "is" : "are")} still missing.";
    }
}
