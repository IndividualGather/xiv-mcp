using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Mcp;
using XivMcp.Util;
using XivMcp.Windows;

namespace XivMcp.Tools;

/// <summary>
/// XIV MCP's own window: showing it on a tab, screenshots of the game or of the window, and (dev builds only) pressing its controls,
/// so it can be tested without simulated mouse input.
/// </summary>
internal static class XivMcpWindowTools
{
    /// <summary>The tool's tab names and the window's tab keys.</summary>
    private static readonly Dictionary<string, string> Tabs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["connect"] = "Connect", ["modules"] = "Modules", ["third_party"] = "Third-party", ["jobs"] = "Jobs", ["overlay"] = "Overlay", ["info"] = "Info",
    };

    public static IEnumerable<McpTool> Create(Func<ConfigWindow> window, Func<OverlayWindow> overlay, Func<JobManager?> jobs, bool dev)
    {
        yield return new McpTool
        {
            Name = "show_xivmcp_window",
            Description = "Opens XIV MCP's own settings window in game (the one /xivmcp opens) on a tab, so the player can see something there: " +
                          "a background job (tab 'jobs', with 'job' to open and scroll to it), a third-party plugin's permissions (tab " +
                          "'third_party', with 'plugin'), the modules, the connection settings, the overlay settings or the info tab. Use 'close' to close the window. " +
                          "It only shows the window; nothing in it is changed. Requires 'Game & navigation' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "tab": { "type": "string", "enum": ["connect", "modules", "third_party", "jobs", "overlay", "info"], "description": "The tab to show (default jobs)." },
                    "job": { "type": "string", "description": "A job id or name from list_jobs: opens the Jobs tab with that job unfolded." },
                    "plugin": { "type": "string", "description": "A third-party plugin's internal name: opens the Third-party tab with its card unfolded." },
                    "close": { "type": "boolean", "description": "Close the window instead (default false)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, _) =>
            {
                var w = window();
                if (args.Node("close")?.GetValue<bool>() == true)
                {
                    await Svc.Framework.RunOnFrameworkThread(() => w.IsOpen = false).ConfigureAwait(false);
                    return new { open = false };
                }
                var jobArg = args.String("job");
                var plugin = args.String("plugin");
                var tabArg = args.String("tab") ?? (plugin is not null ? "third_party" : "jobs");
                if (!Tabs.TryGetValue(tabArg, out var tab)) throw new ToolException($"Unknown tab '{tabArg}'. Use one of: {string.Join(", ", Tabs.Keys)}.");
                string? jobId = null;
                if (jobArg is not null)
                {
                    var manager = jobs() ?? throw new ToolException("Background jobs are not available.");
                    jobId = manager.Get(jobArg).Id; // throws for an unknown job
                    tab = "Jobs";
                }
                if (plugin is not null) tab = "Third-party";
                await Svc.Framework.RunOnFrameworkThread(() => w.Show(tab, jobId, plugin)).ConfigureAwait(false);
                return new { open = true, tab = Tabs.First(t => t.Value == tab).Key, job = jobId, plugin };
            },
        };

        yield return new McpTool
        {
            Name = "take_screenshot",
            Description = "Takes a screenshot of the game, with plugin windows on it, and returns it as an image: the whole game window " +
                          "(area 'game', the default), only XIV MCP's own window (area 'xivmcp', which must be open; see show_xivmcp_window), or " +
                          "only the activity overlay (area 'overlay', shown while a tool call or job runs). " +
                          "Use it to see what the player sees, or to check XIV MCP's window. The screen can show chat, tells and other players' " +
                          "names, so only describe what the player asked about. Large screenshots are scaled down to 'max_width' pixels across. " +
                          "Follows the 'Screen' setting in /xivmcp, which asks the player by default.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "area": { "type": "string", "enum": ["game", "xivmcp", "overlay"], "description": "What to capture (default game)." },
                    "max_width": { "type": "integer", "minimum": 320, "maximum": 3840, "description": "Scale down to at most this width in pixels (default 1600)." }
                  }
                }
                """,
            ReadOnly = true,
            Handler = async (args, ct) =>
            {
                var area = args.String("area") ?? "game";
                if (area is not ("game" or "xivmcp" or "overlay")) throw new ToolException("area must be game, xivmcp or overlay.");
                var maxWidth = args.Int("max_width", 1600, 320, 3840);
                var w = window();
                (System.Numerics.Vector2 Position, System.Numerics.Vector2 Size)? bounds = null;
                if (area == "xivmcp")
                {
                    bounds = await Svc.Framework.RunOnFrameworkThread(() => w.IsOpen ? w.Bounds : null).ConfigureAwait(false);
                    if (bounds is null) throw new ToolException("The XIV MCP window is not open. Open it with show_xivmcp_window first.");
                }
                else if (area == "overlay")
                {
                    var o = overlay();
                    bounds = await Svc.Framework.RunOnFrameworkThread(() => o.Shown ? o.Bounds : null).ConfigureAwait(false);
                    if (bounds is null) throw new ToolException("The overlay is not shown: nothing is running (or it is turned off in /xivmcp → Overlay).");
                }
                // Let a closing approval window (the default 'Ask') disappear first.
                await Task.Delay(150, ct).ConfigureAwait(false);
                var shot = await ScreenCapture.GameWindow(ct).ConfigureAwait(false);
                var full = (shot.Width, shot.Height);
                if (bounds is { } b) shot = shot.Crop((int)b.Position.X, (int)b.Position.Y, (int)b.Size.X, (int)b.Size.Y);
                var scaled = shot.FitWidth(maxWidth);
                var png = new Canvas(scaled).ToPng();
                var data = new
                {
                    area,
                    captured = new { width = shot.Width, height = shot.Height },
                    image = new { width = scaled.Width, height = scaled.Height },
                    gameWindow = new { width = full.Width, height = full.Height },
                };
                return new ToolResultWithImages(data, [new ToolImage(png, area switch { "xivmcp" => "The XIV MCP window.", "overlay" => "The activity overlay.", _ => "The game screen." })]);
            },
        };

        if (!dev) yield break;

        yield return new McpTool
        {
            Name = "press_xivmcp_control",
            Description = "Development builds only: presses a control in XIV MCP's own window as if it were clicked, running the same code " +
                          "as a click, to test the window. Without 'control', lists the controls that can be pressed right now. Ids: " +
                          "'tab:<Connect|Modules|Third-party|Jobs|Overlay|Info>'; on the Jobs tab 'job:<id>:<pause|resume|retry|skip|cancel|fold|" +
                          "show-earlier|hide-earlier>'; on the Overlay tab 'overlay:preview', 'overlay:reset-position', 'overlay:layout:<minimal|full>'; " +
                          "on the overlay itself 'overlay:job:<id>:<pause|resume|retry|cancel>'. Cancel asks for a second press within 4 seconds, " +
                          "like a click. The approval window and the permission settings can't be pressed. Only controls on screen can be pressed.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "control": { "type": "string", "description": "The control id, e.g. \"tab:Jobs\" or \"job:41b72ca5:pause\". Leave out to list them." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, _) =>
            {
                var w = window();
                var control = args.String("control");
                if (control is null) return new { controls = w.Controls.Visible.Order().ToList() };
                if (!await w.Controls.Request(control, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                    throw new ToolException($"'{control}' is not on screen right now (open the window with show_xivmcp_window). Shown: {string.Join(", ", w.Controls.Visible.Order())}.");
                await Task.Delay(100).ConfigureAwait(false); // a few frames, so the controls listed below reflect the press
                return new { pressed = control, controls = w.Controls.Visible.Order().ToList() };
            },
        };
    }
}
