using System.Collections.Generic;
using System.Linq;
using XivMcp.Mcp;
using static XivMcp.Permissions.Capabilities;

namespace XivMcp.Permissions;

/// <summary>
/// What XIV MCP's own tools can do, in the capabilities third-party plugins declare. The player's AI is held to the permission groups;
/// a plugin that runs these tools (in its jobs) is also held to what the player allowed that plugin, through these capabilities.
/// Integration tools declare theirs in the integration catalog; core tools are mapped here by group, with the riskier ones on their own.
/// </summary>
public static class BuiltInCapabilities
{
    /// <summary>
    /// Tools no plugin may run, whatever its permissions: running or changing jobs (a job can't start jobs), other plugins and their
    /// settings (which can hold secrets), screenshots, spending approvals, and XIV MCP's own window.
    /// </summary>
    public static IReadOnlySet<string> NotForPlugins { get; } = new HashSet<string>
    {
        "start_job", "update_job", "pause_job", "resume_job", "cancel_job",
        "list_plugin_config_files", "get_plugin_config", "set_plugin_config", "set_plugin_enabled", "reload_plugin",
        "take_screenshot", "request_spending_approval", "revoke_approval",
        "show_xivmcp_window", "press_xivmcp_control", "capture_ui_events",
    };

    private static readonly Dictionary<string, string[]> ByGroup = new()
    {
        ["game_data"] = [ReadGame],
        ["game_navigation"] = [GameUi, MoveCharacter],
        ["items_retainers"] = [GameUi, MoveItems],
        ["market"] = [GameUi, MoveCharacter, SpendGil],
        ["ui_editing"] = [EditSettings],
        ["online"] = [Network],
        ["plugin_management"] = [EditSettings],
        ["screen"] = [ReadGame],
        ["trading"] = [GameUi, TradeItems],
        ["jobs"] = [ReadGame],
    };

    private static readonly Dictionary<string, string[]> ByTool = new()
    {
        ["switch_character"] = [Login, MoveCharacter],
        ["refresh_character_list"] = [Login],
        ["visit_world"] = [MoveCharacter, SpendGil],
        ["leave_duty"] = [GameUi, Combat],
        ["move_gil"] = [MoveItems, SpendGil],
        ["turn_in_collectables"] = [GameUi, TradeItems],
        ["assign_venture"] = [GameUi, SpendCurrency],
        ["recall_venture"] = [GameUi],
        ["repair_submersible"] = [GameUi, SpendCurrency],
        ["deploy_submersible"] = [GameUi, SpendCurrency],
        ["recall_submersible"] = [GameUi, SpendCurrency],
        ["dye_item"] = [GameUi, MoveItems, SpendCurrency],
        ["buy_item"] = [GameUi, MoveCharacter, SpendGil, SpendCurrency],
        ["buy_from_market_board"] = [GameUi, MoveCharacter, SpendGil],
        ["sell_item"] = [GameUi, TradeItems],
        ["reprice_listings"] = [GameUi, TradeItems],
        ["set_macro"] = [EditSettings, ChatSend],
        ["clear_macro"] = [EditSettings],
    };

    /// <summary>The capabilities a built-in tool uses (reading tools: reading game state only).</summary>
    public static IReadOnlyList<string> Of(McpTool tool)
    {
        if (tool.Capabilities.Count > 0) return tool.Capabilities;
        if (PermissionCatalog.AccessOf(tool) == Access.Read) return [ReadGame];
        if (ByTool.TryGetValue(tool.Name, out var own)) return own;
        return PermissionCatalog.GroupOf(tool) is { } g && ByGroup.TryGetValue(g.Id, out var caps) ? caps : [GameUi];
    }

    /// <summary>Whether the tool can do something critical (destroy items): "always allow" is never kept for it.</summary>
    public static bool IsCritical(McpTool tool) => Of(tool).Any(c => Find(c)?.Risk == RiskLevel.Critical);
}
