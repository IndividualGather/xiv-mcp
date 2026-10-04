using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using Lumina.Excel.Sheets;
using XivMcp.Mcp;
using XivMcp.Util;
using static XivMcp.Util.Navigation;

namespace XivMcp.Tools;

/// <summary>
/// Navigating the character to a summoning bell, the FC chest, the workshop, the inn or a property. Walking uses vnavmesh and
/// travel uses Lifestream when they are installed; without them the player is shown the way and XIV MCP waits (NavigationTools.Guided).
/// </summary>
internal static partial class NavigationTools
{
    private const float ArriveRange = 2.5f;
    private const float LocalSearchRange = 120f;

    /// <summary>The one running navigation (so stop_navigation and status can see it).</summary>
    private static CancellationTokenSource? running;
    private static string phase = "idle";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private static readonly string[] Destinations = ["summoning_bell", "company_chest", "workshop", "inn", "home", "fc_house", "apartment", "object", "npc", "fishing_spot"];

    public static IEnumerable<McpTool> Create(Configuration config)
    {
        yield return new McpTool
        {
            Name = "navigate_to",
            Description = "Gets the character to a destination. Walking and travel are automatic when the player has navigation plugins; " +
                          "otherwise XIV MCP shows the player the way and waits: it puts the flag on the map and asks them to walk, names the " +
                          "aetheryte to teleport to, or asks them to enter the inn room or house. Tell the player when that happens. Destinations: " +
                          "summoning_bell (nearby one, otherwise the preferred bell location from /xivmcp, falling back to the inn), company_chest (FC " +
                          "house), workshop (FC workshop, walks to the voyage control panel), inn, home, fc_house, apartment, object (by name in the " +
                          "current zone), npc (an NPC anywhere in the world by name, from the game data: travels to its zone and walks up to it), or fishing_spot (a fishing spot by name or id, as find_fish names it: travels there and walks to the water until Cast can be used, as a Fisher). Waits until arrived (or the timeout) and returns the steps taken; then use interact_with_object. " +
                          "stop_navigation aborts. Teleports cost gil as usual. Requires 'Game & navigation' in /xivmcp.",
            InputSchema = $$"""
                {
                  "type": "object",
                  "properties": {
                    "destination": { "type": "string", "enum": [{{string.Join(", ", Destinations.Select(d => $"\"{d}\""))}}] },
                    "name": { "type": "string", "description": "For destination=object: the object's name (e.g. \"Material Supplier\"). For destination=npc: the NPC's name (e.g. \"Masked Rose\"). For destination=fishing_spot: the spot's name or id." },
                    "timeout_seconds": { "type": "integer", "description": "Give up after this long (default 600, max 1800). The player may need a while when they walk or teleport themselves." }
                  },
                  "required": ["destination"]
                }
                """,
            ReadOnly = false,
            Handler = async (args, ct) =>
            {
                if (!config.AllowGameNavigation)
                    throw new ToolException("Navigation is disabled. Enable \"Game & navigation\" in the XIV MCP settings window (/xivmcp) in game.");
                var destination = args.String("destination")?.ToLowerInvariant() ?? throw new ToolException("'destination' is required.");
                if (!Destinations.Contains(destination)) throw new ToolException($"Unknown destination '{destination}'.");
                var name = args.String("name");
                if (destination is "object" or "npc" or "fishing_spot" && name is null) throw new ToolException($"destination={destination} needs 'name'.");

                if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false)) throw new ToolException("A navigation is already running; use stop_navigation first.");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(args.Int("timeout_seconds", 600, 10, 1800)));
                running = cts;
                var steps = new List<string>();
                try
                {
                    await Game.RunLoggedIn(() => { EnsureCanTravel(); return true; }).ConfigureAwait(false);
                    var result = await Navigate(destination, name, config, steps, cts.Token).ConfigureAwait(false);
                    return new { arrived = true, destination, at = result, steps };
                }
                catch (OperationCanceledException)
                {
                    StopAll();
                    return new { arrived = false, destination, reason = ct.IsCancellationRequested ? "stopped" : "timed out", steps };
                }
                catch (ToolException ex)
                {
                    StopAll();
                    return new { arrived = false, destination, reason = ex.Message, steps };
                }
                finally
                {
                    running = null;
                    phase = "idle";
                    Gate.Release();
                }
            },
        };

        yield return new McpTool
        {
            Name = "get_navigation_status",
            Description = "Whether walking (pathfinding) and travel (teleports, housing) are automatic or done by the player following XIV MCP's " +
                          "directions, whether something is moving the character right now, and the phase of a running navigate_to (for example " +
                          "\"waiting for you to walk to Summoning Bell\"). Only the navigation plugins the player has installed are listed.",
            Handler = (_, _) => Game.Run<object?>(() =>
            {
                // Plugins that aren't installed are left out, so the assistant doesn't learn about (and suggest) them.
                var plugins = new Dictionary<string, object?>();
                if (VnavmeshLoaded) plugins["vnavmesh"] = new { meshReady = SafeBool(() => NavReady), moving = SafeBool(() => PathRunning) };
                if (LifestreamLoaded) plugins["Lifestream"] = new { busy = SafeBool(() => LifestreamBusy) };
                return new
                {
                    walking = VnavmeshLoaded ? "automatic" : "the player walks to the map flag",
                    travel = LifestreamLoaded ? "automatic" : "the player teleports or enters the place",
                    plugins,
                    navigation = running is null ? "idle" : phase,
                    preferredBellLocation = config.PreferredBellLocation,
                };
            }),
        };

        yield return new McpTool
        {
            Name = "stop_navigation",
            Description = "Stops a running navigate_to, and any walking or travel the navigation plugins are doing, immediately.",
            ReadOnly = false,
            Handler = (_, _) =>
            {
                running?.Cancel();
                StopAll();
                return Task.FromResult<object?>(new { stopped = true });
            },
        };
    }

    // ------------------------------------------------------------------ flow

    private static async Task<object> Navigate(string destination, string? name, Configuration config, List<string> steps, CancellationToken ct)
    {
        switch (destination)
        {
            case "fishing_spot":
            {
                if (config.AllowOnlineData && FishingSources.Cached is null)
                    try { await FishingSources.Get(ct).ConfigureAwait(false); } catch (ToolException) { /* the sheet's position is used */ }
                var spotId = await Task.Run(() => FishingTools.ResolveSpotName(name!), ct).ConfigureAwait(false);
                var place = await Game.Run(() => FindFishingSpot(spotId)).ConfigureAwait(false)
                            ?? throw new ToolException($"Fishing spot '{name}' has no place in the game data.");
                return await GoToFishingSpot(place, steps, ct).ConfigureAwait(false);
            }

            case "npc":
            {
                var territory = await Game.Run(() => (uint)Svc.ClientState.TerritoryType).ConfigureAwait(false);
                var spot = await Task.Run(() => TriadData.FindNpc(name!, territory), ct).ConfigureAwait(false)
                           ?? throw new ToolException($"No NPC named '{name}' has a place in the game data.");
                return await GoToNpc(spot, steps, ct).ConfigureAwait(false);
            }

            case "object":
                return await WalkTo(name!, o => o.Name.TextValue.Equals(name, StringComparison.OrdinalIgnoreCase) || Game.Matches(o.Name.TextValue, name), steps, ct)
                       ?? throw new ToolException($"No object named '{name}' in this zone.");

            case "summoning_bell":
            {
                if (await WalkTo("Summoning Bell", InteractionTools.IsSummoningBell, steps, ct) is { } here) return here;
                var preference = config.PreferredBellLocation;
                await TravelTo(preference, steps, ct).ConfigureAwait(false);
                if (await WalkTo("Summoning Bell", InteractionTools.IsSummoningBell, steps, ct) is { } bell) return bell;
                if (preference != "inn")
                {
                    steps.Add("No summoning bell at that property; going to the inn instead.");
                    await TravelTo("inn", steps, ct).ConfigureAwait(false);
                    if (await WalkTo("Summoning Bell", InteractionTools.IsSummoningBell, steps, ct) is { } innBell) return innBell;
                }
                throw new ToolException("No summoning bell found at the destination.");
            }

            case "company_chest":
            {
                if (await WalkTo("Company Chest", IsObject(CompanyChestIds), steps, ct) is { } here) return here;
                await TravelTo("fc", steps, ct).ConfigureAwait(false);
                return await WalkTo("Company Chest", IsObject(CompanyChestIds), steps, ct)
                       ?? throw new ToolException("Arrived at the FC house, but no company chest was found.");
            }

            case "workshop":
            {
                if (await WalkTo("Voyage Control Panel", IsObject(VoyagePanelIds), steps, ct) is { } here) return here;
                await TravelTo("workshop", steps, ct).ConfigureAwait(false);
                return await WalkTo("Voyage Control Panel", IsObject(VoyagePanelIds), steps, ct)
                       ?? throw new ToolException("Arrived at the workshop, but no voyage control panel was found.");
            }

            case "fc_house": await TravelTo("fc", steps, ct).ConfigureAwait(false); return await Where().ConfigureAwait(false);
            default: await TravelTo(destination, steps, ct).ConfigureAwait(false); return await Where().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Travel to "lifestream" (Lifestream's property priority), "inn", "fc", "home", "apartment" or "workshop": with Lifestream, or by
    /// asking the player to go there and waiting.
    /// </summary>
    private static async Task TravelTo(string where, List<string> steps, CancellationToken ct)
    {
        if (!LifestreamLoaded)
        {
            await GuidePlace(where, steps, ct).ConfigureAwait(false);
            return;
        }
        await Game.RunLoggedIn(() =>
        {
            EnsureCanTravel();
            switch (where)
            {
                case "inn": GoToInn(); break;
                case "fc": EnsureOwns(HasFcHouse, "a free company house"); GoToProperty(PropertyType.FC, HouseEnterMode.Enter_house); break;
                case "workshop": EnsureOwns(HasFcHouse, "a free company house"); GoToProperty(PropertyType.FC, HouseEnterMode.Enter_workshop); break;
                case "home": EnsureOwns(HasHouse, "a private house"); GoToProperty(PropertyType.Home, HouseEnterMode.Enter_house); break;
                case "apartment": EnsureOwns(HasApartment, "an apartment"); GoToProperty(PropertyType.Apartment, HouseEnterMode.Enter_house); break;
                default: GoToProperty(PropertyType.Auto, HouseEnterMode.Enter_house); break; // Lifestream's own property priority
            }
            return true;
        }).ConfigureAwait(false);
        steps.Add($"Lifestream: travelling to {(where == "lifestream" ? "your preferred property (Lifestream's priority list)" : where)}.");
        phase = $"travelling to {where} (Lifestream)";

        await WaitUntilSettled(ct).ConfigureAwait(false);

        if (where == "workshop" && !await InWorkshop().ConfigureAwait(false) && await Game.Run(() => CanMoveToWorkshop).ConfigureAwait(false))
        {
            await Game.Run(() => { MoveToWorkshop(); return true; }).ConfigureAwait(false);
            steps.Add("Lifestream: moving into the workshop.");
            await WaitUntilSettled(ct).ConfigureAwait(false);
        }
        steps.Add($"Arrived in {await Game.Run(ZoneName).ConfigureAwait(false)}.");
    }

    /// <summary>Waits for Lifestream to finish and the zone to be loaded and stable.</summary>
    internal static async Task WaitUntilSettled(CancellationToken ct)
    {
        await Task.Delay(1500, ct).ConfigureAwait(false);
        var stableSince = DateTime.MaxValue;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var settled = await Game.Run(() => !LifestreamBusy && Svc.Objects.LocalPlayer is not null &&
                                               !Svc.Condition[ConditionFlag.BetweenAreas] && !Svc.Condition[ConditionFlag.BetweenAreas51] &&
                                               !Svc.Condition[ConditionFlag.Casting]).ConfigureAwait(false);
            if (!settled) stableSince = DateTime.MaxValue;
            else if (stableSince == DateTime.MaxValue) stableSince = DateTime.UtcNow;
            else if (DateTime.UtcNow - stableSince > TimeSpan.FromSeconds(2)) return;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Finds the nearest matching object in the zone and walks to it with vnavmesh. Null if there is none.</summary>
    internal static async Task<object?> WalkTo(string label, Func<IGameObject, bool> match, List<string> steps, CancellationToken ct)
    {
        var target = await Game.RunLoggedIn(() =>
        {
            var self = Svc.Objects.LocalPlayer!;
            return Svc.Objects.Where(o => o.IsTargetable && o.Address != self.Address && match(o))
                .Select(o => (o.Name.TextValue, o.Position, Distance: Vector3.Distance(o.Position, self.Position)))
                .Where(x => x.Distance <= LocalSearchRange)
                .OrderBy(x => x.Distance)
                .Select(x => ((string Name, Vector3 Position, float Distance)?)x)
                .FirstOrDefault();
        }).ConfigureAwait(false);
        if (target is not { } t) return null;

        if (t.Distance > ArriveRange + 0.5f && !VnavmeshLoaded)
        {
            // No pathfinding: flag it and wait until the player is next to it.
            var territory = await Game.Run(() => Svc.ClientState.TerritoryType).ConfigureAwait(false);
            await GuideWalk(territory, t.Position, t.Name, ArriveRange + 1,
                () => Svc.Objects.Any(o => match(o) && Game.DistanceToPlayer(o.Position) <= ArriveRange + 1), steps, ct).ConfigureAwait(false);
        }
        else if (t.Distance > ArriveRange + 0.5f)
        {
            phase = $"waiting for the navmesh ({label})";
            var waited = DateTime.UtcNow;
            while (!await Game.Run(() => NavReady).ConfigureAwait(false))
            {
                if (DateTime.UtcNow - waited > TimeSpan.FromSeconds(90)) throw new ToolException("vnavmesh did not finish building the navmesh for this zone.");
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            if (!await StartPath(t.Position, ArriveRange, steps, ct).ConfigureAwait(false))
                throw new ToolException($"vnavmesh could not start a path to {t.Name}.");
            steps.Add($"vnavmesh: walking to {t.Name} ({t.Distance:0.#} yalms).");
            phase = $"walking to {t.Name}";
            await Task.Delay(500, ct).ConfigureAwait(false);
            while (await Game.Run(() => PathRunning).ConfigureAwait(false))
                await Task.Delay(250, ct).ConfigureAwait(false);
        }

        await GetOffMount(steps, ct).ConfigureAwait(false);
        var final = await Game.Run(() => Svc.Objects.LocalPlayer is { } p ? Vector3.Distance(p.Position, t.Position) : float.MaxValue).ConfigureAwait(false);
        if (final > ArriveRange + 2) throw new ToolException($"Stopped {final:0.#} yalms from {t.Name}; the path may be blocked.");
        steps.Add($"At {t.Name} ({final:0.#} yalms).");
        return new { name = t.Name, distance = MathF.Round(final, 1), zone = await Game.Run(ZoneName).ConfigureAwait(false), next = $"interact_with_object \"{t.Name}\"" };
    }

    // ------------------------------------------------------------------ helpers

    internal static void EnsureCanTravel()
    {
        InventoryActionTools.EnsureNotBusy();
        if (Svc.Condition[ConditionFlag.BoundByDuty] || Svc.Condition[ConditionFlag.BoundByDuty56] || Svc.Condition[ConditionFlag.BoundByDuty95])
            throw new ToolException("Can't navigate while in a duty.");
    }

    private static void EnsureOwns(bool? owns, string what)
    {
        if (owns == false) throw new ToolException($"This character doesn't have {what} (according to Lifestream).");
    }

    internal static void StopAll()
    {
        Svc.Framework.RunOnFrameworkThread(() => { StopMoving(); LifestreamAbort(); });
    }

    private static Task<object> Where() => Game.Run<object>(() => new { zone = ZoneName() });

    private static string ZoneName() =>
        Excel.Name(Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(Svc.ClientState.TerritoryType)?.PlaceName ?? default) ?? Svc.ClientState.TerritoryType.ToString();

    /// <summary>In the FC workshop: a housing interior (intended use 14, shared with houses and apartments) with a voyage control panel.</summary>
    private static Task<bool> InWorkshop() =>
        Game.Run(() => Svc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(Svc.ClientState.TerritoryType)?.TerritoryIntendedUse.RowId == XivMcp.Maps.PlayerGuidance.HousingInteriorUse
                       && Svc.Objects.Any(o => VoyagePanelIds.Value.Contains(o.BaseId)));

    private static bool SafeBool(Func<bool> f)
    {
        try { return f(); }
        catch { return false; }
    }

    private static Func<IGameObject, bool> IsObject(Lazy<HashSet<uint>> ids) => o => ids.Value.Contains(o.BaseId);

    /// <summary>All event objects sharing a reference object's name (so every chest/panel variant is found, in any client language).</summary>
    private static Lazy<HashSet<uint>> SameNameAs(uint referenceId) => new(() =>
    {
        var names = Svc.Data.GetExcelSheet<EObjName>();
        var reference = names.GetRowOrDefault(referenceId)?.Singular.ExtractText();
        return reference is null ? [referenceId] : names.Where(e => e.Singular.ExtractText().Equals(reference, StringComparison.OrdinalIgnoreCase)).Select(e => e.RowId).ToHashSet();
    });

    private static readonly Lazy<HashSet<uint>> CompanyChestIds = SameNameAs(2000470);
    internal static readonly Lazy<HashSet<uint>> VoyagePanelIds = SameNameAs(2011587);
}
