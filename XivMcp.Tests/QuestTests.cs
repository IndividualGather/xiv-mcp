using System.Collections.Generic;
using System.Linq;
using XivMcp.Quests;

namespace XivMcp.Tests;

public class QuestChainTests
{
    private static readonly Dictionary<uint, QuestInfo> Quests = new QuestInfo[]
    {
        new(65536 + 1, "Start"),
        new(65536 + 2, "Middle") { Previous = [65536 + 1] },
        new(65536 + 3, "Side") { Previous = [65536 + 1] },
        new(65536 + 4, "Goal") { Previous = [65536 + 2, 65536 + 3] },
        new(65536 + 5, "Gridania path") { Previous = [65536 + 1] },
        new(65536 + 6, "Limsa path") { Previous = [65536 + 1] },
        new(65536 + 7, "Either") { Previous = [65536 + 5, 65536 + 6], PreviousJoin = QuestJoin.Any },
        new(65536 + 8, "Rival") { Previous = [65536 + 1] },
        new(65536 + 9, "Locked out") { Previous = [65536 + 1], Locks = [65536 + 8] },
        new(65536 + 10, "Loop A") { Previous = [65536 + 11] },
        new(65536 + 11, "Loop B") { Previous = [65536 + 10] },
    }.ToDictionary(q => q.Id);

    private static QuestPlan Plan(uint target, params uint[] complete) =>
        QuestChain.Plan(65536 + target, id => Quests.GetValueOrDefault(id), id => complete.Contains(id - 65536));

    private static string[] Names(QuestPlan plan) => plan.Quests.Select(q => q.Name).ToArray();

    [Fact]
    public void Prerequisites_come_first_and_each_only_once()
    {
        var plan = Plan(4);
        Assert.Equal(["Start", "Middle", "Side", "Goal"], Names(plan));
        Assert.Empty(plan.Problems);
    }

    [Fact]
    public void Completed_quests_are_left_out()
    {
        Assert.Equal(["Side", "Goal"], Names(Plan(4, 1, 2)));
        Assert.Empty(Plan(4, 1, 2, 3, 4).Quests);
    }

    [Fact]
    public void One_of_several_is_enough_when_any_will_do()
    {
        Assert.Equal(["Either"], Names(Plan(7, 1, 6)));
        Assert.Equal(["Start", "Gridania path", "Either"], Names(Plan(7)));
        var preferLimsa = QuestChain.Plan(65536 + 7, id => Quests.GetValueOrDefault(id), _ => false, preferred: id => id == 65536 + 6);
        Assert.Equal(["Start", "Limsa path", "Either"], Names(preferLimsa));
    }

    [Fact]
    public void A_quest_locked_out_by_another_is_a_problem()
    {
        Assert.Contains(Plan(9, 1, 8).Problems, p => p.Contains("Locked out") && p.Contains("Rival"));
        Assert.Empty(Plan(9, 1).Problems);
    }

    [Fact]
    public void Unknown_quests_and_loops_do_not_hang()
    {
        Assert.Contains(QuestChain.Plan(99, _ => null, _ => false).Problems, p => p.Contains("99"));
        Assert.Equal(2, Plan(10).Quests.Count);
    }

    [Fact]
    public void Questionable_uses_the_quest_number_without_the_sheet_offset()
    {
        Assert.Equal("4522", new QuestInfo(70058, "The Ultimate Weapon").QuestionableId);
    }
}

public class QuestionablePriorityTests
{
    [Fact]
    public void Round_trips_the_priority_export()
    {
        var text = QuestionablePriority.Encode(["1219", "A1x2", "4522"]);
        Assert.StartsWith("qst:priority:", text);
        Assert.Equal(["1219", "A1x2", "4522"], QuestionablePriority.Decode(text));
    }

    [Fact]
    public void Reads_the_older_format_and_nothing()
    {
        Assert.Equal(["5"], QuestionablePriority.Decode("qst:v1:" + System.Convert.ToBase64String("5"u8.ToArray())));
        Assert.Empty(QuestionablePriority.Decode(""));
        Assert.Empty(QuestionablePriority.Decode(null));
        Assert.Empty(QuestionablePriority.Decode("qst:priority:"));
        Assert.Empty(QuestionablePriority.Decode("garbage"));
    }
}

