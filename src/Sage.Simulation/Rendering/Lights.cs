#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Point lights (docs/design/06 §3.9, 07 §3.5; TODO F2's last leftover).
//
// **Why now.** The engine got interiors (F16) and a room with a roof is lit by a sun it cannot see: the
// inside of the Sandbox's hut was a uniform dark grey box, and no amount of ambient fixes that without
// flattening the outdoors too. A light you can put in a room is the smallest thing that makes an
// interior look like a place.
//
// Forward lighting, four lights at a time, chosen per object. That ceiling is not a limitation of the
// idea but of the shader model DesktopGL gives us (07 §2) — and four is enough for a room, which is what
// this is for. Lightmaps (HL1's answer, and the right one for a big level) are still later.
[Component("sage:point_light")]
public struct PointLight : IComponent
{
    [Property(Min = 0, Tooltip = "Linear RGB; 1 1 1 is white")]
    public Vector3 Colour;      // linear rgb; 1,1,1 is white
    [Property(Min = 0, Unit = "m", Tooltip = "Where the light fades to nothing")]
    public float Range;         // metres to where it fades to nothing
    [Property(Min = 0, Tooltip = "Multiplies the colour; 1 is a lamp")]
    public float Intensity;     // multiplies the colour; 1 is "a lamp"
    // Switched off (issue 4h-7): it gives no light until a TurnOn or Toggle reaches it (the inputs are the
    // lights plugin's, sage.gameplay.lights). Saved, so a lamp lit at dusk is lit after a load; a save from
    // before it has no such field and loads as on, which is what every light was.
    [Property(Tooltip = "Switched off: no light until TurnOn or Toggle (entity I/O) reaches it")]
    public bool Off;
    // Baked (issue #313): a level with a lightmap bakes it in, shadows and all, and its lightmapped faces
    // leave it out of their four; everything else (props, creatures, doors) is still lit by it. It cannot
    // switch or flicker as far as those faces are concerned: it is in the texture.
    [Property(Tooltip = "Static: baked into the lightmap of the level that places it (with shadows); still lights what is not lightmapped")]
    public bool Baked;

    // A flicker (issue #314): a Quake light style — a string of letters a..z read at `PatternRate` letters a
    // second, `a` dark, `m` the light as set, `z` about double — or one of `LightStyles`' presets by name
    // ("torch", "candle", "flicker", "fluorescent"...). Empty is steady. Evaluated from the world's
    // simulated seconds (`WorldTime.Scaled`, which saves), so every run of a world flickers the same and a
    // load picks the pattern up where it was. Set by the `SetPattern` input as well as by data.
    [Property(Tooltip = "Flicker: Quake-style letters a..z (m = as set, a = dark, z = double) or a preset (torch, candle, flicker, pulse, strobe, fluorescent...); empty = steady")]
    public string? Pattern;
    [Property(Min = 0, Unit = "1/s", Tooltip = "Letters of the pattern a second; 0 = 10, Quake's")]
    public float PatternRate;

    // A spot light (issue #314): the half-angle of its cone in degrees around the entity's forward (-Z);
    // 0 lights all round, as a point light does. `InnerCone` is where it reaches full strength; 0 (or one
    // not inside the cone) is three quarters of the cone.
    [Property(Min = 0, Max = 179, Unit = "deg", Tooltip = "Spot light: the cone's half-angle around the entity's forward; 0 = all round (a point light)")]
    public float Cone;
    [Property(Min = 0, Max = 179, Unit = "deg", Tooltip = "Spot light: full strength inside this half-angle, fading to the cone's edge; 0 = three quarters of the cone")]
    public float InnerCone;

    // Casts shadows (issue #315): the renderer gives it a depth map — a spot's cone, or a cube all round —
    // when it is among the `r_shadow_lamps` nearest that ask (LampShadows). Off by default: a lamp that
    // does not ask costs no map. A save from before it loads as off.
    [Property(Tooltip = "Casts shadows: one of the r_shadow_lamps nearest lamps that ask gets a depth map (a cube, or a spot's cone); costs a shadow pass")]
    public bool Shadows;

    // Whether it lights anything: switched on, with a range and an intensity.
    public readonly bool Lit => !Off && Range > 0f && Intensity > 0f;
}

// Flicker patterns (issue #314): Quake's light styles, the convention every Quake-descended engine kept
// (Half-Life's `style`, Source's `_light` patterns), because a string of letters is something a mapper
// can write and read. `a` is dark, `m` is the light as set, `z` is a little over double; one letter is one
// step at the pattern's rate. No interpolation: the steps are the look.
public static class LightStyles
{
    // Letters a second when a light gives no rate: Quake's ten.
    public const float DefaultRate = 10f;

