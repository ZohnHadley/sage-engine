#nullable enable
using System;
using System.Numerics;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The editor's terrain tools (issue #372, 15 §11): a brush (raise, lower, smooth, flatten, paint), its
// radius and strength, the water over the sector under the camera, and the sculpt's own undo, redo and
// save. Armed, a left drag in the viewport sculpts where the pointer's ray meets the ground — one undo step
// a drag — and the gizmo leaves the click alone. What a dab does and what is saved are `TerrainDocument`'s
// (Sage.Editing, tested headlessly); this draws, and passes the pointer on.
internal sealed class TerrainPanel
{
    public const string Title = "Terrain";

    private static readonly string[] Tools = { "Raise", "Lower", "Smooth", "Flatten", "Paint" };

    private readonly Func<TerrainDocument?> _document;
    private readonly Func<Vector2, Vector2, EditorRay?> _rayThrough;
    private readonly Func<Vector3> _camera;
    private bool _armed;
    private bool _flattenFixed;
    private float _flatten;
    private float _water;
    private Vector3? _hover;

    public TerrainPanel(Func<TerrainDocument?> document, Func<Vector2, Vector2, EditorRay?> rayThrough, Func<Vector3> camera)
    {
        _document = document;
        _rayThrough = rayThrough;
        _camera = camera;
    }

    // A brush is armed: a viewport drag sculpts, so the gizmo and picking leave it alone.
    public bool IsArmed => _armed;

    public void Draw()
    {
        bool visible = ImGui.Begin(Title);
        var doc = _document();
        if (!visible || doc == null)
        {
            ImGui.End();
            return;
        }
        if (!doc.CanEdit)
        {
            ImGui.TextWrapped("No terrain to sculpt: the scene names no `terrain` record (and the game set no generator).");
            _armed = false;
            ImGui.End();
            return;
        }
        doc.LoadAround(_camera());   // an edit world streams nothing: the ground the camera looks at

        ImGui.TextUnformatted(doc.Title);
        ImGui.TextDisabled(doc.SculptPath);
        if (ImGui.Checkbox("brush armed (drag in the viewport)", ref _armed) && !_armed && doc.Stroking) doc.EndStroke();

        var brush = doc.Brush;
        int tool = (int)brush.Tool;
        if (ImGui.Combo("tool", ref tool, Tools, Tools.Length)) brush.Tool = (TerrainTool)tool;
        ImGui.SliderFloat("radius (m)", ref brush.Radius, 2f, 256f, "%.0f");
        if (brush.Tool is TerrainTool.Raise or TerrainTool.Lower) ImGui.SliderFloat("metres a dab", ref brush.Strength, 0.05f, 10f, "%.2f");
        else
        {
            brush.Strength = Math.Clamp(brush.Strength, 0f, 1f);
            ImGui.SliderFloat("strength", ref brush.Strength, 0.01f, 1f, "%.2f");
        }
        if (brush.Tool == TerrainTool.Paint) ImGui.SliderInt("layer", ref brush.Layer, 0, 3);
        if (brush.Tool == TerrainTool.Flatten)
        {
            ImGui.Checkbox("to a height", ref _flattenFixed);
            if (_flattenFixed) ImGui.InputFloat("height (m)", ref _flatten);
            else ImGui.TextDisabled("to where each stroke begins");
            brush.FlattenHeight = _flattenFixed ? _flatten : null;
        }
        if (_hover is { } at) ImGui.TextDisabled($"ground at {at.X:0.0} {at.Y:0.00} {at.Z:0.0}");

        ImGui.Separator();
        if (doc.Terrain is { } terrain)
        {
            var sector = terrain.Origin.SectorOf(_camera());
            var current = doc.Sculpt.Get(sector)?.Water;
            ImGui.TextUnformatted($"water over sector {sector}: {(current is { } w ? $"{w:0.0} m" : "none")}");
            ImGui.InputFloat("surface (m)", ref _water);
            if (ImGui.SmallButton("set water")) doc.SetWater(sector, _water);
            ImGui.SameLine();
            if (ImGui.SmallButton("no water")) doc.SetWater(sector, null);
        }

        ImGui.Separator();
        if (ImGui.SmallButton("undo")) doc.Undo();
        ImGui.SameLine();
        if (ImGui.SmallButton("redo")) doc.Redo();
        ImGui.SameLine();
        if (ImGui.SmallButton(doc.Dirty ? "save *" : "save")) doc.Save();
        var history = doc.History;
        for (int i = history.Entries.Count - 1; i >= 0 && i >= history.Entries.Count - 8; i--)
            ImGui.TextDisabled($"{(i < history.Position ? " " : "~")} {history.Entries[i].Description}");
        ImGui.End();
    }

    // After the panels are drawn: armed, a left drag that ImGui does not want sculpts, a dab a frame; Escape
    // disarms. The drag is one stroke, so one undo step.
    public void HandleViewport()
    {
        _hover = null;
        if (!_armed || _document() is not { CanEdit: true } doc) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { _armed = false; if (doc.Stroking) doc.EndStroke(); return; }
        var io = ImGui.GetIO();
        if (doc.Stroking && !ImGui.IsMouseDown(ImGuiMouseButton.Left)) { doc.EndStroke(); return; }
        if (io.WantCaptureMouse && !doc.Stroking) return;
        if (_rayThrough(io.MousePos, io.DisplaySize) is not { } ray || doc.Pick(ray) is not { } point) return;
        _hover = point;
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)) doc.BeginStroke();
        if (doc.Stroking) doc.Dab(point);
    }
}
