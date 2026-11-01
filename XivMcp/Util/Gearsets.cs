using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>The player's gearsets (the game's Gear Set list) and switching between them, like clicking one in the list.</summary>
internal static class Gearsets
{
    public sealed record Set(int Number, string Name, uint JobId, string Job, string JobName, int ItemLevel, bool Current, bool CombatJob, int MissingItems);

    /// <summary>All gearsets; Number is 1-based like in game. Framework thread.</summary>
    public static unsafe List<Set> All()
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null) return [];
        var jobs = Svc.Data.GetExcelSheet<ClassJob>();
        var current = module->CurrentGearsetIndex;
        var sets = new List<Set>();
        for (var i = 0; i < 100; i++)
        {
            if (!module->IsValidGearset(i)) continue;
            var e = module->GetGearset(i);
            var job = jobs.GetRowOrDefault(e->ClassJob);
            var missing = 0;
            foreach (ref var item in e->Items)
                if (item.ItemId != 0 && item.Flags.HasFlag(RaptureGearsetModule.GearsetItemFlag.ItemMissing)) missing++;
            sets.Add(new Set(i + 1, e->NameString, e->ClassJob, job?.Abbreviation.ExtractText() ?? "?", job?.Name.ExtractText() ?? "?", e->ItemLevel,
                i == current, job is { } j && j.DohDolJobIndex < 0, missing));
        }
        return sets;
    }

    public static bool IsCombatJob(uint jobId) => Svc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(jobId) is { } j && j.DohDolJobIndex < 0 && jobId != 0;

    /// <summary>A gearset by number, name or job (abbreviation or name; the highest item level set of that job). Framework thread.</summary>
    public static Set Resolve(string query)
    {
        var sets = All();
        if (sets.Count == 0) throw new ToolException("No gearsets are saved.");
        if (int.TryParse(query, out var n))
            return sets.FirstOrDefault(s => s.Number == n) ?? throw new ToolException($"There is no gearset {n}.");
        var byName = sets.Where(s => s.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count > 0) return byName[0];
        var byJob = sets.Where(s => s.Job.Equals(query, StringComparison.OrdinalIgnoreCase) || s.JobName.Equals(query, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(s => s.MissingItems > 0).ThenByDescending(s => s.ItemLevel).ToList();
        if (byJob.Count > 0) return byJob[0];
        var partial = sets.Where(s => Game.Matches(s.Name, query)).ToList();
        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new ToolException($"No gearset matches '{query}' (by number, name or job). Saved: {string.Join(", ", sets.Select(s => $"{s.Number} {s.Name} ({s.Job})"))}."),
            _ => throw new ToolException($"'{query}' matches several gearsets: {string.Join(", ", partial.Select(s => $"{s.Number} {s.Name}"))}."),
        };
    }

    /// <summary>Addon rows of the game's "… registered to this gear set could not be found … Replace it with …?" prompt.</summary>
    private static readonly uint[] ReplaceMissingPrompts = [4384, 4385];

    /// <summary>
    /// Equips a gearset and waits until the game has switched to its job. When an item of the set is missing, the game asks
    /// whether to replace it with another one it found: <paramref name="replaceMissing"/> answers Yes (the default, like the
    /// player usually does) or No; what was asked lands in <paramref name="steps"/>. Any other prompt stops with its text.
    /// </summary>
    public static async Task<Set> Switch(string query, CancellationToken ct, bool replaceMissing = true, List<string>? steps = null)
    {
        var set = await Game.RunLoggedIn(() =>
        {
            var s = Resolve(query);
            EnsureCanChangeGear();
            return s;
        }).ConfigureAwait(false);
        if (set.Current && await Game.Run(CurrentJob).ConfigureAwait(false) == set.JobId) return set;

        var templates = await Game.Run(() => ReplaceMissingPrompts.Select(GameWindows.TemplatePieces).ToList()).ConfigureAwait(false);
        bool AsksToReplace(string? p) => p is not null && templates.Any(t => Ui.PromptTemplate.Matches(p, t));
        // An earlier attempt may have left the prompt open: answering it finishes that switch instead of starting another.
        if (!AsksToReplace(await Game.Run(GameWindows.YesNoPrompt).ConfigureAwait(false)))
        {
            var result = await Game.Run(() => { unsafe { return RaptureGearsetModule.Instance()->EquipGearset(set.Number - 1); } }).ConfigureAwait(false);
            if (result < 0) throw new ToolException($"The game refused to equip gearset {set.Number} ({set.Name}).");
        }
        var answered = 0;
        for (var i = 0; i < 40; i++)
        {
            if (i > 0) await Task.Delay(250, ct).ConfigureAwait(false);
            if (await Game.Run(CurrentJob).ConfigureAwait(false) == set.JobId) return set with { Current = true };
            var prompt = await Game.Run(GameWindows.YesNoPrompt).ConfigureAwait(false);
            if (prompt is null) continue;
            if (!AsksToReplace(prompt))
                throw new ToolException($"Equipping gearset {set.Number} ({set.Name}) shows a prompt: \"{prompt}\". Answer it with select_menu_option (\"Yes\" or \"No\").");
            if (++answered > 14) throw new ToolException($"The game keeps asking \"{prompt}\" while equipping gearset {set.Number} ({set.Name}).");
            await Game.Run(() => { GameWindows.AnswerYesNo(replaceMissing); return true; }).ConfigureAwait(false);
            steps?.Add($"The game asked: \"{prompt}\" Answered {(replaceMissing ? "Yes" : "No")}.");
            i = 0; // the job changes after the answer (one prompt per missing item at most)
        }
        throw new ToolException($"Equipped gearset {set.Number} ({set.Name}), but the job did not change to {set.Job}.");
    }

    /// <summary>Gear can't change in combat, while crafting, gathering, casting, in a cutscene or a zone change. Framework thread.</summary>
    public static void EnsureCanChangeGear()
    {
        var c = Svc.Condition;
        if (c[ConditionFlag.InCombat]) throw new ToolException("Can't change gear in combat.");
        if (c[ConditionFlag.Crafting] || c[ConditionFlag.Gathering] || c[ConditionFlag.Casting] || c[ConditionFlag.OccupiedInCutSceneEvent]
            || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.Occupied39])
            throw new ToolException("Can't change gear right now (crafting, gathering, casting, in a cutscene or a zone change).");
    }

    public static uint CurrentJob() => Svc.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
}
