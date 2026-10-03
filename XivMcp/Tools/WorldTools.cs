using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

internal static class WorldTools
{
    public static IEnumerable<McpTool> Create(RetainerTracker retainers)
    {
        yield return new McpTool
        {
            Name = "get_party",
            Description = "Members of the current party (or alliance): name, world, job, level, HP/MP, zone, position, distance and status effects, plus who the leader is.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() => new
            {
                size = Svc.Party.Length,
                isAlliance = Svc.Party.IsAlliance,
                leaderIndex = Svc.Party.Length > 0 ? (int?)Svc.Party.PartyLeaderIndex : null,
                members = Svc.Party.Select((m, i) => new
                {
                    index = i,
                    name = m.Name.TextValue,
                    contentId = m.ContentId,
                    entityId = m.EntityId,
                    world = Excel.Ref(m.World),
                    classJob = Excel.Ref(m.ClassJob),
                    level = m.Level,
                    hp = new { current = m.CurrentHP, max = m.MaxHP },
                    mp = new { current = m.CurrentMP, max = m.MaxMP },
                    territory = Excel.Ref(m.Territory),
                    position = Game.Pos(m.Position),
                    distance = Game.DistanceToPlayer(m.Position),
                    statuses = Game.Statuses(m.Statuses),
                }).ToList(),
            }),
        };

