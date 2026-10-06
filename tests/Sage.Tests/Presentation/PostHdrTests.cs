#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// HDR, bloom, anti-aliasing and the depth hook for post-processing (issue #316): the chain's order, the
// bloom chain's sizes, the tonemap and bloom curves post.fx mirrors, depth_haze.fx's linear depth, the
// device fallbacks and the `r_aa` values, all headless (PostChainPlan, PostCurves). The drawing is the
// client's: the smoke run (`r_hdr 1`, `r_bloom 1`, `r_aa ...`, the Sandbox's haze) proves its targets and
// steps, and Windows CI compiles post.fx and depth_haze.fx.
public class PostHdrTests
{
    public PostHdrTests() { _ = TestEnv.UserRoot; }

    private static PostStep[] Plan(in PostOptions options, params bool[] enabled)
    {
        var steps = new PostStep[PostChainPlan.Capacity(enabled.Length)];
        int n = PostChainPlan.Plan(enabled, options, steps);
        return steps.AsSpan(0, n).ToArray();
    }

    private static PostStep Step(PostStepKind kind, PostTarget source, PostTarget destination) => new(-1, source, destination, kind);

    // The fixed order: the bloom chain (down, then back up), the tonemap, the post_effects, FXAA last. The
    // bloom steps write only the bloom targets; the LDR steps ping-pong from the scene and the last writes
    // the screen.
    [Fact]
    public void BloomThenTonemapThenTheEffectsThenFxaa_InThatOrder()
    {
        var b0 = PostChainPlan.BloomTarget(0);
        var b1 = PostChainPlan.BloomTarget(1);
        var b2 = PostChainPlan.BloomTarget(2);
        var steps = Plan(new PostOptions(1f, Hdr: true, BloomLevels: 3, Aa: AntiAliasing.Fxaa), true, false, true);
        Assert.Equal(new[]
        {
            Step(PostStepKind.BloomPrefilter, PostTarget.Scene, b0),
            Step(PostStepKind.BloomDown, b0, b1),
            Step(PostStepKind.BloomDown, b1, b2),
            Step(PostStepKind.BloomUp, b2, b1),
            Step(PostStepKind.BloomUp, b1, b0),
            Step(PostStepKind.Tonemap, PostTarget.Scene, PostTarget.Post0),
            new PostStep(0, PostTarget.Post0, PostTarget.Post1),
            new PostStep(2, PostTarget.Post1, PostTarget.Post0),
            Step(PostStepKind.Fxaa, PostTarget.Post0, PostTarget.Screen),
        }, steps);

        // No step reads what it writes; the tonemap reads the scene (bloom0 beside it) after the bloom
        // came back up into bloom0.
        foreach (var step in steps) Assert.NotEqual(step.Source, step.Destination);
        Assert.Equal("sage:bloom0", PostChainPlan.TargetName(b0));
        Assert.Equal("sage:bloom2", PostChainPlan.TargetName(b2));
        Assert.Equal("sage:depth", PostChainPlan.TargetName(PostTarget.Depth));
        Assert.True(PostChainPlan.IsBloom(b2));
        Assert.False(PostChainPlan.IsBloom(PostTarget.Post1));

        // The most a chain can need fits Capacity.
        var all = new bool[5];
        Array.Fill(all, true);
        var full = Plan(new PostOptions(0.5f, true, PostChainPlan.MaxBloomLevels, AntiAliasing.Fxaa), all);
        Assert.Equal(5 + 2 * PostChainPlan.MaxBloomLevels - 1 + 2, full.Length);
        Assert.Equal(PostTarget.Screen, full[^1].Destination);
    }

    // Each of r_hdr, r_bloom and r_aa turns the chain on by itself, like r_scale, with r_post's effects off.
    [Fact]
    public void HdrBloomAndAntiAliasing_EachTurnTheChainOnAlone()
    {
        Assert.Empty(Plan(new PostOptions(1f), false, false));
        Assert.Equal(new[] { Step(PostStepKind.Tonemap, PostTarget.Scene, PostTarget.Screen) }, Plan(new PostOptions(Hdr: true), false));
        Assert.Equal(new[] { Step(PostStepKind.Fxaa, PostTarget.Scene, PostTarget.Screen) }, Plan(new PostOptions(Aa: AntiAliasing.Fxaa)));

        // MSAA is in how the scene is drawn: the chain is one copy (which reads the resolved scene).
        var msaa = new PostOptions(Aa: AntiAliasing.Msaa4);
        Assert.Equal(new[] { new PostStep(-1, PostTarget.Scene, PostTarget.Screen) }, Plan(msaa, false));
        Assert.Equal(4, msaa.Samples);
        Assert.Equal(0, new PostOptions(Aa: AntiAliasing.Fxaa).Samples);

        // Bloom without HDR still ends in the tonemap step, which adds it and clamps.
        var bloom = Plan(new PostOptions(BloomLevels: 1));
        Assert.Equal(new[]
        {
            Step(PostStepKind.BloomPrefilter, PostTarget.Scene, PostChainPlan.BloomTarget(0)),
            Step(PostStepKind.Tonemap, PostTarget.Scene, PostTarget.Screen),
        }, bloom);

        // The old overload is the same chain as before #316.
        var steps = new PostStep[4];
        Assert.Equal(1, PostChainPlan.Plan(new[] { true }, 1f, steps));
        Assert.Equal(new PostStep(0, PostTarget.Scene, PostTarget.Screen), steps[0]);
    }

