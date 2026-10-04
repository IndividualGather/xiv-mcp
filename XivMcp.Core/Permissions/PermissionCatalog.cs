using System;
using System.Collections.Generic;
using System.Linq;
using XivMcp.Integrations;
using XivMcp.Mcp;

namespace XivMcp.Permissions;

/// <summary>Whether a call reads or changes something (from the tool's ReadOnly flag).</summary>
public enum Access { Read, Write }

/// <summary>
/// A permission group for XIV MCP's own tools: an area of the core (Game &amp; navigation, Market &amp; purchases, …) or one maintained
/// integration (<see cref="PluginId"/> set). Each has a read and a write setting, Allow / Ask / Deny.
/// </summary>
public sealed record PermissionGroup(string Id, string Title, string Description, PolicyMode DefaultRead, PolicyMode DefaultWrite,
                                     bool HasRead = true, bool HasWrite = true, string? PluginId = null)
{
    public PolicyMode Default(Access access) => access == Access.Read ? DefaultRead : DefaultWrite;
    public bool Has(Access access) => access == Access.Read ? HasRead : HasWrite;
}

/// <summary>Which group each of XIV MCP's tools belongs to. Group ids are stored in the settings: never rename one.</summary>
public static class PermissionCatalog
{
    private const PolicyMode A = PolicyMode.Allow, D = PolicyMode.Deny;

    public static IReadOnlyList<PermissionGroup> Groups { get; } =
    [
        new("game_data", "Game data", "Your character, inventory, quests, collections, surroundings and game data.",
            A, D, HasWrite: false),
        new("game_navigation", "Game & navigation", "Windows, NPCs, walking and travel, logins, gearsets and leaving duties.", A, D),
        new("items_retainers", "Items & retainers", "Moving items, retainers, ventures, collectables and the FC chest.", A, D),
        new("market", "Market & purchases", "Buying from vendors and selling on the market board.", A, D),
        new("ui_editing", "UI editing", "Macros and waymark presets.", A, D),
        new("online", "Online lookups", "Item sources and market prices from the web.", D, D, HasWrite: false),
        new("plugin_management", "Plugin management", "Other plugins and their settings, which may contain secrets.", D, D),
        new("jobs", "Background jobs", "Background jobs; each step is checked against its own group.", A, A),
        .. IntegrationCatalog.All.Select(i => new PermissionGroup(IntegrationGroupId(i.PluginId), i.DisplayName, i.Summary, A, D,
            // Tools that declare nothing beyond reading only read; the others change something.
            HasRead: i.Tools.Values.Any(c => c.Length == 0), HasWrite: i.Tools.Values.Any(c => c.Length > 0), PluginId: i.PluginId)),
    ];

    /// <summary>The group id of an integration: its plugin's internal name, lower-cased (e.g. "autoduty").</summary>
    public static string IntegrationGroupId(string pluginId) => pluginId.ToLowerInvariant();

    public static IReadOnlyDictionary<string, string> CoreTools { get; } = Map(new()
    {
        ["game_data"] =
        [
            "get_game_status", "get_character", "get_class_jobs", "get_inventory", "search_inventory", "get_equipment", "get_currencies",
            "list_unlock_categories", "check_unlocks", "get_active_quests", "check_quests", "get_party", "get_targets", "get_nearby_objects",
            "get_fates", "get_aetherytes", "get_companions", "get_submersibles", "list_game_sheets", "search_game_data", "get_game_data_row",
            "inspect_window", "get_collections", "get_armoire", "get_glamour_dresser", "get_job_actions", "get_hotbars", "list_gearsets",
            "get_cache_status", "wait_for_cache_refresh", "run_self_test", "plan_craft", "list_plugins",
        ],
        ["game_navigation"] =
        [
            "list_windows", "get_menu", "get_automation_status", "get_navigation_status", "list_characters",
            "open_window", "close_window", "interact_with_object", "select_menu_option", "load_game_data", "navigate_to", "stop_navigation",
            "switch_character", "refresh_character_list", "switch_gearset", "leave_duty", "place_waymark_preset",
        ],
        ["items_retainers"] =
        [
            "get_retainers", "get_retainer_inventories", "get_fc_chest", "find_ventures",
            "sort_inventory", "move_items", "open_retainer", "close_retainer", "transfer_retainer_items", "refresh_retainer_inventories",
            "assign_venture", "recall_venture", "turn_in_collectables",
        ],
        ["market"] =
        [
            "get_market_listings", "get_sales", "list_approvals",
            "buy_item", "request_spending_approval", "revoke_approval", "sell_item", "reprice_listings", "get_sale_history",
        ],
        ["ui_editing"] = ["get_macros", "list_waymark_presets", "get_waymark_preset", "set_macro", "clear_macro", "set_waymark_preset"],
        ["online"] = ["get_item_sources", "get_market_prices"],
        ["plugin_management"] = ["list_plugin_config_files", "get_plugin_config", "set_plugin_enabled", "reload_plugin", "set_plugin_config"],
        ["jobs"] = ["list_jobs", "get_job", "start_job", "update_job", "pause_job", "resume_job", "cancel_job", "wait"],
    });

