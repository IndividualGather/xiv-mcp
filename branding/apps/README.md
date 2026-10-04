# App logos

Logos of AI apps that the Connect tab shows when the app isn't installed on the player's PC (installed apps show their own icon, read from the app itself).

| File | App | Source |
|---|---|---|
| `grok.svg` | Grok (xAI) | [LobeHub Icons](https://github.com/lobehub/lobe-icons) 1.95.1, MIT |
| `copilot.svg` | GitHub Copilot | LobeHub Icons 1.95.1, MIT |
| `cursor.svg` | Cursor | LobeHub Icons 1.95.1, MIT |

The logos are trademarks of their owners and are used only to name the app a button connects to.

The plugin uses 64 px PNGs of them in the UI's text colour (`XivMcp/images/apps/`), rendered with [resvg](https://github.com/yisibl/resvg-js):

```js
const svg = fs.readFileSync("branding/apps/grok.svg", "utf8").replace(/currentColor/g, "#E6EDF3");
fs.writeFileSync("XivMcp/images/apps/grok.png", new Resvg(svg, { fitTo: { mode: "width", value: 64 } }).render().asPng());
```
