using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;
using Action = Lumina.Excel.Sheets.Action;

namespace XivMcp.Tools;

/// <summary>Job actions (with live cooldowns), hotbars and macros.</summary>
internal static class ActionMacroTools
{
    private const int GcdCooldownGroup = 58;
    private const int MacroCount = 100;
    private const int MacroLines = 15;
    private const int MaxLineLength = 180;
    private const int MaxNameLength = 20;
    private const uint DefaultMacroIcon = 66001;

    private static readonly string[] Sets = ["individual", "shared"];

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        void RequireEnabled()
        {
            if (!config.AllowUiEditing)
                throw new ToolException("Macro editing is disabled. Enable \"UI editing\" in the XIV MCP settings window (/xivmcp) in game.");
        }

        // ------------------------------------------------------------------ actions

        yield return new McpTool
        {
            Name = "get_job_actions",
            Description = "The actions of a job (default: the current one), sorted by level: name, level, whether it is unlocked, type " +
                          "(spell / weaponskill / ability), GCD or oGCD, cast and recast time, charges, range, description — and for the current job the " +
                          "live cooldown remaining and charges available. Includes the base class's actions (e.g. GLA for PLD); role actions optional.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "job": { "type": "string", "description": "Job or class name or abbreviation (e.g. PLD, Paladin). Default: current job." },
                    "include_role_actions": { "type": "boolean", "description": "Also list role actions (default true)." },
                    "include_descriptions": { "type": "boolean", "description": "Include the tooltip text (default false, it is long)." },
                    "only_unlocked": { "type": "boolean", "description": "Hide actions the character has not learned yet (default false)." }
                  }
                }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                var job = ResolveJob(args.String("job"));
                var isCurrent = job.RowId == Svc.PlayerState.ClassJob.RowId;
                var includeRole = args.Bool("include_role_actions", true);
                var includeDescriptions = args.Bool("include_descriptions", false);
                var onlyUnlocked = args.Bool("only_unlocked", false);
                var level = Svc.PlayerState.GetClassJobLevel(job);
                var transient = Svc.Data.GetExcelSheet<ActionTransient>();
                var abbreviations = new[] { job.Abbreviation.ExtractText(), job.ClassJobParent.ValueNullable?.Abbreviation.ExtractText() }
                    .Where(a => !string.IsNullOrEmpty(a)).OfType<string>().Distinct().ToArray();

                var actions = Svc.Data.GetExcelSheet<Action>()
                    .Where(a => a.IsPlayerAction && !a.IsPvP && a.ClassJobLevel > 0 && !a.Name.IsEmpty)
                    .Where(a => (includeRole || !a.IsRoleAction) && CategoryIncludes(a.ClassJobCategory.ValueNullable, abbreviations))
                    .OrderBy(a => a.ClassJobLevel).ThenBy(a => a.RowId)
                    .ToList();

                var result = new List<object>();
                foreach (var a in actions)
                {
                    // Actions without an unlock quest (UnlockLink 0) are learned by level alone; the game's unlock check only covers quest unlocks.
                    var questGated = a.UnlockLink.RowId != 0;
                    var unlocked = level >= a.ClassJobLevel && (!questGated || Svc.Unlocks.IsActionUnlocked(a));
                    if (onlyUnlocked && !unlocked) continue;
                    var maxCharges = a.MaxCharges;
                    var entry = new Dictionary<string, object?>
                    {
                        ["id"] = a.RowId,
                        ["name"] = a.Name.ExtractText(),
                        ["level"] = a.ClassJobLevel,
                        ["unlocked"] = unlocked,
                        ["unlockedBy"] = questGated ? UnlockSource(a) : "level",
                        ["type"] = Excel.Name(a.ActionCategory),
                        ["gcd"] = a.CooldownGroup == GcdCooldownGroup,
                        ["roleAction"] = a.IsRoleAction ? true : null,
                        ["castSeconds"] = a.Cast100ms / 10.0,
                        ["recastSeconds"] = a.Recast100ms / 10.0,
                        ["maxCharges"] = maxCharges > 1 ? maxCharges : null,
                        ["range"] = a.Range < 0 ? "melee" : a.Range == 0 ? "self" : $"{a.Range}y",
                        ["radius"] = a.EffectRange > 0 ? a.EffectRange : null,
                    };
                    if (includeDescriptions && transient.GetRowOrDefault(a.RowId) is { } t) entry["description"] = t.Description.ExtractText();
                    if (isCurrent && unlocked) AddLiveState(entry, a, (uint)Math.Max((int)level, 1));
                    result.Add(entry);
                }

                return new
                {
                    job = Excel.Ref<ClassJob>(job.RowId),
                    level,
                    isCurrentJob = isCurrent,
                    note = isCurrent ? null : "Cooldowns are only reported for the job you are currently on.",
                    actions = result,
                };
            }),
        };

        // ------------------------------------------------------------------ hotbars

        yield return new McpTool
        {
            Name = "get_hotbars",
            Description = "What is on the character's hotbars (1-10) and cross hotbars (1-8): per slot the type (action, macro, item, emote, mount, " +
                          "gear set, ...) and its name. Empty slots are omitted; empty bars are skipped.",
            InputSchema = """
                { "type": "object", "properties": { "include_cross_hotbars": { "type": "boolean", "description": "Include cross hotbars (default false)." } } }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                unsafe
                {
                    var module = RaptureHotbarModule.Instance();
                    var bars = new List<object>();
                    AddBars(module->StandardHotbars, "hotbar", bars);
                    if (args.Bool("include_cross_hotbars", false)) AddBars(module->CrossHotbars, "crossHotbar", bars);
                    return new { hotbars = bars };
                }
            }),
        };

        // ------------------------------------------------------------------ macros

        yield return new McpTool
        {
            Name = "get_macros",
            Description = "The character's macros: individual (this character) and/or shared (all characters), 100 each. Per macro its number, " +
                          "name, icon and lines. Empty macros are omitted unless include_empty=true.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "set": { "type": "string", "enum": ["individual", "shared", "both"], "description": "Which macro set (default both)." },
                    "query": { "type": "string", "description": "Only macros whose name or lines contain this text." },
                    "include_empty": { "type": "boolean", "description": "Also list empty slots (default false)." }
                  }
                }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                var set = args.String("set")?.ToLowerInvariant() ?? "both";
                var query = args.String("query");
                var includeEmpty = args.Bool("include_empty", false);
                var result = new Dictionary<string, object>();
                for (uint s = 0; s < 2; s++)
                {
                    if (set != "both" && set != Sets[s]) continue;
                    var list = new List<object>();
                    for (uint i = 0; i < MacroCount; i++)
                    {
                        var m = ReadMacro(s, i);
                        if (!includeEmpty && m.Empty) continue;
                        if (query is not null && !Game.Matches(m.Name, query) && !m.Lines.Any(l => Game.Matches(l, query))) continue;
                        list.Add(new { number = i, name = m.Name, iconId = m.IconId, lines = m.Lines, empty = m.Empty ? true : (bool?)null });
                    }
                    result[Sets[s]] = list;
                }
                return result;
            }),
        };

        yield return new McpTool
        {
            Name = "set_macro",
            Description = $"Creates or edits a macro (like the in-game macro editor): name (max {MaxNameLength} characters), icon and up to {MacroLines} lines " +
                          $"(max {MaxLineLength} characters each). Omitted fields keep their current value; giving 'lines' replaces all lines. " +
                          "Returns the previous content, which is also backed up. Use dry_run to preview. Refuses while the in-game macro window is open " +
                          "(it would overwrite the change). Requires 'UI editing' in /xivmcp.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    "set": { "type": "string", "enum": ["individual", "shared"], "description": "Macro set (default individual)." },
                    "number": { "type": "integer", "description": "Macro number 0-99." },
                    "name": { "type": "string", "description": "Macro name (max {{MaxNameLength}} characters)." },
                    "lines": { "type": "array", "items": { "type": "string" }, "description": "Macro lines, e.g. [\"/ac Sprint\", \"/wait 1\"] (max {{MacroLines}})." },
                    "icon_id": { "type": "integer", "description": "Icon id (default for new macros: {{DefaultMacroIcon}})." },
                    "dry_run": { "type": "boolean", "description": "Validate and show before/after without changing anything (default false)." }
                  },
                  "required": ["number"]
                }
                """,
            ReadOnly = false,
            Handler = (args, _) =>
            {
                RequireEnabled();
                var set = ParseSet(args.String("set"));
                var number = ParseNumber(args);
                var name = args.String("name");
                var lines = args.Node("lines") is null ? null : args.StringListRaw("lines");
                var iconId = args.UInt("icon_id");
                var dryRun = args.Bool("dry_run", false);

                if (name is not null && name.Length > MaxNameLength) throw new ToolException($"Name is longer than {MaxNameLength} characters.");
                if (lines is not null)
                {
                    if (lines.Count > MacroLines) throw new ToolException($"A macro has at most {MacroLines} lines (got {lines.Count}).");
                    var tooLong = lines.Select((l, i) => (l, i)).FirstOrDefault(x => x.l.Length > MaxLineLength);
                    if (tooLong.l is not null) throw new ToolException($"Line {tooLong.i + 1} is longer than {MaxLineLength} characters.");
                    if (lines.Any(l => l.IndexOfAny(['\r', '\n']) >= 0)) throw new ToolException("Lines must not contain line breaks; pass one entry per line.");
                }
                if (name is null && lines is null && iconId is null) throw new ToolException("Nothing to change: give name, lines and/or icon_id.");

                return Game.RunLoggedIn<object?>(() =>
                {
                    EnsureEditorClosed();
                    var before = ReadMacro(set, number);
                    var after = before with
                    {
                        Name = name ?? before.Name,
                        Lines = lines ?? before.Lines,
                        IconId = iconId ?? (before.Empty && before.IconId == 0 ? DefaultMacroIcon : before.IconId),
                    };
                    after = after with { Empty = after.Name.Length == 0 && after.Lines.All(l => l.Length == 0) };
                    if (dryRun) return new { dryRun = true, set = Sets[set], number, before = Describe(before), after = Describe(after) };

                    var backup = Backup(set, number, before);
                    WriteMacro(set, number, after);
                    Svc.Log.Information($"[MCP] Wrote {Sets[set]} macro {number} \"{after.Name}\"");
                    return new { set = Sets[set], number, before = Describe(before), after = Describe(ReadMacro(set, number)), backup };
                });
            },
        };

        yield return new McpTool
        {
            Name = "clear_macro",
            Description = "Empties a macro slot (name, icon and all lines), like deleting it in the macro editor. The previous content is returned and " +
                          "backed up. Refuses while the in-game macro window is open. Requires 'UI editing' in /xivmcp.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "set": { "type": "string", "enum": ["individual", "shared"], "description": "Macro set (default individual)." },
                    "number": { "type": "integer", "description": "Macro number 0-99." }
                  },
                  "required": ["number"]
                }
                """,
            ReadOnly = false,
            Destructive = true,
            Handler = (args, _) =>
            {
                RequireEnabled();
                var set = ParseSet(args.String("set"));
                var number = ParseNumber(args);
                return Game.RunLoggedIn<object?>(() =>
                {
                    EnsureEditorClosed();
                    var before = ReadMacro(set, number);
                    if (before.Empty) return new { set = Sets[set], number, action = "already empty" };
                    var backup = Backup(set, number, before);
                    unsafe
                    {
                        var module = RaptureMacroModule.Instance();
                        module->GetMacro(set, number)->Clear();
                        module->SetSavePendingFlag(true, set);
                    }
                    Svc.Log.Information($"[MCP] Cleared {Sets[set]} macro {number}");
                    return new { set = Sets[set], number, action = "cleared", before = Describe(before), backup };
                });
            },
        };
    }

    // ------------------------------------------------------------------ helpers: actions

    private static ClassJob ResolveJob(string? job)
    {
        var sheet = Svc.Data.GetExcelSheet<ClassJob>();
        if (job is null) return sheet.GetRow(Svc.PlayerState.ClassJob.RowId);
        var match = sheet.FirstOrDefault(c => c.RowId != 0 &&
                        (c.Abbreviation.ExtractText().Equals(job, StringComparison.OrdinalIgnoreCase) ||
                         c.Name.ExtractText().Equals(job, StringComparison.OrdinalIgnoreCase)));
        return match.RowId != 0 ? match : throw new ToolException($"Unknown job '{job}'. Use an abbreviation like PLD or a name like Paladin.");
    }

    /// <summary>What unlocks a quest-gated action: the quest's name if the unlock link is a quest, otherwise the raw link id.</summary>
    private static object UnlockSource(Action a)
    {
        var link = a.UnlockLink.RowId;
        if (link >= 65536 && Svc.Data.GetExcelSheet<Quest>().GetRowOrDefault(link) is { } quest && !quest.Name.IsEmpty)
            return new { quest = Game.Clean(quest.Name.ExtractText()), questId = link };
        return new { unlockLink = link };
    }

    /// <summary>ClassJobCategory has one boolean column per class/job abbreviation (GLA, PLD, ...).</summary>
    private static bool CategoryIncludes(ClassJobCategory? category, string[] abbreviations)
    {
        if (category is not { } c) return false;
        foreach (var abbr in abbreviations)
            if (typeof(ClassJobCategory).GetProperty(abbr)?.GetValue(c) is true) return true;
        return false;
    }

    private static unsafe void AddLiveState(Dictionary<string, object?> entry, Action a, uint level)
    {
        var am = ActionManager.Instance();
        var adjusted = am->GetAdjustedActionId(a.RowId);
        if (adjusted != a.RowId) entry["currentlyBecomes"] = Excel.Ref<Action>(adjusted);
        var total = am->GetRecastTime(ActionType.Action, a.RowId);
        var elapsed = am->GetRecastTimeElapsed(ActionType.Action, a.RowId);
        var remaining = total > 0 ? Math.Max(0, total - elapsed) : 0;
        entry["cooldownRemainingSeconds"] = Math.Round(remaining, 1);
        var maxCharges = ActionManager.GetMaxCharges(a.RowId, level);
        if (maxCharges > 1)
        {
            var perCharge = total > 0 ? total / maxCharges : 0;
            entry["chargesAvailable"] = total <= 0 || perCharge <= 0 ? maxCharges : (int)Math.Floor(elapsed / perCharge);
        }
    }

    // ------------------------------------------------------------------ helpers: hotbars

    private static unsafe void AddBars(Span<RaptureHotbarModule.Hotbar> hotbars, string kind, List<object> bars)
    {
        for (var b = 0; b < hotbars.Length; b++)
        {
            var slots = new List<object>();
            var bar = hotbars[b].Slots;
            for (var s = 0; s < bar.Length; s++)
            {
                ref var slot = ref bar[s];
                if (slot.IsEmpty || slot.CommandType == RaptureHotbarModule.HotbarSlotType.Empty) continue;
                slots.Add(new { slot = s + 1, type = slot.CommandType.ToString(), id = slot.CommandId, name = SlotName(slot.CommandType, slot.CommandId) });
            }
            if (slots.Count > 0) bars.Add(new { bar = $"{kind} {b + 1}", slots });
        }
    }

    private static string? SlotName(RaptureHotbarModule.HotbarSlotType type, uint id) => type switch
    {
        RaptureHotbarModule.HotbarSlotType.Action => Excel.NameOf<Action>(id),
        RaptureHotbarModule.HotbarSlotType.Item => InventoryTools.ItemName(id > 1_000_000 ? id - 1_000_000 : id > 500_000 ? id - 500_000 : id),
        RaptureHotbarModule.HotbarSlotType.EventItem or RaptureHotbarModule.HotbarSlotType.KeyItem => InventoryTools.ItemName(id),
        RaptureHotbarModule.HotbarSlotType.Emote => Excel.NameOf<Emote>(id),
        RaptureHotbarModule.HotbarSlotType.GeneralAction => Excel.NameOf<GeneralAction>(id),
        RaptureHotbarModule.HotbarSlotType.CraftAction => Excel.NameOf<CraftAction>(id),
        RaptureHotbarModule.HotbarSlotType.Mount => Excel.NameOf<Mount>(id),
        RaptureHotbarModule.HotbarSlotType.Companion => Excel.NameOf<Companion>(id),
        RaptureHotbarModule.HotbarSlotType.Ornament => Excel.NameOf<Ornament>(id),
        RaptureHotbarModule.HotbarSlotType.MainCommand => Excel.NameOf<MainCommand>(id),
        RaptureHotbarModule.HotbarSlotType.PetAction => Excel.NameOf<PetAction>(id),
        RaptureHotbarModule.HotbarSlotType.BuddyAction => Excel.NameOf<BuddyAction>(id),
        RaptureHotbarModule.HotbarSlotType.Macro => MacroName(id),
        RaptureHotbarModule.HotbarSlotType.GearSet => GearsetName(id),
        RaptureHotbarModule.HotbarSlotType.FieldMarker => Excel.NameOf<FieldMarker>(id),
        RaptureHotbarModule.HotbarSlotType.Marker => Excel.NameOf<Marker>(id),
        RaptureHotbarModule.HotbarSlotType.McGuffin => Svc.Data.GetExcelSheet<McGuffin>().GetRowOrDefault(id)?.UIData.ValueNullable?.Name.ExtractText(),
        RaptureHotbarModule.HotbarSlotType.Glasses => Excel.NameOf<Glasses>(id),
        _ => null,
    };

    /// <summary>Hotbar macro ids: 0-99 individual, 256-355 shared.</summary>
    private static string? MacroName(uint id)
    {
        var set = id >= 256 ? 1u : 0u;
        var index = id % 256;
        if (index >= MacroCount) return null;
        var m = ReadMacro(set, index);
        return $"{Sets[set]} macro {index}: {m.Name}";
    }

    private static unsafe string? GearsetName(uint id)
    {
        var module = RaptureGearsetModule.Instance();
        var entry = module == null ? null : module->GetGearset((int)id);
        return entry == null ? null : $"gear set {id + 1}: {entry->NameString}";
    }

    // ------------------------------------------------------------------ helpers: macros

    private sealed record MacroData(string Name, uint IconId, List<string> Lines, bool Empty);

    private static unsafe MacroData ReadMacro(uint set, uint index)
    {
        var macro = RaptureMacroModule.Instance()->GetMacro(set, index);
        if (macro == null) throw new ToolException("Macro data is not available.");
        var lines = new List<string>();
        foreach (ref var line in macro->Lines) lines.Add(line.ToString());
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var name = macro->Name.ToString();
        return new MacroData(name, macro->IconId, lines, !macro->IsNotEmpty());
    }

    private static unsafe void WriteMacro(uint set, uint index, MacroData data)
    {
        var module = RaptureMacroModule.Instance();
        var macro = module->GetMacro(set, index);
        if (macro == null) throw new ToolException("Macro data is not available.");
        macro->Name.SetString(data.Name);
        var lines = macro->Lines;
        for (var i = 0; i < lines.Length; i++)
            lines[i].SetString(i < data.Lines.Count ? data.Lines[i] : "");
        if (data.IconId != 0 && data.IconId != macro->IconId) macro->SetIcon(data.IconId);
        module->SetSavePendingFlag(true, set);
    }

    private static object Describe(MacroData m) => new { name = m.Name, iconId = m.IconId, lines = m.Lines, empty = m.Empty ? true : (bool?)null };

    private static void EnsureEditorClosed()
    {
        if (Svc.GameGui.GetAddonByName("Macro", 1) is { IsNull: false, IsVisible: true })
            throw new ToolException("The in-game macro window is open; close it first (it would overwrite the change when it saves).");
    }

    private static string Backup(uint set, uint index, MacroData before)
    {
        var owner = set == 0 ? Svc.PlayerState.ContentId.ToString() : "shared";
        var dir = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "backups", "macros", owner);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Sets[set]}-{index:00}.{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { set = Sets[set], number = index, before.Name, before.IconId, before.Lines }, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var old in new DirectoryInfo(dir).GetFiles($"{Sets[set]}-{index:00}.*.json").OrderByDescending(f => f.Name).Skip(10)) old.Delete();
        return path;
    }

    private static uint ParseSet(string? set) => set?.ToLowerInvariant() switch
    {
        null or "individual" => 0,
        "shared" => 1,
        _ => throw new ToolException("set must be 'individual' or 'shared'."),
    };

    private static uint ParseNumber(ToolArgs args)
    {
        var n = args.UInt("number") ?? throw new ToolException("'number' is required (0-99).");
        return n < MacroCount ? n : throw new ToolException("Macro numbers go from 0 to 99.");
    }
}
