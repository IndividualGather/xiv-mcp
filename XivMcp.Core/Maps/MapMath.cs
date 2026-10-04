using System;

namespace XivMcp.Maps;

/// <summary>What converting between world and map coordinates needs from a Map sheet row.</summary>
public sealed record MapInfo(uint SizeFactor, short OffsetX, short OffsetY);

/// <summary>
/// World positions (yalms; X east, Z south) and map coordinates (the X/Y the in-game map shows, about 1 to 42). The map is 2048 pixels
/// across, 41 units wide, and scaled by the map's size factor (100 for most field maps, 200 for cities).
/// </summary>
public static class MapMath
{
    private static double Scale(MapInfo map) => map.SizeFactor / 100.0;

    public static (double X, double Y) ToMap(MapInfo map, double worldX, double worldZ)
    {
        var scale = Scale(map);
        double Coord(double w, short offset) => 41 / scale * (((w + offset) * scale + 1024) / 2048) + 1;
        return (Coord(worldX, map.OffsetX), Coord(worldZ, map.OffsetY));
    }

    public static (double X, double Z) ToWorld(MapInfo map, double mapX, double mapY)
    {
        var scale = Scale(map);
        double World(double c, short offset) => ((c - 1) * scale * 2048 / 41 - 1024) / scale - offset;
        return (World(mapX, map.OffsetX), World(mapY, map.OffsetY));
    }

    /// <summary>How many yalms one map unit spans on this map.</summary>
    public static double YalmsPerMapUnit(MapInfo map) => 2048 / 41.0 / Scale(map);

    /// <summary>Distance on the ground (X/Z), ignoring height.</summary>
    public static double GroundDistance(double ax, double az, double bx, double bz) => Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));

    /// <summary>The player is in the target's zone and within <paramref name="radius"/> yalms of it on the ground.</summary>
    public static bool Arrived(uint territory, double x, double z, uint targetTerritory, double targetX, double targetZ, double radius) =>
        territory == targetTerritory && GroundDistance(x, z, targetX, targetZ) <= radius;
}
