#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Physics as game events and the richer queries (issue #269): the step's trigger and contact buffers
// arrive on the world's event bus (TriggerEntered, TriggerExited, Collided with its impulse,
// CollisionEnded), so a system reads them in code — an impact sound, fall damage — without going through
// entity I/O; RaycastAll reports every hit along a ray and OverlapSphere what a sphere really touches.
public class PhysicsEventTests
{
    public PhysicsEventTests() { _ = TestEnv.UserRoot; }

    internal const float Dt = 1f / 60f;

    internal static Engine NewEngine() => HeadlessApp.Bare().With(new PhysicsModule()).Build().Engine;

    internal static Pose At(Vector3 position) => new() { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One };

    private static Entity Box(World world, Vector3 center, Vector3 size, string name, bool trigger = false, byte layer = 0)
    {
        var entity = world.Create(Transform.At(center), name);
        world.Add(entity, new Collider { Shape = ColliderShape.Box, Size = size, IsTrigger = trigger, Layer = layer });
        return entity;
    }

    // What a game system does with them: reads each kind with its own cursor, after the physics plugin
    // has sent them. Kept as lists here; AllocationTests' listener only counts.
    [System("test.physics_events.listener", Phase.PostPhysics, After = new[] { "sage.physics.events" })]
    internal sealed class Listener : ISystem
    {
        private readonly EventReader<TriggerEntered> _entered;
        private readonly EventReader<TriggerExited> _exited;
        private readonly EventReader<Collided> _collided;
        private readonly EventReader<CollisionEnded> _ended;
        public readonly List<TriggerEntered> Entered = new();
        public readonly List<TriggerExited> Exited = new();
        public readonly List<Collided> Collisions = new();
        public readonly List<CollisionEnded> Ended = new();
        public bool Keep = true;
        public int Count;
        public float TotalImpulse;

        public Listener(World world)
        {
            _entered = world.Events.Reader<TriggerEntered>(this);
            _exited = world.Events.Reader<TriggerExited>(this);
            _collided = world.Events.Reader<Collided>(this);
            _ended = world.Events.Reader<CollisionEnded>(this);
        }

        public void Run(in SystemContext ctx)
        {
            foreach (ref readonly var e in _entered.Read()) { Count++; if (Keep) Entered.Add(e); }
            foreach (ref readonly var e in _exited.Read()) { Count++; if (Keep) Exited.Add(e); }
            foreach (ref readonly var e in _collided.Read()) { Count++; TotalImpulse += e.Impulse; if (Keep) Collisions.Add(e); }
            foreach (ref readonly var e in _ended.Read()) { Count++; if (Keep) Ended.Add(e); }
        }
    }

    // A crate that asks for contacts falls through a trigger volume onto the floor: a system reading the
    // bus sees it enter and leave the volume and land once, with the speed it hit at and the impulse that
    // stopped it (mass times speed against a static), then lift off. The spans say the same things.
    [Fact]
    public void TriggersAndContactsArriveOnTheEventBusWithTheImpactsImpulse()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();
        var listener = new Listener(world);
        world.AddSystem(listener);

        var ground = Box(world, new Vector3(0, -0.5f, 0), new Vector3(20, 1, 20), "ground");
        var zone = Box(world, new Vector3(0, 2f, 0), new Vector3(4, 1, 4), "zone", trigger: true);   // y 1.5 to 2.5
        var crate = world.Create(Transform.At(new Vector3(0, 4f, 0)), "crate");                     // bottom at 3.5
        world.Add(crate, Collider.Box(Vector3.One) with { ReportContacts = true });
        world.Add(crate, RigidBody.Dynamic(2f));

        int spanBegins = 0, spanEnters = 0;
        for (int i = 0; i < 240; i++)
        {
            world.RunFixed(Dt);
            spanBegins += physics.ContactBegin.Length;
            spanEnters += physics.TriggerEnter.Length;
        }

        var entered = Assert.Single(listener.Entered);
        Assert.Equal(new TriggerEntered(zone, crate), entered);
        var exited = Assert.Single(listener.Exited);
        Assert.Equal(new TriggerExited(zone, crate), exited);
        Assert.Equal(1, spanEnters);

        var landing = Assert.Single(listener.Collisions);
        Assert.Equal(1, spanBegins);
        Assert.Equal(ground, landing.OtherThan(crate));
        Assert.Equal(crate, landing.OtherThan(ground));
        Vector3 up = landing.A == crate ? landing.Normal : -landing.Normal;
        Assert.True(up.Y > 0.9f, $"the normal should push the crate up, was {up}");

        // Dropped 3.5 m: about 8.3 m/s at the floor (a tick either way of the contact beginning).
        float expected = MathF.Sqrt(2f * 9.81f * 3.5f);
        Assert.InRange(landing.Speed, expected * 0.85f, expected * 1.1f);
        Assert.Equal(2f * landing.Speed, landing.Impulse, 2);   // 2 kg against something immovable
        Assert.Empty(listener.Ended);

