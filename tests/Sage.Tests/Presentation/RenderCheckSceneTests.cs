#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The render check's scene (issue #318, tests/games/render-check): the fixed scene CI draws in the real host
// and reads back with `r_pixelcheck`. A pixel check is only as good as its regions, and the frame itself can
// be seen only where the shaders compile, so this proves headlessly what each region of the specs looks at:
// rays cast through it from the scene's camera into the scene's physics (the `.map`'s brushes) find the sky,
// the slab's shadow, the sunlit ground or the ground in full fog, as its `sees` says. The colours and limits
// the checks expect are held against the sky, fog and lighting maths the shaders implement (SkyRules,
// FogMath, lit.fx's HemiAmbient and sun), so a change to either shows here before CI's drawing job.
public class RenderCheckSceneTests
{
    private const float Dt = 1f / 60f;
    private const float Gap = 0.1f;   // how far a check's limit stays from what the maths says the frame holds

    private static string Game => Path.Combine(TestEnv.FolderAbove("tests"), "tests", "games", "render-check");

    private static PixelCheckSpec Spec(string name) =>
        PixelCheckSpec.Parse(File.ReadAllText(Path.Combine(Game, "content", "checks", name)));

    private static HeadlessApp Boot()
    {
        var app = HeadlessApp.ForGame(Game).WithEngineContent()
            .OnRegistered(a => { a.Records.Register<PostEffectRecord>(); a.Records.Register<MaterialRecord>(); })
            .Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        app.World.RunFixed(Dt);          // the sky system writes the environment; the map's brushes are in physics
        app.World.RunFrame(Dt, 1f);
        return app;
    }

    // The scene's camera, as the client projects it: the view's rays through points of the frame.
    private sealed class View
    {
        private readonly Pose _pose;
        private readonly float _tanHalf, _aspect;
        public readonly float Far;

        public View(World world, PixelCheckSpec spec)
        {
            var eye = world.FindByName("eye");
            Assert.False(eye.IsNull, "the render check has no camera called eye");
            var camera = world.Get<Camera>(eye);
            Assert.True(camera.Enabled);
            Assert.Equal(CameraProjection.Perspective, camera.Projection);
            Assert.Equal(eye, world.Resources.Get<CameraViews>().Main.Entity);   // it is the screen's view
            _pose = world.Get<GlobalTransform>(eye).Interpolated(1f);
            _tanHalf = MathF.Tan(camera.FovY * MathF.PI / 360f);
            _aspect = spec.Width / (float)spec.Height;
            Far = camera.Far;
        }

        public Vector3 From => _pose.Position;

        // u, v: fractions of the frame from its top left.
        public Vector3 Ray(float u, float v) => Vector3.Normalize(Vector3.Transform(
            new Vector3((2f * u - 1f) * _tanHalf * _aspect, (1f - 2f * v) * _tanHalf, -1f), _pose.Rotation));
    }

    // Points spread over a region, inset from its edges: what the region's mean is made of.
    private static IEnumerable<(float U, float V)> Grid(PixelRegion r, int n = 6)
    {
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                yield return (r.X + r.Width * (i + 0.5f) / n, r.Y + r.Height * (j + 0.5f) / n);
    }

    // What a ray from the camera sees, in the words of a region's `sees`.
    private static string Sees(IPhysicsWorld physics, RenderEnvironment env, View view, Vector3 ray, out float distance)
    {
        var hit = physics.Raycast(view.From, ray, view.Far);
        distance = hit.Distance;
        if (!hit.Hit) return ray.Y > SkyRules.HazeBand ? "sky" : "sky in the haze";
        float fog = FogMath.Factor(env.FogMode, hit.Distance, env.FogStart, env.FogEnd, env.FogDensity);
        if (fog >= 0.999f) return "fog";
        if (fog > 0.02f) return "ground in some fog";
        if (hit.Normal.Y < 0.99f) return "something other than the ground";
        var shade = physics.Raycast(hit.Position + hit.Normal * 0.02f, -env.SunDirection, 500f);
        return shade.Hit ? "shadow" : "lit";
    }

    private static float Luma(Vector3 c) => PixelCheck.Measure(PixelMeasure.Luma, c);

