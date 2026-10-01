#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Load doors, interiors and fast travel (phase 4g issue 4g-5). A door's Use takes the player into an
// interior scene: the exterior goes dormant with its state, the rings stop and the terrain unloads, and the
// crypt is lit by its lights; coming back restores it all around the door. Fast travel goes to a discovered
// travel point in warp's order, and the journey passes time.
public class TravelTests
{
    public TravelTests() { _ = TestEnv.UserRoot; }

    private const float Sector = Terrain.SectorSize;

    // The village streams on flat ground; the crypt is an interior five kilometres east, on a slab.
    private const string Content = """
    [
      { "type": "attribute", "id": "health", "max": 100, "start": 100 },
      { "type": "effect", "id": "hurt", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
      { "type": "item", "id": "sword", "label": "a sword", "weight": 3 },
      { "type": "terrain", "id": "plain", "generator": "Flat", "height": 0 },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"], "parts": { "character": { "layer": "player" }, "inventory": { "capacity": 50 } } },
      { "type": "prefab", "id": "marker", "name": "marker" },
      { "type": "prefab", "id": "goblin", "name": "goblin", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "skeleton", "name": "skeleton", "parts": { "attributes": {} } },
      { "type": "prefab", "id": "slab", "name": "slab", "parts": { "body": { "size": [40, 1, 40] } } },
      { "type": "prefab", "id": "door", "name": "door",
        "parts": { "body": { "size": [1.5, 2.5, 0.2] }, "load_door": { "scene": "crypt", "entry": "crypt_in" } } },
      { "type": "prefab", "id": "way_out", "name": "way_out",
        "parts": { "body": { "size": [1.5, 2.5, 0.2] }, "load_door": { "scene": "village", "entry": "crypt_door_out" } } },
      { "type": "prefab", "id": "waystone", "name": "waystone", "parts": { "travel_point": { "label": "Old Mill", "radius": 25 } } },
      { "type": "scene", "id": "village", "streamed": true, "terrain": "plain", "relativeTo": "Ground",
        "player": { "prefab": "hero", "at": [0, 0.05, 0] },
        "place": [ { "prefab": "door", "at": [0, 1.25, -1.4], "name": "crypt_door" },
                   { "prefab": "marker", "at": [0, 0.05, 1.5], "yaw": 180, "name": "crypt_door_out" },
                   { "prefab": "goblin", "at": [30, 0, 30], "name": "home", "id": "home" },
                   { "prefab": "waystone", "at": [3000, 0, 520], "name": "mill" } ] },
      { "type": "scene", "id": "crypt", "space": "interior", "environment": { "ambient": [0.1, 0.1, 0.12] },
        "place": [ { "prefab": "slab", "at": [5000, -0.5, 0] },
                   { "prefab": "marker", "at": [5000, 0.05, 2], "yaw": 90, "name": "crypt_in" },
                   { "prefab": "way_out", "at": [5000, 1.25, 4], "name": "crypt_exit" },
                   { "prefab": "skeleton", "at": [5005, 0, -5], "name": "bones", "id": "bones" } ] }
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

    private static RecordId SceneOf(World world) => world.Resources.Get<ActiveScene>().Id;

    private static void AssertNoIdTwice(World world)
    {
        var ids = world.Query<Persistent>().Entities.ToEntityList().Select(e => e.GetComponent<Persistent>().Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float within = 0.05f) =>
        Assert.True(Vector3.Distance(expected, actual) <= within, $"expected {expected}, was {actual}");

    // The plan's first test: Use the door and you are at the crypt's entry, the village asleep, the terrain
    // unloaded and the rings stopped, and the crypt lit by its lights alone.
    [Xunit.Fact]
    public void UsingTheDoorPutsYouAtTheCryptEntryWithTerrainUnloaded()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        var terrain = world.Resources.Get<Terrain>();
        var environment = world.Resources.Get<RenderEnvironment>();
        var sun = environment.SunColor;
        Assert.Equal(9, terrain.Sectors.Count);
        One(world, "home");
        var player = Hero(world);
        world.Get<PawnIntent>(player).Yaw = 0f;   // facing -Z, the door

        world.Get<PawnIntent>(player).Pressed = new ActionMask().With(app.Engine.Actions.Get("Use"));
        Tick(world);
        world.Get<PawnIntent>(player).Pressed = default;

        Assert.Equal(Game("crypt"), SceneOf(world));
        Assert.Equal(player, Hero(world));                               // the same player, moved
        AssertNear(new Vector3(5000, 0.05f, 2), Absolute(world, player));
        Assert.Equal(90f, SageMath.YawOf(world.Get<Transform>(player).LocalRotation) * 180f / MathF.PI, 2);
        Assert.Equal(new SectorCoord(4, 0), world.Origin().Sector);        // the origin followed
        Assert.Empty(terrain.Sectors);
        Assert.Empty(Named(world, "home"));                               // the village sleeps
        Assert.True(ContentIds.Baseline(world).IsDormant(ContentIds.SectorSource(Game("village"), new SectorCoord(0, 0))));
        One(world, "bones");
        Assert.True(Interiors.Active(world));
        Assert.Equal(Vector3.Zero, environment.SunColor);                 // lights only
        Assert.False(environment.DrawSky);
        Assert.Equal(0f, environment.ShadowStrength);
        Assert.Equal(new Vector3(0.1f, 0.1f, 0.12f), environment.AmbientSky);

        Tick(world, 10);                                                  // and nothing streams back in
        Assert.Empty(terrain.Sectors);
        Assert.True(world.Resources.Get<SectorRing>().Suspended);
        Assert.Empty(world.Resources.Get<SectorRing>().Live);
        Assert.Equal(new SectorCoord(4, 0), world.Origin().Sector);
        Assert.NotEqual(sun, Vector3.Zero);
    }

    // Come back out and you are at the door with the exterior as you left it: the ground there before you
    // are, the wounded goblin still wounded, the light the village had. What you did in the crypt is still
    // there when you go back in.
    [Xunit.Fact]
    public void ComingBackYouAreAtTheDoorWithTheExteriorIntact()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        var terrain = world.Resources.Get<Terrain>();
        var environment = world.Resources.Get<RenderEnvironment>();
        var outside = (environment.SunColor, environment.AmbientSky, environment.DrawSky);
        Effects.Apply(world, One(world, "home"), Game("hurt"), One(world, "home"), 12f);
        world.FlushCommands();

        Assert.True(Travel.Use(world, One(world, "crypt_door"), Hero(world)));
        Assert.Equal(Game("crypt"), SceneOf(world));
        world.Destroy(One(world, "bones"));
        var sword = world.SpawnPickup(Game("sword"), 1, world.Origin().ToOrigin(new Vector3(5003, 0, 1)));
        world.FlushCommands();
        var swordId = sword.GetComponent<Persistent>().Id;
        Tick(world, 3);

        Assert.True(Travel.Use(world, One(world, "crypt_exit"), Hero(world)));
        Assert.Equal(Game("village"), SceneOf(world));
        var player = Hero(world);
        AssertNear(new Vector3(0, 0.05f, 1.5f), Absolute(world, player));
        Assert.Equal(new SectorCoord(0, 0), world.Origin().Sector);
        Assert.Equal(9, terrain.Sectors.Count);                           // ground first (warp's order)
        Assert.False(Interiors.Active(world));
        Assert.False(world.Resources.Get<SectorRing>().Suspended);
        Assert.Equal(outside, (environment.SunColor, environment.AmbientSky, environment.DrawSky));
        Assert.True(world.Resolve(swordId).IsNull);                        // asleep in the crypt

        Tick(world);                                                       // the ring, then its sectors
        Assert.Equal(88f, world.Attribute(One(world, "home"), Game("health")));
        One(world, "crypt_door");
        Assert.True(world.Resources.Get<SectorRing>().Ready);
        AssertNear(new Vector3(0, 0.05f, 1.5f), Absolute(world, Hero(world)), 0.1f);

        Assert.True(Travel.Use(world, One(world, "crypt_door"), Hero(world)));
        Assert.Empty(Named(world, "bones"));                               // still dead
        var back = world.Resolve(swordId);
        Assert.False(back.IsNull);                                         // still on the floor
        AssertNear(new Vector3(5003, 0, 1), Absolute(world, back));
        Assert.Single(world.Query<Pickup>().Entities.ToEntityList());
        AssertNoIdTwice(world);
    }

    // Saved in the crypt, loaded into a game that started in the village: in the crypt, the terrain gone,
    // the ring stopped, lit as an interior; and the way out still leads to the village as it was.
    [Xunit.Fact]
    public void SaveAndLoadInTheCrypt()
    {
        string saves = TestEnv.NewTempDir();
        using (var app = Run(Files(), saves))
        {
            var world = app.World;
            Effects.Apply(world, One(world, "home"), Game("hurt"), One(world, "home"), 30f);
            world.FlushCommands();
            Travel.Use(world, One(world, "crypt_door"), Hero(world));
            Tick(world, 2);
            Assert.True(app.Engine.Saves.Save("crypt"));
        }

        using (var app = Run(Files(), saves))
        {
            var world = app.World;
            Assert.Equal(9, world.Resources.Get<Terrain>().Sectors.Count);
            Assert.True(app.Engine.Saves.Load("crypt"));
            Tick(world);
            Assert.Equal(Game("crypt"), SceneOf(world));
            Assert.Empty(world.Resources.Get<Terrain>().Sectors);
            Assert.True(world.Resources.Get<SectorRing>().Suspended);
            Assert.True(Interiors.Active(world));
            Assert.Equal(Vector3.Zero, world.Resources.Get<RenderEnvironment>().SunColor);
            Assert.Empty(Named(world, "home"));
            One(world, "bones");
            AssertNear(new Vector3(5000, 0.05f, 2), Absolute(world, Hero(world)), 0.1f);

            Assert.True(Travel.Use(world, One(world, "crypt_exit"), Hero(world)));
            Tick(world);
            Assert.Equal(70f, world.Attribute(One(world, "home"), Game("health")));
            Assert.Equal(9, world.Resources.Get<Terrain>().Sectors.Count);
            AssertNoIdTwice(world);
        }
    }

    // A scene's `environment.hour` is the hour it starts at, not the hour it always is: coming back to the
    // village through the crypt's way out at 02:30 keeps the clock, where `scene_load` still sets it (4g-7).
    [Xunit.Fact]
    public void GoingThroughADoorKeepsTheClock()
    {
        string content = Content.Replace("""{ "type": "scene", "id": "village", "streamed": true,""",
                                         """{ "type": "scene", "id": "village", "streamed": true, "environment": { "hour": 18.5 },""");
        using var app = Run(Files(content), TestEnv.NewTempDir());
        var world = app.World;
        var clock = WorldClock.Of(world);
        Assert.Equal(18.5, clock.Hour, 3);                                 // the world started there

        Assert.True(Travel.Use(world, One(world, "crypt_door"), Hero(world)));
        Assert.True(Time.Pass(world, 8, "rest"));
        Assert.Equal(2.5, clock.Hour, 3);
        Assert.True(Travel.Use(world, One(world, "crypt_exit"), Hero(world)));
        Assert.Equal(Game("village"), SceneOf(world));
        Assert.Equal(2.5, clock.Hour, 3);                                  // not dusk again

        app.CVars.Execute("sv_cheats 1");
        Assert.True(app.CVars.Execute("scene_load village"));
        Assert.Equal(18.5, clock.Hour, 3);                                 // starting a scene still sets it
    }

    // An entry that is a map entity's `targetname` (a point with no prefab) is found as the player arrives:
    // the interior's level is placed and its entities spawned in the journey, not a tick later (4g-7, the
    // Sandbox's crypt).
    [Xunit.Fact]
    public void AnEntryInAMapIsFoundAsThePlayerArrives()
    {
        string content = Content.Replace("""{ "type": "scene", "id": "crypt",""", """
              { "type": "map", "id": "vault", "file": "maps/vault.map", "at": [6000, 0, 0] },
              { "type": "scene", "id": "vault", "space": "interior", "maps": ["vault"] },
              { "type": "scene", "id": "crypt",
            """);
        var files = Files(content);
        files.Write("game", "maps/vault.map", """
            { "classname" "worldspawn"
            { ( -256 -256 -16 ) ( -256 -255 -16 ) ( -256 -256 -15 ) stone 0 0 0 1 1
            ( -256 -256 -16 ) ( -256 -256 -15 ) ( -255 -256 -16 ) stone 0 0 0 1 1
            ( -256 -256 -16 ) ( -255 -256 -16 ) ( -256 -255 -16 ) stone 0 0 0 1 1
            ( 256 256 0 ) ( 256 257 0 ) ( 257 256 0 ) stone 0 0 0 1 1
            ( 256 256 0 ) ( 257 256 0 ) ( 256 256 1 ) stone 0 0 0 1 1
            ( 256 256 0 ) ( 256 256 1 ) ( 256 257 0 ) stone 0 0 0 1 1 } }
            { "classname" "info_target" "targetname" "vault_in" "origin" "64 -96 8" "angle" "90" }
            { "classname" "marker" "targetname" "vault_marker" "origin" "0 0 8" }
            """);
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule(), new MapModule()).WithGameplay()
            .Mount(files).StartScene("game:village").Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        Tick(world);

        Assert.True(Travel.To(world, Game("vault"), "vault_in"));
        Assert.Equal(Game("vault"), SceneOf(world));
        AssertNear(new Vector3(6002, 0.25f, 3), Absolute(world, Hero(world)));   // Quake units, 32 to the metre
        Assert.Equal(0f, SageMath.YawOf(world.Get<Transform>(Hero(world)).LocalRotation) * 180f / MathF.PI, 2);
        One(world, "vault_marker");                                       // spawned with the journey
    }

    // A door used during a tick takes the player through at its end, like a save: no system later in the
    // tick sees the scene change under it, and the hours it costs pass with it.
    [Xunit.Fact]
    public void TravelRunsAtTheTickBoundary()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Hour = 7;
        var door = new UseDuringTick(Game("crypt"), "crypt_in", 2);
        var later = new SceneInLate();
        world.AddSystem(door, Phase.Commands);
        world.AddSystem(later, Phase.Late);

        Tick(world);
        Assert.Equal(Game("village"), door.SceneAfterAsking);
        Assert.Equal(Game("village"), later.Scene);                        // not even later in the tick
        Assert.Equal(Game("crypt"), SceneOf(world));                       // and then, at its end
        Assert.Equal(9, clock.Hour, 6);
        Tick(world);
        Assert.Equal(Game("crypt"), later.Scene);
    }

    // Entity I/O: `Travel` sent to a load door goes through it, and with "<scene> <entry>" sent to anything
    // goes there.
    [Xunit.Fact]
    public void TheTravelInputGoesThroughADoorOrToAnEntry()
    {
        using var app = Run(Files(), TestEnv.NewTempDir());
        var world = app.World;
        Assert.True(app.Engine.CVars.Execute("ent_fire crypt_door Travel", ExecSource.Code));
        Tick(world, 2);
        Assert.Equal(Game("crypt"), SceneOf(world));
        AssertNear(new Vector3(5000, 0.05f, 2), Absolute(world, Hero(world)), 0.1f);

        Assert.True(app.Engine.CVars.Execute("ent_fire bones Travel \"village crypt_door_out\"", ExecSource.Code));
        Tick(world, 2);
        Assert.Equal(Game("village"), SceneOf(world));
        AssertNear(new Vector3(0, 0.05f, 1.5f), Absolute(world, Hero(world)), 0.1f);
    }

    private sealed class UseDuringTick(RecordId scene, string entry, double hours) : ISystem
    {
        public RecordId SceneAfterAsking;
        private bool _asked;

        public void Run(in SystemContext ctx)
        {
            if (_asked) return;
            _asked = true;
            Assert.True(Travel.To(ctx.World, scene, entry, hours));
            Assert.False(Travel.To(ctx.World, scene, entry, hours));      // one journey a tick
            SceneAfterAsking = SceneOf(ctx.World);
        }
    }

    private sealed class SceneInLate : ISystem
    {
        public RecordId Scene;
        public void Run(in SystemContext ctx) => Scene = SceneOf(ctx.World);
    }

    // Fast travel: walking near the waystone discovers it (and the save keeps it); `travel Old Mill` from
    // the village costs the distance over `travel_speed` in game hours, and lands on the ground there, in
    // warp's order, with the ground generated before the player stands on it.
    [Xunit.Fact]
    public void FastTravelAdvancesTheClockAndLandsOnTheGround()
    {
        string content = Content
            .Replace("""{ "type": "terrain", "id": "plain", "generator": "Flat", "height": 0 }""",
                     """{ "type": "terrain", "id": "plain", "generator": "Hills", "seed": 3, "height": 10, "amplitude": 20, "wavelength": 300 }""")
            .Replace("\"character\": { \"layer\": \"player\" }, ", "");
        string saves = TestEnv.NewTempDir();
        using (var app = Run(Files(content), saves))
        {
            var world = app.World;
            Assert.False(Travel.ToPoint(world, "Old Mill"));                 // not discovered yet
            var player = Hero(world);
            world.Get<Transform>(player).LocalPosition = world.Origin().ToOrigin(new Vector3(2990, 10, 515));
            Tick(world, 3);                                                // the ring, the sector, the discovery
            Assert.True(world.Get<TravelPoint>(One(world, "mill")).Discovered);
            var point = Assert.Single(TravelLog.Of(world).Points);
            Assert.Equal("Old Mill", point.Label);
            Assert.Equal(Game("village"), point.Scene);
            Assert.Equal(new Vector3(3000, 0, 520).X, point.At.X, 2);
            Assert.True(app.Engine.Saves.Save("found"));
        }

        using (var app = Run(Files(content), saves))
        {
            var world = app.World;
            Assert.True(app.Engine.Saves.Load("found"));
            Tick(world);
            var clock = WorldClock.Of(world);
            clock.Scale = 0;
            clock.Hour = 7;
            var player = Hero(world);
            world.Get<Transform>(player).LocalPosition = world.Origin().ToOrigin(new Vector3(0, 10, 0));
            Tick(world, 2);
            Assert.Equal(new SectorCoord(0, 0), world.Origin().Sector);
            var watch = new Watch(world);

            double expected = Vector2.Distance(Vector2.Zero, new Vector2(3000, 520)) / Travel.DefaultSpeed;
            Assert.Equal(expected, Travel.HoursTo(world, TravelLog.Of(world).Find("old mill")!), 3);
            app.CVars.Execute("travel Old Mill");

            Assert.Equal(7 + expected, clock.Hour, 3);
            Assert.Equal(new SectorCoord(2, 0), world.Origin().Sector);
            var terrain = world.Resources.Get<Terrain>();
            Assert.True(terrain.IsLoaded(new SectorCoord(2, 0)));            // the ground before the player
            var at = world.Get<Transform>(Hero(world)).LocalPosition;
            Assert.Equal(terrain.HeightAt(at.X, at.Z), at.Y, 3);              // on it
            var absolute = Absolute(world, Hero(world));
            Assert.Equal(3000f, absolute.X, 2);
            Assert.Equal(520f, absolute.Z, 2);

            Tick(world);
            var skip = Assert.Single(watch.Seen);
            Assert.Equal("travel", skip.Reason);
            Tick(world, 2);
            One(world, "mill");                                            // its sector streamed in around the player
            AssertNoIdTwice(world);
        }
    }

    private sealed class Watch : ISystem
    {
        public readonly List<TimePassed> Seen = new();
        private readonly EventReader<TimePassed> _reader;

        public Watch(World world)
        {
            _reader = world.Events.Reader<TimePassed>(this, Schedule.Fixed);
            world.AddSystem(this, Phase.Gameplay);
        }

        public void Run(in SystemContext ctx)
        {
            foreach (ref readonly var e in _reader.Read()) Seen.Add(e);
        }
    }

    // A door to a scene or an entry that does not exist is a load error, at the door's line, with the
    // nearest entry the scene has; `sage validate` reports it as an error too.
    [Xunit.Fact]
    public void ADoorToAMissingSceneOrEntryIsALoadError()
    {
        const string data = """
        [
          { "type": "prefab", "id": "marker", "name": "marker" },
          { "type": "prefab", "id": "door", "parts": { "load_door": { "scene": "crypt", "entry": "crypt_in" } } },
          { "type": "prefab", "id": "lost_door", "parts": { "load_door": { "scene": "nowhere", "entry": "here" } } },
          { "type": "prefab", "id": "blank_door", "parts": { "load_door": { "entry": "crypt_in" } } },
          { "type": "scene", "id": "crypt", "space": "interior",
            "place": [ { "prefab": "marker", "at": [0, 0, 0], "name": "crypt_in" } ] },
          { "type": "scene", "id": "village",
            "place": [ { "prefab": "door", "at": [0, 0, 0] },
                       { "prefab": "door", "at": [2, 0, 0], "overrides": { "parts": { "load_door": { "entry": "crypt_inn" } } } },
                       { "prefab": "blank_door", "at": [4, 0, 0] } ] }
        ]
        """;
        string game = TestEnv.NewTempDir();
        Directory.CreateDirectory(Path.Combine(game, "content", "data"));
        File.WriteAllText(Path.Combine(game, "game.json"),
            """{ "name": "Doors", "id": "doors", "plugins": [], "mounts": ["content"], "scene": "village" }""");
        File.WriteAllText(Path.Combine(game, "content", "data", "doors.json"), data);

        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = game,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.False(report.Ok);
        Assert.Contains(report.Errors, e => e.Contains("prefab doors:lost_door") && e.Contains("refers to scene doors:nowhere, which doesn't exist"));
        Assert.Contains(report.Errors, e => e.Contains("doors.json:10:") && e.Contains("scene doors:crypt has no entry 'crypt_inn'") && e.Contains("did you mean 'crypt_in'?"));
        Assert.Contains(report.Errors, e => e.Contains("doors.json:11:") && e.Contains("a load door needs a \"scene\""));
        Assert.Equal(3, report.Errors.Count);                               // the good door is not one of them
    }
}
