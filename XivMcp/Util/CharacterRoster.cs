using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;

namespace XivMcp.Util;

/// <summary>
/// The characters XIV MCP has seen, as the lobby lists them: name, home and current world, data center and the slot within the
/// current world's list (what /swapcharacter needs). Captured whenever the game has the character list loaded (in the lobby) plus the
/// character in game, and kept in pluginConfigs/XivMcp/characters.json, so characters on other data centers are remembered once seen.
/// <para>
/// Accounts are told apart by what is observed: one lobby list always belongs to one account, everything seen in one game session
/// (logging out keeps the account) belongs to the same account, and a character appearing in two lists links them. Each group gets
/// a local account key; the game offers no stable account id for this.
/// </para>
/// </summary>
internal sealed class CharacterRoster : IDisposable
{
    public sealed record Character(ulong ContentId, string Name, uint HomeWorldId, string HomeWorld, uint CurrentWorldId, string CurrentWorld,
                                   string DataCenter, int Slot, DateTime SeenUtc, string? AccountKey, int ServiceAccount = -1);

    private readonly string file = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "characters.json");
    private readonly Dictionary<ulong, Character> characters;
    private readonly object sync = new();
    private DateTime nextPoll = DateTime.MinValue;
    private string lastSignature = "";

    /// <summary>The account of this game session (set by the first list or character seen; the game can't switch accounts in-session).</summary>
    private string? sessionKey;
    private readonly string sessionFile = Path.Combine(Svc.PluginInterface.GetPluginConfigDirectory(), "characters.session");
    private static readonly string GameSession = $"{Environment.ProcessId}:{System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime():O}";

    public CharacterRoster()
    {
        try { characters = File.Exists(file) ? (JsonSerializer.Deserialize<List<Character>>(File.ReadAllText(file)) ?? []).ToDictionary(c => c.ContentId) : []; }
        catch { characters = []; }
        // The account of this game session survives plugin reloads (same game process).
        try { if (File.Exists(sessionFile) && File.ReadAllText(sessionFile).Split('|') is [var s, var k] && s == GameSession) sessionKey = k; }
        catch { /* start without */ }
        Svc.Framework.Update += OnUpdate;
    }

    public void Dispose() => Svc.Framework.Update -= OnUpdate;

    public List<Character> All()
    {
        lock (sync) return characters.Values.OrderBy(c => c.DataCenter).ThenBy(c => c.CurrentWorld).ThenBy(c => c.Slot).ToList();
    }

    /// <summary>The account key of the account in use this session, if known.</summary>
    public string? CurrentAccount
    {
        get { lock (sync) return sessionKey; }
    }

    private void OnUpdate(IFramework framework)
    {
        if (DateTime.UtcNow < nextPoll) return;
        nextPoll = DateTime.UtcNow.AddSeconds(2);
        try { Capture(); RecordInGame(); }
        catch (Exception ex) { Svc.Log.Debug($"[MCP] Character list capture failed: {ex.Message}"); }
    }

    private unsafe void Capture()
    {
        var agent = AgentLobby.Instance();
        if (agent == null) return;
        var entries = agent->LobbyData.CharaSelectEntries;
        if (entries.Count == 0) return;
        var serviceAccount = LobbyLogin.LastServiceAccount ?? (agent->ServiceAccountIndex >= 0 ? agent->ServiceAccountIndex : -1);

        var worlds = Svc.Data.GetExcelSheet<World>();
        var found = new List<(ulong Id, string Name, ushort Home, ushort Current, byte Index)>();
        foreach (var ptr in entries)
        {
            var e = ptr.Value;
            if (e == null || e->ContentId == 0) continue;
            found.Add((e->ContentId, e->NameString, e->HomeWorldId, e->CurrentWorldId, e->Index));
        }
        var signature = string.Join(",", found.Select(f => $"{f.Id}:{f.Current}:{f.Index}"));
        if (signature == lastSignature) return;
        lastSignature = signature;

        lock (sync)
        {
            // Same account as: this session, or any listed character already known.
            var key = Link(found.Select(f => f.Id));
            // The lobby lists a character under its current world; /swapcharacter counts within that world's list.
            foreach (var group in found.GroupBy(f => f.Current))
            {
                var slot = 0;
                foreach (var f in group.OrderBy(f => f.Index))
                {
                    var current = worlds.GetRowOrDefault(f.Current);
                    var home = worlds.GetRowOrDefault(f.Home);
                    characters[f.Id] = new Character(f.Id, f.Name, f.Home, home?.Name.ExtractText() ?? "", f.Current, current?.Name.ExtractText() ?? "",
                                                     current?.DataCenter.ValueNullable?.Name.ExtractText() ?? "", slot++, DateTime.UtcNow, key,
                                                     serviceAccount >= 0 ? serviceAccount : characters.TryGetValue(f.Id, out var old) ? old.ServiceAccount : -1);
                }
            }
            Save();
        }
        Svc.Log.Information($"[MCP] Character list captured: {found.Count} characters");
    }

    /// <summary>Remembers the character in game (without a lobby slot if its list hasn't been seen) as part of this session's account.</summary>
    private void RecordInGame()
    {
        if (!Svc.ClientState.IsLoggedIn || Svc.Objects.LocalPlayer is not { } player) return;
        var me = Svc.PlayerState.ContentId;
        lock (sync)
        {
            if (characters.TryGetValue(me, out var known) && known.AccountKey is not null && known.AccountKey == sessionKey) return;
            var key = Link([me]);
            if (known is not null)
            {
                if (known.AccountKey != key) { characters[me] = known with { AccountKey = key }; Save(); }
                return;
            }
            var worlds = Svc.Data.GetExcelSheet<World>();
            var home = worlds.GetRowOrDefault(Svc.PlayerState.HomeWorld.RowId);
            var cur = worlds.GetRowOrDefault(Svc.PlayerState.CurrentWorld.RowId);
            characters[me] = new Character(me, player.Name.TextValue, Svc.PlayerState.HomeWorld.RowId, home?.Name.ExtractText() ?? "",
                                           Svc.PlayerState.CurrentWorld.RowId, cur?.Name.ExtractText() ?? "", cur?.DataCenter.ValueNullable?.Name.ExtractText() ?? "",
                                           -1, DateTime.UtcNow, key);
            Save();
        }
    }

    /// <summary>
    /// The account key for characters seen together now: the session's key, any key those characters already had (merging keys
    /// that turn out to be the same account), or a new one. Becomes the session's key. Call under the lock.
    /// </summary>
    private string Link(IEnumerable<ulong> ids)
    {
        var keys = ids.Select(id => characters.TryGetValue(id, out var c) ? c.AccountKey : null).Where(k => k is not null).Cast<string>().ToHashSet();
        if (sessionKey is not null) keys.Add(sessionKey);
        var key = keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault() ?? Guid.NewGuid().ToString("N");
        foreach (var other in keys.Where(k => k != key))
            foreach (var c in characters.Values.Where(c => c.AccountKey == other).ToList())
                characters[c.ContentId] = c with { AccountKey = key };
        if (sessionKey != key)
        {
            sessionKey = key;
            try { File.WriteAllText(sessionFile, $"{GameSession}|{key}"); } catch { /* not critical */ }
        }
        return key;
    }

    private void Save() => File.WriteAllText(file, JsonSerializer.Serialize(characters.Values.ToList()));
}
