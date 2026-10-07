#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;
using Sage.Editing;

namespace Sage.Editor;

// The Brushes panel (issue #61): blocking a level out of boxes, wedges and cylinders. Arm a shape and click
// in the viewport to stand one there on the grid; arm a material and click a face to paint it (Shift: the
// whole brush); and the selected brush's shape, size and per-face materials as controls. A grid is drawn
// on the floor the selection stands on. Everything it changes is BlockoutTools' (Sage.Editing, tested
// headlessly), so each edit is one undo step and the same as an `ed_brush*` command; moving, turning and
// scaling a brush are the gizmo's, as for any placement.
internal sealed class BrushPanel
{
    public const string Title = "Brushes";

    private enum Tool { None, Place, Paint }

    private readonly Engine _engine;
    private readonly EditorSelection _selection;
    private readonly ViewportTools _tools;
    private readonly Func<Vector2, Vector2, EditorRay?> _rayThrough;

    private Tool _tool;
    private BrushShape _shape = BrushShape.Box;
    private Vector3 _size = new(2f, 2f, 2f);
    private RecordId _material;     // what a new brush is made of, and what painting puts on
    private bool _showGrid = true;
    private const int GridLines = 40;

    public BrushPanel(Engine engine, EditorSelection selection, ViewportTools tools, Func<Vector2, Vector2, EditorRay?> rayThrough)
    {
        _engine = engine;
        _selection = selection;
        _tools = tools;
        _rayThrough = rayThrough;
    }

    // A shape or a material is in hand: the next viewport click is this panel's, not the gizmo's.
    public bool IsArmed => _tool != Tool.None;

    public void Draw()
    {
        if (!ImGui.Begin(Title))
        {
            ImGui.End();
            return;
        }
        var document = _selection.Document;
        if (!document.IsOpen)
        {
            ImGui.TextDisabled("open a document to build in (File > New or doc_open)");
            ImGui.End();
            return;
        }

        // ---- New brushes ----
        ImGui.SeparatorText("New brush");
        foreach (var shape in new[] { BrushShape.Box, BrushShape.Wedge, BrushShape.Cylinder })
        {
            bool armed = _tool == Tool.Place && _shape == shape;
            if (armed) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.6f, 0.2f, 1f));
            if (ImGui.Button(shape.ToString()))
            {
                _tool = armed ? Tool.None : Tool.Place;
                _shape = shape;
            }
            if (armed) ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        ImGui.NewLine();
        ImGui.DragFloat3("size##new", ref _size, 0.05f, 0.01f, 1000f, "%.2f m");
        MaterialCombo("material##new", ref _material, "(plain)");

