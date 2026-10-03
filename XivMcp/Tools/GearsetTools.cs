using System.Collections.Generic;
using System.Linq;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>The Gear Set list: what is saved, and switching job by equipping a set.</summary>
internal static class GearsetTools
{
    public static IEnumerable<McpTool> Create(Configuration config)
    {
        yield return new McpTool
        {
            Name = "list_gearsets",
            Description = "The saved gearsets (number as in game, name, job, item level, items missing from the inventory) and which one is worn.",
            Handler = async (_, _) =>
            {
                var sets = await Game.RunLoggedIn(Gearsets.All).ConfigureAwait(false);
                return new
                {
                    current = sets.FirstOrDefault(s => s.Current)?.Number,
                    gearsets = sets.Select(s => new
                    {
                        number = s.Number, name = s.Name, job = s.Job, jobName = s.JobName, itemLevel = s.ItemLevel,
                        kind = s.CombatJob ? "combat" : "crafter/gatherer", current = s.Current, missingItems = s.MissingItems > 0 ? s.MissingItems : (int?)null,
                    }).ToList(),
                };
            },
        };

        yield return new McpTool
        {
            Name = "switch_gearset",
            Description = "Changes job by equipping a gearset, like clicking it in the Gear Set list: give its number, name or a job " +
                          "(\"WAR\", \"Warrior\"; the highest item level set of that job). Not in combat, while crafting or gathering. " +
                          "Needs 'Game & navigation' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": { "gearset": { "type": "string", "description": "Gearset number, name or job." } },
                  "required": ["gearset"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (!config.AllowGameNavigation)
                    throw new ToolException("Switching gearsets is disabled. Enable \"Game & navigation\" in the XIV MCP settings window (/xivmcp) in game.");
                var set = await Gearsets.Switch(args.String("gearset") ?? throw new ToolException("Give 'gearset'."), ct).ConfigureAwait(false);
                return new
                {
                    equipped = set.Number, name = set.Name, job = set.Job, itemLevel = set.ItemLevel,
                    warning = set.MissingItems > 0 ? $"{set.MissingItems} item(s) of this set are not in your inventory." : null,
                };
            },
        };
    }
}
