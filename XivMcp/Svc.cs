using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace XivMcp;

/// <summary>Dalamud services, injected once at plugin start.</summary>
internal sealed class Svc
{
    [PluginService] public static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
    [PluginService] public static ICommandManager Commands { get; private set; } = null!;
    [PluginService] public static IFramework Framework { get; private set; } = null!;
    [PluginService] public static IClientState ClientState { get; private set; } = null!;
    [PluginService] public static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] public static IObjectTable Objects { get; private set; } = null!;
    [PluginService] public static IDataManager Data { get; private set; } = null!;
    [PluginService] public static IGameInventory Inventory { get; private set; } = null!;
    [PluginService] public static IUnlockState Unlocks { get; private set; } = null!;
    [PluginService] public static IPartyList Party { get; private set; } = null!;
    [PluginService] public static IBuddyList Buddies { get; private set; } = null!;
    [PluginService] public static ITargetManager Targets { get; private set; } = null!;
    [PluginService] public static ICondition Condition { get; private set; } = null!;
    [PluginService] public static IDutyState DutyState { get; private set; } = null!;
    [PluginService] public static IFateTable Fates { get; private set; } = null!;
    [PluginService] public static IAetheryteList Aetherytes { get; private set; } = null!;
}
