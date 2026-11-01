using System;

namespace XivMcp.Maps;

/// <summary>
/// Tells when the player stands still: the same spot for several samples in a row, and nothing else going on (mounted, moving,
/// casting). Some items, like the Gold Saucer VIP Card, are only used while standing still.
/// </summary>
public sealed class StandingStill(int samples)
{
    /// <summary>Yalms the player may drift between samples and still count as standing.</summary>
    public const float Tolerance = 0.05f;

    private float x, y, z;
    private int count;

    /// <summary>Adds a sample of the player's position; <paramref name="busy"/> when they are mounted, casting or otherwise occupied.</summary>
    /// <returns>Whether the player has now stood still for <c>samples</c> samples.</returns>
    public bool Add(float px, float py, float pz, bool busy = false)
    {
        var same = count > 0 && Math.Abs(px - x) <= Tolerance && Math.Abs(py - y) <= Tolerance && Math.Abs(pz - z) <= Tolerance;
        count = busy ? 0 : same ? count + 1 : 1;
        (x, y, z) = (px, py, pz);
        return count >= samples;
    }
}
