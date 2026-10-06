#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Water you can see (issue #411, docs/design/06): the top of a water volume (issue #262) drawn as a
// surface that ripples, reflects the sky and the shore, shows the bottom through itself, thickens with
// depth and fades out at the shore; and, with the camera under it, a view tinted and fogged by the water.
//
//   { "type": "water_surface", "id": "lake", "colour": [0.05, 0.15, 0.18], "fogDensity": 0.3,
//     "waveLength": 5, "waveStrength": 0.1, "waveSpeed": 0.6, "waveDirection": 30 }
//   { "type": "prefab", "id": "lake", "parts": { "water": { "size": [60, 6, 40] }, "water_surface": "lake" } }
//
// **The same split as the sky and the post chain**: which surfaces a frame draws, whether the camera is
// under one and what that does to the view, and every curve the shader uses (the waves' normals, the
// Fresnel term, the depth fog, the shore fade, the underwater fog) are here, headless and tested; the
// client's `sage:water` post step (shaders/water.fx) draws them line for line.
//
// **The surface is the volume's top face**, so the water a character swims in and the water drawn are the
// same box: the view goes under exactly where the swim volume says the camera's point is under (test:
// TheViewGoesUnderWhereTheSwimmerDoes). A volume with no `water_surface` is still water to swim in and to
// be under (its look is `sage:water`'s), but draws no surface: a flooded cellar seen from inside it.

// What a water surface looks like (a lake, a river, a swamp).
[Record("water_surface", Plugin = RegistrationOwners.Core)]
public sealed class WaterSurfaceRecord
{
    [Property(Tooltip = "The water's own colour: what deep water and the far depths fade to (lit by the sky)", Category = "Colour")]
    public Vector3 Colour = new(0.05f, 0.15f, 0.18f);
    [Property(Min = 0, Unit = "1/m", Tooltip = "How fast the bottom disappears into the colour: 1 - exp(-density x metres of water looked through)", Category = "Colour")]
    public float FogDensity = 0.35f;
    [Property(Min = 0, Unit = "m", Tooltip = "Water shallower than this fades out toward the shore, so the edge is soft and not a line on the bank", Category = "Colour")]
    public float ShoreFade = 0.6f;

    [Property(Min = 0, Max = 1, Tooltip = "How much it mirrors: the Fresnel reflection's strength (1: a still lake at a glancing angle is a mirror)", Category = "Light")]
    public float Reflectivity = 1f;
    [Property(Min = 0, Max = 0.2, Tooltip = "How far the ripples bend what is seen through the water, as a fraction of the screen per unit of tilt", Category = "Light")]
    public float Refraction = 0.03f;
    [Property(Min = 0, Tooltip = "The sun's glint on the ripples", Category = "Light")]
    public float Specular = 1.5f;

    [Property(Min = 0.05, Unit = "m", Tooltip = "The length of the longest ripple; three shorter ones ride on it", Category = "Waves")]
    public float WaveLength = 4f;
    [Property(Min = 0, Max = 1, Tooltip = "How steep the ripples are (the most the surface tilts); 0 is a mirror-flat pond", Category = "Waves")]
    public float WaveStrength = 0.12f;
    [Property(Min = 0, Unit = "m/s", Tooltip = "How fast the longest ripple travels; shorter ones travel slower, as on real water", Category = "Waves")]
    public float WaveSpeed = 0.8f;
    [Property(Unit = "deg", Tooltip = "Which way the ripples travel: 0 is +x, 90 is +z", Category = "Waves")]
    public float WaveDirection = 30f;

    [Property(Tooltip = "What the view fades into with the camera under the surface", Category = "Underwater")]
    public Vector3 UnderwaterColour = new(0.04f, 0.16f, 0.2f);
    [Property(Min = 0, Unit = "1/m", Tooltip = "How thick the water is to look through from inside it: 1 - exp(-density x metres)", Category = "Underwater")]
    public float UnderwaterDensity = 0.12f;
    [Property(Tooltip = "What everything seen from under the surface is multiplied by: water swallows red first", Category = "Underwater")]
    public Vector3 UnderwaterTint = new(0.55f, 0.8f, 0.85f);