public class TribePlanTests
{
    private static TribeQuest Daily(uint id, bool ready = true, bool accepted = false) =>
        new(new QuestInfo(65536 + id, $"Daily {id}") { Repeatable = true, Society = 1, SocietyRank = 1 }, accepted, ready);

    private static TribeQuest Story(uint id, byte rank, bool ready = true, bool accepted = false) =>
        new(new QuestInfo(65536 + id, $"Rank up {id}") { Society = 1, SocietyRank = rank }, accepted, ready);

    private static string[] Names(IEnumerable<QuestInfo> quests) => quests.Select(q => q.Name).ToArray();

    [Fact]
    public void Accepted_quests_first_then_the_rank_up_then_dailies()
    {
        var plan = TribePlan.Next([Daily(1), Story(10, 2), Daily(2, accepted: true, ready: false), Daily(3)], allowances: 5);
        Assert.Equal(["Daily 2", "Rank up 10", "Daily 1", "Daily 3"], Names(plan));
    }

    [Fact]
    public void Dailies_need_allowances_rank_ups_do_not()
    {
        Assert.Equal(["Rank up 10", "Daily 1"], Names(TribePlan.Next([Daily(1), Daily(3), Story(10, 2)], allowances: 1)));
        Assert.Equal(["Daily 2", "Rank up 10"], Names(TribePlan.Next([Daily(1), Daily(2, accepted: true), Story(10, 2)], allowances: 0)));
    }

    [Fact]
    public void Quests_not_offered_today_are_left_out()
    {
        Assert.Empty(TribePlan.Next([Daily(1, ready: false), Story(10, 2, ready: false)], allowances: 12));
    }
}

public class QuestJobTests
{
    private const uint Min = 16, Btn = 17, Fsh = 18, Pld = 19, Blm = 25;
    private static readonly JobOption[] Sets = [new(Min, 100), new(Btn, 95), new(Fsh, 100), new(Pld, 100), new(Blm, 80)];

    [Fact]
    public void Gathering_quests_are_never_done_as_fisher()
    {
        Assert.Equal(Min, QuestJobs.Pick([Min, Btn, Fsh], Fsh, Sets, 90));
        Assert.Equal(Btn, QuestJobs.Pick([Min, Btn, Fsh], Btn, Sets, 90));
        Assert.Equal(Fsh, QuestJobs.Pick([Fsh], Min, Sets, 90));
    }

    [Fact]
    public void The_current_job_stays_when_it_may_do_the_quest()
    {
        Assert.Equal(Blm, QuestJobs.Pick([Pld, Blm], Blm, Sets, 70));
        Assert.Equal(Pld, QuestJobs.Pick([Pld, Blm], Blm, Sets, 90));
        Assert.Equal(Pld, QuestJobs.Pick([Pld, Blm], Min, Sets, 50));
    }

    [Fact]
    public void Only_jobs_with_a_gearset_can_be_picked()
    {
        Assert.Null(QuestJobs.Pick([30], Min, Sets, 50));
        Assert.Null(QuestJobs.Pick([], Min, Sets, 50));
    }

    [Fact]
    public void A_gathering_quest_accepted_as_fisher_cannot_be_done()
    {
        Assert.True(QuestJobs.AcceptedAsFisherInstead([Min, Btn, Fsh], Fsh));
        Assert.False(QuestJobs.AcceptedAsFisherInstead([Min, Btn, Fsh], Min));
        Assert.False(QuestJobs.AcceptedAsFisherInstead([Fsh], Fsh));
    }
}

public class AcceptedQuestMatchTests
{
    private static readonly QuestInfo[] Journal = [new(70800, "Toil for the Soil"), new(71051, "A Familiar Issue"), new(69637, "Irresistible"), new(69576, "A New Path of Resistance")];

    [Fact]
    public void Finds_an_accepted_quest_by_name_or_id()
    {
        Assert.Equal(70800u, QuestMatch.Accepted("toil for the soil", Journal).Id);
        Assert.Equal(70800u, QuestMatch.Accepted("Toil", Journal).Id);
        Assert.Equal(70800u, QuestMatch.Accepted("70800", Journal).Id);
        Assert.Equal(70800u, QuestMatch.Accepted("5264", Journal).Id);
        Assert.Equal(69637u, QuestMatch.Accepted("Irresistible", Journal).Id);
    }

