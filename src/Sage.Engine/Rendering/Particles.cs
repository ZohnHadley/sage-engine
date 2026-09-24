#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Sparks, embers, smoke and blood (docs/design/06 §3.12, TODO F39).
//
// **The simulation is here and the drawing is not**, the same split as the audio mixer: where a spark
// is after four tenths of a second is arithmetic anybody can check, and turning it into two triangles
// needs a graphics device. So this file has no MonoGame in it and a test can run ten thousand particles
// without a window.
//
// Everything is **flat arrays and a free list**. A particle is not an entity: there are thousands of
// them, they live for half a second, and nothing ever refers to one. Giving each an entity would cost
// an archetype move per spark and make the ECS the bottleneck of a puff of smoke.
//
// Positions are **origin space** (R6) like everything else, so a rebase moves them with the world.

public enum EmitShape
{
    Point,        // straight out along the emitter's direction
    Cone,         // a spread around it: sparks off a blade, a muzzle flash
    Sphere,       // every direction: a burst, an explosion
    Box,          // anywhere in a volume, falling: rain, snow, dust in a shaft of light
}

// What a puff of something looks like, as data (05 §3.5). One record covers a burst and a steady
// stream, because the difference is `burst` against `rate` and nothing else.
[Record("particle")]
public sealed class ParticleRecord
{
    public AssetPath Texture;                 // the sprite each particle draws
    public RecordId Material;                 // empty = the engine's additive particle material

    // How many, and how they arrive.
    public int Burst;                         // emitted at once when something triggers it
    public float Rate;                        // and/or this many a second while an emitter runs
    public int MaxParticles = 256;            // the ceiling this record may hold at once

    // How long each one lasts, and how fast it sets off.
    public float LifeMin = 0.5f, LifeMax = 1f;
    public float SpeedMin = 1f, SpeedMax = 3f;

    public EmitShape Shape = EmitShape.Cone;
    public float AngleDegrees = 25f;          // Cone: the half-angle
    public Vector3 Extents;                   // Box: where they may start, around the emitter

    // What happens to one while it lives.
    public float Gravity = -9.81f;            // metres per second squared, on Y
    public float Drag;                        // fraction of speed lost per second
    public float SizeStart = 0.15f, SizeEnd = 0.05f;
    public uint ColourStart = 0xFFFFFFFF;     // RGBA, and the alpha is what fades
    public uint ColourEnd = 0x00FFFFFF;
    public float SpinDegrees;                 // turned per second, for smoke and leaves

    // World, or carried: a spark stays where it was thrown, a magic aura moves with its owner.
    public bool Local;

    public static readonly RecordId DefaultMaterial = new("sage", "particle_additive");
}

// An entity that emits while it exists: a torch's embers, rain over the player, an aura. The component
// is the engine's, so a headless run carries it and simply never draws anything.
public struct ParticleEmitter : IComponent
{
    public RecordId Effect;
    public bool Enabled;
    [Transient] public float Pending;      // fractional particles carried between ticks
}

// The particles themselves: flat arrays, one slot per particle, reused for ever.
public sealed class Particles
{
    // One buffer per record, because everything drawn together shares a texture and a material. The
    // renderer then draws one batch per effect and never sorts by texture.
    private readonly Dictionary<RecordId, Group> _groups = new();
    private readonly List<Group> _order = new();
    private readonly Random _random = new();

    public int Budget { get; set; } = 4000;      // across every effect, so a spell cannot eat a frame

    public int Live { get; private set; }

    public int Refused { get; private set; }     // asked for beyond the budget, for `fx_stats`

    public bool Enabled { get; set; } = true;

    public IReadOnlyList<Group> Groups => _order;

    public void ResetStats() => Refused = 0;

    // A batch of particles that share a record, and so a texture and a material.
    public sealed class Group
    {
        public required RecordId Effect;
        public required ParticleRecord Record;
        public Vector3[] Position = Array.Empty<Vector3>();
        public Vector3[] Velocity = Array.Empty<Vector3>();
        public float[] Age = Array.Empty<float>();
        public float[] Life = Array.Empty<float>();
        public float[] Rotation = Array.Empty<float>();
        public float[] Spin = Array.Empty<float>();
        public Entity[] Follows = Array.Empty<Entity>();     // for `local` effects
        public Vector3[] Offset = Array.Empty<Vector3>();    // ...and where they sit on it
        public int Count;

