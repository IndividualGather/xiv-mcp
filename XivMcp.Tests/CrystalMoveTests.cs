using XivMcp.Transfers;

namespace XivMcp.Tests;

public class CrystalMoveTests
{
    [Fact]
    public void A_whole_stack_that_fits_moves_whole()
    {
        var p = CrystalMove.For(null, 9_999, 0);
        Assert.Equal(9_999, p.Amount);
        Assert.False(p.Partial(9_999));
        Assert.Null(p.Note);
    }

    [Fact]
    public void Only_what_fits_moves_into_a_fuller_slot()
    {
        var p = CrystalMove.For(null, 9_999, 9_450);
        Assert.Equal(549, p.Amount);
        Assert.True(p.Partial(9_999));
        Assert.Contains("only 549 fit", p.Note);
    }

    [Fact]
    public void A_quantity_moves_part_of_a_stack()
    {
        var p = CrystalMove.For(1_000, 9_999, 0);
        Assert.Equal(1_000, p.Amount);
        Assert.True(p.Partial(9_999));
    }

    [Fact]
    public void A_quantity_beyond_the_stack_moves_the_stack()
    {
        var p = CrystalMove.For(20_000, 850, 0);
        Assert.Equal(850, p.Amount);
        Assert.False(p.Partial(850));
        Assert.Contains("only 850 there", p.Note);
    }

    [Fact]
    public void A_full_slot_takes_nothing()
    {
        var p = CrystalMove.For(null, 500, 9_999);
        Assert.Equal(0, p.Amount);
        Assert.Contains("9,999", p.Problem);
    }

    [Fact]
    public void An_empty_source_is_a_problem()
    {
        Assert.NotNull(CrystalMove.For(null, 0, 0).Problem);
    }
}
