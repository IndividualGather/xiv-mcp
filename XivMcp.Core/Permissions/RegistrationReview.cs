using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Permissions;

/// <summary>
/// Whether a plugin's registration needs the player's decision. The player decides once per registration (enable it, or keep it
/// disabled) and XIV MCP remembers which tools and capabilities that decision was made for. It asks again only when the registration
/// grows: a new tool or a new capability. Removing tools is never news, and neither is a plugin that only reads.
/// </summary>
public static class RegistrationReview
{
    public enum Kind { None, NewPlugin, Changed }

    /// <summary><see cref="New"/>: capabilities to point out (riskiest first); <see cref="NewTools"/>: tools not seen at the last decision.</summary>
    public sealed record Result(Kind What, IReadOnlyList<Capability> New, IReadOnlyList<string> NewTools)
    {
        public Capability? Riskiest => New.FirstOrDefault();
    }

    public static Result Check(PluginPolicy policy, IEnumerable<string> tools, IEnumerable<string> declared)
    {
        var toolList = tools.Distinct().ToList();
        var acting = declared.Distinct().Select(Capabilities.Find).OfType<Capability>().Where(c => c.Id != Capabilities.ReadGame)
                             .OrderByDescending(c => c.Risk).ThenBy(c => c.Title).ToList();
        if (!policy.Reviewed)
            return policy.Enabled && acting.Count == 0
                ? new Result(Kind.None, [], [])
                : new Result(Kind.NewPlugin, acting, toolList);
        var newCaps = acting.Where(c => !policy.ReviewedCapabilities.Contains(c.Id)).ToList();
        var newTools = toolList.Where(t => !policy.ReviewedTools.Contains(t)).ToList();
        return new Result(newCaps.Count > 0 || newTools.Count > 0 ? Kind.Changed : Kind.None, newCaps, newTools);
    }

    /// <summary>
    /// Called on every registration. If the player had enabled the plugin and its registration now goes beyond what they consented to,
    /// the plugin waits for consent again (its tools are refused) until the player enables or keeps it disabled.
    /// </summary>
    public static Result OnRegistered(PluginPolicy policy, IEnumerable<string> tools, IEnumerable<string> declared)
    {
        var result = Check(policy, tools, declared);
        if (policy.Enabled && result.What != Kind.None) policy.AwaitingConsent = true;
        return result;
    }

    /// <summary>Remembers the registration the player has now seen (replacing what was remembered before).</summary>
    public static void MarkReviewed(PluginPolicy policy, IEnumerable<string> tools, IEnumerable<string> declared)
    {
        policy.Reviewed = true;
        policy.ReviewedTools = [.. tools];
        policy.ReviewedCapabilities = [.. declared];
    }

    /// <summary>The player chose "Keep disabled": off, and not asked again until the registration changes.</summary>
    public static void KeepDisabled(PluginPolicy policy, IEnumerable<string> tools, IEnumerable<string> declared)
    {
        policy.Enabled = false;
        policy.AwaitingConsent = false;
        MarkReviewed(policy, tools, declared);
    }

    public static void Enable(PluginPolicy policy, IEnumerable<string> tools, IEnumerable<string> declared)
    {
        policy.Enabled = true;
        policy.AwaitingConsent = false;
        MarkReviewed(policy, tools, declared);
    }
}
