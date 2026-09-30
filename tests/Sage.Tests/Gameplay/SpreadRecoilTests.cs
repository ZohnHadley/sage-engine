#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Spread and recoil (issue #136, docs/design/16 "As built (spread and recoil)"): a cone that grows with
// every shot and recovers, a deterministic random source for the pellets, and a kick written into the
// wielder's PawnIntent that returns.
public class SpreadRecoilTests
{
    public SpreadRecoilTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const float Deg = MathF.PI / 180f;

    internal const string Extra = """
        [{ "type": "spread", "id": "tight", "baseDegrees": 0.5, "growthPerShot": 0, "maxDegrees": 0.5 },
         { "type": "spread", "id": "wide", "baseDegrees": 8, "growthPerShot": 0, "maxDegrees": 8 },
         { "type": "spread", "id": "bloom", "baseDegrees": 1, "growthPerShot": 1, "maxDegrees": 4, "recoveryPerSecond": 2,
           "movingMultiplier": 2, "crouchingMultiplier": 0.5 },
         { "type": "recoil", "id": "lift", "pitchMinDegrees": 2, "pitchMaxDegrees": 2, "yawMinDegrees": 0, "yawMaxDegrees": 0,
           "returnSpeedDegrees": 20 },
         { "type": "recoil", "id": "wander", "pitchMinDegrees": 1, "pitchMaxDegrees": 2, "yawMinDegrees": -1, "yawMaxDegrees": 1,
           "returnSpeedDegrees": 20 },
         { "type": "attack", "id": "scatter_tight", "base": "shotgun", "damage": 1, "pellets": 100, "spread": "tight" },
         { "type": "attack", "id": "scatter_wide", "base": "shotgun", "damage": 1, "pellets": 100, "spread": "wide" },
         { "type": "attack", "id": "rifle", "base": "pistol", "damage": 1, "spread": "bloom", "recoil": "wander", "automatic": true, "cooldown": 0.05 },
         { "type": "attack", "id": "kicker", "base": "pistol", "damage": 1, "recoil": "lift" }]
        """;

    // One array: the pipeline's records, then these.
    internal static readonly string Records = HitPipelineTests.Records.TrimEnd().TrimEnd(']') + ",\n" + Extra.TrimStart().TrimStart('[');

    private static HeadlessApp NewGame() => HitPipelineTests.NewGame(Records);

    private static void Fire(HeadlessApp app, Entity e)
    {
        app.World.Get<PawnIntent>(e).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        app.World.RunFixed(Dt);
        app.World.Get<PawnIntent>(e).Pressed = default;
        app.World.RunFixed(Dt);   // a zero-windup blow lands the tick after the press
    }

    private static void Run(HeadlessApp app, int ticks)
    {
        for (int i = 0; i < ticks; i++) app.World.RunFixed(Dt);
    }

