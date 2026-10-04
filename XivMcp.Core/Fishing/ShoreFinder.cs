using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Fishing;

/// <summary>A place at the water's edge: where to stand, the ground a few yalms inland to walk from (so the character faces the water), and the direction of the water.</summary>
public sealed record ShorePoint(double X, double Z, double InlandX, double InlandZ, double Angle);

/// <summary>
/// Finds the water's edge around a fishing spot. Fishing needs the character at the edge, facing the water; a spot's centre is
/// often on land (a pier or a cliff) or out on the water. From the centre, it steps outward in every direction while the ground can
/// be walked on; the last walkable step before the ground ends is the shore in that direction.
/// </summary>
public static class ShoreFinder
{
    /// <param name="walkable">Whether the ground at (x, z) can be walked on (the navmesh).</param>
    /// <param name="fromX">Where the character is: candidates are sorted by distance from there.</param>
    public static IReadOnlyList<ShorePoint> Candidates(double centreX, double centreZ, double maxRadius, Func<double, double, bool> walkable,
                                                       double fromX, double fromZ, int directions = 16, double step = 1.5, double inland = 3)
    {
        var found = new List<ShorePoint>();
        for (var d = 0; d < directions; d++)
        {
            var angle = 2 * Math.PI * d / directions;
            var (dx, dz) = (Math.Sin(angle), Math.Cos(angle));
            double? lastX = null, lastZ = null;
            var r = 0.0;
            var leftGround = false;
            // From ground, the shore is where the ground ends; from the water, it is the first ground on the way out.
            var startOnGround = walkable(centreX, centreZ);
            for (r = 0; r <= maxRadius; r += step)
            {
                var (x, z) = (centreX + dx * r, centreZ + dz * r);
                var ground = walkable(x, z);
                if (startOnGround)
                {
                    if (ground) { lastX = x; lastZ = z; continue; }
                    leftGround = true;
                    break;
                }
                // From the water: the first ground on the way out is the shore, and the water lies back towards the centre.
                if (ground)
                {
                    var back = angle + Math.PI;
                    found.Add(new ShorePoint(x, z, x + dx * inland, z + dz * inland, Normalize(back)));
                    break;
                }
            }
            if (startOnGround && leftGround && lastX is { } sx && lastZ is { } sz)
                found.Add(new ShorePoint(sx, sz, sx - dx * inland, sz - dz * inland, Normalize(angle)));
        }
        return found.OrderBy(p => (p.X - fromX) * (p.X - fromX) + (p.Z - fromZ) * (p.Z - fromZ)).ToList();
    }

    private static double Normalize(double a) => (a % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI);
}
