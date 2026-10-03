#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

#pragma warning disable SAGE0134 // joints and Sleep: part of the scenes these test

// Perf and determinism of the physics backend (issue #273, docs/spec/subsystems/08-physics.md REQ-PHYS-22):
// a steady step allocates nothing, the same inputs give the same bits every run, and a rebase leaves a
// sleeping body asleep where it now is.
public class PhysicsPerfTests
{
    public PhysicsPerfTests() { _ = TestEnv.UserRoot; }

    private const float Dt = JointTests.Dt;
    private static Pose At(Vector3 position) => JointTests.At(position);

    // A small busy scene: a floor, a stack of crates, a spray of balls thrown at it, a swinging chain on
    // joints and a trigger. Everything a step does — contacts, stacking, joints, sleeping, triggers.
    internal static List<Entity> BusyScene(World world)
    {
        var physics = world.Resources.Get<IPhysicsWorld>();
        var all = new List<Entity>();
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "floor");
        physics.AddBody(floor, Collider.Box(new Vector3(60, 1, 60)), new RigidBody { Kind = BodyKind.Static }, At(new Vector3(0, -0.5f, 0)));
        var zone = world.Create(Transform.At(new Vector3(0, 1, 0)), "zone");
        physics.AddBody(zone, new Collider { Shape = ColliderShape.Box, Size = new Vector3(6, 2, 6), IsTrigger = true },
                        new RigidBody { Kind = BodyKind.Static }, At(new Vector3(0, 1, 0)));

        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 3; x++)
            {
                var at = new Vector3(x * 1.05f - 1.05f, 0.5f + y * 1.01f, 0);
                var crate = world.Create(Transform.At(at), $"crate {x},{y}");
                world.Add(crate, physics.AddBody(crate, Collider.Box(Vector3.One), RigidBody.Dynamic(2f), At(at)));
                all.Add(crate);
            }
        for (int i = 0; i < 12; i++)
        {
            var at = new Vector3(-8 + i * 0.3f, 2 + (i % 3), 6 - i * 0.2f);
            var ball = world.Create(Transform.At(at), $"ball {i}");
            var body = physics.AddBody(ball, Collider.Sphere(0.2f + 0.02f * i), RigidBody.Dynamic(0.5f + i * 0.1f), At(at));
            physics.SetVelocity(body, new Vector3(6 + i * 0.1f, 1, -3 + i * 0.4f));
            world.Add(ball, body);
            all.Add(ball);
        }
        PhysicsBody previous = default;
        for (int i = 0; i < 6; i++)
        {
            var at = new Vector3(8 + i * 0.5f, 8, 4);
            var link = world.Create(Transform.At(at), $"link {i}");
            var body = physics.AddBody(link, Collider.Capsule(0.08f, 0.35f), RigidBody.Dynamic(1f), At(at), group: 1);
            var anchor = at - new Vector3(0.25f, 0, 0);
            var desc = JointDesc.FromWorld(JointKind.Ball, physics.PoseOf(body), i == 0 ? Pose.Identity : physics.PoseOf(previous), anchor);
            if (i == 0) physics.AddJoint(body, desc);
            else physics.AddJoint(body, previous, desc);
            previous = body;
            world.Add(link, body);
            all.Add(link);
        }
        return all;
    }

    // Every pose and velocity of `bodies`, bit for bit.
    private static void Snapshot(World world, List<Entity> bodies, List<int> into)
    {
        var physics = world.Resources.Get<IPhysicsWorld>();
        foreach (var entity in bodies)
        {
            var body = world.Get<PhysicsBody>(entity);
            var pose = physics.PoseOf(body);
            var v = physics.VelocityOf(body);
            var w = physics.AngularVelocityOf(body);
            Add(into, pose.Position); Add(into, new Vector3(pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z));
            into.Add(BitConverter.SingleToInt32Bits(pose.Rotation.W));
            Add(into, v); Add(into, w);
        }
    }

    private static void Add(List<int> into, Vector3 v)
    {
        into.Add(BitConverter.SingleToInt32Bits(v.X));
        into.Add(BitConverter.SingleToInt32Bits(v.Y));
        into.Add(BitConverter.SingleToInt32Bits(v.Z));
    }

    // One run from a fresh engine: 360 ticks of the busy scene, every body's state every 30 ticks.
    private static List<int> Run(int workers)
    {
        using var engine = JointTests.NewEngine();
        var world = engine.CreateWorld("determinism");
        var space = world.Resources.Get<Sage.Physics3D.PhysicsSpace>();
        if (workers >= 0) space.UseWorkers(workers);
        var bodies = BusyScene(world);
        var states = new List<int>();
        for (int tick = 1; tick <= 360; tick++)
        {
            world.RunFixed(Dt);
            if (tick % 30 == 0) Snapshot(world, bodies, states);
        }
        return states;
    }

    private static void AssertSameBits(List<int> a, List<int> b, string what)
    {
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                Assert.Fail($"{what}: the runs part at value {i} (snapshot {i / 13 / 30 + 1}, body {i / 13 % 30}, component {i % 13}): " +
                            $"{BitConverter.Int32BitsToSingle(a[i]):R} vs {BitConverter.Int32BitsToSingle(b[i]):R}");
    }

    // Cross-run determinism (for replays, tests and the netcode to come): the same scene, built the same
    // way, gives bit-identical poses and velocities for every body over 360 ticks, run twice in fresh
    // engines with this machine's worker threads — and the same bits again with two workers and with
    // seven, so a machine's core count does not change the result (Simulation.Deterministic, 10 §3; a
    // space never has one worker, whose path in Bepu gives other bits).
    [Xunit.Fact]
    public void TheSameSceneGivesBitIdenticalBodiesEveryRun()
    {
        var first = Run(workers: -1);   // as the module made it
        var second = Run(workers: -1);
        Assert.Equal(13 * 30 * 12, first.Count);   // 30 bodies, 13 values, 12 snapshots
        AssertSameBits(first, second, "two runs");
        AssertSameBits(first, Run(workers: 2), "this machine's workers against two");
        AssertSameBits(first, Run(workers: 7), "this machine's workers against seven");
        AssertSameBits(Run(workers: 1), Run(workers: 2), "one worker asked for, two given");
        Assert.Contains(first, bits => bits != 0);
    }

    // Issue #273: a sleeping body stays asleep across a floating-origin rebase, and is where it now is: a
    // ray finds it at its new place, a ball dropped there lands on it (and wakes it), and it does not
    // fall through the floor it was resting on when it wakes.
    [Xunit.Fact]
    public void ASleepingCrateStaysAsleepAcrossARebaseAndIsFoundWhereItNowIs()
    {
        using var engine = JointTests.NewEngine();
        var world = engine.CreateWorld("rebase");
        var physics = world.Resources.Get<IPhysicsWorld>();
        var space = world.Resources.Get<Sage.Physics3D.PhysicsSpace>();
        var floor = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "floor");
        physics.AddBody(floor, Collider.Box(new Vector3(400, 1, 400)), new RigidBody { Kind = BodyKind.Static }, At(new Vector3(0, -0.5f, 0)));
        var crate = world.Create(Transform.At(new Vector3(0, 0.5f, 0)), "crate");
        var body = physics.AddBody(crate, Collider.Box(Vector3.One), RigidBody.Dynamic(5f), At(new Vector3(0, 0.5f, 0)));
        for (int i = 0; i < 240 && space.BodyCount > 0; i++) world.RunFixed(Dt);
        Assert.Equal(0, space.BodyCount);   // asleep

        var offset = new Vector3(-100, 0, 50);
        physics.Rebase(offset);
        Assert.Equal(0, space.BodyCount);   // still asleep: nothing it rests on moved relative to it
        Assert.Equal(offset.X, physics.PoseOf(body).Position.X, 3);
        var down = physics.Raycast(new Vector3(offset.X, 5, offset.Z), -Vector3.UnitY, 10f);
        Assert.Equal(crate, down.Entity);
        Assert.Equal(1f, down.Position.Y, 2);
        Assert.NotEqual(crate, physics.Raycast(new Vector3(0, 5, 0), -Vector3.UnitY, 10f).Entity);   // not where it was

        for (int i = 0; i < 30; i++) world.RunFixed(Dt);
        Assert.Equal(0, space.BodyCount);   // and stays asleep while nothing touches it

        var ball = world.Create(Transform.At(new Vector3(offset.X, 3, offset.Z)), "ball");
        var ballBody = physics.AddBody(ball, Collider.Sphere(0.25f), RigidBody.Dynamic(1f), At(new Vector3(offset.X, 3, offset.Z)));
        for (int i = 0; i < 60; i++) world.RunFixed(Dt);
        Assert.InRange(physics.PoseOf(ballBody).Position.Y, 1.15f, 1.35f);       // on the crate
        Assert.InRange(physics.PoseOf(body).Position.Y, 0.45f, 0.55f);           // which is on the floor
        Assert.Equal(offset.X, physics.PoseOf(body).Position.X, 1);
    }
    // A steady-state step with workers allocated 152 bytes now and then (#434's Windows run): Bepu's
    // workers take narrow-phase jobs as they come free, so which worker reports which trigger pair
    // changes every step, and the stepping thread's own buffer (worker 0) grew the first time it drew
    // nine pairs, whenever that came. Every worker's buffer now holds the busiest step's whole count
    // before each step, and there is one buffer per worker however many workers are asked for.
    [Xunit.Fact]
    public void EveryWorkerBufferHoldsTheBusiestStep()
    {
        using var engine = JointTests.NewEngine();
        var world = engine.CreateWorld("busy");
        var space = world.Resources.Get<Sage.Physics3D.PhysicsSpace>();
        space.UseWorkers(3);
        var bodies = BusyScene(world);
        var physics = world.Resources.Get<IPhysicsWorld>();
        var reporter = world.Create(Transform.At(new Vector3(-6, 3, 0.6f)), "reporter");   // reports its contacts
        var reporterBody = physics.AddBody(reporter, Collider.Sphere(0.3f) with { ReportContacts = true }, RigidBody.Dynamic(1f), At(new Vector3(-6, 3, 0.6f)));
        var data = space.CallbackData;
        Assert.Equal(space.WorkerThreads, data.WorkerBuffers);

        var thrown = world.Get<PhysicsBody>(bodies[12]);
        var before = data.BufferHeadroom();
        for (int tick = 0; tick < 240; tick++)
        {
            if (tick % 40 == 0)
            {
                physics.SetPose(thrown, Collider.Sphere(0.2f), At(new Vector3(-6, 3, 0)));
                physics.SetVelocity(thrown, new Vector3(8, 2, 0));
                physics.SetPose(reporterBody, Collider.Sphere(0.3f), At(new Vector3(-6, 3, 0.6f)));
                physics.SetVelocity(reporterBody, new Vector3(8, 1, 0));
            }
            world.RunFixed(Dt);
            var now = data.BufferHeadroom();
            // Each step began with every buffer able to take all of the busiest step before it.
            Assert.True(now.TriggerCapacity >= before.TriggerPeak, $"tick {tick}: a trigger buffer of {now.TriggerCapacity} against a peak of {before.TriggerPeak}");
            Assert.True(now.ContactCapacity >= before.ContactPeak, $"tick {tick}: a contact buffer of {now.ContactCapacity} against a peak of {before.ContactPeak}");
            before = now;
        }
        Assert.True(before.TriggerPeak > 8, $"the crates in the zone give more trigger pairs than a list's first capacity ({before.TriggerPeak})");
        Assert.True(before.ContactPeak > 0, "the reporter reports its contacts");

    }

    // More workers than this machine may have cores still get one buffer each, and none gets one; on a
    // space that has not stepped, since a dispatcher is chosen before stepping (PhysicsSpace.UseWorkers).
    [Xunit.Fact]
    public void ThereIsOneReportBufferPerWorkerHoweverManyAreAskedFor()
    {
        using var engine = JointTests.NewEngine();
        var world = engine.CreateWorld("workers");
        var space = world.Resources.Get<Sage.Physics3D.PhysicsSpace>();
        space.UseWorkers(7);
        Assert.Equal(7, space.CallbackData.WorkerBuffers);
        space.UseWorkers(0);
        Assert.Equal(1, space.CallbackData.WorkerBuffers);

        world.RunFixed(Dt);
        Assert.Throws<InvalidOperationException>(() => space.UseWorkers(3));   // not once it has stepped
    }
}

