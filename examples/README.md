# Examples for the XIV MCP plugin API

| File | What it is |
|---|---|
| [`XivMcpClient.cs`](XivMcpClient.cs) | Drop-in client: copy it into your plugin to offer tools and start jobs through XIV MCP. One file; it needs only Dalamud and `System.Text.Json`. |
| [`HelloMcp/`](HelloMcp) | A small, complete Dalamud plugin that uses the client. Start from this. |

The full guide is [docs/plugin-api.md](../docs/plugin-api.md).

## What Hello MCP shows

| Tool | Kind | Shows |
|---|---|---|
| `hellomcp_greet` | quick, read-only | Reading game state on the framework thread, an `enum` argument, structured results, `McpToolException` |
| `hellomcp_print` | quick, acting | A tool that changes something (prints to the local chat log), so `ReadOnly` stays `false`; validating arguments first |
| `hellomcp_countdown` | long-running | Replying "pending", `call.Progress(…)`, stopping on `call.Cancellation`, going back to the framework thread |

The `/hellomcp job` command starts a job: the countdown, then a chat message that uses the countdown's result (`{{count.seconds}}`), then XIV MCP's built-in `get_game_status`. `/hellomcp jobs` lists the jobs the plugin started.

## Try it

1. **Build**:
   ```
   dotnet build examples/HelloMcp/HelloMcp.csproj -c Debug
   ```
2. **Load it as a dev plugin.** In game, open Dalamud Settings → **Experimental** → **Dev Plugin Locations**, add the full path to `examples/HelloMcp/bin/Debug/HelloMcp.dll` and save. Then enable *Hello MCP* in the plugin installer under **Dev Tools**.
3. **Allow it.** Open `/xivmcp` → **Permissions** → **Tools from other plugins** and switch on *Hello MCP*. The three tools appear in your assistant's tool list.
4. **Use it.** Ask your assistant to "greet me with Hello MCP", or type `/hellomcp job` and watch the job in `/xivmcp` → **Jobs**. Pause it while the countdown runs to see cancellation, then resume it.

## Tips for your own plugin

- **Start from the client, not the raw gates.** It handles the Ready and Disposing messages, re-registers after XIV MCP reloads, and turns exceptions into errors the assistant can read.
- **Prefix every tool name** with your plugin's name (`myplugin_…`) and start it with a verb.
- **Write the description for the assistant:** what the tool does, when to use it, what it needs, and which tools go before or after it.
- **Read-only means it changes nothing.** Acting tools are refused while a job step runs, which keeps them from colliding with the job.
- **Quick tools must be quick.** They run on the framework thread. Anything that waits belongs in `AddLongRunningTool`.
- **Make cancellation safe.** Finish the fight or close the window first, then stop. XIV MCP waits up to two minutes for that.
- **Fail with a next step:** "Not at a summoning bell; use navigate_to with destination summoning_bell first."