    private static PixelCheckRule Check(PixelCheckSpec spec, string name) =>
        spec.Checks.SingleOrDefault(c => c.Name == name) ?? throw new Xunit.Sdk.XunitException($"no check '{name}'");

    [Fact]
    public void TheRenderCheckGameValidates()
    {
        var report = ContentValidation.Run(new ValidateOptions
        {
            GameDirectory = Game,
            EngineContentDirectory = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content"),
            AvailablePlugins = BasePlugins.All(),
        });
        Assert.True(report.Ok, string.Join("\n", report.Errors));
    }

    // Both specs read, cover the frame's four kinds of region, and lay them out alike.
    [Fact]
    public void TheSpecsReadAndShareTheirRegions()
    {
        var drawing = Spec("drawing.json");
        var post = Spec("post.json");
        Assert.Equal((640, 360), (drawing.Width, drawing.Height));
        Assert.Equal(drawing.Regions, post.Regions);
        Assert.Equal(new[] { "fog", "lit", "shadow", "sky" }, drawing.Regions.Select(r => r.Sees).OrderBy(s => s, StringComparer.Ordinal));
        // Each spec has checks for the frame drawn with shaders and for the one drawn without.
        foreach (var spec in new[] { drawing, post })
        {
            Assert.Contains(spec.Checks, c => c.When == PixelCheckWhen.Shaders);
            Assert.Contains(spec.Checks, c => c.When == PixelCheckWhen.NoShaders);
        }
    }

    // The heart of it: every point of every region sees what the region says, from the scene's own camera
    // at the spec's aspect, so a check on a region measures the thing it is named for.
    [Fact]
    public void EachRegionSeesWhatItsSpecSays()
    {
        using var app = Boot();
        var world = app.World;
        var spec = Spec("drawing.json");
        var env = world.Resources.Get<RenderEnvironment>();
        var physics = world.Resources.Get<IPhysicsWorld>();
        var view = new View(world, spec);

        foreach (var region in spec.Regions)
            foreach (var (u, v) in Grid(region))
            {
                string sees = Sees(physics, env, view, view.Ray(u, v), out float distance);
                Assert.True(sees == region.Sees, $"region {region.Name} at ({u:0.000}, {v:0.000}) sees {sees} ({distance:0.0} m), not {region.Sees}");
            }
    }

    // The scene holds still and draws everything the checks look for: a sky pass, fog, and a sun high enough
    // to cast a full shadow.
    [Fact]
    public void TheSceneHasASkyFogAndAFullShadowAtNoon()
    {
        using var app = Boot();
        var world = app.World;
        var env = world.Resources.Get<RenderEnvironment>();
        Assert.True(env.DrawSky);
        Assert.True(env.Fog);
        Assert.Equal(FogMode.Linear, env.FogMode);
        Assert.Equal(1f, ShadowMath.Strength(env.ShadowStrength, env.SunDirection), 3);
        Assert.Equal(0f, env.Stars);
        Assert.True(-env.SunDirection.Y > 0.9f, $"the sun is at {-env.SunDirection}");

        // Noon, as the scene says (CI also stops the clock with time_scale 0).
        var clock = WorldClock.Of(world);
        Assert.Equal(12.0, clock.Hour, 2);
    }

