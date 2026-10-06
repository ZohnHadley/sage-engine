#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Mesh LOD and per-layer draw distance (issue 4n-1, docs/design/06 §3.6): which mesh a renderer draws at a
// distance, with hysteresis, and what is left out, decided headless by `MeshLod` as the client's Extract
// asks it per renderer and view (and counts for r_stats). The drawing itself is the smoke run's.
public class MeshLodTests
{
    public MeshLodTests() { _ = TestEnv.UserRoot; }

    private const string Content = """
    [
      { "type": "mesh_lod", "id": "rock", "levels": [
          { "mesh": "models/rock_lod1.glb", "distance": 30 },
          { "mesh": "models/rock_lod2.glb", "distance": 80 } ],
        "cullDistance": 150 },
      { "type": "mesh_lod", "id": "statue", "metric": "ScreenSize", "levels": [
          { "mesh": "models/rock_lod1.glb", "screenSize": 0.2 } ],
        "cullScreenSize": 0.02 },
      { "type": "prefab", "id": "rock", "components": { "mesh_renderer": { "mesh": "models/rock.glb", "lod": "rock" } } },
      { "type": "prefab", "id": "pebble", "components": { "mesh_renderer": { "mesh": "models/rock.glb", "layer": 3 } } }
    ]
    """;

