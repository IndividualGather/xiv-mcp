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
/// (navigate_to and visit_world with vnavmesh / Lifestream, buy_item with Item Vendor Location, …) stay core: they also work without.
/// </summary>
public static class IntegrationCatalog
{
    /// <summary>One integration: the plugin's internal name, and per tool the capabilities it uses besides reading.</summary>
    public sealed record Integration(string PluginId, string DisplayName, string Summary, IReadOnlyDictionary<string, string[]> Tools)
    {
        public ToolProvider Provider { get; } = new(PluginId, DisplayName, ProviderTrust.Maintained);

        /// <summary>Tools that other plugins make available as well: offered while any one of them is loaded (e.g. TriadBuddy for reading cards).</summary>
        public IReadOnlyDictionary<string, string[]> AlsoWith { get; init; } = new Dictionary<string, string[]>();

        /// <summary>Tools of the integration that need no plugin at all (the Fashion Report in the Gold Saucer module): always offered.</summary>
        public IReadOnlySet<string> Standalone { get; init; } = new HashSet<string>();

        /// <summary>Plugins that do a part of a tool's work, which the tool also does without (walking, teleporting), like core tools' helpers.</summary>
        public IReadOnlyDictionary<string, ToolRequirement[]> Helpers { get; init; } = new Dictionary<string, ToolRequirement[]>();

        /// <summary>
        /// Other plugins a tool cannot work without, besides its own (ocean fishing's voyages need AutoHook to fish): the tool is only
        /// offered while all of them are loaded, also when it is <see cref="Standalone"/>.
        /// </summary>
        public IReadOnlyDictionary<string, ToolRequirement[]> Requires { get; init; } = new Dictionary<string, ToolRequirement[]>();
    }

    private static ToolRequirement OceanData =>
        new("DistantSeas", Need.Improves, "Its community data names the fish worth going for at each stop.", "Only the route and its stops.");

    private static ToolRequirement OceanOverlay =>
        new("DistantSeas", Need.Improves, "Shows its overlay during the voyage: missions, intuition and spectral timers.", "The game's own voyage display.");

    private static ToolRequirement OceanFisher =>
        new("AutoHook", Need.Needed, "Fishes the voyage: moves to the railing, casts, and switches bait for each stop and spectral current.");

    private static ToolRequirement[] Travel =>
    [
        new("vnavmesh", Need.Improves, "Walks there for you.", "XIV MCP puts a flag on your map and waits while you walk there."),
        new("Lifestream", Need.Improves, "Teleports you to the right zone.", "XIV MCP tells you which aetheryte to teleport to, and waits until you are there."),
    ];

