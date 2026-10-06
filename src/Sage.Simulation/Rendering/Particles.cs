#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

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

// What a particle does when it meets the world (issue 4n-6). Off by default: a ray per spark is not free,
// and smoke, embers and rain have nothing to say to the floor.
public enum ParticleCollision
{
    None,         // passes through everything, as every particle did before
    Bounce,       // reflects off what it hits, losing `restitution` and `friction`: sparks on stone
    Die,          // ends where it hits: rain, blood
}

// What a puff of something looks like, as data (05 §3.5). One record covers a burst and a steady
// stream, because the difference is `burst` against `rate` and nothing else.
[Record("particle", Plugin = "sage.client")]
public sealed class ParticleRecord
{
    [AssetKind("texture")] public AssetPath Texture;                 // the sprite each particle draws
    public RecordRef<MaterialRecord> Material; // empty = the engine's additive particle material

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
    // A constant push, like gravity but in any direction: what makes rain fall slanted and smoke lean
    // away from a fire. Weather sets it per frame (06 §3.13); a record may bake one in.
    public Vector3 Wind;
    public float Drag;                        // fraction of speed lost per second
    public float SizeStart = 0.15f, SizeEnd = 0.05f;
    // "#RRGGBBAA" or [r, g, b, a] in a record (ColourJsonConverter), and the alpha is what fades.
    [System.Text.Json.Serialization.JsonConverter(typeof(ColourJsonConverter))]
    public uint ColourStart = 0xFFFFFFFF;
    [System.Text.Json.Serialization.JsonConverter(typeof(ColourJsonConverter))]
    public uint ColourEnd = 0x00FFFFFF;
    public float SpinDegrees;                 // turned per second, for smoke and leaves

    // World, or carried: a spark stays where it was thrown, a magic aura moves with its owner.
    public bool Local;

    // ---- Meeting the world (issue 4n-6) ----
    //
    // A free particle that collides casts one ray a frame along the way it is moving, through the world's
    // physics (`IPhysicsWorld.Raycast`, triggers left out), within `Particles.CollisionBudget` rays a frame
    // for everything at once. A carried (`local`) effect never collides: it is wherever its owner is.
    [Property(Tooltip = "What a particle does when it meets the world: None (passes through), Bounce, or Die")]
    public ParticleCollision Collision;
    [Property(Min = 0, Max = 1, Tooltip = "Bounce: the share of speed into the surface kept after a bounce; 0 = stops dead on it")]
    public float Restitution = 0.4f;
    [Property(Min = 0, Max = 1, Tooltip = "Bounce: the share of speed along the surface lost at each bounce")]
    public float Friction = 0.2f;

    // ---- A sprite sheet over a life (issue 4n-6) ----
    //
    // The texture as a grid of `sheetColumns` x `sheetRows` frames, read left to right and top to bottom;
    // each particle runs through `sheetFrames` of them (0: all) `sheetCycles` times between birth and
    // death. 1 x 1, the default, is the whole texture, as before.
    [Property(Min = 1, Max = 64, Category = "Sheet", Tooltip = "Frames across the texture")]
    public int SheetColumns = 1;
    [Property(Min = 1, Max = 64, Category = "Sheet", Tooltip = "Frames down the texture")]
    public int SheetRows = 1;
    [Property(Min = 0, Category = "Sheet", Tooltip = "How many of the grid's frames are used, in order; 0 = all of them")]
    public int SheetFrames;
    [Property(Min = 0, Category = "Sheet", Tooltip = "How many times a particle runs through its frames over its life; 0 = holds one frame")]
    public float SheetCycles = 1f;
    [Property(Category = "Sheet", Tooltip = "Each particle starts on a frame of its own, so a cloud of them does not flicker in step")]
    public bool SheetRandomStart;

    public static readonly RecordId DefaultMaterial = new("sage", "particle_additive");

    // The frames a particle runs through: the grid's, or fewer when `sheetFrames` says.
    internal int FrameCount => SheetFrames > 0 ? SheetFrames : Math.Max(1, SheetColumns) * Math.Max(1, SheetRows);

