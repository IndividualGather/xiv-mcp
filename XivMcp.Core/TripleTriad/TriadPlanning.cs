using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace XivMcp.TripleTriad;

/// <summary>Eorzea time: 1 Eorzea hour lasts 175 real seconds, counted from the Unix epoch.</summary>
public static class EorzeaClock
{
    /// <summary>Minutes since Eorzea midnight (0–1439).</summary>
    public static int MinuteOfDay(DateTime utc)
    {
        var eorzeaSeconds = (long)((utc - DateTime.UnixEpoch).TotalSeconds * 3600 / 175);
        return (int)(eorzeaSeconds / 60 % (24 * 60));
    }

    /// <summary>Whether a minute of the day is inside an HHMM window (start inclusive, end exclusive). 0–0 means always; windows may cross midnight.</summary>
    public static bool InWindow(int startHhmm, int endHhmm, int minute)
    {
        if (startHhmm == endHhmm) return true;
        var start = startHhmm / 100 * 60 + startHhmm % 100;
        var end = endHhmm / 100 * 60 + endHhmm % 100;
        return start < end ? minute >= start && minute < end : minute >= start || minute < end;
    }

    public static string Format(int hhmm) => $"{hhmm / 100:00}:{hhmm % 100:00}";
}

/// <summary>Whether a Triple Triad opponent can be challenged, as Saucy decides it.</summary>
public static class TriadAvailability
{
    /// <summary>
    /// Unlocked when the opponent needs no quest, any of their unlock quests is complete, or the player has already beaten them or
    /// owns one of their cards (which can only happen after playing them).
    /// </summary>
    public static bool IsUnlocked(bool beaten, bool ownsAReward, IReadOnlyCollection<uint> quests, Func<uint, bool> isComplete) =>
        beaten || ownsAReward || quests.Count == 0 || quests.Any(isComplete);
}

/// <summary>A stop of a card-farming route: an opponent, where they stand, and the cards still missing from them.</summary>
public sealed record TriadStop(uint Id, string Name, uint Territory, string Zone, Vector3 Position, IReadOnlyList<int> Missing);

/// <summary>Orders opponents into a route that keeps travel short, counting a card that several opponents give only once.</summary>
public static class TriadFarmPlan
{
    /// <summary>
    /// The current zone first, then the other zones by name; within a zone always the nearest next opponent. A card already on an
    /// earlier stop is taken off later ones, and stops left with nothing to win drop out. At most <paramref name="maxStops"/> stops.
    /// </summary>
    public static List<TriadStop> Order(IEnumerable<TriadStop> stops, uint currentTerritory, Vector3 start, int maxStops = int.MaxValue)
    {
        var zones = stops.GroupBy(s => s.Territory)
                         .OrderBy(g => g.Key == currentTerritory ? 0 : 1)
                         .ThenBy(g => g.First().Zone, StringComparer.OrdinalIgnoreCase);
        var route = new List<TriadStop>();
        var taken = new HashSet<int>();
        foreach (var zone in zones)
        {
            var left = zone.ToList();
            var here = zone.Key == currentTerritory ? start : left[0].Position;
            while (left.Count > 0)
            {
                var next = left.OrderBy(s => Vector3.DistanceSquared(s.Position, here)).First();
                left.Remove(next);
                here = next.Position;
                var missing = next.Missing.Where(taken.Add).ToList();
                if (missing.Count == 0) continue;
                route.Add(next with { Missing = missing });
                if (route.Count >= maxStops) return route;
            }
        }
        return route;
    }
}