    private static HeadlessApp Boot(string content = Content)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", content);
        foreach (var model in new[] { "rock", "rock_lod1", "rock_lod2" }) files.Write("game", $"models/{model}.glb", "");
        files.Mount("game", "game");
        return HeadlessApp.Simulation().Mount(files).Boot();
    }

    private static RecordId Id(string name) => new("game", name);

    // What the client's Extract does for the screen's view: the level at a distance, remembered.
    private static int Pick(World world, Entity entity, float distance, ref LodCounts counts)
    {
        ref var renderer = ref world.Get<MeshRenderer>(entity);
        world.Engine!.Records.TryGet(renderer.Lod, out MeshLodRecord lod);
        return MeshLod.Pick(ref renderer, lod, world.Resources.Get<RenderEnvironment>(), distance, 0f, remember: true, ref counts);
    }

    private static AssetPath MeshAt(World world, Entity entity, int level)
    {
        var renderer = world.Get<MeshRenderer>(entity);
        return level == 0 ? renderer.Mesh : world.Engine!.Records.Get<MeshLodRecord>(renderer.Lod).Levels[level - 1].Mesh;
    }

    // Walking away from a rock: its own mesh up close, the first coarse one from 30 m, the second from
    // 80 m, and nothing past 150 m; each a draw r_stats counts as lowered or culled.
    [Xunit.Fact]
    public void TheChosenMeshChangesWithDistance_AndLodCulledDrawsAreCounted()
    {
        using var app = Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var rock = world.Spawn(Id("rock"), Vector3.Zero);
        var counts = new LodCounts();

        Assert.Equal(0, Pick(world, rock, 5f, ref counts));
        Assert.Equal("models/rock.glb", MeshAt(world, rock, 0).ToString());
        Assert.Equal(1, Pick(world, rock, 40f, ref counts));
        Assert.Equal("models/rock_lod1.glb", MeshAt(world, rock, 1).ToString());
        Assert.Equal(2, Pick(world, rock, 100f, ref counts));
        Assert.Equal("models/rock_lod2.glb", MeshAt(world, rock, 2).ToString());
        Assert.Equal(MeshLod.Culled, Pick(world, rock, 200f, ref counts));
        Assert.Equal(MeshLod.Culled, Pick(world, rock, 1000f, ref counts));

        Assert.Equal(2, counts.Culled);
        Assert.Equal(2, counts.Lowered);
    }

    // At a switch point the camera may stand still and jitter: going coarser happens at the switch, coming
    // back waits until it is 10% (the default) inside it.
    [Xunit.Fact]
    public void ASwitchBackWaitsForTheHysteresisBand()
    {
        using var app = Boot();
        var world = app.World;
        var rock = world.Spawn(Id("rock"), Vector3.Zero);
        var counts = new LodCounts();

        Assert.Equal(0, Pick(world, rock, 29.9f, ref counts));
        Assert.Equal(1, Pick(world, rock, 30.1f, ref counts));
        Assert.Equal(1, Pick(world, rock, 29.0f, ref counts));    // inside 30 m but not past 27 m
        Assert.Equal(1, Pick(world, rock, 28.0f, ref counts));
        Assert.Equal(0, Pick(world, rock, 26.5f, ref counts));

        Assert.Equal(MeshLod.Culled, Pick(world, rock, 151f, ref counts));
        Assert.Equal(MeshLod.Culled, Pick(world, rock, 140f, ref counts));   // the cull point has the band too
        Assert.Equal(2, Pick(world, rock, 134f, ref counts));
        // From far, straight back close: every band passed at once.
        Assert.Equal(0, Pick(world, rock, 1f, ref counts));

        // A first choice has nothing to hold on to.
        var lod = world.Engine!.Records.Get<MeshLodRecord>(Id("rock"));
        Assert.Equal(1, MeshLod.Choose(lod, 29f, 0f, 1));
        Assert.Equal(0, MeshLod.Choose(lod, 29f, 0f));
    }

    // A screen-size group switches by how much of the view the mesh covers, so a narrower field of view
    // (a scope) keeps the fine mesh farther out.
    [Xunit.Fact]
    public void AScreenSizeGroupSwitchesByTheShareOfTheViewItCovers()
    {
        using var app = Boot();
        var lod = app.Engine.Records.Get<MeshLodRecord>(Id("statue"));
        float wide = 1f / MathF.Tan(MathF.PI / 4f);   // 90° field of view: projection M22
        float scope = 1f / MathF.Tan(MathF.PI / 36f); // 10°

        // A 1 m statue at 4 m covers a quarter of a 90° view's height; at 10 m a tenth; at 100 m 1%.
        Assert.Equal(0.25f, MeshLod.ScreenSize(1f, 4f, wide, orthographic: false), 4);
        Assert.Equal(0, MeshLod.Choose(lod, 4f, MeshLod.ScreenSize(1f, 4f, wide, false)));
        Assert.Equal(1, MeshLod.Choose(lod, 10f, MeshLod.ScreenSize(1f, 10f, wide, false)));
        Assert.Equal(MeshLod.Culled, MeshLod.Choose(lod, 100f, MeshLod.ScreenSize(1f, 100f, wide, false)));
        Assert.Equal(0, MeshLod.Choose(lod, 10f, MeshLod.ScreenSize(1f, 10f, scope, false)));
        Assert.Equal(1, MeshLod.Choose(lod, 100f, MeshLod.ScreenSize(1f, 100f, scope, false)));
        // An orthographic view does not shrink with distance.
        Assert.Equal(MeshLod.ScreenSize(1f, 1f, 0.1f, true), MeshLod.ScreenSize(1f, 500f, 0.1f, true));
    }

    // A layer's draw distance leaves small props out early, with or without a LOD group, and is counted
    // with what LOD culled; 0 (the default) is no limit.
    [Xunit.Fact]
    public void ALayersDrawDistanceLeavesItsMeshesOutPastIt()
    {
        using var app = Boot();
        var world = app.World;
        var pebble = world.Spawn(Id("pebble"), Vector3.Zero);
        var rock = world.Spawn(Id("rock"), Vector3.Zero);
        var counts = new LodCounts();

        Assert.Equal(0, Pick(world, pebble, 1000f, ref counts));   // no limit yet
        world.Resources.Get<RenderEnvironment>().LayerDrawDistance[3] = 40f;
        Assert.Equal(0, Pick(world, pebble, 39f, ref counts));
        Assert.Equal(MeshLod.Culled, Pick(world, pebble, 41f, ref counts));
        Assert.Equal(1, Pick(world, rock, 41f, ref counts));       // the rock is on layer 0
        Assert.Equal(1, counts.Culled);
        Assert.True(MeshLod.PastLayerDistance(world.Resources.Get<RenderEnvironment>(), 3, 41f));
        Assert.False(MeshLod.PastLayerDistance(world.Resources.Get<RenderEnvironment>(), 0, 1e6f));
    }

    // A LOD group's mistakes are load errors at their lines: a level without a mesh, a switch no farther
    // than the one before, a level with no switch for its metric, a cull point before the last level,
    // and a renderer naming a group that is not there.
    [Xunit.Fact]
    public void ABadLodGroupIsALoadError()
    {
        using var log = new CaptureSink();
        using var app = Boot("""
        [
          { "type": "mesh_lod", "id": "no_mesh", "levels": [ { "distance": 30 } ] },
          { "type": "mesh_lod", "id": "backwards", "levels": [
              { "mesh": "models/rock_lod1.glb", "distance": 50 },
              { "mesh": "models/rock_lod2.glb", "distance": 20 } ] },
          { "type": "mesh_lod", "id": "no_switch", "metric": "ScreenSize", "levels": [ { "mesh": "models/rock_lod1.glb", "distance": 30 } ] },
          { "type": "mesh_lod", "id": "early_cull", "levels": [ { "mesh": "models/rock_lod1.glb", "distance": 30 } ], "cullDistance": 20 },
          { "type": "mesh_lod", "id": "good", "levels": [ { "mesh": "models/rock_lod1.glb", "distance": 30 } ] },
          { "type": "prefab", "id": "lost", "components": { "mesh_renderer": { "mesh": "models/rock.glb", "lod": "missing" } } }
        ]
        """);
        Assert.Equal(5, app.Records.ErrorCount);
        Assert.Contains(log.Entries, e => e.Message.Contains("no_mesh") && e.Message.Contains("names no mesh"));
        Assert.Contains(log.Entries, e => e.Message.Contains("backwards") && e.Message.Contains("switches no later"));
        Assert.Contains(log.Entries, e => e.Message.Contains("no_switch") && e.Message.Contains("\"screenSize\" above 0"));
        Assert.Contains(log.Entries, e => e.Message.Contains("early_cull") && e.Message.Contains("is not past the last level"));
        Assert.Contains(log.Entries, e => e.Message.Contains("missing"));
    }
}
