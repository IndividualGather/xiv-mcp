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

    /// <summary>Equips a gearset and waits until the game has switched to its job.</summary>
    public static async Task<Set> Switch(string query, CancellationToken ct)
    {
        var set = await Game.RunLoggedIn(() =>
        {
            var s = Resolve(query);
            var c = Svc.Condition;
            if (c[ConditionFlag.InCombat]) throw new ToolException("Can't change gear in combat.");
            if (c[ConditionFlag.Crafting] || c[ConditionFlag.Gathering] || c[ConditionFlag.Casting] || c[ConditionFlag.OccupiedInCutSceneEvent]
                || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.Occupied39])
                throw new ToolException("Can't change gear right now (crafting, gathering, casting, in a cutscene or a zone change).");
            return s;
        }).ConfigureAwait(false);
        if (set.Current && await Game.Run(CurrentJob).ConfigureAwait(false) == set.JobId) return set;

        var result = await Game.Run(() => { unsafe { return RaptureGearsetModule.Instance()->EquipGearset(set.Number - 1); } }).ConfigureAwait(false);
        if (result < 0) throw new ToolException($"The game refused to equip gearset {set.Number} ({set.Name}).");
        for (var i = 0; i < 40; i++)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            if (await Game.Run(CurrentJob).ConfigureAwait(false) == set.JobId) return set with { Current = true };
        }
        throw new ToolException($"Equipped gearset {set.Number} ({set.Name}), but the job did not change to {set.Job}.");
    }

    public static uint CurrentJob() => Svc.Objects.LocalPlayer?.ClassJob.RowId ?? 0;
}