        // Lifted off: the contact ends, on the bus as well.
        physics.SetPose(world.Get<PhysicsBody>(crate), world.Get<Collider>(crate), At(new Vector3(0, 8, 0)));
        world.RunFixed(Dt);
        world.RunFixed(Dt);
        var ended = Assert.Single(listener.Ended);
        Assert.Equal(ground, ended.OtherThan(crate));
    }

    // Two dynamic bodies meeting head on: the impulse goes through the pair's effective mass (here 1 kg
    // and 1 kg, so half a kilogram at their closing speed), and a gentle touch is a small number.
    [Fact]
    public void TheImpulseOfACollisionBetweenTwoBodiesUsesTheirEffectiveMass()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();
        var listener = new Listener(world);
        world.AddSystem(listener);

        // High above anything, falling together, so gravity does not change their closing speed.
        var left = world.Create(Transform.At(new Vector3(-2, 500, 0)), "left");
        world.Add(left, Collider.Sphere(0.5f) with { ReportContacts = true });
        world.Add(left, RigidBody.Dynamic(1f));
        var right = world.Create(Transform.At(new Vector3(2, 500, 0)), "right");
        world.Add(right, Collider.Sphere(0.5f));
        world.Add(right, RigidBody.Dynamic(1f));
        world.RunFixed(Dt);
        physics.SetVelocity(world.Get<PhysicsBody>(left), new Vector3(3, 0, 0));
        physics.SetVelocity(world.Get<PhysicsBody>(right), new Vector3(-3, 0, 0));

        for (int i = 0; i < 60 && listener.Collisions.Count == 0; i++) world.RunFixed(Dt);

        var hit = Assert.Single(listener.Collisions);
        Assert.Equal(right, hit.OtherThan(left));
        Assert.Equal(6f, hit.Speed, 1);
        Assert.Equal(0.5f * hit.Speed, hit.Impulse, 2);
    }

    // RaycastAll: every collider along the ray, nearest first, one hit each; triggers only when asked;
    // a short buffer keeps the nearest; `ignore` and the mask work as for Raycast.
    [Fact]
    public void RaycastAllReportsEveryHitNearestFirst()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var far = Box(world, new Vector3(0, 0, 8), Vector3.One, "far");
        var near = Box(world, new Vector3(0, 0, 2), Vector3.One, "near");
        var zone = Box(world, new Vector3(0, 0, 3.5f), Vector3.One, "zone", trigger: true);
        var middle = Box(world, new Vector3(0, 0, 5), Vector3.One, "middle", layer: 3);
        Box(world, new Vector3(4, 0, 5), Vector3.One, "beside");   // not on the ray
        world.RunFixed(Dt);   // statics enter the space on the first tick

        var hits = new RayHit[8];
        int count = physics.RaycastAll(Vector3.Zero, Vector3.UnitZ, 20f, hits);
        Assert.Equal(3, count);
        Assert.Equal(new[] { near, middle, far }, new[] { hits[0].Entity, hits[1].Entity, hits[2].Entity });
        Assert.Equal(1.5f, hits[0].Distance, 3);
        Assert.Equal(4.5f, hits[1].Distance, 3);
        Assert.Equal(7.5f, hits[2].Distance, 3);
        Assert.True(hits[0].Hit);
        Assert.True(hits[0].Normal.Z < -0.9f, $"the normal faces the ray, was {hits[0].Normal}");
        Assert.Equal(new Vector3(0, 0, 1.5f), hits[0].Position);

        // The nearest is the one Raycast reports.
        Assert.Equal(near, physics.Raycast(Vector3.Zero, Vector3.UnitZ, 20f).Entity);

        Assert.Equal(4, physics.RaycastAll(Vector3.Zero, Vector3.UnitZ, 20f, hits, includeTriggers: true));
        Assert.Equal(zone, hits[1].Entity);

        var two = new RayHit[2];
        Assert.Equal(2, physics.RaycastAll(Vector3.Zero, Vector3.UnitZ, 20f, two));
        Assert.Equal(new[] { near, middle }, new[] { two[0].Entity, two[1].Entity });

        Assert.Equal(2, physics.RaycastAll(Vector3.Zero, Vector3.UnitZ, 20f, hits, ignore: near));
        Assert.Equal(middle, hits[0].Entity);
        Assert.Equal(2, physics.RaycastAll(Vector3.Zero, Vector3.UnitZ, 20f, hits, LayerMask.All.Except(3)));
        Assert.Equal(new[] { near, far }, new[] { hits[0].Entity, hits[1].Entity });
        Assert.Equal(2, physics.RaycastAll(Vector3.Zero, Vector3.UnitZ, 6f, hits));   // `far` is out of reach
        Assert.Equal(0, physics.RaycastAll(Vector3.Zero, -Vector3.UnitZ, 20f, hits));
    }

    // OverlapSphere tests the sphere, not its bounds: a box whose corner is inside the sphere's bounding
    // box but outside the sphere is not reported, while OverlapBox over the same bounds reports it.
    [Fact]
    public void OverlapSphereReportsWhatTheSphereTouchesNotItsBounds()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("physics");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var touched = Box(world, new Vector3(1.2f, 0, 0), Vector3.One, "touched");          // near face at x 0.7
        var corner = Box(world, new Vector3(1.3f, 1.3f, 0), Vector3.One, "corner");        // nearest corner 1.13 m away
        Box(world, new Vector3(4, 0, 0), Vector3.One, "far");
        var zone = Box(world, new Vector3(-1.2f, 0, 0), Vector3.One, "zone", trigger: true);
        world.RunFixed(Dt);

        var hits = new OverlapHit[8];
        int count = physics.OverlapSphere(Vector3.Zero, 1f, hits);
        var hit = Assert.Single(hits.AsSpan(0, count).ToArray());
        Assert.Equal(touched, hit.Entity);
        Assert.Equal(0.3f, hit.Depth, 2);
        Assert.True(hit.Normal.X < -0.9f, $"the way out is away from the box, was {hit.Normal}");

        var bounds = new Entity[8];
        int byBounds = physics.OverlapBox(Vector3.Zero, Vector3.One, bounds);
        Assert.Contains(corner, bounds.AsSpan(0, byBounds).ToArray());

        Assert.Equal(2, physics.OverlapSphere(Vector3.Zero, 1f, hits, includeTriggers: true));
        Assert.Contains(zone, new[] { hits[0].Entity, hits[1].Entity });
        Assert.Equal(0, physics.OverlapSphere(Vector3.Zero, 1f, hits, ignore: touched));
    }
}

