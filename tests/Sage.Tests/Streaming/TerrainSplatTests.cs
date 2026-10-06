#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Splat terrain (issue #307): the `terrain_material` record, its load checks, and the weight map
// (TerrainSplat) the client's chunk meshes carry. The blend is the client's shader's; everything that
// decides it — which layer lies where, and that both sides of a sector edge agree — is here, headless.
public class TerrainSplatTests
{
    public TerrainSplatTests() { _ = TestEnv.UserRoot; }

    private static string SandboxGame => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");

    private static HeadlessApp Boot(string records) =>
        HeadlessApp.Simulation().File("data/terrain.json", records).File("textures/a.png", "").File("textures/b.png", "").Boot("splat");

    private static TerrainMaterialRecord TwoLayers(float minSlope = 10f, float blend = 6f) => new()
    {
        Layers =
        {
            new TerrainLayer { Texture = AssetPath.Intern("textures/a.png") },
            new TerrainLayer { Texture = AssetPath.Intern("textures/b.png"), Tile = 4, MinSlope = minSlope, SlopeBlend = blend },
        },
    };

    [Fact]
    public void ATerrainMaterialLoadsItsLayersRulesAndDetail()
    {
        using var app = Boot("""
        [ { "type": "terrain_material", "id": "downs", "detail": "textures/b.png", "detailTile": 1.5, "detailStrength": 0.25,
            "layers": [ { "texture": "textures/a.png" },
                        { "texture": "textures/b.png", "tile": 12, "minSlope": 20, "slopeBlend": 5 },
                        { "texture": "textures/a.png", "minHeight": 30, "maxHeight": 80, "heightBlend": 10 } ] },
          { "type": "terrain", "id": "ground", "generator": "Hills", "material": "downs" } ]
        """);
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var record = app.Records.All<TerrainMaterialRecord>().Single();
        Assert.Equal(3, record.Layers.Count);
        Assert.Equal(8f, record.Layers[0].Tile);              // the default
        Assert.Equal(12f, record.Layers[1].Tile);
        Assert.Equal(20f, record.Layers[1].MinSlope);
        Assert.Equal(90f, record.Layers[1].MaxSlope);          // open above
        Assert.Equal(30f, record.Layers[2].MinHeight);
        Assert.Equal(1.5f, record.DetailTile);
        Assert.Equal(new RecordId("sage", "terrain_default"), record.Fallback);
        Assert.Equal(new RecordId("sage", "downs"), app.Records.All<TerrainRecord>().Single().Material);
    }

    [Fact]
    public void TerrainMaterialMistakesAreLoadErrors()
    {
        using var app = Boot("""
        [ { "type": "terrain_material", "id": "bare" },
          { "type": "terrain_material", "id": "five", "layers": [
              { "texture": "textures/a.png" }, { "texture": "textures/a.png" }, { "texture": "textures/a.png" },
              { "texture": "textures/a.png" }, { "texture": "textures/a.png" } ] },
          { "type": "terrain_material", "id": "blank", "layers": [ { "tile": 4 } ] },
          { "type": "terrain_material", "id": "flat_tile", "layers": [ { "texture": "textures/a.png", "tile": 0 } ] },
          { "type": "terrain_material", "id": "ruled_base", "layers": [ { "texture": "textures/a.png", "minSlope": 10 } ] },
          { "type": "terrain_material", "id": "upside", "layers": [ { "texture": "textures/a.png" },
              { "texture": "textures/b.png", "minHeight": 50, "maxHeight": 10 } ] },
          { "type": "terrain_material", "id": "cliff", "layers": [ { "texture": "textures/a.png" },
              { "texture": "textures/b.png", "minSlope": 95 } ] },
          { "type": "terrain_material", "id": "loud", "detailStrength": 2, "layers": [ { "texture": "textures/a.png" } ] },
          { "type": "terrain_material", "id": "fine", "layers": [ { "texture": "textures/a.png" },
              { "texture": "textures/b.png", "minSlope": 90, "maxSlope": 90, "slopeBlend": 0 } ] } ]
        """);
        string errors = string.Join("\n", app.Records.LoadErrors);
        Assert.Equal(9, app.Records.ErrorCount);
        Assert.Contains("bare: has no layers", errors);
        Assert.Contains("five: has 5 layers; the terrain shader blends at most 4", errors);
        Assert.Contains("blank: names no texture", errors);
        Assert.Contains("flat_tile: must be more than 0 m", errors);
        Assert.Contains("ruled_base: is the first layer", errors);
        Assert.Contains("upside: is more than maxHeight", errors);
        Assert.Contains("cliff: must be between 0 and 90 degrees", errors);   // and so more than maxSlope
        Assert.Contains("cliff: is more than maxSlope", errors);
        Assert.Contains("loud: must be between 0 and 1", errors);
        Assert.DoesNotContain("fine", errors);
    }

