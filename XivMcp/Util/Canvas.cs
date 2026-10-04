using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace XivMcp.Util;

/// <summary>
/// Minimal RGBA canvas with PNG output — just enough to draw waymarks on a map without extra dependencies
/// (Dalamud's runtime has no System.Drawing, and image libraries would bloat the plugin).
/// </summary>
internal sealed class Canvas
{
    public readonly int Width, Height;
    private readonly byte[] rgba;

    public Canvas(int width, int height, uint background = 0xFF1E2230)
    {
        Width = width;
        Height = height;
        rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++) Put(i * 4, background);
    }

    /// <summary>A canvas holding existing pixels, such as a screenshot, to encode them as PNG.</summary>
    public Canvas(XivMcp.Ui.Pixels pixels)
    {
        Width = pixels.Width;
        Height = pixels.Height;
        rgba = pixels.Rgba;
    }

    private void Put(int offset, uint argb)
    {
        rgba[offset] = (byte)(argb >> 16);
        rgba[offset + 1] = (byte)(argb >> 8);
        rgba[offset + 2] = (byte)argb;
        rgba[offset + 3] = (byte)(argb >> 24);
    }

    /// <summary>Alpha-blends a colour onto a pixel.</summary>
    public void Blend(int x, int y, uint argb, float coverage = 1f)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;
        var a = ((argb >> 24) & 0xFF) / 255f * Math.Clamp(coverage, 0, 1);
        if (a <= 0) return;
        var o = (y * Width + x) * 4;
        // Signed arithmetic: (uint channel - byte) would wrap around for darker source colours.
        int r = (int)((argb >> 16) & 0xFF), g = (int)((argb >> 8) & 0xFF), b = (int)(argb & 0xFF);
        rgba[o] = (byte)Math.Clamp(rgba[o] + (r - rgba[o]) * a, 0, 255);
        rgba[o + 1] = (byte)Math.Clamp(rgba[o + 1] + (g - rgba[o + 1]) * a, 0, 255);
        rgba[o + 2] = (byte)Math.Clamp(rgba[o + 2] + (b - rgba[o + 2]) * a, 0, 255);
        rgba[o + 3] = (byte)Math.Max(rgba[o + 3], (byte)(a * 255));
    }

    /// <summary>Draws a BGRA source image region scaled into the whole canvas (bilinear).</summary>
    public void DrawImage(byte[] bgra, int srcWidth, int srcHeight, float srcX, float srcY, float srcW, float srcH)
    {
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var sx = srcX + (x + 0.5f) / Width * srcW - 0.5f;
            var sy = srcY + (y + 0.5f) / Height * srcH - 0.5f;
            if (sx < 0 || sy < 0 || sx > srcWidth - 1 || sy > srcHeight - 1) continue;
            int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(x0 + 1, srcWidth - 1), y1 = Math.Min(y0 + 1, srcHeight - 1);
            float fx = sx - x0, fy = sy - y0;
            float Sample(int c) =>
                Lerp(Lerp(bgra[(y0 * srcWidth + x0) * 4 + c], bgra[(y0 * srcWidth + x1) * 4 + c], fx),
                     Lerp(bgra[(y1 * srcWidth + x0) * 4 + c], bgra[(y1 * srcWidth + x1) * 4 + c], fx), fy);
            var a = (uint)Sample(3);
            var color = (a << 24) | ((uint)Sample(2) << 16) | ((uint)Sample(1) << 8) | (uint)Sample(0);
            Blend(x, y, color | 0xFF000000, a / 255f);
        }
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    public void FillCircle(float cx, float cy, float r, uint argb)
    {
        for (var y = (int)(cy - r - 1); y <= (int)(cy + r + 1); y++)
        for (var x = (int)(cx - r - 1); x <= (int)(cx + r + 1); x++)
        {
            var d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            Blend(x, y, argb, r - d + 0.5f);
        }
    }

    public void Ring(float cx, float cy, float r, float thickness, uint argb)
    {
        for (var y = (int)(cy - r - 2); y <= (int)(cy + r + 2); y++)
        for (var x = (int)(cx - r - 2); x <= (int)(cx + r + 2); x++)
        {
            var d = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            Blend(x, y, argb, thickness / 2 - MathF.Abs(d - r) + 0.5f);
        }
    }

    public void FillRect(float x0, float y0, float x1, float y1, uint argb)
    {
        for (var y = (int)y0; y < (int)MathF.Ceiling(y1); y++)
        for (var x = (int)x0; x < (int)MathF.Ceiling(x1); x++)
            Blend(x, y, argb);
    }

    public void StrokeRect(float x0, float y0, float x1, float y1, float t, uint argb)
    {
        FillRect(x0, y0, x1, y0 + t, argb);
        FillRect(x0, y1 - t, x1, y1, argb);
        FillRect(x0, y0, x0 + t, y1, argb);
        FillRect(x1 - t, y0, x1, y1, argb);
    }

    // 5x7 glyphs for waymark labels and small captions.
    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['A'] = [".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#"],
        ['B'] = ["####.", "#...#", "#...#", "####.", "#...#", "#...#", "####."],
        ['C'] = [".###.", "#...#", "#....", "#....", "#....", "#...#", ".###."],
        ['D'] = ["####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####."],
        ['1'] = ["..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###."],
        ['2'] = [".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####"],
        ['3'] = [".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###."],
        ['4'] = ["...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#."],
    };

    /// <summary>Draws a glyph centred at (cx, cy) with the given pixel size per glyph dot, with a dark outline.</summary>
    public void Glyph(char c, float cx, float cy, float dot, uint argb)
    {
        if (!Glyphs.TryGetValue(c, out var rows)) return;
        var x0 = cx - 2.5f * dot;
        var y0 = cy - 3.5f * dot;
        foreach (var (ox, oy, color) in new[] { (-1f, 0f, 0xC0000000u), (1f, 0f, 0xC0000000u), (0f, -1f, 0xC0000000u), (0f, 1f, 0xC0000000u), (0f, 0f, argb) })
            for (var r = 0; r < 7; r++)
            for (var col = 0; col < 5; col++)
                if (rows[r][col] == '#')
                    FillRect(x0 + col * dot + ox, y0 + r * dot + oy, x0 + (col + 1) * dot + ox, y0 + (r + 1) * dot + oy, color);
    }

    // ------------------------------------------------------------------ PNG

    public byte[] ToPng()
    {
        using var raw = new MemoryStream();
        for (var y = 0; y < Height; y++)
        {
            raw.WriteByte(0); // filter: none
            raw.Write(rgba, y * Width * 4, Width * 4);
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(z);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)Width);
        WriteBigEndian(header, 4, (uint)Height);
        header[8] = 8;  // bit depth
        header[9] = 6;  // colour type RGBA
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", compressed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBigEndian(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = CrcUpdate(CrcUpdate(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF; // CRC over type + data
        var crcBytes = new byte[4];
        WriteBigEndian(crcBytes, 0, crc);
        s.Write(crcBytes);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint CrcUpdate(uint crc, byte[] bytes)
    {
        foreach (var b in bytes) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
