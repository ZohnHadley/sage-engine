#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Sage.UI;

namespace Sage.Gameplay;

// Topics: Morrowind's way of talking (docs/design/16 "As built (dialogue topics)", issue #93).
//
// **A topic is a keyword, and its answer depends on who asks, whom they ask and the state of the
// world.** A `dialogue_topic` record is the keyword and an ordered list of infos; each info is a
// `requires` (one condition of #89's language — `{ "all": [ … ] }` for several), a `then` (actions) and
// a line. The first info whose `requires` holds is the answer, so the specific ones go first and a
// catch-all with no `requires` goes last:
//
//   { "type": "dialogue_topic", "id": "the_bridge", "keyword": "the bridge",
//     "infos": [ { "requires": { "standing": "guard", "min": 20 }, "text": "Open to you, friend." },
//                { "requires": { "quest": "toll", "atLeast": "paid" }, "text": "You paid. Go on." },
//                { "requires": { "speaker": "ferryman" }, "text": "Ask the guard.", "then": [ { "add_topic": "the_guard" } ] },
//                { "text": "Closed." } ] }
//
// Conditions are asked with the listener (the player) as the subject and the speaker as the other, so
// `standing`, `quest`, `var`, `has_item` read the player and the world, and `speaker` picks the NPC.
//
// **What a listener knows is saved on it** (`sage:known_topics`), learnt by the `add_topic` action —
// usually in the `then` of an info or a node option — or by `DialogueTopics.Learn`. A topic marked
// `known: true` is known by everyone from the start and is never written into the component.
//
// **Text is stored as written.** A `keyword` or `text` may be a raw `@key`; localisation (4c, #96) is
// resolved where it is shown, never here.
//
// Without the dialogue plugin, `dialogue_topic` records are skipped with a warning (as every record of
// an unregistered type is), `add_topic` and `speaker` are not registered — so content in another
// plugin's records that names them is a load error at its line, not a crash — and this API answers
// "nothing to say": Available fills nothing, Answer and Ask return null, Learn returns false.

[Record("dialogue_topic", Plugin = "sage.gameplay.dialogue")]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // topics (#93): may change before 1.0
public sealed class TopicRecord
{
    [Property(Tooltip = "The word the player picks; empty = the id's name. May be a raw @key, resolved where it is shown")]
    public string Keyword = "";
    [Property(Tooltip = "Every listener knows it from the start (Morrowind's 'latest rumors')")]
    public bool Known;
    [Property(Tooltip = "The answers, in order: the first whose requires holds is the one given")]
    public List<TopicInfo> Infos = new();
    [Property(Tooltip = "Learnt when its keyword is said in an answer, a greeting or a node's line (Morrowind's hyperlinks); off = learnt only by add_topic")]
    public bool Linked;

    public string KeywordOf(RecordId id) => Keyword.Length > 0 ? Keyword : id.Name;
}

// One answer to a topic.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // topics (#93): may change before 1.0
public sealed class TopicInfo
{
    [Property(Tooltip = "When this is the answer: one condition (use all for several), asked about the listener, with the speaker as the other; none = always")]
    public ICondition? Requires;
    [Property(Tooltip = "What giving this answer does: the listener is the subject, the speaker the other")]
    public List<IAction> Then = new();
    [Property(Tooltip = "What the speaker says. May be a raw @key, resolved where it is shown")]
    public string Text = "";
}

// The topics a listener knows, in the order learnt. Saved: `"sage:known_topics": { "Topics": ["ns:topic", …] }`.
[Component("sage:known_topics")]
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // topics (#93): may change before 1.0
public struct KnownTopics : IComponent
{
    [Property(Tooltip = "The dialogue_topic records it has learnt")]
    public List<RecordId>? Topics;
}

// One row of a topics list. The keyword is as stored (maybe `@key`).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // topics (#93): may change before 1.0
public readonly record struct AvailableTopic(RecordId Topic, string Keyword);

