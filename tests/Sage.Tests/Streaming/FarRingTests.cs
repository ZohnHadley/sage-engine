#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Issue #277: the far ring past the full-detail ring, per-sector asset scopes, generation on jobs and
// seam-free normals (docs/design/14 §3 "As built (the far ring, asset scopes and jobs)").
public class FarRingTests
{
    public FarRingTests() { _ = TestEnv.UserRoot; }

    private const float Sector = Terrain.SectorSize;

    // Hills from a `terrain` record; a tower in (3, 0) that has a far look and stands on the ground; a rock
    // and a lamp in (1, 0), the lamp drawing a model the game also uses elsewhere.
    private const string Content = """
    [
      { "type": "terrain", "id": "hills", "generator": "Hills", "seed": 4, "height": 0, "amplitude": 20, "wavelength": 300 },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
      { "type": "prefab", "id": "tower", "name": "tower",
        "components": { "mesh_renderer": { "mesh": "models/tower.glb" } },
        "far": { "size": [6, 18, 6] } },
      { "type": "prefab", "id": "spire", "name": "spire", "far": { "mesh": "models/spire_far.glb" } },
      { "type": "prefab", "id": "rock", "name": "rock", "components": { "mesh_renderer": { "mesh": "models/rock.glb" } } },
      { "type": "prefab", "id": "lamp", "name": "lamp", "components": { "mesh_renderer": { "mesh": "models/shared.glb" } } },
      { "type": "scene", "id": "valley", "streamed": true, "terrain": "hills",
        "player": { "prefab": "hero", "at": [20, 1, 20] },
        "place": [ { "prefab": "tower", "at": [3172, 2, 100], "relativeTo": "Ground", "yaw": 90 },
                   { "prefab": "spire", "at": [3300, 50, 300] },
                   { "prefab": "rock", "at": [1100, 0, 50] },
                   { "prefab": "lamp", "at": [1120, 0, 60] } ] }
    ]
    """;

    private static HeadlessApp Run()
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        foreach (var model in new[] { "tower", "spire_far", "rock", "shared" }) files.Write("game", $"models/{model}.glb", "");
        files.Mount("game", "game");
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .Mount(files).StartScene("game:valley").Boot();
        app.Engine.Saves.Root = TestEnv.NewTempDir();
        app.CVars.Execute("save_autosave 0");
        Tick(app.World);
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static void Walk(World world, float x, float z, int ticks = 1)
    {
        world.Get<Transform>(Hero(world)).LocalPosition = world.Origin().ToOrigin(new Vector3(x, 1, z));
        Tick(world, ticks);
    }

    // Past the full ring, out to `stream_far_radius` (4), every sector has coarse ground and the far looks of
    // what is placed there — and none of them is also full.
    [Xunit.Fact]
    public void TheFarRingHasCoarseGroundAndFarLooksPastTheFullRing()
    {
        using var app = Run();
        var world = app.World;
        var lod = world.Resources.Get<SectorLod>();
        var ring = world.Resources.Get<SectorRing>();
        var terrain = world.Resources.Get<Terrain>();
        lod.Finish();

        Assert.Equal(9 * 9 - 3 * 3, lod.Count);
        Assert.All(lod.Sectors, far => Assert.DoesNotContain(far.Coord, ring.Live));
        Assert.All(lod.Sectors, far => Assert.Equal(Terrain.FarResolution, far.Heights!.Resolution));
        Assert.Equal(9, terrain.Sectors.Count);   // only the full ring is full detail

        // The coarse ground is the same hills, just sampled every 64 m.
        var sampler = Assert.IsAssignableFrom<ITerrainSampler>(terrain.Generator);
        var east = lod.Get(new SectorCoord(3, 0))!;
        for (int i = 0; i < Terrain.FarResolution; i += 4)
            Assert.Equal(sampler.SampleHeight(3 * Sector + i * 64.0, i * 64.0, terrain.Seed), east.Heights![i, i], 3);

        // The tower is a box on the ground there; the spire a far model at its own height. The rock has no
        // far look and is not in the far ring at all.
        var tower = Assert.Single(east.Proxies, p => p.Mesh.IsEmpty);
        Assert.True(tower.OnGround);
        Assert.Equal(new Vector3(3172, 2, 100), tower.Absolute);
        Assert.Equal(new Vector3(6, 18, 6), tower.Size);
        Assert.Equal(90f, tower.Yaw);
        var spire = Assert.Single(east.Proxies, p => !p.Mesh.IsEmpty);
        Assert.False(spire.OnGround);
        Assert.Equal(50f, spire.Absolute.Y);
        Assert.Equal(2, lod.Sectors.Sum(far => far.Proxies.Count));
    }

