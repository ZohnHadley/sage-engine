#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// The sky over a day (issue 4h-2, REDESIGN §4.7): a record of keyframes by hour, and the maths that turns
// "what hour is it" into the light the world is drawn with.
//
//   { "type": "sky", "id": "day", "sunrise": 6, "sunset": 18, "keys": [
//       { "hour": 0,  "sun": [0,0,0],       "ambientSky": [0.02,0.03,0.08], "horizon": [0.02,0.03,0.06], "shadow": 0 },
//       { "hour": 12, "sun": [1,0.95,0.85], "ambientSky": [0.45,0.5,0.6],   "horizon": [0.6,0.7,0.85],   "shadow": 1 },
//       { "hour": 19, "sun": [0.9,0.4,0.2], "ambientSky": [0.15,0.12,0.2],  "horizon": [0.6,0.3,0.2],    "shadow": 0.6 } ] }
//
// **The numbers are here and the drawing is not**, like the weather's and the lights': `SkyRules.Evaluate`
// is headless, so "how dark is it at 21:00" and "does the curve close up across midnight" are questions a
// test asks (test: DuskDarkensSteadily, TheCurveWrapsPastMidnight). The client only reads what
// `SkyRules.Apply` wrote into `RenderEnvironment`.
//
// **The curve wraps.** Keys are in hours, sorted on load; between the last key of the day and the first
// of the next the colours blend across midnight, so a sky with keys at 5 and 21 still has a value at 2.
//
// **The sun is geometry.** Its direction comes from the hour, sunrise and sunset (east at sunrise,
// overhead at the middle of the day, below the world at night), not from keys. A sun below the horizon
// gives no light and casts no shadow whatever the keys say (test: TheSunBelowTheHorizonGivesNoLightAndNoShadow).
//
// A world chooses its sky with `WorldClock.Sky` (a scene's `environment.sky` sets it). With none, nothing
// here runs and the look is exactly what it was before (test: WithoutASkyTheLookIsUnchanged).
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Record("sky", Plugin = RegistrationOwners.Core)]
public sealed class SkyRecord
{
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "When the sun rises (it is on the eastern horizon)", Category = "Sun")]
    public float Sunrise = 6f;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "When the sun sets", Category = "Sun")]
    public float Sunset = 18f;
    [Property(Tooltip = "How far the sun's path leans toward the south (z), 0 is straight overhead", Category = "Sun")]
    public float Lean = 0.3f;

    [Property(Tooltip = "How fog thickens with distance: Linear (start to end) or Exp2 (soft, then closing in fast; issue 4h-5)", Category = "Fog")]
    public FogMode FogMode = FogMode.Linear;

    [Property(Min = 0.01f, Max = 1, Tooltip = "How high above the horizon fog's haze reaches (sin of elevation); 0.12 is about seven degrees", Category = "Fog")]
    public float HazeBand = SkyRules.HazeBand;
    [Property(Min = 0, Max = 1, Tooltip = "How much fog colour stays over the whole sky above the haze band: 0 leaves it clear, 1 is a fog bank to the zenith (issue 4n-16)", Category = "Fog")]
    public float HazeAbove;

    [AssetKind("texture"), Property(Tooltip = "The moon's picture, drawn as a disc whose phase follows the world's calendar (CalendarRecord.MoonCycle) and which crosses the sky opposite the sun at the full. Empty: no moon (issue 4n-16)", Category = "Moon")]
    public AssetPath Moon;
    [Property(Min = 0.1f, Max = 45, Unit = "deg", Tooltip = "The moon's angular width", Category = "Moon")]
    public float MoonSize = 5f;

    [AssetKind("texture"), Property(Tooltip = "A cloud layer: a tiling grey picture whose bright parts are cloud, thresholded by the weather's cloudCover and scrolled across the sky with the hour. Empty: no clouds (issue 4n-16)", Category = "Clouds")]
    public AssetPath Clouds;
    [Property(Min = 0.05f, Max = 20, Tooltip = "How many times the cloud picture repeats across the sky", Category = "Clouds")]
    public float CloudScale = 1.5f;
    [Property(Min = 0, Unit = "1/h", Tooltip = "How fast the clouds drift, in repeats of the picture per game hour", Category = "Clouds")]
    public float CloudSpeed = 0.05f;
    [Property(Min = 0, Max = 360, Unit = "deg", Tooltip = "Which way the clouds drift, turning from +x toward +z", Category = "Clouds")]
    public float CloudDirection;

    [Property(Min = 0, Max = 90, Unit = "deg", Tooltip = "How high the pole star is: the stars turn about an axis this far above the northern (-z) horizon, once a day (issue 4n-16)", Category = "Stars")]
    public float StarTilt = 45f;

    [Property(Tooltip = "The keyframes, by hour; any order, any number. One is a sky that never changes")]
    public List<SkyKey> Keys = new();
}

