using System.Collections.Generic;

namespace XivMcp.Mcp;

/// <summary>
/// Plain-language descriptions of XIV MCP's own tools for the player: what the tool does in the game, in one short sentence. The tools'
/// own descriptions stay as they are; they are written for the assistant (arguments, preconditions, edge cases). Style rules: one
/// sentence (two when the second names a cost or a safety behaviour), 100 characters at most, no contractions, no plugin names;
/// reading tools start with "Looks at", "Checks", "Lists", "Finds" (or "Waits"); costs, risks and safety behaviour are named.
/// </summary>
public static class ToolSummaries
{
    public static IReadOnlyDictionary<string, string> All { get; } = new Dictionary<string, string>
    {
        // Game data
        ["get_game_status"] = "Checks whether you are logged in, where you are and what you are doing.",
        ["get_character"] = "Looks at your character: job, level, stats, position and status effects.",
        ["get_class_jobs"] = "Lists the levels and experience of all your classes and jobs.",
        ["get_inventory"] = "Looks at your bags, armoury, saddlebags, crystals and other containers.",
        ["search_inventory"] = "Finds where you keep an item and how many you have, including your retainers.",
        ["get_equipment"] = "Looks at the gear you are wearing, with its materia, dyes and glamours.",
        ["get_currencies"] = "Checks your gil, tomestones, scrips, seals and other currencies.",
        ["list_unlock_categories"] = "Lists the kinds of unlocks it can check, such as mounts, emotes or recipes.",
        ["check_unlocks"] = "Checks which achievements, mounts, minions, recipes and more you have unlocked.",
        ["get_active_quests"] = "Lists the quests in your journal and how far along you are.",
        ["check_quests"] = "Checks whether particular quests are done or accepted.",
        ["get_party"] = "Looks at your party and alliance members.",
        ["get_targets"] = "Checks what you and your target are targeting and casting.",
        ["get_nearby_objects"] = "Lists the players, NPCs and objects around you.",
        ["get_fates"] = "Lists the FATEs in your current zone.",
        ["get_aetherytes"] = "Lists the aetherytes you have attuned to, with their teleport costs.",
        ["get_companions"] = "Checks your chocobo companion, pet and trust members.",
        ["get_submersibles"] = "Checks your free company submersibles and airships: routes, return times and loot.",
        ["list_game_sheets"] = "Lists the game data tables, such as items, quests and actions.",
        ["search_game_data"] = "Finds entries in the game data, such as an item or a quest by name.",
        ["get_game_data_row"] = "Looks at one entry of the game data in full.",
        ["inspect_window"] = "Looks at what an open game window shows.",
        ["get_collections"] = "Checks how many mounts, minions, orchestrion rolls and other collectibles you own.",
        ["get_armoire"] = "Looks at what is stored in your armoire.",
        ["get_glamour_dresser"] = "Looks at the items in your glamour dresser.",
        ["get_job_actions"] = "Lists the actions of a job, with their levels and cooldowns.",
        ["get_hotbars"] = "Looks at what is on your hotbars.",
        ["list_gearsets"] = "Lists your saved gear sets.",
        ["get_cache_status"] = "Checks how recently XIV MCP saw your retainers, chests and submersibles.",
        ["wait_for_cache_refresh"] = "Waits up to 10 minutes for you to open a retainer or chest, so XIV MCP sees it again.",
        ["run_self_test"] = "Checks that XIV MCP and its connected plugins work. Changes nothing.",
        ["plan_craft"] = "Checks what a crafting project needs: crafting order, missing materials and where to get them.",
        ["list_plugins"] = "Lists your installed Dalamud plugins.",

        // Game & navigation
        ["list_windows"] = "Lists the main menu windows it can open, such as the armoury or currencies.",
        ["get_menu"] = "Looks at the choices of the menu or dialogue that is open.",
        ["get_automation_status"] = "Checks which automation plugins XIV MCP pauses or waits for.",
        ["get_navigation_status"] = "Checks whether your character is walking or travelling somewhere.",
        ["list_characters"] = "Lists the characters XIV MCP has seen on your worlds and accounts.",
        ["open_window"] = "Opens a game window, such as your armoury or currencies.",
        ["close_window"] = "Closes an open game window.",
        ["recover_game_state"] = "Frees your character from a stuck conversation or window, pressing Escape if needed.",
        ["list_triad_npcs"] = "Lists Triple Triad opponents with where they stand, their rules and which of their cards you have.",
        ["list_triad_cards"] = "Lists Triple Triad cards you have or are missing, and who gives them.",
        ["get_triad_decks"] = "Looks at your saved Triple Triad decks.",
        ["set_triad_deck"] = "Saves five of your Triple Triad cards as one of your decks.",
        ["get_saucy_stats"] = "Checks how many Triple Triad matches were played, won and lost, and the cards and MGP won.",
        ["build_triad_deck"] = "Builds the best deck against a Triple Triad opponent and saves it as your fifth deck.",
        ["play_triple_triad"] = "Goes to a Triple Triad opponent and plays a number of matches, or until a card drops.",
        ["farm_triad_cards"] = "Collects missing Triple Triad cards from one opponent, or from opponent to opponent.",
        ["play_mini_cactpot"] = "Goes to the Mini Cactpot broker and plays your tickets for today.",
        ["play_jumbo_cactpot"] = "Collects your Jumbo Cactpot prizes and buys this week's tickets with your numbers or random ones.",
        ["stop_saucy"] = "Stops Triple Triad and travel that are running at the Gold Saucer.",
        ["show_xivmcp_window"] = "Opens the XIV MCP window on a tab, for example to show you a job or a plugin.",
        ["press_xivmcp_control"] = "Presses a button in the XIV MCP window, for testing the plugin while it is developed.",
        ["buy_from_market_board"] = "Buys an item on the market board, the cheapest offers first, up to your price limit.",
        ["farm_duty_item"] = "Runs a dungeon that drops an item until you have as many as you want.",
        ["equip_items"] = "Puts on pieces of gear from your bags or armoury chest.",
        ["retrieve_glamour_item"] = "Takes an item out of your glamour dresser or armoire into your bags.",
        ["get_fashion_report"] = "Looks at this week's Fashion Report: theme, hints, ready-made sets and which items you have.",
        ["present_fashion_report"] = "Puts on your Fashion Report outfit and presents it to the Masked Rose for judging.",
        ["complete_fashion_report"] = "Gets, dyes and puts on this week's easy Fashion Report set, then presents it for judging.",
        ["dye_item"] = "Dyes a piece of your gear in a color, using a dye from your bags.",
        ["capture_ui_events"] = "Records what game windows send while you click, for developing new tools.",
        ["take_screenshot"] = "Looks at your screen: takes a screenshot of the game or of the XIV MCP window.",
        ["run_autoretainer"] = "Opens a summoning bell and lets AutoRetainer handle your retainers.",
        ["interact_with_object"] = "Talks to an NPC or uses an object near you, such as a summoning bell.",
        ["select_menu_option"] = "Clicks a choice in an open menu or dialogue. It can confirm purchases or discards.",
        ["load_game_data"] = "Has the game send your achievements or your titles, without opening their windows.",
        ["navigate_to"] = "Gets you to a bell, your house, an inn or a named place, or shows the way. Teleports cost gil.",
        ["stop_navigation"] = "Stops your character from walking or travelling.",
        ["switch_character"] = "Logs out and logs in as another of your characters, on any world.",
        ["refresh_character_list"] = "Logs out and back in, so XIV MCP learns your list of characters.",
        ["switch_gearset"] = "Changes your job by equipping one of your saved gear sets, never during combat.",
        ["leave_duty"] = "Leaves the duty you are in, once the current fight is over.",
        ["place_waymark_preset"] = "Places a saved set of waymarks in your current duty, outside of combat.",
        ["visit_world"] = "Takes your character to another world or data center, or tells you how to get there.",

        // Items & retainers
        ["get_retainers"] = "Lists your retainers with their jobs, gil and current ventures.",
        ["get_retainer_inventories"] = "Looks at what your retainers are holding and selling.",
        ["get_fc_chest"] = "Looks at what is in your free company chest.",
        ["find_ventures"] = "Finds which venture brings back an item and which retainer can go.",
        ["sort_inventory"] = "Sorts your bags, armoury, saddlebag or a retainer inventory with the game's own sorting.",
        ["move_items"] = "Moves items between your bags, armoury, saddlebag, retainers and chest, one at a time.",
        ["open_retainer"] = "Calls a retainer at the summoning bell.",
        ["close_retainer"] = "Dismisses the retainer you are talking to.",
        ["transfer_retainer_items"] = "Moves stacks of items between your retainers and your bags.",
        ["refresh_retainer_inventories"] = "Calls each retainer once at a summoning bell next to you, so XIV MCP knows what they hold.",
        ["assign_venture"] = "Sends a retainer on a venture. Costs venture tokens.",
        ["recall_venture"] = "Calls a retainer back from a running venture, only after you approve it.",
        ["turn_in_collectables"] = "Takes you to an appraiser and hands in your collectables for scrips, stopping before the scrip cap.",

        // Market & purchases
        ["get_market_listings"] = "Looks at what your retainers are selling and whether someone sells cheaper.",
        ["get_sales"] = "Lists the sales your retainers made while XIV MCP was running.",
        ["list_approvals"] = "Lists the spending approvals you have given.",
        ["buy_item"] = "Buys an item from a vendor. Purchases not paid in gil ask you first.",
        ["request_spending_approval"] = "Asks you to allow spending up to an amount of one currency, so later purchases need no more asking.",
        ["revoke_approval"] = "Withdraws a spending approval you have given.",
        ["sell_item"] = "Lists an item through a retainer, just below the cheapest offer. Prices far too low are refused.",
        ["sell_to_vendor"] = "Sells items from your bags to a merchant for gil, while the shop is open.",
        ["reprice_listings"] = "Undercuts cheaper offers on your listings. Cuts of more than 30 percent are skipped.",
        ["get_sale_history"] = "Calls a retainer at the summoning bell to look at their recent sales.",

        // UI editing
        ["get_macros"] = "Looks at your macros.",
        ["list_waymark_presets"] = "Lists your saved waymark presets.",
        ["get_waymark_preset"] = "Looks at where a waymark preset places its markers.",
        ["set_macro"] = "Writes or changes a macro. The old macro is backed up first.",
        ["clear_macro"] = "Deletes a macro. It is backed up first.",
        ["set_waymark_preset"] = "Saves a waymark preset to one of your preset slots, replacing what was there.",

        // Online lookups
        ["get_item_sources"] = "Finds online where an item comes from: crafting, vendors, drops or gathering.",
        ["get_market_prices"] = "Checks current market board prices online.",
        ["repair_submersible"] = "Repairs a submersible's parts with Magitek Repair Materials.",
        ["recall_submersible"] = "Recalls a submersible from its voyage; the Ceruleum Tanks used are not refunded.",
        ["collect_submersible"] = "Collects what returned submersibles found, and shows their new rank and sectors.",
        ["deploy_submersible"] = "Sends submersibles on a voyage, on their last route or a new one. Uses Ceruleum Tanks.",
        ["move_gil"] = "Moves gil between you and a retainer or the free company chest.",
        ["get_trade"] = "Looks at the trade window: who you trade with and what each side offers.",
        ["trade_with_player"] = "Trades items and gil with a player next to you. Every trade asks you first.",
        ["cancel_trade"] = "Cancels the open trade. Nothing changes hands.",
        ["find_fish"] = "Finds online where, when and with which bait a fish bites, and how to catch it.",
        ["set_autohook_preset"] = "Sets up automatic hooking for the fish you are after.",
        ["fish_until"] = "Fishes until you have caught a fish, waiting for its time and weather.",
        ["get_custom_deliveries"] = "Lists this week's custom deliveries: each client's requests, rank and deliveries left.",
        ["deliver_custom_delivery"] = "Delivers one client's request by crafting, gathering or fishing it, with Satisfier.",
        ["do_custom_deliveries"] = "Does this week's custom deliveries for all clients, one after another, as a job.",
        ["stop_custom_delivery"] = "Stops the custom delivery Satisfier is doing.",
        ["get_ocean_fishing_schedule"] = "Lists the next ocean fishing voyages, their stops and the fish worth going for.",
        ["get_ocean_fishing_status"] = "Checks the voyage you are on: the stop, time left, spectral current, missions and points.",
        ["board_ocean_fishing"] = "Goes to the ferry docks in Limsa Lominsa and boards the voyage while boarding is open.",
        ["import_ocean_presets"] = "Imports AutoHook's ocean fishing presets for a goal: points, legends, achievements or levelling.",
        ["set_ocean_fishing_alarm"] = "Turns the Distant Seas departure alarm on or off.",
        ["go_ocean_fishing"] = "Fishes ocean voyages with AutoHook until your target: voyages, points or a fish.",
        ["fish_ocean_voyages"] = "Boards and fishes voyages one after another, as a step of an ocean fishing job.",
        ["catch_fish"] = "Catches a fish for you: travels there, buys bait, waits for its window and fishes.",
        ["stop_fishing"] = "Stops fishing and reels in your line.",
        ["abandon_quest"] = "Abandons a quest in your journal. Its progress is lost.",
        ["get_questing_status"] = "Looks at which quest is being done for you, and at your allied society reputation.",
        ["complete_quest"] = "Does a quest for you, together with the quests it needs first.",
        ["do_tribe_dailies"] = "Does the day's allied society quests for you, rank-up quests included.",
        ["do_quests"] = "Does quests for you until they are done.",
        ["start_main_scenario"] = "Starts doing the main scenario quests for you.",
        ["stop_questing"] = "Stops doing quests for you, once a fight is over.",

        // Plugin management
        ["list_plugin_config_files"] = "Lists the settings files of another plugin.",
        ["get_plugin_config"] = "Looks at the settings of another plugin, including any passwords or keys stored there.",
        ["set_plugin_enabled"] = "Turns another plugin on or off.",
        ["reload_plugin"] = "Restarts another plugin.",
        ["set_plugin_config"] = "Changes the settings of another plugin. The old settings are backed up first.",

        // Background jobs
        ["list_jobs"] = "Lists your background jobs and how far they have got.",
        ["get_job"] = "Looks at the steps and log of one background job.",
        ["start_job"] = "Starts a background job: tasks that run one after another, each following your settings here.",
        ["update_job"] = "Retries, skips or changes the steps of a background job.",
        ["pause_job"] = "Pauses a background job.",
        ["resume_job"] = "Continues a paused background job.",
        ["cancel_job"] = "Stops a background job.",
        ["wait"] = "Waits a while, as a step of a background job.",
        ["wait_until_arrived"] = "Waits until you reach a spot on the map, as a step of a background job.",
        ["get_position"] = "Looks at where you are: zone, area, map coordinates and the flag on your map.",
        ["get_weather_forecast"] = "Checks the weather forecast of a zone and when a weather comes next.",
        ["set_map_flag"] = "Places the flag on your map at a spot in any zone, and can open the map.",
        ["clear_map_flag"] = "Removes the flag from your map.",
        ["start_route"] = "Plans a route as a background job: flags each stop on your map once you reach the one before.",

        // AutoDuty
        ["list_duties"] = "Lists the duties that can be run for you, with their levels and modes.",
        ["get_duty_status"] = "Checks whether a dungeon run is going on and how far it has got.",
        ["stop_duty"] = "Stops the dungeon run once the current fight is over.",
        ["run_duty"] = "Runs a dungeon with NPC allies or a party, as often as needed. Your character can be defeated.",

        // Artisan
        ["get_crafting_lists"] = "Lists your crafting lists.",
        ["set_crafting_list"] = "Creates or changes a crafting list. The old lists are backed up first.",
        ["delete_crafting_list"] = "Deletes a crafting list. The old lists are backed up first.",
        ["craft_item"] = "Crafts an item a number of times, using your materials.",
        ["crafting_control"] = "Starts, pauses or stops a crafting list.",
        ["run_crafting_list"] = "Crafts a whole crafting list, using your materials.",
        ["prepare_craft_plan"] = "Writes a crafting list for a project and works out each craft in advance. Crafts nothing yet.",

        // GatherBuddy Reborn
        ["get_gather_lists"] = "Lists your gathering lists.",
        ["set_gather_list"] = "Creates or changes a gathering list. The old lists are backed up first.",
        ["delete_gather_list"] = "Deletes a gathering list. The old lists are backed up first.",
        ["set_auto_gather"] = "Turns automatic gathering on or off. While it is on, your character moves and gathers on its own.",
        ["gather_until"] = "Gathers until you have the amounts you need, moving your character from node to node.",

        // Item Vendor Location
        ["find_vendors"] = "Finds which NPCs sell an item and where they stand.",

        // FCCH
        ["fc_chest_transfer"] = "Puts items into the free company chest or takes them out.",
    };

    /// <summary>The player-facing text for a tool, or null (third-party tools, or one not written yet).</summary>
    public static string? For(string toolName) => All.GetValueOrDefault(toolName);
}
