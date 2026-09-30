#nullable enable
using System.Collections.Generic;
using Sage.UI;

namespace Sage.Kits.Rpg;

// The journal as a widget screen's view-model (docs/design/13 "As built (the HUD, journal, map and
// menus)", issue #99): what JournalScreen's panel lists, as data a `ui_layout` binds — one line per
// quest, its stage's text under it, and each objective with its progress. A game's `screen` record names
// it (`"viewModel": "rpg_journal"`) and its layout says what each kind of line looks like; the Sandbox's
// is `sandbox:journal` (games/Sandbox/content/data/ui.json).
//
// Lines, not nested lists: a line says which kind it is (Quest, Stage, Objective; Done for a finished
// quest's), so a layout shows each kind with its own widget — `"bindings": { "visible": "quest" }` —
// and localises the words around them ("done") itself. Nothing here is English.
//
// Refresh reads the journal every frame and rebuilds its lines only when what they say changed (a quest
// started, moved on or finished, an objective counted), reusing them, so an open journal allocates nothing.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0125", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the RPG screens (#99): SAGE0125, as Sage.UI
[ViewModel("rpg_journal")]
public sealed class JournalView : IViewModel
{
    public sealed class Line
    {
        public string Text { get; internal set; } = "";

        // "1/3" on an objective's line; empty otherwise.
        public string Progress { get; internal set; } = "";

        public bool Quest { get; internal set; }
        public bool Stage { get; internal set; }
        public bool Objective { get; internal set; }

        // A finished quest's line (and only that: a finished quest shows no stage or objectives).
        public bool Done { get; internal set; }

        public RecordId QuestId { get; internal set; }
    }

    private readonly List<Line> _pool = new();
    private int _signature = int.MinValue;

    public List<Line> Lines { get; } = new();

    // No quest yet: a layout shows its "nothing yet" line on this.
    public bool Empty => Lines.Count == 0;

    public int Active { get; private set; }
    public int Finished { get; private set; }

    public void Refresh(in UiBindContext context)
    {
        if (context.World is not { } world) return;
        var journal = Quests.JournalOf(world);
        var records = world.Resources.Get<RecordStore>();
        var subject = context.Subject;

        int signature = Signature(world, journal, records, subject);
        if (signature == _signature) return;
        _signature = signature;

        Lines.Clear();
        Active = Finished = 0;
        if (journal == null) return;
        int used = 0;
        foreach (var entry in journal.Entries)
        {
            if (!records.TryGet(entry.Quest, out QuestRecord record)) continue;
            if (entry.Finished) Finished++; else Active++;
            var quest = Next(ref used);
            quest.Text = record.Label.Length > 0 ? record.Label : entry.Quest.Name;
            quest.Quest = true;
            quest.Done = entry.Finished;
            quest.QuestId = entry.Quest;

            var stage = record.Stage(entry.Stage);
            if (entry.Finished || stage == null) continue;
            if (stage.Text.Length > 0)
            {
                var line = Next(ref used);
                line.Text = stage.Text;
                line.Stage = true;
                line.QuestId = entry.Quest;
            }
            for (int i = 0; i < stage.Objectives.Count; i++)
            {
                var objective = stage.Objectives[i];
                int done = Quests.Progress(world, subject, entry.Quest, i);
                int want = objective.Count < 1 ? 1 : objective.Count;
                var line = Next(ref used);
                line.Text = Quests.Describe(world, objective, done);
                line.Progress = $"{(done > want ? want : done)}/{want}";
                line.Objective = true;
                line.QuestId = entry.Quest;
            }
        }
    }

    // A line from the pool, cleared, added to Lines.
    private Line Next(ref int used)
    {
        if (used == _pool.Count) _pool.Add(new Line());
        var line = _pool[used++];
        line.Text = line.Progress = "";
        line.Quest = line.Stage = line.Objective = line.Done = false;
        line.QuestId = default;
        Lines.Add(line);
        return line;
    }

    // What the lines depend on, folded into one number without allocating: each entry's quest, stage and
    // whether it is done, and every objective's progress.
    private static int Signature(World world, Journal? journal, RecordStore records, Entity subject)
    {
        if (journal == null) return 0;
        int hash = journal.Entries.Count;
        foreach (var entry in journal.Entries)
        {
            hash = hash * 31 + entry.Quest.GetHashCode();
            hash = hash * 31 + (entry.Stage?.GetHashCode() ?? 0);
            hash = hash * 31 + (entry.Finished ? 1 : 2);
            if (entry.Finished || !records.TryGet(entry.Quest, out QuestRecord record)) continue;
            var stage = record.Stage(entry.Stage ?? "");
            if (stage == null) continue;
            for (int i = 0; i < stage.Objectives.Count; i++)
                hash = hash * 31 + Quests.Progress(world, subject, entry.Quest, i);
        }
        return hash;
    }
}
