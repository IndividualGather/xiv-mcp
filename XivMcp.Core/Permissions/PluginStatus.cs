namespace XivMcp.Permissions;

/// <summary>Where a third-party plugin stands with the player. Ids (see <see cref="PluginStatus.Id"/>) are part of the plugin API.</summary>
public enum PluginState
{
    /// <summary>Registered, but the player hasn't decided yet (they were notified).</summary>
    Undecided,

    /// <summary>The player chose to keep it disabled; they aren't asked again until the registration changes.</summary>
    KeptDisabled,

    /// <summary>Enabled: its tools are offered and run under the capability policies.</summary>
    Enabled,

    /// <summary>Was enabled, but its registration grew; all its tools are paused until the player consents again.</summary>
    AwaitingConsent,

    /// <summary>A call did something it didn't declare; refused until the player lifts the suspension.</summary>
    Suspended,
}

public sealed record PluginStatus(PluginState State, string? SuspendReason)
{
    public bool CanRun => State == PluginState.Enabled;

    public static PluginStatus Of(PluginPolicy policy) => new(
        policy.Suspended ? PluginState.Suspended
        : policy.Enabled && policy.AwaitingConsent ? PluginState.AwaitingConsent
        : policy.Enabled ? PluginState.Enabled
        : policy.Reviewed ? PluginState.KeptDisabled
        : PluginState.Undecided,
        policy.Suspended ? policy.SuspendReason : null);

    public static string Id(PluginState state) => state switch
    {
        PluginState.Undecided => "undecided",
        PluginState.KeptDisabled => "kept_disabled",
        PluginState.Enabled => "enabled",
        PluginState.AwaitingConsent => "awaiting_consent",
        _ => "suspended",
    };
}
