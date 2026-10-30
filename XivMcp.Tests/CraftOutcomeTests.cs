using XivMcp.Crafting;

namespace XivMcp.Tests;

public class CraftOutcomeTests
{
    // Rarefied Tacos de Carne Asada: tiers at collectability 660 / 900 / 1140 (quality 6600 / 9000 / 11400).
    private static readonly int[] Tacos = [6600, 9000, 11400];

    [Fact]
    public void A_collectable_below_the_lowest_tier_is_a_problem()
    {
        var outcome = new CraftOutcome(Finishes: true, Quality: 5230, Tacos);
        Assert.Equal(0, outcome.Tier);
        Assert.Equal(523, outcome.Collectability);
        Assert.Contains("523", outcome.Problem(collectable: true));
        Assert.Contains("660", outcome.Problem(collectable: true));
    }

    [Theory]
    [InlineData(6600, 1)]
    [InlineData(8999, 1)]
    [InlineData(9000, 2)]
    [InlineData(12000, 3)]
    public void The_tier_is_the_highest_breakpoint_reached(int quality, int tier)
    {
        var outcome = new CraftOutcome(true, quality, Tacos);
        Assert.Equal(tier, outcome.Tier);
        Assert.Null(outcome.Problem(collectable: true));
    }

    [Fact]
    public void A_macro_that_does_not_finish_is_a_problem_for_any_recipe()
    {
        var outcome = new CraftOutcome(Finishes: false, Quality: 20000, Tacos);
        Assert.Equal(0, outcome.Tier);
        Assert.NotNull(outcome.Problem(collectable: false));
        Assert.NotNull(outcome.Problem(collectable: true));
    }

    [Fact]
    public void Quality_does_not_matter_for_an_ordinary_recipe()
    {
        Assert.Null(new CraftOutcome(true, 0, []).Problem(collectable: false));
    }

    [Fact]
    public void Unset_breakpoints_are_ignored()
    {
        // Some collectables have only two tiers; the game leaves the third at 0.
        var outcome = new CraftOutcome(true, 7000, [6000, 6500, 0]);
        Assert.Equal(2, outcome.Tier);
        Assert.Equal("tier 2 of 2 (collectability 700)", outcome.Describe());
    }
}
