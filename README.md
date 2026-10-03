<img src="XivMcp/images/icon.png" width="96" align="right" alt="XIV MCP icon: a brass mammet with an aether crystal antenna">

# XIV MCP

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that runs a local **Model Context Protocol (MCP) server** inside the game client. It gives AI assistants such as Claude Code, Claude Desktop, Cursor or VS Code read-only access to the live data of the logged-in character.

- **Transport:** MCP Streamable HTTP (JSON responses) at `http://localhost:37521/mcp`, bound to localhost only
- **Auth:** bearer token, generated on first start (can be turned off)
- **Read-only game access:** no tool changes game state, sends packets or performs actions. The only tools that change anything are the optional plugin-management and inventory-action tools (see below).

## Tools

| Tool | What it returns |
|---|---|
| `get_game_status` | Logged-in state, zone/map/instance, active duty, PvP/GPose, all active condition flags, Eorzea time |
| `get_character` | Name, worlds, race/clan, nameday, guardian, GC and ranks, job/level, HP/MP/GP/CP, position, statuses, aetherytes, mentor flags, all attributes (stats and substats) |
| `get_class_jobs` | Level, EXP and EXP to next level for every class/job |
| `get_inventory` | Contents of bags, armory, saddlebag, crystals, key items, retainer, FC chest, housing containers |
| `search_inventory` | Where an item is stored and how many you have in total |
| `get_equipment` | Equipped gear with item level, materia, dyes, glamour; average item level |
| `get_currencies` | Gil, MGP, seals, tomestones and weekly cap, wolf marks, currency container |
| `list_unlock_categories` / `check_unlocks` | Unlock or completion state for every `IUnlockState` category: achievements, quests, mounts, minions, emotes, orchestrion, Triple Triad cards, titles, recipes, fashion accessories, glasses, aether currents, duties, … Summary counts, or a filtered list of what's missing or owned |
| `get_active_quests` | Journal quests and their current step, daily (beast tribe) quests, levequests, allowances, tribe reputation |
| `check_quests` | Whether specific quests are completed or accepted, by name or id |
| `get_party` | Party/alliance members with job, HP/MP, zone, distance, statuses |
| `get_targets` | Target, focus, soft and mouse-over target, target of target, cast bars |
| `get_nearby_objects` | Objects around you (players, enemies, NPCs, gathering points, …), filterable by kind, name, distance |
| `get_fates` | Active FATEs in the zone |
| `get_aetherytes` | Attuned aetherytes with teleport costs |
| `get_companions` | Chocobo, pet, trusts / duty support |
| `get_submersibles` | FC submersibles: rank and EXP, parts and build code (e.g. `SSUC`), stats (base + bonus), current route with sector names, return time / ready state, last voyage loot, unlocked and explored sectors per sea. Airships with rank, stats and voyage timing |
| `list_game_sheets` / `search_game_data` / `get_game_data_row` | Generic access to any of the game's Excel sheets (items, quests, achievements, duties, recipes, …) in the client language |

### Plugin management (opt-in)

These tools are disabled until you tick **Allow plugin management** in `/xivmcp`. `list_plugins` always works.

| Tool | What it does |
|---|---|
| `list_plugins` | Installed plugins with internal name, version, load state and flags |
| `set_plugin_enabled` | Enables (loads) or disables (unloads) a plugin, like the installer toggle; the state is saved in the default collection |
| `reload_plugin` | Unloads and loads a plugin again |
| `list_plugin_config_files` | The plugin's `<InternalName>.json` and the files in its config folder |
| `get_plugin_config` | Reads a config file, optionally a sub-tree (`path`, e.g. `Profiles[0].Name`) or a collapsed overview (`depth`) |
| `set_plugin_config` | Sets values by path (`[{ "path": "...", "value": ... }]`), with `dry_run`, type checks and `create_missing` |

How config edits work:

- Plugins keep their config in memory and often save it when they shut down. So `set_plugin_config` unloads a loaded plugin first, re-reads the file, writes the changes, and loads the plugin again.
- The write goes through Dalamud's reliable file storage, so its backup copy stays in sync.
- The previous file is saved to `pluginConfigs/XivMcp/backups/<plugin>/` (the last 10 versions per file are kept).
- Only files inside the target plugin's own config area can be read or written.
- XIV MCP refuses to disable, reload or reconfigure itself.

Dalamud has no public API for loading or unloading other plugins. These tools use Dalamud's internal `PluginManager` the same way the plugin installer does, so they may need adjusting after Dalamud updates. They also make the plugin unsuitable for the official Dalamud repository.

### Retainers

| Tool | What it does |
|---|---|
| `get_retainers` | Retainer list: job, level, gil, venture and when it completes |
| `get_retainer_inventories` | Every retainer's items, market listings (with prices), gear and crystals, from the cache |
| `open_retainer` / `close_retainer` | Opens a retainer's inventory from the summoning bell's retainer list (greeting and menu handled automatically), or closes it again *(inventory actions)* |
| `transfer_retainer_items` | Moves whole stacks retainer → retainer, retainer → bags or bags → retainer. Retainer-to-retainer goes through your bags; every move is confirmed *(inventory actions)* |

`search_inventory` also searches all cached retainer inventories.

### Caches, freshness and watching

The game only sends some data in certain places. XIV MCP snapshots it there and saves it, one copy per character, in `pluginConfigs/XivMcp/`:

| Cache | Refreshed when |
|---|---|
| `submersibles` | You open the voyage control panel in the FC workshop |
| `retainers` | You are at a summoning bell (retainer list), and per retainer while that retainer is open |

Every cached result carries a `cache` block: `capturedAt`, `age`, `live`, `stale` (older than *Cache stale after* hours, default 12) and a `suggestion` that says exactly what to do in game to refresh it.

