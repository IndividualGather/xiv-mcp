using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XivMcp.Maps;

namespace XivMcp.Util;

/// <summary>
/// The doors into zones that have no aetheryte of their own (Fortemps Manor, the Rising Stones, ...), from the game data: a Warp row
/// leads into the zone, an NPC (its ENpcData) or event object (its Data) opens that warp when talked to, and the Level sheet says where
/// it stands. Read the first time a door is needed (about half a second: it goes through every NPC), then kept. Static data, any thread.
/// </summary>
internal static class ZoneDoors
{
    private const uint WarpHandler = 2; // EventHandlerType of a Warp row: the high 16 bits of its row id

    internal sealed record Door(uint ObjectId, string Name, uint Territory, uint Map, Vector3 Position, uint Warp, string Question, DoorCondition Condition);

    private static readonly Lazy<IReadOnlyDictionary<uint, IReadOnlyList<Door>>> Into = new(Build);

    /// <summary>The doors that lead into the zone; empty when none is known.</summary>
    public static IReadOnlyList<Door> Of(uint territory) => Into.Value.TryGetValue(territory, out var doors) ? doors : [];

    /// <summary>Whether the zone has an aetheryte or aethernet shard of its own to travel to.</summary>
    public static bool HasOwnAetheryte(uint territory) =>
        Svc.Data.GetExcelSheet<Aetheryte>().Any(a => a.Territory.RowId == territory && (a.IsAetheryte || a.AethernetName.RowId != 0));

    /// <summary>Whether the door opens for the player now, as far as the game data tells. Framework thread.</summary>
    public static DoorRefusal? Refusal(DoorCondition condition)
    {
        uint gil;
        unsafe { gil = InventoryManager.Instance()->GetGil(); }
        return ZoneDoor.Refusal(condition, QuestDone, gil, Svc.Objects.LocalPlayer?.Level ?? 0);
    }

    /// <summary>The refusal in words, naming the quests.</summary>
    public static string Describe(DoorRefusal r)
    {
        var parts = new List<string>();
        if (r.MissingQuests.Count > 0)
            parts.Add($"the quest{(r.MissingQuests.Count > 1 ? "s" : "")} " +
                      string.Join(r.AnyQuest ? " or " : " and ", r.MissingQuests.Select(q => $"\"{Excel.NameOf<Quest>(q) ?? $"quest {q}"}\"")));
        if (r.ClassLevel > 0) parts.Add($"level {r.ClassLevel}");
        if (r.Gil > 0) parts.Add($"{r.Gil:N0} gil");
        return string.Join(", ", parts);
    }

    /// <summary>Quest ids appear with and without the 65536 offset of the Quest sheet; either counts. Framework thread.</summary>
    private static bool QuestDone(uint quest) =>
        QuestManager.IsQuestComplete(quest) || (quest > 65536 ? QuestManager.IsQuestComplete(quest - 65536) : QuestManager.IsQuestComplete(quest + 65536));

    private static IReadOnlyDictionary<uint, IReadOnlyList<Door>> Build()
    {
        var warps = Svc.Data.GetExcelSheet<Warp>().Where(w => w.TerritoryType.RowId != 0).ToDictionary(w => w.RowId);

        var opensWarp = new Dictionary<uint, uint>(); // NPC or event object -> the warp it opens
        foreach (var npc in Svc.Data.GetExcelSheet<ENpcBase>())
            foreach (var data in npc.ENpcData)
                if (data.RowId >> 16 == WarpHandler && warps.ContainsKey(data.RowId))
                    opensWarp.TryAdd(npc.RowId, data.RowId);
        foreach (var obj in Svc.Data.GetExcelSheet<EObj>())
            if (obj.Data.RowId >> 16 == WarpHandler && warps.ContainsKey(obj.Data.RowId))
                opensWarp.TryAdd(obj.RowId, obj.Data.RowId);

        var npcNames = Svc.Data.GetExcelSheet<ENpcResident>();
        var objNames = Svc.Data.GetExcelSheet<EObjName>();
        var doors = new Dictionary<uint, List<Door>>();
        var placed = new HashSet<uint>();
        foreach (var level in Svc.Data.GetExcelSheet<Level>())
        {
            var id = level.Object.RowId;
            if (id == 0 || level.Territory.RowId == 0 || !opensWarp.TryGetValue(id, out var warpId) || !placed.Add(id)) continue;
            var warp = warps[warpId];
            // A door out of a zone into the same zone (a ship's cabins) leads nowhere new.
            if (warp.TerritoryType.RowId == level.Territory.RowId) continue;
            var name = (npcNames.GetRowOrDefault(id)?.Singular ?? objNames.GetRowOrDefault(id)?.Singular)?.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var target = warp.TerritoryType.RowId;
            // Inn rooms and housing districts are travel of their own (navigate_to inn, home, fc_house), reached through a menu of
            // rooms or wards that a door here does not answer.
            if (warp.TerritoryType.ValueNullable?.TerritoryIntendedUse.RowId is PlayerGuidance.InnUse or PlayerGuidance.HousingAreaUse) continue;
            if (!doors.TryGetValue(target, out var list)) doors[target] = list = [];
            list.Add(new Door(id, char.ToUpperInvariant(name[0]) + name[1..], level.Territory.RowId, level.Map.RowId,
                new Vector3(level.X, level.Y, level.Z), warpId, warp.Question.ExtractText(), Condition(warp)));
        }
        return doors.ToDictionary(d => d.Key, d => (IReadOnlyList<Door>)d.Value);
    }

    private static DoorCondition Condition(Warp warp)
    {
        var scripted = warp.WarpLogic.ValueNullable is { } logic && logic.WarpParams.Any(p => !p.Function.IsEmpty);
        if (warp.WarpCondition.ValueNullable is not { } c) return DoorCondition.None with { Scripted = scripted };
        var quests = new[] { c.RequiredQuest1.RowId, c.RequiredQuest2.RowId, c.RequiredQuest3.RowId, c.RequiredQuest4.RowId }.Where(q => q != 0).ToList();
        return new DoorCondition(quests, c.CompleteParam == 2, c.Gil, c.ClassLevel, scripted);
    }
}
