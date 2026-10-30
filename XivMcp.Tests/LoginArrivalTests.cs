using System;
using XivMcp.Ui;

namespace XivMcp.Tests;

public class LoginArrivalTests
{
    private const ulong Shynxi = 1, Estelle = 2;
    private static readonly DateTime T0 = new(2026, 10, 30, 16, 35, 13, DateTimeKind.Utc);

    [Fact]
    public void The_target_in_game_is_done()
    {
        var arrival = new LoginArrival();
        Assert.Equal(LoginArrivalVerdict.Arrived, arrival.Judge(Estelle, Estelle, justLoggedIn: true, T0));
    }

    [Fact]
    public void Another_character_before_logging_in_is_logged_out_at_once()
    {
        var arrival = new LoginArrival();
        Assert.Equal(LoginArrivalVerdict.LogOut, arrival.Judge(Shynxi, Estelle, justLoggedIn: false, T0));
    }

    [Fact]
    public void Right_after_logging_in_the_previous_character_is_waited_out()
    {
        // The game still reported the previous character for a moment after the login; logging out then sent it back to the title.
        var arrival = new LoginArrival();
        Assert.Equal(LoginArrivalVerdict.Wait, arrival.Judge(Shynxi, Estelle, justLoggedIn: true, T0));
        Assert.Equal(LoginArrivalVerdict.Wait, arrival.Judge(Shynxi, Estelle, justLoggedIn: true, T0.AddSeconds(5)));
        Assert.Equal(LoginArrivalVerdict.Arrived, arrival.Judge(Estelle, Estelle, justLoggedIn: true, T0.AddSeconds(6)));
    }

    [Fact]
    public void Another_character_that_stays_is_logged_out()
    {
        var arrival = new LoginArrival();
        arrival.Judge(Shynxi, Estelle, justLoggedIn: true, T0);
        Assert.Equal(LoginArrivalVerdict.LogOut, arrival.Judge(Shynxi, Estelle, justLoggedIn: true, T0 + LoginArrival.Grace));
    }

    [Fact]
    public void Each_login_gets_its_own_wait()
    {
        var arrival = new LoginArrival();
        arrival.Judge(Shynxi, Estelle, justLoggedIn: true, T0);
        arrival.Reset();
        Assert.Equal(LoginArrivalVerdict.Wait, arrival.Judge(Shynxi, Estelle, justLoggedIn: true, T0.AddMinutes(1)));
    }
}
