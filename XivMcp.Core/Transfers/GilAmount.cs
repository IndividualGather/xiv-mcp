using System;
using System.Globalization;

namespace XivMcp.Transfers;

/// <summary>Gil amounts as players write them: "1200", "1,200", "15k", "1.5m", "50%" or "all".</summary>
public static class GilAmount
{
    /// <summary>The most gil the game keeps in one place (bags, a retainer or the company chest).</summary>
    public const long Cap = 999_999_999;

    /// <summary>
    /// The amount meant by <paramref name="text"/>, out of <paramref name="available"/>: what there is to move. <paramref name="room"/>
    /// is how much the receiving side can still take, if it is less than the cap.
    /// </summary>
    public static long Parse(string text, long available, long room = Cap)
    {
        var t = text.Trim().ToLowerInvariant().Replace(",", "").Replace("_", "");
        long amount;
        if (t == "all")
        {
            if (available <= 0) throw new FormatException("There is no gil to move.");
            amount = Math.Min(available, room);
        }
        else if (t.EndsWith('%'))
        {
            if (!double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent)) throw Unknown(text);
            if (percent is <= 0 or > 100) throw new FormatException("A percentage must be between 1% and 100%.");
            amount = (long)Math.Floor(available * percent / 100);
        }
        else
        {
            var factor = t.EndsWith('k') ? 1_000 : t.EndsWith('m') ? 1_000_000 : 1;
            var number = factor == 1 ? t : t[..^1];
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) throw Unknown(text);
            amount = (long)Math.Round(value * factor);
        }

        if (amount <= 0) throw new FormatException("The amount must be more than 0 gil.");
        if (amount > available) throw new FormatException($"There is only {available:N0} gil to move, not {amount:N0}.");
        if (amount > room) throw new FormatException($"That would go over {Cap:N0} gil; at most {room:N0} more fit.");
        return amount;
    }

    private static FormatException Unknown(string text) =>
        new($"Unknown gil amount '{text}'. Use a number (1200, 15k, 1.5m), a percentage (50%) or all.");
}
