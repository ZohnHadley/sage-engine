#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Easing curves (issue #90, phase 4b): how a tween, a camera blend or a UI transition (4c) gets from 0 to
// 1. Pure maths, so it lives beside the entity I/O that drives it and has no state, no allocation and
// no dependency on anything but `MathF`.
//
// **The curves are Robert Penner's**, named the way content names them: the family, then the direction
// (`QuadIn`, `QuadOut`, `QuadInOut`). An enum rather than a vocabulary: the set is closed (a new curve is
// an engine change, not a mod's), an enum field is written by name in records and saves (the
// dialects' JsonStringEnumConverter) and `sage schema` offers its names, which is everything a
// `[VocabularyRef]` would give content without a registry to keep. Text that is not a field — a
// `TweenTo` parameter — goes through `Easing.TryParse`, which also reads the CSS/Penner spellings
// (`easeInOutQuad`, `in_out_quad`, `quad-in-out`).
//
// What every curve promises: `Apply(e, 0) == 0` and `Apply(e, 1) == 1`, and `t` outside [0, 1] is clamped
// first. Everything but `Back*` (overshoots), `Elastic*` (oscillates) and `Bounce*` (bounces) is monotonic
// (test: EveryCurveStartsAtZeroAndEndsAtOne, TheMonotonicCurvesNeverGoBack).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90): 4c's UI transitions are the next consumer
public enum Ease
{
    Linear,
    QuadIn, QuadOut, QuadInOut,
    CubicIn, CubicOut, CubicInOut,
    QuartIn, QuartOut, QuartInOut,
    QuintIn, QuintOut, QuintInOut,
    SineIn, SineOut, SineInOut,
    ExpoIn, ExpoOut, ExpoInOut,
    BackIn, BackOut, BackInOut,
    ElasticIn, ElasticOut, ElasticInOut,
    BounceIn, BounceOut, BounceInOut,
    // Hermite 3t² − 2t³: the shader function, and the gentlest ease that starts and stops at rest.
    SmoothStep,
    // Perlin's 6t⁵ − 15t⁴ + 10t³: SmoothStep with no jolt in its acceleration either.
    SmootherStep,
}

[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90): 4c's UI transitions are the next consumer
public static class Easing
{
    // Penner's overshoot for Back (about 10%), and its InOut scaling.
    private const float BackC1 = 1.70158f;
    private const float BackC2 = BackC1 * 1.525f;
    private const float BackC3 = BackC1 + 1f;
    private const float ElasticC4 = 2f * MathF.PI / 3f;
    private const float ElasticC5 = 2f * MathF.PI / 4.5f;

    // How far along `t` (0..1, clamped) is after the curve. The ends are exact, whatever the curve's
    // floating-point arithmetic would have given, so a tween always lands where it was sent.
    public static float Apply(Ease ease, float t)
    {
        if (!(t > 0f)) return 0f;          // also NaN: a curve given nothing stays at its start
        if (t >= 1f) return 1f;

        switch (ease)
        {
            case Ease.Linear: return t;

            case Ease.QuadIn: return t * t;
            case Ease.QuadOut: return 1f - Pow(1f - t, 2);
            case Ease.QuadInOut: return t < 0.5f ? 2f * t * t : 1f - Pow(-2f * t + 2f, 2) / 2f;

            case Ease.CubicIn: return t * t * t;
            case Ease.CubicOut: return 1f - Pow(1f - t, 3);
            case Ease.CubicInOut: return t < 0.5f ? 4f * t * t * t : 1f - Pow(-2f * t + 2f, 3) / 2f;

            case Ease.QuartIn: return Pow(t, 4);
            case Ease.QuartOut: return 1f - Pow(1f - t, 4);
            case Ease.QuartInOut: return t < 0.5f ? 8f * Pow(t, 4) : 1f - Pow(-2f * t + 2f, 4) / 2f;

            case Ease.QuintIn: return Pow(t, 5);
            case Ease.QuintOut: return 1f - Pow(1f - t, 5);
            case Ease.QuintInOut: return t < 0.5f ? 16f * Pow(t, 5) : 1f - Pow(-2f * t + 2f, 5) / 2f;

            case Ease.SineIn: return 1f - MathF.Cos(t * MathF.PI / 2f);
            case Ease.SineOut: return MathF.Sin(t * MathF.PI / 2f);
            case Ease.SineInOut: return -(MathF.Cos(MathF.PI * t) - 1f) / 2f;

            // Penner's exponentials do not quite reach their ends (2^-10 is not 0); the clamp above and
            // the ones here make them, so `ExpoIn` is continuous at 0 only to within 0.001.
            case Ease.ExpoIn: return MathF.Pow(2f, 10f * t - 10f);
            case Ease.ExpoOut: return 1f - MathF.Pow(2f, -10f * t);
            case Ease.ExpoInOut:
                return t < 0.5f ? MathF.Pow(2f, 20f * t - 10f) / 2f : (2f - MathF.Pow(2f, -20f * t + 10f)) / 2f;

            case Ease.BackIn: return BackC3 * t * t * t - BackC1 * t * t;
            case Ease.BackOut: return 1f + BackC3 * Pow(t - 1f, 3) + BackC1 * Pow(t - 1f, 2);
            case Ease.BackInOut:
                return t < 0.5f
                    ? Pow(2f * t, 2) * ((BackC2 + 1f) * 2f * t - BackC2) / 2f
                    : (Pow(2f * t - 2f, 2) * ((BackC2 + 1f) * (t * 2f - 2f) + BackC2) + 2f) / 2f;

            case Ease.ElasticIn: return -MathF.Pow(2f, 10f * t - 10f) * MathF.Sin((t * 10f - 10.75f) * ElasticC4);
            case Ease.ElasticOut: return MathF.Pow(2f, -10f * t) * MathF.Sin((t * 10f - 0.75f) * ElasticC4) + 1f;
            case Ease.ElasticInOut:
                return t < 0.5f
                    ? -(MathF.Pow(2f, 20f * t - 10f) * MathF.Sin((20f * t - 11.125f) * ElasticC5)) / 2f
                    : MathF.Pow(2f, -20f * t + 10f) * MathF.Sin((20f * t - 11.125f) * ElasticC5) / 2f + 1f;

            case Ease.BounceIn: return 1f - BounceOut(1f - t);
            case Ease.BounceOut: return BounceOut(t);
            case Ease.BounceInOut:
                return t < 0.5f ? (1f - BounceOut(1f - 2f * t)) / 2f : (1f + BounceOut(2f * t - 1f)) / 2f;

            case Ease.SmoothStep: return t * t * (3f - 2f * t);
            case Ease.SmootherStep: return t * t * t * (t * (t * 6f - 15f) + 10f);

            // An enum value from a newer save or a cast: linear, rather than a tween that never moves.
            default: return t;
        }
    }

