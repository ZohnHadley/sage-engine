#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// GPU skinning's headless half (issue #117, docs/design/07 §3.5, 12 §3): the palette maths the
// `Skinned` technique runs (SkinMath mirrors common.fxh's SkinMatrix/SkinPosition/SkinNormal), and how
// extract hands each skinned renderer's palette to every view that sees it (SkinnedExtract.Emit). The
// drawing half needs a GPU and is exercised by the smoke run (`r_testskin`).
public class SkinMathTests
{
    // A two-joint chain: root at the origin, `bend` a metre up. Bind pose = rest pose.
    private static readonly Matrix4x4[] Rest =
    {
        Matrix4x4.Identity,
        Matrix4x4.CreateTranslation(0f, 1f, 0f),
    };

    private static Matrix4x4[] InverseBind()
    {
        var inverse = new Matrix4x4[Rest.Length];
        for (int i = 0; i < Rest.Length; i++) Assert.True(Matrix4x4.Invert(Rest[i], out inverse[i]));
        return inverse;
    }

    private static void Near(Vector3 expected, Vector3 actual) =>
        Assert.True(Vector3.Distance(expected, actual) < 1e-5f, $"expected {expected}, got {actual}");

    [Fact]
    public void TheBindPose_GivesAnIdentityPalette()
    {
        var palette = new Matrix4x4[2];
        SkinMath.Palette(Rest, InverseBind(), palette);

        foreach (var m in palette)
        {
            var d = m - Matrix4x4.Identity;
            float worst = 0f;
            for (int r = 0; r < 4; r++)
                for (int c = 0; c < 4; c++) worst = MathF.Max(worst, MathF.Abs(d[r, c]));
            Assert.True(worst < 1e-6f, $"not identity: {m}");
        }
        Near(new Vector3(0.25f, 1.5f, 0f), SkinMath.SkinPosition(new Vector3(0.25f, 1.5f, 0f), palette, new[] { 1, 0 }, new[] { 1f, 0f }));
    }

    [Fact]
    public void AJointTurned90Degrees_CarriesItsVertices_AroundIt()
    {
        // `bend` turned 90° about Z, where it stands: a vertex half a metre above it goes half a metre
        // to its left (-X), and its normal +X becomes +Y.
        var posed = new[] { Matrix4x4.Identity, Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Rest[1] };
        var palette = new Matrix4x4[2];
        SkinMath.Palette(posed, InverseBind(), palette);

        int[] bend = { 1, 0, 0, 0 };
        float[] only = { 1f, 0f, 0f, 0f };
        Near(new Vector3(-0.5f, 1f, 0f), SkinMath.SkinPosition(new Vector3(0f, 1.5f, 0f), palette, bend, only));
        Near(Vector3.UnitY, SkinMath.SkinNormal(Vector3.UnitX, palette, bend, only));
        // The root's vertices do not move.
        Near(new Vector3(0.2f, 0.3f, 0f), SkinMath.SkinPosition(new Vector3(0.2f, 0.3f, 0f), palette, new[] { 0 }, new[] { 1f }));
    }

    [Fact]
    public void AVertexSharedHalfAndHalf_GoesHalfway()
    {
        // Both joints lifted: the root by 1, `bend` by 3. A vertex weighted evenly between them rises 2.
        var posed = new[] { Matrix4x4.CreateTranslation(0f, 1f, 0f), Matrix4x4.CreateTranslation(0f, 4f, 0f) };
        var palette = new Matrix4x4[2];
        SkinMath.Palette(posed, InverseBind(), palette);

        Near(new Vector3(0.3f, 3f, 0f), SkinMath.SkinPosition(new Vector3(0.3f, 1f, 0f), palette, new[] { 0, 1 }, new[] { 0.5f, 0.5f }));
    }

    [Fact]
    public void Palette_IsInverseBindThenJoint_InRowVectorOrder()
    {
        var joint = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationY(0.7f) * Matrix4x4.CreateTranslation(3f, 0f, -1f);
        var inverseBind = Matrix4x4.CreateTranslation(0f, -1f, 0f);
        var palette = new Matrix4x4[1];

        SkinMath.Palette(new[] { joint }, new[] { inverseBind }, palette);

        Assert.Equal(inverseBind * joint, palette[0]);
    }

    [Fact]
    public void Palette_RefusesSpansThatAreTooShort()
    {
        var two = new Matrix4x4[2];
        Assert.Throws<ArgumentException>(() => SkinMath.Palette(two, new Matrix4x4[1], new Matrix4x4[2]));
        Assert.Throws<ArgumentException>(() => SkinMath.Palette(two, two, new Matrix4x4[1]));
    }
}

public class SkinnedExtractTests
{
    // What the client's SkinnedMeshExtract does with Emit, without MonoGame: `seen[v]` stands for the
    // frustum and hidden-entity checks, the bone list is the snapshot's, and each draw is recorded.
    private struct Views : ISkinnedDraws
    {
        public bool[] Seen;
        public Matrix4x4[] Bones;
        public int BoneCount;
        public (int View, int Start, int Count)[] Drawn;
        public int DrawCount;

        readonly int ISkinnedDraws.Views => Seen.Length;
        public readonly bool Sees(int view) => Seen[view];

        public Span<Matrix4x4> AddBones(int count, out int start)
        {
            start = BoneCount;
            BoneCount += count;
            return Bones.AsSpan(start, count);
        }

        public void Draw(int view, int boneStart, int boneCount) => Drawn[DrawCount++] = (view, boneStart, boneCount);

