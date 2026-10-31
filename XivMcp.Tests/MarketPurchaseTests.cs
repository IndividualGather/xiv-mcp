using XivMcp.Shops;

namespace XivMcp.Tests;

public class MarketPurchaseTests
{
    [Fact]
    public void Enough_bought_is_no_shortfall()
    {
        Assert.Null(MarketPurchase.Shortfall("Rroneek Fleece", wanted: 12, got: 13, spent: 600, nextPrice: null, maxPrice: 50));
    }

    [Fact]
    public void Short_because_the_rest_costs_more_says_what_it_costs()
    {
        var message = MarketPurchase.Shortfall("Rroneek Fleece", wanted: 12, got: 1, spent: 315, nextPrice: 500, maxPrice: 350);
        Assert.Equal("Bought only 1 of 12 Rroneek Fleece (315 gil): the next listing costs 500 gil each, above your limit of 350. " +
                     "11 are still missing.", message);
    }

    [Fact]
    public void Short_because_nothing_else_is_listed()
    {
        var message = MarketPurchase.Shortfall("Rroneek Fleece", wanted: 12, got: 5, spent: 25, nextPrice: null, maxPrice: 350);
        Assert.Equal("Bought only 5 of 12 Rroneek Fleece (25 gil): nobody sells more on this world right now. 7 are still missing.", message);
    }

    [Fact]
    public void One_missing_is_singular()
    {
        var message = MarketPurchase.Shortfall("Rroneek Fleece", wanted: 2, got: 1, spent: 5, nextPrice: null, maxPrice: 10);
        Assert.EndsWith("1 is still missing.", message);
    }
}
