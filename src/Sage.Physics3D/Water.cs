#nullable enable
using System;
using System.Numerics;

namespace Sage.Physics3D;

// Water (issue #262, docs/design/10 §3.6): finding the water at a point, and floating dynamic bodies in
// it. The character controller swims with the same lookup (CharacterMovementSystem).

// The water a thing is in: the volume, its surface height and what it does to what moves through it.
internal struct WaterSample
{
    public Entity Volume;
    public float Surface;
    public float Drag;
    public float Buoyancy;
    public Vector3 Current;
}

// The world's water volumes, asked "what water covers this point". A scan: a level has a handful of
// volumes, and a world with none costs one count per asker per tick.
internal sealed class WaterVolumes
{
    private readonly Query<Transform, WaterVolume> _volumes;

    public WaterVolumes(World world) { _volumes = world.Query<Transform, WaterVolume>(); }

    public bool Any => _volumes.Count > 0;

    // The water whose footprint covers `point` and whose box reaches between `bottom` and `top` (a
    // shape's vertical extent); of several, the one with the highest surface. False if there is none.
    public bool Find(Vector3 point, float bottom, float top, out WaterSample sample)
    {
        sample = default;
        bool found = false;
        if (_volumes.Count == 0) return false;

        foreach (var (transforms, volumes, entities) in _volumes.Chunks)
        {
            var t = transforms.Span;
            var v = volumes.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                Vector3 origin = Origin(entity, t[n]);
                if (!v[n].Covers(origin, point)) continue;
                float surface = v[n].SurfaceAt(origin);
                float floor = origin.Y - v[n].Size.Y * 0.5f;
                if (bottom >= surface || top <= floor) continue;
                if (found && surface <= sample.Surface) continue;

                found = true;
                sample = new WaterSample
                {
                    Volume = entity,
                    Surface = surface,
                    Drag = v[n].Drag,
                    Buoyancy = v[n].Buoyancy,
                    Current = v[n].Current,
                };
            }
        }
        return found;
    }

    // A root volume's transform is its place; one parented to something (a level) is where its
    // GlobalTransform says.
    private static Vector3 Origin(Entity entity, in Transform transform) =>
        !entity.Parent.IsNull && entity.TryGetComponent<GlobalTransform>(out var global) ? global.Current.Position : transform.LocalPosition;
}

// PrePhysics, once the bodies exist and before the step: lifts every awake dynamic body by how much of
// it is under water, drags it toward the water's current, and damps its spin. The lift is a multiple
// of the body's weight (WaterVolume.Buoyancy), so mass cancels: buoyancy 2 floats any body half under.
// A body is measured by its shape's vertical extent, which is exact for spheres and upright boxes and
// near enough for the rest. A sleeping body is left alone: it is resting, and waking it every tick would
// keep a settled crate awake for ever.
[System("sage.physics.buoyancy", Phase.PrePhysics, After = new[] { "sage.physics.sync" })]
internal sealed class BuoyancySystem : ISystem
{
    private readonly PhysicsSpace _space;
    private readonly WaterVolumes _water;
    private readonly Query<Collider, PhysicsBody> _bodies;

    public BuoyancySystem(World world, PhysicsSpace space)
    {
        _space = space;
        _water = new WaterVolumes(world);
        _bodies = world.Query<Collider, PhysicsBody>();
    }

    public void Run(in SystemContext ctx)
    {
        if (!_water.Any) return;
        float dt = ctx.Tick.Dt;
        Vector3 gravity = _space.Gravity;

        foreach (var (colliders, handles, _) in _bodies.Chunks)
        {
            var c = colliders.Span;
            var h = handles.Span;
            for (int n = 0; n < c.Length; n++)
            {
                if (h[n].IsStatic || c[n].IsTrigger || !_space.IsDynamic(h[n]) || !_space.IsAwake(h[n])) continue;
                var pose = _space.PoseOf(h[n]);
                float half = HalfHeight(c[n], pose.Rotation);
                if (half <= 0f) continue;
                Vector3 centre = pose.Position;
                if (!_water.Find(centre, centre.Y - half, centre.Y + half, out var water)) continue;

                float submerged = Math.Clamp((water.Surface - (centre.Y - half)) / (2f * half), 0f, 1f);
                if (submerged <= 0f) continue;

                Vector3 velocity = _space.VelocityOf(h[n]);
                velocity -= gravity * (water.Buoyancy * submerged * dt);
                velocity = Drag(velocity, water.Current, water.Drag * submerged, dt);
                _space.SetVelocity(h[n], velocity);

#pragma warning disable SAGE0134 // spin: experimental with the joints, and the physics plugin ships with the engine that declares it
                Vector3 spin = _space.AngularVelocityOf(h[n]);
                if (spin != Vector3.Zero) _space.SetAngularVelocity(h[n], Drag(spin, Vector3.Zero, water.Drag * submerged, dt));
#pragma warning restore SAGE0134
            }
        }
    }

    // `velocity` relative to `current`, slowed by `rate` per second: what the water does to anything
    // moving through it (the character controller's swim uses the same).
    internal static Vector3 Drag(Vector3 velocity, Vector3 current, float rate, float dt) =>
        current + (velocity - current) * MathF.Max(0f, 1f - rate * dt);

    // How far the shape reaches above and below its centre, rotated.
    private static float HalfHeight(in Collider collider, Quaternion rotation)
    {
        switch (collider.Shape)
        {
            case ColliderShape.Sphere:
                return collider.Size.X;
            case ColliderShape.Capsule:
                return collider.Size.Y * 0.5f * MathF.Abs(Vector3.Transform(Vector3.UnitY, rotation).Y) + collider.Size.X;
            case ColliderShape.Box:
                Vector3 half = collider.Size * 0.5f;
                return half.X * MathF.Abs(Vector3.Transform(Vector3.UnitX, rotation).Y)
                     + half.Y * MathF.Abs(Vector3.Transform(Vector3.UnitY, rotation).Y)
                     + half.Z * MathF.Abs(Vector3.Transform(Vector3.UnitZ, rotation).Y);
            default:
                return 0f;   // a mesh: terrain, never dynamic
        }
    }
}
