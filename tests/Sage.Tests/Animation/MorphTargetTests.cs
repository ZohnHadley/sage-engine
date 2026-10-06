#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Morph targets and facial animation (issue #363, docs/design/12): glTF morph targets and `weights`
// tracks read into the skeleton and its clips, the pose's morph_weights channel sampled, blended and
// masked like the joints, an anim_graph driving it from clips and from params (`morphs`), the mesh's
// deltas and the CPU morph pass the client runs before skinning, and the cooked mesh keeping them.
// Against SkinnedModelBuilder's rig with `withMorphs`: targets "blink" (the tip vertices down by
// BlinkDrop) and "jaw_open" (the root vertices +Z by JawReach), rest weights [0, JawRest]; clip "blink"
// (1 s) takes blink 0 → 1 → 0 with jaw held at JawRest, clip "talk" (1 s) takes jaw 0 → 1.
public class MorphTargetTests
{
    public MorphTargetTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;
    private const float Eps = 1e-4f;
    private const string Blink = SkinnedModelBuilder.Blink, Jaw = SkinnedModelBuilder.JawOpen;

    private static AnimationSet LoadRig()
    {
        var fixture = new MountFixture();
        fixture.Mount("game", "g");
        SkinnedModelBuilder.Write(Path.Combine(fixture.Dir("game"), "models", "head.glb"), withMorphs: true);
        var reader = new GltfAnimationReader(fixture.Vfs);
        return reader.Load(AssetPath.Intern("models/head.glb")) ?? throw new Xunit.Sdk.XunitException("the rig did not load");
    }

    private static MeshGeometry ReadMesh(bool withSkin = true) =>
        MeshGeometry.ReadGlb(new MemoryStream(SkinnedModelBuilder.Build(withSkin: withSkin, withMorphs: true)), "head.glb")
        ?? throw new Xunit.Sdk.XunitException("the mesh did not load");

    [Fact]
    public void TheReaderReadsMorphTargetsByNameWithRestWeightsAndWeightTracks()
    {
        var set = LoadRig();
        var skeleton = set.Skeleton;
        Assert.Equal(2, skeleton.MorphTargetCount);
        Assert.Equal(Blink, skeleton.MorphTargetName(0));
        Assert.Equal(Jaw, skeleton.MorphTargetName(1));
        Assert.Equal(1, skeleton.MorphIndexOf(Jaw));
        Assert.Equal(-1, skeleton.MorphIndexOf("smile"));
        Assert.Equal(new[] { 0f, SkinnedModelBuilder.JawRest }, skeleton.RestMorphWeights.ToArray());

        var blink = set.FindClip(SkinnedModelBuilder.Blink)!;
        var talk = set.FindClip(SkinnedModelBuilder.Talk)!;
        Assert.Equal(2, blink.MorphTargetCount);
        Assert.True(blink.AnimatesMorph(0));
        Assert.True(talk.AnimatesMorph(1));
        Assert.False(set.FindClip(SkinnedModelBuilder.Walk)!.AnimatesMorph(0));
        Assert.Equal(1f, blink.Duration, 4);
        // The morph channel is on the body node, not a joint: no joint is moved by it.
        Assert.False(blink.Animates(0) || blink.Animates(1) || blink.Animates(2));

        // A pose starts at the rest weights.
        using var pose = new SkeletonPose(skeleton);
        Assert.Equal(2, pose.MorphCount);
        Assert.Equal(new[] { 0f, SkinnedModelBuilder.JawRest }, pose.MorphWeights.ToArray());
    }

