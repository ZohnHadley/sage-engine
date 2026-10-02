#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

#pragma warning disable SAGE0134 // ragdolls and getting up are what these test

// Getting up (issue #247, docs/design/12 "As built (getting up, issue #247)"): tests/games/skeletal's
// mannequin is knocked down as a ragdoll and gets back up with its graph's getup_back or getup_front,
// fading from the pose it lay in, into idle.
public class RagdollGetUpTests
{
    public RagdollGetUpTests() { _ = TestEnv.UserRoot; }

    private const float Dt = NpcLocomotionTests.Dt;
    private const float AnkleHeight = 0.08f;

    // A knockdown: the yard's NPC that gets up by itself 2 s after it goes down.
    private const string Records = """
        [{ "type": "prefab", "id": "knockdown", "base": "skeletal:npc",
           "parts": { "ragdoll": { "getUpAfter": 2, "getUpFade": 0.3 } } }]
        """;
    private static readonly RecordId Knockdown = new("getup", "knockdown");

    private static HeadlessApp Yard() =>
        NpcLocomotionTests.WithoutExitPair(HeadlessApp.ForGame(NpcLocomotionTests.Game("skeletal")).WithEngineContent()
            .File("data/getup.json", Records, "getup").Boot());

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    private static Entity Npc(World world, Vector3 at, RecordId? prefab = null)
    {
        var npc = world.Spawn(prefab ?? NpcLocomotionTests.Npc, at);
        Assert.False(npc.IsNull);
        return npc;
    }

    // A joint's world position from the entity's registered pose.
    private static Vector3 JointWorld(World world, Entity entity, string joint)
    {
        Assert.True(world.Resources.Get<SkeletonPoses>().TryGet(entity, out var pose));
        var root = Pose.FromLocal(world.Get<Transform>(entity)).ToMatrix();
        return Vector3.Transform(pose.ModelSpace[pose.Skeleton.IndexOf(joint)].Translation, root);
    }

    // Shoves the head (+Z: backwards, onto its back; -Z: forwards, onto its front) and waits until every
    // body is slower than 0.2 m/s (at most 6 s).
    private static void KnockDown(World world, Entity npc, float push)
    {
        Step(world, 3);
        var chest = JointWorld(world, npc, "chest");
        Assert.True(Ragdolls.Start(world, npc, new Vector3(0, 0, push), chest));
        var physics = world.Resources.Get<IPhysicsWorld>();
        var bodies = RagdollTests.Instance(world, npc).Bodies;
        Step(world, 60);
        for (int t = 0; t < 300; t++)
        {
            float fastest = 0f;
            foreach (var body in bodies) fastest = MathF.Max(fastest, physics.VelocityOf(body).Length());
            if (fastest < 0.2f) return;
            Step(world);
        }
        throw new Xunit.Sdk.XunitException("the ragdoll did not come to rest in 6 s");
    }

    // Stood up: idle, both ankles at their height over the floor, the head well above the pelvis.
    private static void AssertStanding(World world, Entity npc)
    {
        Assert.Equal("idle", Animators.StateOf(world, npc));
        Assert.False(Ragdolls.IsActive(world, npc));
        Assert.False(npc.Tags.Has<Ragdolled>());
        foreach (var foot in new[] { "foot_l", "foot_r" })
        {
            float y = JointWorld(world, npc, foot).Y;
            Assert.True(MathF.Abs(y - AnkleHeight) < 0.03f, $"{foot} is {y:F3} m up: not on the floor");
        }
        var pelvis = JointWorld(world, npc, "pelvis");
        var head = JointWorld(world, npc, "head");
        Assert.True(head.Y - pelvis.Y > 0.55f, $"the head is {head.Y - pelvis.Y:F2} m above the pelvis: not standing");
        Assert.True(pelvis.Y > 0.85f, $"the pelvis is {pelvis.Y:F2} m up");
    }

