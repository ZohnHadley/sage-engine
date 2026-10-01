#nullable enable
using System;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Post-processing (issue 4h-6, docs/design/06 "As built (post-processing)"): which full-screen draws a
// frame makes, through which targets, decided headless by PostChainPlan. The drawing is the client's
// and is exercised by the smoke run (`r_post 1`, `r_scale 0.5`); Windows CI compiles post.fx.
public class PostProcessTests
{
    public PostProcessTests() { _ = TestEnv.UserRoot; }

    private static PostStep[] Plan(float scale, params bool[] enabled)
    {
        var steps = new PostStep[Math.Max(1, enabled.Length)];
        int n = PostChainPlan.Plan(enabled, scale, steps);
        return steps.AsSpan(0, n).ToArray();
    }

    // Nothing on and no render scale: no steps, and the screen's views draw straight into the back
    // buffer as they did before post-processing existed.
    [Fact]
    public void NoEffectsMeansNoChain_TheViewsDrawStraightToTheScreen()
    {
        Assert.Empty(Plan(1f));
        Assert.Empty(Plan(1f, false, false, false));
        Assert.Empty(Plan(1.5f, false));                       // above 1 is not a scale down
    }

    [Fact]
    public void DisabledEffectsAreSkipped()
    {
        var steps = Plan(1f, false, true, false, true, false);
        Assert.Equal(new[] { 1, 3 }, steps.Select(s => s.Effect));

        // One effect on: it reads the scene and writes the screen, with no ping-pong at all.
        Assert.Equal(new[] { new PostStep(2, PostTarget.Scene, PostTarget.Screen) }, Plan(1f, false, false, true));
    }

    [Fact]
    public void TheChainPingPongsItsTargets_AndTheLastWritesTheScreen()
    {
        var steps = Plan(1f, true, true, true, true);
        Assert.Equal(new[]
        {
            new PostStep(0, PostTarget.Scene, PostTarget.Post0),
            new PostStep(1, PostTarget.Post0, PostTarget.Post1),
            new PostStep(2, PostTarget.Post1, PostTarget.Post0),
            new PostStep(3, PostTarget.Post0, PostTarget.Screen),
        }, steps);

        // Never reads the target it writes, and each step reads what the one before wrote.
        for (int i = 0; i < steps.Length; i++)
        {
            Assert.NotEqual(steps[i].Source, steps[i].Destination);
            if (i > 0) Assert.Equal(steps[i - 1].Destination, steps[i].Source);
        }
        Assert.Equal("sage:scene", PostChainPlan.TargetName(PostTarget.Scene));
        Assert.Equal("sage:post1", PostChainPlan.TargetName(PostTarget.Post1));
    }

    // `r_scale` below 1: the scene (and the ping-pong pair) are drawn smaller, and the last step writes the
    // full-size screen. With no effect on, the chain is one plain copy, the upscale.
    [Fact]
    public void ARenderScaleDrawsTheSceneSmaller_AndAloneIsOneCopy()
    {
        Assert.Equal(new[] { new PostStep(-1, PostTarget.Scene, PostTarget.Screen) }, Plan(0.5f, false, false));
        Assert.Equal(new[] { new PostStep(1, PostTarget.Scene, PostTarget.Screen) }, Plan(0.5f, false, true));

        Assert.Equal((960, 540), PostChainPlan.ScaledSize(1920, 1080, 0.5f));
        Assert.Equal((1920, 1080), PostChainPlan.ScaledSize(1920, 1080, 1f));
        Assert.Equal((427, 240), PostChainPlan.ScaledSize(1280, 720, 1f / 3f));      // rounded
        Assert.Equal((320, 180), PostChainPlan.ScaledSize(1280, 720, 0.01f));        // clamped to MinScale
        Assert.Equal((1, 1), PostChainPlan.ScaledSize(2, 2, 0.25f));                 // never zero
        Assert.False(PostChainPlan.IsScaled(1f));
        Assert.True(PostChainPlan.IsScaled(0.75f));
    }

    private const string Records = """
        [{ "type": "post_effect", "id": "vignette", "material": "sage:m", "order": 200, "cvar": "test_vignette" },
         { "type": "post_effect", "id": "grade", "material": "sage:m", "order": 100, "cvar": "test_grade" },
         { "type": "post_effect", "id": "bloom", "material": "sage:m", "order": 100 },
         { "type": "post_effect", "id": "level", "material": "sage:m", "order": 300, "cvar": "test_level" },
         { "type": "post_effect", "id": "gone", "material": "sage:m", "order": 50 },
         { "type": "post_effect", "id": "gone", "patch": true, "disabled": true },
         { "type": "sky", "id": "day", "sunrise": 6, "sunset": 18, "keys": [ { "hour": 12 } ] }]
        """;

