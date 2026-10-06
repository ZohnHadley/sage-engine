#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Phase 4h's exit (issue 4h-7, REDESIGN §5): a dusk-to-night transition with shadows in a streamed
// exterior, on the Sandbox's hills (decision 7). The Sandbox starts at 18:30 under its `sandbox:day` sky,
// its hut's lamps are lit at sunset by a state machine asking `time_between`, and its client turns sun
// shadows on. Everything a test can see is headless: the clock, the sky's light (RenderEnvironment), the
// lamps' switch, and where the client would put the shadow map (ShadowMath.Fit, exactly as `sage:shadow`
// calls it). The drawing is the client's, proved by the CI smoke run with `r_shadows`, `r_post` and a
// `time_set` (.github/workflows/ci.yml).
public class DuskToNightExitTests
{
    public DuskToNightExitTests() { _ = TestEnv.UserRoot; }

    private const float Frame = 1f / 60f;
    private const float Aspect = 16f / 9f;

    private static string SandboxGame => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");

    // `weather` is the client's record type (sage.client); a headless Sandbox that wants its rain registers
    // it, as SkyTests do. Without it the weather reads as clear.
    private static HeadlessApp Sandbox(bool weather = false)
    {
        var builder = HeadlessApp.ForGame(SandboxGame, new global::Sandbox.SandboxModule()).WithEngineContent();
        if (weather) builder = builder.OnRegistered(a => a.Records.Register<WeatherRecord>());
        var app = builder.Boot();
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static void Step(World world)
    {
        world.RunFixed(Frame);
        world.RunFrame(Frame, 1f);
    }

    private static float Bright(Vector3 v) => v.X * 0.2126f + v.Y * 0.7152f + v.Z * 0.0722f;

    private static Entity Player(World world) =>
        Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());

    // The hut's (the `lamp` prefab's); not the lamp circling the brick wall (issue #410), which is always lit.
    private static List<Entity> Lamps(World world) => world.Query<PointLight>().Entities.ToEntityList().Where(e => e.Name == "lamp").ToList();

    // The fit `sage:shadow` makes for the screen's main view (ShadowPass.Extract), from the headless camera.
    private static ShadowFit Fit(World world, Vector3 sun)
    {
        var views = world.Resources.Get<CameraViews>();
        Assert.True(views.HasMain);
        var main = views.Main;
        float tanY = MathF.Tan(main.FovY * 0.5f);
        var (centre, radius) = ShadowMath.PerspectiveSlice(main.Near, ShadowMath.DefaultDistance, tanY * Aspect, tanY);
        return ShadowMath.Fit(main.Position, main.Forward, centre, radius, sun, ShadowMath.DefaultSize,
                              world.Origin().ToAbsolute(Vector3.Zero));
    }

    private static float Fraction(float x) => x - MathF.Round(x);

