using System;
using System.Collections.Generic;
using System.Linq;
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
        foreach (var tag in StopRequestTags) Svc.PluginInterface.RelinquishData(tag);
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
        else if (DateTime.UtcNow - bellLeftSince > TimeSpan.FromSeconds(3)) ReleaseBell("bell closed");
    }

    public sealed record Info(bool AutoRetainer, bool? AutoRetainerBusy, bool? AutoRetainerSuppressed, bool SuppressedByUs,
                              bool YesAlready, bool TextAdvance, bool ClickersPausedByUs, bool HoldingBell,
                              bool Fcch, bool? FcchBusy, bool WaymarkPresetPlugin, bool Vnavmesh, bool Lifestream);

    /// <summary>Snapshot for the settings window (IPC calls; don't call every frame).</summary>
    public Info GetInfo()
    {
        var ar = AutoRetainerLoaded;
        return new Info(ar, ar ? Ipc<bool>("AutoRetainer.PluginState.IsBusy") : null, ar ? Ipc<bool>("AutoRetainer.GetSuppressed") : null,
                        suppressedAutoRetainer, IsLoaded("YesAlready"), IsLoaded("TextAdvance"), pausedClickers, holdingBell,
                        FcchLoaded, FcchLoaded ? Ipc<bool>("FCCH.IsBusy") : null, IsLoaded("WaymarkPresetPlugin"), IsLoaded("vnavmesh"), IsLoaded("Lifestream"));
    }

    public object Status() => new
    {
        autoRetainer = AutoRetainerLoaded
            ? new
            {
                loaded = true,
                busy = Ipc<bool>("AutoRetainer.PluginState.IsBusy"),
                multiMode = Ipc<bool>("AutoRetainer.GetMultiModeEnabled"),
                suppressed = Ipc<bool>("AutoRetainer.GetSuppressed"),
                suppressedByXivMcp = suppressedAutoRetainer,
            }
            : (object)new { loaded = false },
        yesAlready = new { loaded = IsLoaded("YesAlready"), pausedByXivMcp = pausedClickers && IsLoaded("YesAlready") },
        textAdvance = new { loaded = IsLoaded("TextAdvance"), pausedByXivMcp = pausedClickers && IsLoaded("TextAdvance") },
        fcch = FcchLoaded ? new { loaded = true, busy = Ipc<bool>("FCCH.IsBusy") } : (object)new { loaded = false },
        waymarkPresetPlugin = new { loaded = IsLoaded("WaymarkPresetPlugin") },
        holdingBell,
    };

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
