using System;

namespace XivMcp.Maps;

/// <summary>
/// Whether a walk towards a target still makes progress: a character running into a wall (or sliding along it) keeps moving a
/// little, but stops getting closer. The walk has stalled once the distance hasn't dropped by <see cref="MinGain"/> yalms for
/// the given window.
/// </summary>
public sealed class Approach(TimeSpan window)
{
    /// <summary>Yalms the distance must drop by to count as progress.</summary>
    public const float MinGain = 0.5f;

    private float best = float.MaxValue;
    private DateTime bestAt;

    /// <summary>Adds the current distance to the target; true when the walk has stalled.</summary>
    public bool Stalled(float distance, DateTime now)
    {
        if (best == float.MaxValue || distance < best - MinGain)
        {
            best = distance;
            bestAt = now;
            return false;
        }
        return now - bestAt >= window;
    }
}
