using XivMcp.Gear;

namespace XivMcp.Tests;

public class DyeWindowTests
{
    // Recorded in game (7.x): dyeing worn legs Rolanberry Red (stain 9, Shade 4, SubOrder 3): [4, 1], [5, 2, 9, 0], [0].

    [Fact]
    public void The_tab_is_the_colors_shade_group()
    {
        Assert.Equal([4, 1], DyeWindow.PickTab(shade: 4)); // reds, as recorded
        Assert.Equal([4, 0], DyeWindow.PickTab(shade: 2)); // whites, greys and blacks
        Assert.Equal([4, 6], DyeWindow.PickTab(shade: 9)); // purples
    }

    [Fact]
    public void An_unknown_shade_has_no_tab()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DyeWindow.PickTab(shade: 3));
    }

    [Fact]
    public void A_color_is_picked_by_its_place_in_the_tab_and_its_stain()
    {
        Assert.Equal([5, 2, 9, 0], DyeWindow.PickColor(stainId: 9, subOrder: 3));
        Assert.Equal([5, 7, 84, 0], DyeWindow.PickColor(stainId: 84, subOrder: 8));
    }

    [Fact]
    public void Apply_is_the_first_button()
    {
        Assert.Equal([0], DyeWindow.Apply);
    }
}