    // Bad data is a load error at its line (issue #22), not water that silently draws black.
    internal static void Check(WaterSurfaceRecord record, RecordCheck check)
    {
        static bool Colour(Vector3 c) => c.X >= 0f && c.Y >= 0f && c.Z >= 0f && float.IsFinite(c.X + c.Y + c.Z);
        if (!Colour(record.Colour)) check.Error(nameof(Colour), "colour must be three channels of 0 or more");
        if (!Colour(record.UnderwaterColour)) check.Error(nameof(UnderwaterColour), "underwaterColour must be three channels of 0 or more");
        if (!Colour(record.UnderwaterTint)) check.Error(nameof(UnderwaterTint), "underwaterTint must be three channels of 0 or more");
        if (!(record.FogDensity >= 0f) || float.IsInfinity(record.FogDensity)) check.Error(nameof(FogDensity), $"fogDensity must be 0 or more, not {record.FogDensity}");
        if (!(record.UnderwaterDensity >= 0f) || float.IsInfinity(record.UnderwaterDensity))
            check.Error(nameof(UnderwaterDensity), $"underwaterDensity must be 0 or more, not {record.UnderwaterDensity}");
        if (!(record.ShoreFade >= 0f) || float.IsInfinity(record.ShoreFade)) check.Error(nameof(ShoreFade), $"shoreFade must be 0 m or more, not {record.ShoreFade}");
        if (!(record.Reflectivity >= 0f && record.Reflectivity <= 1f)) check.Error(nameof(Reflectivity), $"reflectivity must be between 0 and 1, not {record.Reflectivity}");
        if (!(record.Refraction >= 0f && record.Refraction <= 0.2f)) check.Error(nameof(Refraction), $"refraction must be between 0 and 0.2, not {record.Refraction}");
        if (!(record.Specular >= 0f) || float.IsInfinity(record.Specular)) check.Error(nameof(Specular), $"specular must be 0 or more, not {record.Specular}");
        if (!(record.WaveLength >= 0.05f) || float.IsInfinity(record.WaveLength)) check.Error(nameof(WaveLength), $"waveLength must be at least 0.05 m, not {record.WaveLength}");
        if (!(record.WaveStrength >= 0f && record.WaveStrength <= 1f)) check.Error(nameof(WaveStrength), $"waveStrength must be between 0 and 1, not {record.WaveStrength}");
        if (!(record.WaveSpeed >= 0f) || float.IsInfinity(record.WaveSpeed)) check.Error(nameof(WaveSpeed), $"waveSpeed must be 0 m/s or more, not {record.WaveSpeed}");
        if (!float.IsFinite(record.WaveDirection)) check.Error(nameof(WaveDirection), "waveDirection must be a number of degrees");
    }
}

// A water volume's visible top: the look it is drawn with. On the same entity as its WaterVolume.
[Component("sage:water_surface")]
public struct WaterSurface : IComponent
{
    [RecordRef("water_surface")]
    [Property(Tooltip = "How the surface looks (a water_surface record); empty: the engine's sage:water")]
    public RecordId Look;
}

// "water_surface": "lake" (or { "look": "lake" }, or {} for the engine's own look), beside a "water" part:
// the volume's top face is drawn as water.
[PrefabPart("water_surface", Plugin = RegistrationOwners.Core, After = new[] { "water" }, Shorthand = nameof(Look))]
public sealed class WaterSurfacePart : IPrefabPart
{
    [RecordRef("water_surface")]
    [Property(Tooltip = "How the surface looks (a water_surface record); empty: the engine's sage:water")]
    public RecordId Look;

    public void Apply(in PrefabPartContext ctx)
    {
        if (!ctx.World.Has<WaterVolume>(ctx.Entity))
        {
            ctx.Error("a water surface is the top of a water volume: put a \"water\" part (with its size) on the same prefab");
            return;
        }
        ctx.World.Add(ctx.Entity, new WaterSurface { Look = Look });
    }
}

// One volume's surface as a frame sees it: its footprint (x and z, origin space), the height of its top
// and bottom, and its look.
internal struct WaterPlane
{
    public Entity Volume;
    public Vector2 Min, Max;     // the footprint's corners: (x, z)
    public float Surface;        // the top face's height
    public float Floor;          // the bottom's
    public float Distance;       // across the ground from the camera to the footprint: 0 standing over it
    public WaterSurfaceRecord Look;
}

// The world's water as the view sees it: which surfaces a frame draws, and whether a point (the camera) is
// under one. A scan, like the physics' WaterVolumes: a level has a handful of volumes.
internal sealed class WaterViews
{
    // The most surfaces a frame draws: the nearest ones (water.fx has this many slots).
    public const int MaxSurfaces = 4;

    // The look of a surface that names none, and of a volume with no surface (what being under it looks like).
    public static readonly RecordId DefaultLook = new("sage", "water");

    private static readonly WaterSurfaceRecord Fallback = new();

    private readonly RecordStore _records;
    private readonly Query<Transform, WaterVolume> _volumes;

    public WaterViews(World world, RecordStore records)
    {
        _records = records;
        _volumes = world.Query<Transform, WaterVolume>();
    }

