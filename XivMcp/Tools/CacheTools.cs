using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

internal static class CacheTools
{
    public static IEnumerable<McpTool> Create(CacheRegistry caches)
    {
        yield return new McpTool
        {
            Name = "get_cache_status",
            Description = "Lists the plugin's snapshot caches (data the game only sends in certain places: FC workshop → submersibles, " +
                          "summoning bell → retainer list and retainer inventories) with the age of every entry, whether it is stale, " +
                          "and exactly what the player has to do in game to refresh it. Each cache is also an MCP resource (xiv://cache/<id>) " +
                          "that clients can subscribe to for update notifications.",
            Handler = (_, _) => Task.FromResult<object?>(new
            {
                staleAfterHours = CacheFreshness.StaleAfter.TotalHours,
                caches = caches.Status(),
            }),
        };

        yield return new McpTool
        {
            Name = "wait_for_cache_refresh",
            Description = "Waits until a snapshot cache is refreshed (e.g. after asking the user to open a retainer or enter the FC workshop), " +
                          "then returns. Pass the 'version' from a previous call or get_cache_status to wait for the next refresh after it; " +
                          "without it, waits for the next refresh from now. Times out after timeout_seconds (default 120, max 600).",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "cache": { "type": "string", "description": "Cache id (submersibles, retainers) or resource URI (xiv://cache/<id>)." },
                    "since_version": { "type": "integer", "description": "Return as soon as the cache version is greater than this." },
                    "timeout_seconds": { "type": "integer", "description": "How long to wait (default 120, max 600)." }
                  },
                  "required": ["cache"]
                }
                """,
            Handler = async (args, ct) =>
            {
                var cache = caches.Get(args.String("cache") ?? throw new ToolException("'cache' is required."));
                var since = args.Node("since_version") is not null ? (long)args.Int("since_version", 0, 0) : cache.Version;
                var timeout = TimeSpan.FromSeconds(args.Int("timeout_seconds", 120, 1, 600));
                var started = DateTime.UtcNow;
                var refreshed = await CacheRegistry.WaitForUpdate(cache, since, timeout, ct).ConfigureAwait(false);
                return new
                {
                    cache = cache.Id,
                    refreshed,
                    version = cache.Version,
                    waitedSeconds = Math.Round((DateTime.UtcNow - started).TotalSeconds, 1),
                    note = refreshed ? "The cache was refreshed; read it again with the matching tool." : "No refresh within the timeout. The player may not have done the refresh action yet.",
                };
            },
        };
    }
}
