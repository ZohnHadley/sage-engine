#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Several placements at once, a full rotation and a scale (issue #367): a selection of many (Ctrl+click,
// a box, ed_select_all), the scale gizmo, three rotate rings, local space, and group move, turn, scale,
// delete and duplicate — each one undo step. And the placement's new fields: `pitch`, `roll` and
// `scale`, which a yaw-only file never writes and still loads without.
public class MultiSelectTests
{
    public MultiSelectTests() { _ = TestEnv.UserRoot; }

    // Ten crates in a row along X, a metre apart, and a post.
    private static string Records(string extra = "")
    {
        var crates = string.Join(",\n", Enumerable.Range(0, 10).Select(i => $$"""{ "prefab": "crate", "at": [{{i}}, 0, 0], "name": "crate{{i}}", "yaw": {{(i == 3 ? 30 : 0)}} }"""));
        return $$"""
            [
              { "type": "prefab", "id": "crate", "components": { "transform": {} } },
              { "type": "prefab", "id": "post", "components": { "transform": {} } },
              { "type": "prefab", "id": "big", "components": { "transform": { "localScale": [2, 2, 2] } } },
              {
                "type": "placements",
                "id": "yard",
                "place": [
                  {{crates}},
                  { "prefab": "post", "at": [0, 0, 5], "name": "post" }{{extra}}
                ]
              }
            ]
            """;
    }

    private sealed record Editor(Engine Engine, MountFixture Fixture, EditDocument Document, EditorSelection Selection, ViewportTools Tools)
        : IDisposable
    {
        public CVarRegistry Console => Engine.CVars;
        public void Dispose() => Engine.Dispose();
    }

    private static Editor Open(string records)
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", records);
        fixture.Mount("game", "sandbox");
        var engine = HeadlessApp.Bare().Mount(fixture).Build().Engine;