// `listener` asked `speaker` about `topic` and was answered (DialogueTopics.Ask, issues #391, #392): what a
// quest's `talk` objective with a `topic` counts.
[GameEvent]
public readonly record struct TopicAsked(Entity Speaker, Entity Listener, RecordId Topic);

// The headless view model a topics screen reads (the RPG kit's is #98). The signatures were frozen on
// issue #93 before the rest was built; change them only with that screen.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // topics (#93): may change before 1.0
public static class DialogueTopics
{
    // Clears `into` and fills it with the topics `listener` knows (its own, then every `known` one)
    // that `speaker` has an answer for, sorted by keyword (ordinal, ignoring case: a localised screen
    // re-sorts by what it shows). Returns how many. Allocates nothing once `into` has the capacity
    // (test: ListingTopicsAllocatesNothing).
    public static int Available(World world, Entity speaker, Entity listener, List<AvailableTopic> into)
    {
        into.Clear();
        if (Store(world) is not { } records) return 0;
        var context = new ConditionContext(world, listener, speaker);

        if (world.IsAlive(listener) && world.TryGet<KnownTopics>(listener, out var known) && known.Topics is { } learnt)
            for (int i = 0; i < learnt.Count; i++)
                Offer(records, learnt[i], in context, into);

        var common = TopicIndex.Of(records).Common;
        for (int i = 0; i < common.Count; i++)
            Offer(records, common[i], in context, into);

        // And what this speaker brings up, learnt or not (issue #392).
        if (SpeakerTopics(world, records, speaker) is { } brought)
            for (int i = 0; i < brought.Count; i++)
                Offer(records, brought[i].Id, in context, into);

        if (into.Count > 1) into.Sort(ByKeyword);
        return into.Count;
    }

    // The info that would answer, doing nothing: the first whose `requires` holds. Null when the listener
    // does not know the topic, there is no such topic (or no dialogue plugin), or nothing holds.
    public static TopicInfo? Answer(World world, Entity speaker, Entity listener, RecordId topic)
    {
        if (Store(world) is not { } records) return null;
        if (!records.TryGet(topic, out TopicRecord record)) return null;
        if (!record.Known && !Knows(world, listener, topic) && !Brings(world, records, speaker, topic)) return null;
        return First(record, new ConditionContext(world, listener, speaker));
    }

    // Asks: does what the answering info's `then` says (subject = the listener, other = the speaker) and
    // returns it. Null, with nothing done, exactly when Answer is null — a row Available lists is a row
    // Ask answers (R17).
    public static TopicInfo? Ask(World world, Entity speaker, Entity listener, RecordId topic)
    {
        var info = Answer(world, speaker, listener, topic);
        if (info == null) return null;

        // One the speaker brought up is the listener's now, to ask of others (issue #392).
        Learn(world, listener, topic);
        if (info.Then.Count > 0)
        {
            Conditions.Run(info.Then, new ActionContext(world, listener, speaker));
            // As a node option does: an answer that took or gave something may have finished an errand.
            Quests.Check(world);
        }
        LearnLinks(world, listener, info.Text);
        world.Events.Send(new TopicAsked(speaker, listener, topic));
        return info;
    }

    // Morrowind's hyperlinks (issue #392): teaches `listener` every `linked` topic whose keyword is said in
    // `text` — a whole word or phrase, ignoring case, both as shown (a `@key` is resolved by the world's
    // Localisation when it has one). Returns how many were new. Ask, a greeting and a node's line call it.
    public static int LearnLinks(World world, Entity listener, string text)
    {
        if (string.IsNullOrEmpty(text) || !world.IsAlive(listener)) return 0;
        if (Store(world) is not { } records) return 0;
        var linked = TopicIndex.Of(records).Linked;
        if (linked.Count == 0) return 0;

        world.Resources.TryGet<Localisation>(out var words);
        string said = Shown(words, text);
        int learnt = 0;
        for (int i = 0; i < linked.Count; i++)
        {
            var topic = linked[i];
            if (!records.TryGet(topic, out TopicRecord record)) continue;
            if (Says(said, Shown(words, record.KeywordOf(topic))) && Learn(world, listener, topic)) learnt++;
        }
        return learnt;
    }

