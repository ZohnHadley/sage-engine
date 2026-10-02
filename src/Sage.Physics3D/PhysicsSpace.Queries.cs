#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.Trees;

namespace Sage.Physics3D;

// The richer queries (issue #269): every hit along a ray, and a sphere overlap, which is the narrow-phase
// Overlap (PhysicsSpace.Overlap.cs, issue #259) with a sphere for its shape.
public sealed partial class PhysicsSpace
{
    private readonly List<(CollidableReference Collidable, RayHit Hit)> _rayHits = new();   // reused by RaycastAll

    public int RaycastAll(Vector3 from, Vector3 direction, float maxDistance, Span<RayHit> results, LayerMask mask = default,
                          bool includeTriggers = false, Entity ignore = default)
    {
        if (results.Length == 0) return 0;
        _rayHits.Clear();
        var handler = new AllRayHandler
        {
            Data = _data, Mask = mask.Bits == 0 ? LayerMask.All : mask, IncludeTriggers = includeTriggers, Ignore = ignore,
            Origin = from, Direction = Vector3.Normalize(direction), Hits = _rayHits,
        };
        Simulation.RayCast(from, handler.Direction, maxDistance, ref handler, 0);

        // Nearest first, truncated to the nearest: an insertion into the caller's buffer, which is small.
        int count = 0;
        for (int i = 0; i < _rayHits.Count; i++)
        {
            var hit = _rayHits[i].Hit;
            if (count < results.Length) results[count++] = hit;
            else if (hit.Distance < results[count - 1].Distance) results[count - 1] = hit;
            else continue;
            for (int j = count - 1; j > 0 && results[j].Distance < results[j - 1].Distance; j--)
                (results[j], results[j - 1]) = (results[j - 1], results[j]);
        }
        return count;
    }

    public int OverlapSphere(Vector3 center, float radius, Span<OverlapHit> results, LayerMask mask = default, bool includeTriggers = false,
                             Entity ignore = default)
    {
        var at = new Pose { Position = center, Rotation = Quaternion.Identity, Scale = Vector3.One };
        return Overlap(Collider.Sphere(radius), at, results, mask, includeTriggers, ignore);
    }

    // Every hit, the nearest per collidable (a mesh or a compound may answer once per child).
    private struct AllRayHandler : IRayHitHandler
    {
        public bool IncludeTriggers;
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public Entity Ignore;
        public Vector3 Origin;
        public Vector3 Direction;
        public List<(CollidableReference Collidable, RayHit Hit)> Hits;   // a list the space reuses

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => Data.Allows(collidable, Mask, IncludeTriggers, Ignore);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            var hit = new RayHit
            {
                Entity = Data.EntityOf(collidable),
                Position = Origin + Direction * t,
                Normal = Vector3.Normalize(normal),
                Distance = t,
                Hit = true,
                Surface = Data.SurfaceAt(collidable, childIndex, normal),   // issue #270
            };
            for (int i = 0; i < Hits.Count; i++)
            {
                if (Hits[i].Collidable.Packed != collidable.Packed) continue;
                if (t < Hits[i].Hit.Distance) Hits[i] = (collidable, hit);
                return;
            }
            Hits.Add((collidable, hit));   // maximumT is left alone: every hit up to maxDistance
        }
    }
}
