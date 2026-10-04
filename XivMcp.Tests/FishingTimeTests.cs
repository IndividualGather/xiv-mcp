using System;
using System.Linq;
using XivMcp.Fishing;

namespace XivMcp.Tests;

public class EorzeaTimeTests
{
    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    [Fact]
    public void An_eorzean_hour_is_175_real_seconds()
    {
        Assert.Equal(0, EorzeaTime.Hour(At(0)), 3);
        Assert.Equal(1, EorzeaTime.Hour(At(175)), 3);
        Assert.Equal(23.5, EorzeaTime.Hour(At(175 * 23 + 87)), 1);
        Assert.Equal(0, EorzeaTime.Hour(At(175 * 24)), 3); // a new day
    }

    [Fact]
    public void Weather_periods_last_eight_eorzean_hours()
    {
        Assert.Equal(At(1400), EorzeaTime.PeriodStart(At(2799)));
        Assert.Equal(At(2800), EorzeaTime.PeriodStart(At(2800)));
    }

    [Fact]
    public void Formats_as_a_clock()
    {
        Assert.Equal("16:30", EorzeaTime.Format(16.5));
        Assert.Equal("00:00", EorzeaTime.Format(24));
    }
}

public class WeatherForecastTests
{
    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    // A zone with Clear Skies (20 %), Fair Skies (30 %), Clouds (30 %) and Rain (20 %).
    private static readonly WeatherRate[] Rates = [new(1, 20), new(2, 30), new(3, 30), new(7, 20)];

    [Theory]
    // Values from the algorithm the game uses, as the community trackers implement it.
    [InlineData(0, 56)]
    [InlineData(1400, 12)]
    [InlineData(1700000000, 53)]
    [InlineData(1759600000, 9)]
    [InlineData(1759601400, 97)]
    [InlineData(2000000000, 87)]
    public void The_forecast_target_matches_the_game(long unix, int expected) =>
        Assert.Equal(expected, WeatherForecast.Target(At(unix)));

    [Fact]
    public void Picks_the_weather_whose_cumulative_rate_covers_the_target()
    {
        Assert.Equal(1u, WeatherForecast.Pick(Rates, 0));
        Assert.Equal(1u, WeatherForecast.Pick(Rates, 19));
        Assert.Equal(2u, WeatherForecast.Pick(Rates, 20));
        Assert.Equal(3u, WeatherForecast.Pick(Rates, 79));
        Assert.Equal(7u, WeatherForecast.Pick(Rates, 99));
    }

    [Fact]
    public void Periods_follow_each_other_and_know_the_weather_before()
    {
        var periods = WeatherForecast.Periods(Rates, At(1759600100), 3);
        Assert.Equal(3, periods.Count);
        Assert.Equal(At(1759599800), periods[0].Start); // the period that is running now
        Assert.Equal(periods[0].End, periods[1].Start);
        Assert.Equal(periods[0].Weather, periods[1].Previous);
        Assert.Equal(WeatherForecast.Pick(Rates, WeatherForecast.Target(At(1759599800 - 1400))), periods[0].Previous);
    }
}

public class WeatherSourceTests
{
    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);

    [Fact]
    public void Periods_can_come_from_any_weather_source()
    {
        // A source that alternates Rain and Clouds by period, as the game's own forecast could.
        uint Source(DateTimeOffset start) => start.ToUnixTimeSeconds() / EorzeaTime.PeriodSeconds % 2 == 0 ? 7u : 3u;
        var periods = WeatherForecast.Periods(Source, At(1400 * 10 + 5), 3);
        Assert.Equal([7u, 3u, 7u], periods.Select(p => p.Weather));
        Assert.Equal(3u, periods[0].Previous);
        Assert.Equal(7u, periods[1].Previous);
    }

    [Fact]
    public void Fish_windows_follow_the_weather_source()
    {
        uint Source(DateTimeOffset start) => start.ToUnixTimeSeconds() / EorzeaTime.PeriodSeconds % 2 == 0 ? 7u : 3u;
        // Rain after Clouds: every even period. The one running now is open already.
        var w = FishWindows.Next(new FishConditions(0, 24, [3], [7]), Source, At(1400 * 10 + 5), 2, TimeSpan.FromDays(1));
        Assert.Equal(At(1400 * 10 + 5), w[0].Start);
        Assert.Equal(At(1400 * 11), w[0].End);
        Assert.Equal(At(1400 * 12), w[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(1400), w[1].Length);
    }
}

