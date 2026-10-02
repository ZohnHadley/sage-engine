#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Physics data the simulation holds (docs/design/10 §3). The physics backend (IPhysicsWorld; Bepu in
// Sage.Physics3D) owns the bodies; components hold handles (R9). Everything here is System.Numerics and
// names no backend, so it runs headless and means the same to a 2D backend (REDESIGN §0.5).

public enum ColliderShape { Box, Sphere, Capsule, Mesh }
public enum BodyKind { Static, Kinematic, Dynamic }

// The shape and collision behaviour of an entity. Size means:
//   Box     full extents
//   Sphere  X = radius
//   Capsule X = radius, Y = length of the cylinder between the caps
//   Mesh    a mesh the game registered with the space (terrain does this itself)
//
// A shape is centred on `Center`, an offset from the entity's own origin. A backend poses a shape by its
// centre, so anything whose transform sits at its *feet* — a character, a tree — needs an offset of
// half its height, or its collider ends up buried to the waist while the thing appears to stand on
// the ground (review #44). Standing() does that for you.
[Component("sage:collider")]
public struct Collider : IComponent
{
    [Property(Category = "Shape", Tooltip = "Box, Sphere, Capsule or Mesh")]
    public ColliderShape Shape;
    [Property(Category = "Shape", Min = 0, Unit = "m", Tooltip = "Box: full extents. Sphere: X is the radius. Capsule: X radius, Y cylinder length")]
    public Vector3 Size;
    [Property(Category = "Shape", Unit = "m", Tooltip = "Offset from the entity's origin to the shape's centre")]
    public Vector3 Center;    // local offset from the entity's origin to the shape's centre
    [Property(Category = "Collision", Min = 0, Max = 31, Tooltip = "Index into the physics_layers record (0 = default)")]
    public byte Layer;        // index into the physics_layers record (0 = "default")
    [Property(Category = "Collision", Tooltip = "Reports overlaps and never blocks")]
    public bool IsTrigger;    // generates overlap events, never a collision response
    [Property(Category = "Collision", Tooltip = "Reports contact begin and end events")]
    public bool ReportContacts;   // opt-in: tracking every pair of a crowded world would cost every tick

    // The shortest cylinder a capsule may keep: two hemispheres and nothing between them is still a
    // capsule, but a negative length is not a shape.
    internal const float MinCylinder = 0.01f;

    public static Collider Box(Vector3 size, byte layer = 0) => new() { Shape = ColliderShape.Box, Size = size, Layer = layer };
    public static Collider Sphere(float radius, byte layer = 0) => new() { Shape = ColliderShape.Sphere, Size = new Vector3(radius, 0, 0), Layer = layer };
    public static Collider Capsule(float radius, float length, byte layer = 0) => new() { Shape = ColliderShape.Capsule, Size = new Vector3(radius, length, 0), Layer = layer };

    // A capsule of `totalHeight` standing on the entity's origin: the shape for anything with feet.
    // It owns the height -> cylinder conversion, so the character controller's sweeps and the body it
    // pushes into Bepu can't drift apart.
    public static Collider Standing(float radius, float totalHeight, byte layer = 0) => new()
    {
        Shape = ColliderShape.Capsule,
        Size = new Vector3(radius, MathF.Max(totalHeight - 2f * radius, MinCylinder), 0),
        Center = new Vector3(0, totalHeight * 0.5f, 0),
        Layer = layer,
    };

    // Where the shape's centre is, for an entity at `pose`.
    public Vector3 CenterAt(in Pose pose) =>
        Center == Vector3.Zero ? pose.Position : pose.Position + Vector3.Transform(Center, pose.Rotation);
}

// How the collider moves. Static never moves, Kinematic is moved by gameplay (the transform wins),
// Dynamic is moved by physics (the body wins, and its pose is written back to the transform).
[Component("sage:rigid_body")]
public struct RigidBody : IComponent
{
    [Property(Tooltip = "Static never moves, Kinematic is moved by gameplay, Dynamic by physics")]
    public BodyKind Kind;
    [Property(Min = 0, Unit = "kg", Tooltip = "Dynamic bodies only; 0 becomes 1")]
    public float Mass;          // dynamic only; <= 0 becomes 1
    [Property(Min = 0, Max = 1, Tooltip = "0 slides like ice; 0 means the default, 0.7")]
    public float Friction;      // 0..1-ish, default 0.7 when left at 0
    [Property(Min = 0, Max = 1, Tooltip = "Bounciness: 0 stops dead, 1 bounces back as fast")]
    public float Restitution;   // bounciness 0..1