    // The named ones: Quake's own styles 1..11 under names, and a torch for Daggerfall's and S.T.A.L.K.E.R.'s
    // wall torches (Quake's second flicker: busy, but never far from the set brightness).
    private static readonly Dictionary<string, string> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["steady"] = "m",
        ["flicker"] = "mmnmmommommnonmmonqnmmo",
        ["torch"] = "nmonqnmomnmomomno",
        ["candle"] = "mmmmmaaaaammmmmaaaaaabcdefgabcdefg",
        ["candle2"] = "mmmaaaabcdefgmmmmaaaammmaamm",
        ["candle3"] = "mmmaaammmaaammmabcdefaaaammmmabcdefmmmaaaa",
        ["pulse"] = "abcdefghijklmnopqrstuvwxyzyxwvutsrqponmlkjihgfedcba",
        ["gentle_pulse"] = "jklmnopqrstuvwxyzyxwvutsrqponmlkj",
        ["slow_pulse"] = "abcdefghijklmnopqrrqponmlkjihgfedcba",
        ["strobe"] = "mamamamamama",
        ["slow_strobe"] = "aaaaaaaazzzzzzzz",
        ["fluorescent"] = "mmamammmmammamamaaamammma",
    };

    public static IEnumerable<string> PresetNames => Presets.Keys;

    // The letters a pattern stands for: a preset's, or the pattern itself when it is letters a..z; false
    // for anything else (which a light's part reports when content loads, and a light treats as steady).
    public static bool TryResolve(string? pattern, out string letters)
    {
        letters = "";
        if (string.IsNullOrEmpty(pattern)) return true;
        if (Presets.TryGetValue(pattern, out var preset)) { letters = preset; return true; }
        foreach (char c in pattern)
            if (c < 'a' || c > 'z') return false;
        letters = pattern;
        return true;
    }

    // How bright the pattern is `seconds` into the world: 1 is the light as set. Deterministic: the same
    // pattern, rate and time give the same answer on every machine and after every load.
    public static float Brightness(string? pattern, float rate, double seconds)
    {
        if (string.IsNullOrEmpty(pattern) || !TryResolve(pattern, out var letters) || letters.Length == 0) return 1f;
        double step = Math.Floor(seconds * (rate > 0f ? rate : DefaultRate));
        if (!double.IsFinite(step)) return 1f;
        long index = (long)(step % letters.Length);
        if (index < 0) index += letters.Length;
        return (letters[(int)index] - 'a') / 12f;   // 'm' - 'a' = 12: m is the light as set
    }
}

// One light as the renderer wants it: where it is, what it contributes, how far it reaches.
public readonly record struct LightSample(Vector3 Position, Vector3 Colour, float Range)
{
    // A spot light's cone (issue #314), as the shader reads it (common.fxh `LightSpots`): xyz is the
    // direction the cone points times s, w is cos(inner half-angle) times s, where s = 1 / (cos inner -
    // cos outer). Then the cone's strength toward a point is 1 - saturate(w - dot(fromLight, xyz)): full
    // inside the inner angle, nothing outside the outer, a ramp between, in one dot product. Zero — the
    // default — is 1 everywhere: a point light (`LightRules.Spot` makes one).
    public Vector4 Spot { get; init; }

    // Shadows (issue #315): whether the light asks for a map (`PointLight.Shadows`), and the map it has this
    // frame: 1 + its block in the lamps' atlas (LampShadows), 0 for none.
    internal bool CastsShadows { get; init; }
    internal int ShadowSlot { get; init; }

    // The cone's strength toward a point: 1 for a point light.
    public float ConeAt(Vector3 at)
    {
        if (Spot == Vector4.Zero) return 1f;
        var from = at - Position;
        float distance = from.Length();
        if (distance > 0.001f) from /= distance;
        else from = Vector3.Zero;
        return 1f - Math.Clamp(Spot.W - Vector3.Dot(from, new Vector3(Spot.X, Spot.Y, Spot.Z)), 0f, 1f);
    }

    // How much this light matters at a point. Distance alone is the wrong measure — a bright lamp four
    // metres away beats a candle at two — so it is the falloff, which is what the shader will compute;
    // and a spot light matters only inside its cone.
    public float InfluenceAt(Vector3 at)
    {
        if (Range <= 0f) return 0f;
        float distance = Vector3.Distance(Position, at);
        if (distance >= Range) return 0f;

        float falloff = 1f - distance / Range;
        return falloff * falloff * MathF.Max(MathF.Max(Colour.X, Colour.Y), Colour.Z) * ConeAt(at);
    }
}

public static class LightRules
{
    // How many lights one draw can carry. The shader declares arrays of this size (common.fxh).
    public const int PerObject = 4;