    public bool Any => _volumes.Count > 0;

    // The surfaces to draw from `camera` (origin space): every volume with a WaterSurface whose footprint is
    // within `reach` metres across the ground, nearest first, at most `into.Length` (MaxSurfaces). Returns how
    // many were written.
    public int Collect(Vector3 camera, float reach, Span<WaterPlane> into)
    {
        int count = 0;
        if (_volumes.Count == 0 || into.Length == 0) return 0;
        foreach (var (transforms, volumes, entities) in _volumes.Chunks)
        {
            var t = transforms.Span;
            var v = volumes.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                if (!entity.TryGetComponent<WaterSurface>(out var surface)) continue;
                var plane = Plane(entity, t[n], v[n], LookOf(surface.Look));
                plane.Distance = Across(plane, camera);
                if (!(plane.Distance <= reach)) continue;

                // Insertion into the nearest `into.Length`, keeping them sorted.
                int at = count;
                while (at > 0 && into[at - 1].Distance > plane.Distance) at--;
                if (at >= into.Length) continue;
                for (int k = Math.Min(count, into.Length - 1); k > at; k--) into[k] = into[k - 1];
                into[at] = plane;
                if (count < into.Length) count++;
            }
        }
        return count;
    }

    // Whether `point` (origin space) is under water: inside a volume's footprint, below its surface and above
    // its floor; of several, the one with the highest surface, as the swim volume chooses (WaterVolumes.Find).
    // A volume with no WaterSurface is water too, with the engine's look.
    public bool Under(Vector3 point, out WaterPlane water)
    {
        water = default;
        bool found = false;
        if (_volumes.Count == 0) return false;
        foreach (var (transforms, volumes, entities) in _volumes.Chunks)
        {
            var t = transforms.Span;
            var v = volumes.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                var origin = Origin(entity, t[n]);
                if (!v[n].Covers(origin, point)) continue;
                float surface = v[n].SurfaceAt(origin);
                float floor = origin.Y - v[n].Size.Y * 0.5f;
                if (point.Y >= surface || point.Y <= floor) continue;
                if (found && surface <= water.Surface) continue;
                found = true;
                var look = entity.TryGetComponent<WaterSurface>(out var s) ? LookOf(s.Look) : LookOf(default);
                water = Plane(entity, t[n], v[n], look);
            }
        }
        return found;
    }

    // A surface's look: its record, else the engine's sage:water, else the defaults in code (a world with
    // no engine content).
    public WaterSurfaceRecord LookOf(RecordId look)
    {
        if (!look.IsEmpty && _records.TryGet(look, out WaterSurfaceRecord record)) return record;
        return _records.TypeNameOf(typeof(WaterSurfaceRecord)) != null && _records.TryGet(DefaultLook, out WaterSurfaceRecord fallback)
            ? fallback : Fallback;
    }

    private static WaterPlane Plane(Entity entity, in Transform transform, in WaterVolume volume, WaterSurfaceRecord look)
    {
        var origin = Origin(entity, transform);
        var half = volume.Size * 0.5f;
        return new WaterPlane
        {
            Volume = entity,
            Min = new Vector2(origin.X - half.X, origin.Z - half.Z),
            Max = new Vector2(origin.X + half.X, origin.Z + half.Z),
            Surface = volume.SurfaceAt(origin),
            Floor = origin.Y - half.Y,
            Look = look,
        };
    }

    // How far across the ground `point` is from the footprint: 0 over it.
    internal static float Across(in WaterPlane plane, Vector3 point)
    {
        float dx = MathF.Max(MathF.Max(plane.Min.X - point.X, point.X - plane.Max.X), 0f);
        float dz = MathF.Max(MathF.Max(plane.Min.Y - point.Z, point.Z - plane.Max.Y), 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    // A root volume's transform is its place; one parented to something is where its GlobalTransform says
    // (as the physics reads it).
    private static Vector3 Origin(Entity entity, in Transform transform) =>
        !entity.Parent.IsNull && entity.TryGetComponent<GlobalTransform>(out var global) ? global.Current.Position : transform.LocalPosition;
}

// The curves water.fx draws with, here so a test can check them: each is the shader's function of the
// same name, line for line.
internal static class WaterShading
{
    // What water.fx is given by the engine (Renderer.SetWaterParams), which a material need not set: the
    // main view, the frame's surfaces relative to its camera, the sky they reflect and the underwater view.
    public static readonly string[] EngineParams =
    {
        "WaterInvViewProj", "WaterViewProj", "WaterCamera", "WaterViewport", "WaterCount", "WaterRect", "WaterLevel",
        "WaterColour", "WaterWaves", "WaterMore", "WaterHorizon", "WaterZenith", "WaterFog", "WaterSunDir",
        "WaterSunColour", "WaterSky", "WaterLight", "WaterUnder", "WaterUnderTint",
    };

    // The four ripples a surface is made of: turned from the look's direction by `Turn` radians, `Length`
    // times its wave length, and `Share` of its strength. Long and slow first, short and quick last.
    internal static readonly (float Turn, float Length, float Share)[] Ripples =
    {
        (0f, 1f, 0.45f), (0.55f, 0.57f, 0.3f), (-0.8f, 0.33f, 0.15f), (1.9f, 0.21f, 0.1f),
    };

    // The way the ripples travel, (x, z), unit length.
    public static Vector2 Direction(WaterSurfaceRecord look)
    {
        float a = look.WaveDirection * (MathF.PI / 180f);
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }

    // The surface's normal at (x, z) (origin space) and `time` seconds: the slope of four travelling sine
    // waves, each moving along its direction at `WaveSpeed` times the square root of its share of the
    // length (deep water: longer waves are faster). Unit length; straight up when `WaveStrength` is 0.
    public static Vector3 Normal(WaterSurfaceRecord look, float x, float z, float time)
    {
        var dir = Direction(look);
        float gx = 0f, gz = 0f;
        float length = MathF.Max(look.WaveLength, 0.05f);
        foreach (var (turn, scale, share) in Ripples)
        {
            float c = MathF.Cos(turn), s = MathF.Sin(turn);
            var d = new Vector2(dir.X * c - dir.Y * s, dir.X * s + dir.Y * c);
            float k = 2f * MathF.PI / (length * scale);
            float speed = look.WaveSpeed * MathF.Sqrt(scale);
            float phase = k * (d.X * x + d.Y * z - speed * time);
            float slope = look.WaveStrength * share * MathF.Cos(phase);
            gx += d.X * slope;
            gz += d.Y * slope;
        }
        return Vector3.Normalize(new Vector3(-gx, 1f, -gz));
    }

    // How much of the light leaving the surface toward the eye is reflected: Schlick's Fresnel for water
    // (0.02 head-on, all of it at a grazing angle), scaled by `reflectivity`. `cosine` is the angle between
    // the eye's direction and the normal.
    public static float Fresnel(float cosine, float reflectivity)
    {
        float m = 1f - Math.Clamp(cosine, 0f, 1f);
        float m2 = m * m;
        return reflectivity * (0.02f + 0.98f * m2 * m2 * m);
    }

    // How much of the bottom the water's colour hides, looking through `metres` of it.
    public static float DepthFog(float density, float metres) => 1f - MathF.Exp(-MathF.Max(density, 0f) * MathF.Max(metres, 0f));

    // How much of the surface shows where the bottom is `depth` metres under it: none at the waterline,
    // all of it from `shoreFade` down (a smoothstep). A shore fade of 0 is a hard edge.
    public static float ShoreFade(float shoreFade, float depth)
    {
        if (shoreFade <= 0f) return depth > 0f ? 1f : 0f;
        float x = Math.Clamp(depth / shoreFade, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    // What `colour`, seen through `metres` of water from under the surface, becomes: tinted, then fogged
    // into the underwater colour (lit by `light`).
    public static Vector3 Underwater(WaterSurfaceRecord look, Vector3 colour, float metres, Vector3 light)
    {
        float fog = DepthFog(look.UnderwaterDensity, metres);
        return Vector3.Lerp(colour * look.UnderwaterTint, look.UnderwaterColour * light, fog);
    }

    // The light the water's own colour is seen in: the sky's ambient plus the sun by its height, at most 1
    // a channel. Dark at night, so a lake does not glow.
    public static Vector3 Light(Vector3 ambientSky, Vector3 sunColour, Vector3 sunDirection) =>
        Vector3.Clamp(ambientSky + sunColour * MathF.Max(0f, -sunDirection.Y), Vector3.Zero, Vector3.One);

    // Where a ray from `origin` along unit `dir` meets a surface's top (from above or below): the distance
    // along it, or +infinity when it misses the footprint or runs parallel or away.
    public static float Hit(in WaterPlane plane, Vector3 origin, Vector3 dir)
    {
        if (MathF.Abs(dir.Y) < 1e-6f) return float.PositiveInfinity;
        float t = (plane.Surface - origin.Y) / dir.Y;
        if (!(t > 0f)) return float.PositiveInfinity;
        var p = origin + dir * t;
        if (p.X < plane.Min.X || p.X > plane.Max.X || p.Z < plane.Min.Y || p.Z > plane.Max.Y) return float.PositiveInfinity;
        return t;
    }
}
