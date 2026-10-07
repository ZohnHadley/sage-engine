#nullable enable
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The AI graph view (issue #369; docs/EDITOR.md "A behaviour-tree view"): the state_machine, ai_schedule or
// routine open in the Records panel, drawn as a tree of states (with their transitions) or a list of
// steps, a node at a time to add, remove, move up and down, move into another state or rename; and, while
// a world runs it, the nodes the chosen entity is in highlighted. What the nodes are, every edit (one undo
// in the record's own history, so the Records panel's Undo and `ed_rec_undo` take it back) and what is
// active is `AIGraph` (Sage.Editing, tested headlessly; `ed_ai_*` press the same buttons); this only draws.
internal sealed class AIGraphPanel
{
    public const string Title = "AI Graph";

    private static readonly Vector4 ActiveColour = new(0.35f, 0.85f, 0.35f, 1f);
    private static readonly Vector4 EdgeColour = new(0.6f, 0.6f, 0.75f, 1f);

    private readonly RecordEditor _records;
    private readonly Func<World?> _world;
    private AIGraph? _graph;
    private string? _selected;          // a node's path
    private Entity _watched;            // whose active nodes are highlighted
    private World? _watchedIn;
    private string _name = "";

    public AIGraphPanel(RecordEditor records, Func<World?> world)
    {
        _records = records;
        _world = world;
    }

    public void Draw()
    {
        ImGui.SetNextWindowSize(new Vector2(320, 420), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin(Title)) { ImGui.End(); return; }

        var record = _records.Current;
        if (_graph?.Document != record)
        {
            _graph = AIGraph.Of(record);
            _selected = null;
            _watched = default;
        }
        if (_graph is not { } graph)
        {
            ImGui.TextDisabled(record == null ? "Open a state_machine, ai_schedule or routine in the Records panel."
                                              : $"{record.Type} is not an AI graph (state_machine, ai_schedule, routine).");
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted(graph.Document.Title);
        ImGui.BeginDisabled(!graph.Document.History.CanUndo);
        if (ImGui.Button("Undo")) graph.Document.Undo();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(!graph.Document.History.CanRedo);
        if (ImGui.Button("Redo")) graph.Document.Redo();
        ImGui.EndDisabled();

        var active = DrawWatch(graph);
        ImGui.Separator();
        DrawEdit(graph);
        ImGui.Separator();
        DrawTree(graph, active);
        ImGui.End();
    }

    // Who is watched: one of the entities of the world on screen (the play world while playing) that runs it.
    private string[] DrawWatch(AIGraph graph)
    {
        if (_world() is not { } world) return Array.Empty<string>();
        if (_watchedIn != world || !world.IsAlive(_watched)) { _watched = default; _watchedIn = world; }
        var runners = graph.Runners(world);
        if (_watched.IsNull && runners.Count > 0) _watched = runners[0];

        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##aiwatch", _watched.IsNull ? $"nothing in '{world.Name}' runs it" : $"watch {Label(_watched)}  ({world.Name})"))
        {
            foreach (var entity in runners)
                if (ImGui.Selectable(Label(entity), entity == _watched)) _watched = entity;
            ImGui.EndCombo();
        }
        return _watched.IsNull ? Array.Empty<string>() : graph.Active(world, _watched).ToArray();
    }

    private void DrawEdit(AIGraph graph)
    {
        var node = _selected != null ? graph.Find(_selected) : null;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X * 0.5f);
        ImGui.InputTextWithHint("##ainame", graph.Kind == AIGraphKind.StateMachine ? "state name" : graph.Kind == AIGraphKind.Schedule ? "task" : "schedule", ref _name, 64);
        ImGui.SameLine();
        ImGui.BeginDisabled(_name.Trim().Length == 0);
        if (ImGui.Button("Add")) Added(graph, graph.Add(graph.IsTree ? node?.Parent : null, _name.Trim(), index: node != null ? node.Index + 1 : -1));
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("After the selected node, beside it (at the end with none selected)");
        if (graph.IsTree)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(node == null);
            if (ImGui.Button("Add inside") && node != null) Added(graph, graph.Add(node.Path, _name.Trim()));
            ImGui.EndDisabled();
        }
        ImGui.SameLine();
        ImGui.BeginDisabled(node == null || graph.Kind == AIGraphKind.Routine);
        if (ImGui.Button("Rename") && node != null && graph.Rename(node.Path, _name.Trim()))
            _selected = graph.Find(_name.Trim())?.Path ?? _selected;
        ImGui.EndDisabled();
        ImGui.EndDisabled();

        if (node == null) { ImGui.TextDisabled("Select a node to move or remove it."); return; }
        if (ImGui.ArrowButton("##aiup", ImGuiDir.Up) && node.Index > 0) Keep(graph, node, graph.Move(node.Path, node.Index - 1), node.Index - 1);
        ImGui.SameLine();
        if (ImGui.ArrowButton("##aidown", ImGuiDir.Down)) Keep(graph, node, graph.Move(node.Path, node.Index + 1), node.Index + 1);
        if (graph.IsTree)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(node.Parent == null);
            if (ImGui.Button("Out") && graph.Reparent(node.Path, graph.Find(node.Parent!)?.Parent)) _selected = graph.Find(node.Name)?.Path;
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            if (ImGui.BeginCombo("##aiinto", "Move into..."))
            {
                foreach (var other in graph.Nodes)
                    if (other != node && other.Path != node.Parent && !other.Path.StartsWith(node.Path + ".", StringComparison.OrdinalIgnoreCase)
                        && ImGui.Selectable(new string(' ', other.Depth * 2) + other.Name) && graph.Reparent(node.Path, other.Path))
                        _selected = graph.Find(node.Name)?.Path;
                ImGui.EndCombo();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Remove") && graph.Remove(node.Path)) _selected = null;
    }

    private void Added(AIGraph graph, bool added)
    {
        if (!added) return;
        _selected = graph.IsTree ? graph.Find(_name.Trim())?.Path : _selected;
        _name = "";
    }

    // A list's node is its index, so a moved one is selected where it went.
    private void Keep(AIGraph graph, AIGraphNode node, bool moved, int index)
    {
        if (!moved) return;
        _selected = graph.IsTree ? graph.Find(node.Name)?.Path : graph.Find(index.ToString(System.Globalization.CultureInfo.InvariantCulture))?.Path;
    }

    private void DrawTree(AIGraph graph, string[] active)
    {
        if (graph.Kind == AIGraphKind.StateMachine) ImGui.TextDisabled($"initial: {(graph.Initial.Length == 0 ? "(none)" : graph.Initial)}");
        if (!ImGui.BeginChild("##aitree", Vector2.Zero, ImGuiChildFlags.Border)) { ImGui.EndChild(); return; }
        if (graph.Nodes.Count == 0) ImGui.TextDisabled("(empty: add a node above)");
        foreach (var node in graph.Nodes)
        {
            bool on = active.Contains(node.Path, StringComparer.OrdinalIgnoreCase);
            ImGui.Indent(node.Depth * 14f + 1f);
            string mark = graph.IsTree ? (node.IsInitial ? "* " : "  ") : $"{node.Index}. ";
            if (on) ImGui.PushStyleColor(ImGuiCol.Text, ActiveColour);
            if (ImGui.Selectable($"{mark}{node.Label}{(on ? "   <" : "")}##{node.Path}", node.Path == _selected)) _selected = node.Path;
            if (on) ImGui.PopStyleColor();
            foreach (var edge in node.Edges)
                ImGui.TextColored(EdgeColour, $"    -> {edge.To}  {edge.Label}");
            ImGui.Unindent(node.Depth * 14f + 1f);
        }
        foreach (var edge in graph.AnyStateEdges)
            ImGui.TextColored(EdgeColour, $"(any) -> {edge.To}  {edge.Label}");
        ImGui.EndChild();
    }

    private static string Label(Entity entity) => string.IsNullOrEmpty(entity.Name) ? World.Describe(entity) : entity.Name;
}
