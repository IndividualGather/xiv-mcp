using System;
using System.Numerics;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Movement through vnavmesh (pathfinding inside a zone) and Lifestream (teleports, housing, inns, workshop) — the same public
/// IPC that AutoDuty, AutoRetainer and GatherBuddy Reborn use. Both are optional; each feature checks what is installed.
/// </summary>
internal static class Navigation
{
    public const string Vnavmesh = "vnavmesh";
    public const string Lifestream = "Lifestream";

    public static bool VnavmeshLoaded => PluginCompat.IsLoaded(Vnavmesh);
    public static bool LifestreamLoaded => PluginCompat.IsLoaded(Lifestream);

    // Mirrors of Lifestream's enums (Dalamud converts between same-named enum values across plugins, as ECommons does).
    public enum PropertyType { Auto, Home, FC, Apartment, Inn, Shared_Estate }
    public enum HouseEnterMode { None, Walk_to_door, Enter_house, Enter_workshop }

    // ------------------------------------------------------------------ vnavmesh

    public static bool NavReady => Func<bool>("vnavmesh.Nav.IsReady");
    public static float NavBuildProgress => Svc.PluginInterface.GetIpcSubscriber<float>("vnavmesh.Nav.BuildProgress").InvokeFunc();
    public static bool PathRunning => Func<bool>("vnavmesh.Path.IsRunning") || Func<bool>("vnavmesh.SimpleMove.PathfindInProgress");

    /// <summary>Pathfinds and walks (or flies, when mounted where flying is allowed) to within <paramref name="range"/> of a point.</summary>
    public static bool MoveCloseTo(Vector3 target, float range, bool fly = false) =>
        Svc.PluginInterface.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo").InvokeFunc(target, fly, range);

    public static void StopMoving()
    {
        if (!VnavmeshLoaded) return;
        Try(() => Svc.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop").InvokeAction());
        Try(() => Svc.PluginInterface.GetIpcSubscriber<object>("vnavmesh.Nav.PathfindCancelAll").InvokeAction());
    }

    // ------------------------------------------------------------------ Lifestream

    public static bool LifestreamBusy => LifestreamLoaded && Func<bool>("Lifestream.IsBusy");

    public static void LifestreamAbort()
    {
        if (LifestreamLoaded) Try(() => Svc.PluginInterface.GetIpcSubscriber<object>("Lifestream.Abort").InvokeAction());
    }

    /// <summary>Teleports to a property (Auto = Lifestream's configured property priority) and walks in.</summary>
    public static void GoToProperty(PropertyType type, HouseEnterMode mode) =>
        Svc.PluginInterface.GetIpcSubscriber<PropertyType, HouseEnterMode?, object>("Lifestream.EnqueuePropertyShortcut").InvokeAction(type, mode);

    /// <summary>Goes to an inn room (null = Lifestream's preferred inn).</summary>
    public static void GoToInn() =>
        Svc.PluginInterface.GetIpcSubscriber<int?, object>("Lifestream.EnqueueInnShortcut").InvokeAction(null);

    public static bool CanMoveToWorkshop => LifestreamLoaded && Func<bool>("Lifestream.CanMoveToWorkshop");

    public static void MoveToWorkshop() => Svc.PluginInterface.GetIpcSubscriber<object>("Lifestream.MoveToWorkshop").InvokeAction();

    public static bool? HasFcHouse => NullableFunc("Lifestream.HasFreeCompanyHouse");
    public static bool? HasHouse => NullableFunc("Lifestream.HasPrivateHouse");
    public static bool? HasApartment => NullableFunc("Lifestream.HasApartment");

    // ------------------------------------------------------------------ helpers

    private static T Func<T>(string name)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T>(name).InvokeFunc(); }
        catch (Exception ex) { throw new ToolException($"{name.Split('.')[0]} did not answer ({ex.GetType().Name}). Is it enabled and up to date?"); }
    }

    private static bool? NullableFunc(string name)
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<bool?>(name).InvokeFunc(); }
        catch { return null; }
    }

    private static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { Svc.Log.Debug($"Navigation IPC failed: {ex.Message}"); }
    }
}
