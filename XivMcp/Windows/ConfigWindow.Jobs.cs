using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using XivMcp.Jobs;
using XivMcp.Mcp;
using XivMcp.Util;
using JobState = XivMcp.Util.JobManager.JobState;
using StepState = XivMcp.Util.JobManager.StepState;

namespace XivMcp.Windows;

/// <summary>
/// The Jobs tab: a card per job with its state, a progress bar of its steps, elapsed and working time, a timeline that shows where
/// the job is, the controls (pause, resume, cancel; retry or skip a failed step) and its log.
/// </summary>
internal sealed partial class ConfigWindow
{
    /// <summary>Jobs the player folded (active jobs start open) or opened (finished jobs start folded).</summary>
    private readonly HashSet<string> toggledJobs = [];

    /// <summary>Jobs whose earlier steps the player unfolded.</summary>
    private readonly HashSet<string> jobHistoryShown = [];

    /// <summary>Cancel asks for a second click within a few seconds: when the first one was.</summary>
    private readonly Dictionary<string, DateTime> cancelArmed = [];

    private static readonly TimeSpan CancelConfirmWindow = TimeSpan.FromSeconds(4);

    internal static Vector4 JobColor(JobState s) => s switch
    {
        JobState.Running => Accent,
        JobState.Queued => Cyan with { W = 0.85f },
        JobState.Paused or JobState.Pending => Amber,
        JobState.Completed => Green,
        JobState.Failed => Red,
        _ => Muted,
    };

    internal static FontAwesomeIcon JobIcon(JobState s) => s switch
    {
        JobState.Running => FontAwesomeIcon.Play,
        JobState.Queued => FontAwesomeIcon.HourglassHalf,
        JobState.Paused => FontAwesomeIcon.Pause,
        JobState.Pending => FontAwesomeIcon.ExclamationTriangle,
        JobState.Completed => FontAwesomeIcon.CheckCircle,
        JobState.Failed => FontAwesomeIcon.TimesCircle,
        _ => FontAwesomeIcon.Ban,
    };

    private static string JobStateText(JobState s) => s switch
    {
        JobState.Queued => "WAITING",
        _ => s.ToString().ToUpperInvariant(),
    };

    private static string JobStateTip(JobState s) => s switch
    {
        JobState.Running => "A step is running in the game right now.",
        JobState.Queued => "Waiting for its turn: one job runs a step at a time.",
        JobState.Paused => "Paused. Resume to continue with the current step; it runs again from its start.",
        JobState.Pending => "A step failed or was stopped. The job waits until the assistant (or you) retries or skips it, or you cancel it.",
        JobState.Completed => "Every step is done.",
        JobState.Failed => "The job ended with an error.",
        _ => "Cancelled. Its remaining steps were not run.",
    };

    internal static Vector4 StepColor(StepState s) => s switch
    {
        StepState.Done => Green,
        StepState.Running => Accent,
        StepState.Failed => Red,
        StepState.Interrupted => Amber,
        StepState.Skipped => Muted,
        _ => new Vector4(1, 1, 1, 0.35f),
    };

    private static string StepStateText(StepState s) => s switch
    {
        StepState.Done => "done",
        StepState.Running => "running",
        StepState.Failed => "failed",
        StepState.Interrupted => "stopped",
        StepState.Skipped => "skipped",
        _ => "up next",
    };

