using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;

namespace XivMcp.Util;

/// <summary>Renders waymarks onto the game's own map texture for a duty/territory.</summary>
internal static class WaymarkMap
{
    public static readonly string[] Labels = ["A", "B", "C", "D", "1", "2", "3", "4"];

    // Waymark colours as in game: A/1 red, B/2 yellow, C/3 blue, D/4 purple.
    private static readonly uint[] Colors = [0xFFE8504B, 0xFFF0D048, 0xFF4AA8F0, 0xFFB861F0];

    public readonly record struct Marker(int Index, float X, float Y, float Z);

    public sealed record Rendered(byte[] Png, string MapName, float YalmsAcross, string Note);

    /// <summary>Renders the markers on the best fitting map of a territory. Returns null if the territory has no map texture.</summary>
    public static Rendered? Render(uint territoryId, IReadOnlyList<Marker> markers, int size = 640)
    {
        var map = PickMap(territoryId, markers);
        if (map is not { } m) return null;

        var id = m.Id.ExtractText();
        var tex = Svc.Data.GetFile<TexFile>($"ui/map/{id}/{id.Replace("/", "")}_m.tex");
        if (tex is null) return null;
        var texW = (int)tex.Header.Width;
        var texH = (int)tex.Header.Height;
        var bgra = tex.ImageData;
        var k = texW / 2048f; // map pixel space is 2048 wide regardless of texture size

        var points = markers.Select(mk => (mk, p: ToMapPixel(m, mk.X, mk.Z))).ToList();
        float cx, cy, span;
        if (points.Count > 0)
        {
            float minX = points.Min(p => p.p.X), maxX = points.Max(p => p.p.X), minY = points.Min(p => p.p.Y), maxY = points.Max(p => p.p.Y);
            cx = (minX + maxX) / 2;
            cy = (minY + maxY) / 2;
            // Marker spread plus margin, but at least 40 yalms across (map pixels per yalm = SizeFactor / 100).
            span = Math.Max(Math.Max(maxX - minX, maxY - minY) * 1.6f, 40 * m.SizeFactor / 100f);
        }
        else
        {
            cx = cy = 1024;
            span = 2048;
        }
        span = Math.Min(span, 2048);

        var canvas = new Canvas(size, size, 0xFF2A2620);
        canvas.DrawImage(bgra, texW, texH, (cx - span / 2) * k, (cy - span / 2) * k, span * k, span * k);

        var scale = size / span;
        var radius = Math.Clamp(size / 22f, 14, 30);
        foreach (var (mk, p) in points)
        {
            var x = (p.X - (cx - span / 2)) * scale;
            var y = (p.Y - (cy - span / 2)) * scale;
            var color = Colors[mk.Index % 4];
            if (mk.Index < 4)
            {
                canvas.FillCircle(x, y, radius, (color & 0x00FFFFFF) | 0x99000000);
                canvas.Ring(x, y, radius, 3, color);
            }
            else
            {
                canvas.FillRect(x - radius, y - radius, x + radius, y + radius, (color & 0x00FFFFFF) | 0x99000000);
                canvas.StrokeRect(x - radius, y - radius, x + radius, y + radius, 3, color);
            }
            canvas.Glyph(Labels[mk.Index][0], x, y, Math.Max(2, radius / 5f), 0xFFFFFFFF);
        }

        var yalms = span / (m.SizeFactor / 100f);
        var mapName = string.Join(" - ", new[] { Excel.Name(m.PlaceName), Excel.Name(m.PlaceNameSub) }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new Rendered(canvas.ToPng(), mapName, MathF.Round(yalms, 1),
            "North is up. A-D are circles, 1-4 squares (red, yellow, blue, purple).");
    }

    /// <summary>World X/Z to the 2048-pixel map space (same formula the game and WaymarkPresetPlugin use).</summary>
    public static (float X, float Y) ToMapPixel(Map map, float worldX, float worldZ) =>
        ((worldX + map.OffsetX) / 100f * map.SizeFactor + 1024, (worldZ + map.OffsetY) / 100f * map.SizeFactor + 1024);

    /// <summary>The territory's map whose area contains the most markers (sub-areas of a duty have separate maps).</summary>
    private static Map? PickMap(uint territoryId, IReadOnlyList<Marker> markers)
    {
        var maps = Svc.Data.GetExcelSheet<Map>().Where(m => m.TerritoryType.RowId == territoryId && !m.Id.IsEmpty).ToList();
        if (maps.Count == 0)
            return Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.Map.ValueNullable;
        if (markers.Count == 0)
            return Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(territoryId)?.Map.ValueNullable ?? maps[0];
        return maps
            .OrderByDescending(m => markers.Count(mk => ToMapPixel(m, mk.X, mk.Z) is var p && p.X is >= 0 and <= 2048 && p.Y is >= 0 and <= 2048))
            .ThenByDescending(m => m.SizeFactor)
            .First();
    }
}
