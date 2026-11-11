using System;

namespace XivMcp.Maps;

/// <summary>
/// A world's data center from the World sheet. Since the November 2026 patch the column the sheet definitions call
/// "DataCenter" holds a coarser group (Chaos for every European world, Aether for every North American one), and the
/// logical data center the lobby and the data center selection use sits in the column after it.
/// </summary>
public static class WorldDataCenter
{
    /// <param name="groupColumn">The value of the column named DataCenter.</param>
    /// <param name="nextColumn">The value of the column after it.</param>
    /// <param name="isDataCenter">Whether an id is a row of WorldDCGroupType with a name.</param>
    public static uint Pick(uint groupColumn, uint nextColumn, Func<uint, bool> isDataCenter) =>
        nextColumn != 0 && isDataCenter(nextColumn) ? nextColumn : groupColumn;
}
