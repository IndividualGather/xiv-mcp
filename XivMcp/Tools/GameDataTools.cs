using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Generic read access to the game's static Excel data (items, quests, achievements, duties, ...), in the client's language.</summary>
internal static class GameDataTools
{
    public static IEnumerable<McpTool> Create()
    {
        yield return new McpTool
        {
            Name = "list_game_sheets",
            Description = "Lists the names of the game's Excel data sheets that search_game_data / get_game_data_row can read " +
                          "(e.g. Item, Quest, Achievement, Mount, ContentFinderCondition, Recipe, ClassJob, TerritoryType, Status, Action). Optional name filter.",
            InputSchema = """
                { "type": "object", "properties": { "query": { "type": "string", "description": "Case-insensitive part of the sheet name." } } }
                """,
            Handler = (args, _) =>
            {
                var query = args.String("query");
                return Task.FromResult<object?>(Excel.Sheets.Keys.Where(k => Game.Matches(k, query)).OrderBy(k => k).ToList());
            },
        };

        yield return new McpTool
        {
            Name = "search_game_data",
            Description = "Searches a game data sheet by row name (Name/Singular/Text/...) and returns matching row ids and names. " +
                          "Use get_game_data_row for all columns of a row. Works without being logged in.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "sheet": { "type": "string", "description": "Sheet name, e.g. Item, Quest, Achievement, Mount, Companion, Recipe, ContentFinderCondition." },
                    "query": { "type": "string", "description": "Case-insensitive part of the row name. Omit to page through all named rows." },
                    "exact": { "type": "boolean", "description": "Require an exact (case-insensitive) name match (default false)." },
                    "include_columns": { "type": "boolean", "description": "Return all columns for each match instead of id+name (default false)." },
                    "limit": { "type": "integer", "description": "Max rows (default 25, max 200)." },
                    "offset": { "type": "integer", "description": "Rows to skip (for paging)." }
                  },
                  "required": ["sheet"]
                }
                """,
            Handler = (args, _) => Task.Run<object?>(() =>
            {
                var type = Excel.ResolveSheet(args.String("sheet") ?? throw new ToolException("'sheet' is required."));
                var query = args.String("query");
                var exact = args.Bool("exact", false);
                var withColumns = args.Bool("include_columns", false);
                var limit = args.Int("limit", 25, 1, 200);
                var offset = args.Int("offset", 0, 0);

                var matches = new List<object?>();
                var total = 0;
                foreach (var row in Excel.GetSheet(type))
                {
                    var name = Excel.DisplayName(row);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (query is not null && (exact ? !name.Equals(query, StringComparison.OrdinalIgnoreCase) : !Game.Matches(name, query))) continue;
                    total++;
                    if (total <= offset || matches.Count >= limit) continue;
                    matches.Add(withColumns
                        ? new Dictionary<string, object?> { ["rowId"] = Excel.RowId(row), ["name"] = name, ["columns"] = Excel.ToPlain(row, 0, 1) }
                        : new Dictionary<string, object?> { ["rowId"] = Excel.RowId(row), ["name"] = name });
                }
                return new { sheet = type.Name, totalMatches = total, offset, rows = matches };
            }),
        };

        yield return new McpTool
        {
            Name = "get_game_data_row",
            Description = "Returns all columns of one or more rows of a game data sheet by row id, resolving references to other sheets to { id, sheet, name }. " +
                          "Works without being logged in.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "sheet": { "type": "string", "description": "Sheet name, e.g. Item." },
                    "ids": { "type": "array", "items": { "type": "integer" }, "description": "Row ids (max 20)." },
                    "depth": { "type": "integer", "description": "How deep nested structures are expanded (default 2, max 4)." }
                  },
                  "required": ["sheet", "ids"]
                }
                """,
            Handler = (args, _) => Task.Run<object?>(() =>
            {
                var type = Excel.ResolveSheet(args.String("sheet") ?? throw new ToolException("'sheet' is required."));
                var ids = args.UIntList("ids");
                if (ids.Count == 0) throw new ToolException("'ids' is required.");
                var depth = args.Int("depth", 2, 0, 4);
                return ids.Take(20).Select(id => Excel.GetRow(type, id) is { } row
                    ? (object?)new { rowId = id, name = Excel.DisplayName(row), columns = Excel.ToPlain(row, 0, depth) }
                    : new { rowId = id, error = "Row not found" }).ToList();
            }),
        };
    }
}
