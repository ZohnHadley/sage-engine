#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Large-world coordinates and streaming (docs/design/03 §3.6, 14 §3; TODO R6, F14).
//
// The point of rebasing is that **nothing in memory is ever far from the origin**, so these tests walk
// a player across sectors and insist on two things at once: that the world keeps arriving and leaving
// around them, and that everything which holds a position moves *together* when the frame of reference
// shifts. A rebase that missed one holder is the bug this whole file exists to catch — the symptom is
// a kilometre-wide teleport of everything except the thing that was forgotten.
public class StreamingTests
{
    public StreamingTests() { _ = TestEnv.UserRoot; }

    private sealed class FlatGround : ITerrainGenerator
    {
        public int Generated;

        public void Generate(SectorCoord coord, Heightfield heights, int seed)
        {
            Generated++;
            for (int z = 0; z < heights.Resolution; z++)
                for (int x = 0; x < heights.Resolution; x++)
                    heights[x, z] = 0f;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Engine Engine;
        public readonly World World;
        public readonly FlatGround Ground = new();

        public Fixture()
        {
            var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay().Boot("streaming");
            Engine = app.Engine;
            World = app.World;
            World.Resources.Get<Terrain>().Generator = Ground;
        }

        // A player at an origin-space position, which is what a streaming source is.
        public Entity Player(Vector3 at)
        {
            var entity = World.Create(Transform.At(at), "player");
            entity.AddTag<PlayerControlled>();
            return entity;
        }

        public void Tick(int ticks = 1)
        {
            for (int i = 0; i < ticks; i++) World.RunFixed(1f / 60f);
        }

        public void Dispose() => Engine.Dispose();
    }

    private static Terrain Terrain(Fixture fx) => fx.World.Resources.Get<Terrain>();

