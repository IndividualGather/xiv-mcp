using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace XivMcp.Ui;

/// <summary>
/// Presses controls of XIV MCP's own window on behalf of a tool (press_xivmcp_control, dev builds only). The window's draw code
/// asks <see cref="Consume"/> for every control it draws, next to the real ImGui click, so a requested press runs exactly the code a
/// click runs. What was drawn in the last full frame is <see cref="Visible"/>, so the tool can list what can be pressed.
/// </summary>
public sealed class UiControlQueue
{
    private readonly object sync = new();
    private readonly Dictionary<string, TaskCompletionSource<bool>> requested = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> drawing = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> visible = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The controls drawn in the last full frame.</summary>
    public IReadOnlyCollection<string> Visible
    {
        get { lock (sync) return [.. visible]; }
    }

    /// <summary>Called at the start of each frame of the window (also when it is closed): the controls drawn since become visible.</summary>
    public void BeginFrame()
    {
        lock (sync)
        {
            visible = drawing;
            drawing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Called by the draw code for each control it draws. True once when a press of it was requested.</summary>
    public bool Consume(string id)
    {
        lock (sync)
        {
            drawing.Add(id);
            if (!requested.Remove(id, out var press)) return false;
            press.TrySetResult(true);
            return true;
        }
    }

    /// <summary>Requests a press of <paramref name="id"/>: true once it was drawn and pressed, false if it wasn't drawn in time.</summary>
    public async Task<bool> Request(string id, TimeSpan timeout)
    {
        var press = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (requested.Remove(id, out var earlier)) earlier.TrySetResult(false);
            requested[id] = press;
        }
        var done = await Task.WhenAny(press.Task, Task.Delay(timeout)).ConfigureAwait(false);
        if (done == press.Task) return press.Task.Result;
        lock (sync)
            if (requested.TryGetValue(id, out var current) && current == press) requested.Remove(id);
        return press.Task.IsCompletedSuccessfully && press.Task.Result;
    }
}
