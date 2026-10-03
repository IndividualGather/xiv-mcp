using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Collections overview (mounts, minions, orchestrion, fashion accessories, facewear) plus armoire and glamour dresser.</summary>
internal static class CollectionTools
{
    /// <summary>Friendly collection name → unlock category (see list_unlock_categories).</summary>
    private static readonly (string Key, string Title, string Category)[] Collections =
    [
        ("mounts", "Mounts", "mount"),
        ("minions", "Minions", "companion"),
        ("orchestrion", "Orchestrion rolls", "orchestrion"),
        ("fashion_accessories", "Fashion accessories", "ornament"),
        ("facewear", "Facewear (each colour variant counted)", "glasses"),
    ];

    public static IEnumerable<McpTool> Create(GlamourTracker glamour)
    {
        yield return new McpTool
        {
            Name = "get_collections",
            Description = "Collection progress of the logged-in character: mounts, minions, orchestrion rolls, fashion accessories and facewear " +
                          "(owned / total; totals come from the game data and may include entries that can't be obtained), plus how many items are in the " +
                          "armoire and the glamour dresser. Give 'collection' to list its entries " +
                          "(filter owned / missing, name query).",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "collection": { "type": "string", "enum": ["mounts", "minions", "orchestrion", "fashion_accessories", "facewear"],
                                    "description": "List the entries of one collection." },
                    "filter": { "type": "string", "enum": ["all", "owned", "missing"], "description": "Which entries to list (default all)." },
                    "query": { "type": "string", "description": "Only entries whose name contains this text." },
                    "limit": { "type": "integer", "description": "Max entries (default 200, max 1000)." }
                  }
                }
                """,
            Handler = async (args, _) =>
            {
                var which = args.String("collection")?.ToLowerInvariant();
                if (which is not null)
                {
                    var c = Collections.FirstOrDefault(x => x.Key == which || x.Category == which);
                    if (c.Key is null) throw new ToolException($"Unknown collection '{which}'.");
                    var filter = args.String("filter")?.ToLowerInvariant() ?? "all";
                    var rows = await UnlockTools.Evaluate(c.Category, args.String("query")).ConfigureAwait(false);
                    return new
                    {
                        collection = c.Title,
                        owned = rows.Count(r => r.Unlocked),
                        total = rows.Count,
                        entries = rows
                            .Where(r => filter switch { "owned" => r.Unlocked, "missing" => !r.Unlocked, _ => true })
                            .Take(args.Int("limit", 200, 1, 1000))
                            .Select(r => new { id = r.Id, name = r.Name, owned = r.Unlocked })
                            .ToList(),
                    };
                }

                var summary = new List<object>();
                foreach (var c in Collections)
                {
                    var rows = await UnlockTools.Evaluate(c.Category).ConfigureAwait(false);
                    var owned = rows.Count(r => r.Unlocked);
                    summary.Add(new { collection = c.Title, key = c.Key, owned, total = rows.Count, percent = rows.Count == 0 ? 0 : Math.Round(100.0 * owned / rows.Count, 1) });
                }
                var snap = Svc.PlayerState.IsLoaded ? glamour.Get(Svc.PlayerState.ContentId) : null;
                var (armoireLive, dresserLive) = await Game.Run(() => (GlamourTracker.ArmoireLive, GlamourTracker.DresserLive)).ConfigureAwait(false);
                return new
                {
                    collections = summary,
                    armoire = snap?.ArmoireCapturedUtc is { } at
                        ? new { items = snap.ArmoireCabinetIds.Count, total = ArmoireTotal(), cache = CacheFreshness.Describe(at, armoireLive, GlamourTracker.ArmoireHint) }
                        : (object)new { notCaptured = true, suggestion = "To capture it: " + GlamourTracker.ArmoireHint },
                    glamourDresser = snap?.DresserCapturedUtc is { } dt
                        ? new { items = snap.DresserItems.Count, capacity = snap.DresserCapacity, cache = CacheFreshness.Describe(dt, dresserLive, GlamourTracker.DresserHint) }
                        : (object)new { notCaptured = true, suggestion = "To capture it: " + GlamourTracker.DresserHint },
                };
            },
        };

        yield return new McpTool
        {
            Name = "get_armoire",
            Description = "Items stored in the armoire (seasonal and event gear, ...), grouped by armoire category, from the glamour cache — the game " +
                          "only sends the armoire after it was opened in an inn room. Optionally list which armoire-eligible items are NOT stored.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Only items whose name contains this text." },
                    "category": { "type": "string", "description": "Only this armoire category (part of its name)." },
                    "show_missing": { "type": "boolean", "description": "List armoire-eligible items that are not stored instead (default false)." }
                  }
                }
                """,
            Handler = (args, _) => Game.Run<object?>(() =>
            {
                var snap = Snapshot(glamour) is { ArmoireCapturedUtc: { } captured } s ? s
                    : throw new ToolException("No armoire data recorded yet. To capture it: " + GlamourTracker.ArmoireHint);
                var query = args.String("query");
                var category = args.String("category");
                var missing = args.Bool("show_missing", false);
                var stored = s.ArmoireCabinetIds.ToHashSet();

                var groups = Svc.Data.GetExcelSheet<Cabinet>()
                    .Where(r => r.RowId != 0 && r.Item.RowId != 0 && stored.Contains(r.RowId) != missing)
                    .Select(r => (row: r, item: Excel.Name(r.Item), cat: CategoryName(r)))
                    .Where(x => Game.Matches(x.item, query) && Game.Matches(x.cat, category))
                    .GroupBy(x => x.cat ?? "Other")
                    .OrderBy(g => g.Key)
                    .Select(g => new { category = g.Key, count = g.Count(), items = g.OrderBy(x => x.row.Order).Select(x => new { itemId = x.row.Item.RowId, name = x.item }).ToList() })
                    .ToList();

                return new
                {
                    character = s.Character,
                    showing = missing ? "not stored" : "stored",
                    stored = stored.Count,
                    total = ArmoireTotal(),
                    cache = CacheFreshness.Describe(captured, GlamourTracker.ArmoireLive, GlamourTracker.ArmoireHint),
                    categories = groups,
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_glamour_dresser",
            Description = "Items stored in the glamour dresser, with dyes and the dresser slot, from the glamour cache — the game only sends the dresser " +
                          "after it (or a glamour plate) was opened. Filter by item name or equipment slot category.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Only items whose name contains this text." },
                    "slot": { "type": "string", "description": "Equipment category, e.g. Head, Body, Hands, Legs, Feet, Ears, Neck, Wrists, Ring, Weapon." }
                  }
                }
                """,
            Handler = (args, _) => Game.Run<object?>(() =>
            {
                var snap = Snapshot(glamour) is { DresserCapturedUtc: { } captured } s ? s
                    : throw new ToolException("No glamour dresser data recorded yet. To capture it: " + GlamourTracker.DresserHint);
                var query = args.String("query");
                var slot = args.String("slot");
                var items = Svc.Data.GetExcelSheet<Item>();
                var stains = Svc.Data.GetExcelSheet<Stain>();

                var list = snap.DresserItems.Select(d =>
                    {
                        var baseId = d.ItemId % 1_000_000;
                        var row = items.GetRowOrDefault(baseId);
                        return new
                        {
                            dresserSlot = d.Slot,
                            itemId = baseId,
                            name = row?.Name.ExtractText(),
                            hq = d.ItemId >= 1_000_000 ? true : (bool?)null,
                            category = row is { } r ? Excel.Name(r.ItemUICategory) : null,
                            dyes = new[] { d.Stain0, d.Stain1 }.Where(x => x != 0).Select(x => stains.GetRowOrDefault(x)?.Name.ExtractText() ?? x.ToString()).ToList(),
                        };
                    })
                    .Where(x => Game.Matches(x.name, query) && Game.Matches(x.category, slot))
                    .ToList();

                return new
                {
                    character = s.Character,
                    stored = snap.DresserItems.Count,
                    capacity = snap.DresserCapacity,
                    cache = CacheFreshness.Describe(captured, GlamourTracker.DresserLive, GlamourTracker.DresserHint),
                    items = list,
                };
            }),
        };
    }

    private static GlamourSnapshot? Snapshot(GlamourTracker glamour) =>
        Svc.ClientState.IsLoggedIn && Svc.PlayerState.IsLoaded ? glamour.Get(Svc.PlayerState.ContentId)
        : throw new ToolException("No character is logged in.");

    private static int ArmoireTotal() => Svc.Data.GetExcelSheet<Cabinet>().Count(r => r.RowId != 0 && r.Item.RowId != 0);

    private static string? CategoryName(Cabinet row) =>
        row.Category.ValueNullable is { } c ? c.Category.ValueNullable?.Text.ExtractText() : null;
}
