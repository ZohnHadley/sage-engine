#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.Json.Serialization;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Gameplay;

// Something to do, and something that notices you did it (docs/design/16 §3.5, TODO F24).
//
// **A quest is a list of stages, and a stage is a line of text with things that must be true.** It does
// not run code, it does not own entities, and it never moves anybody: it *watches*. The things it
// watches for are things the game already does — somebody died, something is in your bag — so a quest
// adds no new machinery to the rest of the engine, only a place to write down what counts.
//
// Where a stage comes from is deliberately open: dialogue sets one (F24), a trigger volume could, a
// console command does. The quest itself never decides to advance except by its objectives being met,
// which is the one rule that keeps "what state is this quest in" answerable from the journal alone.

// What a stage asks for (issue #28: an open vocabulary, where it was `ObjectiveKind { Kill, Have }`).
// Content names one by its `kind` — `kill` when it says none, as before — and a game declares more:
//
//   [QuestObjective("light_beacon", Plugin = "mygame")]
//   public sealed class LightBeacon : QuestObjective { … Notice(…) for its own happening … }
//
// An objective either *measures* its progress from the world now (`have`: what is in the bag) or
// *counts* happenings the quests plugin hears of — a kill, a conversation, where the player is standing
// (QuestHappening) — which the journal remembers, one number per objective (Journal.Entry.Progress).
// The engine's are `kill`, `have`, `reach` and `talk` (QuestObjectives.cs).
[Vocabulary("quest_objective", Key = "kind", Default = "kill")]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public abstract class QuestObjective
{
    [Property(Min = 1, Tooltip = "How many it takes")]
    public int Count = 1;
    [Property(Tooltip = "What the journal calls it; empty = built from its fields")]
    public string Text = "";

    // Progress measured from the world now, or null when the journal's count is the progress.
    public virtual int? Measure(World world, Entity player) => null;

    // How much a happening counts toward this objective; 0 when it is none of its business.
    public virtual int Notice(World world, in QuestHappening happened) => 0;

    // True for an objective asked every tick with the player (`reach`): a happening nothing sends.
    [JsonIgnore] public virtual bool Polls => false;

    // What the journal says when the content wrote no `text`: "kill 3 wolves".
    public abstract string Describe(World world, int count);

    // Where a map marks this objective while it is unmet, for a quest the player tracks (issue #349): the
    // entity of this name, wherever it is now...
    [Property(Tooltip = "Who or what a map marks while this is unmet: an entity's name; empty: the objective's own place, if it has one")]
    public string Target = "";

    // ...or, when it names none, a place of the objective's own in absolute metres (`reach`'s `at`).
    public virtual bool TryGetPlace(out Vector3 at)
    {
        at = default;
        return false;
    }
}

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public sealed class QuestObjectiveAttribute : VocabularyEntryAttribute<QuestObjective>
{
    public QuestObjectiveAttribute(string id) : base(id) { }
}

// Something the quests plugin heard of: `Kind` says what (the engine's are below; a game sends its own
// with Quests.Notice), `Subject` is what it happened to and `Actor` who did it.
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public readonly record struct QuestHappening(string Kind, Entity Subject, Entity Actor)
{
    public const string Killed = "killed";     // Subject died; Actor killed it (Died)
    public const string Talked = "talked";     // Subject said Node to Actor (Spoke)
    public const string Polled = "polled";     // every tick, Subject = Actor = the player
    public const string Entered = "entered";   // Actor walked into the trigger volume Subject (TriggerEntered, issue #391)
    public const string Asked = "asked";       // Actor asked Subject about Topic and was answered (TopicAsked, issue #391)

    public string Node { get; init; } = "";

    // The dialogue_topic asked about, on an `asked` happening.
    public RecordId Topic { get; init; }
}

public sealed class QuestStage
{
    public string Id = "";
    public string Text = "";                        // what the journal says while this stage is on
    public List<QuestObjective> Objectives = new();
    public string Next = "";                        // the stage that follows when they are all met
    // Who or what a map marks while this stage is on, by entity name (issue #349): the hermit a "go and
    // tell him" stage waits on. An objective names its own (QuestObjective.Target).
    public string Target = "";
    // Reaching this stage ends the quest. A stage that waits to be *told* it is over — "go and report" —
    // is not this: it has no objectives, no `next`, and something outside finishes it (a conversation
    // with `finishQuest`, a trigger, a command).
    public bool Done;

