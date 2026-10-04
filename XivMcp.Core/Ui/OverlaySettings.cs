using System;

namespace XivMcp.Ui;

public enum OverlayLayout { Minimal, Full }

/// <summary>The player's settings for the activity overlay (/xivmcp → Overlay). Stored with the plugin settings.</summary>
public sealed class OverlaySettings
{
    public const float MinScale = 0.6f, MaxScale = 2.5f, MinOpacity = 0.15f;

    /// <summary>Show the overlay while a tool call or job runs.</summary>
    public bool Enabled { get; set; } = true;

    public OverlayLayout Layout { get; set; } = OverlayLayout.Full;

    /// <summary>Size of text and spacing, 1 = the normal size.</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Opacity of the background (the text stays opaque).</summary>
    public float Opacity { get; set; } = 0.85f;

    /// <summary>The overlay can't be moved.</summary>
    public bool Locked { get; set; }

    /// <summary>Clicks go through the overlay to the game: it can't be moved and its buttons don't work.</summary>
    public bool ClickThrough { get; set; }

    /// <summary>The overlay's buttons (pause, resume, cancel a job) work.</summary>
    public bool Interactive { get; set; } = true;

    public bool ShowToolCalls { get; set; } = true;
    public bool ShowJobs { get; set; } = true;

    /// <summary>A tool call shows once it has run this long, so quick reads don't make the overlay flicker.</summary>
    public int ShowAfterMs { get; set; } = 400;

    /// <summary>How long a finished call or job stays on the overlay.</summary>
    public int LingerSeconds { get; set; } = 3;

    /// <summary>At most this many calls and jobs; the rest are counted.</summary>
    public int MaxItems { get; set; } = 4;

    public bool CanMove => !Locked && !ClickThrough;
    public bool CanClick => Interactive && !ClickThrough;

    public TimeSpan ShowAfter => TimeSpan.FromMilliseconds(ShowAfterMs);
    public TimeSpan Linger => TimeSpan.FromSeconds(LingerSeconds);

    /// <summary>Whether the overlay is shown: while the preview is on, or while a call or job it shows is active.</summary>
    public bool ShouldShow(bool preview, int calls, int jobs) =>
        preview || Enabled && (ShowToolCalls && calls > 0 || ShowJobs && jobs > 0);

    /// <summary>Brings values from an edited settings file back into range.</summary>
    public void Normalize()
    {
        Scale = Math.Clamp(float.IsFinite(Scale) ? Scale : 1f, MinScale, MaxScale);
        Opacity = Math.Clamp(float.IsFinite(Opacity) ? Opacity : 0.85f, MinOpacity, 1f);
        ShowAfterMs = Math.Clamp(ShowAfterMs, 0, 5000);
        LingerSeconds = Math.Clamp(LingerSeconds, 0, 30);
        MaxItems = Math.Clamp(MaxItems, 1, 10);
        if (!Enum.IsDefined(Layout)) Layout = OverlayLayout.Full;
    }
}
