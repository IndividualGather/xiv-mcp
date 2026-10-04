using System;
using System.Linq;
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

        // 2. Changes something (here: prints to the local chat log), so it isn't read-only and declares what it does.
        mcp.AddTool(
            new McpToolDefinition("hellomcp_print",
                "Prints a message into the player's own chat log (only they see it). Use it to tell the player something in game.")
            {
                Capabilities = [McpCapabilities.GameUi],
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
                Capabilities = [McpCapabilities.GameUi],
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

        // 4. Asks for approval at the risky moment, with the real numbers. (This example only pretends: nothing is bought.)
        mcp.AddLongRunningTool(
            new McpToolDefinition("hellomcp_pretend_purchase",
                "Pretends to buy an item for gil, to try XIV MCP's approval flow: it asks the player first and reports what they decided. " +
                "Nothing is actually bought or spent.")
            {
                Capabilities = [McpCapabilities.SpendGil],
                InputSchema = """
                    {
                      "type": "object",
                      "properties": {
                        "item": { "type": "string", "description": "What to pretend to buy (default Hi-Potion)." },
                        "gil": { "type": "integer", "minimum": 1, "description": "The pretend price (default 1200)." }
                      }
                    }
                    """,
            },
            async call =>
            {
                var item = call.Args["item"]?.GetValue<string>() ?? "Hi-Potion";
                var gil = call.Args["gil"]?.GetValue<int>() ?? 1200;
                if (mcp.CheckPermission(McpCapabilities.SpendGil) == McpPermission.Deny)
                    throw new McpToolException("Spending gil is blocked for Hello MCP in /xivmcp.");
                var approved = await call.RequestApprovalAsync(McpCapabilities.SpendGil, $"Buy {item} for {gil:N0} gil (pretend)");
                if (!approved) throw new McpToolException("The player declined the purchase; nothing was bought.");
                await Framework.RunOnFrameworkThread(() => Chat.Print($"Pretended to buy {item} for {gil:N0} gil.", "Hello MCP"));
                return new { item, gil, approved, note = "Nothing was actually bought." };
            });

        // 5. The tools our jobs use besides our own. XIV MCP shows them on Hello MCP's page in /xivmcp → Third-party plugins, and
        //    tells the player what is missing. navigate_to can use a walking and a travel plugin (XIV MCP knows which); without
        //    them it shows the player the way and waits, so the card shows a hint, not an error.
        //    Another plugin's tool would be declared with McpDependency.FromPlugin(tool, plugin, name, repo, minVersion).
        mcp.UsesTools(
            McpDependency.BuiltIn("get_game_status"),
            McpDependency.BuiltIn("navigate_to"));

        Commands.AddHandler("/hellomcp", new CommandInfo(OnCommand)
        {
            HelpMessage = "/hellomcp job → start an example XIV MCP job · /hellomcp bell → walk to a summoning bell, then say so · " +
                          "/hellomcp jobs → list this plugin's jobs · /hellomcp status",
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
                case "bell":
                    // A built-in tool that uses other plugins, then ours with its result. navigate_to reports arrived: false (with a
                    // reason) when it can't get there, for example because a plugin it needs is missing; Hello MCP's page in
                    // /xivmcp shows what to install.
                    var walk = mcp.StartJob("Hello MCP walks to a bell",
                        new McpJobStep("navigate_to", new { destination = "summoning_bell" }, Id: "walk"),
                        new McpJobStep("hellomcp_print", new { text = "At a summoning bell: {{walk.arrived}}." }));
                    Chat.Print($"Started job {walk["id"]} — see /xivmcp → Jobs.", "Hello MCP");
                    break;
                case "jobs":
                    foreach (var j in mcp.ListJobs())
                        Chat.Print($"{j?["id"]} {j?["name"]}: {j?["state"]} ({j?["progress"]})", "Hello MCP");
                    break;
                default:
                    // Tell the player where things stand, the way your own UI could.
                    var status = mcp.GetStatus();
                    Chat.Print(status.State switch
                    {
                        McpPluginState.Unavailable => "XIV MCP is not loaded.",
                        McpPluginState.Undecided => "XIV MCP is waiting for your decision: enable Hello MCP in /xivmcp → Third-party plugins.",
                        McpPluginState.KeptDisabled => "You kept Hello MCP disabled in XIV MCP.",
                        McpPluginState.AwaitingConsent => "Hello MCP changed its tools; XIV MCP waits for your consent in /xivmcp → Third-party plugins.",
                        McpPluginState.Suspended => $"XIV MCP suspended Hello MCP: {status.SuspendReason}",
                        _ => "Enabled in XIV MCP. " + string.Join(", ", status.Tools.Select(t => $"{t.Key}: {t.Value}")),
                    }, "Hello MCP");
                    foreach (var d in status.Dependencies.Where(d => !d.Ok))
                        Chat.PrintError($"Our jobs use {d.Tool}: {d.Message}", "Hello MCP");
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
