using System;
using System.Threading.Tasks;
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
    private readonly OverlayWindow overlayWindow;
    internal OverlayWindow Overlay => overlayWindow;
    private readonly ConsentWindow consentWindow;
    private readonly WorkshopTracker workshop;
    private readonly RetainerTracker retainers;
    private readonly GlamourTracker glamour;
    private readonly ProgressTracker progress;
    private readonly StorageTracker storage;
    private readonly SalesTracker sales;
    private readonly CharacterRoster roster;
    private JobManager? jobs;
    internal JobManager? Jobs => jobs;
    private readonly PluginCompat compat;
    private readonly CacheRegistry caches;
    private readonly XivMcp.Api.PluginApi pluginApi;
    internal XivMcp.Api.PluginApi PluginApi => pluginApi;
    private readonly ConfigPolicyStore policyStore;
    private readonly GameProbe probe;
    private readonly ChatSecurityNotifier notifier;
    private readonly XivMcp.Permissions.ToolGate gate;
    internal XivMcp.Permissions.ToolGate Gate => gate;
    internal XivMcp.Permissions.IPolicyStore Policies => policyStore;
    internal GameProbe Probe => probe;
    private readonly PluginDecisions decisions;
    internal PluginDecisions Decisions => decisions;

    internal static Plugin? Instance { get; private set; }

    public Configuration Config { get; }
    public McpServer Server { get; }
    internal CacheRegistry Caches => caches;
    internal PluginCompat Compat => compat;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        pluginInterface.Create<Svc>();
        CoreLog.Information = m => Svc.Log.Information(m);
        CoreLog.Warning = m => Svc.Log.Warning(m);
        CoreLog.Error = m => Svc.Log.Error(m);
        if (pluginInterface.GetPluginConfig() is Configuration saved)
        {
            Config = saved;
            if (Config.Migrate()) Config.Save();
            Config.Overlay.Normalize();
        }
        else
        {
            // First start: persist right away so the generated access token stays the same across game restarts.
            Config = new Configuration();
            Config.Save();
        }

        workshop = new WorkshopTracker();
        retainers = new RetainerTracker();
        glamour = new GlamourTracker();
        progress = new ProgressTracker();
        storage = new StorageTracker();
        sales = new SalesTracker();
        roster = new CharacterRoster();
        compat = new PluginCompat();
        caches = new CacheRegistry();
        caches.Add(workshop);
        caches.Add(retainers);
        caches.Add(glamour);
        caches.Add(progress);
        caches.Add(storage);

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
            .Concat(GilTools.Create(Config, compat))
            .Concat(SubmarineTools.Create(Config))
            .Concat(TradeTools.Create())
            .Concat(NavigationTools.Create(Config))
            .Concat(CraftGatherTools.Create(Config))
            .Concat(ItemSourceTools.Create(Config))
            .Concat(FishingTools.Create(Config))
            .Concat(ShopTools.Create(Config, compat))
            .Concat(ShopTools.ApprovalTools(Config))
            .Concat(VentureTools.Create(Config, retainers, compat))
            .Concat(CollectableTools.Create(Config, compat))
            .Append(VentureTools.RecallTool(Config, compat))
            .Concat(CraftPlanTools.Create(Config, retainers))
            .Concat(LongRunningTools.Create(Config))
            .Concat(DutyTools.Create(Config, () => jobs!, () => Server?.LastClient))
            .Concat(GearsetTools.Create(Config))
            .Concat(MarketTools.Create(Config, retainers, sales, compat))
            .Concat(LoginTools.Create(Config, roster))
            .Concat(WindowInspectTools.Create())
            .Concat(CacheTools.Create(caches))
            .Append(SelfTest.Tool())
            .Concat(JobTools.Create(() => jobs!, () => Server?.LastClient))
            .Concat(MapTools.Create(() => jobs!, () => Server?.LastClient))
            .Concat(DyeTools.Create())
            .Concat(MarketBoardTools.Create(Config))
            .Concat(FashionTools.Create(Config, retainers, () => jobs!, () => Server?.LastClient))
            .Concat(SaucyTools.Create(() => jobs!, () => Server?.LastClient))
            .Concat(AutoHookTools.Create(Config, () => jobs!, () => Server?.LastClient))
            .Concat(XivMcpWindowTools.Create(() => configWindow, () => overlayWindow, () => jobs, pluginInterface.IsDev))
            .ToList();
        // Tools that drive one other plugin belong to that plugin's integration (same provider + capability contract as third-party tools).
        tools = XivMcp.Integrations.IntegrationCatalog.Apply(tools, PluginCompat.IsLoaded).ToList();
        var registry = new ToolRegistry(tools);

        // Third-party tools: policy per capability, in-game approval, side-effect checks and an audit log.
        policyStore = new ConfigPolicyStore(Config);
        probe = new GameProbe();
        notifier = new ChatSecurityNotifier();
        var audit = new XivMcp.Permissions.AuditLog(500, AuditFile.Append);
        foreach (var e in AuditFile.ReadRecent(500)) audit.AddRestored(e);
        gate = new XivMcp.Permissions.ToolGate(policyStore, new ConsentApprovalGate(), probe, audit, notifier);

        jobs = new JobManager(registry, gate);
        caches.Add(jobs);
        foreach (var t in tools) t.ParsedSchema(); // logs any tool whose input schema is not valid JSON, right at startup
        Server = new McpServer(registry, gate, Config, caches);
        pluginApi = new XivMcp.Api.PluginApi(registry, policyStore, gate, () => jobs);
        decisions = new PluginDecisions(registry, policyStore, gate, () => Server.NotifyIfToolsChanged());
        var announcer = new RegistrationNotifier(decisions, policyStore, id => configWindow?.ShowThirdParty(id));
        pluginApi.Registered += announcer.Registered;

        configWindow = new ConfigWindow(this);
        windows.AddWindow(configWindow);
        configWindow.RestoreAfterReload();
        consentWindow = new ConsentWindow();
        windows.AddWindow(consentWindow);
        // After the settings window: both press controls through its queue, whose frame starts in the overlay's Update.
        overlayWindow = new OverlayWindow(this, configWindow);
        windows.AddWindow(overlayWindow);
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        pluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;

        Svc.Commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the XIV MCP settings · /xivmcp selftest → run the live self-test and print the results in chat.",
        });

        if (Config.ServerEnabled) Server.Start();
        pluginApi.AnnounceReady(); // plugins that loaded before us register their tools now
    }

    private void OnCommand(string command, string arguments)
    {
        if (!arguments.Trim().Equals("selftest", StringComparison.OrdinalIgnoreCase))
        {
            configWindow.Toggle();
            return;
        }
        if (SelfTest.Running) return;
        Svc.Chat.Print("[XIV MCP] Running the self-test…");
        _ = Task.Run(async () =>
        {
            var results = await SelfTest.RunAsync(this).ConfigureAwait(false);
            await Svc.Framework.RunOnFrameworkThread(() => SelfTest.PrintToChat(results)).ConfigureAwait(false);
        });
    }

    public void Dispose()
    {
        UiEventRecorder.Dispose();
        Svc.Commands.RemoveHandler(Command);
        Svc.PluginInterface.UiBuilder.Draw -= windows.Draw;
        Svc.PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        Svc.PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        Consent.DeclineAll();
        configWindow.RememberForReload();
        windows.RemoveAllWindows();
        pluginApi.Dispose();
        probe.Dispose();
        jobs?.Dispose();
        Server.Dispose();
        XivMcp.Util.AppIcons.Reset();
        workshop.Dispose();
        retainers.Dispose();
        glamour.Dispose();
        progress.Dispose();
        storage.Dispose();
        sales.Dispose();
        roster.Dispose();
        compat.Dispose();
        Instance = null;
    }
}
