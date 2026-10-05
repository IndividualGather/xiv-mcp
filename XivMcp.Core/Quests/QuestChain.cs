using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XivMcp.Quests;

/// <summary>How a quest's list of quests combines (the Quest sheet's PreviousQuestJoin and QuestLockJoin).</summary>
public enum QuestJoin : byte { None = 0, All = 1, Any = 2 }

/// <summary>What XIV MCP needs to know of a quest from the game's Quest sheet. <see cref="Id"/> is the sheet row (65536 and up).</summary>
public sealed record QuestInfo(uint Id, string Name)
{
    /// <summary>The quests to complete first, and whether all of them or one is needed.</summary>
    public IReadOnlyList<uint> Previous { get; init; } = [];
    public QuestJoin PreviousJoin { get; init; } = QuestJoin.All;

    /// <summary>Quests that shut this one out once completed (such as the other Grand Companies' quests).</summary>
    public IReadOnlyList<uint> Locks { get; init; } = [];
    public QuestJoin LockJoin { get; init; } = QuestJoin.Any;

    public int Level { get; init; }
    public bool MainScenario { get; init; }

    /// <summary>The jobs (ClassJob rows) that may accept the quest; empty when any may.</summary>
    public IReadOnlyList<uint> Jobs { get; init; } = [];

    /// <summary>The allied society (beast tribe) the quest belongs to, 0 for none, and the rank it is for.</summary>
    public byte Society { get; init; }
    public byte SocietyRank { get; init; }

    /// <summary>A quest that can be done again (allied society dailies).</summary>
    public bool Repeatable { get; init; }

    /// <summary>The id Questionable uses: the quest number without the sheet's offset.</summary>
    public string QuestionableId => (Id & 0xFFFF).ToString();
}

/// <summary>The quests to do, in order, and why some cannot be done.</summary>
public sealed record QuestPlan(IReadOnlyList<QuestInfo> Quests, IReadOnlyList<string> Problems);

/// <summary>A quest and everything it needs first.</summary>
public static class QuestChain
{
    /// <summary>
    /// The quests that are not complete yet, prerequisites before the quests that need them, the target last. Where one of several
    /// quests is enough, a <paramref name="preferred"/> one is picked (such as one Questionable knows), else the first.
    /// </summary>
    public static QuestPlan Plan(uint target, Func<uint, QuestInfo?> find, Func<uint, bool> isComplete, Func<uint, bool>? preferred = null)
    {
        var order = new List<QuestInfo>();
        var problems = new List<string>();
        var seen = new HashSet<uint>();

        void Visit(uint id)
        {
            if (id == 0 || !seen.Add(id) || isComplete(id)) return;
            if (find(id) is not { } quest)
            {
                problems.Add($"Quest {id} is not in the game data.");
                return;
            }

            var locks = quest.Locks.Where(l => l != 0).ToList();
            var locked = locks.Count > 0 && (quest.LockJoin == QuestJoin.All ? locks.All(isComplete) : locks.Any(isComplete));
            if (locked)
                problems.Add($"{quest.Name} can no longer be done: {string.Join(" and ", locks.Where(isComplete).Select(l => find(l)?.Name ?? l.ToString()))} was completed instead.");

            var previous = quest.Previous.Where(p => p != 0).ToList();
            if (quest.PreviousJoin == QuestJoin.Any && previous.Count > 1)
            {
                if (!previous.Any(isComplete))
                    Visit(previous.FirstOrDefault(q => preferred?.Invoke(q) == true) is var pick and not 0 ? pick : previous[0]);
            }
            else
            {
                foreach (var p in previous) Visit(p);
            }

            order.Add(quest);
        }

        Visit(target);
        return new QuestPlan(order, problems);
    }
}

/// <summary>Questionable's priority list as it exports and imports it: "qst:priority:" and the ids, separated by ';', in base64.</summary>
public static class QuestionablePriority
{
    private const string Prefix = "qst:priority:";
    private const string LegacyPrefix = "qst:v1:";

    public static string Encode(IEnumerable<string> ids) => Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(';', ids)));

    public static IReadOnlyList<string> Decode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var body = text.StartsWith(Prefix, StringComparison.Ordinal) ? text[Prefix.Length..]
                 : text.StartsWith(LegacyPrefix, StringComparison.Ordinal) ? text[LegacyPrefix.Length..]
                 : null;
        if (string.IsNullOrEmpty(body)) return [];
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(body)).Split(';', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (FormatException)
        {
            return [];
        }
    }
}

/// <summary>An allied society quest and where it stands today: accepted, or offered and ready to accept.</summary>
public sealed record TribeQuest(QuestInfo Quest, bool Accepted, bool Ready);

