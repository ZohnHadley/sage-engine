#nullable enable
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// What the engine costs when the world is full (docs/design/02 §4.6, 14; TODO R18).
//
// The readiness review said this in so many words: the tests prove correctness, not fitness. The
// largest test created two hundred entities and the Sandbox placed twenty-five, so every performance
// claim in these docs was an assumption.
//
// **What a performance test may assert** is the awkward part, because a wall-clock threshold passes on
// this machine and fails on a slower one, and a test that is disabled for being flaky protects
// nothing. So these assert two things that are true on *any* machine:
//
//   * the steady state **allocates nothing per tick**, which is exact and the rule the engine set
//     itself (02 §4.6);
//   * the cost grows **sub-quadratically** with entity count, which catches the accident that actually
//     happens — somebody's inner loop over "everything else" — without caring how fast the box is.
//
// The wall-clock numbers themselves are recorded by running the game (`scale_spawn`, `scale_report`)
// and written into docs/history/scale-2026-09-24.md, where a human can compare them next time.
[Collection(MeasurementsCollection.Name)]
public class ScaleTests
{
    public ScaleTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
    [
      { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
      { "type": "damage_type", "id": "physical" },
      { "type": "attack", "id": "claws", "damage": 6, "damageType": "physical", "reach": 2.0 },
      { "type": "ai_profile", "id": "default_ai", "sightRange": 25, "meleeRange": 1.8, "thinkRate": 6 },
      { "type": "ai_schedule", "id": "idle",  "tasks": [{ "task": "Wait", "seconds": 1.5 }], "interrupts": ["SeeEnemy"] },
      { "type": "ai_schedule", "id": "chase", "tasks": [{ "task": "MoveToTarget", "distance": 1.6 }], "interrupts": ["EnemyInMeleeRange", "LostEnemy", "NoEnemy"] },
      { "type": "ai_schedule", "id": "melee_attack", "tasks": ["FaceTarget", { "task": "MeleeAttack", "giveUpAfter": 1.5 }, { "task": "Wait", "seconds": 0.5 }], "interrupts": ["LostEnemy", "NoEnemy"] },
      { "type": "ai_schedule", "id": "hold_ground", "tasks": ["FaceTarget", { "task": "Wait", "seconds": 0.3 }], "interrupts": ["LostEnemy", "NoEnemy"] },
      { "type": "ai_schedule", "id": "cast_spell", "tasks": ["FaceTarget", { "task": "CastSpell", "giveUpAfter": 2 }, { "task": "Wait", "seconds": 0.8 }], "interrupts": ["LostEnemy", "NoEnemy"] },

      { "type": "prefab", "id": "prop", "name": "prop" },
      { "type": "prefab", "id": "walker", "name": "walker",
        "parts": { "character": { "layer": "enemy" }, "attributes": {} } },
      { "type": "prefab", "id": "creature", "name": "creature",
        "components": { "ai_state": { "schedule": "sage:idle" } },
        "parts": { "character": { "layer": "enemy" }, "attributes": {}, "melee": { "attack": "claws" } } }
    ]
    """;

    private sealed class FlatGround : ITerrainGenerator
    {
        public void Generate(SectorCoord coord, Heightfield heights, int seed)
        {
            for (int i = 0; i < heights.Heights.Length; i++) heights.Heights[i] = 0f;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public readonly Engine Engine;
        public readonly World World;

        public Fixture()
        {
            var app = HeadlessApp.Bare().With(new PhysicsModule(), new StreamingModule()).WithGameplay()
                .File("data/scale.json", Records)
                .Boot("scale");
            Engine = app.Engine;
            World = app.World;
            World.Resources.Get<Terrain>().Generator = new FlatGround();
        }

        public Entity Player()
        {
            var entity = World.Create(Transform.At(new Vector3(0, 0.1f, 0)), "player");
            World.AddCharacter(entity, layer: 1);
            World.AddAttributes(entity);
            entity.AddTag<PlayerControlled>();
            return entity;
        }

        // A grid of props, and a handful of creatures that actually think, which is the shape of a
        // real scene: mostly scenery, a few things with opinions.
        public void Fill(int props, int creatures)
        {
            int side = (int)MathF.Ceiling(MathF.Sqrt(props));
            for (int i = 0; i < props; i++)
                World.Spawn(new RecordId("sage", "prop"),
                            new Vector3(((i % side) - side * 0.5f) * 6f, 0, ((i / side) - side * 0.5f) * 6f));

            for (int i = 0; i < creatures; i++)
            {
                float angle = i * MathF.Tau / MathF.Max(creatures, 1);
                World.Spawn(new RecordId("sage", "creature"),
                            new Vector3(MathF.Cos(angle) * 15f, 0.1f, MathF.Sin(angle) * 15f));
            }
            World.FlushCommands();
        }

        public void Tick(int ticks)
        {
            for (int i = 0; i < ticks; i++) World.RunFixed(1f / 60f);
        }

        public void Dispose() => Engine.Dispose();
    }

    // Two thousand entities, ticked for two seconds, and nothing falls over: the world keeps its
    // entities, the creatures keep thinking, and the terrain ring is still there.
    [Xunit.Fact]
    public void AWorldOfTwoThousandEntitiesRuns()
    {
        using var fx = new Fixture();
        fx.Player();
        fx.Fill(props: 1900, creatures: 100);
        fx.Tick(120);

        Assert.True(fx.World.EntityCount >= 2000, $"only {fx.World.EntityCount} entities survived");
        Assert.Equal(9, fx.World.Resources.Get<Terrain>().Sectors.Count);

        // The creatures nearest the player have noticed it: the AI is doing its work at this size,
        // not quietly failing.
        int seeing = 0;
        foreach (var entity in fx.World.Query<AIState>().Entities)
            if (((AICondition)fx.World.Get<AIState>(entity).Conditions).HasFlag(AICondition.SeeEnemy)) seeing++;
        Assert.True(seeing > 0, "no creature in a crowd of a hundred noticed the player");
    }

    // The rule the engine set itself (02 §4.6), at the size where breaking it would matter. Measured
    // as a difference against an empty world, because the tick is not otherwise silent: Bepu's
    // profiler allocates about 40 bytes inside `Timestep` (TODO #41).
    //
    // **With a little slack, and why that loses nothing.** The allocation is counted per thread, and
    // Bepu hands its work to whichever threads are free — so on a loaded machine that 40-byte
    // allocation lands on the measured thread in one run and on a worker in the other, and the
    // difference comes out as exactly 40. It did, three times, while CI-sized builds ran beside the
    // tests (2026-09-28). What this test exists to catch is an allocation *per tick*: even the smallest
    // object, every tick for 60 ticks, is 60 × 24 = 1440 bytes. A slack of a quarter of that absorbs
    // the scheduling noise and still fails on the smallest real regression.
    private const long SchedulingSlackBytes = 360;

    [Xunit.Fact]
    public void AFullWorldStillAllocatesNothingPerTick()
    {
        using var fx = new Fixture();
        fx.Player();
        fx.Tick(30);

        long before = GC.GetAllocatedBytesForCurrentThread();
        fx.Tick(60);
        long empty = GC.GetAllocatedBytesForCurrentThread() - before;

        fx.Fill(props: 1900, creatures: 100);
        fx.Tick(30);                                  // settle: spawning allocates, steady state must not

        before = GC.GetAllocatedBytesForCurrentThread();
        fx.Tick(60);
        long full = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(full - empty <= SchedulingSlackBytes,
            $"two thousand entities added {full - empty} bytes over 60 ticks (empty {empty}, full {full})");
    }

    // The failure this is really here to catch: somebody's inner loop over "everything else", which
    // turns a crowd into a quadratic. Ratios, not milliseconds, so the assertion means the same thing
    // on a slow machine as on a fast one.
    [Xunit.Fact]
    public void CostGrowsWithTheCrowdButNotWithItsSquare()
    {
        double small = TimePerTick(props: 450, creatures: 50);
        double large = TimePerTick(props: 1950, creatures: 50);

        // Four times the entities. Linear would be ~4x, quadratic ~16x; anything under 8x is a slope
        // a crowd can live with, and the number is in the failure message either way.
        double ratio = large / MathF.Max((float)small, 0.0001f);
        Assert.True(ratio < 8.0,
            $"4x the entities cost {ratio:F1}x the time ({small * 1000:F0} µs -> {large * 1000:F0} µs per tick)");
    }

    private static double TimePerTick(int props, int creatures)
    {
        using var fx = new Fixture();
        fx.Player();
        fx.Fill(props, creatures);
        fx.Tick(30);                                  // warm: JIT, archetypes, the terrain ring

        var watch = Stopwatch.StartNew();
        fx.Tick(120);
        return watch.Elapsed.TotalMilliseconds / 120.0;
    }

    // Streaming a full world: walking two sectors with two thousand entities in tow must rebase them
    // all and leave them where they were, which is the R6 promise at a size that could expose it.
    [Xunit.Fact]
    public void ACrowdSurvivesARebase()
    {
        using var fx = new Fixture();
        var player = fx.Player();
        fx.Fill(props: 1900, creatures: 100);
        fx.Tick(5);

        var origin = fx.World.Origin();
        var sample = fx.World.Query<Transform>().Entities.First(e => e != player);
        var absoluteBefore = origin.ToAbsolute(fx.World.Get<Transform>(sample).LocalPosition);

        fx.World.Get<Transform>(player).LocalPosition = new Vector3(2 * 1024f + 10f, 0.1f, 10f);
        fx.Tick(5);

        Assert.Equal(new SectorCoord(2, 0), origin.Sector);
        var absoluteAfter = origin.ToAbsolute(fx.World.Get<Transform>(sample).LocalPosition);
        Assert.Equal(absoluteBefore.X, absoluteAfter.X, 2);
        Assert.Equal(absoluteBefore.Z, absoluteAfter.Z, 2);
    }
}