    // The records' order is `order`, then id; each is switched by its cvar (bool, or a number: 0 is off)
    // or, with none, always on. A mod removes one like any record, with a disabling patch.
    [Fact]
    public void EffectsRunByOrderThenId_EachSwitchedByItsCvar()
    {
        using var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<PostEffectRecord>())
            .File("data/post.json", Records)
            .Boot();
        Assert.Equal(0, app.Records.ErrorCount);

        var ordered = PostChainPlan.Ordered(app.Records);
        Assert.Equal(new[] { "sage:bloom", "sage:grade", "sage:vignette", "sage:level" }, ordered.Select(e => e.Id.ToString()));

        var cvars = new CVarRegistry();
        var grade = cvars.Register("test_grade", true, CVarFlags.None, "");
        var level = cvars.Register("test_level", 1, CVarFlags.None, "", 0, 2);
        var vignette = cvars.Register("test_vignette", 1f, CVarFlags.None, "", 0f, 1f);
        bool[] On() => ordered.Select(e => PostChainPlan.IsOn(e.Effect.Cvar.Length == 0 ? null : cvars.Find(e.Effect.Cvar))).ToArray();

        Assert.Equal(new[] { true, true, true, true }, On());
        grade.Value = false;
        vignette.Value = 0f;
        level.Value = 0;
        Assert.Equal(new[] { true, false, false, false }, On());

        // What the frame then draws: only `bloom`, straight from the scene to the screen.
        var steps = new PostStep[ordered.Count];
        int n = PostChainPlan.Plan(On(), 1f, steps);
        Assert.Equal(new[] { new PostStep(0, PostTarget.Scene, PostTarget.Screen) }, steps.AsSpan(0, n).ToArray());
        level.Value = 2;
        n = PostChainPlan.Plan(On(), 1f, steps);
        Assert.Equal(new[] { new PostStep(0, PostTarget.Scene, PostTarget.Post0), new PostStep(3, PostTarget.Post0, PostTarget.Screen) },
                     steps.AsSpan(0, n).ToArray());

        // A game without the record type (a server) has no effects, not an error.
        using var bare = HeadlessApp.Gameplay().Boot();
        Assert.Empty(PostChainPlan.Ordered(bare.Records));
    }

    // The grade's night tint follows the sun: none at noon, all of it at midnight, part of it at dusk, and
    // none at all in a world without a sky.
    [Fact]
    public void TheNightTintFollowsTheSky()
    {
        using var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<PostEffectRecord>())
            .File("data/post.json", Records)
            .Boot();
        var clock = WorldClock.Of(app.World);
        Assert.Equal(0f, PostChainPlan.Night(app.Records, clock));      // no sky named
        Assert.Equal(0f, PostChainPlan.Night(app.Records, null));

        clock.Sky = new RecordId("sage", "day");
        float At(double hour) { clock.Hour = hour; return PostChainPlan.Night(app.Records, clock); }

        Assert.Equal(0f, At(12), 3);
        Assert.Equal(1f, At(0), 3);
        Assert.Equal(1f, At(23), 3);
        float dusk = At(18);                                             // the sun on the horizon
        Assert.InRange(dusk, 0.1f, 0.9f);
        float previous = 0f;
        for (double hour = 16; hour <= 20; hour += 0.25)
        {
            float night = At(hour);
            Assert.True(night >= previous - 1e-5f, $"{hour}: the night tint went back down ({night} < {previous})");
            previous = night;
        }
    }

    // The engine's own effects load, and name materials that exist and use post.fx.
    [Fact]
    public void TheEnginesEffectsAndTheirMaterialsLoad()
    {
        using var app = HeadlessApp.Gameplay().WithEngineContent()
            .OnRegistered(a => { a.Records.Register<PostEffectRecord>(); a.Records.Register<MaterialRecord>(); })
            .Boot();
        Assert.Equal(0, app.Records.ErrorCount);

        var ordered = PostChainPlan.Ordered(app.Records);
        Assert.Equal(new[] { "sage:grade", "sage:vignette" }, ordered.Select(e => e.Id.ToString()));
        Assert.Equal(new[] { "r_post_grade", "r_post_vignette" }, ordered.Select(e => e.Effect.Cvar));
        foreach (var (id, effect) in ordered)
        {
            var material = app.Records.Get<MaterialRecord>(effect.Material);
            Assert.Equal("shaders/post.mgfxo", material.Effect.ToString());
            Assert.Equal(SamplerFilter.Point, material.Sampler.Filter);
            Assert.False(material.DepthTest);
            foreach (var param in new[] { "Exposure", "Contrast", "Saturation", "ColorFilter", "NightColor", "NightStrength",
                                          "VignetteStrength", "VignetteRadius", "VignetteSoftness" })
                Assert.True(material.Params.ContainsKey(param), $"{id}'s material has no {param} (GL has no .fx defaults)");
        }
        Assert.Equal("Grade", app.Records.Get<MaterialRecord>(ordered[0].Effect.Material).Technique);
        Assert.Equal("Vignette", app.Records.Get<MaterialRecord>(ordered[1].Effect.Material).Technique);
    }
}
