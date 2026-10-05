using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XivMcp.Jobs;
using XivMcp.Mcp;
using XivMcp.Ui;
using XivMcp.Util;
using static XivMcp.Windows.ConfigWindow;
using JobState = XivMcp.Util.JobManager.JobState;
using StepState = XivMcp.Util.JobManager.StepState;

namespace XivMcp.Windows;

/// <summary>
/// The activity overlay: a small window that shows the tool calls and background jobs running right now, and only while something
/// runs. Layout, size, opacity, locking, click-through and its buttons are set in /xivmcp → Overlay (<see cref="OverlaySettings"/>).
/// </summary>
internal sealed class OverlayWindow : Window
{
    private const ImGuiWindowFlags BaseFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
                                               ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav |
                                               ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoCollapse;

    private readonly Plugin plugin;
    private readonly ConfigWindow settingsWindow;
    private readonly Dictionary<string, DateTime> cancelArmed = [];
    private readonly HashSet<string> expanded = [];

    /// <summary>Steps shown per job: a few around the current one, or up to this many when the job is clicked open.</summary>
    private const int CollapsedSteps = 3, ExpandedSteps = 12;

    private static readonly Vector4 Bright = new(0.93f, 0.94f, 0.96f, 1);
    private List<JobManager.Job> jobs = [];
    private IReadOnlyList<CallActivity> calls = [];
    private bool resetPosition;
    private int pushedStyles;

    /// <summary>Shows the overlay with sample content, to place it and try settings (from the Overlay tab).</summary>
    public bool Preview { get; set; }

    /// <summary>Whether the overlay is on screen this frame (the window stays open; <see cref="DrawConditions"/> hides it).</summary>
    public bool Shown { get; private set; }

    /// <summary>Where the overlay was drawn last, in game client pixels (for take_screenshot); null until it was shown.</summary>
    public (Vector2 Position, Vector2 Size)? Bounds { get; private set; }

