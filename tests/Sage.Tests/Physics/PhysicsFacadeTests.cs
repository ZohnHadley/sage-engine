#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The physics facade (issue #30): what gameplay, levels and entity I/O may ask of a world's physics,
// through IPhysicsWorld and nothing else, so a 2D backend can stand in for Bepu. These go through the
// interface on purpose; PhysicsTests covers the Bepu space itself.
public class PhysicsFacadeTests
{
    public PhysicsFacadeTests() { _ = TestEnv.UserRoot; }

    private static Engine NewEngine() => HeadlessApp.Bare().With(new PhysicsModule()).Build().Engine;

    private static void Tick(World world, int ticks)
    {
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
    }

    private static Pose At(Vector3 position) => new() { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One };

    [Fact]
    public void ThePhysicsPluginInstallsItsSpaceAsTheWorldsIPhysicsWorld()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();
        Assert.Same(world.Resources.Get<PhysicsSpace>(), physics);

        // Bodies through the facade: a static, a dynamic, and removing them again.
        var wall = world.Create(Transform.At(Vector3.Zero), "wall");
        var body = physics.AddBody(wall, Collider.Box(Vector3.One), new RigidBody { Kind = BodyKind.Static }, At(Vector3.Zero));
        var ball = world.Create(Transform.At(new Vector3(0, 5, 0)), "ball");
        var falling = physics.AddBody(ball, Collider.Sphere(0.5f), RigidBody.Dynamic(1f), At(new Vector3(0, 5, 0)));
        Assert.Equal(1, physics.StaticCount);
        Assert.Equal(1, physics.BodyCount);
        Assert.True(physics.IsDynamic(falling));
        Assert.False(physics.IsDynamic(body));

        physics.SetVelocity(falling, new Vector3(0, 3, 0));
        Assert.Equal(3f, physics.VelocityOf(falling).Y, 3);

