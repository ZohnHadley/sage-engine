using System;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Moon, clouds and turning stars (issue 4n-16): the headless side of `sky.fx`. The moon's place and phase come
// from the clock and the calendar, the clouds' cover from the weather blend and their drift from the hour, and
// the stars' turn from the hour; the drawing needs a GPU and is checked by the smoke run.
public class SkyExtrasTests
{
    public SkyExtrasTests() { _ = TestEnv.UserRoot; }

    private const string Records = """
        [{ "type": "sky", "id": "plain", "sunrise": 6, "sunset": 18, "keys": [ { "hour": 12, "horizon": [0.6, 0.7, 0.85], "zenith": [0.2, 0.4, 0.8] } ] },
         { "type": "sky", "id": "night", "sunrise": 6, "sunset": 18, "moon": "textures/moon.png", "moonSize": 6,
           "clouds": "textures/clouds.png", "cloudSpeed": 0.1, "cloudDirection": 90, "hazeAbove": 0.2, "hazeBand": 0.3, "starTilt": 30,
           "keys": [ { "hour": 12, "horizon": [0.6, 0.7, 0.85], "zenith": [0.2, 0.4, 0.8] } ] },
         { "type": "calendar", "id": "lunar", "moonCycle": 30, "moonStart": 15 },
         { "type": "weather", "id": "clear", "label": "clear", "cloudCover": 0.2 },
         { "type": "weather", "id": "overcast", "label": "overcast", "cloudCover": 1.0, "sunScale": 0.5 }]
        """;

    private static RecordId Id(string name) => new("sage", name);

    private static HeadlessApp Boot()
    {
        using var log = new CaptureSink();
        var app = HeadlessApp.Gameplay()
            .OnRegistered(a => a.Records.Register<WeatherRecord>())
            .File("data/sky.json", Records)
            .Boot("sky");
        Assert.Equal(0, app.Records.ErrorCount);
        return app;
    }

    private static Weather Settled(string id) => new() { Current = Id(id), Target = Id(id) };

    // ---- the moon ----

    [Fact]
    public void TheMoonFollowsTheCalendarThroughItsPhases()
    {
        using var app = Boot();
        var calendar = app.Records.Get<CalendarRecord>(Id("lunar"));   // 30-day cycle, day 0 is 15 days in: full

        Assert.Equal(0.5, SkyRules.MoonAgeAt(calendar, 0, 0), 6);
        Assert.Equal(0.0, SkyRules.MoonAgeAt(calendar, 15, 0), 6);
        // Within a day the age creeps forward (continuous, so the moon does not jump at midnight).
        Assert.True(SkyRules.MoonAgeAt(calendar, 3, 18) > SkyRules.MoonAgeAt(calendar, 3, 6));

        // The same phase the calendar names for the day.
        Assert.Equal(4, calendar.MoonPhaseOf(0));
        Assert.Equal(0, calendar.MoonPhaseOf(15));
    }

    [Fact]
    public void ANewMoonRidesWithTheSunAndAFullOneOpposesIt()
    {
        using var app = Boot();
        var sky = app.Records.Get<SkyRecord>(Id("night"));

        foreach (double hour in new[] { 3.0, 9.0, 12.0, 15.0, 21.0 })
        {
            var (sun, _) = SkyRules.SunAt(sky, hour);
            var newMoon = SkyRules.MoonDirection(sky, hour, 0.0);
            var fullMoon = SkyRules.MoonDirection(sky, hour, 0.5);
            Assert.True(Vector3.Dot(newMoon, sun) > 0.999f, $"new at {hour}");
            Assert.True(Vector3.Dot(fullMoon, sun) < -0.7f && fullMoon.Y * sun.Y < 0f, $"full at {hour}");
        }

        // The full moon is up at midnight; the new moon is not.
        Assert.True(SkyRules.MoonDirection(sky, 0, 0.5).Y > 0.9f);
        Assert.True(SkyRules.MoonDirection(sky, 0, 0.0).Y < -0.9f);
        // A first quarter peaks at sunset: it is on the meridian at 18:00.
        Assert.True(SkyRules.MoonDirection(sky, 18, 0.25).Y > 0.9f);
    }

