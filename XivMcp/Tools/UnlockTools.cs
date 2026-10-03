using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

internal static class UnlockTools
{
    private sealed record Category(string Name, Type RowType, MethodInfo Check);

    /// <summary>Rows that are real, player-facing entries of a category.</summary>
    private static bool Include(Category c, object row) => row switch
    {
        Emote e => e.TextCommand.RowId != 0, // internal emotes have no /command
        _ => true,
    };

    /// <summary>
    /// The game's unlock check, plus entries everyone has by default: emotes without an unlock (Bow, Cheer, ...) are reported as
    /// "not unlocked" by the game's check although every character can use them.
    /// </summary>
    private static bool IsUnlocked(Category c, object row) =>
        row is Emote { UnlockLink: 0 } || (bool)c.Check.Invoke(Svc.Unlocks, [row])!;

    /// <summary>
    /// Every IUnlockState.IsXxxUnlocked/IsXxxComplete(d)(Row) method becomes a category, so new Dalamud unlock checks show up automatically.
    /// </summary>
    private static readonly Lazy<Dictionary<string, Category>> Categories = new(() =>
        typeof(IUnlockState).GetMethods()
            .Where(m => m.ReturnType == typeof(bool) && !m.IsGenericMethod && m.Name.StartsWith("Is", StringComparison.Ordinal))
            .Where(m => m.Name != nameof(IUnlockState.IsItemUnlockable))
            .Select(m => (Method: m, Params: m.GetParameters()))
            .Where(x => x.Params.Length == 1 && x.Params[0].ParameterType.Namespace == "Lumina.Excel.Sheets")
            .Select(x => new Category(Excel.SnakeCase(x.Params[0].ParameterType.Name), x.Params[0].ParameterType, x.Method))
            .GroupBy(c => c.Name)
            .ToDictionary(g => g.Key, g => g.First()));

    internal sealed record UnlockRow(uint Id, string? Name, bool Unlocked);

    /// <summary>Unlock state of every named row of a category (e.g. "mount"), optionally filtered by name.</summary>
    internal static async Task<List<UnlockRow>> Evaluate(string categoryName, string? query = null)
    {
        if (!Categories.Value.TryGetValue(categoryName, out var category)) throw new ToolException($"Unknown category '{categoryName}'.");
        var rows = new List<(object Row, uint Id, string? Name)>();
        foreach (var row in Excel.GetSheet(category.RowType))
        {
            var name = Excel.DisplayName(row);
            if (string.IsNullOrWhiteSpace(name) || (query is not null && !Game.Matches(name, query)) || !Include(category, row)) continue;
            rows.Add((row, Excel.RowId(row), name));
        }
        return await Game.RunLoggedIn(() =>
        {
            var fallback = SnapshotFallback(category);
            return rows.Select(r =>
            {
                bool unlocked;
                try { unlocked = fallback is { } f ? f.Ids.Contains(r.Id) : IsUnlocked(category, r.Row); }
                catch { unlocked = false; }
                return new UnlockRow(r.Id, r.Name, unlocked);
            }).ToList();
        }).ConfigureAwait(false);
    }

    /// <summary>For achievements/titles while the game hasn't loaded them: the last snapshot (call on the framework thread).</summary>
    private static (HashSet<uint> Ids, DateTime Captured, string Hint)? SnapshotFallback(Category c)
    {
        var loaded = c.Name switch
        {
            "achievement" => Svc.Unlocks.IsAchievementListLoaded,
            "title" => Svc.Unlocks.IsTitleListLoaded,
            _ => true,
        };
        return loaded ? null : ProgressTracker.Instance?.Cached(c.Name);
    }

