#nullable enable
using System.IO;
using System.Numerics;
using Sage.Gameplay;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Sockets, bone attachments and IK (issue #120, docs/design/12 "As built (attachments and IK)"). The
// solvers are tested on poses made with PoseSampler directly (no animation graph: that is #118), against
// SkinnedModelBuilder's root → mid → tip chain (two 1 m bones, straight up at rest) and
// HumanoidSkeletonBuilder's legs, spine and arm. The world tests stand in for #118's Animator: they
// register a pose with SkeletonPoses and rewrite it before every tick, as the Animator will.
public class AttachmentAndIkTests
{
    private const float Eps = 1e-4f;
    private const float Deg = MathF.PI / 180f;

    public AttachmentAndIkTests() { _ = TestEnv.UserRoot; }

    private static AnimationSet LoadRig()
    {
        var fixture = new MountFixture();
        fixture.Mount("game", "g");
        SkinnedModelBuilder.Write(Path.Combine(fixture.Dir("game"), "models", "rig.glb"));
        return new GltfAnimationReader(fixture.Vfs).Load(AssetPath.Intern("models/rig.glb"))
               ?? throw new Xunit.Sdk.XunitException("the rig did not load");
    }

    private static SkeletonPose RestPose(Skeleton skeleton)
    {
        var pose = new SkeletonPose(skeleton);
        PoseSampler.ToModelSpace(skeleton, pose);
        return pose;
    }

    private static Vector3 At(SkeletonPose pose, int joint) => pose.ModelSpace[joint].Translation;

    private static void Near(Vector3 expected, Vector3 actual, float eps = Eps) =>
        Assert.True(Vector3.Distance(expected, actual) < eps, $"expected {expected}, got {actual} ({Vector3.Distance(expected, actual)} apart)");

    private static float AngleOf(Quaternion q) => 2f * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Normalize(q).W), 0f, 1f));

    private static Quaternion ModelRotation(SkeletonPose pose, int joint)
    {
        Matrix4x4.Decompose(pose.ModelSpace[joint], out _, out var rotation, out _);
        return rotation;
    }

    // ---- two-bone IK ------------------------------------------------------------------------------

    [Fact]
    public void TheTwoBoneSolverReachesReachableTargets()
    {
        var skeleton = LoadRig().Skeleton;
        using var pose = new SkeletonPose(skeleton);
        var pole = new Vector3(1, 1, 1);
        var targets = new[]
        {
            new Vector3(1, 1, 0), new Vector3(0.5f, 1.5f, 0.5f), new Vector3(-1.2f, 0.3f, 0.4f),
            new Vector3(0, 0.5f, 0), new Vector3(0, 1.999f, 0), new Vector3(0.3f, -1.1f, -0.8f),
        };
        foreach (var target in targets)
        {
            pose.ResetToRest();
            PoseSampler.ToModelSpace(skeleton, pose);
            Assert.True(TwoBoneIk.Solve(pose, 0, 1, 2, target, pole), $"{target} is within reach");

            Near(target, At(pose, 2), 1e-3f);
            Assert.Equal(1f, Vector3.Distance(At(pose, 0), At(pose, 1)), 3);   // the bones keep their lengths
            Assert.Equal(1f, Vector3.Distance(At(pose, 1), At(pose, 2)), 3);

            // ModelSpace is left current: recomputing it from Local changes nothing.
            var tip = At(pose, 2);
            PoseSampler.ToModelSpace(skeleton, pose);
            Near(tip, At(pose, 2));
        }

        // It bends toward the pole: the middle joint sits on the pole's side of the root-target line.
        pose.ResetToRest();
        PoseSampler.ToModelSpace(skeleton, pose);
        TwoBoneIk.Solve(pose, 0, 1, 2, new Vector3(0, 1.2f, 0), new Vector3(0, 1, -5));
        Assert.True(At(pose, 1).Z < -0.5f, $"the middle joint bends toward -Z, got {At(pose, 1)}");
        pose.ResetToRest();
        PoseSampler.ToModelSpace(skeleton, pose);
        TwoBoneIk.Solve(pose, 0, 1, 2, new Vector3(0, 1.2f, 0), new Vector3(0, 1, 5));
        Assert.True(At(pose, 1).Z > 0.5f, $"the middle joint bends toward +Z, got {At(pose, 1)}");
    }

    [Fact]
    public void TheTwoBoneSolverClampsUnreachableTargets()
    {
        var skeleton = LoadRig().Skeleton;
        using var pose = RestPose(skeleton);

        // Too far: straight at it, fully stretched, and it says so.
        var far = new Vector3(3, 4, 0);   // 5 m away; the chain is 2 m long
        Assert.False(TwoBoneIk.Solve(pose, 0, 1, 2, far, new Vector3(0, 0, 1)));
        Near(Vector3.Normalize(far) * 2f, At(pose, 2), 1e-3f);
        Near(Vector3.Normalize(far), At(pose, 1), 1e-3f);

        // Weight 0 changes nothing; a half weight lands in between.
        pose.ResetToRest();
        PoseSampler.ToModelSpace(skeleton, pose);
        TwoBoneIk.Solve(pose, 0, 1, 2, new Vector3(1, 1, 0), new Vector3(1, 1, 1), weight: 0f);
        Near(new Vector3(0, 2, 0), At(pose, 2));
        TwoBoneIk.Solve(pose, 0, 1, 2, new Vector3(2, 0, 0), new Vector3(1, 1, 1), weight: 0.5f);
        var half = At(pose, 2);
        Assert.True(half.X > 0.3f && half.X < 1.9f && half.Y > 0.3f, $"half way, got {half}");

        // Not one branch: refused rather than solved nonsense.
        Assert.Throws<ArgumentException>(() => TwoBoneIk.Solve(pose, 2, 1, 0, Vector3.One, Vector3.UnitZ));
    }

    [Fact]
    public void KeepingTheTipRotationHoldsAHandsAngleWhileTheArmMoves()
    {
        var skeleton = HumanoidSkeletonBuilder.Build();
        int upper = skeleton.IndexOf(HumanoidSkeletonBuilder.UpperArmR);
        int fore = skeleton.IndexOf(HumanoidSkeletonBuilder.ForearmR);
        int hand = skeleton.IndexOf(HumanoidSkeletonBuilder.HandR);
        using var pose = new SkeletonPose(skeleton);
        pose.Local[hand].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 30 * Deg);
        PoseSampler.ToModelSpace(skeleton, pose);
        var before = ModelRotation(pose, hand);

        var target = new Vector3(0.4f, 1.3f, -0.2f);
        Assert.True(TwoBoneIk.Solve(pose, upper, fore, hand, target, new Vector3(0.4f, 1.6f, 1f), keepTipRotation: true));
        Near(target, At(pose, hand), 1e-3f);
        Assert.True(MathF.Abs(Quaternion.Dot(before, ModelRotation(pose, hand))) > 1 - Eps, "the hand keeps its model-space rotation");
    }

    // ---- aim chain --------------------------------------------------------------------------------

    private static AimJoint[] AimChain(Skeleton skeleton) => new[]
    {
        new AimJoint(skeleton.IndexOf(HumanoidSkeletonBuilder.Spine), 0.5f, 20 * Deg, 30 * Deg),
        new AimJoint(skeleton.IndexOf(HumanoidSkeletonBuilder.Chest), 0.3f, 20 * Deg, 30 * Deg),
        new AimJoint(skeleton.IndexOf(HumanoidSkeletonBuilder.Head), 0.2f, 40 * Deg, 60 * Deg),
    };

    private static Vector3 Forward(SkeletonPose pose, int joint) => Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, pose.ModelSpace[joint]));

    [Fact]
    public void AimPitchTurnsTheSpineWithinItsLimits()
    {
        var skeleton = HumanoidSkeletonBuilder.Build();
        int spine = skeleton.IndexOf(HumanoidSkeletonBuilder.Spine);
        int chest = skeleton.IndexOf(HumanoidSkeletonBuilder.Chest);
        int head = skeleton.IndexOf(HumanoidSkeletonBuilder.Head);
        int foot = skeleton.IndexOf(HumanoidSkeletonBuilder.FootL);
        var chain = AimChain(skeleton);
        using var pose = RestPose(skeleton);

        // A small aim is shared by weight: 5°, 3° and 2° of 10°, and the head looks 10° up.
        AimChainIk.Solve(pose, chain, 10 * Deg, 0f);
        Assert.Equal(5f, AngleOf(pose.Local[spine].Rotation) / Deg, 2);
        Assert.Equal(3f, AngleOf(pose.Local[chest].Rotation) / Deg, 2);
        Assert.Equal(2f, AngleOf(pose.Local[head].Rotation) / Deg, 2);
        var look = Forward(pose, head);
        Assert.Equal(10f, MathF.Asin(look.Y) / Deg, 2);      // up, positive pitch
        Assert.Equal(0f, look.X, 4);

        // A big one stops at each joint's limit: 20° + 20° + 16°, not 40° + 24° + 16°.
        pose.ResetToRest();
        PoseSampler.ToModelSpace(skeleton, pose);
        AimChainIk.Solve(pose, chain, 80 * Deg, 0f);
        Assert.Equal(20f, AngleOf(pose.Local[spine].Rotation) / Deg, 2);
        Assert.Equal(20f, AngleOf(pose.Local[chest].Rotation) / Deg, 2);
        Assert.Equal(16f, AngleOf(pose.Local[head].Rotation) / Deg, 2);
        Assert.Equal(56f, MathF.Asin(Forward(pose, head).Y) / Deg, 2);
        Assert.Equal(20f, MathF.Asin(Forward(pose, spine).Y) / Deg, 2);

        // Pitch and yaw together: the head ends up looking along the summed yaw, then the summed pitch.
        pose.ResetToRest();
        PoseSampler.ToModelSpace(skeleton, pose);
        AimChainIk.Solve(pose, chain, -30 * Deg, 40 * Deg);
        var expected = Vector3.Transform(-Vector3.UnitZ,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 40 * Deg) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, -30 * Deg));
        Near(expected, Forward(pose, head), 1e-3f);
        Assert.True(Forward(pose, head).X < 0, "positive yaw turns left (toward -X)");

        // Only the chain turns: the legs are where they were.
        Near(new Vector3(-HumanoidSkeletonBuilder.Stance, HumanoidSkeletonBuilder.FootHeight, 0), At(pose, foot));
    }

    [Fact]
    public void AnAimIkComponentTurnsItsEntitysRegisteredPoseInTheLatePhase()
    {
        using var app = HeadlessApp.Bare().Boot("aim");
        var world = app.World;
        var skeleton = HumanoidSkeletonBuilder.Build();
        using var pose = new SkeletonPose(skeleton);
        var body = world.Create(Transform.At(Vector3.Zero), "body");
        world.Resources.Get<SkeletonPoses>().Set(body, pose);
        // One registration: the skinned renderer's seam (#117) has it too.
        Assert.True(world.Resources.Get<SkinPoses>().TryGet(body.Id, out var drawn) && ReferenceEquals(pose, drawn));
        world.Add(body, new AimIk
        {
            Pitch = 80 * Deg,
            Weight = 1f,
            Joints = new()
            {
                new AimIkJoint { Joint = HumanoidSkeletonBuilder.Spine, Weight = 0.5f, PitchLimit = 20 },
                new AimIkJoint { Joint = "no_such_joint", Weight = 0.5f },   // said once, left out
                new AimIkJoint { Joint = HumanoidSkeletonBuilder.Head, Weight = 0.5f, PitchLimit = 10 },
            },
        });

        for (int i = 0; i < 3; i++)
        {
            pose.ResetToRest();                          // what the Animator does every tick
            PoseSampler.ToModelSpace(skeleton, pose);
            world.RunFixed(1f / 60f);
        }
        int spine = skeleton.IndexOf(HumanoidSkeletonBuilder.Spine), head = skeleton.IndexOf(HumanoidSkeletonBuilder.Head);
        Assert.Equal(20f, AngleOf(pose.Local[spine].Rotation) / Deg, 2);
        Assert.Equal(10f, AngleOf(pose.Local[head].Rotation) / Deg, 2);
        Assert.Equal(30f, MathF.Asin(Forward(pose, head).Y) / Deg, 2);
    }

    // ---- attachments ------------------------------------------------------------------------------

    private const string SwordRecords = """
    [
      { "type": "skeleton_sockets", "id": "rig", "model": "models/rig.glb",
        "sockets": { "hand": { "joint": "tip", "offset": [0, 0.5, 0], "angles": [0, 0, 90] } } },
      { "type": "skeleton_sockets", "id": "rig_extra", "model": "models/rig.glb",
        "sockets": { "elbow": { "joint": "mid" } } },
      { "type": "prefab", "id": "sword", "name": "sword", "parts": { "bone_attachment": "hand" } },
      { "type": "prefab", "id": "shield", "name": "shield", "parts": { "bone_attachment": { "socket": "elbow", "offset": [0.1, 0, 0] } } },
      { "type": "prefab", "id": "gem", "name": "gem", "parts": { "bone_attachment": { "bone": "root" } } }
    ]
    """;

    [Fact]
    public void AnAttachedSwordFollowsTheHandThroughAClip()
    {
        var set = LoadRig();
        var skeleton = set.Skeleton;
        var walk = set.FindClip(SkinnedModelBuilder.Walk)!;
        using var app = HeadlessApp.Simulation().File("data/sword.json", SwordRecords).Boot("sword");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;

        var knight = world.Create(Transform.At(new Vector3(5, 0, 2)), "knight");
        world.Get<Transform>(knight).LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 90 * Deg);
        using var pose = new SkeletonPose(skeleton);
        world.Resources.Get<SkeletonPoses>().Set(knight, pose, AssetPath.Intern("models/rig.glb"));

        var sword = world.Spawn(new RecordId("sage", "sword"));
        world.SetParent(sword, knight);
        var shield = world.Spawn(new RecordId("sage", "shield"));      // a socket from a second record for the model
        world.SetParent(shield, knight);
        var gem = world.Spawn(new RecordId("sage", "gem"));            // a bone, no socket
        world.SetParent(gem, knight);

        var socket = BoneAttachments.OffsetMatrix(new Vector3(0, 0.5f, 0), new Vector3(0, 0, 90));
        float lowest = float.MaxValue, highest = float.MinValue;
        for (int tick = 1; tick <= 60; tick++)
        {
            PoseSampler.Sample(walk, tick / 60f, loop: true, pose);   // what the Animator does
            PoseSampler.ToModelSpace(skeleton, pose);
            world.RunFixed(1f / 60f);

            var knightWorld = world.Get<GlobalTransform>(knight).Current.ToMatrix();
            var expected = socket * pose.ModelSpace[2] * knightWorld;
            var actual = world.Get<GlobalTransform>(sword).Current;
            Near(expected.Translation, actual.Position, 1e-3f);
            // Its blade (+Y in the sword's space) points where the socket's does.
            Near(Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, expected)), Vector3.Transform(Vector3.UnitY, actual.Rotation), 1e-3f);

            Near(Vector3.Transform(new Vector3(0.1f, 0, 0), pose.ModelSpace[1] * knightWorld), world.Get<GlobalTransform>(shield).Current.Position, 1e-3f);
            Near((pose.ModelSpace[0] * knightWorld).Translation, world.Get<GlobalTransform>(gem).Current.Position, 1e-3f);

            lowest = MathF.Min(lowest, actual.Position.Y);
            highest = MathF.Max(highest, actual.Position.Y);
        }
        Assert.True(highest - lowest > 1f, $"the hand swings, and the sword with it ({lowest}..{highest})");

        // The knight walks off: the sword goes too.
        world.Get<Transform>(knight).LocalPosition = new Vector3(-3, 1, 0);
        world.RunFixed(1f / 60f);
        var moved = socket * pose.ModelSpace[2] * world.Get<GlobalTransform>(knight).Current.ToMatrix();
        Near(moved.Translation, world.Get<GlobalTransform>(sword).Current.Position, 1e-3f);
    }

    [Fact]
    public void AnAttachmentCanFollowAnotherEntitysSkeletonWithoutBeingItsChild_AndAttachParentsIt()
    {
        var skeleton = LoadRig().Skeleton;
        using var app = HeadlessApp.Simulation().File("data/sword.json", SwordRecords).Boot("rider");
        var world = app.World;
        var horse = world.Create(Transform.At(new Vector3(0, 0, -4)), "horse");
        using var pose = RestPose(skeleton);
        world.Resources.Get<SkeletonPoses>().Set(horse, pose, AssetPath.Intern("models/rig.glb"));

        // A rider: its own root, following the horse's mid joint by Target.
        var rider = world.Create(Transform.At(Vector3.Zero), "rider");
        world.Add(rider, new BoneAttachment { Target = horse, Bone = "mid", Socket = "", Offset = new Vector3(0, 0.25f, 0) });
        // A saddlebag attached from code, to a socket.
        var bag = world.Create(Transform.At(Vector3.Zero), "bag");
        BoneAttachments.Attach(world, bag, horse, "elbow");
        // One naming a socket the model does not have: warned once, and it stays put.
        var lost = world.Create(Transform.At(new Vector3(9, 9, 9)), "lost");
        BoneAttachments.Attach(world, lost, horse, "tail");

        world.RunFixed(1f / 60f);
        Near(new Vector3(0, 1.25f, -4), world.Get<GlobalTransform>(rider).Current.Position);
        Assert.True(world.Get<Transform>(rider).LocalPosition == world.Get<GlobalTransform>(rider).Current.Position, "a root: its transform is its world pose");
        Assert.Equal(horse, bag.Parent);
        Near(new Vector3(0, 1, -4), world.Get<GlobalTransform>(bag).Current.Position);
        Near(new Vector3(9, 9, 9), world.Get<Transform>(lost).LocalPosition);
    }

    [Fact]
    public void SocketRecordsWithoutAModelOrAJointAreLoadErrors()
    {
        using var app = HeadlessApp.Simulation().File("data/bad.json", """
        [ { "type": "skeleton_sockets", "id": "nomodel", "sockets": { "hand": { "joint": "tip" } } },
          { "type": "skeleton_sockets", "id": "nojoint", "model": "models/rig.glb", "sockets": { "hand": { "offset": [0, 1, 0] } } } ]
        """).Build();
        Assert.Equal(2, app.Records.ErrorCount);
    }

    // ---- foot IK ----------------------------------------------------------------------------------

    private const float Slope = 0.57735026f;   // tan 30°: the ramp rises 0.577 m per metre along +X

    private static float RampAt(float x) => Slope * x;

    private const string WalkerRecords = """
    [ { "type": "prefab", "id": "walker", "name": "walker",
        "parts": { "foot_ik": { "pelvis": "pelvis", "footHeight": 0.1, "rayAbove": 0.5, "rayBelow": 0.5, "maxPelvisDrop": 0.4,
                                "left":  { "hip": "thigh.L", "knee": "shin.L", "foot": "foot.L" },
                                "right": { "hip": "thigh.R", "knee": "shin.R", "foot": "foot.R" } } } } ]
    """;

    private static void AddRamp(World world)
    {
        var ramp = world.Create(Transform.At(Vector3.Zero), "ramp");
        var points = new[]
        {
            new Vector3(-2, RampAt(-2), -2), new Vector3(2, RampAt(2), -2), new Vector3(-2, RampAt(-2), 2), new Vector3(2, RampAt(2), 2),
            new Vector3(-2, -3, -2), new Vector3(2, -3, -2), new Vector3(-2, -3, 2), new Vector3(2, -3, 2),
        };
        Assert.NotEqual(default, world.Resources.Get<IPhysicsWorld>().AddHull(ramp, points, Vector3.Zero));
    }

    [Fact]
    public void FeetRestOnARamp()
    {
        using var app = HeadlessApp.Gameplay().File("data/walker.json", WalkerRecords).Boot("ramp");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        AddRamp(world);

        var skeleton = HumanoidSkeletonBuilder.Build();
        using var pose = new SkeletonPose(skeleton);
        var walker = world.Spawn(new RecordId("sage", "walker"), Vector3.Zero);
        world.Resources.Get<SkeletonPoses>().Set(walker, pose);

        for (int i = 0; i < 3; i++)
        {
            pose.ResetToRest();
            PoseSampler.ToModelSpace(skeleton, pose);
            world.RunFixed(1f / 60f);
        }

        var ik = world.Get<FootIk>(walker);
        Assert.True(ik.LeftGrounded && ik.RightGrounded);
        // The pelvis dropped for the downhill (left) foot: the ramp is 0.087 m lower under it.
        Assert.Equal(RampAt(-HumanoidSkeletonBuilder.Stance), ik.PelvisDrop, 3);
        var toWorld = world.Get<GlobalTransform>(walker).Current.ToMatrix();
        foreach (var name in new[] { HumanoidSkeletonBuilder.FootL, HumanoidSkeletonBuilder.FootR })
        {
            var ankle = Vector3.Transform(pose.ModelSpace[skeleton.IndexOf(name)].Translation, toWorld);
            Assert.Equal(RampAt(ankle.X) + HumanoidSkeletonBuilder.FootHeight, ankle.Y, 3);   // the sole on the ramp
            Assert.Equal(name == HumanoidSkeletonBuilder.FootL ? -HumanoidSkeletonBuilder.Stance : HumanoidSkeletonBuilder.Stance, ankle.X, 3);
            Assert.Equal(0f, ankle.Z, 3);
        }
        // The uphill knee bent forward (toward -Z), and the foot tilted to lie along the ramp (issue #361:
        // its up is the ramp's normal, 30°, within the part's default maxFootTilt of 30°).
        Assert.True(pose.ModelSpace[skeleton.IndexOf(HumanoidSkeletonBuilder.ShinR)].Translation.Z < -0.05f);
        var rampNormal = Vector3.Normalize(new Vector3(-Slope, 1, 0));
        Near(rampNormal, Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, pose.ModelSpace[skeleton.IndexOf(HumanoidSkeletonBuilder.FootR)])), 1e-3f);
        Assert.Equal(30f, ik.RightTilt, 1);

        // On flat ground at the walker's feet, nothing moves.
        var flat = world.Spawn(new RecordId("sage", "walker"), new Vector3(0, 0, 20));
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 20)), "ground");
        world.Add(ground, Collider.Box(new Vector3(4, 1, 4)));
        using var still = new SkeletonPose(skeleton);
        world.Resources.Get<SkeletonPoses>().Set(flat, still);
        for (int i = 0; i < 3; i++)
        {
            still.ResetToRest();
            PoseSampler.ToModelSpace(skeleton, still);
            world.RunFixed(1f / 60f);
        }
        Assert.True(world.Get<FootIk>(flat).LeftGrounded);
        Assert.Equal(0f, world.Get<FootIk>(flat).PelvisDrop, 3);
        Near(new Vector3(HumanoidSkeletonBuilder.Stance, HumanoidSkeletonBuilder.FootHeight, 0), still.ModelSpace[skeleton.IndexOf(HumanoidSkeletonBuilder.FootR)].Translation, 1e-3f);
    }
}

