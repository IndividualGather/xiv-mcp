using System;
using System.Linq;
using XivMcp.Transfers;

namespace XivMcp.Tests;

public class GilAmountTests
{
    [Theory]
    [InlineData("1200", 5000, 1200)]
    [InlineData("1,200", 5000, 1200)]
    [InlineData("15k", 100000, 15000)]
    [InlineData("1.5m", 9000000, 1500000)]
    [InlineData("50%", 5001, 2500)]
    [InlineData("all", 4321, 4321)]
    [InlineData(" ALL ", 4321, 4321)]
    public void Reads_amounts_like_players_write_them(string text, long available, long expected) =>
        Assert.Equal(expected, GilAmount.Parse(text, available));

    [Theory]
    [InlineData("0", "more than 0")]
    [InlineData("-5", "more than 0")]
    [InlineData("150%", "between 1% and 100%")]
    [InlineData("lots", "Unknown gil amount")]
    [InlineData("6000", "only 5,000 gil")]
    public void Refuses_what_cannot_be_moved(string text, string message) =>
        Assert.Contains(message, Assert.Throws<FormatException>(() => GilAmount.Parse(text, 5000)).Message);

    [Fact]
    public void All_of_nothing_is_refused()
    {
        Assert.Contains("no gil", Assert.Throws<FormatException>(() => GilAmount.Parse("all", 0)).Message);
    }

    [Fact]
    public void A_cap_limits_what_fits()
    {
        Assert.Contains("999,999,999", Assert.Throws<FormatException>(() => GilAmount.Parse("10", 100, room: 5)).Message);
        Assert.Equal(5, GilAmount.Parse("all", 100, room: 5));
    }
}

public class TradeCheckTests
{
    private static string Name(uint id) => id switch { 1 => "Gil", 4 => "Potion", 5 => "Ether", 6 => "Elixir", _ => $"#{id}" };

    private static readonly TradeOffer Give = new([new(4, false, 3), new(5, true, 1)], 1000);

    [Fact]
    public void A_trade_that_matches_has_no_problems()
    {
        var actual = new TradeOffer([new(5, true, 1), new(4, false, 2), new(4, false, 1)], 1000);
        Assert.Empty(TradeCheck.Problems(Give, actual, new TradeOffer([new(6, false, 1)], 0), new TradeOffer([new(6, false, 1)], 50), Name));
    }

    [Fact]
    public void Giving_anything_else_is_a_problem()
    {
        var p = TradeCheck.Problems(Give, new TradeOffer([new(4, false, 3), new(5, false, 1)], 2000), null, TradeOffer.Empty, Name);
        Assert.Contains(p, s => s.Contains("Ether (HQ)"));
        Assert.Contains(p, s => s.Contains("2,000 gil instead of 1,000"));
    }

    [Fact]
    public void Receiving_less_than_expected_is_a_problem_receiving_more_is_not()
    {
        var expected = new TradeOffer([new(6, false, 2)], 500);
        Assert.Contains(TradeCheck.Problems(Give, Give, expected, new TradeOffer([new(6, false, 1)], 500), Name), s => s.Contains("1 of 2 Elixir"));
        Assert.Contains(TradeCheck.Problems(Give, Give, expected, new TradeOffer([new(6, false, 2)], 400), Name), s => s.Contains("400 gil instead of 500"));
        Assert.Empty(TradeCheck.Problems(Give, Give, expected, new TradeOffer([new(6, false, 3), new(4, false, 1)], 900), Name));
    }

    [Fact]
    public void Describes_an_offer_for_the_approval()
    {
        Assert.Equal("3 Potion, 1 Ether (HQ) and 1,000 gil", Give.Describe(Name));
        Assert.Equal("nothing", TradeOffer.Empty.Describe(Name));
    }

    [Fact]
    public void Only_one_side_can_put_gil_in()
    {
        Assert.Null(TradeCheck.PlanProblem(new TradeOffer([], 100), null));
        Assert.Null(TradeCheck.PlanProblem(new TradeOffer([new(4, false, 1)], 0), new TradeOffer([], 50)));
        Assert.Contains("one side", TradeCheck.PlanProblem(new TradeOffer([], 100), new TradeOffer([], 50)));
    }

    [Fact]
    public void A_trade_has_room_for_five_items()
    {
        Assert.Null(TradeOffer.Problem(Give));
        var six = new TradeOffer(Enumerable.Range(10, 6).Select(i => new TradeItem((uint)i, false, 1)).ToList(), 0);
        Assert.Contains("5 items", TradeOffer.Problem(six));
        Assert.Contains("1,000,000", TradeOffer.Problem(new TradeOffer([], 2_000_000)));
    }
}
