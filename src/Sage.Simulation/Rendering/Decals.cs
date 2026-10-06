#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// Marks that stay: bullet holes, blood on the wall, a scorch where a fireball burst (docs/design/06
// §3.12, issue #306).
//
// **The same split as particles**: where a decal is, which way it faces, how old it is and how faded
// are arithmetic, kept here with no MonoGame so a test can check them headless; the client only draws
// what the pool holds (a quad laid on the surface, `DecalExtract`). What puts one down is gameplay's
// business (`DecalSystem`, Sage.Gameplay): an impact cue or a damage type names a `decal` record as it
// names a `particle` one, and the system finds the surface with a short ray.
//
// **A pool with a ceiling, per world.** A firefight leaves hundreds of holes and nobody counts them, so
// when the pool is full the *oldest* goes to make room: the newest mark is the one the player is looking
// at. Each decal also has a lifetime and fades out over its last `fade` seconds.
//
// Positions are origin space (R6) like everything else, so a rebase moves them with the world.

// What a mark looks like and how long it stays, as data (05 §3.5).
//
//   { "type": "decal", "id": "bullet_hole", "texture": "textures/hole.png", "size": 0.12,
//     "lifetime": 30, "fade": 3, "colour": "#202020" }
[Record("decal", Plugin = "sage.client")]
public sealed class DecalRecord
{
    [AssetKind("texture"), Property(Tooltip = "The image laid on the surface (required)")]
    public AssetPath Texture;
    [Property(Tooltip = "What it draws with; empty = the engine's blended decal material (sage:decal)")]
    public RecordRef<MaterialRecord> Material;

    [Property(Min = 0.001, Unit = "m", Tooltip = "How wide the square is on the surface")]
    public float Size = 0.2f;
    [Property(Min = 0, Unit = "s", Tooltip = "How long it stays; 0 = until the pool needs its room")]
    public float Lifetime = 30f;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds over the end of its lifetime it fades out; no more than the lifetime")]
    public float Fade = 2f;
    // "#RRGGBBAA" or [r, g, b, a] (ColourJsonConverter): the texture's tint, and the alpha is its opacity.
    [System.Text.Json.Serialization.JsonConverter(typeof(ColourJsonConverter))]
    [Property(Tooltip = "Tint and opacity, \"#RRGGBB(AA)\"; white leaves the texture as it is")]
    public uint Colour = 0xFFFFFFFF;
    [Property(Tooltip = "Turned a random amount about the surface's normal, so a row of holes does not look stamped")]
    public bool RandomRotation = true;
    [Property(Min = 0, Unit = "m", Tooltip = "How far from where something happened it may land: behind a wounded target along the blow, or below a burst")]
    public float Reach = 2f;

    public static readonly RecordId DefaultMaterial = new("sage", "decal");

    // Bad data is a load error at its line (issue #22), not a mark that silently never shows.
    internal static void Check(DecalRecord record, RecordCheck check)
    {
        if (record.Texture.IsEmpty) check.Error(nameof(Texture), "a decal needs a texture");
        if (!(record.Size > 0f) || float.IsInfinity(record.Size)) check.Error(nameof(Size), $"size must be above 0 m, not {record.Size}");
        if (!(record.Lifetime >= 0f)) check.Error(nameof(Lifetime), $"lifetime must be 0 (until pushed out) or more, not {record.Lifetime}");
        if (!(record.Fade >= 0f)) check.Error(nameof(Fade), $"fade must be 0 or more, not {record.Fade}");
        else if (record.Lifetime > 0f && record.Fade > record.Lifetime)
            check.Error(nameof(Fade), $"fade ({record.Fade} s) is longer than the lifetime ({record.Lifetime} s)");
        if (!(record.Reach >= 0f)) check.Error(nameof(Reach), $"reach must be 0 or more, not {record.Reach}");
    }
}

// One mark on a surface. `Right` and `Up` span its square (unit length, in the surface's plane, already
// turned by its rotation); `Normal` faces out of the surface.
public struct Decal
{
    public RecordId Effect;
    public DecalRecord Record;
    public Vector3 Position;
    public Vector3 Normal;
    public Vector3 Right;
    public Vector3 Up;
    public float Age;
    // What it is on, when that is an entity: it goes when that does (a crate broken, a sector unloaded).
    public Entity Surface;
    // Whether it was put on one at all. Asked separately because a destroyed entity's handle reads as
    // null (Particles.Update learned this): `Surface.IsNull` cannot tell "on nothing" from "on a wall
    // that is gone".
    internal bool OnEntity;

    // How opaque it is now: 1 until its last `fade` seconds, then down to 0.
    public readonly float Alpha
    {
        get
        {
            if (Record.Lifetime <= 0f) return 1f;
            float left = Record.Lifetime - Age;
            if (left <= 0f) return 0f;
            return Record.Fade <= 0f || left >= Record.Fade ? 1f : left / Record.Fade;
        }
    }