    public static RigidBody Dynamic(float mass) => new() { Kind = BodyKind.Dynamic, Mass = mass };
    public static RigidBody Kinematic() => new() { Kind = BodyKind.Kinematic };
}

// The backend's handle for an entity's collider (a Bepu body or static handle in 3D), added and removed
// by the physics systems. Never authored and never saved: it is rebuilt from Collider/RigidBody on load
// (09 §3.5).
[Transient]
[Component("sage:physics_body")]
public struct PhysicsBody : IComponent
{
    public int Handle;
    public bool IsStatic;
}

// Which layers collide with which (10 §3). Layer 0 is "default"; a missing entry means "collides".
// `"ignore": { "hitbox": ["*"] }` makes a layer collide with nothing at all: a query-only layer
// (LayerMatrix.QueryOnly, issue #137).
[Record("physics_layers", Plugin = "sage.physics3d")]
public sealed class PhysicsLayersRecord
{
    public List<string> Layers = new();                                  // index → name
    public Dictionary<string, List<string>> Ignore = new(StringComparer.OrdinalIgnoreCase);   // name → layers it does NOT collide with

    public static readonly RecordId Default = new("sage", "default");
}

// A 32-layer collision matrix, built from the record.
public sealed class LayerMatrix
{
    private readonly uint[] _masks = new uint[32];
    private readonly string[] _names = new string[32];

    public LayerMatrix()
    {
        for (int i = 0; i < 32; i++) { _masks[i] = uint.MaxValue; _names[i] = i == 0 ? "default" : $"layer{i}"; }
    }

    public string Name(int layer) => _names[layer & 31];

    // The layers the engine's own code needs by name (10 §3). Resolved from the record once, so a game
    // that reorders physics_layers can't silently repoint AI vision or a character's self-exclusion at
    // the wrong layer (review #45).
    public byte Default { get; private set; }
    public byte Player { get; private set; } = 1;
    public byte Enemy { get; private set; } = 2;
    public byte Trigger { get; private set; } = 4;