// Solving, following and planting every tick allocate nothing (02 §4.6). Measured per thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class AttachmentAndIkAllocationTests
{
    public AttachmentAndIkAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void SolversAllocateNothing()
    {
        var skeleton = HumanoidSkeletonBuilder.Build();
        using var pose = new SkeletonPose(skeleton);
        int thigh = skeleton.IndexOf(HumanoidSkeletonBuilder.ThighL), shin = skeleton.IndexOf(HumanoidSkeletonBuilder.ShinL), foot = skeleton.IndexOf(HumanoidSkeletonBuilder.FootL);
        var chain = new[]
        {
            new AimJoint(skeleton.IndexOf(HumanoidSkeletonBuilder.Spine), 0.5f, 0.3f, 0.5f),
            new AimJoint(skeleton.IndexOf(HumanoidSkeletonBuilder.Head), 0.5f, 0.6f, 1f),
        };
        float t = 0;
        void Step()
        {
            t += 1f / 60f;
            pose.ResetToRest();
            PoseSampler.ToModelSpace(skeleton, pose);
            TwoBoneIk.Solve(pose, thigh, shin, foot, new Vector3(-0.15f, 0.3f + 0.1f * MathF.Sin(t), -0.2f), new Vector3(-0.15f, 0.5f, -1f), 0.8f, keepTipRotation: true);
            AimChainIk.Solve(pose, chain, MathF.Sin(t), MathF.Cos(t));
        }
        for (int i = 0; i < 10; i++) Step();
        AllocationProbe.AssertNone(10_000, Step);
    }

    [Fact]
    public void AttachmentsAimAndFootIkAllocateNothingPerTick()
    {
        using var app = HeadlessApp.Gameplay().File("data/walker.json", """
        [ { "type": "prefab", "id": "walker", "name": "walker",
            "parts": { "foot_ik": { "pelvis": "pelvis", "left": { "hip": "thigh.L", "knee": "shin.L", "foot": "foot.L" },
                                                        "right": { "hip": "thigh.R", "knee": "shin.R", "foot": "foot.R" } },
                       "aim_ik": { "joints": [ { "joint": "spine", "weight": 0.5 }, { "joint": "head", "weight": 0.5 } ] } } },
          { "type": "prefab", "id": "sword", "name": "sword", "parts": { "bone_attachment": { "bone": "hand.R" } } } ]
        """).Boot("alloc");
        var world = app.World;
        var ground = world.Create(Transform.At(new Vector3(0, -0.5f, 0)), "ground");
        world.Add(ground, Collider.Box(new Vector3(8, 1, 8)));
        var skeleton = HumanoidSkeletonBuilder.Build();
        using var pose = new SkeletonPose(skeleton);
        var walker = world.Spawn(new RecordId("sage", "walker"), Vector3.Zero);
        world.Resources.Get<SkeletonPoses>().Set(walker, pose);
        var sword = world.Spawn(new RecordId("sage", "sword"));
        world.SetParent(sword, walker);

        float t = 0;
        void Step()
        {
            t += 1f / 60f;
            pose.ResetToRest();
            PoseSampler.ToModelSpace(skeleton, pose);
            world.Get<AimIk>(walker).Pitch = 0.3f * MathF.Sin(t);
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
        }
        for (int i = 0; i < 10; i++) Step();

        // Everything #120 adds runs in Phase.Late, which must allocate nothing, and nothing else in the
        // tick may either — except the physics backend's own step (PhysicsStepSystem), which allocates 40
        // bytes a tick in this scene: that is the backend's, not this issue's code (see the report).
        long lateBefore = ScopeBytes("Fixed.Late"), physicsBefore = ScopeBytes("Fixed.Physics");
        var report = AllocationProbe.Measure(200, Step);
        long late = ScopeBytes("Fixed.Late") - lateBefore, physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(late == 0 && report.Bytes - physics == 0, report.ToString());
        Assert.True(world.Get<FootIk>(walker).LeftGrounded);
        Near(Vector3.Transform(Vector3.Zero, pose.ModelSpace[skeleton.IndexOf(HumanoidSkeletonBuilder.HandR)]),
             world.Get<Transform>(sword).LocalPosition);
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"expected {expected}, got {actual}");

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }
}
