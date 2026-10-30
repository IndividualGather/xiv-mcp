using System;
using System.Collections.Generic;
using XivMcp.Shops;

namespace XivMcp.Tests;

public class CreditShopTests
{
    // The window's values as the game filled them in a workshop (FC rank 9, 238,459 credits, one row: Ceruleum Tank).
    private static readonly Dictionary<int, object> Window = new()
    {
        [0] = 9u, [1] = true, [3] = 238459u, [9] = 1u,
        [10] = "Ceruleum Tank", [30] = 10155u, [70] = 6u, [90] = 31u, [110] = 99, [130] = 100u,
    };

    private static uint UInt(Dictionary<int, object> v, int i) => v.TryGetValue(i, out var o) ? o switch { uint u => u, int n => (uint)n, _ => 0 } : 0;

    private static CreditShop Read(Dictionary<int, object> v) =>
        CreditShop.Read(i => UInt(v, i), i => v.TryGetValue(i, out var o) && o is true, i => v.TryGetValue(i, out var o) ? o as string : null);

    [Fact]
    public void The_window_lists_credits_and_rows()
    {
        var shop = Read(Window);
        Assert.Equal(238459, shop.Credits);
        Assert.True(shop.MaySpend);
        var row = Assert.Single(shop.Rows);
        Assert.Equal(new CreditShopRow(0, 10155, "Ceruleum Tank", 100, 31, 99), row);
        Assert.Same(row, shop.Find(10155));
        Assert.Null(shop.Find(1));
    }

    [Fact]
    public void A_character_without_the_right_may_not_spend()
    {
        var v = new Dictionary<int, object>(Window) { [1] = false };
        Assert.False(Read(v).MaySpend);
    }

    [Fact]
    public void Purchases_are_split_into_the_most_one_purchase_allows()
    {
        Assert.Equal([99, 99, 52], CreditShop.Batches(250, credits: 238459, price: 100, perPurchase: 99));
        Assert.Equal([10], CreditShop.Batches(10, credits: 238459, price: 100, perPurchase: 99));
    }

    [Fact]
    public void Only_what_the_credits_pay_for_is_bought()
    {
        Assert.Equal([99, 21], CreditShop.Batches(500, credits: 12050, price: 100, perPurchase: 99));
        Assert.Empty(CreditShop.Batches(5, credits: 99, price: 100, perPurchase: 99));
    }

    [Fact]
    public void Nonsense_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreditShop.Batches(0, 1000, 100, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreditShop.Batches(5, 1000, 0, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreditShop.Batches(5, 1000, 100, 0));
    }
}
