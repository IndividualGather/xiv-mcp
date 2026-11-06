using System.Text.RegularExpressions;

namespace XivMcp.Ui;

/// <summary>
/// Which chat and log messages count as the game's answer to something XIV MCP did (a vendor refusing to trade, an action that
/// can't be used here): system notices and the red error text only. Battle log lines ("... hits you for 123 damage."), chat and
/// NPC dialogue that happen to arrive at the same time are not the answer and are left out.
/// </summary>
public static class GameFeedback
{
    /// <summary>The "System Messages" channel.</summary>
    public const int SystemMessage = 57;

    /// <summary>The "Error Messages" channel (the red text in the middle of the screen is logged here).</summary>
    public const int ErrorMessage = 60;

    /// <summary>
    /// True for a system or error notice. The channel is the low seven bits of the chat type; the game sets the bits above it on
    /// battle messages to say who the message is about. Battle channels (damage 41 to effects lost 49, battle system 58) are left out.
    /// </summary>
    public static bool IsNotice(int chatType) => (chatType & 0x7F) is SystemMessage or ErrorMessage;

    /// <summary>The message text trimmed with runs of blanks collapsed, or null when nothing is left.</summary>
    public static string? Clean(string? text)
    {
        if (text is null) return null;
        var cleaned = Regex.Replace(text, @"\s+", " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }
}