    // Timed stages and failure (issue #391). A stage with a time limit fails when the world's clock passes
    // it; one with `failWhen` fails the moment that condition holds (asked every tick, about the player).
    // Failing goes to `fail` — a stage of its own, usually marked `failed`, whose text says what went
    // wrong — or, when it names none, ends the quest failed where it stands.
    [Property(Min = 0, Unit = "h", Tooltip = "Game hours the stage may take before it fails; 0 = no limit")]
    public float TimeLimit;
    [Property(Tooltip = "Fails the stage the moment this holds, asked about the player (one condition; use all for several)")]
    public ICondition? FailWhen;
    [Property(Tooltip = "The stage failing goes to; empty = the quest ends failed at this one")]
    public string Fail = "";
    [Property(Tooltip = "Reaching this stage ends the quest failed")]
    public bool Failed;
}

[Record("quest", Plugin = "sage.gameplay.quests")]
public sealed class QuestRecord
{
    public string Label = "";
    public string Start = "";                       // empty = the first stage
    public List<QuestStage> Stages = new();

    public QuestStage? Stage(string id)
    {
        if (id.Length == 0) return Stages.Count > 0 ? Stages[0] : null;
        foreach (var stage in Stages)
            if (stage.Id == id) return stage;
        return null;
    }
}

// What the player is on, and how far. Saved, because a quest you have half done is the thing a save is
// most obviously *for* (09 §3.1).
[SavedResource("journal", Plugin = "sage.gameplay.quests")]
public sealed class Journal
{
    public sealed class Entry
    {
        public RecordId Quest { get; set; }
        public string Stage { get; set; } = "";
        public bool Finished { get; set; }
        // Kills counted since this stage began, one number per objective, by index. Counting is the
        // only thing a quest has to *remember*; everything else it can look up.
        public List<int> Progress { get; set; } = new();

        // The stages it has moved on from, in order (issue #349): what the journal's history shows.
        public List<string> History { get; set; } = new();

        // The player follows it: a map marks its targets (issue #349). Starting a quest tracks it.
        public bool Tracked { get; set; }

        // It ended badly (issue #391): a `failed` stage was reached, time ran out or `failWhen` held with
        // nowhere to go. Finished is true too — a failed quest is over — but IsFinished says "succeeded".
        public bool Failed { get; set; }

        // When the current stage runs out, on the world clock's total hours (WorldClock.Elapsed); 0 = untimed.
        public double Deadline { get; set; }

        // What the journal said at each stage as it was reached (issue #391): History's stages, then the
        // current one. Kept as written then, so a mod that rewrites a stage's text later does not rewrite
        // what the player was told. A save from before it has none, and the journal reads the record.
        public List<string> Told { get; set; } = new();
    }

    public List<Entry> Entries { get; set; } = new();

    public Entry? Of(RecordId quest)
    {
        foreach (var entry in Entries)
            if (entry.Quest == quest) return entry;
        return null;
    }
}

// A quest started, moved on, or finished — for a HUD to say so and a game to react.
[GameEvent]
public readonly record struct QuestChanged(RecordId Quest, string Stage, bool Finished)
{
    // It ended failed (issue #391); Finished is true with it.
    public bool Failed { get; init; }
}

public static class Quests
{
    // Puts a quest in the journal at its first stage. Starting one twice is not an error: it is what a
    // conversation does when the player asks about it again, and the answer is "you are already on it".
    public static bool Start(World world, RecordId quest)
    {
        if (JournalOf(world) is not { } journal) return false;   // a game without quests (issue #26)
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(quest, out QuestRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"quest:{quest}", $"No quest record {quest}");
            return false;
        }

        if (journal.Of(quest) != null) return false;

