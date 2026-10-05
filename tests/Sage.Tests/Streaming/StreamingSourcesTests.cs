#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Several streaming sources, each with its own rings, and followers who travel with the player (issue #290).
// The player is always a source; a tagged entity streams with the cvars' rings and one with `streaming_ring`
// with its own; the origin follows the player whoever else is a source. A journey puts every follower behind
// the player, and the follower stays live in the sector it arrived in.
public class StreamingSourcesTests
{
    public StreamingSourcesTests() { _ = TestEnv.UserRoot; }

    private const float Sector = Terrain.SectorSize;

    private sealed class FlatGround : ITerrainGenerator
    {
        public void Generate(SectorCoord coord, Heightfield heights, int seed)
        {
            for (int z = 0; z < heights.Resolution; z++)
                for (int x = 0; x < heights.Resolution; x++)
                    heights[x, z] = 0f;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Engine Engine;
        public readonly World World;

        public Fixture()
        {
            var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay().Boot("sources");
            Engine = app.Engine;
            World = app.World;
            World.Resources.Get<Terrain>().Generator = new FlatGround();
        }

        public Entity Player(Vector3 at)
        {
            var entity = World.Create(Transform.At(at), "player");
            entity.AddTag<PlayerControlled>();
            return entity;
        }

        public Terrain Terrain => World.Resources.Get<Terrain>();

        public void Tick(int ticks = 1)
        {
            for (int i = 0; i < ticks; i++) World.RunFixed(1f / 60f);
        }

        public void Dispose() => Engine.Dispose();
    }

    // A tagged companion five sectors out keeps its own 3x3 ring next to the player's, and the origin stays
    // with the player; one with a ring of radius 0 keeps only the sector it stands in.
    [Xunit.Fact]
    public void EachSourceKeepsItsOwnRing()
    {
        using var fx = new Fixture();
        fx.Player(new Vector3(10, 0, 10));
        var companion = fx.World.Create(Transform.At(new Vector3(5 * Sector + 10, 0, 10)), "companion");
        companion.AddTag<StreamingSource>();
        var camera = fx.World.Create(Transform.At(new Vector3(10, 0, -8 * Sector + 10)), "camera");
        fx.World.Add(camera, new StreamingRing { Radius = 0 });

        fx.Tick();

        Assert.Equal(9 + 9 + 1, fx.Terrain.Sectors.Count);
        Assert.True(fx.Terrain.IsLoaded(new SectorCoord(-1, -1)));     // the player's ring
        Assert.True(fx.Terrain.IsLoaded(new SectorCoord(6, 1)));       // the companion's
        Assert.True(fx.Terrain.IsLoaded(new SectorCoord(0, -8)));      // the camera's own sector
        Assert.False(fx.Terrain.IsLoaded(new SectorCoord(1, -8)));     // and nothing round it
        Assert.Equal(3, fx.World.Resources.Get<SectorRing>().Sources);
        Assert.Equal(new SectorCoord(0, 0), fx.World.Origin().Sector);  // the frame stays with the player

        // A source that stops being one lets its ring go past the margin.
        fx.World.Remove<StreamingRing>(camera);
        companion.RemoveTag<StreamingSource>();
        fx.Tick();
        Assert.Equal(9, fx.Terrain.Sectors.Count);
        Assert.Equal(1, fx.World.Resources.Get<SectorRing>().Sources);
    }

    // The player's own `streaming_ring` replaces the cvars' rings for them, and the origin follows the player
    // even with another source standing nearer to it.
    [Xunit.Fact]
    public void ThePlayersOwnRingIsUsedAndTheOriginFollowsThePlayer()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        fx.World.Add(player, new StreamingRing { Radius = 0 });
        var companion = fx.World.Create(Transform.At(new Vector3(20, 0, 20)), "companion");
        fx.World.Add(companion, new StreamingRing { Radius = 0 });

        fx.Tick();
        Assert.Single(fx.Terrain.Sectors);                             // the one sector both stand in, once
        Assert.Equal(1, fx.World.Resources.Get<SectorRing>().Sources);

