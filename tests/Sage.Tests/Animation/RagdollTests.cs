#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

#pragma warning disable SAGE0134 // ragdolls, joints and groups are what these test

// Going ragdoll (issue #246, docs/design/12 "As built (going ragdoll, issue #246)"): tests/games/skeletal's
// mannequin (its ragdoll.json, eleven bodies) falls as a ragdoll on the yard's floor, from code, on a death
// and from a hit.
public class RagdollTests
{
    public RagdollTests() { _ = TestEnv.UserRoot; }

    private const float DegToRad = MathF.PI / 180f;
    private const float Tolerance = 10f * DegToRad;

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    private static Entity Npc(World world, Vector3 at)
    {
        var npc = world.Spawn(NpcLocomotionTests.Npc, at);
        Assert.False(npc.IsNull);
        return npc;
    }

    internal static RagdollInstance Instance(World world, Entity entity) =>
        world.Resources.Get<RagdollInstances>().Find(entity) ?? throw new Xunit.Sdk.XunitException("no ragdoll instance");

    private static Pose BodyPose(World world, Entity entity, string joint)
    {
        var instance = Instance(world, entity);
        int i = Array.FindIndex(instance.Table!.Bodies.ToArray(), b => b.Name == joint);
        return world.Resources.Get<IPhysicsWorld>().PoseOf(instance.Bodies[i]);
    }

    // A joint's world position from the entity's registered pose.
    private static Vector3 JointWorld(World world, Entity entity, string joint)
    {
        Assert.True(world.Resources.Get<SkeletonPoses>().TryGet(entity, out var pose));
        var root = Pose.FromLocal(world.Get<Transform>(entity)).ToMatrix();
        return Vector3.Transform(pose.ModelSpace[pose.Skeleton.IndexOf(joint)].Translation, root);
    }

    // How far each joint is from where its limits allow, in radians (0 = inside): the child body's turn
    // relative to its parent body's, less the rest turn, split into swing off the joint's axis and twist
    // about it, as the record's limits are measured.
    internal static (string Body, float Swing, float Twist, float Outside)[] JointAngles(World world, Entity entity)
    {
        var physics = world.Resources.Get<IPhysicsWorld>();
        var instance = Instance(world, entity);
        var table = instance.Table!;
        var result = new (string, float, float, float)[table.Count];
        for (int i = 0; i < table.Count; i++)
        {
            var b = table.Bodies[i];
            if (b.Parent < 0) { result[i] = (b.Name, 0, 0, 0); continue; }
            var a = physics.PoseOf(instance.Bodies[i]).Rotation;
            var p = physics.PoseOf(instance.Bodies[b.Parent]).Rotation;
            var rest = Quaternion.Normalize(Quaternion.Conjugate(instance.RestRotation[b.Parent]) * instance.RestRotation[i]);
            var d = Quaternion.Normalize(Quaternion.Conjugate(rest) * Quaternion.Conjugate(p) * a);
            if (d.W < 0) d = new Quaternion(-d.X, -d.Y, -d.Z, -d.W);
            var axis = Vector3.Normalize(Vector3.Transform(b.Axis, Quaternion.Conjugate(b.Rotation)));
            float along = Vector3.Dot(new Vector3(d.X, d.Y, d.Z), axis);
            float twist = 2f * MathF.Atan2(along, d.W);
            var swingAxis = Vector3.Transform(axis, d);
            float swing = MathF.Acos(Math.Clamp(Vector3.Dot(swingAxis, axis), -1f, 1f));
            float outside;
            if (b.JointKind == RagdollJointKind.Hinge)
                outside = MathF.Max(swing, MathF.Max(b.HingeMin - twist, twist - b.HingeMax));
            else
                outside = MathF.Max(swing - b.Swing, MathF.Max(b.TwistMin - twist, twist - b.TwistMax));
            result[i] = (b.Name, swing, twist, MathF.Max(outside, 0f));
        }
        return result;
    }

