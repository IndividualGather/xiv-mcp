using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;
using XivMcp.Ui;

namespace XivMcp.Util;

/// <summary>
/// Screenshots of the game window, with plugin windows on it, through Windows' PrintWindow (which captures DirectX content via the
/// desktop compositor, even when other windows cover the game). Called off the game's thread: PrintWindow may wait for that thread.
/// </summary>
internal static class ScreenCapture
{
    public static async Task<Pixels> GameWindow(CancellationToken ct)
    {
        var hwnd = Process.GetCurrentProcess().MainWindowHandle;
        if (hwnd == IntPtr.Zero) throw new ToolException("The game window was not found.");
        if (IsIconic(hwnd)) throw new ToolException("The game is minimized, so there is nothing to capture. Restore its window first.");
        return await Task.Run(() => Capture(hwnd), ct).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }

    private static Pixels Capture(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var rect) || rect.Right <= 0 || rect.Bottom <= 0) throw new ToolException("The game window has no size.");
        int w = rect.Right, h = rect.Bottom;
        var screen = GetDC(IntPtr.Zero);
        var dc = CreateCompatibleDC(screen);
        var info = new BITMAPINFO { biSize = Marshal.SizeOf<BITMAPINFO>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 }; // top-down
        var bitmap = CreateDIBSection(dc, ref info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (bitmap == IntPtr.Zero) throw new ToolException("Could not create the screenshot bitmap.");
            var old = SelectObject(dc, bitmap);
            var ok = PrintWindow(hwnd, dc, PwClientOnly | PwRenderFullContent);
            SelectObject(dc, old);
            if (!ok) throw new ToolException("Windows could not capture the game window.");
            var bgra = new byte[w * h * 4];
            Marshal.Copy(bits, bgra, 0, bgra.Length);
            return Pixels.FromBgra(w, h, bgra);
        }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private const uint PwClientOnly = 1, PwRenderFullContent = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
