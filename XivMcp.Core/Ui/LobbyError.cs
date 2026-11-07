using System.Linq;
using System.Text.RegularExpressions;

namespace XivMcp.Ui;

/// <summary>
/// Lobby error boxes: a lost connection (90002 and its kin) only needs OK and a new login; others (maintenance, a full world) stop it.
/// </summary>
public static class LobbyError
{
    /// <summary>Error codes the game shows after the connection dropped; logging in again is what the player would do.</summary>
    private static readonly string[] Disconnects = ["2002", "90001", "90002", "90006", "90007"];

    /// <summary>The error code in the box's text (the game shows it as a separate number), or null.</summary>
    public static string? Code(string text) => Regex.Matches(text, @"\b\d{4,5}\b").Select(m => m.Value).LastOrDefault();

    public static bool IsDisconnect(string text) => Code(text) is { } code && Disconnects.Contains(code);
}
