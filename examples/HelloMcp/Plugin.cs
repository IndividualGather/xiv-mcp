using System;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using XivMcp.Client;

namespace HelloMcp;

/// <summary>
/// Example for the XIV MCP plugin API: one quick read-only tool, one tool that changes something, one long-running tool with
/// progress and cancellation, and a chat command that starts a background job mixing these with a built-in XIV MCP tool.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] private static IClientState ClientState { get; set; } = null!;
    [PluginService] private static IObjectTable Objects { get; set; } = null!;
    [PluginService] private static IFramework Framework { get; set; } = null!;
    [PluginService] private static IChatGui Chat { get; set; } = null!;
    [PluginService] private static ICommandManager Commands { get; set; } = null!;
    [PluginService] private static IPluginLog Log { get; set; } = null!;

    private readonly XivMcpClient mcp;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        mcp = new XivMcpClient(pluginInterface);
        mcp.RegistrationFailed += message => Log.Warning($"XIV MCP rejected a tool: {message}");

        // 1. Quick and read-only: runs on the framework thread, so game state can be read directly.
        mcp.AddTool(
            new McpToolDefinition("hellomcp_greet",
                "Greets the logged-in character by name and says where they are. Use it to check that Hello MCP is working.")
            {
                ReadOnly = true,
                InputSchema = """
                    {
                      "type": "object",
                      "properties": {
                        "style": { "type": "string", "enum": ["casual", "formal"], "description": "How to greet (default casual)." }
                      }
                    }
                    """,
            },
            args =>
            {
                var player = Objects.LocalPlayer ?? throw new McpToolException("No character is logged in.");
                var name = player.Name.TextValue;
                var greeting = args["style"]?.GetValue<string>() == "formal" ? $"Well met, {name}." : $"Hey {name}!";
                return new { greeting, world = player.CurrentWorld.Value.Name.ExtractText(), territory = ClientState.TerritoryType };
            });

        // 2. Changes something (here: prints to the local chat log), so ReadOnly stays false.
        mcp.AddTool(
            new McpToolDefinition("hellomcp_print",
                "Prints a message into the player's own chat log (only they see it). Use it to tell the player something in game.")
            {
                InputSchema = """
                    {
                      "type": "object",
                      "properties": { "text": { "type": "string", "description": "The message, at most 200 characters." } },
                      "required": ["text"]
                    }
                    """,
            },
            args =>
            {
                var text = args["text"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(text)) throw new McpToolException("Give 'text'.");
                if (text.Length > 200) throw new McpToolException("'text' is longer than 200 characters.");
                Chat.Print(text, "Hello MCP");
                return new { printed = text };
            });

        // 3. Long-running: replies "pending" at once, reports progress, and stops cleanly when cancelled.
        mcp.AddLongRunningTool(
            new McpToolDefinition("hellomcp_countdown",
                "Counts down a number of seconds, then prints a message in chat. Long-running: use it as a job step (start_job) to try " +
                "progress reporting and pause / cancel.")
            {
                InputSchema = """
                    {
                      "type": "object",
                      "properties": { "seconds": { "type": "integer", "minimum": 1, "maximum": 3600, "description": "How long (default 30)." } }
                    }
                    """,
            },
            async call =>
            {
                var seconds = Math.Clamp(call.Args["seconds"]?.GetValue<int>() ?? 30, 1, 3600);
                for (var left = seconds; left > 0; left--)
                {
                    if (left % 10 == 0) call.Progress($"{left} s left");
                    await Task.Delay(1000, call.Cancellation); // throws when cancelled: the client reports "Cancelled."
                }
                await Framework.RunOnFrameworkThread(() => Chat.Print($"Countdown of {seconds} s finished.", "Hello MCP"));
                return new { seconds, finishedAt = DateTime.UtcNow };
            });

        Commands.AddHandler("/hellomcp", new CommandInfo(OnCommand)
        {
            HelpMessage = "/hellomcp job → start an example XIV MCP job · /hellomcp jobs → list this plugin's jobs · /hellomcp status",
        });
    }

    private void OnCommand(string command, string arguments)
    {
        try
        {
            switch (arguments.Trim())
            {
                case "job":
                    // Steps run one after another in XIV MCP. Later steps can use earlier results: {{count.seconds}}.
                    var job = mcp.StartJob("Hello MCP example",
                        new McpJobStep("hellomcp_countdown", new { seconds = 20 }, Id: "count"),
                        new McpJobStep("hellomcp_print", new { text = "The job counted {{count.seconds}} seconds." }),
                        new McpJobStep("get_game_status")); // a built-in XIV MCP tool
                    Chat.Print($"Started job {job["id"]} — see /xivmcp → Jobs.", "Hello MCP");
                    break;
                case "jobs":
                    foreach (var j in mcp.ListJobs())
                        Chat.Print($"{j?["id"]} {j?["name"]}: {j?["state"]} ({j?["progress"]})", "Hello MCP");
                    break;
                default:
                    Chat.Print(mcp.IsAvailable ? "XIV MCP is loaded; the tools are offered once you allow Hello MCP in /xivmcp → Permissions."
                                               : "XIV MCP is not loaded.", "Hello MCP");
                    break;
            }
        }
        catch (McpToolException ex)
        {
            Chat.PrintError($"XIV MCP: {ex.Message}", "Hello MCP");
        }
    }

    public void Dispose()
    {
        Commands.RemoveHandler("/hellomcp");
        mcp.Dispose();
    }
}
