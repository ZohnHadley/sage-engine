#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
using Friflo.Engine.ECS;

namespace sage_engine;

// One BepuPhysics v2 simulation per World (docs/design/10 §3), as a world resource. Bepu owns the
// bodies and statics; entities hold handles (PhysicsBody). Everything is in origin space, the same as
// GlobalTransform; Rebase shifts it when the origin sector moves (14).
//
// Threading: Bepu's narrow-phase callbacks run on worker threads. They only read the layer matrix and
// write trigger overlaps into per-worker buffers, which are merged on the main thread in PostPhysics.
//
// Stepping allocates about 40 bytes per tick inside Bepu itself (its stage profiler), with or without
// the thread dispatcher. That is the only per-frame allocation left in the host (TODO #41).
public sealed class PhysicsSpace : IDisposable
{
    private readonly BufferPool _pool = new();
    private readonly ThreadDispatcher _dispatcher;
    private readonly PhysicsCallbackData _data;
    private readonly Dictionary<ShapeKey, TypedIndex> _shapes = new();
    private readonly List<Entity> _overlapResults = new();   // reused by OverlapBox
    private readonly List<TypedIndex> _meshShapes = new();

    internal Simulation Simulation { get; }

    public PhysicsSpace(Vector3? gravity = null)
    {
        Gravity = gravity ?? new Vector3(0, -9.81f, 0);
        _data = new PhysicsCallbackData(Math.Max(1, Environment.ProcessorCount));
        _dispatcher = new ThreadDispatcher(Math.Max(1, Environment.ProcessorCount - 1));
        Simulation = Simulation.Create(_pool,
            new NarrowPhaseCallbacks { Data = _data },
            new PoseIntegratorCallbacks(Gravity),
            new SolveDescription(velocityIterationCount: 8, substepCount: 1));
        // Same result every run on this machine: the simulation is fixed-tick and will want
        // repeatable behaviour for replays and tests. Bepu has no rollback either way (10 §2).
        Simulation.Deterministic = true;
        Log.Info(LogCat.Physics, $"Physics space created (Bepu v2, gravity {Gravity.Y:F2} m/s², {_dispatcher.ThreadCount} worker threads)");
    }

    public Vector3 Gravity { get; }
    public LayerMatrix Layers => _data.Layers;
    public int BodyCount => Simulation.Bodies.ActiveSet.Count;
    public int StaticCount => Simulation.Statics.Count;
    public double LastStepMilliseconds { get; private set; }

    // Trigger overlaps for this tick, drained by gameplay in PostPhysics (10 §3).
    public ReadOnlySpan<TriggerOverlap> TriggerEnter => _data.Entered;
    public ReadOnlySpan<TriggerOverlap> TriggerExit => _data.Exited;

    // ---- Bodies -------------------------------------------------------------------------------

    // Adds an entity's collider to the simulation and returns its handle component.
    internal PhysicsBody Add(Entity entity, in Collider collider, in RigidBody body, in Pose pose)
    {
        var shape = ShapeFor(collider, out BodyInertia inertia, body.Mass <= 0 ? 1f : body.Mass);
        var rigidPose = new RigidPose(collider.CenterAt(pose), pose.Rotation);
        float friction = body.Friction <= 0 ? 0.7f : body.Friction;

        if (body.Kind == BodyKind.Dynamic)
        {
            var description = BodyDescription.CreateDynamic(rigidPose, inertia, new CollidableDescription(shape, 0.1f), new BodyActivityDescription(0.01f));
            var handle = Simulation.Bodies.Add(description);
            _data.RegisterBody(handle.Value, entity, collider.Layer, collider.IsTrigger, friction, body.Restitution);
            return new PhysicsBody { Handle = handle.Value, IsStatic = false };
        }
        if (body.Kind == BodyKind.Kinematic)
        {
            var description = BodyDescription.CreateKinematic(rigidPose, new CollidableDescription(shape, 0.1f), new BodyActivityDescription(0.01f));
            var handle = Simulation.Bodies.Add(description);
            _data.RegisterBody(handle.Value, entity, collider.Layer, collider.IsTrigger, friction, body.Restitution);
            return new PhysicsBody { Handle = handle.Value, IsStatic = false };
        }
        var staticHandle = Simulation.Statics.Add(new StaticDescription(rigidPose, shape));
        _data.RegisterStatic(staticHandle.Value, entity, collider.Layer, collider.IsTrigger, friction, body.Restitution);
        return new PhysicsBody { Handle = staticHandle.Value, IsStatic = true };
    }

