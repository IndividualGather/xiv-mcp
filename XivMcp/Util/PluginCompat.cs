using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Cooperation with automation plugins that react to the same windows we drive:
/// <list type="bullet">
/// <item>AutoRetainer starts processing ventures when a summoning bell is opened (default "OpenBellBehavior"). While XIV MCP uses
/// the bell it is suppressed through its public IPC (AutoRetainer.SetSuppressed) and released again when the bell session ends.</item>
/// <item>YesAlready / TextAdvance auto-click dialogs and menus. They are paused through their shared "StopRequests" sets
/// (the same mechanism AutoRetainer uses) while XIV MCP drives game windows.</item>
/// </list>
/// </summary>
internal sealed class PluginCompat : IDisposable
{
    private const string AutoRetainer = "AutoRetainer";
    private static readonly string[] StopRequestTags = ["YesAlready.StopRequests", "TextAdvance.StopRequests"];

    private bool holdingBell;
    private bool suppressedAutoRetainer;
    private bool pausedClickers;
    private DateTime bellLeftSince = DateTime.MaxValue;

    public PluginCompat() => Svc.Framework.Update += OnUpdate;

    public void Dispose()
    {
        Svc.Framework.Update -= OnUpdate;
        ReleaseBell("plugin unloading");
        lock (YesAlreadyHolders)
            foreach (var holder in YesAlreadyHolders.ToList()) SetYesAlreadyStop(holder, false);
        foreach (var tag in StopRequestTags) Svc.PluginInterface.RelinquishData(tag);
    }

    private const string YesAlreadyStopRequests = "YesAlready.StopRequests";
    private static readonly HashSet<string> YesAlreadyHolders = [];

    /// <summary>
    /// Pauses YesAlready alone (TextAdvance keeps clicking through dialogue, which Questionable relies on) until disposed. YesAlready's
    /// "Custom Deliveries" option turns in the moment a delivery window is loaded, even while it is still hidden behind a client's
    /// dialogue; the conversation then runs ahead without the window, Satisfier waits for a window that never shows, and acting on
    /// the hidden window crashes the game.
    /// </summary>
    public static IDisposable PauseYesAlready(string purpose)
    {
        var holder = $"{Svc.PluginInterface.InternalName}:{purpose}";
        lock (YesAlreadyHolders) SetYesAlreadyStop(holder, true);
        return new Releaser(() => { lock (YesAlreadyHolders) SetYesAlreadyStop(holder, false); });
    }

    /// <summary>Whether YesAlready is installed and currently paused by anyone (XIV MCP or another plugin).</summary>
    public static bool YesAlreadyPaused =>
        Svc.PluginInterface.TryGetData<HashSet<string>>(YesAlreadyStopRequests, out var set) && set.Count > 0;

    private static void SetYesAlreadyStop(string holder, bool stop)
    {
        if (stop) YesAlreadyHolders.Add(holder); else YesAlreadyHolders.Remove(holder);
        if (!Svc.PluginInterface.TryGetData<HashSet<string>>(YesAlreadyStopRequests, out var set)) return;
        lock (set) { if (stop) set.Add(holder); else set.Remove(holder); }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }

    public static bool IsLoaded(string internalName) =>
        Svc.PluginInterface.InstalledPlugins.Any(p => p.InternalName.Equals(internalName, StringComparison.OrdinalIgnoreCase) && p.IsLoaded);

    public bool AutoRetainerLoaded => IsLoaded(AutoRetainer);

    public const string FcchName = "FCCH";
    public static bool FcchLoaded => IsLoaded(FcchName);

    /// <summary>True while FCCH (FC chest automation) is running a deposit/withdraw or otherwise can't take a command.</summary>
    public static bool FcchBusy => FcchLoaded && Ipc<bool>("FCCH.IsBusy") == true;

    /// <summary>Call before XIV MCP moves items: two plugins sending item moves at the same time confuse the server and each other.</summary>
    public static void EnsureFcchIdle()
    {
        if (FcchBusy)
            throw new ToolException("FCCH is currently moving items (free company chest). Wait until it is done, or stop it, then retry.");
    }