    [Fact]
    public void SamplingAClipWritesTheMorphWeightsChannelAndBlendingLerpsIt()
    {
        var set = LoadRig();
        using var a = new SkeletonPose(set.Skeleton);
        using var b = new SkeletonPose(set.Skeleton);
        var blink = set.FindClip(SkinnedModelBuilder.Blink)!;
        var talk = set.FindClip(SkinnedModelBuilder.Talk)!;

        PoseSampler.Sample(blink, 0.25f, loop: true, a);
        Assert.Equal(0.5f, a.MorphWeights[0], 4);
        Assert.Equal(SkinnedModelBuilder.JawRest, a.MorphWeights[1], 4);
        PoseSampler.Sample(blink, 0.5f, loop: true, a);
        Assert.Equal(1f, a.MorphWeights[0], 4);
        PoseSampler.Sample(blink, 1.25f, loop: true, a);   // wrapped: 0.25 s again
        Assert.Equal(0.5f, a.MorphWeights[0], 4);

        PoseSampler.Sample(talk, 0.5f, loop: false, b);
        Assert.Equal(0f, b.MorphWeights[0], 4);
        Assert.Equal(0.5f, b.MorphWeights[1], 4);

        // A clip without weight tracks leaves the rest weights.
        using var still = new SkeletonPose(set.Skeleton);
        still.MorphWeights[0] = 0.9f;
        PoseSampler.Sample(set.FindClip(SkinnedModelBuilder.Walk)!, 0.3f, loop: true, still);
        Assert.Equal(new[] { 0f, SkinnedModelBuilder.JawRest }, still.MorphWeights.ToArray());

        // Blend: halfway between blink (0.5, 0.2) and talk (0, 0.5).
        using var mixed = new SkeletonPose(set.Skeleton);
        PoseSampler.Blend(a, b, 0.5f, null, mixed);
        Assert.Equal(0.25f, mixed.MorphWeights[0], 4);
        Assert.Equal(0.35f, mixed.MorphWeights[1], 4);

        // A mask weighs each target by its own entry: only the jaw comes from b.
        var mask = new JointMask(set.Skeleton);
        Assert.True(mask.SetMorph(Jaw, 1f));
        Assert.False(mask.SetMorph("smile", 1f));
        PoseSampler.Blend(a, b, 1f, mask, mixed);
        Assert.Equal(0.5f, mixed.MorphWeights[0], 4);
        Assert.Equal(0.5f, mixed.MorphWeights[1], 4);

        // CopyFrom and ResetToRest carry the channel.
        mixed.CopyFrom(b);
        Assert.Equal(0.5f, mixed.MorphWeights[1], 4);
        mixed.ResetToRest();
        Assert.Equal(SkinnedModelBuilder.JawRest, mixed.MorphWeights[1], 4);
    }

    [Fact]
    public void ASkeletonMadeInCodeCarriesMorphTargets()
    {
        var skeleton = new Skeleton(new[] { "root" }, new[] { -1 }, new[] { Pose.Identity }, new[] { Matrix4x4.Identity },
                                    new[] { "smile", "frown" }, new[] { 0.5f, 0f });
        Assert.Equal(2, skeleton.MorphTargetCount);
        Assert.Equal(0.5f, skeleton.RestMorphWeights[0]);
        Assert.Throws<ArgumentException>(() => new Skeleton(new[] { "root" }, new[] { -1 }, new[] { Pose.Identity }, new[] { Matrix4x4.Identity },
                                                             new[] { "smile" }, new[] { 0f, 1f }));
        var clip = new AnimationClip("grin", 1, 2, 1f);
        clip.SetMorphWeights(0, AnimationInterpolation.Step, new[] { 0f, 0.5f }, new[] { 0f, 1f });
        Assert.Throws<ArgumentOutOfRangeException>(() => clip.SetMorphWeights(2, AnimationInterpolation.Step, new[] { 0f }, new[] { 0f }));
        Assert.Throws<ArgumentException>(() => clip.SetMorphWeights(1, AnimationInterpolation.Linear, new[] { 0f, 1f }, new[] { 0f }));
        using var pose = new SkeletonPose(skeleton);
        PoseSampler.Sample(clip, 0.25f, loop: false, pose);
        Assert.Equal(0f, pose.MorphWeights[0]);
        PoseSampler.Sample(clip, 0.75f, loop: false, pose);
        Assert.Equal(1f, pose.MorphWeights[0]);
        Assert.Equal(0f, pose.MorphWeights[1]);
    }

    // ---- the graph ------------------------------------------------------------------------------------

    // "face": the base talks (jaw from the clip) and a face layer masked to the blink target blinks on a
    // trigger. "lips": lip-sync, the jaw set straight from a param through `morphs`.
    private const string Faces = """
    [
      { "type": "anim_graph", "id": "face", "initial": "talk", "fade": 0,
        "params": { "blinking": { "kind": "Trigger" } },
        "states": { "talk": { "clip": "talk" } },
        "layers": [ { "name": "eyes", "mask": ["blink"], "initial": "open",
                      "states": { "open": {}, "blink": { "clip": "blink", "loop": false } },
                      "transitions": [ { "to": "blink", "on": "blinking" } ] } ] },
      { "type": "anim_graph", "id": "lips", "initial": "stand",
        "params": { "mouth": {} },
        "morphs": { "jaw_open": "mouth" },
        "states": { "stand": { "clip": "walk" } } },
      { "type": "prefab", "id": "talker", "parts": { "animator": { "graph": "face", "model": "models/head.glb" } } },
      { "type": "prefab", "id": "singer", "parts": { "animator": { "graph": "lips", "model": "models/head.glb" } } }
    ]
    """;

