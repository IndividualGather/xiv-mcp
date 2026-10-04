using System;

namespace XivMcp.Fishing;

/// <summary>
/// Eorzean time: an Eorzean hour lasts 175 real seconds, a day 70 minutes. The weather changes every 8 Eorzean hours (1,400 real
/// seconds), at 00:00, 08:00 and 16:00.
/// </summary>
public static class EorzeaTime
{
    public const int SecondsPerHour = 175;
    public const int SecondsPerDay = SecondsPerHour * 24;
    public const int PeriodSeconds = SecondsPerHour * 8;

    /// <summary>The Eorzean hour at a real moment, 0 to below 24, with the minutes as a fraction.</summary>
    public static double Hour(DateTimeOffset t)
    {
        var seconds = t.ToUnixTimeMilliseconds() / 1000.0;
        var intoDay = seconds % SecondsPerDay;
        if (intoDay < 0) intoDay += SecondsPerDay;
        return intoDay / SecondsPerHour;
    }

    /// <summary>The start of the weather period running at <paramref name="t"/>.</summary>
    public static DateTimeOffset PeriodStart(DateTimeOffset t)
    {
        var unix = t.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds(unix - (unix % PeriodSeconds + PeriodSeconds) % PeriodSeconds);
    }

    /// <summary>An Eorzean hour as a clock, such as "16:30".</summary>
    public static string Format(double hour)
    {
        var minutes = (int)Math.Round(hour * 60) % (24 * 60);
        return $"{minutes / 60:00}:{minutes % 60:00}";
    }
}
