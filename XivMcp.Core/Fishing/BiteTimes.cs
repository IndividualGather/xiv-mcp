using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Fishing;

/// <summary>How many seconds after the cast a fish bites: most bites fall between <see cref="Min"/> and <see cref="Max"/>.</summary>
public sealed record BiteWindow(double Min, double Max, double Median, int Samples)
{
    public bool Overlaps(BiteWindow other) => Min < other.Max && other.Min < Max;

    public string Describe() => $"{Min:0}–{Max:0} s";
}

public static class BiteTimes
{
    /// <summary>Reports needed before a window is given.</summary>
    public const int MinSamples = 5;

    /// <summary>The share of reports cut off at each end, so a few odd reports do not stretch the window.</summary>
    private const double Trim = 0.025;

    /// <summary>
    /// A window from reported bite times (whole seconds, rounded down, and how often each was reported), or null with too few reports.
    /// </summary>
    public static BiteWindow? Summarize(IEnumerable<(int Seconds, int Count)> reports)
    {
        var sorted = reports.Where(r => r.Count > 0).GroupBy(r => r.Seconds).Select(g => (Seconds: g.Key, Count: g.Sum(x => x.Count)))
                            .OrderBy(r => r.Seconds).ToList();
        var total = sorted.Sum(r => r.Count);
        if (total < MinSamples) return null;

        double At(double share)
        {
            var target = share * total;
            var seen = 0;
            foreach (var r in sorted)
            {
                seen += r.Count;
                if (seen > target) return r.Seconds;
            }
            return sorted[^1].Seconds;
        }

        var min = At(Trim);
        var max = At(1 - Trim) + 1;
        return new BiteWindow(min, max, At(0.5), total);
    }
}
