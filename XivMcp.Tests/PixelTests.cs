using XivMcp.Ui;

namespace XivMcp.Tests;

public class PixelTests
{
    /// <summary>A w×h image whose pixel (x, y) is RGBA (x, y, 0, 255).</summary>
    private static Pixels Gradient(int w, int h)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 4;
                data[o] = (byte)x; data[o + 1] = (byte)y; data[o + 3] = 255;
            }
        return new Pixels(w, h, data);
    }

    [Fact]
    public void Bgra_from_the_screen_becomes_opaque_rgba()
    {
        var p = Pixels.FromBgra(1, 1, [10, 20, 30, 0]);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, p.Rgba);
    }

    [Fact]
    public void Crop_keeps_the_part_inside_the_image()
    {
        var c = Gradient(10, 10).Crop(8, 7, 5, 5); // runs past the right and bottom edges
        Assert.Equal((2, 3), (c.Width, c.Height));
        Assert.Equal(new byte[] { 8, 7, 0, 255 }, c.Rgba[..4]);
    }

    [Fact]
    public void Crop_outside_the_image_is_refused()
    {
        Assert.Throws<ArgumentException>(() => Gradient(10, 10).Crop(20, 0, 5, 5));
    }

    [Fact]
    public void Downscale_keeps_the_aspect_and_averages_pixels()
    {
        var p = new Pixels(4, 2, [
            0, 0, 0, 255, 200, 200, 200, 255, 10, 10, 10, 255, 10, 10, 10, 255,
            0, 0, 0, 255, 200, 200, 200, 255, 10, 10, 10, 255, 10, 10, 10, 255,
        ]);
        var s = p.FitWidth(2);
        Assert.Equal((2, 1), (s.Width, s.Height));
        Assert.Equal(new byte[] { 100, 100, 100, 255, 10, 10, 10, 255 }, s.Rgba);
    }

    [Fact]
    public void Images_already_small_enough_are_left_alone()
    {
        var p = Gradient(10, 5);
        Assert.Same(p, p.FitWidth(10));
        Assert.Same(p, p.FitWidth(4000));
    }
}