    // The colours and limits the shader checks expect, against the maths the shaders implement.
    [Fact]
    public void TheChecksExpectWhatTheSkyFogAndLightGive()
    {
        using var app = Boot();
        var world = app.World;
        var spec = Spec("drawing.json");
        var env = world.Resources.Get<RenderEnvironment>();
        var view = new View(world, spec);
        spec.TryRegion("sky", out var skyRegion);

        // The sky pass: the mean of SkyRules.ColorAt over the region (what sky.fx draws, stars aside: noon).
        var sky = Vector3.Zero;
        int n = 0;
        foreach (var (u, v) in Grid(skyRegion, 12)) { sky += SkyRules.ColorAt(env, view.Ray(u, v)); n++; }
        sky /= n;
        var skyColour = Check(spec, "the sky pass draws the sky's colour");
        Assert.True(Vector3.Distance(sky, skyColour.Color!.Value) < 0.03f, $"the sky region's colour is {PixelCheck.Format(sky)}");
        Assert.True(PixelCheck.Measure(PixelMeasure.BlueMinusRed, sky) >= Check(spec, "the sky is blue").Min + Gap);

        // Fog: the far ground is entirely the fog's colour, which is orange.
        Assert.Equal(env.FogColor, Check(spec, "the far ground is the fog's colour").Color!.Value);
        Assert.True(PixelCheck.Measure(PixelMeasure.RedMinusBlue, env.FogColor) >= Check(spec, "the far ground is fogged, not white").Min + Gap);

        // The ground is white (sage:lit_default, no texture of its own): in the sun lit.fx gives the hemisphere
        // ambient plus the sun, saturated; in the slab's shadow the ambient alone (`ShadowLit` only scales the sun).
        var up = Vector3.UnitY;
        var ambient = Vector3.Lerp(env.AmbientGround, env.AmbientSky, up.Y * 0.5f + 0.5f);
        var sunlit = Vector3.Clamp(ambient + env.SunColor * MathF.Max(0f, Vector3.Dot(up, -env.SunDirection)), Vector3.Zero, Vector3.One);
        float lit = Luma(sunlit), shadow = Luma(ambient);
        Assert.True(Check(spec, "the ground in the sun is bright").Min <= lit - Gap, $"lit ground is {lit:0.000}");
        Assert.True(Check(spec, "the slab's shadow is dark").Max >= shadow + Gap, $"shadowed ground is {shadow:0.000}");
        Assert.True(Check(spec, "the shadow is darker than the sunlit ground beside it").Max >= shadow - lit + Gap);
        // ...and a frame where the shadow stopped drawing (both regions sunlit) fails them.
        Assert.True(lit > Check(spec, "the slab's shadow is dark").Max!.Value + Gap);

        // A white ground in the sun fails the fog checks, so a frame whose fog stopped drawing fails.
        Assert.True(Math.Abs(1f - env.FogColor.Z) > Check(spec, "the far ground is the fog's colour").Tolerance + Gap);

        // Without shaders every view is its clear colour: the sky's horizon.
        foreach (var check in spec.Checks.Where(c => c.When == PixelCheckWhen.NoShaders))
            Assert.True(Vector3.Distance(env.ClearColor, check.Color!.Value) < 0.01f, $"{check.Name}: the clear colour is {PixelCheck.Format(env.ClearColor)}");
        foreach (var check in Spec("post.json").Checks.Where(c => c.When == PixelCheckWhen.NoShaders))
            Assert.True(Vector3.Distance(env.ClearColor, check.Color!.Value) < 0.01f, check.Name);
    }

    // The post check's grade: the scene's own effect, after the engine's grade, with no saturation, so the
    // blue sky and the orange fog both come out grey when the chain draws.
    [Fact]
    public void ThePostCheckGradesEverythingGrey()
    {
        using var app = Boot();
        var ordered = PostChainPlan.Ordered(app.Records);
        Assert.Equal(new[] { "sage:grade", "rendercheck:grey", "sage:vignette" }, ordered.Select(e => e.Id.ToString()));
        var grey = ordered.Single(e => e.Id.ToString() == "rendercheck:grey").Effect;
        Assert.True(string.IsNullOrEmpty(grey.Cvar), "the grey grade runs whenever r_post is on");
        var material = app.Records.Get<MaterialRecord>(grey.Material);
        Assert.Equal("Grade", material.Technique);
        Assert.Equal("shaders/post.mgfxo", material.Effect.ToString());   // from sage:post_copy, with every param post.fx has
        Assert.Equal(0f, material.Params["Saturation"].Values![0]);
        Assert.Equal(1f, material.Params["Contrast"].Values![0]);

        var post = Spec("post.json");
        Assert.All(post.Checks.Where(c => c.Measure == PixelMeasure.Chroma), c => Assert.True(c.Max <= 0.05f));
        // Ungraded, the regions those checks look at are far from grey.
        var env = app.World.Resources.Get<RenderEnvironment>();
        Assert.True(PixelCheck.Measure(PixelMeasure.Chroma, env.FogColor) > 0.05f + Gap);
        Assert.True(PixelCheck.Measure(PixelMeasure.Chroma, env.Zenith) > 0.05f + Gap);
    }
}