    // Acceptance: a mannequin sent ragdoll from code falls, comes to rest on the floor within four seconds
    // — every body slow, above the floor, the pelvis low — with every joint inside its limits (± 10°); its
    // root lies under the pelvis on the ground, and IsActive says so until Stop.
    [Xunit.Fact]
    public void AMannequinFallsAndRestsOnTheFloorWithinItsJointLimits()
    {
        using var app = NpcLocomotionTests.Skeletal();
        var world = app.World;
        var physics = world.Resources.Get<IPhysicsWorld>();
        var npc = Npc(world, new Vector3(12, 0, 14));
        Step(world, 3);
        Assert.False(Ragdolls.IsActive(world, npc));
        int bodiesBefore = physics.BodyCount;

        Assert.True(Ragdolls.Start(world, npc));
        Assert.True(Ragdolls.IsActive(world, npc));
        Assert.True(Animators.IsSuspended(world, npc));
        Assert.True(npc.Tags.Has<Ragdolled>());
        Assert.False(Ragdolls.Start(world, npc));                  // once
        var instance = Instance(world, npc);
        Assert.Equal(11, instance.Bodies.Length);
        Assert.Equal(10, instance.Joints.Count(j => !j.IsNull));
        Assert.All(instance.Bodies, b => Assert.Equal(instance.Group, physics.GroupOf(b)));

        float worst = 0f;
        string where = "";
        for (int t = 0; t < 240; t++)
        {
            Step(world);
            foreach (var j in JointAngles(world, npc))
                if (j.Outside > worst) { worst = j.Outside; where = $"{j.Body} at tick {t}: swing {j.Swing / DegToRad:F1}°, twist {j.Twist / DegToRad:F1}°"; }
        }
        Assert.True(worst < 2f * Tolerance, $"a joint went {worst / DegToRad:F1}° past its limits while falling: {where}");

        foreach (var body in instance.Bodies)
        {
            Assert.True(physics.VelocityOf(body).Length() < 0.3f, $"a body still moves at {physics.VelocityOf(body).Length():F2} m/s");
            Assert.True(physics.PoseOf(body).Position.Y > -0.05f, "a body went through the floor");
        }
        var pelvis = BodyPose(world, npc, "pelvis").Position;
        Assert.True(pelvis.Y < 0.4f, $"the pelvis is {pelvis.Y:F2} m up: it did not fall");
        Assert.True(BodyPose(world, npc, "head").Position.Y < 0.5f);
        var angles = JointAngles(world, npc);
        Assert.All(angles, j => Assert.True(j.Outside < Tolerance, $"{j.Body}: swing {j.Swing / DegToRad:F1}°, twist {j.Twist / DegToRad:F1}°"));

        // The root lies under the pelvis on the floor; the pose's pelvis is the body's joint.
        var root = world.Get<Transform>(npc).LocalPosition;
        Assert.Equal(pelvis.X, root.X, 2);
        Assert.Equal(pelvis.Z, root.Z, 2);
        Assert.Equal(0f, root.Y, 2);
        Assert.True(Vector3.Distance(JointWorld(world, npc, "pelvis"), pelvis) < 0.02f);

        // Stop: the bodies go, the animator has the pose again.
        Assert.True(Ragdolls.Stop(world, npc));
        Assert.False(Ragdolls.IsActive(world, npc));
        Assert.False(Animators.IsSuspended(world, npc));
        Assert.False(npc.Tags.Has<Ragdolled>());
        Step(world);
        Assert.Equal(0, physics.JointCount);
        Assert.True(physics.BodyCount <= bodiesBefore);
    }

