using System;
using System.Collections.Generic;
using System.Linq;
using XivMcp.Mcp;
using static XivMcp.Permissions.Capabilities;

namespace XivMcp.Integrations;

/// <summary>
/// The integrations XIV MCP maintains: tools whose whole purpose is driving one other plugin. They register through the same
/// provider + capability contract as third-party plugins, under a <see cref="ProviderTrust.Maintained"/> provider named after the
/// plugin they drive, and are only offered while that plugin is loaded. Core tools that merely use a plugin as an optional backend
/// (navigate_to with vnavmesh / Lifestream, buy_item with Item Vendor Location, …) stay core.
/// </summary>
public static class IntegrationCatalog
{
    /// <summary>One integration: the plugin's internal name, and per tool the capabilities it uses besides reading.</summary>
    public sealed record Integration(string PluginId, string DisplayName, string Summary, IReadOnlyDictionary<string, string[]> Tools)
    {
        public ToolProvider Provider { get; } = new(PluginId, DisplayName, ProviderTrust.Maintained);
    }

    public static IReadOnlyList<Integration> All { get; } =
    [
        new("AutoDuty", "AutoDuty", "Runs and loops dungeons until a loop count, item or currency target is reached.", new Dictionary<string, string[]>
        {
            ["list_duties"] = [],
            ["get_duty_status"] = [],
            ["stop_duty"] = [Combat],
            ["run_duty"] = [Combat, MoveCharacter, GameUi],
        }),
        new("Artisan", "Artisan", "Crafts and crafting lists, and Raphael solutions prepared ahead of time.", new Dictionary<string, string[]>
        {
            ["get_crafting_lists"] = [],
            ["set_crafting_list"] = [EditSettings],
            ["delete_crafting_list"] = [EditSettings],
            ["craft_item"] = [GameUi],
            ["crafting_control"] = [GameUi],
            ["run_crafting_list"] = [GameUi],
            ["prepare_craft_plan"] = [EditSettings],
        }),
        new("GatherbuddyReborn", "GatherBuddy Reborn", "Auto-gathering and its gather lists.", new Dictionary<string, string[]>
        {
            ["get_gather_lists"] = [],
            ["set_gather_list"] = [EditSettings],
            ["delete_gather_list"] = [EditSettings],
            ["set_auto_gather"] = [MoveCharacter, GameUi],
            ["gather_until"] = [MoveCharacter, GameUi, EditSettings],
        }),
        new("Lifestream", "Lifestream", "World visits and data center travel.", new Dictionary<string, string[]>
        {
            ["visit_world"] = [MoveCharacter, SpendGil],
        }),
        new("ItemVendorLocation", "Item Vendor Location", "Which NPCs sell an item and where they stand.", new Dictionary<string, string[]>
        {
            ["find_vendors"] = [],
        }),
        new("FCCH", "FCCH", "Free company chest deposits and withdrawals.", new Dictionary<string, string[]>
        {
            ["fc_chest_transfer"] = [MoveItems, GameUi],
        }),
    ];

    /// <summary>The integration a tool belongs to, if any.</summary>
    public static Integration? For(string toolName) => All.FirstOrDefault(i => i.Tools.ContainsKey(toolName));

    /// <summary>Moves the tools that belong to an integration to its provider; <paramref name="isLoaded"/> tells whether a plugin is loaded.</summary>
    public static IEnumerable<McpTool> Apply(IEnumerable<McpTool> tools, Func<string, bool> isLoaded)
    {
        foreach (var tool in tools)
        {
            if (For(tool.Name) is not { } integration)
            {
                yield return tool;
                continue;
            }
            var plugin = integration.PluginId;
            yield return tool.WithProvider(integration.Provider, [ReadGame, .. integration.Tools[tool.Name]], () => isLoaded(plugin));
        }
    }
}
