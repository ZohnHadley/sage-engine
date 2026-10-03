#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using BepuPhysics.Constraints;
using BepuUtilities;
using BepuUtilities.Memory;
// Inside namespace Sage.Physics3D, `Simulation` would name the Sage.Simulation namespace first.
using BepuSimulation = BepuPhysics.Simulation;

namespace Sage.Physics3D;

// One BepuPhysics v2 simulation per World (docs/design/10 §3), as a world resource and as the world's
// IPhysicsWorld (issue #30): everything outside this plugin — gameplay, levels, entity I/O — reaches it
// through that interface and never sees a Bepu type. Bepu owns the bodies and statics; entities hold
// handles (PhysicsBody). Everything is in origin space, the same as GlobalTransform; Rebase shifts it
// when the origin sector moves (14).
//
// Threading: Bepu's narrow-phase callbacks run on worker threads. They only read the layer matrix and
// poses, and write trigger overlaps and reported contacts into per-worker buffers, which are merged on
// the main thread at the end of the step.
//
// Stepping allocates nothing (issue #273). The 40 bytes a tick long blamed on Bepu's profiler were the
// Stopwatch that timed the step (Stopwatch.StartNew is a 40-byte object); Bepu 2.4's Timestep allocates
// nothing, on the calling thread or its workers (test: ASteadyStateStepAllocatesNothingOnAnyThread).
// Our own per-worker report buffers are sized to the busiest step before each step, since any worker,
// the stepping thread among them, may draw all of a step's pairs (PhysicsCallbackData).
//
// Joints (issue #242, SAGE0134) are Bepu constraints behind PhysicsJoint handles: PhysicsSpace.Joints.cs.
#pragma warning disable SAGE0134 // joints and groups: this is the backend that implements them
public sealed partial class PhysicsSpace : IPhysicsWorld, IDisposable
{
    private readonly BufferPool _pool = new();
    private ThreadDispatcher? _dispatcher;
    private bool _stepped;   // UseWorkers is refused from then on
    private readonly PhysicsCallbackData _data;
    private readonly Dictionary<ShapeKey, TypedIndex> _shapes = new();
    private readonly List<Entity> _overlapResults = new();   // reused by OverlapBox
    // Shapes this space built itself (brush hulls, terrain collision meshes), by static handle, so that
    // removing the static can release the shape too. It used to be a write-only list: the shape and its
    // pooled triangles outlived every static made from them, which nothing noticed while terrain only
    // ever grew, and which a level that reloads would have leaked on every reload (R12 second pass
    // found the same shape of bug in asset reloading; this is its physics twin).
    private readonly Dictionary<int, TypedIndex> _ownedShapes = new();

    // Where a hull's own centre of mass sits relative to the position it was added at. Bepu recentres a
    // hull and hands back that offset, so anything that moves one afterwards has to add it back or the
    // door jumps by its own half-width the first time it opens.
    private readonly Dictionary<int, Vector3> _hullOffsets = new();

    internal BepuSimulation Simulation { get; }

    public PhysicsSpace(Vector3? gravity = null)
    {
        Gravity = gravity ?? new Vector3(0, -9.81f, 0);
        _data = new PhysicsCallbackData(1);
        UseWorkers(Environment.ProcessorCount - 1);
        Simulation = BepuSimulation.Create(_pool,
            new NarrowPhaseCallbacks { Data = _data },
            new PoseIntegratorCallbacks(Gravity),
            new SolveDescription(velocityIterationCount: 8, substepCount: 1));
        // Same result every run: the simulation is fixed-tick and wants repeatable behaviour for replays,
        // tests and the netcode to come (test: TheSameSceneGivesBitIdenticalBodiesEveryRun). Bepu has no
        // rollback either way (10 §2).
        Simulation.Deterministic = true;
        _data.Simulation = Simulation;
        Log.Info(LogCat.Physics, $"Physics space created (Bepu v2, gravity {Gravity.Y:F2} m/s², {WorkerThreads} worker threads)");
    }

    public Vector3 Gravity { get; }
    public LayerMatrix Layers => _data.Layers;
    public int BodyCount => Simulation.Bodies.ActiveSet.Count - AwakeAnchors();
    public int StaticCount => Simulation.Statics.Count;
    public double LastStepMilliseconds { get; private set; }