        public static Views Of(params bool[] seen) => new()
        {
            Seen = seen, Bones = new Matrix4x4[256], Drawn = new (int, int, int)[16],
        };
    }

    private static Matrix4x4[] Chain(int joints, float lift)
    {
        var m = new Matrix4x4[joints];
        for (int i = 0; i < joints; i++) m[i] = Matrix4x4.CreateTranslation(0f, i + lift, 0f);
        return m;
    }

    private static Matrix4x4[] Inverse(Matrix4x4[] joints)
    {
        var m = new Matrix4x4[joints.Length];
        for (int i = 0; i < joints.Length; i++) Matrix4x4.Invert(joints[i], out m[i]);
        return m;
    }

    [Fact]
    public void EachViewThatSeesARenderer_DrawsFromItsOneRange_WrittenOnce()
    {
        // Three views. Renderer A (3 joints) is seen by views 0 and 2, B (2 joints) by view 1 only, and C
        // by none. A's palette is written once and shared; B's follows it; C writes nothing.
        var rest3 = Chain(3, 0f);
        var rest2 = Chain(2, 0f);

        var a = Views.Of(true, false, true);
        int drawnA = SkinnedExtract.Emit(ref a, Chain(3, 2f), Inverse(rest3), "a.glb");

        var b = Views.Of(false, true, false);
        b.Bones = a.Bones;
        b.BoneCount = a.BoneCount;
        int drawnB = SkinnedExtract.Emit(ref b, Chain(2, 5f), Inverse(rest2), "b.glb");

        var c = Views.Of(false, false, false);
        c.Bones = b.Bones;
        c.BoneCount = b.BoneCount;
        int drawnC = SkinnedExtract.Emit(ref c, Chain(3, 1f), Inverse(rest3), "c.glb");

        Assert.Equal((2, 1, 0), (drawnA, drawnB, drawnC));
        Assert.Equal(new[] { (0, 0, 3), (2, 0, 3) }, a.Drawn[..a.DrawCount]);
        Assert.Equal(new[] { (1, 3, 2) }, b.Drawn[..b.DrawCount]);
        Assert.Equal(5, c.BoneCount);   // A's three and B's two: nothing for C

        // The ranges hold each renderer's palette: A lifted by 2, B by 5.
        for (int i = 0; i < 3; i++) Assert.Equal(new Vector3(0f, 2f, 0f), a.Bones[i].Translation);
        for (int i = 3; i < 5; i++) Assert.Equal(new Vector3(0f, 5f, 0f), a.Bones[i].Translation);
    }

    [Fact]
    public void ARendererWithNoSkin_DrawsInEveryViewThatSeesIt_WithNoBones()
    {
        var rigid = Views.Of(true, true);
        Assert.Equal(2, SkinnedExtract.Emit(ref rigid, ReadOnlySpan<Matrix4x4>.Empty, ReadOnlySpan<Matrix4x4>.Empty, "rigid.glb"));
        Assert.Equal(new[] { (0, 0, 0), (1, 0, 0) }, rigid.Drawn[..rigid.DrawCount]);
        Assert.Equal(0, rigid.BoneCount);
    }

    [Fact]
    public void MoreJointsThanADrawTakes_AreCutToMaxBones_WithAWarning()
    {
        using var capture = new CaptureSink();
        string name = TestEnv.Unique("too_many_bones.glb");   // the warning is once per name per process
        var rest = Chain(SkinMath.MaxBones + 6, 0f);
        var views = Views.Of(true);
        views.Bones = new Matrix4x4[128];

        SkinnedExtract.Emit(ref views, rest, Inverse(rest), name);

        Assert.Equal((0, 0, SkinMath.MaxBones), views.Drawn[0]);
        Assert.Equal(SkinMath.MaxBones, views.BoneCount);
        Assert.Contains(capture.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains(name) && e.Message.Contains("70 joints"));
    }
}

[Collection(MeasurementsCollection.Name)]
public class SkinnedExtractAllocationTests
{
    private struct Draws : ISkinnedDraws
    {
        public Matrix4x4[] Bones;
        public int BoneCount, DrawCount;
        public readonly int Views => 3;
        public readonly bool Sees(int view) => view != 1;
        public Span<Matrix4x4> AddBones(int count, out int start)
        {
            start = BoneCount;
            BoneCount += count;
            return Bones.AsSpan(start, count);
        }
        public void Draw(int view, int boneStart, int boneCount) => DrawCount++;
    }

    [Fact]
    public void ExtractingSkinnedItems_AllocatesNothing()
    {
        // A frame of 100 skinned renderers of 32 joints each, in three views: the bone list is cleared
        // and refilled every frame, as RenderSnapshot.Bones is.
        var joints = new Matrix4x4[32];
        var inverseBind = new Matrix4x4[32];
        for (int i = 0; i < 32; i++)
        {
            joints[i] = Matrix4x4.CreateRotationZ(0.1f * i) * Matrix4x4.CreateTranslation(0f, i, 0f);
            Matrix4x4.Invert(Matrix4x4.CreateTranslation(0f, i, 0f), out inverseBind[i]);
        }
        var draws = new Draws { Bones = new Matrix4x4[100 * 32] };

        void Frame()
        {
            draws.BoneCount = draws.DrawCount = 0;
            for (int r = 0; r < 100; r++) SkinnedExtract.Emit(ref draws, joints, inverseBind, "frame.glb");
        }

        Frame();   // warm up
        AllocationProbe.AssertNone(200, Frame);
        Assert.Equal(200, draws.DrawCount);
        Assert.Equal(3200, draws.BoneCount);
    }
}
