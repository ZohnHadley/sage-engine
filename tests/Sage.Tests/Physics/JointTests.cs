#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

#pragma warning disable SAGE0134 // joints and collision groups are what these test

// Joints and collision groups in the physics facade (issue #242, phase 4k): through IPhysicsWorld, as
// gameplay and the ragdoll will use them.
public class JointTests
{
    public JointTests() { _ = TestEnv.UserRoot; }

    internal const float Dt = 1f / 60f;
    private const float Degree = MathF.PI / 180f;

    internal static Engine NewEngine() => HeadlessApp.Bare().With(new PhysicsModule()).Build().Engine;

    internal static Pose At(Vector3 position) => new() { Position = position, Rotation = Quaternion.Identity, Scale = Vector3.One };

    private static (Entity Entity, PhysicsBody Body) Dynamic(World world, IPhysicsWorld physics, in Collider collider, Vector3 at, float mass = 1f, int group = 0)
    {
        var entity = world.Create(Transform.At(at), "body");
        return (entity, physics.AddBody(entity, collider, RigidBody.Dynamic(mass), At(at), group));
    }

    // The angle of `rotation` about `axis` (its twist), in radians.
    private static float TwistAbout(Quaternion rotation, Vector3 axis)
    {
        float along = Vector3.Dot(new Vector3(rotation.X, rotation.Y, rotation.Z), axis);
        float angle = 2f * MathF.Atan2(along, rotation.W);
        if (angle > MathF.PI) angle -= 2f * MathF.PI;
        if (angle < -MathF.PI) angle += 2f * MathF.PI;
        return angle;
    }

    // A bob on a 2 m ball joint to the world, let go level with its pivot: it swings down and up the
    // other side, and never stretches or shrinks the joint.
    [Fact]
    public void APendulumSwingsAndKeepsItsLength()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("joints");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var pivot = new Vector3(0, 10, 0);
        var (_, bob) = Dynamic(world, physics, Collider.Sphere(0.1f), pivot + new Vector3(2, 0, 0));
        var joint = physics.AddJoint(bob, JointDesc.FromWorld(JointKind.Ball, physics.PoseOf(bob), Pose.Identity, pivot));
        Assert.True(physics.JointExists(joint));
        Assert.Equal(1, physics.JointCount);
        Assert.Equal(1, physics.BodyCount);   // the joint's world anchor is not a body anyone sees

        float lowest = float.MaxValue, furthestBack = float.MaxValue, worstStretch = 0f;
        for (int i = 0; i < 150; i++)
        {
            world.RunFixed(Dt);
            var at = physics.PoseOf(bob).Position;
            lowest = MathF.Min(lowest, at.Y);
            furthestBack = MathF.Min(furthestBack, at.X);
            worstStretch = MathF.Max(worstStretch, MathF.Abs(Vector3.Distance(at, pivot) - 2f));
        }

