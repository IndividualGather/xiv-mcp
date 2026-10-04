using System;
using Dalamud.Interface.Utility;

namespace XivMcp.Windows;

/// <summary>
/// UI scale and fonts for XIV MCP's windows, through the plugin's own UiBuilder (Dalamud's recommended font handles) rather than
/// Dalamud's static helpers, which depend on Dalamud internals (<see cref="ScaleSource"/> can be replaced, e.g. for tests).
/// </summary>
internal static class Ui
{
    public static Func<float> ScaleSource { get; set; } = () => ImGuiHelpers.GlobalScale;

    public static float Scale => ScaleSource();

    public static IDisposable IconFont() => Svc.PluginInterface.UiBuilder.IconFontHandle.Push();

    public static IDisposable MonoFont() => Svc.PluginInterface.UiBuilder.MonoFontHandle.Push();
}
