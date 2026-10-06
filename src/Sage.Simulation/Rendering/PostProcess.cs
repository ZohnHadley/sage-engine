#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Post-processing (issue 4h-6, REDESIGN §4.7, docs/design/06 "As built (post-processing)"): a chain of
// full-screen effects between the screen's views and the UI.
//
//   { "type": "post_effect", "id": "grade", "material": "sage:post_grade", "order": 100, "cvar": "r_post_grade" }
//
// An effect is a material (an effect file, a technique and its params, 07 §3.3) drawn over the whole
// picture, reading the step before it. Effects run by `order`, lowest first (ties by id); each can be
// switched by a cvar, and a mod removes one of the engine's like any record (`"patch": true,
// "disabled": true`). `r_post 0` turns the whole chain off.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Record("post_effect", Plugin = "sage.client")]
public sealed class PostEffectRecord
{
    [RecordRef("material")]
    [Property(Tooltip = "The material drawn over the whole picture: its effect reads the picture as Source")]
    public RecordId Material;

    [Property(Tooltip = "Where in the chain it runs: lower first; equal orders run by id")]
    public int Order;

    [Property(Tooltip = "A cvar that switches it: a bool, or a number where 0 is off. Empty: on whenever r_post is")]
    public string Cvar = "";

    // Issue #316: the engine draws the scene's depth into `sage:depth` before the chain and hands it to
    // this effect as SceneDepth (point-sampled, z/w of the screen view that drew each pixel; 1 is the
    // sky) with DepthParams (near, far, 1 when the main view is orthographic). Depth fog, SSAO and the
    // like are written this way. Without it SceneDepth is a blank texture.
    [Property(Tooltip = "Reads the scene's depth: the engine hands it SceneDepth (z/w, 1 is the sky) and DepthParams (near, far, orthographic)")]
    public bool Depth;
}

// `r_aa` (issue #316): anti-aliasing. FXAA is a post step, the chain's last, on the LDR picture; MSAA
// draws `sage:scene` with that many samples (resolved when the chain reads it). Either turns the chain
// on, like `r_scale`; a value that is not one of these is refused by the cvar.
internal enum AntiAliasing
{
    Off = 0,
    Fxaa = 1,
    Msaa2 = 2,
    Msaa4 = 4,
    Msaa8 = 8,
}

// What a step of the chain draws (issue #316): a post_effect's material (or, with Effect -1, a plain
// copy), or one of the engine's own steps, each with its own material in post.json.
internal enum PostStepKind : byte
{
    Effect,           // a post_effect (Effect >= 0), or a plain copy (Effect -1)
    BloomPrefilter,   // the scene's bright parts into `sage:bloom0`, at half size (soft threshold)
    BloomDown,        // one level of the bloom chain into the next, half its size
    BloomUp,          // a level back up into the one above it, added to what that holds
    Tonemap,          // the HDR scene (plus the bloom) into an LDR picture
    Fxaa,             // r_aa fxaa: the last step, on the LDR picture
    Water,            // the frame's water surfaces and the underwater view (issue #411), first of the LDR steps
}

// What the frame asks of the chain beyond its post_effects (issue #316): the render scale, an HDR scene
// with a tonemap, bloom with how many levels the scene's size allows (PostChainPlan.BloomLevels), and
// anti-aliasing; and (issue #411) water to draw: a surface in reach, or the camera under one.
internal readonly record struct PostOptions(float Scale = 1f, bool Hdr = false, int BloomLevels = 0,
                                            AntiAliasing Aa = AntiAliasing.Off, bool Water = false)
{
    public bool Bloom => BloomLevels > 0;
    public bool Msaa => Aa >= AntiAliasing.Msaa2;
    public int Samples => Msaa ? (int)Aa : 0;
}

// Where a step of the chain reads from or writes to.
internal enum PostTarget
{
    Screen = -1,   // the back buffer: the last step writes here
    Scene = 0,     // `sage:scene`, what the screen's views drew into (colour and depth)
    Post0 = 1,     // `sage:post0` and `sage:post1`, the ping-pong pair between effects
    Post1 = 2,
    Depth = 3,     // `sage:depth`, the scene's depth for effects that read it (issue #316); never a step's target
    Bloom0 = 8,    // `sage:bloom0` .. `sage:bloom7`, the bloom chain, each half the one before (issue #316)
}

// One full-screen draw: `Effect` (an index into the ordered effects; -1 for a plain copy, the render
// scale's upscale when no effect is on, and for the engine's own steps) reads `Source` and writes
// `Destination`. `Kind` says which (issue #316).
internal readonly record struct PostStep(int Effect, PostTarget Source, PostTarget Destination,
                                         PostStepKind Kind = PostStepKind.Effect);

