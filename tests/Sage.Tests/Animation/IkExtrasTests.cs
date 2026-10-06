#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Sage.Gameplay;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The IK extras of issue #361 (docs/design/12 "As built (attachments and IK)"): foot IK's pelvis
// smoothing on stairs and its foot tilt (FeetRestOnARamp checks the tilt), look-at IK with limits, hand
// IK for grabbing and grips, and `skeleton_sockets` joints checked against the model at load. Poses are
// HumanoidSkeletonBuilder's, registered with SkeletonPoses and reset before every tick, as the Animator
// would.
public class IkExtrasTests
{
    private const float Deg = MathF.PI / 180f;

    public IkExtrasTests() { _ = TestEnv.UserRoot; }

    private static void Near(Vector3 expected, Vector3 actual, float eps = 1e-3f) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"expected {expected}, got {actual} ({Vector3.Distance(expected, actual)} apart)");

    private static Vector3 Forward(SkeletonPose pose, int joint) => Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, pose.ModelSpace[joint]));

    private static void Tick(World world, Skeleton skeleton, SkeletonPose pose)
    {
        pose.ResetToRest();                      // what the Animator does every tick
        PoseSampler.ToModelSpace(skeleton, pose);
        world.RunFixed(1f / 60f);
    }

    // ---- foot IK on stairs --------------------------------------------------------------------------

    private const float Rise = 0.18f, Run = 0.3f;

    private static float StairAt(float x) => MathF.Floor(x / Run) * Rise;

    private static void AddStairs(World world)
    {
        for (int i = 0; i < 16; i++)
        {
            float top = i * Rise, height = top + 1f;
            var step = world.Create(Transform.At(new Vector3((i + 0.5f) * Run, top - height / 2f, 0)), $"step{i}");
            world.Add(step, Collider.Box(new Vector3(Run, height, 4)));
        }
    }

    // A walker climbs a staircase as a character controller moves it: its root jumps up a step at a
    // time, and the feet straddle step edges. The pelvis rises with the stairs but never by more than a
    // few centimetres a tick; with smoothing off, the same walk pops it by most of a step.
    private static (float MaxJump, float Rose) ClimbStairs(float smoothing)
    {
        using var app = HeadlessApp.Gameplay().File("data/walker.json", $$"""
        [ { "type": "prefab", "id": "walker", "name": "walker",
            "parts": { "foot_ik": { "pelvis": "pelvis", "footHeight": 0.1, "rayAbove": 0.5, "rayBelow": 0.5, "maxPelvisDrop": 0.4,
                                    "pelvisSmoothing": {{smoothing.ToString(System.Globalization.CultureInfo.InvariantCulture)}},
                                    "left":  { "hip": "thigh.L", "knee": "shin.L", "foot": "foot.L" },
                                    "right": { "hip": "thigh.R", "knee": "shin.R", "foot": "foot.R" } } } } ]
        """).Boot("stairs");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        AddStairs(world);
        var skeleton = HumanoidSkeletonBuilder.Build();
        int pelvis = skeleton.IndexOf(HumanoidSkeletonBuilder.Pelvis);
        using var pose = new SkeletonPose(skeleton);
        float x = 1.0123f;
        var walker = world.Spawn(new RecordId("sage", "walker"), new Vector3(x, StairAt(x), 0));
        world.Resources.Get<SkeletonPoses>().Set(walker, pose);

        float PelvisY() => Vector3.Transform(pose.ModelSpace[pelvis].Translation, world.Get<GlobalTransform>(walker).Current.ToMatrix()).Y;
        for (int i = 0; i < 30; i++) Tick(world, skeleton, pose);   // settle on the first step
        float start = PelvisY(), last = start, maxJump = 0f;
        for (int i = 0; i < 96; i++)                                 // 2.4 m at 1.5 m/s: eight steps up
        {
            x += 0.025f;
            world.Get<Transform>(walker).LocalPosition = new Vector3(x, StairAt(x), 0);
            Tick(world, skeleton, pose);
            var ik = world.Get<FootIk>(walker);
            Assert.True(ik.LeftGrounded && ik.RightGrounded, $"both feet find a step at x = {x}");
            float y = PelvisY();
            maxJump = MathF.Max(maxJump, MathF.Abs(y - last));
            last = y;
        }
        for (int i = 0; i < 30; i++) Tick(world, skeleton, pose);   // and settle on the last
        return (maxJump, PelvisY() - start);
    }

    [Fact]
    public void AStairWalkRaisesThePelvisSmoothlyWithoutPopping()
    {
        var (smooth, rose) = ClimbStairs(0.1f);
        Assert.True(smooth < 0.04f, $"the pelvis moved {smooth} m in one tick climbing the stairs");
        // It climbed what the root climbed: from step 3 to step 11, eight rises.
        Assert.Equal(StairAt(1.0123f + 2.4f) - StairAt(1.0123f), rose, 2);

        // The same walk without smoothing pops: the drop for the lower foot comes and goes a step at once.
        var (popping, _) = ClimbStairs(0f);
        Assert.True(popping > 0.12f, $"without smoothing the pelvis jumps most of a step ({popping} m)");
    }

    // ---- look-at IK ---------------------------------------------------------------------------------

    private const string LookerRecords = """
    [ { "type": "prefab", "id": "looker", "name": "looker",
        "parts": { "look_at_ik": { "joints": [ { "joint": "neck", "weight": 0.4, "pitchLimit": 30, "yawLimit": 30 },
                                               { "joint": "head", "weight": 0.6, "pitchLimit": 40, "yawLimit": 40 } ],
                                   "maxAngle": 120, "smoothing": 0 } } } ]
    """;

    [Fact]
    public void ALookAtTargetBehindTheShoulderIsClampedToTheChainsLimits()
    {
        using var app = HeadlessApp.Simulation().File("data/looker.json", LookerRecords).Boot("look");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var skeleton = HumanoidSkeletonBuilder.Build();
        int head = skeleton.IndexOf(HumanoidSkeletonBuilder.Head), neck = skeleton.IndexOf(HumanoidSkeletonBuilder.Neck);
        using var pose = new SkeletonPose(skeleton);
        var npc = world.Spawn(new RecordId("sage", "looker"), new Vector3(10, 0, 0));
        world.Resources.Get<SkeletonPoses>().Set(npc, pose);
        var player = world.Create(Transform.At(new Vector3(10, 0, 0)), "player");
        world.Get<LookAtIk>(npc).Target = player;
        world.Get<LookAtIk>(npc).Offset = new Vector3(0, 1.7f, 0);   // eye height: the head's own

        // Within limits: a target a little to the left, level with the eye: the head looks straight at it.
        world.Get<Transform>(player).LocalPosition = new Vector3(10 - 1, 0, -10);
        Tick(world, skeleton, pose);
        Assert.True(world.Get<LookAtIk>(npc).Looking);
        Near(Vector3.Normalize(new Vector3(-1, 0, -10)), Forward(pose, head));

        // Far round to the left (79°): the neck gives its 30°, the head its 40°, and no more: 70°.
        world.Get<Transform>(player).LocalPosition = new Vector3(10 - 5, 0, -1);
        Tick(world, skeleton, pose);
        var look = world.Get<LookAtIk>(npc);
        Assert.True(look.Looking);
        Assert.Equal(MathF.Atan2(5, 1) / Deg, look.Yaw / Deg, 2);
        var forward = Forward(pose, head);
        Assert.Equal(70f, MathF.Atan2(-forward.X, -forward.Z) / Deg, 1);
        var neckForward = Forward(pose, neck);
        Assert.Equal(30f, MathF.Atan2(-neckForward.X, -neckForward.Z) / Deg, 1);

        // Up a balcony: pitch clamps the same way (30° + 40° of 80°).
        world.Get<Transform>(player).LocalPosition = new Vector3(10, 1.7f + 10 * MathF.Tan(80 * Deg) - 1.7f, -10);
        Tick(world, skeleton, pose);
        Assert.Equal(70f, MathF.Asin(Forward(pose, head).Y) / Deg, 1);

        // Behind it (180°, past maxAngle 120°): it does not try; the head looks ahead as animated.
        world.Get<Transform>(player).LocalPosition = new Vector3(10, 0, 5);
        Tick(world, skeleton, pose);
        Assert.False(world.Get<LookAtIk>(npc).Looking);
        Near(-Vector3.UnitZ, Forward(pose, head));

        // Smoothing eases a new look in rather than snapping to it.
        ref var ik = ref world.Get<LookAtIk>(npc);
        ik.Smoothing = 0.15f;
        world.Get<Transform>(player).LocalPosition = new Vector3(10 - 1, 0, -2);   // 26.6° left
        Tick(world, skeleton, pose);
        float first = world.Get<LookAtIk>(npc).Yaw / Deg;
        Assert.True(first > 0.5f && first < 26f, $"one tick in, the look is on its way ({first}°)");
        for (int i = 0; i < 120; i++) Tick(world, skeleton, pose);
        Assert.Equal(MathF.Atan2(1, 2) / Deg, world.Get<LookAtIk>(npc).Yaw / Deg, 1);

        // A point instead of a target.
        ref var byPoint = ref world.Get<LookAtIk>(npc);
        byPoint.Target = default;
        byPoint.Smoothing = 0f;
        byPoint.Point = new Vector3(10 + 3, 1.7f, -3);
        byPoint.HasPoint = true;
        Tick(world, skeleton, pose);
        Near(Vector3.Normalize(new Vector3(1, 0, -1)), Forward(pose, head));
    }

    [Fact]
    public void LookAtAndHandIkPartsWithoutJointsAddNothing()
    {
        using var app = HeadlessApp.Simulation().File("data/bad.json", """
        [ { "type": "prefab", "id": "blind", "name": "blind", "parts": { "look_at_ik": { "joints": [] } } },
          { "type": "prefab", "id": "armless", "name": "armless", "parts": { "hand_ik": { "left": { "shoulder": "a" } } } } ]
        """).Boot("bad");
        var blind = app.World.Spawn(new RecordId("sage", "blind"), Vector3.Zero);
        var armless = app.World.Spawn(new RecordId("sage", "armless"), Vector3.Zero);
        Assert.False(blind.HasComponent<LookAtIk>());
        Assert.False(armless.HasComponent<HandIk>());
    }

    // ---- hand IK ------------------------------------------------------------------------------------

    [Fact]
    public void AHandReachesAnEntityOrAGripOnItsOwnSkeleton()
    {
        using var app = HeadlessApp.Simulation().File("data/grabber.json", """
        [ { "type": "prefab", "id": "grabber", "name": "grabber",
            "parts": { "hand_ik": { "right": { "shoulder": "upper_arm.R", "elbow": "forearm.R", "hand": "hand.R" } } } },
          { "type": "prefab", "id": "gripper", "name": "gripper",
            "parts": { "hand_ik": { "right": { "shoulder": "upper_arm.R", "elbow": "forearm.R", "hand": "hand.R",
                                                "bone": "head", "offset": [0.3, 0, -0.2] } } } } ]
        """).Boot("hands");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var skeleton = HumanoidSkeletonBuilder.Build();
        int shoulder = skeleton.IndexOf(HumanoidSkeletonBuilder.UpperArmR), elbow = skeleton.IndexOf(HumanoidSkeletonBuilder.ForearmR);
        int hand = skeleton.IndexOf(HumanoidSkeletonBuilder.HandR);

        // A lever in front of and below the shoulder, the grabber turned to face +X.
        using var pose = new SkeletonPose(skeleton);
        var grabber = world.Spawn(new RecordId("sage", "grabber"), new Vector3(0, 0, 5));
        world.Get<Transform>(grabber).LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -90 * Deg);
        world.Resources.Get<SkeletonPoses>().Set(grabber, pose);
        var turned = Matrix4x4.CreateFromQuaternion(world.Get<Transform>(grabber).LocalRotation) * Matrix4x4.CreateTranslation(0, 0, 5);
        var reachInModel = new Vector3(0.4f, 1.3f, -0.3f);
        var lever = world.Create(Transform.At(Vector3.Transform(reachInModel, turned)), "lever");

        Tick(world, skeleton, pose);                       // no target yet: the arm is as animated
        Assert.False(world.Get<HandIk>(grabber).RightReached);
        Near(new Vector3(0.75f, 1.6f, 0), pose.ModelSpace[hand].Translation);

        world.Get<HandIk>(grabber).RightTarget = lever;
        Tick(world, skeleton, pose);
        Assert.True(world.Get<HandIk>(grabber).RightReached);
        Near(reachInModel, pose.ModelSpace[hand].Translation);
        Assert.Equal(0.3f, Vector3.Distance(pose.ModelSpace[shoulder].Translation, pose.ModelSpace[elbow].Translation), 3);   // bones keep their lengths
        Assert.True(pose.ModelSpace[elbow].Translation.Y < 1.5f, "the elbow bends down");

        // Out of reach: stretched toward it, not there.
        world.Get<Transform>(lever).LocalPosition = Vector3.Transform(new Vector3(3, 1.6f, -3), turned);
        Tick(world, skeleton, pose);
        Assert.False(world.Get<HandIk>(grabber).RightReached);
        Assert.Equal(0.55f, Vector3.Distance(pose.ModelSpace[shoulder].Translation, pose.ModelSpace[hand].Translation), 3);

        // A grip on the same skeleton (here a point beside the head; a foregrip in the gun hand's space
        // in a game) moves with that joint in the same tick.
        using var gripPose = new SkeletonPose(skeleton);
        var gripper = world.Spawn(new RecordId("sage", "gripper"), new Vector3(20, 0, 0));
        world.Resources.Get<SkeletonPoses>().Set(gripper, gripPose);
        Tick(world, skeleton, gripPose);
        Assert.True(world.Get<HandIk>(gripper).RightReached);
        Near(new Vector3(0.3f, 1.7f, -0.2f), gripPose.ModelSpace[hand].Translation);
    }

    // ---- sockets at load ----------------------------------------------------------------------------

    [Fact]
    public void ASocketOnABoneTheModelLacksIsALoadError()
    {
        var files = new MountFixture();
        files.Write("game", "data/sockets.json", """
        [ { "type": "skeleton_sockets", "id": "rig", "model": "models/rig.glb",
            "sockets": { "hand": { "joint": "tip" }, "elbow": { "joint": "mid" } } },
          { "type": "skeleton_sockets", "id": "rig_typo", "model": "models/rig.glb",
            "sockets": { "hand_r": { "joint": "hand.R" }, "back": { "joint": "TIP" } } } ]
        """);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"));
        files.Mount("game", "test");
        using var app = HeadlessApp.Simulation().Mount(files).Build();

        var errors = app.Records.LoadErrors.ToList();
        Assert.True(app.Records.ErrorCount == 2, string.Join("\n", errors));
        Assert.Contains(errors, e => e.Contains("'hand.R'") && e.Contains("models/rig.glb"));
        Assert.Contains(errors, e => e.Contains("'TIP'") && e.Contains("did you mean 'tip'"));
    }
}

