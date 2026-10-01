#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Entities stream by sector (phase 4g issue 4g-3, the 4g plan's decisions 2 and 5). A scene with
// `"streamed": true` is placed sector by sector as the player comes near, at a tick boundary and within a
// budget, and a sector the player leaves goes dormant with its state (4g-1's cells) — the wounded stay
// wounded, the dead stay dead and what was dropped stays where it fell, across a save and a load too.
public class StreamedSceneTests
{
    public StreamedSceneTests() { _ = TestEnv.UserRoot; }

    private const float Sector = Terrain.SectorSize;

    // The valley: the player's sector (0, 0) has a goblin and two stones; (2, 0) a goblin "east"; (5, 0) a
    // goblin "far". No terrain: the rings run without a generator.
    private const string Content = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "effect", "id": "hurt", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "item", "id": "arrow", "label": "an arrow", "weight": 0.1 },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"], "parts": { "inventory": { "capacity": 10 } } },
      { "type": "prefab", "id": "goblin", "name": "goblin", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "stone", "name": "stone" },
      { "type": "scene", "id": "valley", "streamed": true,
        "player": { "prefab": "hero", "at": [20, 1, 20] },
        "place": [ { "prefab": "goblin", "at": [30, 0, 30], "name": "home", "id": "home" },
                   { "prefab": "stone", "at": [40, 0, 40], "name": "cairn" },
                   { "prefab": "stone", "at": [60, 0, 60], "name": "cairn" },
                   { "prefab": "goblin", "at": [2078, 0, 30], "name": "east", "id": "east" },
                   { "prefab": "goblin", "at": [5150, 0, 30], "name": "far" } ] }
    ]
    """;

    private static MountFixture Files(string content = Content)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", content);
        files.Mount("game", "game");
        return files;
    }

    private static HeadlessApp Run(MountFixture files, string saves, string scene = "game:valley")
    {
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .Mount(files).StartScene(scene).Boot();
        app.Engine.Saves.Root = saves;
        app.CVars.Execute("save_autosave 0");
        Tick(app.World);
        return app;
    }

    private static void Tick(World world, int ticks = 1)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static RecordId Game(string name) => new("game", name);

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Entity One(World world, string name) => Assert.Single(Named(world, name));

    private static Entity Hero(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    private static int Placed(World world) => world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Count;

    private static Vector3 Absolute(World world, Entity entity) => world.Origin().ToAbsolute(world.Get<Transform>(entity).LocalPosition);

    // The player to absolute metres (x, z), and a tick: the ring follows in Late, the sectors at its end.
    private static void Walk(World world, float x, float z)
    {
        world.Get<Transform>(Hero(world)).LocalPosition = world.Origin().ToOrigin(new Vector3(x, 1, z));
        Tick(world);
    }

    private static void AssertNoIdTwice(World world)
    {
        var ids = world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    private static string SectorSource(int x, int z) => ContentIds.SectorSource(Game("valley"), new SectorCoord(x, z));

    // The plan's first test: walk three sectors east, and what is behind goes dormant (the count drops) and
    // comes back with its state when the player returns.
    [Xunit.Fact]
    public void WalkingThreeSectorsEastPutsWhatIsBehindToSleepAndBringsItBack()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;

        // The ring around (0, 0) is placed: its three, and nothing from (2, 0) or (5, 0).
        Assert.Equal(3, Placed(world));
        Assert.Empty(Named(world, "east"));
        var home = One(world, "home");
        Assert.Equal(PersistentId.FromName("scene:game:valley:id:home"), home.GetComponent<Persistent>().Id);   // 4i's formula
        Assert.True(ContentIds.Baseline(world).IsLive(SectorSource(0, 0)));
        Effects.Apply(world, home, Game("hurt"), home, 12f);
        world.FlushCommands();

        Walk(world, 3 * Sector + 20, 20);
        Assert.Equal(new SectorCoord(3, 0), world.Origin().Sector);
        Assert.Empty(Named(world, "home"));           // asleep with (0, 0)
        Assert.Empty(Named(world, "cairn"));
        Assert.Equal(1, Placed(world));               // only "east" in (2, 0) is out now
        One(world, "east");
        Assert.True(ContentIds.Baseline(world).IsDormant(SectorSource(0, 0)));

        Walk(world, 20, 20);
        Assert.Equal(88f, world.Attribute(One(world, "home"), Game("health")));
        Assert.Equal(2, Named(world, "cairn").Length);
        Assert.Equal(new Vector3(30, 0, 30), Absolute(world, One(world, "home")));
        AssertNoIdTwice(world);
    }

    // A goblin killed in (2, 0) stays dead when the sector unloads and loads again, and across a save and a
    // load, while the one in (0, 0) is untouched.
    [Xunit.Fact]
    public void AGoblinKilledInSectorTwoStaysDeadAcrossUnloadReloadSaveAndLoad()
    {
        string saves = TestEnv.NewTempDir();
        using (var app = Run(Files(), saves))
        {
            var world = app.World;
            Walk(world, 2 * Sector + 20, 20);
            world.Destroy(One(world, "east"));
            world.FlushCommands();

            Walk(world, 5 * Sector + 20, 20);   // (2, 0) is three sectors behind: dormant
            One(world, "far");
            Walk(world, 2 * Sector + 20, 20);
            Assert.Empty(Named(world, "east"));
            Assert.True(app.Engine.Saves.Save("killed"));

            var file = JsonNode.Parse(File.ReadAllText(Path.Combine(saves, "killed", "world_main.json")))!;
            var dead = file["tombstones"]![SectorSource(2, 0)]!.AsArray().Select(n => (string?)n).ToList();
            Assert.Contains(PersistentId.FromName("scene:game:valley:id:east").ToString(), dead);
        }

        using (var app = Run(Files(), saves))
        {
            var world = app.World;
            Assert.True(app.Engine.Saves.Load("killed"));
            Tick(world);
            Assert.Empty(Named(world, "east"));
            Walk(world, 20, 20);
            One(world, "home");
            Walk(world, 5 * Sector + 20, 20);
            Walk(world, 2 * Sector + 20, 20);
            Assert.Empty(Named(world, "east"));
            AssertNoIdTwice(world);
        }
    }

    // An arrow left in a sector the player walks away from sleeps with it, through a save and a load, and is
    // where it fell when the player comes back.
    [Xunit.Fact]
    public void AnArrowLeftInAnUnloadedSectorSurvives()
    {
        string saves = TestEnv.NewTempDir();
        PersistentId arrow;
        using (var app = Run(Files(), saves))
        {
            var world = app.World;
            Walk(world, 2 * Sector + 20, 20);
            var dropped = world.SpawnPickup(Game("arrow"), 3, world.Origin().ToOrigin(new Vector3(2 * Sector + 50, 0, 70)));
            world.FlushCommands();
            arrow = dropped.GetComponent<Persistent>().Id;
            Assert.Equal(SectorSource(2, 0), world.Get<InCell>(dropped).Source);   // the sector it was made in

            Walk(world, 5 * Sector + 20, 20);
            Assert.True(world.Resolve(arrow).IsNull);
            Assert.Empty(world.Query<Pickup>().Entities.ToEntityList());
            Assert.True(app.Engine.Saves.Save("arrow"));
        }

        using (var app = Run(Files(), saves))
        {
            var world = app.World;
            Assert.True(app.Engine.Saves.Load("arrow"));
            Tick(world);
            Assert.True(world.Resolve(arrow).IsNull);
            Walk(world, 2 * Sector + 20, 20);
            var back = world.Resolve(arrow);
            Assert.False(back.IsNull);
            Assert.Equal(3, world.Get<Pickup>(back).Count);
            Assert.Equal(new Vector3(2 * Sector + 50, 0, 70), Absolute(world, back));
            Assert.Single(world.Query<Pickup>().Entities.ToEntityList());
        }
    }

    // A root that crosses a sector edge belongs to the sector it is in now: a placed goblin leaves (0, 0)
    // for (1, 0) — tombstoned in (0, 0), so it is never placed twice — and sleeps and wakes with (1, 0); an
    // arrow's cell follows it.
    [Xunit.Fact]
    public void ARootThatCrossesASectorEdgeChangesOwner()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        var home = One(world, "home");
        var id = home.GetComponent<Persistent>().Id;
        var arrow = world.SpawnPickup(Game("arrow"), 1, new Vector3(50, 0, 50));
        world.FlushCommands();

        world.Get<Transform>(home).LocalPosition = new Vector3(Sector + 30, 0, 30);
        world.Get<Transform>(arrow).LocalPosition = new Vector3(Sector + 40, 0, 40);
        Tick(world);
        var baseline = ContentIds.Baseline(world);
        Assert.Equal(SectorSource(1, 0), world.Get<InCell>(home).Source);
        Assert.False(baseline.TryGetSource(id, out _));                  // no sector's content any more
        Assert.True(baseline.IsTombstoned(SectorSource(0, 0), id));     // and (0, 0) will not place it again
        Assert.Equal(SectorSource(1, 0), world.Get<InCell>(arrow).Source);

        // (0, 0) sleeps and (1, 0) stays: the goblin is still here.
        Walk(world, 3 * Sector + 20, 20);
        Assert.Equal(id, One(world, "home").GetComponent<Persistent>().Id);

        // Both asleep, then both back: one goblin, where it walked to.
        Walk(world, 6 * Sector + 20, 20);
        Assert.Empty(Named(world, "home"));
        Walk(world, 20, 20);
        Assert.Equal(new Vector3(Sector + 30, 0, 30), Absolute(world, One(world, "home")));
        Assert.Equal(id, One(world, "home").GetComponent<Persistent>().Id);
        AssertNoIdTwice(world);
    }

    // Placing happens at the tick boundary, within `stream_place_budget` entities a tick: a sector of ten
    // stones arrives over four ticks with a budget of three, and is finished (its source live) only then.
    [Xunit.Fact]
    public void PlacingASectorIsBudgetedAtTheTickBoundary()
    {
        var stones = string.Join(",\n", Enumerable.Range(0, 10).Select(i => $$"""{ "prefab": "stone", "at": [{{10 + i}}, 0, 10], "name": "stone" }"""));
        string content = Content.Replace("""{ "type": "scene", "id": "valley",""", $$"""
              { "type": "scene", "id": "quarry", "streamed": true, "player": { "prefab": "hero", "at": [5, 1, 5] }, "place": [ {{stones}} ] },
              { "type": "scene", "id": "valley",
            """);
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .Mount(Files(content)).StartScene("game:quarry").Boot();
        var world = app.World;
        app.CVars.Execute("stream_place_budget 3");
        Assert.Empty(Named(world, "stone"));   // nothing until the ring is known and a tick has ended

        var counts = new List<int>();
        for (int i = 0; i < 4; i++)
        {
            Tick(world);
            counts.Add(Named(world, "stone").Length);
        }
        Assert.Equal(new[] { 3, 6, 9, 10 }, counts);
        Assert.True(ContentIds.Baseline(world).IsLive(ContentIds.SectorSource(Game("quarry"), new SectorCoord(0, 0))));
    }

    // The data-only world: a `terrain` record with the built-in hills, a streamed scene that names it, and
    // no C#. The ground is generated around the player, a Ground-relative placement stands on it, and the
    // hills walk on with the player.
    [Xunit.Fact]
    public void TheDataOnlyWorldStreams()
    {
        const string data = """
        [
          { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
          { "type": "prefab", "id": "stone", "name": "stone" },
          { "type": "terrain", "id": "downs", "generator": "Hills", "seed": 7, "height": 10, "amplitude": 30, "wavelength": 300 },
          { "type": "scene", "id": "downs", "streamed": true, "terrain": "downs", "relativeTo": "Ground",
            "player": { "prefab": "hero", "at": [20, 2, 20] },
            "place": [ { "prefab": "stone", "at": [100, 0, 140], "name": "menhir" },
                       { "prefab": "stone", "at": [2148, 0, 100], "name": "barrow" } ] }
        ]
        """;
        using var app = Run(Files(data), TestEnv.NewTempDir(), "game:downs");
        var world = app.World;
        var terrain = world.Resources.Get<Terrain>();

        Assert.IsType<BuiltInTerrain.HillsGenerator>(terrain.Generator);
        Assert.Equal(7, terrain.Seed);
        Assert.Equal(9, terrain.Sectors.Count);
        var menhir = One(world, "menhir");
        var at = world.Get<Transform>(menhir).LocalPosition;
        Assert.Equal(terrain.HeightAt(at.X, at.Z), at.Y, 3);
        Assert.NotEqual(10f, at.Y);   // hills, not a plane at their middle

        Walk(world, 2 * Sector + 20, 20);
        Assert.True(terrain.IsLoaded(new SectorCoord(3, 0)));
        One(world, "barrow");
        Walk(world, 4 * Sector + 20, 20);
        Assert.Empty(Named(world, "menhir"));
    }

    // The built-in generators are functions of absolute position: neighbouring sectors meet without a step,
    // and the same seed is the same ground. Flat is a plane at its height.
    [Xunit.Fact]
    public void BuiltInTerrainIsSeamlessAndSeeded()
    {
        var hills = BuiltInTerrain.Create(new TerrainRecord { Generator = TerrainGeneratorKind.Hills, Amplitude = 40, Wavelength = 200 });
        Heightfield Make(int x, int seed)
        {
            var field = new Heightfield(Terrain.SectorResolution, Sector);
            hills.Generate(new SectorCoord(x, 0), field, seed);
            return field;
        }
        var west = Make(0, 3);
        var east = Make(1, 3);
        for (int z = 0; z < west.Resolution; z += 16)
            Assert.Equal(west[west.Resolution - 1, z], east[0, z], 3);
        Assert.Equal(west.Heights, Make(0, 3).Heights);
        Assert.NotEqual(west.Heights, Make(0, 4).Heights);
        Assert.InRange(west.Heights.Max() - west.Heights.Min(), 5f, 80f);

        var flat = new Heightfield(Terrain.SectorResolution, Sector);
        BuiltInTerrain.Create(new TerrainRecord { Height = 4 }).Generate(new SectorCoord(9, -2), flat, 0);
        Assert.All(flat.Heights, h => Assert.Equal(4f, h));
    }
}

// Placing stays out of the fixed schedule's allocation-free systems: a streamed scene at rest, the ring
// known and every sector placed, ticks without allocating — the owner check, the ring and the boundary
// included.
[Xunit.Collection(MeasurementsCollection.Name)]
public class StreamedSceneMeasurements
{
    public StreamedSceneMeasurements() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void AStreamedSceneAtRestAllocatesNothingPerTick()
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", """
        [
          { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
          { "type": "prefab", "id": "stone", "name": "stone" },
          { "type": "scene", "id": "field", "streamed": true, "player": { "prefab": "hero", "at": [20, 1, 20] },
            "place": [ { "prefab": "stone", "at": [30, 0, 30] }, { "prefab": "stone", "at": [1100, 0, 30] } ] }
        ]
        """);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().With(new StreamingModule()).Mount(files).StartScene("game:field").Boot();
        app.CVars.Execute("save_autosave 0");
        var world = app.World;
        void Step()
        {
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
        }
        for (int i = 0; i < 60; i++) Step();
        Assert.Equal(2, world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Count);
        AllocationProbe.AssertNone(300, Step);
    }
}
