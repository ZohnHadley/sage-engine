#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities;

namespace Sage.Physics3D;

#pragma warning disable SAGE0134 // joints and groups: this is the backend that implements them

// Shapes changed in place and parented colliders (issue #268, docs/design/10 "As built (shapes in place
// and parented colliders)").
//
// SetShape swaps a body's or a static's shape without a new handle, so what refers to it — the entity's
// PhysicsBody, a joint, a collision group — keeps working: a crouching character's capsule shrinks this
// way. The new shape is the body's own (never the size-keyed cache: a smooth crouch would leave a cached
// capsule behind for every height it passed through), and the one it replaces is released when it was
// the body's own too, so swapping a shape every tick leaks nothing.
//
// A dynamic body with colliders on child entities (that have no body of their own) is one compound:
// SetCompound, from PhysicsSyncSystem. Its children are the cached shapes, so releasing the compound
// releases only its own child list.
public sealed partial class PhysicsSpace
{
    public void SetShape(in PhysicsBody body, in Collider collider, in Pose pose)
    {
        if (collider.Shape == ColliderShape.Mesh)
        {
            Log.Warn(LogCat.Physics, $"SetShape: {World.Describe(_data.BodyEntity(body.Handle, body.IsStatic))} asked for a Mesh; meshes are built by the engine (AddMesh), so its shape is left as it is");
            return;
        }
        var rigidPose = new RigidPose(collider.CenterAt(pose), pose.Rotation);
        if (body.IsStatic)
        {
            var handle = new StaticHandle(body.Handle);
            if (!Simulation.Statics.StaticExists(handle)) return;
            var shape = AddOwnShape(collider, 1f, out _);
            Simulation.Statics.GetDescription(handle, out var description);
            description.Shape = shape;
            description.Pose = rigidPose;
            Simulation.Statics.ApplyDescription(handle, description);   // wakes what slept against the old one, refreshes bounds
            if (_ownedShapes.Remove(body.Handle, out var old)) Simulation.Shapes.RemoveAndDispose(old, _pool);
            _ownedShapes[body.Handle] = shape;
            _hullOffsets.Remove(body.Handle);
        }
        else
        {
            var handle = new BodyHandle(body.Handle);
            if (!Simulation.Bodies.BodyExists(handle)) return;
            var reference = Simulation.Bodies[handle];
            bool dynamic = !reference.Kinematic;
            float mass = dynamic && reference.LocalInertia.InverseMass > 0f ? 1f / reference.LocalInertia.InverseMass : 1f;
            var shape = AddOwnShape(collider, mass, out var inertia);
            Simulation.Bodies.SetShape(handle, shape);
            if (dynamic) Simulation.Bodies.SetLocalInertia(handle, inertia);
            reference.Pose = rigidPose;
            reference.Awake = true;
            Simulation.Bodies.UpdateBounds(handle);
            ReleaseBodyShape(body.Handle);
            _ownedBodyShapes[body.Handle] = shape;
            _data.SetParts(body.Handle, null);
        }
        _data.Reconfigure(body.Handle, body.IsStatic, collider.Layer, collider.IsTrigger, collider.ReportContacts);
    }

    // A collider shaped for one body alone. Allocates nothing managed: Bepu's shape batches come from its pool.
    private TypedIndex AddOwnShape(in Collider collider, float mass, out BodyInertia inertia)
    {
        switch (collider.Shape)
        {
            case ColliderShape.Sphere:
            {
                var sphere = new Sphere(MathF.Max(collider.Size.X, 0.001f));
                inertia = sphere.ComputeInertia(mass);
                return Simulation.Shapes.Add(sphere);
            }
            case ColliderShape.Capsule:
            {
                var capsule = new Capsule(MathF.Max(collider.Size.X, 0.001f), MathF.Max(collider.Size.Y, 0.001f));
                inertia = capsule.ComputeInertia(mass);
                return Simulation.Shapes.Add(capsule);
            }
            default:
            {
                var box = new Box(MathF.Max(collider.Size.X, 0.001f), MathF.Max(collider.Size.Y, 0.001f), MathF.Max(collider.Size.Z, 0.001f));
                inertia = box.ComputeInertia(mass);
                return Simulation.Shapes.Add(box);
            }
        }
    }

    // ---- Compounds (a dynamic body and its children's colliders) --------------------------------------

    // One collider of a compound: its shape and where its entity is relative to the owner's entity.
    internal readonly record struct CompoundPart(Entity Entity, Collider Collider, Pose Local);