        var transform = fx.World.Get<Transform>(player);
        transform.LocalPosition = new Vector3(3 * Sector + 10, 0, 10);
        fx.World.Teleport(player, transform);
        fx.Tick();

        Assert.Equal(new SectorCoord(3, 0), fx.World.Origin().Sector);   // the player's, not the nearer companion's
        Assert.Equal(new SectorCoord(3, 0), fx.World.Resources.Get<SectorRing>().Centre);
        Assert.True(fx.Terrain.IsLoaded(new SectorCoord(3, 0)));
        Assert.True(fx.Terrain.IsLoaded(new SectorCoord(0, 0)));          // the companion's ground stays
        Assert.Equal(2, fx.Terrain.Sectors.Count);
    }

    // A source with a far radius of its own has a far ring; one without has none.
    [Xunit.Fact]
    public void ASourcesFarRadiusIsItsOwn()
    {
        using var fx = new Fixture();
        fx.Engine.CVars.Execute("stream_far_radius 0");
        fx.Player(new Vector3(10, 0, 10));
        fx.Tick();
        var lod = fx.World.Resources.Get<SectorLod>();
        Assert.Equal(0, lod.Count);

        var scout = fx.World.Create(Transform.At(new Vector3(10, 0, 10)), "scout");
        fx.World.Add(scout, new StreamingRing { Radius = 1, FarRadius = 2 });
        fx.Tick();
        Assert.Equal(25 - 9, lod.Count);                                  // 5x5 less the 3x3 full ring
        Assert.Equal(2, lod.Radius);
    }

    // ---- followers -------------------------------------------------------------------------------------

    private const string Content = """
    [
      { "type": "terrain", "id": "plain", "generator": "Flat", "height": 0 },
      { "type": "prefab", "id": "hero", "name": "hero", "tags": ["player_controlled"] },
      { "type": "prefab", "id": "hound", "name": "hound", "parts": { "follower": { "distance": 3 } } },
      { "type": "prefab", "id": "goblin", "name": "goblin" },
      { "type": "prefab", "id": "waystone", "name": "waystone", "parts": { "travel_point": { "label": "Old Mill", "radius": 25 } } },
      { "type": "scene", "id": "village", "streamed": true, "terrain": "plain", "relativeTo": "Ground",
        "player": { "prefab": "hero", "at": [0, 0.05, 0] },
        "place": [ { "prefab": "hound", "at": [2, 0, 2], "name": "hound", "id": "hound" },
                   { "prefab": "goblin", "at": [4, 0, 4], "name": "stay", "id": "stay" },
                   { "prefab": "waystone", "at": [3000, 0, 520], "yaw": 90, "name": "mill" } ] }
    ]
    """;

    private static HeadlessApp Run(MountFixture files, string saves)
    {
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
            .Mount(files).StartScene("game:village").Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        app.Engine.Saves.Root = saves;
        app.CVars.Execute("save_autosave 0");
        WorldClock.Of(app.World).Scale = 0;
        app.World.RunFixed(1f / 60f);
        return app;
    }

    private static Entity[] Named(World world, string name) =>
        world.QueryAll().Entities.ToEntityList().Where(e => e.Name == name).ToArray();

    private static Vector3 Absolute(World world, Entity entity) => world.Origin().ToAbsolute(world.Get<Transform>(entity).LocalPosition);

