using XivMcp.CustomDeliveries;

namespace XivMcp.Tests;

public class SpecialCurrencyTests
{
    [Fact]
    public void The_inventory_count_wins_while_the_currency_manager_still_shows_the_old_amount()
    {
        // Two turn-ins of 198 purple crafters' scrips: the inventory has 396, the currency manager still says 0.
        Assert.Equal(396, SpecialCurrency.Held(inventoryCount: 396, managerCount: 0));
    }

    [Fact]
    public void Spending_shows_at_once_too()
    {
        Assert.Equal(100, SpecialCurrency.Held(inventoryCount: 100, managerCount: 1500));
    }

    [Fact]
    public void A_currency_the_inventory_does_not_count_falls_back_to_the_currency_manager()
    {
        Assert.Equal(12, SpecialCurrency.Held(inventoryCount: 0, managerCount: 12));
    }

    [Fact]
    public void Room_under_the_cap()
    {
        Assert.Equal(3604, SpecialCurrency.Room(have: 396, max: 4000));
        Assert.Equal(0, SpecialCurrency.Room(have: 4100, max: 4000));
    }

    [Fact]
    public void No_cap_means_unlimited_room()
    {
        Assert.Equal(long.MaxValue, SpecialCurrency.Room(have: 396, max: 0));
    }
}
