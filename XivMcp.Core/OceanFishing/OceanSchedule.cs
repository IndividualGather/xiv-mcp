using System;
using System.Collections.Generic;

namespace XivMcp.OceanFishing;

/// <summary>One boarding window: when it opens, when the boat leaves, and the IKDRouteTable row that says where it goes.</summary>
public sealed record OceanVoyage(DateTimeOffset Boarding, DateTimeOffset Departure, int Row, bool Open);

/// <summary>
/// When ocean fishing boats leave. Boarding opens at every even UTC hour for 15 minutes; the boat leaves when it closes. The route
/// rotates through the IKDRouteTable sheet one row per 2-hour slot, counted from 1970-01-01 16:00 UTC (the same count clib and
/// the community schedules use).
/// </summary>
public static class OceanSchedule
{
    public const int SlotSeconds = 2 * 60 * 60;
    public const int BoardingSeconds = 15 * 60;
    private const long Epoch = 16 * 60 * 60;

    public static bool BoardingOpen(DateTimeOffset utc) => Mod(utc.ToUnixTimeSeconds(), SlotSeconds) < BoardingSeconds;

    /// <summary>The IKDRouteTable row of the slot <paramref name="utc"/> falls in.</summary>
    public static int Row(DateTimeOffset utc, int tableSize) =>
        (int)Mod(FloorDiv(utc.ToUnixTimeSeconds() - Epoch, SlotSeconds), tableSize);

    /// <summary>The next <paramref name="count"/> voyages; the first is the one boarding now, if any.</summary>
    public static List<OceanVoyage> Upcoming(DateTimeOffset utc, int count, int tableSize)
    {
        var now = utc.ToUnixTimeSeconds();
        var slot = now - Mod(now, SlotSeconds);
        if (now - slot >= BoardingSeconds) slot += SlotSeconds;
        var voyages = new List<OceanVoyage>(count);
        for (var i = 0; i < count; i++, slot += SlotSeconds)
        {
            var boarding = DateTimeOffset.FromUnixTimeSeconds(slot);
            voyages.Add(new OceanVoyage(boarding, boarding.AddSeconds(BoardingSeconds), Row(boarding, tableSize), now >= slot && now < slot + BoardingSeconds));
        }
        return voyages;
    }

    private static long Mod(long a, long m) => (a % m + m) % m;

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
}