    // Makes a dynamic body the compound of its own collider and `parts` (each placed relative to the owner's
    // entity), with the mass it has now shared out by volume. The body's origin stays its own collider's
    // centre, so its pose and the write-back are what they were; the inertia is taken about that point.
    // A ray that hits a part reports the part's entity; everything else reports the owner. No parts gives
    // the body back its own single shape.
    internal void SetCompound(in PhysicsBody body, Entity owner, in Collider own, ReadOnlySpan<CompoundPart> parts)
    {
        if (body.IsStatic) return;
        var handle = new BodyHandle(body.Handle);
        if (!Simulation.Bodies.BodyExists(handle)) return;
        var reference = Simulation.Bodies[handle];
        if (parts.Length == 0)
        {
            var pose = PoseOf(body);
            var entityPose = new Pose { Position = pose.Position - Vector3.Transform(own.Center, pose.Rotation), Rotation = pose.Rotation, Scale = Vector3.One };
            SetShape(body, own, entityPose);
            return;
        }

        float mass = !reference.Kinematic && reference.LocalInertia.InverseMass > 0f ? 1f / reference.LocalInertia.InverseMass : 1f;
        float total = VolumeOf(own);
        for (int i = 0; i < parts.Length; i++) total += VolumeOf(parts[i].Collider);
        if (total <= 0f) total = 1f;

        var builder = new CompoundBuilder(_pool, Simulation.Shapes, parts.Length + 1);
        try
        {
            AddPart(ref builder, own, new RigidPose(Vector3.Zero), mass * VolumeOf(own) / total);
            for (int i = 0; i < parts.Length; i++)
            {
                var local = parts[i].Local;
                var centre = local.Position + Vector3.Transform(parts[i].Collider.Center, local.Rotation) - own.Center;
                AddPart(ref builder, parts[i].Collider, new RigidPose(centre, Quaternion.Normalize(local.Rotation)), mass * VolumeOf(parts[i].Collider) / total);
            }
            builder.BuildDynamicCompound(out var children, out var inertia);   // not recentred: the origin stays put
            var shape = Simulation.Shapes.Add(new Compound(children));
            Simulation.Bodies.SetShape(handle, shape);
            if (!reference.Kinematic) Simulation.Bodies.SetLocalInertia(handle, inertia);
            reference.Awake = true;
            Simulation.Bodies.UpdateBounds(handle);
            ReleaseBodyShape(body.Handle);
            _ownedBodyShapes[body.Handle] = shape;
        }
        finally
        {
            builder.Dispose();
        }

        var entities = new Entity[parts.Length + 1];   // rebuilt when a part comes or goes, never per tick
        entities[0] = owner;
        for (int i = 0; i < parts.Length; i++) entities[i + 1] = parts[i].Entity;
        _data.SetParts(body.Handle, entities);
    }

    private void AddPart(ref CompoundBuilder builder, in Collider collider, RigidPose pose, float weight)
    {
        weight = MathF.Max(weight, 1e-4f);
        var shape = ShapeFor(collider, out var inertia, weight);
        builder.Add(shape, pose, inertia.InverseInertiaTensor, weight);
    }

    private static float VolumeOf(in Collider collider)
    {
        var s = collider.Size;
        return collider.Shape switch
        {
            ColliderShape.Sphere => 4f / 3f * MathF.PI * s.X * s.X * s.X,
            ColliderShape.Capsule => MathF.PI * s.X * s.X * (s.Y + 4f / 3f * s.X),
            _ => MathF.Abs(s.X * s.Y * s.Z),
        };
    }

    // Is the body's shape already where an entity at `pose` puts it? A parented collider that has not moved
    // is left alone, so it can sleep (PhysicsSyncSystem).
    internal bool IsAt(in PhysicsBody body, in Collider collider, in Pose pose)
    {
        if (body.IsStatic) return true;
        var handle = new BodyHandle(body.Handle);
        if (!Simulation.Bodies.BodyExists(handle)) return true;
        var current = Simulation.Bodies[handle].Pose;
        return current.Position == collider.CenterAt(pose) && current.Orientation == pose.Rotation;
    }

    // Colliders whose compound needs rebuilding (a part was destroyed), and parts whose owner went, for
    // PhysicsSyncSystem to deal with at the start of its next run.
    internal List<Entity> CompoundsToRebuild { get; } = new();
    internal List<Entity> PartsLeftBehind { get; } = new();

    // The physics module's EntityDestroyed hook: a destroyed part's compound is rebuilt without it, and the
    // parts of a destroyed owner that outlive it (children parented by hand do) get bodies of their own.
    internal void NoteDestroyed(World world, Entity entity, in PhysicsBody? body)
    {
        if (world.TryGet<ColliderPart>(entity, out var part)) CompoundsToRebuild.Add(part.Body);
        if (body is { IsStatic: false } owned && _data.PartsOf(owned.Handle) is { } parts)
            for (int i = 1; i < parts.Length; i++) PartsLeftBehind.Add(parts[i]);
    }

    // Draws a compound's children (DrawDebug).
    private void DrawCompound(DebugDraw debug, TypedIndex shape, RigidPose pose, bool trigger, Vector3 around, float range)
    {
        ref var compound = ref Simulation.Shapes.GetShape<Compound>(shape.Index);
        for (int i = 0; i < compound.Children.Length; i++)
        {
            ref var child = ref compound.Children[i];
            var at = new RigidPose(pose.Position + Vector3.Transform(child.LocalPose.Position, pose.Orientation),
                                   Quaternion.Normalize(pose.Orientation * child.LocalPose.Orientation));
            DrawShape(debug, child.ShapeIndex, at, trigger, around, range);
        }
    }
}

#pragma warning restore SAGE0134