        // Where a particle is now and what it looks like: the two questions the renderer asks and the
        // only two a test needs. Size and colour are pure functions of age, so nothing stores them.
        public float Fraction(int i) => Life[i] <= 0f ? 1f : Math.Clamp(Age[i] / Life[i], 0f, 1f);

        public float SizeOf(int i) => Lerp(Record.SizeStart, Record.SizeEnd, Fraction(i));

        public uint ColourOf(int i) => LerpColour(Record.ColourStart, Record.ColourEnd, Fraction(i));

        internal void Grow(int wanted)
        {
            if (Position.Length >= wanted) return;
            int size = Math.Max(wanted, Math.Max(16, Position.Length * 2));
            Array.Resize(ref Position, size);
            Array.Resize(ref Velocity, size);
            Array.Resize(ref Age, size);
            Array.Resize(ref Life, size);
            Array.Resize(ref Rotation, size);
            Array.Resize(ref Spin, size);
            Array.Resize(ref Follows, size);
            Array.Resize(ref Offset, size);
        }

        // Dropping one is a swap with the last: order does not matter to a puff of smoke, and this keeps
        // the arrays dense so the renderer walks them straight through.
        internal void RemoveAt(int i)
        {
            int last = --Count;
            if (i == last) return;
            Position[i] = Position[last];
            Velocity[i] = Velocity[last];
            Age[i] = Age[last];
            Life[i] = Life[last];
            Rotation[i] = Rotation[last];
            Spin[i] = Spin[last];
            Follows[i] = Follows[last];
            Offset[i] = Offset[last];
        }
    }

    // Throws `count` particles from a point. `direction` is which way the cone or point faces; for a
    // sphere it is ignored. `follows` makes them ride an entity (an aura, a burning coat).
    public int Emit(RecordId effect, ParticleRecord? record, Vector3 at, Vector3 direction, int count,
                    Entity follows = default)
    {
        if (!Enabled || record == null || count <= 0) return 0;

        var group = GroupFor(effect, record);
        int room = Math.Min(count, record.MaxParticles - group.Count);
        room = Math.Min(room, Budget - Live);

        // Every particle that did not fit is counted, not only the calls where *none* did: "I asked for
        // fifty and got ten" is the thing `fx_stats` is asked about, and a ceiling that hides how much it
        // is turning away is a ceiling nobody can tune.
        if (room < count) Refused += count - Math.Max(room, 0);
        if (room <= 0) return 0;

        group.Grow(group.Count + room);
        if (direction.LengthSquared() < 0.0001f) direction = Vector3.UnitY;
        direction = Vector3.Normalize(direction);

        for (int n = 0; n < room; n++)
        {
            int i = group.Count++;
            Vector3 offset = record.Shape == EmitShape.Box ? RandomInBox(record.Extents) : Vector3.Zero;
            group.Position[i] = at + offset;
            group.Velocity[i] = Direction(record, direction) * Range(record.SpeedMin, record.SpeedMax);
            group.Age[i] = 0f;
            group.Life[i] = Range(record.LifeMin, record.LifeMax);
            group.Rotation[i] = (float)(_random.NextDouble() * Math.Tau);
            group.Spin[i] = record.SpinDegrees * MathF.PI / 180f * (_random.NextDouble() < 0.5 ? -1f : 1f);
            group.Follows[i] = record.Local ? follows : default;
            group.Offset[i] = record.Local ? offset : Vector3.Zero;
            Live++;
        }
        return room;
    }