    // The record's colour with its alpha scaled by the fade: what the renderer tints the texture with.
    public readonly uint Colour
    {
        get
        {
            uint a = (uint)MathF.Round(((Record.Colour >> 24) & 0xFF) * Alpha);
            return (Record.Colour & 0x00FFFFFFu) | (Math.Min(a, 255u) << 24);
        }
    }
}

// The pool: one flat array, oldest first, reused for ever. A decal is not an entity, for the reason a
// particle is not: hundreds of them, nothing refers to one, and an archetype move per bullet hole would
// make the ECS the cost of a firefight.
public sealed class Decals
{
    public const int DefaultCeiling = 256;

    private Decal[] _decals = new Decal[16];
    private readonly Random _random = new();
    private int _ceiling = DefaultCeiling;

    public int Count { get; private set; }

    // Pushed out to make room for a newer one, since the last ResetStats: for tuning the ceiling.
    public int Evicted { get; private set; }

    // How many this world keeps at once (the client's `r_decals`); lowering it drops the oldest now.
    // 0 keeps none.
    public int Ceiling
    {
        get => _ceiling;
        set
        {
            _ceiling = Math.Max(0, value);
            if (Count > _ceiling) RemoveOldest(Count - _ceiling);
        }
    }

    // Oldest first.
    public ReadOnlySpan<Decal> Live => _decals.AsSpan(0, Count);

    public void ResetStats() => Evicted = 0;

    // Lays `record` at `point` facing out along `normal`, turned `rotation` radians about it (null: the
    // record's random turn, or none). With the pool full the oldest goes. False when nothing was placed:
    // no record, no room at all (a ceiling of 0) or no normal to lay it on.
    public bool Place(RecordId effect, DecalRecord? record, Vector3 point, Vector3 normal, float? rotation = null,
                      Entity surface = default)
    {
        if (record == null || _ceiling <= 0 || normal.LengthSquared() < 1e-8f) return false;
        if (Count >= _ceiling) RemoveOldest(Count - _ceiling + 1);
        if (Count == _decals.Length) Array.Resize(ref _decals, Math.Min(Math.Max(16, _decals.Length * 2), Math.Max(_ceiling, 16)));

        normal = Vector3.Normalize(normal);
        float turn = rotation ?? (record.RandomRotation ? (float)(_random.NextDouble() * Math.Tau) : 0f);
        Basis(normal, turn, out var right, out var up);

        _decals[Count++] = new Decal
        {
            Effect = effect, Record = record, Position = point, Normal = normal, Right = right, Up = up,
            Age = 0f, Surface = surface, OnEntity = !surface.IsNull,
        };
        return true;
    }

    // Ages everything by `dt` and drops what has run out, and what was on an entity that is gone. Keeps
    // the order, so the front is still the oldest.
    public void Update(World? world, float dt)
    {
        int kept = 0;
        for (int i = 0; i < Count; i++)
        {
            ref var decal = ref _decals[i];
            decal.Age += MathF.Max(dt, 0f);
            bool expired = decal.Record.Lifetime > 0f && decal.Age >= decal.Record.Lifetime;
            bool orphaned = world != null && decal.OnEntity && !world.IsAlive(decal.Surface);
            if (expired || orphaned) continue;
            if (kept != i) _decals[kept] = decal;
            kept++;
        }
        Array.Clear(_decals, kept, Count - kept);   // drop the record references
        Count = kept;
    }

    // Everything moves with the world (R6): a bullet hole a sector away from its wall is the bug every
    // other position in the engine was fixed for.
    public void Rebase(Vector3 offset)
    {
        for (int i = 0; i < Count; i++) _decals[i].Position += offset;
    }

    public void Clear()
    {
        Array.Clear(_decals, 0, Count);
        Count = 0;
    }

    private void RemoveOldest(int n)
    {
        n = Math.Min(n, Count);
        if (n <= 0) return;
        Array.Copy(_decals, n, _decals, 0, Count - n);
        Array.Clear(_decals, Count - n, n);
        Count -= n;
        Evicted += n;
    }

    // A square in the plane whose normal is `normal`: any tangent, turned `turn` about the normal. World
    // up is "up" on a wall, so an unturned mark on a wall reads the right way round.
    internal static void Basis(Vector3 normal, float turn, out Vector3 right, out Vector3 up)
    {
        var reference = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitZ;
        right = Vector3.Normalize(Vector3.Cross(reference, normal));
        up = Vector3.Cross(normal, right);
        if (turn == 0f) return;
        float cos = MathF.Cos(turn), sin = MathF.Sin(turn);
        (right, up) = (right * cos + up * sin, up * cos - right * sin);
    }
}