    // Trigger overlaps for this tick, drained by gameplay in PostPhysics (10 §3).
    public ReadOnlySpan<TriggerOverlap> TriggerEnter => _data.Entered;
    public ReadOnlySpan<TriggerOverlap> TriggerExit => _data.Exited;

    // Contacts that began and ended this tick, for colliders with ReportContacts.
    public ReadOnlySpan<ContactEvent> ContactBegin => _data.ContactBegin;
    public ReadOnlySpan<ContactEvent> ContactEnd => _data.ContactEnd;

    // Joints that broke this tick (JointDesc.BreakForce), each once.
    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public ReadOnlySpan<JointBroken> JointBroken => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_broken);

    // ---- Bodies -------------------------------------------------------------------------------

    // Adds an entity's collider to the simulation and returns its handle component.
    public PhysicsBody AddBody(Entity entity, in Collider collider, in RigidBody body, in Pose pose) =>
        AddBody(entity, collider, body, pose, 0);

    // The same, in a collision group (0 = none): bodies sharing a nonzero group never collide.
    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public PhysicsBody AddBody(Entity entity, in Collider collider, in RigidBody body, in Pose pose, int group)
    {
        var shape = ShapeFor(collider, out BodyInertia inertia, body.Mass <= 0 ? 1f : body.Mass);
        var rigidPose = new RigidPose(collider.CenterAt(pose), pose.Rotation);
        BodyMaterial(collider, body, out float friction, out float restitution);   // issue #270: the surface's, unless the body says

        if (body.Kind == BodyKind.Dynamic)
        {
            var description = BodyDescription.CreateDynamic(rigidPose, inertia, new CollidableDescription(shape, 0.1f), new BodyActivityDescription(0.01f));
            var handle = Simulation.Bodies.Add(description);
            _data.RegisterBody(handle.Value, entity, collider.Layer, collider.IsTrigger, friction, restitution, collider.ReportContacts, group);
            _data.SetSurface(handle.Value, false, collider.Surface, null, null, null);
            return new PhysicsBody { Handle = handle.Value, IsStatic = false };
        }
        if (body.Kind == BodyKind.Kinematic)
        {
            var description = BodyDescription.CreateKinematic(rigidPose, new CollidableDescription(shape, 0.1f), new BodyActivityDescription(0.01f));
            var handle = Simulation.Bodies.Add(description);
            _data.RegisterBody(handle.Value, entity, collider.Layer, collider.IsTrigger, friction, restitution, collider.ReportContacts, group);
            _data.SetSurface(handle.Value, false, collider.Surface, null, null, null);
            return new PhysicsBody { Handle = handle.Value, IsStatic = false };
        }
        var staticHandle = Simulation.Statics.Add(new StaticDescription(rigidPose, shape));
        _data.RegisterStatic(staticHandle.Value, entity, collider.Layer, collider.IsTrigger, friction, restitution, collider.ReportContacts, group);
        _data.SetSurface(staticHandle.Value, true, collider.Surface, null, null, null);
        return new PhysicsBody { Handle = staticHandle.Value, IsStatic = true };
    }

    public void RemoveBody(in PhysicsBody body)
    {
        RemoveJointsOf(body);   // a joint never outlives either of its bodies
        if (body.IsStatic)
        {
            var handle = new StaticHandle(body.Handle);
            if (Simulation.Statics.StaticExists(handle)) Simulation.Statics.Remove(handle);
            _data.UnregisterStatic(body.Handle);

            // A shape this space built for that static alone goes with it. Shapes from `_shapes` are
            // shared between bodies and cached by size, so those stay.
            if (_ownedShapes.Remove(body.Handle, out var owned))
                Simulation.Shapes.RemoveAndDispose(owned, _pool);
            _hullOffsets.Remove(body.Handle);
        }
        else
        {
            var handle = new BodyHandle(body.Handle);
            if (Simulation.Bodies.BodyExists(handle)) Simulation.Bodies.Remove(handle);
            _data.UnregisterBody(body.Handle);
            ReleaseBodyShape(body.Handle);
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
    public PhysicsBody AddHull(Entity entity, ReadOnlySpan<Vector3> points, Vector3 position, byte layer = 0, bool isTrigger = false)
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
        _hullOffsets[handle.Value] = centre;
        _data.RegisterStatic(handle.Value, entity, layer, isTrigger, 0.8f, 0f);
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

    // Moves a static the engine built (a door, a lift). Statics are meant to stay put, so Bepu needs its
    // bounds refreshed by hand — the same call the origin rebase makes, and for the same reason: a stale
    // broadphase bound is collision that happens where the thing used to be, with nothing to show for it.
    //
    // A mover makes its static kinematic (MakeKinematic, issue #261) so the solver sees it move; this
    // is for anything else the engine moves by hand.
    public void MoveStatic(in PhysicsBody body, Vector3 position)
    {
        if (!body.IsStatic) return;
        var handle = new StaticHandle(body.Handle);
        if (!Simulation.Statics.StaticExists(handle)) return;

        var offset = _hullOffsets.TryGetValue(body.Handle, out var centre) ? centre : Vector3.Zero;
        Simulation.Statics[handle].Pose.Position = position + offset;
        Simulation.Statics.UpdateBounds(handle);
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

    // ---- Groups, spin and impulses (issue #242) -------------------------------------------------

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public void SetGroup(in PhysicsBody body, int group)
    {
        _data.SetGroup(body.Handle, body.IsStatic, group);
        // Pairs already touching keep their contact constraints until Bepu next asks ShouldCollide,
        // which it does every step for pairs whose bounds overlap: nothing else to refresh.
    }

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public int GroupOf(in PhysicsBody body) => _data.GroupOf(body.Handle, body.IsStatic);

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public Vector3 AngularVelocityOf(in PhysicsBody body) =>
        body.IsStatic ? Vector3.Zero : Simulation.Bodies[new BodyHandle(body.Handle)].Velocity.Angular;

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public void SetAngularVelocity(in PhysicsBody body, Vector3 velocity)
    {
        if (body.IsStatic) return;
        var reference = Simulation.Bodies[new BodyHandle(body.Handle)];
        reference.Velocity.Angular = velocity;
        reference.Awake = true;
    }

    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public void ApplyImpulse(in PhysicsBody body, Vector3 impulse, Vector3 worldPoint)
    {
        if (body.IsStatic) return;
        var reference = Simulation.Bodies[new BodyHandle(body.Handle)];
        reference.Awake = true;   // first: waking moves the body into the active set
        reference.ApplyImpulse(impulse, worldPoint - reference.Pose.Position);
    }

    // Issue #248: bounds first (a body posed by hand since the step still has its old ones, and a
    // sleeping body's bounds stay as they were put to sleep), then Bepu's forced sleep, which takes the
    // whole constraint-connected island with it.
    [Experimental(JointsApi.Id, UrlFormat = JointsApi.Url)]
    public void Sleep(ReadOnlySpan<PhysicsBody> bodies)
    {
        var all = Simulation.Bodies;
        for (int i = 0; i < bodies.Length; i++)
        {
            if (bodies[i].IsStatic) continue;
            var handle = new BodyHandle(bodies[i].Handle);
            if (!all.BodyExists(handle)) continue;
            var reference = all[handle];
            reference.Velocity.Linear = Vector3.Zero;
            reference.Velocity.Angular = Vector3.Zero;
            if (reference.Awake) all.UpdateBounds(handle);
        }
        for (int i = 0; i < bodies.Length; i++)
        {
            if (bodies[i].IsStatic) continue;
            var handle = new BodyHandle(bodies[i].Handle);
            if (!all.BodyExists(handle)) continue;
            var reference = all[handle];
            if (reference.Awake) reference.Awake = false;
        }
    }

    // ---- Stepping -----------------------------------------------------------------------------

    // How many of Bepu's worker threads step this space; 0 = the calling thread alone.
    internal int WorkerThreads => _dispatcher?.ThreadCount ?? 0;

    // What the narrow-phase callbacks write into, for tests of its per-worker buffers.
    internal PhysicsCallbackData CallbackData => _data;

    // Never one worker (issue #273): with Simulation.Deterministic, Bepu gives the same bits with any two
    // or more workers, but its one-worker and no-dispatcher paths each give different ones. A two-core
    // machine used to get one worker and so a different simulation from everyone else's, which a replay or
    // a lockstep peer would have parted from within a second. 0 (tests only) steps on the calling thread,
    // which puts every callback on the thread an allocation test measures.
    //
    // Only before the first step: Bepu keeps memory taken from a dispatcher's per-thread pools between
    // steps, so disposing a dispatcher a space has stepped with frees memory the simulation still uses
    // (a test that switched from three workers to seven after 240 steps corrupted the native heap).
    internal void UseWorkers(int count)
    {
        if (_stepped) throw new InvalidOperationException("UseWorkers must be called before the space's first step");
        _dispatcher?.Dispose();
        _dispatcher = count <= 0 ? null : new ThreadDispatcher(Math.Max(2, count));
        _data.UseWorkers(WorkerThreads);   // a report buffer per worker, never shared between two
    }

    internal void Step(float dt)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();   // not StartNew: that is a 40-byte object a tick (#273)
        _stepped = true;
        _data.BeginStep();
        _broken.Clear();
        Simulation.Timestep(dt, _dispatcher);
        _data.EndStep();
        CheckBreaks(dt);
        LastStepMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    // The origin sector moved (14 §3, R6): shift everything by the same offset, once, between ticks.
    // Bepu keeps the authoritative pose of everything it simulates, so a rebase that moved only the
    // components would have physics drag the world back a kilometre on the next step.
    //
    // Beyond moving the poses, every **bounding box** in the broad phase is refreshed, since none of
    // them follows its pose: an unrefreshed static still blocks rays where it used to be, and a
    // sleeping body (whose bounds Bepu keeps in the static tree and only re-reads when it wakes) would
    // be hit where it was and wake a sector away from its own collision box. Awake bodies are
    // re-bounded too, so a query between the rebase and the next step sees them where they are.
    //
    // **A sleeping body stays asleep** (issue #273). Everything moves by the same offset, so nothing a
    // sleeping island rests on or is joined to has moved relative to it: its contacts and joints are
    // stored relative to its bodies, and nothing has to wake. It used to wake every island, which cost a
    // world full of settled crates a burst of solving and a second of settling after every kilometre.
    public void Rebase(Vector3 offset)
    {
        if (offset == Vector3.Zero) return;

        var bodies = Simulation.Bodies;
        int asleep = 0;
        for (int i = 0; i < bodies.Sets.Length; i++)
        {
            ref var set = ref bodies.Sets[i];
            if (!set.Allocated) continue;
            for (int j = 0; j < set.Count; j++)
            {
                set.SolverStates[j].Motion.Pose.Position += offset;
                bodies.UpdateBounds(set.IndexToHandle[j]);
            }
            if (i > 0) asleep += set.Count;
        }

        for (int i = 0; i < Simulation.Statics.Count; i++)
        {
            var handle = Simulation.Statics.IndexToHandle[i];
            ref var description = ref Simulation.Statics[i];
            description.Pose.Position += offset;
            Simulation.Statics.UpdateBounds(handle);
        }

        Log.Info(LogCat.Physics, $"Physics rebased by {offset.X:F0}, {offset.Z:F0} m " +
                                 $"({bodies.ActiveSet.Count} active bodies, {asleep} asleep, {Simulation.Statics.Count} statics)");
    }

    // ---- Queries ------------------------------------------------------------------------------

    // The nearest hit along a ray, or Hit = false. Triggers are invisible to queries unless asked for.
    public RayHit Raycast(Vector3 from, Vector3 direction, float maxDistance, LayerMask mask = default, bool includeTriggers = false,
                          Entity ignore = default)
    {
        var handler = new NearestRayHandler { Data = _data, Mask = mask.Bits == 0 ? LayerMask.All : mask, IncludeTriggers = includeTriggers, Ignore = ignore, Origin = from, Direction = Vector3.Normalize(direction) };
        Simulation.RayCast(from, handler.Direction, maxDistance, ref handler, 0);
        return handler.Hit;
    }

    // Sweeps a shape and returns the first thing it touches (IPhysicsWorld.Sweep says what a start
    // inside something means). Used by the character controller (F7), combat, abilities and projectiles.
    public SweepHit Sweep(in Collider shape, in Pose from, Vector3 direction, float maxDistance, LayerMask mask = default, bool includeTriggers = false,
                          Entity ignore = default, bool ignoreInitialOverlaps = false)
    {
        var pose = new RigidPose(shape.CenterAt(from), from.Rotation);   // `from` is the entity, not the shape
        var unit = Vector3.Normalize(direction);
        var handler = new SweepHandler
        {
            Data = _data, Mask = mask.Bits == 0 ? LayerMask.All : mask, IncludeTriggers = includeTriggers, Ignore = ignore,
            ReportInitialOverlaps = !ignoreInitialOverlaps, Start = pose.Position, Direction = unit,
        };
        var velocity = new BodyVelocity(unit * maxDistance);
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
        if (hit.Hit) hit.Surface = SweepSurface(handler.Collidable, hit);   // issue #270
        return hit;
    }

    // Entities whose bounding boxes overlap a box (10 §4). Bounds only: it may report entities whose
    // shapes don't actually touch, which is fine for "what is around here" queries; Overlap
    // (PhysicsSpace.Overlap.cs, issue #259) tests the shapes.
    public int OverlapBox(Vector3 center, Vector3 halfExtents, Span<Entity> results, LayerMask mask = default, bool includeTriggers = false,
                          Entity ignore = default)
    {
        _overlapResults.Clear();
        var enumerator = new OverlapEnumerator
        {
            Data = _data,
            Mask = mask.Bits == 0 ? LayerMask.All : mask,
            IncludeTriggers = includeTriggers,
            Ignore = ignore,
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

    internal Entity EntityOf(CollidableReference collidable) => _data.EntityOf(collidable);

    // ---- Debug draw ---------------------------------------------------------------------------

    // What Bepu actually holds, where it holds it (IPhysicsWorld.DrawDebug, 10 §9): bodies and statics
    // by their shapes, brush hulls by their bounds, triggers in magenta. Terrain meshes are left out —
    // far too many lines to be useful.
    public void DrawDebug(DebugDraw debug, Vector3 around, float range)
    {
        var bodies = Simulation.Bodies;
        for (int s = 0; s < bodies.Sets.Length; s++)
        {
            ref var set = ref bodies.Sets[s];
            if (!set.Allocated) continue;
            for (int i = 0; i < set.Count; i++)
            {
                var handle = set.IndexToHandle[i];
                var body = bodies[handle];
                if (!body.Collidable.Shape.Exists) continue;   // a joint's world anchor has no shape
                var reference = new CollidableReference(body.Kinematic ? CollidableMobility.Kinematic : CollidableMobility.Dynamic, handle);
                DrawShape(debug, body.Collidable.Shape, body.Pose, _data.IsTrigger(reference), around, range);
            }
        }

        for (int i = 0; i < Simulation.Statics.Count; i++)
        {
            ref var description = ref Simulation.Statics[i];
            var reference = new CollidableReference(Simulation.Statics.IndexToHandle[i]);
            DrawShape(debug, description.Shape, description.Pose, _data.IsTrigger(reference), around, range);
        }

        DrawJoints(debug, around, range);
    }

    private void DrawShape(DebugDraw debug, TypedIndex shape, RigidPose pose, bool trigger, Vector3 around, float range)
    {
        uint colour = trigger ? DebugColour.Magenta : DebugColour.Cyan;
        Vector3 at = pose.Position;
        switch (shape.Type)
        {
            case Sphere.Id:
                if (SageMath.DistanceXZ(at, around) > range) return;
                debug.Sphere(at, Simulation.Shapes.GetShape<Sphere>(shape.Index).Radius, colour);
                return;
            case Capsule.Id:
            {
                if (SageMath.DistanceXZ(at, around) > range) return;
                var capsule = Simulation.Shapes.GetShape<Capsule>(shape.Index);
                float height = capsule.Length + 2f * capsule.Radius;
                debug.Capsule(at - Vector3.UnitY * (height * 0.5f), capsule.Radius, height, colour);
                return;
            }
            case Box.Id:
            {
                if (SageMath.DistanceXZ(at, around) > range) return;
                var box = Simulation.Shapes.GetShape<Box>(shape.Index);
                debug.Box(at, new Vector3(box.HalfWidth, box.HalfHeight, box.HalfLength), pose.Orientation, colour);
                return;
            }
            case ConvexHull.Id:
            {
                // A brush can be larger than the draw range, so it is in range if any of it is.
                Simulation.Shapes.GetShape<ConvexHull>(shape.Index).ComputeBounds(pose.Orientation, out var min, out var max);
                min += at;
                max += at;
                var nearest = new Vector3(Math.Clamp(around.X, min.X, max.X), 0f, Math.Clamp(around.Z, min.Z, max.Z));
                if (SageMath.DistanceXZ(nearest, around) > range) return;
                debug.Box((min + max) * 0.5f, (max - min) * 0.5f, colour);
                return;
            }
            case Compound.Id:
                DrawCompound(debug, shape, pose, trigger, around, range);   // a body and its children's colliders (issue #268)
                return;
            default:
                return;   // meshes (terrain) and anything else
        }
    }

    public void Dispose()
    {
        Simulation.Dispose();
        _dispatcher?.Dispose();
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
        private BepuSimulation _simulation;

        // Touching, for a contact event: a speculative contact (negative depth, up to the speculative
        // margin away) is "about to", and a resting one hovers around zero.
        private const float ContactSlop = 0.01f;

        public void Initialize(BepuSimulation simulation) { _simulation = simulation; }

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
            if (manifold.Count > 0 && Data.WantsContacts(pair)) ReportContact(workerIndex, pair, ref manifold);
            Data.MaterialFor(pair, out float friction, out float restitution);
            pairMaterial = new PairMaterialProperties(friction, 2f, new SpringSettings(30, restitution > 0 ? 0.4f : 1f));
            return true;
        }

        // The deepest contact of a pair that asked for them, in origin space (Bepu gives it relative to
        // A, with the normal from B to A). Poses are only read here: nothing moves during collision
        // detection.
        private void ReportContact<TManifold>(int workerIndex, CollidablePair pair, ref TManifold manifold)
            where TManifold : unmanaged, IContactManifold<TManifold>
        {
            int deepest = 0;
            float depth = float.NegativeInfinity;
            for (int i = 0; i < manifold.Count; i++)
            {
                manifold.GetContact(i, out _, out _, out float d, out _);
                if (d >= depth) { depth = d; deepest = i; }
            }
            // Not touching yet is still reported, as an approach: the solver stops a fast body within
            // the speculative margin, a step before the contact touches, and Collided wants the speed it
            // arrived with, not what was left of it (issue #269).
            bool touching = depth >= -ContactSlop;
            manifold.GetContact(deepest, out var offset, out var normal, out _, out _);
            var a = pair.A;
            Vector3 origin = a.Mobility == CollidableMobility.Static
                ? _simulation.Statics[a.StaticHandle].Pose.Position
                : _simulation.Bodies[a.BodyHandle].Pose.Position;
            var point = origin + offset;

            // How hard (Collided, issue #269): the closing speed along the normal at the contact point,
            // from the velocities the bodies arrive with (collision detection runs before the solve), and
            // the impulse that stops it, through the pair's effective mass. A static or kinematic side
            // has no inverse mass.
            Motion(a, point, out var velocityA, out float inverseA);
            Motion(pair.B, point, out var velocityB, out float inverseB);
            float speed = MathF.Max(0f, -Vector3.Dot(velocityA - velocityB, normal));   // the normal points from B to A
            float inverse = inverseA + inverseB;
            float impulse = inverse > 0f ? speed / inverse : 0f;
            Data.ReportContact(workerIndex, pair, point, normal, impulse, speed, touching);
        }

        // A collidable's velocity at a world point, and its inverse mass (0 for a static or kinematic).
        private readonly void Motion(CollidableReference collidable, Vector3 point, out Vector3 velocity, out float inverseMass)
        {
            if (collidable.Mobility == CollidableMobility.Static)
            {
                velocity = Vector3.Zero;
                inverseMass = 0f;
                return;
            }
            var body = _simulation.Bodies[collidable.BodyHandle];
            var motion = body.Velocity;
            velocity = motion.Linear + Vector3.Cross(motion.Angular, point - body.Pose.Position);
            inverseMass = collidable.Mobility == CollidableMobility.Kinematic ? 0f : body.LocalInertia.InverseMass;
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

        public void Initialize(BepuSimulation simulation) { }

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
        public Entity Ignore;
        public Vector3 Origin;
        public Vector3 Direction;
        public RayHit Hit;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable) => Data.Allows(collidable, Mask, IncludeTriggers, Ignore);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool AllowTest(CollidableReference collidable, int childIndex) => true;

        public void OnRayHit(in BepuPhysics.Trees.RayData ray, ref float maximumT, float t, in Vector3 normal, CollidableReference collidable, int childIndex)
        {
            if (Hit.Hit && t >= Hit.Distance) return;
            maximumT = t;   // nearest hit only
            Hit = new RayHit
            {
                Entity = Data.EntityOf(collidable, childIndex),
                Position = Origin + Direction * t,
                Normal = Vector3.Normalize(normal),
                Distance = t,
                Hit = true,
                Surface = Data.SurfaceAt(collidable, childIndex, normal),   // issue #270
            };
        }
    }

    private struct SweepHandler : ISweepHitHandler
    {
        public bool IncludeTriggers;
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public Entity Ignore;
        public bool ReportInitialOverlaps;
        public CollidableReference Collidable;   // what Hit is on, for its surface (issue #270)
        public Vector3 Start;       // the shape's centre where the sweep begins
        public Vector3 Direction;   // unit
        public SweepHit Hit;

        public bool AllowTest(CollidableReference collidable) => Data.Allows(collidable, Mask, IncludeTriggers, Ignore);
        public bool AllowTest(CollidableReference collidable, int child) => true;

        public void OnHit(ref float maximumT, float t, in Vector3 hitLocation, in Vector3 hitNormal, CollidableReference collidable)
        {
            if (Hit.Hit && t >= Hit.Distance) return;
            maximumT = t;
            Collidable = collidable;
            Hit = new SweepHit
            {
                Entity = Data.EntityOf(collidable),
                Position = hitLocation,
                Normal = Vector3.Normalize(hitNormal),
                Distance = t,
                Hit = true,
            };
        }

        // Already touching at the start: Bepu has no location or normal for it. It used to be dropped
        // for everyone, which is right for the character controller (it rests a skin width above the
        // floor, and recording this hid the real hits further along) and wrong for everything else: a
        // swing that starts inside its target, or a bolt fired point-blank, reported nothing at all
        // (issue #30). So it is a hit at distance 0 unless the caller asks otherwise, and being the
        // nearest possible it wins; the caller that started inside itself says so with `ignore`.
        // Getting *out* of a surface is Overlap's job (issue #259).
        public void OnHitAtZeroT(ref float maximumT, CollidableReference collidable)
        {
            if (!ReportInitialOverlaps || (Hit.Hit && Hit.StartsInside)) return;
            maximumT = 0f;
            Collidable = collidable;
            Hit = new SweepHit
            {
                Entity = Data.EntityOf(collidable),
                Position = Start,
                Normal = -Direction,
                Distance = 0f,
                Hit = true,
                StartsInside = true,
            };
        }
    }

    private struct OverlapEnumerator : IBreakableForEach<CollidableReference>
    {
        public bool IncludeTriggers;
        public PhysicsCallbackData Data;
        public LayerMask Mask;
        public Entity Ignore;
        public List<Entity> Results;   // a list the space reuses: a Span can't live in a non-ref struct
        public int Limit;

        public bool LoopBody(CollidableReference collidable)
        {
            if (!Data.Allows(collidable, Mask, IncludeTriggers, Ignore)) return true;
            var entity = Data.EntityOf(collidable);
            if (entity.IsNull) return true;
            Results.Add(entity);
            return Results.Count < Limit;
        }
    }
}
#pragma warning restore SAGE0134
