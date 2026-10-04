using System;
using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Fishing;

/// <summary>
/// When a fish bites: from <see cref="StartHour"/> to <see cref="EndHour"/> Eorzean time (wrapping past midnight when the end is
/// earlier), in one of <see cref="Weathers"/>, right after one of <see cref="PreviousWeathers"/>. Empty sets mean any weather.
/// </summary>
public sealed record FishConditions(double StartHour, double EndHour, IReadOnlyCollection<uint> PreviousWeathers, IReadOnlyCollection<uint> Weathers)
{
    public static FishConditions Always { get; } = new(0, 24, [], []);

    public bool Timed => !(StartHour == 0 && EndHour == 24) && StartHour != EndHour;
    public bool Weathered => PreviousWeathers.Count > 0 || Weathers.Count > 0;
    public bool AnyTime => !Timed && !Weathered;

    /// <summary>The Eorzean hours within one weather period (starting at <paramref name="periodHour"/>) when the fish bites.</summary>
    internal IEnumerable<(double From, double To)> HoursWithin(double periodHour)
    {
        var periodEnd = periodHour + 8;
        if (!Timed)
        {
            yield return (periodHour, periodEnd);
            yield break;
        }
        // The window as ranges of a 48-hour line, so a window wrapping past midnight is one range.
        var end = EndHour > StartHour ? EndHour : EndHour + 24;
        foreach (var shift in new[] { -24.0, 0.0, 24.0 })
        {
            var from = Math.Max(StartHour + shift, periodHour);
            var to = Math.Min(end + shift, periodEnd);
            if (to > from) yield return (from, to);
        }
    }
}

/// <summary>A stretch of real time in which a fish can bite.</summary>
public sealed record FishWindow(DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Length => End - Start;
}

public static class FishWindows
{
    /// <summary>
    /// The next <paramref name="count"/> windows from <paramref name="from"/>, looking at most <paramref name="horizon"/> ahead. A
    /// window that is open at <paramref name="from"/> starts there. Back-to-back periods are joined into one window.
    /// </summary>
    public static IReadOnlyList<FishWindow> Next(FishConditions c, IReadOnlyList<WeatherRate> rates, DateTimeOffset from, int count, TimeSpan horizon) =>
        Next(c, t => WeatherForecast.WeatherAt(rates, t), from, count, horizon);

    /// <summary>The same, with the weather from a weather source (see <see cref="WeatherForecast.Periods(Func{DateTimeOffset, uint}, DateTimeOffset, int)"/>).</summary>
    public static IReadOnlyList<FishWindow> Next(FishConditions c, Func<DateTimeOffset, uint> weatherAt, DateTimeOffset from, int count, TimeSpan horizon)
    {
        var periods = (int)Math.Ceiling(horizon.TotalSeconds / EorzeaTime.PeriodSeconds) + 1;
        var windows = new List<FishWindow>();
        foreach (var p in WeatherForecast.Periods(weatherAt, from, periods))
        {
            if (c.Weathers.Count > 0 && !c.Weathers.Contains(p.Weather)) continue;
            if (c.PreviousWeathers.Count > 0 && !c.PreviousWeathers.Contains(p.Previous)) continue;
            var periodHour = Math.Round(EorzeaTime.Hour(p.Start));
            foreach (var (fromHour, toHour) in c.HoursWithin(periodHour))
            {
                var start = p.Start.AddSeconds((fromHour - periodHour) * EorzeaTime.SecondsPerHour);
                var end = p.Start.AddSeconds((toHour - periodHour) * EorzeaTime.SecondsPerHour);
                if (end <= from) continue;
                if (start < from) start = from;
                if (windows.Count > 0 && windows[^1].End >= start) windows[^1] = windows[^1] with { End = end };
                else windows.Add(new FishWindow(start, end));
            }
        }
        // The last window may continue past the horizon; with no conditions that is the only one.
        return windows.Take(count).ToList();
    }
}