    // Whether `text` says `phrase` as words of its own: not "ward" inside "warden".
    internal static bool Says(string text, string phrase)
    {
        if (phrase.Length == 0) return false;
        int from = 0;
        while (from <= text.Length - phrase.Length)
        {
            int at = text.IndexOf(phrase, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            int end = at + phrase.Length;
            if ((at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end])))
                return true;
            from = at + 1;
        }
        return false;
    }

    private static string Shown(Localisation? words, string text) =>
        words != null && Localisation.IsKey(text) ? words.Text(text) : text;

    // The topics `speaker`'s dialogue brings up; null when it has none.
    private static List<RecordRef<TopicRecord>>? SpeakerTopics(World world, RecordStore records, Entity speaker)
    {
        if (!world.IsAlive(speaker) || !world.TryGet<global::Sage.Gameplay.Dialogue>(speaker, out var dialogue) || dialogue.Record.IsEmpty) return null;
        if (records.TypeNameOf(typeof(DialogueRecord)) == null || !records.TryGet(dialogue.Record, out DialogueRecord record)) return null;
        return record.Topics.Count > 0 ? record.Topics : null;
    }

    private static bool Brings(World world, RecordStore records, Entity speaker, RecordId topic)
    {
        if (SpeakerTopics(world, records, speaker) is not { } brought) return false;
        for (int i = 0; i < brought.Count; i++)
            if (brought[i].Id == topic) return true;
        return false;
    }

    // Whether `listener` knows it: learnt, or `known` by everyone.
    public static bool Knows(World world, Entity listener, RecordId topic)
    {
        if (topic.IsEmpty) return false;
        if (world.IsAlive(listener) && world.TryGet<KnownTopics>(listener, out var known) && known.Topics != null && known.Topics.Contains(topic))
            return true;
        return Store(world) is { } records && records.TryGet(topic, out TopicRecord record) && record.Known;
    }

    // Teaches it; true when it was new. False for a topic nobody wrote (said once), for one everyone
    // knows already, and in a game without the dialogue plugin. Outside query loops, like world.Teach.
    public static bool Learn(World world, Entity listener, RecordId topic)
    {
        if (topic.IsEmpty || !world.IsAlive(listener)) return false;
        if (Store(world) is not { } records) return false;
        if (!records.TryGet(topic, out TopicRecord record))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Warn, $"topic:{topic}",
                $"No dialogue_topic {topic} to learn (a typo, or the dialogue plugin is off)");
            return false;
        }
        if (record.Known) return false;

        if (!world.Has<KnownTopics>(listener)) world.Add(listener, new KnownTopics { Topics = new List<RecordId>() });
        ref var known = ref world.Get<KnownTopics>(listener);
        known.Topics ??= new List<RecordId>();
        if (known.Topics.Contains(topic)) return false;
        known.Topics.Add(topic);
        return true;
    }

    // The world's records, when they know what a topic is: without the dialogue plugin the type is not
    // registered (and asking the store for one would throw), so every question here answers "none".
    private static RecordStore? Store(World world) =>
        world.Resources.TryGet<RecordStore>(out var records) && records != null && records.TypeNameOf(typeof(TopicRecord)) != null
            ? records : null;

    private static void Offer(RecordStore records, RecordId topic, in ConditionContext context, List<AvailableTopic> into)
    {
        if (!records.TryGet(topic, out TopicRecord record)) return;   // a topic a removed mod taught
        for (int i = 0; i < into.Count; i++)
            if (into[i].Topic == topic) return;
        if (First(record, context) != null) into.Add(new AvailableTopic(topic, record.KeywordOf(topic)));
    }

    private static TopicInfo? First(TopicRecord record, in ConditionContext context)
    {
        var infos = record.Infos;
        for (int i = 0; i < infos.Count; i++)
            if (infos[i] is { } info && Conditions.Test(info.Requires, in context, out _)) return info;
        return null;
    }

    private static readonly Comparison<AvailableTopic> ByKeyword = static (a, b) =>
    {
        int order = string.Compare(a.Keyword, b.Keyword, StringComparison.OrdinalIgnoreCase);
        if (order == 0) order = string.CompareOrdinal(a.Topic.Namespace, b.Topic.Namespace);
        return order != 0 ? order : string.CompareOrdinal(a.Topic.Name, b.Topic.Name);
    };
}

