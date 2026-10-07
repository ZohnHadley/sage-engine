#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The editor's terrain tools (issue #372, docs/design/15 §11): brushes that sculpt and paint the ground and
// set its water, each stroke one undo step, saved to the file the streaming system loads. The claim is about
// what a game sees, so it is checked the way a game sees it: sculpt in an edit world, save, boot the game
// again from the same folder, and walk the streaming ring onto the hill.
public class TerrainToolsTests
{
    public TerrainToolsTests() { _ = TestEnv.UserRoot; }

    private const float Ground = 10f;

    private const string Content = """
    [
      { "type": "terrain_material", "id": "downs",
        "layers": [ { "texture": "textures/grass.png" }, { "texture": "textures/road.png", "minHeight": 1000 } ] },
      { "type": "terrain", "id": "ground", "generator": "Flat", "height": 10, "material": "downs" },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"], "parts": { "inventory": { "capacity": 10 } } },
      { "type": "scene", "id": "valley", "terrain": "ground", "streamed": true, "player": { "prefab": "hero", "at": [100, 20, 100] } }
    ]
    """;

    private static MountFixture Files()
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Write("game", "textures/grass.png", "");
        files.Write("game", "textures/road.png", "");
        files.Mount("game", "game");
        return files;
    }

    private static HeadlessApp Build(MountFixture files) =>
        HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay().Mount(files).StartScene("game:valley").Build();

    // The editor's side: an edit world of the valley, and its terrain document.
    private static (HeadlessApp App, TerrainDocument Terrain) Edit(MountFixture files)
    {
        var app = Build(files);
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var world = app.App.CreateEditWorld("edit");
        var doc = new TerrainDocument(world);
        Assert.True(doc.CanEdit);
        return (app, doc);
    }

    private static Terrain TerrainOf(TerrainDocument doc) => doc.Terrain!;

    private static void Raise(TerrainDocument doc, Vector3 at, float radius = 40f, float metres = 5f, int dabs = 4)
    {
        doc.Brush.Tool = TerrainTool.Raise;
        doc.Brush.Radius = radius;
        doc.Brush.Strength = metres;
        doc.BeginStroke();
        for (int i = 0; i < dabs; i++) doc.Dab(at);
        Assert.NotNull(doc.EndStroke());
    }

    private static Vector4 WeightsAt(Terrain terrain, float x, float z)
    {
        var sector = terrain.Sector(Terrain.SectorOf(x, z))!;
        var material = new TerrainMaterialRecord
        {
            Layers = { new TerrainLayer { Texture = AssetPath.Intern("textures/grass.png") }, new TerrainLayer { Texture = AssetPath.Intern("textures/road.png"), MinHeight = 1000 } },
        };
        var corner = sector.Coord.Origin(Terrain.SectorSize);
        return TerrainSplat.Unpack(TerrainSplat.Weights(material, sector.Heights, (int)MathF.Round((x - corner.X) / 8f), (int)MathF.Round((z - corner.Z) / 8f)));
    }

    // The issue's done criterion: sculpt a hill, paint a path, set a lake, save — and a game booted again
    // from the same folder streams the ground in with the hill, the path and the lake on it.
    [Fact]
    public void ASculptedHillAPaintedPathAndALakeAreSavedAndTheStreamedWorldShowsThem()
    {
        var files = Files();
        float hill;
        var (app, doc) = Edit(files);
        using (app)
        {
            var terrain = TerrainOf(doc);
            Raise(doc, new Vector3(100, 0, 100));
            hill = terrain.HeightAt(100, 100);
            Assert.True(hill > Ground + 15f, $"the hill is {hill} m");
            Assert.Equal(Ground, terrain.HeightAt(300, 300));   // outside the brush, the generator's

            // A road along z = 200, painted in one stroke of three dabs.
            doc.Brush.Tool = TerrainTool.Paint;
            doc.Brush.Layer = 1;
            doc.Brush.Radius = 24f;
            doc.Brush.Strength = 1f;
            doc.BeginStroke();
            foreach (float x in new[] { 160f, 176f, 192f }) doc.Dab(new Vector3(x, 0, 200));
            doc.EndStroke();
            Assert.Equal(1f, WeightsAt(terrain, 176, 200).Y, 2);
            Assert.Equal(1f, WeightsAt(terrain, 400, 400).X, 2);   // the grass everywhere else

            Assert.True(doc.SetWater(new SectorCoord(0, 0), 12f));
            Assert.Equal(3, doc.History.Entries.Count);
            Assert.True(doc.Dirty);
            Assert.True(doc.Save());
            Assert.False(doc.Dirty);
            Assert.True(File.Exists(Path.Combine(files.Dir("game"), "terrain", "ground" + TerrainSculpt.Extension)));
        }

        // The game: the scene's terrain reads the sculpt, and the ring loads the player's sector with it.
        using var game = Build(files);
        var world = game.CreateWorld("main");
        for (int i = 0; i < 3; i++) world.RunFixed(1f / 60f);
        var ground = world.Resources.Get<Terrain>();
        Assert.True(ground.IsLoaded(new SectorCoord(0, 0)));
        Assert.Equal(hill, ground.HeightAt(100, 100), 3);
        Assert.Equal(1f, WeightsAt(ground, 176, 200).Y, 2);
        Assert.Equal(1, ground.Sector(new SectorCoord(0, 0))!.Heights.LayerAt(176 / 8, 200 / 8));   // the road's surface layer

        var water = Assert.Single(world.Query<Transform, WaterVolume>().Entities.ToEntityList());
        Assert.Equal(12f, world.Get<WaterVolume>(water).SurfaceAt(world.Get<Transform>(water).LocalPosition), 3);
        Assert.Equal(new SectorCoord(0, 0), world.Get<SectorOwned>(water).Sector);
    }

    // A stroke is one undo step however many dabs it had, and undoing it puts the ground back exactly —
    // in the document, in the world's terrain, and on the loaded sector the editor shows.
    [Fact]
    public void AStrokeOfManyDabsIsOneUndoStepAndRedoPutsItBack()
    {
        var (app, doc) = Edit(Files());
        using (app)
        {
            var terrain = TerrainOf(doc);
            Raise(doc, new Vector3(500, 0, 500), dabs: 6);
            float raised = terrain.HeightAt(500, 500);
            Assert.True(raised > Ground + 25f);
            var entry = Assert.Single(doc.History.Entries);
            Assert.Contains("Raise", entry.Description);
            Assert.Contains("6 dabs", entry.Description);

            Assert.True(doc.Undo());
            Assert.Equal(Ground, terrain.HeightAt(500, 500), 4);
            Assert.Equal(0, doc.Sculpt.Count);
            Assert.Equal(0, terrain.CopySculpt().Count);
            Assert.True(doc.Redo());
            Assert.Equal(raised, terrain.HeightAt(500, 500), 4);

            // A stroke that changed nothing (smoothing flat ground) is not an undo step.
            doc.Brush.Tool = TerrainTool.Smooth;
            Assert.Null(doc.Stroke(new Vector3(800, 0, 800)));
            Assert.Single(doc.History.Entries);
        }
    }

    // Lower digs; flatten pulls the ground to a height (or to where the stroke began); smooth takes a peak down.
    [Fact]
    public void LowerFlattenAndSmoothMoveTheGroundTowardTheirTargets()
    {
        var (app, doc) = Edit(Files());
        using (app)
        {
            var terrain = TerrainOf(doc);
            doc.Brush.Tool = TerrainTool.Lower;
            doc.Brush.Radius = 30f;
            doc.Brush.Strength = 4f;
            doc.Stroke(new Vector3(200, 0, 600));
            Assert.Equal(Ground - 4f, terrain.HeightAt(200, 600), 3);

            Raise(doc, new Vector3(600, 0, 600), radius: 24f, metres: 10f, dabs: 3);
            float peak = terrain.HeightAt(600, 600);
            doc.Brush.Tool = TerrainTool.Smooth;
            doc.Brush.Radius = 24f;
            doc.Brush.Strength = 1f;
            doc.BeginStroke();
            for (int i = 0; i < 5; i++) doc.Dab(new Vector3(600, 0, 600));
            doc.EndStroke();
            Assert.True(terrain.HeightAt(600, 600) < peak - 1f, $"smoothed {peak} to {terrain.HeightAt(600, 600)}");

            doc.Brush.Tool = TerrainTool.Flatten;
            doc.Brush.FlattenHeight = 15f;
            doc.Brush.Radius = 40f;
            doc.Brush.Strength = 1f;
            doc.Stroke(new Vector3(600, 0, 600));
            Assert.Equal(15f, terrain.HeightAt(600, 600), 2);
            Assert.Equal(4, doc.History.Entries.Count);
        }
    }

    // A hill raised on the line between two sectors: the vertices they share have one height and one normal,
    // so neither the ground nor its lighting has a step, and both sectors are in the one undo step.
    [Fact]
    public void AHillOnASectorEdgeHasNoSeam()
    {
        var (app, doc) = Edit(Files());
        using (app)
        {
            var terrain = TerrainOf(doc);
            Raise(doc, new Vector3(Terrain.SectorSize, 0, 300), radius: 48f);
            var stroke = (TerrainStroke)doc.History.Entries[0];
            Assert.Contains(stroke.Changes, c => c.Coord == new SectorCoord(0, 0));
            Assert.Contains(stroke.Changes, c => c.Coord == new SectorCoord(1, 0));

            var west = terrain.Sector(new SectorCoord(0, 0))!.Heights;
            var east = terrain.Sector(new SectorCoord(1, 0))!.Heights;
            for (int z = 30; z <= 45; z++)
            {
                Assert.Equal(west[128, z], east[0, z]);
                Assert.True(Vector3.Distance(west.VertexNormal(128, z), east.VertexNormal(0, z)) < 1e-4f, $"normal at z {z}");
            }
            Assert.True(west[128, 37] > Ground + 10f);
        }
    }

    // The console has every tool, positions in absolute metres: what a script or the smoke run presses.
    [Fact]
    public void TheConsoleSculptsPaintsSetsWaterUndoesAndSaves()
    {
        var files = Files();
        var (app, doc) = Edit(files);
        using (app)
        {
            TerrainCommands.Register(app.CVars, () => doc);
            var terrain = TerrainOf(doc);
            Assert.True(app.CVars.Execute("ed_sculpt raise 304 304 30 6"));
            Assert.Equal(Ground + 6f, terrain.HeightAt(304, 304), 3);
            Assert.True(app.CVars.Execute("ed_sculpt flatten 304 304 30 1 8"));
            Assert.Equal(8f, terrain.HeightAt(304, 304), 3);
            Assert.True(app.CVars.Execute("ed_paint 1 300 300 10 1"));
            Assert.True(app.CVars.Execute("ed_water 300 300 9.5"));
            Assert.Equal(9.5f, doc.Sculpt.Get(new SectorCoord(0, 0))!.Water);
            Assert.Equal(4, doc.History.Entries.Count);
            Assert.True(app.CVars.Execute("ed_terrain_undo"));
            Assert.Null(doc.Sculpt.Get(new SectorCoord(0, 0))!.Water);
            Assert.True(app.CVars.Execute("ed_terrain_save"));
            Assert.False(doc.Dirty);
            Assert.True(app.CVars.Execute("ed_terrain"));
        }
    }

    // The file: what is written is what is read, a sector with nothing in it is not written, and a file that
    // is not a sculpt is refused (the ground is then the generator's alone).
    [Fact]
    public void TheSculptFileRoundTripsAndOneThatIsNotASculptIsRefused()
    {
        var sculpt = new TerrainSculpt();
        var hill = sculpt.GetOrAdd(new SectorCoord(-2, 3));
        hill.Heights[100] = 4.5f;
        hill.Paint = new uint[SculptSector.Vertices];
        hill.Paint[7] = 0x0000FF00;
        sculpt.GetOrAdd(new SectorCoord(5, 5)).Water = -3f;
        sculpt.GetOrAdd(new SectorCoord(9, 9));   // empty: not written
        var back = TerrainSculpt.FromBytes(sculpt.ToBytes());
        Assert.Equal(2, back.Count);
        Assert.True(back.Get(new SectorCoord(-2, 3))!.SameAs(hill));
        Assert.Equal(-3f, back.Get(new SectorCoord(5, 5))!.Water);
        Assert.Throws<InvalidDataException>(() => TerrainSculpt.FromBytes(new byte[] { 1, 2, 3, 4, 5 }));

        var files = Files();
        files.Write("game", "terrain/ground.sterrain", "not a sculpt");
        using var app = Build(files);
        var world = app.CreateWorld("main");
        world.RunFixed(1f / 60f);
        var terrain = world.Resources.Get<Terrain>();
        Assert.NotNull(terrain.Generator);
        Assert.Equal(0, terrain.CopySculpt().Count);
        Assert.Equal(Ground, terrain.HeightAt(100, 100));
    }
}
