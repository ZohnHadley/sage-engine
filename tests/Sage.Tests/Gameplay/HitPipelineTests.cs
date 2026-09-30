#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// One hit pipeline (issue #133, docs/design/16 "As built (the hit pipeline)"): an attack's `delivery`
// decides how its blow reaches what it hits — a sweep (the swing CombatTests already covers), a ray or a
// projectile — and every landing goes through Combat.ApplyHit.
public class HitPipelineTests
{
    public HitPipelineTests() { _ = TestEnv.UserRoot; }

    internal static readonly RecordId Health = new("sage", "health");
    internal const float Dt = 1f / 60f;

    internal const string Records = """
        [{ "type": "gameplay_conventions", "id": "default_conventions", "health": "health", "dead": "state.dead", "damageType": "physical" },
         { "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
         { "type": "tag", "id": "state.dead" },
         { "type": "effect", "id": "damage", "duration": "Instant", "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
         { "type": "damage_type", "id": "physical", "effect": "damage" },

         { "type": "attack", "id": "sword", "damage": 20, "reach": 2.0, "radius": 0.3, "arcDegrees": 120,
           "windupTime": 0.2, "recoverTime": 0.1, "cooldown": 0.5 },
         { "type": "attack", "id": "pistol", "delivery": "ray", "damage": 25, "range": 50,
           "windupTime": 0, "recoverTime": 0.1, "cooldown": 0.3 },
         { "type": "attack", "id": "shotgun", "base": "pistol", "damage": 5, "pellets": 3 },
         { "type": "attack", "id": "sling", "delivery": "projectile", "damage": 15, "range": 40, "radius": 0.1,
           "projectileSpeed": 30, "windupTime": 0, "recoverTime": 0.1, "cooldown": 0.5 }]
        """;

    internal static HeadlessApp NewGame(string records = Records)
    {
        var app = HeadlessApp.Gameplay().File("data/hits.json", records).Boot("hits");
        Assert.Equal(0, app.Records.ErrorCount);
        var ground = app.World.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        app.World.Add(ground, Collider.Box(new Vector3(400, 1, 400)));
        return app;
    }

    // A character at `feet` facing `lookAt`, holding `attack` (none: a target).
    internal static Entity Body(World world, Vector3 feet, string name, Vector3 lookAt, string? attack = null)
    {
        var entity = world.Create(Transform.At(feet), name);
        world.AddCharacter(entity, world.Resources.Get<IPhysicsWorld>().Layers.Player);
        if (attack != null) world.Add(entity, Melee.With(new RecordId("sage", attack)));
        world.AddAttributes(entity);
        world.Get<PawnIntent>(entity).Yaw = SageMath.YawTo(feet, lookAt);
        return entity;
    }

    private static void Press(HeadlessApp app, Entity fighter)
    {
        var world = app.World;
        world.Get<PawnIntent>(fighter).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        world.RunFixed(Dt);
        world.Get<PawnIntent>(fighter).Pressed = default;
    }

