using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using XivMcp.Mcp;

namespace XivMcp.Util;

internal static class Game
{
    /// <summary>Runs on the game's framework thread — required for anything that reads game memory.</summary>
    public static Task<T> Run<T>(Func<T> func) => Svc.Framework.RunOnFrameworkThread(func);

    /// <summary>Runs on the framework thread after verifying that a character is logged in.</summary>
    public static Task<T> RunLoggedIn<T>(Func<T> func) => Svc.Framework.RunOnFrameworkThread(() =>
    {
        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer is null)
            throw new ToolException("No character is currently logged in.");
        return func();
    });

    public static object Pos(Vector3 v) => new { x = MathF.Round(v.X, 2), y = MathF.Round(v.Y, 2), z = MathF.Round(v.Z, 2) };

    public static float? DistanceToPlayer(Vector3 v) =>
        Svc.Objects.LocalPlayer is { } p ? MathF.Round(Vector3.Distance(p.Position, v), 2) : null;

    public static IEnumerable<object> Statuses(IEnumerable<IStatus>? statuses) =>
        statuses?.Where(s => s.StatusId != 0).Select(s => (object)new
        {
            id = s.StatusId,
            name = Excel.Name(s.GameData),
            stacksOrParam = s.Param,
            remainingSeconds = MathF.Round(s.RemainingTime, 1),
            sourceId = s.SourceId,
        }).ToList() ?? [];

    /// <summary>Common summary for any game object.</summary>
    public static Dictionary<string, object?> Describe(IGameObject obj, bool detailed)
    {
        var d = new Dictionary<string, object?>
        {
            ["name"] = obj.Name.TextValue,
            ["kind"] = obj.ObjectKind.ToString(),
            ["gameObjectId"] = obj.GameObjectId,
            ["entityId"] = obj.EntityId,
            ["baseId"] = obj.BaseId,
            ["distance"] = DistanceToPlayer(obj.Position),
            ["position"] = Pos(obj.Position),
            ["targetable"] = obj.IsTargetable,
            ["dead"] = obj.IsDead,
        };

        if (obj is ICharacter c)
        {
            d["level"] = c.Level;
            d["classJob"] = Excel.Ref(c.ClassJob);
            d["hp"] = new { current = c.CurrentHp, max = c.MaxHp };
            if (detailed)
            {
                d["mp"] = new { current = c.CurrentMp, max = c.MaxMp };
                d["companyTag"] = c.CompanyTag.TextValue is { Length: > 0 } tag ? tag : null;
                d["onlineStatus"] = Excel.Ref(c.OnlineStatus);
                d["mount"] = c.CurrentMount is { } mount ? Excel.Ref(mount) : null;
                d["minion"] = c.CurrentMinion is { } minion ? Excel.Ref(minion) : null;
            }
        }

        if (obj is Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter pc)
        {
            d["homeWorld"] = Excel.Ref(pc.HomeWorld);
            d["currentWorld"] = Excel.Ref(pc.CurrentWorld);
        }

        if (obj is IBattleChara b)
        {
            if (b.IsCasting)
                d["casting"] = new
                {
                    actionId = b.CastActionId,
                    actionType = b.CastActionType.ToString(),
                    action = Excel.Ref<Lumina.Excel.Sheets.Action>(b.CastActionId),
                    current = MathF.Round(b.CurrentCastTime, 2),
                    total = MathF.Round(b.TotalCastTime, 2),
                    interruptible = b.IsCastInterruptible,
                    targetId = b.CastTargetObjectId,
                };
            if (detailed) d["statuses"] = Statuses(b.StatusList);
        }

        if (detailed && obj.TargetObject is { } target)
            d["target"] = new { name = target.Name.TextValue, gameObjectId = target.GameObjectId };

        return d;
    }

    public static bool Matches(string? haystack, string? needle) =>
        needle is null || (haystack?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);
}