        yield return new McpTool
        {
            Name = "get_targets",
            Description = "The logged-in character's current target, focus target, soft target, mouse-over target and the target's target, with HP, cast bar and status effects.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
            {
                var t = Svc.Targets;
                return new
                {
                    target = t.Target is { } a ? Game.Describe(a, true) : null,
                    focusTarget = t.FocusTarget is { } f ? Game.Describe(f, true) : null,
                    softTarget = t.SoftTarget is { } s ? Game.Describe(s, false) : null,
                    mouseOverTarget = t.MouseOverTarget is { } m ? Game.Describe(m, false) : null,
                    previousTarget = t.PreviousTarget is { } p ? Game.Describe(p, false) : null,
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_nearby_objects",
            Description = "Lists game objects around the character (players, NPCs, enemies, treasure, gathering points, aetherytes, event objects, ...) " +
                          "sorted by distance. Filter by kind, name and maximum distance.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    "kinds": { "type": "array", "items": { "type": "string", "enum": [{{string.Join(", ", Enum.GetNames<ObjectKind>().Select(n => $"\"{n}\""))}}] },
                               "description": "Object kinds to include (default all). Enemies and battle NPCs are 'BattleNpc', players are 'Pc'." },
                    "query": { "type": "string", "description": "Case-insensitive part of the object name." },
                    "max_distance": { "type": "number", "description": "Maximum distance in yalms (default unlimited)." },
                    "limit": { "type": "integer", "description": "Max objects (default 50, max 500)." },
                    "detailed": { "type": "boolean", "description": "Include statuses, mount/minion, target etc. (default false)." }
                  }
                }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                var kinds = args.StringList("kinds")
                    .Select(k => Enum.TryParse<ObjectKind>(k, true, out var kind) ? kind : throw new ToolException($"Unknown kind '{k}'."))
                    .ToHashSet();
                var query = args.String("query");
                var maxDistance = args.Float("max_distance");
                var limit = args.Int("limit", 50, 1, 500);
                var detailed = args.Bool("detailed", false);
                var self = Svc.Objects.LocalPlayer!;

                var objects = Svc.Objects
                    .Where(o => o.Address != self.Address)
                    .Where(o => kinds.Count == 0 || kinds.Contains(o.ObjectKind))
                    .Where(o => Game.Matches(o.Name.TextValue, query))
                    .Select(o => (Obj: o, Dist: Vector3Distance(self, o)))
                    .Where(x => maxDistance is null || x.Dist <= maxDistance)
                    .OrderBy(x => x.Dist)
                    .ToList();

                return new
                {
                    total = objects.Count,
                    objects = objects.Take(limit).Select(x => Game.Describe(x.Obj, detailed)).ToList(),
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_fates",
            Description = "Active FATEs in the current zone with name, level, state, progress, remaining time, bonus flag, position and distance.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
                Svc.Fates.Where(f => f is not null).Select(f => new
                {
                    id = f.FateId,
                    name = f.Name.TextValue,
                    objective = f.Objective.TextValue,
                    state = f.State.ToString(),
                    level = f.Level,
                    maxLevel = f.MaxLevel,
                    progressPercent = f.Progress,
                    secondsRemaining = f.TimeRemaining,
                    durationSeconds = f.Duration,
                    hasBonus = f.HasBonus,
                    handInCount = f.HandInCount,
                    position = Game.Pos(f.Position),
                    radius = f.Radius,
                    distance = Game.DistanceToPlayer(f.Position),
                }).OrderBy(f => f.distance).ToList()),
        };

        yield return new McpTool
        {
            Name = "get_aetherytes",
            Description = "Aetherytes the character has attuned to (the teleport list) with zone, teleport gil cost, favourite flags and housing info.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
                Svc.Aetherytes.Select(a => new
                {
                    aetheryteId = a.AetheryteId,
                    name = Excel.Name(a.AetheryteData),
                    territory = Excel.Ref<TerritoryType>(a.TerritoryId),
                    gilCost = a.GilCost,
                    favourite = a.IsFavourite,
                    housing = a.Ward > 0 ? new { ward = a.Ward, plot = a.Plot, sharedHouse = a.IsSharedHouse, apartment = a.IsApartment } : null,
                }).ToList()),
        };

        yield return new McpTool
        {
            Name = "get_companions",
            Description = "The character's chocobo companion, pet (summoner/scholar pet) and battle buddies (trusts / duty support), with HP.",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
            {
                var b = Svc.Buddies;
                return new
                {
                    chocobo = b.CompanionBuddy is { } c ? new { name = c.GameObject?.Name.TextValue, hp = new { current = c.CurrentHP, max = c.MaxHP } } : null,
                    pet = b.PetBuddy is { } p ? new { name = p.GameObject?.Name.TextValue, pet = Excel.Ref(p.PetData), hp = new { current = p.CurrentHP, max = p.MaxHP } } : null,
                    battleBuddies = b.Select(m => new
                    {
                        name = m.GameObject?.Name.TextValue,
                        trust = m.TrustData.RowId != 0 ? Excel.Ref(m.TrustData) : null,
                        hp = new { current = m.CurrentHP, max = m.MaxHP },
                    }).ToList(),
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_retainers",
            Description = "The character's retainers: name, class/job, level, gil, item and market listing counts, city, current venture and when it completes. " +
                          "Comes from the retainer cache (refreshed while at a summoning bell); the 'cache' block gives its age and how to refresh it. " +
                          "Venture completion times stay accurate between refreshes. For retainer inventories use get_retainer_inventories.",
            InputSchema = """
                { "type": "object", "properties": { "all_characters": { "type": "boolean", "description": "Include all characters seen by the plugin (default false)." } } }
                """,
            Handler = (args, _) => Svc.Framework.RunOnFrameworkThread<object?>(() =>
            {
                List<CharacterRetainers> characters;
                if (args.Bool("all_characters", false)) characters = retainers.All();
                else if (!Svc.ClientState.IsLoggedIn || !Svc.PlayerState.IsLoaded)
                    throw new ToolException("No character is logged in. Use all_characters=true to see saved retainer data.");
                else characters = retainers.Get(Svc.PlayerState.ContentId) is { } c ? [c]
                    : throw new ToolException("No retainer data recorded yet for this character. To capture it: " + RetainerTracker.ListRefreshHint);

                var now = DateTimeOffset.UtcNow;
                return characters.Select(c => new
                {
                    character = c.Label,
                    cache = CacheFreshness.Describe(c.ListCapturedUtc,
                        Svc.PlayerState.ContentId == c.ContentId && (RetainerUi.RetainerListOpen || RetainerUi.InventoryOpen), RetainerTracker.ListRefreshHint),
                    retainers = c.Retainers.OrderBy(r => r.SortIndex).Select(r =>
                    {
                        var ventureDone = r.VentureComplete > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.VentureComplete) : (DateTimeOffset?)null;
                        return new
                        {
                            name = r.Name,
                            classJob = Excel.Ref<ClassJob>(r.ClassJob),
                            level = r.Level,
                            gil = r.Gil,
                            itemCount = r.ItemCount,
                            marketItemCount = r.MarketItemCount,
                            marketExpires = r.MarketExpire > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.MarketExpire) : (DateTimeOffset?)null,
                            town = r.Town,
                            venture = r.VentureId != 0 ? Excel.Ref<RetainerTask>(r.VentureId) : null,
                            ventureCompletes = ventureDone,
                            ventureReady = ventureDone is { } done && done <= now,
                            inventoryCachedAt = r.InventoryCapturedUtc,
                        };
                    }).ToList(),
                }).ToList();
            }),
        };
    }

    private static float Vector3Distance(IGameObject a, IGameObject b) => System.Numerics.Vector3.Distance(a.Position, b.Position);
}