    private static float Angle(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f));

    // Acceptance: with a fixed seed, 100 pellets land inside the cone, and the same shots throw the same
    // directions on every run.
    [Xunit.Fact]
    public void HundredPelletsLandInsideTheConeAndRepeatExactly()
    {
        var aim = Vector3.Normalize(new Vector3(0.3f, 0.2f, -1f));
        float cone = 5f * Deg;
        Vector3[] Volley() => Enumerable.Range(0, 100).Select(p => Spread.Deflect(aim, cone, 42, 7, 3, p)).ToArray();

        var first = Volley();
        Assert.All(first, d =>
        {
            Assert.InRange(Angle(aim, d), 0f, cone + 1e-4f);
            Assert.Equal(1f, d.Length(), 3);
        });
        Assert.Equal(first, Volley());                              // the same every run
        Assert.True(first.Max(d => Angle(aim, d)) > cone * 0.8f);   // and it does fill the cone,
        Assert.True(first.Distinct().Count() > 90);                 // not one spot
        Assert.NotEqual(first[0], Spread.Deflect(aim, cone, 43, 7, 3, 0));   // another tick, another roll
        Assert.NotEqual(first[0], Spread.Deflect(aim, cone, 42, 8, 3, 0));   // another shooter
        Assert.NotEqual(first[0], Spread.Deflect(aim, cone, 42, 7, 4, 0));   // another shot
        Assert.Equal(aim, Spread.Deflect(aim, 0f, 42, 7, 3, 0));             // no cone: true
    }

    [Xunit.Fact]
    public void ShotRandomIsUniformEnoughAndInRange()
    {
        var values = Enumerable.Range(0, 10000).Select(i => ShotRandom.Value(i, 5, (uint)(i % 7), 1)).ToList();
        Assert.All(values, v => Assert.InRange(v, 0f, 0.99999994f));
        Assert.InRange(values.Average(), 0.47f, 0.53f);
    }

    // The pellets really fly the cone: a tight one puts all hundred on a target ten metres off, a wide
    // one misses most of them, and two runs of the wide one agree to the pellet.
    [Xunit.Fact]
    public void RayPelletsFlyTheConeAndTwoRunsAgree()
    {
        float Hits(string attack)
        {
            using var app = NewGame();
            var world = app.World;
            var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), attack);
            var target = HitPipelineTests.Body(world, new Vector3(0, 0, -10), "target", Vector3.Zero);
            Fire(app, shooter);
            Run(app, 3);
            return 100f - world.Attribute(target, HitPipelineTests.Health);
        }

        Assert.Equal(100f, Hits("scatter_tight"));
        float wide = Hits("scatter_wide");
        Assert.InRange(wide, 1f, 60f);
        Assert.Equal(wide, Hits("scatter_wide"));
    }

    // The cone: the base, growing with each shot up to the cap, recovering per second, and scaled by
    // crouching and moving.
    [Xunit.Fact]
    public void TheConeGrowsWithShotsRecoversAndScalesWithStance()
    {
        using var app = NewGame();
        var world = app.World;
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -50), "rifle");
        app.Records.TryGet(new RecordId("sage", "bloom"), out SpreadRecord spread);
        float Cone() => Spread.ConeOf(world, shooter, spread) / Deg;

        Assert.Equal(1f, Cone(), 3);
        Fire(app, shooter);
        Run(app, 2);
        Assert.InRange(Cone(), 1.9f, 2f);                             // one shot wider (less a few ticks of recovery)
        for (int i = 0; i < 6; i++) { Fire(app, shooter); Run(app, 6); }
        float capped = Cone();
        Assert.InRange(capped, 3.3f, 4f);                            // and capped at 4

        world.Get<CharacterController>(shooter).Crouching = true;
        Assert.Equal(capped * 0.5f, Cone(), 2);
        world.Get<CharacterController>(shooter).Crouching = false;
        world.Get<CharacterController>(shooter).Velocity = new Vector3(3, 0, 0);
        Assert.Equal(capped * 2f, Cone(), 2);
        world.Get<CharacterController>(shooter).Velocity = default;

        Run(app, 120);                                               // two seconds at 2 deg/s: all of it back
        Assert.Equal(1f, Cone(), 2);
    }

    // Acceptance: recoil lifts the pitch, then returns it within the recovery time (2 deg at 20 deg/s = 0.1 s).
    [Xunit.Fact]
    public void RecoilLiftsThePitchThenReturnsItWithinTheRecoveryTime()
    {
        using var app = NewGame();
        var world = app.World;
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -50), "kicker");
        world.Get<PawnIntent>(shooter).Pitch = 0.1f;   // nothing rewrites this: the system hands its kick back itself
        float yaw = world.Get<PawnIntent>(shooter).Yaw;

        Fire(app, shooter);
        float peak = 0f;
        int peakTick = 0;
        for (int t = 1; t <= 60; t++)
        {
            world.RunFixed(Dt);
            float lift = world.Get<PawnIntent>(shooter).Pitch - 0.1f;
            if (lift > peak) { peak = lift; peakTick = t; }
        }
        Assert.InRange(peak / Deg, 1.5f, 2.01f);
        Assert.True(world.Get<PawnIntent>(shooter).Pitch - 0.1f < 1e-4f);   // returned
        Assert.Equal(yaw, world.Get<PawnIntent>(shooter).Yaw, 4);

        // and it took no longer than kick / speed (+ the tick it took to start and a little slack)
        Fire(app, shooter);
        Run(app, 1);
        Assert.True(world.Get<PawnIntent>(shooter).Pitch - 0.1f > 1f * Deg);
        Run(app, (int)MathF.Ceiling(2f / 20f / Dt) + 2);
        Assert.True(MathF.Abs(world.Get<PawnIntent>(shooter).Pitch - 0.1f) < 1e-4f);
        Assert.True(peakTick <= 3);
    }

    // The same through a controller that rewrites the intent every tick (a player's view, an AI's aim):
    // the kick is an offset on top of it, not something it accumulates.
    [Xunit.Fact]
    public void ARewrittenIntentSeesTheKickAsAnOffsetAndDoesNotAccumulateIt()
    {
        using var app = NewGame();
        var world = app.World;
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -50), "kicker");
        float Step()
        {
            world.Get<PawnIntent>(shooter).Pitch = 0.2f;   // the controller's view, every tick
            world.RunFixed(Dt);
            return world.Get<PawnIntent>(shooter).Pitch - 0.2f;
        }
        Assert.Equal(0f, Step());
        world.Get<PawnIntent>(shooter).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        float peak = 0f;
        for (int t = 0; t < 40; t++) { peak = MathF.Max(peak, Step()); world.Get<PawnIntent>(shooter).Pressed = default; }
        Assert.InRange(peak / Deg, 1.5f, 2.01f);
        Assert.InRange(MathF.Abs(Step()), 0f, 1e-4f);
    }

    // The kick is drawn from the tick, the entity and the shot, so two runs of the same volley kick alike,
    // and its yaw goes both ways across the range.
    [Xunit.Fact]
    public void TheKickIsDeterministicAndDrawnFromItsRanges()
    {
        List<(float Pitch, float Yaw)> Kicks()
        {
            using var app = NewGame();
            var world = app.World;
            var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -50), "rifle");
            var kicks = new List<(float, float)>();
            for (int i = 0; i < 12; i++)
            {
                Fire(app, shooter);
                var state = world.Get<WeaponState>(shooter);
                kicks.Add((state.KickPitch, state.KickYaw));
                Run(app, 10);   // long enough that the last kick is gone
            }
            return kicks;
        }
        var a = Kicks();
        Assert.Equal(a, Kicks());
        Assert.All(a, k => Assert.InRange(k.Pitch / Deg, 0.9f, 2.01f));   // 1..2 deg, less what has returned
        Assert.Contains(a, k => k.Yaw > 0);
        Assert.Contains(a, k => k.Yaw < 0);
    }

    // Weapon state is never saved.
    [Xunit.Fact]
    public void WeaponStateIsTransient()
    {
        Assert.NotEmpty(typeof(WeaponState).GetCustomAttributes(typeof(TransientAttribute), false));
    }
}