    /// <summary>A pulse between 0 and 1, for whatever is running right now.</summary>
    internal static float Pulse(float speed = 2.4f) => 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * speed);

    // ---------------------------------------------------------------- the tab

    private void DrawJobs()
    {
        var manager = plugin.Jobs;
        if (manager is null) return;
        var all = manager.All();
        var active = all.Where(j => !j.Finished).ToList();
        var finished = all.Where(j => j.Finished).ToList();

        ImGui.PushTextWrapPos();
        ImGui.TextColored(Muted, "Background jobs started by your assistant or a plugin: queues of tool calls that run here in the game, one after another, " +
                                 "for as long as they take. Closing your AI app does not stop them.");
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        DrawJobCounts(active, finished.Count);
        ImGui.Spacing();

        if (active.Count == 0) DrawNoActiveJobs();
        foreach (var job in active) DrawJobCard(manager, job);

        DrawStandingApprovals();

        if (finished.Count > 0)
        {
            ImGui.Spacing();
            Section(FontAwesomeIcon.History, $"Finished ({finished.Count})");
            ImGui.Spacing();
            foreach (var job in finished) DrawJobCard(manager, job);
        }
    }

    /// <summary>"1 running · 2 waiting · 5 finished", each in its state's colour.</summary>
    private static void DrawJobCounts(List<JobManager.Job> active, int finished)
    {
        var parts = new List<(string Text, Vector4 Color)>();
        void Add(int n, string text, Vector4 color) { if (n > 0) parts.Add(($"{n} {text}", color)); }
        Add(active.Count(j => j.State == JobState.Running), "running", Accent);
        Add(active.Count(j => j.State == JobState.Queued), "waiting", Cyan);
        Add(active.Count(j => j.State == JobState.Paused), "paused", Amber);
        Add(active.Count(j => j.State == JobState.Pending), "need attention", Amber);
        Add(finished, "finished", Muted);
        if (parts.Count == 0) return;
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Muted, "·");
                ImGui.SameLine();
            }
            ImGui.TextColored(parts[i].Color, parts[i].Text);
        }
    }

    private static void DrawNoActiveJobs()
    {
        var scale = Ui.Scale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X - 2 * scale;
        var pad = 14 * scale;
        ImGui.Dummy(new Vector2(0, pad - ImGui.GetStyle().ItemSpacing.Y));
        ImGui.Indent(pad);
        using (Ui.IconFont())
        {
            ImGui.SetWindowFontScale(1.6f);
            ImGui.TextColored(Muted with { W = 0.6f }, FontAwesomeIcon.Tasks.ToIconString());
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.SameLine(0, 12 * scale);
        using (ImRaii.Group())
        {
            ImGui.TextUnformatted("No jobs running");
            ImGui.PushTextWrapPos(start.X - ImGui.GetWindowPos().X + width - pad);
            ImGui.TextColored(Muted, "Ask your assistant for something that takes a while, such as \"craft my list, then sell the results\", and it can run it here as a job.");
            ImGui.PopTextWrapPos();
        }
        ImGui.Unindent(pad);
        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + pad);
        var dl = ImGui.GetWindowDrawList();
        dl.AddRect(start, end, ImGui.GetColorU32(Muted with { W = 0.3f }), 6 * scale, ImDrawFlags.None, 1.5f * scale);
        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 10 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    private void DrawStandingApprovals()
    {
        var approvals = Approvals.All().Where(a => a.Active).ToList();
        if (approvals.Count == 0) return;
        ImGui.Spacing();
        Section(FontAwesomeIcon.CheckCircle, "Standing spending approvals");
        Tooltip("Approvals you gave a job that buys repeatedly. Each purchase is taken from the amount until it runs out or expires.");
        ImGui.Spacing();
        foreach (var a in approvals)
        {
            using var aid = ImRaii.PushId(a.Id);
            var fraction = a.MaxAmount > 0 ? (float)a.Remaining / a.MaxAmount : 0;
            using (ImRaii.PushColor(ImGuiCol.PlotHistogram, Accent with { W = 0.75f }))
                ImGui.ProgressBar(fraction, new Vector2(160 * Ui.Scale, ImGui.GetTextLineHeight()), $"{a.Remaining:N0} / {a.MaxAmount:N0}");
            ImGui.SameLine();
            ImGui.TextUnformatted(Items.Name(a.CurrencyId));
            ImGui.SameLine();
            ImGui.TextColored(Muted, $"{a.Purpose} · until {a.ExpiresUtc.ToLocalTime():g}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Revoke")) Approvals.Revoke(a.Id);
        }
        ImGui.Spacing();
    }

    // ---------------------------------------------------------------- one job

    private void DrawJobCard(JobManager manager, JobManager.Job job)
    {
        using var id = ImRaii.PushId(job.Id);
        var scale = Ui.Scale;
        var now = DateTime.UtcNow;
        var color = JobColor(job.State);
        if (focusJob == job.Id)
        {
            // show_xivmcp_window asked for this job: open its card and scroll it into view.
            if (job.Finished) toggledJobs.Add(job.Id); else toggledJobs.Remove(job.Id);
            ImGui.SetScrollHereY(0);
            focusJob = null;
        }
        var open = toggledJobs.Contains(job.Id) == job.Finished; // active jobs start open, finished ones folded

        var pad = 10 * scale;
        var bar = 4 * scale;
        var width = ImGui.GetContentRegionAvail().X - 2 * scale;
        var inner = width - 2 * pad - bar;
        var start = ImGui.GetCursorScreenPos();

        // Content on the top channel, the card's frame and the timeline's lines beneath it.
        var dl = ImGui.GetWindowDrawList();
        dl.ChannelsSplit(3);
        dl.ChannelsSetCurrent(2);

        ImGui.Indent(pad + bar);
        ImGui.Dummy(new Vector2(0, pad - ImGui.GetStyle().ItemSpacing.Y));
        var left = ImGui.GetCursorPosX();
        var headerY = ImGui.GetCursorPosY();
        var controlsWidth = job.Finished ? 0 : JobControlsWidth(job);

        // Header: the whole row except the buttons folds and unfolds the card.
        ImGui.SetCursorPos(new Vector2(left, headerY));
        var headerHeight = ImGui.GetTextLineHeightWithSpacing() * 2;
        // Pressed by a click, or by press_xivmcp_control ("|" so both run every frame).
        if (ImGui.InvisibleButton("##fold", new Vector2(Math.Max(1, inner - controlsWidth - 8 * scale), headerHeight)) | Controls.Consume($"job:{job.Id}:fold"))
            if (!toggledJobs.Remove(job.Id)) toggledJobs.Add(job.Id);
        var headerHovered = ImGui.IsItemHovered();
        ImGui.SetCursorPos(new Vector2(left, headerY));

        using (Ui.IconFont())
        {
            ImGui.SetWindowFontScale(1.45f);
            var iconColor = job.State == JobState.Running ? color with { W = 0.55f + 0.45f * Pulse() } : color;
            ImGui.TextColored(iconColor, JobIcon(job.State).ToIconString());
            ImGui.SetWindowFontScale(1f);
        }
        ImGui.SameLine(0, 10 * scale);
        using (ImRaii.Group())
        {
            ImGui.TextColored(headerHovered ? new Vector4(1, 1, 1, 1) : new Vector4(0.92f, 0.93f, 0.95f, 1), job.Name);
            ImGui.SameLine();
            StateBadge(JobStateText(job.State), color);
            Tooltip(JobStateTip(job.State));
            var by = string.IsNullOrWhiteSpace(job.Client) ? "" : $"by {job.Client} · ";
            ImGui.TextColored(Muted, $"{by}started {job.CreatedUtc.ToLocalTime():t}");
        }

        if (!job.Finished)
        {
            ImGui.SetCursorPos(new Vector2(left + inner - controlsWidth, headerY));
            DrawJobControls(manager, job);
        }
        else
        {
            using (Ui.IconFont())
            {
                var chevron = (open ? FontAwesomeIcon.ChevronUp : FontAwesomeIcon.ChevronDown).ToIconString();
                ImGui.SetCursorPos(new Vector2(left + inner - ImGui.CalcTextSize(chevron).X, headerY + 4 * scale));
                ImGui.TextColored(Muted, chevron);
            }
        }
        ImGui.SetCursorPos(new Vector2(left, headerY + headerHeight));

        // Progress bar and figures.
        var doneSteps = job.Steps.Count(s => s.State is StepState.Done or StepState.Skipped);
        DrawStepBar(job, inner);
        DrawJobFigures(job, doneSteps, now);

        if (job.Reason is { } reason && job.State is not (JobState.Completed or JobState.Running) && reason != "Cancelled.")
        {
            ImGui.PushTextWrapPos(left + inner);
            ImGui.TextColored(job.State is JobState.Pending or JobState.Failed ? Amber : Muted, reason);
            ImGui.PopTextWrapPos();
        }

        if (open)
        {
            PermissionsPanel.Divider(inner, new Vector4(1, 1, 1, 0.10f));
            DrawTimeline(job, left, inner, now, dl);
            DrawJobLog(job, left, inner);
        }

        var contentBottom = ImGui.GetItemRectMax().Y;
        ImGui.Unindent(pad + bar);

        var end = new Vector2(start.X + width, contentBottom + pad);
        var rounding = 6 * scale;
        dl.ChannelsSetCurrent(0);
        var tint = job.Finished ? new Vector4(1, 1, 1, 0.025f) : color with { W = 0.06f };
        dl.AddRectFilled(start, end, ImGui.GetColorU32(tint), rounding);
        dl.AddRect(start, end, ImGui.GetColorU32(color with { W = job.Finished ? 0.25f : 0.45f }), rounding);
        dl.AddRectFilled(start, new Vector2(start.X + bar, end.Y), ImGui.GetColorU32(job.Finished ? color with { W = 0.55f } : color), rounding, ImDrawFlags.RoundCornersLeft);
        dl.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y + 8 * scale));
        ImGui.Dummy(Vector2.Zero);
    }

    /// <summary>A small filled label: "RUNNING".</summary>
    private static void StateBadge(string text, Vector4 color)
    {
        var scale = Ui.Scale;
        ImGui.SetWindowFontScale(0.8f);
        var size = ImGui.CalcTextSize(text);
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, (ImGui.GetTextLineHeight() / 0.8f - size.Y) / 2);
        var padding = new Vector2(6 * scale, 1.5f * scale);
        ImGui.GetWindowDrawList().AddRectFilled(pos - new Vector2(0, padding.Y), pos + size + new Vector2(2 * padding.X, padding.Y),
                                                ImGui.GetColorU32(color with { W = 0.22f }), 3 * scale);
        ImGui.SetCursorScreenPos(pos + new Vector2(padding.X, 0));
        ImGui.TextColored(color, text);
        ImGui.SetWindowFontScale(1f);
    }

    /// <summary>One segment per step in its state's colour (a plain bar for very long jobs); the running step pulses.</summary>
    internal static void DrawStepBar(JobManager.Job job, float width)
    {
        var scale = Ui.Scale;
        var height = 6 * scale;
        var pos = ImGui.GetCursorScreenPos() + new Vector2(0, 2 * scale);
        var dl = ImGui.GetWindowDrawList();
        var count = job.Steps.Count;
        var empty = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.08f));
        if (count == 0)
        {
            dl.AddRectFilled(pos, pos + new Vector2(width, height), empty, height / 2);
        }
        else if (count <= 40)
        {
            var gap = 3 * scale;
            var segment = (width - gap * (count - 1)) / count;
            for (var i = 0; i < count; i++)
            {
                var s = job.Steps[i];
                var a = pos + new Vector2(i * (segment + gap), 0);
                var c = s.State switch
                {
                    StepState.Queued => new Vector4(1, 1, 1, 0.08f),
                    StepState.Running => Accent with { W = 0.45f + 0.55f * Pulse(3.2f) },
                    StepState.Skipped => Muted with { W = 0.45f },
                    var other => StepColor(other) with { W = 0.85f },
                };
                dl.AddRectFilled(a, a + new Vector2(segment, height), ImGui.GetColorU32(c), 2 * scale);
            }
        }
        else
        {
            var done = job.Steps.Count(s => s.State is StepState.Done or StepState.Skipped);
            dl.AddRectFilled(pos, pos + new Vector2(width, height), empty, height / 2);
            dl.AddRectFilled(pos, pos + new Vector2(width * done / count, height), ImGui.GetColorU32(JobColor(job.State) with { W = 0.85f }), height / 2);
        }
        ImGui.Dummy(new Vector2(width, height + 6 * scale));
    }

    /// <summary>"3 / 7 steps", "Elapsed 12:34", "Working 10:02", "Now: navigate_to".</summary>
    private static void DrawJobFigures(JobManager.Job job, int doneSteps, DateTime now)
    {
        var scale = Ui.Scale;
        var elapsed = JobTimeline.Elapsed(job.CreatedUtc, job.Finished ? job.UpdatedUtc : null, now);
        var working = JobTimeline.Working(job.Steps.Select(Timing), now);

        Figure(FontAwesomeIcon.ListOl, $"{doneSteps} / {job.Steps.Count} steps", "Steps done (or skipped) of all steps.");
        ImGui.SameLine(0, 16 * scale);
        Figure(FontAwesomeIcon.Stopwatch, JobTimeline.Clock(elapsed), job.Finished ? "How long the job took, from start to end, pauses included." : "Time since the job started, pauses included.");
        ImGui.SameLine(0, 16 * scale);
        Figure(FontAwesomeIcon.Cog, JobTimeline.Clock(working), "Time spent running steps, without pauses and waiting.");
        if (!job.Finished && job.Current is { } cur)
        {
            ImGui.SameLine(0, 16 * scale);
            var index = job.Steps.IndexOf(cur) + 1;
            Figure(cur.State == StepState.Running ? FontAwesomeIcon.Play : FontAwesomeIcon.MapMarkerAlt, $"Step {index}:", "Where the job is now.");
            ImGui.SameLine(0, 4 * scale);
            using (Ui.MonoFont()) ImGui.TextColored(StepColor(cur.State), cur.Tool);
        }

        static void Figure(FontAwesomeIcon icon, string text, string tip)
        {
            using (ImRaii.Group())
            {
                IconText(icon, Muted);
                ImGui.SameLine(0, 5 * Ui.Scale);
                ImGui.TextUnformatted(text);
            }
            Tooltip(tip);
        }
    }

    internal static StepTiming Timing(JobManager.Step s) =>
        new(s.StartedUtc, s.State == StepState.Running ? null : s.FinishedUtc);

    // ---------------------------------------------------------------- controls

    private float JobControlsWidth(JobManager.Job job)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        return JobButtons(job).Sum(b => ButtonWidth(b.Icon, b.Label) + spacing) - spacing;
    }

    private static float ButtonWidth(FontAwesomeIcon icon, string label)
    {
        float iconWidth;
        using (Ui.IconFont()) iconWidth = ImGui.CalcTextSize(icon.ToIconString()).X;
        return iconWidth + ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetStyle().ItemInnerSpacing.X;
    }

    private enum JobAction { Pause, Resume, Retry, Skip, Cancel }

    private IEnumerable<(JobAction Action, FontAwesomeIcon Icon, string Label, Vector4 Color, string Tip)> JobButtons(JobManager.Job job)
    {
        var failed = job.Current?.State == StepState.Failed;
        if (job.State is JobState.Running or JobState.Queued)
            yield return (JobAction.Pause, FontAwesomeIcon.Pause, "Pause", Amber, "Pauses the job. A running step is stopped safely and runs again from its start when you resume.");
        else if (failed)
        {
            yield return (JobAction.Retry, FontAwesomeIcon.Redo, "Retry", Green, "Runs the failed step again and continues the job.");
            yield return (JobAction.Skip, FontAwesomeIcon.Forward, "Skip", Muted, "Skips the failed step and continues with the next one.");
        }
        else
            yield return (JobAction.Resume, FontAwesomeIcon.Play, "Resume", Green, "Continues the job with its current step.");

        var armed = cancelArmed.TryGetValue(job.Id, out var at) && DateTime.UtcNow - at < CancelConfirmWindow;
        yield return (JobAction.Cancel, FontAwesomeIcon.Stop, armed ? "Really cancel?" : "Cancel", Red,
                      armed ? "Click again to cancel the job. Its remaining steps will not run." : "Cancels the job: its remaining steps will not run. Asks once more.");
    }

    private void DrawJobControls(JobManager manager, JobManager.Job job)
    {
        var first = true;
        foreach (var (action, icon, label, color, tip) in JobButtons(job).ToList())
        {
            if (!first) ImGui.SameLine();
            first = false;
            bool clicked;
            using (ImRaii.PushColor(ImGuiCol.Button, color with { W = action == JobAction.Cancel && label != "Cancel" ? 0.55f : 0.16f })
                         .Push(ImGuiCol.ButtonHovered, color with { W = 0.4f })
                         .Push(ImGuiCol.ButtonActive, color with { W = 0.6f })
                         .Push(ImGuiCol.Text, action == JobAction.Skip ? new Vector4(0.9f, 0.9f, 0.92f, 1) : color with { W = 1 }))
                clicked = ImGuiComponents.IconButtonWithText(icon, label) | Controls.Consume($"job:{job.Id}:{action.ToString().ToLowerInvariant()}");
            Tooltip(tip);
            if (!clicked) continue;
            try
            {
                switch (action)
                {
                    case JobAction.Pause: manager.Pause(job.Id, "Paused in game."); break;
                    case JobAction.Resume: manager.Resume(job.Id); break;
                    case JobAction.Retry: manager.Update(job.Id, "retry", null, resume: true); break;
                    case JobAction.Skip: manager.Update(job.Id, "skip", null, resume: true); break;
                    case JobAction.Cancel:
                        if (label == "Cancel") cancelArmed[job.Id] = DateTime.UtcNow;
                        else
                        {
                            cancelArmed.Remove(job.Id);
                            manager.Cancel(job.Id);
                        }
                        break;
                }
            }
            catch (ToolException ex) { Svc.Log.Warning($"[MCP] {ex.Message}"); }
        }
    }

    // ---------------------------------------------------------------- timeline

    /// <summary>
    /// The steps as a vertical timeline: a node per step (check, pulsing dot, cross, …) joined by a line that is solid up to where the
    /// job is. Each row shows the tool, what it does, its state and how long it took; hover for its arguments and result.
    /// </summary>
    private void DrawTimeline(JobManager.Job job, float left, float inner, DateTime now, ImDrawListPtr dl)
    {
        var scale = Ui.Scale;
        var current = job.Current is { } c ? job.Steps.IndexOf(c) : -1;
        var hidden = jobHistoryShown.Contains(job.Id) ? 0 : JobTimeline.HiddenBefore(job.Steps.Count, current);
        var gutter = 22 * scale;
        var radius = 5.5f * scale;
        var nodes = new List<(Vector2 Center, bool Reached)>();

        if (hidden > 0)
        {
            var rowTop = ImGui.GetCursorScreenPos();
            ImGui.SetCursorPosX(left + gutter);
            ImGui.TextColored(Muted, $"{hidden} earlier step{(hidden == 1 ? "" : "s")}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Show") | Controls.Consume($"job:{job.Id}:show-earlier")) jobHistoryShown.Add(job.Id);
            nodes.Add((new Vector2(rowTop.X + gutter / 2 - 2 * scale, rowTop.Y + ImGui.GetTextLineHeight() / 2 + 2 * scale), true));
            dl.ChannelsSetCurrent(2);
            for (var k = -1; k <= 1; k++)
                dl.AddCircleFilled(nodes[^1].Center + new Vector2(0, k * 4 * scale), 1.5f * scale, ImGui.GetColorU32(Muted));
            ImGui.Dummy(new Vector2(0, 2 * scale));
        }
        else if (jobHistoryShown.Contains(job.Id) && JobTimeline.HiddenBefore(job.Steps.Count, current) > 0)
        {
            ImGui.SetCursorPosX(left + gutter);
            if (ImGui.SmallButton("Hide earlier steps") | Controls.Consume($"job:{job.Id}:hide-earlier")) jobHistoryShown.Remove(job.Id);
        }

        for (var i = hidden; i < job.Steps.Count; i++)
        {
            var step = job.Steps[i];
            using var sid = ImRaii.PushId(i);
            var rowTop = ImGui.GetCursorScreenPos();
            var center = new Vector2(rowTop.X + gutter / 2 - 2 * scale, rowTop.Y + ImGui.GetTextLineHeight() / 2 + 1 * scale);
            nodes.Add((center, step.State is not StepState.Queued));
            DrawNode(dl, center, radius, step.State);

            ImGui.SetCursorPosX(left + gutter);
            using (ImRaii.Group())
            {
                // Line 1: number, tool, state, and the duration on the right.
                var isCurrent = i == current && !job.Finished;
                ImGui.TextColored(Muted, $"{i + 1}.");
                ImGui.SameLine();
                using (Ui.MonoFont())
                    ImGui.TextColored(step.State == StepState.Queued ? new Vector4(0.8f, 0.81f, 0.85f, 1) : isCurrent ? StepColor(step.State) : new Vector4(0.92f, 0.93f, 0.95f, 1), step.Tool);
                ImGui.SameLine();
                ImGui.TextColored(StepColor(step.State) with { W = step.State == StepState.Queued ? 0.7f : 1 }, StepStateText(step.State));
                if (step.Attempts > 1)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Muted, $"· attempt {step.Attempts}");
                }
                var duration = StepDuration(step, now);
                if (duration is not null)
                {
                    var w = ImGui.CalcTextSize(duration).X;
                    ImGui.SameLine();
                    ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(), left + inner - w));
                    ImGui.TextColored(step.State == StepState.Running ? Accent : Muted, duration);
                }

                // Line 2: what the step is for.
                ImGui.PushTextWrapPos(left + inner);
                var about = step.Note ?? ToolSummaries.For(step.Tool);
                if (about is not null) ImGui.TextColored(Muted, about);
                if (step.State == StepState.Running && JobTimeline.LatestProgress(job.Log, step.Id) is { } progress)
                {
                    IconText(FontAwesomeIcon.AngleRight, Accent);
                    ImGui.SameLine(0, 5 * scale);
                    ImGui.TextColored(Accent, progress);
                }
                if (step.Error is { } error && step.State is StepState.Failed or StepState.Interrupted)
                    ImGui.TextColored(step.State == StepState.Failed ? Red : Amber, error);
                ImGui.PopTextWrapPos();
            }
            StepTooltip(step);
            ImGui.Dummy(new Vector2(0, 3 * scale));
        }

        // The line between the nodes: solid where the job has been, faint ahead.
        dl.ChannelsSetCurrent(1);
        for (var k = 1; k < nodes.Count; k++)
        {
            var (a, _) = nodes[k - 1];
            var (b, reached) = nodes[k];
            var col = reached ? new Vector4(1, 1, 1, 0.28f) : new Vector4(1, 1, 1, 0.09f);
            dl.AddLine(a + new Vector2(0, radius + 2 * scale), b - new Vector2(0, radius + 2 * scale), ImGui.GetColorU32(col), 2 * scale);
        }
        dl.ChannelsSetCurrent(2);
    }

    private static void DrawNode(ImDrawListPtr dl, Vector2 center, float radius, StepState state)
    {
        var scale = Ui.Scale;
        var color = StepColor(state);
        switch (state)
        {
            case StepState.Queued:
                dl.AddCircle(center, radius, ImGui.GetColorU32(color), 20, 1.5f * scale);
                break;
            case StepState.Running:
                // A ring that grows and fades around a solid dot.
                var t = (float)(ImGui.GetTime() % 1.4) / 1.4f;
                dl.AddCircle(center, radius + t * 6 * scale, ImGui.GetColorU32(color with { W = 0.7f * (1 - t) }), 24, 2 * scale);
                dl.AddCircleFilled(center, radius, ImGui.GetColorU32(color));
                break;
            default:
                dl.AddCircleFilled(center, radius, ImGui.GetColorU32(color with { W = state == StepState.Skipped ? 0.5f : 0.95f }));
                var icon = state switch
                {
                    StepState.Done => FontAwesomeIcon.Check,
                    StepState.Failed => FontAwesomeIcon.Times,
                    StepState.Interrupted => FontAwesomeIcon.Pause,
                    _ => FontAwesomeIcon.Forward,
                };
                using (Ui.IconFont())
                {
                    ImGui.SetWindowFontScale(0.55f);
                    var glyph = icon.ToIconString();
                    var size = ImGui.CalcTextSize(glyph);
                    dl.AddText(center - size / 2, ImGui.GetColorU32(new Vector4(0.08f, 0.08f, 0.1f, 1)), glyph);
                    ImGui.SetWindowFontScale(1f);
                }
                break;
        }
    }

    /// <summary>How long a step took ("took 3m 12s"), or how long it has been running ("1:23").</summary>
    private static string? StepDuration(JobManager.Step step, DateTime now) => step switch
    {
        { State: StepState.Running, StartedUtc: { } s } => JobTimeline.Clock(now - s),
        { State: StepState.Done or StepState.Failed or StepState.Interrupted, StartedUtc: { } s, FinishedUtc: { } f } when f >= s => f - s < TimeSpan.FromSeconds(1) ? "<1s" : JobTimeline.Compact(f - s),
        _ => null,
    };

    /// <summary>The step's arguments and result, for the curious.</summary>
    private static void StepTooltip(JobManager.Step step)
    {
        if (!ImGui.IsItemHovered()) return;
        using var tt = ImRaii.Tooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 34);
        ImGui.TextColored(Muted, $"Step id: {step.Id}");
        ImGui.TextColored(Muted, "Arguments");
        using (Ui.MonoFont()) ImGui.TextUnformatted(ToolText.Truncate(step.Args.Count == 0 ? "none" : step.Args.ToJsonString(), 400));
        if (step.Result is { } result)
        {
            ImGui.TextColored(Muted, "Result");
            using (Ui.MonoFont()) ImGui.TextUnformatted(ToolText.Truncate(result.ToJsonString(), 600));
        }
        if (step.StartedUtc is { } started) ImGui.TextColored(Muted, $"Started {started.ToLocalTime():T}" + (step.FinishedUtc is { } f && step.State != StepState.Running ? $", ended {f.ToLocalTime():T}" : ""));
        ImGui.PopTextWrapPos();
    }

    // ---------------------------------------------------------------- log

    private void DrawJobLog(JobManager.Job job, float left, float inner)
    {
        if (job.Log.Count == 0) return;
        ImGui.Dummy(new Vector2(0, 2 * Ui.Scale));
        using var node = ImRaii.TreeNode($"Log ({job.Log.Count})##log");
        if (!node) return;
        var lines = job.Log.TakeLast(40).ToList();
        var height = Math.Min(lines.Count, 10) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0.18f));
        using var child = ImRaii.Child("##logscroll", new Vector2(left + inner - ImGui.GetCursorPosX(), height), false);
        if (!child) return;
        ImGui.PushTextWrapPos();
        foreach (var line in lines)
        {
            var (time, text) = JobTimeline.SplitLog(line);
            using (Ui.MonoFont()) ImGui.TextColored(Muted with { W = 0.7f }, time);
            ImGui.SameLine();
            ImGui.TextUnformatted(text);
        }
        ImGui.PopTextWrapPos();
        if (ImGui.IsWindowAppearing() || ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 1) ImGui.SetScrollHereY(1);
    }
}
