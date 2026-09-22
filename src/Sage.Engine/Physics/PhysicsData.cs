#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Physics data the simulation holds (docs/design/10 §3). Bepu owns the bodies; components hold
// handles (R9). Everything here is System.Numerics, so it runs headless.

public enum ColliderShape { Box, Sphere, Capsule, Mesh }
public enum BodyKind { Static, Kinematic, Dynamic }

// The shape and collision behaviour of an entity. Size means:
//   Box     full extents
//   Sphere  X = radius
//   Capsule X = radius, Y = length of the cylinder between the caps
//   Mesh    a mesh the game registered with the space (terrain does this itself)
public struct Collider : IComponent
{
    public ColliderShape Shape;
    public Vector3 Size;
    public byte Layer;        // index into the physics_layers record (0 = "default")
    public bool IsTrigger;    // generates overlap events, never a collision response

    public static Collider Box(Vector3 size, byte layer = 0) => new() { Shape = ColliderShape.Box, Size = size, Layer = layer };
    public static Collider Sphere(float radius, byte layer = 0) => new() { Shape = ColliderShape.Sphere, Size = new Vector3(radius, 0, 0), Layer = layer };
    public static Collider Capsule(float radius, float length, byte layer = 0) => new() { Shape = ColliderShape.Capsule, Size = new Vector3(radius, length, 0), Layer = layer };
}

// How the collider moves. Static never moves, Kinematic is moved by gameplay (the transform wins),
// Dynamic is moved by physics (the body wins, and its pose is written back to the transform).
public struct RigidBody : IComponent
{
    public BodyKind Kind;
    public float Mass;          // dynamic only; <= 0 becomes 1
    public float Friction;      // 0..1-ish, default 0.7 when left at 0
    public float Restitution;   // bounciness 0..1

    public static RigidBody Dynamic(float mass) => new() { Kind = BodyKind.Dynamic, Mass = mass };
    public static RigidBody Kinematic() => new() { Kind = BodyKind.Kinematic };
}

// The Bepu handle for an entity's collider, added and removed by the physics systems. Never authored
// and never saved: it is rebuilt from Collider/RigidBody on load (09 §3.5).
public struct PhysicsBody : IComponent
{
    public int Handle;
    public bool IsStatic;
}

// Which layers collide with which (10 §3). Layer 0 is "default"; a missing entry means "collides".
[Record("physics_layers")]
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

    public int IndexOf(string name)
    {
        for (int i = 0; i < 32; i++)
            if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public bool Collide(int a, int b) => (_masks[a & 31] & (1u << (b & 31))) != 0;

    public void Apply(PhysicsLayersRecord record)
    {
        for (int i = 0; i < 32; i++) { _masks[i] = uint.MaxValue; _names[i] = i == 0 ? "default" : $"layer{i}"; }
        for (int i = 0; i < record.Layers.Count && i < 32; i++) _names[i] = record.Layers[i];
        if (record.Layers.Count > 32) Log.Error(LogCat.Physics, $"physics_layers: {record.Layers.Count} layers, only the first 32 are used");

        foreach (var (name, ignored) in record.Ignore)
        {
            int a = IndexOf(name);
            if (a < 0) { Log.Warn(LogCat.Physics, $"physics_layers: unknown layer '{name}' in \"ignore\""); continue; }
            foreach (var other in ignored)
            {
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
    public bool StartsTouching;  // already overlapping at the start
}

// Trigger overlaps collected during the step, drained in PostPhysics (10 §3). Game events come with
// the event bus (04); until then systems read these lists.
public readonly record struct TriggerOverlap(Entity Trigger, Entity Other);