    private static Dictionary<string, string> Map(Dictionary<string, string[]> byGroup) =>
        byGroup.SelectMany(kv => kv.Value.Select(t => (Tool: t, Group: kv.Key))).ToDictionary(x => x.Tool, x => x.Group);

    private static readonly Dictionary<string, PermissionGroup> ById = Groups.ToDictionary(g => g.Id);

    public static PermissionGroup? Find(string id) => ById.GetValueOrDefault(id);

    public static PermissionGroup? ForIntegration(string pluginId) => Find(IntegrationGroupId(pluginId));

    /// <summary>The group of one of XIV MCP's tools (core or maintained); null for third-party tools and unknown names.</summary>
    public static PermissionGroup? GroupOf(McpTool tool) => tool.Provider.Trust switch
    {
        ProviderTrust.Maintained => ForIntegration(tool.Provider.Id),
        ProviderTrust.Core => CoreTools.TryGetValue(tool.Name, out var g) ? Find(g) : null,
        _ => null,
    };

    public static Access AccessOf(McpTool tool) => tool.ReadOnly ? Access.Read : Access.Write;
}

/// <summary>The switches of settings version 3 and before, read once to migrate.</summary>
public sealed record LegacySwitches(bool GameNavigation, bool ItemsRetainers, bool MarketPurchases, bool CraftingGathering, bool UiEditing,
                                    bool OnlineData, bool PluginManagement);

/// <summary>The player's Allow / Ask / Deny per group and access for XIV MCP's own tools. Unset entries use the group defaults.</summary>
public sealed class CorePolicy
{
    /// <summary>Keyed "group:read" / "group:write".</summary>
    public Dictionary<string, PolicyMode> Modes { get; set; } = new();

    private static string Key(string group, Access access) => $"{group}:{(access == Access.Read ? "read" : "write")}";

    public PolicyMode ModeFor(string group, Access access)
    {
        if (PermissionCatalog.Find(group) is not { } g || !g.Has(access)) return PolicyMode.Deny;
        return Modes.TryGetValue(Key(group, access), out var m) ? m : g.Default(access);
    }

    public void Set(string group, Access access, PolicyMode mode) => Modes[Key(group, access)] = mode;

    /// <summary>Per-tool settings that override the tool's group (by tool name). Tools not listed follow their group.</summary>
    public Dictionary<string, PolicyMode> Tools { get; set; } = new();

    public PolicyMode? ToolMode(string tool) => Tools.TryGetValue(tool, out var m) ? m : null;

    /// <summary>Sets a tool's own mode, or (null) lets it follow its group again.</summary>
    public void SetTool(string tool, PolicyMode? mode)
    {
        if (mode is { } m) Tools[tool] = m;
        else Tools.Remove(tool);
    }

    /// <summary>Old on/off switches: a switch that was on allows the group's writes (and reads, where they were gated); integrations inherit the switch their tools needed.</summary>
    public static CorePolicy FromLegacy(LegacySwitches s)
    {
        static PolicyMode M(bool on) => on ? PolicyMode.Allow : PolicyMode.Deny;
        var p = new CorePolicy();
        p.Set("game_navigation", Access.Write, M(s.GameNavigation));
        p.Set("items_retainers", Access.Write, M(s.ItemsRetainers));
        p.Set("market", Access.Write, M(s.MarketPurchases));
        p.Set("ui_editing", Access.Write, M(s.UiEditing));
        p.Set("online", Access.Read, M(s.OnlineData));
        p.Set("plugin_management", Access.Read, M(s.PluginManagement));
        p.Set("plugin_management", Access.Write, M(s.PluginManagement));
        p.Set(PermissionCatalog.IntegrationGroupId("AutoDuty"), Access.Write, M(s.GameNavigation));
        p.Set(PermissionCatalog.IntegrationGroupId("Lifestream"), Access.Write, M(s.GameNavigation));
        p.Set(PermissionCatalog.IntegrationGroupId("Artisan"), Access.Write, M(s.CraftingGathering));
        p.Set(PermissionCatalog.IntegrationGroupId("GatherbuddyReborn"), Access.Write, M(s.CraftingGathering));
        p.Set(PermissionCatalog.IntegrationGroupId("FCCH"), Access.Write, M(s.ItemsRetainers));
        return p;
    }
}