    /// <summary>
    /// Waits until FCCH has finished with the company chest. FCCH may start working on its own as soon as the chest opens (its
    /// automatic deposits), so this first gives it a moment to start, then waits while it is busy (at most <paramref name="timeout"/>).
    /// Returns at once without FCCH.
    /// </summary>
    public static async System.Threading.Tasks.Task WaitForFcch(System.TimeSpan timeout, System.Threading.CancellationToken ct)
    {
        if (!await Game.Run(() => FcchLoaded).ConfigureAwait(false)) return;
        var deadline = System.DateTime.UtcNow + timeout;
        var quietSince = System.DateTime.UtcNow;
        while (System.DateTime.UtcNow - quietSince < System.TimeSpan.FromSeconds(2))
        {
            if (await Game.Run(() => FcchBusy).ConfigureAwait(false)) quietSince = System.DateTime.UtcNow;
            if (System.DateTime.UtcNow > deadline)
                throw new ToolException("FCCH is still moving items in the company chest. Wait until it is done, or stop it, then retry.");
            await System.Threading.Tasks.Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Call before XIV MCP touches the summoning bell or retainer windows. Throws if AutoRetainer is working.</summary>
    public void AcquireBell()
    {
        if (AutoRetainerLoaded)
        {
            if (Ipc<bool>("AutoRetainer.PluginState.IsBusy") == true && !suppressedAutoRetainer)
                throw new ToolException("AutoRetainer is currently processing retainers. Wait until it is done (or stop it), then retry.");
            if (Ipc<bool>("AutoRetainer.GetMultiModeEnabled") == true)
                throw new ToolException("AutoRetainer multi mode is enabled and controls the character; disable multi mode first.");

            if (!suppressedAutoRetainer && Ipc<bool>("AutoRetainer.GetSuppressed") == false)
            {
                IpcAction("AutoRetainer.SetSuppressed", true);
                suppressedAutoRetainer = true;
                Svc.Log.Information("[MCP] AutoRetainer suppressed while XIV MCP uses the summoning bell");
            }
        }
        PauseClickers();
        holdingBell = true;
        bellLeftSince = DateTime.MaxValue;
    }

    /// <summary>Pauses YesAlready/TextAdvance (if installed) while windows are being driven.</summary>
    public void PauseClickers()
    {
        if (pausedClickers) return;
        foreach (var tag in StopRequestTags)
            if (Svc.PluginInterface.TryGetData<HashSet<string>>(tag, out var set))
                lock (set) set.Add(Svc.PluginInterface.InternalName);
        pausedClickers = true;
    }

    public void ResumeClickers()
    {
        if (!pausedClickers || holdingBell) return;
        foreach (var tag in StopRequestTags)
            if (Svc.PluginInterface.TryGetData<HashSet<string>>(tag, out var set))
                lock (set) set.Remove(Svc.PluginInterface.InternalName);
        pausedClickers = false;
    }

    private void ReleaseBell(string reason)
    {
        if (!holdingBell && !suppressedAutoRetainer) return;
        holdingBell = false;
        if (suppressedAutoRetainer)
        {
            IpcAction("AutoRetainer.SetSuppressed", false);
            suppressedAutoRetainer = false;
            Svc.Log.Information($"[MCP] AutoRetainer released ({reason})");
        }
        ResumeClickers();
    }

    /// <summary>Releases the bell once the player has left it for a few seconds (the bell session is over).</summary>
    private void OnUpdate(IFramework framework)
    {
        if (!holdingBell) return;
        var atBell = Svc.Condition[ConditionFlag.OccupiedSummoningBell] || RetainerUi.RetainerListOpen || RetainerUi.InventoryOpen;
        if (atBell) { bellLeftSince = DateTime.MaxValue; return; }
        if (bellLeftSince == DateTime.MaxValue) bellLeftSince = DateTime.UtcNow;
        // A grace period: an interaction that is retried, or a retainer being switched, leaves the bell for a moment.
        else if (DateTime.UtcNow - bellLeftSince > TimeSpan.FromSeconds(10)) ReleaseBell("bell closed");
    }

    public sealed record Info(bool AutoRetainer, bool? AutoRetainerBusy, bool? AutoRetainerSuppressed, bool SuppressedByUs,
                              bool YesAlready, bool TextAdvance, bool ClickersPausedByUs, bool HoldingBell,
                              bool Fcch, bool? FcchBusy, bool WaymarkPresetPlugin, bool Vnavmesh, bool Lifestream, bool Artisan, bool GatherBuddy,
                              bool ItemVendorLocation);

    /// <summary>Snapshot for the settings window (IPC calls; don't call every frame).</summary>
    public Info GetInfo()
    {
        var ar = AutoRetainerLoaded;
        return new Info(ar, ar ? Ipc<bool>("AutoRetainer.PluginState.IsBusy") : null, ar ? Ipc<bool>("AutoRetainer.GetSuppressed") : null,
                        suppressedAutoRetainer, IsLoaded("YesAlready"), IsLoaded("TextAdvance"), pausedClickers, holdingBell,
                        FcchLoaded, FcchLoaded ? Ipc<bool>("FCCH.IsBusy") : null, IsLoaded("WaymarkPresetPlugin"), IsLoaded("vnavmesh"), IsLoaded("Lifestream"), IsLoaded("Artisan"), IsLoaded("GatherbuddyReborn"),
                        IsLoaded("ItemVendorLocation"));
    }

    /// <summary>
    /// The automation plugins XIV MCP works around, and what it pauses. Only loaded plugins are listed: the assistant shouldn't learn
    /// about (and suggest) plugins the player doesn't use.
    /// </summary>
    public object Status()
    {
        var plugins = new Dictionary<string, object?>();
        if (AutoRetainerLoaded)
            plugins["AutoRetainer"] = new
            {
                busy = Ipc<bool>("AutoRetainer.PluginState.IsBusy"),
                multiMode = Ipc<bool>("AutoRetainer.GetMultiModeEnabled"),
                suppressed = Ipc<bool>("AutoRetainer.GetSuppressed"),
                suppressedByXivMcp = suppressedAutoRetainer,
            };
        if (IsLoaded("YesAlready")) plugins["YesAlready"] = new { pausedByXivMcp = pausedClickers };
        if (IsLoaded("TextAdvance")) plugins["TextAdvance"] = new { pausedByXivMcp = pausedClickers };
        if (FcchLoaded) plugins["FCCH"] = new { busy = Ipc<bool>("FCCH.IsBusy") };
        if (IsLoaded("WaymarkPresetPlugin")) plugins["WaymarkPresetPlugin"] = new { };
        return new { plugins, holdingBell };
    }

    internal static T? Ipc<T>(string name) where T : struct
    {
        try { return Svc.PluginInterface.GetIpcSubscriber<T>(name).InvokeFunc(); }
        catch (Exception ex) { Svc.Log.Debug($"IPC {name} unavailable: {ex.Message}"); return null; }
    }

    private static void IpcAction(string name, bool value)
    {
        try { Svc.PluginInterface.GetIpcSubscriber<bool, object>(name).InvokeAction(value); }
        catch (Exception ex) { Svc.Log.Warning($"IPC {name} failed: {ex.Message}"); }
    }
}