    [Fact]
    public void ThePhaseLightsTheDiscFromTheRightGrowingToFull()
    {
        var right = new Vector2(0.6f, 0f);
        var left = new Vector2(-0.6f, 0f);
        var middle = Vector2.Zero;

        // New: dark everywhere. Full: lit everywhere. First quarter: the right half only.
        Assert.Equal(0f, SkyRules.MoonLit(middle, 0.0));
        Assert.Equal(1f, SkyRules.MoonLit(middle, 0.5));
        Assert.Equal(1f, SkyRules.MoonLit(left, 0.5));
        Assert.True(SkyRules.MoonLit(right, 0.25) > 0.9f);
        Assert.True(SkyRules.MoonLit(left, 0.25) < 0.01f);
        // Waning: the left half.
        Assert.True(SkyRules.MoonLit(left, 0.75) > 0.9f);
        Assert.True(SkyRules.MoonLit(right, 0.75) < 0.01f);
        // A crescent lights less of the disc than a gibbous moon.
        Assert.True(SkyRules.MoonLit(middle, 0.1) < SkyRules.MoonLit(middle, 0.4));
    }

    [Fact]
    public void TheMoonDiscCoversOnlyItsOwnAngle()
    {
        var toMoon = Vector3.Normalize(new Vector3(0.3f, 0.7f, -0.4f));
        float radius = 3f * MathF.PI / 180f;

        Assert.Equal(1f, SkyRules.MoonDisc(toMoon, toMoon, radius, out var centre));
        Assert.True(centre.Length() < 1e-4f);

        var right = Vector3.Normalize(Vector3.Cross(toMoon, Vector3.UnitY));
        var edge = Vector3.Normalize(toMoon + right * MathF.Tan(radius * 0.5f));
        Assert.Equal(1f, SkyRules.MoonDisc(edge, toMoon, radius, out var half));
        Assert.Equal(0.5f, half.X, 2);
        Assert.True(half.Y < 1e-3f);

        var off = Vector3.Normalize(toMoon + right * 0.2f);
        Assert.Equal(0f, SkyRules.MoonDisc(off, toMoon, radius, out _));
        Assert.Equal(0f, SkyRules.MoonDisc(-toMoon, toMoon, radius, out _));
    }

    [Fact]
    public void TheSkySystemWritesTheMoonFromTheClockAndCalendar()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        var environment = world.Resources.Get<RenderEnvironment>();
        clock.Sky = Id("night");
        clock.Calendar = Id("lunar");
        clock.Scale = 0;
        clock.Hour = 0;

        world.RunFixed(1f / 60f);
        Assert.Equal("textures/moon.png", environment.MoonTexture.Path.ToString());
        Assert.Equal(0.5f, environment.MoonAge, 3);          // the full moon, at midnight: up
        Assert.True(environment.MoonDirection.Y > 0.9f);
        Assert.True(environment.MoonLevel > 0.99f);
        Assert.Equal(6f * MathF.PI / 360f, environment.MoonSize, 4);

        // By noon it is faint (the daylight hides it) and, the next new moon, below the world.
        clock.Hour = 12;
        world.RunFixed(1f / 60f);
        Assert.True(environment.MoonLevel < 0.2f);

