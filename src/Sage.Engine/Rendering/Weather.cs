#nullable enable
using System;
using System.Numerics;

namespace sage_engine;

// Rain, snow, and the light going out of the day (docs/design/06 §3.13, TODO F40).
//
// **Weather is one record and a blend between two of them.** A `weather` record says what falls, how
// hard the wind blows it, what colour the fog goes and how much of the sun is left; the world holds the
// one it is in, the one it is going to, and how far between them it has got. Everything else — the
// particles, the sky, the sound of rain — is read off that.
//
// The numbers are here and the drawing is not, which is what makes "what does the world look like at
// half past a storm" a question a test can ask. What the client does with the answer is spawn particles
// round the camera and write the environment resource.

[Record("weather")]
public sealed class WeatherRecord
{
    public string Label = "";

    // What falls, and how much of it a second. The effect is an ordinary `particle` record; weather is
    // just the thing that keeps asking for it, in a box that follows the camera.
    public RecordId Particles;
    public float Rate;                            // particles a second, across the whole box
    public Vector3 Volume = new(28f, 16f, 28f);   // half-extents around the camera it falls inside
    public float Ceiling = 9f;                    // how far above the camera it starts

    // Which way it is blown, in metres a second. Applied to every weather particle while it falls, so a
    // gale really does drive rain sideways.
    public Vector3 Wind;

    // The sky. Everything here is blended toward as weather changes, so a storm rolls in rather than
    // arriving between one frame and the next.
    public Vector3 FogColor = new(0.333f, 0.420f, 0.184f);
    public float FogStart = 30f;
    public float FogEnd = 200f;
    public Vector3 SkyColor = new(0.333f, 0.420f, 0.184f);
    public float SunScale = 1f;                   // how much of the sun is left: 0.4 is a heavy sky
    public float AmbientScale = 1f;

    // What it sounds like: a 2D looping sound, started when this weather takes hold and stopped when it
    // lets go (11 §3). Rain you cannot hear is a screen saver.
    public RecordId Sound;
    public float SoundVolume = 1f;

    public static readonly RecordId Clear = new("sage", "clear");
}

// What the world is doing, and what it is turning into. One per world: a world has one sky.
public sealed class Weather
{
    public RecordId Current;          // what it was
    public RecordId Target;           // what it is becoming
    public float Blend = 1f;          // 0 = entirely Current, 1 = entirely Target
    public float BlendRate = 1f;      // per second; a storm takes as long as it was asked to take

    public bool Settled => Blend >= 1f;

    // Where it is now, for a HUD or a save: the one it is closest to being.
    public RecordId Showing => Blend >= 0.5f ? Target : Current;

    public void Set(RecordId weather, float seconds)
    {
        if (weather == Target && Blend >= 1f) return;

        // Changing mid-change starts from where it *looks* now, not from where the last change began,
        // or asking for sun during a storm's first second would snap back to rain first.
        Current = Blend >= 1f ? Target : Showing;
        Target = weather;
        Blend = 0f;
        BlendRate = seconds <= 0.001f ? float.PositiveInfinity : 1f / seconds;
    }

    public void Advance(float dt)
    {
        if (Blend >= 1f) return;
        Blend = float.IsPositiveInfinity(BlendRate) ? 1f : MathF.Min(1f, Blend + BlendRate * dt);
    }
}

public static class WeatherRules
{
    // The record the world is blending *from* and *to*, and how far. Missing records read as clear,
    // because a typo in a weather id should make the sun come out, not crash the sky.
    public static (WeatherRecord From, WeatherRecord To, float T) Blend(RecordStore records, Weather weather)
    {
        var from = Find(records, weather.Current);
        var to = Find(records, weather.Target);
        return (from, to, Math.Clamp(weather.Blend, 0f, 1f));
    }

    // What the sky should look like right now. Written into `RenderEnvironment` by the client every
    // frame; asked by a test at any point in the blend.
    public static void Apply(RecordStore records, Weather weather, RenderEnvironment environment,
                             RenderEnvironment clearSky)
    {
        var (from, to, t) = Blend(records, weather);

        environment.FogColor = Vector3.Lerp(from.FogColor, to.FogColor, t);
        environment.ClearColor = Vector3.Lerp(from.SkyColor, to.SkyColor, t);
        environment.FogStart = Lerp(from.FogStart, to.FogStart, t);
        environment.FogEnd = Lerp(from.FogEnd, to.FogEnd, t);

        // The sun and the ambient are *scaled* rather than replaced: a game sets the light it wants and
        // weather only says how much of it gets through, so dusk and a storm compose instead of fighting.
        float sun = Lerp(from.SunScale, to.SunScale, t);
        float ambient = Lerp(from.AmbientScale, to.AmbientScale, t);
        environment.SunColor = clearSky.SunColor * sun;
        environment.AmbientSky = clearSky.AmbientSky * ambient;
        environment.AmbientGround = clearSky.AmbientGround * ambient;
    }

    // How many particles this frame, and how hard the wind is blowing them. Both fade with the blend, so
    // rain thins out as it stops instead of switching off.
    public static float RateNow(RecordStore records, Weather weather)
    {
        var (from, to, t) = Blend(records, weather);
        return Lerp(from.Rate, to.Rate, t);
    }

    public static Vector3 WindNow(RecordStore records, Weather weather)
    {
        var (from, to, t) = Blend(records, weather);
        return Vector3.Lerp(from.Wind, to.Wind, t);
    }

    // What falls *now*: the one whose rate dominates, because two kinds of precipitation at once is a
    // blend nobody asked for and rain turning to snow should change over, not overlap.
    public static WeatherRecord Falling(RecordStore records, Weather weather)
    {
        var (from, to, t) = Blend(records, weather);
        return t >= 0.5f ? to : from;
    }

    private static WeatherRecord Find(RecordStore records, RecordId id)
    {
        if (!id.IsEmpty && records.TryGet(id, out WeatherRecord record)) return record;
        return Fallback;
    }

    // A sky with nothing in it, used when a record is missing and as the starting weather of any world.
    public static readonly WeatherRecord Fallback = new() { Label = "clear" };

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
