using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using XivMcp.Mcp;
using XivMcp.Tools;
using XivMcp.Windows;

namespace XivMcp;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xivmcp";

    private readonly WindowSystem windows = new("XivMcp");
    private readonly ConfigWindow configWindow;

    public Configuration Config { get; }
    public McpServer Server { get; }

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Svc>();
        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var tools = CharacterTools.Create()
            .Concat(InventoryTools.Create())
            .Concat(UnlockTools.Create())
            .Concat(WorldTools.Create())
            .Concat(GameDataTools.Create())
            .Concat(PluginTools.Create(Config));
        Server = new McpServer(tools, Config);

        configWindow = new ConfigWindow(this);
        windows.AddWindow(configWindow);
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        pluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;

        Svc.Commands.AddHandler(Command, new CommandInfo((_, _) => configWindow.Toggle())
        {
            HelpMessage = "Open the XIV MCP server settings and connection info.",
        });

        if (Config.ServerEnabled) Server.Start();
    }

    public void Dispose()
    {
        Svc.Commands.RemoveHandler(Command);
        Svc.PluginInterface.UiBuilder.Draw -= windows.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        windows.RemoveAllWindows();
        Server.Dispose();
    }
}
