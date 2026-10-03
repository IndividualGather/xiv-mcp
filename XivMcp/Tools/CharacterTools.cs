using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Player;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

internal static class CharacterTools
{
    public static IEnumerable<McpTool> Create()
    {
        yield return new McpTool
        {
            Name = "get_game_status",
            Description = "Overall client state: whether a character is logged in, character name/world, current zone, map, instance, " +
                          "active duty, PvP/GPose state, client language and all active condition flags (in combat, mounted, crafting, bound by duty, ...).",
            Handler = (_, _) => Game.Run<object?>(() =>
            {
                var cs = Svc.ClientState;
                var territory = Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(cs.TerritoryType);
                return new
                {
                    loggedIn = cs.IsLoggedIn,
                    character = Svc.PlayerState.IsLoaded ? Svc.PlayerState.CharacterName : null,
                    world = Svc.PlayerState.IsLoaded ? Excel.Ref(Svc.PlayerState.CurrentWorld) : null,
                    clientLanguage = cs.ClientLanguage.ToString(),
                    territory = territory is { } t
                        ? new
                        {
                            id = t.RowId,
                            name = Excel.Name(t.PlaceName),
                            region = Excel.Name(t.PlaceNameRegion),
                            zone = Excel.Name(t.PlaceNameZone),
                            intendedUse = t.TerritoryIntendedUse.RowId,
                            contentFinderCondition = t.ContentFinderCondition.RowId != 0 ? Excel.Ref(t.ContentFinderCondition) : null,
                        }
                        : null,
                    mapId = cs.MapId,
                    instance = cs.Instance,
                    duty = new
                    {
                        started = Svc.DutyState.IsDutyStarted,
                        contentFinderCondition = Svc.DutyState.ContentFinderCondition.RowId != 0 ? Excel.Ref(Svc.DutyState.ContentFinderCondition) : null,
                    },
                    isPvP = cs.IsPvP,
                    isGPosing = cs.IsGPosing,
                    isClientIdle = cs.IsClientIdle(),
                    conditions = Svc.Condition.AsReadOnlySet().Select(f => f.ToString()).OrderBy(s => s).ToList(),
                    eorzeaTime = EorzeaTime(),
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_character",
            Description = "Detailed profile of the logged-in character: name, worlds, race/clan/gender, nameday, guardian, starting city, grand company and rank, " +
                          "current job and level, HP/MP/GP/CP, position, online status, mount/minion, active status effects, home & favourite aetherytes, " +
                          "mentor/novice/returner flags, commendations, rested EXP and all character attributes (main stats, substats, defenses).",
            Handler = (_, _) => Game.RunLoggedIn<object?>(() =>
            {
                var ps = Svc.PlayerState;
                var lp = Svc.Objects.LocalPlayer!;
                var gcRanks = Svc.Data.GetExcelSheet<GrandCompany>()
                    .Where(gc => gc.RowId != 0)
                    .ToDictionary(gc => gc.Name.ExtractText(), gc => (int)ps.GetGrandCompanyRank(gc));

                var attributes = new Dictionary<string, object?>();
                foreach (var attr in Enum.GetValues<PlayerAttribute>())
                {
                    try { attributes[attr.ToString()] = ps.GetAttribute(attr); }
                    catch { /* unsupported attribute */ }
                }

                return new
                {
                    name = ps.CharacterName,
                    contentId = ps.ContentId,
                    entityId = ps.EntityId,
                    homeWorld = Excel.Ref(ps.HomeWorld),
                    currentWorld = Excel.Ref(ps.CurrentWorld),
                    sex = ps.Sex.ToString(),
                    race = Excel.Ref(ps.Race),
                    tribe = Excel.Ref(ps.Tribe),
                    nameday = new { month = ps.BirthMonth, day = ps.BirthDay },
                    guardianDeity = Excel.Ref(ps.GuardianDeity),
                    startTown = Excel.Ref(ps.StartTown),
                    firstClass = Excel.Ref(ps.FirstClass),
                    classJob = Excel.Ref(ps.ClassJob),
                    level = ps.Level,
                    effectiveLevel = ps.EffectiveLevel,
                    isLevelSynced = ps.IsLevelSynced,
                    hp = new { current = lp.CurrentHp, max = lp.MaxHp },
                    mp = new { current = lp.CurrentMp, max = lp.MaxMp },
                    gp = new { current = lp.CurrentGp, max = lp.MaxGp },
                    cp = new { current = lp.CurrentCp, max = lp.MaxCp },
                    shieldPercent = lp.ShieldPercentage,
                    position = Game.Pos(lp.Position),
                    rotation = MathF.Round(lp.Rotation, 3),
                    companyTag = lp.CompanyTag.TextValue,
                    onlineStatus = Excel.Ref(lp.OnlineStatus),
                    mount = lp.CurrentMount is { } mount ? Excel.Ref(mount) : null,
                    minion = lp.CurrentMinion is { } minion ? Excel.Ref(minion) : null,
                    statuses = Game.Statuses(lp.StatusList),
                    grandCompany = Excel.Ref(ps.GrandCompany),
                    grandCompanyRanks = gcRanks,
                    homeAetheryte = Excel.Ref(ps.HomeAetheryte),
                    favoriteAetherytes = ps.FavoriteAetherytes.Where(a => a.RowId != 0).Select(a => Excel.Ref(a)).ToList(),
                    freeAetheryte = Excel.Ref(ps.FreeAetheryte),
                    restedExperience = ps.BaseRestedExperience,
                    playerCommendations = ps.PlayerCommendations,
                    deliveryLevel = ps.DeliveryLevel,
                    flags = new
                    {
                        isMentor = ps.IsMentor,
                        isBattleMentor = ps.IsBattleMentor,
                        isTradeMentor = ps.IsTradeMentor,
                        isNovice = ps.IsNovice,
                        isReturner = ps.IsReturner,
                        isAfk = ps.IsAwayFromKeyboard,
                    },
                    baseStats = new
                    {
                        strength = ps.BaseStrength,
                        dexterity = ps.BaseDexterity,
                        vitality = ps.BaseVitality,
                        intelligence = ps.BaseIntelligence,
                        mind = ps.BaseMind,
                        piety = ps.BasePiety,
                    },
                    attributes,
                };
            }),
        };

        yield return new McpTool
        {
            Name = "get_class_jobs",
            Description = "Level, current EXP and EXP needed for the next level for every class and job of the logged-in character, " +
                          "plus whether it is unlocked and its desynthesis skill (for crafters). Set include_locked=false to hide classes at level 0.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "include_locked": { "type": "boolean", "description": "Include classes/jobs that are not unlocked yet (default true)." }
                  }
                }
                """,
            Handler = (args, _) => Game.RunLoggedIn<object?>(() =>
            {
                var includeLocked = args.Bool("include_locked", true);
                var paramGrow = Svc.Data.GetExcelSheet<ParamGrow>();
                var ps = Svc.PlayerState;
                var result = new List<object>();
                foreach (var cj in Svc.Data.GetExcelSheet<ClassJob>())
                {
                    if (cj.RowId == 0 || cj.ExpArrayIndex < 0) continue;
                    var level = ps.GetClassJobLevel(cj);
                    var unlocked = Svc.Unlocks.IsClassJobUnlocked(cj);
                    if (!includeLocked && level <= 0) continue;
                    var expToNext = paramGrow.GetRowOrDefault((uint)Math.Max((int)level, 1))?.ExpToNext ?? 0;
                    result.Add(new
                    {
                        id = cj.RowId,
                        name = Game.TitleCase(cj.Name.ExtractText()),
                        abbreviation = cj.Abbreviation.ExtractText(),
                        role = cj.Role,
                        isJob = cj.JobIndex > 0,
                        parentClass = cj.ClassJobParent.RowId != cj.RowId ? Excel.Name(cj.ClassJobParent) : null,
                        unlocked,
                        level,
                        exp = ps.GetClassJobExperience(cj),
                        expToNextLevel = level > 0 ? expToNext : 0,
                        desynthesisLevel = cj.DohDolJobIndex >= 0 && cj.ClassJobCategory.RowId == 33 ? ps.GetDesynthesisLevel(cj) : (float?)null,
                    });
                }
                return new { currentClassJob = Excel.Ref(ps.ClassJob), classJobs = result };
            }),
        };
    }

    /// <summary>Eorzea time runs 3600/175 times faster than real time.</summary>
    private static string EorzeaTime()
    {
        var et = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 3600 / 175 / 1000;
        return $"{et / 3600 % 24:00}:{et / 60 % 60:00}";
    }
}
