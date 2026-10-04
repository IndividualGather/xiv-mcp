using System;

namespace XivMcp.Ui;

/// <summary>An RGBA image in memory, for screenshots: crop to a window, scale down to save the assistant's tokens.</summary>
public sealed class Pixels(int width, int height, byte[] rgba)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Rgba { get; } = rgba.Length == width * height * 4 ? rgba : throw new ArgumentException("Wrong size of pixel data.");

    /// <summary>Pixels as Windows captures them (blue, green, red, unused) as opaque RGBA.</summary>
    public static Pixels FromBgra(int width, int height, byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = 255;
        }
        return new Pixels(width, height, rgba);
    }

    /// <summary>The part of the image inside the rectangle (clipped to the image).</summary>
    public Pixels Crop(int x, int y, int w, int h)
    {
        var x0 = Math.Max(0, x);
        var y0 = Math.Max(0, y);
        var x1 = Math.Min(Width, x + w);
        var y1 = Math.Min(Height, y + h);
        if (x1 <= x0 || y1 <= y0) throw new ArgumentException("The area is outside the image.");
        var cw = x1 - x0;
        var ch = y1 - y0;
        var data = new byte[cw * ch * 4];
        for (var row = 0; row < ch; row++)
            Buffer.BlockCopy(Rgba, ((y0 + row) * Width + x0) * 4, data, row * cw * 4, cw * 4);
        return new Pixels(cw, ch, data);
    }

    /// <summary>Scaled down to at most <paramref name="maxWidth"/> pixels across (averaging pixels), keeping the aspect ratio.</summary>
    public Pixels FitWidth(int maxWidth)
    {
        if (maxWidth <= 0 || Width <= maxWidth) return this;
        var w = maxWidth;
        var h = Math.Max(1, (int)Math.Round((double)Height * w / Width));
        var data = new byte[w * h * 4];
        for (var oy = 0; oy < h; oy++)
        {
            var sy0 = oy * Height / h;
            var sy1 = Math.Max(sy0 + 1, (oy + 1) * Height / h);
            for (var ox = 0; ox < w; ox++)
            {
                var sx0 = ox * Width / w;
                var sx1 = Math.Max(sx0 + 1, (ox + 1) * Width / w);
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var o = (sy * Width + sx) * 4;
                        r += Rgba[o]; g += Rgba[o + 1]; b += Rgba[o + 2]; a += Rgba[o + 3]; n++;
                    }
                var d = (oy * w + ox) * 4;
                data[d] = (byte)(r / n); data[d + 1] = (byte)(g / n); data[d + 2] = (byte)(b / n); data[d + 3] = (byte)(a / n);
            }
        }
        return new Pixels(w, h, data);
    }
}