        Assert.True(lowest < pivot.Y - 1.9f, $"it swung down to {lowest}");
        Assert.True(furthestBack < -1.5f, $"it swung up the other side to x = {furthestBack}");
        Assert.True(worstStretch < 0.05f, $"the joint stretched by {worstStretch} m");
    }

    // A rod hung from the world by a ball joint with a 30° cone and ±20° of twist, shoved hard sideways
    // and spun about its own length: it reaches its limits and does not go past them.
    [Fact]
    public void ABallJointHoldsItsSwingConeAndTwistLimitsUnderAShove()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("joints");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var top = new Vector3(0, 10, 0);
        var (_, rod) = Dynamic(world, physics, Collider.Box(new Vector3(0.2f, 1f, 0.2f)), top - new Vector3(0, 0.5f, 0));
        var desc = JointDesc.FromWorld(JointKind.Ball, physics.PoseOf(rod), Pose.Identity, top, Vector3.UnitY);
        desc.Swing = 30f * Degree;
        desc.TwistMin = -20f * Degree;
        desc.TwistMax = 20f * Degree;
        physics.AddJoint(rod, desc);

        physics.ApplyImpulse(rod, new Vector3(8, 0, 0), top - new Vector3(0, 1f, 0));   // at its foot
        physics.SetAngularVelocity(rod, physics.AngularVelocityOf(rod) + new Vector3(0, 15, 0));

        float widestSwing = 0f, widestTwist = 0f;
        for (int i = 0; i < 120; i++)
        {
            world.RunFixed(Dt);
            var rotation = physics.PoseOf(rod).Rotation;
            var down = Vector3.Transform(Vector3.UnitY, rotation);
            widestSwing = MathF.Max(widestSwing, MathF.Acos(Math.Clamp(Vector3.Dot(down, Vector3.UnitY), -1f, 1f)));
            widestTwist = MathF.Max(widestTwist, MathF.Abs(TwistAbout(rotation, down)));
        }

        Assert.True(widestSwing > 25f * Degree, $"the shove swung it {widestSwing / Degree:F1}°");
        Assert.True(widestSwing < 34f * Degree, $"it swung {widestSwing / Degree:F1}° past a 30° cone");
        Assert.True(widestTwist > 15f * Degree, $"the spin twisted it {widestTwist / Degree:F1}°");
        Assert.True(widestTwist < 24f * Degree, $"it twisted {widestTwist / Degree:F1}° past ±20°");
    }

    // A door hinged to the world at its edge, about the vertical, swings open to its 45° stop when spun
    // one way and to its -30° stop the other, and only ever about its hinge.
    [Fact]
    public void AHingeStopsAtItsLimits()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("joints");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var edge = new Vector3(0, 1, 0);
        var (_, door) = Dynamic(world, physics, Collider.Box(new Vector3(1f, 2f, 0.1f)), edge + new Vector3(0.5f, 0, 0), mass: 10f);
        var desc = JointDesc.FromWorld(JointKind.Hinge, physics.PoseOf(door), Pose.Identity, edge, Vector3.UnitY);
        desc.HingeMin = -30f * Degree;
        desc.HingeMax = 45f * Degree;
        physics.AddJoint(door, desc);

        float Spin(float speed)
        {
            physics.SetAngularVelocity(door, new Vector3(0, speed, 0));
            float furthest = speed > 0 ? float.MinValue : float.MaxValue;   // the furthest it gets the way it is spun
            for (int i = 0; i < 60; i++)
            {
                world.RunFixed(Dt);
                var rotation = physics.PoseOf(door).Rotation;
                float angle = TwistAbout(rotation, Vector3.UnitY);
                furthest = speed > 0 ? MathF.Max(furthest, angle) : MathF.Min(furthest, angle);
                // Only about the hinge: the door's up stays up.
                Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitY, rotation), Vector3.UnitY) > 0.995f);
            }
            return furthest;
        }

        float open = Spin(6f);
        Assert.InRange(open / Degree, 40f, 49f);
        float shut = Spin(-6f);
        Assert.InRange(shut / Degree, -34f, -25f);
    }

    // A 50 kg crate on a joint that gives at 100 N: it breaks on the first step, is reported once, and
    // the crate falls. One that holds 1000 N keeps its crate.
    [Fact]
    public void ABrokenJointIsReportedOnce()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("joints");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var (weakCrate, weakBody) = Dynamic(world, physics, Collider.Box(Vector3.One), new Vector3(0, 10, 0), mass: 50f);
        var weak = JointDesc.FromWorld(JointKind.Ball, physics.PoseOf(weakBody), Pose.Identity, new Vector3(0, 10.5f, 0));
        weak.BreakForce = 100f;
        var weakJoint = physics.AddJoint(weakBody, weak);

        var (_, strongBody) = Dynamic(world, physics, Collider.Box(Vector3.One), new Vector3(5, 10, 0), mass: 50f);
        var strong = JointDesc.FromWorld(JointKind.Ball, physics.PoseOf(strongBody), Pose.Identity, new Vector3(5, 10.5f, 0));
        strong.BreakForce = 1000f;
        var strongJoint = physics.AddJoint(strongBody, strong);

        int reports = 0;
        for (int i = 0; i < 60; i++)
        {
            world.RunFixed(Dt);
            foreach (var broken in physics.JointBroken)
            {
                reports++;
                Assert.Equal(weakJoint, broken.Joint);
                Assert.Equal(weakCrate, broken.A);
                Assert.True(broken.B.IsNull);   // the world
                Assert.True(broken.Force > 100f);
            }
        }

        Assert.Equal(1, reports);
        Assert.False(physics.JointExists(weakJoint));
        Assert.True(physics.JointExists(strongJoint));
        Assert.Equal(1, physics.JointCount);
        Assert.True(physics.PoseOf(weakBody).Position.Y < 7f, "the crate fell once its joint broke");
        Assert.InRange(physics.PoseOf(strongBody).Position.Y, 9.9f, 10.1f);

        physics.RemoveJoint(weakJoint);   // stale: ignored
        Assert.Equal(1, physics.JointCount);
    }

    // Bodies sharing a nonzero group pass through each other; bodies in different groups, or none,
    // still collide.
    [Fact]
    public void SameGroupBodiesPassThroughEachOtherAndOtherGroupsDont()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("joints");
        var physics = world.Resources.Get<IPhysicsWorld>();

        PhysicsBody Floor(float x, int group)
        {
            var entity = world.Create(Transform.At(new Vector3(x, 0, 0)), "floor");
            return physics.AddBody(entity, Collider.Box(new Vector3(3, 1, 3)), new RigidBody { Kind = BodyKind.Static }, At(new Vector3(x, 0, 0)), group);
        }

        var shared = Floor(0, 7);
        var other = Floor(10, 8);
        Floor(20, 7);
        Assert.Equal(7, physics.GroupOf(shared));
        Assert.Equal(8, physics.GroupOf(other));

        var (_, through) = Dynamic(world, physics, Collider.Sphere(0.4f), new Vector3(0, 3, 0), group: 7);
        var (_, lands) = Dynamic(world, physics, Collider.Sphere(0.4f), new Vector3(10, 3, 0), group: 7);
        var (_, regrouped) = Dynamic(world, physics, Collider.Sphere(0.4f), new Vector3(20, 3, 0));
        physics.SetGroup(regrouped, 7);   // the same as AddBody's group, set afterwards

        // And between bodies: a ball dropped onto a (kinematic) plank of its group falls through it.
        var plank = world.Create(Transform.At(new Vector3(30, 1, 0)), "plank");
        var lower = physics.AddBody(plank, Collider.Box(new Vector3(2, 0.2f, 2)), RigidBody.Kinematic(), At(new Vector3(30, 1, 0)), group: 3);
        var (_, upper) = Dynamic(world, physics, Collider.Sphere(0.3f), new Vector3(30, 3, 0), group: 3);
        var (_, stranger) = Dynamic(world, physics, Collider.Sphere(0.3f), new Vector3(30.5f, 3, 0.5f), group: 4);

        for (int i = 0; i < 90; i++) world.RunFixed(Dt);

        Assert.True(physics.PoseOf(through).Position.Y < -2f, "a body fell through a floor of its own group");
        Assert.True(physics.PoseOf(regrouped).Position.Y < -2f, "SetGroup puts a body in a group");
        Assert.InRange(physics.PoseOf(lands).Position.Y, 0.8f, 1.0f);   // on a floor of another group
        Assert.True(physics.PoseOf(upper).Position.Y < physics.PoseOf(lower).Position.Y, "a ball fell through a body of its own group");
        Assert.True(physics.PoseOf(stranger).Position.Y > physics.PoseOf(lower).Position.Y, "a ball of another group landed on it");
    }

    // A lamp hanging from the world comes to rest and sleeps like any body, and an impulse wakes it and
    // swings it; the joint's anchor is never counted as a body.
    [Fact]
    public void AJointedBodySleepsAndAnImpulseWakesIt()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("joints");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var hook = new Vector3(0, 6, 0);
        var (_, lamp) = Dynamic(world, physics, Collider.Sphere(0.2f), hook - new Vector3(0, 1, 0));
        var desc = JointDesc.FromWorld(JointKind.Ball, physics.PoseOf(lamp), Pose.Identity, hook);
        desc.Swing = 60f * Degree;
        physics.AddJoint(lamp, desc);

        for (int i = 0; i < 300 && physics.IsAwake(lamp); i++) world.RunFixed(Dt);
        Assert.False(physics.IsAwake(lamp), "a lamp at rest on its joint sleeps");
        Assert.Equal(0, physics.BodyCount);

        physics.ApplyImpulse(lamp, new Vector3(2, 0, 0), physics.PoseOf(lamp).Position);
        Assert.True(physics.IsAwake(lamp));
        Assert.Equal(1, physics.BodyCount);
        float furthest = 0f;
        for (int i = 0; i < 30; i++)
        {
            world.RunFixed(Dt);
            furthest = MathF.Max(furthest, physics.PoseOf(lamp).Position.X);
            Assert.InRange(Vector3.Distance(physics.PoseOf(lamp).Position, hook), 0.95f, 1.05f);
        }
        Assert.True(furthest > 0.3f, $"the impulse swung it {furthest} m");
    }

    // phys_debug draws a joint as a yellow line between its anchors (and the world anchor's body, which
    // has no shape, draws nothing).
    [Fact]
    public void TheDebugDrawShowsAJointBetweenItsAnchors()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("joints");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var hook = new Vector3(0, 6, 0);
        var (_, lamp) = Dynamic(world, physics, Collider.Sphere(0.2f), hook - new Vector3(0, 1, 0));
        var desc = JointDesc.FromWorld(JointKind.Distance, physics.PoseOf(lamp), Pose.Identity, hook);
        desc.AnchorA = Vector3.Zero;   // a rope to the lamp's centre
        physics.AddJoint(lamp, desc);

        var debug = new DebugDraw { Enabled = true };
        physics.DrawDebug(debug, Vector3.Zero, float.PositiveInfinity);
        var lines = new System.Collections.Generic.List<DebugLine>();
        debug.CopyTo(lines);
        Assert.Contains(lines, l => l.Rgba == DebugColour.Yellow &&
                                    Vector3.Distance(l.A, hook - Vector3.UnitY) < 1e-3f && Vector3.Distance(l.B, hook) < 1e-3f);
        Assert.DoesNotContain(lines, l => l.Rgba == DebugColour.Cyan && Vector3.Distance(l.A, hook) < 0.25f);
    }

    // Removing a body takes its joints with it; a joint between two bodies and one to a static work like
    // one to the world.
    [Fact]
    public void RemovingABodyRemovesItsJoints()
    {
        using var engine = NewEngine();
        var world = engine.CreateWorld("joints");
        var physics = world.Resources.Get<IPhysicsWorld>();

        var postEntity = world.Create(Transform.At(new Vector3(0, 5, 0)), "post");
        var post = physics.AddBody(postEntity, Collider.Box(new Vector3(0.2f, 10, 0.2f)), new RigidBody { Kind = BodyKind.Static }, At(new Vector3(0, 5, 0)));
        var (_, sign) = Dynamic(world, physics, Collider.Box(new Vector3(1, 0.5f, 0.05f)), new Vector3(0.7f, 8, 0));
        var (_, tassel) = Dynamic(world, physics, Collider.Sphere(0.05f), new Vector3(1.1f, 7.5f, 0));

        var signPose = physics.PoseOf(sign);
        var hinge = JointDesc.FromWorld(JointKind.Hinge, signPose, physics.PoseOf(post), new Vector3(0.2f, 8.25f, 0), Vector3.UnitX);
        var toPost = physics.AddJoint(sign, post, hinge);
        var rope = JointDesc.FromWorld(JointKind.Distance, physics.PoseOf(tassel), signPose, new Vector3(1.1f, 7.75f, 0));
        rope.AnchorA = Vector3.Zero;   // the tassel's centre, 0.25 m below the sign's corner
        var toSign = physics.AddJoint(tassel, sign, rope);
        Assert.Equal(2, physics.JointCount);

        for (int i = 0; i < 60; i++) world.RunFixed(Dt);
        Assert.InRange(physics.PoseOf(sign).Position.Y, 7.5f, 8.5f);   // hanging from its hinge
        Assert.True(Vector3.Distance(physics.PoseOf(tassel).Position, physics.PoseOf(sign).Position) < 1.2f);

        physics.RemoveBody(sign);
        Assert.False(physics.JointExists(toPost));
        Assert.False(physics.JointExists(toSign));
        Assert.Equal(0, physics.JointCount);
        for (int i = 0; i < 60; i++) world.RunFixed(Dt);
        Assert.True(physics.PoseOf(tassel).Position.Y < 6f, "the tassel fell once the sign was gone");
    }
}

