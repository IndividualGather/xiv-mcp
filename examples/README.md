# Examples for the XIV MCP plugin API

| File | What it is |
|---|---|
| [`XivMcpClient.cs`](XivMcpClient.cs) | Drop-in client (API version 2): copy it into your plugin to offer tools, ask for approvals and start jobs through XIV MCP. One file; it needs only Dalamud and `System.Text.Json`. |
| [`HelloMcp/`](HelloMcp) | A small, complete Dalamud plugin that uses the client. Start from this. |

The full guide is [docs/plugin-api.md](../docs/plugin-api.md).

## What Hello MCP shows

| Tool | Kind | Declares | Shows |
|---|---|---|---|
| `hellomcp_greet` | quick, read-only | (reading) | Reading game state on the framework thread, an `enum` argument, structured results, `McpToolException` |
| `hellomcp_print` | quick, acting | `game_ui` | A tool that changes something (prints to the local chat log): not read-only, declares what it does, validates arguments first |
| `hellomcp_countdown` | long-running | `game_ui` | Replying "pending", `call.Progress(…)`, stopping on `call.Cancellation`, going back to the framework thread |
| `hellomcp_pretend_purchase` | long-running | `spend_gil` | `mcp.CheckPermission(…)` and `call.RequestApprovalAsync(…)`: asking the player at the risky moment with the real numbers. Nothing is bought. |

The `/hellomcp job` command starts a job: the countdown, then a chat message that uses the countdown's result (`{{count.seconds}}`), then XIV MCP's own `get_game_status`. `/hellomcp bell` starts a job that walks to a summoning bell with XIV MCP's `navigate_to`, then prints the result. `/hellomcp jobs` lists the jobs the plugin started.

The plugin declares the tools its jobs use besides its own (`mcp.UsesTools(McpDependency.BuiltIn("get_game_status"), McpDependency.BuiltIn("navigate_to"))`). They show on its card in `/xivmcp` → **Third-party plugins**, with an error and an install button when a plugin they need is missing.

## Try it

1. **Build**:
   ```
   dotnet build examples/HelloMcp/HelloMcp.csproj -c Debug
   ```
2. **Load it as a dev plugin.** In game, open Dalamud Settings → **Experimental** → **Dev Plugin Locations**, add the full path to `examples/HelloMcp/bin/Debug/HelloMcp.dll` and save. Then enable *Hello MCP* in the plugin installer under **Dev Tools**.
3. **Enable it.** A notification pops up: "Hello MCP wants to register with XIV MCP". Click **Enable**, or **Review…** to see its four tools and the capabilities they declare (each set to **Ask** except reading) in `/xivmcp` → **Third-party plugins**. `/hellomcp status` shows what the plugin sees (`mcp.GetStatus()`).
4. **Use it.**
   - Ask your assistant to "greet me with Hello MCP": it runs without asking, because it only reads.
   - Ask it to "print hello in chat with Hello MCP": the approval window asks first. Choose *Approve for this session* and ask again; it doesn't ask a second time.
   - Ask it to "pretend to buy a Hi-Potion with Hello MCP": the call itself is approved first, then the tool asks again with the price.
   - Type `/hellomcp job` and watch the job in `/xivmcp` → **Jobs**. Pause it while the countdown runs to see cancellation, then resume it.
   - Check the activity list under *Hello MCP*: every call, decision and approval is recorded.

## Tips for your own plugin

**Getting started**

- **Start from the client, not the raw gates.** It handles the Ready and Disposing messages, re-registers after XIV MCP reloads, and turns exceptions into errors the assistant can read.
- **Prefix every tool name** with your plugin's name (`myplugin_…`) and start it with a verb.
- **Write the description for the assistant:** what the tool does, when to use it, what it needs, and which tools go before or after it.

**Permissions**

- **Declare every capability a tool can use, including indirect ones.** A teleport costs gil, so declare `spend_gil` too. Undeclared side effects suspend your plugin.
- **Read-only means it changes nothing.** It can't declare acting capabilities, and acting tools are refused while a job step runs.
- **Ask at the moment of risk, with real numbers:** `RequestApprovalAsync(McpCapabilities.SpendGil, "Buy 3 Hi-Potions for 1,200 gil")`. Use `CheckPermission` to adapt (e.g. skip an optional purchase when spending is denied).
- **Register everything at startup.** A tool or capability added later pauses your plugin until the player consents again.
- **Show your status.** `mcp.GetStatus()` tells you whether the player has decided, kept you disabled, needs to consent again, or you are suspended; say so in your UI.
- **Keep critical actions rare and explicit.** `discard_items` is asked every time and can't be allowed permanently.

**Behaviour**

- **Quick tools must be quick.** They run on the framework thread. Anything that waits belongs in `AddLongRunningTool`.
- **Make cancellation safe.** Finish the fight or close the window first, then stop. XIV MCP waits up to two minutes for that.
- **Fail with a next step:** "Not at a summoning bell; use navigate_to with destination summoning_bell first."
