# Examples for the XIV MCP plugin API

| File | What it is |
|---|---|
| [`XivMcpClient.cs`](XivMcpClient.cs) | Drop-in client (API version 2): copy it into your plugin to offer tools, ask for approvals and start jobs through XIV MCP. One file; it needs only Dalamud and `System.Text.Json`. |
| [`HelloMcp/`](HelloMcp) | A small, complete Dalamud plugin that uses the client. Start from this. |

Build Hello MCP with `dotnet build examples/HelloMcp/HelloMcp.csproj -c Debug` and load `examples/HelloMcp/bin/Debug/HelloMcp.dll` as a dev plugin.

What Hello MCP shows, how to try it, and tips for your own plugin are in the developer docs: [Hello MCP](https://individualgather.github.io/xiv-mcp/docs/developer/plugin-api/hello-mcp/) and [Guidelines](https://individualgather.github.io/xiv-mcp/docs/developer/plugin-api/guidelines/). Until the site is published, the pages are in [`docs-site/content/docs/developer`](../docs-site/content/docs/developer).