// Issue #273: the physics step allocates nothing. The 40 bytes a tick long put down to Bepu's profiler
// were the Stopwatch that timed the step. A busy world — contacts, a stack, joints, a trigger — is
// measured whole, as a world tick with physics on: with Bepu's worker threads (the measured thread is the
// one that steps), and on the calling thread alone, which puts every narrow-phase and solver callback on
// the measured thread too.
[Xunit.Collection(MeasurementsCollection.Name)]
public class PhysicsAllocationTests
{
    public PhysicsAllocationTests() { _ = TestEnv.UserRoot; }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ASteadyStateStepAllocatesNothingOnAnyThread(bool callingThreadAlone)
    {
        using var engine = JointTests.NewEngine();
        var world = engine.CreateWorld("busy");
        var space = world.Resources.Get<Sage.Physics3D.PhysicsSpace>();
        if (callingThreadAlone) space.UseWorkers(0);
        var bodies = PhysicsPerfTests.BusyScene(world);
        var physics = world.Resources.Get<IPhysicsWorld>();
        var thrown = world.Get<PhysicsBody>(bodies[12]);   // one ball kept thrown about, so the step is never idle
        int tick = 0;
        void Step()
        {
            if (tick++ % 40 == 0)
            {
                physics.SetPose(thrown, Collider.Sphere(0.2f), JointTests.At(new Vector3(-6, 3, 0)));
                physics.SetVelocity(thrown, new Vector3(8, 2, 0));
            }
            world.RunFixed(JointTests.Dt);
        }

        for (int i = 0; i < 120; i++) Step();   // warm: contacts made, the stack settled, queues at their high water
        Assert.True(space.BodyCount > 0, "something is awake");
        AllocationProbe.AssertNone(240, Step);
        Assert.True(space.LastStepMilliseconds > 0);
    }
}
