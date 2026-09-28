#nullable enable
using System;
using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Rain, snow, and the light going out of the day (docs/design/06 §3.13, TODO F40).
//
// Weather is a blend between two records, so "what does the world look like at half past a storm" is a
// question with an arithmetic answer — which is the whole reason the numbers live in the engine and only
// the falling and the drawing are the client's.
public class WeatherTests
{
    public WeatherTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "weather", "id": "clear", "label": "clear",
           "fogColor": [0.3, 0.4, 0.2], "fogStart": 30, "fogEnd": 200,
           "skyColor": [0.3, 0.4, 0.2], "sunScale": 1, "ambientScale": 1 },

         { "type": "weather", "id": "storm", "label": "storm",
           "particles": "sage:raindrop", "rate": 800,
           "wind": [-4, 0, 2],
           "fogColor": [0.1, 0.1, 0.1], "fogStart": 10, "fogEnd": 100,
           "skyColor": [0.1, 0.1, 0.1], "sunScale": 0.5, "ambientScale": 0.5,
           "sound": "sage:rain_loop" },

         { "type": "weather", "id": "snow", "label": "snow", "particles": "sage:snowflake", "rate": 200 },

         { "type": "particle", "id": "raindrop", "texture": "textures/particle.png", "shape": "Box" },
         { "type": "particle", "id": "snowflake", "texture": "textures/particle.png", "shape": "Box" }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static RecordStore NewRecords()
    {
        return HeadlessApp.Gameplay()
            .OnRegistered(app =>
            {
                app.Records.Register<WeatherRecord>();
                app.Records.Register<ParticleRecord>();
            })
            .File("data/weather.json", Records)
            .Build().Records;
    }

    // A storm rolls in: it is not raining, then it is half raining, then it is.
    [Fact]
    public void WeatherArrivesOverTimeRatherThanBetweenFrames()
    {
        var records = NewRecords();
        var weather = new Weather { Current = Id("clear"), Target = Id("clear") };

        weather.Set(Id("storm"), seconds: 4f);
        Assert.Equal(0f, weather.Blend);
        Assert.Equal(0f, WeatherRules.RateNow(records, weather), 1);

        weather.Advance(2f);
        Assert.Equal(0.5f, weather.Blend, 2);
        Assert.Equal(400f, WeatherRules.RateNow(records, weather), 1);      // half the rain
        Assert.Equal(-2f, WeatherRules.WindNow(records, weather).X, 2);     // and half the wind

        weather.Advance(2f);
        Assert.True(weather.Settled);
        Assert.Equal(800f, WeatherRules.RateNow(records, weather), 1);
        Assert.Equal(Id("storm"), weather.Showing);
    }

    // The sky closes in with it: fog nearer, sun dimmer, everything the colour of the weather.
    [Fact]
    public void TheSkyBlendsWithTheWeather()
    {
        var records = NewRecords();
        var clearSky = new RenderEnvironment();
        var environment = new RenderEnvironment();
        var weather = new Weather { Current = Id("clear"), Target = Id("clear") };

        WeatherRules.Apply(records, weather, environment, clearSky);
        Assert.Equal(200f, environment.FogEnd, 1);
        Assert.Equal(clearSky.SunColor, environment.SunColor);

        weather.Set(Id("storm"), seconds: 2f);
        weather.Advance(1f);
        WeatherRules.Apply(records, weather, environment, clearSky);

        Assert.Equal(150f, environment.FogEnd, 1);                       // half way from 200 to 100
        Assert.Equal(20f, environment.FogStart, 1);
        Assert.Equal(clearSky.SunColor * 0.75f, environment.SunColor);   // and three-quarters of the sun

        weather.Advance(1f);
        WeatherRules.Apply(records, weather, environment, clearSky);
        Assert.Equal(100f, environment.FogEnd, 1);
        Assert.Equal(clearSky.SunColor * 0.5f, environment.SunColor);
    }

    // **The sun is scaled, not replaced**, so two storms in a row do not darken the world twice and the
    // light always comes back to what the game set up.
    [Fact]
    public void TheLightComesBackAfterTheWeatherPasses()
    {
        var records = NewRecords();
        var clearSky = new RenderEnvironment();
        var environment = new RenderEnvironment();
        var weather = new Weather { Current = Id("clear"), Target = Id("clear") };

        for (int storm = 0; storm < 3; storm++)
        {
            weather.Set(Id("storm"), 1f);
            weather.Advance(1f);
            WeatherRules.Apply(records, weather, environment, clearSky);

            weather.Set(Id("clear"), 1f);
            weather.Advance(1f);
            WeatherRules.Apply(records, weather, environment, clearSky);
        }

        Assert.Equal(clearSky.SunColor, environment.SunColor);
        Assert.Equal(clearSky.AmbientSky, environment.AmbientSky);
        Assert.Equal(200f, environment.FogEnd, 1);
    }

    // Changing your mind mid-storm starts from what the sky looks like *now*, not from where the last
    // change began: asking for sun a second into rain must not snap back to rain first.
    [Fact]
    public void ChangingItMidChangeStartsFromWhereItLooks()
    {
        var records = NewRecords();
        var weather = new Weather { Current = Id("clear"), Target = Id("clear") };

        weather.Set(Id("storm"), 10f);
        weather.Advance(1f);                    // a tenth of the way in: still mostly clear
        Assert.Equal(Id("clear"), weather.Showing);

        weather.Set(Id("snow"), 2f);
        Assert.Equal(Id("clear"), weather.Current);   // it goes on from the clear sky it still looks like
        Assert.Equal(Id("snow"), weather.Target);
        Assert.Equal(0f, weather.Blend);
    }

    // What falls changes over rather than overlapping: rain turning to snow is not both at once.
    [Fact]
    public void OneThingFallsAtATime()
    {
        var records = NewRecords();
        var weather = new Weather { Current = Id("storm"), Target = Id("storm") };
        Assert.Equal(Id("raindrop"), WeatherRules.Falling(records, weather).Particles.Id);

        weather.Set(Id("snow"), 2f);
        weather.Advance(0.5f);
        Assert.Equal(Id("raindrop"), WeatherRules.Falling(records, weather).Particles.Id);   // still mostly rain

        weather.Advance(1f);
        Assert.Equal(Id("snowflake"), WeatherRules.Falling(records, weather).Particles.Id);
    }

    // A weather id nobody wrote makes the sun come out rather than crashing the sky.
    [Fact]
    public void AMissingWeatherRecordIsAClearSky()
    {
        var records = NewRecords();
        var environment = new RenderEnvironment();
        var clearSky = new RenderEnvironment();
        var weather = new Weather { Current = Id("nonsense"), Target = Id("nonsense") };

        WeatherRules.Apply(records, weather, environment, clearSky);

        Assert.Equal(0f, WeatherRules.RateNow(records, weather), 1);
        Assert.True(WeatherRules.Falling(records, weather).Particles.IsEmpty);
        Assert.Equal(clearSky.SunColor, environment.SunColor);
    }

    // Wind pushes every particle, not only the rain: the same gale leans a campfire's embers.
    [Fact]
    public void WindPushesWhatIsAlreadyInTheAir()
    {
        var particles = new Particles { Wind = new Vector3(10f, 0f, 0f) };
        var record = new ParticleRecord
        {
            LifeMin = 5f, LifeMax = 5f, SpeedMin = 0f, SpeedMax = 0f,
            Gravity = 0f, Shape = EmitShape.Point,
        };

        using var app = HeadlessApp.Bare().Boot("wind");
        var world = app.World;

        particles.Emit(Id("ember"), record, Vector3.Zero, Vector3.UnitY, 1);
        for (int i = 0; i < 60; i++) particles.Update(world, 1f / 60f);

        Assert.True(particles.Groups[0].Position[0].X > 4f,
            $"the wind moved it to {particles.Groups[0].Position[0].X:F2} m, which is not much of a gale");
    }
}
