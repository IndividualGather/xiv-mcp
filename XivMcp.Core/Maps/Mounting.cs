namespace XivMcp.Maps;

/// <summary>How to cover a walk: on foot, by mounting up first, or on the mount the player already sits on.</summary>
public enum MountChoice { Walk, Mount, Ride }

/// <summary>When walks use a mount, and when the mount flies.</summary>
public static class Mounting
{
    /// <summary>Shorter walks than this (yalms) stay on foot; mounting takes longer than it saves.</summary>
    public const float MinDistance = 50;

    /// <param name="distance">Straight-line distance to the destination, in yalms.</param>
    /// <param name="enabled">The player's setting.</param>
    /// <param name="mounted">The player sits on a mount already.</param>
    /// <param name="canMount">The game allows mounting here and now.</param>
    /// <param name="canFly">The zone allows flying (and the player unlocked it there).</param>
    public static MountChoice Choose(float distance, bool enabled, bool mounted, bool canMount, bool canFly)
    {
        if (mounted) return MountChoice.Ride;
        return enabled && canMount && distance >= MinDistance ? MountChoice.Mount : MountChoice.Walk;
    }

    /// <summary>Whether vnavmesh should plan a flying path.</summary>
    public static bool Fly(bool mounted, bool canFly) => mounted && canFly;
}
