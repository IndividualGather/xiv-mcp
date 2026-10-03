<img src="XivMcp/images/icon.png" width="96" align="right" alt="XIV MCP icon: a brass mammet with an aether crystal antenna">

# XIV MCP

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that runs a local **Model Context Protocol (MCP) server** inside the game client. It gives AI assistants such as Claude Code or Codex access to the live data of the logged-in character. Reading is always allowed; everything that changes something is opt-in. Other plugins can add their own tools ([for plugin developers](#for-plugin-developers)).

- **Transport:** MCP Streamable HTTP (JSON responses) at `http://localhost:37521/mcp`, bound to localhost only
- **Auth:** bearer token, generated on first start (can be turned off)
- **Permissions:** every tool that acts in game, edits something or goes online is off until you switch its permission on in `/xivmcp`

## Permissions

`/xivmcp` → **Permissions** has seven switches. All start off.

| Permission | Allows |
|---|---|
| **Game & navigation** | Opening game windows, interacting with objects and NPCs, moving the character (vnavmesh / Lifestream), logging into other characters, world travel, switching gearsets, running dungeons (AutoDuty) |
| **Items & retainers** | Sorting and moving items, retainer and FC chest transfers, sending retainers on ventures (recalling one always asks in game) |
| **Market & purchases** | Buying from NPC vendors (anything not paid in gil asks in game first), putting items up for sale, undercutting listings, reading sale histories |
| **Crafting & gathering** | Artisan crafts and lists, GatherBuddy Reborn auto-gather and lists, preparing crafting projects. Only shown when one of them is installed |
| **UI editing** | Macros and the waymark preset slots |
| **Online lookups** | Item sources (FFXIV Teamcraft, ffxiv.consolegameswiki.com) and market prices (universalis.app) |
| **Plugin management** | Enabling, disabling and reloading plugins, and editing their settings |

Some tools need another plugin. The settings only show compatibility rows and plugin-specific options for plugins that are installed.

**Tools from other plugins.** Other plugins can offer their own tools through XIV MCP ([plugin API](#for-plugin-developers)). Each one appears under **Tools from other plugins** with its tool count and stays off until you switch it on. Its tools then act with that plugin's own logic, so only allow plugins you trust.

## Tools

### Character and world (always available)

| Tool | What it returns |
|---|---|
| `get_game_status` | Logged-in state, zone/map/instance, active duty, PvP/GPose, all active condition flags, Eorzea time |
| `get_character` | Name, worlds, race/clan, nameday, guardian, GC and ranks, job/level, HP/MP/GP/CP, position, statuses, aetherytes, mentor flags, all attributes (stats and substats) |
| `get_class_jobs` | Level, EXP and EXP to next level for every class/job |
| `get_inventory` | Contents of bags, armory, saddlebag, crystals, key items, retainer, FC chest, housing containers |
| `search_inventory` | Where an item is stored and how many you have in total, including cached retainers, saddlebag and FC chest |
| `get_equipment` | Equipped gear with item level, materia, dyes, glamour; average item level |
| `list_gearsets` | Saved gearsets: number, name, job, item level, items missing from the inventory, and the one worn |
| `get_currencies` | Gil, MGP, seals, tomestones and weekly cap, wolf marks, currency container |
| `list_unlock_categories` / `check_unlocks` | Unlock or completion state for every `IUnlockState` category: achievements, quests, mounts, minions, emotes, orchestrion, Triple Triad cards, titles, recipes, … |
| `get_active_quests` / `check_quests` | Journal quests and steps, tribe quests, levequests, allowances, reputation; whether specific quests are done or accepted |
| `get_party` / `get_targets` / `get_nearby_objects` | Party and alliance, targets and cast bars, objects around you |
| `get_fates` / `get_aetherytes` / `get_companions` | FATEs in the zone, attuned aetherytes with costs, chocobo, pet and trusts |
| `get_submersibles` | FC submersibles and airships: rank, parts and build code, stats, route, return time, last loot, explored sectors |
| `list_game_sheets` / `search_game_data` / `get_game_data_row` | Generic access to any of the game's Excel sheets in the client language |
| `inspect_window` | Which game windows are open, or the values and texts one of them shows |

### Collections, actions and macros

| Tool | What it does |
|---|---|
| `get_collections` | Owned / total for mounts, minions, orchestrion rolls, fashion accessories and facewear, armoire and dresser; owned or missing entries of one collection |
| `get_armoire` / `get_glamour_dresser` | Armoire contents (or eligible items not stored yet); dresser items with dyes |
| `get_job_actions` / `get_hotbars` | A job's actions with level, unlock state, timing and cooldowns; every hotbar and cross hotbar slot |
| `get_macros` / `set_macro` / `clear_macro` | Read, create, edit and clear macros, with dry run and backups *(UI editing)* |

### Waymark presets

| Tool | What it does |
|---|---|
| `list_waymark_presets` | The game's 30 preset slots and, if WaymarkPresetPlugin is installed, its library |
| `get_waymark_preset` | Coordinates of a preset (or the waymarks placed now) plus **a picture of the arena** with the waymarks drawn on the duty's map |
| `set_waymark_preset` | Writes a preset slot: copy one, or set marker positions *(UI editing)* |
| `place_waymark_preset` | Places a preset inside a duty, out of combat; library presets go through WaymarkPresetPlugin *(Game & navigation)* |

### Game interaction and navigation *(Game & navigation)*

| Tool | What it does |
|---|---|
| `list_windows` / `open_window` / `close_window` | Main menu windows (Achievements, Saddlebag, Currency, …); `close_window` closes any open window by name |
| `interact_with_object` | Targets and interacts with a nearby object or NPC, like clicking it (summoning bell, company chest, voyage panel, …) |
| `get_menu` / `select_menu_option` | The choice menu the game shows after an interaction, and picking an entry or advancing dialogue |
| `load_game_data` | Asks the game to send achievements or titles without opening their window |
| `navigate_to` | Walks (vnavmesh) and travels (Lifestream) to a summoning bell, company chest, workshop, inn, house, apartment or a named object |
| `get_navigation_status` / `stop_navigation` | What is installed and moving, and an immediate stop |
| `get_automation_status` | What XIV MCP detected about AutoRetainer, YesAlready and TextAdvance, and what it is pausing |

Windows that act instead of opening something (Log Out, Exit Game, Return, Ready Check, Countdown, Stance) can't be triggered through `open_window`.

### Characters, worlds and jobs *(Game & navigation)*

| Tool | What it does |
|---|---|
| `list_characters` | The characters XIV MCP has seen (lobby lists and the character in game): worlds, data center, service account, and whether each is on the account in use |
| `switch_character` | Logs out and into another character of the account, on any world, data center and service account, and waits until it is in game |
| `refresh_character_list` | Logs out and straight back in, so XIV MCP learns this data center's character list |
| `visit_world` | World visit or data center travel for the current character (needs Lifestream) |
| `switch_gearset` | Changes job by equipping a gearset, by number, name or job (the highest item level set of that job) |

Logging in is built in: XIV MCP goes through the game's own lobby (title screen, data center, service account, character list) and confirms only when the game's prompt names the right character. It remembers each character's service account. Accounts are told apart by what XIV MCP observes, so it never tries to reach a character of another account.

### Inventory and retainers *(Items & retainers)*

| Tool | What it does |
|---|---|
| `sort_inventory` | Runs the game's own `/itemsort` with your conditions for bags, armory, saddlebag or retainer |
| `move_items` | Moves items like a manual drag between bags, armory, saddlebag, retainers and the FC chest |
| `get_retainers` / `get_retainer_inventories` | Retainer list (job, level, gil, venture) and every retainer's items, listings, gear and crystals from the cache |
| `open_retainer` / `close_retainer` | Opens a retainer's inventory (or just its menu) at the bell, or closes it |
| `refresh_retainer_inventories` | Opens every (or every stale) retainer once to refresh the cache |
| `transfer_retainer_items` | Moves whole stacks retainer → retainer, retainer → bags or bags → retainer |
| `get_fc_chest` / `fc_chest_transfer` | FC chest contents; deposit or withdraw through **FCCH** (if installed) |
| `find_ventures` | Which venture brings an item, which retainer can take it, and every retainer's current venture |
| `assign_venture` | Sends a retainer (or a free one that can take it) on a venture. If all are busy it suggests which to recall: quick ventures first, then the long ones |
| `recall_venture` | Recalls a retainer from a running venture, **only after you approve it in game** |
| `turn_in_collectables` | Turns in collectables at a Collectable Appraiser for scrips (travels there if needed). Stops before the scrip cap: when the game warns that a trade would overcap, it answers No and stops |

Every move waits until the game and server confirmed the previous one, then pauses (random 500–800 ms by default, adjustable in `/xivmcp`). Nothing happens in combat, while crafting or gathering, in trades, cutscenes or zone changes. Equipped gear, currency, crystals and key items can't be moved.

**Slots and displayed order.** The game's sort only stores a display order, so `get_inventory` lists items as you see them, with `shown` (page and slot in game) and `slot` (the physical slot `move_items` expects).

### Item sources, vendors and the market

| Tool | What it does |
|---|---|
| `get_item_sources` | Where an item comes from: crafting, vendors, currency exchanges, gathering nodes, drops, duties, FATEs, ventures, voyages, desynthesis, … from FFXIV Teamcraft's data, optionally with the wiki page *(Online lookups)* |
| `find_vendors` | NPCs that sell an item, with zone and map position, from the **Item Vendor Location** plugin |
| `buy_item` | Travels to a vendor, opens its shop (gil shops, exchanges and the multi-page scrip exchanges) and buys in batches of up to 99. Gil shops buy directly; any other currency shows an approval popup in game while the shop is open, unless a standing approval is given *(Market & purchases)* |
| `request_spending_approval` | Asks you once in game to approve spending up to an amount of one currency (optionally only on given items, for a while), e.g. for a farming job. `buy_item` with that `approval` then doesn't ask again but never spends more than approved *(Market & purchases)* |
| `list_approvals` / `revoke_approval` | Standing approvals with what is spent and left; revoke one (also in `/xivmcp` → Jobs) |
| `get_market_prices` | Current listings, recent sales, averages and sales per day from universalis.app for your world, data center or region *(Online lookups)* |
| `get_market_listings` | Your retainers' listings from the cache, optionally flagged when undercut (Universalis) |
| `get_sales` | Sale notices the game showed while XIV MCP was running (item, quantity, gil, time) |
| `get_sale_history` | A retainer's last 20 sales (item, quantity, price, buyer, date) at the bell *(Market & purchases)* |
| `sell_item` | Puts a stack up for sale, undercutting the live market (`Compare prices`) unless you give a price *(Market & purchases)* |
| `reprice_listings` | Undercuts competing listings where someone else is cheaper; big cuts are skipped and reported, `dry_run` only reports *(Market & purchases)* |

Undercuts follow **Penny Pincher**'s settings when it is installed (amount, rounding, minimum, HQ, never your own retainers), otherwise 1 gil. A price below half the recent Universalis average is refused unless allowed.

### Crafting and gathering *(Crafting & gathering, needs Artisan and/or GatherBuddy Reborn)*

| Tool | What it does |
|---|---|
| `get_crafting_lists` / `set_crafting_list` / `delete_crafting_list` | Artisan's lists and state; create, edit or delete lists |
| `craft_item` / `crafting_control` | Craft an item N times; start, pause, resume or stop a list |
| `get_gather_lists` / `set_gather_list` / `delete_gather_list` / `set_auto_gather` | GatherBuddy Reborn's auto-gather lists and auto-gather on/off |
| `plan_craft` | Plans a project: recipe tree, craft order, stock from bags and retainers, where to get missing materials, bag space and batches, and the stats Artisan will craft with. `"quantity": "fill"` plans as many as fit in the bags; for collectables also no more than can be turned in before the scrip cap, with the scrip per collectability tier *(always available)* |
| `prepare_craft_plan` | Creates the Artisan list in the right order and builds the **Raphael** solution for each recipe ahead of time, with the exact stats Artisan will use |
| `gather_until` | Gathers until the bags hold the target quantities (your other lists are paused and restored) |
| `run_crafting_list` | Runs an Artisan list to the end and reports what was crafted |

Neither plugin has IPC for its lists, so editing one briefly unloads the plugin, updates its file (with a backup) and loads it again. Raphael preparation uses Artisan's internals and may need an update after Artisan changes.

### Dungeons *(Game & navigation, only while AutoDuty is loaded)*

| Tool | What it does |
|---|---|
| `list_duties` | Duties AutoDuty has a path for, with level and item level and the modes it can run them in (Support, Trust, Squadron, Regular, …) |
| `run_duty` | Runs a duty with AutoDuty, `loops` times or `until` the inventory holds the items or currency you want (e.g. a drop, or tomestones: `{ item, quantity }` or `{ item, gain }`). `gearset` switches to a combat job first; on a crafter or gatherer it is required |
| `get_duty_status` / `stop_duty` | What AutoDuty is doing (stage, duty, loop); stop it (after the current fight) |
| `leave_duty` | Leaves the current duty like the Duty Finder's Leave entry; if AutoDuty is running it is stopped first, after the current fight (works without AutoDuty too) |

AutoDuty does the running and the looping. For a `run_duty` call, its loop count, duty mode, unsynced setting and stop conditions are set temporarily through its IPC overrides (never saved), and its termination action is set to do nothing. They are restored afterwards. Its "stop at item quantity" list has no IPC, so it is swapped in memory while the run lasts and put back after. This may need an update when AutoDuty changes. A run takes about 20 minutes, so use `run_duty` as a job step. Pausing or cancelling the job (or its timeout) stops AutoDuty and leaves the duty, but **never mid-fight**: AutoDuty keeps fighting until you have been out of combat for a few seconds, and is resumed if a fight starts again before you are out. These tools disappear from the tool list while AutoDuty isn't loaded, and clients are notified (`notifications/tools/list_changed`).

### Background jobs

A job is a queue of tool calls XIV MCP runs in the game, one after another, for as long as they take. It doesn't depend on the conversation or connection.

| Tool | What it does |
|---|---|
| `start_job` | Starts a job with its steps (tool + arguments); later steps can use earlier results, e.g. `"list": "{{plan.list.id}}"` |
| `list_jobs` / `get_job` | Jobs with state, progress and the current step; one job's steps, results, errors and log |
| `update_job` | For a pending or paused job: retry or skip the current step, append steps or replace the remaining ones |
| `pause_job` / `resume_job` / `cancel_job` | Control a job; also available in game in `/xivmcp` → **Jobs** |
| `wait` | A step that waits a number of seconds or until a time |

When a step fails or is stopped, the job becomes **pending** and waits for the agent to fix it. While a step runs, other changing tool calls are refused so they can't collide with the job. Jobs survive plugin reloads and come back paused. Each step still needs its own permission.

A string argument `"{{stepId.path}}"` takes the value from an earlier step's result, keeping its type. Inside a longer string (`"Made {{craft.crafted}} pies"`), it's replaced by the value as text.

Example: `prepare_craft_plan` → `gather_until` (the missing materials) → `run_crafting_list` with `{{plan.list.id}}`.

Farming a drop or currency: one `run_duty` step with `"until": [{ "item": "Allagan Tomestone of Poetics", "gain": 500 }]`, which loops the dungeon until the target is reached.

A scrip farming round as one job: `prepare_craft_plan` with `"quantity": "fill"` for a collectable → `gather_until` / `transfer_retainer_items` for the materials → `run_crafting_list` → `turn_in_collectables` (stops before the scrip cap) → `buy_item` with a standing `approval` → `sell_item`.

MCP's own task mechanism (the Tasks extension) isn't used because no common client supports it yet. Jobs work with every client through ordinary tools.

### Plugin management *(Plugin management)*

| Tool | What it does |
|---|---|
| `list_plugins` | Installed plugins with internal name, version, load state and flags (always available) |
| `set_plugin_enabled` / `reload_plugin` | Enables, disables or reloads a plugin, like the installer toggle |
| `list_plugin_config_files` / `get_plugin_config` / `set_plugin_config` | Lists, reads and changes plugin settings by path, with dry run and type checks |

Plugins keep their settings in memory and often save on shutdown, so `set_plugin_config` unloads a loaded plugin, writes the change through Dalamud's reliable file storage and loads it again. The previous file is backed up to `pluginConfigs/XivMcp/backups/<plugin>/` (last 10 per file). XIV MCP refuses to manage itself. Dalamud has no public API for this, so these tools use its internal `PluginManager` like the installer does and may need adjusting after Dalamud updates.

## Approvals in game

Some actions are never decided by the assistant alone: buying with anything but gil, and recalling a running venture. They open an approval window in game, and nothing happens until you click **Approve**. Unanswered requests are declined after two minutes.

For jobs that buy repeatedly, a **standing approval** asks once: "spend up to N of this currency (on these items, for this long)". Every purchase under it counts its real cost (measured from the currency balance), stops when the approved amount is used up, and stops if a purchase turns out to be paid with another currency. Active approvals are listed in `/xivmcp` → Jobs with a Revoke button.

## Caches, freshness and watching

The game only sends some data in certain places. XIV MCP snapshots it there and keeps it per character in `pluginConfigs/XivMcp/`:

| Cache | Refreshed when |
|---|---|
| `submersibles` | Whenever the voyage control panel data is loaded in the FC workshop |
| `retainers` | Whenever the game has the data: the retainer list at a bell, and each retainer's inventory when it is selected (also by plugins like AutoRetainer) |
| `glamour` | Glamour dresser: whenever it or a glamour plate is opened (the armoire is read live) |
| `progress` | Achievements and titles: whenever the game has the lists loaded |
| `storage` | Saddlebag and FC chest: whenever they are loaded |
| `jobs` | Background jobs, live |

Every cached result carries a `cache` block (`capturedAt`, `age`, `live`, `stale`, and a `suggestion` for refreshing it). `get_cache_status` lists every entry; `wait_for_cache_refresh` waits up to 10 minutes for a cache to refresh. Each cache is also an MCP **resource** (`xiv://cache/<id>`) supporting `resources/subscribe`: clients that open the event stream (`GET /mcp` with their `Mcp-Session-Id`) receive `notifications/resources/updated`.

XIV MCP also keeps what it learns about item sources (Teamcraft's data), characters (`characters.json`) and sale notices (`sales.json`) in the same folder.

## Compatibility with other plugins

XIV MCP detects these automatically and only mentions the installed ones in its settings:

- **AutoRetainer** starts processing ventures as soon as a bell opens. While XIV MCP uses the bell, it suppresses AutoRetainer through its public IPC and releases it a few seconds after the bell is closed. If AutoRetainer is busy or in multi mode, XIV MCP doesn't touch the bell.
- **YesAlready and TextAdvance** are paused through their `StopRequests` sets while XIV MCP drives game windows.
- **FCCH** handles FC chest transfers; item moves wait while it works.
- **WaymarkPresetPlugin** provides its preset library.
- **vnavmesh** and **Lifestream** do walking and travel; **Artisan** and **GatherBuddy Reborn** do crafting and gathering.
- **AutoDuty** runs and loops dungeons for the dungeon tools, which only exist while it is loaded.
- **Item Vendor Location** finds vendors (an Install button appears under *Market & purchases* when it's missing); **Penny Pincher**'s settings drive undercuts.

Automating game actions is against the FFXIV ToS. XIV MCP sends the same requests as manual input, but use it at your own risk.

## Building

Requirements: .NET 10 SDK, and XIVLauncher with Dalamud installed (the build references `%APPDATA%\XIVLauncher\addon\Hooks\dev`).

```sh
dotnet build XivMcp.slnx
```

The plugin is written to `XivMcp/bin/Debug/XivMcp.dll`.

## Installing as a dev plugin

1. In game, open `/xlsettings` → **Experimental** → **Dev Plugin Locations**, add the full path to `XivMcp/bin/Debug/XivMcp.dll`, and save.
2. Open `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** and enable **XIV MCP**.
3. Run `/xivmcp` to open the settings window, which shows the server status, port, token and copy-paste client configs.

## Connecting a client

The **Connect** tab in `/xivmcp` has copy buttons for everything below, with your token already filled in.

**Claude Code:**

```sh
claude mcp add --transport http ffxiv http://localhost:37521/mcp --header "Authorization: Bearer <token>"
```

**Codex:** Codex reads the token from an environment variable. Open a new terminal after `setx` and before starting Codex:

```sh
setx XIVMCP_TOKEN "<token>"
codex mcp add ffxiv --url http://localhost:37521/mcp --bearer-token-env-var XIVMCP_TOKEN
```

Or add the server to `~/.codex/config.toml` directly:

```toml
[mcp_servers.ffxiv]
url = "http://localhost:37521/mcp"
http_headers = { "Authorization" = "Bearer <token>" }
```

**Other MCP clients:** clients that read an `mcpServers` JSON configuration and support HTTP servers:

```json
{
  "mcpServers": {
    "ffxiv": {
      "type": "http",
      "url": "http://localhost:37521/mcp",
      "headers": { "Authorization": "Bearer <token>" }
    }
  }
}
```

**The token** is generated once on first start and saved with the plugin settings. It stays the same across game restarts until you click **Regenerate** in `/xivmcp`.

## For plugin developers

Your Dalamud plugin can offer tools to AI assistants through XIV MCP, and start background jobs, over Dalamud IPC. You don't need a server or a reference to XIV MCP, and your plugin keeps working without it.

```csharp
mcp = new XivMcpClient(pluginInterface);                     // examples/XivMcpClient.cs, copied into your plugin
mcp.AddTool(new("myplugin_status", "What My Plugin is doing right now.") { ReadOnly = true },
            args => new { mode = Mode.ToString() });
```

- **Guide:** [docs/plugin-api.md](docs/plugin-api.md) covers quick start, writing tools assistants use well, long-running tools and cancellation, jobs, the IPC reference and troubleshooting.
- **Examples:** [examples/](examples) holds the drop-in client `XivMcpClient.cs` and *Hello MCP*, a complete example plugin with a quick tool, an acting tool, a long-running tool and a job.

Five things to get right:

1. **Prefix tool names** with your plugin's name, and start them with a verb: `myplugin_set_mode`.
2. **Write descriptions for the assistant:** what the tool does, when to use it, what it needs, and what comes before or after.
3. **Set `ReadOnly` honestly.** Acting tools are held back while a job step runs, which prevents collisions.
4. **Keep quick tools quick.** They run on the framework thread. Anything that waits is a long-running tool.
5. **Cancel safely:** finish the fight, close the window, then stop.

## Notes

- All game-memory reads run on the game's framework thread. Static sheet lookups run on a background thread.
- The server answers only on `localhost`, rejects browser requests from non-local `Origin`s (DNS rebinding protection) and requires the token by default.
- By default XIV MCP only reads data; everything that acts in game is opt-in. Third-party tools are against the FFXIV ToS, so use it at your own discretion, and don't share data about other players.

## License

MIT