    // Load: a sheet's numbers fit its grid, and collision's are fractions. A mistake is a load error at its
    // line (issue 4n-6), not a particle drawn from the wrong corner of its texture.
    internal static void Check(ParticleRecord record, RecordCheck check)
    {
        if (record.SheetColumns < 1 || record.SheetColumns > 64) check.Error("sheetColumns", "\"sheetColumns\" is a whole number from 1 to 64");
        if (record.SheetRows < 1 || record.SheetRows > 64) check.Error("sheetRows", "\"sheetRows\" is a whole number from 1 to 64");
        if (record.SheetFrames < 0) check.Error("sheetFrames", "\"sheetFrames\" cannot be negative (0 = every frame of the grid)");
        else if (record.SheetColumns >= 1 && record.SheetRows >= 1 && record.SheetFrames > record.SheetColumns * record.SheetRows)
            check.Error("sheetFrames", $"\"sheetFrames\" is more than the {record.SheetColumns} x {record.SheetRows} grid holds ({record.SheetColumns * record.SheetRows})");
        if (!(record.SheetCycles >= 0f) || float.IsInfinity(record.SheetCycles)) check.Error("sheetCycles", "\"sheetCycles\" is a number of 0 or more");
        if (!(record.Restitution >= 0f && record.Restitution <= 1f)) check.Error("restitution", "\"restitution\" is a fraction from 0 to 1");
        if (!(record.Friction >= 0f && record.Friction <= 1f)) check.Error("friction", "\"friction\" is a fraction from 0 to 1");
        if (record.Collision != ParticleCollision.None && record.Local)
            check.Warn("collision", "is not read on a local (carried) effect: its particles are wherever their owner is");
    }
}

// An entity that emits while it exists: a torch's embers, rain over the player, an aura. The component
// is the engine's, so a headless run carries it and simply never draws anything.
[Component("sage:particle_emitter")]
public struct ParticleEmitter : IComponent
{
    [RecordRef("particle")] public RecordId Effect;
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

    // Rays a frame for every colliding particle at once (issue 4n-6). Past it, the rest move unchecked
    // that frame — and may pass through a thin floor — and are counted in `RaysSkipped`, so a scene that
    // needs more says so in `fx_stats` instead of quietly losing its sparks through the ground.
    public int CollisionBudget { get; set; } = 512;

    public int Rays { get; private set; }         // cast since ResetStats, for `fx_stats`
    public int RaysSkipped { get; private set; }  // wanted beyond the budget since ResetStats
    public int Hits { get; private set; }         // particles that met the world since ResetStats

    public bool Enabled { get; set; } = true;

    // What the weather is doing to everything at once (06 §3.13). On top of each record's own wind, so
    // a fire's embers lean in the same gale that drives the rain.
    public Vector3 Wind { get; set; }

    public IReadOnlyList<Group> Groups => _order;

    public void ResetStats() => Refused = Rays = RaysSkipped = Hits = 0;

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
        public int[] FirstFrame = Array.Empty<int>();       // `sheetRandomStart`: where on the sheet it began
        public int Count;

        // Where a particle is now and what it looks like: the two questions the renderer asks and the
        // only two a test needs. Size and colour are pure functions of age, so nothing stores them.
        public float Fraction(int i) => Life[i] <= 0f ? 1f : Math.Clamp(Age[i] / Life[i], 0f, 1f);

        public float SizeOf(int i) => Lerp(Record.SizeStart, Record.SizeEnd, Fraction(i));

        public uint ColourOf(int i) => LerpColour(Record.ColourStart, Record.ColourEnd, Fraction(i));

        // Which frame of its sheet it shows (issue 4n-6): its share of life, times the cycles, across the
        // frames — the last frame at death rather than wrapping to the first — then moved on by where it
        // began. 0 for a 1 x 1 sheet.
        public int FrameOf(int i) => Frame(Record, Fraction(i), FirstFrame[i]);

