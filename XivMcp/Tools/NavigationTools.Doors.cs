using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using XivMcp.Maps;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>
/// Zones without an aetheryte of their own, entered through a door: an NPC or object in another zone that asks "Enter ...?"
/// (<see cref="ZoneDoors"/>). XIV MCP goes to the door like to any NPC, talks to it and answers its question.
/// </summary>
internal static partial class NavigationTools
{
    /// <summary>Yalms within which doors into the same zone count as standing on one spot.</summary>
    private const float SameSpot = 3f;

    /// <summary>Whether the zone is entered only through a door that XIV MCP knows. Off the framework thread.</summary>
    private static bool EnteredThroughDoor(uint territory) => !ZoneDoors.HasOwnAetheryte(territory) && ZoneDoors.Of(territory).Count > 0;

    /// <summary>Goes through a door into the zone of <paramref name="spot"/>; checks first that it will open for the player.</summary>
    private static async Task EnterThroughDoor(NpcSpot spot, List<string> steps, CancellationToken ct)
    {
        var zone = TerritoryName(spot.Territory);
        var doors = await Task.Run(() => ZoneDoors.Of(spot.Territory).Select(d => (Door: d, Own: ZoneDoors.HasOwnAetheryte(d.Territory))).ToList(), ct)
            .ConfigureAwait(false);
        var door = await Game.Run(() =>
        {
            var choices = doors.Select(d => new DoorChoice(d.Door.ObjectId, d.Door.Territory, d.Own, d.Door.Condition)).ToList();
            var pick = ZoneDoor.Pick(choices, Svc.ClientState.TerritoryType, c => ZoneDoors.Refusal(c) is null);
            if (pick is not null) return doors.First(d => d.Door.ObjectId == pick.ObjectId).Door;
            // Say why: the first door the player could reach that won't open, else that every door is behind another door.
            foreach (var (d, own) in doors.Where(d => d.Own))
                if (ZoneDoors.Refusal(d.Condition) is { } refusal)
                    throw new ToolException($"{zone} is entered through {d.Name} in {TerritoryName(d.Territory)}, which lets you in only with " +
                                            $"{ZoneDoors.Describe(refusal)}.");
            var first = doors[0].Door;
            throw new ToolException($"{zone} is entered through {first.Name} in {TerritoryName(first.Territory)}, which has no aetheryte either; go there first, then retry.");
        }).ConfigureAwait(false);

        steps.Add($"{zone} has no aetheryte: going through {door.Name} in {TerritoryName(door.Territory)}.");
        // Several doors into the same zone can stand on one spot, one per stage of the story (three Crystarium gatekeeps lead
        // to the Ocular), and the game shows only one of them: walk to whichever of them is there.
        var onSpot = doors.Where(d => d.Door.Territory == door.Territory && Vector3.Distance(d.Door.Position, door.Position) < SameSpot)
                        .Select(d => d.Door.ObjectId).ToHashSet();
        await GoToNpc(new NpcSpot(door.ObjectId, door.Name, door.Territory, door.Map, door.Position, true), steps, ct, onSpot).ConfigureAwait(false);
        phase = $"entering {zone}";

        var start = await Game.Run(() => Svc.ClientState.TerritoryType).ConfigureAwait(false);
        // The game can ignore a talk without a word (right after a landing, for one): talk again until the door answers.
        bool Answered() => Svc.ClientState.TerritoryType != start || Svc.Condition[ConditionFlag.BetweenAreas]
                           || RetainerUi.Ready("Talk") || RetainerUi.Ready("SelectYesno") || RetainerUi.Ready("SelectString") || RetainerUi.Ready("SelectIconString");
        ZoneDoors.Door? answered = null;
        for (var attempt = 1; answered is null; attempt++)
        {
            var talked = await Game.Run(() =>
            {
                var obj = Svc.Objects.Where(o => o.IsTargetable && onSpot.Contains(o.BaseId)).OrderBy(o => Game.DistanceToPlayer(o.Position) ?? float.MaxValue).FirstOrDefault()
                          ?? throw new ToolException($"{door.Name} is not here.");
                InteractionTools.Interact(obj.Address, lineOfSight: false);
                return doors.First(d => d.Door.ObjectId == obj.BaseId).Door;
            }).ConfigureAwait(false);
            var answerBy = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < answerBy && !await Game.Run(Answered).ConfigureAwait(false))
                await Task.Delay(200, ct).ConfigureAwait(false);
            if (await Game.Run(Answered).ConfigureAwait(false)) answered = talked;
            else if (attempt == 3) throw new ToolException($"{talked.Name} did not answer after three tries.");
            else steps.Add($"{talked.Name} did not answer; talking again.");
        }
        var used = answered;

        // The door asks its question, sometimes after a line of dialogue or a menu: answer until the zone changes.
        var question = used.Question.TrimEnd('?', ' ');
        bool Ours(string text) =>
            (question.Length > 0 && text.Contains(question, StringComparison.OrdinalIgnoreCase)) || text.Contains(zone, StringComparison.OrdinalIgnoreCase);
        var until = DateTime.UtcNow.AddSeconds(20);
        var leftToPlayer = false;
        try
        {
            await AnswerDoor().ConfigureAwait(false);
        }
        catch (ToolException) when (!leftToPlayer)
        {
            // Leave the player free: close what the door left open (a menu without the way in, a talk that led nowhere).
            if (await Game.Run(() => Recovery.Occupied).ConfigureAwait(false)) await Recovery.Run(ct).ConfigureAwait(false);
            throw;
        }

        async Task AnswerDoor()
        {
            while (true)
            {
                var (here, loading) = await Game.Run(() => (Svc.ClientState.TerritoryType,
                    Svc.Condition[ConditionFlag.BetweenAreas] || Svc.Condition[ConditionFlag.BetweenAreas51])).ConfigureAwait(false);
                if (here != start && !loading) break;
                if (DateTime.UtcNow > until)
                    throw new ToolException($"Talked to {used.Name}, but did not get into {zone}.");
                var acted = loading ? null : await Game.Run(() =>
                {
                    if (GameWindows.YesNoPrompt() is { } prompt)
                    {
                        if (!Ours(prompt))
                        {
                            leftToPlayer = true;
                            throw new ToolException($"{used.Name} asked \"{prompt}\"; that is left for you to answer.");
                        }
                        GameWindows.AnswerYesNo(true);
                        return $"Answered Yes to \"{prompt}\".";
                    }
                    if (ShopTools.MenuEntries(out var menu) is { Count: > 0 } entries)
                    {
                        var index = entries.FindIndex(Ours);
                        if (index < 0) throw new ToolException($"{used.Name} offers {string.Join(" | ", entries)}; none of them leads to {zone}.");
                        ShopTools.FireMenu(menu, index);
                        return $"Chose \"{entries[index]}\".";
                    }
                    if (RetainerUi.Ready("Talk"))
                    {
                        RetainerUi.ClickTalk();
                        return "";
                    }
                    return null;
                }).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(acted)) steps.Add(acted);
                await Task.Delay(acted is null ? 250 : 700, ct).ConfigureAwait(false);
            }
        }
        await WaitUntilSettled(ct).ConfigureAwait(false);
        if (await Game.Run(() => Svc.ClientState.TerritoryType).ConfigureAwait(false) != spot.Territory)
            throw new ToolException($"Went through {used.Name}, but ended up in {await Game.Run(ZoneName).ConfigureAwait(false)} instead of {zone}.");
        steps.Add($"Went through {used.Name} into {zone}.");
    }
}
