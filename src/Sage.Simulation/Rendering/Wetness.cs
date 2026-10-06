#nullable enable
using System;

namespace Sage.Simulation;

// Wet ground and puddles from rain (issue #311, docs/design/06 §3.13).
//
// **The world has one wetness**, `Weather.Wetness`, 0 dry to 1 soaked: it rises by the weather's `Wetting` a
// second while it rains and falls by its `Drying`, both blended as the weather changes, so a storm soaks the
// ground over half a minute and the sun takes minutes to dry it. It is the world's and saved, like the weather.
//
// **A surface shows it by where it faces and what is over it.** `Surface` is the maths the lit and terrain
// shaders do per pixel (common.fxh `Wetting` is this function, and these tests are its): how wet a surface is
// rises with how much it faces the sky (walls stay dry, slopes less wet than flat ground), and is gated by its
// *sky term*, the share of the sky a lightmapped face can see (issue #313's bake, in the lightmap's alpha), so
// a brush level's floors under its roof stay dry. A surface without a lightmap (terrain, a model) is taken to
// be under the open sky. Past half wet, flat open ground gathers puddles where a tiling noise mask
// (`PuddleMask`) is highest, growing as it gets wetter. An interior scene, a viewmodel and a material with
// `weathering` 0 show none.
//
// What the shaders do with it — darken the albedo, raise the gloss and the highlights, flatten the normal and
// reflect the sky in a puddle — is in lit.fx and terrain.fx; how much is decided here.
internal static class WetnessRules
{
    // The sky term (lightmap alpha) at which a face starts to wet, and where it is fully open. The bake keeps
    // at least 0.2 (bounce light's stand-in), and a floor inside the Sandbox hut sees about that; a roof sees ~1.
    public const float ShelteredSky = 0.55f, OpenSky = 0.85f;

    // Facing: a surface whose normal's y is below this is a wall and stays dry; at FullFacing and above (about
    // 25° from flat) it is as wet as the ground. Puddles need flatter still: from FlatStart to FlatFull.
    public const float WallFacing = 0.3f, FullFacing = 0.9f, FlatStart = 0.9f, FlatFull = 0.98f;

    // Puddles begin at this wetness and cover up to MaxPuddles of the mask's range at 1; Sharpness is how
    // quickly a puddle's edge goes from wet ground to standing water across the mask.
    public const float PuddleStart = 0.4f, MaxPuddles = 0.45f, Sharpness = 8f;

    // The puddle mask tiles every this many metres (lit.fx and terrain.fx: PUDDLE_TILE) and is this many texels
    // a side.
    public const float MaskTile = 16f;
    public const int MaskSize = 64;

    // One tick of the world's wetness.
    public static void Step(Weather weather, RecordStore records, float dt)
    {
        var (from, to, t) = WeatherRules.Blend(records, weather);
        float wetting = from.Wetting + (to.Wetting - from.Wetting) * t;
        float drying = from.Drying + (to.Drying - from.Drying) * t;
        weather.Wetness = Math.Clamp(weather.Wetness + (wetting - drying) * dt, 0f, 1f);
    }

    // The wetness the shaders are given for a world: its own, or none inside an interior scene (it has no sky).
    public static float Shown(World world)
    {
        if (Interiors.Active(world)) return 0f;
        return world.Resources.TryGet<Weather>(out var weather) && weather != null ? Math.Clamp(weather.Wetness, 0f, 1f) : 0f;
    }

    // How far up the mask the water has risen (0 none, MaxPuddles at soaked): WetParams.y.
    public static float PuddleLevel(float wetness) =>
        Saturate((wetness - PuddleStart) / (1f - PuddleStart)) * MaxPuddles;

    // How much a material lets the rain show: its `weathering`, and nothing on one that is not opaque (glass,
    // a particle, a decal over something already wet).
#pragma warning disable SAGE0130   // the surface fields are phase 4h/4n rendering API, still settling
    public static float Weathering(MaterialRecord record) =>
        record.Blend == MaterialBlend.Opaque && record.Pass != RenderPass.Transparent ? Saturate(record.Weathering) : 0f;
#pragma warning restore SAGE0130

