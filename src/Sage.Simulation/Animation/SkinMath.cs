#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// The maths of GPU skinning (issue #117, docs/design/07 §3.5, 12 §3), headless so it can be tested and
// so the simulation (hit detection, attachments) can skin a point the way the shader does.
//
// Row vectors throughout, as System.Numerics and MonoGame both use: a vertex in bind space is
// `v * palette[j]`, and `palette[j] = inverseBind[j] * modelJoint[j]` — first out of the bind pose into
// joint j's space, then out to where joint j is now in model space. A skeleton standing in its bind
// pose therefore has an identity palette.
//
// The plain spans keep this independent of how a skeleton or a pose is stored (#116's `Skeleton` and
// pose arrays feed it as spans).
[Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // skeletal animation (#115)
public static class SkinMath
{
    // The most joints one draw can be skinned by. The shaders are vs_3_0, and 64 4x3 matrices are 192
    // of its 256 constant registers; the rest go to the world and view matrices (07 §3.5). A skin with
    // more is drawn with its first 64 (and a warning): split the mesh by bones to go past it.
    public const int MaxBones = 64;

    // How many influences one vertex has: JOINTS_0 / WEIGHTS_0 hold four.
    public const int Influences = 4;

    // palette[i] = inverseBind[i] * modelJoints[i], for every joint. `inverseBind` and `palette` need at
    // least as many entries as `modelJoints`.
    public static void Palette(ReadOnlySpan<Matrix4x4> modelJoints, ReadOnlySpan<Matrix4x4> inverseBind, Span<Matrix4x4> palette)
    {
        int n = modelJoints.Length;
        if (inverseBind.Length < n) throw new ArgumentException($"{n} joints need {n} inverse bind matrices, got {inverseBind.Length}", nameof(inverseBind));
        if (palette.Length < n) throw new ArgumentException($"{n} joints need a palette of {n}, got {palette.Length}", nameof(palette));
        for (int i = 0; i < n; i++) palette[i] = inverseBind[i] * modelJoints[i];
    }

    // The blended skin matrix of one vertex: sum of weights[k] * palette[joints[k]], the shader's
    // `SkinMatrix`. Weights are used as given (the loader normalises them).
    public static Matrix4x4 Blend(ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<int> joints, ReadOnlySpan<float> weights)
    {
        if (joints.Length != weights.Length) throw new ArgumentException("one weight per joint");
        var m = new Matrix4x4();
        for (int k = 0; k < joints.Length; k++)
        {
            if (weights[k] == 0f) continue;
            m += palette[joints[k]] * weights[k];
        }
        return m;
    }

    // A bind-space position skinned by up to four joints: the shader's `SkinPosition`.
    public static Vector3 SkinPosition(Vector3 position, ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<int> joints, ReadOnlySpan<float> weights) =>
        Vector3.Transform(position, Blend(palette, joints, weights));

    // A bind-space normal skinned the same way, and renormalised: the shader's `SkinNormal`. (The
    // rotation part only: exact for rigid joints and uniform scale, which is what skeletons carry.)
    public static Vector3 SkinNormal(Vector3 normal, ReadOnlySpan<Matrix4x4> palette, ReadOnlySpan<int> joints, ReadOnlySpan<float> weights)
    {
        var n = Vector3.TransformNormal(normal, Blend(palette, joints, weights));
        float length = n.Length();
        return length > 1e-12f ? n / length : normal;
    }
}