        var stage = record.Stage(record.Start);
        if (stage == null)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"quest-start:{quest}", $"{quest} has no stage to start at");
            return false;
        }

        var entry = new Journal.Entry { Quest = quest, Stage = stage.Id, Tracked = true };
        journal.Entries.Add(entry);
        Entered(world, entry, stage);
        world.Events.Send(new QuestChanged(quest, stage.Id, entry.Finished) { Failed = entry.Failed });
        Log.Info(LogCat.Gameplay, $"Quest started: {(record.Label.Length > 0 ? record.Label : quest.Name)}");
        Check(world, quest);        // a stage whose objectives are already met does not sit there
        return true;
    }

    // Moves it on by hand: what a conversation does when telling somebody you are done is the thing that
    // finishes the errand. An unknown stage finishes the quest rather than leaving it pointing nowhere.
    public static bool SetStage(World world, RecordId quest, string stage)
    {
        var entry = JournalOf(world)?.Of(quest);
        if (entry == null || entry.Finished) return false;
        if (!world.Resources.Get<RecordStore>().TryGet(quest, out QuestRecord record)) return false;

        var next = record.Stage(stage);
        if (next == null)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"quest-stage:{quest}:{stage}",
                $"{quest} has no stage '{stage}', so it is marked finished");
            entry.Finished = true;
            world.Events.Send(new QuestChanged(quest, entry.Stage, true));
            return false;
        }

        if (entry.Stage != next.Id)
        {
            // A save from before #391 told nothing: what the record says now is the best it can remember.
            for (int i = entry.Told.Count; i <= entry.History.Count; i++)
                entry.Told.Add(record.Stage(i < entry.History.Count ? entry.History[i] : entry.Stage)?.Text ?? "");
            entry.History.Add(entry.Stage);
        }
        entry.Stage = next.Id;
        entry.Progress.Clear();
        entry.Finished = next.Done && next.Objectives.Count == 0;
        Entered(world, entry, next);
        world.Events.Send(new QuestChanged(quest, entry.Stage, entry.Finished) { Failed = entry.Failed });
        if (entry.Failed) Log.Info(LogCat.Gameplay, $"Quest failed: {(record.Label.Length > 0 ? record.Label : quest.Name)}");
        Check(world, quest);
        return true;
    }

    // What reaching a stage writes down (issue #391): its text as it is now (one line per stage reached,
    // History's then the current one's; a stage set again is told again), its deadline, and whether it
    // ends the quest failed.
    private static void Entered(World world, Journal.Entry entry, QuestStage stage)
    {
        if (entry.Told.Count > entry.History.Count) entry.Told.RemoveRange(entry.History.Count, entry.Told.Count - entry.History.Count);
        while (entry.Told.Count < entry.History.Count) entry.Told.Add("");
        entry.Told.Add(stage.Text);
        entry.Deadline = stage.TimeLimit > 0f ? WorldClock.Of(world).Elapsed + stage.TimeLimit : 0.0;
        if (stage.Failed)
        {
            entry.Finished = true;
            entry.Failed = true;
        }
    }

    // Fails it (issue #391): what running out of time and `failWhen` do, and what a `fail_quest` action
    // does. The stage's `fail` stage when it names one, which is where the journal says what went wrong;
    // else the quest ends failed where it stands. False when it is not on the go.
    public static bool Fail(World world, RecordId quest)
    {
        var entry = JournalOf(world)?.Of(quest);
        if (entry == null || entry.Finished) return false;
        var records = world.Resources.Get<RecordStore>();
        var record = records.TryGet(quest, out QuestRecord found) ? found : null;
        var stage = record?.Stage(entry.Stage);
        // A `fail` stage not itself marked `failed` carries on — a second chance — which is the content's call.
        if (stage is { Fail.Length: > 0 } && record!.Stage(stage.Fail) is { } next && next.Id != entry.Stage)
            return SetStage(world, quest, next.Id);

        entry.Finished = true;
        entry.Failed = true;
        entry.Deadline = 0.0;
        world.Events.Send(new QuestChanged(quest, entry.Stage, true) { Failed = true });
        Log.Info(LogCat.Gameplay, $"Quest failed: {(record != null && record.Label.Length > 0 ? record.Label : quest.Name)}");
        return true;
    }

    public static bool IsFailed(World world, RecordId quest) => JournalOf(world)?.Of(quest)?.Failed == true;

    // Game hours left on the current stage's time limit, or null when it has none (or the quest is over).
    public static double? HoursLeft(World world, RecordId quest)
    {
        var entry = JournalOf(world)?.Of(quest);
        if (entry == null || entry.Finished || entry.Deadline <= 0.0) return null;
        return Math.Max(entry.Deadline - WorldClock.Of(world).Elapsed, 0.0);
    }

    // The timers and `failWhen`s of every stage on the go (issue #391), asked every tick by
    // QuestWatchSystem with the player. A timed stage loaded from a save made before it had a deadline
    // starts its clock now.
    internal static void Watch(World world, Entity player)
    {
        if (JournalOf(world) is not { Entries.Count: > 0 } journal) return;
        var records = world.Resources.Get<RecordStore>();
        for (int e = 0; e < journal.Entries.Count; e++)
        {
            var entry = journal.Entries[e];
            if (entry.Finished || !records.TryGet(entry.Quest, out QuestRecord record)) continue;
            var stage = record.Stage(entry.Stage);
            if (stage == null) continue;

            bool fail = false;
            if (stage.TimeLimit > 0f)
            {
                double now = WorldClock.Of(world).Elapsed;
                if (entry.Deadline <= 0.0) entry.Deadline = now + stage.TimeLimit;
                else if (now >= entry.Deadline) fail = true;
            }
            if (!fail && stage.FailWhen != null)
                fail = Conditions.Test(stage.FailWhen, new ConditionContext(world, player, default), out _);
            if (fail) Fail(world, entry.Quest);
        }
    }

    // Ends it, wherever it had got to. What a conversation does when the errand is reported: the quest
    // does not know what "telling somebody" is, and should not have to.
    public static bool Finish(World world, RecordId quest)
    {
        var entry = JournalOf(world)?.Of(quest);
        if (entry == null || entry.Finished) return false;
        entry.Finished = true;
        world.Events.Send(new QuestChanged(quest, entry.Stage, true));

        var records = world.Resources.Get<RecordStore>();
        string label = records.TryGet(quest, out QuestRecord record) && record.Label.Length > 0 ? record.Label : quest.Name;
        Log.Info(LogCat.Gameplay, $"Quest finished: {label}");
        return true;
    }

    // Follows a quest or stops (issue #349): a map marks a tracked quest's targets. False when it is not
    // in the journal.
    public static bool Track(World world, RecordId quest, bool tracked = true)
    {
        var entry = JournalOf(world)?.Of(quest);
        if (entry == null) return false;
        entry.Tracked = tracked;
        return true;
    }

    public static bool IsTracked(World world, RecordId quest) => JournalOf(world)?.Of(quest)?.Tracked == true;

    public static bool IsActive(World world, RecordId quest)
    {
        var entry = JournalOf(world)?.Of(quest);
        return entry != null && !entry.Finished;
    }

    // Finished and not failed: done, as the player meant it (a failed quest is over, but not this).
    public static bool IsFinished(World world, RecordId quest) => JournalOf(world)?.Of(quest) is { Finished: true, Failed: false };

    // Never started: not in the journal, or no journal at all.
    public static bool IsNotStarted(World world, RecordId quest) => JournalOf(world)?.Of(quest) == null;

    public static string StageOf(World world, RecordId quest) => JournalOf(world)?.Of(quest)?.Stage ?? "";

    // Whether the quest has got as far as `stage`: it is on that stage or one after it in the record's
    // list, or it is finished (Morrowind's "journal ≥ 30"; the `quest` condition's `atLeast`, issue #89).
    // A stage the quest does not have is never reached, short of finishing.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // stage 2 logic (#89): may change before 1.0
    public static bool HasReached(World world, RecordId quest, string stage)
    {
        var entry = JournalOf(world)?.Of(quest);
        if (entry == null) return false;
        if (entry.Finished && !entry.Failed) return true;
        if (!world.Resources.Get<RecordStore>().TryGet(quest, out QuestRecord record)) return false;
        int at = -1, wanted = -1;
        for (int i = 0; i < record.Stages.Count; i++)
        {
            if (record.Stages[i].Id == entry.Stage) at = i;
            if (record.Stages[i].Id == stage) wanted = i;
        }
        return wanted >= 0 && at >= wanted;
    }

    // The world's journal, or null in a game without the quests plugin: then nobody is on anything,
    // and asking is not an error (dialogue asks, issue #26).
    public static Journal? JournalOf(World world) =>
        world.Resources.TryGet<Journal>(out var journal) ? journal : null;

    // How far along one objective is: what has been counted, or what is in the bag. The journal shows
    // this and so does a test, which is why it is a question rather than a field.
    public static int Progress(World world, Entity carrier, RecordId quest, int objective)
    {
        var entry = JournalOf(world)?.Of(quest);
        if (entry == null || !world.Resources.Get<RecordStore>().TryGet(quest, out QuestRecord record)) return 0;
        var stage = record.Stage(entry.Stage);
        if (stage == null || objective < 0 || objective >= stage.Objectives.Count) return 0;

        var wanted = stage.Objectives[objective];
        if (wanted.Measure(world, carrier) is int measured) return measured;
        return objective < entry.Progress.Count ? entry.Progress[objective] : 0;
    }

    // The death seam's other half (16 §3.3): a kill is the commonest objective in any game, and the only
    // one the engine can count without being told. QuestDeathSystem calls it for every `Died` (#26).
    public static void OnKilled(World world, Entity victim, Entity killer) =>
        Notice(world, new QuestHappening(QuestHappening.Killed, victim, killer));

    // Something happened that an objective may count (issue #28): every active stage's objectives are
    // asked how much it counts, and a quest whose objectives are then all met moves on. The engine sends
    // kills, conversations and the player's whereabouts; a game sends its own kinds.
    public static void Notice(World world, in QuestHappening happened)
    {
        if (JournalOf(world) is not { Entries.Count: > 0 } journal) return;
        if (Count(world, journal, in happened, pollsOnly: false)) Check(world);
    }

    // The objectives that watch the player (`reach`), asked with where the player is (QuestWatchSystem).
    public static void Poll(World world, Entity player)
    {
        if (player.IsNull || JournalOf(world) is not { Entries.Count: > 0 } journal) return;
        var happened = new QuestHappening(QuestHappening.Polled, player, player);
        if (Count(world, journal, in happened, pollsOnly: true)) Check(world);
    }

    private static bool Count(World world, Journal journal, in QuestHappening happened, bool pollsOnly)
    {
        var records = world.Resources.Get<RecordStore>();
        bool counted = false;
        foreach (var entry in journal.Entries)
        {
            if (entry.Finished || !records.TryGet(entry.Quest, out QuestRecord record)) continue;
            var stage = record.Stage(entry.Stage);
            if (stage == null) continue;

            for (int i = 0; i < stage.Objectives.Count; i++)
            {
                var objective = stage.Objectives[i];
                if (pollsOnly && !objective.Polls) continue;
                int amount = objective.Notice(world, in happened);
                if (amount <= 0) continue;

                while (entry.Progress.Count <= i) entry.Progress.Add(0);
                int wanted = Math.Max(objective.Count, 1);
                if (entry.Progress[i] >= wanted) continue;   // done; standing in the circle adds nothing
                entry.Progress[i] = Math.Min(entry.Progress[i] + amount, wanted);
                counted = true;
            }
        }
        return counted;
    }

    // Are this stage's objectives all met? If so, move on — which is the only way a quest advances by
    // itself, and the reason the journal alone answers "where am I in this".
    public static void Check(World world, RecordId quest = default)
    {
        if (JournalOf(world) is not { } journal) return;
        var records = world.Resources.Get<RecordStore>();
        var carrier = PlayerOf(world);

        for (int e = 0; e < journal.Entries.Count; e++)
        {
            var entry = journal.Entries[e];
            if (entry.Finished) continue;
            if (!quest.IsEmpty && entry.Quest != quest) continue;
            if (!records.TryGet(entry.Quest, out QuestRecord record)) continue;

            var stage = record.Stage(entry.Stage);
            if (stage == null || stage.Objectives.Count == 0) continue;

            bool all = true;
            for (int i = 0; i < stage.Objectives.Count && all; i++)
                all = Progress(world, carrier, entry.Quest, i) >= Math.Max(stage.Objectives[i].Count, 1);
            if (!all) continue;

            if (stage.Next.Length > 0) SetStage(world, entry.Quest, stage.Next);
            else
            {
                entry.Finished = true;
                world.Events.Send(new QuestChanged(entry.Quest, entry.Stage, true));
                Log.Info(LogCat.Gameplay, $"Quest finished: {(record.Label.Length > 0 ? record.Label : entry.Quest.Name)}");
            }
        }
    }

    // What an objective says in a journal, when the content did not write the line itself.
    public static string Describe(World world, QuestObjective objective, int progress) =>
        objective.Text.Length > 0 ? objective.Text : objective.Describe(world, Math.Max(objective.Count, 1));

    // What content calls a record: its label when it has one (an item, a faction), else its name.
    public static string Label(World world, RecordId id)
    {
        if (id.IsEmpty) return "something";
        var records = world.Resources.Get<RecordStore>();
        if (records.TypeNameOf(typeof(ItemRecord)) != null && records.TryGet(id, out ItemRecord item) && item.Label.Length > 0) return item.Label;
        if (Factions.TryGetFaction(records, id, out var faction) && faction.Label.Length > 0) return faction.Label;
        return id.Name;
    }

    private static Entity PlayerOf(World world)
    {
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
            return entity;
        return default;
    }
}

// Kills a quest counts (16 §3.5, issue #26): reads `Died` rather than being called by the effect
// system, so a game without quests has nothing here to call. Before the rules, while the victim still
// exists to ask its faction and prefab.
[System("sage.quests.deaths", Phase.Gameplay, After = new[] { "sage.effects.tick" }, Before = new[] { "sage.effects.deaths" })]
internal sealed class QuestDeathSystem : ISystem
{
    private readonly EventReader<Died> _died;

    public QuestDeathSystem(World world) => _died = world.Events.Reader<Died>(this);

    public void Run(in SystemContext ctx)
    {
        if (!_died.HasPending) return;
        foreach (ref readonly var died in _died.Read())
            Quests.OnKilled(ctx.World, died.Victim, died.Killer);
    }
}
