#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// How distance fog thickens (issue 4h-5, docs/design/06 §3.9).
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public enum FogMode
{
    Linear,   // none at the start, all of it at the end, a straight line between (the look before 4h-5)
    Exp2,     // exp²: a soft onset past the start that closes in fast, the Morrowind haze
}

// Distance fog (issue 4h-5, docs/design/06 "As built (sky and fog)"). **The maths is here and the drawing
// is not**, like the sky's and the shadows': how much fog covers a point at a distance, and past which
// distance nothing shows through at all, are questions a headless test asks. `common.fxh`'s `FogFactor`
// is this function in HLSL, fed by `ShaderParams`.
//
//   Linear  f = saturate((d - start) / (end - start)): exactly 1 from `end` on.
//   Exp2    f = 1 - exp(-(density · max(d - start, 0))²). `density` is per metre; 0 means "complete at
//           `end`": the density that leaves `Invisible` of the surface at `end`, so `fogStart`/`fogEnd`
//           mean the same in both modes and a sky can switch modes without retuning.
//
// **Culling:** past `CullDistance` a fogged surface is its fog colour to within half an 8-bit step, so an
// opaque thing wholly beyond it is not drawn (test: FogCullsWhatItFullyHides).
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class FogMath
{
    // What is left of a surface's own colour where exp² fog counts as complete: under half of the
    // smallest step an 8-bit target can show, so the difference rounds away.
    public const float Invisible = 1f / 512f;

    // √(ln 512): the exp² exponent that leaves `Invisible`.
    private static readonly float CompleteExponent = MathF.Sqrt(-MathF.Log(Invisible));

    // The exp² density this fog uses: the given one, or (0) the one that completes it at `end`.
    public static float Density(float start, float end, float density) =>
        density > 0f ? density : CompleteExponent / MathF.Max(end - start, 0.001f);

    // How much fog covers a point `distance` metres from the camera: 0 none, 1 all.
    public static float Factor(FogMode mode, float distance, float start, float end, float density = 0f)
    {
        float x = MathF.Max(distance - start, 0f);
        if (mode == FogMode.Exp2)
        {
            float k = Density(start, end, density) * x;
            return Math.Clamp(1f - MathF.Exp(-k * k), 0f, 1f);
        }
        return Math.Clamp(x / MathF.Max(end - start, 0.001f), 0f, 1f);
    }

    // The distance past which fog hides a surface completely (Factor >= 1 - Invisible).
    public static float CullDistance(FogMode mode, float start, float end, float density = 0f)
    {
        if (mode == FogMode.Exp2) return start + CompleteExponent / Density(start, end, density);
        return MathF.Max(end, start + 0.001f);
    }

    // Whether fog hides a sphere (camera-relative centre, radius) wholly: its nearest point is past the
    // cull distance. Fog is measured per pixel from the camera, so no point of it shows.
    public static bool Hides(float cullDistance, Vector3 center, float radius) =>
        center.Length() - radius >= cullDistance;

    // What the shaders are given in `FogParams.w`: 0 for linear, else exp²'s density scaled for `exp2()`
    // (e^-y = 2^(-y·log2 e)), so the mode costs no constant of its own.
    public static float ShaderParam(FogMode mode, float start, float end, float density) =>
        mode == FogMode.Exp2 ? Density(start, end, density) * MathF.Sqrt(1f / MathF.Log(2f)) : 0f;
}
