# XIV MCP plugin API

Your Dalamud plugin can offer its own tools to AI assistants through XIV MCP, and start background jobs, without running a server of its own. XIV MCP lists your tools next to its built-in ones, runs them when an assistant calls them, and can chain them into jobs that run for hours.

This guide is for plugin developers. If you only want to use XIV MCP, see the [main README](../README.md).

- [How it works](#how-it-works)
- [Quick start](#quick-start)
- [Writing tools the assistant uses well](#writing-tools-the-assistant-uses-well)
- [Long-running tools](#long-running-tools)
- [Starting jobs from your plugin](#starting-jobs-from-your-plugin)
- [Lifecycle and availability](#lifecycle-and-availability)
- [IPC reference](#ipc-reference)
- [Testing and troubleshooting](#testing-and-troubleshooting)
- [Limits and security](#limits-and-security)

## How it works

```
 AI assistant ──MCP──▶ XIV MCP ──Dalamud IPC──▶ your plugin
                       │  lists your tool         runs it (framework thread)
                       │  checks the player       replies with a result,
                       │  allowed your plugin     an error, or "pending"
                       │  runs it in jobs    ◀──  reports progress / completion
```

- **Dalamud IPC only.** Plugins can't share types, so everything crossing the boundary is a string of JSON. You don't reference XIV MCP's assembly, and your plugin keeps working when XIV MCP isn't installed.
- **The player decides.** A registered tool is offered to assistants only after the player allows your plugin in `/xivmcp` → **Permissions** → **Tools from other plugins**. Until then it's listed there as waiting.
- **Your logic, XIV MCP's plumbing.** XIV MCP handles the MCP protocol, the access token, the tool list, job queueing, collision checks and the in-game job UI. You handle what the tool does.

## Quick start

**1. Copy the client.** Add [`examples/XivMcpClient.cs`](../examples/XivMcpClient.cs) to your project. It's a single file with no dependencies beyond Dalamud and `System.Text.Json`. Rename its namespace if you like.

**2. Create it and add tools** in your plugin's constructor:

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
            new McpToolDefinition("myplugin_status", "What My Plugin is doing right now: its mode and the current target.")
            {
                ReadOnly = true,
            },
            args => new { mode = Mode.ToString(), target = CurrentTarget?.Name });
    }

    public void Dispose() => mcp.Dispose();
}
```

**3. Build and load your plugin**, then open `/xivmcp` → **Permissions** and switch your plugin on under **Tools from other plugins**.

**4. Ask your assistant** to "check My Plugin's status". It sees `myplugin_status` in its tool list (clients are notified when the list changes).

The [example plugin](../examples/HelloMcp/Plugin.cs) shows three tools (quick and read-only, one that changes something, one long-running) and a command that starts a job.

### Tools with arguments

Describe arguments with a JSON schema. The assistant fills them in; you read them from the `JsonObject`:

```csharp
mcp.AddTool(
    new McpToolDefinition("myplugin_set_mode", "Switches My Plugin's mode. Use \"idle\" to stop it.")
    {
        InputSchema = """
            {
              "type": "object",
              "properties": {
                "mode": { "type": "string", "enum": ["idle", "follow", "assist"], "description": "The new mode." }
              },
              "required": ["mode"]
            }
            """,
    },
    args =>
    {
        var mode = args["mode"]?.GetValue<string>() ?? throw new McpToolException("Give 'mode'.");
        SetMode(mode);
        return new { mode };
    });
```

## Writing tools the assistant uses well

The assistant only knows your tool by its name, description and schema. These tips make the difference between a tool that gets used correctly and one that gets misused or ignored.

### Names

- **Prefix with your plugin**: `myplugin_set_mode`, not `set_mode`. Names are global; built-in names and other plugins' names are refused.
- Use 3–64 characters `a-z`, `0-9` and `_`, starting with a letter.
- Start with a verb (`get_`, `list_`, `start_`, `set_`) so the purpose is obvious.

### Descriptions

- **Say what it does and when to use it** in the first sentence: "Lists the duties My Plugin can run. Use it before `myplugin_run`."
- **Say what it needs and what can go wrong**: "Only at a summoning bell." / "Fails when the inventory is full."
- **Name related tools**, yours or XIV MCP's (`get_game_status`, `switch_gearset`, `navigate_to`), so the assistant can chain them.
- Keep it to a few sentences. XIV MCP adds "[Provided by the *Your Plugin* plugin.]" for you.

### Schemas

- Give every property a `description`, and use `enum` for fixed choices and `minimum`/`maximum` for numbers.
- Mark what's required with `required`. Make everything else optional with a sensible default, and state the default in the description.
- Accept the forms players use. If an argument is an item, take a name *or* an id.

### Results

- **Return structured data** (an anonymous object), not prose. The assistant reads JSON well.
- **Keep it small.** Return what answers the question, and offer a `limit` or filter argument for long lists.
- **Echo what you did**: `{ "mode": "follow", "previous": "idle" }` makes the assistant's report accurate.

### Errors

- Throw `McpToolException("…")` with a message the player could act on: "Not at a summoning bell. Use navigate_to first." The assistant sees it verbatim.
- Validate arguments before doing anything, so a bad call changes nothing.
- Other exceptions also reach the assistant, but as "TypeName: message", so prefer `McpToolException` for expected failures.

### Read-only vs acting

- Set `ReadOnly = true` only when the tool **changes nothing**. Assistants and XIV MCP rely on it: while a job step runs, XIV MCP refuses non-read-only calls, so nothing collides with the job.
- Set `Destructive = true` when changes are hard to undo, like deleting, overwriting, spending currency or discarding items.
- **Ask the player for anything risky.** Show your own confirmation in game for purchases with special currency, discards and similar, and fail the call if they decline.

### Threads

- `AddTool` handlers run **on the framework thread**: read game state directly, but return quickly. Never wait or sleep in them. Use a long-running tool for anything that waits.
- Long-running handlers start on the framework thread and continue on the thread pool after their first `await`. Use `IFramework.RunOnFrameworkThread` for game access after that.

## Long-running tools

Anything that waits (walking somewhere, a crafting list, a dungeon) should be a long-running tool. XIV MCP gets a "pending" reply right away and waits for the result, which keeps the game responsive and lets the call run as a job step for hours.

```csharp
mcp.AddLongRunningTool(
    new McpToolDefinition("myplugin_farm", "Farms the current zone until the bags are full or 'minutes' have passed.")
    {
        InputSchema = """{ "type": "object", "properties": { "minutes": { "type": "integer", "minimum": 1, "maximum": 600 } } }""",
    },
    async call =>
    {
        var minutes = call.Args["minutes"]?.GetValue<int>() ?? 60;
        var until = DateTime.UtcNow.AddMinutes(minutes);
        var kills = 0;
        await Framework.RunOnFrameworkThread(StartFarming);
        try
        {
            while (DateTime.UtcNow < until && !await Framework.RunOnFrameworkThread(BagsFull))
            {
                await Task.Delay(5000, call.Cancellation);     // throws when the job is paused or cancelled
                kills = await Framework.RunOnFrameworkThread(() => Kills);
                if (kills % 10 == 0) call.Progress($"{kills} kills");
            }
            return new { kills, reason = DateTime.UtcNow >= until ? "time" : "bags full" };
        }
        finally
        {
            await Framework.RunOnFrameworkThread(StopFarmingSafely);   // runs on cancel too
        }
    });
```

### Cancellation

When the player or the assistant pauses or cancels the job, or the client gives up, XIV MCP calls your Cancel gate and `call.Cancellation` fires.

- **Stop safely, not instantly.** Never stop mid-combat or with a window half-filled. Finish the fight, close the window, then return or throw. XIV MCP waits up to **2 minutes** for you to report the end before it moves on, and while it waits the job counts as running, so nothing collides with your cleanup.
- Cleanup in `finally` runs on cancel as well as on success and failure.
- Throwing `OperationCanceledException` (e.g. from `Task.Delay(…, call.Cancellation)`) is the normal way to end a cancelled call.

### Progress

`call.Progress("Room 2 of 5")` adds a line to the job's log, which the player sees in `/xivmcp` → **Jobs** and the assistant sees in `get_job`. Report milestones, not every frame. Lines are cut at 300 characters.

### Results and failures

- Returning a value completes the call, and the value becomes the step's result for later steps.
- Throwing fails the call. A failed job step makes the job **pending**: it waits for the assistant to retry, skip or change it, so make the message explain what's left, e.g. "Bags full after 120 kills; 3 Iron Ore still missing."

## Starting jobs from your plugin

A job is a queue of tool calls (yours, other plugins' or XIV MCP's built-in ones) that XIV MCP runs one after another, each for as long as it takes. Jobs survive reloads (they come back paused) and show up in `/xivmcp` → **Jobs** with pause, resume and cancel buttons.

```csharp
var job = mcp.StartJob("Farm, then head to a bell",
    new McpJobStep("myplugin_farm", new { minutes = 90 }, Id: "farm"),
    new McpJobStep("navigate_to", new { destination = "summoning_bell" }),
    new McpJobStep("myplugin_report", new { text = "Farmed {{farm.kills}} kills ({{farm.reason}}); waiting at the bell." }));

Log.Information($"Started job {job["id"]}");
```

- **Placeholders** pass results along. A string that is exactly `"{{farm.kills}}"` becomes that value with its type (a number stays a number). Inside a longer string it's replaced by the value as text. Paths can go into objects and arrays: `{{plan.list.id}}`, `{{search.items.0.id}}`.
- **Permissions still apply.** Each step needs the permission its tool needs (e.g. `navigate_to` needs *Game & navigation*). Starting a job also needs your plugin to be allowed.
- **Watch it** with `GetJob(id)` or `ListJobs()` (jobs your plugin started), and control it with `PauseJob`, `ResumeJob` and `CancelJob`.

Job states are `queued`, `running`, `paused`, `pending` (a step failed or was stopped; waiting for a fix), `completed`, `failed` and `cancelled`.

## Lifecycle and availability

| Situation | What happens |
|---|---|
| Your plugin loads before XIV MCP | `AddTool` keeps the tool. When XIV MCP loads it sends `XivMcp.Ready`, and the client registers everything. |
| XIV MCP loads before your plugin | `AddTool` registers immediately. |
| XIV MCP reloads | It sends `XivMcp.Disposing` (the client cancels running calls), then `XivMcp.Ready` again (the client registers again). |
| Your plugin unloads | `mcp.Dispose()` unregisters your tools. If you forget, XIV MCP removes them when it sees your plugin unload, and fails your running calls. |
| The player hasn't allowed your plugin | Tools are registered but not offered to assistants. Calls and jobs are refused with a message saying how to allow it. |

Registering the same name again from the same plugin replaces the tool, so you can change a description at runtime.

## IPC reference

You don't need this with `XivMcpClient.cs`. It's here for plugins that call the gates directly, or that aren't written in C#.

All gates XIV MCP provides start with `XivMcp.`. Gates that return a string return a JSON envelope: `{"ok": true, …}` or `{"ok": false, "error": "…"}`. They never throw into your plugin.

### Gates XIV MCP provides

| Gate | Signature | Purpose |
|---|---|---|
| `XivMcp.ApiVersion` | `Func<int>` | API version (currently `1`). Use it to detect XIV MCP. |
| `XivMcp.IsReady` | `Func<bool>` | True while XIV MCP is loaded. |
| `XivMcp.RegisterTool` | `Func<string owner, string definitionJson, string>` | Registers or replaces a tool. Returns `{"ok":true,"name":…,"allowed":bool}`. |
| `XivMcp.UnregisterTool` | `Func<string owner, string name, string>` | Removes one of your tools. |
| `XivMcp.UnregisterAll` | `Func<string owner, string>` | Removes all your tools. |
| `XivMcp.CompleteCall` | `Func<string callId, string resultJson, string>` | Finishes a pending call with a result (any JSON). |
| `XivMcp.FailCall` | `Func<string callId, string message, string>` | Finishes a pending call with an error. |
| `XivMcp.ReportProgress` | `Func<string callId, string text, string>` | Adds a line to the job log. |
| `XivMcp.StartJob` | `Func<string owner, string jobJson, string>` | Starts a job: `{"name":…,"steps":[{"id","tool","args","note"}]}`. Returns `{"ok":true,"job":{…}}`. |
| `XivMcp.GetJob` | `Func<string id, string>` | `{"ok":true,"job":{…}}`. |
| `XivMcp.ListJobs` | `Func<string owner, string>` | Jobs started by `owner` (empty string: all jobs). |
| `XivMcp.PauseJob` / `ResumeJob` / `CancelJob` | `Func<string id, string>` | Job control. |
| `XivMcp.Ready` | message (`ICallGateSubscriber<object>.Subscribe`) | XIV MCP loaded: register your tools. |
| `XivMcp.Disposing` | message | XIV MCP is unloading: stop running calls. |

`owner` is always your plugin's internal name (`IDalamudPluginInterface.InternalName`), and it must belong to a loaded plugin.

### The tool definition

```json
{
  "name": "myplugin_set_mode",
  "description": "Switches My Plugin's mode. Use \"idle\" to stop it.",
  "inputSchema": { "type": "object", "properties": { "mode": { "type": "string" } }, "required": ["mode"] },
  "readOnly": false,
  "destructive": false
}
```

`inputSchema` is optional (no arguments), and so are `readOnly` and `destructive` (both default to `false`).

### Gates your plugin provides

| Gate | Signature | Purpose |
|---|---|---|
| `<InternalName>.XivMcp.Invoke` | `Func<string callId, string tool, string argsJson, string>` | Runs a tool. Called on the framework thread. |
| `<InternalName>.XivMcp.Cancel` | `Action<string callId>` | Optional: asks a pending call to stop. |

`Invoke` replies with exactly one of:

| Reply | Meaning |
|---|---|
| `{"result": <any JSON>}` | Done; this is the result. |
| `{"error": "message"}` | Failed; the assistant sees the message. |
| `{"pending": true}` | Still running; finish later with `CompleteCall` or `FailCall` using the same `callId`. |

### Call sequence of a long-running call

```
XIV MCP                                   your plugin
   │ Invoke(callId, tool, args) ────────────▶ start work
   │ ◀──────────────────────── {"pending":true}
   │            ◀── ReportProgress(callId, "1/3")   (any number of times)
   │ Cancel(callId) ─────────────────────────▶ (only if the job is paused/cancelled)
   │            ◀── CompleteCall(callId, result)    or FailCall(callId, message)
```

Calling `CompleteCall` from inside `Invoke`, before you've replied, is fine: XIV MCP registers the call before invoking you.

## Testing and troubleshooting

**See what the assistant sees.** List the tools straight from the server; the URL and token are in `/xivmcp` → **Connect**:

```bash
curl -s http://localhost:37521/mcp -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

and call one:

```bash
curl -s http://localhost:37521/mcp -H "Authorization: Bearer <token>" -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"myplugin_status","arguments":{}}}'
```

**Read the log.** XIV MCP logs each registration and removal under `[XivMcp]` in `/xllog`.

| Symptom | Cause |
|---|---|
| Tool missing from `tools/list` | Your plugin isn't allowed in `/xivmcp` yet, or registration failed (check `RegistrationFailed` and the log). |
| "No loaded plugin has the internal name …" | `owner` isn't your `InternalName` (the client uses it automatically). |
| "'x' is a built-in XIV MCP tool" / "already registered by another plugin" | Pick a prefixed name. |
| "… doesn't provide &lt;Name&gt;.XivMcp.Invoke" | You registered a tool by hand but no Invoke gate. |
| "The background job '…' is running a step" | A job is running and your tool isn't `ReadOnly`. That's by design; pause the job first. |
| A cancelled call keeps the job "running" for 2 minutes | Your handler doesn't observe `call.Cancellation`. |
| `InvalidOperationException` / hitches | Game access from the thread pool: wrap it in `RunOnFrameworkThread`. |

## Limits and security

- **No caller identity.** Dalamud IPC doesn't tell XIV MCP which plugin is calling, so `owner` is taken as given (it must be a loaded plugin's internal name). The player's per-plugin switch is the trust boundary. Don't register tools under another plugin's name.
- **Strings of JSON both ways.** Results should stay well under a megabyte; assistants work best with a few kilobytes.
- **One step at a time.** The game has one character, so XIV MCP runs one job step at a time and refuses other acting calls while one runs. Read-only tools always work.
- **Versioning.** `XivMcp.ApiVersion` goes up only for breaking changes; new gates may be added without a bump. `XivMcpClient` checks for at least the version it was written for.
- Automating game actions is against the FFXIV ToS. Your tools act with your plugin's own logic; make them as careful as you would make a button in your own UI.
