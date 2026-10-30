using System;

namespace XivMcp.Ui;

public enum LoginArrivalVerdict { Arrived, Wait, LogOut }

/// <summary>
/// Judges who is in game while logging into a character. Right after a login the game can still report the previous character for a
/// moment; logging out then sends the new login back to the title screen. So after a login, another character only counts once it
/// stays for <see cref="Grace"/>.
/// </summary>
public sealed class LoginArrival
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

    private DateTime? otherSince;

    /// <param name="current">The character in game.</param>
    /// <param name="target">The character to log into.</param>
    /// <param name="justLoggedIn">Whether this login confirmed the target in the lobby.</param>
    public LoginArrivalVerdict Judge(ulong current, ulong target, bool justLoggedIn, DateTime now)
    {
        if (current == target) return LoginArrivalVerdict.Arrived;
        if (!justLoggedIn) return LoginArrivalVerdict.LogOut;
        otherSince ??= now;
        return now - otherSince < Grace ? LoginArrivalVerdict.Wait : LoginArrivalVerdict.LogOut;
    }

    /// <summary>Forgets the wait, for the next login.</summary>
    public void Reset() => otherSince = null;
}
