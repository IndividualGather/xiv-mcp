using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using XivMcp.Api;
using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Permissions;
using XivMcp.Util;

namespace XivMcp.Windows;

/// <summary>
/// Tools that don't come from XIV MCP's core: the integrations XIV MCP maintains (shown under Modules, gated by the core switches)
/// and third-party plugins (their own tab, see <see cref="ThirdPartyPanel"/>).
/// </summary>
internal sealed partial class ConfigWindow
{
    private ThirdPartyPanel? thirdParty;
    private ThirdPartyPanel ThirdParty => thirdParty ??= new ThirdPartyPanel(new PluginHost(plugin));

    private PermissionsPanel? permissions;
    private PermissionsPanel Permissions => permissions ??= new PermissionsPanel(new CoreHost(this));

    private sealed class CoreHost(ConfigWindow window) : IPermissionsHost
    {
        public IReadOnlyCollection<McpTool> Tools => window.plugin.Server.Tools;
        public CorePolicy Policy => window.plugin.Config.CorePolicy;
        public void Save() => window.plugin.Config.Save();
        public bool IsInstalled(string pluginId) => Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        public bool IsLoaded(string pluginId) => PluginCompat.IsLoaded(pluginId);
        public void ToolsChanged() => window.plugin.Server.NotifyIfToolsChanged();

        public Action? Options(string groupId) => groupId switch
        {
            "game_navigation" when window.compat is { } n && (n.Vnavmesh || n.Lifestream) => window.DrawBellPreference,
            "items_retainers" => window.DrawMoveDelay,
            "market" => () =>
            {
                window.DrawGilLimit();
                if (!(window.compat?.ItemVendorLocation ?? false)) DrawVendorPluginMissing();
            },
            _ => null,
        };
    }

    private string ThirdPartyTabLabel() => ThirdParty.TabLabel();

    private void DrawThirdParty() => ThirdParty.Draw();

    /// <summary>For the Tools tab: where a tool comes from.</summary>
    private static string SourceOf(McpTool t) => t.Provider.Trust switch
    {
        ProviderTrust.Core => "XIV MCP",
        ProviderTrust.Maintained => $"{t.Provider.DisplayName} integration",
        _ => $"{t.Provider.DisplayName} (third-party)",
    };

    private sealed class PluginHost(Plugin plugin) : IThirdPartyHost
    {
        public List<PluginApi.PluginInfo> Plugins() => plugin.PluginApi.Plugins(plugin.Config);
        public ToolGate Gate => plugin.Gate;
        public IPolicyStore Policies => plugin.Policies;
        public void ToolsChanged() => plugin.Server.NotifyIfToolsChanged();
        public RegistrationReview.Result Pending(string pluginId) => plugin.Decisions.Pending(pluginId);
        public void Decide(string pluginId, bool enable) => plugin.Decisions.Decide(pluginId, enable);
    }
}