    [Fact]
    public void Refuses_what_is_not_accepted_or_not_clear()
    {
        Assert.Contains("not in your journal", Assert.Throws<System.ArgumentException>(() => QuestMatch.Accepted("Rival", Journal)).Message);
        Assert.Contains("Irresistible", Assert.Throws<System.ArgumentException>(() => QuestMatch.Accepted("resist", Journal)).Message);
    }
}

public class StallWatchTests
{
    private static readonly System.DateTime T0 = new(2026, 10, 5, 19, 0, 0, System.DateTimeKind.Utc);
    private static readonly System.Numerics.Vector3 Here = new(10, 0, 10);

    [Fact]
    public void Standing_still_for_too_long_is_a_stall()
    {
        var watch = new StallWatch(System.TimeSpan.FromMinutes(2));
        Assert.False(watch.Stalled(T0, Here, busy: false));
        Assert.False(watch.Stalled(T0.AddSeconds(90), Here + new System.Numerics.Vector3(0.5f, 0, 0), busy: false));
        Assert.True(watch.Stalled(T0.AddSeconds(121), Here, busy: false));
    }

    [Fact]
    public void Moving_or_being_busy_is_progress()
    {
        var watch = new StallWatch(System.TimeSpan.FromMinutes(2));
        watch.Stalled(T0, Here, busy: false);
        Assert.False(watch.Stalled(T0.AddSeconds(100), Here + new System.Numerics.Vector3(5, 0, 0), busy: false));
        Assert.False(watch.Stalled(T0.AddSeconds(200), Here + new System.Numerics.Vector3(5, 0, 0), busy: false));
        Assert.False(watch.Stalled(T0.AddSeconds(300), Here + new System.Numerics.Vector3(5, 0, 0), busy: true));
        Assert.False(watch.Stalled(T0.AddSeconds(400), Here + new System.Numerics.Vector3(5, 0, 0), busy: false));
        Assert.True(watch.Stalled(T0.AddSeconds(521), Here + new System.Numerics.Vector3(5, 0, 0), busy: false));
    }

    [Fact]
    public void After_a_stall_it_starts_over()
    {
        var watch = new StallWatch(System.TimeSpan.FromMinutes(2));
        watch.Stalled(T0, Here, busy: false);
        Assert.True(watch.Stalled(T0.AddSeconds(130), Here, busy: false));
        Assert.False(watch.Stalled(T0.AddSeconds(140), Here, busy: false));
    }
}

public class QuestionableMessageTests
{
    [Fact]
    public void Reads_what_questionable_says_in_chat()
    {
        const string duty = "[QST v15.756.3.30] Duty - Could not use AutoDuty to queue for The Clyteum, required item level: 750, current item level: 742.";
        Assert.Equal("Duty - Could not use AutoDuty to queue for The Clyteum, required item level: 750, current item level: 742.", QuestionableMessage.Text(duty));
        Assert.True(QuestionableMessage.NeedsPlayer(duty));
        Assert.True(QuestionableMessage.NeedsPlayer("[QST v1] Single player duty - The Big Sleep"));
        Assert.True(QuestionableMessage.NeedsPlayer("[QST v1] Manual interaction required - Use the lever"));
        Assert.False(QuestionableMessage.NeedsPlayer("[QST v15.756.3.30] Set next quest to 5264 (Toil for the Soil)."));
        Assert.Null(QuestionableMessage.Text("You obtain 2,697 gil."));
    }
}

public class QuestBatchTests
{
    private static QuestInfo Q(uint id) => new(65536 + id, $"Quest {id}") { Repeatable = true, Society = 1 };

    [Fact]
    public void Quests_ready_together_are_accepted_first()
    {
        var batch = new[] { Q(1), Q(2), Q(3), Q(4) };
        var ready = new HashSet<uint> { 65536 + 2, 65536 + 3, 65536 + 4 };
        Assert.Equal([65536 + 2u, 65536 + 3, 65536 + 4], QuestBatch.AcceptTogether(batch, q => ready.Contains(q.Id)).Select(q => q.Id));
    }

    [Fact]
    public void One_quest_alone_is_just_done()
    {
        Assert.Empty(QuestBatch.AcceptTogether([Q(1), Q(2)], q => q.Id == 65536 + 2));
        Assert.Empty(QuestBatch.AcceptTogether([], _ => true));
    }
}
