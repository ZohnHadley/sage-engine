#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Weapon projectiles (issue #134, docs/design/16 "As built (weapon projectiles)"): an attack's
// `projectile` delivery throws the carrier abilities fly on, now with a prefab, gravity (an arc, swept
// tick by tick) and piercing, and a bolt in flight survives a save. The ability side of the carrier is
// AbilityTests' (its fireball and ember bolt land on the ticks they always did).
public class ProjectileAttackTests
{
    public ProjectileAttackTests() { _ = TestEnv.UserRoot; }

    private const float Dt = HitPipelineTests.Dt;
    private const float Gravity = 9.81f;
    private const float BoltSpeed = 50f;

    private static readonly string Records = HitPipelineTests.Records.TrimEnd().TrimEnd(']') + """
        ,
         { "type": "attack", "id": "crossbow", "delivery": "projectile", "damage": 30, "range": 60, "radius": 0.05,
           "projectileSpeed": 50, "projectileGravity": 9.81, "windupTime": 0, "recoverTime": 0.1, "cooldown": 1 },
         { "type": "attack", "id": "longbow", "base": "crossbow", "projectileGravity": 0, "projectilePierce": 1 }]
        """;

    private static HeadlessApp NewGame() => HitPipelineTests.NewGame(Records);

    private static void Press(HeadlessApp app, Entity fighter)
    {
        var world = app.World;
        world.Get<PawnIntent>(fighter).Pressed = default(ActionMask).With(app.Engine.Actions.Get("Attack"));
        world.RunFixed(Dt);
        world.Get<PawnIntent>(fighter).Pressed = default;
    }

    private static Entity[] InFlight(World world) => world.Query<Transform, Projectile>().Entities.ToEntityList().ToArray();

