using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using XivMcp.Util;

namespace XivMcp.Mcp;

/// <summary>Resources (the snapshot caches), sessions, subscriptions and the server → client event stream.</summary>
public sealed partial class McpServer
{
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(15);

    private readonly CacheRegistry caches;
    private readonly ConcurrentDictionary<string, Session> sessions = new();

    private sealed class Session
    {
        public readonly ConcurrentDictionary<string, byte> Subscriptions = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<EventStream> Streams = [];
        public DateTime LastSeenUtc = DateTime.UtcNow;
        public string? ClientName;
    }

    private DateTime? lastStatelessRequestUtc;

    /// <summary>A client seen since the server started: its name (from initialize) and when it last sent a request.</summary>
    public sealed record ClientInfo(string Name, DateTime LastSeenUtc, bool EventStream);

    /// <summary>Clients seen since the server (re)started, most recent first. Requests without a session show as "unnamed client".</summary>
    public List<ClientInfo> Clients()
    {
        var list = sessions.Values
            .Select(s => new ClientInfo(s.ClientName ?? "unnamed client", s.LastSeenUtc, s.Streams.Count > 0))
            .ToList();
        if (lastStatelessRequestUtc is { } t) list.Add(new ClientInfo("unnamed client (no session)", t, false));
        return list.OrderByDescending(c => c.LastSeenUtc).ToList();
    }

    private sealed class EventStream(HttpListenerResponse response)
    {
        private readonly SemaphoreSlim writeLock = new(1, 1);
        public bool Closed { get; private set; }

        public async Task Write(string text)
        {
            if (Closed) return;
            await writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                await response.OutputStream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                Close();
            }
            finally { writeLock.Release(); }
        }

        public void Close()
        {
            if (Closed) return;
            Closed = true;
            try { response.Abort(); } catch { /* already gone */ }
        }
    }

    public int SessionCount => sessions.Count;
    public int SubscriptionCount => sessions.Values.Sum(s => s.Subscriptions.Count);

    private string StartSession(string? clientName)
    {
        // Forget sessions that never came back (clients usually don't send DELETE).
        foreach (var (id, s) in sessions)
            if (DateTime.UtcNow - s.LastSeenUtc > TimeSpan.FromHours(12) && s.Streams.Count == 0)
                sessions.TryRemove(id, out _);

        var sessionId = Guid.NewGuid().ToString("N");
        sessions[sessionId] = new Session { ClientName = clientName };
        return sessionId;
    }

    private void EndSession(string sessionId)
    {
        if (!sessions.TryRemove(sessionId, out var session)) return;
        lock (session.Streams)
        {
            foreach (var s in session.Streams) s.Close();
            session.Streams.Clear();
        }
    }

    private JsonObject ListResources() => new()
    {
        ["resources"] = new JsonArray(caches.All.Select(c => (JsonNode)new JsonObject
        {
            ["uri"] = CacheRegistry.Uri(c),
            ["name"] = c.Id,
            ["title"] = c.Title,
            ["description"] = c.Description + " Subscribe to get notified when the snapshot is refreshed.",
            ["mimeType"] = "application/json",
        }).ToArray()),
    };

    private JsonObject ReadResource(JsonObject? p)
    {
        var uri = p?["uri"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing uri");
        ICache cache;
        try { cache = caches.Get(uri); }
        catch (ToolException) { throw new RpcException(-32002, $"Resource not found: {uri}"); }

        var payload = new
        {
            cache = cache.Id,
            version = cache.Version,
            entries = cache.Entries().Select(e => new { character = e.Character, entry = e.Entry, freshness = CacheFreshness.Describe(e.CapturedUtc, false, e.RefreshHint) }).ToList(),
            data = cache.Read(),
        };
        return new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["uri"] = CacheRegistry.Uri(cache),
                ["mimeType"] = "application/json",
                ["text"] = JsonSerializer.Serialize(payload, JsonOptions),
            }),
        };
    }

    private JsonObject Subscribe(JsonObject? p, Session? session, bool subscribe)
    {
        var uri = p?["uri"]?.GetValue<string>() ?? throw new RpcException(-32602, "Missing uri");
        if (session is null) throw new RpcException(-32602, "Subscriptions need a session: send the Mcp-Session-Id header from initialize.");
        ICache cache;
        try { cache = caches.Get(uri); }
        catch (ToolException) { throw new RpcException(-32002, $"Resource not found: {uri}"); }

        var canonical = CacheRegistry.Uri(cache);
        if (subscribe) session.Subscriptions[canonical] = 0;
        else session.Subscriptions.TryRemove(canonical, out _);
        return new JsonObject();
    }

    /// <summary>GET /mcp: a long-lived text/event-stream carrying server → client notifications for one session.</summary>
    private async Task OpenEventStream(HttpListenerRequest req, HttpListenerResponse res, Session? session, CancellationToken ct)
    {
        var accept = req.Headers["Accept"] ?? "";
        if (session is null || !accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            res.Headers["Allow"] = "POST, DELETE";
            await WriteText(res, 405, "GET needs an Mcp-Session-Id header and Accept: text/event-stream").ConfigureAwait(false);
            return;
        }

        res.StatusCode = 200;
        res.ContentType = "text/event-stream";
        res.Headers["Cache-Control"] = "no-cache";
        res.SendChunked = true;
        var stream = new EventStream(res);
        lock (session.Streams) session.Streams.Add(stream);
        try
        {
            await stream.Write(": connected\n\n").ConfigureAwait(false);
            while (!stream.Closed && !ct.IsCancellationRequested)
            {
                session.LastSeenUtc = DateTime.UtcNow;
                await Task.Delay(KeepAliveInterval, ct).ConfigureAwait(false);
                await stream.Write(": keep-alive\n\n").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* server stopping */ }
        finally
        {
            stream.Close();
            lock (session.Streams) session.Streams.Remove(stream);
        }
    }

    private void OnCacheUpdated(ICache cache)
    {
        var uri = CacheRegistry.Uri(cache);
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/resources/updated",
            ["params"] = new JsonObject { ["uri"] = uri, ["title"] = cache.Title },
        }.ToJsonString(JsonOptions);
        var frame = $"event: message\ndata: {message}\n\n";

        foreach (var session in sessions.Values.Where(s => s.Subscriptions.ContainsKey(uri)))
        {
            EventStream[] streams;
            lock (session.Streams) streams = [.. session.Streams];
            foreach (var s in streams) _ = s.Write(frame);
        }
    }
}