// The planning half of post-processing: pure and headless, like RenderViewPlan, so the chain a frame
// draws is something a test can check. The client's renderer draws what it says.
internal static class PostChainPlan
{
    public const string SceneTarget = "sage:scene";
    public const string Post0Target = "sage:post0";
    public const string Post1Target = "sage:post1";
    public const string DepthTarget = "sage:depth";

    // The bloom chain (issue #316): at most this many levels, the first half the scene's size and each
    // next half the one before, stopping before a side would drop below MinBloomSize pixels.
    public const int MaxBloomLevels = 8;
    public const int DefaultBloomLevels = 6;
    public const int MinBloomSize = 4;

    // The smallest render scale: below a quarter the picture is not a picture.
    public const float MinScale = 0.25f;

    public static string TargetName(PostTarget target) => target switch
    {
        PostTarget.Scene => SceneTarget,
        PostTarget.Post0 => Post0Target,
        PostTarget.Post1 => Post1Target,
        PostTarget.Depth => DepthTarget,
        >= PostTarget.Bloom0 and < PostTarget.Bloom0 + MaxBloomLevels => BloomTargets[target - PostTarget.Bloom0],
        _ => throw new ArgumentOutOfRangeException(nameof(target), "The screen is not a render target."),
    };

    private static readonly string[] BloomTargets =
        { "sage:bloom0", "sage:bloom1", "sage:bloom2", "sage:bloom3", "sage:bloom4", "sage:bloom5", "sage:bloom6", "sage:bloom7" };

    public static PostTarget BloomTarget(int level) =>
        level >= 0 && level < MaxBloomLevels ? PostTarget.Bloom0 + level : throw new ArgumentOutOfRangeException(nameof(level));

    public static bool IsBloom(PostTarget target) => target >= PostTarget.Bloom0 && target < PostTarget.Bloom0 + MaxBloomLevels;

    // Whether a render scale draws the scene smaller than the screen (1, or anything above it, does not).
    public static bool IsScaled(float scale) => scale < 0.999f;

    // The size the scene (and the ping-pong pair) is drawn at: the screen's times the scale, at least
    // one pixel. The last step writes the screen at its own size, and the UI draws there after it.
    public static (int Width, int Height) ScaledSize(int width, int height, float scale)
    {
        if (!IsScaled(scale)) return (Math.Max(1, width), Math.Max(1, height));
        scale = Math.Max(scale, MinScale);
        return (Math.Max(1, (int)MathF.Round(width * scale)), Math.Max(1, (int)MathF.Round(height * scale)));
    }

    // The frame's chain, written to `steps` (one per effect at most, or one): the enabled effects in
    // order, the first reading the scene, each writing the ping-pong target the one before did not, and
    // the last writing the screen. Returns how many steps there are; 0 means the chain is off, and the
    // screen's views draw straight into the back buffer as they always have. With no effect on but a
    // render scale below 1 it is one copy, the scene stretched over the screen.
    public static int Plan(ReadOnlySpan<bool> enabled, float scale, Span<PostStep> steps) =>
        Plan(enabled, new PostOptions(scale), steps);

