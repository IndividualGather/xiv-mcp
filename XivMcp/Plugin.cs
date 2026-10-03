using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using XivMcp.Mcp;
using XivMcp.Tools;
using XivMcp.Util;
using XivMcp.Windows;

namespace XivMcp;

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/xivmcp";

    private readonly WindowSystem windows = new("XivMcp");
    private readonly ConfigWindow configWindow;
    private readonly WorkshopTracker workshop;
    private readonly RetainerTracker retainers;
    private readonly GlamourTracker glamour;
    private readonly PluginCompat compat;
    private readonly CacheRegistry caches;

    internal static Plugin? Instance { get; private set; }

    public Configuration Config { get; }
    public McpServer Server { get; }
    internal CacheRegistry Caches => caches;
    internal PluginCompat Compat => compat;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        pluginInterface.Create<Svc>();
        if (pluginInterface.GetPluginConfig() is Configuration saved)
            Config = saved;
        else
        {
            // First start: persist right away so the generated access token stays the same across game restarts.
            Config = new Configuration();
            Config.Save();
        }

        workshop = new WorkshopTracker();
        retainers = new RetainerTracker();
        glamour = new GlamourTracker();
        compat = new PluginCompat();
        caches = new CacheRegistry();
        caches.Add(workshop);
        caches.Add(retainers);
        caches.Add(glamour);

        var tools = CharacterTools.Create()
            .Concat(InventoryTools.Create(retainers))
            .Concat(UnlockTools.Create())
            .Concat(WorldTools.Create(retainers))
            .Concat(GameDataTools.Create())
            .Concat(PluginTools.Create(Config))
            .Concat(VoyageTools.Create(workshop))
            .Concat(InventoryActionTools.Create(Config))
            .Concat(RetainerTools.Create(Config, retainers, compat))
            .Concat(InteractionTools.Create(Config, compat))
            .Concat(ActionMacroTools.Create(Config))
            .Concat(WaymarkTools.Create(Config))
            .Concat(CollectionTools.Create(glamour))
            .Concat(FcChestTools.Create(Config))
            .Concat(CacheTools.Create(caches));
        Server = new McpServer(tools, Config, caches);

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
        workshop.Dispose();
        retainers.Dispose();
        glamour.Dispose();
        compat.Dispose();
        Instance = null;
    }
}
