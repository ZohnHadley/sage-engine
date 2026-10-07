#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// Selecting and moving things in the viewport (`-edit`, issue #221): a click picks, the selection's gizmo
// is drawn over the picture and dragged, the editor's keys press the editor's commands, and a small
// toolbar sets the gizmo and the snapping. Everything it decides is Sage.Editing's — EditorPicking finds
// what is under the pointer (and, for a box, EditorPicking.PlacementsInBox), TranslateGizmo, RotateGizmo
// and ScaleGizmo find the handle, GizmoDrag turns a drag into one SetPlacement (SetPlacements for a
// selection of several, #367), ViewportTools holds the settings — so this only reads the mouse and draws.
//
// **The viewport is the screen** (§10f): the free camera draws the whole back buffer and the docked
// panels lie over its edges, so a pixel under the mouse is a pixel of that camera's picture, and the
// mouse is the viewport's wherever ImGui does not want it (the dock's pass-through middle).
internal sealed class ViewportGizmo
{
    private const float GizmoPixels = 110f;   // how long an axis handle looks, at any distance

    private readonly ViewportTools _tools;
    private readonly CVarRegistry _cvars;
    private GizmoDrag? _drag;

    public ViewportGizmo(ViewportTools tools, CVarRegistry cvars)
    {
        _tools = tools;
        _cvars = cvars;
    }

    public bool Dragging => _drag != null || _boxFrom != null;

    // A box being dragged out on empty picture (issue #367): where the mouse went down.
    private Vector2? _boxFrom;
    private const float BoxThreshold = 4f;   // pixels the mouse moves before a click becomes a box