    // Ages everything by `dt` and drops what is spent. Called once a frame by the client; a headless
    // server never creates this resource at all, so nothing is simulated for nobody.
    public void Update(World world, float dt)
    {
        if (dt <= 0f) return;

        for (int g = 0; g < _order.Count; g++)
        {
            var group = _order[g];
            var record = group.Record;
            float drag = MathF.Max(0f, 1f - record.Drag * dt);

            for (int i = group.Count - 1; i >= 0; i--)
            {
                group.Age[i] += dt;
                if (group.Age[i] >= group.Life[i]) { group.RemoveAt(i); Live--; continue; }

                // **Whether these are carried is the effect's business, not the handle's.** Asking
                // `Follows[i].IsNull` looked equivalent and was not: a destroyed entity's handle reads as
                // null, so a carried particle whose owner had gone quietly became a free one and hung in
                // the air for the rest of its life instead of dying with it.
                if (record.Local)
                {
                    var owner = group.Follows[i];
                    if (owner.IsNull || !world.IsAlive(owner) || !world.TryGet<Transform>(owner, out var at))
                    { group.RemoveAt(i); Live--; continue; }
                    group.Position[i] = at.LocalPosition + group.Offset[i];
                    group.Offset[i] += group.Velocity[i] * dt;
                    group.Velocity[i] = group.Velocity[i] * drag + new Vector3(0, record.Gravity * dt, 0);
                }
                else
                {
                    group.Velocity[i] = group.Velocity[i] * drag + new Vector3(0, record.Gravity * dt, 0);
                    group.Position[i] += group.Velocity[i] * dt;
                }
                group.Rotation[i] += group.Spin[i] * dt;
            }
        }
    }

    // Everything moves with the world (R6): a puff of smoke a kilometre behind where it was blown is
    // the same bug the audio mixer had, and the same one-line answer.
    public void Rebase(Vector3 offset)
    {
        foreach (var group in _order)
        {
            if (group.Record.Local) continue;   // carried ones are wherever their owner is, already moved
            for (int i = 0; i < group.Count; i++) group.Position[i] += offset;
        }
    }

    public void Clear()
    {
        foreach (var group in _order) group.Count = 0;
        Live = 0;
    }

    private Group GroupFor(RecordId effect, ParticleRecord record)
    {
        if (_groups.TryGetValue(effect, out var group))
        {
            group.Record = record;     // hot reload: the same particles, the new numbers
            return group;
        }
        group = new Group { Effect = effect, Record = record };
        _groups[effect] = group;
        _order.Add(group);
        return group;
    }

    private Vector3 Direction(ParticleRecord record, Vector3 forward) => record.Shape switch
    {
        EmitShape.Point => forward,
        EmitShape.Sphere => RandomOnSphere(),
        EmitShape.Box => Vector3.UnitY * -1f,          // rain falls; the box is where it starts
        _ => InCone(forward, record.AngleDegrees),
    };

    private Vector3 InCone(Vector3 forward, float angleDegrees)
    {
        // A direction inside a cone: pick one on the sphere, then bend it toward the axis. Cheap, and
        // the distribution is even enough for sparks.
        float angle = angleDegrees * MathF.PI / 180f;
        var random = RandomOnSphere();
        var mixed = Vector3.Normalize(forward + random * MathF.Tan(angle * 0.5f) * 2f);
        return mixed.LengthSquared() < 0.0001f ? forward : mixed;
    }

    private Vector3 RandomOnSphere()
    {
        double z = _random.NextDouble() * 2 - 1;
        double t = _random.NextDouble() * Math.Tau;
        double r = Math.Sqrt(Math.Max(0, 1 - z * z));
        return new Vector3((float)(r * Math.Cos(t)), (float)z, (float)(r * Math.Sin(t)));
    }

    private Vector3 RandomInBox(Vector3 extents) =>
        new(((float)_random.NextDouble() * 2 - 1) * extents.X,
            ((float)_random.NextDouble() * 2 - 1) * extents.Y,
            ((float)_random.NextDouble() * 2 - 1) * extents.Z);

    private float Range(float min, float max) => max <= min ? min : min + (float)_random.NextDouble() * (max - min);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    // Straight down each channel, alpha included: a fade is the alpha going to zero, which is why the
    // end colour of most effects is the start colour with no alpha.
    private static uint LerpColour(uint a, uint b, float t)
    {
        uint result = 0;
        for (int shift = 0; shift < 32; shift += 8)
        {
            float from = (a >> shift) & 0xFF, to = (b >> shift) & 0xFF;
            result |= (uint)(byte)(from + (to - from) * t) << shift;
        }
        return result;
    }
}