    public static IEnumerable<McpTool> Create()
    {
        yield return new McpTool
        {
            Name = "list_unlock_categories",
            Description = "Lists every unlock/completion category that can be checked with check_unlocks (achievement, quest, mount, companion (minion), " +
                          "emote, orchestrion, triple_triad_card, title, recipe, leve, aether_current, ornament (fashion accessory), glasses, " +
                          "instance_content, action, trait, item, ...).",
            Handler = (_, _) => System.Threading.Tasks.Task.FromResult<object?>(
                Categories.Value.Values.OrderBy(c => c.Name).Select(c => new { category = c.Name, sheet = c.RowType.Name }).ToList()),
        };

        yield return new McpTool
        {
            Name = "check_unlocks",
            Description = "Checks unlock / completion state of the logged-in character for one category (see list_unlock_categories). " +
                          "Without ids or query it returns a summary (unlocked/total). With 'ids' or a name 'query' it returns matching rows and their state. " +
                          "Use filter='locked' to find what is still missing, filter='unlocked' to list what the player has. " +
                          "For 'achievement' and 'title' the in-game Achievements / Titles window must have been opened once this session.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "category": { "type": "string", "description": "e.g. achievement, quest, mount, companion, emote, orchestrion, triple_triad_card, title, recipe, ornament" },
                    "ids": { "type": "array", "items": { "type": "integer" }, "description": "Specific row ids to check." },
                    "query": { "type": "string", "description": "Case-insensitive part of the row name." },
                    "filter": { "type": "string", "enum": ["all", "unlocked", "locked"], "description": "Which rows to list (default all)." },
                    "list": { "type": "boolean", "description": "List rows even without ids/query (respecting filter, limit and offset). Default false = summary only." },
                    "limit": { "type": "integer", "description": "Max rows to return (default 100, max 1000)." },
                    "offset": { "type": "integer", "description": "Rows to skip (for paging)." }
                  },
                  "required": ["category"]
                }
                """,
            Handler = async (args, _) =>
            {
                var categoryName = args.String("category") ?? throw new ToolException("'category' is required.");
                if (!Categories.Value.TryGetValue(Excel.SnakeCase(categoryName.Replace(" ", "")).Replace("__", "_"), out var category) &&
                    !Categories.Value.TryGetValue(categoryName.ToLowerInvariant(), out category))
                    throw new ToolException($"Unknown category '{categoryName}'. Valid: {string.Join(", ", Categories.Value.Keys.OrderBy(k => k))}");

                var ids = args.UIntList("ids").ToHashSet();
                var query = args.String("query");
                var filter = args.String("filter")?.ToLowerInvariant() ?? "all";
                var listRows = args.Bool("list", false) || ids.Count > 0 || query is not null;
                var limit = args.Int("limit", 100, 1, 1000);
                var offset = args.Int("offset", 0, 0);

                // Collect candidate rows off the framework thread (pure sheet data).
                var rows = new List<(object Row, uint Id, string? Name)>();
                foreach (var row in Excel.GetSheet(category.RowType))
                {
                    var id = Excel.RowId(row);
                    if (ids.Count > 0 && !ids.Contains(id)) continue;
                    var name = Excel.DisplayName(row);
                    if (ids.Count == 0 && (string.IsNullOrWhiteSpace(name) || !Include(category, row))) continue; // skip unused/placeholder rows
                    if (query is not null && !Game.Matches(name, query)) continue;
                    rows.Add((row, id, name));
                }

                // Unlock checks read game memory: do them on the framework thread in one go.
                var states = await Game.RunLoggedIn(() =>
                {
                    var notes = new List<string>();
                    // Achievements / titles need the game's list to be loaded; if it isn't, use the last snapshot (with its age).
                    var fallback = SnapshotFallback(category);
                    if (fallback is { } fb)
                        notes.Add($"The game has not loaded this list right now; showing the snapshot from {CacheFreshness.FormatAge(DateTime.UtcNow - fb.Captured)} ago. To refresh: {fb.Hint}");
                    else if (category.Name == "achievement" && !Svc.Unlocks.IsAchievementListLoaded)
                        notes.Add("Achievement data is not loaded yet: open the in-game Achievements window once (or open_window \"Achievements\"), then retry.");
                    else if (category.Name == "title" && !Svc.Unlocks.IsTitleListLoaded)
                        notes.Add("Title data is not loaded yet: open the in-game title list (Character > Titles) once, then retry.");
                    if (category.Name == "xbm_pet" && !Svc.Unlocks.IsXBMPetListLoaded)
                        notes.Add("This list is not loaded yet; open the matching in-game window once.");

                    var unlocked = new bool[rows.Count];
                    for (var i = 0; i < rows.Count; i++)
                    {
                        try { unlocked[i] = fallback is { } f ? f.Ids.Contains(rows[i].Id) : IsUnlocked(category, rows[i].Row); }
                        catch { unlocked[i] = false; }
                    }
                    return (unlocked, notes);
                }).ConfigureAwait(false);

                var unlockedCount = states.unlocked.Count(u => u);
                object? listed = null;
                if (listRows)
                {
                    listed = rows.Select((r, i) => (r, unlocked: states.unlocked[i]))
                        .Where(x => filter switch { "unlocked" => x.unlocked, "locked" => !x.unlocked, _ => true })
                        .Skip(offset).Take(limit)
                        .Select(x => new { id = x.r.Id, name = x.r.Name, unlocked = x.unlocked })
                        .ToList();
                }

                return new
                {
                    category = category.Name,
                    sheet = category.RowType.Name,
                    total = rows.Count,
                    unlocked = unlockedCount,
                    locked = rows.Count - unlockedCount,
                    notes = states.notes.Count > 0 ? states.notes : null,
                    rows = listed,
                    hint = listRows ? null : "Pass list=true (with filter/limit/offset), ids or query to see individual rows.",
                };
            },
        };

        yield return new McpTool
        {
            Name = "get_active_quests",
            Description = "The quests currently in the logged-in character's journal (accepted, not yet completed) with their current step, " +
                          "plus accepted daily (beast tribe) quests, active levequests, leve allowances and beast tribe reputation.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
            {
                unsafe
                {
                    var qm = QuestManager.Instance();
                    var questSheet = Svc.Data.GetExcelSheet<Quest>();

                    var normal = new List<object>();
                    foreach (ref var q in qm->NormalQuests)
                    {
                        if (q.QuestId == 0) continue;
                        var row = questSheet.GetRowOrDefault(q.QuestId + 65536u);
                        normal.Add(new
                        {
                            questId = q.QuestId + 65536u,
                            name = Game.Clean(row?.Name.ExtractText()),
                            sequence = q.Sequence,
                            readyToTurnIn = q.Sequence == 255,
                            journalGenre = row is { } r ? Excel.Name(r.JournalGenre) : null,
                            classJobLevel = row?.ClassJobLevel[0],
                            hidden = q.IsHidden,
                            priority = q.IsPriority,
                        });
                    }

                    var dailies = new List<object>();
                    foreach (ref var d in qm->DailyQuests)
                    {
                        if (d.QuestId == 0) continue;
                        dailies.Add(new { questId = d.QuestId + 65536u, name = questSheet.GetRowOrDefault(d.QuestId + 65536u)?.Name.ExtractText(), completed = d.IsCompleted });
                    }

                    var leves = new List<object>();
                    foreach (ref var l in qm->LeveQuests)
                    {
                        if (l.LeveId == 0) continue;
                        leves.Add(new { leveId = l.LeveId, name = Svc.Data.GetExcelSheet<Leve>().GetRowOrDefault(l.LeveId)?.Name.ExtractText(), sequence = l.Sequence });
                    }

                    var tribes = new List<object>();
                    var tribeSheet = Svc.Data.GetExcelSheet<BeastTribe>();
                    var rankSheet = Svc.Data.GetExcelSheet<BeastReputationRank>();
                    var index = 0;
                    foreach (ref var b in qm->BeastReputation)
                    {
                        index++;
                        if (b.Rank == 0 && b.Value == 0) continue;
                        tribes.Add(new
                        {
                            tribe = tribeSheet.GetRowOrDefault((uint)index)?.Name.ExtractText(),
                            rank = rankSheet.GetRowOrDefault((uint)(b.Rank & 0x7F))?.Name.ExtractText(),
                            reputation = b.Value,
                            rankMax = rankSheet.GetRowOrDefault((uint)(b.Rank & 0x7F))?.RequiredReputation,
                        });
                    }

                    return new
                    {
                        acceptedQuests = normal,
                        dailyQuests = dailies,
                        dailyAllowancesRemaining = qm->GetBeastTribeAllowance(),
                        levequests = leves,
                        leveAllowances = qm->NumLeveAllowances,
                        nextLeveAllowances = QuestManager.GetNextLeveAllowancesDateTime(),
                        beastTribes = tribes,
                    };
                }
            }),
        };

        yield return new McpTool
        {
            Name = "check_quests",
            Description = "Checks completion of quests by name or id for the logged-in character, including whether each is currently accepted " +
                          "and which step it is on. Useful for main scenario / unlock progress questions (e.g. \"have I unlocked Eureka?\").",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Case-insensitive part of the quest name." },
                    "ids": { "type": "array", "items": { "type": "integer" }, "description": "Quest row ids (65536+)." },
                    "limit": { "type": "integer", "description": "Max quests (default 50, max 500)." }
                  }
                }
                """,
            Handler = (args, _) =>
            {
                var query = args.String("query");
                var ids = args.UIntList("ids").ToHashSet();
                if (query is null && ids.Count == 0) throw new ToolException("Provide 'query' or 'ids'.");
                var limit = args.Int("limit", 50, 1, 500);
                var matches = Svc.Data.GetExcelSheet<Quest>()
                    .Where(q => ids.Count > 0 ? ids.Contains(q.RowId) : Game.Matches(q.Name.ExtractText(), query))
                    .Where(q => !q.Name.IsEmpty)
                    .Take(limit)
                    .ToList();

                return Game.RunLoggedIn<object?>(() =>
                {
                    unsafe
                    {
                        var qm = QuestManager.Instance();
                        return matches.Select(q =>
                        {
                            var id = (ushort)(q.RowId - 65536);
                            var accepted = qm->IsQuestAccepted(id);
                            return new
                            {
                                questId = q.RowId,
                                name = Game.Clean(q.Name.ExtractText()),
                                journalGenre = Excel.Name(q.JournalGenre),
                                level = q.ClassJobLevel[0],
                                place = Excel.Name(q.PlaceName),
                                completed = Svc.Unlocks.IsQuestCompleted(q),
                                accepted,
                                currentSequence = accepted ? QuestManager.GetQuestSequence(id) : (byte?)null,
                            };
                        }).ToList();
                    }
                });
            },
        };
    }
}