// Twenty wielders firing spread, bloom and recoil weapons — shotgun pellets and a rifle with a kick — allocate
// nothing over 600 ticks beside the physics backend's own step (handoff 2026-09-30 §5).
[Xunit.Collection(MeasurementsCollection.Name)]
public class SpreadAllocationTests
{
    public SpreadAllocationTests() { _ = TestEnv.UserRoot; }

    private static readonly string Records = SpreadRecoilTests.Records
        .Replace("""{ "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 }""",
                 """{ "type": "attribute", "id": "health", "start": 1000000, "min": 0, "max": 1000000 }""");

    [Xunit.Fact]
    public void TwentyWieldersAllocateNothingOver600Ticks()
    {
        using var app = HitPipelineTests.NewGame(Records);
        var world = app.World;
        var attack = app.Engine.Actions.Get("Attack");
        string[] kinds = { "scatter_wide", "rifle", "kicker", "shotgun" };
        var shooters = new Entity[20];
        var targets = new Entity[20];
        for (int i = 0; i < shooters.Length; i++)
        {
            float x = i * 4f;
            shooters[i] = HitPipelineTests.Body(world, new Vector3(x, 0, 0), $"shooter{i}", new Vector3(x, 0, -8), kinds[i % kinds.Length]);
            targets[i] = HitPipelineTests.Body(world, new Vector3(x, 0, -8), $"target{i}", new Vector3(x, 0, 0));
        }
        void Step()
        {
            for (int i = 0; i < shooters.Length; i++) world.Get<PawnIntent>(shooters[i]).Pressed = default(ActionMask).With(attack);
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 400; i++) Step();   // warm: every list has met its largest volley
        Assert.All(shooters, s => Assert.True(world.Has<WeaponState>(s) || world.Get<Melee>(s).Attack.Name == "shotgun"));

        long physicsBefore = ScopeBytes("Fixed.Physics");
        var allocated = AllocationProbe.Measure(600, Step);
        long physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(allocated.Bytes - physics == 0, allocated.ToString());
    }

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }
}