    // Acceptance: hitscan has no flight time. A pistol whose blow lands with no windup damages a target
    // 30 m away on the first tick after the press — the tick the state machine lands any zero-windup
    // blow — where a sword's sweep from the same spot reaches nothing.
    [Xunit.Fact]
    public void ARayAttackDamagesATargetThirtyMetresAwayInOneTick()
    {
        using var app = NewGame();
        var world = app.World;
        var shooter = Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -30), "pistol");
        var target = Body(world, new Vector3(0, 0, -30), "target", Vector3.Zero);
        var damage = new EventProbe<Damaged>(world);

        Press(app, shooter);                              // tick 0: the press starts the (zero) windup
        Assert.Equal(100f, world.Attribute(target, Health), 3);
        world.RunFixed(Dt);                               // tick 1: the ray lands, 30 m away
        Assert.Equal(75f, world.Attribute(target, Health), 3);

        var hit = Assert.Single(damage.All);
        Assert.Equal(shooter, hit.Hit.Attacker);
        Assert.Equal(target, hit.Hit.Target);
        Assert.InRange(hit.Hit.Point.Z, -30.5f, -29.4f);  // on the target's capsule, not at the ray's end
        Assert.True(hit.Location.IsEmpty);                // no hitboxes yet (#137): the body

        // The same spot with a sword: its sweep reaches 2 m, so nothing.
        world.Get<Melee>(shooter).Attack = new RecordId("sage", "sword");
        Press(app, shooter);
        for (int i = 0; i < 30; i++) world.RunFixed(Dt);
        Assert.Single(damage.All);
    }

    // `pellets` rays per shot, each its own hit (spread is #136's: today they share a line).
    [Xunit.Fact]
    public void EachPelletLandsItsOwnHit()
    {
        using var app = NewGame();
        var world = app.World;
        var shooter = Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -10), "shotgun");
        var target = Body(world, new Vector3(0, 0, -10), "target", Vector3.Zero);
        var damage = new EventProbe<Damaged>(world);

        Press(app, shooter);
        world.RunFixed(Dt);

        Assert.Equal(3, damage.All.Count(d => d.Hit.Target == target));
        Assert.Equal(85f, world.Attribute(target, Health), 3);
    }

    // A ray is a physics query like a swing: a wall in the way stops it and is not hurt.
    [Xunit.Fact]
    public void SceneryStopsARay()
    {
        using var app = NewGame();
        var world = app.World;
        var shooter = Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -20), "pistol");
        var target = Body(world, new Vector3(0, 0, -20), "target", Vector3.Zero);
        var wall = world.Create(Transform.At(new Vector3(0, 1, -10)), "wall");
        world.Add(wall, Collider.Box(new Vector3(4, 3, 0.4f)));
        var damage = new EventProbe<Damaged>(world);

        Press(app, shooter);
        for (int i = 0; i < 5; i++) world.RunFixed(Dt);

        Assert.Empty(damage.All);
        Assert.Equal(100f, world.Attribute(target, Health), 3);
    }

    // A `projectile` delivery throws the carrier abilities fly on; it lands through ApplyHit where it
    // stops, credited to the thrower, after the flight time and not before.
    [Xunit.Fact]
    public void AProjectileAttackFliesAndLandsThroughTheSamePipeline()
    {
        using var app = NewGame();
        var world = app.World;
        var thrower = Body(world, Vector3.Zero, "thrower", new Vector3(0, 0, -12), "sling");
        var target = Body(world, new Vector3(0, 0, -12), "target", Vector3.Zero);
        var damage = new EventProbe<Damaged>(world);

        Press(app, thrower);
        world.RunFixed(Dt);                                // released: one carrier in flight
        var flying = world.Query<Projectile>();
        int inFlight = 0;
        foreach (var (projectiles, _) in flying.Chunks) inFlight += projectiles.Span.Length;
        Assert.Equal(1, inFlight);
        Assert.Equal(100f, world.Attribute(target, Health), 3);

        int ticks = 0;
        while (damage.All.Count == 0 && ticks < 120) { world.RunFixed(Dt); ticks++; }

        // ~11.2 m at 30 m/s is ~22 ticks; the point is that it took a flight, and landed.
        Assert.InRange(ticks, 15, 30);
        var hit = Assert.Single(damage.All);
        Assert.Equal(thrower, hit.Hit.Attacker);
        Assert.Equal(target, hit.Hit.Target);
        Assert.Equal(85f, world.Attribute(target, Health), 3);
    }

    // ApplyDamage is a thin wrapper over ApplyHit; a landing's location reaches the Damaged event.
    [Xunit.Fact]
    public void ApplyHitCarriesTheLandingIntoDamaged()
    {
        using var app = NewGame();
        var world = app.World;
        var target = Body(world, Vector3.Zero, "target", Vector3.UnitZ);
        var damage = new EventProbe<Damaged>(world);
        var head = new RecordId("sage", "head");

        var request = new DamageInfo(default, default, default, 10f, Vector3.Zero, -Vector3.UnitZ);
        float applied = Combat.ApplyHit(world, request, new HitResult(target, new Vector3(0, 1.7f, 0), Vector3.UnitZ, target, head));

        Assert.Equal(10f, applied, 3);
        var hit = Assert.Single(damage.All);
        Assert.Equal(target, hit.Hit.Target);
        Assert.Equal(head, hit.Location);
        Assert.Equal(1.7f, hit.Hit.Point.Y, 3);

        Assert.Equal(10f, Combat.ApplyDamage(world, new DamageInfo(default, target, default, 10f, Vector3.Zero, Vector3.UnitY)), 3);
        Assert.True(damage.All[1].Location.IsEmpty);
    }

    // An attack's `delivery` must name a registered one: a typo is a load error with the nearest name.
    [Xunit.Fact]
    public void AnUnknownDeliveryIsALoadErrorThatSaysTheNearestName()
    {
        const string bad = """[{ "type": "attack", "id": "raygun", "delivery": "rey" }]""";
        using var log = new CaptureSink();
        using var app = HeadlessApp.Gameplay().File("data/bad.json", bad).Build();
        var errors = log.Entries.Where(e => e.Level == LogLevel.Error).Select(e => e.Message).ToList();
        Assert.Contains(errors, e => e.Contains("no hit_delivery 'rey'; did you mean 'ray'?"));
    }
}

