#nullable enable
using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace sage_engine;

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

public enum ObjectiveKind
{
    Kill,       // members of a faction, or entities from a prefab
    Have,       // an item, in the bag, right now
}

public sealed class QuestObjective
{
    public ObjectiveKind Kind = ObjectiveKind.Kill;
    public RecordId Faction;        // Kill: whose
    public RecordId Prefab;         // Kill: or what, exactly
    public RecordId Item;           // Have: which
    public int Count = 1;
    public string Text = "";        // what the journal calls it; empty = built from the fields
}

public sealed class QuestStage
{
    public string Id = "";
    public string Text = "";                        // what the journal says while this stage is on
    public List<QuestObjective> Objectives = new();
    public string Next = "";                        // the stage that follows when they are all met
    // Reaching this stage ends the quest. A stage that waits to be *told* it is over — "go and report" —
    // is not this: it has no objectives, no `next`, and something outside finishes it (a conversation
    // with `finishQuest`, a trigger, a command).
    public bool Done;
}

[Record("quest", Plugin = "sage.gameplay.factions")]
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
[SavedResource("journal", Plugin = "sage.gameplay.factions")]
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
public readonly record struct QuestChanged(RecordId Quest, string Stage, bool Finished);

public static class Quests
{
    // Puts a quest in the journal at its first stage. Starting one twice is not an error: it is what a
    // conversation does when the player asks about it again, and the answer is "you are already on it".
    public static bool Start(World world, RecordId quest)
    {
        var records = world.Resources.Get<RecordStore>();
        if (!records.TryGet(quest, out QuestRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"quest:{quest}", $"No quest record {quest}");
            return false;
        }

        var journal = world.Resources.Get<Journal>();
        if (journal.Of(quest) != null) return false;

        var stage = record.Stage(record.Start);
        if (stage == null)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"quest-start:{quest}", $"{quest} has no stage to start at");
            return false;
        }

        journal.Entries.Add(new Journal.Entry { Quest = quest, Stage = stage.Id });
        world.Events.Send(new QuestChanged(quest, stage.Id, false));
        Log.Info(LogCat.Gameplay, $"Quest started: {(record.Label.Length > 0 ? record.Label : quest.Name)}");
        Check(world, quest);        // a stage whose objectives are already met does not sit there
        return true;
    }

    // Moves it on by hand: what a conversation does when telling somebody you are done is the thing that
    // finishes the errand. An unknown stage finishes the quest rather than leaving it pointing nowhere.
    public static bool SetStage(World world, RecordId quest, string stage)
    {
        var entry = world.Resources.Get<Journal>().Of(quest);
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

        entry.Stage = next.Id;
        entry.Progress.Clear();
        entry.Finished = next.Done && next.Objectives.Count == 0;
        world.Events.Send(new QuestChanged(quest, entry.Stage, entry.Finished));
        Check(world, quest);
        return true;
    }

    // Ends it, wherever it had got to. What a conversation does when the errand is reported: the quest
    // does not know what "telling somebody" is, and should not have to.
    public static bool Finish(World world, RecordId quest)
    {
        var entry = world.Resources.Get<Journal>().Of(quest);
        if (entry == null || entry.Finished) return false;
        entry.Finished = true;
        world.Events.Send(new QuestChanged(quest, entry.Stage, true));

        var records = world.Resources.Get<RecordStore>();
        string label = records.TryGet(quest, out QuestRecord record) && record.Label.Length > 0 ? record.Label : quest.Name;
        Log.Info(LogCat.Gameplay, $"Quest finished: {label}");
        return true;
    }

    public static bool IsActive(World world, RecordId quest)
    {
        var entry = world.Resources.Get<Journal>().Of(quest);
        return entry != null && !entry.Finished;
    }

    public static bool IsFinished(World world, RecordId quest) => world.Resources.Get<Journal>().Of(quest)?.Finished == true;

    public static string StageOf(World world, RecordId quest) => world.Resources.Get<Journal>().Of(quest)?.Stage ?? "";

    // How far along one objective is: what has been counted, or what is in the bag. The journal shows
    // this and so does a test, which is why it is a question rather than a field.
    public static int Progress(World world, Entity carrier, RecordId quest, int objective)
    {
        var entry = world.Resources.Get<Journal>().Of(quest);
        if (entry == null || !world.Resources.Get<RecordStore>().TryGet(quest, out QuestRecord record)) return 0;
        var stage = record.Stage(entry.Stage);
        if (stage == null || objective < 0 || objective >= stage.Objectives.Count) return 0;

        var wanted = stage.Objectives[objective];
        if (wanted.Kind == ObjectiveKind.Have)
            return carrier.IsNull ? 0 : world.CountOf(carrier, wanted.Item);
        return objective < entry.Progress.Count ? entry.Progress[objective] : 0;
    }

    // The death seam's other half (16 §3.3): a kill is the commonest objective in any game, and the only
    // one the engine can count without being told.
    public static void OnKilled(World world, Entity victim, Entity killer)
    {
        if (killer.IsNull || !world.IsAlive(killer) || !killer.Tags.Has<PlayerControlled>()) return;

        var journal = world.Resources.Get<Journal>();
        if (journal.Entries.Count == 0) return;

        var records = world.Resources.Get<RecordStore>();
        var faction = Factions.FactionOf(world, victim);
        RecordId prefab = world.TryGet<FromPrefab>(victim, out var from) ? from.Prefab : default;

        foreach (var entry in journal.Entries)
        {
            if (entry.Finished || !records.TryGet(entry.Quest, out QuestRecord record)) continue;
            var stage = record.Stage(entry.Stage);
            if (stage == null) continue;

            for (int i = 0; i < stage.Objectives.Count; i++)
            {
                var objective = stage.Objectives[i];
                if (objective.Kind != ObjectiveKind.Kill) continue;
                if (!objective.Faction.IsEmpty && objective.Faction != faction) continue;
                if (!objective.Prefab.IsEmpty && objective.Prefab != prefab) continue;
                if (objective.Faction.IsEmpty && objective.Prefab.IsEmpty) continue;   // counts nothing

                while (entry.Progress.Count <= i) entry.Progress.Add(0);
                entry.Progress[i]++;
            }
        }

        Check(world);
    }

    // Are this stage's objectives all met? If so, move on — which is the only way a quest advances by
    // itself, and the reason the journal alone answers "where am I in this".
    public static void Check(World world, RecordId quest = default)
    {
        var journal = world.Resources.Get<Journal>();
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
    public static string Describe(World world, QuestObjective objective, int progress)
    {
        if (objective.Text.Length > 0) return objective.Text;
        int count = Math.Max(objective.Count, 1);
        string what = objective.Kind == ObjectiveKind.Have
            ? Label(world, objective.Item)
            : !objective.Prefab.IsEmpty ? Label(world, objective.Prefab) : Label(world, objective.Faction);
        return objective.Kind == ObjectiveKind.Have ? $"carry {count} {what}" : $"kill {count} {what}";
    }

    private static string Label(World world, RecordId id)
    {
        if (id.IsEmpty) return "something";
        var records = world.Resources.Get<RecordStore>();
        if (records.TryGet(id, out ItemRecord item) && item.Label.Length > 0) return item.Label;
        if (records.TryGet(id, out FactionRecord faction) && faction.Label.Length > 0) return faction.Label;
        return id.Name;
    }

    private static Entity PlayerOf(World world)
    {
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
            return entity;
        return default;
    }
}
