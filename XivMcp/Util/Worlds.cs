using Lumina.Excel.Sheets;
using XivMcp.Maps;

namespace XivMcp.Util;

/// <summary>A world's logical data center (see <see cref="WorldDataCenter"/> for why not World.DataCenter).</summary>
internal static class Worlds
{
    public static uint DataCenterId(World world) =>
        WorldDataCenter.Pick(world.DataCenter.RowId, world.Unknown0,
                             id => Svc.Data.GetExcelSheet<WorldDCGroupType>().GetRowOrDefault(id) is { } dc && !dc.Name.IsEmpty);

    public static WorldDCGroupType? DataCenter(World world) =>
        Svc.Data.GetExcelSheet<WorldDCGroupType>().GetRowOrDefault(DataCenterId(world));

    public static string DataCenterName(World? world) =>
        world is { } w ? DataCenter(w)?.Name.ExtractText() ?? "" : "";
}
