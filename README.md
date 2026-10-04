<p align="center"><img src="branding/banner.png" width="800" alt="XIV MCP: uplink AI assistants to Final Fantasy XIV. A Dalamud plugin and Model Context Protocol server."></p>

# XIV MCP

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that runs a local **Model Context Protocol (MCP) server** inside the game client. AI assistants such as ChatGPT (Codex), Claude or a model in LM Studio can read your character, inventory, retainers and the game around you, and, when you allow it, act in the game: walk and travel, move items, crystals and gil, trade with other players, send ventures and submersibles, buy and sell, craft, gather, run dungeons, play Triple Triad and the Cactpot, find and catch fish, and run long tasks as background jobs. Other plugins can add their own tools.

- **Local only:** MCP Streamable HTTP at `http://localhost:37521/mcp`, with an access token
- **You decide:** reading is allowed by default; acting, editing and going online are denied until you allow them, per module and per tool, in `/xivmcp`
- **Works alone:** no other plugin is required; vnavmesh, Lifestream, AutoDuty, Artisan, Saucy, AutoHook and others are used when installed

## Documentation

**[individualgather.github.io/xiv-mcp](https://individualgather.github.io/xiv-mcp/)**, published once the repository is public. Until then, the pages are in [`docs-site/content/docs`](docs-site/content/docs).

| For | Start here |
|---|---|
| Players | [What is XIV MCP?](https://individualgather.github.io/xiv-mcp/docs/user/), [Installing](https://individualgather.github.io/xiv-mcp/docs/user/install/), [Connecting your AI app](https://individualgather.github.io/xiv-mcp/docs/user/connect/), [Modules and permissions](https://individualgather.github.io/xiv-mcp/docs/user/permissions/modules/), [Tools reference](https://individualgather.github.io/xiv-mcp/docs/user/tools/), [Troubleshooting](https://individualgather.github.io/xiv-mcp/docs/user/troubleshooting/) |
| Plugin developers | [Quick start](https://individualgather.github.io/xiv-mcp/docs/developer/plugin-api/), [Hello MCP](https://individualgather.github.io/xiv-mcp/docs/developer/plugin-api/hello-mcp/), [Client reference](https://individualgather.github.io/xiv-mcp/docs/developer/plugin-api/reference/) |
| Contributors | [Contributing](https://individualgather.github.io/xiv-mcp/docs/developer/contributing/), [Building](https://individualgather.github.io/xiv-mcp/docs/developer/contributing/building/), [Testing](https://individualgather.github.io/xiv-mcp/docs/developer/contributing/testing/), [Architecture](https://individualgather.github.io/xiv-mcp/docs/developer/contributing/architecture/) |

## Quick start

XIV MCP is not in a plugin repository yet, so build it and load it as a dev plugin. You need the .NET 10 SDK, and XIVLauncher with Dalamud.

```sh
git clone https://github.com/IndividualGather/xiv-mcp.git
cd xiv-mcp
dotnet build XivMcp.slnx
```

In the game, add the full path to `XivMcp/bin/Debug/XivMcp.dll` under `/xlsettings` → **Experimental** → **Dev Plugin Locations**, enable it in `/xlplugins` → **Dev Tools**, then open `/xivmcp` → **Connect** and set up your AI app with one click.

Tests: `dotnet test --project XivMcp.Tests/XivMcp.Tests.csproj`. Docs site: `cd docs-site && npm install && npm run dev`.

## For plugin developers

Copy [`examples/XivMcpClient.cs`](examples/XivMcpClient.cs) into your plugin to offer tools, ask the player for approvals and start jobs over Dalamud IPC, with no reference to XIV MCP. [`examples/HelloMcp`](examples/HelloMcp) is a complete example plugin.

## Notes

Automating game actions is against the FFXIV terms of service. XIV MCP sends the same requests as manual input, but use it at your own risk, and do not share data about other players.

## License

MIT
