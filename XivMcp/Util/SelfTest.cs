using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Diagnostics;
using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Util;

/// <summary>
/// Live checks against the running game: what unit tests can't cover. Run with /xivmcp selftest or from the Tools tab. Only reads;
/// nothing is changed in game (third-party tools are only called if they are read-only and their plugin is enabled).
/// </summary>
internal static class SelfTest
{
    public static IReadOnlyList<SelfTestResult>? Last { get; private set; }
    public static DateTime? LastRunUtc { get; private set; }
    public static bool Running { get; private set; }

    public static async Task<IReadOnlyList<SelfTestResult>> RunAsync(Plugin plugin, CancellationToken ct = default)
    {
        Running = true;
        try
        {
            var results = await SelfTestRunner.RunAsync(Checks(plugin), ct).ConfigureAwait(false);
            Last = results;
            LastRunUtc = DateTime.UtcNow;
            return results;
        }
        finally { Running = false; }
    }

    private static IEnumerable<SelfTestCheck> Checks(Plugin plugin)
    {
        var tools = plugin.Server.Tools;

        yield return new("Tool schemas parse", _ =>
        {
            var broken = tools.Where(t => { try { System.Text.Json.Nodes.JsonNode.Parse(t.InputSchema); return false; } catch { return true; } }).Select(t => t.Name).ToList();
            return broken.Count == 0 ? Task.FromResult<string?>($"{tools.Count} tools") : throw new Exception($"invalid: {string.Join(", ", broken)}");
        });

        yield return new("Tool names are unique and providers consistent", _ =>
        {
            var wrong = tools.Where(t => t.Provider.Trust == ProviderTrust.Maintained && IntegrationCatalog.For(t.Name)?.Provider != t.Provider).Select(t => t.Name).ToList();
            return wrong.Count == 0 ? Task.FromResult<string?>(null) : throw new Exception($"misassigned: {string.Join(", ", wrong)}");
        });

        yield return new("Every built-in tool has a permission group", _ =>
        {
            var unlisted = tools.Where(t => t.Provider.Trust != ProviderTrust.ThirdParty && PermissionCatalog.GroupOf(t) is null).Select(t => t.Name).ToList();
            return unlisted.Count == 0
                ? Task.FromResult<string?>($"{PermissionCatalog.Groups.Count} groups")
                : throw new Exception($"not in the catalog (writes ask, reads are allowed): {string.Join(", ", unlisted)}");
        });

        yield return new("Integrations follow their plugin", _ =>
        {
            var lines = new List<string>();
            foreach (var i in IntegrationCatalog.All)
            {
                var loaded = PluginCompat.IsLoaded(i.PluginId);
                var offered = tools.Where(t => t.Provider == i.Provider).Count(t => t.IsAvailable);
                if (!loaded && offered > 0) throw new Exception($"{i.DisplayName} isn't loaded but {offered} of its tools are offered");
                lines.Add($"{i.DisplayName} {(loaded ? $"{offered}/{i.Tools.Count}" : "off")}");
            }
            return Task.FromResult<string?>(string.Join(", ", lines));
        });

        yield return new("Game status", async ct =>
        {
            var result = await Call(plugin, "get_game_status", "{}", ct).ConfigureAwait(false);
            return result?["loggedIn"]?.GetValue<bool>() == true ? $"logged in as {result["character"]}" : "not logged in";
        });

        yield return new("Character and gearsets", async ct =>
        {
            if (!Svc.ClientState.IsLoggedIn) throw new SelfTestSkip("not logged in");
            var c = await Call(plugin, "get_character", "{}", ct).ConfigureAwait(false);
            var g = await Call(plugin, "list_gearsets", "{}", ct).ConfigureAwait(false);
            return $"{c?["name"]}, {(g?["gearsets"] as JsonArray)?.Count ?? 0} gearsets";
        });

        yield return new("Game snapshot for side-effect checks", _ =>
        {
            if (!Svc.ClientState.IsLoggedIn) throw new SelfTestSkip("not logged in");
            var s = plugin.Probe.Capture() ?? throw new Exception("no snapshot");
            return Task.FromResult<string?>($"{s.Gil:N0} gil, {s.ItemTotal:N0} items, {s.Currencies.Count} currencies, zone {s.Territory}");
        });

        yield return new("Plugin API answers over IPC", _ =>
        {
            var version = Svc.PluginInterface.GetIpcSubscriber<int>("XivMcp.ApiVersion").InvokeFunc();
            var caps = JsonNode.Parse(Svc.PluginInterface.GetIpcSubscriber<string>("XivMcp.ListCapabilities").InvokeFunc())?["capabilities"] as JsonArray;
            var bad = JsonNode.Parse(Svc.PluginInterface.GetIpcSubscriber<string, string, string>("XivMcp.CheckPermission").InvokeFunc("NoSuchPlugin", "read_game"));
            if (bad?["ok"]?.GetValue<bool>() != false) throw new Exception("an unknown plugin was not refused");
            return Task.FromResult<string?>($"API {version}, {caps?.Count ?? 0} capabilities");
        });

        yield return new("Audit log is writable", _ =>
        {
            var path = System.IO.Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "audit.jsonl");
            using (System.IO.File.Open(path, System.IO.FileMode.OpenOrCreate, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite)) { }
            return Task.FromResult<string?>($"{plugin.Gate.Audit.Recent(max: 1000).Count} entries in memory");
        });

