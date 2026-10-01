#nullable enable
using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The world clock, the sky over a day and weather on top of it (issue 4h-2, REDESIGN §4.7).
//
// Everything here is arithmetic on records: the clock is a number that moves in fixed ticks, the sky is a
// curve by hour, and weather scales and tints what the sky says. The client only draws the result, so a
// test can ask "how dark is it at 21:00 in a storm" without a GPU.
public class SkyTests
{
    public SkyTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "sky", "id": "day", "sunrise": 6, "sunset": 18, "keys": [
             { "hour": 0,  "sun": [0.0, 0.0, 0.0],  "ambientSky": [0.02, 0.03, 0.08], "ambientGround": [0.01, 0.01, 0.02],
               "horizon": [0.02, 0.03, 0.06], "zenith": [0.0, 0.01, 0.04], "fog": [0.02, 0.03, 0.06], "fogStart": 20, "fogEnd": 120, "shadow": 0 },
             { "hour": 6,  "sun": [0.9, 0.5, 0.3],  "ambientSky": [0.25, 0.22, 0.25], "ambientGround": [0.1, 0.09, 0.08],
               "horizon": [0.8, 0.5, 0.4], "zenith": [0.3, 0.4, 0.6], "fog": [0.7, 0.5, 0.4], "fogStart": 30, "fogEnd": 200, "shadow": 0.7 },
             { "hour": 12, "sun": [1.0, 0.95, 0.85], "ambientSky": [0.45, 0.5, 0.6],  "ambientGround": [0.22, 0.2, 0.17],
               "horizon": [0.6, 0.7, 0.85], "zenith": [0.2, 0.4, 0.8], "fog": [0.6, 0.7, 0.85], "fogStart": 40, "fogEnd": 300, "shadow": 1 },
             { "hour": 18, "sun": [0.9, 0.4, 0.2],  "ambientSky": [0.25, 0.2, 0.25],  "ambientGround": [0.1, 0.08, 0.08],
               "horizon": [0.8, 0.4, 0.3], "zenith": [0.25, 0.25, 0.45], "fog": [0.7, 0.4, 0.3], "fogStart": 30, "fogEnd": 200, "shadow": 0.7 },
             { "hour": 22, "sun": [0.0, 0.0, 0.0],  "ambientSky": [0.03, 0.04, 0.1],  "ambientGround": [0.01, 0.01, 0.02],
               "horizon": [0.03, 0.04, 0.08], "zenith": [0.01, 0.02, 0.05], "fog": [0.03, 0.04, 0.08], "fogStart": 20, "fogEnd": 120, "shadow": 0 } ] },

         { "type": "sky", "id": "wrap", "keys": [
             { "hour": 5,  "horizon": [0.0, 0.0, 0.0], "fogEnd": 100 },
             { "hour": 21, "horizon": [0.8, 0.8, 0.8], "fogEnd": 500 } ] },

         { "type": "weather", "id": "clear", "label": "clear",
           "fogColor": [0.3, 0.4, 0.2], "fogStart": 30, "fogEnd": 200, "skyColor": [0.3, 0.4, 0.2] },
         { "type": "weather", "id": "storm", "label": "storm", "sunScale": 0.4, "ambientScale": 0.5,
           "fogTint": [0.5, 0.5, 0.55], "skyTint": [0.45, 0.45, 0.5], "fogStartScale": 0.5, "fogEndScale": 0.4 }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp Boot(string world = "sky")
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<WeatherRecord>())
            .File("data/sky.json", Records)
            .Boot(world);
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static float Bright(Vector3 v) => v.X * 0.2126f + v.Y * 0.7152f + v.Z * 0.0722f;

    private static SkyState At(HeadlessApp app, string sky, double hour) =>
        SkyRules.Evaluate(app.Records.Get<SkyRecord>(Id(sky)), hour);

    private static Weather Settled(string id) => new() { Current = Id(id), Target = Id(id) };

    // ---- the clock ----

    [Fact]
    public void TheClockRunsInFixedTicksAndWrapsIntoTheNextDay()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Hour = 23.5;
        clock.Scale = 3600;              // a game hour a second

        for (int i = 0; i < 60; i++) world.RunFixed(1f / 60f);   // one second: one hour

        Assert.Equal(1, clock.Day);
        Assert.Equal(0.5, clock.Hour, 3);

        clock.Scale = 0;
        for (int i = 0; i < 60; i++) world.RunFixed(1f / 60f);
        Assert.Equal(0.5, clock.Hour, 3);                        // stopped

        clock.Hour = 25;                                         // wraps, and does not change the day
        Assert.Equal(1.0, clock.Hour, 6);
        Assert.Equal(1, clock.Day);
    }

    [Fact]
    public void TimeBetweenWrapsPastMidnight()
    {
        Assert.True(WorldClock.Between(21, 20, 6));
        Assert.True(WorldClock.Between(3, 20, 6));
        Assert.False(WorldClock.Between(12, 20, 6));
        Assert.True(WorldClock.Between(20, 20, 6));      // inclusive at the start
        Assert.False(WorldClock.Between(6, 20, 6));      // exclusive at the end
        Assert.True(WorldClock.Between(9, 6, 12));       // a window inside the day
        Assert.False(WorldClock.Between(13, 6, 12));
        Assert.False(WorldClock.Between(9, 9, 9));       // an empty window

        using var app = Boot();
        var world = app.World;
        var night = JsonSerializer.Deserialize<ICondition>("""{ "time_between": { "from": 19, "to": 6 } }""", app.Records.Json)!;

        WorldClock.Of(world).Hour = 12;
        Assert.False(Conditions.Evaluate(world, default, night));
        WorldClock.Of(world).Hour = 19.5;
        Assert.True(Conditions.Evaluate(world, default, night));
        WorldClock.Of(world).Hour = 2;
        Assert.True(Conditions.Evaluate(world, default, night));
    }

    [Fact]
    public void TheConsoleSetsTheClock()
    {
        using var app = Boot();
        var clock = WorldClock.Of(app.World);

        app.CVars.Execute("time_set 18:30");
        Assert.Equal(12, clock.Hour);                            // a cheat: refused until cheats are on
        app.CVars.Execute("sv_cheats 1");
        app.CVars.Execute("time_set 18:30");
        Assert.Equal(18.5, clock.Hour, 6);
        app.CVars.Execute("time_scale 120");
        Assert.Equal(120, clock.Scale);
        app.CVars.Execute("time_set nonsense");                  // refused, not applied
        Assert.Equal(18.5, clock.Hour, 6);
        app.CVars.Execute("time_scale -4");
        Assert.Equal(120, clock.Scale);
        Assert.Equal("18:30", WorldClock.Format(clock.Hour));
        app.CVars.Execute("time");                               // prints; does not throw
    }

    [Fact]
    public void TheClockSurvivesASave()
    {
        using var app = Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Day = 2; clock.Hour = 18.5; clock.Scale = 30; clock.Sky = Id("day");

        Assert.True(app.Engine.Saves.Save("clock"));
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(app.Engine.Saves.Root, "clock", "world_sky.json")))!;
        Assert.Equal(18.5, (double)saved["resources"]!["clock"]!["data"]!["Hour"]!);

        clock.Day = 9; clock.Hour = 3; clock.Scale = 0; clock.Sky = default;
        Assert.True(app.Engine.Saves.Load("clock"));

        var loaded = WorldClock.Of(world);
        Assert.Equal(2, loaded.Day);
        Assert.Equal(18.5, loaded.Hour, 6);
        Assert.Equal(30, loaded.Scale);
        Assert.Equal(Id("day"), loaded.Sky);
    }

    // A save from before the clock has no `clock` resource. It loads, and the world keeps the clock it has.
    [Fact]
    public void AnOldSaveWithoutAClockStillLoads()
    {
        using var app = Boot();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        Assert.True(app.Engine.Saves.Save("old"));

        string file = Path.Combine(app.Engine.Saves.Root, "old", "world_sky.json");
        var doc = JsonNode.Parse(File.ReadAllText(file))!;
        Assert.NotNull(doc["resources"]!["clock"]);
        doc["resources"]!.AsObject().Remove("clock");
        File.WriteAllText(file, doc.ToJsonString());

        var clock = WorldClock.Of(app.World);
        clock.Hour = 9;
        using var log = new CaptureSink();
        Assert.True(app.Engine.Saves.Load("old"));
        Assert.DoesNotContain(log.Entries, e => e.Level >= LogLevel.Warn && e.Message.Contains("clock"));
        Assert.True(WorldClock.Of(app.World).Hour is >= 0 and < 24);
    }

    // ---- the sky ----

    // Dusk: from the afternoon to night the light only goes down.
    [Fact]
    public void DuskDarkensSteadily()
    {
        using var app = Boot();
        float previous = float.MaxValue;
        for (double hour = 15; hour <= 22.5; hour += 0.25)
        {
            var s = At(app, "day", hour);
            float light = Bright(s.SunColor) + Bright(s.AmbientSky);
            Assert.True(light <= previous + 1e-5f, $"{hour}: {light} is brighter than the quarter hour before, {previous}");
            previous = light;
        }
        Assert.True(Bright(At(app, "day", 21).SunColor) < Bright(At(app, "day", 17).SunColor));
        Assert.True(Bright(At(app, "day", 21).AmbientSky) < Bright(At(app, "day", 17).AmbientSky));
    }

    [Fact]
    public void TheCurveWrapsPastMidnight()
    {
        using var app = Boot();

        // Keys at 5 (black, fog 100) and 21 (grey, fog 500): the gap from 21:00 to 5:00 is 8 hours, and
        // 1:00 is the middle of it.
        var midnightMiddle = At(app, "wrap", 1);
        Assert.Equal(0.4f, midnightMiddle.Horizon.X, 3);
        Assert.Equal(300f, midnightMiddle.FogEnd, 1);

        // Continuous across midnight, and a full day later is the same sky.
        Assert.Equal(At(app, "wrap", 23.999).Horizon.X, At(app, "wrap", 0.001).Horizon.X, 3);
        Assert.Equal(At(app, "wrap", 2).Horizon.X, At(app, "wrap", 26).Horizon.X, 5);
        Assert.Equal(At(app, "wrap", 2).Horizon.X, At(app, "wrap", -22).Horizon.X, 5);

        // Exactly on a key it is that key.
        Assert.Equal(0f, At(app, "wrap", 5).Horizon.X, 4);
        Assert.Equal(0.8f, At(app, "wrap", 21).Horizon.X, 4);
    }

    [Fact]
    public void TheSunBelowTheHorizonGivesNoLightAndNoShadow()
    {
        using var app = Boot();

        foreach (double hour in new[] { 19.5, 21, 23, 0, 3, 5.5 })
        {
            var s = At(app, "day", hour);
            Assert.True(s.SunElevation <= 0.05f, $"{hour}: the sun should be down, elevation {s.SunElevation}");
            if (s.SunElevation > 0) continue;
            Assert.Equal(Vector3.Zero, s.SunColor);
            Assert.Equal(0f, s.ShadowStrength);
        }

        var noon = At(app, "day", 12);
        Assert.True(noon.SunElevation > 0.9f);
        Assert.True(noon.SunDirection.Y < -0.9f);                // the light travels down
        Assert.True(Bright(noon.SunColor) > 0.5f);
        Assert.Equal(1f, noon.ShadowStrength, 3);

        // Sunrise and sunset are on the horizon: nothing yet, nothing any more.
        Assert.Equal(Vector3.Zero, At(app, "day", 6).SunColor);
        Assert.Equal(0f, At(app, "day", 18).ShadowStrength, 3);
        Assert.True(At(app, "day", 23).SunDirection.Y > 0);      // below the world: it would light from under
    }

    // ---- weather on the sky ----

    [Fact]
    public void AStormAtNightIsDarkerThanClear()
    {
        using var app = Boot();
        var sky = app.Records.Get<SkyRecord>(Id("day"));
        var clear = new RenderEnvironment();
        var storm = new RenderEnvironment();

        SkyRules.Apply(sky, 22, app.Records, Settled("clear"), clear);
        SkyRules.Apply(sky, 22, app.Records, Settled("storm"), storm);

        Assert.True(Bright(storm.AmbientSky) < Bright(clear.AmbientSky));
        Assert.True(Bright(storm.ClearColor) < Bright(clear.ClearColor));
        Assert.True(Bright(storm.FogColor) < Bright(clear.FogColor));
        Assert.True(storm.FogEnd < clear.FogEnd);

        // And by day the storm is still darker than a clear day, but not as dark as a clear night.
        var day = new RenderEnvironment();
        var stormyDay = new RenderEnvironment();
        SkyRules.Apply(sky, 12, app.Records, Settled("clear"), day);
        SkyRules.Apply(sky, 12, app.Records, Settled("storm"), stormyDay);
        Assert.True(Bright(stormyDay.SunColor) < Bright(day.SunColor));
        Assert.True(stormyDay.ShadowStrength < day.ShadowStrength);                 // a heavy sky, soft shadows
        Assert.True(Bright(stormyDay.AmbientSky) > Bright(clear.AmbientSky));
    }

    // The bug this fixes: weather used to write its own fog and sky colours into the environment every
    // frame, and cached the light as its baseline, so a storm at dusk froze the dusk. The sky is the
    // baseline now, re-read from the clock every tick, and weather only adjusts it.
    [Fact]
    public void AStormAtDuskKeepsDimmingWithTheDusk()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Sky = Id("day");
        clock.Hour = 16;
        clock.Scale = 3600;                                      // a game hour a second
        var weather = world.Resources.Get<Weather>();
        weather.Set(Id("storm"), 0f);
        weather.Advance(1f);
        var environment = world.Resources.Get<RenderEnvironment>();

        float previousAmbient = float.MaxValue;
        Vector3 fog16 = default;
        for (int second = 0; second < 5; second++)
        {
            for (int i = 0; i < 60; i++) world.RunFixed(1f / 60f);
            // The storm is the same throughout: what changes is the hour, and the light follows it.
            Assert.Equal(Id("storm"), weather.Showing);
            Assert.True(Bright(environment.AmbientSky) < previousAmbient, $"hour {clock.Hour}: the storm held the light still");
            previousAmbient = Bright(environment.AmbientSky);
            if (second == 0) fog16 = environment.FogColor;
        }
        Assert.NotEqual(fog16, environment.FogColor);            // the fog followed the sky, not the weather's own colour

        // Exactly the sky at that hour, scaled by the storm: nothing baked in, nothing left over.
        var sky = app.Records.Get<SkyRecord>(Id("day"));
        var expected = SkyRules.Evaluate(sky, clock.Hour);
        Assert.Equal(expected.AmbientSky * 0.5f, environment.AmbientSky);
        Assert.Equal(expected.Fog * new Vector3(0.5f, 0.5f, 0.55f), environment.FogColor);

        // When it clears the light is the sky's, all of it.
        weather.Set(Id("clear"), 0f);
        weather.Advance(1f);
        world.RunFixed(1f / 60f);
        expected = SkyRules.Evaluate(sky, clock.Hour);
        Assert.Equal(expected.AmbientSky, environment.AmbientSky);
        Assert.Equal(expected.Fog, environment.FogColor);        // the clear weather record's own green fog did not win
    }

    // `sage:clear` is all ones: under a sky it changes nothing, and a half-blended storm is halfway.
    [Fact]
    public void ClearWeatherChangesNothingUnderASky()
    {
        using var app = Boot();
        var sky = app.Records.Get<SkyRecord>(Id("day"));
        var environment = new RenderEnvironment();

        SkyRules.Apply(sky, 12, app.Records, Settled("clear"), environment);
        var s = SkyRules.Evaluate(sky, 12);
        Assert.Equal(s.SunColor, environment.SunColor);
        Assert.Equal(s.AmbientSky, environment.AmbientSky);
        Assert.Equal(s.Fog, environment.FogColor);
        Assert.Equal(s.FogEnd, environment.FogEnd);
        Assert.Equal(s.Horizon, environment.ClearColor);
        Assert.Equal(s.SunDirection, environment.SunDirection);
        Assert.Equal(s.ShadowStrength, environment.ShadowStrength);

        var half = Settled("clear");
        half.Set(Id("storm"), 2f);
        half.Advance(1f);
        SkyRules.Apply(sky, 12, app.Records, half, environment);
        Assert.Equal(s.SunColor * 0.7f, environment.SunColor);   // halfway from 1 to 0.4
        Assert.Equal(s.FogEnd * 0.7f, environment.FogEnd, 1);
    }

    // With no sky record nothing in the clock touches the environment: a world looks as it did before.
    [Fact]
    public void WithoutASkyTheLookIsUnchanged()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        Assert.True(clock.Sky.IsEmpty);
        clock.Scale = 3600;

        var environment = world.Resources.Get<RenderEnvironment>();
        var before = new RenderEnvironment();
        environment.SunColor = new Vector3(0.1f, 0.2f, 0.3f);    // a game's own light
        before.SunColor = environment.SunColor;
        for (int i = 0; i < 300; i++) world.RunFixed(1f / 60f);

        Assert.True(clock.Day > 0 || clock.Hour != 12);          // the clock ran
        Assert.Equal(before.SunColor, environment.SunColor);
        Assert.Equal(before.SunDirection, environment.SunDirection);
        Assert.Equal(before.AmbientSky, environment.AmbientSky);
        Assert.Equal(before.FogColor, environment.FogColor);
        Assert.Equal(before.FogEnd, environment.FogEnd);
        Assert.Equal(before.ClearColor, environment.ClearColor);
        Assert.Equal(1f, environment.ShadowStrength);

        // A sky named by a typo is no sky either.
        clock.Sky = Id("nonsense");
        for (int i = 0; i < 60; i++) world.RunFixed(1f / 60f);
        Assert.Equal(before.SunColor, environment.SunColor);
        Assert.Null(SkyRules.Current(app.Records, clock));

        // The legacy weather path (no sky) still replaces fog and sky colour, as WeatherTests pin.
        var legacy = new RenderEnvironment();
        WeatherRules.Apply(app.Records, Settled("clear"), legacy, new RenderEnvironment());
        Assert.Equal(new Vector3(0.3f, 0.4f, 0.2f), legacy.FogColor);
    }

    // A world with no client has no `weather` record type (it is sage.client's). Under a sky, a weather
    // named by a save or a scene reads as clear there, rather than throwing from the sky system every tick
    // (found by the 4h exit, issue 4h-7: a headless Sandbox with rain set).
    [Fact]
    public void WithoutTheClientsWeatherTypeTheWeatherReadsAsClear()
    {
        using var app = HeadlessApp.Gameplay()
            .File("data/sky.json", """
                [{ "type": "sky", "id": "day", "keys": [ { "hour": 12, "sun": [1, 0.9, 0.8], "fog": [0.5, 0.6, 0.7] } ] }]
                """)
            .Boot("server");
        Assert.Equal(0, app.Records.ErrorCount);
        Assert.Null(app.Records.TypeNameOf(typeof(WeatherRecord)));
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Sky = Id("day");
        clock.Hour = 12;
        clock.Scale = 0;
        var weather = world.Resources.Get<Weather>();
        weather.Current = weather.Target = new RecordId("sandbox", "rain");

        world.RunFixed(1f / 60f);
        var environment = world.Resources.Get<RenderEnvironment>();
        var sky = SkyRules.Evaluate(app.Records.Get<SkyRecord>(Id("day")), 12);
        Assert.Equal(sky.SunColor, environment.SunColor);
        Assert.Equal(sky.Fog, environment.FogColor);
    }

    // A scene says which sky its world is lit by and what hour it starts at.
    [Fact]
    public void ASceneChoosesTheSkyAndTheHour()
    {
        using var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<WeatherRecord>())
            .File("data/sky.json", Records)
            .File("data/scene.json", """
                [{ "type": "scene", "id": "dusk", "environment": { "sky": "sage:day", "hour": 18.5 } }]
                """)
            .Boot("scene");
        Assert.Equal(0, app.Records.ErrorCount);

        Assert.True(app.Engine.Scenes.Load(app.World, Id("dusk")));
        var clock = WorldClock.Of(app.World);
        Assert.Equal(Id("day"), clock.Sky);
        Assert.Equal(18.5, clock.Hour, 6);
    }
}