// Twenty attackers — sweeps, rays, pellets and projectiles, pressing Attack whenever they may — allocate nothing
// over 600 ticks, beside the physics backend's own step. Allocation is measured per thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class HitAllocationTests
{
    public HitAllocationTests() { _ = TestEnv.UserRoot; }

    // Health enough for 600 ticks of blows: nobody dies, so this is the steady state of a fight.
    private static readonly string Records = HitPipelineTests.Records
        .Replace("""{ "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 }""",
                 """{ "type": "attribute", "id": "health", "start": 1000000, "min": 0, "max": 1000000 }""");

    [Xunit.Fact]
    public void TwentyAttackersAllocateNothingOver600Ticks()
    {
        using var app = HitPipelineTests.NewGame(Records);
        var world = app.World;
        var attack = app.Engine.Actions.Get("Attack");
        string[] kinds = { "sword", "pistol", "shotgun", "sling" };
        var attackers = new Entity[20];
        var targets = new Entity[20];
        for (int i = 0; i < attackers.Length; i++)
        {
            float x = i * 4f;
            float reach = kinds[i % kinds.Length] == "sword" ? 1.5f : 15f;
            attackers[i] = HitPipelineTests.Body(world, new Vector3(x, 0, 0), $"attacker{i}", new Vector3(x, 0, -reach), kinds[i % kinds.Length]);
            targets[i] = HitPipelineTests.Body(world, new Vector3(x, 0, -reach), $"target{i}", new Vector3(x, 0, 0));
        }
        void Step()
        {
            for (int i = 0; i < attackers.Length; i++) world.Get<PawnIntent>(attackers[i]).Pressed = default(ActionMask).With(attack);
            world.RunFixed(HitPipelineTests.Dt);
            Profiler.EndFrame();
        }

        for (int i = 0; i < 120; i++) Step();   // warm: every attacker has swung, shot, thrown and recovered
        Assert.All(targets, t => Assert.True(world.Attribute(t, HitPipelineTests.Health) < 1000000f));
        var before = targets.Select(t => world.Attribute(t, HitPipelineTests.Health)).ToArray();

        // Nothing may allocate but the physics backend's own step (40 bytes a tick with a static collider,
        // handoff 2026-09-30 §5), which is not this code; the report shows it by scope.
        long physicsBefore = ScopeBytes("Fixed.Physics");
        var allocated = AllocationProbe.Measure(600, Step);
        long physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(allocated.Bytes - physics == 0, allocated.ToString());
        for (int i = 0; i < targets.Length; i++)   // and the blows kept landing while it was measured
            Assert.True(world.Attribute(targets[i], HitPipelineTests.Health) < before[i], $"target{i} was not hit while measured");
    }

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }
}