    // The bloom's mip chain: level 0 is half the scene, each next half the one before, while both sides
    // stay at least MinBloomSize; a scene too small for one level has no bloom.
    [Fact]
    public void TheBloomChainHalvesFromHalfTheScene_UntilASideIsTooSmall()
    {
        Assert.Equal((960, 540), PostChainPlan.BloomSize(1920, 1080, 0));
        Assert.Equal((480, 270), PostChainPlan.BloomSize(1920, 1080, 1));
        Assert.Equal((30, 16), PostChainPlan.BloomSize(1920, 1080, 5));
        Assert.Equal(PostChainPlan.DefaultBloomLevels, PostChainPlan.BloomLevels(1920, 1080));
        Assert.Equal(8, PostChainPlan.BloomLevels(1920, 1080, 8));      // 7.5x4: the eighth level still fits
        Assert.Equal(8, PostChainPlan.BloomLevels(4096, 4096, 20));     // never past MaxBloomLevels

        Assert.Equal(4, PostChainPlan.BloomLevels(64, 64));             // 32, 16, 8, 4
        Assert.Equal(3, PostChainPlan.BloomLevels(512, 40));            // 20, 10, 5: the short side decides
        Assert.Equal(0, PostChainPlan.BloomLevels(6, 6));               // 3x3 is under MinBloomSize
        Assert.Equal((1, 1), PostChainPlan.BloomSize(2, 2, 3));         // never zero
        Assert.Equal(0, PostChainPlan.BloomLevels(1280, 720, 0));
    }

    // The tonemap: ACES (Narkowicz's fit), what post.fx's `Aces` is. 0 stays black, mid grey lands near
    // 0.27, it rises all the way, and saturates a few stops above white; exposure is in stops.
    [Fact]
    public void TheTonemapCurve_IsMonotonic_AndMapsBlackGreyAndWhite()
    {
        Assert.Equal(0f, PostCurves.Aces(0f));
        Assert.Equal(0f, PostCurves.Aces(-3f));                          // never negative
        Assert.InRange(PostCurves.Aces(0.18f), 0.24f, 0.30f);
        Assert.InRange(PostCurves.Aces(1f), 0.75f, 0.85f);
        Assert.Equal(1f, PostCurves.Aces(16f), 2);
        Assert.True(PostCurves.Aces(1000f) <= 1f);

        float previous = -1f;
        for (float x = 0f; x <= 12f; x += 0.01f)
        {
            float y = PostCurves.Aces(x);
            Assert.True(y >= previous, $"the curve went down at {x}");
            previous = y;
        }

        Assert.Equal(PostCurves.Aces(0.5f), PostCurves.Tonemap(0.25f, 1f), 5);   // +1 stop doubles
        Assert.Equal(PostCurves.Aces(0.5f), PostCurves.Tonemap(1f, -1f), 5);
        Assert.Equal(PostCurves.Aces(0.7f), PostCurves.Tonemap(0.7f, 0f), 5);
    }

    // The bloom's soft threshold (post.fx's `BloomWeight`): nothing well below the threshold, what is above
    // it past the knee, a smooth ramp between, and a brighter pixel never gives less bloom.
    [Fact]
    public void TheBloomThreshold_IsSoftAroundItsKnee()
    {
        const float threshold = 1f, knee = 0.5f;
        float Bloom(float b) => b * PostCurves.BloomWeight(b, threshold, knee);

        Assert.Equal(0f, Bloom(0.4f), 5);                               // under threshold - knee
        Assert.Equal(0f, Bloom(0.5f), 4);
        Assert.Equal(2f, Bloom(3f), 4);                                 // past the knee: b - threshold
        Assert.InRange(Bloom(1f), 0.1f, 0.15f);                         // at the threshold: knee / 4
        Assert.Equal(0.5f, Bloom(1.5f), 3);                             // the ramp meets the line at threshold + knee

        float previous = 0f;
        for (float b = 0f; b <= 4f; b += 0.01f)
        {
            float y = Bloom(b);
            Assert.True(y >= previous - 1e-5f, $"the bloom went down at {b}");
            Assert.True(y - previous < 0.02f, $"the bloom jumped at {b}");
            previous = y;
        }
        Assert.Equal(0f, PostCurves.BloomWeight(0f, threshold, knee));   // black is no division by zero
    }

