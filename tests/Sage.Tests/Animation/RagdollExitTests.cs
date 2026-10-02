#nullable enable
using System;
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

#pragma warning disable SAGE0134 // ragdolls, settling and getting up are what these test

// REDESIGN §5 phase 4k's exit criterion (issue #249, docs/design/12 "As built (phase 4k's exit, issue
// #249)"): a skeletal character falls as a ragdoll on death or on a wire, settles with its joint limits
// respected, and gets back up into idle with its feet on the ground — in tests/games/skeletal's yard, all
// data: the `tumble` machine sends the tumbler `Ragdoll` with a shove, its OnSettled wire moves the machine
// on and its part's getUpAfter gets it up; the `doomed` machine kills the casualty with ApplyEffect, and
// it falls (onDeath) and stays down. Nothing here starts a ragdoll or gets one up: the game's content does.
public class RagdollExitTests
{
    public RagdollExitTests() { _ = TestEnv.UserRoot; }

    private const float Dt = NpcLocomotionTests.Dt;
    private const float DegToRad = MathF.PI / 180f;
    private const float Tolerance = 10f * DegToRad;         // as RagdollTests
    private const float AnkleHeight = 0.08f;

    private static void Step(World world, int ticks = 1) => NpcLocomotionTests.Step(world, ticks);

