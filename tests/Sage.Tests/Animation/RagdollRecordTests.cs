#nullable enable
using System;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The `ragdoll` record and resolving it against a skeleton (phase 4k, issue #243, docs/design/12 "As built
// (the ragdoll record, issue #243)"). Pure data: nothing here builds a physics body.
public class RagdollRecordTests
{
    public RagdollRecordTests() { _ = TestEnv.UserRoot; }

    private const float DegToRad = MathF.PI / 180f;
    private static readonly AssetPath Model = AssetPath.Intern("models/rig.glb");

    private const string Humanoid = """
    [ { "type": "ragdoll", "id": "rig", "model": "models/rig.glb", "friction": 0.9, "settleSpeed": 0.3,
        "bodies": {
          "pelvis":  { "shape": "Box", "size": [0.3, 0.2, 0.2], "mass": 10 },
          "chest":   { "shape": "Box", "size": [0.4, 0.5, 0.2], "mass": 20, "joint": { "kind": "Ball", "swing": 30, "twistMin": -20, "twistMax": 25 } },
          "head":    { "shape": "Sphere", "size": [0.12, 0, 0], "offset": [0, 0.1, 0], "mass": 5, "joint": { "swing": 40 } },
          "thigh.L": { "shape": "Capsule", "size": [0.07, 0.3, 0], "offset": [0, -0.2, 0], "mass": 8, "joint": { "swing": 70, "axis": [0, -1, 0] } },
          "shin.L":  { "shape": "Capsule", "size": [0.06, 0.3, 0], "offset": [0, -0.2, 0], "mass": 4,
                       "joint": { "kind": "Hinge", "axis": [-2, 0, 0], "min": 0, "max": 140 } },
          "wing":    { "shape": "Box", "size": [0.1, 0.1, 0.1], "mass": 1 }
        } } ]
    """;

    private static HeadlessApp Boot(string records) => HeadlessApp.Simulation().File("data/ragdoll.json", records).Boot("rag");

    private static RagdollRecord Record(HeadlessApp app) => app.Records.All<RagdollRecord>().Single();

    [Fact]
    public void ARagdollRecordLoadsItsBodiesAndSettings()
    {
        using var app = Boot(Humanoid);
        Assert.True(app.Records.ErrorCount == 0, string.Join("\n", app.Records.LoadErrors));
        var record = Record(app);
        Assert.Equal(Model, record.Model);
        Assert.Equal(0.9f, record.Friction);
        Assert.Equal(0.3f, record.SettleSpeed);
        Assert.Equal(1f, record.SettleTime);   // the default
        Assert.Equal(0.1f, record.Damping);
        Assert.Equal(6, record.Bodies.Count);
        var shin = record.Bodies["shin.L"];
        Assert.Equal(RagdollShape.Capsule, shin.Shape);
        Assert.Equal(RagdollJointKind.Hinge, shin.Joint.Kind);
        Assert.Equal(140f, shin.Joint.Max);
    }

    [Fact]
    public void RagdollRecordsWithoutAModelAShapeAMassOrSaneLimitsAreLoadErrors()
    {
        using var app = Boot("""
        [ { "type": "ragdoll", "id": "nomodel", "bodies": { "pelvis": { "shape": "Box", "size": [1, 1, 1], "mass": 1 } } },
          { "type": "ragdoll", "id": "noshape", "model": "models/rig.glb", "bodies": { "pelvis": { "size": [1, 1, 1], "mass": 1 } } },
          { "type": "ragdoll", "id": "nomass", "model": "models/rig.glb", "bodies": { "pelvis": { "shape": "Sphere", "size": [1, 0, 0] } } },
          { "type": "ragdoll", "id": "twist", "model": "models/rig.glb", "bodies": {
              "a": { "shape": "Sphere", "size": [1, 0, 0], "mass": 1, "joint": { "twistMin": 40, "twistMax": 10 } } } },
          { "type": "ragdoll", "id": "hinge", "model": "models/rig.glb", "bodies": {
              "a": { "shape": "Sphere", "size": [1, 0, 0], "mass": 1, "joint": { "kind": "Hinge", "min": 50, "max": 10 } } } },
          { "type": "ragdoll", "id": "cone", "model": "models/rig.glb", "bodies": {
              "a": { "shape": "Sphere", "size": [1, 0, 0], "mass": 1, "joint": { "swing": 200 } } } },
          { "type": "ragdoll", "id": "fine", "model": "models/rig.glb", "bodies": {
              "a": { "shape": "Sphere", "size": [1, 0, 0], "mass": 1, "joint": { "swing": 180 } } } } ]
        """);
        Assert.Equal(6, app.Records.ErrorCount);
        string errors = string.Join("\n", app.Records.LoadErrors);
        Assert.Contains("nomodel", errors);
        Assert.Contains("no shape", errors);
        Assert.Contains("mass", errors);
        Assert.Contains("twistMax", errors);
        Assert.Contains("is more than max", errors);
        Assert.Contains("between 0 and 180", errors);
        Assert.DoesNotContain("fine", errors);
    }

    [Fact]
    public void ResolvingAgainstASkeletonLinksEachBodyToItsNearestBodiedAncestor()
    {
        using var app = Boot(Humanoid);
        var skeleton = HumanoidSkeletonBuilder.Build();
        var table = Ragdolls.Resolve(app.Records, skeleton, Model)!;

        // The wing is no joint of this skeleton: skipped. The rest come in joint order.
        Assert.Equal(new[] { "pelvis", "chest", "head", "thigh.L", "shin.L" }, table.Bodies.Select(b => b.Name));
        Assert.Equal(new[] { -1, 0, 1, 0, 3 }, table.Bodies.Select(b => b.Parent));   // spine and neck have no body: chest hangs from the pelvis, head from the chest
        Assert.Equal(skeleton.IndexOf("shin.L"), table.Bodies[4].Joint);
        Assert.Equal(4, table.BodyOfJoint(skeleton.IndexOf("shin.L")));
        Assert.Equal(-1, table.BodyOfJoint(skeleton.IndexOf("spine")));
        Assert.All(table.Bodies.Select((b, i) => (b, i)), x => Assert.True(x.b.Parent < x.i));

        Assert.Equal(0.9f, table.Friction);
        Assert.Equal(0.3f, table.SettleSpeed);
        Assert.Equal(1f, table.SettleTime);

        var chest = table.Bodies[1];
        Assert.Equal(RagdollShape.Box, chest.Shape);
        Assert.Equal(20f, chest.Mass);
        Assert.Equal(RagdollJointKind.Ball, chest.JointKind);
        Assert.Equal(30 * DegToRad, chest.Swing, 5);
        Assert.Equal(-20 * DegToRad, chest.TwistMin, 5);
        Assert.Equal(25 * DegToRad, chest.TwistMax, 5);

        var head = table.Bodies[2];
        Assert.Equal(new Vector3(0, 0.1f, 0), head.Offset);
        Assert.Equal(Vector3.UnitY, head.Axis);   // the default: the bone's direction

        var shin = table.Bodies[4];
        Assert.Equal(RagdollJointKind.Hinge, shin.JointKind);
        Assert.Equal(-Vector3.UnitX, shin.Axis);  // normalised
        Assert.Equal(0f, shin.HingeMin);
        Assert.Equal(140 * DegToRad, shin.HingeMax, 5);
    }

    [Fact]
    public void ResolvingIsCachedPerSkeletonAndRecordsAndAMissingJointWarnsOnce()
    {
        using var app = Boot("""
        [ { "type": "ragdoll", "id": "rig", "model": "models/rig.glb", "bodies": {
              "pelvis": { "shape": "Box", "size": [1, 1, 1], "mass": 10 },
              "wing_cache_test": { "shape": "Box", "size": [1, 1, 1], "mass": 1 } } } ]
        """);
        var skeleton = HumanoidSkeletonBuilder.Build();
        using var capture = new CaptureSink();
        var first = Ragdolls.Resolve(app.Records, skeleton, Model);
        var second = Ragdolls.Resolve(app.Records, skeleton, Model);
        Assert.Same(first, second);
        Assert.Single(capture.Entries, e => e.Level == LogLevel.Warn && e.Category == LogCat.Animation && e.Message.Contains("wing_cache_test"));
        Assert.NotSame(first, Ragdolls.Resolve(app.Records, HumanoidSkeletonBuilder.Build(), Model));
        Assert.Null(Ragdolls.Resolve(app.Records, skeleton, AssetPath.Intern("models/other.glb")));
    }

    [Fact]
    public void SeveralRecordsForAModelMergeInIdOrderAndTheFirstOneWinsANameAndTheSettings()
    {
        using var app = Boot("""
        [ { "type": "ragdoll", "id": "a_base", "model": "models/rig.glb", "friction": 0.5,
            "bodies": { "pelvis": { "shape": "Box", "size": [1, 1, 1], "mass": 10 } } },
          { "type": "ragdoll", "id": "b_mod", "model": "models/rig.glb", "friction": 0.99,
            "bodies": { "pelvis": { "shape": "Box", "size": [1, 1, 1], "mass": 99 },
                        "chest": { "shape": "Box", "size": [1, 1, 1], "mass": 20 } } } ]
        """);
        var table = Ragdolls.Resolve(app.Records, HumanoidSkeletonBuilder.Build(), Model)!;
        Assert.Equal(2, table.Count);
        Assert.Equal(10f, table.Bodies[0].Mass);
        Assert.Equal(0, table.Bodies[1].Parent);
        Assert.Equal(0.5f, table.Friction);
    }

    [Fact]
    public void TheMannequinsRagdollResolvesAgainstItsModel()
    {
        using var app = NpcLocomotionTests.Skeletal();
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        var npc = world.Spawn(NpcLocomotionTests.Npc, new Vector3(0, 0, 0));
        NpcLocomotionTests.Step(world, 2);
        Assert.True(Animators.TryGetPose(world, npc, out var pose));
        var skeleton = pose.Skeleton;

        var table = Ragdolls.Resolve(app.Records, skeleton, AssetPath.Intern("models/mannequin.glb"))!;
        Assert.NotNull(table);
        Assert.Equal(11, table.Count);

        int Body(string joint) => table.BodyOfJoint(skeleton.IndexOf(joint));
        Assert.Equal(-1, table.Bodies[Body("pelvis")].Parent);
        Assert.Equal(Body("pelvis"), table.Bodies[Body("chest")].Parent);   // the spine has no body
        Assert.Equal(Body("chest"), table.Bodies[Body("head")].Parent);     // nor does the neck
        foreach (var side in new[] { "l", "r" })
        {
            Assert.Equal(Body("chest"), table.Bodies[Body("upper_arm_" + side)].Parent);
            Assert.Equal(Body("upper_arm_" + side), table.Bodies[Body("forearm_" + side)].Parent);
            Assert.Equal(Body("pelvis"), table.Bodies[Body("thigh_" + side)].Parent);
            Assert.Equal(Body("thigh_" + side), table.Bodies[Body("shin_" + side)].Parent);
            Assert.Equal(-1, Body("hand_" + side));
            Assert.Equal(-1, Body("foot_" + side));

            var knee = table.Bodies[Body("shin_" + side)];
            Assert.Equal(RagdollJointKind.Hinge, knee.JointKind);
            Assert.Equal(0f, knee.HingeMin);
            Assert.Equal(140 * DegToRad, knee.HingeMax, 5);
            var elbow = table.Bodies[Body("forearm_" + side)];
            Assert.Equal(RagdollJointKind.Hinge, elbow.JointKind);
            Assert.Equal(140 * DegToRad, elbow.HingeMax, 5);
            var hip = table.Bodies[Body("thigh_" + side)];
            Assert.Equal(RagdollJointKind.Ball, hip.JointKind);
            Assert.Equal(70 * DegToRad, hip.Swing, 5);
            Assert.Equal(30 * DegToRad, hip.TwistMax, 5);
            Assert.Equal(80 * DegToRad, table.Bodies[Body("upper_arm_" + side)].Swing, 5);
        }
        Assert.Equal(40 * DegToRad, table.Bodies[Body("head")].Swing, 5);
        Assert.Equal(RagdollShape.Capsule, table.Bodies[Body("thigh_l")].Shape);
        Assert.Equal(RagdollShape.Sphere, table.Bodies[Body("head")].Shape);
        Assert.InRange(table.Bodies.Sum(b => b.Mass), 50f, 100f);
    }
}
