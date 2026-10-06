#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using SharpGLTF.Schema2;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Animation retargeting (issue #360): a clip authored on one rig plays on another of different joint
// names, count, order, rest orientations and size, through ClipRetargeter and the `skeleton_map` record,
// with the end effectors where the source puts them (scaled by the rigs' size).
public class RetargetingTests
{
    public RetargetingTests() { _ = TestEnv.UserRoot; }

    // Rig A: a small humanoid, rest rotations identity. Hips at 1 m.
    private static Skeleton RigA()
    {
        var names = new[] { "Hips", "Spine", "Arm", "Hand", "Leg", "Foot" };
        var parents = new[] { -1, 0, 1, 2, 0, 4 };
        var at = new[] { new Vector3(0, 1, 0), new Vector3(0, 0.5f, 0), new Vector3(0.3f, 0.4f, 0), new Vector3(0.5f, 0, 0),
                         new Vector3(0.1f, -0.5f, 0), new Vector3(0, -0.5f, 0) };
        var rest = at.Select(p => new Pose { Position = p, Rotation = Quaternion.Identity, Scale = Vector3.One }).ToArray();
        return new Skeleton(names, parents, rest, Enumerable.Repeat(Matrix4x4.Identity, names.Length).ToArray());
    }

    // Rig B: the same T-pose twice the size, with other names, legs listed first, an extra (unmapped)
    // twist joint between chest and shoulder, and every joint's local axes pointing some other way.
    private static Skeleton RigB()
    {
        var names = new[] { "pelvis", "thigh", "ankle", "chest", "twist", "shoulder", "wrist" };
        var parents = new[] { -1, 0, 1, 0, 3, 4, 5 };
        var global = new[] { new Vector3(0, 2, 0), new Vector3(0.2f, 1, 0), new Vector3(0.2f, 0, 0), new Vector3(0, 3, 0),
                             new Vector3(0, 3.4f, 0), new Vector3(0.6f, 3.8f, 0), new Vector3(1.6f, 3.8f, 0) };
        var rotations = new[]
        {
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2),
            Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 4),
        };
        return new Skeleton(names, parents, Locals(parents, global, rotations), Enumerable.Repeat(Matrix4x4.Identity, names.Length).ToArray());
    }

    // Local transforms that put each joint at `global` turned by `rotations`, in model space.
    private static Pose[] Locals(int[] parents, Vector3[] global, Quaternion[] rotations)
    {
        var rest = new Pose[parents.Length];
        for (int j = 0; j < rest.Length; j++)
        {
            int p = parents[j];
            var parentRot = p < 0 ? Quaternion.Identity : rotations[p];
            var parentAt = p < 0 ? Vector3.Zero : global[p];
            var inv = Quaternion.Inverse(parentRot);
            rest[j] = new Pose { Position = Vector3.Transform(global[j] - parentAt, inv), Rotation = inv * rotations[j], Scale = Vector3.One };
        }
        return rest;
    }

    private static readonly Dictionary<string, string> BtoA = new(StringComparer.Ordinal)
    {
        ["pelvis"] = "Hips", ["thigh"] = "Leg", ["ankle"] = "Foot", ["chest"] = "Spine", ["shoulder"] = "Arm", ["wrist"] = "Hand",
    };

    // A second of everything moving: the hips walk forward and turn, the spine bends, the arm swings, the leg kicks.
    private static AnimationClip ClipA(Skeleton a)
    {
        var clip = new AnimationClip("strike", a.JointCount, 1f);
        var t3 = new[] { 0f, 0.5f, 1f };
        Quaternion R(Vector3 axis, float degrees) => Quaternion.CreateFromAxisAngle(axis, degrees * MathF.PI / 180f);
        clip.SetTranslation(a.IndexOf("Hips"), AnimationInterpolation.Linear, t3, new[] { new Vector3(0, 1, 0), new Vector3(0, 1.2f, 0.5f), new Vector3(0, 1, 1) });
        clip.SetRotation(a.IndexOf("Hips"), AnimationInterpolation.Linear, t3, new[] { Quaternion.Identity, R(Vector3.UnitY, 40), Quaternion.Identity });
        clip.SetRotation(a.IndexOf("Spine"), AnimationInterpolation.Linear, new[] { 0f, 1f }, new[] { Quaternion.Identity, R(Vector3.UnitX, 30) });
        clip.SetRotation(a.IndexOf("Arm"), AnimationInterpolation.Linear, t3, new[] { Quaternion.Identity, R(Vector3.UnitZ, -60), R(Vector3.UnitZ, 20) });
        clip.SetRotation(a.IndexOf("Leg"), AnimationInterpolation.Linear, t3, new[] { Quaternion.Identity, R(Vector3.UnitX, -45), R(Vector3.UnitX, 30) });
        clip.AddEvent(0.5f, "hit");
        return clip;
    }

    private static Vector3 At(SkeletonPose pose, int joint)
    {
        var m = pose.ModelSpace[joint];
        return new Vector3(m.M41, m.M42, m.M43);
    }

    private static void Near(Vector3 expected, Vector3 actual, float tolerance) =>
        Assert.True(Vector3.Distance(expected, actual) < tolerance, $"expected {expected}, got {actual} (off by {Vector3.Distance(expected, actual)})");

    // Done (#360): a clip authored on rig A plays on rig B with matching end effectors (hand, foot and
    // hips; B is twice A's size, so twice as far from the origin), on its keys and between them.
    [Fact]
    public void RetargetedClipPutsEndEffectorsWhereTheSourceDoes()
    {
        var a = RigA();
        var b = RigB();
        var clip = ClipA(a);
        var retargeted = ClipRetargeter.Retarget(clip, a, b, BtoA);
        Assert.Equal(clip.Duration, retargeted.Duration);
        Assert.Equal(b.JointCount, retargeted.JointCount);
        Assert.False(retargeted.Animates(b.IndexOf("twist")));
        Assert.Equal("hit", Assert.Single(retargeted.Events).Name);

        using var poseA = new SkeletonPose(a);
        using var poseB = new SkeletonPose(b);
        foreach (var (time, tolerance) in new[] { (0f, 1e-3f), (0.5f, 1e-3f), (1f / 3f, 1e-3f), (1f, 1e-3f), (0.41f, 5e-3f), (0.77f, 5e-3f) })
        {
            PoseSampler.Sample(clip, time, false, poseA);
            PoseSampler.ToModelSpace(a, poseA);
            PoseSampler.Sample(retargeted, time, false, poseB);
            PoseSampler.ToModelSpace(b, poseB);
            Near(2 * At(poseA, a.IndexOf("Hand")), At(poseB, b.IndexOf("wrist")), tolerance);
            Near(2 * At(poseA, a.IndexOf("Foot")), At(poseB, b.IndexOf("ankle")), tolerance);
            Near(2 * At(poseA, a.IndexOf("Hips")), At(poseB, b.IndexOf("pelvis")), tolerance);
            Near(2 * At(poseA, a.IndexOf("Arm")), At(poseB, b.IndexOf("shoulder")), tolerance);
        }
    }

    // The rest offset: a pose that is A's rest is B's rest, whatever way B's joint axes point; an
    // explicit scale replaces the height ratio; an unmapped joint stays at rest.
    [Fact]
    public void RestMapsToRestAndScaleAndUnmappedJointsHold()
    {
        var a = RigA();
        var b = RigB();
        var still = new AnimationClip("still", a.JointCount, 0.5f);
        var restB = b.RestPose.ToArray();
        var retargeted = ClipRetargeter.Retarget(still, a, b, BtoA);
        using var pose = new SkeletonPose(b);
        PoseSampler.Sample(retargeted, 0.25f, false, pose);
        for (int j = 0; j < b.JointCount; j++)
        {
            Assert.True(Vector3.Distance(restB[j].Position, pose.Local[j].Position) < 1e-4f, $"{b.NameOf(j)} moved");
            Assert.True(MathF.Abs(Quaternion.Dot(restB[j].Rotation, pose.Local[j].Rotation)) > 1 - 1e-5f, $"{b.NameOf(j)} turned");
        }

        // Scale 1: the hips move as far as A's, not twice as far.
        var clip = ClipA(a);
        var once = ClipRetargeter.Retarget(clip, a, b, BtoA, scale: 1f);
        PoseSampler.Sample(once, 1f, false, pose);
        PoseSampler.ToModelSpace(b, pose);
        Near(new Vector3(0, 2, 1), At(pose, b.IndexOf("pelvis")), 1e-3f);

        // Only the arm mapped: the legs and hips hold their rest.
        var armOnly = ClipRetargeter.Retarget(clip, a, b, new Dictionary<string, string> { ["shoulder"] = "Arm", ["nope"] = "Arm", ["wrist"] = "Nope" });
        Assert.True(armOnly.Animates(b.IndexOf("shoulder")));
        Assert.False(armOnly.Animates(b.IndexOf("pelvis")));
        Assert.False(armOnly.Animates(b.IndexOf("wrist")));
    }

    // ---- the record, through an animator ----------------------------------------------------------

    // A rig that has no clips of its own: base → knee → end, twice the test rig's height (each bone 2 m),
    // the knee's axes turned 90° about X and the end's −90° about Y, so its locals are nothing like the source's.
    private static void WriteBigRig(string file)
    {
        var model = ModelRoot.CreateModel();
        var scene = model.UseScene("scene");
        var kneeRot = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2);
        var endRot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2);
        var bones = Locals(new[] { -1, 0, 1 }, new[] { Vector3.Zero, new Vector3(0, 2, 0), new Vector3(0, 4, 0) },
                           new[] { Quaternion.Identity, kneeRot, endRot });
        var root = scene.CreateNode("base");
        var knee = root.CreateNode("knee");
        knee.LocalMatrix = Matrix4x4.CreateFromQuaternion(bones[1].Rotation) * Matrix4x4.CreateTranslation(bones[1].Position);
        var end = knee.CreateNode("end");
        end.LocalMatrix = Matrix4x4.CreateFromQuaternion(bones[2].Rotation) * Matrix4x4.CreateTranslation(bones[2].Position);
        var skin = model.CreateSkin("big");
        skin.BindJoints(Matrix4x4.Identity, root, knee, end);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, model.WriteGLB().ToArray());
    }

    private const string Content = """
    [
      { "type": "skeleton_map", "id": "big", "source": "models/rig.glb", "target": "models/big.glb", "scale": 2,
        "bones": { "base": "root", "knee": "mid", "end": "tip" } },
      { "type": "anim_events", "id": "rig", "model": "models/rig.glb", "clips": { "walk": [ { "time": 0.5, "name": "half" } ] } },
      { "type": "anim_graph", "id": "walker", "initial": "walk", "states": { "walk": { "clip": "walk" } } },
      { "type": "prefab", "id": "big", "name": "big", "parts": { "animator": { "graph": "walker", "model": "models/big.glb" } } }
    ]
    """;

    // Done (#360), with records: an animator on a model with no clips plays the clip library's walk
    // through a skeleton_map, its tip where the library rig's is (times the map's scale), with the
    // library's clip events.
    [Fact]
    public void SkeletonMapLetsAModelPlayAnotherRigsClips()
    {
        var files = new MountFixture();
        files.Write("game", "data/big.json", Content);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        WriteBigRig(Path.Combine(files.Dir("game"), "models", "big.glb"));
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot("retarget");
        var world = app.World;
        var e = world.Spawn(new RecordId("game", "big"), Vector3.Zero);
        for (int i = 0; i < 20; i++) world.RunFixed(1f / 60f);

        Assert.Equal(1f, Animators.ClipWeight(world, e, "walk"));
        Assert.True(Animators.TryGetPose(world, e, out var pose));
        Assert.True(app.Engine.Animations.TryGet(AssetPath.Intern("models/rig.glb"), out var rig));
        float time = world.Get<Animator>(e).Layers![0].Phase * SkinnedModelBuilder.WalkDuration;
        using var expected = new SkeletonPose(rig.Skeleton);
        PoseSampler.Sample(rig.FindClip("walk")!, time, true, expected);
        PoseSampler.ToModelSpace(rig.Skeleton, expected);
        var tip = At(expected, rig.Skeleton.IndexOf("tip"));
        Assert.True(Vector3.Distance(Vector3.Zero, tip - new Vector3(0, 2, 0)) > 0.1f, "the walk should have moved the tip off its rest");
        Near(2 * tip, At(pose, pose.Skeleton.IndexOf("end")), 0.01f);
        Near(2 * At(expected, rig.Skeleton.IndexOf("mid")), At(pose, pose.Skeleton.IndexOf("knee")), 0.01f);

        Assert.True(app.Engine.Animations.TryGet(AssetPath.Intern("models/big.glb"), out var big));
        Assert.Null(big.FindClip("walk"));                                   // the model's own set is untouched
    }

    // Content checks: a map needs both models (not the same one), joints with names, a sane scale and rate.
    [Fact]
    public void BadSkeletonMapsAreLoadErrors()
    {
        var files = new MountFixture();
        files.Write("game", "data/maps.json", """
        [ { "type": "skeleton_map", "id": "none", "scale": -1, "sampleRate": 0 },
          { "type": "skeleton_map", "id": "self", "source": "models/rig.glb", "target": "models/rig.glb", "bones": { "root": "" } } ]
        """);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot("bad");
        // none: source, target, scale, sampleRate, bones; self: target is the source, bones.root names nothing.
        Assert.Equal(7, app.Records.ErrorCount);
    }
}
