using XivMcp.TripleTriad;

namespace XivMcp.Tests;

public class TriadDeckRulesTests
{
    private static TriadDeckCard C(int id, int stars, bool owned = true) => new(id, $"Card {id}", stars, owned);

    [Fact]
    public void Five_owned_different_cards_make_a_deck()
    {
        Assert.Null(TriadDeckRules.Problem([C(1, 1), C(2, 2), C(3, 3), C(4, 4), C(5, 5)]));
    }

    [Fact]
    public void A_deck_has_exactly_five_cards()
    {
        Assert.Contains("5 cards", TriadDeckRules.Problem([C(1, 1), C(2, 1)]));
    }

    [Fact]
    public void Cards_must_be_owned_and_different()
    {
        Assert.Contains("Card 3", TriadDeckRules.Problem([C(1, 1), C(2, 1), C(3, 1, owned: false), C(4, 1), C(5, 1)]));
        Assert.Contains("twice", TriadDeckRules.Problem([C(1, 1), C(1, 1), C(3, 1), C(4, 1), C(5, 1)]));
    }

    [Fact]
    public void At_most_one_five_star_and_two_four_star_or_better()
    {
        Assert.Contains("5-star", TriadDeckRules.Problem([C(1, 5), C(2, 5), C(3, 1), C(4, 1), C(5, 1)]));
        Assert.Contains("4-star", TriadDeckRules.Problem([C(1, 4), C(2, 4), C(3, 5), C(4, 1), C(5, 1)]));
        Assert.Null(TriadDeckRules.Problem([C(1, 4), C(2, 5), C(3, 3), C(4, 1), C(5, 1)]));
    }
}

public class JumboTicketTests
{
    [Theory]
    [InlineData("1234", true)]
    [InlineData("0007", true)]
    [InlineData("123", false)]
    [InlineData("12a4", false)]
    [InlineData("12345", false)]
    public void Ticket_numbers_have_four_digits(string number, bool valid)
    {
        Assert.Equal(valid, JumboTickets.IsValid(number));
    }

    [Fact]
    public void At_most_three_numbers_are_taken()
    {
        Assert.Throws<ArgumentException>(() => JumboTickets.Parse(["1111", "2222", "3333", "4444"]));
        Assert.Throws<ArgumentException>(() => JumboTickets.Parse(["12"]));
        Assert.Equal(["1111", "2222"], JumboTickets.Parse(["1111", " 2222 "]));
    }
}

public class NameMatchTests
{
    private static readonly string[] Npcs = ["Triple Triad Trader", "Joellaut", "Mother Miounne", "Roger", "Rowena"];

    [Fact]
    public void An_exact_name_wins_ignoring_case()
    {
        Assert.Equal("Roger", NameMatch.Single(Npcs, "roger", n => n));
    }

    [Fact]
    public void A_unique_part_of_a_name_is_enough()
    {
        Assert.Equal("Mother Miounne", NameMatch.Single(Npcs, "miounne", n => n));
    }

    [Fact]
    public void An_ambiguous_or_unknown_name_says_what_matched()
    {
        var ambiguous = Assert.Throws<ArgumentException>(() => NameMatch.Single(Npcs, "ro", n => n));
        Assert.Contains("Roger", ambiguous.Message);
        Assert.Contains("Rowena", ambiguous.Message);
        Assert.Throws<ArgumentException>(() => NameMatch.Single(Npcs, "nobody", n => n));
    }
}