    public OverlayWindow(Plugin plugin, ConfigWindow settingsWindow) : base("XIV MCP activity###XivMcpOverlay", BaseFlags)
    {
        this.plugin = plugin;
        this.settingsWindow = settingsWindow;
        // Always open: Dalamud only runs Update for open windows, and the overlay decides each frame whether to draw.
        IsOpen = true;
        RespectCloseHotkey = false;
        DisableWindowSounds = true;
        ShowCloseButton = false;
        AllowPinning = false;
        AllowClickthrough = false;
        // First use: on the left, below where other plugins put their bars. Afterwards ImGui remembers where the player put it.
        Position = DefaultPosition;
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    private static Vector2 DefaultPosition => new Vector2(40, 260) * Ui.Scale;

    private OverlaySettings Settings => plugin.Config.Overlay;

    /// <summary>Puts the overlay back near the top left on the next frame.</summary>
    public void ResetPosition() => resetPosition = true;

    // Runs every frame (the overlay is always open): start the control queue's frame, and decide whether there is anything to show.
    public override void Update()
    {
        settingsWindow.Controls.BeginFrame();
        IsOpen = true;
        var s = Settings;
        var now = DateTime.UtcNow;
        calls = s.ShowToolCalls ? plugin.Server.Activity.Visible(now, s.ShowAfter, s.Linger) : [];
        jobs = s.ShowJobs && plugin.Jobs is { } manager
            ? manager.All().Where(j => j.State is JobState.Running or JobState.Queued || now - j.UpdatedUtc <= Lingers(j, s)).ToList()
            : [];
        Shown = s.ShouldShow(Preview, calls.Count, jobs.Count);
    }

    public override bool DrawConditions() => Shown;

    /// <summary>
    /// How long a job that stopped running stays: finished ones for the player's setting; paused and pending ones at least 10 seconds,
    /// so they can be resumed or retried here right after pausing.
    /// </summary>
    private static TimeSpan Lingers(JobManager.Job job, OverlaySettings s) =>
        job.Finished ? s.Linger : TimeSpan.FromSeconds(Math.Max(10, s.LingerSeconds));

    public override void PreDraw()
    {
        var s = Settings;
        Flags = BaseFlags | (s.CanMove ? 0 : ImGuiWindowFlags.NoMove) | (s.ClickThrough ? ImGuiWindowFlags.NoInputs : 0);
        BgAlpha = s.Opacity;
        if (resetPosition)
        {
            Position = DefaultPosition;
            PositionCondition = ImGuiCond.Always;
            resetPosition = false;
        }
        else if (PositionCondition == ImGuiCond.Always)
        {
            // Applied once; from now on the player places it.
            Position = null;
            PositionCondition = ImGuiCond.FirstUseEver;
        }
        var k = s.Scale * Ui.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10, 8) * k);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8 * k);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1);
        ImGui.PushStyleColor(ImGuiCol.Border, Accent with { W = 0.35f });
        pushedStyles = 3;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(pushedStyles);
        ImGui.PopStyleColor();
        pushedStyles = 0;
    }

    public override void Draw()
    {
        var s = Settings;
        var k = s.Scale * Ui.Scale;
        Bounds = (ImGui.GetWindowPos(), ImGui.GetWindowSize());
        ImGui.SetWindowFontScale(s.Scale);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4) * k)
                                  .Push(ImGuiStyleVar.FramePadding, new Vector2(4, 2) * k);

        var shownJobs = Preview && jobs.Count == 0 && calls.Count == 0 ? [SampleJob()] : jobs;
        var shownCalls = Preview && jobs.Count == 0 && calls.Count == 0 ? [SampleCall()] : calls;
        var sample = !ReferenceEquals(shownJobs, jobs);
        var full = s.Layout == OverlayLayout.Full;
        var width = 300 * k;

        if (full)
        {
            ImGui.Dummy(new Vector2(width, 0)); // the full layout has a fixed width; auto-resize fits the height
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() - ImGui.GetStyle().ItemSpacing.Y);
            IconText(FontAwesomeIcon.SatelliteDish, Accent);
            ImGui.SameLine();
            ImGui.TextColored(Accent, "XIV MCP");
            ImGui.SameLine();
            ImGui.TextColored(Muted, Summary(shownJobs.Count, shownCalls.Count));
        }

        var max = s.MaxItems;
        var drawn = 0;
        foreach (var job in shownJobs)
        {
            if (drawn++ >= max) break;
            if (full) { Separator(width); DrawJobFull(job, width, sample); }
            else DrawJobMinimal(job, sample);
        }
        foreach (var call in shownCalls)
        {
            if (drawn++ >= max) break;
            if (full) { Separator(width); DrawCallFull(call, width); }
            else DrawCallMinimal(call);
        }
        var hidden = shownJobs.Count + shownCalls.Count - max;
        if (hidden > 0) ImGui.TextColored(Muted, $"+{hidden} more");

        if (Consent.Open.Count > 0)
        {
            IconText(FontAwesomeIcon.HandPaper, Amber);
            ImGui.SameLine();
            ImGui.TextColored(Amber, "Waiting for your approval");
        }
        if (Preview)
        {
            if (full) Separator(width);
            ImGui.TextColored(Muted, s.CanMove ? "Preview: drag to move" : s.ClickThrough ? "Preview: click-through is on" : "Preview: locked");
        }
        ImGui.SetWindowFontScale(1f);
    }

    private static string Summary(int jobs, int calls)
    {
        var parts = new List<string>();
        if (jobs > 0) parts.Add(jobs == 1 ? "1 job" : $"{jobs} jobs");
        if (calls > 0) parts.Add(calls == 1 ? "1 tool call" : $"{calls} tool calls");
        return string.Join(" · ", parts);
    }

    private static void Separator(float width)
    {
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(width, 0), ImGui.GetColorU32(new Vector4(1, 1, 1, 0.10f)), 1);
        ImGui.Dummy(new Vector2(0, 2));
    }

    // ---------------------------------------------------------------- jobs

    private void DrawJobFull(JobManager.Job job, float width, bool sample)
    {
        var now = DateTime.UtcNow;
        var color = JobColor(job.State);
        var start = ImGui.GetCursorPosX();
        var open = expanded.Contains(job.Id);
        using (ImRaii.Group())
        {
            IconText(JobIcon(job.State), job.State == JobState.Running ? color with { W = 0.55f + 0.45f * Pulse() } : color);
            ImGui.SameLine();
            ImGui.TextUnformatted(job.Name);
            if (Settings.CanClick)
            {
                ImGui.SameLine();
                IconText(open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown, Muted);
            }
        }
        ToggleOnClick(job, open);
        DrawJobButtons(job, start + width, sample);

        DrawStepBar(job, width);

        var elapsed = JobTimeline.Clock(JobTimeline.Elapsed(job.CreatedUtc, job.Finished ? job.UpdatedUtc : null, now));
        if (job.Finished)
        {
            ImGui.TextColored(color, job.State == JobState.Completed ? "Done" : job.State == JobState.Failed ? "Failed" : "Cancelled");
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"after {elapsed}");
            if (open) DrawStepRows(job, start, width, ExpandedSteps);
        }
        else
        {
            if (job.Current is { } cur)
            {
                ImGui.TextColored(Muted, $"Step {job.Steps.IndexOf(cur) + 1} of {job.Steps.Count}");
                RightAligned(elapsed, start + width, Muted);
            }
            DrawStepRows(job, start, width, open ? ExpandedSteps : CollapsedSteps);
            if (job.State == JobState.Queued) ImGui.TextColored(Muted, "Waiting for another job's step to finish");
        }
    }

    /// <summary>Clicking a job's header shows or hides all its steps (when the overlay takes clicks).</summary>
    private void ToggleOnClick(JobManager.Job job, bool open)
    {
        var pressed = settingsWindow.Controls.Consume($"overlay:job:{job.Id}:expand");
        if (Settings.CanClick && ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            Tooltip(open ? "Hide the steps" : "Show all steps");
            pressed |= ImGui.IsItemClicked();
        }
        if (!pressed) return;
        if (open) expanded.Remove(job.Id);
        else expanded.Add(job.Id);
    }

    /// <summary>
    /// A job's steps around the current one: an icon for its state, its note (or tool), and how long it took or has run; the
    /// current step stands out, with its latest progress below.
    /// </summary>
    private static void DrawStepRows(JobManager.Job job, float start, float width, int room)
    {
        var current = job.Current is { } c ? job.Steps.IndexOf(c) : -1;
        var (from, to) = JobTimeline.StepWindow(job.Steps.Count, current, room);
        var now = DateTime.UtcNow;
        if (from > 0) ImGui.TextColored(Muted, $"   … {from} earlier step{(from == 1 ? "" : "s")}");
        for (var i = from; i < to; i++)
        {
            var step = job.Steps[i];
            var isCurrent = i == current;
            var color = StepColor(step.State);
            IconText(StepIcon(step.State), step.State == StepState.Running ? color with { W = 0.55f + 0.45f * Pulse() } : color);
            ImGui.SameLine();
            var label = string.IsNullOrWhiteSpace(step.Note) ? step.Tool : step.Note!;
            var time = step.StartedUtc is { } began ? JobTimeline.Compact((step.FinishedUtc ?? now) - began) : "";
            var space = width - (ImGui.GetCursorPosX() - start) - ImGui.CalcTextSize(time).X - 12 * Ui.Scale;
            ImGui.TextColored(isCurrent ? Bright : step.State is StepState.Done or StepState.Skipped ? Muted : Bright with { W = 0.75f }, Fit(label, space));
            if (ImGui.IsItemHovered() && label != step.Tool) Tooltip(step.Tool);
            if (time.Length > 0) RightAligned(time, start + width, Muted);
            if (isCurrent && step.State == StepState.Running && JobTimeline.LatestProgress(job.Log, step.Id) is { } progress)
            {
                ImGui.Indent(18 * Ui.Scale);
                ImGui.PushTextWrapPos(start + width);
                ImGui.TextColored(Accent, progress);
                ImGui.PopTextWrapPos();
                ImGui.Unindent(18 * Ui.Scale);
            }
            else if (isCurrent && step.State == StepState.Failed && step.Error is { } error)
            {
                ImGui.Indent(18 * Ui.Scale);
                ImGui.PushTextWrapPos(start + width);
                ImGui.TextColored(Red, error);
                ImGui.PopTextWrapPos();
                ImGui.Unindent(18 * Ui.Scale);
            }
        }
        var more = job.Steps.Count - to;
        if (more > 0) ImGui.TextColored(Muted, $"   … {more} more step{(more == 1 ? "" : "s")}");
    }

    private static FontAwesomeIcon StepIcon(StepState s) => s switch
    {
        StepState.Done => FontAwesomeIcon.Check,
        StepState.Running => FontAwesomeIcon.Play,
        StepState.Failed => FontAwesomeIcon.Times,
        StepState.Skipped => FontAwesomeIcon.Forward,
        _ => FontAwesomeIcon.Circle,
    };

    /// <summary>The text, shortened with an ellipsis to fit <paramref name="room"/> pixels.</summary>
    private static string Fit(string text, float room)
    {
        if (room <= 0 || ImGui.CalcTextSize(text).X <= room) return text;
        var cut = text.Length;
        while (cut > 1 && ImGui.CalcTextSize(text[..cut] + "…").X > room) cut--;
        return text[..cut].TrimEnd() + "…";
    }

    private void DrawJobMinimal(JobManager.Job job, bool sample)
    {
        var color = JobColor(job.State);
        Dot(job.State == JobState.Running ? color with { W = 0.5f + 0.5f * Pulse() } : color);
        ImGui.SameLine();
        var open = expanded.Contains(job.Id);
        var rowStart = ImGui.GetCursorPosX();
        ImGui.TextUnformatted(job.Name);
        ToggleOnClick(job, open);
        ImGui.SameLine();
        var done = job.Steps.Count(st => st.State is StepState.Done or StepState.Skipped);
        if (job.Finished) ImGui.TextColored(color, job.State == JobState.Completed ? "done" : job.State.ToString().ToLowerInvariant());
        else
        {
            ImGui.TextColored(Muted, $"{done}/{job.Steps.Count}");
            if (job.Current is { } cur)
            {
                ImGui.SameLine();
                using (Ui.MonoFont()) ImGui.TextColored(StepColor(cur.State), cur.Tool);
            }
            ImGui.SameLine();
            ImGui.TextColored(Muted, JobTimeline.Clock(JobTimeline.Elapsed(job.CreatedUtc, null, DateTime.UtcNow)));
            ImGui.SameLine();
            DrawJobButtons(job, null, sample);
        }
        if (open) DrawStepRows(job, rowStart, 300 * Settings.Scale * Ui.Scale, ExpandedSteps);
    }

    /// <summary>Pause or resume, and cancel (a second click within 4 seconds confirms). Right-aligned at <paramref name="right"/> if given.</summary>
    private void DrawJobButtons(JobManager.Job job, float? right, bool sample)
    {
        if (!Settings.CanClick || job.Finished) return;
        var manager = plugin.Jobs;
        var running = job.State is JobState.Running or JobState.Queued;
        var failed = job.Current?.State == StepState.Failed;
        var armed = cancelArmed.TryGetValue(job.Id, out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(4);
        var size = ImGui.GetFrameHeight();
        if (right is { } r)
        {
            ImGui.SameLine();
            ImGui.SetCursorPosX(r - 3 * size - 2 * ImGui.GetStyle().ItemSpacing.X);
        }
        using var id = ImRaii.PushId($"ov-{job.Id}");
        if (ImGuiComponents.IconButton("##open", FontAwesomeIcon.ListUl) | settingsWindow.Controls.Consume($"overlay:job:{job.Id}:open"))
            if (!sample) settingsWindow.Show("Jobs", job.Id);
        Tooltip("Open this job in /xivmcp → Jobs: every step, its results and the log");
        ImGui.SameLine();
        var primary = running ? FontAwesomeIcon.Pause : failed ? FontAwesomeIcon.Redo : FontAwesomeIcon.Play;
        var primaryAction = running ? "pause" : failed ? "retry" : "resume";
        bool clicked;
        using (ImRaii.PushColor(ImGuiCol.Button, (running ? Amber : Green) with { W = 0.18f }))
            clicked = ImGuiComponents.IconButton("##primary", primary) | settingsWindow.Controls.Consume($"overlay:job:{job.Id}:{primaryAction}");
        Tooltip(running ? "Pause this job" : failed ? "Retry the failed step" : "Resume this job");
        ImGui.SameLine();
        bool cancel;
        using (ImRaii.PushColor(ImGuiCol.Button, Red with { W = armed ? 0.7f : 0.18f }))
            cancel = ImGuiComponents.IconButton("##cancel", FontAwesomeIcon.Stop) | settingsWindow.Controls.Consume($"overlay:job:{job.Id}:cancel");
        Tooltip(armed ? "Click again to cancel the job" : "Cancel this job (asks once more)");
        if (sample || manager is null) return;
        try
        {
            if (clicked)
            {
                if (running) manager.Pause(job.Id, "Paused from the overlay.");
                else if (failed) manager.Update(job.Id, "retry", null, resume: true);
                else manager.Resume(job.Id);
            }
            if (cancel)
            {
                if (!armed) cancelArmed[job.Id] = DateTime.UtcNow;
                else
                {
                    cancelArmed.Remove(job.Id);
                    manager.Cancel(job.Id);
                }
            }
        }
        catch (ToolException ex) { Svc.Log.Warning($"[MCP] {ex.Message}"); }
    }

    // ---------------------------------------------------------------- tool calls

    private static void DrawCallFull(CallActivity call, float width)
    {
        var start = ImGui.GetCursorPosX();
        var color = call.Running ? Accent : call.Failed ? Red : Green;
        IconText(call.Running ? FontAwesomeIcon.CircleNotch : call.Failed ? FontAwesomeIcon.TimesCircle : FontAwesomeIcon.CheckCircle,
                 call.Running ? color with { W = 0.5f + 0.5f * Pulse(3.2f) } : color);
        ImGui.SameLine();
        using (Ui.MonoFont()) ImGui.TextUnformatted(call.Tool);
        RightAligned(CallTime(call), start + width, call.Running ? Accent : Muted);
        ImGui.PushTextWrapPos(start + width);
        var about = ToolSummaries.For(call.Tool) ?? "A tool from another plugin.";
        ImGui.TextColored(Muted, call.Client is { Length: > 0 } client ? $"{about} · {client}" : about);
        ImGui.PopTextWrapPos();
    }

    private static void DrawCallMinimal(CallActivity call)
    {
        Dot(call.Running ? Accent with { W = 0.5f + 0.5f * Pulse(3.2f) } : call.Failed ? Red : Green);
        ImGui.SameLine();
        using (Ui.MonoFont()) ImGui.TextUnformatted(call.Tool);
        ImGui.SameLine();
        ImGui.TextColored(Muted, CallTime(call));
    }

    private static string CallTime(CallActivity call) =>
        JobTimeline.Clock((call.Finished ?? DateTime.UtcNow) - call.Started);

    // ---------------------------------------------------------------- helpers

    private static void Dot(Vector4 color)
    {
        var h = ImGui.GetTextLineHeight();
        var pos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddCircleFilled(pos + new Vector2(h * 0.35f, h / 2), h * 0.22f, ImGui.GetColorU32(color));
        ImGui.Dummy(new Vector2(h * 0.7f, h));
    }

    private static void RightAligned(string text, float right, Vector4 color)
    {
        ImGui.SameLine();
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), right - ImGui.CalcTextSize(text).X));
        ImGui.TextColored(color, text);
    }

    /// <summary>What the preview shows when nothing runs: a job on its third step and a tool call.</summary>
    private static JobManager.Job SampleJob()
    {
        var started = DateTime.UtcNow.AddSeconds(-95);
        JobManager.Step Step(string tool, StepState state, int from, int? to) => new()
        {
            Id = tool, Tool = tool, State = state, StartedUtc = from < 0 ? null : started.AddSeconds(from),
            FinishedUtc = to is { } t ? started.AddSeconds(t) : null,
        };
        return new JobManager.Job
        {
            Id = "preview", Name = "Craft and sell (preview)", State = JobState.Running, CreatedUtc = started,
            Steps = [Step("prepare_craft_plan", StepState.Done, 0, 4), Step("gather_until", StepState.Done, 4, 60),
                     Step("run_crafting_list", StepState.Running, 60, null), Step("sell_item", StepState.Queued, -1, null)],
            Log = ["00:00:00 run_crafting_list: 12 of 20 crafted"],
        };
    }

    private static CallActivity SampleCall() => new(0, "navigate_to", "your assistant", DateTime.UtcNow.AddSeconds(-12), null, false);
}