    // The issue's done criterion: a follower arrives with the player after a fast travel, stays live in the
    // sector it arrived in while the one it left goes to sleep, and is there (once) after a save and a load.
    [Xunit.Fact]
    public void AFollowerArrivesWithThePlayerAfterFastTravel()
    {
        var files = new MountFixture();
        files.Write("game", "data/content.json", Content);
        files.Mount("game", "game");
        string saves = TestEnv.NewTempDir();
        using (var app = Run(files, saves))
        {
            var world = app.World;
            var player = Scenes.Player(world);
            var hound = Assert.Single(Named(world, "hound"));
            Assert.Single(Named(world, "stay"));
            Assert.Equal(Followers.DefaultDistance + 1, world.Get<Follower>(hound).Distance);

            // Discover the mill, then come home to the hound.
            world.Get<Transform>(player).LocalPosition = world.Origin().ToOrigin(new Vector3(2990, 0, 515));
            for (int i = 0; i < 3; i++) world.RunFixed(1f / 60f);
            Assert.Single(TravelLog.Of(world).Points);
            world.Get<Transform>(player).LocalPosition = world.Origin().ToOrigin(new Vector3(0, 0.05f, 0));
            for (int i = 0; i < 3; i++) world.RunFixed(1f / 60f);
            Assert.Equal(new SectorCoord(0, 0), world.Origin().Sector);
            hound = Assert.Single(Named(world, "hound"));

            Assert.True(Travel.ToPoint(world, "Old Mill"));

            var at = Absolute(world, player);
            Assert.Equal(3000f, at.X, 2);
            Assert.Equal(520f, at.Z, 2);
            Assert.Equal(hound, Assert.Single(Named(world, "hound")));      // the same hound, moved
            var houndAt = Absolute(world, hound);
            Assert.Equal(3f, Vector2.Distance(new Vector2(at.X, at.Z), new Vector2(houndAt.X, houndAt.Z)), 2);
            Assert.Equal(0f, houndAt.Y, 2);                                  // on the ground
            // Behind the player: yaw 90 faces -X, so behind is +X.
            Assert.Equal(3003f, houndAt.X, 2);
            Assert.Equal(SageMath.YawOf(world.Get<Transform>(player).LocalRotation),
                         SageMath.YawOf(world.Get<Transform>(hound).LocalRotation), 3);

            for (int i = 0; i < 10; i++) world.RunFixed(1f / 60f);

            Assert.True(world.IsAlive(hound));                               // the ring it left did not take it
            Assert.Equal(hound, Assert.Single(Named(world, "hound")));
            Assert.Empty(Named(world, "stay"));                              // who did not follow sleeps at home
            Assert.True(ContentIds.Baseline(world).IsDormant(ContentIds.SectorSource(new RecordId("game", "village"), new SectorCoord(0, 0))));
            Assert.Equal(3003f, Absolute(world, hound).X, 1);
            Assert.True(app.Engine.Saves.Save("mill"));
        }

        using (var app = Run(files, saves))
        {
            var world = app.World;
            Assert.True(app.Engine.Saves.Load("mill"));
            for (int i = 0; i < 3; i++) world.RunFixed(1f / 60f);
            var hound = Assert.Single(Named(world, "hound"));
            Assert.Equal(3003f, Absolute(world, hound).X, 1);
            Assert.Equal(3f, world.Get<Follower>(hound).Distance);
        }
    }

    // Several followers stand in rows behind the player, apart from each other; `warp` brings them too.
    [Xunit.Fact]
    public void FollowersStandApartBehindThePlayerAndComeAlongOnAWarp()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        var followers = Enumerable.Range(0, 4).Select(i =>
        {
            var e = fx.World.Create(Transform.At(new Vector3(12 + i, 0, 12)), $"f{i}");
            fx.World.Add(e, new Follower());
            return e;
        }).ToArray();
        fx.Tick();

        fx.Engine.CVars.Execute("sv_cheats 1");
        fx.Engine.CVars.Execute("warp 5000 100");

        var at = Absolute(fx.World, player);
        Assert.Equal(5000f, at.X, 2);
        var places = followers.Select(f => Absolute(fx.World, f)).ToArray();
        for (int i = 0; i < places.Length; i++)
        {
            Assert.True(places[i].Z > at.Z + 1.5f, $"follower {i} at {places[i]} is not behind {at}");   // yaw 0 faces -Z
            for (int j = 0; j < i; j++) Assert.True(Vector3.Distance(places[i], places[j]) >= 1.4f);
        }
        Assert.Equal(at.Z + Followers.DefaultDistance, places[0].Z, 2);
        fx.Tick(3);
        Assert.All(followers, f => Assert.True(fx.World.IsAlive(f)));
    }
}
