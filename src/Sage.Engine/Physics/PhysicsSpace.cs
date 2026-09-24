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
    // Shapes this space built itself (brush hulls, terrain collision meshes), by static handle, so that
    // removing the static can release the shape too. It used to be a write-only list: the shape and its
    // pooled triangles outlived every static made from them, which nothing noticed while terrain only
    // ever grew, and which a level that reloads would have leaked on every reload (R12 second pass
    // found the same shape of bug in asset reloading; this is its physics twin).
    private readonly Dictionary<int, TypedIndex> _ownedShapes = new();

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

            // A shape this space built for that static alone goes with it. Shapes from `_shapes` are
            // shared between bodies and cached by size, so those stay.
            if (_ownedShapes.Remove(body.Handle, out var owned))
                Simulation.Shapes.RemoveAndDispose(owned, _pool);
        }
        else
        {
            var handle = new BodyHandle(body.Handle);
            if (Simulation.Bodies.BodyExists(handle)) Simulation.Bodies.Remove(handle);
            _data.UnregisterBody(body.Handle);
        }
    }

    // A static collider from a convex point cloud: what a brush is (15 §3, TODO F16).
    //
    // A brush is the intersection of half-spaces, so it is convex by construction and a hull is the
    // collider it *wants* — solid rather than a surface, cheap to test against, and with none of a
    // triangle mesh's one-sidedness. A player who ends up inside a mesh wall falls through it; one
    // inside a hull is pushed out.
    //
    // Bepu recentres a hull on its own centre of mass and hands back the offset, so the static's pose
    // has to carry it or every brush sits at the level's origin.
    public PhysicsBody AddHull(Entity entity, ReadOnlySpan<Vector3> points, Vector3 position, byte layer = 0)
    {
        var buffer = new Vector3[points.Length];
        points.CopyTo(buffer);

        TypedIndex shape;
        Vector3 centre;
        try
        {
            var hull = new ConvexHull(buffer, _pool, out centre);
            shape = Simulation.Shapes.Add(hull);
        }
        catch (Exception ex)
        {
            // A brush flat enough to have no volume reaches Bepu as a degenerate hull. That is a
            // content problem: it costs this brush its collision, not the level.
            Log.Warn(LogCat.Physics, $"Convex hull of {points.Length} points failed ({ex.GetType().Name}); no collision for it");
            return default;
        }

        var handle = Simulation.Statics.Add(new StaticDescription(new RigidPose(position + centre), shape));
        _ownedShapes[handle.Value] = shape;
        _data.RegisterStatic(handle.Value, entity, layer, false, 0.8f, 0f);
        return new PhysicsBody { Handle = handle.Value, IsStatic = true };
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
        var handle = Simulation.Statics.Add(new StaticDescription(new RigidPose(position), shape));
        _ownedShapes[handle.Value] = shape;
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

    // The origin sector moved (14 §3, R6): shift everything by the same offset, once, between ticks.
    // Bepu keeps the authoritative pose of everything it simulates, so a rebase that moved only the
    // components would have physics drag the world back a kilometre on the next step.
    //
    // Two things beyond moving the poses, both of which are silent corruption if forgotten:
    // a **static's bounds** live in the broad phase and do not follow its pose, so an unrefreshed
    // static still blocks rays where it used to be; and a **sleeping body** is not re-bounded until it
    // wakes, so it would wake a sector away from its own collision box.
    public void Rebase(Vector3 offset)
    {
        if (offset == Vector3.Zero) return;

        for (int i = 0; i < Simulation.Bodies.Sets.Length; i++)
        {
            ref var set = ref Simulation.Bodies.Sets[i];
            if (!set.Allocated) continue;
            for (int j = 0; j < set.Count; j++) set.SolverStates[j].Motion.Pose.Position += offset;
        }

        // Every sleeping island, woken so Bepu re-bounds it where it now is. Waking a world's worth of
        // bodies is affordable because a rebase happens once every kilometre of travel.
        //
        // Backwards, because waking a set deallocates it and shuffles the list: walking forwards over
        // a collection that the loop body is removing from is the oldest bug there is.
        for (int i = Simulation.Bodies.Sets.Length - 1; i >= 1; i--)
            if (Simulation.Bodies.Sets[i].Allocated) Simulation.Awakener.AwakenSet(i);

        for (int i = 0; i < Simulation.Statics.Count; i++)
        {
            var handle = Simulation.Statics.IndexToHandle[i];
            ref var description = ref Simulation.Statics[i];
            description.Pose.Position += offset;
            Simulation.Statics.UpdateBounds(handle);
        }

        Log.Info(LogCat.Physics, $"Physics rebased by {offset.X:F0}, {offset.Z:F0} m " +
                                 $"({Simulation.Bodies.ActiveSet.Count} active bodies, {Simulation.Statics.Count} statics)");
    }

    // ---- Queries ------------------------------------------------------------------------------

    // The nearest hit along a ray, or Hit = false. Triggers are invisible to queries unless asked for.
    public RayHit Raycast(Vector3 from, Vector3 direction, float maxDistance, LayerMask mask = default, bool includeTriggers = false)
    {
        var handler = new NearestRayHandler { Data = _data, Mask = mask.Bits == 0 ? LayerMask.All : mask, IncludeTriggers = includeTriggers, Origin = from, Direction = Vector3.Normalize(direction) };
        Simulation.RayCast(from, handler.Direction, maxDistance, ref handler, 0);
        return handler.Hit;
    }

    // Sweeps a shape and returns the first thing it touches. Used by the character controller (F7).
    public SweepHit Sweep(in Collider shape, in Pose from, Vector3 direction, float maxDistance, LayerMask mask = default, bool includeTriggers = false)
    {
        var handler = new SweepHandler { Data = _data, Mask = mask.Bits == 0 ? LayerMask.All : mask, IncludeTriggers = includeTriggers };
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
    public int OverlapBox(Vector3 center, Vector3 halfExtents, Span<Entity> results, LayerMask mask = default, bool includeTriggers = false)
    {
        _overlapResults.Clear();
        var enumerator = new OverlapEnumerator
        {
            Data = _data,
            Mask = mask.Bits == 0 ? LayerMask.All : mask,
            IncludeTriggers = includeTriggers,
            Results = _overlapResults,
            Limit = results.Length,
        };
        Simulation.BroadPhase.GetOverlaps(new BepuUtilities.BoundingBox(center - halfExtents, center + halfExtents), ref enumerator);

        // **Truncated, not thrown.** The enumerator stops asking once it has enough, but the broad phase
        // finishes the leaf it is in, so it can hand back a few more than the caller's buffer holds. A
        // query that finds more than you asked for is an ordinary answer — "here are the first N" — and
        // a caller that crashed on a crowded world would be the worst possible way to say so (F24 found
        // this with a 64-entity buffer in a world of two thousand).
        int n = Math.Min(_overlapResults.Count, results.Length);
        for (int i = 0; i < n; i++) results[i] = _overlapResults[i];
        return n;
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
        public bool IncludeTriggers;
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public Vector3 Origin;
        public Vector3 Direction;
        public RayHit Hit;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => Data.Allows(collidable, Mask, IncludeTriggers);

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
        public bool IncludeTriggers;
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public SweepHit Hit;

        public bool AllowTest(CollidableReference collidable) => Data.Allows(collidable, Mask, IncludeTriggers);
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
        public bool IncludeTriggers;
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public List<Entity> Results;   // a list the space reuses: a Span can't live in a non-ref struct
        public int Limit;

        public bool LoopBody(CollidableReference collidable)
        {
            if (!Data.Allows(collidable, Mask, IncludeTriggers)) return true;
            var entity = Data.EntityOf(collidable);
            if (entity.IsNull) return true;
            Results.Add(entity);
            return Results.Count < Limit;
        }
    }
}