/// <summary>Which allied society quests to do next.</summary>
public static class TribePlan
{
    /// <summary>
    /// Accepted quests first (they are under way, and accepted dailies already used their allowance), then the rank-up and story
    /// quests that are ready (a full reputation bar gains nothing more until the rank-up is done; they need no allowance), then the
    /// day's dailies, as many as the allowances left allow.
    /// </summary>
    public static IReadOnlyList<QuestInfo> Next(IEnumerable<TribeQuest> quests, int allowances)
    {
        var all = quests.ToList();
        var accepted = all.Where(q => q.Accepted).Select(q => q.Quest);
        var story = all.Where(q => !q.Accepted && q.Ready && !q.Quest.Repeatable).Select(q => q.Quest).OrderBy(q => q.SocietyRank).ThenBy(q => q.Id);
        var dailies = all.Where(q => !q.Accepted && q.Ready && q.Quest.Repeatable).Select(q => q.Quest).OrderBy(q => q.Id).Take(Math.Max(0, allowances));
        return [.. accepted, .. story, .. dailies];
    }
}

/// <summary>A job the character has a gearset for, and its level.</summary>
public sealed record JobOption(uint JobId, int Level);

/// <summary>Which job to accept a quest on: quests remember the job they were accepted on.</summary>
public static class QuestJobs
{
    public const uint Miner = 16, Botanist = 17, Fisher = 18;

    /// <summary>
    /// The job to accept a quest on, from the jobs the quest allows: the current one when it is allowed and has the level, else the
    /// highest level allowed job with a gearset. Gathering quests are never done as Fisher when Miner or Botanist may do them:
    /// Questionable's paths gather as Miner or Botanist. Null when no allowed job has a gearset.
    /// </summary>
    public static uint? Pick(IReadOnlyCollection<uint> allowed, uint current, IEnumerable<JobOption> gearsets, int questLevel)
    {
        var usable = allowed.Where(j => j != Fisher || !GathersOnLand(allowed)).ToHashSet();
        var options = gearsets.Where(o => usable.Contains(o.JobId)).GroupBy(o => o.JobId).Select(g => g.First()).ToList();
        if (options.Count == 0) return null;
        if (options.FirstOrDefault(o => o.JobId == current) is { } now && now.Level >= questLevel) return current;
        return options.OrderByDescending(o => o.Level >= questLevel).ThenByDescending(o => o.Level).First().JobId;
    }

    /// <summary>A gathering quest that was accepted as Fisher although Miner or Botanist may do it: Questionable cannot finish it.</summary>
    public static bool AcceptedAsFisherInstead(IReadOnlyCollection<uint> allowed, uint acceptedJob) =>
        acceptedJob == Fisher && GathersOnLand(allowed);

    private static bool GathersOnLand(IReadOnlyCollection<uint> allowed) => allowed.Contains(Miner) || allowed.Contains(Botanist);
}

/// <summary>Picking one quest out of a list by what the player calls it.</summary>
public static class QuestMatch
{
    /// <summary>
    /// The accepted quest meant by a name or id (the sheet row, or the quest number): an exact name first, else the one name that
    /// contains the text. Throws <see cref="ArgumentException"/> with what to say when none or several fit.
    /// </summary>
    public static QuestInfo Accepted(string query, IReadOnlyList<QuestInfo> accepted)
    {
        query = query.Trim();
        if (uint.TryParse(query, out var id))
            return accepted.FirstOrDefault(q => q.Id == id || (q.Id & 0xFFFF) == id && id < 65536)
                   ?? throw new ArgumentException($"Quest {id} is not in your journal.");
        if (accepted.FirstOrDefault(q => q.Name.Equals(query, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        var partial = accepted.Where(q => q.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count switch
        {
            1 => partial[0],
            0 => throw new ArgumentException($"'{query}' is not in your journal."),
            _ => throw new ArgumentException($"'{query}' fits several quests in your journal: {string.Join(", ", partial.Select(q => q.Name))}."),
        };
    }
}

/// <summary>
/// Notices when automation stopped making progress: the character stood in the same spot, not busy (talking, fighting, a
/// cutscene, a zone change), for longer than the limit. After reporting a stall it starts counting again.
/// </summary>
public sealed class StallWatch(TimeSpan limit, float distance = 2f)
{
    private System.Numerics.Vector3? anchor;
    private DateTime since;

    public bool Stalled(DateTime now, System.Numerics.Vector3 position, bool busy)
    {
        if (busy || anchor is not { } a || System.Numerics.Vector3.Distance(a, position) > distance)
        {
            anchor = position;
            since = now;
            return false;
        }
        if (now - since <= limit) return false;
        anchor = null;
        return true;
    }
}

/// <summary>What Questionable prints in chat, tagged "[QST v…]": why it stopped or what it waits for.</summary>
public static class QuestionableMessage
{
    private const string Tag = "[QST";

    /// <summary>The message without its tag, or null when Questionable did not send it.</summary>
    public static string? Text(string chat)
    {
        var start = chat.IndexOf(Tag, StringComparison.Ordinal);
        if (start < 0) return null;
        var end = chat.IndexOf(']', start);
        return end < 0 ? null : chat[(end + 1)..].Trim();
    }

    /// <summary>The notices Questionable sends when a step needs the player: a duty it cannot run (or have AutoDuty run), a solo duty, or something to do by hand.</summary>
    private static readonly string[] NeedsPlayerNotices = ["Duty", "Single player duty", "Manual interaction required"];

    public static bool NeedsPlayer(string chat) =>
        Text(chat) is { } text && NeedsPlayerNotices.Any(n => text.StartsWith(n, StringComparison.OrdinalIgnoreCase));
}
