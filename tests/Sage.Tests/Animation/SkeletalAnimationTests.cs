#nullable enable
using System.IO;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Skeletons, clips and headless pose sampling (issue #116, docs/design/12 "As built (skeletons and
// sampling)"), against a real skinned .glb that SkinnedModelBuilder writes: a root → mid → tip chain
// with an "idle" clip (linear root bob, stepped tip scale) and a "walk" clip (linear mid swing about +Z,
// cubic-spline root slide). Every expected value below is worked out from those keys by hand.
public class SkeletalAnimationTests
{
    private const float Eps = 1e-4f;

    private static AnimationSet LoadRig(out GltfAnimationReader reader, out AssetPath path)
    {
        var fixture = new MountFixture();
        fixture.Mount("game", "g");
        SkinnedModelBuilder.Write(Path.Combine(fixture.Dir("game"), "models", "rig.glb"));
        reader = new GltfAnimationReader(fixture.Vfs);
        path = AssetPath.Intern("models/rig.glb");
        return reader.Load(path) ?? throw new Xunit.Sdk.XunitException("the rig did not load");
    }

    private static AnimationSet LoadRig() => LoadRig(out _, out _);

    private static void Near(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < Eps, $"expected {expected}, got {actual}");
    }

    private static void Near(Quaternion expected, Quaternion actual)
    {
        // q and -q are the same rotation.
        float dot = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(expected), Quaternion.Normalize(actual)));
        Assert.True(dot > 1 - Eps, $"expected {expected}, got {actual}");
    }

    private static Quaternion AboutZ(float degrees) => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, degrees * MathF.PI / 180f);

    [Fact]
    public void TheSkeletonIsReadInParentFirstOrderWithItsRestPoseAndInverseBinds()
    {
        var set = LoadRig();
        var skeleton = set.Skeleton;

        Assert.Equal(3, skeleton.JointCount);
        Assert.Equal(new[] { SkinnedModelBuilder.Root, SkinnedModelBuilder.Mid, SkinnedModelBuilder.Tip },
                     new[] { skeleton.NameOf(0), skeleton.NameOf(1), skeleton.NameOf(2) });
        Assert.Equal(new[] { -1, 0, 1 }, skeleton.Parents.ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, skeleton.JointOfSkinIndex.ToArray());
        Assert.Equal(1, skeleton.IndexOf("mid"));
        Assert.Equal(-1, skeleton.IndexOf("tail"));

        Near(Vector3.Zero, skeleton.RestPose[0].Position);
        Near(new Vector3(0, 1, 0), skeleton.RestPose[1].Position);
        Near(new Vector3(0, 1, 0), skeleton.RestPose[2].Position);
        Near(Quaternion.Identity, skeleton.RestPose[2].Rotation);
        Near(Vector3.One, skeleton.RestPose[2].Scale);

        // Each inverse bind undoes its joint's rest model-space placement.
        Near(new Vector3(0, -2, 0), skeleton.InverseBind[2].Translation);
        Near(new Vector3(0, -1, 0), skeleton.InverseBind[1].Translation);

        Assert.Equal(2, set.Clips.Count);
        Assert.Equal(SkinnedModelBuilder.IdleDuration, set.FindClip("idle")!.Duration, 4);
        Assert.Equal(SkinnedModelBuilder.WalkDuration, set.FindClip("walk")!.Duration, 4);
        Assert.Null(set.FindClip("run"));
        Assert.Empty(set.FindClip("walk")!.Events);   // #119 fills them
    }

    [Fact]
    public void JointTransformsMatchTheKeysAtChosenTimes()
    {
        var set = LoadRig();
        using var pose = new SkeletonPose(set.Skeleton);
        var idle = set.FindClip("idle")!;
        var walk = set.FindClip("walk")!;

        // Linear: halfway up the bob.
        PoseSampler.Sample(idle, 0.5f, loop: false, pose);
        Near(new Vector3(0, 0.05f, 0), pose.Local[0].Position);
        // Step: the tip holds (1,1,1) until the 1 s key, then (2,2,2).
        Near(Vector3.One, pose.Local[2].Scale);
        PoseSampler.Sample(idle, 0.999f, loop: false, pose);
        Near(Vector3.One, pose.Local[2].Scale);
        PoseSampler.Sample(idle, 1f, loop: false, pose);
        Near(new Vector3(2, 2, 2), pose.Local[2].Scale);
        Near(new Vector3(0, 0.1f, 0), pose.Local[0].Position);
        // Joints the clip does not move keep their rest transform.
        Near(new Vector3(0, 1, 0), pose.Local[1].Position);
        Near(Quaternion.Identity, pose.Local[1].Rotation);
        Assert.False(idle.Animates(1));
        Assert.True(idle.Animates(0));

        // Linear rotation: a quarter of the way is 45°, the key itself 90°.
        PoseSampler.Sample(walk, 0.25f, loop: true, pose);
        Near(AboutZ(45), pose.Local[1].Rotation);
        PoseSampler.Sample(walk, 0.5f, loop: true, pose);
        Near(AboutZ(90), pose.Local[1].Rotation);
        // Cubic spline, glTF's Hermite: 0.5·0 + 0.125·1 + 0.5·2 − 0.125·3 = 0.75.
        Near(new Vector3(0.75f, 0, 0), pose.Local[0].Position);
        PoseSampler.Sample(walk, 0f, loop: true, pose);
        Near(Vector3.Zero, pose.Local[0].Position);
    }

    [Fact]
    public void ALoopingClipWrapsAndAOneShotClampsAtItsEnd()
    {
        var set = LoadRig();
        using var pose = new SkeletonPose(set.Skeleton);
        var walk = set.FindClip("walk")!;
        var idle = set.FindClip("idle")!;

        Assert.Equal(0.25f, PoseSampler.ClipTime(walk, 1.25f, loop: true), 4);
        Assert.Equal(0.75f, PoseSampler.ClipTime(walk, -0.25f, loop: true), 4);
        Assert.Equal(1f, PoseSampler.ClipTime(walk, 1.25f, loop: false), 4);
        Assert.Equal(0f, PoseSampler.ClipTime(walk, -3f, loop: false), 4);
        Assert.Equal(0f, PoseSampler.ClipTime(walk, float.NaN, loop: true), 4);

        // Looping, 1.25 s is 0.25 s into the second pass: 45° again.
        PoseSampler.Sample(walk, 1.25f, loop: true, pose);
        Near(AboutZ(45), pose.Local[1].Rotation);
        PoseSampler.Sample(walk, 3.5f, loop: true, pose);
        Near(AboutZ(90), pose.Local[1].Rotation);

        // Once, it holds the last key: back at identity, the root at the end of its slide.
        PoseSampler.Sample(walk, 1.25f, loop: false, pose);
        Near(Quaternion.Identity, pose.Local[1].Rotation);
        Near(new Vector3(2, 0, 0), pose.Local[0].Position);

        // And idle, well past its 2 s: bob back at rest, tip at its last stepped scale.
        PoseSampler.Sample(idle, 5f, loop: false, pose);
        Near(Vector3.Zero, pose.Local[0].Position);
        Near(new Vector3(2, 2, 2), pose.Local[2].Scale);
        // Looped, 2.5 s is 0.5 s: halfway up again, and the tip back at (1,1,1).
        PoseSampler.Sample(idle, 2.5f, loop: true, pose);
        Near(new Vector3(0, 0.05f, 0), pose.Local[0].Position);
        Near(Vector3.One, pose.Local[2].Scale);
    }

    [Fact]
    public void BlendingWorksWithAndWithoutAMask()
    {
        var set = LoadRig();
        var skeleton = set.Skeleton;
        using var a = new SkeletonPose(skeleton);
        using var b = new SkeletonPose(skeleton);
        using var result = new SkeletonPose(skeleton);

        PoseSampler.Sample(set.FindClip("idle")!, 1f, loop: false, a);   // root (0,0.1,0), tip scale 2
        PoseSampler.Sample(set.FindClip("walk")!, 0.5f, loop: true, b);  // root (0.75,0,0), mid 90°

        // No mask: every joint halfway.
        PoseSampler.Blend(a, b, 0.5f, null, result);
        Near(new Vector3(0.375f, 0.05f, 0), result.Local[0].Position);
        Near(AboutZ(45), result.Local[1].Rotation);
        Near(new Vector3(1.5f, 1.5f, 1.5f), result.Local[2].Scale);

        // Weight 0 is a, 1 is b.
        PoseSampler.Blend(a, b, 0f, null, result);
        Near(new Vector3(0, 0.1f, 0), result.Local[0].Position);
        PoseSampler.Blend(a, b, 1f, null, result);
        Near(new Vector3(0.75f, 0, 0), result.Local[0].Position);

        // A mask on mid's branch: the root stays a's; mid and tip take b's.
        var upper = new JointMask(skeleton);
        Assert.True(upper.SetBranch("mid", 1f));
        Assert.False(upper.SetBranch("tail", 1f));
        Assert.Equal(new[] { 0f, 1f, 1f }, upper.Weights.ToArray());
        PoseSampler.Blend(a, b, 1f, upper, result);
        Near(new Vector3(0, 0.1f, 0), result.Local[0].Position);
        Near(AboutZ(90), result.Local[1].Rotation);
        Near(Vector3.One, result.Local[2].Scale);

        // Mask weights scale the blend's: half of a half.
        upper[1] = 0.5f;
        PoseSampler.Blend(a, b, 1f, upper, result);
        Near(AboutZ(45), result.Local[1].Rotation);

        // In place, into a.
        PoseSampler.Blend(a, b, 0.5f, null);
        Near(new Vector3(0.375f, 0.05f, 0), a.Local[0].Position);
    }

    [Fact]
    public void ToModelSpaceGivesTheExpectedChain()
    {
        var set = LoadRig();
        var skeleton = set.Skeleton;
        using var pose = new SkeletonPose(skeleton);

        // At rest: the chain stands straight up, and each joint's skinning matrix is the identity.
        PoseSampler.ToModelSpace(skeleton, pose);
        Near(Vector3.Zero, pose.ModelSpace[0].Translation);
        Near(new Vector3(0, 1, 0), pose.ModelSpace[1].Translation);
        Near(new Vector3(0, 2, 0), pose.ModelSpace[2].Translation);
        for (int j = 0; j < 3; j++)
            Assert.True(IsIdentity(skeleton.InverseBind[j] * pose.ModelSpace[j]), $"joint {j}'s skinning matrix at rest is not the identity");

        // Walk at 0.5 s: the root slid to x = 0.75, mid turned 90° about +Z, so the tip (one up from mid)
        // points along −X: (0.75 − 1, 1, 0).
        PoseSampler.Sample(set.FindClip("walk")!, 0.5f, loop: true, pose);
        PoseSampler.ToModelSpace(skeleton, pose);
        Near(new Vector3(0.75f, 0, 0), pose.ModelSpace[0].Translation);
        Near(new Vector3(0.75f, 1, 0), pose.ModelSpace[1].Translation);
        Near(new Vector3(-0.25f, 1, 0), pose.ModelSpace[2].Translation);
        // The tip's own +Y axis follows mid's turn.
        Near(new Vector3(-1, 0, 0), Vector3.TransformNormal(Vector3.UnitY, pose.ModelSpace[2]));

        // A stepped scale on the tip scales only the tip's frame, not where it sits.
        PoseSampler.Sample(set.FindClip("idle")!, 1f, loop: false, pose);
        PoseSampler.ToModelSpace(skeleton, pose);
        Near(new Vector3(0, 2.1f, 0), pose.ModelSpace[2].Translation);
        Near(new Vector3(0, 2, 0), Vector3.TransformNormal(Vector3.UnitY, pose.ModelSpace[2]));
    }

    private static bool IsIdentity(Matrix4x4 m)
    {
        var d = m - Matrix4x4.Identity;
        return MathF.Abs(d.M11) + MathF.Abs(d.M12) + MathF.Abs(d.M13) + MathF.Abs(d.M14)
             + MathF.Abs(d.M21) + MathF.Abs(d.M22) + MathF.Abs(d.M23) + MathF.Abs(d.M24)
             + MathF.Abs(d.M31) + MathF.Abs(d.M32) + MathF.Abs(d.M33) + MathF.Abs(d.M34)
             + MathF.Abs(d.M41) + MathF.Abs(d.M42) + MathF.Abs(d.M43) + MathF.Abs(d.M44) < 1e-4f;
    }

    [Fact]
    public void TheReaderCachesByPathAndTryGetNeverLoads()
    {
        var fixture = new MountFixture();
        fixture.Mount("game", "g");
        string file = SkinnedModelBuilder.Write(Path.Combine(fixture.Dir("game"), "models", "cached.glb"));
        var reader = new GltfAnimationReader(fixture.Vfs);
        var path = AssetPath.Intern("models/cached.glb");

        // Not asked for yet: TryGet does not read it.
        Assert.False(reader.TryGet(path, out _));
        Assert.Equal(0, reader.Count);

        var first = reader.Load(path);
        Assert.NotNull(first);
        Assert.True(reader.TryGet(path, out var cached));
        Assert.Same(first, cached);

        // The file going away does not matter to what was read; Forget makes the next Load read again.
        File.Delete(file);
        Assert.Same(first, reader.Load(path));
        Assert.True(reader.Forget(path));
        Assert.Null(reader.Load(path));
        Assert.False(reader.TryGet(path, out _));
    }

    [Fact]
    public void ABadOrMissingFileLogsAWarningRatherThanCrashing()
    {
        var fixture = new MountFixture();
        fixture.Mount("game", "g");
        string garbage = "garbage.glb";
        fixture.Write("game", "models/" + garbage, "this is not a glb");
        string unskinned = "unskinned" + Guid.NewGuid().ToString("N") + ".glb";
        SkinnedModelBuilder.Write(Path.Combine(fixture.Dir("game"), "models", unskinned), withSkin: false);
        string missing = "missing" + Guid.NewGuid().ToString("N") + ".glb";
        var reader = new GltfAnimationReader(fixture.Vfs);

        using var capture = new CaptureSink();
        Assert.Null(reader.Load(AssetPath.Intern("models/" + garbage)));
        Assert.Null(reader.Load(AssetPath.Intern("models/" + missing)));
        Assert.Null(reader.Load(AssetPath.Intern("models/" + unskinned)));
        Assert.Null(reader.Load(AssetPath.None));
        // A failure is remembered: asking again neither reads nor warns again.
        Assert.Null(reader.Load(AssetPath.Intern("models/" + missing)));

        var entries = capture.Entries;
        Assert.Contains(entries, e => e.Level == LogLevel.Warn && e.Category == LogCat.Animation && e.Message.Contains(garbage) && e.Message.Contains("not a .glb"));
        Assert.Single(entries, e => e.Level == LogLevel.Warn && e.Category == LogCat.Animation && e.Message.Contains(missing) && e.Message.Contains("not in any mount"));
        Assert.Contains(entries, e => e.Level == LogLevel.Warn && e.Category == LogCat.Animation && e.Message.Contains(unskinned) && e.Message.Contains("no skin"));

        // And the stream form, without the VFS.
        Assert.Null(GltfAnimationReader.Read(new MemoryStream(new byte[] { 1, 2, 3 }), "bytes"));
    }

    [Fact]
    public void ASkeletonWhoseParentComesAfterItsChildIsRefused()
    {
        var rest = new[] { Pose.Identity, Pose.Identity };
        var ibm = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
        Assert.Throws<ArgumentException>(() => new Skeleton(new[] { "child", "parent" }, new[] { 1, -1 }, rest, ibm));
        Assert.Throws<ArgumentException>(() => new Skeleton(new[] { "a" }, new[] { -1, -1 }, rest, ibm));
        var ok = new Skeleton(new[] { "parent", "child" }, new[] { -1, 0 }, rest, ibm);
        Assert.True(ok.IsInBranch(1, 0));
        Assert.False(ok.IsInBranch(0, 1));
    }

    [Fact]
    public void AClipKeepsItsEventsSortedByTime()
    {
        var clip = new AnimationClip("swing", 1, 1f);
        clip.AddEvent(0.5f, "hit");
        clip.AddEvent(0.1f, "whoosh");
        clip.AddEvent(0.5f, "grunt");
        Assert.Equal(new[] { "whoosh", "hit", "grunt" }, clip.Events.Select(e => e.Name).ToArray());
        Assert.Throws<ArgumentException>(() => clip.SetTranslation(0, AnimationInterpolation.Linear, new[] { 0f, 1f }, new[] { Vector3.Zero }));
        Assert.Throws<ArgumentException>(() => clip.SetTranslation(0, AnimationInterpolation.Linear, new[] { 1f, 0f }, new[] { Vector3.Zero, Vector3.One }));
    }
}