    // depth_haze.fx's `LinearDepth` undoes the engine's projection: a point projected with the camera's
    // matrix comes back at its distance, for a perspective and an orthographic view.
    [Fact]
    public void TheDepthHookUnprojectsToMetres()
    {
        const float near = 0.1f, far = 500f;
        var perspective = Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 16f / 9f, near, far);
        var orthographic = Matrix4x4.CreateOrthographic(20f, 10f, near, far);
        foreach (float metres in new[] { 0.1f, 0.5f, 3f, 25f, 120f, 499f })
        {
            var clip = Vector4.Transform(new Vector4(0.3f, -0.2f, -metres, 1f), perspective);
            Assert.Equal(metres, PostCurves.LinearDepth(clip.Z / clip.W, near, far, orthographic: false), metres * 1e-3f);
            clip = Vector4.Transform(new Vector4(1f, 2f, -metres, 1f), orthographic);
            Assert.Equal(metres, PostCurves.LinearDepth(clip.Z / clip.W, near, far, orthographic: true), 1e-3f);
        }
        Assert.Equal(far, PostCurves.LinearDepth(1f, near, far, false), 0.1f);    // the sky
        Assert.Equal(near, PostCurves.LinearDepth(0f, near, far, false), 1e-5f);
    }

    // A device without half-float targets draws the scene in 8-bit colour, and one with fewer samples uses
    // what it has; each with a warning (logged once by the client). FXAA and bloom need nothing.
    [Fact]
    public void WhatTheDeviceLacks_FallsBackWithAWarning()
    {
        var asked = new PostOptions(1f, Hdr: true, BloomLevels: 4, Aa: AntiAliasing.Msaa8);

        var same = PostChainPlan.Fallback(asked, hdrSupported: true, maxSamples: 8, out string? none);
        Assert.Equal(asked, same);
        Assert.Null(none);

        var noHdr = PostChainPlan.Fallback(asked, hdrSupported: false, maxSamples: 8, out string? warning);
        Assert.False(noHdr.Hdr);
        Assert.Equal(4, noHdr.BloomLevels);
        Assert.Equal(AntiAliasing.Msaa8, noHdr.Aa);
        Assert.Contains("r_hdr", warning);

        var four = PostChainPlan.Fallback(asked, hdrSupported: true, maxSamples: 4, out warning);
        Assert.Equal(AntiAliasing.Msaa4, four.Aa);
        Assert.Contains("r_aa", warning);
        Assert.Contains("4x", warning);

        var neither = PostChainPlan.Fallback(asked, hdrSupported: false, maxSamples: 0, out warning);
        Assert.Equal(AntiAliasing.Off, neither.Aa);
        Assert.False(neither.Hdr);
        Assert.Contains("r_hdr", warning);
        Assert.Contains("no anti-aliasing", warning);

        var fxaa = new PostOptions(Aa: AntiAliasing.Fxaa);
        Assert.Equal(fxaa, PostChainPlan.Fallback(fxaa, false, 0, out warning));
        Assert.Null(warning);

        // After the fallback the chain is still whole: bloom and the tonemap, clamping.
        Assert.Equal(PostStepKind.Tonemap, Plan(noHdr)[^1].Kind);
    }

    // `r_aa` takes off, fxaa, msaa2/4/8 or their numbers, and refuses anything else (CVar<AntiAliasing>,
    // registered as the client registers it).
    [Fact]
    public void TheAntiAliasingCvar_TakesItsNamesAndSampleCounts_AndRefusesTheRest()
    {
        var cvars = new CVarRegistry();
        var aa = cvars.Register("r_aa", AntiAliasing.Off, CVarFlags.Archive, "");
        Assert.True(aa.TrySet("fxaa", out _));
        Assert.Equal(AntiAliasing.Fxaa, aa.Value);
        Assert.True(aa.TrySet("MSAA4", out _));
        Assert.Equal(AntiAliasing.Msaa4, aa.Value);
        Assert.True(aa.TrySet("8", out _));
        Assert.Equal(AntiAliasing.Msaa8, aa.Value);
        Assert.True(aa.TrySet("0", out _));
        Assert.Equal(AntiAliasing.Off, aa.Value);

        Assert.False(aa.TrySet("3", out _));
        Assert.False(aa.TrySet("16", out _));
        Assert.False(aa.TrySet("smaa", out _));
        Assert.Equal(AntiAliasing.Off, aa.Value);
    }

    private const string Records = """
        [{ "type": "post_effect", "id": "fog", "material": "sage:m", "order": 50, "depth": true, "cvar": "test_fog" },
         { "type": "post_effect", "id": "grade", "material": "sage:m", "order": 100 }]
        """;

    // An effect that says `"depth": true` makes the frame draw the scene's depth, while it is on.
    [Fact]
    public void AnEffectThatReadsDepth_MakesTheFrameDrawIt()
    {
        using var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<PostEffectRecord>())
            .File("data/post.json", Records)
            .Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        var ordered = PostChainPlan.Ordered(app.Records);
        var reads = ordered.Select(e => e.Effect.Depth).ToArray();
        Assert.Equal(new[] { true, false }, reads);

        Assert.True(PostChainPlan.NeedsDepth(new[] { true, true }, reads));
        Assert.False(PostChainPlan.NeedsDepth(new[] { false, true }, reads));   // the fog switched off
        Assert.False(PostChainPlan.NeedsDepth(ReadOnlySpan<bool>.Empty, reads));
    }

    // The engine's own steps have materials in post.json that use post.fx's techniques and carry every
    // param post.fx has (GL has no .fx defaults); the depth sample has its own effect.
    [Fact]
    public void TheEnginesStepMaterialsLoad()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent()
            .OnRegistered(a => { a.Records.Register<PostEffectRecord>(); a.Records.Register<MaterialRecord>(); })
            .Boot();
        Assert.Equal(0, app.Records.ErrorCount);

        foreach (var (id, technique) in new[] { ("post_bloom_prefilter", "BloomPrefilter"), ("post_bloom_down", "BloomDown"),
                                                ("post_bloom_up", "BloomUp"), ("post_tonemap", "Tonemap"), ("post_fxaa", "Fxaa") })
        {
            var material = app.Records.Get<MaterialRecord>(new RecordId("sage", id));
            Assert.Equal("shaders/post.mgfxo", material.Effect.ToString());
            Assert.Equal(technique, material.Technique);
            Assert.False(material.DepthTest);
            foreach (var param in new[] { "Exposure", "Contrast", "VignetteStrength", "BloomThreshold", "BloomKnee",
                                          "BloomIntensity", "HdrExposure", "FxaaSpan" })
                Assert.True(material.Params.ContainsKey(param), $"sage:{id} has no {param}");
        }
        Assert.Equal(SamplerFilter.Linear, app.Records.Get<MaterialRecord>(new RecordId("sage", "post_fxaa")).Sampler.Filter);

        var haze = app.Records.Get<MaterialRecord>(new RecordId("sage", "post_depth_haze"));
        Assert.Equal("shaders/depth_haze.mgfxo", haze.Effect.ToString());
        foreach (var param in new[] { "HazeColor", "HazeStart", "HazeDensity", "HazeSky" })
            Assert.True(haze.Params.ContainsKey(param), $"sage:post_depth_haze has no {param}");
        Assert.Equal(SamplerFilter.Point, haze.Sampler.Filter);
    }

    // The depth hook's sample in a game: the Sandbox's haze reads the scene's depth, before the grade.
    [Fact]
    public void TheSandboxsHaze_ReadsTheScenesDepth()
    {
        string sandbox = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
        using var app = HeadlessApp.ForGame(sandbox, new global::Sandbox.SandboxModule()).WithEngineContent()
            .OnRegistered(a => a.Records.Register<PostEffectRecord>())
            .Boot();
        Assert.Equal(0, app.Records.ErrorCount);

        var ordered = PostChainPlan.Ordered(app.Records);
        Assert.Equal(new[] { "sandbox:haze", "sage:grade", "sage:vignette" }, ordered.Select(e => e.Id.ToString()));
        var haze = ordered[0].Effect;
        Assert.True(haze.Depth);
        Assert.Equal("sage:post_depth_haze", haze.Material.ToString());
        Assert.Equal("r_post_haze", haze.Cvar);
        Assert.True(PostChainPlan.NeedsDepth(new[] { true, true, true }, ordered.Select(e => e.Effect.Depth).ToArray()));
    }
}