// The event path allocates nothing per tick: a crate dropped through a trigger onto the floor over and
// over (entered, exited, landed, lifted off), read by a system, with RaycastAll and OverlapSphere asked
// every tick, adds nothing over a world with one loose body (the step itself allocates about 40 bytes a
// tick inside Bepu, TODO #41, in both).
[Xunit.Collection(MeasurementsCollection.Name)]
public class PhysicsEventAllocationTests
{
    public PhysicsEventAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void PhysicsEventsAndTheNewQueriesAllocateNothingPerTick()
    {
        using var engine = PhysicsEventTests.NewEngine();

        var loose = engine.CreateWorld("loose");
        var ball = loose.Create(Transform.At(new Vector3(0, 1000, 0)), "ball");
        loose.Resources.Get<IPhysicsWorld>().AddBody(ball, Collider.Sphere(0.1f), RigidBody.Dynamic(1f), PhysicsEventTests.At(new Vector3(0, 1000, 0)));

        var world = engine.CreateWorld("events");
        var physics = world.Resources.Get<IPhysicsWorld>();
        var listener = new PhysicsEventTests.Listener(world) { Keep = false };
        world.AddSystem(listener);
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(20, 1, 20)));
        var zone = world.Create(Transform.At(new Vector3(0, 2f, 0)), "zone");
        world.Add(zone, new Collider { Shape = ColliderShape.Box, Size = new Vector3(4, 1, 4), IsTrigger = true });
        var crate = world.Create(Transform.At(new Vector3(0, 4f, 0)), "crate");
        world.Add(crate, Collider.Box(Vector3.One) with { ReportContacts = true });
        world.Add(crate, RigidBody.Dynamic(1f));
        world.RunFixed(PhysicsEventTests.Dt);
        var body = world.Get<PhysicsBody>(crate);
        var collider = world.Get<Collider>(crate);

        var rays = new RayHit[4];
        var overlaps = new OverlapHit[4];
        int tick = 0, found = 0;
        void Loose() => loose.RunFixed(PhysicsEventTests.Dt);
        void Events()
        {
            if (tick++ % 60 == 0)   // drop it again
            {
                physics.SetPose(body, collider, PhysicsEventTests.At(new Vector3(0, 4f, 0)));
                physics.SetVelocity(body, new Vector3(0, -2f, 0));
            }
            world.RunFixed(PhysicsEventTests.Dt);
            found += physics.RaycastAll(new Vector3(0, 10, 0), -Vector3.UnitY, 20f, rays, includeTriggers: true);
            found += physics.OverlapSphere(Vector3.Zero, 1f, overlaps);
        }

        for (int i = 0; i < 240; i++) { Loose(); Events(); }   // warm both, through several drops
        int before = listener.Count;
        var baseline = AllocationProbe.Measure(240, Loose);
        var withEvents = AllocationProbe.Measure(240, Events);

        Assert.True(withEvents.Bytes - baseline.Bytes <= 0, $"the events added {withEvents.Bytes - baseline.Bytes} bytes over 240 ticks\n{withEvents}");
        Assert.True(listener.Count - before >= 4 * 4, $"four drops should each enter, leave, land and lift off; saw {listener.Count - before} events");
        Assert.True(listener.TotalImpulse > 0f);
        Assert.True(found > 0);
    }
}
