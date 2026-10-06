#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Reverb zones (issue #329, docs/design/11 §11): the inside of a hut rings like a room, a crypt like a
// cave, and the moment you step out of the door it is dry again. Half-Life's `env_sound` and Morrowind's
// interiors are the reason.
//
// **All of it is data.** A `reverb` record is a preset — how long the tail rings, how dense and bright it
// is, how much of it is heard — and the engine ships four (`sage:room`, `sage:hall`, `sage:cave`,
// `sage:outdoors`, engine_content/data/audio.json) that a game overrides with "patch" or adds to. A
// `ReverbZone` is a box on an entity that names one: a prefab part (`"reverb_zone": { "reverb":
// "sage:room", "size": [8, 4, 8] }`) or a map brush entity with `"trigger" "1"` whose classname is a
// prefab with that part, which takes its box from its own brushes.
//
// Which zone the listener is in, and the blend from one preset to the next, is `AudioEnvironment`'s
// (headless, tested); the client hands the blended values to OpenAL's EFX reverb, and plays dry where
// there is none.

[Record("reverb", Plugin = RegistrationOwners.Core)]
public sealed class ReverbRecord
{
    // The ranges are EFX's standard reverb's (AL_EFFECT_REVERB), so a preset from any EFX table can be
    // copied in as it is; the backend clamps to them as well.
    [Property(Min = 0, Max = 1, Tooltip = "How much of the reverb is heard: the wet level, 0 = dry")]
    public float Mix = 0.5f;
    [Property(Min = 0.1, Max = 20, Unit = "s", Tooltip = "How long the tail rings")]
    public float DecayTime = 1.49f;
    [Property(Min = 0.1, Max = 2, Tooltip = "High-frequency decay relative to the rest: below 1 is a soft room, above 1 a hard one")]
    public float DecayHFRatio = 0.83f;
    [Property(Min = 0, Max = 1, Tooltip = "Modal density of the tail: low is grainy, 1 smooth")]
    public float Density = 1f;
    [Property(Min = 0, Max = 1, Tooltip = "Echo density: low hears separate echoes, 1 a wash")]
    public float Diffusion = 1f;
    [Property(Min = 0, Max = 1, Tooltip = "Overall reverb level")]
    public float Gain = 0.32f;
    [Property(Min = 0, Max = 1, Tooltip = "High-frequency level of the reverb: low is muffled")]
    public float GainHF = 0.89f;
    [Property(Min = 0, Max = 3.16, Tooltip = "Level of the early reflections")]
    public float ReflectionsGain = 0.05f;
    [Property(Min = 0, Max = 0.3, Unit = "s", Tooltip = "Delay before the early reflections")]
    public float ReflectionsDelay = 0.007f;
    [Property(Min = 0, Max = 10, Tooltip = "Level of the late reverb, relative to the reflections")]
    public float LateGain = 1.26f;
    [Property(Min = 0, Max = 0.1, Unit = "s", Tooltip = "Delay of the late reverb after the reflections")]
    public float LateDelay = 0.011f;
}

// The reverb in effect: a preset's values, or a blend between two. What the backend is given.
internal struct ReverbMix : IEquatable<ReverbMix>
{
    public float Mix;
    public float DecayTime;
    public float DecayHFRatio;
    public float Density;
    public float Diffusion;
    public float Gain;
    public float GainHF;
    public float ReflectionsGain;
    public float ReflectionsDelay;
    public float LateGain;
    public float LateDelay;

    // No reverb: a record's defaults with nothing heard, so fading out of a zone only fades the level.
    public static ReverbMix Dry => From(new ReverbRecord()) with { Mix = 0f };

    public static ReverbMix From(ReverbRecord record) => new()
    {
        Mix = record.Mix,
        DecayTime = record.DecayTime,
        DecayHFRatio = record.DecayHFRatio,
        Density = record.Density,
        Diffusion = record.Diffusion,
        Gain = record.Gain,
        GainHF = record.GainHF,
        ReflectionsGain = record.ReflectionsGain,
        ReflectionsDelay = record.ReflectionsDelay,
        LateGain = record.LateGain,
        LateDelay = record.LateDelay,
    };

    public static ReverbMix Lerp(in ReverbMix a, in ReverbMix b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new ReverbMix
        {
            Mix = a.Mix + (b.Mix - a.Mix) * t,
            DecayTime = a.DecayTime + (b.DecayTime - a.DecayTime) * t,
            DecayHFRatio = a.DecayHFRatio + (b.DecayHFRatio - a.DecayHFRatio) * t,
            Density = a.Density + (b.Density - a.Density) * t,
            Diffusion = a.Diffusion + (b.Diffusion - a.Diffusion) * t,
            Gain = a.Gain + (b.Gain - a.Gain) * t,
            GainHF = a.GainHF + (b.GainHF - a.GainHF) * t,
            ReflectionsGain = a.ReflectionsGain + (b.ReflectionsGain - a.ReflectionsGain) * t,
            ReflectionsDelay = a.ReflectionsDelay + (b.ReflectionsDelay - a.ReflectionsDelay) * t,
            LateGain = a.LateGain + (b.LateGain - a.LateGain) * t,
            LateDelay = a.LateDelay + (b.LateDelay - a.LateDelay) * t,
        };
    }

