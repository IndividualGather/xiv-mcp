# XIV MCP plugin API

Offer your Dalamud plugin's features to AI assistants through XIV MCP: register tools the assistant can call, ask the player for approval at risky moments, and start background jobs. This guide covers **API version 2**. If you only want to use XIV MCP, see the [README](../README.md). The same guide, split into pages, is in the [developer docs](https://individualgather.github.io/xiv-mcp/docs/developer/plugin-api/).

- [Quick start](#quick-start)
- [How to …](#how-to-)
- [Capabilities: what to declare](#capabilities-what-to-declare)
- [What the player controls](#what-the-player-controls)
- [Client reference](#client-reference)
- [Rules and limits](#rules-and-limits)
- [Troubleshooting](#troubleshooting)
- [Appendix: raw IPC](#appendix-raw-ipc)

## Quick start

1. Copy [`examples/XivMcpClient.cs`](../examples/XivMcpClient.cs) into your plugin. It needs only Dalamud and `System.Text.Json`, and works whether or not XIV MCP is installed.
2. Create the client, add tools, and dispose it:

```csharp
using XivMcp.Client;

public sealed class Plugin : IDalamudPlugin
{
    private readonly XivMcpClient mcp;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        mcp = new XivMcpClient(pluginInterface);
        mcp.RegistrationFailed += message => Log.Warning($"XIV MCP rejected a tool: {message}");

        mcp.AddTool(
            new McpToolDefinition("myplugin_status", "Whom My Plugin is following right now, if anyone.") { ReadOnly = true },
            args => new { following = FollowTarget?.Name });
    }

    public void Dispose() => mcp.Dispose();
}
```

3. Load your plugin. The player gets a notification that your plugin wants to register; they click **Enable** (or open `/xivmcp` → **Third-party plugins**).
4. Ask the assistant to "check My Plugin's status". It calls `myplugin_status`.

[Hello MCP](../examples/HelloMcp/Plugin.cs) is a complete example plugin with a tool of each kind and a job.

## How to …

### Add a tool that changes something

Anything that isn't read-only must declare what it does. The player decides, per capability, whether it runs freely, asks first or is blocked.

```csharp
mcp.AddTool(
    new McpToolDefinition("myplugin_follow", "Follows the current target on foot until myplugin_stop is called.")
    {
        Capabilities = [McpCapabilities.MoveCharacter],
    },
    args => { StartFollowing(); return new { following = CurrentTarget?.Name }; });
```

Pick capabilities from [the table below](#capabilities-what-to-declare). If you're unsure, declare more: an undeclared effect gets your plugin suspended.

### Take arguments

Arguments are the details of one call. The assistant fills them in from what the player asked for, or a job step passes them in its `args`. `InputSchema` is a JSON schema that tells the assistant which arguments exist, what each means and which values are allowed; the handler reads what was sent from the `JsonObject`.

A real example: XIV MCP's own `set_map_flag`, here as a plugin tool. "Put a flag at 21.4, 18.2 in Kozama'uka" becomes `{ "zone": "Kozama'uka", "x": 21.4, "y": 18.2 }`:

```csharp
mcp.AddTool(
    new McpToolDefinition("myplugin_set_flag", "Places the flag on the player's map at map coordinates in any zone, and opens the map at it.")
    {
        Capabilities = [McpCapabilities.GameUi],
        InputSchema = """
            {
              "type": "object",
              "properties": {
                "x": { "type": "number", "description": "Map X coordinate, as the in-game map shows it (about 1 to 42)." },
                "y": { "type": "number", "description": "Map Y coordinate, as the in-game map shows it." },
                "zone": { "type": "string", "description": "Zone name or territory id. Default: the zone you are in." },
                "open_map": { "type": "boolean", "description": "Open the map at the flag. Default true." }
              },
              "required": ["x", "y"]
            }
            """,
    },
    args =>
    {
        var x = args["x"]?.GetValue<double>() ?? throw new McpToolException("'x' is required.");
        var y = args["y"]?.GetValue<double>() ?? throw new McpToolException("'y' is required.");
        if (x is < 1 or > 42 || y is < 1 or > 42) throw new McpToolException($"({x}, {y}) is off the map: map coordinates run from 1 to 42.");
        var zone = args["zone"]?.GetValue<string>();                // optional: not in "required"; null means the current zone
        var openMap = args["open_map"]?.GetValue<bool>() ?? true;   // optional, default in the description
        PlaceFlag(zone, x, y, openMap);
        return new { zone, x, y, mapOpened = openMap };
    });
```

XIV MCP passes arguments on as they were sent and doesn't check them against the schema, so validate them before acting.

### Return results and errors

- **Result:** return any JSON-serializable value. An anonymous object is ideal: `return new { kills, gil = earned };`.
- **Error:** `throw new McpToolException("Not at a summoning bell. Use navigate_to with destination summoning_bell first.");`. The assistant sees the message as-is. Other exceptions arrive as "TypeName: message".
- Validate before acting, so a bad call changes nothing.

### Run something that takes a while

Use `AddLongRunningTool` for anything that waits. The call can run for hours as a job step.

```csharp
mcp.AddLongRunningTool(
    new McpToolDefinition("myplugin_farm", "Farms the current zone until the bags are full or 'minutes' have passed.")
    {
        Capabilities = [McpCapabilities.MoveCharacter, McpCapabilities.Combat],
        InputSchema = """{ "type": "object", "properties": { "minutes": { "type": "integer", "minimum": 1, "maximum": 600 } } }""",
    },
    async call =>
    {
        var until = DateTime.UtcNow.AddMinutes(call.Args["minutes"]?.GetValue<int>() ?? 60);
        await Framework.RunOnFrameworkThread(StartFarming);
        try
        {
            while (DateTime.UtcNow < until && !await Framework.RunOnFrameworkThread(BagsFull))
            {
                await Task.Delay(5000, call.Cancellation);   // throws when the job is paused or cancelled
                call.Progress($"{Kills} kills");              // shown in the job log
            }
            return new { kills = Kills };
        }
        finally
        {
            await Framework.RunOnFrameworkThread(StopFarmingSafely);  // also runs on cancel
        }
    });
```

- **Threads:** the handler starts on the framework thread and continues on the thread pool after its first `await`. Use `Framework.RunOnFrameworkThread` for game access after that.
- **Cancel safely:** when `call.Cancellation` fires, finish the fight or close the window, then return or throw. XIV MCP waits up to two minutes for you.
- **Progress:** `call.Progress("Room 2 of 5")` adds a line to the job log. Report milestones, not every frame.
- **Ending a cancelled call:** let the `OperationCanceledException` from `Task.Delay(…, call.Cancellation)` propagate, or throw it yourself. The client reports "Cancelled."
- **Pause and resume:** a paused job reruns the step from the start, with the same arguments. Make tools safe to rerun: prefer targets ("until 20 ore") over counts ("20 more").
- **Failing** (throwing) makes a job step *pending*: the job waits for the assistant to retry or change it. Say what's left: "Bags full after 120 kills; 3 Iron Ore still missing."

### Ask for approval at the risky moment

When the real cost is only known during the call, ask then, with the numbers:

```csharp
var basket = await Framework.RunOnFrameworkThread(PlanPurchase);
if (!await call.RequestApprovalAsync(McpCapabilities.SpendGil, $"Buy {basket.Summary} for {basket.Gil:N0} gil"))
    throw new McpToolException("The player declined the purchase; nothing was bought.");
```

- Returns `true` right away if the player allows the capability (or set this tool to *Allow* on its own), `false` if it's denied, and otherwise shows an approval window and waits (two minutes, then `false`).
- **For a capability set to *Ask*, the player is asked twice:** before the call, and again here. The exception is if they chose *Approve for this session* or *Always allow* the first time. So only ask here when your question adds something, like the price.
- **A "no" is final for this call.** Throw and say what wasn't done. If the assistant calls again, the player is asked again, and can set the capability to *Deny* to stop that.
- You can only ask for capabilities the tool declared. Anything else returns `false` and is flagged.
- Only in long-running tools: quick tools run on the framework thread and must not wait.

### Show the player where you stand

Explain in your own UI why your tools aren't used:

```csharp
var status = mcp.GetStatus();
var hint = status.State switch
{
    McpPluginState.Unavailable     => "XIV MCP isn't installed.",
    McpPluginState.Undecided       => "Enable My Plugin in /xivmcp → Third-party plugins to use it from your assistant.",
    McpPluginState.KeptDisabled    => "You kept My Plugin disabled in XIV MCP.",
    McpPluginState.AwaitingConsent => "My Plugin has new features; confirm them in /xivmcp → Third-party plugins.",
    McpPluginState.Suspended       => $"XIV MCP suspended My Plugin: {status.SuspendReason}",
    _                              => null,   // Enabled
};
var canBuy = status.Capabilities.GetValueOrDefault(McpCapabilities.SpendGil) != McpPermission.Deny;
```

Call it when your window opens or before a step, not every frame. Don't cache it: the player can change settings at any time.

### Start a job

A job runs steps one after another, each as long as it needs. Steps can be your tools, other plugins' or XIV MCP's own.

```csharp
var job = mcp.StartJob("Farm, then head to a bell",
    new McpJobStep("myplugin_farm", new { minutes = 90 }, Id: "farm"),
    new McpJobStep("navigate_to", new { destination = "summoning_bell" }),
    new McpJobStep("myplugin_report", new { text = "Farmed {{farm.kills}} kills." }));
```

- **Placeholders:** `"{{farm.kills}}"` on its own becomes that value with its type. Inside a longer string it becomes text. Paths go into objects and arrays (`{{plan.list.id}}`, `{{search.items.0.id}}`) and ignore case, so a result `new { Kills = 12 }` (sent as `{"kills": 12}`) is read by `{{farm.kills}}`.
- **XIV MCP's own tools** are listed, with their arguments, in the [README](../README.md#tools) and in `tools/list`. For example, `new McpJobStep("sell_item", new { retainer = "{{pick.retainer}}", item = "Iron Ore" })` passes your result into one of them.
- **Permissions:** every step is checked against the player's settings, so a step set to *Ask* waits for the player.
- **Control:** use `GetJob(id)`, `ListJobs()`, `PauseJob`, `ResumeJob` and `CancelJob`. Your jobs show in `/xivmcp` → **Jobs** with your plugin's name.
- **Missing tools:** `StartJob` refuses a job if a step's tool doesn't exist, isn't available (its plugin isn't loaded or isn't enabled in XIV MCP) or is turned off. A built-in tool whose plugin is missing fails when its step runs.

### Use other plugins' tools, and declare them

Steps can call XIV MCP's own tools and other plugins' tools. Each step is checked against the settings of the tool it calls, not yours. Declare the ones your jobs use at startup, so the player sees on your card in `/xivmcp` → **Third-party plugins** what is missing, with a way to install it:

```csharp
mcp.UsesTools(
    McpDependency.BuiltIn("navigate_to"),          // XIV MCP knows it needs vnavmesh and Lifestream
    McpDependency.FromPlugin("otherplugin_announce",
        plugin: "OtherPlugin",                       // its internal name
        pluginName: "Other Plugin",                  // its name in the installer
        repo: "https://example.com/repo.json",       // its repo.json, or "official"
        minVersion: "1.2.0"));                       // the oldest version with the tool
```

- Built-in tools take no plugin fields; naming a plugin for one is refused. Any other name needs all four.
- Calling `UsesTools` again replaces the list. It's sent again whenever XIV MCP loads.
- `GetStatus().Dependencies` tells you where each one stands: `ok`, `needs_plugin`, `plugin_missing`, `plugin_not_loaded`, `plugin_outdated`, `tool_missing` or `not_enabled`.
- Declaring asks the player for nothing and allows nothing; it only shows what your jobs need.

## Capabilities: what to declare

Declare every capability a tool can use on any path, including indirect costs. `read_game` is added for you.

| Declare | When your tool … | Risk |
|---|---|---|
| `McpCapabilities.GameUi` (`game_ui`) | opens, closes or clicks game windows, talks to NPCs | medium |
| `MoveCharacter` (`move_character`) | walks, mounts, teleports, or changes zone, instance or world | medium |
| `MoveItems` (`move_items`) | moves items between bags, armoury, saddlebag, retainers or chests | medium |
| `Network` (`network`) | sends web requests | medium |
| `Combat` (`combat`) | enters duties or fights | high |
| `SpendGil` (`spend_gil`) | pays gil, including teleports and repairs | high |
| `SpendCurrency` (`spend_currency`) | pays tomestones, scrips, seals, MGP or items used as currency | high |
| `TradeItems` (`trade_items`) | lists on the market board, sells to vendors or trades with players | high |
| `ChatSend` (`chat_send`) | sends chat or commands others can see | high |
| `Login` (`login`) | logs out, switches character or closes the game | high |
| `EditSettings` (`edit_settings`) | changes game, plugin or file settings | high |
| `DiscardItems` (`discard_items`) | discards or desynthesises, anything that can't be undone | critical |

Tips:

- **Split by risk.** A read-only `myplugin_plan_purchase` plus a `myplugin_buy` that declares `spend_gil` lets the player allow planning freely and approve only purchases.
- **Declare indirect effects.** Teleporting spends gil, buying with tokens spends a currency, and selling removes items.
- **Don't over-declare.** Every capability is a line the player must accept, and an unneeded *high* one makes them hesitate.
- **Buying** needs `SpendGil` or `SpendCurrency`, not `TradeItems`. Items you gain are never a problem; only what leaves the inventory counts.
- **A tool tied to an optional feature:** register it at startup anyway, and throw `McpToolException("Turn on Auto-Restock in My Plugin's settings first.")` while the feature is off. Adding it later would pause your plugin.

## What the player controls

You don't configure any of this, but it decides how your code must behave:

| The player … | What happens | What you do |
|---|---|---|
| hasn't enabled your plugin yet (the default) | Your tools aren't offered; calls are refused. | Show it in your UI (`GetStatus`). |
| chose *Keep disabled* | Same, and they aren't asked again while your registration stays the same. | Respect it. |
| was asked again because your registration grew (a new tool or capability) | All your tools pause (not offered, refused) until they consent. Removing tools never asks. | **Register every tool at startup**, with every capability it may need. |
| set a capability to *Deny* | Calls of tools declaring it are refused before your code runs. | Offer a fallback, or say so in your UI. |
| set a capability to *Ask* | An approval window before each call (or once per session). Critical ones (`discard_items`) are always asked. | Expect waits in jobs. |
| set one of your tools on its own | That tool ignores its capabilities' settings: *Allow*, *Ask* or *Deny* for the whole call (critical tools still ask every time). *Always allow* in the approval window sets just that tool. Undeclared side effects still suspend your plugin. | Read it from `GetStatus().Tools`. |
| got a call that changed gil, currencies, items, zone, chat or login state without declaring it | If the call took under two minutes, your plugin is suspended: still listed, but every call is refused. Longer calls are only flagged, since the player may have acted meanwhile. | Declare honestly; the player lifts suspensions. |

Every call, decision and approval is listed under your plugin in `/xivmcp` → **Third-party plugins**, and in `pluginConfigs/XivMcp/audit.jsonl`.

## Client reference

### `XivMcpClient`

| Member | Does |
|---|---|
| `new XivMcpClient(pluginInterface)` | Connects; tools are (re)registered whenever XIV MCP loads. |
| `AddTool(definition, args => result)` | A tool that answers right away, called on the framework thread. |
| `AddLongRunningTool(definition, async call => result)` | A tool that may take long; see `McpCall`. |
| `RemoveTool(name)` | Unregisters one tool. |
| `GetStatus()` | `McpStatus`: your `State`, `CanRun`, `SuspendReason`, the setting of each declared capability (`Capabilities`) and of each tool as a whole (`Tools`), and each declared dependency (`Dependencies`). |
| `CheckPermission(capability)` | `Allow`, `Ask` or `Deny` for one capability (`Deny` unless enabled). |
| `UsesTools(params dependencies)` | Declares the tools your jobs use that aren't yours (`McpDependency.BuiltIn` / `FromPlugin`). |
| `StartJob(name, params steps)` | Starts a job; returns it as JSON (`id`, `state`, …). |
| `GetJob(id)` / `ListJobs()` | A job, or the jobs your plugin started. |
| `PauseJob(id)` / `ResumeJob(id)` / `CancelJob(id)` | Job control. |
| `IsAvailable` | XIV MCP is loaded and speaks this API version. |
| `RegistrationFailed` | Event with XIV MCP's message when a definition is rejected. Log it. |
| `Dispose()` | Unregisters everything; call it in your plugin's `Dispose`. |

### `McpToolDefinition(Name, Description)`

| Property | Meaning |
|---|---|
| `Name` | 3–64 characters `a-z0-9_`, starting with a letter. Prefix it with your plugin's name: `myplugin_…`. |
| `Description` | What the assistant reads to decide when to call it. |
| `InputSchema` | JSON schema of the arguments (`"type": "object"`), or null for none. |
| `ReadOnly` | True if it changes nothing. It runs during jobs and needs no capabilities. |
| `Destructive` | True if the changes are hard to undo. |
| `Capabilities` | `McpCapabilities` ids. Required unless `ReadOnly`. |

### `McpCall` (long-running tools)

| Member | Meaning |
|---|---|
| `Args` | The arguments (`JsonObject`). |
| `Cancellation` | Fires when the job is paused or cancelled. |
| `Progress(text)` | Adds a line to the job log. |
| `RequestApprovalAsync(capability, summary)` | Asks the player now; `true` if approved. |
| `Id`, `Tool` | The call's id and tool name. |

### Other types

- **`McpToolException(message)`:** an error the assistant sees as-is.
- **`McpJobStep(Tool, Args, Id, Note)`:** one job step.
- **`McpPermission`:** `Allow`, `Ask` or `Deny`.
- **`McpPluginState`:** `Unavailable`, `Undecided`, `KeptDisabled`, `Enabled`, `AwaitingConsent` or `Suspended`.

## Rules and limits

**Descriptions** are what the assistant reads:

- Say what the tool does and when to use it, what it needs, and what can go wrong.
- Name related tools.
- Keep it to a few sentences.

The player sees an abbreviated version in `/xivmcp`, with the full text as a tooltip. A blank line (`\n\n`) starts a paragraph, a line starting with `- ` becomes a bullet, and sentences starting "Requires", "Needs" or "Only available" are set apart.

Limits:

- **Names:** names are global, so prefix them. XIV MCP's names and other plugins' names are refused.
- **Threads:** quick tools run on the framework thread and must return fast.
- **One acting step at a time:** while a job step runs, calls that aren't read-only are refused. Read-only tools always work.
- **Size:** results are JSON strings; keep them to a few kilobytes.
- **Identity:** IPC doesn't identify the caller, so your owner name is taken as given. Don't register under another plugin's name.
- **Observation, not a sandbox:** XIV MCP notices changes to gil, currencies, item counts, zone, world, sent chat and logins. Other effects, such as a market listing, still need declaring.
- **Versioning:** `ApiVersion` goes up only for breaking changes.
- **ToS:** automating game actions is against the FFXIV ToS. Make your tools as careful as a button in your own UI.

## Troubleshooting

- **Self-test:** `/xivmcp selftest` (or the `run_self_test` tool) checks that your plugin provides its Invoke gate, and calls one of your read-only tools if your plugin is enabled.
- **Logs:** `/xllog` shows each registration under `[XivMcp]`.
- **What the assistant sees:** the URL and token are in `/xivmcp` → **Connect**.

```bash
curl -s http://localhost:37521/mcp -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

| Symptom | Fix |
|---|---|
| Your tool isn't in `tools/list` | The player hasn't enabled your plugin, or registration failed: check `RegistrationFailed`. |
| "A tool that isn't readOnly must declare its 'capabilities'" | Add `Capabilities = [...]`. |
| "A read-only tool can't declare …" | Drop `ReadOnly` or the acting capabilities. |
| "'x' is an XIV MCP tool" / "already registered by another plugin" | Use a prefixed name. |
| "… is blocked for … in /xivmcp" | The player denied a declared capability. |
| "The player declined …" | Declined, or not answered within two minutes. |
| "… is suspended by XIV MCP (…)" | A call did something undeclared. Fix the declaration; the player lifts the suspension. |
| "… changed its registration … waits for the player to consent again" | You added a tool or capability. Register everything at startup. |
| `RequestApprovalAsync` is always false | The capability isn't declared on that tool. |
| "The background job '…' is running a step" | A job is running and your tool isn't read-only. Pause the job first. |
| A cancelled job stays "running" for two minutes | Your handler ignores `call.Cancellation`. |
| `InvalidOperationException` or hitches | Game access off the framework thread: use `RunOnFrameworkThread`. |

## Appendix: raw IPC

For plugins that don't use `XivMcpClient.cs`. XIV MCP's gates are named `XivMcp.<Gate>`, and yours are named `<InternalName>.XivMcp.<Gate>`. XIV MCP's gates reply with `{"ok": true, …}` or `{"ok": false, "error": "…"}` and never throw into your plugin. `owner` is your `IDalamudPluginInterface.InternalName`; registering from your constructor is fine.

### Gates XIV MCP provides

| Gate | Signature | Returns / does |
|---|---|---|
| `ApiVersion` | `Func<int>` | `2` |
| `IsReady` | `Func<bool>` | True while loaded |
| `ListCapabilities` | `Func<string>` | `{"capabilities":[{"id","risk","title","description","default"}]}` |
| `RegisterTool` | `Func<string owner, string definitionJson, string>` | `{"name","enabled","awaitingConsent","state","capabilities":[{"id","mode"}]}` |
| `UnregisterTool` | `Func<string owner, string name, string>` | `{"removed": bool}` |
| `UnregisterAll` | `Func<string owner, string>` | `{"removed": count}` |
| `GetStatus` | `Func<string owner, string>` | `{"state","canRun","suspendReason","tools":[{"name","mode","own"}],"capabilities":[{"id","mode"}],"dependencies":[{"tool","plugin","state","message","install"}]}` |
| `DeclareDependencies` | `Func<string owner, string json, string>` | `{"tools":[{"tool"}, {"tool","plugin","name","repo","minVersion"}]}`; returns `{"dependencies":[…]}` |
| `CheckPermission` | `Func<string owner, string capability, string>` | `{"mode":"allow"\|"ask"\|"deny","enabled","suspended","state"}` |
| `RequestApproval` | `Func<string callId, string requestJson, string>` | Request `{"capability","summary"}`; returns `{"approvalId","state"}` |
| `GetApproval` | `Func<string approvalId, string>` | `{"state":"pending"\|"approved"\|"denied"}` |
| `CompleteCall` | `Func<string callId, string resultJson, string>` | Finishes a pending call |
| `FailCall` | `Func<string callId, string message, string>` | Fails a pending call |
| `ReportProgress` | `Func<string callId, string text, string>` | Adds a job log line |
| `StartJob` | `Func<string owner, string jobJson, string>` | Job `{"name","steps":[{"id","tool","args","note"}]}`; returns `{"job":{…}}` |
| `GetJob` / `ListJobs` | `Func<string id \| owner, string>` | `{"job"}` / `{"jobs"}` |
| `PauseJob` / `ResumeJob` / `CancelJob` | `Func<string id, string>` | Job control |
| `Ready` / `Disposing` | messages | Register on `Ready`; stop running calls on `Disposing` |

`state` is one of `undecided`, `kept_disabled`, `enabled`, `awaiting_consent` or `suspended`.

### Tool definition

```json
{
  "name": "myplugin_restock",
  "description": "Buys the consumables your list is short of from the nearest vendor.",
  "inputSchema": { "type": "object", "properties": {} },
  "readOnly": false,
  "destructive": false,
  "capabilities": ["move_character", "game_ui", "spend_gil"]
}
```

`capabilities` is required unless `readOnly`. `readOnly` and `destructive` can't both be true, and unknown capability ids are rejected.

### Gates your plugin provides

| Gate | Signature | Does |
|---|---|---|
| `<InternalName>.XivMcp.Invoke` | `Func<string callId, string tool, string argsJson, string>` | Runs a tool on the framework thread, after the permission checks. Reply with `{"result": …}`, `{"error": "…"}` or `{"pending": true}`; finish a pending call with `CompleteCall` / `FailCall`. |
| `<InternalName>.XivMcp.Cancel` | `Action<string callId>` | Optional: stop a pending call. |

`CompleteCall` and `RequestApproval` may be called from inside `Invoke`, before you reply.

A call that takes a minute:

```
XivMcp → you   Invoke("c1", "myplugin_farm", "{\"minutes\":1}")
you → XivMcp   reply {"pending": true}                         (start the work, return at once)
you → XivMcp   ReportProgress("c1", "12 kills")                 (any number of times)
you → XivMcp   RequestApproval("c1", "{\"capability\":\"spend_gil\",\"summary\":\"Repair for 3,200 gil\"}")
                → {"approvalId":"a7","state":"pending"}; poll GetApproval("a7") every ~250 ms until approved or denied
XivMcp → you   Cancel("c1")                                     (only if the job is paused or cancelled: stop safely)
you → XivMcp   CompleteCall("c1", "{\"kills\":31}")             (the result itself, not wrapped in {"result": …})
               or FailCall("c1", "Bags full after 31 kills.")
```

XIV MCP has no time limit of its own on pending calls. Approval windows decline after two minutes, and after a `Cancel` XIV MCP waits up to two minutes for your `CompleteCall` or `FailCall`.