    // The same with HDR, bloom and anti-aliasing (issue #316). The order is fixed:
    //
    //   bloom     the scene's bright parts into bloom0 (half size), down the chain a level a step, then
    //             back up it, each level added into the one above; these write only the bloom targets
    //   tonemap   with HDR or bloom: the scene plus bloom0 into an LDR picture (HDR: the tonemap curve;
    //             LDR: clamped)
    //   water     the frame's water surfaces and the underwater view (issue #411), reading the scene's
    //             depth; before the effects, so a grade or a haze falls on the water too
    //   effects   the enabled post_effects in order, LDR in and out
    //   fxaa      r_aa fxaa, last
    //
    // The LDR steps ping-pong post0/post1 from the scene as before, the last writing the screen. With
    // none of them (only a render scale, or only MSAA) the chain is one plain copy. `steps` needs
    // Capacity(enabled.Length) entries.
    public static int Plan(ReadOnlySpan<bool> enabled, in PostOptions options, Span<PostStep> steps)
    {
        int on = 0;
        for (int i = 0; i < enabled.Length; i++)
            if (enabled[i]) on++;
        int levels = Math.Clamp(options.BloomLevels, 0, MaxBloomLevels);
        bool tonemap = options.Hdr || levels > 0;
        bool fxaa = options.Aa == AntiAliasing.Fxaa;
        int ldr = on + (tonemap ? 1 : 0) + (options.Water ? 1 : 0) + (fxaa ? 1 : 0);

        if (ldr == 0)
        {
            if (!IsScaled(options.Scale) && !options.Msaa) return 0;
            if (steps.Length < 1) throw new ArgumentException("steps needs room for the copy", nameof(steps));
            steps[0] = new PostStep(-1, PostTarget.Scene, PostTarget.Screen);
            return 1;
        }
        int total = ldr + (levels > 0 ? 2 * levels - 1 : 0);
        if (steps.Length < total) throw new ArgumentException($"steps needs {total} entries (Capacity)", nameof(steps));

        int n = 0;
        if (levels > 0)
        {
            steps[n++] = new PostStep(-1, PostTarget.Scene, BloomTarget(0), PostStepKind.BloomPrefilter);
            for (int i = 1; i < levels; i++)
                steps[n++] = new PostStep(-1, BloomTarget(i - 1), BloomTarget(i), PostStepKind.BloomDown);
            for (int i = levels - 1; i > 0; i--)
                steps[n++] = new PostStep(-1, BloomTarget(i), BloomTarget(i - 1), PostStepKind.BloomUp);
        }

        var source = PostTarget.Scene;
        int done = 0;
        void Add(Span<PostStep> steps, ref int n, int effect, PostStepKind kind)
        {
            var destination = ++done == ldr ? PostTarget.Screen
                : source == PostTarget.Post0 ? PostTarget.Post1 : PostTarget.Post0;
            steps[n++] = new PostStep(effect, source, destination, kind);
            source = destination;
        }
        if (tonemap) Add(steps, ref n, -1, PostStepKind.Tonemap);
        if (options.Water) Add(steps, ref n, -1, PostStepKind.Water);
        for (int i = 0; i < enabled.Length; i++)
            if (enabled[i]) Add(steps, ref n, i, PostStepKind.Effect);
        if (fxaa) Add(steps, ref n, -1, PostStepKind.Fxaa);
        return n;
    }

    // How many steps a chain of `effects` post_effects can need at most: every effect, the full bloom
    // chain, the tonemap, the water (issue #411) and FXAA.
    public static int Capacity(int effects) => Math.Max(1, effects) + 2 * MaxBloomLevels + 2;

    // How many bloom levels fit a scene of this size (issue #316): level 0 is half the scene, each next
    // half the one before, while both sides stay at least MinBloomSize; at most `max` (and MaxBloomLevels).
    public static int BloomLevels(int width, int height, int max = DefaultBloomLevels)
    {
        max = Math.Clamp(max, 0, MaxBloomLevels);
        int levels = 0;
        while (levels < max)
        {
            var (w, h) = BloomSize(width, height, levels);
            if (w < MinBloomSize || h < MinBloomSize) break;
            levels++;
        }
        return levels;
    }

    // The size of bloom level `level` for a scene of this size: the scene's halved level + 1 times
    // (rounded down, at least a pixel).
    public static (int Width, int Height) BloomSize(int width, int height, int level) =>
        (Math.Max(1, width >> (level + 1)), Math.Max(1, height >> (level + 1)));

    // Whether a frame's chain draws the scene's depth for an effect (issue #316): an enabled effect that
    // declares `depth`. The chain must also be on (some step). The water step (issue #411) reads it too.
    public static bool NeedsDepth(ReadOnlySpan<bool> enabled, ReadOnlySpan<bool> readsDepth, bool water) =>
        water || NeedsDepth(enabled, readsDepth);

    public static bool NeedsDepth(ReadOnlySpan<bool> enabled, ReadOnlySpan<bool> readsDepth)
    {
        for (int i = 0; i < enabled.Length && i < readsDepth.Length; i++)
            if (enabled[i] && readsDepth[i]) return true;
        return false;
    }