// Look-at and hand IK allocate nothing per tick once resolved (02 §4.6). Measured per thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class IkExtrasAllocationTests
{
    public IkExtrasAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void LookAtAndHandIkAllocateNothingPerTick()
    {
        using var app = HeadlessApp.Simulation().File("data/npc.json", """
        [ { "type": "prefab", "id": "npc", "name": "npc",
            "parts": { "look_at_ik": { "joints": [ { "joint": "neck", "weight": 0.4 }, { "joint": "head", "weight": 0.6 } ] },
                       "hand_ik": { "right": { "shoulder": "upper_arm.R", "elbow": "forearm.R", "hand": "hand.R" } } } } ]
        """).Boot("alloc");
        var world = app.World;
        var skeleton = HumanoidSkeletonBuilder.Build();
        using var pose = new SkeletonPose(skeleton);
        var npc = world.Spawn(new RecordId("sage", "npc"), Vector3.Zero);
        world.Resources.Get<SkeletonPoses>().Set(npc, pose);
        var target = world.Create(Transform.At(new Vector3(0.4f, 1.3f, -0.3f)), "target");
        world.Get<LookAtIk>(npc).Target = target;
        world.Get<HandIk>(npc).RightTarget = target;

        float t = 0;
        void Step()
        {
            t += 1f / 60f;
            world.Get<Transform>(target).LocalPosition = new Vector3(0.4f + 0.1f * MathF.Sin(t), 1.3f, -0.3f);
            pose.ResetToRest();
            PoseSampler.ToModelSpace(skeleton, pose);
            world.RunFixed(1f / 60f);
        }
        for (int i = 0; i < 10; i++) Step();
        Assert.True(world.Get<LookAtIk>(npc).Looking && world.Get<HandIk>(npc).RightReached);
        AllocationProbe.AssertNone(200, Step);
    }
}