        clock.Day = 15;
        clock.Hour = 0;
        world.RunFixed(1f / 60f);
        Assert.True(environment.MoonAge < 0.01f);
        Assert.True(environment.MoonDirection.Y < -0.9f);
    }

    // ---- the clouds ----

    [Fact]
    public void CloudCoverFollowsTheWeatherAndItsBlend()
    {
        using var app = Boot();
        var sky = app.Records.Get<SkyRecord>(Id("night"));

        var clear = new RenderEnvironment();
        var overcast = new RenderEnvironment();
        SkyRules.Apply(sky, 12, app.Records, Settled("clear"), clear);
        SkyRules.Apply(sky, 12, app.Records, Settled("overcast"), overcast);
        Assert.Equal(0.2f, clear.CloudCover, 4);
        Assert.Equal(1f, overcast.CloudCover, 4);

        // Half way through the change it is half way between.
        var mid = new RenderEnvironment();
        var weather = new Weather { Current = Id("clear"), Target = Id("overcast"), Blend = 0.5f };
        SkyRules.Apply(sky, 12, app.Records, weather, mid);
        Assert.Equal(0.6f, mid.CloudCover, 4);
        Assert.Equal("textures/clouds.png", clear.CloudTexture.Path.ToString());

        // More cover is more cloud from the same picture; none is none, whatever the picture.
        foreach (float sample in new[] { 0.2f, 0.5f, 0.8f })
        {
            Assert.Equal(0f, SkyRules.CloudAlpha(sample, 0f));
            Assert.True(SkyRules.CloudAlpha(sample, 0.7f) >= SkyRules.CloudAlpha(sample, 0.4f));
        }
        Assert.Equal(1f, SkyRules.CloudAlpha(0.3f, 1f));
        Assert.True(SkyRules.CloudAlpha(0.9f, 0.3f) > SkyRules.CloudAlpha(0.3f, 0.3f));
        Assert.Equal(0f, SkyRules.CloudFade(0f));
        Assert.Equal(1f, SkyRules.CloudFade(0.5f));
    }

    [Fact]
    public void CloudsDriftWithTheHourAndWrap()
    {
        using var app = Boot();
        var sky = app.Records.Get<SkyRecord>(Id("night"));   // 0.1 repeat an hour, toward +z

        var a = SkyRules.CloudScroll(sky, 0);
        var b = SkyRules.CloudScroll(sky, 5);
        Assert.Equal(0f, a.X, 4);
        Assert.Equal(0f, a.Y, 4);
        Assert.Equal(0f, b.X, 3);
        Assert.Equal(0.5f, b.Y, 3);
        // Ten hours is a whole repeat: back where it began.
        Assert.Equal(0f, SkyRules.CloudScroll(sky, 10).Y, 3);
        Assert.True(SkyRules.CloudScroll(sky, 123456.25).Y is >= 0f and < 1f);

        // The sky system writes it from the clock: day 1, 03:00 is hour 27.
        var world = app.World;
        var clock = WorldClock.Of(world);
        clock.Sky = Id("night");
        clock.Scale = 0;
        clock.Day = 1;
        clock.Hour = 3;
        world.RunFixed(1f / 60f);
        Assert.Equal(0.7f, world.Resources.Get<RenderEnvironment>().CloudScroll.Y, 3);
    }

    [Fact]
    public void CloudsAreBrightByDayAndDarkAtNightAndHideTheSun()
    {
        var day = new RenderEnvironment { AmbientSky = new Vector3(0.45f, 0.47f, 0.52f), SunColor = new Vector3(0.85f, 0.82f, 0.75f) };
        var night = new RenderEnvironment { AmbientSky = new Vector3(0.03f), SunColor = Vector3.Zero };
        Assert.True(SkyRules.CloudColor(day).Y > 8f * SkyRules.CloudColor(night).Y);

        // A named picture with cover colours the sky and dims the sun disc; no picture, no change.
        var plain = new RenderEnvironment { DrawSky = true, SunColor = Vector3.One };
        var up = Vector3.Normalize(new Vector3(0.1f, 1f, 0f));
        var before = SkyRules.ColorAt(plain, up);
        var cloudy = new RenderEnvironment { DrawSky = true, SunColor = Vector3.One, CloudCover = 1f };
        Assert.Equal(before, SkyRules.ColorAt(cloudy, up));      // no CloudTexture
        cloudy.CloudTexture = AssetPath.Intern("textures/clouds.png");
        Assert.NotEqual(before, SkyRules.ColorAt(cloudy, up));
    }

    // ---- the stars ----

    [Fact]
    public void TheStarsTurnOnceADayAboutTheAxis()
    {
        Assert.Equal(0f, SkyRules.StarTurn(0));
        Assert.Equal(MathF.PI / 2f, SkyRules.StarTurn(6), 5);
        Assert.Equal(MathF.PI, SkyRules.StarTurn(12), 5);
        Assert.Equal(SkyRules.StarTurn(3), SkyRules.StarTurn(27), 5);   // tomorrow's 03:00 is today's

        var axis = SkyRules.StarAxis(30);
        Assert.Equal(1f, axis.Length(), 5);
        Assert.Equal(MathF.Sin(MathF.PI / 6f), axis.Y, 5);
        Assert.True(axis.Z < 0f);                                // the pole is north

        // The pole stays put; anything else goes round and comes back after a day.
        var star = Vector3.Normalize(new Vector3(0.5f, 0.4f, 0.3f));
        Assert.True(Vector3.Distance(axis, SkyRules.RotateStars(axis, axis, SkyRules.StarTurn(7))) < 1e-5f);
        Assert.True(Vector3.Distance(star, SkyRules.RotateStars(star, axis, SkyRules.StarTurn(0))) < 1e-5f);
        Assert.True(Vector3.Distance(star, SkyRules.RotateStars(star, axis, SkyRules.StarTurn(24))) < 1e-5f);
        var later = SkyRules.RotateStars(star, axis, SkyRules.StarTurn(6));
        Assert.True(Vector3.Distance(star, later) > 0.3f);
        Assert.Equal(Vector3.Dot(star, axis), Vector3.Dot(later, axis), 4);   // the same height about the pole
        Assert.Equal(1f, later.Length(), 5);
        // Twelve hours is half a turn: the part of the sky at the axis' side goes to the other.
        var perp = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitX));
        Assert.True(Vector3.Distance(perp, -SkyRules.RotateStars(perp, axis, SkyRules.StarTurn(12))) < 1e-4f);
    }

    [Fact]
    public void TheSkySystemTurnsTheStarsWithTheClock()
    {
        using var app = Boot();
        var world = app.World;
        var clock = WorldClock.Of(world);
        var environment = world.Resources.Get<RenderEnvironment>();
        clock.Sky = Id("night");
        clock.Scale = 0;

        clock.Hour = 3;
        world.RunFixed(1f / 60f);
        float early = environment.StarTurn;
        clock.Hour = 9;
        world.RunFixed(1f / 60f);
        Assert.Equal(early + MathF.PI / 2f, environment.StarTurn, 4);
        Assert.Equal(SkyRules.StarAxis(30).Y, environment.StarAxis.Y, 5);
    }

    // ---- fog above the haze band ----

    [Fact]
    public void FogStaysOverTheSkyAboveTheHazeBand()
    {
        // The default: haze to 0.12, nothing above, as before.
        Assert.Equal(1f, SkyRules.Haze(0f));
        Assert.Equal(0f, SkyRules.Haze(0.5f));
        Assert.Equal(SkyRules.Haze(0.05f), SkyRules.Haze(0.05f, SkyRules.HazeBand, 0f));

        // A taller band reaches higher; a floor keeps fog all the way up.
        Assert.True(SkyRules.Haze(0.2f, 0.3f, 0f) > SkyRules.Haze(0.2f));
        Assert.Equal(0.25f, SkyRules.Haze(1f, 0.12f, 0.25f), 5);
        Assert.Equal(1f, SkyRules.Haze(-0.2f, 0.12f, 0.25f));

        using var app = Boot();
        var plain = new RenderEnvironment();
        var hazy = new RenderEnvironment();
        SkyRules.Apply(app.Records.Get<SkyRecord>(Id("plain")), 12, app.Records, Settled("clear"), plain);
        SkyRules.Apply(app.Records.Get<SkyRecord>(Id("night")), 12, app.Records, Settled("clear"), hazy);
        Assert.Equal(0.12f, plain.HazeBand);
        Assert.Equal(0f, plain.HazeAbove);
        Assert.Equal(0.3f, hazy.HazeBand);
        Assert.Equal(0.2f, hazy.HazeAbove);

        var up = Vector3.UnitY;
        Assert.Equal(plain.Zenith, SkyRules.ColorAt(plain, up));
        Assert.NotEqual(hazy.Zenith, SkyRules.ColorAt(hazy, up));
    }

    // ---- nothing named, nothing changed ----

    [Fact]
    public void ASkyThatNamesNoMoonOrCloudsLooksAsItDid()
    {
        using var app = Boot();
        var sky = app.Records.Get<SkyRecord>(Id("plain"));
        var env = new RenderEnvironment();
        SkyRules.Apply(sky, 12, app.Records, Settled("overcast"), env);

        Assert.True(env.MoonTexture.IsEmpty);
        Assert.True(env.CloudTexture.IsEmpty);
        Assert.Equal(0.12f, env.HazeBand);

        // The noon colour in every direction is the gradient and the haze, whatever the weather's cover.
        foreach (var d in new[] { Vector3.UnitY, Vector3.Normalize(new Vector3(1, 0.3f, 0)), Vector3.Normalize(new Vector3(1, 0, 0.2f)) })
        {
            var expected = SkyRules.Gradient(env.ClearColor, env.Zenith, d.Y);
            if (env.Fog) expected = Vector3.Lerp(expected, env.FogColor, SkyRules.Haze(d.Y));
            expected += env.SunColor * (SkyRules.SunDiscGain * SkyRules.SunDisc(d, env.SunDirection));
            Assert.Equal(expected, SkyRules.ColorAt(env, d));
        }
    }
}