// One moment of the sky. Anything left out is what the world has by default (RenderEnvironment).
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class SkyKey
{
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour of day this key is at")]
    public float Hour;
    [Property(Tooltip = "The sun's colour at full height; it fades to nothing at the horizon")]
    public Vector3 Sun = new(0.85f, 0.82f, 0.75f);
    [Property(Tooltip = "Ambient light from above")]
    public Vector3 AmbientSky = new(0.45f, 0.47f, 0.52f);
    [Property(Tooltip = "Ambient light from below")]
    public Vector3 AmbientGround = new(0.22f, 0.20f, 0.17f);
    [Property(Tooltip = "The sky at the horizon; also the clear colour")]
    public Vector3 Horizon = new(0.333f, 0.420f, 0.184f);
    [Property(Tooltip = "The sky overhead")]
    public Vector3 Zenith = new(0.333f, 0.420f, 0.184f);
    [Property(Tooltip = "The colour things fade into with distance")]
    public Vector3 Fog = new(0.333f, 0.420f, 0.184f);
    [Property(Min = 0, Unit = "m", Tooltip = "Where fog begins")]
    public float FogStart = 30f;
    [Property(Min = 0, Unit = "m", Tooltip = "Where fog is complete")]
    public float FogEnd = 200f;
    [Property(Min = 0, Unit = "1/m", Tooltip = "Exp2 fog only: how thick it is per metre past fogStart; 0 makes it complete at fogEnd")]
    public float FogDensity;
    [Property(Min = 0, Max = 1, Tooltip = "How dark shadows are (0: none, 1: full); the sun's own height fades it too")]
    public float Shadow = 1f;
}

// What the sky is at one moment: the output of `SkyRules.Evaluate`.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public readonly record struct SkyState(
    Vector3 SunDirection,      // the way the light travels (from the sun toward the ground)
    float SunElevation,        // sin of the sun's angle above the horizon: 1 overhead, 0 on it, negative below
    Vector3 SunColor,          // already faded by the horizon: zero when the sun is down
    Vector3 AmbientSky,
    Vector3 AmbientGround,
    Vector3 Horizon,
    Vector3 Zenith,
    Vector3 Fog,
    float FogStart,
    float FogEnd,
    float ShadowStrength,      // zero when the sun is down
    float FogDensity = 0f,     // exp² only; 0: complete at FogEnd (FogMath.Density)
    float Stars = 0f);         // how much the stars show, 0 by day to 1 at night (SkyRules.StarsAt; issue 4h-5)

