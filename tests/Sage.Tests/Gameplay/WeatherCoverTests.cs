#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Weather that respects roofs, with lightning and a picker (issue #311, docs/design/06 §3.13, TODO F40 and
// F16). The simulation answers every question the client asks: is there sky over the camera
// (`WeatherSky.Exposure`), is it flashing, has the thunder arrived, what did the clock and the region draw.
public class WeatherCoverTests
{
    public WeatherCoverTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private const string Records = """
        [{ "type": "weather", "id": "clear", "label": "clear" },
         { "type": "weather", "id": "storm", "label": "storm", "particles": "sage:raindrop", "rate": 800,
           "sound": "sage:rain", "shelteredVolume": 0.2,
           "lightningRate": 600, "lightningFlash": 0.8, "thunder": "sage:thunder", "thunderVolume": 0.9 },
         { "type": "weather", "id": "snow", "label": "snow", "particles": "sage:raindrop", "rate": 100 },
         { "type": "particle", "id": "raindrop", "texture": "textures/particle.png", "shape": "Box" },

         { "type": "weather_pattern", "id": "pattern", "slotHours": 3, "transition": 10, "seed": 7, "picks": [
             { "weather": "sage:clear", "weight": 1, "from": 6, "to": 18 },
             { "weather": "sage:storm", "weight": 1, "from": 18, "to": 6 },
             { "weather": "sage:snow", "weight": 1000, "region": "north" } ] }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp Boot()
    {
        var app = HeadlessApp.Gameplay()
            .OnRegistered(a =>
            {
                a.Records.Register<WeatherRecord>();
                a.Records.Register<WeatherPatternRecord>();
                a.Records.Register<ParticleRecord>();
            })
            .File("data/weather.json", Records)
            .Boot("weather");
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static void Run(World world, float seconds)
    {
        for (int i = 0; i < (int)MathF.Round(seconds / Dt); i++) world.RunFixed(Dt);
    }

    private static ActiveCamera Camera(World world)
    {
        if (!world.Resources.TryGet<ActiveCamera>(out var camera) || camera == null)
        {
            camera = new ActiveCamera();
            world.Resources.Add(camera);
        }
        return camera;
    }

    private static Entity Roof(World world, Vector3 at)
    {
        var roof = world.Create(Transform.At(at), "roof");
        world.Add(roof, Collider.Box(new Vector3(6, 0.4f, 6)));
        return roof;
    }

    // ---- cover ----

    [Fact]
    public void ARoofOverTheCameraStopsTheDropsAndAnOpenSkyBringsThemBack()
    {
        using var app = Boot();
        var world = app.World;
        var sky = world.Resources.Get<WeatherSky>();
        var camera = Camera(world);
        Roof(world, new Vector3(0, 4, 0));

        camera.Position = new Vector3(20, 1.7f, 0);
        Run(world, 1f);
        Assert.Equal(1f, sky.Exposure);
        Assert.False(sky.Sheltered);

        camera.Position = new Vector3(0, 1.7f, 0);                 // walks in under it
        Run(world, 0.1f);
        Assert.True(sky.Sheltered);
        Assert.InRange(sky.Exposure, 0.01f, 0.99f);                // a fade, not a switch
        Run(world, 1f);
        Assert.Equal(0f, sky.Exposure);

        camera.Position = new Vector3(20, 1.7f, 0);                // and out again
        Run(world, 1f);
        Assert.Equal(1f, sky.Exposure);
    }

    [Fact]
    public void WhatMovesIsNoRoof()
    {
        using var app = Boot();
        var world = app.World;
        var sky = world.Resources.Get<WeatherSky>();
        Camera(world).Position = new Vector3(0, 1.7f, 0);

        // A crate that falls and a character over the camera's head keep no rain off.
        var crate = world.Create(Transform.At(new Vector3(0, 3, 0)), "crate");
        world.Add(crate, Collider.Box(new Vector3(1, 1, 1)));
        world.Add(crate, RigidBody.Dynamic(1f));
        var upstairs = world.Create(Transform.At(new Vector3(0, 6, 0)), "upstairs");
        world.AddCharacter(upstairs, 1);
        world.RunFixed(Dt);   // bodies are made
        Assert.False(WeatherCover.Sheltered(world, new Vector3(0, 1.7f, 0)));

        Roof(world, new Vector3(0, 9, 0));                          // behind both of them, a real one
        world.RunFixed(Dt);
        Assert.True(WeatherCover.Sheltered(world, new Vector3(0, 1.7f, 0)));
        Run(world, 1f);
        Assert.Equal(0f, sky.Exposure);
    }

    // The done criterion, on the Sandbox itself: the hut's roof is a brush level, so the drops stop and the
    // rain is muffled when the player walks in, and both come back outside.
    [Fact]
    public void WalkingIntoTheSandboxHutStopsTheRain()
    {
        string game = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
        using var app = HeadlessApp.ForGame(game, new global::Sandbox.SandboxModule()).WithEngineContent()
            .OnRegistered(a =>
            {
                a.Records.Register<WeatherRecord>();
                a.Records.Register<WeatherPatternRecord>();
            })
            .Boot();
        var world = app.World;
        var sky = world.Resources.Get<WeatherSky>();
        world.Resources.Get<Weather>().Set(new RecordId("sandbox", "rain"), 0f);

        void Step(int n = 1) { for (int i = 0; i < n; i++) { world.RunFixed(Dt); world.RunFrame(Dt, 1f); } }
        Step(3);

        var player = Assert.Single(world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList());
        var terrain = world.Resources.Get<Terrain>();
        var lamp = world.Query<PointLight, FromMap>().Entities.ToEntityList().First();   // a lamp the hut placed
        var hut = world.Get<GlobalTransform>(lamp).Current.Position;

        // Out in the open, 30 m from the hut's door: all the rain there is.
        var outside = hut + new Vector3(30f, 0f, 0f);
        world.Teleport(player, Transform.At(outside with { Y = terrain.HeightAt(outside.X, outside.Z) + 1f }));
        Step(90);
        Assert.False(sky.Sheltered);
        Assert.Equal(1f, sky.Exposure);

        // Inside, under the roof.
        world.Teleport(player, Transform.At(hut with { Y = hut.Y - 1f }));
        Step(90);
        Assert.True(sky.Sheltered, "the hut's roof is overhead");
        Assert.Equal(0f, sky.Exposure);

        world.Teleport(player, Transform.At(outside with { Y = terrain.HeightAt(outside.X, outside.Z) + 1f }));
        Step(90);
        Assert.Equal(1f, sky.Exposure);
    }

    // ---- lightning ----

    [Fact]
    public void LightningFlashesTheSkyAndThunderFollowsLater()
    {
        using var app = Boot();
        var world = app.World;
        var sky = world.Resources.Get<WeatherSky>();
        var environment = world.Resources.Get<RenderEnvironment>();
        Camera(world).Position = new Vector3(0, 1.7f, 0);
        world.Resources.Get<Weather>().Set(Id("storm"), 0f);

        for (int i = 0; i < 1200 && sky.Strikes == 0; i++) world.RunFixed(Dt);
        Assert.True(sky.Strikes > 0, "a storm strikes");
    }

    [Fact]
    public void AStrikeFlashesBrightAtOnceAndDiesAwayAndItsThunderComesLater()
    {
        var record = new WeatherRecord { LightningFlash = 0.8f, Thunder = new RecordRef<SoundRecord>(Id("thunder")), ThunderVolume = 0.9f };
        var sky = new WeatherSky();

        LightningRules.Strike(sky, record);
        LightningRules.Step(sky, record, 0f, Dt);
        Assert.InRange(sky.Flash, 0.6f, 0.8f + 1e-4f);              // brightest at once
        Assert.False(sky.TakeThunder(out _), "the thunder has not arrived with the light");

        float peakAfter = 0f;
        for (int i = 0; i < 120; i++)
        {
            LightningRules.Step(sky, record, 0f, Dt);
            if (i > 20) peakAfter = MathF.Max(peakAfter, sky.Flash);
        }
        Assert.Equal(0f, sky.Flash);                                // gone in a second or two
        Assert.True(peakAfter > 0.1f, "with a flicker: the second flash is dimmer, not absent");

        // Thunder arrives some seconds after (distance over 343 m/s: 0.6 to 7.3 s), once per strike.
        int thunder = 0;
        for (int i = 0; i < 60 * 12; i++)
        {
            LightningRules.Step(sky, record, 0f, Dt);
            while (sky.TakeThunder(out float volume)) { thunder++; Assert.Equal(0.9f, volume, 3); }
        }
        Assert.Equal(1, thunder);
        Assert.False(sky.TakeThunder(out _));
    }

    [Fact]
    public void TheFlashLightsTheEnvironmentOutdoorsAndNotUnderARoof()
    {
        var record = new WeatherRecord { LightningRate = 60, LightningFlash = 1f };
        var open = new WeatherSky();
        var covered = new WeatherSky { Exposure = 0f };
        LightningRules.Strike(open, record);
        LightningRules.Strike(covered, record);
        LightningRules.Step(open, record, 0f, Dt);
        LightningRules.Step(covered, record, 0f, Dt);

        var dark = new RenderEnvironment();
        var outside = new RenderEnvironment();
        var inside = new RenderEnvironment();
        LightningRules.Apply(open, outside);
        LightningRules.Apply(covered, inside);

        Assert.True(outside.AmbientSky.X > dark.AmbientSky.X + 0.5f);
        Assert.True(outside.ClearColor.Z > dark.ClearColor.Z);
        Assert.Equal(dark.AmbientSky, inside.AmbientSky);
    }

    [Fact]
    public void TheSameSeedStrikesTheSameWayAndAClearSkyNeverStrikes()
    {
        using var app = Boot();
        var records = app.Records;
        var storm = records.Get<WeatherRecord>(Id("storm"));

        int Count(WeatherRecord record, int seconds)
        {
            var sky = new WeatherSky();
            for (int i = 0; i < seconds * 60; i++) LightningRules.Step(sky, record, record.LightningRate, Dt);
            return sky.Strikes;
        }

        Assert.Equal(Count(storm, 20), Count(storm, 20));
        Assert.InRange(Count(storm, 20), 100, 300);            // 600 a minute for 20 s: about 200
        Assert.Equal(0, Count(records.Get<WeatherRecord>(Id("clear")), 60));
    }

    // ---- the picker ----

    [Fact]
    public void TheClockAndTheRegionPickTheWeather()
    {
        using var app = Boot();
        var pattern = app.Records.Get<WeatherPatternRecord>(Id("pattern"));

        // By hour: slot 3 begins at 9:00 (only clear applies), slot 7 at 21:00 (only the storm).
        for (long day = 0; day < 5; day++)
        {
            Assert.Equal(Id("clear"), WeatherPicker.Pick(pattern, day * 8 + 3, ""));
            Assert.Equal(Id("storm"), WeatherPicker.Pick(pattern, day * 8 + 7, ""));
        }

        // By region: the north draws snow (it is nearly all the weight), and only there.
        Assert.Equal(Id("snow"), WeatherPicker.Pick(pattern, 3, "north"));
        Assert.Equal(Id("clear"), WeatherPicker.Pick(pattern, 3, "south"));

        // Deterministic, and across a day with both applying it is not always the same one.
        Assert.Equal(WeatherPicker.Pick(pattern, 0, ""), WeatherPicker.Pick(pattern, 0, ""));
        var seen = Enumerable.Range(0, 80).Select(i => WeatherPicker.Pick(pattern, i * 8 + 5, "")).Distinct().ToList();
        Assert.True(seen.Count >= 1);
    }

    [Fact]
    public void TheWorldChangesItsWeatherAsTheClockTurnsAndKeepsItWhenNothingApplies()
    {
        using var app = Boot();
        var world = app.World;
        var weather = world.Resources.Get<Weather>();
        var clock = WorldClock.Of(world);

        // No pattern: nothing is drawn, whatever the hour.
        clock.Scale = 0;
        clock.Hour = 9.5;
        weather.Set(Id("snow"), 0f);
        Run(world, 0.5f);
        Assert.Equal(Id("snow"), weather.Target);

        weather.Pattern = Id("pattern");
        Run(world, 0.1f);
        Assert.Equal(Id("clear"), weather.Target);                 // 9:30 is in the day's slot
        Assert.False(weather.Settled);                             // and it takes ten seconds to arrive
        Run(world, 11f);
        Assert.True(weather.Settled);
        long picked = weather.PickedSlot;
        Assert.Equal(3, picked);

        weather.Set(Id("snow"), 0f);                               // a game's own change stands until the slot ends
        Run(world, 1f);
        Assert.Equal(Id("snow"), weather.Target);

        clock.Hour = 21.5;
        Run(world, 0.1f);
        Assert.Equal(Id("storm"), weather.Target);
        Assert.Equal(7, weather.PickedSlot);

        weather.Region = "north";
        weather.PickedSlot = -1;
        Run(world, 0.1f);
        Assert.Equal(Id("snow"), weather.Target);
    }

    // A save from before this had none of the picker's fields: they come back as "no pattern", and a
    // weather with no lightning in its record never strikes (the golden saves in tests/.../Saves load too).
    [Fact]
    public void AnOldWeatherHasNoPatternAndNoRegion()
    {
        var weather = System.Text.Json.JsonSerializer.Deserialize<Weather>(
            """{ "Current": "sage:clear", "Target": "sage:clear", "Blend": 1, "BlendRate": 1 }""")!;
        Assert.True(weather.Pattern.IsEmpty);
        Assert.Equal("", weather.Region);
        Assert.Equal(-1, weather.PickedSlot);
    }
}