// Sampling, blending and model space every tick allocate nothing (02 §4.6). Measured per thread, alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class SkeletalAnimationAllocationTests
{
    public SkeletalAnimationAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void SamplingAllocatesNothing()
    {
        var fixture = new MountFixture();
        fixture.Mount("game", "g");
        SkinnedModelBuilder.Write(Path.Combine(fixture.Dir("game"), "models", "rig.glb"));
        var reader = new GltfAnimationReader(fixture.Vfs);
        var path = AssetPath.Intern("models/rig.glb");
        var set = reader.Load(path)!;
        var skeleton = set.Skeleton;
        var idle = set.FindClip("idle")!;
        var walk = set.FindClip("walk")!;
        using var a = new SkeletonPose(skeleton);
        using var b = new SkeletonPose(skeleton);
        var mask = new JointMask(skeleton);
        mask.SetBranch("mid", 1f);

        float time = 0;
        void Step()
        {
            time += 1f / 60f;
            reader.TryGet(path, out _);
            PoseSampler.Sample(idle, time, loop: true, a);
            PoseSampler.Sample(walk, time * 1.3f, loop: true, b);
            PoseSampler.Blend(a, b, 0.4f, null, b);
            PoseSampler.Blend(a, b, 0.7f, mask);
            PoseSampler.ToModelSpace(skeleton, a);
        }
        for (int i = 0; i < 10; i++) Step();   // warm up
        AllocationProbe.AssertNone(10_000, Step);
        Assert.True(float.IsFinite(a.ModelSpace[2].M42));
    }
}