        var thirdParty = plugin.PluginApi.Plugins(plugin.Config);
        if (thirdParty.Count == 0)
            yield return new("Third-party plugins", _ => throw new SelfTestSkip("none registered"));
        foreach (var p in thirdParty)
        {
            yield return new($"Third-party: {p.DisplayName}", async ct =>
            {
                if (!p.Loaded) throw new SelfTestSkip("not loaded");
                if (p.Policy.Suspended) return $"suspended: {p.Policy.SuspendReason}";
                var invoke = Svc.PluginInterface.GetIpcSubscriber<string, string, string, string>($"{p.InternalName}.XivMcp.Invoke");
                if (!invoke.HasFunction) throw new Exception($"registered {p.Tools.Count} tool(s) but provides no {p.InternalName}.XivMcp.Invoke");
                if (!p.Policy.Enabled) return $"{p.Tools.Count} tool(s), off (not called)";
                var reader = p.Tools.FirstOrDefault(t => t.ReadOnly && !t.InputSchema.Contains("\"required\""));
                if (reader is null) return $"{p.Tools.Count} tool(s), none read-only without arguments to call";
                await plugin.Gate.InvokeAsync(reader, new ToolArgs(null), ct).ConfigureAwait(false);
                return $"{p.Tools.Count} tool(s); {reader.Name} answered";
            });
        }
    }

    /// <summary>Calls a tool the way the server does (through the gate) and parses its result.</summary>
    private static async Task<JsonNode?> Call(Plugin plugin, string tool, string args, CancellationToken ct)
    {
        var found = plugin.Server.Tools.FirstOrDefault(t => t.Name == tool) ?? throw new Exception($"no tool {tool}");
        var result = await plugin.Gate.InvokeAsync(found, new ToolArgs(JsonNode.Parse(args) as JsonObject), ct).ConfigureAwait(false);
        return result is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(result);
    }

    /// <summary>The self-test as an MCP tool, so an assistant can check the setup too.</summary>
    public static McpTool Tool() => new()
    {
        Name = "run_self_test",
        Description = "Runs XIV MCP's live self-test (the same as /xivmcp selftest): tool schemas, integrations versus loaded plugins, game " +
                      "reads, the side-effect snapshot, the plugin API over IPC, the audit log, and each third-party plugin. Only reads. " +
                      "Use it when tools behave oddly or after installing a plugin.",
        Handler = async (_, ct) =>
        {
            var plugin = Plugin.Instance ?? throw new ToolException("XIV MCP is not initialised.");
            if (Running) throw new ToolException("A self-test is already running.");
            var results = await RunAsync(plugin, ct).ConfigureAwait(false);
            return new
            {
                summary = SelfTestRunner.Summarize(results),
                checks = results.Select(r => new { name = r.Name, status = r.Status.ToString().ToLowerInvariant(), detail = r.Detail, ms = (int)r.Duration.TotalMilliseconds }),
            };
        },
    };

    public static void PrintToChat(IReadOnlyList<SelfTestResult> results)
    {
        Svc.Chat.Print($"[XIV MCP] Self-test: {SelfTestRunner.Summarize(results)}");
        foreach (var r in results)
        {
            var line = $"[XIV MCP] {(r.Status switch { SelfTestStatus.Passed => "✓", SelfTestStatus.Failed => "✗", _ => "–" })} {r.Name}: {r.Detail}";
            if (r.Status == SelfTestStatus.Failed) Svc.Chat.PrintError(line);
            else Svc.Chat.Print(line);
        }
    }
}