    private static HeadlessApp Yard(string? savesRoot = null)
    {
        var app = NpcLocomotionTests.Skeletal(exitPair: true);
        app.Engine.Saves.Root = savesRoot ?? Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    private static Entity Named(World world, string name)
    {
        var e = world.FindByName(name);
        Assert.False(e.IsNull, $"no {name} in the yard");
        return e;
    }

    private static string MachineState(World world, Entity e) => world.Get<StateMachine>(e).State ?? "";

    private static Vector3 JointWorld(World world, Entity entity, string joint)
    {
        Assert.True(world.Resources.Get<SkeletonPoses>().TryGet(entity, out var pose));
        var root = Pose.FromLocal(world.Get<Transform>(entity)).ToMatrix();
        return Vector3.Transform(pose.ModelSpace[pose.Skeleton.IndexOf(joint)].Translation, root);
    }

    private static Vector3 PelvisBody(World world, Entity npc) =>
        world.Resources.Get<IPhysicsWorld>().PoseOf(RagdollTests.Instance(world, npc).Bodies[0]).Position;

    // Steps until `done` holds (at most `seconds`), and says how many ticks it took.
    private static int Until(World world, Func<bool> done, float seconds, string what)
    {
        for (int t = 0; t <= seconds * 60; t++)
        {
            if (done()) return t;
            Step(world);
        }
        throw new Xunit.Sdk.XunitException($"{what}: not within {seconds} s");
    }

    // Lying: every joint inside its limits (± the tolerance), the pelvis low, every body slow and above the floor.
    private static void AssertLyingWithinLimits(World world, Entity npc)
    {
        foreach (var j in RagdollTests.JointAngles(world, npc))
            Assert.True(j.Outside < Tolerance, $"{World.Describe(npc)} {j.Body}: swing {j.Swing / DegToRad:F1}°, twist {j.Twist / DegToRad:F1}°");
        var physics = world.Resources.Get<IPhysicsWorld>();
        foreach (var body in RagdollTests.Instance(world, npc).Bodies)
        {
            Assert.True(physics.VelocityOf(body).Length() < 0.3f, $"a body still moves at {physics.VelocityOf(body).Length():F2} m/s");
            Assert.True(physics.PoseOf(body).Position.Y > -0.05f, "a body went through the floor");
        }
        Assert.True(PelvisBody(world, npc).Y < 0.4f, $"the pelvis is {PelvisBody(world, npc).Y:F2} m up: it did not fall");
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
        Assert.True(JointWorld(world, npc, "head").Y - pelvis.Y > 0.55f, "the head is not above the pelvis: not standing");
        Assert.True(pelvis.Y > 0.85f, $"the pelvis is {pelvis.Y:F2} m up");
    }

    // Acceptance: the tumbler stands three seconds, its machine sends it `Ragdoll` with a shove and it falls
    // — never far past a joint limit on the way — settles with every joint inside its limits, says so
    // (OnSettled moves its machine to `down`), gets up by itself 1.5 s later with a get-up clip, ends in
    // idle on both feet, and goes round again.
    [Xunit.Fact]
    public void TheTumblerFallsOnItsWire_SettlesWithinItsLimits_AndGetsUpIntoIdle()
    {
        using var app = Yard();
        var world = app.World;
        var tumbler = Named(world, "tumbler");

        Step(world, 170);
        Assert.False(Ragdolls.IsActive(world, tumbler));
        Assert.Equal("stand", MachineState(world, tumbler));
        Assert.Equal("idle", Animators.StateOf(world, tumbler));
        Until(world, () => Ragdolls.IsActive(world, tumbler), 0.5f, "the tumbler going ragdoll on its wire");
        Assert.Equal("fall", MachineState(world, tumbler));
        Assert.True(Animators.IsSuspended(world, tumbler));

        float worst = 0f;
        string where = "";
        Until(world, () =>
        {
            foreach (var j in RagdollTests.JointAngles(world, tumbler))
                if (j.Outside > worst) { worst = j.Outside; where = $"{j.Body}: swing {j.Swing / DegToRad:F1}°, twist {j.Twist / DegToRad:F1}°"; }
            return Ragdolls.IsSettled(world, tumbler);
        }, 8f, "the tumbler settling");
        Assert.True(worst < 2f * Tolerance, $"a joint went {worst / DegToRad:F1}° past its limits while falling: {where}");
        AssertLyingWithinLimits(world, tumbler);
        var lay = PelvisBody(world, tumbler);
        Step(world, 2);                                             // OnSettled's wire
        Assert.Equal("down", MachineState(world, tumbler));
        Assert.True(Ragdolls.IsActive(world, tumbler));

        // getUpAfter: 1.5 s after settling it gets up, where it lay.
        int ticks = Until(world, () => !Ragdolls.IsActive(world, tumbler), 2f, "the tumbler getting up");
        Assert.InRange(ticks, (int)(1.3f / Dt), (int)(1.6f / Dt));
        Assert.Contains(Animators.StateOf(world, tumbler), new[] { "getup_back", "getup_front" });
        var root = world.Get<Transform>(tumbler).LocalPosition;
        Assert.True(Vector2.Distance(new Vector2(root.X, root.Z), new Vector2(lay.X, lay.Z)) < 0.05f, $"it got up at {root}, it lay at {lay}");
        Step(world, (int)(1.6f / Dt) + 40);
        AssertStanding(world, tumbler);

        // And round again: its machine stands it a while and knocks it over once more.
        Until(world, () => MachineState(world, tumbler) == "stand", 2f, "the tumbler's machine standing again");
        Until(world, () => Ragdolls.IsActive(world, tumbler), 3.5f, "the tumbler going ragdoll a second time");
    }

    // Acceptance: the casualty's machine kills it two seconds in (ApplyEffect lethal); it dies, falls as a
    // ragdoll, settles within its limits and stays down — long past the getUpAfter its part sets.
    [Xunit.Fact]
    public void TheCasualtyIsKilled_FallsAndStaysDown()
    {
        using var app = Yard();
        var world = app.World;
        var casualty = Named(world, "casualty");
        Step(world, 100);
        Assert.False(Ragdolls.IsActive(world, casualty));
        Assert.Equal(1.5f, world.Get<RagdollGetUp>(casualty).After);

        Until(world, () => Ragdolls.IsActive(world, casualty), 1f, "the casualty dying");
        Assert.Equal("dead", MachineState(world, casualty));
        Assert.True(world.Get<RagdollGetUp>(casualty).StayDown);
        Until(world, () => Ragdolls.IsSettled(world, casualty), 8f, "the casualty settling");
        AssertLyingWithinLimits(world, casualty);
        var lay = PelvisBody(world, casualty);

        Step(world, (int)(6f / Dt));
        Assert.True(Ragdolls.IsActive(world, casualty));
        Assert.True(Ragdolls.IsSettled(world, casualty));
        Assert.True(world.Get<RagdollGetUp>(casualty).Down > 5f);
        Assert.True(Vector3.Distance(PelvisBody(world, casualty), lay) < 0.01f, "the corpse moved");
    }

    // Acceptance: the yard saved a third of a second into the tumbler's fall (the casualty already down) and
    // loaded in a fresh app: the tumbler lands where the run that never stopped did, its machine still hears
    // OnSettled, and it gets up into idle; the casualty is still down.
    [Xunit.Fact]
    public void ASaveMidFallLoadsMidFall_LandsWhereTheUninterruptedRunDid_AndGetsUp()
    {
        using var first = Yard();
        var world = first.World;
        var tumbler = Named(world, "tumbler");
        Until(world, () => Ragdolls.IsActive(world, tumbler), 4f, "the tumbler going ragdoll");
        Step(world, 20);
        Assert.False(Ragdolls.IsSettled(world, tumbler));
        Assert.True(Ragdolls.IsActive(world, Named(world, "casualty")));
        Assert.True(first.Engine.Saves.Save("mid"));

        int settled = Until(world, () => Ragdolls.IsSettled(world, tumbler), 8f, "the tumbler settling");
        var landed = PelvisBody(world, tumbler);

        using var second = Yard(first.Engine.Saves.Root);
        var again = second.World;
        Assert.True(second.Engine.Saves.Load("mid"));
        tumbler = Named(again, "tumbler");
        var casualty = Named(again, "casualty");
        Step(again);
        Assert.True(Ragdolls.IsActive(again, tumbler));
        Assert.False(Ragdolls.IsSettled(again, tumbler));
        Assert.Equal("fall", MachineState(again, tumbler));
        int settledAgain = Until(again, () => Ragdolls.IsSettled(again, tumbler), 8f, "the loaded tumbler settling") + 1;
        Assert.InRange(settledAgain, settled - 15, settled + 15);
        var there = PelvisBody(again, tumbler);
        Assert.True(Vector3.Distance(there, landed) < 0.05f, $"the loaded fall landed at {there}, the uninterrupted one at {landed}");
        AssertLyingWithinLimits(again, tumbler);

        Step(again, 2);
        Assert.Equal("down", MachineState(again, tumbler));
        Until(again, () => !Ragdolls.IsActive(again, tumbler), 2f, "the loaded tumbler getting up");
        Step(again, (int)(1.6f / Dt) + 40);
        AssertStanding(again, tumbler);

        Assert.True(Ragdolls.IsActive(again, casualty));
        Assert.True(again.Get<RagdollGetUp>(casualty).StayDown);
    }
}
