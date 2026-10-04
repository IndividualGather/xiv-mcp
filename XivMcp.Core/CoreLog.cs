using System;

namespace XivMcp;

/// <summary>Logging hooks for the core library; the plugin points them at Dalamud's log, tests leave them unset or capture them.</summary>
public static class CoreLog
{
    public static Action<string>? Information { get; set; }
    public static Action<string>? Warning { get; set; }
    public static Action<string>? Error { get; set; }
}