public class FishWindowTests
{
    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);
    private static readonly WeatherRate[] Rates = [new(1, 20), new(2, 30), new(3, 30), new(7, 20)];
    private static readonly DateTimeOffset From = At(1759600100);

    [Fact]
    public void A_fish_without_conditions_is_always_up()
    {
        var w = FishWindows.Next(FishConditions.Always, Rates, From, 3, TimeSpan.FromDays(1));
        Assert.Single(w);
        Assert.Equal(From, w[0].Start);
        Assert.True(w[0].End >= From.AddDays(1));
    }

    [Fact]
    public void Time_windows_repeat_every_eorzean_day()
    {
        var w = FishWindows.Next(new FishConditions(16, 20, [], []), Rates, From, 3, TimeSpan.FromDays(1));
        Assert.Equal(3, w.Count);
        foreach (var window in w)
        {
            Assert.Equal(16, EorzeaTime.Hour(window.Start), 2);
            Assert.Equal(TimeSpan.FromSeconds(4 * 175), window.End - window.Start);
        }
        Assert.Equal(TimeSpan.FromSeconds(24 * 175), w[1].Start - w[0].Start);
    }

    [Fact]
    public void Windows_that_wrap_past_midnight_stay_one_window()
    {
        var w = FishWindows.Next(new FishConditions(22, 2, [], []), Rates, From, 2, TimeSpan.FromDays(1));
        Assert.All(w, window => Assert.Equal(TimeSpan.FromSeconds(4 * 175), window.End - window.Start));
    }

    [Fact]
    public void Weather_and_the_weather_before_must_both_match()
    {
        var c = new FishConditions(0, 24, [3], [7]); // rain after clouds
        var w = FishWindows.Next(c, Rates, From, 5, TimeSpan.FromDays(30));
        Assert.NotEmpty(w);
        foreach (var window in w)
        {
            var period = WeatherForecast.Periods(Rates, window.Start, 1)[0];
            Assert.Equal(7u, period.Weather);
            Assert.Equal(3u, period.Previous);
        }
    }

    [Fact]
    public void A_window_that_is_open_now_starts_now()
    {
        var hour = EorzeaTime.Hour(From);
        var w = FishWindows.Next(new FishConditions(Math.Floor(hour), Math.Floor(hour) + 2, [], []), Rates, From, 1, TimeSpan.FromDays(1));
        Assert.Equal(From, w[0].Start);
    }

    [Fact]
    public void Impossible_conditions_find_nothing()
    {
        var w = FishWindows.Next(new FishConditions(0, 24, [], [99]), Rates, From, 3, TimeSpan.FromDays(2));
        Assert.Empty(w);
    }
}

public class BiteTimeTests
{
    [Fact]
    public void Summarises_the_middle_of_the_reports()
    {
        // 2 outliers at 4 s and 33 s; most bites between 6 and 8 s.
        var b = BiteTimes.Summarize([(4, 2), (6, 268), (7, 2393), (8, 300), (33, 2)])!;
        Assert.Equal(6, b.Min);
        Assert.Equal(9, b.Max); // floored seconds: 8 means up to 9
        Assert.Equal(7, b.Median);
        Assert.Equal(2965, b.Samples);
    }

    [Fact]
    public void Too_few_reports_give_no_window()
    {
        Assert.Null(BiteTimes.Summarize([(10, 2)]));
        Assert.Null(BiteTimes.Summarize([]));
    }

    [Fact]
    public void Windows_overlap_when_they_share_seconds()
    {
        Assert.True(new BiteWindow(6, 9, 7, 100).Overlaps(new BiteWindow(8, 12, 10, 100)));
        Assert.False(new BiteWindow(6, 9, 7, 100).Overlaps(new BiteWindow(9, 12, 10, 100)));
    }
}
