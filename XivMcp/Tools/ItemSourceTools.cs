using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;
using SourceType = XivMcp.Util.Teamcraft.SourceType;

namespace XivMcp.Tools;

/// <summary>Where an item comes from: Teamcraft's item data (vendors, exchanges, drops, ventures, nodes, ...) and optionally the wiki.</summary>
internal static class ItemSourceTools
{
    private const string WikiApi = "https://ffxiv.consolegameswiki.com/mediawiki/api.php";

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        yield return new McpTool
        {
            Name = "get_item_sources",
            Description = "Where an item can be obtained, from FFXIV Teamcraft's item data: crafted by (job, level, ingredients), gil vendors (NPC, zone, " +
                          "map coordinates, price), currency exchanges (what they cost — tomestones, scrips, seals, other items), gathering nodes, " +
                          "timed nodes, fishing, monster drops, duties, FATEs, retainer ventures, voyages, desynthesis, reduction, treasure coffers, quests " +
                          "and more. include_wiki=true also returns the item's ffxiv.consolegameswiki.com entry (acquisition notes). The Teamcraft data " +
                          "(~30 MB) is downloaded once and refreshed when Teamcraft updates it. Requires 'Online lookups' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": "string", "description": "Item name or id." },
                    "types": { "type": "array", "items": { "type": "string" }, "description": "Only these source kinds, e.g. [\"vendor\", \"exchange\", \"gathering\", \"drop\", \"venture\", \"crafted\"]." },
                    "include_wiki": { "type": "boolean", "description": "Also fetch the wiki page (default false)." },
                    "max_per_type": { "type": "integer", "description": "Cap entries per kind (default 10)." }
                  },
                  "required": ["item"]
                }
                """,
            Handler = async (args, ct) =>
            {
                RequireOnline(config);
                var item = await Game.Run(() => Items.Resolve(args.String("item") ?? throw new ToolException("'item' is required."))).ConfigureAwait(false);
                var filter = args.StringList("types").Select(t => t.ToLowerInvariant()).ToHashSet();
                var max = args.Int("max_per_type", 10, 1, 100);
                var raw = await Teamcraft.Sources(item.RowId, ct).ConfigureAwait(false);
                var sources = await Game.Run(() => Describe(raw, filter, max)).ConfigureAwait(false);
                object? wiki = args.Bool("include_wiki", false) ? await Wiki(item.Name.ExtractText(), ct).ConfigureAwait(false) : null;
                return new
                {
                    item = new { id = item.RowId, name = item.Name.ExtractText() },
                    sources,
                    none = sources.Count == 0 ? "Teamcraft lists no source for this item (it may be untradeable event/quest only, or unobtainable)." : null,
                    wiki,
                    data = Teamcraft.Status(),
                };
            },
        };
    }

    public static void RequireOnline(Configuration config)
    {
        if (!config.AllowOnlineData)
            throw new ToolException("Online lookups are disabled. Enable \"Online lookups\" in the XIV MCP settings window (/xivmcp) in game.");
    }

    // ------------------------------------------------------------------ describing Teamcraft sources

    private static readonly Dictionary<SourceType, string> Kinds = new()
    {
        [SourceType.CraftedBy] = "crafted", [SourceType.TradeSources] = "exchange", [SourceType.Vendors] = "vendor",
        [SourceType.ReducedFrom] = "reduction", [SourceType.Desynths] = "desynthesis", [SourceType.Instances] = "duty",
        [SourceType.GatheredBy] = "gathering", [SourceType.Gardening] = "gardening", [SourceType.Voyages] = "voyage", [SourceType.Drops] = "drop",
        [SourceType.Alarms] = "timed_node", [SourceType.Masterbooks] = "masterbook", [SourceType.Treasures] = "treasure", [SourceType.Fates] = "fate",
        [SourceType.Ventures] = "venture", [SourceType.TripleTriadDuels] = "triple_triad", [SourceType.TripleTriadPack] = "triple_triad_pack",
        [SourceType.Quests] = "quest", [SourceType.Achievements] = "achievement", [SourceType.Requirements] = "requirement",
        [SourceType.Mogstation] = "mogstation", [SourceType.IslandPasture] = "island_pasture", [SourceType.IslandCrop] = "island_crop",
    };

    /// <summary>Teamcraft sources as { kind → entries } with names resolved from the game data. Framework thread (sheet access).</summary>
    public static Dictionary<string, object> Describe(JsonArray? sources, HashSet<string> filter, int max)
    {
        var result = new Dictionary<string, object>();
        if (sources is null) return result;
        foreach (var source in sources.OfType<JsonObject>())
        {
            var type = (SourceType)(source["type"]?.GetValue<int>() ?? 0);
            if (!Kinds.TryGetValue(type, out var kind)) continue;
            if (filter.Count > 0 && !filter.Contains(kind) && !filter.Contains(kind + "s")) continue;
            var data = source["data"];
            object? described = type switch
            {
                SourceType.CraftedBy => Each(data, max, Crafted),
                SourceType.Vendors => Each(data, max, Vendor),
                SourceType.TradeSources => Each(data, max, Exchange),
                SourceType.GatheredBy => Gathered(data, max),
                SourceType.Alarms => Each(data, max, TimedNode),
                SourceType.Drops => Each(data, max, Drop),
                SourceType.Ventures => Each(data, max, Venture),
                SourceType.Fates => Each(data, max, Fate),
                SourceType.Instances => Ids(data, max, Duty),
                SourceType.ReducedFrom or SourceType.Desynths or SourceType.Treasures => Ids(data, max, id => Excel.Ref<Item>(id)),
                SourceType.Quests => Ids(data, max, id => Excel.Ref<Quest>(id)),
                SourceType.Achievements => Ids(data, max, id => Excel.Ref<Achievement>(id)),
                SourceType.Voyages => Each(data, max, v => new { kind = v["type"]?.GetValue<int>() == 1 ? "submersible" : "airship", name = En(v["name"]) }),
                SourceType.Masterbooks => Each(data, max, m => Excel.Ref<Item>(UInt(m["id"]))),
                _ => data?.DeepClone(),
            };
            if (described is not null) result[kind] = described;
        }
        return result;
    }

    private static List<object> Each(JsonNode? data, int max, Func<JsonObject, object?> map) =>
        (data as JsonArray ?? []).OfType<JsonObject>().Select(map).Where(o => o is not null).Take(max).ToList()!;

    private static List<object> Ids(JsonNode? data, int max, Func<uint, object> map) =>
        (data as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<uint>(out var id) ? id : UInt(n?["id"]))
            .Where(id => id != 0).Distinct().Take(max).Select(map).ToList();

    private static object Crafted(JsonObject r) => new
    {
        recipeId = uint.TryParse(r["id"]?.ToString(), out var rid) ? rid : 0,
        job = JobAbbr(UInt(r["job"])),
        level = r["lvl"]?.GetValue<int>(),
        yield = r["yield"]?.GetValue<int>(),
        ingredients = (r["ingredients"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(i => new { id = UInt(i["id"]), name = Items.Name(UInt(i["id"])), amount = i["amount"]?.GetValue<int>() }).ToList(),
    };

    private static object Vendor(JsonObject v) => new
    {
        npc = NpcName(UInt(v["npcId"])),
        npcId = UInt(v["npcId"]),
        shop = En(v["shopName"]) is { Length: > 0 } s ? s : null,
        priceGil = v["price"]?.GetValue<int>(),
        location = Location(v),
    };

    private static object Exchange(JsonObject t) => new
    {
        shop = En(t["shopName"]),
        shopId = UInt(t["id"]),
        shopType = t["type"]?.ToString(),
        npcs = (t["npcs"] as JsonArray ?? []).OfType<JsonObject>().Take(5)
            .Select(n => new { npc = NpcName(UInt(n["id"])), npcId = UInt(n["id"]), location = Location(n) }).ToList(),
        trades = (t["trades"] as JsonArray ?? []).OfType<JsonObject>().Take(5).Select(tr => new
        {
            cost = (tr["currencies"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(c => new { id = UInt(c["id"]), name = Items.Name(UInt(c["id"])), amount = c["amount"]?.GetValue<int>() }).ToList(),
            receive = (tr["items"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(c => new { id = UInt(c["id"]), name = Items.Name(UInt(c["id"])), amount = c["amount"]?.GetValue<int>() }).ToList(),
        }).ToList(),
    };

    private static readonly string[] NodeTypes = ["mining", "quarrying", "logging", "harvesting"];

    private static object? Gathered(JsonNode? data, int max)
    {
        if (data is not JsonObject g) return null;
        return new
        {
            level = g["level"]?.GetValue<int>(),
            nodes = (g["nodes"] as JsonArray ?? []).OfType<JsonObject>().Take(max).Select(n => new
            {
                type = n["type"]?.GetValue<int>() is int t && t >= 0 && t < NodeTypes.Length ? NodeTypes[t] : n["type"]?.ToString(),
                level = n["level"]?.GetValue<int>(),
                zone = Excel.NameOf<PlaceName>(UInt(n["zoneId"])),
                map = new { x = n["x"]?.GetValue<double>(), y = n["y"]?.GetValue<double>() },
                timed = n["limited"]?.GetValue<bool>() == true || (n["spawns"] as JsonArray)?.Count > 0,
                legendary = n["legendary"]?.GetValue<bool>() == true ? true : (bool?)null,
                ephemeral = n["ephemeral"]?.GetValue<bool>() == true ? true : (bool?)null,
                spawnsEorzeaHours = (n["spawns"] as JsonArray)?.Count > 0 ? n["spawns"]!.DeepClone() : null,
            }).ToList(),
        };
    }

    private static object TimedNode(JsonObject n) => new
    {
        type = n["type"]?.GetValue<int>() is int t && t >= 0 && t < NodeTypes.Length ? NodeTypes[t] : n["type"]?.ToString(),
        zone = Excel.NameOf<PlaceName>(UInt(n["zoneId"])),
        map = n["coords"]?.DeepClone(),
        spawnsEorzeaHours = n["spawns"]?.DeepClone(),
        durationHours = n["duration"]?.GetValue<int>(),
        ephemeral = n["ephemeral"]?.GetValue<bool>(),
    };

    private static object Drop(JsonObject d) => new
    {
        monster = Excel.NameOf<BNpcName>(UInt(d["id"])) is { Length: > 0 } m ? Game.TitleCase(m) : $"monster {UInt(d["id"])}",
        zone = d["zoneid"] is { } z ? Excel.NameOf<PlaceName>(UInt(z)) : null,
        map = d["position"] is JsonObject p ? new { x = Math.Round(p["x"]?.GetValue<double>() ?? 0, 1), y = Math.Round(p["y"]?.GetValue<double>() ?? 0, 1) } : null,
    };

    private static object Venture(JsonObject v)
    {
        var task = Svc.Data.GetExcelSheet<RetainerTask>().GetRowOrDefault(UInt(v["id"]));
        return new
        {
            ventureId = UInt(v["id"]),
            retainerClass = task is { } t ? t.ClassJobCategory.ValueNullable?.Name.ExtractText() : null,
            retainerLevel = v["lvl"]?.GetValue<int>(),
            ventureCost = v["cost"]?.GetValue<int>(),
            requiredItemLevel = v["reqIlvl"]?.GetValue<int>() is > 0 and var il ? il : (int?)null,
            requiredGathering = v["reqGathering"]?.GetValue<int>() is > 0 and var rg ? rg : (int?)null,
            quantities = (v["quantities"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(q => new { quantity = q["quantity"]?.GetValue<int>(), stat = q["stat"]?.ToString(), atLeast = q["value"]?.GetValue<int>() ?? 0 }).ToList(),
        };
    }

    private static object Fate(JsonObject f) => new
    {
        fate = Excel.NameOf<Fate>(UInt(f["id"])),
        level = f["level"]?.GetValue<int>(),
        zone = Excel.NameOf<PlaceName>(UInt(f["zoneId"])),
        map = f["coords"]?.DeepClone(),
    };

    private static object Duty(uint instanceContentId)
    {
        var cfc = Svc.Data.GetExcelSheet<ContentFinderCondition>()
            .FirstOrDefault(c => c.ContentLinkType == 1 && c.Content.RowId == instanceContentId);
        return new { id = instanceContentId, name = cfc.RowId != 0 ? Game.TitleCase(cfc.Name.ExtractText()) : null };
    }

    public static object? Location(JsonObject n)
    {
        if (n["zoneId"] is null) return null;
        return new
        {
            zone = Excel.NameOf<PlaceName>(UInt(n["zoneId"])),
            map = n["coords"] is JsonObject c ? new { x = c["x"]?.GetValue<double>(), y = c["y"]?.GetValue<double>() } : null,
        };
    }

    public static string NpcName(uint npcId) =>
        Svc.Data.GetExcelSheet<ENpcResident>().GetRowOrDefault(npcId)?.Singular.ExtractText() is { Length: > 0 } n ? Game.TitleCase(n) : $"NPC {npcId}";

    private static string? JobAbbr(uint job) => Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(job)?.Abbreviation.ExtractText();

    public static uint UInt(JsonNode? n) =>
        n is JsonValue v ? (v.TryGetValue<uint>(out var u) ? u : v.TryGetValue<double>(out var d) ? (uint)d : uint.TryParse(v.ToString(), out var p) ? p : 0) : 0;

    public static string? En(JsonNode? n) => n?["en"]?.ToString();

    // ------------------------------------------------------------------ wiki

    /// <summary>The item's wiki page (consolegameswiki), trimmed to the parts about obtaining it.</summary>
    private static async Task<object> Wiki(string itemName, CancellationToken ct)
    {
        var url = $"{WikiApi}?action=parse&prop=wikitext&format=json&redirects=1&page={Uri.EscapeDataString(itemName)}";
        var page = $"https://ffxiv.consolegameswiki.com/wiki/{Uri.EscapeDataString(itemName.Replace(' ', '_'))}";
        try
        {
            var json = JsonNode.Parse(await Teamcraft.Http.GetStringAsync(url, ct).ConfigureAwait(false));
            var text = json?["parse"]?["wikitext"]?["*"]?.ToString();
            if (text is null) return new { url = page, found = false, error = json?["error"]?["info"]?.ToString() };

            // Keep the template fields and sections that describe acquisition; drop the rest to stay compact.
            var interesting = new Regex(@"(?im)^\|\s*(?<k>[a-z ]*(acquir|obtain|gather|drop|vendor|purchas|sold|shop|venture|reward|desynth|reduc|trade|exchange|cost|currency|source|note)[a-z ]*)\s*=\s*(?<v>.*)$");
            var fields = interesting.Matches(text).Select(m => new { field = m.Groups["k"].Value.Trim(), value = Clean(m.Groups["v"].Value) })
                .Where(f => f.value.Length > 0).Take(30).ToList();
            var sections = Regex.Matches(text, @"(?ms)^==+\s*(?<h>[^=]+?)\s*==+\s*$(?<b>.*?)(?=^==|\z)")
                .Where(m => Regex.IsMatch(m.Groups["h"].Value, "(?i)acqui|obtain|source|gather|purchas|drop|venture|reward"))
                .Select(m => new { heading = m.Groups["h"].Value.Trim(), text = Truncate(Clean(m.Groups["b"].Value), 1500) }).ToList();
            return new { url = page, found = true, fields, sections, rawExcerpt = fields.Count == 0 && sections.Count == 0 ? Truncate(Clean(text), 2000) : null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { url = page, found = false, error = ex.Message };
        }
    }

    private static string Clean(string wikitext)
    {
        var s = Regex.Replace(wikitext, @"\[\[(?:[^|\]]*\|)?([^\]]*)\]\]", "$1");   // [[link|text]] → text
        s = Regex.Replace(s, @"<[^>]+>", " ");
        s = Regex.Replace(s, @"'{2,}", "");
        return Regex.Replace(s, @"[ \t]+", " ").Trim();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + " …";
}