    internal void Remove(in PhysicsBody body)
    {
        if (body.IsStatic)
        {
            var handle = new StaticHandle(body.Handle);
            if (Simulation.Statics.StaticExists(handle)) Simulation.Statics.Remove(handle);
            _data.UnregisterStatic(body.Handle);
        }
        else
        {
            var handle = new BodyHandle(body.Handle);
            if (Simulation.Bodies.BodyExists(handle)) Simulation.Bodies.Remove(handle);
            _data.UnregisterBody(body.Handle);
        }
    }

    // A static collider the engine builds itself (terrain chunks): triangles in world space.
    public PhysicsBody AddMesh(Entity entity, ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, Vector3 position, byte layer = 0)
    {
        int triangleCount = indices.Length / 3;
        _pool.Take<Triangle>(triangleCount, out var triangles);
        for (int i = 0; i < triangleCount; i++)
            triangles[i] = new Triangle(vertices[indices[i * 3]], vertices[indices[i * 3 + 1]], vertices[indices[i * 3 + 2]]);
        var mesh = new Mesh(triangles, Vector3.One, _pool);
        var shape = Simulation.Shapes.Add(mesh);
        _meshShapes.Add(shape);
        var handle = Simulation.Statics.Add(new StaticDescription(new RigidPose(position), shape));
        _data.RegisterStatic(handle.Value, entity, layer, false, 0.8f, 0f);
        return new PhysicsBody { Handle = handle.Value, IsStatic = true };
    }

    public Pose PoseOf(in PhysicsBody body)
    {
        if (body.IsStatic)
        {
            var pose = Simulation.Statics[new StaticHandle(body.Handle)].Pose;
            return new Pose { Position = pose.Position, Rotation = pose.Orientation, Scale = Vector3.One };
        }
        var bodyPose = Simulation.Bodies[new BodyHandle(body.Handle)].Pose;
        return new Pose { Position = bodyPose.Position, Rotation = bodyPose.Orientation, Scale = Vector3.One };
    }

    // `pose` is the entity's pose; the collider's Center offsets the shape (review #44).
    public void SetPose(in PhysicsBody body, in Collider collider, in Pose pose)
    {
        SetShapePose(body, new Pose { Position = collider.CenterAt(pose), Rotation = pose.Rotation, Scale = pose.Scale });
    }

    private void SetShapePose(in PhysicsBody body, in Pose pose)
    {
        if (body.IsStatic)
        {
            var handle = new StaticHandle(body.Handle);
            var staticRef = Simulation.Statics[handle];   // a reference into Bepu's storage
            staticRef.Pose = new RigidPose(pose.Position, pose.Rotation);
            Simulation.Statics.UpdateBounds(handle);
            return;
        }
        var reference = Simulation.Bodies[new BodyHandle(body.Handle)];
        reference.Pose = new RigidPose(pose.Position, pose.Rotation);
        reference.Awake = true;
    }

    public Vector3 VelocityOf(in PhysicsBody body) =>
        body.IsStatic ? Vector3.Zero : Simulation.Bodies[new BodyHandle(body.Handle)].Velocity.Linear;

    public void SetVelocity(in PhysicsBody body, Vector3 velocity)
    {
        if (body.IsStatic) return;
        var reference = Simulation.Bodies[new BodyHandle(body.Handle)];
        reference.Velocity.Linear = velocity;
        reference.Awake = true;
    }

    public bool IsDynamic(in PhysicsBody body) =>
        !body.IsStatic && Simulation.Bodies[new BodyHandle(body.Handle)].Kinematic == false;

    public bool IsAwake(in PhysicsBody body) => !body.IsStatic && Simulation.Bodies[new BodyHandle(body.Handle)].Awake;

    // ---- Stepping -----------------------------------------------------------------------------