- `get_cache_status` shows every cache entry with its age. Retainers that were never opened are listed as not captured.
- `wait_for_cache_refresh` waits up to 10 minutes for a cache to refresh. Use it after asking the user to open a retainer or enter the workshop. It works with every MCP client.
- Each cache is also an MCP **resource** (`xiv://cache/submersibles`, `xiv://cache/retainers`) that supports `resources/subscribe`. Clients that open the Streamable-HTTP event stream (`GET /mcp` with their `Mcp-Session-Id`) receive `notifications/resources/updated` whenever the cache refreshes.

### Game interaction (opt-in)

Disabled until you tick **Allow game interaction** in `/xivmcp`.

| Tool | What it does |
|---|---|
| `list_windows` | Game windows that can be opened (the main menu commands), whether each is unlocked and open |
| `open_window` / `close_window` | Opens or closes one, e.g. Achievements (loads achievement progress), Chocobo Saddlebag, Armoury Chest, Currency |
| `interact_with_object` | Targets and interacts with a nearby object by name or id, like clicking it: summoning bell, company chest, voyage control panel, NPCs. Your character doesn't move, so the object must be within about 8 yalms. |
| `get_automation_status` | What XIV MCP detected about AutoRetainer, YesAlready and TextAdvance, and what it is currently pausing |

Windows that act instead of opening something (Log Out, Exit Game, Return, Ready Check, Countdown, Stance) can't be triggered.

**Compatibility with automation plugins** is detected automatically:

- **AutoRetainer.** By default it starts processing ventures as soon as a summoning bell opens. When XIV MCP opens the bell or drives retainer windows, it first suppresses AutoRetainer through AutoRetainer's public IPC (`AutoRetainer.SetSuppressed`). It releases AutoRetainer again a few seconds after the bell is closed, or when XIV MCP unloads. If AutoRetainer is already busy, or its multi mode is on, XIV MCP refuses to touch the bell instead of interfering.
- **YesAlready and TextAdvance** auto-click dialogs and menus. While XIV MCP holds the bell, it pauses them through their shared `StopRequests` sets, the same mechanism AutoRetainer uses.

### Inventory actions (opt-in)

Disabled until you tick **Allow inventory actions** in `/xivmcp`.

| Tool | What it does |
|---|---|
| `sort_inventory` | Runs the game's own `/itemsort` with your conditions (e.g. item level descending, then id) for `inventory`, `armoury` or a single armory slot (`mh`, `head`, `rings`, …), `armoury_slots` (every slot), `saddlebag`, `rightsaddlebag`, `retainer` |
| `move_items` | Moves items like a manual drag: rearrange slots, bags ↔ armory, bags ↔ saddlebag, bags ↔ retainer. Source by slot or by item id (first stack); target slot optional (first empty slot). Occupied targets swap or merge stacks |

**Slots and displayed order.** The game's sort (`/itemsort` and the Sort button) doesn't move items between slots. It only stores a display order per container. So `get_inventory` lists items in the order you see them, and each item has two positions:
- `shown` is its page and slot as displayed in game.
- `slot` is the physical slot, which is what `move_items` expects.

Safeguards:

- **One move at a time.** Each move waits until the game has no inventory operation pending and the server has confirmed it. Then the tool pauses before the next one: by default a random 500–800 ms, picked anew for every move. In `/xivmcp` you can change the range or switch to an exact fixed delay. Results list the pauses actually used (`pausesMs`).
- **Fail fast.** A batch stops at the first failure unless `continue_on_error` is set.
- **Refused states.** Nothing happens in combat, while crafting or gathering, during a trade, in cutscenes, or during zone changes.
- **Open windows required.** Saddlebag and retainer actions need the matching window open, just like by hand.
- **Armory rules.** Gear only goes into its own armory section, and this is checked in both directions when two items would swap.
- **Not covered.** Equipped gear, currency, crystals and key items can't be moved with these tools.

Automating game actions is against the FFXIV ToS. These tools send the same requests as manual drags and the game's own sort command, but use them at your own risk.

The unlock and game-data tools work by reflection over Dalamud's `IUnlockState` and Lumina's sheet types. New categories and sheets therefore show up automatically when Dalamud is updated.

The game only sends submersible and airship data when you open the voyage control panel in the FC workshop. From then on, while you are in the workshop, XIV MCP saves a snapshot every few seconds to `pluginConfigs/XivMcp/workshop.json`, one per character. `get_submersibles` then works from anywhere, including for alts (`all_characters=true`). Return times are absolute, so "voyaging / ready to collect" stays correct between visits. Rank and loot reflect your last visit (`capturedAt`).

Some data is only filled in by the game after the matching window has been opened once per session. This applies to the Achievements window, the title list, the retainer list (summoning bell), the saddlebag and retainer inventories. The tools say so when this is the case.

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

**Claude Code** (the settings window has a button that copies this command with your token filled in):

```sh
claude mcp add --transport http ffxiv http://localhost:37521/mcp --header "Authorization: Bearer <token>"
```

**JSON config** (Cursor, VS Code and other clients that support HTTP MCP servers):

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

Clients that only support stdio servers can use a bridge such as `npx mcp-remote http://localhost:37521/mcp --header "Authorization: Bearer <token>"`.

## Notes

- All game-memory reads run on the game's framework thread. Static sheet lookups run on a background thread.
- The server answers only on `localhost`, rejects browser requests from non-local `Origin`s (DNS rebinding protection) and requires the token by default.
- This is a read-only data plugin, but third-party tools are against the FFXIV ToS. Use at your own discretion, and don't share data about other players.

## License

MIT
