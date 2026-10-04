using System;
using System.Security.Cryptography;
using Dalamud.Configuration;

namespace XivMcp;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 4;

    /// <summary>Whether the MCP server should be running.</summary>
    public bool ServerEnabled { get; set; } = true;

    /// <summary>Local TCP port the server listens on (localhost only).</summary>
    public int Port { get; set; } = 37521;

    /// <summary>When true, clients must send "Authorization: Bearer &lt;token&gt;".</summary>
    public bool RequireToken { get; set; } = true;

    public string Token { get; set; } = NewToken();

    /// <summary>The assistant chosen in the Connect tab: "claude", "codex" or "other".</summary>
    public string ConnectClient { get; set; } = "claude-desktop";

    /// <summary>
    /// Allow / Ask / Deny per permission group (core areas and maintained integrations) and access (read / write) for XIV MCP's own
    /// tools. Enforced by the permission gate before a tool runs; unset entries use the group's defaults.
    /// </summary>
    public XivMcp.Permissions.CorePolicy CorePolicy { get; set; } = new();

    // ---- The switches of settings version 3 and before. Reading them gives the matching group's current setting (for the checks some
    // tools still make, e.g. whether turning in collectables may travel); setting them only happens when an old settings file is read.
    private bool legacyPluginManagement, legacyGameNavigation, legacyUiEditing, legacyItemsRetainers, legacyMarketPurchases, legacyCraftingGathering, legacyOnlineData;

    private bool NotDenied(string group, XivMcp.Permissions.Access access) => CorePolicy.ModeFor(group, access) != XivMcp.Permissions.PolicyMode.Deny;

    public bool AllowPluginManagement { get => NotDenied("plugin_management", XivMcp.Permissions.Access.Read) || NotDenied("plugin_management", XivMcp.Permissions.Access.Write); set => legacyPluginManagement = value; }
    public bool AllowGameNavigation { get => NotDenied("game_navigation", XivMcp.Permissions.Access.Write); set => legacyGameNavigation = value; }
    public bool AllowUiEditing { get => NotDenied("ui_editing", XivMcp.Permissions.Access.Write); set => legacyUiEditing = value; }
    public bool AllowItemsRetainers { get => NotDenied("items_retainers", XivMcp.Permissions.Access.Write); set => legacyItemsRetainers = value; }
    public bool AllowMarketPurchases { get => NotDenied("market", XivMcp.Permissions.Access.Write); set => legacyMarketPurchases = value; }
    public bool AllowCraftingGathering { get => NotDenied("artisan", XivMcp.Permissions.Access.Write) || NotDenied("gatherbuddyreborn", XivMcp.Permissions.Access.Write); set => legacyCraftingGathering = value; }
    public bool AllowOnlineData { get => NotDenied("online", XivMcp.Permissions.Access.Read); set => legacyOnlineData = value; }
    public bool ShouldSerializeAllowPluginManagement() => false;
    public bool ShouldSerializeAllowGameNavigation() => false;
    public bool ShouldSerializeAllowUiEditing() => false;
    public bool ShouldSerializeAllowItemsRetainers() => false;
    public bool ShouldSerializeAllowMarketPurchases() => false;
    public bool ShouldSerializeAllowCraftingGathering() => false;
    public bool ShouldSerializeAllowOnlineData() => false;

    /// <summary>
    /// What each third-party plugin (by internal name) may do through the plugin API: enabled, suspended, and Allow / Ask / Deny per
    /// capability. A plugin is off until the player enables it.
    /// </summary>
    public System.Collections.Generic.Dictionary<string, XivMcp.Permissions.PluginPolicy> ThirdPartyPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Before version 3: the plugins whose tools were allowed. Read once and moved into <see cref="ThirdPartyPolicies"/>.</summary>
    public System.Collections.Generic.HashSet<string> AllowedToolPlugins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ShouldSerializeAllowedToolPlugins() => false;

    /// <summary>Optional: gil purchases costing more than this in one buy_item call also ask in game first (0 = gil never asks).</summary>
    public int AskAboveGil { get; set; }

    // ---- Permissions before version 2 (merged above). Read once from older settings files, never written again.
    public bool AllowInventoryActions { get; set; }
    public bool AllowGameInteraction { get; set; }
    public bool AllowMacroEditing { get; set; }
    public bool AllowWaymarkEditing { get; set; }
    public bool AllowNavigation { get; set; }
    public bool AllowPurchasing { get; set; }
    public bool AllowMarket { get; set; }
    public bool AllowVentures { get; set; }
    public bool ShouldSerializeAllowInventoryActions() => false;
    public bool ShouldSerializeAllowGameInteraction() => false;
    public bool ShouldSerializeAllowMacroEditing() => false;
    public bool ShouldSerializeAllowWaymarkEditing() => false;
    public bool ShouldSerializeAllowNavigation() => false;
    public bool ShouldSerializeAllowPurchasing() => false;
    public bool ShouldSerializeAllowMarket() => false;
    public bool ShouldSerializeAllowVentures() => false;

    /// <summary>
    /// Version 1 → 2: the eleven permissions were merged into seven. A merged permission starts on only if every permission it replaces
    /// was on (so nothing becomes allowed that wasn't before). Returns true if something changed and should be saved.
    /// </summary>
    public bool Migrate()
    {
        if (Version >= 4) return false;
        if (Version < 2)
        {
            legacyGameNavigation = AllowGameInteraction && AllowNavigation;
            legacyUiEditing = AllowMacroEditing && AllowWaymarkEditing;
            legacyItemsRetainers = AllowInventoryActions && AllowVentures;
            legacyMarketPurchases = AllowPurchasing && AllowMarket;
        }
        // Version 3 → 4: on/off switches become Allow / Ask / Deny per group, read and write (a switch that was on allows its writes).
        if (CorePolicy.Modes.Count == 0)
            CorePolicy = XivMcp.Permissions.CorePolicy.FromLegacy(new XivMcp.Permissions.LegacySwitches(legacyGameNavigation, legacyItemsRetainers,
                legacyMarketPurchases, legacyCraftingGathering, legacyUiEditing, legacyOnlineData, legacyPluginManagement));
        foreach (var plugin in AllowedToolPlugins)
        {
            if (!ThirdPartyPolicies.TryGetValue(plugin, out var p)) ThirdPartyPolicies[plugin] = p = new XivMcp.Permissions.PluginPolicy();
            p.Enabled = true;
        }
        AllowedToolPlugins.Clear();
        Version = 4;
        return true;
    }

    /// <summary>Where navigate_to looks for a summoning bell when none is nearby: lifestream (its property priority), inn, fc, home, apartment.</summary>
    public string PreferredBellLocation { get; set; } = "lifestream";

    /// <summary>Pause between two item moves, so the server can confirm each one.</summary>
    public int MoveDelayMs { get; set; } = 700;

    /// <summary>When true, the pause between moves is random between <see cref="MoveDelayMinMs"/> and <see cref="MoveDelayMaxMs"/>;
    /// otherwise it is exactly <see cref="MoveDelayMs"/>.</summary>
    public bool MoveDelayRandom { get; set; } = true;

    public int MoveDelayMinMs { get; set; } = 500;
    public int MoveDelayMaxMs { get; set; } = 800;

    public const int MoveDelayLimitMin = 200;
    public const int MoveDelayLimitMax = 5000;

    /// <summary>The pause to wait after one item move before the next one.</summary>
    public TimeSpan NextMoveDelay()
    {
        if (!MoveDelayRandom) return TimeSpan.FromMilliseconds(Math.Clamp(MoveDelayMs, MoveDelayLimitMin, MoveDelayLimitMax));
        var min = Math.Clamp(Math.Min(MoveDelayMinMs, MoveDelayMaxMs), MoveDelayLimitMin, MoveDelayLimitMax);
        var max = Math.Clamp(Math.Max(MoveDelayMinMs, MoveDelayMaxMs), MoveDelayLimitMin, MoveDelayLimitMax);
        return TimeSpan.FromMilliseconds(Random.Shared.Next(min, max + 1));
    }

    public string MoveDelayDescription => MoveDelayRandom ? $"random {MoveDelayMinMs}-{MoveDelayMaxMs} ms" : $"exactly {MoveDelayMs} ms";

    /// <summary>Cached snapshots (submersibles, retainers) older than this are reported as stale with a refresh suggestion.</summary>
    public int CacheStaleHours { get; set; } = 12;

    /// <summary>The activity overlay: shown while a tool call or job runs (/xivmcp → Overlay).</summary>
    public XivMcp.Ui.OverlaySettings Overlay { get; set; } = new();

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    public void Save() => Svc.PluginInterface.SavePluginConfig(this);
}