    // The lights that matter most at a point, strongest first, into `result`; returns how many.
    //
    // This is a *decision* — which of a hundred lamps a wall is lit by — so it lives here rather than in
    // the renderer, and is tested headlessly. A selection sort, because picking four out of a handful is
    // what this is for and an allocation-free pass beats a clever data structure at that size (02 §4.6).
    public static int Nearest(ReadOnlySpan<LightSample> lights, Vector3 at, Span<LightSample> result)
    {
        int wanted = Math.Min(result.Length, PerObject);
        if (wanted == 0) return 0;

        Span<float> influence = stackalloc float[PerObject];
        int found = 0;

        foreach (var light in lights)
        {
            float score = light.InfluenceAt(at);
            if (score <= 0f) continue;                    // out of range: not dim, absent

            // Where it belongs among the ones kept so far.
            int place = found;
            while (place > 0 && influence[place - 1] < score) place--;
            if (place >= wanted) continue;

            for (int i = Math.Min(found, wanted - 1); i > place; i--)
            {
                influence[i] = influence[i - 1];
                result[i] = result[i - 1];
            }
            influence[place] = score;
            result[place] = light;
            if (found < wanted) found++;
        }

        return found;
    }

    // A spot light's cone as `LightSample.Spot` carries it: pointing along `direction`, full strength
    // within `innerDegrees` of it and nothing past `coneDegrees` (both half-angles). A cone of 0, or of
    // 180 and more, lights all round: zero, a point light. An inner angle of 0 or not inside the cone is
    // three quarters of it.
    internal static Vector4 Spot(Vector3 direction, float coneDegrees, float innerDegrees)
    {
        if (!(coneDegrees > 0f) || coneDegrees >= 180f || direction.LengthSquared() < 1e-8f) return Vector4.Zero;
        if (!(innerDegrees > 0f) || innerDegrees >= coneDegrees) innerDegrees = coneDegrees * 0.75f;
        float cosOuter = MathF.Cos(coneDegrees * MathF.PI / 180f);
        float cosInner = MathF.Cos(innerDegrees * MathF.PI / 180f);
        float s = 1f / MathF.Max(cosInner - cosOuter, 1e-4f);
        var d = Vector3.Normalize(direction) * s;
        return new Vector4(d, cosInner * s);
    }

    // A light as the renderer draws it this frame: at `at` (camera-relative), turned by `rotation` (a spot
    // points along the entity's forward), its colour times its intensity times its pattern's brightness
    // `seconds` into the world (WorldTime.Scaled). The client's light extract is this and nothing else.
    internal static LightSample Sample(in PointLight light, Vector3 at, Quaternion rotation, double seconds)
    {
        float brightness = LightStyles.Brightness(light.Pattern, light.PatternRate, seconds);
        var spot = light.Cone > 0f ? Spot(Vector3.Transform(TransformMath.Forward, rotation), light.Cone, light.InnerCone) : Vector4.Zero;
        return new LightSample(at, light.Colour * (light.Intensity * brightness), light.Range) { Spot = spot, CastsShadows = light.Shadows };
    }

    // What the chosen lights add at a point on a surface facing `normal`: the shaders' sum, and these are
    // its tests (common.fxh `PointLights`, Lambert; sprite.fx `SpritePointLights`, `wrapped` half-Lambert
    // because a billboard's normal only faces the camera). Falloff (1 - d/range) squared, times the cone.
    internal static Vector3 Sum(ReadOnlySpan<LightSample> chosen, Vector3 at, Vector3 normal, bool wrapped)
    {
        var sum = Vector3.Zero;
        foreach (var light in chosen)
        {
            var toLight = light.Position - at;
            float distance = toLight.Length();
            if (distance >= light.Range) continue;
            float falloff = 1f - distance / MathF.Max(light.Range, 0.001f);
            var l = toLight / MathF.Max(distance, 0.001f);
            float facing = wrapped ? Math.Clamp(Vector3.Dot(normal, l) * 0.5f + 0.5f, 0f, 1f) : Math.Clamp(Vector3.Dot(normal, l), 0f, 1f);
            sum += light.Colour * (falloff * falloff * facing * light.ConeAt(at));
        }
        return sum;
    }

    // Whether two choices from `Nearest` are the same lights, so one draw can carry both (the renderer
    // batches sprites while it holds). Order matters not: the shader sums them.
    internal static bool SameSet(ReadOnlySpan<LightSample> a, ReadOnlySpan<LightSample> b)
    {
        if (a.Length != b.Length) return false;
        foreach (var light in a)
        {
            bool found = false;
            foreach (var other in b)
                if (light == other) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }
}
