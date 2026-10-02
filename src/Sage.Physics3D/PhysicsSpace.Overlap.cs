#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuUtilities;

namespace Sage.Physics3D;

// The narrow-phase overlap (IPhysicsWorld.Overlap, issue #259): what a shape actually intersects, and by
// how much, so the character controller can push itself out of a door that closed on it, and a mover
// can push what it has moved into (issue #260). The broad phase
// finds the candidates by bounds; Bepu's collision batcher then runs the same contact tests the step
// uses, one pair per candidate, and the deepest contact of each pair is the answer for that collider.
public sealed partial class PhysicsSpace
{
    private readonly List<CollidableReference> _overlapCandidates = new();   // reused by Overlap
    private readonly OverlapCollector _overlapCollector = new();

    public int Overlap(in Collider shape, in Pose at, Span<OverlapHit> results, LayerMask mask = default, bool includeTriggers = false,
                       Entity ignore = default)
    {
        var pose = new RigidPose(shape.CenterAt(at), at.Rotation);
        return Overlap(ShapeFor(shape, out _, 1f), pose, default, false, results, mask, includeTriggers, ignore);
    }

    // What a body or static already in the space intersects where it is now (a mover that has just moved,
    // issue #260): itself and its own entity's colliders left out.
    public int Overlap(in PhysicsBody body, Span<OverlapHit> results, LayerMask mask = default, bool includeTriggers = false)
    {
        TypedIndex shape;
        RigidPose pose;
        CollidableReference self;
        if (body.IsStatic)
        {
            var handle = new StaticHandle(body.Handle);
            if (!Simulation.Statics.StaticExists(handle)) return 0;
            var description = Simulation.Statics[handle];
            (shape, pose, self) = (description.Shape, description.Pose, new CollidableReference(handle));
        }
        else
        {
            var handle = new BodyHandle(body.Handle);
            if (!Simulation.Bodies.BodyExists(handle)) return 0;
            var reference = Simulation.Bodies[handle];
            (shape, pose) = (reference.Collidable.Shape, reference.Pose);
            self = new CollidableReference(reference.Kinematic ? CollidableMobility.Kinematic : CollidableMobility.Dynamic, handle);
        }
        if (!shape.Exists) return 0;
        return Overlap(shape, pose, self, true, results, mask, includeTriggers, _data.EntityOf(self));
    }

    private int Overlap(TypedIndex query, RigidPose pose, CollidableReference self, bool hasSelf, Span<OverlapHit> results, LayerMask mask, bool includeTriggers,
                        Entity ignore)
    {
        if (results.Length == 0) return 0;
        Simulation.Shapes.UpdateBounds(pose, ref query, out var bounds);

        _overlapCandidates.Clear();
        var enumerator = new CandidateEnumerator
        {
            Data = _data, Mask = mask.Bits == 0 ? LayerMask.All : mask, IncludeTriggers = includeTriggers, Ignore = ignore,
            Self = self, HasSelf = hasSelf, Results = _overlapCandidates,
        };
        Simulation.BroadPhase.GetOverlaps(bounds, ref enumerator);
        if (_overlapCandidates.Count == 0) return 0;

        _overlapCollector.Begin(_overlapCandidates.Count);
        var batcher = new CollisionBatcher<OverlapCallbacks>(_pool, Simulation.Shapes, Simulation.NarrowPhase.CollisionTaskRegistry, 0f,
                                                            new OverlapCallbacks { Collector = _overlapCollector });
        for (int i = 0; i < _overlapCandidates.Count; i++)
        {
            var candidate = _overlapCandidates[i];
            TypedIndex other;
            RigidPose otherPose;
            if (candidate.Mobility == CollidableMobility.Static)
            {
                var description = Simulation.Statics[candidate.StaticHandle];
                other = description.Shape;
                otherPose = description.Pose;
            }
            else
            {
                var body = Simulation.Bodies[candidate.BodyHandle];
                other = body.Collidable.Shape;
                otherPose = body.Pose;
            }
            if (!other.Exists) continue;   // a joint's world anchor
            batcher.Add(query, other, otherPose.Position - pose.Position, pose.Orientation, otherPose.Orientation, 0f, new PairContinuation(i));
        }
        batcher.Flush();

        // The deepest first: that is the one to get out of first.
        int count = 0;
        for (int i = 0; i < _overlapCandidates.Count; i++)
        {
            float depth = _overlapCollector.Depths[i];
            if (depth <= 0f) continue;
            var hit = new OverlapHit { Entity = _data.EntityOf(_overlapCandidates[i]), Normal = _overlapCollector.Normals[i], Depth = depth };
            if (count < results.Length)
            {
                results[count++] = hit;
            }
            else if (depth > results[count - 1].Depth)
            {
                results[count - 1] = hit;   // truncated, keeping the deepest
            }
            else continue;
            for (int j = count - 1; j > 0 && results[j].Depth > results[j - 1].Depth; j--)
                (results[j], results[j - 1]) = (results[j - 1], results[j]);
        }
        return count;
    }

    // The deepest contact per candidate pair (the pair id is the candidate's index).
    private sealed class OverlapCollector
    {
        public float[] Depths = Array.Empty<float>();
        public Vector3[] Normals = Array.Empty<Vector3>();

        public void Begin(int count)
        {
            if (Depths.Length < count)
            {
                Depths = new float[Math.Max(count, Depths.Length * 2)];
                Normals = new Vector3[Depths.Length];
            }
            Array.Clear(Depths, 0, count);
        }

        // Bepu's normal points from B (the collider) toward A (the query shape): the way out.
        public void Record(int pair, float depth, Vector3 normal)
        {
            if (depth <= Depths[pair]) return;
            Depths[pair] = depth;
            Normals[pair] = normal;
        }
    }

    private struct OverlapCallbacks : ICollisionCallbacks
    {
        public OverlapCollector Collector;

        public bool AllowCollisionTesting(int pairId, int childA, int childB) => true;

        public void OnChildPairCompleted(int pairId, int childA, int childB, ref ConvexContactManifold manifold) { }

        public void OnPairCompleted<TManifold>(int pairId, ref TManifold manifold) where TManifold : unmanaged, IContactManifold<TManifold>
        {
            for (int i = 0; i < manifold.Count; i++)
            {
                manifold.GetContact(i, out _, out var normal, out float depth, out _);
                Collector.Record(pairId, depth, normal);
            }
        }
    }

    private struct CandidateEnumerator : IBreakableForEach<CollidableReference>
    {
        public bool IncludeTriggers;
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public Entity Ignore;
        public CollidableReference Self;   // the body asking, when HasSelf
        public bool HasSelf;
        public List<CollidableReference> Results;

        public bool LoopBody(CollidableReference collidable)
        {
            if (!(HasSelf && collidable.Packed == Self.Packed) && Data.Allows(collidable, Mask, IncludeTriggers, Ignore)) Results.Add(collidable);
            return true;
        }
    }
}