        if (_tool == Tool.Place) ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), $"Click in the viewport to stand a {_shape.ToString().ToLowerInvariant()} there; Esc to stop");
        bool painting = _tool == Tool.Paint;
        if (ImGui.Checkbox("Paint faces with this material", ref painting)) _tool = painting ? Tool.Paint : Tool.None;
        if (_tool == Tool.Paint) ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f), "Click a face to paint it, Shift+click for the whole brush; Esc to stop");

        // ---- The grid ----
        ImGui.SeparatorText("Grid");
        ImGui.Checkbox("Show grid", ref _showGrid);
        ImGui.SameLine();
        ImGui.TextDisabled(_tools.GridStep > 0f ? $"{_tools.GridStep:0.###} m (ed_grid, ed_snap)" : "snapping off (ed_snap 1)");

        // ---- The selection ----
        ImGui.SeparatorText("Selected brush");
        if (_selection.Placement is { } placement && BlockoutTools.TryGet(document, placement, out var brush))
            DrawBrush(document, placement, brush);
        else ImGui.TextDisabled("select a brush to change its shape, size and faces");

        ImGui.End();
    }

    private void DrawBrush(EditDocument document, Placement placement, BlockoutBrush brush)
    {
        ImGui.TextUnformatted(placement.Name.Length > 0 ? placement.Name : placement.Prefab.Id.ToString());

        int shape = (int)brush.Shape;
        if (ImGui.Combo("shape", ref shape, "Box\0Wedge\0Cylinder\0") && shape != (int)brush.Shape)
            BlockoutTools.SetShape(document, placement, (BrushShape)shape);

        if (brush.Shape == BrushShape.Cylinder)
        {
            int sides = brush.Sides;
            if (ImGui.SliderInt("sides", ref sides, BrushPart.MinSides, BrushPart.MaxSides) && sides != brush.Sides)
                BlockoutTools.SetSides(document, placement, sides);
            if (ImGui.IsItemDeactivatedAfterEdit()) document.History.EndMerge();
        }

        // The size the part writes, not the scaled one: the scale gizmo multiplies it.
        var scale = placement.Scale;
        var size = new Vector3(brush.Size.X / scale.X, brush.Size.Y / scale.Y, brush.Size.Z / scale.Z);
        if (ImGui.DragFloat3("size", ref size, MathF.Max(_tools.GridStep, 0.05f), 0.01f, 1000f, "%.2f m"))
            BlockoutTools.SetSize(document, placement, size, _tools.GridStep);
        if (ImGui.IsItemDeactivatedAfterEdit()) document.History.EndMerge();

        var material = brush.Material;
        if (MaterialCombo("material", ref material, "(plain)")) BlockoutTools.SetMaterial(document, placement, null, material);

        foreach (var face in BrushPart.FacesOf(brush.Shape))
        {
            var own = brush.FaceMaterial(face);
            if (MaterialCombo(BlockoutTools.FieldOf(face), ref own, "(the brush's)")) BlockoutTools.SetMaterial(document, placement, face, own);
        }
    }

    // A combo of every material record, with `none` as the empty choice. True when one was picked.
    private bool MaterialCombo(string label, ref RecordId material, string none)
    {
        bool picked = false;
        if (!ImGui.BeginCombo(label, material.IsEmpty ? none : material.ToString())) return false;
        if (ImGui.Selectable(none, material.IsEmpty)) { material = default; picked = true; }
        foreach (var id in BlockoutTools.Materials(_engine))
            if (ImGui.Selectable(id.ToString(), id == material)) { material = id; picked = true; }
        ImGui.EndCombo();
        return picked;
    }

    // After the panels: the grid over the picture, and a click with a shape or a material in hand.
    public void HandleViewport(World world)
    {
        if (_showGrid) DrawGrid(world);
        if (_tool == Tool.None) return;
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) { _tool = Tool.None; return; }
        var io = ImGui.GetIO();
        if (io.WantCaptureMouse || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
        var document = _selection.Document;
        if (!document.IsOpen || _rayThrough(io.MousePos, io.DisplaySize) is not { } ray) return;

        if (_tool == Tool.Paint)
        {
            if (BlockoutTools.PaintAt(document, ray, _material, whole: io.KeyShift) is { } painted) _selection.Select(painted.Placement);
            return;
        }
        if (Placing.Surface(world, ray) is not { } point) return;
        if (BlockoutTools.Place(document, _shape, point, _size, _material, grid: _tools.GridStep) is { } placed) _selection.Select(placed);
    }

    // Lines every grid step on the floor the selection stands on (else y = 0), round the point under the
    // middle of the screen, fading out towards the edge.
    private void DrawGrid(World world)
    {
        if (!world.TryGetMainView(out var view)) return;
        var io = ImGui.GetIO();
        var camera = new ViewportCamera(view, io.DisplaySize);
        float step = _tools.Grid > 0f ? _tools.Grid : 1f;
        while (step * GridLines < 8f) step *= 2f;   // a fine grid is drawn coarser rather than as a smear

        float floor = 0f;
        if (_selection.Placement is { } placement && _selection.Document.IsOpen)
            floor = ViewportTools.OriginOf(_selection.Document, placement).Y;

        var centre = new Vector3(view.Position.X, floor, view.Position.Z);
        if (camera.RayThrough(io.DisplaySize * 0.5f) is { } look && MathF.Abs(look.Direction.Y) > 1e-4f)
        {
            float t = (floor - look.Origin.Y) / look.Direction.Y;
            if (t > 0f && t < 200f) centre = look.At(t);
        }
        centre = new Vector3(Snap.ToGrid(centre.X, step), floor, Snap.ToGrid(centre.Z, step));

        var draw = ImGui.GetBackgroundDrawList();
        float half = step * GridLines / 2f;
        for (int i = -GridLines / 2; i <= GridLines / 2; i++)
        {
            float alpha = 0.35f * (1f - MathF.Abs(i) / (GridLines / 2f + 1f));
            uint colour = ImGui.GetColorU32(i == 0 ? new Vector4(0.9f, 0.9f, 1f, alpha + 0.15f) : new Vector4(0.8f, 0.8f, 0.9f, alpha));
            Line(draw, camera, centre + new Vector3(i * step, 0, -half), centre + new Vector3(i * step, 0, half), colour);
            Line(draw, camera, centre + new Vector3(-half, 0, i * step), centre + new Vector3(half, 0, i * step), colour);
        }
    }

    // A world line drawn as screen segments, cut where it goes behind the camera.
    private static void Line(ImDrawListPtr draw, in ViewportCamera camera, Vector3 from, Vector3 to, uint colour)
    {
        const int Pieces = 8;
        Vector2? last = camera.ToScreen(from);
        for (int i = 1; i <= Pieces; i++)
        {
            var next = camera.ToScreen(Vector3.Lerp(from, to, i / (float)Pieces));
            if (last is { } a && next is { } b) draw.AddLine(a, b, colour, 1f);
            last = next;
        }
    }
}
