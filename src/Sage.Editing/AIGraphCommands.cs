#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Sage.Editing;

// The AI graph view's console commands (issue #369): every button of the editor's AI Graph panel is one, so
// a script, a smoke run or a test presses it the way a person does (phase 10a decision 6). They act on the
// record open in the record browser (`ed_rec_open state_machine guard`), and their edits are undone with
// `ed_rec_undo` like the browser's own.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class AIGraphCommands
{
    // `world` is where `ed_ai_active` looks for the entity: the play world while playing, else the edit world.
    public static void Register(CVarRegistry cvars, RecordEditor records, Func<World?> world)
    {
        ArgumentNullException.ThrowIfNull(cvars);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(world);

        cvars.RegisterCommand("ed_ai_tree", CVarFlags.None,
            "ed_ai_tree: the open state_machine, ai_schedule or routine as a tree of nodes, with transitions.", _ =>
        {
            if (Graph(records) is { } g) Log.Info(LogCat.Console, Describe(g));
        });

        cvars.RegisterCommand("ed_ai_add", CVarFlags.DevOnly,
            "ed_ai_add <parent|-> <name> [index]: add a state inside <parent> (- for the top), or a task / routine entry (parent -) at index.", a =>
        {
            if (Graph(records) is not { } g) return;
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_ai_add <parent|-> <name> [index]   e.g. ed_ai_add - alert, ed_ai_add - Wait 0"); return; }
            if (g.Add(Parent(a[0]), a[1], index: Index(a, 2))) Done(g);
        });

        cvars.RegisterCommand("ed_ai_remove", CVarFlags.DevOnly,
            "ed_ai_remove <node>: remove a node (a state by name, a task or entry by number) and what is inside it.", a =>
        {
            if (Graph(records) is not { } g) return;
            if (a.Count < 1) { Log.Warn(LogCat.Console, "ed_ai_remove <node>"); return; }
            if (g.Remove(a[0])) Done(g);
        });

        cvars.RegisterCommand("ed_ai_move", CVarFlags.DevOnly,
            "ed_ai_move <node> <index>: move a node to that place among the nodes beside it.", a =>
        {
            if (Graph(records) is not { } g) return;
            if (a.Count < 2 || Index(a, 1) < 0) { Log.Warn(LogCat.Console, "ed_ai_move <node> <index>"); return; }
            if (g.Move(a[0], Index(a, 1))) Done(g);
        });

        cvars.RegisterCommand("ed_ai_reparent", CVarFlags.DevOnly,
            "ed_ai_reparent <state> <parent|-> [index]: move a state inside another (- for the top).", a =>
        {
            if (Graph(records) is not { } g) return;
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_ai_reparent <state> <parent|-> [index]"); return; }
            if (g.Reparent(a[0], Parent(a[1]), Index(a, 2))) Done(g);
        });

        cvars.RegisterCommand("ed_ai_rename", CVarFlags.DevOnly,
            "ed_ai_rename <node> <name>: rename a state (and every transition and initial naming it), or a task.", a =>
        {
            if (Graph(records) is not { } g) return;
            if (a.Count < 2) { Log.Warn(LogCat.Console, "ed_ai_rename <node> <name>"); return; }
            if (g.Rename(a[0], a[1])) Done(g);
        });

        cvars.RegisterCommand("ed_ai_active", CVarFlags.None,
            "ed_ai_active [entity]: the nodes of the open record that entity is in now (in the play world while playing); no name: who runs it.", a =>
        {
            if (Graph(records) is not { } g) return;
            if (world() is not { } w) { Log.Warn(LogCat.Console, "ed_ai_active: no world"); return; }
            if (a.Count == 0)
            {
                var runners = g.Runners(w);
                Log.Info(LogCat.Console, runners.Count == 0 ? $"nothing in '{w.Name}' runs {g.Document.Id}"
                    : $"in '{w.Name}', {g.Document.Id} is run by " + string.Join(", ", runners.Select(World.Describe)));
                return;
            }
            var entity = w.FindByName(a.Rest);
            if (entity.IsNull) { Log.Warn(LogCat.Console, $"ed_ai_active: no entity named '{a.Rest}' in '{w.Name}'"); return; }
            var active = g.Active(w, entity);
            Log.Info(LogCat.Console, active.Count == 0 ? $"{World.Describe(entity)}: in no node of {g.Document.Id}"
                : $"{World.Describe(entity)}: " + string.Join(" > ", active.Select(p => g.Find(p)?.Name ?? p)));
        });
    }

    // The open record as a tree: one line a node, indented, `*` the initial, `->` its transitions.
    public static string Describe(AIGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var text = new StringBuilder(graph.Document.Title);
        if (graph.Kind == AIGraphKind.StateMachine) text.Append("  initial ").Append(graph.Initial.Length == 0 ? "(none)" : graph.Initial);
        foreach (var node in graph.Nodes)
        {
            text.Append('\n').Append(' ', 2 + node.Depth * 2)
                .Append(graph.IsTree ? (node.IsInitial ? "* " : "  ") : $"{node.Index}. ").Append(node.Label);
            foreach (var edge in node.Edges) text.Append("\n").Append(' ', 6 + node.Depth * 2).Append("-> ").Append(edge.To).Append("  ").Append(edge.Label);
        }
        foreach (var edge in graph.AnyStateEdges) text.Append("\n  (any) -> ").Append(edge.To).Append("  ").Append(edge.Label);
        if (graph.Nodes.Count == 0) text.Append("\n  (empty)");
        return text.ToString();
    }

    // An edit says what it did, as the undo history has it.
    private static void Done(AIGraph graph)
    {
        var history = graph.Document.History;
        Log.Info(LogCat.Console, history.Entries[history.Position - 1].Description);
    }

    private static AIGraph? Graph(RecordEditor records)
    {
        if (records.Current is not { } record) { Log.Warn(LogCat.Console, "no record open: ed_rec_open <type> <id>"); return null; }
        if (AIGraph.Of(record) is { } graph) return graph;
        Log.Warn(LogCat.Console, $"{record.Type} {record.Id} is not an AI graph (state_machine, ai_schedule, routine)");
        return null;
    }

    private static string? Parent(string text) => text is "-" or "" ? null : text;

    private static int Index(ConsoleArgs a, int at) =>
        a.Count > at && int.TryParse(a[at], NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 ? i : -1;
}
