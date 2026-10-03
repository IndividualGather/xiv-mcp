using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using XivMcp.Mcp;

namespace XivMcp.Util;

/// <summary>
/// Reflection access to Dalamud's internal plugin manager, mirroring what the plugin installer does.
/// Dalamud has no public API to load/unload other plugins, so this may break on Dalamud updates.
/// </summary>
internal static class DalamudInternals
{
    private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Assembly DalamudAssembly = typeof(IDalamudPlugin).Assembly;

    private static Type T(string name) =>
        DalamudAssembly.GetType(name) ?? throw new ToolException($"Dalamud internal type '{name}' not found — this Dalamud version is not supported for plugin management.");

    private static object GetService(string typeName) =>
        T("Dalamud.Service`1").MakeGenericType(T(typeName)).GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)
        ?? throw new ToolException($"Dalamud service {typeName} is not available.");

    private static object PluginManager => GetService("Dalamud.Plugin.Internal.PluginManager");
    private static object ProfileManager => GetService("Dalamud.Plugin.Internal.Profiles.ProfileManager");

    public static object? Prop(object target, string name) => target.GetType().GetProperty(name, Any)?.GetValue(target);

    /// <summary>All installed plugins as Dalamud LocalPlugin instances.</summary>
    public static List<object> InstalledPlugins() =>
        ((IEnumerable)Prop(PluginManager, "InstalledPlugins")!).Cast<object>().ToList();

    public static string InternalName(object plugin) => (string)Prop(plugin, "InternalName")!;
    public static string Name(object plugin) => (string)Prop(plugin, "Name")!;
    public static string State(object plugin) => Prop(plugin, "State")?.ToString() ?? "Unknown";
    public static bool IsLoaded(object plugin) => Prop(plugin, "IsLoaded") is true;

    public static object Find(string nameOrInternalName)
    {
        var plugins = InstalledPlugins();
        var match = plugins.FirstOrDefault(p => InternalName(p).Equals(nameOrInternalName, StringComparison.OrdinalIgnoreCase))
                    ?? plugins.FirstOrDefault(p => Name(p).Equals(nameOrInternalName, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        var fuzzy = plugins.Where(p => Game.Matches(Name(p), nameOrInternalName) || Game.Matches(InternalName(p), nameOrInternalName)).ToList();
        if (fuzzy.Count == 1) return fuzzy[0];
        throw new ToolException(fuzzy.Count == 0
            ? $"No installed plugin named '{nameOrInternalName}'. Use list_plugins."
            : $"'{nameOrInternalName}' is ambiguous: {string.Join(", ", fuzzy.Select(InternalName))}");
    }

    public static Dictionary<string, object?> Describe(object p) => new()
    {
        ["name"] = Name(p),
        ["internalName"] = InternalName(p),
        ["version"] = Prop(p, "EffectiveVersion")?.ToString(),
        ["state"] = State(p),
        ["loaded"] = IsLoaded(p),
        ["dev"] = Prop(p, "IsDev") is true,
        ["thirdParty"] = Prop(p, "IsThirdParty") is true,
        ["testing"] = Prop(p, "IsTesting") is true,
        ["outdated"] = Prop(p, "IsOutdated") is true,
        ["banned"] = Prop(p, "IsBanned") is true,
        ["orphaned"] = Prop(p, "IsOrphaned") is true,
        ["decommissioned"] = Prop(p, "IsDecommissioned") is true,
        ["inDefaultCollection"] = Prop(p, "IsInDefaultProfile") is true,
        ["wantedByAnyCollection"] = Prop(p, "IsWantedByAnyProfile") is true,
    };

    public static void EnsureNotSelf(object plugin)
    {
        if (InternalName(plugin).Equals(Svc.PluginInterface.InternalName, StringComparison.OrdinalIgnoreCase))
            throw new ToolException("XIV MCP cannot manage itself (that would stop the MCP server mid-request).");
    }

    private static void EnsureStable(object plugin)
    {
        var state = State(plugin);
        if (state is "Loading" or "Unloading")
            throw new ToolException($"Plugin is currently {state.ToLowerInvariant()}; try again in a moment.");
    }

    public static async Task Unload(object plugin)
    {
        EnsureStable(plugin);
        if (!IsLoaded(plugin)) return;
        var mode = Enum.Parse(T("Dalamud.Plugin.Internal.Types.PluginLoaderDisposalMode"), "WaitBeforeDispose");
        await (Task)plugin.GetType().GetMethod("UnloadAsync", Any)!.Invoke(plugin, [mode])!;
    }

    public static async Task Load(object plugin)
    {
        EnsureStable(plugin);
        if (IsLoaded(plugin)) return;
        var reason = Enum.Parse(typeof(PluginLoadReason), nameof(PluginLoadReason.Installer));
        await (Task)plugin.GetType().GetMethod("LoadAsync", Any)!.Invoke(plugin, [reason, false, CancellationToken.None])!;
    }

    /// <summary>Persists the enabled state in the default collection, like the installer's toggle. Returns false if not applicable.</summary>
    public static async Task<bool> PersistWantState(object plugin, bool enabled)
    {
        if (Prop(plugin, "IsInDefaultProfile") is not true) return false;
        var profile = Prop(ProfileManager, "DefaultProfile")!;
        var id = (Guid)Prop(plugin, "EffectiveWorkingPluginId")!;
        var manifest = Prop(plugin, "Manifest")!;
        var internalName = (string)Prop(manifest, "InternalName")!;
        await (Task)profile.GetType().GetMethod("AddOrUpdateAsync", Any)!.Invoke(profile, [id, internalName, enabled, false])!;
        return true;
    }

    public static Guid WorkingId(object plugin) => (Guid)Prop(plugin, "EffectiveWorkingPluginId")!;

    /// <summary>Writes a config file through Dalamud's ReliableFileStorage so its backup DB stays in sync; falls back to a plain atomic write.</summary>
    public static async Task WriteConfigFile(string path, string contents, Guid workingPluginId)
    {
        try
        {
            var storage = GetService("Dalamud.Storage.ReliableFileStorage");
            var method = storage.GetType().GetMethod("WriteAllTextAsync", Any, [typeof(string), typeof(string), typeof(Guid)]);
            if (method is not null)
            {
                await (Task)method.Invoke(storage, [path, contents, workingPluginId])!;
                return;
            }
        }
        catch (Exception ex) when (ex is not ToolException)
        {
            Svc.Log.Warning(ex, "ReliableFileStorage write failed, falling back to a direct write");
        }

        var tmp = path + ".xivmcp.tmp";
        await System.IO.File.WriteAllTextAsync(tmp, contents);
        System.IO.File.Move(tmp, path, overwrite: true);
    }
}
