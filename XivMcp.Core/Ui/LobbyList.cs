using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Ui;

public enum LobbyListVerdict { Loading, Found, OtherDataCenter, Missing }

/// <summary>
/// Judges the character list the lobby shows when logging into a character. The game can open at the list of another data center
/// (the one a character was visiting), which only the title screen's data center selection changes.
/// </summary>
public static class LobbyList
{
    /// <param name="found">Whether the target is in the list.</param>
    /// <param name="targetDataCenter">The target's data center (0: unknown).</param>
    /// <param name="listedDataCenters">The data center of each listed character's current world.</param>
    public static LobbyListVerdict Judge(bool found, uint targetDataCenter, IReadOnlyCollection<uint> listedDataCenters)
    {
        if (found) return LobbyListVerdict.Found;
        if (listedDataCenters.Count == 0) return LobbyListVerdict.Loading;
        if (targetDataCenter != 0 && !listedDataCenters.Contains(targetDataCenter)) return LobbyListVerdict.OtherDataCenter;
        return LobbyListVerdict.Missing;
    }
}