    // How wet a surface is (x) and how much of it is puddle (y): the world's `wetness`, the surface's geometric
    // normal's y, its sky term (1 without a lightmap), the puddle mask at that point (0..1) and the material's
    // weathering. common.fxh's `Wetting` is this.
    public static (float Wet, float Puddle) Surface(float wetness, float normalY, float sky, float mask, float weathering)
    {
        float open = Saturate((sky - ShelteredSky) / (OpenSky - ShelteredSky)) * Saturate(weathering);
        float facing = Saturate((normalY - WallFacing) / (FullFacing - WallFacing));
        float wet = Saturate(wetness) * facing * open;
        float flat = Saturate((normalY - FlatStart) / (FlatFull - FlatStart));
        float puddle = Saturate((mask - (1f - PuddleLevel(Saturate(wetness)))) * Sharpness) * flat * open;
        return (wet, puddle);
    }

    private static float Saturate(float x) => x < 0f ? 0f : x > 1f ? 1f : x;
}

// The puddle mask (issue #311): a tiling value-noise picture, MaskSize texels a side, the same every run. The
// client uploads `Pixels` as the shaders' PuddleMask (sampled linearly, wrapping, at world xz / MaskTile); a
// test reads the same values with `At`.
internal static class PuddleMask
{
    private static byte[]? _pixels;

    public static byte[] Pixels => _pixels ??= Make(WetnessRules.MaskSize);

    // The mask at world (x, z), as the shader's linear, wrapping sample of it reads.
    public static float At(float x, float z)
    {
        var pixels = Pixels;
        int n = WetnessRules.MaskSize;
        float u = x / WetnessRules.MaskTile * n - 0.5f, v = z / WetnessRules.MaskTile * n - 0.5f;
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        float Texel(int px, int py) => pixels[Wrap(py, n) * n + Wrap(px, n)] / 255f;
        float top = Texel(x0, y0) + (Texel(x0 + 1, y0) - Texel(x0, y0)) * fx;
        float bottom = Texel(x0, y0 + 1) + (Texel(x0 + 1, y0 + 1) - Texel(x0, y0 + 1)) * fx;
        return top + (bottom - top) * fy;
    }

    // Two octaves of smoothed value noise on lattices that divide the picture (4 and 8 cells: puddles a few
    // metres across with ragged edges), stretched to fill 0..255.
    internal static byte[] Make(int size)
    {
        var values = new float[size * size];
        float min = float.MaxValue, max = float.MinValue;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = (float)x / size, v = (float)y / size;
                float value = Noise(u, v, 4, 0x51u) * 0.7f + Noise(u, v, 8, 0xA3u) * 0.3f;
                values[y * size + x] = value;
                min = MathF.Min(min, value);
                max = MathF.Max(max, value);
            }
        var pixels = new byte[size * size];
        float range = MathF.Max(max - min, 1e-6f);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)Math.Clamp((int)MathF.Round((values[i] - min) / range * 255f), 0, 255);
        return pixels;
    }

    // Value noise on a `cells`-square lattice over the unit square, wrapping, smoothstepped between corners.
    private static float Noise(float u, float v, int cells, uint seed)
    {
        float x = u * cells, y = v * cells;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float Corner(int cx, int cy) => Hash((uint)Wrap(cx, cells), (uint)Wrap(cy, cells), seed);
        float top = Corner(x0, y0) + (Corner(x0 + 1, y0) - Corner(x0, y0)) * fx;
        float bottom = Corner(x0, y0 + 1) + (Corner(x0 + 1, y0 + 1) - Corner(x0, y0 + 1)) * fx;
        return top + (bottom - top) * fy;
    }

    private static float Hash(uint x, uint y, uint seed)
    {
        uint h = x * 0x8DA6B343u ^ y * 0xD8163841u ^ seed * 0xCB1AB31Fu;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
        return (h & 0xFFFFFF) / (float)0xFFFFFF;
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;
}
