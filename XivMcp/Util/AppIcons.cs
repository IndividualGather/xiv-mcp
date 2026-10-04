using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Microsoft.Win32;
using XivMcp.Connect;

namespace XivMcp.Util;

/// <summary>
/// The real icons of the AI apps on this PC, for the Connect tab: Microsoft Store apps' own logo files, and for other apps the
/// program icon Windows shows in Explorer. Nothing is shipped with XIV MCP; apps that aren't installed have no icon here.
/// </summary>
internal static class AppIcons
{
    /// <summary>A loaded icon: a logo file (Dalamud caches those) or a texture made from a program's icon (owned here).</summary>
    private sealed record Icon(string? File, IDalamudTextureWrap? Texture);

    private static readonly Dictionary<string, Icon?> Cache = new();

    /// <summary>The icon for an app id, or null (not installed, or no icon found). Looked up once per app, then kept.</summary>
    public static IDalamudTextureWrap? Get(string appId)
    {
        // The installed app's own icon first; a logo that ships with XIV MCP (branding/apps) when it isn't installed.
        if (!Cache.TryGetValue(appId, out var icon)) Cache[appId] = icon = Find(appId) ?? Shipped(appId);
        // Logo files go through Dalamud's texture cache (it owns them); program icons were made here.
        if (icon?.File is { } file) return Svc.Textures.GetFromFile(file).GetWrapOrDefault();
        return icon?.Texture;
    }

    /// <summary>Forget everything (e.g. after installing an app), and free the textures made here.</summary>
    public static void Reset()
    {
        foreach (var icon in Cache.Values) icon?.Texture?.Dispose();
        Cache.Clear();
    }

    /// <summary>A logo next to the plugin (images/apps/&lt;id&gt;.png), for apps without an icon on this PC.</summary>
    private static Icon? Shipped(string appId)
    {
        var path = Path.Combine(Svc.PluginInterface.AssemblyLocation.DirectoryName ?? "", "images", "apps", $"{appId}.png");
        return File.Exists(path) ? new Icon(path, null) : null;
    }

    private static Icon? Find(string appId)
    {
        try
        {
            return appId switch
            {
                "claude-desktop" or "claude" => StoreLogo("Claude_") ?? ExeIcon(LocalApp("AnthropicClaude", "claude.exe")),
                "codex" => StoreLogo("OpenAI.Codex_") ?? StoreLogo("OpenAI.ChatGPT"),
                "copilot" => ClientInstaller.InstalledApp("GitHub Copilot") is { Length: > 0 } copilotExe ? ExeIcon(copilotExe) : null,
                "vscode" => ExeIcon(SchemeExe("vscode") ?? LocalApp(Path.Combine("Programs", "Microsoft VS Code"), "Code.exe")),
                "cursor" => ExeIcon(SchemeExe("cursor") ?? LocalApp(Path.Combine("Programs", "cursor"), "Cursor.exe")),
                // LM Studio's desktop app is now Bionic (its own link scheme and folder).
                "lmstudio" => ExeIcon(SchemeExe("bionic") ?? SchemeExe("lmstudio") ?? ProgramFile("Bionic", "Bionic.exe") ?? LocalApp(Path.Combine("Programs", "LM Studio"), "LM Studio.exe")),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Svc.Log.Debug(ex, $"[MCP] No icon for {appId}.");
            return null;
        }
    }

    // ---------------------------------------------------------------- Microsoft Store apps

    private const string PackagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>The folder of the installed Store package whose full name starts with <paramref name="prefix"/> (e.g. "Claude_").</summary>
    public static string? StorePackage(string prefix)
    {
        using var packages = Registry.CurrentUser.OpenSubKey(PackagesKey);
        if (packages is null) return null;
        foreach (var name in packages.GetSubKeyNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderDescending())
        {
            using var key = packages.OpenSubKey(name);
            if (key?.GetValue("PackageRootFolder") is string root && Directory.Exists(root)) return root;
        }
        return null;
    }

    private static Icon? StoreLogo(string prefix)
    {
        if (StorePackage(prefix) is not { } root) return null;
        var manifest = Path.Combine(root, "AppxManifest.xml");
        if (!File.Exists(manifest) || AppLogos.LogoFromManifest(File.ReadAllText(manifest)) is not { } logo) return null;
        var dir = Path.GetDirectoryName(Path.Combine(root, logo))!;
        var best = AppLogos.BestLogo(logo, Directory.EnumerateFiles(dir, "*.png"), 64);
        return best is null ? null : new Icon(Path.Combine(dir, best), null);
    }

    // ---------------------------------------------------------------- other apps

    private static string? SchemeExe(string scheme)
    {
        using var key = Registry.ClassesRoot.OpenSubKey($@"{scheme}\shell\open\command");
        var exe = AppLogos.ExeFromCommand(key?.GetValue(null) as string);
        return exe is not null && File.Exists(exe) ? exe : null;
    }

    private static string? ProgramFile(string folder, string exe)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), folder, exe);
        return File.Exists(path) ? path : null;
    }

    private static string? LocalApp(string folder, string exe)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), folder, exe);
        return File.Exists(path) ? path : null;
    }

    /// <summary>The program's icon as Explorer shows it, turned into a texture.</summary>
    private static Icon? ExeIcon(string? exe)
    {
        if (exe is null || ShellImage(exe, 64) is not { } image) return null;
        return new Icon(null, Svc.Textures.CreateFromRaw(RawImageSpecification.Rgba32(image.Width, image.Height), image.Rgba));
    }

    private sealed record Image(int Width, int Height, byte[] Rgba);

    /// <summary>Asks the Windows shell for a file's icon at <paramref name="size"/> pixels, as straight-alpha RGBA.</summary>
    private static Image? ShellImage(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);
        if (factory.GetImage(new Size { Cx = size, Cy = size }, IconOnly | BiggerSizeOk, out var bitmap) != 0 || bitmap == IntPtr.Zero) return null;
        try
        {
            GetObject(bitmap, Marshal.SizeOf<Bitmap>(), out var info);
            int w = info.Width, h = Math.Abs(info.Height);
            var header = new BitmapInfoHeader { Size = Marshal.SizeOf<BitmapInfoHeader>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            var pixels = new byte[w * h * 4];
            var dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, bitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }

            // BGRA → RGBA. The shell hands out premultiplied alpha; ImGui wants straight alpha.
            var premultiplied = true;
            for (var i = 0; i < pixels.Length && premultiplied; i += 4)
                premultiplied = pixels[i] <= pixels[i + 3] && pixels[i + 1] <= pixels[i + 3] && pixels[i + 2] <= pixels[i + 3];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                if (premultiplied && a is > 0 and < 255) { r = (byte)(r * 255 / a); g = (byte)(g * 255 / a); b = (byte)(b * 255 / a); }
                pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b;
            }
            return new Image(w, h, pixels);
        }
        finally { DeleteObject(bitmap); }
    }

    // ---------------------------------------------------------------- Win32

    private const int BiggerSizeOk = 0x1, IconOnly = 0x4;

    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Cx, Cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bitmap { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public ushort Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr obj, int size, out Bitmap bitmap);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