    // Walking east, the far sectors ahead become full one tick at a time and leave the far ring; the full
    // sectors left behind become far ones. Full and far never overlap.
    [Xunit.Fact]
    public void WalkingPromotesFarSectorsOneATickAndDemotesWhatIsLeftBehind()
    {
        using var app = Run();
        var world = app.World;
        var lod = world.Resources.Get<SectorLod>();
        var ring = world.Resources.Get<SectorRing>();
        var terrain = world.Resources.Get<Terrain>();
        var removed = new List<SectorCoord>();
        lod.Removed += removed.Add;
        Assert.True(lod.Contains(new SectorCoord(2, 0)));

        Walk(world, Sector + 512, 512, ticks: 0);
        var loads = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            Tick(world);
            loads.Add(ring.LoadedLastTick);
            Assert.All(lod.Sectors, far => Assert.DoesNotContain(far.Coord, ring.Live));
        }

        // Three new sectors in the column ahead, at most one a tick (`stream_load_budget` 1).
        Assert.All(loads, n => Assert.InRange(n, 0, 1));
        Assert.Equal(3, loads.Sum());
        foreach (var z in new[] { -1, 0, 1 })
        {
            Assert.True(terrain.IsLoaded(new SectorCoord(2, z)));
            Assert.False(lod.Contains(new SectorCoord(2, z)));
            Assert.Contains(new SectorCoord(2, z), removed);
        }
        Assert.True(lod.Contains(new SectorCoord(5, 0)), "the far ring moved east with the player");
        Assert.True(lod.Contains(new SectorCoord(-4, 0)), "and kept the west inside its margin");

        // Two more east: (−1, 0) and (0, 0) were full, and are far now; the west edge went past the margin.
        Walk(world, 3 * Sector + 512, 512, ticks: 6);
        Assert.False(terrain.IsLoaded(new SectorCoord(-1, 0)));
        Assert.False(terrain.IsLoaded(new SectorCoord(0, 0)));
        Assert.True(lod.Contains(new SectorCoord(0, 0)));
        Assert.True(lod.Contains(new SectorCoord(-1, 0)));
        Assert.False(lod.Contains(new SectorCoord(-3, 0)));
        Assert.All(lod.Sectors, far => Assert.DoesNotContain(far.Coord, ring.Live));
    }

    // A jump (the source's own sector has no ground) loads its whole ring at once: nobody falls through.
    [Xunit.Fact]
    public void AJumpLoadsItsWholeRingAtOnce()
    {
        using var app = Run();
        var world = app.World;
        var ring = world.Resources.Get<SectorRing>();
        Walk(world, 20 * Sector + 100, 100);
        Assert.Equal(9, ring.LoadedLastTick);
        Assert.True(world.Resources.Get<Terrain>().IsLoaded(new SectorCoord(20, 0)));
    }

    // A sector's assets are released when it unloads: the models only it drew, and the meshes its own
    // entities held. A model something outside the sector still draws is kept.
    [Xunit.Fact]
    public void ASectorsAssetsAreReleasedWhenItUnloadsAndSharedOnesAreKept()
    {
        using var app = Run();
        var world = app.World;
        var assets = world.Resources.Get<SectorAssets>();
        var released = new List<AssetPath>();
        var handles = new List<MeshHandle>();
        assets.Released += released.Add;
        assets.HandleReleased += handles.Add;

        var rock = AssetPath.Intern("models/rock.glb");
        var shared = AssetPath.Intern("models/shared.glb");
        Assert.Equal(1, assets.RefCount(rock));
        Assert.Equal(1, assets.RefCount(shared));

        // The game draws the lamp's model too, and a terrain chunk the client built for (−1, 0).
        var torch = world.Create(Transform.At(Vector3.Zero), "torch");
        world.Add(torch, new MeshRenderer { Mesh = shared });
        var chunk = world.Create(Transform.At(Vector3.Zero), "chunk");
        world.Add(chunk, new MeshRenderer { Handle = new MeshHandle(42) });
        world.Add(chunk, new SectorOwned { Sector = new SectorCoord(-1, 0) });
        Assert.Equal(2, assets.RefCount(shared));

        // Far east: (1, 0) goes dormant and (−1, 0)'s ground unloads.
        Walk(world, 6 * Sector + 100, 100, ticks: 3);

        Assert.Contains(rock, released);
        Assert.False(assets.IsHeld(rock));
        Assert.DoesNotContain(shared, released);
        Assert.Equal(1, assets.RefCount(shared));
        Assert.Contains(new MeshHandle(42), handles);

        // Something leaving outside a sector's unload is the game's scope: kept.
        world.Destroy(torch);
        Assert.DoesNotContain(shared, released);
        Assert.False(assets.IsHeld(shared));
    }
}