    public int IndexOf(string name)
    {
        for (int i = 0; i < 32; i++)
            if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public bool Collide(int a, int b) => (_masks[a & 31] & (1u << (b & 31))) != 0;

    // The layers that collide with nothing (`"ignore": { "hitbox": ["*"] }`, issue #137): shapes that
    // exist only to be asked about — a creature's hitboxes. A query sees them only when its mask names
    // nothing else (`LayerMask.Only(hitbox)`), so every query written for solid things — the character
    // controller's sweeps, foot IK's rays, AI sight, a swing — goes on seeing exactly what it saw.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public LayerMask QueryOnly => new(_queryOnly);
    private uint _queryOnly;

    // Does a query with `mask` see a collider on `layer`? The mask names it, and a query-only layer
    // only when the mask names nothing but query-only layers (see QueryOnly).
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public bool Sees(LayerMask mask, int layer)
    {
        uint bit = 1u << (layer & 31);
        if ((mask.Bits & bit) == 0) return false;
        return (_queryOnly & bit) == 0 || (mask.Bits & ~_queryOnly) == 0;
    }

    // A layer by name, for data that names one (a prefab's "character" part). The engine's own four
    // are answered from the resolved indices rather than the name table, so a game that never wrote
    // a physics_layers record still gets "enemy" instead of silently getting the default layer.
    public bool TryIndexOf(string name, out byte layer)
    {
        switch (name.ToLowerInvariant())
        {
            case "default": layer = Default; return true;
            case "player":  layer = Player;  return true;
            case "enemy":   layer = Enemy;   return true;
            case "trigger": layer = Trigger; return true;
        }
        int index = IndexOf(name);
        layer = index < 0 ? Default : (byte)index;
        return index >= 0;
    }

    // The index of a layer the engine expects to exist, or `fallback` with a warning: a game may drop
    // one, and losing a layer should not take the simulation down.
    private byte Named(string name, byte fallback)
    {
        int index = IndexOf(name);
        if (index >= 0) return (byte)index;
        Log.Warn(LogCat.Physics, $"physics_layers has no '{name}' layer; the engine falls back to index {fallback} ('{Name(fallback)}')");
        return fallback;
    }

    public void Apply(PhysicsLayersRecord record)
    {
        for (int i = 0; i < 32; i++) { _masks[i] = uint.MaxValue; _names[i] = i == 0 ? "default" : $"layer{i}"; }
        _queryOnly = 0;
        for (int i = 0; i < record.Layers.Count && i < 32; i++) _names[i] = record.Layers[i];
        if (record.Layers.Count > 32) Log.Error(LogCat.Physics, $"physics_layers: {record.Layers.Count} layers, only the first 32 are used");

        Default = 0;
        Player = Named("player", 1);
        Enemy = Named("enemy", 2);
        Trigger = Named("trigger", 4);

        foreach (var (name, ignored) in record.Ignore)
        {
            int a = IndexOf(name);
            if (a < 0) { Log.Warn(LogCat.Physics, $"physics_layers: unknown layer '{name}' in \"ignore\""); continue; }
            foreach (var other in ignored)
            {
                // "*": every layer, including ones a later patch appends — a query-only layer (QueryOnly).
                if (other == "*")
                {
                    for (int k = 0; k < 32; k++) { _masks[a] &= ~(1u << k); _masks[k] &= ~(1u << a); }
                    _queryOnly |= 1u << a;
                    continue;
                }
                int b = IndexOf(other);
                if (b < 0) { Log.Warn(LogCat.Physics, $"physics_layers: unknown layer '{other}' ignored by '{name}'"); continue; }
                _masks[a] &= ~(1u << b);
                _masks[b] &= ~(1u << a);   // symmetric
            }
        }
    }
}

// Which layers a query looks at.
public readonly record struct LayerMask(uint Bits)
{
    public static LayerMask All => new(uint.MaxValue);
    public static LayerMask Only(params int[] layers)
    {
        uint bits = 0;
        foreach (int l in layers) bits |= 1u << (l & 31);
        return new LayerMask(bits);
    }

    public bool Has(int layer) => (Bits & (1u << (layer & 31))) != 0;

    // Everything but this layer: what the character controller sweeps with every tick, so it never
    // hits itself. No params array, because this runs per character per tick.
    public LayerMask Except(int layer) => new(Bits & ~(1u << (layer & 31)));

    public LayerMask Except(params int[] layers)
    {
        uint bits = Bits;
        foreach (int l in layers) bits &= ~(1u << (l & 31));
        return new LayerMask(bits);
    }
}

public struct RayHit
{
    public Entity Entity;
    public Vector3 Position;
    public Vector3 Normal;
    public float Distance;
    public bool Hit;
}

public struct SweepHit
{
    public Entity Entity;
    public Vector3 Position;
    public Vector3 Normal;
    public float Distance;      // along the sweep direction
    public bool Hit;
    // The shape already overlapped this where the sweep started (IPhysicsWorld.Sweep): Distance is 0,
    // Position is where the shape started and Normal is -direction, because an overlap has no surface.
    public bool StartsInside;
}

// What a shape intersects (IPhysicsWorld.Overlap, issue #259): Normal is the way out, pointing from the
// other collider toward the shape, and moving the shape Depth metres along it separates the two.
public struct OverlapHit
{
    public Entity Entity;
    public Vector3 Normal;
    public float Depth;
}

// Trigger overlaps collected during the step, drained in PostPhysics (10 §3). The physics plugin also
// sends them on the event bus as TriggerEntered/TriggerExited (PhysicsEvents.cs, issue #269).
public readonly record struct TriggerOverlap(Entity Trigger, Entity Other);

// A solid contact between two colliders, at least one of which asked for them (Collider.ReportContacts).
// Normal points from B toward A; Point is where they touch. An end event carries the entities only.
// Speed and Impulse say how hard a beginning was (issue #269; Collided says how they are worked out).
public readonly record struct ContactEvent(Entity A, Entity B, Vector3 Point, Vector3 Normal)
{
    public float Impulse { get; init; }   // N·s
    public float Speed { get; init; }     // closing speed along the normal, m/s
}