    private static HeadlessApp NewGame(string content = Faces)
    {
        var files = new MountFixture();
        files.Write("game", "data/faces.json", content);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "head.glb"), withMorphs: true);
        files.Mount("game", "game");
        return HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).Boot("faces");
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    private static float Expected(AnimationSet set, string clip, float time, bool loop, int target)
    {
        using var pose = new SkeletonPose(set.Skeleton);
        PoseSampler.Sample(set.FindClip(clip)!, time, loop, pose);
        return pose.MorphWeights[target];
    }

    // Acceptance (Done): a blink target animates through an anim_graph clip, and the weights are sampled.
    [Fact]
    public void ABlinkAnimatesThroughAnAnimGraphClipWhileTheBaseTalks()
    {
        using var app = NewGame();
        var world = app.World;
        Assert.Equal(0, app.Records.ErrorCount);
        var talker = world.Spawn(new RecordId("game", "talker"), Vector3.Zero);
        Tick(world, 2);
        var set = app.Engine.Animations.TryGet(AssetPath.Intern("models/head.glb"), out var s) ? s : throw new Xunit.Sdk.XunitException("no rig");

        // Eyes open: the face layer plays nothing, so blink is at rest; the jaw follows the base's talk clip.
        Assert.Equal(0f, Animators.GetMorphWeight(world, talker, Blink));
        float basePhase = world.Get<Animator>(talker).Layers![0].Phase;
        Assert.Equal(Expected(set, SkinnedModelBuilder.Talk, basePhase, true, 1), Animators.GetMorphWeight(world, talker, Jaw)!.Value, 3);
        Assert.Null(Animators.GetMorphWeight(world, talker, "smile"));

        Assert.True(Animators.SetTrigger(world, talker, "blinking"));
        Tick(world, 15);   // a quarter of a second into the blink
        Assert.Equal("blink", Animators.StateOf(world, talker, "eyes"));
        var eyes = world.Get<Animator>(talker).Layers![1];
        float blinkTime = eyes.Phase * 1f;
        float blink = Animators.GetMorphWeight(world, talker, Blink)!.Value;
        Assert.Equal(Expected(set, SkinnedModelBuilder.Blink, blinkTime, false, 0), blink, 3);
        Assert.InRange(blink, 0.45f, 0.55f);
        // The mask names blink only: the jaw is still the base's talk, not the blink clip's held JawRest.
        basePhase = world.Get<Animator>(talker).Layers![0].Phase;
        Assert.Equal(Expected(set, SkinnedModelBuilder.Talk, basePhase, true, 1), Animators.GetMorphWeight(world, talker, Jaw)!.Value, 3);

        Tick(world, 15);   // the eyes shut at half a second
        Assert.InRange(Animators.GetMorphWeight(world, talker, Blink)!.Value, 0.95f, 1f);
        Tick(world, 40);   // and open again; the one-shot holds its last frame
        Assert.Equal(0f, Animators.GetMorphWeight(world, talker, Blink)!.Value, 3);

        // The readers' pose carries the channel (what SkinPoses hands the renderer's morph pass).
        Assert.True(world.Resources.Get<SkeletonPoses>().TryGet(talker, out var shown));
        Assert.Equal(2, shown.MorphCount);
    }

    [Fact]
    public void AParamDrivesAMorphTargetThroughTheGraphsMorphs()
    {
        using var app = NewGame();
        var world = app.World;
        var singer = world.Spawn(new RecordId("game", "singer"), Vector3.Zero);
        Tick(world);
        // Bound to `mouth` (default 0): the param wins over the rest weight.
        Assert.Equal(0f, Animators.GetMorphWeight(world, singer, Jaw));
        Assert.Equal(0f, Animators.GetMorphWeight(world, singer, Blink));

        Assert.True(Animators.SetParam(world, singer, "mouth", 0.6f));
        Tick(world);
        Assert.Equal(0.6f, Animators.GetMorphWeight(world, singer, Jaw)!.Value, 4);

        // Through I/O, as a dialogue line's lip-sync would.
        world.IO().FireInput(singer, Animators.SetParamInput, "mouth 0.25");
        Tick(world, 2);   // delivered after this tick's sampling: the next tick's pose has it
        Assert.Equal(0.25f, Animators.GetMorphWeight(world, singer, Jaw)!.Value, 4);
    }

    [Fact]
    public void MorphsAreCheckedAtLoad()
    {
        using var log = new CaptureSink();
        using var app = NewGame("""
        [
          { "type": "anim_graph", "id": "broken", "initial": "a",
            "params": { "go": { "kind": "Trigger" }, "mouth": {} },
            "morphs": { "jaw_open": "mouht", "blink": "go", "smile": "" },
            "states": { "a": {} } }
        ]
        """);
        string errors = string.Join("\n", log.Entries.Where(e => e.Level >= LogLevel.Error).Select(e => e.Message));
        Assert.Contains("'mouht' is not one of its params", errors);
        Assert.Contains("'go' is a Trigger", errors);
        Assert.Contains("names the param that drives it", errors);
    }

    // ---- the mesh and the CPU morph pass ---------------------------------------------------------------

    [Fact]
    public void ASkinnedMeshKeepsItsDeltasBakedAtTheRestWeights()
    {
        var mesh = ReadMesh();
        Assert.Equal(new[] { Blink, Jaw }, mesh.MorphTargets);
        Assert.Equal(new[] { 0f, SkinnedModelBuilder.JawRest }, mesh.RestMorphWeights);
        var part = Assert.Single(mesh.Parts);
        Assert.NotNull(part.Skinned);
        Assert.Equal(2, part.Morphs!.Length);
        Assert.Equal(new[] { 0, 1 }, part.Morphs.Select(m => m.Target).ToArray());
        Assert.Equal(new Vector3(0, -SkinnedModelBuilder.BlinkDrop, 0), part.Morphs[0].Positions[4]);
        // The vertices are where the rest weights put them: the jaw a fifth of the way open.
        Assert.Equal(SkinnedModelBuilder.JawRest * SkinnedModelBuilder.JawReach, part.Skinned![0].Position.Z, 5);
        Assert.Equal(2f, part.Skinned[4].Position.Y, 5);
    }

    [Fact]
    public void ARigidMeshIsDrawnAtItsRestWeights()
    {
        var part = Assert.Single(ReadMesh(withSkin: false).Parts);
        Assert.Null(part.Morphs);
        Assert.Equal(SkinnedModelBuilder.JawRest * SkinnedModelBuilder.JawReach, part.Rigid![0].Position.Z, 5);
    }

    [Fact]
    public void TheCpuMorphPassMovesTheVerticesByThePosesWeights()
    {
        var mesh = ReadMesh();
        var part = mesh.Parts[0];
        var rest = mesh.RestMorphWeights;
        var output = new SkinnedMeshVertex[part.VertexCount];

        Assert.False(MeshMorphing.Moved(new[] { 0f, SkinnedModelBuilder.JawRest }, rest));
        Assert.True(MeshMorphing.Moved(new[] { 1f, SkinnedModelBuilder.JawRest }, rest));

        // Eyes shut: the tip vertices drop; the jaw stays at rest.
        MeshMorphing.Apply(part, new[] { 1f, SkinnedModelBuilder.JawRest }, rest, output);
        Assert.Equal(2f - SkinnedModelBuilder.BlinkDrop, output[4].Position.Y, 5);
        Assert.Equal(2f - SkinnedModelBuilder.BlinkDrop, output[5].Position.Y, 5);
        Assert.Equal(1f, output[2].Position.Y, 5);
        Assert.Equal(SkinnedModelBuilder.JawRest * SkinnedModelBuilder.JawReach, output[0].Position.Z, 5);
        // Everything else about a vertex (its joints and weights) is kept: the GPU skins it after.
        Assert.Equal(part.Skinned![4].Weights, output[4].Weights);
        Assert.Equal(part.Skinned[4].Joint0, output[4].Joint0);

        // Jaw wide open, eyes open.
        MeshMorphing.Apply(part, new[] { 0f, 1f }, rest, output);
        Assert.Equal(SkinnedModelBuilder.JawReach, output[0].Position.Z, 5);
        Assert.Equal(2f, output[4].Position.Y, 5);

        // A pose's weights reach the mesh by target name, whatever order the skeleton has them in.
        var skeleton = new Skeleton(new[] { "root" }, new[] { -1 }, new[] { Pose.Identity }, new[] { Matrix4x4.Identity },
                                    new[] { Jaw, "smile" }, null);
        var map = new int[2];
        MeshMorphing.Map(mesh.MorphTargets, skeleton, map);
        Assert.Equal(new[] { -1, 0 }, map);
        var weights = new float[2];
        MeshMorphing.Weights(new[] { 0.75f, 1f }, map, rest, weights);
        Assert.Equal(new[] { 0f, 0.75f }, weights);
    }

    [Fact]
    public void ACookedMeshKeepsItsMorphTargets()
    {
        var glb = SkinnedModelBuilder.Build(withMorphs: true);
        var loose = MeshGeometry.ReadGlb(new MemoryStream(glb), "head.glb")!;
        var file = new MemoryStream();
        CookedMesh.Write(file, loose, SourceStamp.Of(glb));
        file.Position = 0;
        var cooked = CookedMesh.Read(file, out _);
        Assert.Equal(loose.MorphTargets, cooked.MorphTargets);
        Assert.Equal(loose.RestMorphWeights, cooked.RestMorphWeights);
        var a = loose.Parts[0].Morphs!;
        var b = cooked.Parts[0].Morphs!;
        Assert.Equal(a.Length, b.Length);
        for (int m = 0; m < a.Length; m++)
        {
            Assert.Equal(a[m].Target, b[m].Target);
            Assert.Equal(a[m].Positions, b[m].Positions);
            Assert.Equal(a[m].Normals == null, b[m].Normals == null);
        }
        Assert.Equal(loose.Parts[0].Skinned, cooked.Parts[0].Skinned);
    }
}

