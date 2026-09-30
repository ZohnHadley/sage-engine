#nullable enable
using System;
using System.Collections.Generic;
using Sage.UI;

namespace Sage.Kits.Rpg;

// One of the RPG kit's screens (issue #98); the pattern they share is at the top of InventoryView.cs.

// Morrowind's topics (issue #93's DialogueTopics): what the player (Subject) can ask the NPC (Other),
// sorted by keyword, and the last answer. Confirm on a topic asks it — the answer's `then` runs, and a
// topic it teaches appears in the list at the next refresh.
//   screen rpg:topics — layout rpg:topics
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#98): SAGE0125, as Sage.UI
[ViewModel("rpg_topics")]
public sealed class TopicsView : IViewModel
{
    public const string AskedStyle = "rpg:topic_asked", TopicStyle = "rpg:topic";

    public sealed class TopicRow
    {
        public RecordId Topic { get; internal set; }

        // As content wrote it: maybe a `@key`, which the label resolves.
        public string Keyword { get; internal set; } = "";
        public bool Asked { get; internal set; }
        public string Style => Asked ? AskedStyle : TopicStyle;
    }

    private readonly List<AvailableTopic> _available = new();
    private readonly List<TopicRow> _pool = new();
    private readonly HashSet<RecordId> _asked = new();
    private Entity _named;

    public List<TopicRow> Topics { get; } = new();

    // Who is speaking, by name.
    public string Speaker { get; private set; } = "";

    // What they said last: an info's text as written (maybe a `@key`); empty before anything is asked.
    public string Answer { get; private set; } = "";

    // The keyword of the topic answered last.
    public string Asked { get; private set; } = "";

    public bool HasTopics => Topics.Count > 0;
    public bool NoTopics => Topics.Count == 0;

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        if (context.Other != _named)
        {
            _named = context.Other;
            Speaker = world.IsAlive(context.Other) ? context.Other.Name ?? "" : "";
            _asked.Clear();
            Answer = Asked = "";
        }
        DialogueTopics.Available(world, context.Other, context.Subject, _available);
        if (Same()) return;

        Topics.Clear();
        for (int i = 0; i < _available.Count; i++)
        {
            if (_pool.Count <= i) _pool.Add(new TopicRow());
            var row = _pool[i];
            row.Topic = _available[i].Topic;
            row.Keyword = _available[i].Keyword;
            row.Asked = _asked.Contains(row.Topic);
            Topics.Add(row);
        }
    }

    private bool Same()
    {
        if (_available.Count != Topics.Count) return false;
        for (int i = 0; i < Topics.Count; i++)
            if (Topics[i].Topic != _available[i].Topic || !ReferenceEquals(Topics[i].Keyword, _available[i].Keyword)) return false;
        return true;
    }

    public bool Activate(Widget widget, in UiBindContext context)
    {
        if (context.World is not { } world || UiScreen.RowOf(widget) is not TopicRow row) return false;
        Ask(world, context.Other, context.Subject, row);
        Refresh(in context);
        return true;
    }

    // Asks it, as the screen does on confirm: the answer and what it does (DialogueTopics.Ask).
    public TopicInfo? Ask(World world, Entity speaker, Entity listener, TopicRow row)
    {
        var info = DialogueTopics.Ask(world, speaker, listener, row.Topic);
        Answer = info?.Text ?? "@rpg.topics.no_answer";
        Asked = row.Keyword;
        _asked.Add(row.Topic);
        row.Asked = true;
        return info;
    }
}