    // Painter's order: the first layer is the ground, each later one over the ones before it by its
    // coverage, fading across its blend; the weights always sum to one.
    [Fact]
    public void LayersArePaintedOverTheGroundByTheirHeightAndSlopeRules()
    {
        var material = TwoLayers(minSlope: 10f, blend: 6f);
        Assert.Equal(new Vector4(1, 0, 0, 0), TerrainSplat.Blend(material, 0f, 0f));     // flat: the ground
        Assert.Equal(new Vector4(0, 1, 0, 0), TerrainSplat.Blend(material, 0f, 30f));    // steep: the rock
        var edge = TerrainSplat.Blend(material, 0f, 10f);                                 // the middle of the fade
        Assert.Equal(0.5f, edge.X, 4);
        Assert.Equal(0.5f, edge.Y, 4);

        material.Layers.Add(new TerrainLayer { Texture = AssetPath.Intern("textures/a.png"), MinHeight = 100, HeightBlend = 0 });
        var snow = TerrainSplat.Blend(material, 150f, 30f);                               // snow over the rock
        Assert.Equal(new Vector4(0, 0, 1, 0), snow);
        var low = TerrainSplat.Blend(material, 50f, 10f);
        Assert.Equal(1f, low.X + low.Y + low.Z + low.W, 5);
        Assert.Equal(0f, low.Z);

        // A layer's height band closes at both ends.
        var band = new TerrainLayer { MinHeight = 0, MaxHeight = 10, HeightBlend = 2 };
        Assert.Equal(1f, TerrainSplat.Coverage(band, 5f, 0f));
        Assert.Equal(0f, TerrainSplat.Coverage(band, 12f, 0f));
        Assert.Equal(0f, TerrainSplat.Coverage(band, -2f, 0f));
        Assert.Equal(0.5f, TerrainSplat.Coverage(band, 10f, 0f), 4);
    }

    // What the client's material for it is: four layers (a missing one repeats the first), each tiled by
    // metres per UV unit over its tile, and no detail at strength 0.
    [Fact]
    public void TheSplatEffectsParamsComeFromTheRecord()
    {
        var none = AssetPath.Intern("textures/white.png");
        var p = TerrainSplat.Params(TwoLayers(), none);
        Assert.Equal(AssetPath.Intern("textures/a.png"), p["Layer0"].Texture);
        Assert.Equal(AssetPath.Intern("textures/b.png"), p["Layer1"].Texture);
        Assert.Equal(AssetPath.Intern("textures/a.png"), p["Layer2"].Texture);
        Assert.Equal(AssetPath.Intern("textures/a.png"), p["Layer3"].Texture);
        Assert.Equal(new[] { 1f, 2f, 1f, 1f }, p["LayerTiling"].Values);   // 8 m a UV unit: tiles of 8 m and 4 m
        Assert.Equal(none, p["Detail"].Texture);
        Assert.Equal(0f, p["DetailParams"].Values![1]);
    }

    // The issue's done criterion, on the Sandbox itself: its ground is a terrain material of two layers,
    // and sector (0, 0) has both — grass in the clearing, rock on the steep sides, and a blend between.
    // Walking east into sector (1, 0), the vertices the two sectors share have the same height, the same
    // normal and the same weights, so neither the lighting nor the blend shows a seam.
    [Fact]
    public void TheSandboxBlendsTwoLayersWithNoSeamAtTheSectorEdge()
    {
        using var app = HeadlessApp.ForGame(SandboxGame, new global::Sandbox.SandboxModule()).WithEngineContent().Boot();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var terrain = app.World.Resources.Get<Terrain>();
        Assert.False(terrain.Material.IsEmpty);
        Assert.True(app.Records.TryGet(terrain.Material, out TerrainMaterialRecord material));
        Assert.Equal(2, material.Layers.Count);

        var home = terrain.Load(new SectorCoord(0, 0))!.Heights;
        int n = home.Resolution, grass = 0, rock = 0, blended = 0;
        for (int z = 0; z < n; z++)
            for (int x = 0; x < n; x++)
            {
                var w = TerrainSplat.Unpack(TerrainSplat.Weights(material, home, x, z));
                Assert.Equal(1f, w.X + w.Y + w.Z + w.W, 1);
                if (w.X > 0.95f) grass++;
                else if (w.Y > 0.95f) rock++;
                else blended++;
            }
        int all = n * n;
        Assert.True(grass > all / 10, $"grass on {grass} of {all} vertices");
        Assert.True(rock > all / 100, $"rock on {rock} of {all} vertices");
        Assert.True(blended > all / 100, $"a blend on {blended} of {all} vertices");

        var east = terrain.Load(new SectorCoord(1, 0))!.Heights;
        var north = terrain.Load(new SectorCoord(0, 1))!.Heights;
        int last = n - 1;
        for (int k = 0; k < n; k++)
        {
            Assert.Equal(home[last, k], east[0, k]);
            Assert.Equal(home.VertexNormal(last, k), east.VertexNormal(0, k));
            Assert.Equal(TerrainSplat.Weights(material, home, last, k), TerrainSplat.Weights(material, east, 0, k));

            Assert.Equal(home[k, last], north[k, 0]);
            Assert.Equal(home.VertexNormal(k, last), north.VertexNormal(k, 0));
            Assert.Equal(TerrainSplat.Weights(material, home, k, last), TerrainSplat.Weights(material, north, k, 0));
        }
        // And the edge has slopes on it worth agreeing about: rock lies somewhere along it.
        Assert.Contains(Enumerable.Range(0, n), k => TerrainSplat.Unpack(TerrainSplat.Weights(material, home, last, k)).Y > 0.05f);
    }

    // A scene's terrain record sets the world's terrain material, and placing the same scene with another
    // look regenerates the ground.
    [Fact]
    public void ASceneTerrainRecordSetsTheTerrainMaterial()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .File("data/content.json", """
            [ { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
              { "type": "terrain_material", "id": "downs", "layers": [ { "texture": "textures/a.png" } ] },
              { "type": "terrain", "id": "downs", "generator": "Hills", "material": "downs" },
              { "type": "scene", "id": "downs", "streamed": true, "terrain": "downs", "player": { "prefab": "hero", "at": [20, 2, 20] } } ]
            """, "game")
            .File("textures/a.png", "", "game")
            .StartScene("game:downs").Boot();
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        Assert.Equal(new RecordId("game", "downs"), app.World.Resources.Get<Terrain>().Material);
    }
}