// Morphing allocates nothing per tick: animators blinking and lip-syncing, and the CPU morph pass.
[Xunit.Collection(MeasurementsCollection.Name)]
public class MorphTargetAllocationTests
{
    public MorphTargetAllocationTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void BlinkingAndLipSyncingAllocateNothingPerTick()
    {
        var files = new MountFixture();
        files.Write("game", "data/faces.json", """
        [
          { "type": "anim_graph", "id": "face", "initial": "talk", "fade": 0.1,
            "params": { "blinking": { "kind": "Trigger" }, "mouth": {} },
            "morphs": { "jaw_open": "mouth" },
            "states": { "talk": { "clip": "talk" } },
            "layers": [ { "name": "eyes", "mask": ["blink"], "initial": "open",
                          "states": { "open": {}, "blink": { "clip": "blink", "loop": false, "transitions": [ { "to": "open", "after": 1 } ] } },
                          "transitions": [ { "to": "blink", "on": "blinking" } ] } ] },
          { "type": "prefab", "id": "talker", "parts": { "animator": { "graph": "face", "model": "models/head.glb" } } }
        ]
        """);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "head.glb"), withMorphs: true);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot("faces");
        var world = app.World;
        var heads = new Entity[50];
        for (int i = 0; i < heads.Length; i++) heads[i] = world.Spawn(new RecordId("game", "talker"), new Vector3(i, 0, 0));

        var mesh = MeshGeometry.ReadGlb(new MemoryStream(SkinnedModelBuilder.Build(withMorphs: true)), "head.glb")!;
        var output = new SkinnedMeshVertex[mesh.Parts[0].VertexCount];
        var weights = new float[2];

        int tick = 0;
        void Step()
        {
            tick++;
            for (int i = 0; i < heads.Length; i++)
            {
                Animators.SetParam(world, heads[i], "mouth", 0.5f + 0.5f * MathF.Sin(tick * 0.1f + i));
                if ((tick + i) % 53 == 0) Animators.SetTrigger(world, heads[i], "blinking");
            }
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
            Animators.TryGetPose(world, heads[0], out var pose);
            weights[0] = pose!.MorphWeights[0];
            weights[1] = pose.MorphWeights[1];
            MeshMorphing.Apply(mesh.Parts[0], weights, mesh.RestMorphWeights, output);
        }

        for (int i = 0; i < 120; i++) Step();
        AllocationProbe.AssertNone(300, Step);
        Assert.True(float.IsFinite(output[0].Position.Z));
    }
}
