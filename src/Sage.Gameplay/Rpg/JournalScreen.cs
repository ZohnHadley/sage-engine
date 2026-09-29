#nullable enable

namespace Sage.Gameplay;

// What you are on (docs/design/13 §3, 16 §3.5, F24).
//
// One row per quest, with the stage's line as its detail and its objectives counted underneath — a
// journal is a list with reasons, which is the shape every screen here already has. Finished quests
// stay, greyed: a journal you cannot look back through is a to-do list.
public sealed class JournalScreen : Screen
{
    public override float Width => 620f;

    public override string Hint => "↑↓ choose    Esc close";

    public override void Build(World world, Entity subject)
    {
        var journal = Quests.JournalOf(world);   // null: a game without the quests plugin (issue #26)
        var records = world.Resources.Get<RecordStore>();
        Panel.Begin("Journal", subject);

        if (journal == null || journal.Entries.Count == 0)
        {
            Panel.Add(PanelRow.Of(default, "(nothing yet)", enabled: false));
            return;
        }

        foreach (var entry in journal.Entries)
        {
            if (!records.TryGet(entry.Quest, out QuestRecord record)) continue;
            string label = record.Label.Length > 0 ? record.Label : entry.Quest.Name;
            var stage = record.Stage(entry.Stage);

            Panel.Add(PanelRow.Of(entry.Quest, label,
                                  detail: entry.Finished ? "done" : "",
                                  selected: !entry.Finished,
                                  enabled: !entry.Finished));

            if (entry.Finished || stage == null) continue;
            if (stage.Text.Length > 0)
                Panel.Add(PanelRow.Of(entry.Quest, "   " + stage.Text, enabled: false));

            // The objectives, with what has been done against what is asked: "kill 3 beasts   1/3".
            for (int i = 0; i < stage.Objectives.Count; i++)
            {
                var objective = stage.Objectives[i];
                int done = Quests.Progress(world, subject, entry.Quest, i);
                int want = objective.Count < 1 ? 1 : objective.Count;
                Panel.Add(PanelRow.Of(entry.Quest, "   • " + Quests.Describe(world, objective, done),
                                      detail: $"{(done > want ? want : done)}/{want}", enabled: false));
            }
        }
    }
}