        physics.RemoveBody(body);
        physics.RemoveBody(falling);
        Assert.Equal(0, physics.StaticCount);
        Assert.Equal(0, physics.BodyCount);
    }

    // The sweep gap (issue #30): a shape cast that starts inside something used to report nothing at
    // all, so a swing pressed against its target missed. Now that is a hit at distance 0, it wins over
    // anything further along, and a caller can leave itself out or ask for the old behaviour.
    [Fact]
    public void ASweepThatStartsInsideSomethingHitsItAtDistanceZero()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var near = world.Create(Transform.At(Vector3.Zero), "near");
        world.Add(near, Collider.Box(new Vector3(1, 1, 1)));
        var far = world.Create(Transform.At(new Vector3(0, 0, -5)), "far");
        world.Add(far, Collider.Box(new Vector3(1, 1, 1)));
        Tick(world, 1);

        var sphere = Collider.Sphere(0.2f);
        var inside = physics.Sweep(sphere, At(Vector3.Zero), -Vector3.UnitZ, 10f);
        Assert.True(inside.Hit);
        Assert.True(inside.StartsInside);
        Assert.Equal(near, inside.Entity);
        Assert.Equal(0f, inside.Distance);
        Assert.Equal(Vector3.UnitZ, inside.Normal);   // -direction: an overlap has no surface

        var past = physics.Sweep(sphere, At(Vector3.Zero), -Vector3.UnitZ, 10f, ignore: near);
        Assert.True(past.Hit);
        Assert.False(past.StartsInside);
        Assert.Equal(far, past.Entity);
        Assert.InRange(past.Distance, 4.2f, 4.4f);    // 5 m less the box's half depth and the radius

        // The character controller's view: only what it moves into.
        var moving = physics.Sweep(sphere, At(Vector3.Zero), -Vector3.UnitZ, 10f, ignoreInitialOverlaps: true);
        Assert.Equal(far, moving.Entity);
        Assert.False(moving.StartsInside);
    }

    [Fact]
    public void QueriesCanLeaveOutTheEntityAsking()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var self = world.Create(Transform.At(Vector3.Zero), "self");
        world.Add(self, Collider.Box(new Vector3(1, 1, 1)));
        var other = world.Create(Transform.At(new Vector3(0, 0, -4)), "other");
        world.Add(other, Collider.Box(new Vector3(1, 1, 1)));
        Tick(world, 1);

        var ray = physics.Raycast(new Vector3(0, 0, 2), -Vector3.UnitZ, 20f, ignore: self);
        Assert.Equal(other, ray.Entity);

        var found = new Entity[8];
        int count = physics.OverlapBox(Vector3.Zero, new Vector3(10), found, ignore: self);
        Assert.Equal(1, count);
        Assert.Equal(other, found[0]);
    }

    // Contact events are opt-in (Collider.ReportContacts): a crate that asks reports landing once, with
    // where and which way, stays "touching" while it sleeps, and reports lifting off.
    [Fact]
    public void AColliderThatAsksForContactsReportsTheirBeginningAndEnd()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(20, 1, 20)));
        var quiet = world.Create(Transform.At(new Vector3(5, 3, 0)), "quiet");   // lands too, says nothing
        world.Add(quiet, Collider.Box(Vector3.One));
        world.Add(quiet, RigidBody.Dynamic(1f));
        var crate = world.Create(Transform.At(new Vector3(0, 3, 0)), "crate");
        world.Add(crate, Collider.Box(Vector3.One) with { ReportContacts = true });
        world.Add(crate, RigidBody.Dynamic(1f));

        var began = new List<ContactEvent>();
        var ended = new List<ContactEvent>();
        void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                world.RunFixed(1f / 60f);
                began.AddRange(physics.ContactBegin.ToArray());
                ended.AddRange(physics.ContactEnd.ToArray());
            }
        }

        Run(600);   // lands, settles and falls asleep
        var landing = Assert.Single(began);
        Assert.Contains(crate, new[] { landing.A, landing.B });
        Assert.Contains(ground, new[] { landing.A, landing.B });
        Vector3 up = landing.A == crate ? landing.Normal : -landing.Normal;   // B to A
        Assert.True(up.Y > 0.9f, $"the normal should push the crate up, was {up}");
        Assert.InRange(landing.Point.Y, -0.1f, 0.1f);                         // on the ground's top face
        Assert.Empty(ended);
        Assert.False(physics.IsAwake(world.Get<PhysicsBody>(crate)), "the crate should be asleep by now");

        // Lifted off: the contact ends.
        var body = world.Get<PhysicsBody>(crate);
        physics.SetPose(body, world.Get<Collider>(crate), At(new Vector3(0, 6, 0)));
        Run(2);
        var liftoff = Assert.Single(ended);
        Assert.Contains(crate, new[] { liftoff.A, liftoff.B });
    }

    // A trigger's "inside" survives the thing inside it falling asleep: Bepu stops testing a pair with
    // nothing awake in it, and that is not the same as leaving.
    [Fact]
    public void SomethingAsleepInATriggerIsStillInsideIt()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(20, 1, 20)));
        var zone = world.Create(Transform.At(new Vector3(0, 1, 0)), "zone");
        world.Add(zone, new Collider { Shape = ColliderShape.Box, Size = new Vector3(4, 2, 4), IsTrigger = true });
        var crate = world.Create(Transform.At(new Vector3(0, 3, 0)), "crate");
        world.Add(crate, Collider.Box(Vector3.One));
        world.Add(crate, RigidBody.Dynamic(1f));

        int entered = 0, exited = 0;
        for (int i = 0; i < 600; i++)
        {
            world.RunFixed(1f / 60f);
            entered += physics.TriggerEnter.ToArray().Count(o => o.Trigger == zone && o.Other == crate);
            exited += physics.TriggerExit.ToArray().Count(o => o.Trigger == zone && o.Other == crate);
        }

        Assert.Equal(1, entered);
        Assert.False(physics.IsAwake(world.Get<PhysicsBody>(crate)), "the crate should be asleep by now");
        Assert.Equal(0, exited);
    }

    // phys_debug draws what the backend holds, through the facade: prefab colliders, triggers in
    // magenta, and brush hulls, which have no Collider component and so were never drawn before.
    [Fact]
    public void PhysicsDebugDrawShowsWhatTheBackendSimulates()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();
        var debug = world.Debug();
        debug.Enabled = true;

        var box = world.Create(Transform.At(Vector3.Zero), "box");
        world.Add(box, Collider.Box(Vector3.One));
        var zone = world.Create(Transform.At(new Vector3(5, 0, 0)), "zone");
        world.Add(zone, new Collider { Shape = ColliderShape.Box, Size = new Vector3(2, 2, 2), IsTrigger = true });
        var brush = world.Create(Transform.At(new Vector3(-5, 0, 0)), "brush");
        var cube = new[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(-1, 1, -1), new Vector3(1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(-1, 1, 1), new Vector3(1, 1, 1),
        };
        Assert.NotEqual(default, physics.AddHull(brush, cube, new Vector3(-5, 0, 0)));

        Tick(world, 1);
        var lines = new List<DebugLine>();
        debug.CopyTo(lines);
        Assert.Empty(lines);                                   // phys_debug is off

        engine.CVars.Execute("phys_debug 1");
        Tick(world, 1);
        debug.CopyTo(lines);
        Assert.Contains(lines, l => l.Rgba == DebugColour.Magenta);                      // the trigger
        Assert.Contains(lines, l => l.Rgba == DebugColour.Cyan && l.A.X < -5.5f);        // the hull's bounds
        Assert.Contains(lines, l => l.Rgba == DebugColour.Cyan && MathF.Abs(l.A.X) <= 0.5f);   // the box
    }

    // Combat through the facade: a creature pressed up against you is inside your swing where it
    // starts, which used to be a miss every time (issue #30's sweep gap).
    [Fact]
    public void ASwingHitsATargetPressedAgainstTheAttacker()
    {
        const string records = """
            [{ "type": "attribute", "id": "health", "start": 100, "min": 0, "max": 100 },
             { "type": "tag", "id": "state.dead" },
             { "type": "effect", "id": "damage", "duration": "Instant",
               "modifiers": [ { "attribute": "health", "op": "Add", "value": -1 } ] },
             { "type": "damage_type", "id": "physical", "effect": "damage" },
             { "type": "attack", "id": "sword", "damage": 20, "damageType": "physical", "reach": 2.0,
               "radius": 0.3, "arcDegrees": 120, "windupTime": 0.2, "recoverTime": 0.1, "cooldown": 0.5 }]
            """;
        using var app = HeadlessApp.Gameplay().File("data/combat.json", records).Boot("combat");
        var world = app.World;
        var physics = world.Resources.Get<IPhysicsWorld>();
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(100, 1, 100)));

        Entity Fighter(Vector3 feet, string name, Vector3 lookAt)
        {
            var entity = world.Create(Transform.At(feet), name);
            world.AddCharacter(entity, physics.Layers.Player);
            world.AddAttributes(entity);
            world.Get<PawnIntent>(entity).Yaw = SageMath.YawTo(feet, lookAt);
            return entity;
        }

        var attacker = Fighter(Vector3.Zero, "attacker", new Vector3(0, 0, -2));
        world.Add(attacker, Melee.With(new RecordId("sage", "sword")));
        var target = Fighter(new Vector3(0, 0, -0.3f), "target", Vector3.Zero);   // capsules overlapping
        var attack = app.Engine.Actions.Get("Attack");

        world.Get<PawnIntent>(attacker).Pressed = world.Get<PawnIntent>(attacker).Pressed.With(attack);
        world.RunFixed(1f / 60f);
        world.Get<PawnIntent>(attacker).Pressed = default;
        Tick(world, 20);   // past the windup

        Assert.Equal(80f, world.Attribute(target, new RecordId("sage", "health")), 3);
        Assert.Equal(100f, world.Attribute(attacker, new RecordId("sage", "health")), 3);
    }
}