    // The exit. Walk east from the hut, across the sector edge that moves the origin, while the clock runs
    // from 18:30 to 22:00: the sun's light and its shadows fade to nothing, the lamps are lit by 20:00, the
    // shadow map's texel grid does not slide when every number in memory moves by two sectors, and the
    // clock, the sky and the lamps survive a save and a load on the far side.
    [Fact]
    public void TheSandboxGoesFromDuskToNight_AcrossASectorEdge_WithShadowsAndLamps()
    {
        using var app = Sandbox();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        var clock = WorldClock.Of(world);
        var environment = world.Resources.Get<RenderEnvironment>();
        var origin = world.Origin();
        var day = new RecordId("sandbox", "day");

        // The scene's environment: the Sandbox's sky, at half past six.
        Assert.Equal(day, clock.Sky);
        Assert.Equal(18.5, clock.Hour, 6);

        Step(world);                                                      // the hut stands on the ground, and its entities spawn
        var lamps = Lamps(world);
        Assert.Equal(2, lamps.Count);                                     // the hut's, placed by classname
        Assert.True(environment.DrawSky);
        Assert.Equal(FogMode.Exp2, environment.FogMode);
        Assert.True(Bright(environment.SunColor) > 0.1f, $"at 18:30 the sun is still out ({environment.SunColor})");
        Assert.True(ShadowMath.Strength(environment.ShadowStrength, environment.SunDirection) > 0.2f);
        Step(world);                                                      // the machine's enter, then its wire
        Assert.All(lamps, lamp => Assert.False(world.Get<PointLight>(lamp).Lit, "a lamp is lit at dusk"));

        // Walk: four metres a tick east, standing on the hills, with the clock at four game minutes a second.
        var player = Player(world);
        var terrain = world.Resources.Get<Terrain>();
        clock.Scale = 240;
        float previousSun = float.MaxValue, previousShadow = float.MaxValue, previousAmbient = float.MaxValue;
        bool rebased = false, saved = false, sawLampsOn = false;
        double rebasedAt = 0;

        for (int tick = 0; tick < 4000 && (clock.Day == 0 && clock.Hour < 22.0); tick++)
        {
            if (!rebased)
            {
                var at = world.Get<Transform>(player).LocalPosition + new Vector3(4f, 0f, 0f);
                world.Teleport(player, Transform.At(at with { Y = terrain.HeightAt(at.X, at.Z) + 1f }));
            }

            // Before the tick: where the map sits, in this origin.
            var sectorBefore = origin.Sector;
            var viewsBefore = world.Resources.Get<CameraViews>().Main;
            var absoluteBefore = origin.ToAbsolute(viewsBefore.Position);
            Vector3 sunBefore = environment.SunDirection;
            var fitBefore = Fit(world, sunBefore);

            Step(world);

            if (origin.Sector != sectorBefore)
            {
                // The rebase: the camera's numbers changed by two sectors; the grid must not have moved in the
                // world. The same sun for both fits, because a moving sun rotates the grid (06 "As built (sun
                // shadows)": not quantised yet); what is checked is the origin's move alone.
                Assert.False(rebased, "one rebase was expected");
                rebased = true;
                rebasedAt = clock.Hour;
                Assert.Equal(1, origin.Rebases);
                Assert.Equal(2, origin.Sector.X - sectorBefore.X);

                var viewAfter = world.Resources.Get<CameraViews>().Main;
                var absoluteAfter = origin.ToAbsolute(viewAfter.Position);
                Assert.True(MathF.Abs(viewAfter.Position.X) < Terrain.SectorSize, "the camera is near the new origin");
                var fitAfter = Fit(world, sunBefore);
                Assert.Equal(fitBefore.TexelSize, fitAfter.TexelSize);

                // A point on the ground ten metres ahead, in absolute metres: it falls the same fraction of
                // a texel into its texel on both maps (a whole number of texels over, for the four metres
                // walked).
                var point = absoluteBefore + viewsBefore.Forward * 10f + new Vector3(2f, -1.5f, 0f);
                var texelBefore = ShadowMath.TexelOf(fitBefore, point - absoluteBefore);
                var texelAfter = ShadowMath.TexelOf(fitAfter, point - absoluteAfter);
                var shift = texelAfter - texelBefore;
                Assert.True(MathF.Abs(Fraction(shift.X)) < 2e-3f && MathF.Abs(Fraction(shift.Y)) < 2e-3f,
                            $"the rebase slid the shadow grid by {shift} texels");
                Assert.True(ShadowMath.Contains(fitAfter, point - absoluteAfter));
                Assert.Equal(0.0, Math.Abs(fitAfter.MinX / fitAfter.TexelSize - Math.Round(fitAfter.MinX / fitAfter.TexelSize)), 3);

                // The sun was still up when the walk crossed: these are shadows that would be drawn.
                Assert.True(ShadowMath.Strength(environment.ShadowStrength, environment.SunDirection) > 0f,
                            $"the rebase came at {WorldClock.Format(clock.Hour)}, after the shadows had gone");

                clock.Scale = 3600;                                        // then a game hour a second, to 22:00
            }

            // The light only goes down.
            float sun = Bright(environment.SunColor);
            float shadow = ShadowMath.Strength(environment.ShadowStrength, environment.SunDirection);
            float ambient = Bright(environment.AmbientSky);
            Assert.True(sun <= previousSun + 1e-5f, $"{WorldClock.Format(clock.Hour)}: the sun brightened ({previousSun} -> {sun})");
            Assert.True(shadow <= previousShadow + 1e-5f, $"{WorldClock.Format(clock.Hour)}: shadows darkened ({previousShadow} -> {shadow})");
            Assert.True(ambient <= previousAmbient + 1e-5f, $"{WorldClock.Format(clock.Hour)}: the ambient brightened");
            previousSun = sun; previousShadow = shadow; previousAmbient = ambient;

            // The lamps: dark until sunset (19:30, the machine's window), lit from 20:00.
            if (clock.Hour < 19.45)
                Assert.All(lamps, lamp => Assert.False(world.Get<PointLight>(lamp).Lit, $"lit at {WorldClock.Format(clock.Hour)}"));
            if (clock.Hour >= 20.0)
            {
                Assert.All(lamps, lamp => Assert.True(world.Get<PointLight>(lamp).Lit, $"dark at {WorldClock.Format(clock.Hour)}"));
                sawLampsOn = true;
            }

            // A save at nine on the far side of the rebase, then everything put wrong, then the load.
            if (!saved && clock.Hour >= 21.0)
            {
                saved = true;
                Assert.True(rebased);
                double hour = clock.Hour;
                int dayCount = clock.Day;
                Assert.True(app.Engine.Saves.Save("dusk"));

                clock.Hour = 9; clock.Day = 7; clock.Sky = default; clock.Scale = 0;
                foreach (var lamp in lamps)
                {
                    // Lit, the machine set it flickering (SetPattern "torch", issue #314): saved with the switch.
                    Assert.Equal("torch", world.Get<PointLight>(lamp).Pattern);
                    world.Get<PointLight>(lamp).Off = true;
                    world.Get<PointLight>(lamp).Pattern = null;
                }
                Assert.True(app.Engine.Saves.Load("dusk"));

                clock = WorldClock.Of(world);
                Assert.Equal(hour, clock.Hour, 6);
                Assert.Equal(dayCount, clock.Day);
                Assert.Equal(day, clock.Sky);
                Assert.Equal(3600, clock.Scale);
                // The sky's state is the clock's: one tick later the light is the sky at that hour again.
                clock.Scale = 0;
                Step(world);
                lamps = Lamps(world);
                Assert.Equal(2, lamps.Count);
                Assert.All(lamps, lamp => Assert.True(world.Get<PointLight>(lamp).Lit, "a lamp came back dark"));
                Assert.All(lamps, lamp => Assert.Equal("torch", world.Get<PointLight>(lamp).Pattern));
                var expected = SkyRules.Evaluate(app.Records.Get<SkyRecord>(day), clock.Hour);
                Assert.Equal(expected.AmbientSky, environment.AmbientSky);
                Assert.Equal(expected.Horizon, environment.ClearColor);
                Assert.Equal(expected.Stars, environment.Stars);
                Assert.True(environment.DrawSky);
                clock.Scale = 3600;
            }
        }

        Assert.True(rebased, "the walk never crossed the edge that moves the origin");
        Assert.True(rebasedAt < 19.5, $"rebased at {rebasedAt}");
        Assert.True(saved && sawLampsOn);
        Assert.True(clock.Hour >= 22.0 && clock.Hour < 22.5, $"the clock is at {WorldClock.Format(clock.Hour)}");

        // Night: no sun, no shadows, the stars out, and the lamps on.
        Assert.Equal(Vector3.Zero, environment.SunColor);
        Assert.Equal(0f, environment.ShadowStrength);
        Assert.Equal(0f, ShadowMath.Strength(environment.ShadowStrength, environment.SunDirection));
        Assert.True(environment.Stars > 0.99f);
        Assert.All(lamps, lamp => Assert.True(world.Get<PointLight>(lamp).Lit));
    }

