#nullable enable
using System;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Sage.Editing;

#pragma warning disable SAGE0121 // the panel edits a placement's wires: the editor is who SAGE0121's placements are experimental for

namespace Sage.Editor;

// The I/O panel (issue #225, 15 §7): the selected placement's wires, and a flow to add one: choose an
// output, "pick target" (the next click in the viewport, or a row in the outliner), choose one of the
// inputs that target takes, then a delay and a value. Lines between wired entities are drawn over the
// picture, the selection's highlighted. What is listed, checked and written is `WiringModel` and
// `Wiring` (Sage.Editing, tested headlessly; every edit is a SetOutputs, one undo); this only draws and
// passes the clicks on.
internal sealed class WiringPanel
{
    public const string Title = "I/O";

    private readonly EditorSelection _selection;
    private readonly Func<Entity, bool> _pickable;
    private string _output = "", _target = "", _input = "", _value = "";
    private float _delay;
    private string? _error;
    private Placement? _unnamed;        // a picked target with no name: offered one
    private Placement? _source;         // who is being wired while a target is being picked
    private bool _picking;
    private Connection? _edit;          // the wire being typed into, applied when the field lets go
    private (Placement, int)? _editing;

    public WiringPanel(EditorSelection selection, Func<Entity, bool> pickable)
    {
        _selection = selection;
        _pickable = pickable;
        selection.Changed += OnSelectionChanged;
    }

    // The next click picks a target, so the gizmo leaves it alone.
    public bool IsPicking => _picking;

    public void Draw()
    {
        if (!ImGui.Begin(Title))
        {
            ImGui.End();
            return;
        }
        var document = _selection.Document;
        if (!document.IsOpen || _selection.Placement is not { } placement)
        {
            ImGui.TextDisabled(document.IsOpen ? "select a placed entity to see its wires" : "no document open");
            ImGui.End();
            return;
        }

        var model = new WiringModel(document, placement);
        ImGui.TextUnformatted($"{(placement.Name.Length > 0 ? placement.Name : placement.Prefab.Id.Name)}: {model.Wires.Count} wire(s)");
        ImGui.Separator();
        DrawWires(model);
        ImGui.Separator();
        DrawAdd(model);
        ImGui.End();
    }

    private void DrawWires(WiringModel model)
    {
        var placement = model.Placement;
        int remove = -1;
        for (int i = 0; i < model.Wires.Count; i++)
        {
            bool mine = _editing == (placement, i);
            var wire = mine && _edit != null ? _edit : Clone(model.Wires[i]);
            bool changed = false;
            ImGui.PushID(i);
            ImGui.TextUnformatted($"{i + 1}. {wire.Output} -> {wire.Target}.{wire.Input}");
            if (Wiring.Check(model.Document, wire.Target, wire.Input) is { } problem)
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), problem);
            ImGui.SetNextItemWidth(70);
            changed |= ImGui.DragFloat("delay", ref wire.Delay, 0.05f, 0f, 600f, "%.2f s");
            bool delayDone = ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            changed |= ImGui.InputText("value", ref wire.Parameter, 256);
            bool valueDone = ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SameLine();
            if (ImGui.SmallButton("remove")) remove = i;
            if (wire.Requires != null) ImGui.TextDisabled($"requires {wire.Requires.GetType().Name}");