    // Acceptance: an impulse at the head from the front throws the mannequin backwards: its pelvis and its
    // head land well behind where they stood (the mannequin faces -Z, so behind is +Z), further than they go sideways.
    [Xunit.Fact]
    public void AnImpulseToTheHeadThrowsItBackwards()
    {
        using var app = NpcLocomotionTests.Skeletal();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14));
        Step(world, 3);
        var head = JointWorld(world, npc, "head") + new Vector3(0, 0.1f, 0);
        var start = JointWorld(world, npc, "pelvis");

        Assert.True(Ragdolls.Start(world, npc, new Vector3(0, 0, 120), head));
        Step(world, 120);
        var moved = BodyPose(world, npc, "pelvis").Position - start;
        Assert.True(moved.Z > 0.4f, $"the pelvis went {moved} after a shove at the head");
        Assert.True(moved.Z > MathF.Abs(moved.X) * 2f, $"the pelvis went {moved}: not backwards");
        var headAt = BodyPose(world, npc, "head").Position;
        Assert.True(headAt.Z - head.Z > 0.5f && headAt.Y < 0.5f, $"the head did not go down backwards: it is at {headAt}");
    }

    // Acceptance: an NPC killed by a blow from the front (its `ragdoll` part's onDeath) goes down with the
    // blow: it ragdolls on Died, backwards, and lets go of the sword in its right hand (a bone attachment
    // with `drop`), which falls to the floor on a body of its own; the hitboxes stay on the bones.
    [Xunit.Fact]
    public void AKilledNpcRagdollsAndDropsItsSword()
    {
        using var app = HitLocationTests.Yard();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14));
        var sword = world.Create(Transform.Identity, "sword");
        world.SetParent(sword, npc);
        world.Add(sword, new BoneAttachment { Socket = "", Bone = "hand_r", Offset = new Vector3(0, -0.1f, 0), Drop = true });
        var hitbox = world.Create(Transform.Identity, "keeps");
        world.SetParent(hitbox, npc);
        world.Add(hitbox, new BoneAttachment { Socket = "", Bone = "hand_l" });
        Step(world, 5);
        Assert.True(world.Get<Ragdoll>(npc).OnDeath);
        var hand = JointWorld(world, npc, "hand_r");
        Assert.True(Vector3.Distance(BoneAttachments.WorldPose(sword).Position, hand) < 0.2f);

        var chest = JointWorld(world, npc, "chest");
        Combat.ApplyDamage(world, new DamageInfo(default, npc, default, 1000f, chest, Vector3.UnitZ));
        Step(world, 2);
        Assert.True(Ragdolls.IsActive(world, npc));
        Assert.True(sword.Parent.IsNull);
        Assert.False(world.Has<BoneAttachment>(sword));
        Assert.Equal(BodyKind.Dynamic, world.Get<RigidBody>(sword).Kind);
        Assert.Equal(npc, hitbox.Parent);                          // not marked: it stays

        Step(world, 180);
        var swordAt = world.Get<Transform>(sword).LocalPosition;
        Assert.True(swordAt.Y < 0.3f, $"the sword is still {swordAt.Y:F2} m up");
        Assert.True(BodyPose(world, npc, "pelvis").Position.Z > 14.2f, "the blow from the front did not throw it backwards");
    }

    // Acceptance: the hitboxes follow the ragdolled bones — the head's box is on the head's body wherever
    // it fell, and a shot at it there lands on `head`.
    [Xunit.Fact]
    public void HitboxesFollowTheRagdolledBones()
    {
        using var app = HitLocationTests.Yard();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14));
        Step(world, 3);
        Assert.True(Ragdolls.Start(world, npc, new Vector3(0, 0, 60), JointWorld(world, npc, "head")));
        Step(world, 180);

        var head = BodyPose(world, npc, "head");
        Entity box = default;
        foreach (var e in world.Query<Hitbox>().Entities)
            if (world.Get<Hitbox>(e).Owner == npc && world.Get<Hitbox>(e).Location == new RecordId("skeletal", "head")) box = e;
        Assert.False(box.IsNull);
        var boxAt = world.Get<GlobalTransform>(box).Current.Position;
        Assert.True(head.Position.Y < 0.5f);
        Assert.True(Vector3.Distance(boxAt, head.Position) < 0.12f, $"the head's box is at {boxAt}, the head at {head.Position}");

        // Straight down onto the head where it lies: it lands on the head, on the character.
        var space = world.Resources.Get<IPhysicsWorld>();
        Assert.True(Hits.Ray(space, default, head.Position + new Vector3(0, 2, 0), -Vector3.UnitY, 5f, out var hit));
        Assert.Equal(npc, hit.Target);
        Assert.Equal(new RecordId("skeletal", "head"), hit.Location);
    }

    // A body going ragdoll keeps going: each body starts with the velocity its bone had over the last two
    // ticks (here, the whole NPC carried forward at 3 m/s).
    [Xunit.Fact]
    public void VelocitiesAreSeededFromTheLastTwoTicks()
    {
        using var app = NpcLocomotionTests.Skeletal();
        var world = app.World;
        var physics = world.Resources.Get<IPhysicsWorld>();
        var npc = Npc(world, new Vector3(12, 0, 14));
        Step(world, 3);
        for (int i = 0; i < 5; i++)
        {
            world.Get<Transform>(npc).LocalPosition += new Vector3(0, 0, -3f * NpcLocomotionTests.Dt);
            Step(world);
        }
        Assert.True(Ragdolls.Start(world, npc));
        var pelvis = Instance(world, npc).Bodies[0];
        Assert.Equal(-3f, physics.VelocityOf(pelvis).Z, 1);
        Assert.Equal(0f, physics.VelocityOf(pelvis).X, 1);
    }

    // A character's capsule joins the ragdoll's group and turns into a trigger — no pillar over the corpse
    // for rays to meet — and the character controller stops moving it; Stop makes it solid again.
    [Xunit.Fact]
    public void ACharactersCapsuleStepsAsideAndTheControllerLetsGo()
    {
        using var app = NpcLocomotionTests.Skeletal();
        var world = app.World;
        var physics = world.Resources.Get<IPhysicsWorld>();
        var npc = Npc(world, new Vector3(12, 0, 14));
        world.AddCharacter(npc, physics.Layers.Enemy);
        Step(world, 3);
        Assert.True(physics.Raycast(new Vector3(10, 1.2f, 14), Vector3.UnitX, 4f).Entity == npc);

        Assert.True(Ragdolls.Start(world, npc));
        Assert.True(world.Get<Collider>(npc).IsTrigger);
        Assert.True(world.Get<Ragdoll>(npc).SolidCapsule);
        Assert.Equal(Instance(world, npc).Group, physics.GroupOf(world.Get<PhysicsBody>(npc)));
        world.Get<PawnIntent>(npc).Move = new Vector2(0, 1);      // walk on: the controller must not
        Step(world, 180);
        var root = world.Get<Transform>(npc).LocalPosition;
        var pelvis = BodyPose(world, npc, "pelvis").Position;
        Assert.Equal(pelvis.X, root.X, 2);
        Assert.Equal(pelvis.Z, root.Z, 2);
        Assert.True(Vector3.Distance(root, new Vector3(12, 0, 14)) < 1f, $"it went to {root}");
        Assert.False(physics.Raycast(new Vector3(10, 1.2f, root.Z), Vector3.UnitX, 4f).Hit);   // no capsule standing there

        Assert.True(Ragdolls.Stop(world, npc));
        Assert.False(world.Get<Collider>(npc).IsTrigger);
        Step(world);
        Assert.Equal(0, physics.GroupOf(world.Get<PhysicsBody>(npc)));
        Assert.True(physics.Raycast(new Vector3(root.X - 2f, 1.2f, root.Z), Vector3.UnitX, 4f).Entity == npc);
    }

    // The `Ragdoll` input sends an NPC down with the impulse it carries; a second is refused quietly.
    // Active is saved: a load rebuilds the bodies from the pose where it lay (the bodies themselves are #248's).
    [Xunit.Fact]
    public void TheRagdollInputSendsItDown_AndALoadRebuildsTheBodies()
    {
        using var app = NpcLocomotionTests.Skeletal();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14));
        npc.Name = "faller";
        Step(world, 3);
        var start = JointWorld(world, npc, "pelvis");
        world.IO().FireInput(npc, Ragdolls.Input, "0 0 80");
        Step(world, 90);
        Assert.True(Ragdolls.IsActive(world, npc));
        Assert.True(BodyPose(world, npc, "pelvis").Position.Z - start.Z > 0.3f);

        Assert.True(app.Engine.Saves.Save("ragdoll"));
        Assert.True(app.Engine.Saves.Load("ragdoll"));
        npc = world.FindByName("faller");
        Step(world, 2);
        Assert.True(Ragdolls.IsActive(world, npc));
        Assert.True(npc.Tags.Has<Ragdolled>());
        Assert.Equal(11, Instance(world, npc).Bodies.Count(b => world.Resources.Get<IPhysicsWorld>().PoseOf(b).Position != Vector3.Zero));
        Step(world, 180);
        Assert.True(BodyPose(world, npc, "pelvis").Position.Y < 0.4f);
    }
}

