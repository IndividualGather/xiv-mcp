using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;
using XivMcp.Farming;
using XivMcp.Mcp;
using XivMcp.Util;
using static XivMcp.Util.Navigation;

namespace XivMcp.Tools;

/// <summary>
/// Farming monsters for a drop: on the highest item level combat gearset, travel to where the monster lives (Teamcraft's drop data),
/// target the nearest living one, walk into range and let RotationSolver fight, until the bags hold enough.
/// </summary>
internal static partial class NavigationTools
{
    private const string RotationSolver = "RotationSolver";

    public static McpTool FarmMonsterDropsTool(Configuration config) => new()
    {
        Name = "farm_monster_drops",
        Description = "Kills monsters until the bags hold 'quantity' of an item they drop: switches to the combat gearset with the highest item " +
                      "level (or 'gearset'; when a set fails to equip, e.g. a missing weapon, the next best is tried), travels to where the monster lives (from Teamcraft's drop data; 'monster' picks one when several drop it), targets " +
                      "the nearest living one, walks into range and lets RotationSolver fight (its auto mode), and waits at the spawn area for " +
                      "respawns; below 70% HP out of combat it rests before pulling the next. Stops when enough are in the bags, on death, at the timeout, or after 'stall_minutes' " +
                      "without a kill or a drop. RotationSolver is switched off again at the end. Meant as a job step. Requires RotationSolver, " +
                      "vnavmesh, 'Game & navigation' and 'Online lookups'.",
        InputSchema = """
            {
              "type": "object",
              "properties": {
                "item": { "type": "string", "description": "The item (name or id) the monster drops." },
                "quantity": { "type": "integer", "minimum": 1, "description": "How many to have in the bags (default 1)." },
                "monster": { "type": "string", "description": "Which monster to farm, when several drop the item (default: the first Teamcraft lists with a position)." },
                "gearset": { "type": "string", "description": "Fight with this gearset (number, name or job) instead of the highest item level combat set." },
                "timeout_minutes": { "type": "integer", "minimum": 1, "maximum": 600, "description": "Give up after this long (default 120)." },
                "stall_minutes": { "type": "integer", "minimum": 1, "maximum": 120, "description": "Give up after this long without a kill or a drop (default 10)." }
              },
              "required": ["item"]
            }
            """,
        ReadOnly = false,
        Handler = async (args, ct) =>
        {
            if (!config.AllowGameNavigation)
                throw new ToolException("Farming is disabled. Enable \"Game & navigation\" in the XIV MCP settings window (/xivmcp) in game.");
            ItemSourceTools.RequireOnline(config);
            if (!PluginCompat.IsLoaded(RotationSolver)) throw new ToolException("RotationSolver isn't installed or loaded; it does the fighting.");
            if (!VnavmeshLoaded) throw new ToolException("vnavmesh isn't installed or loaded; it does the walking.");
            var wanted = args.Int("quantity", 1, 1, 9999);
            var timeout = TimeSpan.FromMinutes(args.Int("timeout_minutes", 120, 1, 600));
            var stall = TimeSpan.FromMinutes(args.Int("stall_minutes", 10, 1, 120));
            var monsterArg = args.String("monster");
            var item = await Game.Run(() => Items.Resolve(args.String("item") ?? throw new ToolException("'item' is required."))).ConfigureAwait(false);
            var sources = await Teamcraft.Sources(item.RowId, ct).ConfigureAwait(false);
            var target = await Game.Run(() => PickDrop(sources, monsterArg, item.Name.ExtractText())).ConfigureAwait(false);
            var steps = new List<string> { $"{target.Name} in {TerritoryName(target.Spot.Territory)} drops {item.Name.ExtractText()}." };

            // The strongest combat job; a set that turns out to miss a piece (the game only notices when equipping it) gives way to the next.
            var chosen = args.String("gearset");
            var candidates = chosen is not null
                ? new List<string> { chosen }
                : (await Game.Run(() => CombatGearset.Ranked(Gearsets.All().Select(s => new GearsetChoice(s.Number, s.CombatJob, s.ItemLevel, s.MissingItems))))
                      .ConfigureAwait(false)).Take(3).Select(n => n.ToString()).ToList();
            if (candidates.Count == 0) throw new ToolException("No combat gearset is saved.");
            Gearsets.Set? set = null;
            foreach (var candidate in candidates)
            {
                try { set = await Gearsets.Switch(candidate, ct, steps: steps).ConfigureAwait(false); break; }
                catch (ToolException e) when (candidate != candidates[^1]) { steps.Add($"{e.Message} Trying the next gearset."); }
            }
            steps.Add($"Gearset {set!.Number} {set.Name} ({set.Job}, item level {set.ItemLevel}).");

            await Game.RunLoggedIn(() => { EnsureCanTravel(); return true; }).ConfigureAwait(false);
            await TravelNear(target.Spot, steps, ct).ConfigureAwait(false);
            await WaitForMesh(ct).ConfigureAwait(false);
            // Map positions have no height: walk to the nearest reachable ground there.
            var spawn = await Game.Run(() => GroundNear(target.Spot.Position)).ConfigureAwait(false)
                        ?? throw new ToolException($"vnavmesh finds no ground to walk to where {target.Name} lives.");
            phase = $"farming {target.Name}";

            var started = DateTime.UtcNow;
            var progressAt = started;
            var have = await Game.Run(() => Items.CountInBags(item.RowId)).ConfigureAwait(false);
            var kills = 0;
            ulong? fighting = null;
            var range = await Game.Run(() => MobFarm.Range((byte)(Svc.Objects.LocalPlayer?.ClassJob.ValueNullable?.Role ?? 3))).ConfigureAwait(false);
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var view = await Game.Run(() => Look(target.NameId, spawn, fighting, item.RowId)).ConfigureAwait(false);
                    if (view.Have != have) { have = view.Have; progressAt = DateTime.UtcNow; }
                    if (fighting is not null && !view.FightingAlive) { kills++; fighting = null; progressAt = DateTime.UtcNow; }
                    var now = DateTime.UtcNow;
                    var decision = MobFarm.Next(new MobFarmState(view.Have, wanted, view.HpPercent, view.Dead, view.InCombat, view.FightingAlive,
                        view.NearestDistance, view.DistanceToSpawn, now - started, timeout, now - progressAt, stall));
                    switch (decision.Action)
                    {
                        case MobFarmAction.Done:
                            return new { item = item.Name.ExtractText(), inBags = view.Have, wanted, kills, minutes = Math.Round((now - started).TotalMinutes, 1), steps };
                        case MobFarmAction.Fail:
                            throw new ToolException($"{decision.Reason} {view.Have}/{wanted} {item.Name.ExtractText()} after {kills} kill(s).");
                        case MobFarmAction.Engage:
                            if (await Game.Run(() => Svc.Condition[ConditionFlag.Mounted]).ConfigureAwait(false))
                                await GetOffMount(steps, ct).ConfigureAwait(false);
                            fighting = await Game.Run(() =>
                            {
                                var mob = NearestMob(target.NameId);
                                if (mob is null) return (ulong?)null;
                                Svc.Targets.Target = mob;
                                Svc.Commands.ProcessCommand("/rotation Auto");
                                return mob.GameObjectId;
                            }).ConfigureAwait(false);
                            break;
                        case MobFarmAction.Fight:
                            await Game.Run(() =>
                            {
                                var t = Svc.Targets.Target;
                                if (t is not null && Game.DistanceToPlayer(t.Position) is { } d && d > range + t.HitboxRadius)
                                    MoveCloseTo(t.Position, range + t.HitboxRadius - 0.5f);
                                else if (t is not null && !PathRunning) StopMoving();
                                return true;
                            }).ConfigureAwait(false);
                            break;
                        case MobFarmAction.ReturnToSpawn:
                            if (!await Game.Run(() => PathRunning).ConfigureAwait(false))
                                await StartPath(spawn, 10f, steps, ct).ConfigureAwait(false); // mounts up (and flies) for a longer way
                            break;
                        case MobFarmAction.Rest:
                            await Game.Run(() => { if (PathRunning) StopMoving(); return true; }).ConfigureAwait(false);
                            break;
                        case MobFarmAction.Wait:
                            break;
                    }
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            finally
            {
                await Game.Run(() =>
                {
                    Svc.Commands.ProcessCommand("/rotation Off");
                    StopMoving();
                    return true;
                }).ConfigureAwait(false);
                phase = "idle";
            }
        },
    };

