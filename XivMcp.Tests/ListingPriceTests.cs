using XivMcp.Shops;

namespace XivMcp.Tests;

public class ListingPriceTests
{
    [Theory]
    [InlineData(9_450, 105_820)]
    [InlineData(6_858, 145_815)]
    [InlineData(1, 999_999_999)]
    public void The_highest_unit_price_keeps_the_total_under_the_cap(int quantity, int max) =>
        Assert.Equal(max, ListingPrice.MaxUnitPrice(quantity));

    [Fact]
    public void A_total_over_the_cap_is_a_problem()
    {
        Assert.Contains("105,820", ListingPrice.Problem(999_999, 9_450));
        Assert.Null(ListingPrice.Problem(105_820, 9_450));
    }
}
