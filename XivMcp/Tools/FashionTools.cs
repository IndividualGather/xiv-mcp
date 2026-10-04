using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using XivMcp.Fashion;
using XivMcp.Mcp;
using XivMcp.Permissions;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// The Fashion Report and what it needs: putting on gear, taking items out of the glamour dresser or armoire, this week's report
/// (fashionreportxiv.com), presenting to the Masked Rose, and one job that does all of it for a ready-made set.
/// </summary>
internal static class FashionTools
{
    private const string ReportUrl = "https://fashionreportxiv.com/api/report-state";
    private const uint MaskedRose = 1025176;

    /// <summary>The Gold Saucer VIP Card (item) and the bonus it grants (status).</summary>
    private const uint VipCardItem = 14947, VipCardStatus = 1079;

    /// <summary>The glamour dresser in inn rooms, and the armoire objects (from AutoRetainer's armoire code).</summary>
    private static readonly uint[] GlamourDressers = [2009439];
    private static readonly uint[] Armoires = [2001405, 2001406, 2001407, 2005630, 2007709];

    private static readonly InventoryType[] Carried =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist,
        InventoryType.ArmoryRings,
    ];

    public static IEnumerable<McpTool> Create(Configuration config, RetainerTracker retainers, Func<JobManager> jobs, Func<string?> client)
    {
        yield return new McpTool
        {
            Name = "equip_items",
            Description = "Puts on pieces of gear from the bags or the armoury chest, each into its slot (rings into a free ring slot), as if the " +
                          "player equipped them. Checks that the current job and level can wear each one. Out of combat only. Requires 'Items & " +
                          "retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "items": { "type": "array", "items": { "type": ["string", "integer"] }, "minItems": 1, "description": "Item names or ids." }
                  },
                  "required": ["items"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var items = (args.Node("items")?.AsArray() ?? throw new ToolException("'items' is required.")).Select(n => n?.ToString() ?? "").ToList();
                var done = new List<object>();
                foreach (var item in items) done.Add(await Equip(item, ct).ConfigureAwait(false));
                return new { equipped = done };
            },
        };

        yield return new McpTool
        {
            Name = "retrieve_glamour_item",
            Description = "Takes an item out of the glamour dresser or the armoire into the bags. Needs the player in an inn room (navigate_to " +
                          "inn first): XIV MCP walks to the dresser or armoire, opens it and takes the item out. 'from' says where (default: the " +
                          "glamour dresser if the item is there, otherwise the armoire). Requires 'Items & retainers' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "item": { "type": ["string", "integer"], "description": "Item name or id." },
                    "from": { "type": "string", "enum": ["dresser", "armoire"], "description": "Where it is stored." }
                  },
                  "required": ["item"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var item = await Game.Run(() => ResolveItem(args.Node("item")?.ToString() ?? throw new ToolException("'item' is required."))).ConfigureAwait(false);
                var from = args.String("from") ?? await Game.Run(() => InDresser(item.RowId) ? "dresser" : "armoire").ConfigureAwait(false);
                return from == "armoire" ? await FromArmoire(item, ct).ConfigureAwait(false) : await FromDresser(item, ct).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "get_fashion_report",
            Description = "This week's Fashion Report from fashionreportxiv.com: the theme, the hint per slot, and the ready-made 'easy 80' and 'easy " +
                          "100' sets with their dyes. For each piece: whether the player has it and where (bags, armoury chest, a retainer, the " +
                          "glamour dresser, the armoire), whether a vendor sells it, and whether the player has the dye. Also whether judging is " +
                          "open (Friday to the Tuesday reset), and the attempts left and high score once the Masked Rose was talked to this " +
                          "session. complete_fashion_report does the rest. Goes online (Online lookups in /xivmcp).",
            InputSchema = """{ "type": "object", "properties": { "set": { "type": "string", "enum": ["easy80", "easy100"], "description": "Which set to check (default easy80)." } } }""",
            Handler = async (args, ct) =>
            {
                var report = await FetchReport(ct).ConfigureAwait(false);
                var set = args.String("set") == "easy100" ? report.Easy100 : report.Easy80;
                var pieces = await Game.RunLoggedIn(() => set.Pieces.Select(p => Inspect(p, retainers)).ToList()).ConfigureAwait(false);
                var state = await Game.Run(ReportState).ConfigureAwait(false);
                return new
                {
                    week = report.Week, theme = report.Theme,
                    hints = report.Hints.Select(h => new { h.Slot, h.Hint }),
                    set = args.String("set") ?? "easy80", setIsThisWeeks = set.Fresh,
                    pieces = pieces.Select(p => p.Describe()),
                    otherDyes = set.OtherDyes.Select(d => new { slot = d.Slot, dye = d.Dye }),
                    judgingOpen = FashionSchedule.IsJudgingOpen(DateTime.UtcNow),
                    attemptsLeft = state?.Remaining, highScore = state?.HighScore,
                    theorycraft = report.Theorycraft,
                };
            },
        };

        yield return new McpTool
        {
            Name = "present_fashion_report",
            Description = "Presents the player's outfit to the Masked Rose at the Gold Saucer for this week's Fashion Report (Friday to the Tuesday " +
                          "reset): puts on the given 'items' first (from the bags or armoury chest), goes to the Masked Rose, chooses to be " +
                          "judged and confirms, and returns the score. Right before talking to her it uses a Gold Saucer VIP Card (more MGP) unless " +
                          "its bonus is already active, if the setting in /xivmcp says so (on by default) or 'use_vip_card' is true. " +
                          "Pieces listed in 'discard' (bought for the report) are thrown away after judging when cheap enough, if the setting says so. The look counts as worn, including glamours and dyes. Each week has 4 " +
                          "attempts; the best one counts.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "items": { "type": "array", "items": { "type": ["string", "integer"] }, "description": "Gear to put on first (names or ids)." },
                    "use_vip_card": { "type": "boolean", "description": "Use a Gold Saucer VIP Card right before presenting (default: the setting in /xivmcp, on unless turned off)." },
                    "discard": { "type": "array", "items": { "type": "integer" }, "description": "Item ids bought for the report: after judging, the gearset goes back on and those worth at most the limit in /xivmcp (5,000 gil by default) are thrown away, if that setting is on." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                var vipCard = args.Node("use_vip_card") is { } v ? v.GetValue<bool>() : config.FashionReportVipCard;
                if (!FashionSchedule.IsJudgingOpen(DateTime.UtcNow)) throw new ToolException("The Masked Rose only judges from Friday 08:00 UTC until the weekly reset on Tuesday.");
                var steps = new List<string>();
                foreach (var item in args.Node("items")?.AsArray().Select(n => n?.ToString() ?? "") ?? [])
                {
                    await Equip(item, ct).ConfigureAwait(false);
                    steps.Add($"Put on {item}.");
                }
                var discard = config.FashionReportDiscard
                    ? args.Node("discard")?.AsArray().Select(n => n?.GetValue<uint>() ?? 0).Where(i => i != 0).Distinct().ToList() ?? []
                    : [];
                return await Present(steps, vipCard, discard, config.FashionReportDiscardMaxValue, config.AllowOnlineData, ct).ConfigureAwait(false);
            },
        };

        yield return new McpTool
        {
            Name = "complete_fashion_report",
            Description = "Does this week's Fashion Report with a ready-made set from fashionreportxiv.com ('easy80' by default, or 'easy100'): " +
                          "plans how to get each piece the player doesn't carry (from a retainer at a summoning bell, the glamour dresser or " +
                          "armoire in an inn room, a vendor, the market board up to 'max_market_price', or a dungeon with AutoDuty), the dyes, and starts one background job that gets the pieces, dyes them, puts " +
                          "them on and presents the outfit to the Masked Rose. Pieces with no known way to get them stop it before it starts " +
                          "(see get_item_sources). Follow it with get_job. Goes online for the report (Online lookups in /xivmcp); each step " +
                          "follows its own module.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "set": { "type": "string", "enum": ["easy80", "easy100"], "description": "Which set (default easy80)." },
                    "max_market_price": { "type": "integer", "minimum": 1, "description": "Most gil per piece on the market board (default 50000)." }
                  }
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (config.CorePolicy.ModeFor("online", Access.Read) == PolicyMode.Deny)
                    throw new ToolException("This reads the weekly report online: allow 'Online lookups' in /xivmcp → Modules.");
                var setName = args.String("set") ?? "easy80";
                var report = await FetchReport(ct).ConfigureAwait(false);
                var set = setName == "easy100" ? report.Easy100 : report.Easy80;
                if (set.Pieces.Count == 0) throw new ToolException($"fashionreportxiv.com has no {setName} set this week yet.");
                var pieces = await Game.RunLoggedIn(() => set.Pieces.Select(p => Inspect(p, retainers)).ToList()).ConfigureAwait(false);
                // Pieces with no other way: a dungeon that drops them, if AutoDuty can run one.
                var duties = new Dictionary<uint, (uint TerritoryType, string Name, string Mode)>();
                foreach (var p in pieces.Where(p => p.Source == ItemSource.None))
                    if (await DutyTools.FindFarmDuty(p.ItemId, ct).ConfigureAwait(false) is { } duty) duties[p.ItemId] = duty;
                pieces = pieces.Select(p => duties.ContainsKey(p.ItemId) ? p with { Source = ItemSource.Duty } : p).ToList();
                return Plan(report, setName, set, pieces, jobs(), client(), args.Int("max_market_price", 50_000, 1, int.MaxValue), duties);
            },
        };
    }

    // ---------------------------------------------------------------- planning

    /// <summary>One piece of the set as the player has it: the item, where it is, and its dye.</summary>
    private sealed record PieceState(FashionPiece Piece, uint ItemId, string Name, ItemWhereabouts Where, ItemSource Source,
                                     uint StainId, string? DyeItem, bool HaveDye, bool AlreadyDyed)
    {
        public object Describe() => new
        {
            slot = Piece.Slot, item = Name, itemId = ItemId, source = Source.ToString(), retainer = Where.Retainer,
            inBags = Where.InBags, inGlamourDresser = Where.InGlamourDresser, inArmoire = Where.InArmoire, soldByVendor = Where.AtVendor, onMarketBoard = Where.OnMarket,
            dye = Piece.Dye, dyeItem = DyeItem, haveDye = Piece.Dye is null ? (bool?)null : HaveDye, alreadyDyed = Piece.Dye is null ? (bool?)null : AlreadyDyed,
        };
    }

    /// <summary>Where the player has a piece, and its dye. Framework thread.</summary>
    private static unsafe PieceState Inspect(FashionPiece piece, RetainerTracker retainers)
    {
        var item = ResolveItem(piece.Item, english: true);
        var carried = Find(item.RowId);
        string? retainer = null;
        if (retainers.Get(Svc.PlayerState.ContentId) is { } cached)
            retainer = cached.Retainers.FirstOrDefault(r => r.Items.Any(i => i.ItemId == item.RowId))?.Name;
        var where = new ItemWhereabouts(carried is not null || IsEquipped(item.RowId), retainer, InDresser(item.RowId), InArmoire(item.RowId), SoldByVendor(item.RowId), false,
            OnMarket: !item.IsUntradable && item.ItemSearchCategory.RowId != 0);

        uint stain = 0;
        string? dyeItem = null;
        var haveDye = false;
        var already = false;
        if (piece.Dye is { } dye)
        {
            var row = Svc.Data.GetExcelSheet<Stain>(Dalamud.Game.ClientLanguage.English).FirstOrDefault(s => s.Name.ExtractText().Equals(dye, StringComparison.OrdinalIgnoreCase));
            if (row.RowId == 0) throw new ToolException($"The dye color '{dye}' is not known to the game.");
            stain = row.RowId;
            var dyeItems = row.Item.Where(i => i.RowId != 0).Select(i => i.RowId).ToList();
            var im = InventoryManager.Instance();
            var have = dyeItems.FirstOrDefault(i => im->GetInventoryItemCount(i) > 0);
            haveDye = have != 0;
            dyeItem = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(have != 0 ? have : dyeItems.FirstOrDefault())?.Name.ExtractText();
            already = CurrentStain(item.RowId) == stain;
        }
        var name = Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(item.RowId)?.Name.ExtractText() ?? piece.Item;
        return new PieceState(piece, item.RowId, name, where, FashionPlan.Choose(where), stain, dyeItem, haveDye, already);
    }

    /// <summary>The job: get the pieces (grouped by place), dye them, and present at the end.</summary>
    private static object Plan(WeeklyReport report, string setName, FashionSet set, List<PieceState> pieces, JobManager jobs, string? client, int maxMarketPrice,
                               Dictionary<uint, (uint TerritoryType, string Name, string Mode)> duties)
    {
        var missing = pieces.Where(p => p.Source == ItemSource.None).ToList();
        if (missing.Count > 0)
            throw new ToolException($"No known way to get {string.Join(", ", missing.Select(p => p.Name))}: not carried, not with a retainer, " +
                                    "not in the glamour dresser or armoire, no vendor sells it, it can't be traded, and no dungeon AutoDuty can run drops it. Check get_item_sources for drops or crafting.");

        var steps = new List<JobManager.Step>();
        void Add(string tool, JsonObject args, string note) => steps.Add(new JobManager.Step { Id = $"s{steps.Count + 1}", Tool = tool, Args = args, Note = note });

        foreach (var group in pieces.Where(p => p.Source == ItemSource.Retainer).GroupBy(p => p.Where.Retainer))
        {
            if (steps.All(s => s.Tool != "navigate_to" || s.Args["destination"]?.ToString() != "summoning_bell"))
                Add("navigate_to", new JsonObject { ["destination"] = "summoning_bell" }, "Goes to a summoning bell");
            var transfers = new JsonArray(group.Select(p => (JsonNode)new JsonObject { ["item_id"] = p.ItemId, ["from"] = group.Key, ["to"] = "player", ["stacks"] = 1 }).ToArray());
            Add("transfer_retainer_items", new JsonObject { ["transfers"] = transfers }, $"Takes {string.Join(", ", group.Select(p => p.Name))} from {group.Key}");
        }
        var stored = pieces.Where(p => p.Source is ItemSource.GlamourDresser or ItemSource.Armoire).ToList();
        if (stored.Count > 0)
        {
            Add("navigate_to", new JsonObject { ["destination"] = "inn" }, "Goes to the inn room");
            foreach (var p in stored)
                Add("retrieve_glamour_item", new JsonObject { ["item"] = p.ItemId, ["from"] = p.Source == ItemSource.Armoire ? "armoire" : "dresser" },
                    $"Takes {p.Name} out of the {(p.Source == ItemSource.Armoire ? "armoire" : "glamour dresser")}");
        }
        foreach (var p in pieces.Where(p => p.Source == ItemSource.Vendor))
            Add("buy_item", new JsonObject { ["item"] = p.ItemId, ["quantity"] = 1 }, $"Buys {p.Name}");
        foreach (var p in pieces.Where(p => p.Source == ItemSource.Duty))
        {
            var duty = duties[p.ItemId];
            Add("run_duty", new JsonObject
            {
                ["duty"] = duty.TerritoryType.ToString(), ["mode"] = duty.Mode, ["loops"] = 99,
                ["until"] = new JsonArray(new JsonObject { ["item"] = p.ItemId.ToString(), ["quantity"] = 1 }),
            }, $"Runs {duty.Name} ({duty.Mode}) until {p.Name} drops");
        }
        foreach (var p in pieces.Where(p => p.Source == ItemSource.Market))
            Add("buy_from_market_board", new JsonObject { ["item"] = p.ItemId, ["quantity"] = 1, ["max_unit_price"] = maxMarketPrice },
                $"Buys {p.Name} on the market board (at most {maxMarketPrice:N0} gil)");
        foreach (var dyeItem in pieces.Where(p => p.Piece.Dye is not null && !p.HaveDye && !p.AlreadyDyed && p.DyeItem is not null).Select(p => p.DyeItem!).Distinct())
        {
            var dyeId = ResolveItem(dyeItem).RowId;
            if (SoldByVendor(dyeId)) Add("buy_item", new JsonObject { ["item"] = dyeItem, ["quantity"] = 1 }, $"Buys {dyeItem}");
            else Add("buy_from_market_board", new JsonObject { ["item"] = dyeItem, ["quantity"] = 1, ["max_unit_price"] = maxMarketPrice },
                     $"Buys {dyeItem} on the market board");
        }
        foreach (var p in pieces.Where(p => p.Piece.Dye is not null && !p.AlreadyDyed))
            Add("dye_item", new JsonObject { ["item"] = p.ItemId, ["dye"] = p.Piece.Dye }, $"Dyes {p.Name} {p.Piece.Dye}");

        var judging = FashionSchedule.IsJudgingOpen(DateTime.UtcNow);
        if (judging)
            Add("present_fashion_report", new JsonObject
            {
                ["items"] = new JsonArray(pieces.Select(p => (JsonNode)p.ItemId).ToArray()),
                ["discard"] = new JsonArray(pieces.Where(p => p.Source is ItemSource.Vendor or ItemSource.Market).Select(p => (JsonNode)p.ItemId).ToArray()),
            }, "Puts on the outfit, presents it to the Masked Rose, and throws away cheap pieces it bought");
        if (steps.Count == 0) throw new ToolException("Nothing to do: judging is closed (Friday to the Tuesday reset) and you have everything already.");

        var job = jobs.Start($"Fashion Report: {report.Theme}", steps, client);
        return new
        {
            started = JobManager.Describe(job), week = report.Week, theme = report.Theme, set = setName, setIsThisWeeks = set.Fresh,
            pieces = pieces.Select(p => p.Describe()),
            note = judging ? null : "Judging is closed until Friday 08:00 UTC: the job only prepares the outfit; present it then with present_fashion_report.",
            poll = $"get_job id={job.Id}",
        };
    }

    private static async Task<WeeklyReport> FetchReport(CancellationToken ct)
    {
        try
        {
            using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, ReportUrl);
            using var response = await Teamcraft.Http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return WeeklyReport.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ToolException)
        {
            throw new ToolException($"Could not read this week's report from fashionreportxiv.com: {ex.Message}");
        }
    }

    private static unsafe (int Remaining, int HighScore)? ReportState()
    {
        var m = FashionCheckManager.Instance();
        return m == null || !m->IsInfoRequested || m->Theme.WeeklyTheme == 0 ? null : (m->PlayerInfo.Remaining, m->PlayerInfo.HighScore);
    }

    // ---------------------------------------------------------------- items

    /// <summary>An item by id, or by name in the game's language (or English, as guides write them).</summary>
    private static Item ResolveItem(string query, bool english = false)
    {
        var q = query.Trim();
        if (uint.TryParse(q, out var id) && Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(id) is { } byId) return byId;
        foreach (var language in english ? [Dalamud.Game.ClientLanguage.English] : new Dalamud.Game.ClientLanguage?[] { null, Dalamud.Game.ClientLanguage.English })
        {
            var sheet = language is { } l ? Svc.Data.GetExcelSheet<Item>(l) : Svc.Data.GetExcelSheet<Item>();
            var row = sheet.FirstOrDefault(i => i.Name.ExtractText().Equals(q, StringComparison.OrdinalIgnoreCase));
            if (row.RowId != 0) return Svc.Data.GetExcelSheet<Item>().GetRow(row.RowId);
        }
        throw new ToolException($"There is no item called '{query}'.");
    }

    private static unsafe (InventoryType Container, int Slot)? Find(uint itemId)
    {
        var im = InventoryManager.Instance();
        foreach (var type in Carried)
        {
            var container = im->GetInventoryContainer(type);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
                if (container->GetInventorySlot(i) is var slot && slot != null && slot->ItemId == itemId) return (type, i);
        }
        return null;
    }

    private static unsafe bool IsEquipped(uint itemId)
    {
        var equipped = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        for (var i = 0; equipped != null && i < equipped->Size; i++)
            if (equipped->GetInventorySlot(i)->ItemId == itemId) return true;
        return false;
    }

    private static unsafe byte CurrentStain(uint itemId)
    {
        foreach (var type in Carried.Prepend(InventoryType.EquippedItems))
        {
            var container = InventoryManager.Instance()->GetInventoryContainer(type);
            for (var i = 0; container != null && i < container->Size; i++)
                if (container->GetInventorySlot(i) is var slot && slot != null && slot->ItemId == itemId) return slot->GetStain(0);
        }
        return 0;
    }

    /// <summary>In the glamour dresser, from the game's item finder (kept per character, no need to open the dresser).</summary>
    private static unsafe bool InDresser(uint itemId)
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null) return false;
        foreach (var stored in finder->GlamourDresserItemIds)
            if (stored != 0 && stored % 1_000_000 % 500_000 == itemId) return true;
        return false;
    }

    /// <summary>In the armoire: live once it was opened this session, otherwise from the item finder's unlock bits (by Cabinet row).</summary>
    private static unsafe bool InArmoire(uint itemId)
    {
        if (CabinetRow(itemId) is not { } row) return false;
        var ui = UIState.Instance();
        if (ui != null && ui->Cabinet.IsCabinetLoaded()) return ui->Cabinet.IsItemInCabinet(row);
        var finder = ItemFinderModule.Instance();
        if (finder == null) return false;
        var bits = finder->CabinetItemUnlockBits;
        return row / 32 < bits.Length && (bits[(int)(row / 32)] >> (int)(row % 32) & 1) != 0;
    }

    private static uint? CabinetRow(uint itemId) =>
        Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>().FirstOrDefault(c => c.Item.RowId == itemId) is { RowId: > 0 } c ? c.RowId : null;

    private static readonly Lazy<HashSet<uint>> VendorItems = new(() =>
        Svc.Data.GetSubrowExcelSheet<GilShopItem>().SelectMany(r => r).Select(i => i.Item.RowId).Where(i => i != 0).ToHashSet());

    private static bool SoldByVendor(uint itemId) => VendorItems.Value.Contains(itemId);

    // ---------------------------------------------------------------- equipping

    private static async Task<object> Equip(string query, CancellationToken ct)
    {
        var (itemId, name, target) = await Game.RunLoggedIn(() => PrepareEquip(query)).ConfigureAwait(false);
        if (target is null) return new { item = name, already = true };
        if (!await WaitFor(() => IsEquipped(itemId), TimeSpan.FromSeconds(5), ct).ConfigureAwait(false))
            throw new ToolException($"{name} was not put on (the game may have refused it).");
        return new { item = name, slot = target };
    }

    /// <summary>Checks the item and moves it into its slot; returns the slot (null if it is worn already). Framework thread.</summary>
    private static unsafe (uint ItemId, string Name, int? Slot) PrepareEquip(string query)
    {
        var item = ResolveItem(query);
        var name = item.Name.ExtractText();
        if (IsEquipped(item.RowId)) return (item.RowId, name, null);
        if (Svc.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat]) throw new ToolException("Can't change gear during combat.");
        var category = item.EquipSlotCategory.ValueNullable ?? throw new ToolException($"{name} is not gear.");
        var flags = new[] { category.MainHand, category.OffHand, category.Head, category.Body, category.Gloves, category.Waist, category.Legs,
                            category.Feet, category.Ears, category.Neck, category.Wrists, category.FingerL, category.FingerR, category.SoulCrystal };
        var equipped = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        var slot = GearSlots.Target(flags, s => equipped->GetInventorySlot(s)->ItemId != 0) ?? throw new ToolException($"{name} is not gear.");

        var player = Svc.Objects.LocalPlayer ?? throw new ToolException("Not logged in.");
        if (item.LevelEquip > player.Level) throw new ToolException($"{name} needs level {item.LevelEquip}; you are {player.Level}.");
        if (item.ClassJobCategory.ValueNullable is { } jobs && player.ClassJob.ValueNullable is { } job
            && typeof(ClassJobCategory).GetProperty(job.Abbreviation.ExtractText())?.GetValue(jobs) is false)
            throw new ToolException($"{name} can't be worn as {job.Name.ExtractText()}: switch to a job that can (switch_gearset).");

        var from = Find(item.RowId) ?? throw new ToolException($"You don't carry {name} (bags or armoury chest).");
        InventoryManager.Instance()->MoveItemSlot(from.Container, (ushort)from.Slot, InventoryType.EquippedItems, (ushort)slot, true);
        return (item.RowId, name, slot);
    }

    // ---------------------------------------------------------------- glamour dresser and armoire

    private static async Task<object> FromDresser(Item item, CancellationToken ct)
    {
        var name = item.Name.ExtractText();
        if (await Game.Run(() => Find(item.RowId) is not null).ConfigureAwait(false)) return new { item = name, already = "in your bags" };
        await OpenStorage("Glamour Dresser", GlamourDressers, "MiragePrismPrismBox", ct).ConfigureAwait(false);
        // Loaded once the dresser window has the player's items.
        if (!await WaitFor(() => { unsafe { return MirageManager.Instance()->PrismBoxLoaded; } }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
            throw new ToolException("The glamour dresser's items did not load.");
        var restored = await Game.Run(() =>
        {
            unsafe
            {
                var mirage = MirageManager.Instance();
                for (var i = 0; i < mirage->PrismBoxItemIds.Length; i++)
                    if (mirage->PrismBoxItemIds[i] is var stored && stored != 0 && stored % 1_000_000 % 500_000 == item.RowId)
                        return mirage->RestorePrismBoxItem((uint)i);
            }
            throw new ToolException($"{name} is not in the glamour dresser.");
        }).ConfigureAwait(false);
        if (!restored) throw new ToolException($"The game didn't take {name} out (the bags may be full, or you already carry a unique copy).");
        var arrived = await WaitFor(() => Find(item.RowId) is not null, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        await Game.Run(() => Close("MiragePrismPrismBox")).ConfigureAwait(false);
        if (!arrived) throw new ToolException($"{name} did not arrive in the bags.");
        return new { item = name, from = "glamour dresser" };
    }

    private static async Task<object> FromArmoire(Item item, CancellationToken ct)
    {
        var name = item.Name.ExtractText();
        if (await Game.Run(() => Find(item.RowId) is not null).ConfigureAwait(false)) return new { item = name, already = "in your bags" };
        var row = CabinetRow(item.RowId) ?? throw new ToolException($"{name} can't be stored in the armoire.");
        await OpenStorage("Armoire", Armoires, "Cabinet", ct).ConfigureAwait(false);
        if (!await WaitFor(() => { unsafe { return UIState.Instance()->Cabinet.IsCabinetLoaded(); } }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
            throw new ToolException("The armoire's items did not load.");
        var withdrawn = await Game.Run(() =>
        {
            unsafe
            {
                var cabinet = &UIState.Instance()->Cabinet;
                if (!cabinet->IsItemInCabinet(row)) throw new ToolException($"{name} is not in the armoire.");
                return cabinet->WithdrawCabinetItem(row);
            }
        }).ConfigureAwait(false);
        if (!withdrawn) throw new ToolException($"The game didn't take {name} out of the armoire (the bags may be full).");
        var arrived = await WaitFor(() => Find(item.RowId) is not null, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        await Game.Run(() => Close("Cabinet")).ConfigureAwait(false);
        if (!arrived) throw new ToolException($"{name} did not arrive in the bags.");
        return new { item = name, from = "armoire" };
    }

    /// <summary>Walks to the dresser or armoire in the inn room and opens it; a menu in between is answered with its first entry.</summary>
    private static async Task OpenStorage(string label, uint[] objectIds, string window, CancellationToken ct)
    {
        if (await Game.Run(() => RetainerUi.Ready(window)).ConfigureAwait(false)) return;
        var steps = new List<string>();
        if (await NavigationTools.WalkTo(label, o => objectIds.Contains(o.BaseId), steps, ct).ConfigureAwait(false) is null)
            throw new ToolException($"No {label.ToLowerInvariant()} nearby: go to your inn room first (navigate_to inn).");
        await Game.Run(() => Interact(o => objectIds.Contains(o.BaseId))).ConfigureAwait(false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var (open, menu) = await Game.Run(() => (RetainerUi.Ready(window), RetainerUi.Ready("SelectString"))).ConfigureAwait(false);
            if (open) return;
            if (menu) await Game.Run(() => { RetainerUi.SelectMenuIndex(0); return true; }).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
        throw new ToolException($"The {label.ToLowerInvariant()} did not open.");
    }

    // ---------------------------------------------------------------- presenting

    private static async Task<object> Present(List<string> steps, bool vipCard, List<uint> discard, int maxValue, bool online, CancellationToken ct)
    {
        var state = await Game.Run(ReportState).ConfigureAwait(false);
        if (state is { Remaining: 0 }) throw new ToolException("No attempts left this week.");
        var rose = TriadData.Locate(MaskedRose, "Masked Rose") ?? throw new ToolException("The Masked Rose was not found in the game data.");
        await NavigationTools.GoToNpc(rose, steps, ct).ConfigureAwait(false);
        string? vip = null;
        if (vipCard) vip = await UseVipCard(ct).ConfigureAwait(false);
        await Game.Run(() => Interact(o => o.BaseId == MaskedRose)).ConfigureAwait(false);
        steps.Add("Talking to the Masked Rose.");

        // Talk → menu (entry 1: present yourself for judging) → confirmations → results (FashionCheck) → closing talk.
        var chose = false;
        var judged = false;
        var talked = false;
        var talkedAt = DateTime.UtcNow;
        var quietSince = DateTime.UtcNow;
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var acted = await Game.Run(() =>
            {
                if (RetainerUi.Ready("FashionCheck"))
                {
                    if (!judged && !chose) return false; // the preview of "confirm this week's challenge"; not ours
                    judged = true;
                    Close("FashionCheck");
                    return true;
                }
                if (RetainerUi.Ready("SelectYesno")) { FireYes(); return true; }
                if (RetainerUi.Ready("SelectString"))
                {
                    var entries = ShopTools.MenuEntries(out _);
                    RetainerUi.SelectMenuIndex(!chose ? 1 : Math.Max(0, (entries?.Count ?? 1) - 1)); // present, or leave afterwards
                    chose = true;
                    return true;
                }
                if (RetainerUi.Ready("Talk")) { RetainerUi.ClickTalk(); return true; }
                return false;
            }).ConfigureAwait(false);
            if (acted) { quietSince = DateTime.UtcNow; talked = true; }
            else if (!talked && DateTime.UtcNow - talkedAt > TimeSpan.FromSeconds(3))
            {
                // The game ignores talking during an animation (e.g. using the VIP card); try again.
                await Game.Run(() => Interact(o => o.BaseId == MaskedRose)).ConfigureAwait(false);
                talkedAt = DateTime.UtcNow;
            }
            // Her closing lines come a while after the results, so only stop once the event is over.
            else if (judged && DateTime.UtcNow - quietSince > TimeSpan.FromSeconds(2) && !await Game.Run(InEvent).ConfigureAwait(false)) break;
            await Task.Delay(400, ct).ConfigureAwait(false);
        }
        if (!judged) throw new ToolException("The judging did not happen: the Masked Rose's dialogue went differently than expected.");
        var score = await Game.Run(() => { unsafe { var m = FashionCheckManager.Instance(); return m == null ? (int?)null : m->EquipEvaluations.Score; } }).ConfigureAwait(false);
        var after = await Game.Run(ReportState).ConfigureAwait(false);
        steps.Add("Judged.");
        List<object>? discarded = null;
        if (discard.Count > 0)
        {
            await PutGearsetBackOn(steps, ct).ConfigureAwait(false);
            discarded = [];
            foreach (var id in discard) discarded.Add(await DiscardBought(id, maxValue, online, steps, ct).ConfigureAwait(false));
        }
        return new { score, highScore = after?.HighScore, attemptsLeft = after?.Remaining, vipCard = vip, discarded, steps };
    }

    // ---------------------------------------------------------------- after presenting

    /// <summary>Puts the active gearset back on, so the pieces worn for the report come off.</summary>
    private static async Task PutGearsetBackOn(List<string> steps, CancellationToken ct)
    {
        var equipped = await Game.Run(() =>
        {
            unsafe
            {
                var module = RaptureGearsetModule.Instance();
                var index = module->CurrentGearsetIndex;
                if (index < 0 || !module->IsValidGearset(index)) return false;
                module->EquipGearset(index);
                return true;
            }
        }).ConfigureAwait(false);
        if (!equipped) { steps.Add("No gearset is active: the outfit stays on."); return; }
        steps.Add("Put the gearset back on.");
        await Task.Delay(1500, ct).ConfigureAwait(false);
    }

    private static readonly InventoryType[] DiscardFrom =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar, InventoryType.ArmoryNeck,
        InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
    ];

    /// <summary>
    /// Throws one bought piece away when it is worth at most <paramref name="maxValue"/> gil (vendor price, otherwise the lowest market
    /// listing on the home world). Never throws: a piece that stays is reported with the reason.
    /// </summary>
    private static async Task<object> DiscardBought(uint itemId, int maxValue, bool online, List<string> steps, CancellationToken ct)
    {
        var (name, vendor) = await Game.Run(() =>
            (Items.Name(itemId), SoldByVendor(itemId) ? (int?)(int)(Svc.Data.GetExcelSheet<Item>().GetRowOrDefault(itemId)?.PriceMid ?? 0) : null)).ConfigureAwait(false);
        int? market = null;
        if (vendor is null && online)
        {
            try
            {
                var world = await Game.Run(() => Universalis.HomeScopes().World).ConfigureAwait(false);
                var data = await Universalis.Get([itemId], world, 1, 0, ct).ConfigureAwait(false);
                market = data.TryGetValue(itemId, out var m) && m.Listings.Count > 0 ? (int)Math.Min(int.MaxValue, m.Listings.Min(l => l.PricePerUnit)) : null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* unknown value: kept */ }
        }
        var value = vendor ?? market;
        if (!FashionCleanup.ShouldDiscard(vendor is not null ? ItemSource.Vendor : ItemSource.Market, vendor, market, maxValue))
            return new { item = name, kept = value is null ? "its value is unknown" : $"worth {value:N0} gil, more than {maxValue:N0}" };

        var found = await Game.Run(() =>
        {
            unsafe
            {
                var im = InventoryManager.Instance();
                var worn = im->GetInventoryContainer(InventoryType.EquippedItems);
                for (var i = 0; i < worn->Size; i++)
                    if (worn->GetInventorySlot(i)->ItemId == itemId) return "worn";
                foreach (var type in DiscardFrom)
                {
                    var container = im->GetInventoryContainer(type);
                    if (container == null) continue;
                    for (var i = 0; i < container->Size; i++)
                    {
                        var slot = container->GetInventorySlot(i);
                        if (slot->ItemId != itemId) continue;
                        var addon = FFXIVClientStructs.FFXIV.Client.UI.RaptureAtkUnitManager.Instance()->GetAddonByName("Inventory");
                        FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance()->DiscardItem(slot, type, i, addon != null ? addon->Id : 0u);
                        return "asked";
                    }
                }
                return "missing";
            }
        }).ConfigureAwait(false);
        if (found == "worn") return new { item = name, kept = "still worn (no gearset to put back on)" };
        if (found == "missing") return new { item = name, kept = "not in the bags or armoury chest" };

        // The game asks "Discard ...?"; confirm, then check it is gone.
        var before = await Game.Run(() => Count(itemId)).ConfigureAwait(false);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (await Game.Run(() => RetainerUi.Ready("SelectYesno")).ConfigureAwait(false)) { await Game.Run(() => { FireYes(); return true; }).ConfigureAwait(false); break; }
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
        var gone = await WaitFor(() => Count(itemId) < before, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        if (!gone) return new { item = name, kept = "the game did not discard it" };
        steps.Add($"Discarded {name} (worth {value:N0} gil).");
        return new { item = name, discarded = true, value };
    }

    private static unsafe int Count(uint itemId)
    {
        var im = InventoryManager.Instance();
        return DiscardFrom.Sum(t => im->GetItemCountInContainer(itemId, t));
    }

    /// <summary>
    /// Uses a Gold Saucer VIP Card unless its bonus is already active; returns what happened (for the result). Never throws: a missing
    /// card only means no bonus.
    /// </summary>
    private static async Task<string> UseVipCard(CancellationToken ct)
    {
        var state = await Game.Run(() =>
        {
            unsafe
            {
                if (Svc.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == VipCardStatus) == true) return "already active";
                if (InventoryManager.Instance()->GetInventoryItemCount(VipCardItem) == 0) return "none in your bags";
                FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInventoryContext.Instance()->UseItem(VipCardItem, InventoryType.Invalid, 0, 0);
                return "used";
            }
        }).ConfigureAwait(false);
        if (state != "used") return state;
        var active = await WaitFor(() => Svc.Objects.LocalPlayer?.StatusList.Any(s => s.StatusId == VipCardStatus) == true, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        return active ? "used" : "used, but its bonus did not show up";
    }

    // ---------------------------------------------------------------- helpers (framework thread)

    private static bool InEvent() =>
        Svc.Condition[ConditionFlag.OccupiedInEvent] || Svc.Condition[ConditionFlag.OccupiedInQuestEvent] || Svc.Condition[ConditionFlag.OccupiedInCutSceneEvent];

    private static unsafe bool Interact(Func<IGameObject, bool> match)
    {
        var obj = Svc.Objects.Where(match).OrderBy(o => Game.DistanceToPlayer(o.Position) ?? float.MaxValue).FirstOrDefault()
                  ?? throw new ToolException("It is not nearby any more.");
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
        TargetSystem.Instance()->SetHardTarget(native, false, false, 0);
        TargetSystem.Instance()->InteractWithObject(native, true);
        return true;
    }

    private static unsafe void FireYes()
    {
        var ptr = RetainerUi.Ptr("SelectYesno");
        if (!ptr.IsNull) RetainerUi.Fire((AtkUnitBase*)ptr.Address, true, 0);
    }

    private static unsafe bool Close(string window)
    {
        var ptr = RetainerUi.Ptr(window);
        if (!ptr.IsNull && ptr.IsVisible) RetainerUi.Fire((AtkUnitBase*)ptr.Address, true, -1);
        return true;
    }

    private static async Task<bool> WaitFor(Func<bool> condition, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await Game.Run(condition).ConfigureAwait(false)) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }
}
