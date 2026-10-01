#nullable enable
using System;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Selecting and moving things (issue #221). The viewport only reads the mouse and draws; what a click, a
// key or a drag does is Sage.Editing's — the selection, the console's ed_* commands and a gizmo drag — so
// it is pressed here the way a person would, through the console, and checked in the document and world.
public class ViewportToolsTests
{
    public ViewportToolsTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [
          { "type": "prefab", "id": "post", "components": { "transform": {} } },
          { "type": "prefab", "id": "lamp", "components": { "transform": {} } },
          { "type": "prefab", "id": "cart", "children": [ { "prefab": "lamp", "at": [0, 1, 0], "name": "cart lamp" } ] },
          {
            "type": "placements",
            "id": "yard",
            "place": [
              { "prefab": "post", "at": [10, 0, -4], "name": "corner" },
              { "prefab": "post", "at": [0, 0, 0], "name": "gate" },
              { "prefab": "cart", "at": [4, 0, 4], "name": "cart" }
            ]
          }
        ]
        """;

    private sealed record Editor(Engine Engine, MountFixture Fixture, EditDocument Document, EditorSelection Selection, ViewportTools Tools)
        : IDisposable
    {
        public CVarRegistry Console => Engine.CVars;
        public void Dispose() => Engine.Dispose();
    }

    private static Editor Open()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records);
        fixture.Mount("game", "sandbox");
        var engine = HeadlessApp.Bare().Mount(fixture).Build().Engine;

        var document = new EditDocument(engine.CreateWorld("edit"));
        var selection = new EditorSelection(document);
        EditorCommands.Register(engine.CVars, () => document);
        var tools = ViewportTools.Register(engine.CVars, () => selection);
        Assert.True(engine.CVars.Execute("doc_open yard"));
        return new Editor(engine, fixture, document, selection, tools);
    }

    private static Vector3 PositionOf(Entity entity) => entity.GetComponent<Transform>().LocalPosition;

    [Fact]
    public void TheConsoleSelectsByNameAndTheSelectionFollowsAReSpawn()
    {
        using var editor = Open();
        var (document, selection, console) = (editor.Document, editor.Selection, editor.Console);

        console.Execute("ed_select corner");
        var corner = document.Find("corner")!;
        Assert.Same(corner, selection.Placement);
        var before = selection.Entity;
        Assert.Equal(document.EntityOf(corner), before);

        // A move re-spawns the placement: the entity a click found is gone, the selection has the new one.
        int changes = 0;
        selection.Changed += () => changes++;
        console.Execute("ed_move 1 2 3");
        Assert.Equal(new Vector3(1, 2, 3), corner.At);
        Assert.False(document.World.IsAlive(before));
        Assert.True(document.World.IsAlive(selection.Entity));
        Assert.Same(corner, selection.Placement);
        Assert.Equal(new Vector3(1, 2, 3), PositionOf(selection.Entity));
        Assert.True(selection.Is(selection.Entity));
        Assert.True(changes > 0);   // the outliner and inspector hear about it

        // A click on a prefab's child selects the placed thing; the outliner's row selects exactly what it says.
        var lamp = document.World.FindByName("cart lamp");
        selection.SelectPlaced(lamp);
        Assert.Same(document.Find("cart"), selection.Placement);
        selection.Select(lamp);
        Assert.Null(selection.Placement);
        Assert.Equal(lamp, selection.Entity);

        console.Execute("ed_select");
        Assert.True(selection.IsEmpty);
        console.Execute("ed_select cart lamp");   // not a placement: the entity by name
        Assert.Equal(lamp, selection.Entity);

        // Closing the document takes the selection with it.
        console.Execute("ed_select gate");
        Assert.NotNull(selection.Placement);
        console.Execute("doc_close");
        Assert.True(selection.IsEmpty);
    }

    [Fact]
    public void MovingAndRotatingFromTheConsoleAreAnUndoStepEach()
    {
        using var editor = Open();
        var (document, console) = (editor.Document, editor.Console);
        var gate = document.Find("gate")!;

        console.Execute("ed_move gate 2 0 1");
        console.Execute("ed_move gate 3 0 1");     // the same placement again: a step of its own, not merged
        console.Execute("ed_rotate gate 450");     // turns are kept in (-180, 180]
        Assert.Equal(new Vector3(3, 0, 1), gate.At);
        Assert.Equal(90f, gate.Yaw);
        Assert.Equal(3, document.History.Entries.Count);
        Assert.Equal("Rotate gate", document.History.Entries[2].Description);

        console.Execute("ed_undo");
        Assert.Equal(0f, gate.Yaw);
        console.Execute("ed_undo");
        Assert.Equal(new Vector3(2, 0, 1), gate.At);
        console.Execute("ed_undo");
        Assert.Equal(Vector3.Zero, gate.At);
        Assert.False(document.Dirty);

        // Nothing selected and nothing named: refused, nothing done.
        console.Execute("ed_move 1 1 1");
        console.Execute("ed_rotate nobody 10");
        Assert.Equal(3, document.History.Entries.Count);
        Assert.Equal(0, document.History.Position);
    }

    [Fact]
    public void DeletingClearsTheSelectionAndUndoPutsThePlacementBack()
    {
        using var editor = Open();
        var (document, selection, console) = (editor.Document, editor.Selection, editor.Console);

        console.Execute("ed_select gate");
        var gate = selection.Placement!;
        var entity = selection.Entity;
        console.Execute("ed_delete");
        Assert.Equal(2, document.Placements.Count);
        Assert.False(document.World.IsAlive(entity));
        Assert.True(selection.IsEmpty);   // its placement left the document

        console.Execute("ed_undo");
        Assert.Equal(3, document.Placements.Count);
        Assert.Equal(1, document.IndexOf(gate));
        Assert.True(document.World.IsAlive(document.EntityOf(gate)));

        console.Execute("ed_delete corner");   // by name, with nothing selected
        Assert.Null(document.Find("corner"));
        console.Execute("ed_undo");
        Assert.NotNull(document.Find("corner"));
    }

    [Fact]
    public void DuplicatingMakesACopyWithANameOfItsOwnAndSelectsIt()
    {
        using var editor = Open();
        var (document, selection, console) = (editor.Document, editor.Selection, editor.Console);
        var corner = document.Find("corner")!;

        console.Execute("ed_duplicate corner");
        var copy = selection.Placement!;
        Assert.NotSame(corner, copy);
        Assert.Equal("corner_2", copy.Name);
        Assert.Equal("corner_2", copy.Id);          // its identity in saves is its own
        Assert.Equal(1, document.IndexOf(copy));     // straight after the original
        Assert.Equal(corner.At, copy.At);
        Assert.Equal(corner.Prefab.Id, copy.Prefab.Id);
        Assert.True(document.World.IsAlive(selection.Entity));
        Assert.Equal("corner_2", selection.Entity.Name);

        console.Execute("ed_duplicate");               // the selection, which is the copy now
        Assert.Equal("corner_3", selection.Placement!.Name);
        Assert.Equal(5, document.Placements.Count);

        console.Execute("ed_undo");
        Assert.Equal(4, document.Placements.Count);
        Assert.True(selection.IsEmpty);                // the copy it had selected is gone
        console.Execute("ed_undo");
        Assert.Equal(3, document.Placements.Count);
        Assert.Equal(new[] { "corner", "gate", "cart" }, document.Placements.Select(p => p.Name));
    }

    // A drag sends a SetPlacement every frame; they are one undo step, and the next drag is another.
    [Fact]
    public void ADragIsOneSetPlacementAndSnapsToTheGrid()
    {
        using var editor = Open();
        var (document, tools) = (editor.Document, editor.Tools);
        var corner = document.Find("corner")!;   // at (10, 0, -4)

        // Rays straight down onto the X axis: the pointer at x = 10.5, then further along.
        static EditorRay Down(float x, float z) => new(new Vector3(x, 10, z), -Vector3.UnitY);
        var drag = GizmoDrag.Begin(document, corner, GizmoMode.Move, GizmoHandle.X, Down(10.5f, -4))!;
        Assert.NotNull(drag);
        Assert.Equal(new Vector3(10, 0, -4), drag.Origin);
        Assert.False(drag.Update(Down(10.5f, -4), tools.GridStep));   // not moved yet: no edit
        Assert.True(drag.Update(Down(11.1f, -4), tools.GridStep));
        Assert.True(drag.Update(Down(12.2f, -3), tools.GridStep));    // 11.7 snaps to 11.5; z is not the X handle's
        drag.End();
        Assert.Equal(new Vector3(11.5f, 0, -4), corner.At);
        Assert.Single(document.History.Entries);

        // A turn: from the ring's +X to its -Z is a quarter turn the way yaw goes.
        var turn = GizmoDrag.Begin(document, corner, GizmoMode.Rotate, GizmoHandle.None, Down(12.5f, -4))!;
        Assert.True(turn.Update(Down(11.5f, -5), 0f, tools.AngleStepDegrees));
        turn.End();
        Assert.Equal(90f, corner.Yaw, 3);
        Assert.Equal(2, document.History.Entries.Count);

        document.Undo();
        Assert.Equal(0f, corner.Yaw);
        document.Undo();
        Assert.Equal(new Vector3(10, 0, -4), corner.At);

        // With snapping off the drag goes where the pointer does.
        tools.Snap = false;
        var free = GizmoDrag.Begin(document, corner, GizmoMode.Move, GizmoHandle.XZ, Down(10.4f, -3.6f))!;
        free.Update(Down(11.1f, -2.9f), tools.GridStep);
        free.End();
        Assert.Equal(10.7f, corner.At.X, 4);
        Assert.Equal(-3.3f, corner.At.Z, 4);
    }

    [Fact]
    public void TheSnappingAndTheGizmoAreSetFromTheConsole()
    {
        using var editor = Open();
        var (tools, console) = (editor.Tools, editor.Console);
        Assert.Equal(ViewportTools.DefaultGrid, tools.GridStep);
        Assert.Equal(ViewportTools.DefaultAngleStep, tools.AngleStepDegrees);

        console.Execute("ed_grid 0.25");
        console.Execute("ed_angle 5");
        Assert.Equal(0.25f, tools.GridStep);
        Assert.Equal(5f, tools.AngleStepDegrees);
        console.Execute("ed_snap 0");
        Assert.Equal(0f, tools.GridStep);
        Assert.Equal(0f, tools.AngleStepDegrees);
        console.Execute("ed_snap 1");
        Assert.Equal(0.25f, tools.GridStep);

        console.Execute("ed_gizmo rotate");
        Assert.Equal(GizmoMode.Rotate, tools.Mode);
        console.Execute("ed_gizmo move");
        Assert.Equal(GizmoMode.Move, tools.Mode);

        Assert.Equal(new Vector3(0, 0, 6), ViewportTools.FramePosition(Vector3.Zero, Quaternion.Identity, 6f));   // looking down -Z
        Assert.Equal("gate_2", ViewportTools.UniqueName(editor.Document, "gate"));
        Assert.Equal("cart_7", ViewportTools.UniqueName(editor.Document, "cart_7"));
        Assert.Equal("shed", ViewportTools.UniqueName(editor.Document, "shed"));
    }

    // The gizmo is drawn where the ray a click makes would find it: a point and its pixel agree both ways.
    [Fact]
    public void AViewportCameraProjectsAPointToThePixelWhoseRayPassesThroughIt()
    {
        var view = new CameraView
        {
            Position = new Vector3(1, 2, 3),
            Rotation = Quaternion.CreateFromYawPitchRoll(0.4f, -0.3f, 0f),
            Projection = CameraProjection.Perspective,
            FovY = 1.0f,
            Near = 0.1f,
            Far = 500f,
        };
        var camera = new ViewportCamera(view, new Vector2(1280, 720));
        var pixel = new Vector2(300, 500);
        var ray = camera.RayThrough(pixel)!.Value;
        var onScreen = camera.ToScreen(ray.At(25f))!.Value;
        Assert.Equal(pixel.X, onScreen.X, 2);
        Assert.Equal(pixel.Y, onScreen.Y, 2);

        Assert.Null(camera.ToScreen(view.Position - CameraMath.Forward(view.Rotation)));   // behind the camera
        Assert.True(camera.SizeAt(ray.At(20f), 100f) > camera.SizeAt(ray.At(10f), 100f));
    }

    // A click picks the nearest thing the viewport accepts: not the editor's own camera, whose entity has
    // no collider and stands at the origin (it is driven by its CameraPose), where the gate is too.
    [Fact]
    public void AClickPicksWhatTheViewportAcceptsAndSelectsItsPlacement()
    {
        using var editor = Open();
        var (document, selection) = (editor.Document, editor.Selection);
        var world = document.World;
        var eye = DebugCamera.Spawn(world, "editor free camera");
        for (int i = 0; i < 2; i++) world.RunFixed(1f / 60f);   // transforms propagate

        var down = new EditorRay(new Vector3(0, 5, 0), -Vector3.UnitY);
        Assert.Equal(eye, EditorPicking.PickWhere(world, down, e => e == eye)!.Value.Entity);
        var hit = EditorPicking.PickWhere(world, down, e => e != eye)!.Value;
        selection.SelectPlaced(hit.Entity);
        Assert.Same(document.Find("gate"), selection.Placement);
        Assert.Null(EditorPicking.PickWhere(world, new EditorRay(new Vector3(50, 5, 50), -Vector3.UnitY), e => e != eye));
    }
}
