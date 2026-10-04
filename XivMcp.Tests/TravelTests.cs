using XivMcp.Maps;
using XivMcp.Shops;

namespace XivMcp.Tests;

public class VendorRankingTests
{
    private static VendorSpot Spot(string name, bool gil = true, bool city = false, bool here = false, bool located = true) =>
        new(name, gil, city, here, located);

    private static string[] Rank(params VendorSpot[] spots) => VendorRanking.Order(spots).Select(s => s.Name).ToArray();

    [Fact]
    public void City_vendors_come_before_field_vendors()
    {
        Assert.Equal(["Domitien", "Arms Supplier"], Rank(Spot("Arms Supplier"), Spot("Domitien", city: true)));
    }

    [Fact]
    public void A_city_vendor_beats_a_field_vendor_in_the_current_zone()
    {
        Assert.Equal(["Gwalter", "Junkmonger"], Rank(Spot("Junkmonger", here: true), Spot("Gwalter", city: true)));
    }

    [Fact]
    public void Among_cities_the_one_the_player_is_in_comes_first()
    {
        Assert.Equal(["Domitien", "Iron Thunder"], Rank(Spot("Iron Thunder", city: true), Spot("Domitien", city: true, here: true)));
    }

    [Fact]
    public void Among_field_vendors_the_current_zone_comes_first()
    {
        Assert.Equal(["Junkmonger", "Arms Supplier"], Rank(Spot("Arms Supplier"), Spot("Junkmonger", here: true)));
    }

    [Fact]
    public void Gil_vendors_come_before_exchanges_even_in_a_city()
    {
        Assert.Equal(["Arms Supplier", "Scrip Exchange"], Rank(Spot("Scrip Exchange", gil: false, city: true, here: true), Spot("Arms Supplier")));
    }

    [Fact]
    public void Vendors_without_a_known_location_come_last()
    {
        Assert.Equal(["Arms Supplier", "Nowhere"], Rank(Spot("Nowhere", city: true, located: false), Spot("Arms Supplier")));
    }

    [Fact]
    public void The_order_is_stable_otherwise()
    {
        Assert.Equal(["A", "B", "C"], Rank(Spot("A", city: true), Spot("B", city: true), Spot("C", city: true)));
    }
}

public class MountChoiceTests
{
    [Fact]
    public void Short_walks_stay_on_foot()
    {
        Assert.Equal(MountChoice.Walk, Mounting.Choose(distance: 30, enabled: true, mounted: false, canMount: true, canFly: true));
    }

    [Fact]
    public void Longer_walks_mount_up()
    {
        Assert.Equal(MountChoice.Mount, Mounting.Choose(distance: 80, enabled: true, mounted: false, canMount: true, canFly: false));
    }

    [Fact]
    public void Nothing_happens_when_turned_off_or_mounting_is_not_possible()
    {
        Assert.Equal(MountChoice.Walk, Mounting.Choose(distance: 200, enabled: false, mounted: false, canMount: true, canFly: true));
        Assert.Equal(MountChoice.Walk, Mounting.Choose(distance: 200, enabled: true, mounted: false, canMount: false, canFly: true));
    }

    [Fact]
    public void Already_mounted_rides_on_whatever_the_distance()
    {
        Assert.Equal(MountChoice.Ride, Mounting.Choose(distance: 10, enabled: true, mounted: true, canMount: false, canFly: false));
    }

    [Fact]
    public void Flying_is_used_where_the_zone_allows_it()
    {
        Assert.True(Mounting.Fly(mounted: true, canFly: true));
        Assert.False(Mounting.Fly(mounted: true, canFly: false));
        Assert.False(Mounting.Fly(mounted: false, canFly: true));
    }
}