    // Acceptance: knocked over backwards, it lies face up; GetUp snaps its root under the pelvis (on the
    // floor, facing its feet) without moving the pelvis, plays getup_back fading from the pose it lay in —
    // the pelvis still over where it lay when the fade is done — and ends in idle, standing on both feet.
    [Xunit.Fact]
    public void KnockedDownFaceUp_ItGetsUpWithGetupBackIntoIdle()
    {
        using var app = Yard();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14));
        KnockDown(world, npc, 40f);
        Assert.True(Ragdolls.IsFaceUp(world, npc), $"pelvis {JointWorld(world, npc, "pelvis")} head {JointWorld(world, npc, "head")} chest {JointWorld(world, npc, "chest")} foot {JointWorld(world, npc, "foot_l")} hand {JointWorld(world, npc, "hand_l")}");

        var lay = JointWorld(world, npc, "pelvis");
        var head = JointWorld(world, npc, "head");
        Assert.True(lay.Y < 0.3f, $"the pelvis is {lay.Y:F2} m up: it did not fall");
        Assert.True(Ragdolls.GetUp(world, npc));
        Assert.False(Ragdolls.IsActive(world, npc));
        Assert.Equal("getup_back", Animators.StateOf(world, npc));
        Assert.False(Ragdolls.GetUp(world, npc));                  // not a ragdoll any more

        // The snap: the root on the floor under the pelvis, turned to face away from the head; the pelvis
        // where it lay.
        var root = world.Get<Transform>(npc);
        Assert.Equal(lay.X, root.LocalPosition.X, 3);
        Assert.Equal(lay.Z, root.LocalPosition.Z, 3);
        Assert.Equal(0f, root.LocalPosition.Y, 2);
        var facing = Vector3.Transform(-Vector3.UnitZ, root.LocalRotation);
        var toHead = Vector3.Normalize(new Vector3(head.X - lay.X, 0, head.Z - lay.Z));
        Assert.True(Vector3.Dot(facing, toHead) < -0.8f, $"it faces {facing}, its head is towards {toHead}");
        Assert.True(Vector3.Distance(JointWorld(world, npc, "pelvis"), lay) < 0.01f);

        // Through the fade: the pelvis stays over where it lay.
        Step(world, 13);
        var after = JointWorld(world, npc, "pelvis");
        Assert.True(Vector3.Distance(new Vector3(after.X, 0, after.Z), new Vector3(lay.X, 0, lay.Z)) < 0.05f, $"the pelvis moved from {lay} to {after}");
        Assert.True(after.Y < 0.25f);
        Assert.Equal("getup_back", Animators.StateOf(world, npc));

        // Up: 1.6 s of clip, then the fade into idle.
        Step(world, (int)(1.6f / Dt) + 30);
        AssertStanding(world, npc);
        Assert.True(Vector3.Distance(world.Get<Transform>(npc).LocalPosition, root.LocalPosition) < 0.01f);   // stood where it lay
    }

    // Acceptance: knocked over forwards, it lies face down and gets up with getup_front, facing where its
    // head lay, into idle.
    [Xunit.Fact]
    public void KnockedDownFaceDown_ItGetsUpWithGetupFront()
    {
        using var app = Yard();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14));
        KnockDown(world, npc, -40f);
        Assert.False(Ragdolls.IsFaceUp(world, npc), $"pelvis {JointWorld(world, npc, "pelvis")} head {JointWorld(world, npc, "head")}");
        var lay = JointWorld(world, npc, "pelvis");
        var head = JointWorld(world, npc, "head");

        Assert.True(Ragdolls.GetUp(world, npc));
        Assert.Equal("getup_front", Animators.StateOf(world, npc));
        var facing = Vector3.Transform(-Vector3.UnitZ, world.Get<Transform>(npc).LocalRotation);
        var toHead = Vector3.Normalize(new Vector3(head.X - lay.X, 0, head.Z - lay.Z));
        Assert.True(Vector3.Dot(facing, toHead) > 0.8f, $"it faces {facing}, its head is towards {toHead}");
        Assert.True(Vector3.Distance(JointWorld(world, npc, "pelvis"), lay) < 0.01f);

        Step(world, (int)(1.6f / Dt) + 30);
        AssertStanding(world, npc);
    }

    // The `GetUp` input gets a ragdoll up from a wire; on one standing it does nothing.
    [Xunit.Fact]
    public void TheGetUpInputGetsItUp()
    {
        using var app = Yard();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14));
        world.IO().FireInput(npc, Ragdolls.GetUpInput);           // standing: nothing
        Step(world);
        Assert.Equal("idle", Animators.StateOf(world, npc));
        KnockDown(world, npc, 40f);

        world.IO().FireInput(npc, Ragdolls.GetUpInput);
        Step(world);
        Assert.False(Ragdolls.IsActive(world, npc));
        Assert.Equal("getup_back", Animators.StateOf(world, npc));
        Step(world, (int)(1.6f / Dt) + 30);
        AssertStanding(world, npc);
    }

    // The part's getUpAfter gets a knockdown up by itself that long after it settled, with its fade;
    // the settings default to getup_back, getup_front and 0.2 s, and never by themselves.
    [Xunit.Fact]
    public void GetUpAfterGetsItUpByItself()
    {
        using var app = Yard();
        var world = app.World;
        var plain = Npc(world, new Vector3(-12, 0, 14));
        var defaults = world.Get<RagdollGetUp>(plain);
        Assert.Equal(0f, defaults.After);
        Assert.Equal("getup_back", defaults.BackState);
        Assert.Equal("getup_front", defaults.FrontState);
        Assert.Equal(0.2f, defaults.Fade);

        var npc = Npc(world, new Vector3(12, 0, 14), Knockdown);
        Assert.Equal(2f, world.Get<RagdollGetUp>(npc).After);
        Assert.Equal(0.3f, world.Get<RagdollGetUp>(npc).Fade);
        Step(world, 3);
        Assert.True(Ragdolls.Start(world, npc, new Vector3(0, 0, 40), JointWorld(world, npc, "chest")));
        Assert.True(Ragdolls.Start(world, plain));
        for (int i = 0; i < 600 && !Ragdolls.IsSettled(world, npc); i++) Step(world, 1);
        Assert.True(Ragdolls.IsSettled(world, npc));                 // it counts from here
        Step(world, (int)(1.9f / Dt));
        Assert.True(Ragdolls.IsActive(world, npc));                 // not yet
        Step(world, (int)(0.2f / Dt));
        Assert.False(Ragdolls.IsActive(world, npc));
        Assert.Equal("getup_back", Animators.StateOf(world, npc));
        Assert.Equal(0f, world.Get<RagdollGetUp>(npc).Down);
        Step(world, (int)(1.6f / Dt) + 40);
        AssertStanding(world, npc);
        Assert.True(Ragdolls.IsActive(world, plain));               // getUpAfter 0: it stays down
    }

    // A character that has died never gets up by itself, whatever getUpAfter says; GetUp from code still works.
    [Xunit.Fact]
    public void ADeadKnockdownStaysDown()
    {
        using var app = Yard();
        var world = app.World;
        var npc = Npc(world, new Vector3(12, 0, 14), Knockdown);
        Step(world, 3);
        Combat.ApplyDamage(world, new DamageInfo(default, npc, default, 1000f, JointWorld(world, npc, "chest"), Vector3.UnitZ));
        Step(world, 2);
        Assert.True(Ragdolls.IsActive(world, npc));                 // it died: onDeath
        Assert.True(world.Get<RagdollGetUp>(npc).StayDown);
        int ticks = 0;
        for (; ticks < 1200 && !Ragdolls.IsSettled(world, npc); ticks++) Step(world, 1);
        Assert.True(Ragdolls.IsSettled(world, npc), $"not settled after {ticks} ticks");
        Step(world, (int)(3f / Dt));
        Assert.True(Ragdolls.IsActive(world, npc));
        Assert.True(world.Get<RagdollGetUp>(npc).Down > world.Get<RagdollGetUp>(npc).After);   // long past its getUpAfter

        Assert.True(Ragdolls.GetUp(world, npc));
        Assert.False(world.Get<RagdollGetUp>(npc).StayDown);
    }
}