        var document = new EditDocument(engine.CreateWorld("edit"));
        var selection = new EditorSelection(document);
        EditorCommands.Register(engine.CVars, () => document);
        var tools = ViewportTools.Register(engine.CVars, () => selection);
        Assert.True(engine.CVars.Execute("doc_open yard"));
        return new Editor(engine, fixture, document, selection, tools);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static void SameRotation(Quaternion expected, Quaternion actual) =>
        Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual))) - 1f) < 1e-4f,
                    $"expected {expected}, got {actual}");

    private static EditorRay Ray(Vector3 from, Vector3 to) => new(from, Vector3.Normalize(to - from));

    private static Transform TransformOf(EditDocument document, Placement placement) =>
        document.EntityOf(placement).GetComponent<Transform>();

    // The issue's "done": select ten crates, rotate and scale them together, and one undo a step puts all
    // of them back.
    [Fact]
    public void TenCratesTurnAndScaleTogetherAndUndoPutsEveryOneBack()
    {
        using var editor = Open(Records());
        var (document, selection, console) = (editor.Document, editor.Selection, editor.Console);
        var crates = document.Placements.Where(p => p.Prefab.Id.Name == "crate").ToList();
        var before = crates.Select(PlacementFields.Of).ToList();

        console.Execute("ed_select_all crate");
        Assert.Equal(10, selection.Placements.Count);
        Assert.Same(crates[9], selection.Placement);   // the last picked has the gizmo: the pivot
        Assert.True(crates.All(c => selection.Is(document.EntityOf(c))));
        Assert.False(selection.Is(document.EntityOf(document.Find("post")!)));

        // A quarter turn about Y, round crate9 at (9, 0, 0).
        console.Execute("ed_turn y 90");
        var pivot = new Vector3(9, 0, 0);
        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        for (int i = 0; i < 10; i++)
        {
            Near(pivot + Vector3.Transform(before[i].At - pivot, quarter), crates[i].At);
            Assert.Equal(ViewportTools.WrapDegrees(before[i].Yaw + 90f), crates[i].Yaw, 3);   // a yaw stays a yaw
            Assert.Equal(0f, crates[i].Pitch);
            SameRotation(SageMath.RotationFromYaw(crates[i].Yaw * MathF.PI / 180f), TransformOf(document, crates[i]).LocalRotation);
        }
        Assert.Equal("Rotate 10 placements", document.History.Entries[^1].Description);

        // Tipped 30° about the world's X: a pitch and roll a yaw alone could not say.
        console.Execute("ed_turn x 30");
        var tip = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 6f);
        foreach (var crate in crates)
        {
            Assert.True(crate.Pitch != 0f || crate.Roll != 0f);
            SameRotation(crate.PlacementRotation(), TransformOf(document, crate).LocalRotation);
        }
        SameRotation(Quaternion.Concatenate(SageMath.RotationFromYaw(MathF.PI / 2f), tip), crates[0].PlacementRotation());

        // Twice the size, round the pivot: the crates spread out as they grow.
        var turned = crates.Select(c => c.At).ToList();
        console.Execute("ed_scale 2");
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(new Vector3(2), crates[i].Scale);
            Near(pivot + (turned[i] - pivot) * 2f, crates[i].At);
            Near(new Vector3(2), TransformOf(document, crates[i]).LocalScale);
        }
        Assert.Equal("Scale 10 placements", document.History.Entries[^1].Description);
        Assert.Equal(3, document.History.Entries.Count);

        // Three steps, three undos, and every crate is where it was, as it was.
        Assert.True(document.Undo());
        Assert.True(document.Undo());
        Assert.True(document.Undo());
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(before[i], PlacementFields.Of(crates[i]));
            Assert.Equal(Vector3.Zero, TransformOf(document, crates[i]).LocalScale);   // as the prefab wrote it
            SameRotation(SageMath.RotationFromYaw(before[i].Yaw * MathF.PI / 180f), TransformOf(document, crates[i]).LocalRotation);
        }
        Assert.Equal(10, selection.Placements.Count);   // the selection follows its placements through it all

        console.Execute("ed_nudge 0 1 0");
        Assert.True(crates.All(c => c.At.Y == 1f));
        Assert.Equal("Move 10 placements", document.History.Entries[^1].Description);
        document.Undo();
        Assert.True(crates.All(c => c.At.Y == 0f));
    }

    // A drag of a group's gizmo is one SetPlacements, merged frame by frame: a ring about X, then the
    // scale gizmo's X handle, each one undo step.
    [Fact]
    public void AGroupDragOfTheRingsAndTheScaleGizmoIsOneUndoStepEach()
    {
        using var editor = Open(Records());
        var (document, selection) = (editor.Document, editor.Selection);
        var (a, b, c) = (document.Find("crate0")!, document.Find("crate2")!, document.Find("post")!);
        // The post last, so the gizmo stands on it at (0, 0, 5).
        selection.Select(a);
        selection.Add(b);
        selection.Add(c);
        Assert.Same(c, selection.Placement);
        var origin = new Vector3(0, 0, 5);
        var eye = origin + new Vector3(10f, 0.5f, 0.3f);

        // From the X ring at 45° (between +Y and +Z) to 90° (+Z): an eighth of a turn about X.
        var on = origin + new Vector3(0, 1.4142f, 1.4142f);
        Assert.Equal(GizmoHandle.X, RotateGizmo.HitTest(Ray(eye, on), origin, 2f, Quaternion.Identity));
        var drag = GizmoDrag.Begin(document, selection.Placements, GizmoMode.Rotate, GizmoHandle.X, Ray(eye, on))!;
        Assert.Equal(origin, drag.Origin);
        Assert.True(drag.Update(Ray(eye, origin + new Vector3(0, 0.5f, 1.9f)), 0f, 0f));
        Assert.True(drag.Update(Ray(eye, origin + new Vector3(0, 0, 2f)), 0f, 0f));
        drag.End();
        Assert.Single(document.History.Entries);
        Assert.IsType<SetPlacements>(document.History.Entries[0]);
        Assert.Equal(45f, a.Pitch, 2);
        Assert.Equal(0f, a.Yaw, 2);
        var eighth = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4f);
        Near(origin + Vector3.Transform(new Vector3(0, 0, -5), eighth), a.At);
        Near(origin + Vector3.Transform(new Vector3(2, 0, -5), eighth), b.At);
        Near(origin, c.At);

        // The scale gizmo is the post's own axes; its X is the world's (it turned about X).
        var above = origin + new Vector3(0, 6, 6);
        var axes = ViewportTools.AxesOf(c, GizmoSpace.Local);
        Assert.Equal(GizmoHandle.X, ScaleGizmo.HitTest(Ray(above, origin + Vector3.Transform(new Vector3(1.6f, 0, 0), axes)), origin, 2f, axes));
        Assert.Equal(GizmoHandle.All, ScaleGizmo.HitTest(Ray(above, origin), origin, 2f, axes));
        var bAt = b.At;
        var grow = GizmoDrag.Begin(document, selection.Placements, GizmoMode.Scale, GizmoHandle.X, Ray(above, origin + Vector3.Transform(Vector3.UnitX, axes)))!;
        Assert.Equal(GizmoSpace.Local, grow.Space);
        Assert.True(grow.Update(Ray(above, origin + Vector3.Transform(new Vector3(1.5f, 0, 0), axes)), 0f, 0f, 0f));
        Assert.True(grow.Update(Ray(above, origin + Vector3.Transform(new Vector3(2.04f, 0, 0), axes)), 0f, 0f, 0.1f));   // snapped to 2.0
        grow.End();
        Assert.Equal(2, document.History.Entries.Count);
        Assert.Equal(new Vector3(2f, 1f, 1f), c.Scale);
        Assert.Equal(2f, b.Scale.X, 4);
        Near(bAt + new Vector3(2, 0, 0), b.At);   // 2 m along X from the pivot becomes 4

        document.Undo();
        document.Undo();
        Assert.Equal(new Vector3(0, 0, 0), a.At);
        Assert.Equal(new Vector3(2, 0, 0), b.At);
        Assert.Equal(Vector3.One, b.Scale);
        Assert.Equal(0f, a.Pitch);
    }

    [Fact]
    public void SelectingSeveralTogglesAddsAndDropsWhatLeavesTheDocument()
    {
        using var editor = Open(Records());
        var (document, selection, console) = (editor.Document, editor.Selection, editor.Console);
        var (one, two, three) = (document.Find("crate1")!, document.Find("crate2")!, document.Find("crate3")!);

        console.Execute("ed_select crate1");
        console.Execute("ed_select_add crate2");
        console.Execute("ed_select_add crate3");
        Assert.Equal(new[] { one, two, three }, selection.Placements);
        console.Execute("ed_select_add crate2");   // in it: taken out
        Assert.Equal(new[] { one, three }, selection.Placements);
        selection.Add(one);                        // already in: moves to the end, the gizmo's
        Assert.Same(one, selection.Placement);

        // A Ctrl+click toggles what was clicked; a plain click selects it alone.
        selection.SelectPlaced(document.EntityOf(two), toggle: true);
        Assert.Equal(3, selection.Placements.Count);
        selection.SelectPlaced(document.EntityOf(two), toggle: true);
        Assert.Equal(2, selection.Placements.Count);
        selection.SelectPlaced(document.EntityOf(two));
        Assert.Equal(new[] { two }, selection.Placements);

        // Delete the group: one undo step, and the selection lets go of what left.
        selection.Select(new[] { one, two, three });
        int count = document.Placements.Count;
        console.Execute("ed_delete");
        Assert.Equal(count - 3, document.Placements.Count);
        Assert.True(selection.IsEmpty);
        Assert.Single(document.History.Entries);
        document.Undo();
        Assert.Equal(count, document.Placements.Count);
        Assert.Equal(new[] { 1, 2, 3 }, new[] { one, two, three }.Select(document.IndexOf));   // back where they were

        // Duplicate the group: each copy after its original, named and identified on its own, selected.
        selection.Select(new[] { three, one });
        console.Execute("ed_duplicate");
        Assert.Equal(count + 2, document.Placements.Count);
        Assert.Equal(new[] { "crate3_2", "crate1_2" }, selection.Placements.Select(p => p.Name));
        Assert.Same(document.Placements[document.IndexOf(one) + 1], selection.Placements[1]);
        Assert.Same(document.Placements[document.IndexOf(three) + 1], selection.Placements[0]);
        Assert.Equal(document.Placements.Count, document.Placements.Select(p => p.Id).Where(id => id.Length > 0).Distinct().Count()
                     + document.Placements.Count(p => p.Id.Length == 0));
        Assert.Single(document.History.Entries);   // the undone delete was dropped: a new branch
        Assert.Equal("Duplicate 2 placements", document.History.Entries[0].Description);
        document.Undo();
        Assert.Equal(count, document.Placements.Count);
        Assert.True(selection.IsEmpty);
    }

    // A box drawn on the picture selects the placements drawn inside it.
    [Fact]
    public void ABoxSelectsWhatTheCameraDrawsInsideIt()
    {
        using var editor = Open(Records());
        var document = editor.Document;
        var view = new CameraView
        {
            Position = new Vector3(4.5f, 20, 0),
            Rotation = Quaternion.CreateFromYawPitchRoll(0f, -MathF.PI / 2f, 0f),   // straight down
            Projection = CameraProjection.Perspective,
            FovY = 1.0f,
            Near = 0.1f,
            Far = 500f,
        };
        var camera = new ViewportCamera(view, new Vector2(1280, 720));
        var first = camera.ToScreen(new Vector3(0, 0, 0))!.Value;
        var third = camera.ToScreen(new Vector3(2, 0, 0))!.Value;
        var inBox = EditorPicking.PlacementsInBox(document, camera, third + new Vector2(5, 5), first - new Vector2(5, 5));
        Assert.Equal(new[] { "crate0", "crate1", "crate2" }, inBox.Select(p => p.Name));

        editor.Selection.Select(inBox);
        Assert.Equal(3, editor.Selection.Placements.Count);
        Assert.Empty(EditorPicking.PlacementsInBox(document, camera, Vector2.Zero, new Vector2(4, 4)));
    }

    // Local space: the move gizmo's axes are the placement's own, and so a drag along its X goes where it faces.
    [Fact]
    public void LocalSpaceTurnsTheMoveGizmoWithThePlacement()
    {
        using var editor = Open(Records());
        var (document, tools, console) = (editor.Document, editor.Tools, editor.Console);
        var crate = document.Find("crate3")!;   // yaw 30
        console.Execute("ed_space local");
        Assert.Equal(GizmoSpace.Local, tools.Space);
        var axes = ViewportTools.AxesOf(crate, tools.Space);
        var localX = Vector3.Transform(Vector3.UnitX, axes);
        Assert.True(MathF.Abs(localX.Z) > 0.4f);   // turned off the world's X

        static EditorRay Down(Vector3 at) => new(at + new Vector3(0, 10, 0), -Vector3.UnitY);
        var origin = crate.At;
        Assert.Equal(GizmoHandle.X, TranslateGizmo.HitTest(Down(origin + localX * 1.5f), origin, 2f, axes));
        var drag = GizmoDrag.Begin(document, new[] { crate }, GizmoMode.Move, GizmoHandle.X, Down(origin + localX), tools.Space)!;
        Assert.True(drag.Update(Down(origin + localX * 3.1f + new Vector3(0, 0, 0)), grid: 0.5f));   // 2.1 along it snaps to 2
        drag.End();
        Near(origin + localX * 2f, crate.At);

        console.Execute("ed_gizmo scale");
        Assert.Equal(GizmoMode.Scale, tools.Mode);
        console.Execute("ed_space world");
        Assert.Equal(Quaternion.Identity, ViewportTools.AxesOf(crate, tools.Space));
    }

    // Yaw, pitch and roll come back out of a rotation as they went in.
    [Fact]
    public void AFullRotationComesBackAsTheYawPitchAndRollThatMadeIt()
    {
        var random = new Random(367);
        for (int i = 0; i < 200; i++)
        {
            float yaw = (float)(random.NextDouble() * 2 - 1) * 3.1f, pitch = (float)(random.NextDouble() * 2 - 1) * 1.5f;
            float roll = (float)(random.NextDouble() * 2 - 1) * 3.1f;
            var angles = SageMath.YawPitchRollOf(Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll));
            Near(new Vector3(yaw, pitch, roll), angles, 2e-3f);
        }
        // Straight up, yaw and roll are one turn: whichever it is called, the rotation is the same.
        var up = Quaternion.CreateFromYawPitchRoll(0.5f, MathF.PI / 2f, 0.2f);
        var back = SageMath.YawPitchRollOf(up);
        SameRotation(up, Quaternion.CreateFromYawPitchRoll(back.X, back.Y, back.Z));
        Assert.Equal(Vector3.Zero, SageMath.YawPitchRollOf(Quaternion.Identity));
    }

    // Old files keep loading and are written as they were; the new fields spawn, save and load.
    [Fact]
    public void AYawOnlyFileLoadsAsBeforeAndPitchRollAndScaleSaveAndLoad()
    {
        string records = Records(""" , { "prefab": "post", "at": [3, 0, 5], "name": "leaning", "yaw": 10, "pitch": 15, "roll": -20, "scale": [1, 2.5, 1] }, { "prefab": "big", "at": [6, 0, 5], "name": "big", "scale": [1.5, 1, 1] }""");
        using var editor = Open(records);
        var document = editor.Document;
        Assert.Equal(0, editor.Engine.Records.ErrorCount);

        // A yaw and nothing else: no pitch, no roll, a scale of 1, and the rotation a yaw always made.
        var crate = document.Find("crate3")!;
        Assert.Equal((0f, 0f, Vector3.One), (crate.Pitch, crate.Roll, crate.Scale));
        var transform = TransformOf(document, crate);
        SameRotation(SageMath.RotationFromYaw(30f * MathF.PI / 180f), transform.LocalRotation);
        Assert.Equal(Vector3.Zero, transform.LocalScale);   // the prefab's empty transform, untouched: 0 reads as 1

        // The leaning post: its whole rotation and its scale on its entity, and read back from the world.
        var leaning = document.Find("leaning")!;
        var lean = TransformOf(document, leaning);
        SameRotation(Quaternion.CreateFromYawPitchRoll(10f * MathF.PI / 180f, 15f * MathF.PI / 180f, -20f * MathF.PI / 180f), lean.LocalRotation);
        Assert.Equal(new Vector3(1, 2.5f, 1), lean.LocalScale);
        // A prefab's own scale is multiplied by the placement's.
        Near(new Vector3(3, 2, 2), TransformOf(document, document.Find("big")!).LocalScale);
        var read = document.World.ReadPlacements(new RecordId("sandbox", "yard")).Place.Single(p => p.Name == "leaning");
        Assert.Equal((10f, 15f, -20f), (MathF.Round(read.Yaw, 3), MathF.Round(read.Pitch, 3), MathF.Round(read.Roll, 3)));
        Assert.Equal(new Vector3(1, 2.5f, 1), read.Scale);

        // Saving a document whose yaw-only placements did not change writes none of the new fields for them.
        editor.Console.Execute("ed_select crate5");
        editor.Console.Execute("ed_scale 1 3 1");
        Assert.True(document.Save());
        string text = File.ReadAllText(document.Path);
        Assert.Equal(3, CountOf(text, "\"scale\""));    // the leaning post's and the big one's, as written, and crate5's
        Assert.Equal(1, CountOf(text, "\"pitch\""));

        // And the game loads what was saved.
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", text);
        fixture.Mount("game", "sandbox");
        using var second = HeadlessApp.Bare().Mount(fixture).Build().Engine;
        Assert.Equal(0, second.Records.ErrorCount);
        Assert.True(second.Records.TryGet(new RecordId("sandbox", "yard"), out PlacementsRecord record));
        Assert.Equal(new Vector3(1, 3, 1), record.Place.Single(p => p.Name == "crate5").Scale);
        Assert.Equal(Vector3.One, record.Place.Single(p => p.Name == "crate4").Scale);
        Assert.Equal(15f, record.Place.Single(p => p.Name == "leaning").Pitch);
    }

    // A scale of 0 (or below) on any axis is a content error, and the inspector refuses one.
    [Fact]
    public void AScaleOfZeroIsAContentErrorAndTheInspectorRefusesIt()
    {
        var fixture = new MountFixture();
        fixture.Write("game", "data/yard.json", Records(""" , { "prefab": "post", "at": [3, 0, 5], "name": "flat", "scale": [1, 0, 1] }"""));
        fixture.Mount("game", "sandbox");
        using (var engine = HeadlessApp.Bare().Mount(fixture).Build().Engine)
            Assert.True(engine.Records.ErrorCount > 0);

        using var editor = Open(Records());
        var crate = editor.Document.Find("crate1")!;
        var model = InspectorModel.Of(editor.Document, editor.Document.World, editor.Document.EntityOf(crate));
        Assert.False(model.TrySet("scale", "1 0 1", out string error));
        Assert.Contains("above 0", error);
        Assert.True(model.TrySet("scale", "1 2 1", out _));
        model = InspectorModel.Of(editor.Document, editor.Document.World, editor.Document.EntityOf(crate));
        Assert.True(model.TrySet("pitch", "12", out _));
        Assert.Equal(new Vector3(1, 2, 1), crate.Scale);
        Assert.Equal(12f, crate.Pitch);
    }

    private static int CountOf(string text, string what)
    {
        int count = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal)) count++;
        return count;
    }
}