    // The ring: standing still loads a 3x3 block of sectors and nothing else.
    [Xunit.Fact]
    public void APlayerLoadsTheRingAroundThem()
    {
        using var fx = new Fixture();
        fx.Player(new Vector3(10, 0, 10));

        fx.Tick();

        Assert.Equal(9, Terrain(fx).Sectors.Count);                       // stream_radius 1 → 3x3
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(0, 0)));
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(-1, -1)));
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(1, 1)));
        Assert.False(Terrain(fx).IsLoaded(new SectorCoord(2, 0)));
    }

    // Walking east brings the next column in and drops the one behind — but only past the margin, so
    // a player standing on an edge does not thrash the world.
    [Xunit.Fact]
    public void WalkingLoadsAheadAndUnloadsBehind()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        fx.Tick();
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(-1, 0)));

        // Three sectors east. The origin will follow, so ask in absolute terms afterwards.
        fx.World.Get<Transform>(player).LocalPosition = new Vector3(3 * 1024f + 10f, 0, 10);
        fx.Tick();

        var origin = fx.World.Origin();
        Assert.Equal(new SectorCoord(3, 0), origin.Sector);
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(3, 0)), "the sector it stands in");
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(4, 0)), "and the one ahead");
        Assert.False(Terrain(fx).IsLoaded(new SectorCoord(-1, 0)), "the one it left, past the margin");
        Assert.False(Terrain(fx).IsLoaded(new SectorCoord(0, 0)));

        // Nothing is kept beyond the ring plus its margin — which is the *whole* rule, so it is what
        // the test asserts rather than a count. (Twelve survive here: the nine around the player, plus
        // the three of the old ring that are exactly two sectors away and so inside the margin.)
        foreach (var sector in Terrain(fx).Sectors)
        {
            int distance = Math.Max(Math.Abs(sector.Coord.X - 3), Math.Abs(sector.Coord.Z));
            Assert.True(distance <= 2, $"sector {sector.Coord} is {distance} away and should have gone");
        }
    }

    // Hysteresis: a sector just outside the ring stays loaded until the player is past the margin, so
    // pacing over an edge does not load and unload it every tick.
    [Xunit.Fact]
    public void ASectorJustOutsideTheRingSurvives()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        fx.Tick();
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(-1, 0)));

        fx.World.Get<Transform>(player).LocalPosition = new Vector3(1024f + 10f, 0, 10);   // one sector east
        fx.Tick();

        // (-1,0) is now two sectors from the player: outside the ring, inside the margin.
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(-1, 0)), "kept: hysteresis");
        Assert.True(Terrain(fx).IsLoaded(new SectorCoord(2, 0)), "and the new column is in");
    }

    // The heart of R6: when the origin moves, *everything* moves with it, and the player's position in
    // the world is unchanged — only the numbers describing it are smaller.
    [Xunit.Fact]
    public void RebasingMovesEverythingTogether()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        var prop = fx.World.Create(Transform.At(new Vector3(40, 0, 0)), "prop");
        fx.Tick();

        var origin = fx.World.Origin();
        Assert.Equal(SectorCoord.Zero, origin.Sector);
        var propAbsolute = origin.ToAbsolute(fx.World.Get<Transform>(prop).LocalPosition);

        // Walk two sectors east: far enough to trip the 1.5-sector rule.
        var far = new Vector3(2 * 1024f + 10f, 0, 10);
        fx.World.Get<Transform>(player).LocalPosition = far;
        var playerAbsoluteBefore = origin.ToAbsolute(far);
        fx.Tick();

        Assert.Equal(new SectorCoord(2, 0), origin.Sector);
        Assert.Equal(1, origin.Rebases);

        // The player is where it was in the world, but close to the origin in memory.
        var after = fx.World.Get<Transform>(player).LocalPosition;
        Assert.Equal(playerAbsoluteBefore.X, origin.ToAbsolute(after).X, 3);
        Assert.True(MathF.Abs(after.X) < 1024f, $"the player should be near the origin, not at {after.X:F0} m");

        // And so is everything else: the prop did not move in the world, only in memory.
        var propAfter = fx.World.Get<Transform>(prop).LocalPosition;
        Assert.Equal(propAbsolute.X, origin.ToAbsolute(propAfter).X, 3);
        Assert.Equal(-2 * 1024f + 40f, propAfter.X, 3);
    }

    // The bug this is here to prevent: `GlobalTransform.Previous` is what rendering interpolates *from*.
    // A rebase that shifted only `Current` would draw everything streaking a kilometre for one frame.
    [Xunit.Fact]
    public void RebasingMovesBothPosesSoNothingStreaks()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        var prop = fx.World.Create(Transform.At(new Vector3(40, 0, 0)), "prop");
        fx.Tick(2);

        var before = fx.World.Get<GlobalTransform>(prop);
        float gap = Vector3.Distance(before.Current.Position, before.Previous.Position);

        fx.World.Get<Transform>(player).LocalPosition = new Vector3(2 * 1024f + 10f, 0, 10);
        fx.Tick();

        var after = fx.World.Get<GlobalTransform>(prop);
        Assert.Equal(gap, Vector3.Distance(after.Current.Position, after.Previous.Position), 3);
        Assert.True(Vector3.Distance(after.Current.Position, after.Previous.Position) < 1f,
            "current and previous must stay together, or one frame interpolates across a kilometre");
    }

    // Physics holds its own copy of every pose, so a rebase that forgot it would have the simulation
    // drag everything back a sector on the next step.
    [Xunit.Fact]
    public void PhysicsBodiesRebaseWithTheWorld()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        var crate = fx.World.Create(Transform.At(new Vector3(30, 20, 0)), "crate");
        fx.World.Add(crate, Collider.Box(Vector3.One));
        fx.World.Add(crate, RigidBody.Dynamic(10f));
        fx.Tick(3);

        var origin = fx.World.Origin();
        var absoluteBefore = origin.ToAbsolute(fx.World.Get<Transform>(crate).LocalPosition);

        fx.World.Get<Transform>(player).LocalPosition = new Vector3(2 * 1024f + 10f, 0, 10);
        fx.Tick();                       // the rebase
        fx.Tick(3);                      // and three more steps of physics on top of it

        // Unmoved in the world, which is the whole claim: had physics kept its own pre-rebase pose, the
        // write-back on the next step would have put the crate a sector away from where it was standing.
        var absoluteAfter = origin.ToAbsolute(fx.World.Get<Transform>(crate).LocalPosition);
        Assert.Equal(absoluteBefore.X, absoluteAfter.X, 1);
        Assert.Equal(absoluteBefore.Z, absoluteAfter.Z, 1);

        // In memory it is now two sectors *behind* the player, because that is where it is: the crate
        // did not travel. What matters is that its transform and its body agree about that.
        Assert.Equal(-2 * 1024f + 30f, fx.World.Get<Transform>(crate).LocalPosition.X, 1);
    }

    // Ground height is asked in origin space and answered from sectors keyed absolutely; that
    // translation is the one place the two spaces meet, and it must survive a rebase.
    [Xunit.Fact]
    public void GroundHeightIsAskedInOriginSpaceAndSurvivesARebase()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        fx.Tick();

        var terrain = Terrain(fx);
        Assert.Equal(0f, terrain.HeightAt(10f, 10f));                  // flat ground, loaded

        fx.World.Get<Transform>(player).LocalPosition = new Vector3(2 * 1024f + 10f, 0, 10);
        fx.Tick();

        // In origin space the player is near zero again, and the ground under it is still there.
        var where = fx.World.Get<Transform>(player).LocalPosition;
        Assert.Equal(0f, terrain.HeightAt(where.X, where.Z));
        Assert.Equal(new Vector3(0, 0, 0), terrain.CornerOf(fx.World.Origin().Sector));
    }

    // What a sector owns goes with it: the collision body built for it must not outlive the ground.
    [Xunit.Fact]
    public void UnloadingASectorTakesWhatItOwns()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        fx.Tick(2);   // collision is built the tick after the sector loads

        int owned = Owned(fx, new SectorCoord(-1, -1));
        Assert.True(owned > 0, "the sector should own at least its collision mesh");

        fx.World.Get<Transform>(player).LocalPosition = new Vector3(4 * 1024f, 0, 4 * 1024f);
        fx.Tick(2);

        Assert.False(Terrain(fx).IsLoaded(new SectorCoord(-1, -1)));
        Assert.Equal(0, Owned(fx, new SectorCoord(-1, -1)));
    }

    // Turning streaming off leaves the world exactly as it is — a game with a hand-built level, or a
    // test that wants one sector and no surprises.
    [Xunit.Fact]
    public void StreamingCanBeTurnedOff()
    {
        using var fx = new Fixture();
        Assert.True(fx.Engine.CVars.Find("stream_enabled")!.TrySet("0", out _));
        fx.Player(new Vector3(10, 0, 10));

        fx.Tick(2);

        Assert.Empty(Terrain(fx).Sectors);
        Assert.Equal(SectorCoord.Zero, fx.World.Origin().Sector);
    }

    // The bug the first version of `warp` had, and the reason 14 §3 states the order: rebase, generate,
    // *then* place. Placing first read a ground height from a sector that did not exist yet, got zero,
    // and the player fell 1.6 km before the terrain caught up.
    [Xunit.Fact]
    public void ArrivingSomewhereNewPutsYouOnGroundThatExists()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        fx.Tick();

        // Somewhere no sector has ever been generated — a hundred kilometres away.
        var terrain = Terrain(fx);
        var destination = new SectorCoord(117, -79);
        fx.World.Rebase(destination);
        for (int z = -1; z <= 1; z++)
            for (int x = -1; x <= 1; x++)
                terrain.Load(new SectorCoord(destination.X + x, destination.Z + z));

        var origin = fx.World.Origin();
        var local = origin.ToOrigin(new Vector3(117 * 1024f + 200f, 0, -79 * 1024f + 300f));
        local.Y = terrain.HeightAt(local.X, local.Z) + 1f;
        fx.World.Teleport(player, Transform.At(local));

        fx.Tick(30);   // half a second of gravity, with ground that exists

        var landed = fx.World.Get<Transform>(player).LocalPosition;
        Assert.True(landed.Y > -5f, $"the player should be on the ground, not {landed.Y:F0} m below it");
        Assert.True(MathF.Abs(landed.X) < 2048f && MathF.Abs(landed.Z) < 2048f,
            "and near the origin in memory, however far away in the world");
        Assert.Equal(destination, origin.Sector);
    }

    // The contract of `Origin.Rebased`: a subscriber is called **after** the world has moved, so what
    // it reads is consistent. Raised from inside the shift (as the first version was), it handed out an
    // origin naming the new sector while every position still meant the old one — and the first
    // subscriber, the editor's free camera, would have corrected itself into the wrong place.
    [Xunit.Fact]
    public void SubscribersSeeTheWorldAlreadyMoved()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(10, 0, 10));
        var prop = fx.World.Create(Transform.At(new Vector3(40, 0, 0)), "prop");
        fx.Tick();

        Vector3 seenOffset = Vector3.Zero;
        Vector3 propWhenCalled = Vector3.Zero;
        SectorCoord sectorWhenCalled = default;
        int calls = 0;

        fx.World.Origin().Rebased += offset =>
        {
            calls++;
            seenOffset = offset;
            sectorWhenCalled = fx.World.Origin().Sector;
            propWhenCalled = fx.World.Get<Transform>(prop).LocalPosition;
        };

        fx.World.Get<Transform>(player).LocalPosition = new Vector3(2 * 1024f + 10f, 0, 10);
        fx.Tick();

        Assert.Equal(1, calls);
        Assert.Equal(new Vector3(-2 * 1024f, 0, 0), seenOffset);
        Assert.Equal(new SectorCoord(2, 0), sectorWhenCalled);
        Assert.Equal(-2 * 1024f + 40f, propWhenCalled.X, 3);       // already shifted when called
    }

    // Streaming runs every tick in the fixed schedule, where the engine's rule is that a steady state
    // allocates nothing (02 §4.6). Loading a sector allocates — it makes a heightfield — so the claim
    // is about the ticks in between, which is all of them once the ring is up.
    //
    // Measured as a **difference**, with streaming off and then on, so that only streaming is counted:
    // a flat zero here would be answering for every other plugin of the fixture's tick. (The physics
    // step, once thought to allocate 40 bytes a tick inside Bepu, allocates nothing since #273.)
    [Xunit.Fact]
    public void StandingStillCostsNothingPerTick()
    {
        using var fx = new Fixture();
        fx.Player(new Vector3(10, 0, 10));
        fx.Tick(5);                                  // the ring, its meshes and its collision are up

        var enabled = fx.Engine.CVars.Find("stream_enabled")!;

        Assert.True(enabled.TrySet("0", out _));
        fx.Tick(10);                                 // settle, and let anything one-off happen
        long before = GC.GetAllocatedBytesForCurrentThread();
        fx.Tick(60);
        long without = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(enabled.TrySet("1", out _));
        fx.Tick(10);
        before = GC.GetAllocatedBytesForCurrentThread();
        fx.Tick(60);
        long with = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(with - without <= 0, $"streaming added {with - without} bytes over 60 ticks");
    }

    // Walking back and forth across a sector edge must not load and unload the world each time: that
    // is what the margin is for, and it is the failure that would show up as a stutter every few steps.
    [Xunit.Fact]
    public void PacingOverAnEdgeDoesNotThrash()
    {
        using var fx = new Fixture();
        var player = fx.Player(new Vector3(1000, 0, 10));   // just inside sector (0, 0)
        fx.Tick();
        int generated = fx.Ground.Generated;

        for (int i = 0; i < 10; i++)
        {
            fx.World.Get<Transform>(player).LocalPosition = new Vector3(1030, 0, 10);   // into (1, 0)
            fx.Tick();
            fx.World.Get<Transform>(player).LocalPosition = new Vector3(1000, 0, 10);   // and back
            fx.Tick();
        }

        // The three new sectors of the column ahead are generated once, not once per crossing.
        Assert.True(fx.Ground.Generated - generated <= 3,
            $"{fx.Ground.Generated - generated} sectors generated while pacing: the margin is not holding");
    }

    private static int Owned(Fixture fx, SectorCoord sector)
    {
        int n = 0;
        foreach (var entity in fx.World.Query<SectorOwned>().Entities)
            if (fx.World.Get<SectorOwned>(entity).Sector == sector) n++;
        return n;
    }
}