    // One frame: inside ImGui's frame, after the dock space. `pickable` leaves out the editor's cameras.
    public void Draw(World world, EditorSelection selection, Func<Entity, bool> pickable)
    {
        var io = ImGui.GetIO();
        bool mouseOverViewport = !io.WantCaptureMouse;   // last frame's answer: over the dock's pass-through
        Shortcuts(io, selection);
        DrawToolbar();

        if (!world.TryGetMainView(out var view))
        {
            EndDrag();
            _boxFrom = null;
            return;
        }
        var camera = new ViewportCamera(view, io.DisplaySize);
        var ray = camera.RayThrough(io.MousePos);
        var document = selection.Document;
        var placement = document.IsOpen ? selection.Placement : null;
        var group = document.IsOpen ? selection.Placements : Array.Empty<Placement>();

        // A drag goes on while the button is down, wherever the pointer wanders (over a panel too).
        if (_drag != null)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !SameGroup(_drag.Placements, group)) EndDrag();
            else if (ray is { } now) _drag.Update(now, _tools.GridStep, _tools.AngleStepDegrees, _tools.ScaleStepFactor);
        }

        Vector3 origin = default;
        Quaternion axes = Quaternion.Identity;
        float size = 0f;
        GizmoHandle hover = GizmoHandle.None;
        if (placement != null)
        {
            origin = ViewportTools.OriginOf(document, placement);
            axes = _drag?.Axes ?? ViewportTools.AxesOf(placement, _tools.Mode == GizmoMode.Scale ? GizmoSpace.Local : _tools.Space);
            if (_drag != null) origin = _drag.Origin;
            size = camera.SizeAt(origin, GizmoPixels);
            if (_drag == null && _boxFrom == null && mouseOverViewport && ray is { } r)
                hover = _tools.Mode switch
                {
                    GizmoMode.Move => TranslateGizmo.HitTest(r, origin, size, axes),
                    GizmoMode.Rotate => RotateGizmo.HitTest(r, origin, size, axes),
                    _ => ScaleGizmo.HitTest(r, origin, size, axes),
                };
        }

        // A click on a handle starts a drag; anywhere else in the picture it may start a box, and if the
        // mouse comes up without moving it selects what is under it (Ctrl: adds it, or takes it out).
        if (_drag == null && _boxFrom == null && mouseOverViewport && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ray is { } click)
        {
            if (placement != null && hover != GizmoHandle.None)
                _drag = GizmoDrag.Begin(document, group, _tools.Mode, hover, click, _tools.Space);
            else _boxFrom = io.MousePos;
        }

        var draw = ImGui.GetBackgroundDrawList();   // over the world, under the panels
        if (_boxFrom is { } from)
        {
            bool isBox = Vector2.Distance(from, io.MousePos) > BoxThreshold;
            if (isBox)
            {
                draw.AddRectFilled(Vector2.Min(from, io.MousePos), Vector2.Max(from, io.MousePos), Colour(0.4f, 0.6f, 1f, 0.15f));
                draw.AddRect(Vector2.Min(from, io.MousePos), Vector2.Max(from, io.MousePos), Colour(0.4f, 0.6f, 1f, 0.9f), 0f, ImDrawFlags.None, 1.5f);
            }
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                _boxFrom = null;
                if (isBox)
                {
                    var inBox = EditorPicking.PlacementsInBox(document, camera, from, io.MousePos);
                    if (io.KeyCtrl || io.KeyShift) selection.Select(group.Concat(inBox).Distinct(ReferenceEqualityComparer.Instance).Cast<Placement>());
                    else selection.Select(inBox);
                }
                else if (camera.RayThrough(from) is { } at)
                {
                    if (EditorPicking.PickWhere(world, at, pickable) is { } hit) selection.SelectPlaced(hit.Entity, toggle: io.KeyCtrl);
                    else if (!io.KeyCtrl) selection.Clear();
                }
            }
        }

        // Every selected placement but the gizmo's is marked where it stands.
        foreach (var other in group)
            if (!ReferenceEquals(other, placement) && camera.ToScreen(ViewportTools.OriginOf(document, other)) is { } mark)
                draw.AddCircle(mark, 7f, Colour(1f, 0.85f, 0.2f, 0.9f), 16, 2f);

        if (placement != null)
        {
            var active = _drag?.Handle ?? hover;
            switch (_tools.Mode)
            {
                case GizmoMode.Move: DrawMove(draw, camera, origin, size, axes, active); break;
                case GizmoMode.Rotate: DrawRings(draw, camera, origin, size, axes, active); break;
                default: DrawScale(draw, camera, origin, size, axes, active); break;
            }
        }
        else if (!selection.Entity.IsNull && world.IsAlive(selection.Entity)
                 && selection.Entity.TryGetComponent<GlobalTransform>(out var global)
                 && camera.ToScreen(global.Current.Position) is { } at)
        {
            // Selected but not the document's (a scene's own entity): marked, not movable.
            draw.AddCircle(at, 9f, Colour(1f, 0.85f, 0.2f, 0.9f), 20, 2f);
        }
    }

    private static bool SameGroup(IReadOnlyList<Placement> a, IReadOnlyList<Placement> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false;
        return true;
    }

    // The editor's keys, as the commands they press — never while ImGui has the keyboard (the console).
    private void Shortcuts(ImGuiIOPtr io, EditorSelection selection)
    {
        if (io.WantCaptureKeyboard) return;
        bool ctrl = io.KeyCtrl;
        if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Z, false)) Press(io.KeyShift ? "ed_redo" : "ed_undo");
        else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.Y, false)) Press("ed_redo");
        else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.D, false) && selection.Placement != null) Press("ed_duplicate");
        else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.Delete, false) && selection.Placement != null) Press("ed_delete");
        else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.F, false) && !selection.IsEmpty) Press("ed_frame");
        else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.G, false)) _tools.Mode = GizmoMode.Move;
        else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.R, false)) _tools.Mode = GizmoMode.Rotate;
        else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.T, false)) _tools.Mode = GizmoMode.Scale;
        else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.L, false))
            _tools.Space = _tools.Space == GizmoSpace.World ? GizmoSpace.Local : GizmoSpace.World;
    }

    private void Press(string command)
    {
        EndDrag();   // an undo mid-drag ends the drag first, so it undoes the drag
        _cvars.Execute(command, ExecSource.Console);
    }

    private void EndDrag()
    {
        _drag?.End();
        _drag = null;
    }

    // The gizmo and the snapping, at the top of the picture.
    private void DrawToolbar()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(new Vector2(viewport.WorkPos.X + viewport.WorkSize.X * 0.5f, viewport.WorkPos.Y + 6f), ImGuiCond.Always, new Vector2(0.5f, 0f));
        ImGui.SetNextWindowBgAlpha(0.75f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove;
        if (ImGui.Begin("##sage_viewport_tools", flags))
        {
            if (ImGui.RadioButton("Move (G)", _tools.Mode == GizmoMode.Move)) _tools.Mode = GizmoMode.Move;
            ImGui.SameLine();
            if (ImGui.RadioButton("Rotate (R)", _tools.Mode == GizmoMode.Rotate)) _tools.Mode = GizmoMode.Rotate;
            ImGui.SameLine();
            if (ImGui.RadioButton("Scale (T)", _tools.Mode == GizmoMode.Scale)) _tools.Mode = GizmoMode.Scale;
            ImGui.SameLine();
            bool local = _tools.Space == GizmoSpace.Local;
            if (ImGui.Checkbox("Local (L)", ref local)) _tools.Space = local ? GizmoSpace.Local : GizmoSpace.World;
            ImGui.SameLine();
            bool snap = _tools.Snap;
            if (ImGui.Checkbox("Snap", ref snap)) _tools.Snap = snap;
            ImGui.SameLine();
            float grid = _tools.Grid, angle = _tools.AngleStep;
            ImGui.SetNextItemWidth(70f);
            if (ImGui.DragFloat("m##grid", ref grid, 0.05f, 0f, 100f, "%.2f")) _tools.Grid = MathF.Max(grid, 0f);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60f);
            if (ImGui.DragFloat("deg##angle", ref angle, 1f, 0f, 180f, "%.0f")) _tools.AngleStep = MathF.Max(angle, 0f);
            ImGui.SameLine();
            float scaleStep = _tools.ScaleStep;
            ImGui.SetNextItemWidth(60f);
            if (ImGui.DragFloat("x##scalestep", ref scaleStep, 0.01f, 0f, 10f, "%.2f")) _tools.ScaleStep = MathF.Max(scaleStep, 0f);
        }
        ImGui.End();
    }

    private static void DrawMove(ImDrawListPtr draw, ViewportCamera camera, Vector3 origin, float size, Quaternion axes, GizmoHandle active)
    {
        if (camera.ToScreen(origin) is not { } centre) return;
        Vector3 Dir(GizmoHandle h) => Vector3.Transform(TranslateGizmo.AxisOf(h), axes);

        // The plane squares first, so the axes are drawn over them.
        Plane(GizmoHandle.XY, Dir(GizmoHandle.X), Dir(GizmoHandle.Y), Colour(0.3f, 0.3f, 1f, 0.35f));
        Plane(GizmoHandle.XZ, Dir(GizmoHandle.X), Dir(GizmoHandle.Z), Colour(0.3f, 1f, 0.3f, 0.35f));
        Plane(GizmoHandle.YZ, Dir(GizmoHandle.Y), Dir(GizmoHandle.Z), Colour(1f, 0.3f, 0.3f, 0.35f));

        Axis(GizmoHandle.X, AxisColour(GizmoHandle.X));
        Axis(GizmoHandle.Y, AxisColour(GizmoHandle.Y));
        Axis(GizmoHandle.Z, AxisColour(GizmoHandle.Z));
        draw.AddCircleFilled(centre, 3.5f, Colour(1f, 1f, 1f, 0.9f));

        void Axis(GizmoHandle handle, uint colour)
        {
            if (camera.ToScreen(origin + Dir(handle) * size) is not { } end) return;
            uint c = handle == active ? Highlight : colour;
            draw.AddLine(centre, end, c, handle == active ? 4f : 3f);
            draw.AddCircleFilled(end, 5f, c);
        }

        void Plane(GizmoHandle handle, Vector3 u, Vector3 v, uint colour)
        {
            const float near = 0.25f, far = 0.5f;   // TranslateGizmo's plane square
            if (camera.ToScreen(origin + (u * near + v * near) * size) is not { } a
                || camera.ToScreen(origin + (u * far + v * near) * size) is not { } b
                || camera.ToScreen(origin + (u * far + v * far) * size) is not { } c
                || camera.ToScreen(origin + (u * near + v * far) * size) is not { } d) return;
            draw.AddQuadFilled(a, b, c, d, handle == active ? HighlightFill : colour);
        }
    }

    // A ring about each axis, coloured as the axis it turns about.
    private static void DrawRings(ImDrawListPtr draw, ViewportCamera camera, Vector3 origin, float size, Quaternion axes, GizmoHandle active)
    {
        const int segments = 64;
        foreach (var axis in new[] { GizmoHandle.X, GizmoHandle.Y, GizmoHandle.Z })
        {
            uint colour = axis == active ? Highlight : AxisColour(axis);
            Vector2? previous = null;
            for (int i = 0; i <= segments; i++)
            {
                var point = camera.ToScreen(RotateGizmo.RingPoint(axis, origin, size, axes, i * MathF.Tau / segments));
                if (point is { } p && previous is { } q) draw.AddLine(q, p, colour, axis == active ? 4f : 2.5f);
                previous = point;
            }
        }
        if (camera.ToScreen(origin) is { } centre) draw.AddCircleFilled(centre, 3.5f, Colour(1f, 1f, 1f, 0.9f));
    }

    // An axis with a box at its end for each of the placement's own axes, and a centre for all three.
    private static void DrawScale(ImDrawListPtr draw, ViewportCamera camera, Vector3 origin, float size, Quaternion axes, GizmoHandle active)
    {
        if (camera.ToScreen(origin) is not { } centre) return;
        foreach (var axis in new[] { GizmoHandle.X, GizmoHandle.Y, GizmoHandle.Z })
        {
            if (camera.ToScreen(origin + Vector3.Transform(TranslateGizmo.AxisOf(axis), axes) * size) is not { } end) continue;
            uint c = axis == active ? Highlight : AxisColour(axis);
            draw.AddLine(centre, end, c, axis == active ? 4f : 3f);
            draw.AddRectFilled(end - new Vector2(5f), end + new Vector2(5f), c);
        }
        draw.AddCircleFilled(centre, active == GizmoHandle.All ? 8f : 6f, active == GizmoHandle.All ? Highlight : Colour(1f, 1f, 1f, 0.9f));
    }

    private static uint AxisColour(GizmoHandle axis) => axis switch
    {
        GizmoHandle.X => Colour(0.95f, 0.25f, 0.25f, 1f),
        GizmoHandle.Y => Colour(0.3f, 0.9f, 0.3f, 1f),
        _ => Colour(0.3f, 0.45f, 1f, 1f),
    };

    private static readonly uint Highlight = Colour(1f, 0.85f, 0.2f, 1f);
    private static readonly uint HighlightFill = Colour(1f, 0.85f, 0.2f, 0.55f);

    // IM_COL32: alpha in the high byte, red in the low one.
    private static uint Colour(float r, float g, float b, float a) =>
        (Byte(a) << 24) | (Byte(b) << 16) | (Byte(g) << 8) | Byte(r);

    private static uint Byte(float value) => (uint)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