    public readonly bool Equals(ReverbMix other) =>
        Mix == other.Mix && DecayTime == other.DecayTime && DecayHFRatio == other.DecayHFRatio &&
        Density == other.Density && Diffusion == other.Diffusion && Gain == other.Gain && GainHF == other.GainHF &&
        ReflectionsGain == other.ReflectionsGain && ReflectionsDelay == other.ReflectionsDelay &&
        LateGain == other.LateGain && LateDelay == other.LateDelay;

    public override readonly bool Equals(object? obj) => obj is ReverbMix other && Equals(other);

    public override readonly int GetHashCode() => HashCode.Combine(Mix, DecayTime, Gain, GainHF, Density, Diffusion);

    public static bool operator ==(ReverbMix a, ReverbMix b) => a.Equals(b);

    public static bool operator !=(ReverbMix a, ReverbMix b) => !a.Equals(b);
}

// A box that sounds like somewhere: while the listener is inside it, `Reverb` is the reverb heard. The box
// is axis-aligned, centred on the entity plus `Offset`; rotation is ignored, like water's. Where zones
// overlap the higher `Priority` wins, then the smaller box (a crypt inside a cave level).
[Component("sage:reverb_zone")]
public struct ReverbZone : IComponent
{
    [RecordRef("reverb")] public RecordId Reverb;
    [Property(Min = 0, Unit = "m", Tooltip = "Full extents of the box; a map brush entity's own brushes fill it in")]
    public Vector3 Size;
    [Property(Unit = "m", Tooltip = "Where the box's centre is, relative to the entity")]
    public Vector3 Offset;
    [Property(Tooltip = "Higher wins where zones overlap")]
    public int Priority;
    [Property(Min = 0, Max = 30, Unit = "s", Tooltip = "Seconds to blend into this zone's reverb (and out of it); 0 = the default, 1 s")]
    public float Fade;

    internal const float DefaultFade = 1f;

    internal readonly float FadeOrDefault => Fade > 0f ? Fade : DefaultFade;

    internal readonly bool Contains(Vector3 origin, Vector3 point)
    {
        var d = Vector3.Abs(point - (origin + Offset));
        var half = Size * 0.5f;
        return d.X <= half.X && d.Y <= half.Y && d.Z <= half.Z;
    }

    internal readonly float Volume => Size.X * Size.Y * Size.Z;
}

// "reverb_zone": { "reverb": "sage:room", "size": [8, 4, 8], "priority": 0, "fade": 1 }
//
// On a prefab placed in a scene, or on the prefab a map brush entity is made from (with `"trigger" "1"`,
// so it is walked into rather than against and is not drawn): there the size may be left out, and the
// zone is the box around the entity's brushes.
[PrefabPart("reverb_zone", Plugin = RegistrationOwners.Core)]
public sealed class ReverbZonePart : IPrefabPart
{
    [RecordRef("reverb"), Property(Tooltip = "The reverb preset heard inside")]
    public RecordId Reverb;
    [Property(Min = 0, Unit = "m", Tooltip = "Full extents of the box, centred on the entity; leave out on a map brush entity to use its brushes")]
    public Vector3 Size;
    [Property(Unit = "m", Tooltip = "Where the box's centre is, relative to the entity")]
    public Vector3 Offset;
    [Property(Tooltip = "Higher wins where zones overlap")]
    public int Priority;
    [Property(Min = 0, Max = 30, Unit = "s", Tooltip = "Seconds to blend into this zone's reverb; 0 = the default, 1 s")]
    public float Fade;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Reverb.IsEmpty) { ctx.Error("a reverb zone needs a \"reverb\" (a reverb record, e.g. \"sage:room\")"); return; }
        if (Size.X < 0f || Size.Y < 0f || Size.Z < 0f) { ctx.Error("a reverb zone's \"size\" cannot be negative"); return; }
        ctx.World.Add(ctx.Entity, new ReverbZone { Reverb = Reverb, Size = Size, Offset = Offset, Priority = Priority, Fade = Fade });
    }
}

internal static class ReverbZones
{
    // The zone `point` (origin space) is in, or false: highest priority, then the smallest box.
    public static bool Find(Query<Transform, ReverbZone> zones, Vector3 point, out ReverbZone found)
    {
        found = default;
        bool any = false;
        if (zones.Count == 0) return false;
        foreach (var (transforms, boxes, entities) in zones.Chunks)
        {
            var t = transforms.Span;
            var z = boxes.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                var origin = !entity.Parent.IsNull && entity.TryGetComponent<GlobalTransform>(out var global)
                    ? global.Current.Position : t[n].LocalPosition;
                if (!z[n].Contains(origin, point)) continue;
                if (any && (z[n].Priority < found.Priority ||
                            (z[n].Priority == found.Priority && z[n].Volume >= found.Volume))) continue;
                found = z[n];
                any = true;
            }
        }
        return any;
    }

    // A map brush entity's zone with no size of its own takes the box around its brushes (`hull`, relative
    // to the entity). Called where the map spawns it (MapLevel.SpawnSolid).
    public static void FitToBrushes(Entity entity, Vector3[] hull)
    {
        if (hull.Length == 0 || !entity.HasComponent<ReverbZone>()) return;
        ref var zone = ref entity.GetComponent<ReverbZone>();
        if (zone.Size != Vector3.Zero) return;
        var min = hull[0];
        var max = hull[0];
        foreach (var p in hull) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        zone.Size = max - min;
        zone.Offset = (min + max) * 0.5f;
    }
}