    // What the device can draw of what was asked (issue #316): HDR needs a half-float render target
    // (SurfaceFormat.HdrBlendable); MSAA at most `maxSamples` (0 or 1: none). Returns what to draw and,
    // when it is less than what was asked, the warning to log once. FXAA and bloom need nothing special.
    public static PostOptions Fallback(in PostOptions asked, bool hdrSupported, int maxSamples, out string? warning)
    {
        var options = asked;
        warning = null;
        if (asked.Hdr && !hdrSupported)
        {
            options = options with { Hdr = false };
            warning = "r_hdr: this device cannot draw into half-float (HdrBlendable) render targets; the scene is drawn in 8-bit colour, with no tonemap";
        }
        if (asked.Msaa && asked.Samples > maxSamples)
        {
            var aa = maxSamples >= 8 ? AntiAliasing.Msaa8 : maxSamples >= 4 ? AntiAliasing.Msaa4
                : maxSamples >= 2 ? AntiAliasing.Msaa2 : AntiAliasing.Off;
            options = options with { Aa = aa };
            string msaa = $"r_aa: this device draws at most {Math.Max(1, maxSamples)} sample(s) a pixel; " +
                          (aa == AntiAliasing.Off ? "no anti-aliasing" : $"using {(int)aa}x MSAA") + $" instead of {asked.Samples}x";
            warning = warning == null ? msaa : warning + "; " + msaa;
        }
        return options;
    }

    // The post effects in the order they run: by `order`, then by id. Built when the records load, not
    // per frame.
    public static List<(RecordId Id, PostEffectRecord Effect)> Ordered(RecordStore records)
    {
        var list = new List<(RecordId Id, PostEffectRecord Effect)>();
        if (records.TypeNameOf(typeof(PostEffectRecord)) == null) return list;
        foreach (var id in records.Ids("post_effect"))
            if (records.TryGet(id, out PostEffectRecord effect)) list.Add((id, effect));
        list.Sort((a, b) =>
        {
            int c = a.Effect.Order.CompareTo(b.Effect.Order);
            return c != 0 ? c : string.CompareOrdinal(a.Id.ToString(), b.Id.ToString());
        });
        return list;
    }

    // Whether an effect is on: its cvar (none: always) is not off. A bool cvar is its value; a number is
    // on unless 0; a string unless empty or "0".
    public static bool IsOn(CVar? cvar)
    {
        return cvar switch
        {
            null => true,
            CVar<bool> b => b.Value,
            CVar<int> i => i.Value != 0,
            CVar<float> f => f.Value != 0f,
            _ => cvar.ValueString.Length > 0 && cvar.ValueString != "0",
        };
    }

    // How much of the night tint the grade applies: 0 by day, 1 once the sun is well under the horizon,
    // fading across dusk and dawn with the sun's height. 0 with no sky (a world without a clock's sky
    // keeps the look it had).
    public static float Night(RecordStore records, WorldClock? clock)
    {
        if (SkyRules.Current(records, clock) is not { } sky) return 0f;
        var (_, elevation) = SkyRules.SunAt(sky, clock!.Hour);
        float x = Math.Clamp((elevation + NightFade) / (2f * NightFade), 0f, 1f);   // -NightFade → 1, +NightFade → 0
        return 1f - x * x * (3f - 2f * x);
    }

    // Half the band of sun heights (sin of elevation) over which dusk turns to night.
    private const float NightFade = 0.15f;
}

// The curves post.fx's HDR steps use (issue #316), here so a test can check them: post.fx's
// `Aces`, `BloomWeight` and the exposure are these, line for line.
internal static class PostCurves
{
    // The ACES filmic curve (Narkowicz's fit of the RRT+ODT): 0 stays 0, mid grey (0.18) lands near 0.27,
    // it rises monotonically and saturates at 1 a few stops above white.
    public static float Aces(float x)
    {
        x = MathF.Max(0f, x);
        return Math.Clamp(x * (2.51f * x + 0.03f) / (x * (2.43f * x + 0.59f) + 0.14f), 0f, 1f);
    }

    // The tonemap: exposure in stops (+1 is twice as bright), then the curve.
    public static float Tonemap(float x, float exposure) => Aces(x * MathF.Pow(2f, exposure));

    // depth_haze.fx's `LinearDepth`: the z/w the depth hook hands an effect (Direct3D's 0..1, as the
    // engine's projections make it) back to the distance along the view, with the main view's near and
    // far planes; an orthographic view's depth is linear already.
    public static float LinearDepth(float depth, float near, float far, bool orthographic) =>
        orthographic ? near + depth * (far - near) : near * far / (far - depth * (far - near));

    // How much of a pixel the bloom keeps, by its brightness (the largest channel): none below
    // `threshold - knee`, all of what is above `threshold` past `threshold + knee`, and a quadratic
    // ramp between (a soft knee, so bright edges do not pop). Multiplies the colour.
    public static float BloomWeight(float brightness, float threshold, float knee)
    {
        float soft = Math.Clamp(brightness - threshold + knee, 0f, 2f * knee);
        soft = soft * soft / (4f * knee + 1e-5f);
        return MathF.Max(soft, brightness - threshold) / MathF.Max(brightness, 1e-5f);
    }
}
