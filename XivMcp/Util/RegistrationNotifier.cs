using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiNotification;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Util;

/// <summary>The player's decision on a plugin's registration, from the notification or the plugin's page.</summary>
internal sealed class PluginDecisions(ToolRegistry registry, IPolicyStore policies, ToolGate gate, Action toolsChanged)
{
    public (System.Collections.Generic.List<string> Tools, System.Collections.Generic.List<string> Capabilities) Registration(string pluginId)
    {
        var tools = registry.All.Where(t => t.Provider.Trust == ProviderTrust.ThirdParty && t.Provider.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase)).ToList();
        return (tools.Select(t => t.Name).ToList(), tools.SelectMany(t => t.Capabilities).Distinct().ToList());
    }

    public RegistrationReview.Result Pending(string pluginId)
    {
        var (tools, caps) = Registration(pluginId);
        return RegistrationReview.Check(policies.Get(pluginId), tools, caps);
    }

    /// <summary>Called when the player's Enable leaves something registered since unconsented, so it is announced again.</summary>
    public Action<string>? ChangedSinceShown { get; set; }

    /// <summary>
    /// Enable or keep disabled (remembered until the registration changes). <paramref name="shown"/> is the registration the player was
    /// looking at: Enable consents to exactly that, and anything registered since waits for consent again.
    /// </summary>
    public void Decide(string pluginId, bool enable, (System.Collections.Generic.List<string> Tools, System.Collections.Generic.List<string> Capabilities)? shown = null)
    {
        var (tools, caps) = Registration(pluginId);
        var policy = policies.Get(pluginId);
        if (enable)
        {
            var (seenTools, seenCaps) = shown ?? (tools, caps);
            var since = RegistrationReview.EnableShown(policy, seenTools, seenCaps, tools, caps);
            if (since.What != RegistrationReview.Kind.None)
            {
                Svc.Log.Warning($"[MCP] {pluginId} registered more while the player was deciding: {string.Join(", ", since.NewTools)}; that waits for consent.");
                ChangedSinceShown?.Invoke(pluginId);
            }
        }
        else
        {
            RegistrationReview.KeepDisabled(policy, tools, caps);
            gate.Sessions.Clear(pluginId);
        }
        policies.Save();
        toolsChanged();
        Svc.Log.Information($"[MCP] Player {(enable ? "enabled" : "kept disabled")} {pluginId} ({tools.Count} tools)");
    }
}

/// <summary>
/// Pops up a Dalamud notification when a third-party plugin's registration needs the player's decision: a plugin they never decided on,
/// or one whose registration grew (an enabled one then waits for consent again). The notification has Enable / Keep disabled buttons;
/// clicking it opens the plugin's page. Registrations in quick succession make one notification.
/// </summary>
internal sealed class RegistrationNotifier(PluginDecisions decisions, IPolicyStore policies, Action<string> openPlugin)
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);
    private readonly ConcurrentDictionary<string, byte> scheduled = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IActiveNotification> shown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Called on every registration; announces the plugin once its registrations settle.</summary>
    public void Registered(string pluginId)
    {
        if (!scheduled.TryAdd(pluginId, 0)) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(Debounce).ConfigureAwait(false);
            scheduled.TryRemove(pluginId, out _);
            await Svc.Framework.RunOnFrameworkThread(() => Announce(pluginId)).ConfigureAwait(false);
        });
    }

    private void Announce(string pluginId)
    {
        var shownRegistration = decisions.Registration(pluginId);
        var tools = shownRegistration.Tools;
        if (tools.Count == 0) return;
        var review = decisions.Pending(pluginId);
        if (review.What == RegistrationReview.Kind.None) return;
        var policy = policies.Get(pluginId);
        var name = Svc.PluginInterface.InstalledPlugins.FirstOrDefault(p => p.InternalName.Equals(pluginId, StringComparison.OrdinalIgnoreCase))?.Name ?? pluginId;

        string Caps(int max) => string.Join(", ", review.New.Take(max).Select(c => c.Title.ToLowerInvariant())) + (review.New.Count > max ? ", …" : "");
        var changes = string.Join(" ", new[]
        {
            review.NewTools.Count > 0 ? $"New tool{(review.NewTools.Count == 1 ? "" : "s")}: {string.Join(", ", review.NewTools.Take(4))}{(review.NewTools.Count > 4 ? ", …" : "")}." : null,
            review.New.Count > 0 ? $"New: {Caps(4)}." : null,
        }.Where(s => s is not null));

        var (title, content) = (review.What, policy.Enabled) switch
        {
            (RegistrationReview.Kind.NewPlugin, _) => ($"{name} wants to register with XIV MCP",
                $"It offers {tools.Count} tool{(tools.Count == 1 ? "" : "s")} to your assistant{(review.New.Count > 0 ? $" and asks to: {Caps(3)}" : " (read only)")}. " +
                "Nothing is offered until you enable it."),
            (_, true) => ($"{name} changed its registration", $"{changes} Its tools are paused until you consent again."),
            _ => ($"{name} changed its registration", $"{changes} It stays disabled unless you enable it."),
        };

        if (shown.TryRemove(pluginId, out var old)) old.DismissNow();
        var note = Svc.Notifications.AddNotification(new Notification
        {
            Title = title,
            Content = content,
            MinimizedText = title,
            Type = review.Riskiest?.Risk >= RiskLevel.High ? NotificationType.Warning : NotificationType.Info,
            // Stays until the player decides or closes it (closing decides nothing: it comes back on the next registration).
            HardExpiry = DateTime.MaxValue,
            InitialDuration = TimeSpan.MaxValue,
            ShowIndeterminateIfNoExpiry = false,
            UserDismissable = true,
        });
        shown[pluginId] = note;
        note.DrawActions += _ =>
        {
            // Enable consents to what this notice showed, not to whatever is registered by the time of the click.
            if (ImGui.Button("Enable")) Close(note, () => decisions.Decide(pluginId, true, shownRegistration));
            ImGui.SameLine();
            if (ImGui.Button("Keep disabled")) Close(note, () => decisions.Decide(pluginId, false));
            ImGui.SameLine();
            if (ImGui.Button("Review…")) Close(note, () => openPlugin(pluginId));
        };
        note.Click += _ => Close(note, () => openPlugin(pluginId));
        note.Dismiss += _ => shown.TryRemove(new(pluginId, note));
        Svc.Log.Information($"[MCP] Announced {name}: {review.What}{(policy.AwaitingConsent ? " (waiting for consent)" : "")}");
    }

    private static void Close(IActiveNotification note, Action then)
    {
        then();
        note.DismissNow();
    }
}
