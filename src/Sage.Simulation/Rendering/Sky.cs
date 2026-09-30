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
    float ShadowStrength);     // zero when the sun is down

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
            ShadowStrength: Math.Clamp(Lerp(a.Shadow, b.Shadow, t), 0f, 1f) * daylight);
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
    public static void Apply(SkyRecord sky, double hour, RecordStore records, Weather weather, RenderEnvironment environment)
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

    public SkySystem(World world) => _world = world;

    public void Run(in SystemContext ctx)
    {
        var resources = _world.Resources;
        if (!resources.TryGet<WorldClock>(out var clock) || clock == null) return;
        if (!resources.TryGet<RecordStore>(out var records) || records == null) return;
        if (SkyRules.Current(records, clock) is not { } sky) return;
        if (!resources.TryGet<Weather>(out var weather) || weather == null) return;
        if (!resources.TryGet<RenderEnvironment>(out var environment) || environment == null) return;

        SkyRules.Apply(sky, clock.Hour, records, weather, environment);
    }
}
