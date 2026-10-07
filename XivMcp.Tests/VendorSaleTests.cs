using System;
using XivMcp.Shops;

namespace XivMcp.Tests;

public class VendorSaleTests
{
    private static readonly int[] Stacks = [999, 999, 352, 40];

    [Fact]
    public void Without_a_quantity_every_stack_is_sold()
    {
        Assert.Equal([3, 2, 0, 1], VendorSale.Pick(Stacks, quantity: null, keep: 0));
    }

    [Fact]
    public void Keeping_some_leaves_whole_stacks_behind()
    {
        // Small stacks go first, so the large ones stay.
        Assert.Equal([3, 2], VendorSale.Pick(Stacks, quantity: null, keep: 1000));
        Assert.Empty(VendorSale.Pick(Stacks, quantity: null, keep: 5000));
    }

    [Fact]
    public void A_quantity_is_never_exceeded()
    {
        Assert.Equal([3, 2], VendorSale.Pick(Stacks, quantity: 500, keep: 0));
        Assert.Equal([3], VendorSale.Pick(Stacks, quantity: 45, keep: 0));
        Assert.Empty(VendorSale.Pick(Stacks, quantity: 10, keep: 0));
    }

    [Fact]
    public void Nonsense_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => VendorSale.Pick(Stacks, quantity: 0, keep: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => VendorSale.Pick(Stacks, quantity: null, keep: -1));
    }
}
