using XivMcp.Fashion;

namespace XivMcp.Tests;

public class FashionDyeTests
{
    private const uint Spectrum = 1, OldCharcoal = 2, OldBlue = 3;
    private static readonly uint[] Charcoal = [Spectrum, OldCharcoal], Blue = [Spectrum, OldBlue];

    private static Func<uint, int> Owned(params (uint Item, int Count)[] have) => i => have.FirstOrDefault(h => h.Item == i).Count;

    [Fact]
    public void Every_dye_step_gets_a_dye_including_slots_the_set_has_no_piece_for()
    {
        // Week 460's easy 100: the helm, then body, hands and legs as worn.
        var buy = FashionDyes.ToBuy([Charcoal, Blue, Charcoal, Charcoal], Owned());
        Assert.Equal(4, buy[Spectrum]);
        Assert.Single(buy);
    }

    [Fact]
    public void Dyes_in_the_bags_are_used_first()
    {
        Assert.Equal(1, FashionDyes.ToBuy([Charcoal, Charcoal, Blue], Owned((Spectrum, 2)))[Spectrum]);
        Assert.Empty(FashionDyes.ToBuy([Charcoal, Charcoal], Owned((Spectrum, 5))));
    }

    [Fact]
    public void An_old_dye_of_the_color_counts_only_for_its_own_color()
    {
        var buy = FashionDyes.ToBuy([Charcoal, Blue], Owned((OldCharcoal, 1)));
        Assert.Equal(1, buy[Spectrum]);
        Assert.False(buy.ContainsKey(OldBlue));
    }

    [Fact]
    public void A_color_without_dye_items_buys_nothing()
    {
        Assert.Empty(FashionDyes.ToBuy([[]], Owned()));
    }
}
