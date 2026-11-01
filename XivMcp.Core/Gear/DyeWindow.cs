using System;

namespace XivMcp.Gear;

/// <summary>
/// The values the game's dye window (ColorantColoring) is sent, as recorded when a player dyes a piece: the color tab, the color,
/// Apply. The confirmation that follows (MiragePrismMiragePlateConfirm) is answered with [0]. A color that isn't in the open tab is
/// ignored by the game (it keeps the color picked last), so the tab comes first.
/// </summary>
public static class DyeWindow
{
    /// <summary>The tabs in order, by the Stain sheet's Shade: whites/greys/blacks, reds, browns, yellows, greens, blues, purples, special.</summary>
    private static readonly int[] Shades = [2, 4, 5, 6, 7, 8, 9, 10];

    public static int[] PickTab(int shade)
    {
        var tab = Array.IndexOf(Shades, shade);
        if (tab < 0) throw new ArgumentOutOfRangeException(nameof(shade), shade, "No dye window tab for this shade.");
        return [4, tab];
    }

    /// <summary>A color in the open tab: its place in the list (the Stain sheet's SubOrder, from 1) and its Stain row.</summary>
    public static int[] PickColor(uint stainId, int subOrder) => [5, subOrder - 1, (int)stainId, 0];

    public static readonly int[] Apply = [0];
}
