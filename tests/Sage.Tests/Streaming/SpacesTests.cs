#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Interiors as spaces of their own (phase 4m issue 4m-17, #291): a `live` scene the player leaves stays placed
// and simulated beside the next, so two interiors — and the exterior, while the player is inside — are live
// at once, each lit as its own space; going back takes it up as it is. A save keeps the held spaces.
public class SpacesTests
{
    public SpacesTests() { _ = TestEnv.UserRoot; }

    // A streamed village on flat ground with two doors; a crypt five kilometres east and a tower nine, each an
    // interior on a slab with a crate dropped from ten metres, so what falls there shows whether it is live.
    private const string Content = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "effect", "id": "hurt", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "terrain", "id": "plain", "generator": "Flat", "height": 0 },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"], "parts": { "character": { "layer": "player" } } },
      { "type": "prefab", "id": "marker", "name": "marker" },
      { "type": "prefab", "id": "goblin", "name": "goblin", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "skeleton", "name": "skeleton", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "slab", "name": "slab", "parts": { "body": { "size": [40, 1, 40] } } },
      { "type": "prefab", "id": "crate", "name": "crate", "parts": { "body": { "size": [1, 1, 1], "mass": 10 } } },
      { "type": "scene", "id": "village", "streamed": true, "live": true, "terrain": "plain", "relativeTo": "Ground",
        "player": { "prefab": "hero", "at": [0, 0.05, 0] },
        "place": [ { "prefab": "marker", "at": [0, 0.05, 1.5], "yaw": 180, "name": "crypt_door_out" },
                   { "prefab": "goblin", "at": [30, 0, 30], "name": "home", "id": "home" } ] },
      { "type": "scene", "id": "crypt", "space": "interior", "live": true, "environment": { "ambient": [0.1, 0.1, 0.12] },
        "place": [ { "prefab": "slab", "at": [5000, -0.5, 0] },
                   { "prefab": "marker", "at": [5000, 0.05, 2], "yaw": 90, "name": "crypt_in" },
                   { "prefab": "skeleton", "at": [5005, 0, -5], "name": "bones", "id": "bones" },
                   { "prefab": "crate", "at": [5010, 10, 10], "name": "crypt_crate", "id": "crypt_crate" } ] },
      { "type": "scene", "id": "tower", "space": "interior", "live": true, "environment": { "ambient": [0.3, 0.2, 0.1] },
        "place": [ { "prefab": "slab", "at": [9000, -0.5, 0] },
                   { "prefab": "marker", "at": [9000, 0.05, 2], "name": "tower_in" },
                   { "prefab": "skeleton", "at": [9005, 0, -5], "name": "warden", "id": "warden" },
                   { "prefab": "crate", "at": [9010, 10, 10], "name": "tower_crate", "id": "tower_crate" } ] }
    ]
    """;

    private static MountFixture Files(string content = Content)
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", content);
        files.Mount("game", "game");
        return files;
    }

    private static HeadlessApp Run(MountFixture files, string saves)
    {
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .Mount(files).StartScene("game:village").Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = saves;
        app.CVars.Execute("save_autosave 0");
        WorldClock.Of(app.World).Scale = 0;
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

    private static Vector3 Absolute(World world, Entity entity) => world.Origin().ToAbsolute(world.Get<Transform>(entity).LocalPosition);

    private static RecordId SceneOf(World world) => Scenes.Current(world);

    private static void Go(World world, string scene, string entry)
    {
        Assert.True(Travel.To(world, Game(scene), entry));
        Tick(world);
        Assert.Equal(Game(scene), SceneOf(world));
    }

    private static void AssertNoIdTwice(World world)
    {
        var ids = world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float within = 0.05f) =>
        Assert.True(Vector3.Distance(expected, actual) <= within, $"expected {expected}, was {actual}");

    // The done criterion: the crypt stays live when the player goes back out, the tower is entered, and then
    // the crypt, the tower's way back and the village are live together — the crate in the crypt falls while
    // the player is in the tower, the village's ground and goblin stay — and going back into the crypt takes
    // it up as it is: the same skeleton, nothing placed twice, its own light.
    [Xunit.Fact]
    public void TwoInteriorsAreLiveAtOnceBesideTheirExterior()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        var terrain = world.Resources.Get<Terrain>();
        var environment = world.Resources.Get<RenderEnvironment>();
        var sun = environment.SunColor;

        Go(world, "crypt", "crypt_in");
        var bones = One(world, "bones");
        var crate = One(world, "crypt_crate");
        Assert.NotEmpty(terrain.Sectors);                                  // the village is held: its ground stays
        Assert.Equal(new[] { Game("village") }, Scenes.LiveBeside(world));
        var home = One(world, "home");                                     // and its goblin is live
        Assert.True(world.Resources.Get<SectorRing>().Suspended);          // the ring no longer follows the player
        Assert.Equal(new Vector3(0.1f, 0.1f, 0.12f), environment.AmbientSky);

        Go(world, "village", "crypt_door_out");
        Assert.Equal(new[] { Game("crypt") }, Scenes.LiveBeside(world));
        Assert.Equal(bones, One(world, "bones"));                          // the crypt is still in the world
        Assert.Equal(home, One(world, "home"));                            // and the village was never placed again
        Assert.Equal(sun, environment.SunColor);                           // under the sky again
        float before = Absolute(world, crate).Y;
        Tick(world, 20);
        Assert.True(Absolute(world, crate).Y < before - 0.5f, $"the crypt's crate fell from {before} to {Absolute(world, crate).Y}");

        Go(world, "tower", "tower_in");
        Assert.Equal(new[] { Game("crypt"), Game("village") }, Scenes.LiveBeside(world));
        One(world, "warden");
        Assert.Equal(bones, One(world, "bones"));                          // two interiors live at once
        Assert.Equal(home, One(world, "home"));                            // beside their exterior
        Assert.NotEmpty(terrain.Sectors);
        Assert.Equal(new Vector3(0.3f, 0.2f, 0.1f), environment.AmbientSky);   // each space has its own light
        Assert.Equal(Vector3.Zero, environment.SunColor);
        before = Absolute(world, crate).Y;
        float towerBefore = Absolute(world, One(world, "tower_crate")).Y;
        Tick(world, 20);
        Assert.True(Absolute(world, crate).Y < before - 0.5f, "the crypt is simulated while the player is in the tower");
        Assert.True(Absolute(world, One(world, "tower_crate")).Y < towerBefore - 0.5f);

        Go(world, "crypt", "crypt_in");
        Assert.Equal(new[] { Game("village"), Game("tower") }, Scenes.LiveBeside(world));
        Assert.Equal(bones, One(world, "bones"));
        Assert.Equal(new Vector3(0.1f, 0.1f, 0.12f), environment.AmbientSky);
        Tick(world, 120);                                                  // the crate has landed on its slab
        AssertNear(new Vector3(5010, 0.5f, 10), Absolute(world, crate), 0.1f);
        AssertNoIdTwice(world);

        // The tower's things are its own still: leaving the village behind for good puts nothing twice.
        Go(world, "village", "crypt_door_out");
        Assert.Equal(new[] { Game("tower"), Game("crypt") }, Scenes.LiveBeside(world));
        Assert.Equal(home, One(world, "home"));
        AssertNoIdTwice(world);
    }

    // A save in the tower keeps the crypt live beside it: the load places it again with its state (a wounded
    // skeleton is wounded still, a fallen crate where it fell), and going there finds it once. The village, a
    // streamed exterior, is dormant after the load until the player goes back, as streaming places it.
    [Xunit.Fact]
    public void ASaveKeepsTheSpacesHeldLive()
    {
        string saves = TestEnv.NewTempDir();
        using (var app = Run(Files(), saves))
        {
            var world = app.World;
            Effects.Apply(world, One(world, "home"), Game("hurt"), One(world, "home"), 30f);
            Go(world, "crypt", "crypt_in");
            Effects.Apply(world, One(world, "bones"), Game("hurt"), One(world, "bones"), 40f);
            Tick(world, 120);
            Go(world, "village", "crypt_door_out");
            Go(world, "tower", "tower_in");
            Assert.Equal(new[] { Game("crypt"), Game("village") }, Scenes.LiveBeside(world));
            Assert.True(app.Engine.Saves.Save("tower"));
        }

        using (var app = Run(Files(), saves))
        {
            var world = app.World;
            Assert.True(app.Engine.Saves.Load("tower"));
            Tick(world);
            Assert.Equal(Game("tower"), SceneOf(world));
            Assert.Equal(new[] { Game("crypt") }, Scenes.LiveBeside(world));
            Assert.Equal(60f, world.Attribute(One(world, "bones"), Game("health")));   // live beside, with its state
            AssertNear(new Vector3(5010, 0.5f, 10), Absolute(world, One(world, "crypt_crate")), 0.1f);
            Assert.Empty(Named(world, "home"));
            One(world, "warden");
            AssertNoIdTwice(world);

            var bones = One(world, "bones");
            Go(world, "crypt", "crypt_in");
            Assert.Equal(bones, One(world, "bones"));
            Go(world, "village", "crypt_door_out");
            Tick(world, 3);
            Assert.Equal(70f, world.Attribute(One(world, "home"), Game("health")));
            Assert.Equal(new[] { Game("tower"), Game("crypt") }, Scenes.LiveBeside(world));
            AssertNoIdTwice(world);
        }
    }

    // Something made among a held space's things belongs to that space's cell, so it goes and comes with it;
    // made in the player's scene, it is the player's scene's.
    [Xunit.Fact]
    public void ARuntimeSpawnAmongAHeldSpaceIsThatSpaces()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        Go(world, "crypt", "crypt_in");
        Go(world, "village", "crypt_door_out");

        var there = world.Spawn(Game("marker"), world.Origin().ToOrigin(new Vector3(5004, 0, -4)));
        var here = world.Spawn(Game("marker"), world.Origin().ToOrigin(new Vector3(4, 0, 4)));
        world.FlushCommands();
        Assert.Equal(ContentIds.SceneSource(Game("crypt")), world.Get<InCell>(there).Source);
        Assert.StartsWith(ContentIds.SectorPrefix, world.Get<InCell>(here).Source);
    }

    // Each space falls at its own gravity (`environment.gravityScale`), live at once: the crypt's crate floats
    // with none whether the player is in the crypt or not, and the tower's falls at a quarter of the world's
    // while the village, where the player was, has the world's.
    [Xunit.Fact]
    public void EachSpaceFallsAtItsOwnGravity()
    {
        string content = Content
            .Replace(""""ambient": [0.1, 0.1, 0.12] }"""", """"ambient": [0.1, 0.1, 0.12], "gravityScale": 0 }"""")
            .Replace(""""ambient": [0.3, 0.2, 0.1] }"""", """"ambient": [0.3, 0.2, 0.1], "gravityScale": 0.25 }"""");
        using var app = Run(Files(content), TestEnv.NewTempDir());
        var world = app.World;
        Go(world, "crypt", "crypt_in");
        var crate = One(world, "crypt_crate");
        Tick(world, 30);
        AssertNear(new Vector3(5010, 10, 10), Absolute(world, crate), 0.01f);
        Assert.Equal(0f, SpaceGravity.Of(world).Scale);

        Go(world, "village", "crypt_door_out");
        Assert.Equal(1f, SpaceGravity.Of(world).Scale);
        Tick(world, 30);
        AssertNear(new Vector3(5010, 10, 10), Absolute(world, crate), 0.01f);   // held, and without gravity still

        Go(world, "tower", "tower_in");
        var towerCrate = One(world, "tower_crate");
        float before = Absolute(world, towerCrate).Y;
        Tick(world, 30);
        float fell = before - Absolute(world, towerCrate).Y;                    // ½·g/4·t², t = 0.5 s: 0.31 m
        Assert.InRange(fell, 0.2f, 0.45f);
        AssertNear(new Vector3(5010, 10, 10), Absolute(world, crate), 0.01f);
        Assert.Equal(0f, SpaceGravity.Of(world).ScaleAt(world, world.Origin().ToOrigin(new Vector3(5010, 10, 10))));
    }

    // `space_live_max` bounds how many are held: the one left longest ago goes dormant (and comes back from
    // its cell when visited); 0 is 4g-5's swap, every scene left asleep.
    [Xunit.Fact]
    public void HowManySpacesStayLiveIsBounded()
    {
        using (var app = Run(Files(), TestEnv.NewTempDir()))
        {
            var world = app.World;
            app.CVars.Execute("space_live_max 1");
            Go(world, "crypt", "crypt_in");
            Go(world, "village", "crypt_door_out");
            Go(world, "tower", "tower_in");
            Assert.Equal(new[] { Game("village") }, Scenes.LiveBeside(world));   // the crypt went to sleep
            Assert.Empty(Named(world, "bones"));
            Assert.True(ContentIds.Baseline(world).IsDormant(ContentIds.SceneSource(Game("crypt"))));
            Go(world, "crypt", "crypt_in");
            One(world, "bones");
            AssertNoIdTwice(world);
        }

        using (var app = Run(Files(), TestEnv.NewTempDir()))
        {
            var world = app.World;
            app.CVars.Execute("space_live_max 0");
            Go(world, "crypt", "crypt_in");
            Assert.Empty(Scenes.LiveBeside(world));
            Assert.Empty(world.Resources.Get<Terrain>().Sectors);             // as before 4m-17
            Assert.Empty(Named(world, "home"));
            Go(world, "village", "crypt_door_out");
            Assert.Empty(Named(world, "bones"));
        }
    }
}