// Seam-free normals and generation on jobs (issue #277), on the Terrain alone.
public class TerrainGenerationTests
{
    public TerrainGenerationTests() { _ = TestEnv.UserRoot; }

    private static ITerrainGenerator Hills() => BuiltInTerrain.Create(new TerrainRecord
    {
        Generator = TerrainGeneratorKind.Hills, Seed = 9, Amplitude = 40, Wavelength = 120,
    });

    // The same generator without the sampler: the old behaviour, normals that clamp at the edge.
    private sealed class Blind(ITerrainGenerator inner) : ITerrainGenerator
    {
        public void Generate(SectorCoord sector, Heightfield heights, int seed) => inner.Generate(sector, heights, seed);
    }

    private static float WorstEdgeMismatch(ITerrainGenerator generator)
    {
        var terrain = new Terrain { Generator = generator, Seed = 9, Jobs = false };
        var west = terrain.Load(new SectorCoord(0, 0))!.Heights;
        var east = terrain.Load(new SectorCoord(1, 0))!.Heights;
        float worst = 0f;
        for (int z = 0; z < Terrain.SectorResolution; z++)
        {
            Assert.Equal(west[Terrain.SectorResolution - 1, z], east[0, z]);   // the vertex itself is shared
            worst = MathF.Max(worst, (west.VertexNormal(Terrain.SectorResolution - 1, z) - east.VertexNormal(0, z)).Length());
        }
        return worst;
    }

    // Two sectors agree on the normal of every vertex on their shared edge, because the one past the edge
    // was asked of the generator. Without the sampler they would not, which is the seam this removes.
    [Xunit.Fact]
    public void SectorEdgeNormalsMatchTheNeighboursSoLightingHasNoSeam()
    {
        Assert.True(WorstEdgeMismatch(Hills()) < 1e-5f, "normals differ across the edge");
        Assert.True(WorstEdgeMismatch(new Blind(Hills())) > 1e-3f, "the test's hills are too gentle to show a seam");
    }

    // A sector generated ahead on the thread pool is the same ground, bit for bit, as one generated on the
    // main thread: jobs change where the time goes, not what the world is.
    [Xunit.Fact]
    public void GenerationOnJobsIsTheSameGroundAsOnTheMainThread()
    {
        var onJobs = new Terrain { Generator = Hills(), Seed = 9 };
        var here = new Terrain { Generator = Hills(), Seed = 9, Jobs = false };
        var coords = new[] { new SectorCoord(0, 0), new SectorCoord(-3, 7), new SectorCoord(100, -40) };
        foreach (var coord in coords) onJobs.Prefetch(coord);
        here.Prefetch(coords[0]);   // jobs off: nothing is generated ahead
        Assert.False(here.IsAhead(coords[0]));

        foreach (var coord in coords)
        {
            var a = onJobs.Load(coord)!.Heights;
            var b = here.Load(coord)!.Heights;
            Assert.Equal(b.Heights, a.Heights);
            Assert.Equal(b.Normals, a.Normals);
        }
        Assert.Equal(3, here.GeneratedHere);
        Assert.Equal(3, onJobs.GeneratedHere + onJobs.GeneratedOnJobs);   // a job not started yet runs inline
        Assert.False(onJobs.IsAhead(coords[0]));
    }

    // Changing the ground drops what was generated ahead from the old one.
    [Xunit.Fact]
    public void ChangingTheGeneratorDropsWhatWasGeneratedAhead()
    {
        var terrain = new Terrain { Generator = Hills(), Seed = 9 };
        terrain.Prefetch(new SectorCoord(2, 2));
        Assert.True(terrain.IsAhead(new SectorCoord(2, 2)));
        int version = terrain.Version;
        terrain.Seed = 10;
        Assert.False(terrain.IsAhead(new SectorCoord(2, 2)));
        Assert.NotEqual(version, terrain.Version);
    }
}