    internal void Step(float dt)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _data.BeginStep();
        Simulation.Timestep(dt, _dispatcher);
        _data.EndStep();
        LastStepMilliseconds = watch.Elapsed.TotalMilliseconds;
    }

    // The origin sector moved (14 §3): shift everything by the same offset, once, between ticks.
    public void Rebase(Vector3 offset)
    {
        for (int i = 0; i < Simulation.Bodies.Sets.Length; i++)
        {
            ref var set = ref Simulation.Bodies.Sets[i];
            if (!set.Allocated) continue;
            for (int j = 0; j < set.Count; j++) set.SolverStates[j].Motion.Pose.Position += offset;
        }
        for (int i = 0; i < Simulation.Statics.Count; i++)
        {
            ref var s = ref Simulation.Statics[i];
            s.Pose.Position += offset;
        }
        Log.Info(LogCat.Physics, $"Physics rebased by {offset}");
    }

    // ---- Queries ------------------------------------------------------------------------------

    // The nearest hit along a ray, or Hit = false.
    public RayHit Raycast(Vector3 from, Vector3 direction, float maxDistance, LayerMask mask = default)
    {
        var handler = new NearestRayHandler { Data = _data, Mask = mask.Bits == 0 ? LayerMask.All : mask, Origin = from, Direction = Vector3.Normalize(direction) };
        Simulation.RayCast(from, handler.Direction, maxDistance, ref handler, 0);
        return handler.Hit;
    }

    // Sweeps a shape and returns the first thing it touches. Used by the character controller (F7).
    public SweepHit Sweep(in Collider shape, in Pose from, Vector3 direction, float maxDistance, LayerMask mask = default)
    {
        var handler = new SweepHandler { Data = _data, Mask = mask.Bits == 0 ? LayerMask.All : mask };
        var pose = new RigidPose(shape.CenterAt(from), from.Rotation);   // `from` is the entity, not the shape
        var velocity = new BodyVelocity(Vector3.Normalize(direction) * maxDistance);
        switch (shape.Shape)
        {
            case ColliderShape.Sphere:
                Simulation.Sweep(new Sphere(MathF.Max(shape.Size.X, 0.001f)), pose, velocity, 1f, _pool, ref handler);
                break;
            case ColliderShape.Capsule:
                Simulation.Sweep(new Capsule(MathF.Max(shape.Size.X, 0.001f), MathF.Max(shape.Size.Y, 0.001f)), pose, velocity, 1f, _pool, ref handler);
                break;
            default:
                Simulation.Sweep(new Box(MathF.Max(shape.Size.X, 0.001f), MathF.Max(shape.Size.Y, 0.001f), MathF.Max(shape.Size.Z, 0.001f)), pose, velocity, 1f, _pool, ref handler);
                break;
        }
        var hit = handler.Hit;
        hit.Distance *= maxDistance;   // the sweep runs over t in 0..1
        return hit;
    }

    // Entities whose bounding boxes overlap a box (10 §4). Broad phase only in v1: it may report
    // entities whose shapes don't actually touch, which is fine for "what is around here" queries.
    public int OverlapBox(Vector3 center, Vector3 halfExtents, Span<Entity> results, LayerMask mask = default)
    {
        _overlapResults.Clear();
        var enumerator = new OverlapEnumerator
        {
            Data = _data,
            Mask = mask.Bits == 0 ? LayerMask.All : mask,
            Results = _overlapResults,
            Limit = results.Length,
        };
        Simulation.BroadPhase.GetOverlaps(new BepuUtilities.BoundingBox(center - halfExtents, center + halfExtents), ref enumerator);
        for (int i = 0; i < _overlapResults.Count; i++) results[i] = _overlapResults[i];
        return _overlapResults.Count;
    }

    public Entity EntityOf(CollidableReference collidable) => _data.EntityOf(collidable);

    public void Dispose()
    {
        Simulation.Dispose();
        _dispatcher.Dispose();
        _pool.Clear();
    }

    // ---- Shapes -------------------------------------------------------------------------------

    private readonly record struct ShapeKey(ColliderShape Shape, Vector3 Size);

    private TypedIndex ShapeFor(in Collider collider, out BodyInertia inertia, float mass)
    {
        var key = new ShapeKey(collider.Shape, collider.Size);
        switch (collider.Shape)
        {
            case ColliderShape.Sphere:
            {
                var sphere = new Sphere(MathF.Max(collider.Size.X, 0.001f));
                inertia = sphere.ComputeInertia(mass);
                return Cached(key, sphere);
            }
            case ColliderShape.Capsule:
            {
                var capsule = new Capsule(MathF.Max(collider.Size.X, 0.001f), MathF.Max(collider.Size.Y, 0.001f));
                inertia = capsule.ComputeInertia(mass);
                return Cached(key, capsule);
            }
            default:
            {
                var box = new Box(MathF.Max(collider.Size.X, 0.001f), MathF.Max(collider.Size.Y, 0.001f), MathF.Max(collider.Size.Z, 0.001f));
                inertia = box.ComputeInertia(mass);
                return Cached(key, box);
            }
        }
    }

    private TypedIndex Cached<TShape>(ShapeKey key, TShape shape) where TShape : unmanaged, IShape
    {
        if (_shapes.TryGetValue(key, out var existing)) return existing;
        var index = Simulation.Shapes.Add(shape);
        _shapes[key] = index;
        return index;
    }

    // ---- Bepu callbacks -------------------------------------------------------------------------

    private struct NarrowPhaseCallbacks : INarrowPhaseCallbacks
    {
        public PhysicsCallbackData Data;

        public void Initialize(Simulation simulation) { }

        public bool AllowContactGeneration(int workerIndex, CollidableReference a, CollidableReference b, ref float speculativeMargin) =>
            Data.ShouldCollide(a, b);

        public bool AllowContactGeneration(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB) => true;

        public bool ConfigureContactManifold<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold, out PairMaterialProperties pairMaterial)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            // A trigger reports the overlap and produces no contact constraint.
            if (Data.IsTrigger(pair.A) || Data.IsTrigger(pair.B))
            {
                if (manifold.Count > 0) Data.ReportTrigger(workerIndex, pair);
                pairMaterial = default;
                return false;
            }
            Data.MaterialFor(pair, out float friction, out float restitution);
            pairMaterial = new PairMaterialProperties(friction, 2f, new SpringSettings(30, restitution > 0 ? 0.4f : 1f));
            return true;
        }

        public bool ConfigureContactManifold(int workerIndex, CollidablePair pair, int childIndexA, int childIndexB, ref ConvexContactManifold manifold) => true;

        public void Dispose() { }
    }

    private struct PoseIntegratorCallbacks : IPoseIntegratorCallbacks
    {
        private readonly Vector3 _gravity;
        private Vector3Wide _gravityDt;

        public PoseIntegratorCallbacks(Vector3 gravity) { _gravity = gravity; _gravityDt = default; }

        public AngularIntegrationMode AngularIntegrationMode => AngularIntegrationMode.Nonconserving;
        public bool AllowSubstepsForUnconstrainedBodies => false;
        public bool IntegrateVelocityForKinematics => false;

        public void Initialize(Simulation simulation) { }

        public void PrepareForIntegration(float dt) => Vector3Wide.Broadcast(_gravity * dt, out _gravityDt);

        public void IntegrateVelocity(Vector<int> bodyIndices, Vector3Wide position, QuaternionWide orientation, BodyInertiaWide localInertia,
            Vector<int> integrationMask, int workerIndex, Vector<float> dt, ref BodyVelocityWide velocity)
        {
            velocity.Linear += _gravityDt;
        }
    }

    private struct NearestRayHandler : IRayHitHandler
    {
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public Vector3 Origin;
        public Vector3 Direction;
        public RayHit Hit;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => Data.Allows(collidable, Mask);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in BepuPhysics.Trees.RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            if (Hit.Hit && t >= Hit.Distance) return;
            maximumT = t;   // nearest hit only
            Hit = new RayHit
            {
                Entity = Data.EntityOf(collidable),
                Position = Origin + Direction * t,
                Normal = Vector3.Normalize(normal),
                Distance = t,
                Hit = true,
            };
        }
    }

    private struct SweepHandler : ISweepHitHandler
    {
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public SweepHit Hit;

        public bool AllowTest(CollidableReference collidable) => Data.Allows(collidable, Mask);
        public bool AllowTest(CollidableReference collidable, int child) => true;

        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
        {
            if (Hit.Hit && t >= Hit.Distance) return;
            maximumT = t;
            Hit = new SweepHit
            {
                Entity = Data.EntityOf(collidable),
                Position = hitLocation,
                Normal = Vector3.Normalize(hitNormal),
                Distance = t,
                Hit = true,
            };
        }

        // Already touching at the start: Bepu has no normal for it, and it happens constantly (a
        // character rests a skin width above the floor). Deliberately ignored — recording it here once
        // hid the real hits further along the sweep. Getting out of a surface needs a shape-overlap
        // query and a depenetration pass (10 §4, not built).
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable) { }
    }

    private struct OverlapEnumerator : IBreakableForEach<CollidableReference>
    {
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public List<Entity> Results;   // a list the space reuses: a Span can't live in a non-ref struct
        public int Limit;

        public bool LoopBody(CollidableReference collidable)
        {
            if (!Data.Allows(collidable, Mask)) return true;
            var entity = Data.EntityOf(collidable);
            if (entity.IsNull) return true;
            Results.Add(entity);
            return Results.Count < Limit;
        }
    }
}
