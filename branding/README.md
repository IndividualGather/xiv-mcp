# Branding

XIV MCP's mark is a Final Fantasy crystal as the body of a satellite: tilted as if in orbit, with a solar panel on each side and waves sent from its tip. It is drawn as rounded line art in a teal-to-blue gradient, on a dark tile inside a gradient frame.

Everything here is generated. Change the drawing in `brand.ps1`, then run:

```powershell
powershell -File branding/draw-assets.ps1        # banner, icons, logos, docs-site files
powershell -File XivMcp/images/draw-icon.ps1 -Out XivMcp/images/icon.png   # the plugin icon
```

## Colours and type

| | Value |
|---|---|
| Teal (gradient start, top left) | `#3BEFC4` |
| Blue (gradient end, bottom right) | `#3F7BFF` |
| Tile (behind the mark) | `#1C1C1E` |
| Card (banner and social image) | `#141518` |
| Wordmark | Bahnschrift SemiBold, filled with the gradient |
| Tagline | Segoe UI Semibold, `#E6EDF3` |

## Files

| File | Use |
|---|---|
| `banner.png` | README banner, 1600 × 400 (shown at 800 × 200). |
| `icon.png`, `icon.svg` | The icon: mark, tile and frame. |
| `logo.svg`, `logo.png` | The mark alone on transparent, for any background. The crystal's facets are cut out, so the page shows through them. |
| `icon-outline.*`, `logo-outline.*` | The outline variant: the crystal drawn in lines instead of filled. |

### Docs site (`web/`)

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