// The issue's done criterion (#277): a bigger visible radius at a bounded tick, and no spike when crossing a
// sector edge. Measured in work units, which are exact on any machine — sectors generated on the main thread,
// sectors made live per tick — with the wall clock reported and bounded only generously.
[Xunit.Collection(MeasurementsCollection.Name)]
public class StreamingScaleMeasurements
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public StreamingScaleMeasurements(Xunit.Abstractions.ITestOutputHelper output)
    {
        _ = TestEnv.UserRoot;
        _output = output;
    }

    private const float Sector = Terrain.SectorSize;

    [Xunit.Fact]
    public void AFarRingFourSectorsOutCostsABoundedTickAndCrossingEdgesDoesNotSpike()
    {
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay().Boot("scale");
        using var engine = app.Engine;
        var world = app.World;
        var terrain = world.Resources.Get<Terrain>();
        var lod = world.Resources.Get<SectorLod>();
        var ring = world.Resources.Get<SectorRing>();
        terrain.Generator = BuiltInTerrain.Create(new TerrainRecord { Generator = TerrainGeneratorKind.Hills, Seed = 3, Amplitude = 30 });
        var player = world.Create(Transform.At(new Vector3(512, 0, 512)), "player");
        player.AddTag<PlayerControlled>();

        // Arriving: the full ring at once, the far ring and the ring ahead on jobs.
        Tick(world, 1);
        lod.Finish();
        WaitAhead(terrain);
        Tick(world, 5);
        Assert.Equal(9 * 9 - 3 * 3, lod.Count);   // nine sectors across in sight, three across simulated
        Assert.All(lod.Sectors, far => Assert.NotNull(far.Heights));

        // The far ring's steady cost: a tick with it against a tick without it.
        double withFar = Median(world, 60);
        Assert.True(engine.CVars.Find("stream_far_radius")!.TrySet("0", out _));
        Tick(world, 5);
        double without = Median(world, 60);
        Assert.True(engine.CVars.Find("stream_far_radius")!.TrySet("4", out _));
        Tick(world, 1);
        lod.Finish();
        Tick(world, 5);

        // Walking three sectors east at 20 m a tick. Between ticks the jobs get the time a walk would give
        // them; the main thread generates nothing and makes at most one sector live a tick.
        int generatedHere = terrain.GeneratedHere;
        var ticks = new List<double>();
        var loads = new List<int>();
        int mostChunks = 0;
        for (float x = 512; x < 3 * Sector + 512; x += 20f)
        {
            world.Get<Transform>(player).LocalPosition = world.Origin().ToOrigin(new Vector3(x, 0, 512));
            int owned = world.Query<SectorOwned>().Count;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            world.RunFixed(1f / 60f);
            ticks.Add(watch.Elapsed.TotalMilliseconds);
            loads.Add(ring.LoadedLastTick);
            mostChunks = Math.Max(mostChunks, world.Query<SectorOwned>().Count - owned);   // collision chunks built
            WaitAhead(terrain);
        }
        Tick(world, 5);

        string report = $"far ring steady tick {withFar:F2} ms (without {without:F2} ms); walking: max tick {ticks.Max():F1} ms, " +
                        $"median {Median(ticks):F2} ms, {loads.Sum()} sectors made live, generated here {terrain.GeneratedHere - generatedHere}, " +
                        $"on jobs {terrain.GeneratedOnJobs}";
        _output.WriteLine(report);
        Xunit.Assert.True(terrain.GeneratedHere == generatedHere, report);
        Xunit.Assert.True(loads.Max() <= 1, report);
        Xunit.Assert.InRange(mostChunks, 1, TerrainCollisionSystem.ChunksPerTick);   // 4 of a sector's 16, not 48 at once
        Xunit.Assert.Equal(9, loads.Sum());
        Xunit.Assert.True(ticks.Max() < 1000, report);                  // generous: a stall would be seconds
        Xunit.Assert.True(withFar <= without * 3 + 2, report);          // the far ring is not a per-tick cost
        Xunit.Assert.Equal(new SectorCoord(3, 0), world.Origin().SectorOf(world.Get<Transform>(player).LocalPosition));
        Xunit.Assert.All(lod.Sectors, far => Xunit.Assert.DoesNotContain(far.Coord, ring.Live));
    }

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static void WaitAhead(Terrain terrain)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!terrain.AheadDone && watch.ElapsedMilliseconds < 10_000) System.Threading.Thread.Sleep(1);
    }

    private static double Median(World world, int ticks)
    {
        var times = new List<double>();
        for (int i = 0; i < ticks; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            world.RunFixed(1f / 60f);
            times.Add(watch.Elapsed.TotalMilliseconds);
        }
        return Median(times);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted[sorted.Count / 2];
    }
}