// Ten ragdolls falling and lying allocate nothing per tick apart from the physics backend's own step.
[Xunit.Collection(MeasurementsCollection.Name)]
public class RagdollAllocationTests
{
    public RagdollAllocationTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void TenRagdollsAllocateNothingPerTick()
    {
        using var app = NpcLocomotionTests.Skeletal();
        var world = app.World;
        var npcs = new Entity[10];
        for (int i = 0; i < npcs.Length; i++) npcs[i] = world.Spawn(NpcLocomotionTests.Npc, new Vector3(-12 + i * 2.5f, 0, 12));
        NpcLocomotionTests.Step(world, 3);
        for (int i = 0; i < npcs.Length; i++) Assert.True(Ragdolls.Start(world, npcs[i]));

        void Step()
        {
            world.RunFixed(NpcLocomotionTests.Dt);
            world.RunFrame(NpcLocomotionTests.Dt, 1f);
            Profiler.EndFrame();
        }
        for (int i = 0; i < 300; i++) Step();

        long physicsBefore = ScopeBytes("Fixed.Physics");
        var allocated = AllocationProbe.Measure(120, Step);
        long physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(allocated.Bytes - physics == 0, allocated.ToString());
        Assert.All(npcs, n => Assert.True(Ragdolls.IsActive(world, n)));
    }

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }
}
