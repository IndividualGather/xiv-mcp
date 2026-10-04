using System;
using System.Collections.Generic;

namespace XivMcp.Fishing;

/// <summary>One weather of a zone and its chance in percent (the WeatherRate sheet; a zone's rates add up to 100).</summary>
public sealed record WeatherRate(uint Weather, int Rate);

/// <summary>A weather period: 8 Eorzean hours of one weather, and the weather of the period before.</summary>
public sealed record WeatherPeriod(DateTimeOffset Start, DateTimeOffset End, uint Weather, uint Previous);

/// <summary>
/// The game's weather forecast. Weather is not random: it follows from the time and the zone's rates, so it can be computed for
/// any moment, as the game client does.
/// </summary>
public static class WeatherForecast
{
    /// <summary>The forecast number (0–99) of the weather period running at <paramref name="t"/>.</summary>
    public static int Target(DateTimeOffset t)
    {
        var unix = t.ToUnixTimeSeconds();
        var bell = unix / EorzeaTime.SecondsPerHour;
        var increment = (uint)((bell + 8 - bell % 8) % 24);
        var days = (uint)(unix / EorzeaTime.SecondsPerDay);
        var calcBase = unchecked(days * 100 + increment);
        var step1 = unchecked((calcBase << 11) ^ calcBase);
        var step2 = (step1 >> 8) ^ step1;
        return (int)(step2 % 100);
    }

    /// <summary>The weather for a forecast number: the first whose rates, added up, exceed it.</summary>
    public static uint Pick(IReadOnlyList<WeatherRate> rates, int target)
    {
        var sum = 0;
        foreach (var r in rates)
        {
            sum += r.Rate;
            if (target < sum) return r.Weather;
        }
        return rates.Count > 0 ? rates[^1].Weather : 0;
    }

    public static uint WeatherAt(IReadOnlyList<WeatherRate> rates, DateTimeOffset t) => Pick(rates, Target(t));

    /// <summary><paramref name="count"/> weather periods, starting with the one running at <paramref name="from"/>.</summary>
    public static IReadOnlyList<WeatherPeriod> Periods(IReadOnlyList<WeatherRate> rates, DateTimeOffset from, int count) =>
        Periods(t => WeatherAt(rates, t), from, count);

    /// <summary>
    /// <paramref name="count"/> weather periods from a weather source: the weather of the period starting at a moment (the game's
    /// own forecast, or <see cref="WeatherAt"/>).
    /// </summary>
    public static IReadOnlyList<WeatherPeriod> Periods(Func<DateTimeOffset, uint> weatherAt, DateTimeOffset from, int count)
    {
        var list = new List<WeatherPeriod>(count);
        var start = EorzeaTime.PeriodStart(from);
        var previous = weatherAt(start.AddSeconds(-EorzeaTime.PeriodSeconds));
        for (var i = 0; i < count; i++)
        {
            var weather = weatherAt(start);
            var end = start.AddSeconds(EorzeaTime.PeriodSeconds);
            list.Add(new WeatherPeriod(start, end, weather, previous));
            previous = weather;
            start = end;
        }
        return list;
    }
}
