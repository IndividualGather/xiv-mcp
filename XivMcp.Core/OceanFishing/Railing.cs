using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XivMcp.OceanFishing;

/// <summary>A place to fish from on the boat, and the rotation that faces the ocean from there.</summary>
public sealed record RailSpot(Vector3 Position, float Facing, float Gap);

/// <summary>
/// Where to stand on an ocean fishing boat: along the railing on either side (the same stretches AutoHook uses), as far from the
/// other players as possible, so nobody stands on top of anybody.
/// </summary>
public static class Railing
{
    private const float Deck = 6.711f;
    private const float Step = 0.5f;

    /// <summary>The stretches of railing: x on the boat, the z range, and the rotation facing the water.</summary>
    private static readonly (float X, float ZMin, float ZMax, float Facing)[] Stretches =
    [
        (7.1f, -12f, -4f, 1.5f),   // port side, front part
        (7.1f, -2f, 3f, 1.5f),     // port side, back part
        (-7.1f, -11f, 3.5f, -1.5f), // starboard side
    ];

    /// <summary>
    /// The spot with the most room to the nearest other player (on the deck plane); among equally free spots, the one closest to
    /// <paramref name="me"/>. Gap is that room in yalms (float.MaxValue when alone).
    /// </summary>
    public static RailSpot FreeSpot(Vector3 me, IEnumerable<Vector3> others)
    {
        var players = others.Select(o => new Vector2(o.X, o.Z)).ToList();
        RailSpot? best = null;
        foreach (var (x, zMin, zMax, facing) in Stretches)
            for (var z = zMin; z <= zMax + 0.001f; z += Step)
            {
                var at = new Vector2(x, z);
                var gap = players.Count == 0 ? float.MaxValue : players.Min(p => Vector2.Distance(p, at));
                var spot = new RailSpot(new Vector3(x, Deck, z), facing, gap);
                if (best is null || Better(spot, best, me)) best = spot;
            }
        return best!;
    }

    // More room wins; once both have plenty (3 yalms), the nearer one does.
    private static bool Better(RailSpot a, RailSpot b, Vector3 me)
    {
        const float Plenty = 3f;
        float Room(RailSpot s) => Math.Min(s.Gap, Plenty);
        if (Math.Abs(Room(a) - Room(b)) > 0.01f) return Room(a) > Room(b);
        return Distance(a, me) < Distance(b, me);
    }

    private static float Distance(RailSpot s, Vector3 me) => Vector2.Distance(new Vector2(s.Position.X, s.Position.Z), new Vector2(me.X, me.Z));
}
