#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Physics materials and surface types (docs/design/10 "As built (surfaces)", issue #270): what a
// surface is made of, so a footstep on wood sounds like wood, a bullet hole in plaster looks like
// plaster and a round goes through a plank but not a wall. Daggerfall, Half-Life, Morrowind and
// S.T.A.L.K.E.R. all have one, under different names (Half-Life's materials.txt, Source's
// surfaceproperties, X-Ray's material pairs).
//
// One record per surface; it is given to
//   - a collider:      `Collider.Surface` (the `body` part's "surface");
//   - a brush texture: the record's own "textures" ("wood*" covers wood_plank and wood_dark); a face no
//                      record names takes the map record's "surface";
//   - a terrain layer: TerrainRecord.Surfaces (layer 0 is the ground's own; a C# generator paints the
//                      rest per cell with Heightfield.SetLayer);
// and every RayHit and SweepHit says which one it hit (`Surface`, empty when none was given).
//
//   { "type": "physics_material", "id": "wood", "friction": 0.6, "restitution": 0.1,
//     "footstep": "step_wood", "land": "land_wood", "jump": "jump_wood", "impact": "impact_wood", "decal": "textures/decals/hole_wood.png",
//     "penetration": 0.4, "textures": ["wood*", "crate*", "door"] }
[Record("physics_material", Plugin = "sage.physics3d")]
public sealed class PhysicsMaterialRecord
{
    [Property(Category = "Physics", Min = 0, Max = 2, Tooltip = "0 slides like ice; a body's own friction wins over it")]
    public float Friction = 0.7f;
    [Property(Category = "Physics", Min = 0, Max = 1, Tooltip = "Bounciness: 0 stops dead, 1 bounces back as fast")]
    public float Restitution;

    [RecordRef("cue"), Property(Category = "Effects", Tooltip = "The cue a footstep on it raises (its sound, its dust)")]
    public RecordId Footstep;
    [RecordRef("cue"), Property(Category = "Effects", Tooltip = "The cue landing on it from a fall or a jump raises; empty = its footstep")]
    public RecordId Land;
    [RecordRef("cue"), Property(Category = "Effects", Tooltip = "The cue jumping off it raises; empty = its footstep")]
    public RecordId Jump;
    [RecordRef("cue"), Property(Category = "Effects", Tooltip = "The cue a blow or a shot that lands on it raises")]
    public RecordId Impact;
    [AssetKind("texture"), Property(Category = "Effects", Tooltip = "What a bullet hole in it looks like (a hint for the weapon code)")]
    public AssetPath Decal;
    [Property(Category = "Effects", Min = 0, Tooltip = "How hard it is to shoot through, per metre: 0 never stops a round, 1 is the reference (brick); a hint for hitscan")]
    public float Penetration = 1f;

    [Property(Category = "Brushes", Tooltip = "The brush textures it covers: a name, or a pattern with * (\"wood*\"); case does not matter")]
    public List<string> Textures = new();
}

// One face of a convex collider and what it is made of (IPhysicsWorld.SetSurfaces): a hit takes the
// surface of the face whose normal it is closest to.
public readonly record struct SurfaceFace(Vector3 Normal, RecordId Surface);

// Which physics_material a brush texture is (PhysicsMaterialRecord.Textures). Built once per level from
// the records; an exact name wins over a pattern, and a longer pattern over a shorter one.
internal sealed class SurfaceTextures
{
    private readonly Dictionary<string, RecordId> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Pattern, RecordId Surface)> _patterns = new();
    private readonly Dictionary<string, RecordId> _resolved = new(StringComparer.OrdinalIgnoreCase);

    public RecordId Fallback { get; init; }

    public bool IsEmpty => _exact.Count == 0 && _patterns.Count == 0 && Fallback.IsEmpty;

    public static SurfaceTextures From(RecordStore records, RecordId fallback = default)
    {
        var table = new SurfaceTextures { Fallback = fallback };
        if (records.TypeNameOf(typeof(PhysicsMaterialRecord)) is not { } type) return table;
        foreach (var id in records.Ids(type))
        {
            if (!records.TryGet(id, out PhysicsMaterialRecord record) || record.Textures == null) continue;
            foreach (var texture in record.Textures)
            {
                if (string.IsNullOrWhiteSpace(texture)) continue;
                if (texture.IndexOf('*') < 0) table._exact.TryAdd(texture, id);
                else table._patterns.Add((texture, id));
            }
        }
        // Most specific first: the longest pattern, then the earliest id (Ids is in id order).
        var ordered = new List<(string, RecordId)>(table._patterns.Count);
        for (int i = 0; i < table._patterns.Count; i++) ordered.Add(table._patterns[i]);
        ordered.Sort((a, b) =>
        {
            int byLength = Literal(b.Item1).CompareTo(Literal(a.Item1));
            return byLength != 0 ? byLength : table._patterns.IndexOf(a).CompareTo(table._patterns.IndexOf(b));
        });
        table._patterns.Clear();
        table._patterns.AddRange(ordered);
        return table;

        static int Literal(string pattern) => pattern.Length - pattern.AsSpan().Count('*');
    }

    // The surface of a face with this texture: an exact name, else the most specific pattern, else the
    // fallback.
    public RecordId Of(string texture)
    {
        if (string.IsNullOrEmpty(texture)) return Fallback;
        if (_resolved.TryGetValue(texture, out var known)) return known;
        RecordId found = Fallback;
        if (_exact.TryGetValue(texture, out var exact)) found = exact;
        else
            foreach (var (pattern, surface) in _patterns)
                if (Matches(pattern, texture)) { found = surface; break; }
        _resolved[texture] = found;
        return found;
    }

    // A glob with `*` only (any run of characters), case-insensitive.
    internal static bool Matches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; continue; }
            if (p < pattern.Length && char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])) { p++; t++; continue; }
            if (star < 0) return false;
            p = star + 1;
            t = ++mark;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