[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class SkyRules
{
    // How far above the horizon the sun is fully out: below `0`, nothing; by this, all of it. Dusk fades
    // over the last few degrees rather than cutting out.
    private const float Fade = 0.2f;

    // Where the sun is at this hour: the direction toward it, and sin(elevation). Sunrise to sunset is the
    // upper half of a circle; the night is the lower half, travelled over the night's own length.
    public static (Vector3 ToSun, float Elevation) SunAt(SkyRecord sky, double hour)
    {
        double day = Math.Max(0.01, sky.Sunset - sky.Sunrise);
        if (day >= 24.0) day = 23.99;
        double t = hour - sky.Sunrise;
        t -= Math.Floor(t / 24.0) * 24.0;
        double angle = t < day ? Math.PI * t / day : Math.PI + Math.PI * (t - day) / (24.0 - day);

        float y = (float)Math.Sin(angle);
        var toSun = Vector3.Normalize(new Vector3((float)Math.Cos(angle), y, sky.Lean * Math.Abs(y) + 0.0001f));
        return (toSun, toSun.Y);
    }

    // The sky at an hour (any real number: it wraps). No keys is the world's default look.
    public static SkyState Evaluate(SkyRecord sky, double hour)
    {
        hour -= Math.Floor(hour / 24.0) * 24.0;

        var (toSun, elevation) = SunAt(sky, hour);
        var (a, b, t) = Bracket(sky.Keys, hour);

        float daylight = Smooth(elevation / Fade);   // 0 at and below the horizon
        return new SkyState(
            SunDirection: -toSun,
            SunElevation: elevation,
            SunColor: Vector3.Lerp(a.Sun, b.Sun, t) * daylight,
            AmbientSky: Vector3.Lerp(a.AmbientSky, b.AmbientSky, t),
            AmbientGround: Vector3.Lerp(a.AmbientGround, b.AmbientGround, t),
            Horizon: Vector3.Lerp(a.Horizon, b.Horizon, t),
            Zenith: Vector3.Lerp(a.Zenith, b.Zenith, t),
            Fog: Vector3.Lerp(a.Fog, b.Fog, t),
            FogStart: Lerp(a.FogStart, b.FogStart, t),
            FogEnd: Lerp(a.FogEnd, b.FogEnd, t),
            ShadowStrength: Math.Clamp(Lerp(a.Shadow, b.Shadow, t), 0f, 1f) * daylight,
            FogDensity: Lerp(a.FogDensity, b.FogDensity, t),
            Stars: StarsAt(elevation));
    }

    // Which two keys an hour lies between and how far, wrapping: before the first key it is between the
    // last (yesterday's) and the first. A sky with no keys is one default key; with one, that key always.
    private static (SkyKey A, SkyKey B, float T) Bracket(List<SkyKey> keys, double hour)
    {
        if (keys.Count == 0) return (Default, Default, 0f);
        if (keys.Count == 1) return (keys[0], keys[0], 0f);

        SkyKey? before = null, after = null;       // the latest at or before the hour; the earliest after it
        SkyKey? first = null, last = null;
        foreach (var key in keys)
        {
            if (first == null || key.Hour < first.Hour) first = key;
            if (last == null || key.Hour >= last.Hour) last = key;
            if (key.Hour <= hour && (before == null || key.Hour >= before.Hour)) before = key;
            if (key.Hour > hour && (after == null || key.Hour < after.Hour)) after = key;
        }

        double from, to;
        if (before == null) { before = last!; from = before.Hour - 24.0; } else from = before.Hour;
        if (after == null) { after = first!; to = after.Hour + 24.0; } else to = after.Hour;

        double span = to - from;
        float t = span <= 1e-6 ? 0f : (float)Math.Clamp((hour - from) / span, 0.0, 1.0);
        return (before, after, t);
    }

    private static readonly SkyKey Default = new();

    // ---- What the sky pass draws (issue 4h-5) ----------------------------------------------------------
    //
    // `sky.fx` is these functions in HLSL, over the camera's view ray: a gradient from the horizon colour
    // to the zenith's, fog's haze along the horizon, and a sun disc. Stars are hashed from the direction
    // on the GPU; how *much* they show is `StarsAt`, from the sun, so "are there stars at 21:00" is a
    // number (test: TheSkyAtNineAtNightAgainstNoon).

    // How far below the horizon the sun must be (sin of its elevation) for the stars to be all out; they
    // start to show as it sets.
    public const float StarsOut = 0.15f;

    // The band above the horizon (sin of elevation) the fog's colour hazes over: at the horizon the sky is
    // the fog, so a hill fogged away meets a sky of its own colour.
    public const float HazeBand = 0.12f;

    // The sun disc's angular radius (cosines: fully inside, and its soft edge) and its brightness over the
    // sun's colour.
    public const float SunDiscInner = 0.99970f;   // ~1.4°
    public const float SunDiscOuter = 0.99955f;   // ~1.7°
    public const float SunDiscGain = 4f;

    // How much the stars show with the sun at this sin(elevation): 0 while it is up, 1 once it is
    // `StarsOut` below the horizon.
    public static float StarsAt(float sunElevation) => Smooth(-sunElevation / StarsOut);

    // The sky's gradient at a sin(elevation): the horizon colour at and below the horizon, the zenith's
    // overhead, most of the change low down, where the eye sees it.
    public static Vector3 Gradient(Vector3 horizon, Vector3 zenith, float elevation) =>
        Vector3.Lerp(horizon, zenith, MathF.Sqrt(Math.Clamp(elevation, 0f, 1f)));

    // How much fog colour covers the sky at a sin(elevation): all of it at and below the horizon, none
    // above `HazeBand`.
    public static float Haze(float elevation) => Haze(elevation, HazeBand, 0f);

    // The same with the sky's own band, and the fog colour that stays over the whole sky above it
    // (`SkyRecord.HazeAbove`, issue 4n-16): never less than `above`.
    public static float Haze(float elevation, float band, float above) =>
        MathF.Max(1f - Smooth(elevation / MathF.Max(band, 0.001f)), Math.Clamp(above, 0f, 1f));

    // How much of the sun disc covers a view direction (0 to 1). The disc is where the light comes from:
    // `sunDirection` is the way the light travels.
    public static float SunDisc(Vector3 direction, Vector3 sunDirection)
    {
        float c = Vector3.Dot(Vector3.Normalize(direction), -Vector3.Normalize(sunDirection));
        return Smooth((c - SunDiscOuter) / (SunDiscInner - SunDiscOuter));
    }

    // The sky's colour along a view direction, from what the environment says now (stars aside): what
    // `sky.fx` draws behind everything, the same maths.
    public static Vector3 ColorAt(RenderEnvironment environment, Vector3 direction)
    {
        var d = Vector3.Normalize(direction);
        var sky = Gradient(environment.ClearColor, environment.Zenith, d.Y);
        var cloud = environment.CloudCover > 0f && !environment.CloudTexture.IsEmpty ? CloudColor(environment) : Vector3.Zero;
        // Clouds need the picture: `ColorAt` (no texture here) draws them at their mean, which is what a
        // test of the colours around a cloud needs; the shader samples the picture instead.
        float cloudAlpha = cloud != Vector3.Zero ? CloudAlpha(0.5f, environment.CloudCover) * CloudFade(d.Y) : 0f;
        if (cloudAlpha > 0f) sky = Vector3.Lerp(sky, cloud, cloudAlpha);
        if (environment.Fog) sky = Vector3.Lerp(sky, environment.FogColor, Haze(d.Y, environment.HazeBand, environment.HazeAbove));
        return sky + environment.SunColor * (SunDiscGain * SunDisc(d, environment.SunDirection) * (1f - CloudBlock * cloudAlpha));
    }

    // ---- Moon, clouds, turning stars (issue 4n-16) ------------------------------------------------------
    //
    // All of it is arithmetic on the clock, the calendar and the weather, so "is the moon up at midnight at
    // the full" and "how much cloud is there in a storm" are numbers a test asks (test: SkyExtrasTests).
    // `sky.fx` repeats `MoonDisc`, `MoonLit`, `CloudAlpha` and `RotateStars` over the same values.

    // How much of the sun disc, the moon and the stars a full cloud hides.
    public const float CloudBlock = 0.9f;
    // How far across (in cloud sample) the coverage threshold softens a cloud's edge.
    private const float CloudSoftness = 0.3f;
    // Where, over the horizon (sin of elevation), the cloud layer is gone: it flattens out in the distance.
    private const float CloudHorizon = 0.25f;
    // The moon at its brightest, over its picture.
    public const float MoonGain = 1.4f;

    // How far through its cycle the moon is at a moment, in [0, 1): 0 new, 0.5 full. Continuous through the
    // day, so it does not step at midnight.
    public static double MoonAgeAt(CalendarRecord? calendar, int day, double hour) =>
        (calendar ?? CalendarRecord.Default).MoonAgeAt(day + hour / 24.0);

    // Which way the moon is, as the direction toward it. A new moon is where the sun is; each day it falls
    // a twenty-ninth of the circle behind, so a first quarter peaks at sunset and the full rises as the sun
    // sets: the sun's own path read at an earlier hour.
    public static Vector3 MoonDirection(SkyRecord sky, double hour, double moonAge) =>
        SunAt(sky, hour - moonAge * 24.0).ToSun;

    // How much of the moon disc covers a view direction (0 to 1), and where in the disc it is: `local` runs
    // -1..1 across, x to the viewer's right as they face the moon, y up. `radius` is the angular radius, in
    // radians.
    public static float MoonDisc(Vector3 direction, Vector3 toMoon, float radius, out Vector2 local)
    {
        local = Vector2.Zero;
        var d = Vector3.Normalize(direction);
        var m = Vector3.Normalize(toMoon);
        float c = Vector3.Dot(d, m);
        if (c <= 0f) return 0f;
        var right = Vector3.Cross(m, Vector3.UnitY);
        right = right.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(right);
        var up = Vector3.Cross(right, m);
        var p = d / c - m;                          // where the view ray meets the plane, from the disc's centre
        local = new Vector2(Vector3.Dot(p, right), Vector3.Dot(p, up)) / MathF.Tan(radius);
        float r = local.Length();
        return 1f - Smooth((r - 0.92f) / 0.08f);
    }

    // How much of the disc's surface at `local` the sun lights at this age: all of it at the full, none at
    // the new, the right half at the first quarter (as seen from the northern hemisphere).
    public static float MoonLit(Vector2 local, double moonAge)
    {
        float r2 = Math.Min(1f, local.LengthSquared());
        float z = MathF.Sqrt(1f - r2);
        float alpha = (float)(moonAge * 2.0 * Math.PI);
        float facing = local.X * MathF.Sin(alpha) + z * -MathF.Cos(alpha);   // the surface normal against the sun
        return Smooth(facing * 5f);
    }

    // How much of a cloud picture's sample (0 to 1) is cloud at a coverage (0 clear to 1 overcast): none of
    // it at 0, all of it at 1, the brightest parts first as coverage grows.
    public static float CloudAlpha(float sample, float cover) =>
        Smooth((sample - 1f + (1f + CloudSoftness) * Math.Clamp(cover, 0f, 1f)) / CloudSoftness);

    // How much of the cloud layer shows toward a view direction: it thins to nothing at the horizon.
    public static float CloudFade(float elevation) => Smooth(elevation / CloudHorizon);

    // Where the cloud picture is looked up for a view direction: the layer is a plane overhead, so far
    // clouds bunch toward the horizon. `scroll` is `CloudScroll`.
    public static Vector2 CloudUv(Vector3 direction, float scale, Vector2 scroll)
    {
        var d = Vector3.Normalize(direction);
        return new Vector2(d.X, d.Z) / (MathF.Max(d.Y, 0f) + 0.2f) * scale + scroll;
    }

    // The clouds' lighting: the sky's own ambient and the sun on top, so they are bright by day and gone
    // dark at night, and take the weather's dimming.
    public static Vector3 CloudColor(RenderEnvironment environment) =>
        Vector3.Min(environment.AmbientSky * 1.8f + environment.SunColor * 0.6f, new Vector3(1.2f));

    // How far the clouds have drifted (in repeats of the picture, wrapped to 0..1) after this many game
    // hours since day 0, at the sky's speed and direction.
    public static Vector2 CloudScroll(SkyRecord sky, double elapsedHours)
    {
        double rad = sky.CloudDirection * Math.PI / 180.0;
        double travel = elapsedHours * sky.CloudSpeed;
        double x = travel * Math.Cos(rad), z = travel * Math.Sin(rad);
        return new Vector2((float)(x - Math.Floor(x)), (float)(z - Math.Floor(z)));
    }

    // How much cloud the weather has now: its `CloudCover`, blended as the weather is.
    public static float CloudCoverOf(RecordStore records, Weather weather)
    {
        var (from, to, t) = WeatherRules.Blend(records, weather);
        return Math.Clamp(Lerp(from.CloudCover, to.CloudCover, t), 0f, 1f);
    }

    // The stars' turn: once round in a day, so they are where they were at the same hour tomorrow.
    public static float StarTurn(double hour)
    {
        hour -= Math.Floor(hour / 24.0) * 24.0;
        return (float)(hour / 24.0 * 2.0 * Math.PI);
    }

    // The axis the stars turn about: toward the pole, `tilt` degrees above the northern (-z) horizon.
    public static Vector3 StarAxis(float tiltDegrees)
    {
        float t = Math.Clamp(tiltDegrees, 0f, 90f) * MathF.PI / 180f;
        return new Vector3(0f, MathF.Sin(t), -MathF.Cos(t));
    }

    // A view direction in the stars' own frame (the stars are fixed in it; the sky turns round them): the
    // direction turned about the axis by minus the day's angle.
    public static Vector3 RotateStars(Vector3 direction, Vector3 axis, float turn)
    {
        var k = Vector3.Normalize(axis);
        float c = MathF.Cos(-turn), s = MathF.Sin(-turn);
        return direction * c + Vector3.Cross(k, direction) * s + k * (Vector3.Dot(k, direction) * (1f - c));
    }

    // ---- Putting the sky in the world --------------------------------------------------------------

    // The sky record the world is lit by now, or null: the world has no clock, or no sky named, or the name
    // is not a record (which reads as no sky, like a typo in a weather id reads as clear).
    public static SkyRecord? Current(RecordStore records, WorldClock? clock) =>
        clock != null && !clock.Sky.IsEmpty && records.TryGet(clock.Sky, out SkyRecord sky) ? sky : null;

    // Writes the sky, as the weather leaves it, into the environment. **Weather adjusts, it does not
    // replace** (decision 5): the sky is the baseline every frame, and weather scales its light, tints its
    // fog and sky colours and pulls its fog in. `sage:clear` is all ones, and changes nothing. The
    // baseline is recomputed from the clock each time, so weather never bakes into it: a storm at dusk
    // goes on dimming as dusk does (test: AStormAtDuskKeepsDimmingWithTheDusk).
    public static void Apply(SkyRecord sky, double hour, RecordStore records, Weather weather, RenderEnvironment environment) =>
        Apply(sky, hour, 0, null, records, weather, environment);

    // The same on a given day of a calendar, which the moon's phase and the clouds' drift are read from
    // (issue 4n-16); with no calendar the default one.
    public static void Apply(SkyRecord sky, double hour, int day, CalendarRecord? calendar, RecordStore records, Weather weather, RenderEnvironment environment)
    {
        var state = Evaluate(sky, hour);
        var (from, to, t) = WeatherRules.Blend(records, weather);

        float sun = Lerp(from.SunScale, to.SunScale, t);
        float ambient = Lerp(from.AmbientScale, to.AmbientScale, t);
        var fogTint = Vector3.Lerp(from.FogTint, to.FogTint, t);
        var skyTint = Vector3.Lerp(from.SkyTint, to.SkyTint, t);

        environment.SunDirection = state.SunDirection;
        environment.SunColor = state.SunColor * sun;
        environment.AmbientSky = state.AmbientSky * ambient;
        environment.AmbientGround = state.AmbientGround * ambient;
        environment.ClearColor = state.Horizon * skyTint;
        environment.Zenith = state.Zenith * skyTint;
        environment.FogColor = state.Fog * fogTint;
        environment.FogStart = state.FogStart * Lerp(from.FogStartScale, to.FogStartScale, t);
        environment.FogEnd = state.FogEnd * Lerp(from.FogEndScale, to.FogEndScale, t);
        environment.ShadowStrength = state.ShadowStrength * sun;   // a heavy sky casts soft shadows

        // Issue 4h-5: the sky pass draws, fog takes the sky's curve, and a heavy sky hides the stars.
        float endScale = Lerp(from.FogEndScale, to.FogEndScale, t);
        environment.DrawSky = true;
        environment.FogMode = sky.FogMode;
        environment.FogDensity = state.FogDensity > 0f ? state.FogDensity / MathF.Max(endScale, 0.01f) : 0f;
        environment.Stars = state.Stars * Math.Clamp(sun, 0f, 1f);

        // Issue 4n-16: the haze, the turning stars, the moon and the clouds. A sky that names no moon and no
        // clouds leaves their parameters at their defaults, and the pass draws what it did.
        environment.HazeBand = sky.HazeBand;
        environment.HazeAbove = sky.HazeAbove;
        environment.StarAxis = StarAxis(sky.StarTilt);
        environment.StarTurn = StarTurn(hour);

        double age = MoonAgeAt(calendar, day, hour);
        environment.MoonTexture = sky.Moon;
        environment.MoonAge = (float)age;
        environment.MoonDirection = MoonDirection(sky, hour, age);
        environment.MoonSize = sky.MoonSize * MathF.PI / 180f * 0.5f;
        // Bright at night, faint by day (the sun's daylight), and dimmed as the weather is heavy.
        float daylight = Smooth(state.SunElevation / Fade);
        environment.MoonLevel = (1f - 0.9f * daylight) * Math.Clamp(sun, 0f, 1f);

        environment.CloudTexture = sky.Clouds;
        environment.CloudCover = CloudCoverOf(records, weather);
        environment.CloudScale = sky.CloudScale;
        environment.CloudScroll = CloudScroll(sky, day * 24.0 + hour);
    }

    private static float Smooth(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

// Fixed Late phase, after the clock has moved: lights the world by its sky. Headless, so a server or a
// test sees the same environment the client draws (the client's weather system steps aside for a world
// with a sky). A world with none is left alone.
[Experimental("SAGE0130", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[System(Id, Phase.Late)]
internal sealed class SkySystem : ISystem
{
    public const string Id = "sage.world.sky";

    private readonly World _world;
    private bool _drew;   // this system turned the sky pass on: it turns it off when the sky goes

    public SkySystem(World world) => _world = world;

    public void Run(in SystemContext ctx)
    {
        var resources = _world.Resources;
        if (!resources.TryGet<RenderEnvironment>(out var environment) || environment == null) return;
        // Inside, there is no sky (issue 4g-5): the scene lit the interior when it was placed.
        if (Interiors.Active(_world)) return;
        if (resources.TryGet<WorldClock>(out var clock) && clock != null
            && resources.TryGet<RecordStore>(out var records) && records != null
            && SkyRules.Current(records, clock) is { } sky
            && resources.TryGet<Weather>(out var weather) && weather != null)
        {
            SkyRules.Apply(sky, clock.Hour, clock.Day, Calendars.Of(_world), records, weather, environment);
            if (resources.TryGet<WeatherSky>(out var overhead) && overhead != null) LightningRules.Apply(overhead, environment);
            _drew = true;
        }
        else if (_drew)
        {
            // The sky was taken away (a scene without one, a cleared clock): the pass stops drawing, and
            // the rest of the light stays where the sky left it until the game sets its own.
            environment.DrawSky = false;
            _drew = false;
        }
    }
}