    private sealed record DropTarget(string Name, uint NameId, NpcSpot Spot);

    /// <summary>The monster to farm, from Teamcraft's drops: the named one, else the first with a zone and position. Framework thread.</summary>
    private static DropTarget PickDrop(JsonArray? sources, string? monster, string itemName)
    {
        var drops = (sources ?? []).OfType<JsonObject>()
            .Where(s => JsonNumber.Int(s["type"]) == (int)Teamcraft.SourceType.Drops)
            .SelectMany(s => s["data"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(d => d["zoneid"] is not null && d["position"] is JsonObject)
            .ToList();
        var territories = Svc.Data.GetExcelSheet<TerritoryType>();
        foreach (var d in drops)
        {
            var nameId = (uint)(JsonNumber.Int(d["id"]) ?? 0);
            var name = Excel.NameOf<BNpcName>(nameId) is { Length: > 0 } n ? Game.TitleCase(n) : $"monster {nameId}";
            if (monster is not null && !Game.Matches(name, monster)) continue;
            var zone = (uint)(JsonNumber.Int(d["zoneid"]) ?? 0);
            var territory = territories.Where(t => t.PlaceName.RowId == zone && t.Map.RowId != 0)
                                       .OrderBy(t => t.TerritoryIntendedUse.RowId == 1 ? 0 : 1).FirstOrDefault();
            if (territory.RowId == 0 || territory.Map.ValueNullable is not { } map) continue;
            var p = (JsonObject)d["position"]!;
            var position = MapToWorld(map, p["x"]?.GetValue<double>() ?? 0, p["y"]?.GetValue<double>() ?? 0);
            return new DropTarget(name, nameId, new NpcSpot(0, name, territory.RowId, map.RowId, position, false));
        }
        throw new ToolException(drops.Count == 0
            ? $"Teamcraft knows no monster with a location that drops {itemName}."
            : $"No monster named '{monster}' drops {itemName}; it drops from {string.Join(", ", drops.Select(d => Excel.NameOf<BNpcName>((uint)(JsonNumber.Int(d["id"]) ?? 0))).Distinct())}.");
    }

    private sealed record FarmView(int Have, double HpPercent, bool Dead, bool InCombat, bool FightingAlive, double? NearestDistance, double DistanceToSpawn);

    /// <summary>What the farm loop needs to decide. Framework thread.</summary>
    private static FarmView Look(uint nameId, Vector3 spawn, ulong? fighting, uint itemId)
    {
        var p = Svc.Objects.LocalPlayer;
        var hp = p is { MaxHp: > 0 } ? 100.0 * p.CurrentHp / p.MaxHp : 0;
        var alive = fighting is { } id && Svc.Objects.FirstOrDefault(o => o.GameObjectId == id) is IBattleNpc b && !b.IsDead && b.CurrentHp > 0;
        var nearest = NearestMob(nameId);
        return new FarmView(Items.CountInBags(itemId), hp, p is null || p.IsDead, Svc.Condition[ConditionFlag.InCombat], alive,
            nearest is null ? null : Game.DistanceToPlayer(nearest.Position),
            p is null ? 0 : Vector2.Distance(new(p.Position.X, p.Position.Z), new(spawn.X, spawn.Z)));
    }

    /// <summary>The nearest reachable mesh point at a map position (no height), searching wider and higher step by step. Framework thread.</summary>
    private static Vector3? GroundNear(Vector3 position)
    {
        foreach (var (radius, height) in new[] { (10f, 60f), (30f, 300f), (60f, 600f) })
            if (Ipc<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable", position with { Y = PlayerHeight() }, radius, height) is { } p)
                return p;
        return null;
    }

    /// <summary>The nearest living, targetable monster of the kind. Framework thread.</summary>
    private static IBattleNpc? NearestMob(uint nameId) =>
        Svc.Objects.OfType<IBattleNpc>()
            .Where(o => o.NameId == nameId && o.IsTargetable && !o.IsDead && o.CurrentHp > 0)
            .OrderBy(o => Game.DistanceToPlayer(o.Position) ?? float.MaxValue)
            .FirstOrDefault();
}