        // That frame as UVs in the texture: u0, v0, u1, v1, as the sprite batcher takes them.
        public Vector4 UvOf(int i) => FrameUv(Record, FrameOf(i));
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
            Array.Resize(ref FirstFrame, size);
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
            FirstFrame[i] = FirstFrame[last];
        }
    }

    // Throws `count` particles from a point. `direction` is which way the cone or point faces; for a
    // sphere it is ignored. `follows` makes them ride an entity (an aura, a burning coat).
    // `volume` overrides the record's own `extents` for this call: how wide the rain is belongs to the
    // weather, while how a drop behaves belongs to the effect (06 §3.13). Passed rather than written into
    // the record, because **a record is content and content is read-only at runtime** — the first version
    // assigned `effect.Extents` every frame, which is one storm quietly editing the effect every other
    // storm shares.
    public int Emit(RecordId effect, ParticleRecord? record, Vector3 at, Vector3 direction, int count,
                    Entity follows = default, Vector3? volume = null)
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
            Vector3 offset = record.Shape == EmitShape.Box ? RandomInBox(volume ?? record.Extents) : Vector3.Zero;
            group.Position[i] = at + offset;
            group.Velocity[i] = Direction(record, direction) * Range(record.SpeedMin, record.SpeedMax);
            group.Age[i] = 0f;
            group.Life[i] = Range(record.LifeMin, record.LifeMax);
            group.Rotation[i] = (float)(_random.NextDouble() * Math.Tau);
            group.Spin[i] = record.SpinDegrees * MathF.PI / 180f * (_random.NextDouble() < 0.5 ? -1f : 1f);
            group.Follows[i] = record.Local ? follows : default;
            group.Offset[i] = record.Local ? offset : Vector3.Zero;
            group.FirstFrame[i] = record.SheetRandomStart ? _random.Next(record.FrameCount) : 0;
            Live++;
        }
        return room;
    }

    // Ages everything by `dt` and drops what is spent. Called once a frame by the client; a headless
    // server never creates this resource at all, so nothing is simulated for nobody.
    public void Update(World world, float dt)
    {
        if (dt <= 0f) return;
        world.Resources.TryGet<IPhysicsWorld>(out var physics);
        int rays = 0;

        for (int g = 0; g < _order.Count; g++)
        {
            var group = _order[g];
            var record = group.Record;
            float drag = MathF.Max(0f, 1f - record.Drag * dt);
            var push = (new Vector3(0, record.Gravity, 0) + record.Wind + Wind) * dt;
            bool collides = record.Collision != ParticleCollision.None && physics != null;

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
                    group.Velocity[i] = group.Velocity[i] * drag + push;
                }
                else
                {
                    group.Velocity[i] = group.Velocity[i] * drag + push;
                    var step = group.Velocity[i] * dt;
                    if (collides && step.LengthSquared() > 1e-12f)
                    {
                        if (rays >= CollisionBudget) RaysSkipped++;
                        else
                        {
                            rays++;
                            Rays++;
                            if (Collide(physics!, record, ref group.Position[i], ref group.Velocity[i], step))
                            {
                                Hits++;
                                if (record.Collision == ParticleCollision.Die) { group.RemoveAt(i); Live--; continue; }
                                group.Rotation[i] += group.Spin[i] * dt;
                                continue;
                            }
                        }
                    }
                    group.Position[i] += step;
                }
                group.Rotation[i] += group.Spin[i] * dt;
            }
        }
    }

    // One particle's move against the world: a ray along this frame's `step`, and on a hit it stops on the
    // surface (a hair off it, so the next ray does not start inside) and, for Bounce, turns its velocity —
    // the part into the surface reflected and scaled by `restitution`, the part along it scaled by
    // `1 - friction`. A surface it is leaving (normal along the step) is not a hit. True when it hit.
    internal static bool Collide(IPhysicsWorld physics, ParticleRecord record, ref Vector3 position, ref Vector3 velocity, Vector3 step)
    {
        float length = step.Length();
        var direction = step / length;
        var hit = physics.Raycast(position, direction, length);
        if (!hit.Hit || Vector3.Dot(hit.Normal, direction) >= 0f) return false;

        position = hit.Position + hit.Normal * SurfaceGap;
        if (record.Collision == ParticleCollision.Bounce) velocity = Bounce(velocity, hit.Normal, record.Restitution, record.Friction);
        return true;
    }

    // How far off a surface a particle is left when it meets one.
    internal const float SurfaceGap = 0.005f;

    // A velocity after a bounce off a surface with `normal` (see Collide).
    internal static Vector3 Bounce(Vector3 velocity, Vector3 normal, float restitution, float friction)
    {
        float into = Vector3.Dot(velocity, normal);
        if (into >= 0f) return velocity;                       // already leaving it
        var across = velocity - normal * into;
        return across * (1f - Math.Clamp(friction, 0f, 1f)) - normal * (into * Math.Clamp(restitution, 0f, 1f));
    }

    // The frame of `record`'s sheet at `fraction` of a life, for a particle that began on `first` (see
    // Group.FrameOf).
    internal static int Frame(ParticleRecord record, float fraction, int first = 0)
    {
        int frames = record.FrameCount;
        if (frames <= 1) return 0;
        float span = MathF.Max(0f, record.SheetCycles) * frames;
        int step = (int)(Math.Clamp(fraction, 0f, 1f) * span);
        int last = Math.Max(0, (int)MathF.Ceiling(span) - 1);
        if (step > last) step = last;
        return (int)((uint)(step + first) % (uint)frames);
    }

    // Frame `frame` of `record`'s grid as UVs (u0, v0, u1, v1): left to right, then top to bottom.
    internal static Vector4 FrameUv(ParticleRecord record, int frame)
    {
        int columns = Math.Max(1, record.SheetColumns), rows = Math.Max(1, record.SheetRows);
        if (columns == 1 && rows == 1) return new Vector4(0f, 0f, 1f, 1f);
        int column = frame % columns, row = frame / columns % rows;
        float w = 1f / columns, h = 1f / rows;
        return new Vector4(column * w, row * h, (column + 1) * w, (row + 1) * h);
    }

    // Whether fog hides a particle wholly (issue 4n-6): a camera-relative centre and its size, against
    // the fog's cull distance (`FogMath.Hides`). The client asks it only for a material with fog on.
    internal static bool FogHides(float cullDistance, Vector3 relative, float size) =>
        FogMath.Hides(cullDistance, relative, size * 0.71f);   // the quad's corner: half its diagonal

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
