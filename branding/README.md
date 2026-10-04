# Branding

Everything here is generated. The mark, its colours and type, the files and how to regenerate them are described on the [Branding](https://individualgather.github.io/xiv-mcp/docs/developer/contributing/branding/) page of the developer docs (source: [`docs-site/content/docs/developer/contributing/branding.mdx`](../docs-site/content/docs/developer/contributing/branding.mdx)).

```powershell
powershell -File branding/draw-assets.ps1        # banner, icons, logos, docs-site files
powershell -File XivMcp/images/draw-icon.ps1 -Out XivMcp/images/icon.png   # the plugin icon
```

## Docs site files (`web/`)

Named after the Next.js app folder conventions, so Fumadocs picks them up without configuration:

| File | Put it in | Used for |
|---|---|---|
| `favicon.ico` | `app/` | Browser tab (16, 32 and 48 px). |
| `icon.png` | `app/` | Modern browsers and installed web apps (512 px). |
| `icon.svg` | `app/` | Browsers that take an SVG icon. |
| `apple-icon.png` | `app/` | iOS home screen (180 px, opaque). |
| `opengraph-image.png` | `app/` | Link previews (1200 × 630). |
| `twitter-image.png` | `app/` | Link previews on X (same image). |
| `logo.svg` | `public/` | The navigation bar logo: `nav: { title: <><img src="/logo.svg" alt="" width={24} height={24} /> XIV MCP</> }` in Fumadocs' layout options. |
