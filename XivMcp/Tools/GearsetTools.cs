using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using XivMcp.Gear;
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

        yield return new McpTool
        {
            Name = "update_gearset",
            Description = "Changes a saved gearset, like the Gear Set list's menu: action=save stores the gear you are wearing in it (you must be " +
                          "its job; 'from' puts on another set first, so {gearset: 32, from: 39} copies set 39's gear into set 32), delete " +
                          "removes it (not the one you wear), rename gives it 'name'. Use it when a set lists missing items or is weaker than " +
                          "another of the same job: tools that pick a job's gearset (crafting, for example) take the first one. Not in combat, " +
                          "while crafting or gathering. Requires 'UI editing' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "gearset": { "type": "string", "description": "The set to change: its number or name." },
                    "action": { "type": "string", "enum": ["save", "delete", "rename"] },
                    "from": { "type": "string", "description": "For save: a set (number or name) to put on first." },
                    "name": { "type": "string", "description": "For rename: the new name." }
                  },
                  "required": ["gearset", "action"]
                }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = (args, ct) => Update(args, ct),
        };
    }

    private static async Task<object?> Update(ToolArgs args, CancellationToken ct)
    {
        var query = args.String("gearset") ?? throw new ToolException("Give 'gearset'.");
        var action = args.String("action") switch
        {
            "save" => GearsetEdit.Action.Save,
            "delete" => GearsetEdit.Action.Delete,
            "rename" => GearsetEdit.Action.Rename,
            var other => throw new ToolException($"Unknown action '{other}': save, delete or rename."),
        };
        var name = args.String("name");
        var steps = new List<string>();

        // Resolve the target before 'from' changes which set is worn.
        var number = (await Game.RunLoggedIn(() => Gearsets.Resolve(query)).ConfigureAwait(false)).Number;
        if (action == GearsetEdit.Action.Save && args.String("from") is { } from)
        {
            var worn = await Gearsets.Switch(from, ct).ConfigureAwait(false);
            steps.Add($"Put on gearset {worn.Number} ({worn.Name}).");
        }

        var before = await Game.RunLoggedIn(() =>
        {
            Gearsets.EnsureCanChangeGear();
            var set = Gearsets.All().First(s => s.Number == number);
            var job = Gearsets.CurrentJob();
            var abbreviation = Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(job)?.Abbreviation.ExtractText() ?? "?";
            if (GearsetEdit.Problem(action, new GearsetEdit.Target(set.Number, set.Name, set.JobId, set.Job, set.Current), job, abbreviation, name) is { } problem)
                throw new ToolException(problem);
            Apply(action, number - 1, name);
            return set;
        }).ConfigureAwait(false);
        steps.Add(action switch
        {
            GearsetEdit.Action.Save => $"Saved the worn gear in gearset {number} ({before.Name}).",
            GearsetEdit.Action.Delete => $"Deleted gearset {number} ({before.Name}).",
            _ => $"Renamed gearset {number} from {before.Name} to {name}.",
        });

        if (action == GearsetEdit.Action.Delete) return new { deleted = number, name = before.Name, job = before.Job, steps };
        await Task.Delay(300, ct).ConfigureAwait(false);
        var after = await Game.RunLoggedIn(() => Gearsets.All().First(s => s.Number == number)).ConfigureAwait(false);
        return new
        {
            gearset = after.Number, name = after.Name, job = after.Job,
            itemLevel = new { before = before.ItemLevel, after = after.ItemLevel },
            missingItems = after.MissingItems > 0 ? after.MissingItems : (int?)null,
            steps,
        };
    }

    /// <summary>Asks the game's gearset module to save, delete or rename (index 0-based). Framework thread.</summary>
    private static unsafe void Apply(GearsetEdit.Action action, int index, string? name)
    {
        var module = RaptureGearsetModule.Instance();
        switch (action)
        {
            case GearsetEdit.Action.Save:
                if (module->UpdateGearset(index) < 0) throw new ToolException($"The game refused to save gearset {index + 1}.");
                break;
            case GearsetEdit.Action.Delete:
                module->DeleteGearset(index);
                break;
            case GearsetEdit.Action.Rename:
                var text = Utf8String.FromString(name!);
                try
                {
                    if (!module->RenameGearset(index, text)) throw new ToolException($"The game refused to rename gearset {index + 1}.");
                }
                finally { text->Dtor(true); }
                break;
        }
    }
}
