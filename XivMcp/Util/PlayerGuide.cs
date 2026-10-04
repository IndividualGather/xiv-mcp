using System;
using Dalamud.Interface.ImGuiNotification;

namespace XivMcp.Util;

/// <summary>
/// Asks the player to do something XIV MCP can't do without a plugin (walk to a flag, teleport, open a shop): a chat line, a
/// notification and the flashing taskbar button, so they notice it even in another window. The asking tool then waits for them.
/// </summary>
internal static class PlayerGuide
{
    private static IActiveNotification? current;

    public static void Ask(string text) => Svc.Framework.RunOnFrameworkThread(() =>
    {
        Svc.Chat.Print($"[XIV MCP] {text}");
        current?.DismissNow();
        current = Svc.Notifications.AddNotification(new Notification
        {
            Title = "XIV MCP needs you",
            Content = text,
            MinimizedText = "XIV MCP needs you",
            Type = NotificationType.Info,
            InitialDuration = TimeSpan.FromSeconds(20),
            UserDismissable = true,
        });
        Taskbar.Flash();
    });

    /// <summary>Removes the notification once the player has done it (or the tool stopped waiting).</summary>
    public static void Done() => Svc.Framework.RunOnFrameworkThread(() =>
    {
        current?.DismissNow();
        current = null;
    });
}
