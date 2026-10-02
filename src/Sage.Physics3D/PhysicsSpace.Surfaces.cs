#nullable enable
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;

namespace Sage.Physics3D;

// Surfaces (issue #270, docs/design/10 "As built (surfaces)"): what each collider is made of, a
// physics_material record id kept beside its layer in PhysicsCallbackData, returned in every RayHit and
// SweepHit, and giving the collider the record's friction and restitution. Those feed Bepu's pair
// material in ConfigureContactManifold exactly as a RigidBody's own did (geometric mean of the
// frictions, the larger restitution), per collider: Bepu's per-child manifold callback has no material
// to give, so a mesh whose triangles differ takes the friction of its most common surface.
public sealed partial class PhysicsSpace
{
    // The content the space resolves physics_material ids against; PhysicsModule sets it. Without it
    // (a space made by hand) surfaces are still kept and returned, and friction stays as it was.
    internal RecordStore? Records { get; set; }

    public void SetSurface(in PhysicsBody body, RecordId surface)
    {
        var material = Material(surface);
        _data.SetSurface(body.Handle, body.IsStatic, surface, null, material?.Friction, material?.Restitution);
    }

    public void SetSurfaces(in PhysicsBody body, ReadOnlySpan<SurfaceFace> faces)
    {
        if (faces.Length == 0) { SetSurface(body, default); return; }
        var map = new PhysicsCallbackData.SurfaceMap { Normals = new Vector3[faces.Length], Surfaces = new RecordId[faces.Length] };
        int floor = 0;
        for (int i = 0; i < faces.Length; i++)
        {
            map.Normals[i] = faces[i].Normal;
            map.Surfaces[i] = faces[i].Surface;
            if (faces[i].Normal.Y > faces[floor].Normal.Y) floor = i;
        }
        var material = Material(faces[floor].Surface);
        _data.SetSurface(body.Handle, body.IsStatic, faces[floor].Surface, map, material?.Friction, material?.Restitution);
    }

    public void SetSurfaces(in PhysicsBody body, ReadOnlySpan<RecordId> layers, ReadOnlySpan<byte> perTriangle)
    {
        if (layers.Length == 0) { SetSurface(body, default); return; }
        Span<int> counts = stackalloc int[256];
        counts.Clear();
        foreach (byte layer in perTriangle) counts[layer]++;
        int common = 0;
        for (int i = 1; i < layers.Length && i < 256; i++) if (counts[i] > counts[common]) common = i;

        var map = new PhysicsCallbackData.SurfaceMap { Triangles = perTriangle.ToArray(), Surfaces = layers.ToArray() };
        var material = Material(layers[common]);
        _data.SetSurface(body.Handle, body.IsStatic, layers[common], map, material?.Friction, material?.Restitution);
    }

    public RecordId SurfaceOf(in PhysicsBody body) => _data.SurfaceOf(body.Handle, body.IsStatic);

    // The record behind a surface id, when the space can see the content.
    private PhysicsMaterialRecord? Material(RecordId surface)
    {
        if (surface.IsEmpty || Records == null || Records.TypeNameOf(typeof(PhysicsMaterialRecord)) == null) return null;
        if (Records.TryGet(surface, out PhysicsMaterialRecord record)) return record;
        Log.Once(LogCat.Physics, LogLevel.Warn, $"surface:{surface}", $"no physics_material '{surface}'; the collider keeps its friction");
        return null;
    }

    // What a body made from a Collider is made of, and its friction and restitution: the RigidBody's own
    // when it gives them, else the surface's, else the defaults (0.7, 0).
    private void BodyMaterial(in Collider collider, in RigidBody body, out float friction, out float restitution)
    {
        var material = Material(collider.Surface);
        friction = body.Friction > 0 ? body.Friction : material?.Friction ?? 0.7f;
        restitution = body.Restitution > 0 ? body.Restitution : material?.Restitution ?? 0f;
    }

    // What a sweep hit is made of. A sweep is told no triangle, so a mesh whose triangles differ is
    // asked again with a short ray into the surface at the hit, against that collider alone.
    private RecordId SweepSurface(CollidableReference collidable, in SweepHit hit)
    {
        if (!_data.HasTriangleSurfaces(collidable) || hit.StartsInside) return _data.SurfaceAt(collidable, -1, hit.Normal);
        var probe = new OneCollidableRayHandler { Target = collidable, Child = -1 };
        Simulation.RayCast(hit.Position + hit.Normal * 0.05f, -hit.Normal, 0.1f, ref probe, 0);
        return _data.SurfaceAt(collidable, probe.Child, hit.Normal);
    }

    // The triangle a ray meets on one collidable.
    private struct OneCollidableRayHandler : IRayHitHandler
    {
        public CollidableReference Target;
        public int Child;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => collidable.Packed == Target.Packed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            maximumT = t;
            Child = childIndex;
        }
    }
}
