#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Blockout brushes (issue #61): boxes, wedges and cylinders built in the editor, snapped to the grid, with
// a material per face, each a convex hull and a mesh's worth of polygons, saved in the level's placements
// document, reloaded by the game, checked by `sage validate` and patched by a mod like any placement.
public class BlockoutTests
{
    public BlockoutTests() { _ = TestEnv.UserRoot; }

    private static string EditorGame() => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "editor");

    private static string CopyOf(string from)
    {
        string to = Path.Combine(TestEnv.NewTempDir(), Path.GetFileName(from));
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return to;
    }

    // The editor on a game folder, headless, with the console commands DevTools registers.
    private sealed class Editor : IDisposable
    {
        public readonly HeadlessApp App;
        public readonly World World;
        public readonly EditDocument Document;
        public readonly EditorSelection Selection;

        public Editor(string game, string edit = "level")
        {
            App = HeadlessApp.ForGame(game).WithEngineContent().Build();
            var engine = App.Engine;
            Assert.True(EditTarget.TryResolve(engine.Records, engine.Scenes.Start, edit, out var target, out string error), error);
            World = App.App.CreateEditWorld("edit", target.Scene);
            Document = new EditDocument(World);
            Selection = new EditorSelection(Document);
            EditorCommands.Register(App.CVars, () => Document);
            InspectorCommands.Register(App.CVars, () => Document);
            ViewportTools.Register(App.CVars, () => Selection);   // and the ed_brush* commands with it
        }

        public void Press(string line) => Assert.True(App.CVars.Execute(line), line);
        public Placement Placed(string name) => Document.Find(name) ?? throw new Xunit.Sdk.XunitException($"no placement '{name}'");
        public BlockoutBrush Brush(string name) => World.Get<BlockoutBrush>(Document.EntityOf(Placed(name)));
        public void Dispose() => App.Dispose();
    }

    private static readonly RecordId Lit = new("sage", "lit_default"), Unlit = new("sage", "unlit_default");

    // ---- The geometry ------------------------------------------------------------------------------------

    private static BlockoutGeometry.Built Build(BrushShape shape, Vector3 size, int sides = 12, Quaternion? rotation = null) =>
        BlockoutGeometry.Build(shape, size, sides, new BrushPart { Shape = shape, Material = Lit, Top = Unlit }, rotation ?? Quaternion.Identity, Vector3.Zero, 1f);

    [Theory]
    [InlineData(BrushShape.Box, 6, 8)]
    [InlineData(BrushShape.Wedge, 5, 6)]
    [InlineData(BrushShape.Cylinder, 14, 24)]
    public void EachShapeIsAClosedConvexSolidWithItsFacesFacingOut(BrushShape shape, int faces, int corners)
    {
        var built = Build(shape, new Vector3(2, 3, 4));
        Assert.Equal(faces, built.Brush.Faces.Length);
        Assert.Equal(corners, built.Hull.Length);
        if (shape != BrushShape.Cylinder) Assert.Equal(new Vector3(-1, 0, -2), built.Brush.Min);   // its floor's middle is its origin
        Assert.Equal(0f, built.Brush.Min.Y);
        Assert.Equal(3f, built.Brush.Max.Y, 4);

        foreach (var face in built.Brush.Faces)
        {
            var p = face.Positions;
            // Wound with its normal (BrushGeometry's convention, which the renderer flips once).
            Assert.True(Vector3.Dot(Vector3.Cross(p[1] - p[0], p[2] - p[0]), face.Normal) > 0f);
            // Convex: every corner of the solid is on or behind every face's plane.
            foreach (var corner in built.Hull)
                Assert.True(Vector3.Dot(face.Normal, corner - p[0]) <= 1e-4f, $"{shape}: a corner in front of a face");
        }

        // Each face is named, and its material is its own or the brush's.
        Assert.Equal(BrushPart.FacesOf(shape).ToHashSet(), built.Faces.ToHashSet());
        for (int i = 0; i < built.Faces.Length; i++)
            Assert.Equal(built.Faces[i] == BrushFace.Top ? Unlit.ToString() : Lit.ToString(), built.Brush.Faces[i].Texture);
    }

    // A wedge is a ramp rising to its north; turning a brush turns its hull (statics have no rotation) and
    // not its polygons (its entity's transform turns those); texture coordinates are the world's, in metres.
    [Fact]
    public void AWedgeRisesNorthAndATurnedBrushTurnsItsHull()
    {
        var wedge = Build(BrushShape.Wedge, new Vector3(2, 1, 2));
        var slope = wedge.Brush.Faces[Array.IndexOf(wedge.Faces, BrushFace.Top)];
        Assert.True(slope.Normal.Y > 0f && slope.Normal.Z > 0f, $"the slope faces up and south: {slope.Normal}");
        Assert.Contains(wedge.Hull, p => p == new Vector3(1, 1, -1));
        Assert.DoesNotContain(wedge.Hull, p => p.Y > 0.5f && p.Z > 0f);

        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        var turned = Build(BrushShape.Box, new Vector3(4, 1, 2), rotation: quarter);
        Assert.Equal(new Vector3(-2, 0, -1), turned.Brush.Min);                       // polygons unturned
        Assert.All(turned.Hull, p => Assert.True(MathF.Abs(p.X) <= 1.0001f && MathF.Abs(p.Z) <= 2.0001f));   // hull turned

        var box = Build(BrushShape.Box, new Vector3(2, 2, 2));
        var top = box.Brush.Faces[Array.IndexOf(box.Faces, BrushFace.Top)];
        for (int i = 0; i < top.Positions.Length; i++)
            Assert.Equal(new Vector2(top.Positions[i].X, top.Positions[i].Z), top.Uvs[i]);
    }

    // ---- The editor ----------------------------------------------------------------------------------

    // A room blocked out at the console: a floor, four walls, a ramp and a pillar, snapped to the grid, a
    // face painted; saved, and the game that loads the saved file has every brush with its hull and its
    // polygons, where the editor put them.
    [Fact]
    public void ARoomOfBrushesIsBuiltSavedAndReloadedWithItsCollidersAndMeshes()
    {
        string game = CopyOf(EditorGame());
        string file = Path.Combine(game, "content", "data", "level_placements.json");
        using (var editor = new Editor(game))
        {
            editor.Press("doc_open level_placements");
            editor.Press("ed_grid 0.25");
            editor.Press("ed_brush box 0 -0.25 0 8 0.25 8 sage:lit_default floor");
            editor.Press("ed_brush box 0 0 -4 8 3 0.5 sage:lit_default north_wall");
            editor.Press("ed_brush box 0 0 4 8 3 0.5 sage:lit_default south_wall");
            editor.Press("ed_brush box 4 0 0 0.5 3 8 sage:lit_default east_wall");
            editor.Press("ed_brush box -4 0 0 0.5 3 8 sage:lit_default west_wall");
            editor.Press("ed_brush wedge 2 0 -2 2 1 2 sage:lit_default ramp");
            editor.Press("ed_brush cylinder -2.1 0 2.05 1.1 3 1 sage:lit_default pillar");
            editor.Press("ed_brush_material north_wall south sage:unlit_default");
            editor.Press("ed_brush_shape pillar cylinder 8");

            // Snapped: the pillar stands on the grid and is a whole number of steps wide.
            Assert.Equal(new Vector3(-2, 0, 2), editor.Placed("pillar").At);
            Assert.Equal(new Vector3(1, 3, 1), editor.Brush("pillar").Size);
            Assert.Equal(10, editor.Brush("pillar").FaceCount);

            Assert.Equal(7, BlockoutTools.Brushes(editor.Document).Count());
            foreach (var placement in BlockoutTools.Brushes(editor.Document))
                Assert.True(editor.World.Get<PhysicsBody>(editor.Document.EntityOf(placement)).IsStatic);
            // The hull is there to click: a ray from the middle of the room north meets the wall's inside.
            var physics = editor.World.Resources.Get<IPhysicsWorld>();
            var hit = physics.Raycast(new Vector3(0, 1.5f, 0), -Vector3.UnitZ, 10f);
            Assert.True(hit.Hit);
            Assert.Equal(-3.75f, hit.Position.Z, 2);
            Assert.Equal((editor.Placed("north_wall"), BrushFace.South), BlockoutTools.FaceAt(editor.Document, new EditorRay(new Vector3(0, 1.5f, 0), -Vector3.UnitZ)));

            editor.Press("doc_save");
            Assert.False(editor.Document.Dirty);
        }

        string saved = File.ReadAllText(file);
        Assert.Contains("\"sage:wedge\"", saved);
        Assert.Contains("\"south\": \"sage:unlit_default\"", saved);

        using var app = HeadlessApp.ForGame(game).WithEngineContent().StartScene("editor:level").Boot();
        var world = app.World;
        var brushes = world.Query<BlockoutBrush>().Entities.ToEntityList();
        Assert.Equal(7, brushes.Count);
        foreach (var brush in brushes)
        {
            Assert.True(world.Get<PhysicsBody>(brush).IsStatic, $"{brush.Name} has no hull");
            Assert.True(world.Get<BlockoutBrush>(brush).FaceCount >= 5, $"{brush.Name} has no polygons");
        }
        var wall = world.FindByName("north_wall");
        Assert.Equal(new Vector3(0, 0, -4), world.Get<Transform>(wall).LocalPosition);
        Assert.Equal(Unlit, world.Get<BlockoutBrush>(wall).MaterialOf(BrushFace.South));
        Assert.Equal(Lit, world.Get<BlockoutBrush>(wall).MaterialOf(BrushFace.North));
        Assert.Equal(8, world.Get<BlockoutBrush>(world.FindByName("pillar")).Sides);

        // And it is solid in the game: a ray down onto the floor stops at its top.
        var down = world.Resources.Get<IPhysicsWorld>().Raycast(new Vector3(0, 2, 0), -Vector3.UnitY, 10f);
        Assert.True(down.Hit);
        Assert.Equal(0f, down.Position.Y, 2);
    }

    // Every brush edit is one step of the document's history, and undo puts the brush back as it was.
    [Fact]
    public void BrushEditsAreUndoneAndRedone()
    {
        using var editor = new Editor(CopyOf(EditorGame()));
        editor.Press("doc_open level_placements");
        editor.Press("ed_brush box 0 0 0 2 2 2 sage:lit_default block");
        editor.Press("ed_brush_size block 4 1 2");
        editor.Press("ed_brush_material block top sage:unlit_default");
        editor.Press("ed_brush_shape block wedge");
        Assert.Equal(4, editor.Document.History.Position);
        Assert.Equal(BrushShape.Wedge, editor.Brush("block").Shape);

        editor.Press("ed_undo");
        Assert.Equal(BrushShape.Box, editor.Brush("block").Shape);
        editor.Press("ed_undo");
        Assert.Equal(Lit, editor.Brush("block").MaterialOf(BrushFace.Top));
        editor.Press("ed_undo");
        Assert.Equal(new Vector3(2, 2, 2), editor.Brush("block").Size);
        editor.Press("ed_undo");
        Assert.Null(editor.Document.Find("block"));
        Assert.Empty(editor.World.Query<BlockoutBrush>().Entities.ToEntityList());

        editor.Press("ed_redo 4");
        Assert.Equal(BrushShape.Wedge, editor.Brush("block").Shape);
        Assert.Equal(new Vector3(4, 1, 2), editor.Brush("block").Size);
        Assert.Equal(Unlit, editor.Brush("block").MaterialOf(BrushFace.Top));
        Assert.Single(editor.World.Query<BlockoutBrush>().Entities.ToEntityList());

        // The scale gizmo resizes a brush: its scale multiplies its size, and undoing it puts the size back.
        ViewportTools.ScaleBy(editor.Document, new[] { editor.Placed("block") }, new Vector3(2, 1, 1), Quaternion.Identity);
        Assert.Equal(new Vector3(8, 1, 2), editor.Brush("block").Size);
        editor.Press("ed_undo");
        Assert.Equal(new Vector3(4, 1, 2), editor.Brush("block").Size);
    }

    // A material in hand and a click: the face under the pointer takes it, or with `whole` the brush.
    // A face the shape does not have is refused, and so is taking a face of the wrong brush.
    [Fact]
    public void PaintingAFaceGivesTheFaceUnderTheClickItsMaterial()
    {
        using var editor = new Editor(CopyOf(EditorGame()));
        editor.Press("doc_open level_placements");
        editor.Press("ed_brush box 0 0 0 2 2 2 sage:lit_default block");
        var document = editor.Document;
        var fromSouth = new EditorRay(new Vector3(0, 1, 5), -Vector3.UnitZ);

        Assert.Equal((editor.Placed("block"), BrushFace.South), BlockoutTools.PaintAt(document, fromSouth, Unlit));
        Assert.Equal(Unlit, editor.Brush("block").MaterialOf(BrushFace.South));
        Assert.Equal(Lit, editor.Brush("block").MaterialOf(BrushFace.North));

        var fromAbove = new EditorRay(new Vector3(0.5f, 10, 0.5f), -Vector3.UnitY);
        BlockoutTools.PaintAt(document, fromAbove, Unlit, whole: true);
        Assert.Equal(Unlit, editor.Brush("block").MaterialOf(BrushFace.North));
        Assert.Null(BlockoutTools.FaceAt(document, new EditorRay(new Vector3(0, 10, 0), Vector3.UnitY)));

        Assert.False(BlockoutTools.SetMaterial(document, editor.Placed("block"), BrushFace.Side, Lit));   // a box has no "side"
        editor.Press("ed_brush_material block south none");                                            // back to the brush's
        Assert.Equal(Unlit, editor.Brush("block").MaterialOf(BrushFace.South));
        Assert.True(editor.Brush("block").FaceMaterial(BrushFace.South).IsEmpty);
    }

    // ---- Content -------------------------------------------------------------------------------------

    private const string GameJson = """
        { "name": "Blockout", "id": "blk", "mounts": ["content"], "scene": "room", "plugins": [] }
        """;

    private static string GameWith(string placements)
    {
        string game = Path.Combine(TestEnv.NewTempDir(), "blk");
        Directory.CreateDirectory(Path.Combine(game, "content", "data"));
        File.WriteAllText(Path.Combine(game, "game.json"), GameJson);
        File.WriteAllText(Path.Combine(game, "content", "data", "room.json"), """
            [ { "type": "scene", "id": "room", "placements": ["room_placements"] } ]
            """);
        File.WriteAllText(Path.Combine(game, "content", "data", "room_placements.json"), placements);
        return game;
    }

    private static ValidationReport Validate(string game) => ContentValidation.Run(new ValidateOptions
    {
        GameDirectory = game,
        EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
        AvailablePlugins = BasePlugins.All(),
    });

    // `sage validate` checks a level's brushes: a material that is not one, a face the shape has not got,
    // a size of nothing; a level of good brushes is clean.
    [Fact]
    public void ALevelsBrushesAreCheckedWhenContentLoads()
    {
        var good = Validate(GameWith("""
            [ { "type": "placements", "id": "room_placements", "place": [
                { "prefab": "sage:brush", "at": [0, 0, 0], "overrides": { "parts": { "brush": { "size": [4, 0.5, 4], "material": "sage:lit_default", "top": "sage:unlit_default" } } } },
                { "prefab": "sage:wedge", "at": [0, 0.5, 0] },
                { "prefab": "sage:cylinder", "at": [2, 0, 2], "overrides": { "parts": { "brush": { "sides": 6, "side": "sage:lit_default" } } } } ] } ]
            """));
        Assert.True(good.Ok, string.Join("\n", good.Errors));
        Assert.DoesNotContain(good.Warnings, w => w.Contains("room_placements"));

        var bad = Validate(GameWith("""
            [ { "type": "placements", "id": "room_placements", "place": [
                { "prefab": "sage:brush", "at": [0, 0, 0], "overrides": { "parts": { "brush": { "material": "blk:no_such_stone" } } } },
                { "prefab": "sage:brush", "at": [0, 0, 0], "overrides": { "parts": { "brush": { "shape": "Wedge", "south": "sage:lit_default" } } } },
                { "prefab": "sage:brush", "at": [0, 0, 0], "overrides": { "parts": { "brush": { "size": [4, 0, 4] } } } } ] } ]
            """));
        Assert.False(bad.Ok);
        Assert.Contains(bad.Errors, e => e.Contains("no_such_stone") && e.Contains("doesn't exist"));
        Assert.Contains(bad.Errors, e => e.Contains("a wedge has no south face"));
        Assert.Contains(bad.Errors, e => e.Contains("a brush's size must be above 0"));
    }

    // A mod adds brushes to a level the way it adds any placement (`place+`), and the level builds them.
    [Fact]
    public void AModAddsBrushesToALevelByPatchingItsDocument()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent()
            .File("data/room.json", """
                [ { "type": "placements", "id": "room", "place": [
                    { "prefab": "sage:brush", "at": [0, 0, 0], "name": "floor", "overrides": { "parts": { "brush": { "size": [8, 0.5, 8] } } } } ] } ]
                """, ns: "blkgame")
            .File("data/patch.json", """
                [ { "type": "placements", "id": "blkgame:room", "patch": true, "place+": [
                    { "prefab": "sage:cylinder", "at": [1, 0.5, 1], "name": "column", "overrides": { "parts": { "brush": { "size": [1, 3, 1] } } } } ] } ]
                """, ns: "blkmod")
            .Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        Assert.Equal(2, world.SpawnPlacements(new RecordId("blkgame", "room")));
        var column = world.FindByName("column");
        Assert.Equal(BrushShape.Cylinder, world.Get<BlockoutBrush>(column).Shape);
        Assert.True(world.Get<PhysicsBody>(column).IsStatic);
        Assert.True(world.Get<PhysicsBody>(world.FindByName("floor")).IsStatic);
    }
}