    // `a` to `b`, `t` of the way along the curve: the one-liner most callers want.
    public static float Lerp(Ease ease, float a, float b, float t) => a + (b - a) * Apply(ease, t);

    // Whether the curve only ever goes forwards: false for the ones that overshoot, wobble or bounce, which
    // a caller that must never pass its target (a door into a wall) should not use.
    public static bool IsMonotonic(Ease ease) => ease switch
    {
        Ease.BackIn or Ease.BackOut or Ease.BackInOut => false,
        Ease.ElasticIn or Ease.ElasticOut or Ease.ElasticInOut => false,
        Ease.BounceIn or Ease.BounceOut or Ease.BounceInOut => false,
        _ => true,
    };

    // A curve by name, as content writes it. Case, `_`, `-` and spaces do not matter, a leading `ease`
    // is dropped, and the direction may come first or last: `QuadInOut`, `quad_in_out`, `easeInOutQuad`,
    // `in-out-quad` and `smoothstep` all parse. Allocation-free: a parameter is read every time an input
    // arrives, and an input may arrive every tick.
    public static bool TryParse(ReadOnlySpan<char> text, out Ease ease)
    {
        ease = Ease.Linear;
        Span<char> buffer = stackalloc char[32];
        int n = 0;
        foreach (char c in text)
        {
            if (c is '_' or '-' or ' ' or '.') continue;
            if (n == buffer.Length) return false;
            buffer[n++] = char.ToLowerInvariant(c);
        }
        ReadOnlySpan<char> name = buffer[..n];
        if (name.StartsWith("ease") && name.Length > 4) name = name[4..];
        if (name.Length == 0) return false;

        switch (name)
        {
            case "linear": ease = Ease.Linear; return true;
            case "smoothstep": ease = Ease.SmoothStep; return true;
            case "smootherstep": ease = Ease.SmootherStep; return true;
        }

        // The direction, before or after the family: `inoutquad` / `quadinout`.
        int direction;          // 0 in, 1 out, 2 inout
        scoped ReadOnlySpan<char> family;
        if (name.StartsWith("inout")) { direction = 2; family = name[5..]; }
        else if (name.EndsWith("inout")) { direction = 2; family = name[..^5]; }
        else if (name.StartsWith("out")) { direction = 1; family = name[3..]; }
        else if (name.EndsWith("out")) { direction = 1; family = name[..^3]; }
        else if (name.StartsWith("in")) { direction = 0; family = name[2..]; }
        else if (name.EndsWith("in")) { direction = 0; family = name[..^2]; }
        else return false;

        Ease first;
        switch (family)
        {
            case "quad": first = Ease.QuadIn; break;
            case "cubic": first = Ease.CubicIn; break;
            case "quart": first = Ease.QuartIn; break;
            case "quint": first = Ease.QuintIn; break;
            case "sine": first = Ease.SineIn; break;
            case "expo": first = Ease.ExpoIn; break;
            case "back": first = Ease.BackIn; break;
            case "elastic": first = Ease.ElasticIn; break;
            case "bounce": first = Ease.BounceIn; break;
            default: return false;
        }
        ease = first + direction;
        return true;
    }

    // Penner's piecewise parabolas: four bounces, each lower than the last.
    private static float BounceOut(float t)
    {
        const float n1 = 7.5625f;
        const float d1 = 2.75f;
        if (t < 1f / d1) return n1 * t * t;
        if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
        if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
        t -= 2.625f / d1;
        return n1 * t * t + 0.984375f;
    }

    // Small integer powers by multiplication: MathF.Pow is slower and no more exact.
    private static float Pow(float x, int n)
    {
        float r = x;
        for (int i = 1; i < n; i++) r *= x;
        return r;
    }
}