// The topics everyone knows, found once per content load rather than on every Available.
internal sealed class TopicIndex
{
    private static readonly ConditionalWeakTable<RecordStore, TopicIndex> Indexes = new();

    public readonly List<RecordId> Common = new();
    public readonly List<RecordId> Linked = new();   // `linked` topics, learnt by being said (issue #392)
    private int _count = -1;
    private bool _stale = true;

    public static TopicIndex Of(RecordStore records)
    {
        var index = Indexes.GetValue(records, static store =>
        {
            var made = new TopicIndex();
            store.Reloaded += () => made._stale = true;
            return made;
        });
        if (index._stale || index._count != records.Count)
        {
            lock (index)
            {
                index.Common.Clear();
                index.Linked.Clear();
                foreach (var id in records.Ids("dialogue_topic"))
                {
                    if (!records.TryGet(id, out TopicRecord record)) continue;
                    if (record.Known) index.Common.Add(id);
                    else if (record.Linked) index.Linked.Add(id);
                }
                index._count = records.Count;
                index._stale = false;
            }
        }
        return index;
    }
}

// ---- the dialogue plugin's words -----------------------------------------------------------------

// Teaches the subject (the listener) a topic: `{ "add_topic": "silt_strider" }`.
[Action("add_topic", Plugin = "sage.gameplay.dialogue")]
internal sealed class AddTopicAction : IAction
{
    [EntryValue, Property(Tooltip = "The topic the listener learns")]
    public RecordRef<TopicRecord> Topic;

    public void Run(in ActionContext context)
    {
        if (!Topic.IsEmpty) DialogueTopics.Learn(context.World, context.Subject, Topic.Id);
    }
}

// Who is speaking: the other (in a conversation or a topic, the speaker) has this `sage:dialogue`
// record — `{ "speaker": "innkeeper" }` — and/or this entity name, and/or came from this prefab. So an
// info can be one NPC's answer, the way Morrowind filters an info by speaker.
[Condition("speaker", Plugin = "sage.gameplay.dialogue")]
internal sealed class SpeakerCondition : ICondition
{
    [EntryValue, Property(Tooltip = "The speaker's dialogue record (its sage:dialogue component)")]
    public RecordRef<DialogueRecord> Dialogue;
    [Property(Tooltip = "Or the speaker's entity name")]
    public string Name = "";
    [Property(Tooltip = "Or the prefab it was spawned from")]
    public RecordRef<PrefabRecord> Prefab;

    public bool Test(in ConditionContext context, out string why)
    {
        why = "they have nothing to say about that";
        var world = context.World;
        var other = context.Other;
        if (!world.IsAlive(other)) return Dialogue.IsEmpty && Name.Length == 0 && Prefab.IsEmpty;
        if (!Dialogue.IsEmpty && (!world.TryGet<global::Sage.Gameplay.Dialogue>(other, out var dialogue) || dialogue.Record != Dialogue.Id)) return false;
        if (Name.Length > 0 && !string.Equals(other.Name, Name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Prefab.IsEmpty && (!world.TryGet<FromPrefab>(other, out var from) || from.Prefab != Prefab.Id)) return false;
        return true;
    }
}