    public static IReadOnlyList<Integration> All { get; } =
    [
        new("AutoDuty", "AutoDuty", "Runs and loops dungeons until a loop count, item or currency target is reached.", new Dictionary<string, string[]>
        {
            ["list_duties"] = [],
            ["get_duty_status"] = [],
            ["stop_duty"] = [Combat],
            ["run_duty"] = [Combat, MoveCharacter, GameUi],
            ["farm_duty_item"] = [Combat, MoveCharacter, GameUi, Network],
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
        new("ItemVendorLocation", "Item Vendor Location", "Which NPCs sell an item and where they stand.", new Dictionary<string, string[]>
        {
            ["find_vendors"] = [],
        }),
        new("FCCH", "FCCH", "Free company chest deposits and withdrawals.", new Dictionary<string, string[]>
        {
            ["fc_chest_transfer"] = [MoveItems, GameUi],
        }),
        new("Saucy", "Gold Saucer", "Triple Triad and the Cactpot lotteries with Saucy; your cards, opponents and decks also with TriadBuddy.", new Dictionary<string, string[]>
        {
            ["list_triad_npcs"] = [],
            ["list_triad_cards"] = [],
            ["get_triad_decks"] = [],
            ["set_triad_deck"] = [EditSettings],
            ["get_saucy_stats"] = [],
            ["build_triad_deck"] = [EditSettings, GameUi],
            ["play_triple_triad"] = [MoveCharacter, GameUi, SpendCurrency],
            ["farm_triad_cards"] = [MoveCharacter, GameUi, SpendCurrency],
            ["play_mini_cactpot"] = [MoveCharacter, GameUi, SpendCurrency, EditSettings],
            ["play_jumbo_cactpot"] = [MoveCharacter, GameUi, SpendCurrency, EditSettings],
            ["stop_saucy"] = [GameUi],
            ["present_fashion_report"] = [MoveCharacter, MoveItems, GameUi],
            ["complete_fashion_report"] = [MoveCharacter, MoveItems, GameUi, SpendGil, Network],
        })
        {
            // The Fashion Report needs no plugin: it belongs here because it happens at the Gold Saucer.
            Standalone = new HashSet<string> { "present_fashion_report", "complete_fashion_report" },
            // Reading cards, opponents and decks needs no automation: TriadBuddy players get it too.
            AlsoWith = new Dictionary<string, string[]>
            {
                ["list_triad_npcs"] = ["TriadBuddy"],
                ["list_triad_cards"] = ["TriadBuddy"],
                ["get_triad_decks"] = ["TriadBuddy"],
                ["set_triad_deck"] = ["TriadBuddy"],
            },
            Helpers = new Dictionary<string, ToolRequirement[]>
            {
                ["play_triple_triad"] = Travel,
                ["farm_triad_cards"] = Travel,
                ["play_mini_cactpot"] = Travel,
                ["play_jumbo_cactpot"] = Travel,
                ["present_fashion_report"] = Travel,
                ["complete_fashion_report"] = Travel,
            },
        },
        new("DistantSeas", "Ocean fishing", "Ocean fishing voyages: the schedule and routes, boarding at the ferry docks, and a job that fishes voyages with AutoHook until a target.", new Dictionary<string, string[]>
        {
            ["get_ocean_fishing_schedule"] = [],
            ["get_ocean_fishing_status"] = [],
            ["board_ocean_fishing"] = [MoveCharacter, GameUi],
            ["set_ocean_fishing_alarm"] = [EditSettings],
            ["import_ocean_presets"] = [EditSettings, Network],
            ["go_ocean_fishing"] = [MoveCharacter, GameUi, SpendGil, EditSettings],
            ["fish_ocean_voyages"] = [MoveCharacter, GameUi, EditSettings],
        })
        {
            Standalone = new HashSet<string> { "get_ocean_fishing_schedule", "get_ocean_fishing_status", "board_ocean_fishing", "go_ocean_fishing", "fish_ocean_voyages", "import_ocean_presets" },
            Helpers = new Dictionary<string, ToolRequirement[]>
            {
                ["get_ocean_fishing_schedule"] = [OceanData],
                ["board_ocean_fishing"] = Travel,
                ["go_ocean_fishing"] = [.. Travel, OceanData, OceanOverlay],
                ["fish_ocean_voyages"] = [.. Travel, OceanOverlay],
            },
            Requires = new Dictionary<string, ToolRequirement[]>
            {
                ["go_ocean_fishing"] = [OceanFisher],
                ["fish_ocean_voyages"] = [OceanFisher],
                ["import_ocean_presets"] = [OceanFisher],
            },
        },
        new("AutoHook", "AutoHook", "Fishing with AutoHook: presets built for the fish you are after, fishing until it is caught, and a job that does it all.", new Dictionary<string, string[]>
        {
            ["set_autohook_preset"] = [EditSettings],
            ["fish_until"] = [GameUi, EditSettings],
            ["catch_fish"] = [MoveCharacter, GameUi, SpendGil, EditSettings],
            ["stop_fishing"] = [GameUi],
        })
        {
            Helpers = new Dictionary<string, ToolRequirement[]>
            {
                ["catch_fish"] = Travel,
            },
        },
        new("Questionable", "Questionable", "Quests with Questionable: a quest with the quests it needs first, the day's allied society quests, and the main scenario.", new Dictionary<string, string[]>
        {
            ["get_questing_status"] = [],
            ["complete_quest"] = [Combat, MoveCharacter, GameUi, EditSettings],
            ["do_tribe_dailies"] = [Combat, MoveCharacter, GameUi, EditSettings],
            ["do_quests"] = [Combat, MoveCharacter, GameUi, EditSettings],
            ["start_main_scenario"] = [Combat, MoveCharacter, GameUi],
            ["stop_questing"] = [GameUi],
        }),
    ];

    /// <summary>Whether a tool of an integration is offered: its plugin, or one of the plugins that also make it available, is loaded.</summary>
    public static bool IsAvailable(string toolName, Func<string, bool> isLoaded) =>
        For(toolName) is not { } i
        || (i.Standalone.Contains(toolName) || isLoaded(i.PluginId) || i.AlsoWith.TryGetValue(toolName, out var others) && others.Any(isLoaded))
           && (!i.Requires.TryGetValue(toolName, out var required) || required.All(r => isLoaded(r.PluginId)));

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
            var name = tool.Name;
            yield return tool.WithProvider(integration.Provider, [ReadGame, .. integration.Tools[name]], () => IsAvailable(name, isLoaded));
        }
    }
}
