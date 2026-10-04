using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;
using XivMcp.Tools;
using LuminaMap = Lumina.Excel.Sheets.Map;
using TriadSheet = Lumina.Excel.Sheets.TripleTriad;

namespace XivMcp.Util;

/// <summary>
/// Triple Triad from the game's sheets: the cards, the opponents (who they are, where they stand, their rules and the cards they
/// give) and the places of other NPCs, built once in the background. What the player owns is read live (<see cref="Owned"/>).
/// </summary>
internal static class TriadData
{
    internal sealed record Card(int Id, string Name, int Stars, string Type, int Top, int Right, int Bottom, int Left, uint ItemId, List<uint> Npcs);

    internal sealed record Npc(uint TriadId, uint NpcId, string Name, NavigationTools.NpcSpot Spot, string Zone, double MapX, double MapY,
                               IReadOnlyList<string> Rules, int Fee, IReadOnlyList<int> RewardCards, IReadOnlyList<uint> Quests, int StartTime, int EndTime);

    internal sealed record Db(IReadOnlyDictionary<int, Card> Cards, IReadOnlyList<Npc> Npcs);

    private static readonly Lazy<Dictionary<uint, (uint Territory, uint Map, Vector3 Position)>> Places = new(BuildPlaces);
    private static readonly Lazy<Db> Data = new(Build);

    /// <summary>Opponents and cards. The first call builds them (a moment); call off the framework thread.</summary>
    public static Db Get() => Data.Value;

    /// <summary>Where an NPC stands (from the Level sheet), as a spot to travel to; null if the game doesn't place it.</summary>
    public static NavigationTools.NpcSpot? Locate(uint npcId, string name)
    {
        if (!Places.Value.TryGetValue(npcId, out var p)) return null;
        return new NavigationTools.NpcSpot(npcId, name, p.Territory, p.Map, p.Position, true);
    }