    // Acceptance: a crossbow bolt falls under its `projectileGravity`. Launched level at 50 m/s it is
    // 0.4 s out when it has flown 20 m, and has dropped ½·g·t² = 0.785 m — exactly, since each tick's
    // chord of the parabola is exact at its ends. Then fired from a crossbow by a character at a target
    // 20 m away: the bolt lands on the parabola from the eye, hurts the target and credits the shooter.
    [Xunit.Fact]
    public void ACrossbowBoltDropsByTheExpectedAmountOverTwentyMetresAndHitsItsTarget()
    {
        using var app = NewGame();
        var world = app.World;
        var crossbow = app.Records.Get<AttackRecord>(new RecordId("sage", "crossbow"));
        Assert.Equal("projectile", crossbow.Delivery);

        // The flight alone: 24 ticks is 0.4 s is 20 m.
        var bolt = world.Launch(default, new RecordId("sage", "crossbow"), crossbow, new Vector3(0, 10, 0), -Vector3.UnitZ);
        for (int i = 0; i < 24; i++) world.RunFixed(Dt);
        Assert.True(world.IsAlive(bolt));
        var at = world.Get<Transform>(bolt).LocalPosition;
        float drop = 0.5f * Gravity * 0.4f * 0.4f;
        Assert.Equal(-20f, at.Z, 2);
        Assert.Equal(10f - drop, at.Y, 2);
        Assert.InRange(drop, 0.78f, 0.79f);
        Assert.True(world.Get<Projectile>(bolt).Velocity.Y < -3.9f);         // falling at g·t, and…
        Assert.True(world.Get<Transform>(bolt).LocalRotation != Quaternion.Identity);   // …nosing down its arc
        world.Destroy(bolt);

        // From a crossbow, at a target 20 m away.
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -20), "crossbow");
        var target = HitPipelineTests.Body(world, new Vector3(0, 0, -20), "target", Vector3.Zero);
        var damage = new EventProbe<Damaged>(world);
        Press(app, shooter);
        world.RunFixed(Dt);

        // Where it was launched: level from the eye, so the height it has already lost says how long it
        // has flown (v² = 2·g·h), and the launch point is 0.4 m ahead of the eye (Hits.Launch).
        var flying = Assert.Single(InFlight(world));
        var first = world.Get<Transform>(flying).LocalPosition;
        float fallen = world.Get<Projectile>(flying).Velocity.Y;
        float launchY = first.Y + fallen * fallen / (2f * Gravity);
        const float launchZ = -0.4f;

        int ticks = 0;
        while (damage.All.Count == 0 && ticks < 120) { world.RunFixed(Dt); ticks++; }

        var hit = Assert.Single(damage.All);
        Assert.Equal(shooter, hit.Hit.Attacker);
        Assert.Equal(target, hit.Hit.Target);
        Assert.Equal(70f, world.Attribute(target, HitPipelineTests.Health), 3);
        Assert.InRange(hit.Hit.Point.Z, -20f, -19f);                         // on the target's capsule

        float flown = launchZ - hit.Hit.Point.Z;
        float expected = 0.5f * Gravity * (flown / BoltSpeed) * (flown / BoltSpeed);
        Assert.Equal(expected, launchY - hit.Hit.Point.Y, 2);
        Assert.InRange(launchY - hit.Hit.Point.Y, 0.6f, 0.8f);               // it fell, most of the 0.785 m
        Assert.Empty(InFlight(world));
    }

    // With no gravity the carrier flies straight, as it always has (the sling in HitPipelineTests); and
    // `projectilePierce` lets it pass through that many targets, landing on each, before one stops it.
    [Xunit.Fact]
    public void APiercingBoltLandsOnEachTargetItPassesThroughUntilOneStopsIt()
    {
        using var app = NewGame();
        var world = app.World;
        var archer = HitPipelineTests.Body(world, Vector3.Zero, "archer", new Vector3(0, 0, -30), "longbow");
        var first = HitPipelineTests.Body(world, new Vector3(0, 0, -8), "first", Vector3.Zero);
        var second = HitPipelineTests.Body(world, new Vector3(0, 0, -14), "second", Vector3.Zero);
        var third = HitPipelineTests.Body(world, new Vector3(0, 0, -20), "third", Vector3.Zero);
        var damage = new EventProbe<Damaged>(world);

        Press(app, archer);
        for (int i = 0; i < 60; i++) world.RunFixed(Dt);

        Assert.Equal(new[] { first, second }, damage.All.Select(d => d.Hit.Target).ToArray());
        Assert.All(damage.All, d => Assert.Equal(archer, d.Hit.Attacker));
        Assert.Equal(70f, world.Attribute(first, HitPipelineTests.Health), 3);
        Assert.Equal(70f, world.Attribute(second, HitPipelineTests.Health), 3);
        Assert.Equal(100f, world.Attribute(third, HitPipelineTests.Health), 3);   // the second stopped it
        Assert.Empty(InFlight(world));
    }

    // Acceptance: a bolt saved mid-flight is still in flight after the load — where it was, as fast,
    // still falling, still the shooter's — and lands on the same tick after the load as it did without it.
    [Xunit.Fact]
    public void ABoltSavedMidFlightStillLandsAfterTheLoad()
    {
        using var app = NewGame();
        var world = app.World;
        var shooter = HitPipelineTests.Body(world, Vector3.Zero, "shooter", new Vector3(0, 0, -20), "crossbow");
        var target = HitPipelineTests.Body(world, new Vector3(0, 0, -20), "target", Vector3.Zero);
        world.MakePersistent(shooter);                     // what a scene or the player would be
        world.MakePersistent(target);

        Press(app, shooter);
        for (int i = 0; i < 10; i++) world.RunFixed(Dt);
        var saved = world.Get<Projectile>(Assert.Single(InFlight(world)));
        var savedAt = world.Get<Transform>(Assert.Single(InFlight(world))).LocalPosition;
        Assert.True(app.Engine.Saves.Save("bolt"));

        var damage = new EventProbe<Damaged>(world);
        int without = 0;
        while (damage.All.Count == 0 && without < 120) { world.RunFixed(Dt); without++; }
        Assert.Single(damage.All);
        Assert.Equal(70f, world.Attribute(target, HitPipelineTests.Health), 3);

        Assert.True(app.Engine.Saves.Load("bolt"));
        target = world.FindByName("target");
        shooter = world.FindByName("shooter");
        Assert.Equal(100f, world.Attribute(target, HitPipelineTests.Health), 3);
        var bolt = Assert.Single(InFlight(world));
        var loaded = world.Get<Projectile>(bolt);
        Assert.Equal(shooter, loaded.Caster);
        Assert.Equal(new RecordId("sage", "crossbow"), loaded.Attack);
        Assert.Equal(saved.Velocity, loaded.Velocity);
        Assert.Equal(saved.Gravity, loaded.Gravity);
        Assert.Equal(saved.Life, loaded.Life, 5);
        Assert.Equal(savedAt, world.Get<Transform>(bolt).LocalPosition);

        var after = new EventProbe<Damaged>(world);
        int ticks = 0;
        while (after.All.Count == 0 && ticks < 120) { world.RunFixed(Dt); ticks++; }
        Assert.Equal(without, ticks);
        var hit = Assert.Single(after.All);
        Assert.Equal(shooter, hit.Hit.Attacker);
        Assert.Equal(target, hit.Hit.Target);
        Assert.Equal(70f, world.Attribute(target, HitPipelineTests.Health), 3);
        Assert.Empty(InFlight(world));
    }
}
