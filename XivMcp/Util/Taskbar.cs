using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace XivMcp.Util;

/// <summary>Flashes the game's taskbar button (Windows) until the game window comes to the front, e.g. while an approval waits.</summary>
internal static partial class Taskbar
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    private const uint FlashAll = 0x3;           // caption and taskbar button
    private const uint UntilForeground = 0xC;    // keep flashing until the window is brought to the front

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    /// <summary>Starts flashing if the game isn't the foreground window. Never throws.</summary>
    public static void Flash()
    {
        try
        {
            var window = Process.GetCurrentProcess().MainWindowHandle;
            if (window == IntPtr.Zero || GetForegroundWindow() == window) return;
            var info = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = window, Flags = FlashAll | UntilForeground };
            FlashWindowEx(ref info);
        }
        catch (Exception ex) { Svc.Log.Debug($"[MCP] Taskbar flash failed: {ex.Message}"); }
    }
}
