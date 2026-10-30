using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Shops;

/// <summary>One item of the company credit exchange: its row in the window, the price in credits, how many the bags hold and the most one purchase buys.</summary>
public sealed record CreditShopRow(int Index, uint ItemId, string Name, int Price, int InBags, int PerPurchase);

/// <summary>
/// The free company's credit exchange (FreeCompanyCreditShop: the workshop's mammet sells Ceruleum Tanks there), as its window
/// shows it: the company's credits, whether this character may spend them, and the items. Its values: [1] may spend, [3] credits,
/// [9] the number of items, then per item i the name at 10+i, the item id at 30+i, the bag count at 90+i, the most per purchase at
/// 110+i and the price at 130+i.
/// </summary>
public sealed record CreditShop(long Credits, bool MaySpend, IReadOnlyList<CreditShopRow> Rows)
{
    private const int MaxRows = 20;

    public CreditShopRow? Find(uint itemId) => Rows.FirstOrDefault(r => r.ItemId == itemId);

    public static CreditShop Read(Func<int, uint> number, Func<int, bool> flag, Func<int, string?> text)
    {
        var count = (int)Math.Min(number(9), MaxRows);
        var rows = Enumerable.Range(0, count)
            .Select(i => new CreditShopRow(i, number(30 + i), text(10 + i) ?? "", (int)number(130 + i), (int)number(90 + i), (int)number(110 + i)))
            .Where(r => r.ItemId != 0)
            .ToList();
        return new CreditShop(number(3), flag(1), rows);
    }

    /// <summary>
    /// The purchases that buy <paramref name="quantity"/> items, each at most <paramref name="perPurchase"/>, as far as the credits
    /// pay for them.
    /// </summary>
    public static IReadOnlyList<int> Batches(int quantity, long credits, int price, int perPurchase)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity), "Buy at least one.");
        if (price <= 0) throw new ArgumentOutOfRangeException(nameof(price), "The price must be positive.");
        if (perPurchase <= 0) throw new ArgumentOutOfRangeException(nameof(perPurchase), "A purchase buys at least one.");
        var left = (int)Math.Min(quantity, credits / price);
        var batches = new List<int>();
        while (left > 0)
        {
            var batch = Math.Min(left, perPurchase);
            batches.Add(batch);
            left -= batch;
        }
        return batches;
    }
}