// A chain of twenty links steps without allocating anything of its own: what a 20-link chain adds
// over a world with one loose body is nothing (and the step itself allocates nothing either, #273).
[Xunit.Collection(MeasurementsCollection.Name)]
public class JointAllocationTests
{
    public JointAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void AChainOfTwentyLinksAllocatesNothingPerTick()
    {
        using var engine = JointTests.NewEngine();

        var loose = engine.CreateWorld("loose");
        var loosePhysics = loose.Resources.Get<IPhysicsWorld>();
        var ball = loose.Create(Transform.At(new Vector3(0, 1000, 0)), "ball");
        loosePhysics.AddBody(ball, Collider.Sphere(0.1f), RigidBody.Dynamic(1f), JointTests.At(new Vector3(0, 1000, 0)));

        var chained = engine.CreateWorld("chain");
        var physics = chained.Resources.Get<IPhysicsWorld>();
        var top = new Vector3(0, 30, 0);
        PhysicsBody previous = default;
        var links = new PhysicsBody[20];
        for (int i = 0; i < links.Length; i++)
        {
            var centre = top - new Vector3(0, 0.25f + i * 0.5f, 0) + new Vector3(i * 0.05f, 0, 0);   // a little off plumb: it swings
            var entity = chained.Create(Transform.At(centre), $"link {i}");
            links[i] = physics.AddBody(entity, Collider.Capsule(0.05f, 0.4f), RigidBody.Dynamic(1f), JointTests.At(centre), group: 1);
            var pose = physics.PoseOf(links[i]);
            var anchor = centre + new Vector3(0, 0.25f, 0);
            var desc = i == 0
                ? JointDesc.FromWorld(JointKind.Ball, pose, Pose.Identity, anchor)
                : JointDesc.FromWorld(JointKind.Ball, pose, physics.PoseOf(previous), anchor);
            desc.Swing = 45f * MathF.PI / 180f;
            desc.TwistMin = -0.3f;
            desc.TwistMax = 0.3f;
            desc.BreakForce = 1e6f;   // read every tick, never reached
            if (i == 0) physics.AddJoint(links[i], desc);
            else physics.AddJoint(links[i], previous, desc);
            previous = links[i];
        }
        Assert.Equal(20, physics.JointCount);

        int brokenSeen = 0;
        Vector3 spin = default;
        void Loose() => loose.RunFixed(JointTests.Dt);
        void Chain()
        {
            chained.RunFixed(JointTests.Dt);
            brokenSeen += physics.JointBroken.Length;
            spin += physics.AngularVelocityOf(links[19]);
            _ = physics.BodyCount;
        }

        for (int i = 0; i < 30; i++) { Loose(); Chain(); }   // warm both
        var baseline = AllocationProbe.Measure(120, Loose);
        var withChain = AllocationProbe.Measure(120, Chain);

        Assert.True(withChain.Bytes - baseline.Bytes <= 0, $"the chain added {withChain.Bytes - baseline.Bytes} bytes over 120 ticks\n{withChain}");
        Assert.Equal(0, brokenSeen);
        Assert.Equal(20, physics.JointCount);
        Assert.True(spin != Vector3.Zero, "the chain swung");
    }
}