    /// <summary>
    /// An NPC anywhere in the world by name (as the game spells it, ignoring case; a unique part of a name also works), from the
    /// game data. Prefers one placed in <paramref name="preferTerritory"/>. Off the framework thread is fine.
    /// </summary>
    public static NavigationTools.NpcSpot? FindNpc(string name, uint preferTerritory)
    {
        var q = name.Trim();
        var residents = Svc.Data.GetExcelSheet<ENpcResident>();
        var placed = residents.Where(r => Places.Value.ContainsKey(r.RowId)).Select(r => (Id: r.RowId, Name: r.Singular.ExtractText())).Where(r => r.Name.Length > 0).ToList();
        var exact = placed.Where(r => r.Name.Equals(q, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact : placed.Where(r => r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Select(m => m.Name.ToLowerInvariant()).Distinct().Count() > 1)
            throw new Mcp.ToolException($"'{q}' matches several NPCs: {string.Join(", ", matches.Select(m => m.Name).Distinct().Take(8))}. Give the full name.");
        var best = matches.OrderBy(m => Places.Value[m.Id].Territory == preferTerritory ? 0 : 1).FirstOrDefault();
        if (best.Id == 0) return null;
        var display = char.ToUpperInvariant(best.Name[0]) + best.Name[1..];
        return Locate(best.Id, display);
    }

    private static Dictionary<uint, (uint, uint, Vector3)> BuildPlaces()
    {
        var places = new Dictionary<uint, (uint, uint, Vector3)>();
        foreach (var level in Svc.Data.GetExcelSheet<Level>())
            if (level.Type == 8 && level.Object.RowId != 0 && level.Territory.RowId != 0)
                places.TryAdd(level.Object.RowId, (level.Territory.RowId, level.Map.RowId, new Vector3(level.X, level.Y, level.Z)));
        return places;
    }

    private static Db Build()
    {
        var cardNames = Svc.Data.GetExcelSheet<TripleTriadCard>();
        var types = Svc.Data.GetExcelSheet<TripleTriadCardType>();
        var cards = new Dictionary<int, Card>();
        foreach (var r in Svc.Data.GetExcelSheet<TripleTriadCardResident>())
        {
            if (r.Top == 0 || cardNames.GetRowOrDefault(r.RowId) is not { } named) continue;
            var name = named.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var type = types.GetRowOrDefault(r.TripleTriadCardType.RowId)?.Name.ExtractText() ?? "";
            cards[(int)r.RowId] = new Card((int)r.RowId, name, (int)r.TripleTriadCardRarity.RowId, type, r.Top, r.Right, r.Bottom, r.Left, 0, []);
        }

        // Which ENpc runs which Triple Triad match: its ENpcData lists the TripleTriad row.
        var triadRows = Svc.Data.GetExcelSheet<TriadSheet>().ToDictionary(t => t.RowId);
        var npcOf = new Dictionary<uint, uint>();
        foreach (var e in Svc.Data.GetExcelSheet<ENpcBase>())
            foreach (var data in e.ENpcData)
                if (triadRows.ContainsKey(data.RowId) && (!npcOf.ContainsKey(data.RowId) || !Places.Value.ContainsKey(npcOf[data.RowId])))
                    npcOf[data.RowId] = e.RowId;

        var residents = Svc.Data.GetExcelSheet<ENpcResident>();
        var items = Svc.Data.GetExcelSheet<Item>();
        var npcs = new List<Npc>();
        foreach (var (triadId, row) in triadRows)
        {
            if (!npcOf.TryGetValue(triadId, out var npcId)) continue;
            var name = residents.GetRowOrDefault(npcId)?.Singular.ExtractText();
            if (string.IsNullOrWhiteSpace(name) || Locate(npcId, name) is not { } spot) continue;
            name = char.ToUpperInvariant(name[0]) + name[1..];
            spot = spot with { Name = name };

            var rewards = new List<int>();
            foreach (var reward in row.ItemPossibleReward)
            {
                if (reward.RowId == 0 || items.GetRowOrDefault(reward.RowId) is not { } item) continue;
                var cardId = (int)item.AdditionalData.RowId;
                if (!cards.TryGetValue(cardId, out var card)) continue;
                rewards.Add(cardId);
                cards[cardId] = card with { ItemId = reward.RowId };
                cards[cardId].Npcs.Add(triadId);
            }
            var rules = row.TripleTriadRule.Where(r => r.RowId != 0).Select(r => Excel.Name(r) ?? "").Where(n => n.Length > 0).ToList();
            if (row.UsesRegionalRules) rules.Add("regional rules");
            var map = Svc.Data.GetExcelSheet<LuminaMap>().GetRowOrDefault(spot.Map);
            var (x, y) = map is { } m ? NavigationTools.WorldToMap(m, spot.Position) : (0, 0);
            var zone = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(spot.Territory) is { } t ? Excel.Name(t.PlaceName) ?? "" : "";
            var quests = row.PreviousQuest.Where(q => q.RowId != 0).Select(q => q.RowId).ToList();
            npcs.Add(new Npc(triadId, npcId, name, spot, zone, x, y, rules, row.Fee, rewards, quests, row.StartTime, row.EndTime));
        }
        return new Db(cards, npcs.OrderBy(n => n.Zone).ThenBy(n => n.Name).ToList());
    }

    // ---------------------------------------------------------------- live state (framework thread)

    /// <summary>The cards in the player's collection. Framework thread.</summary>
    public static unsafe HashSet<int> Owned(IEnumerable<int> cards)
    {
        var ui = UIState.Instance();
        return ui == null ? [] : cards.Where(c => ui->IsTripleTriadCardUnlocked((ushort)c)).ToHashSet();
    }

    /// <summary>The cards waiting in the bags as items, not registered yet. Framework thread.</summary>
    public static unsafe HashSet<int> InBags(IEnumerable<Card> cards)
    {
        var im = InventoryManager.Instance();
        return im == null ? [] : cards.Where(c => c.ItemId != 0 && im->GetInventoryItemCount(c.ItemId) > 0).Select(c => c.Id).ToHashSet();
    }

    /// <summary>Whether the player has beaten this opponent at least once. Framework thread.</summary>
    public static unsafe bool Beaten(uint triadId)
    {
        var ui = UIState.Instance();
        return ui != null && ui->IsTripleTriadNpcBeaten(triadId);
    }

    /// <summary>Whether an opponent can be challenged: unlocked (quests, or played before), and inside their hours if they have any. Framework thread.</summary>
    public static (bool Unlocked, bool OpenNow, IReadOnlyList<string> MissingQuests, string? Hours) Availability(Npc npc, IReadOnlySet<int> owned)
    {
        var unlocked = TripleTriad.TriadAvailability.IsUnlocked(Beaten(npc.TriadId), npc.RewardCards.Any(owned.Contains), npc.Quests, QuestDone);
        var quests = unlocked ? [] : npc.Quests.Select(q => Excel.NameOf<Quest>(q) ?? $"quest {q}").ToList();
        var hasHours = npc.StartTime != npc.EndTime;
        var open = TripleTriad.EorzeaClock.InWindow(npc.StartTime, npc.EndTime, TripleTriad.EorzeaClock.MinuteOfDay(DateTime.UtcNow));
        return (unlocked, open, quests,
                hasHours ? $"{TripleTriad.EorzeaClock.Format(npc.StartTime)}–{TripleTriad.EorzeaClock.Format(npc.EndTime)} Eorzea time" : null);
    }

    /// <summary>Quest ids appear with and without the 65536 offset of the Quest sheet; either counts.</summary>
    private static bool QuestDone(uint quest) =>
        QuestManager.IsQuestComplete(quest) || (quest > 65536 ? QuestManager.IsQuestComplete(quest - 65536) : QuestManager.IsQuestComplete(quest + 65536));

    /// <summary>The five deck presets: name and card ids (0 = empty slot). Framework thread.</summary>
    public static unsafe List<(string Name, ushort[] Cards)> Decks()
    {
        var gs = UIModule.Instance()->GetGoldSaucerModule();
        var decks = new List<(string, ushort[])>();
        for (var i = 0; i < 5; i++)
        {
            var deck = gs->GetDeck(i);
            decks.Add(deck == null ? ("", new ushort[5]) : (deck->NameString, deck->Cards.ToArray()));
        }
        return decks;
    }

    /// <summary>Writes a deck preset (index 0–4): the cards and, if given, its name. The caller checks the deck first. Framework thread.</summary>
    public static unsafe void WriteDeck(int index, IReadOnlyList<int> cards, string? name)
    {
        var gs = UIModule.Instance()->GetGoldSaucerModule();
        var deck = gs->GetDeck(index);
        if (deck == null) throw new Mcp.ToolException("The game's deck presets are not available right now.");
        for (var i = 0; i < 5; i++) deck->Cards[i] = (ushort)cards[i];
        if (name is not null)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name);
            var span = deck->Name;
            span.Clear();
            bytes.AsSpan(0, Math.Min(bytes.Length, span.Length - 1)).CopyTo(span);
        }
    }
}
