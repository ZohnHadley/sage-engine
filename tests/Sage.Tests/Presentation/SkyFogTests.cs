#nullable enable
using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The sky pass and fog (issue 4h-5, docs/design/06 "As built (sky and fog)"): how much fog covers a point,
// past which distance nothing shows through, and what the sky looks like at an hour, decided headless by
// `FogMath` and `SkyRules`. The drawing (`sage:sky`, sky.fx, common.fxh's FogFactor) needs a GPU and is
// exercised by the smoke run.
public class SkyFogTests
{
    public SkyFogTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "sky", "id": "day", "sunrise": 6, "sunset": 18, "keys": [
             { "hour": 0,  "sun": [0.0, 0.0, 0.0], "horizon": [0.02, 0.03, 0.06], "zenith": [0.0, 0.01, 0.04], "fog": [0.02, 0.03, 0.06], "fogStart": 20, "fogEnd": 120 },
             { "hour": 12, "sun": [1.0, 0.95, 0.85], "horizon": [0.6, 0.7, 0.85], "zenith": [0.2, 0.4, 0.8], "fog": [0.6, 0.7, 0.85], "fogStart": 40, "fogEnd": 300 },
             { "hour": 18, "sun": [0.9, 0.4, 0.2], "horizon": [0.8, 0.4, 0.3], "zenith": [0.25, 0.25, 0.45], "fog": [0.7, 0.4, 0.3], "fogStart": 30, "fogEnd": 200 },
             { "hour": 22, "sun": [0.0, 0.0, 0.0], "horizon": [0.03, 0.04, 0.08], "zenith": [0.01, 0.02, 0.05], "fog": [0.03, 0.04, 0.08], "fogStart": 20, "fogEnd": 120 } ] },

         { "type": "sky", "id": "haze", "fogMode": "Exp2", "keys": [
             { "hour": 0, "fogStart": 10, "fogEnd": 150, "fogDensity": 0.02 } ] },

         { "type": "weather", "id": "clear", "label": "clear" },
         { "type": "weather", "id": "storm", "label": "storm", "sunScale": 0.4, "ambientScale": 0.5, "fogEndScale": 0.5 }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp Boot()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<WeatherRecord>())
            .File("data/sky.json", Records)
            .Boot("skyfog");
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static Weather Settled(string id) => new() { Current = Id(id), Target = Id(id) };

    private static float Bright(Vector3 v) => v.X * 0.2126f + v.Y * 0.7152f + v.Z * 0.0722f;

    // ---- fog ----

    [Fact]
    public void LinearFogRisesInAStraightLineFromStartToEnd()
    {
        const float Start = 30f, End = 200f;
        Assert.Equal(0f, FogMath.Factor(FogMode.Linear, 0f, Start, End));
        Assert.Equal(0f, FogMath.Factor(FogMode.Linear, Start, Start, End));
        Assert.Equal(0.5f, FogMath.Factor(FogMode.Linear, 115f, Start, End), 5);
        Assert.Equal(1f, FogMath.Factor(FogMode.Linear, End, Start, End));
        Assert.Equal(1f, FogMath.Factor(FogMode.Linear, 5000f, Start, End));

        // What the shaders are given for linear fog is 0: the formula the engine had before 4h-5.
        Assert.Equal(0f, FogMath.ShaderParam(FogMode.Linear, Start, End, 0.05f));
    }

    [Fact]
    public void Exp2FogStartsSoftAndIsCompleteAtItsEnd()
    {
        const float Start = 30f, End = 200f;
        Assert.Equal(0f, FogMath.Factor(FogMode.Exp2, 10f, Start, End));
        Assert.Equal(0f, FogMath.Factor(FogMode.Exp2, Start, Start, End));

        // Softer than linear near the start, then catching up and closing in: it rises all the way.
        float previous = 0f;
        for (float d = Start + 1f; d <= End; d += 1f)
        {
            float f = FogMath.Factor(FogMode.Exp2, d, Start, End);
            Assert.True(f >= previous, $"exp² fog fell at {d} m");
            previous = f;
        }
        Assert.True(FogMath.Factor(FogMode.Exp2, 45f, Start, End) < FogMath.Factor(FogMode.Linear, 45f, Start, End));
        Assert.True(FogMath.Factor(FogMode.Exp2, 150f, Start, End) > FogMath.Factor(FogMode.Linear, 150f, Start, End));

        // With no density, it is complete at `end` like linear fog: under half an 8-bit step shows through.
        Assert.True(FogMath.Factor(FogMode.Exp2, End, Start, End) >= 1f - FogMath.Invisible);
        Assert.True(FogMath.Factor(FogMode.Exp2, End - 10f, Start, End) < 1f - FogMath.Invisible);

        // An explicit density is the textbook curve past the start: 1 - e^-(density·x)².
        const float Density = 0.02f;
        Assert.Equal(1f - MathF.Exp(-1f), FogMath.Factor(FogMode.Exp2, Start + 50f, Start, End, Density), 5);

        // The shaders' form (common.fxh FogFactor: 1 - 2^-(w·x)²) is the same number.
        foreach (float density in new[] { 0f, Density })
            for (float d = Start; d < 400f; d += 17f)
            {
                float w = FogMath.ShaderParam(FogMode.Exp2, Start, End, density) * (d - Start);
                Assert.Equal(FogMath.Factor(FogMode.Exp2, d, Start, End, density), 1f - MathF.Pow(2f, -w * w), 4);
            }
    }

    [Fact]
    public void FogCullsWhatItFullyHides()
    {
        const float Start = 30f, End = 200f;

        // Linear: the end, where the factor reaches 1.
        Assert.Equal(End, FogMath.CullDistance(FogMode.Linear, Start, End));

        // Exp²: the first distance where no more than `Invisible` of the surface shows, with or without a density.
        foreach (float density in new[] { 0f, 0.01f, 0.05f })
        {
            float cull = FogMath.CullDistance(FogMode.Exp2, Start, End, density);
            Assert.True(FogMath.Factor(FogMode.Exp2, cull, Start, End, density) >= 1f - FogMath.Invisible - 1e-6f);
            Assert.True(FogMath.Factor(FogMode.Exp2, cull - 1f, Start, End, density) < 1f - FogMath.Invisible);
        }
        Assert.Equal(End, FogMath.CullDistance(FogMode.Exp2, Start, End), 3);
        Assert.Equal(Start + MathF.Sqrt(MathF.Log(512f)) / 0.05f, FogMath.CullDistance(FogMode.Exp2, Start, End, 0.05f), 3);

        // A sphere is hidden when its nearest point is past the cull distance, whichever way it lies;
        // one that reaches back across it is drawn.
        float at = FogMath.CullDistance(FogMode.Linear, Start, End);
        var ahead = Vector3.Normalize(new Vector3(0.3f, -0.1f, -1f));
        Assert.True(FogMath.Hides(at, ahead * (at + 6f), 5f));
        Assert.True(FogMath.Hides(at, -ahead * (at + 6f), 5f));
        Assert.False(FogMath.Hides(at, ahead * (at + 4f), 5f));
        Assert.False(FogMath.Hides(at, ahead * 50f, 5f));
    }

    // ---- the sky ----

    [Fact]
    public void TheSkyAtNineAtNightAgainstNoon()
    {
        using var app = Boot();
        var sky = app.Records.Get<SkyRecord>(Id("day"));
        var noon = new RenderEnvironment();
        var night = new RenderEnvironment();
        SkyRules.Apply(sky, 12, app.Records, Settled("clear"), noon);
        SkyRules.Apply(sky, 21, app.Records, Settled("clear"), night);

        Assert.True(noon.DrawSky);
        Assert.True(night.DrawSky);

        // Stars: none by day, all of them at 21:00 (the sun set at 18:00 and is far below the horizon);
        // they come out as it goes down.
        Assert.Equal(0f, noon.Stars);
        Assert.Equal(1f, night.Stars);
        Assert.Equal(0f, SkyRules.StarsAt(0.01f));
        Assert.True(SkyRules.StarsAt(-0.05f) is > 0f and < 1f);
        Assert.Equal(1f, SkyRules.StarsAt(-SkyRules.StarsOut));
        Assert.Equal(1f, SkyRules.Evaluate(sky, 21).Stars);

        // The gradient: the horizon's colour at and below the horizon, the zenith's overhead.
        var up = Vector3.UnitY;
        var level = Vector3.Normalize(new Vector3(1f, 0f, 0.2f));
        var high = Vector3.Normalize(new Vector3(1f, 1f, 0f));
        Assert.Equal(noon.ClearColor, SkyRules.Gradient(noon.ClearColor, noon.Zenith, 0f));
        Assert.Equal(noon.ClearColor, SkyRules.Gradient(noon.ClearColor, noon.Zenith, -0.5f));
        Assert.Equal(noon.Zenith, SkyRules.Gradient(noon.ClearColor, noon.Zenith, 1f));

        // With fog on, the horizon is the fog's colour (a hill fogged away meets a sky of its own colour);
        // above the haze band it is the gradient.
        AssertNear(noon.FogColor, SkyRules.ColorAt(noon, level));
        AssertNear(night.FogColor, SkyRules.ColorAt(night, level));
        AssertNear(SkyRules.Gradient(noon.ClearColor, noon.Zenith, high.Y), SkyRules.ColorAt(noon, high));
        AssertNear(noon.Zenith, SkyRules.ColorAt(noon, up));

        // 21:00 is dark everywhere noon is bright, and has no sun in it.
        foreach (var direction in new[] { up, level, high, -noon.SunDirection })
            Assert.True(Bright(SkyRules.ColorAt(night, direction)) < 0.25f * Bright(SkyRules.ColorAt(noon, direction)), $"{direction}");
        Assert.Equal(Vector3.Zero, night.SunColor);

        // At noon the sun disc is where the light comes from, brighter than the sky a few degrees away.
        var toSun = -noon.SunDirection;
        Assert.Equal(1f, SkyRules.SunDisc(toSun, noon.SunDirection));
        var beside = Vector3.Normalize(toSun + new Vector3(0.1f, 0f, 0f));
        Assert.Equal(0f, SkyRules.SunDisc(beside, noon.SunDirection));
        Assert.True(Bright(SkyRules.ColorAt(noon, toSun)) > Bright(SkyRules.ColorAt(noon, beside)) + 1f);
    }

    [Fact]
    public void TheSkySetsTheFogAndDrawsOnlyWhileTheWorldHasOne()
    {
        using var app = Boot();
        var world = app.World;
        var environment = world.Resources.Get<RenderEnvironment>();
        var clock = WorldClock.Of(world);

        // Without a sky, nothing of 4h-5 changes the look: no sky pass, linear fog, no stars.
        for (int i = 0; i < 10; i++) world.RunFixed(1f / 60f);
        Assert.False(environment.DrawSky);
        Assert.Equal(FogMode.Linear, environment.FogMode);
        Assert.Equal(0f, environment.Stars);
        Assert.Equal(0f, environment.FogDensity);

        // A sky with exp² fog: the pass draws, and the fog takes the sky's mode and density.
        clock.Sky = Id("haze");
        world.RunFixed(1f / 60f);
        Assert.True(environment.DrawSky);
        Assert.Equal(FogMode.Exp2, environment.FogMode);
        Assert.Equal(0.02f, environment.FogDensity, 5);

        // The sky taken away: the pass stops drawing.
        clock.Sky = default;
        world.RunFixed(1f / 60f);
        Assert.False(environment.DrawSky);

        // A heavy sky hides the stars and pulls the exp² fog in, as it pulls in linear fog's end.
        var sky = app.Records.Get<SkyRecord>(Id("day"));
        var clear = new RenderEnvironment();
        var storm = new RenderEnvironment();
        SkyRules.Apply(sky, 21, app.Records, Settled("clear"), clear);
        SkyRules.Apply(sky, 21, app.Records, Settled("storm"), storm);
        Assert.True(storm.Stars < clear.Stars);
        var haze = app.Records.Get<SkyRecord>(Id("haze"));
        SkyRules.Apply(haze, 12, app.Records, Settled("clear"), clear);
        SkyRules.Apply(haze, 12, app.Records, Settled("storm"), storm);
        Assert.True(FogMath.CullDistance(storm.FogMode, storm.FogStart, storm.FogEnd, storm.FogDensity)
                    < FogMath.CullDistance(clear.FogMode, clear.FogStart, clear.FogEnd, clear.FogDensity));
    }

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");
}