    // Weather at dusk composes with the sky (decision 5): the Sandbox's rain, rolled in at 19:00, makes
    // the dusk darker — the sun, the ambient, the sky and the fog — and its shadows softer, and pulls the
    // fog in; the light is still the sky's at that hour, scaled, not the rain's own colours.
    [Fact]
    public void AStormAtDuskInTheSandboxIsDarkerThanClear()
    {
        using var app = Sandbox(weather: true);
        var world = app.World;
        var clock = WorldClock.Of(world);
        var environment = world.Resources.Get<RenderEnvironment>();
        clock.Hour = 19;
        clock.Scale = 0;

        Step(world);
        var clear = new RenderEnvironment();
        Copy(environment, clear);
        var sky = SkyRules.Evaluate(app.Records.Get<SkyRecord>(clock.Sky), 19);
        Assert.Equal(sky.SunColor, clear.SunColor);                       // clear changes nothing
        Assert.True(Bright(clear.SunColor) > 0f);

        var rain = new RecordId("sandbox", "rain");
        var weather = world.Resources.Get<Weather>();
        weather.Set(rain, 0f);
        weather.Advance(1f);                                              // the client's system blends it in
        Step(world);
        Assert.Equal(rain, weather.Showing);

        Assert.True(Bright(environment.SunColor) < Bright(clear.SunColor));
        Assert.True(Bright(environment.AmbientSky) < Bright(clear.AmbientSky));
        Assert.True(Bright(environment.ClearColor) < Bright(clear.ClearColor));
        Assert.True(Bright(environment.Zenith) < Bright(clear.Zenith));
        Assert.True(Bright(environment.FogColor) < Bright(clear.FogColor));
        Assert.True(environment.FogEnd < clear.FogEnd);
        Assert.True(environment.ShadowStrength < clear.ShadowStrength);
        Assert.True(environment.ShadowStrength > 0f);

        // The dusk's own colours, scaled: not the rain record's grey `fogColor` (that is for a world with no sky).
        Assert.Equal(sky.Fog * new Vector3(0.55f, 0.6f, 0.6f), environment.FogColor);
        Assert.Equal(sky.SunColor * 0.45f, environment.SunColor);
    }

    private static void Copy(RenderEnvironment from, RenderEnvironment to)
    {
        to.SunColor = from.SunColor;
        to.AmbientSky = from.AmbientSky;
        to.ClearColor = from.ClearColor;
        to.Zenith = from.Zenith;
        to.FogColor = from.FogColor;
        to.FogEnd = from.FogEnd;
        to.ShadowStrength = from.ShadowStrength;
    }
}