            if (changed) { _edit = wire; _editing = (placement, i); }
            if ((delayDone || valueDone) && _edit != null)
            {
                Wiring.Update(model.Document, placement, i, _edit, out _error);
                _edit = null;
                _editing = null;
            }
            ImGui.PopID();
        }
        if (remove >= 0)
        {
            Wiring.Remove(model.Document, placement, remove);
            _edit = null;
            _editing = null;
        }
    }

    private void DrawAdd(WiringModel model)
    {
        ImGui.TextUnformatted("Add a wire");

        // 1. The output: free text, with what is known beside it.
        ImGui.SetNextItemWidth(160);
        ImGui.InputText("##output", ref _output, 64);
        ImGui.SameLine();
        if (ImGui.BeginCombo("output", "", ImGuiComboFlags.NoPreview))
        {
            foreach (var output in model.Outputs())
                if (ImGui.Selectable(output.Declared ? $"{output.Name}  ({output.Description})" : $"{output.Name}  (used here)")) _output = output.Name;
            ImGui.EndCombo();
        }

        // 2. The target: the next click, or an outliner row.
        if (_picking)
        {
            ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "click the target in the viewport or the outliner, Esc to cancel");
            ImGui.SameLine();
            if (ImGui.SmallButton("cancel")) CancelPick();
        }
        else
        {
            ImGui.SetNextItemWidth(160);
            ImGui.InputText("##target", ref _target, 64);
            ImGui.SameLine();
            if (ImGui.Button("pick target")) { _picking = true; _source = model.Placement; }
        }
        if (_unnamed != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), $"{_unnamed.Prefab.Id.Name} has no name, and a wire finds its target by name");
            ImGui.SameLine();
            if (ImGui.SmallButton("name it")) { _target = Wiring.NameIt(model.Document, _unnamed); _unnamed = null; }
        }

        // 3. The input: one the target takes.
        var inputs = model.InputsOf(_target);
        ImGui.SetNextItemWidth(160);
        if (ImGui.BeginCombo("input", _input.Length > 0 ? _input : "(choose)"))
        {
            foreach (var input in inputs)
            {
                string label = input.Components.Count > 0 ? $"{input.Name}  ({string.Join(", ", input.Components)})" : input.Name;
                if (ImGui.Selectable(label, input.Name == _input)) _input = input.Name;
            }
            if (inputs.Count == 0) ImGui.TextDisabled(_target.Length == 0 ? "pick a target first" : "no such target in the world");
            ImGui.EndCombo();
        }

        // 4. Delay and value.
        ImGui.SetNextItemWidth(70);
        ImGui.DragFloat("delay", ref _delay, 0.05f, 0f, 600f, "%.2f s");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        ImGui.InputText("value", ref _value, 256);

        if (ImGui.Button("Add wire"))
        {
            var wire = new Connection { Output = _output.Trim(), Target = _target.Trim(), Input = _input, Delay = _delay, Parameter = _value };
            if (Wiring.Add(model.Document, model.Placement, wire, out _error)) { _input = ""; _value = ""; _delay = 0f; }
        }
        if (_error != null) ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), _error);
    }

    // After the panels are drawn: while a target is being picked, Escape cancels and a left click that ImGui
    // does not want (the viewport is the dock space's hole) picks what is under it.
    public void HandleViewport(World world, EditDocument document, Func<Vector2, Vector2, EditorRay?> rayThrough)
    {
        if (!_picking) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { CancelPick(); return; }
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        if (rayThrough(io.MousePos, io.DisplaySize) is not { } ray) return;
        if (EditorPicking.PickWhere(world, ray, _pickable) is not { } hit) return;
        for (var e = hit.Entity; !e.IsNull && world.IsAlive(e); e = e.Parent)
            if (document.PlacementOf(e) is { } placement) { Picked(placement); return; }
        if (!string.IsNullOrEmpty(hit.Entity.Name)) { _target = hit.Entity.Name; EndPick(); }
    }

    // An outliner row (or anything else that selects) while a target is being picked is the target; the
    // wired placement stays selected.
    private void OnSelectionChanged()
    {
        if (!_picking) return;
        var source = _source;
        if (_selection.Placement is { } placement && !ReferenceEquals(placement, source)) Picked(placement);
        else if (_selection.Placement == null && _selection.Entity is { IsNull: false } entity && !string.IsNullOrEmpty(entity.Name))
        {
            _target = entity.Name;
            EndPick();
        }
        else return;
        if (source != null && _selection.Document.IndexOf(source) >= 0) _selection.Select(source);
    }

    private void Picked(Placement placement)
    {
        if (Wiring.NameOf(placement) is { } name) { _target = name; _unnamed = null; }
        else { _unnamed = placement; _target = ""; }
        EndPick();
    }

    private void EndPick()
    {
        _picking = false;
        _error = null;
        _input = "";
    }

    private void CancelPick() { _picking = false; _source = null; }

    // Lines between wired entities, on the background draw list (over the world, under the panels): the
    // selection's wires, in and out, bright and labelled, the rest dim.
    public void DrawLines(World world)
    {
        var document = _selection.Document;
        if (!document.IsOpen || !world.TryGetMainView(out var view)) return;
        var io = ImGui.GetIO();
        var camera = new ViewportCamera(view, io.DisplaySize);
        var draw = ImGui.GetBackgroundDrawList();
        var selected = _selection.Placement;
        foreach (var line in WiringModel.Lines(document))
        {
            if (line.To is not { } to) continue;
            if (camera.ToScreen(line.From) is not { } a || camera.ToScreen(to) is not { } b) continue;
            bool hot = selected != null && (ReferenceEquals(line.Source, selected)
                || (selected.Name.Length > 0 && string.Equals(line.Wire.Target, selected.Name, StringComparison.OrdinalIgnoreCase)));
            uint colour = ImGui.ColorConvertFloat4ToU32(hot ? new Vector4(1f, 0.85f, 0.2f, 1f) : new Vector4(0.4f, 0.75f, 0.9f, 0.5f));
            draw.AddLine(a, b, colour, hot ? 3f : 1.5f);
            var dir = b - a;
            if (dir.LengthSquared() > 16f)
            {
                dir = Vector2.Normalize(dir);
                var side = new Vector2(-dir.Y, dir.X);
                draw.AddTriangleFilled(b, b - dir * 12f + side * 5f, b - dir * 12f - side * 5f, colour);
            }
            if (hot) draw.AddText((a + b) * 0.5f, colour, line.Wire.Output + " > " + line.Wire.Input);
        }
    }

    private static Connection Clone(Connection wire) => new()
    {
        Output = wire.Output, Target = wire.Target, Input = wire.Input, Parameter = wire.Parameter,
        Delay = wire.Delay, Times = wire.Times, Requires = wire.Requires,
    };
}
