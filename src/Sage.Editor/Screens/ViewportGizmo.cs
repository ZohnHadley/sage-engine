#nullable enable
using System;
using System.Numerics;
using ImGuiNET;

namespace Sage.Editor;

// Selecting and moving things in the viewport (`-edit`, issue #221): a click picks, the selection's gizmo
// is drawn over the picture and dragged, the editor's keys press the editor's commands, and a small
// toolbar sets the gizmo and the snapping. Everything it decides is Sage.Editing's — EditorPicking finds
// what is under the pointer, TranslateGizmo and RotateGizmo find the handle, GizmoDrag turns a drag into
// one SetPlacement, ViewportTools holds the settings — so this only reads the mouse and draws.
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

    public bool Dragging => _drag != null;

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
            return;
        }
        var camera = new ViewportCamera(view, io.DisplaySize);
        var ray = camera.RayThrough(io.MousePos);
        var document = selection.Document;
        var placement = document.IsOpen ? selection.Placement : null;

        // A drag goes on while the button is down, wherever the pointer wanders (over a panel too).
        if (_drag != null)
        {
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left) || !ReferenceEquals(_drag.Placement, placement)) EndDrag();
            else if (ray is { } now) _drag.Update(now, _tools.GridStep, _tools.AngleStepDegrees);
        }

        Vector3 origin = default;
        float size = 0f;
        GizmoHandle hover = GizmoHandle.None;
        bool ringHover = false;
        if (placement != null)
        {
            origin = ViewportTools.OriginOf(document, placement);
            size = camera.SizeAt(origin, GizmoPixels);
            if (_drag == null && mouseOverViewport && ray is { } r)
            {
                if (_tools.Mode == GizmoMode.Move) hover = TranslateGizmo.HitTest(r, origin, size);
                else ringHover = RotateGizmo.HitTest(r, origin, size);
            }
        }

        // A click on a handle starts a drag; anywhere else in the picture it selects what is under it.
        if (_drag == null && mouseOverViewport && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && ray is { } click)
        {
            if (placement != null && (hover != GizmoHandle.None || ringHover))
                _drag = GizmoDrag.Begin(document, placement, _tools.Mode, hover, click);
            else if (EditorPicking.PickWhere(world, click, pickable) is { } hit) selection.SelectPlaced(hit.Entity);
            else selection.Clear();
        }

        var draw = ImGui.GetBackgroundDrawList();   // over the world, under the panels
        if (placement != null)
        {
            if (_tools.Mode == GizmoMode.Move) DrawMove(draw, camera, origin, size, _drag?.Handle ?? hover);
            else DrawRing(draw, camera, origin, size, _drag != null || ringHover);
        }
        else if (!selection.Entity.IsNull && world.IsAlive(selection.Entity)
                 && selection.Entity.TryGetComponent<GlobalTransform>(out var global)
                 && camera.ToScreen(global.Current.Position) is { } at)
        {
            // Selected but not the document's (a scene's own entity): marked, not movable.
            draw.AddCircle(at, 9f, Colour(1f, 0.85f, 0.2f, 0.9f), 20, 2f);
        }
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
            bool snap = _tools.Snap;
            if (ImGui.Checkbox("Snap", ref snap)) _tools.Snap = snap;
            ImGui.SameLine();
            float grid = _tools.Grid, angle = _tools.AngleStep;
            ImGui.SetNextItemWidth(70f);
            if (ImGui.DragFloat("m##grid", ref grid, 0.05f, 0f, 100f, "%.2f")) _tools.Grid = MathF.Max(grid, 0f);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(60f);
            if (ImGui.DragFloat("deg##angle", ref angle, 1f, 0f, 180f, "%.0f")) _tools.AngleStep = MathF.Max(angle, 0f);
        }
        ImGui.End();
    }

    private static void DrawMove(ImDrawListPtr draw, ViewportCamera camera, Vector3 origin, float size, GizmoHandle active)
    {
        if (camera.ToScreen(origin) is not { } centre) return;

        // The plane squares first, so the axes are drawn over them.
        Plane(GizmoHandle.XY, Vector3.UnitX, Vector3.UnitY, Colour(0.3f, 0.3f, 1f, 0.35f));
        Plane(GizmoHandle.XZ, Vector3.UnitX, Vector3.UnitZ, Colour(0.3f, 1f, 0.3f, 0.35f));
        Plane(GizmoHandle.YZ, Vector3.UnitY, Vector3.UnitZ, Colour(1f, 0.3f, 0.3f, 0.35f));

        Axis(GizmoHandle.X, Colour(0.95f, 0.25f, 0.25f, 1f));
        Axis(GizmoHandle.Y, Colour(0.3f, 0.9f, 0.3f, 1f));
        Axis(GizmoHandle.Z, Colour(0.3f, 0.45f, 1f, 1f));
        draw.AddCircleFilled(centre, 3.5f, Colour(1f, 1f, 1f, 0.9f));

        void Axis(GizmoHandle handle, uint colour)
        {
            if (camera.ToScreen(origin + TranslateGizmo.AxisOf(handle) * size) is not { } end) return;
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

    private static void DrawRing(ImDrawListPtr draw, ViewportCamera camera, Vector3 origin, float size, bool active)
    {
        const int segments = 64;
        uint colour = active ? Highlight : Colour(0.3f, 0.9f, 0.3f, 1f);
        Vector2? previous = null;
        for (int i = 0; i <= segments; i++)
        {
            float a = i * MathF.Tau / segments;
            var point = camera.ToScreen(origin + new Vector3(MathF.Cos(a), 0f, MathF.Sin(a)) * size);
            if (point is { } p && previous is { } q) draw.AddLine(q, p, colour, active ? 4f : 3f);
            previous = point;
        }
        if (camera.ToScreen(origin) is { } centre) draw.AddCircleFilled(centre, 3.5f, Colour(1f, 1f, 1f, 0.9f));
    }

    private static readonly uint Highlight = Colour(1f, 0.85f, 0.2f, 1f);
    private static readonly uint HighlightFill = Colour(1f, 0.85f, 0.2f, 0.55f);

    // IM_COL32: alpha in the high byte, red in the low one.
    private static uint Colour(float r, float g, float b, float a) =>
        (Byte(a) << 24) | (Byte(b) << 16) | (Byte(g) << 8) | Byte(r);

    private static uint Byte(float value) => (uint)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}
