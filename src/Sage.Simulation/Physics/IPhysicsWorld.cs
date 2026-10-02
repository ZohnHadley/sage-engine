#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// What the simulation and gameplay ask of a world's physics without naming its engine (REDESIGN §0.5,
// §3.1, issue #30). One interface for both spatial modes: the 3D plugin (Sage.Physics3D, Bepu)
// implements it today and a 2D backend (Sage.Physics2D, phase 7a) will implement the same thing, so
// combat, AI, navigation, abilities, items, movers, levels and entity I/O run on either.
//
// A world with a physics plugin has it as a resource (`world.Resources.Get<IPhysicsWorld>()`); a world
// with none simply has no resource, and code that can live without physics uses TryGet.
//
// Everything here is in origin space (14), the same as GlobalTransform, and uses only Sage types: a
// backend's own handles stay behind PhysicsBody.Handle, and its shapes behind Collider.
public interface IPhysicsWorld
{
    // ---- The world ------------------------------------------------------------------------------

    int BodyCount { get; }                 // active (awake) bodies
    int StaticCount { get; }
    double LastStepMilliseconds { get; }
    Vector3 Gravity { get; }

    // Which layers collide, by name: the physics_layers record (10 §3). Engine and gameplay code name a
    // layer through this (Layers.Enemy, Layers.TryIndexOf("pickup")), never by its index.
    LayerMatrix Layers { get; }

    // Moves everything by `offset` when the floating origin shifts (14).
    void Rebase(Vector3 offset);

    // ---- Bodies ---------------------------------------------------------------------------------
    //
    // Entities with a Collider (and optionally a RigidBody) get a body from the physics plugin's own
    // systems; these are for the engine code that builds colliders itself (levels, terrain) and for
    // whatever moves one by hand.

    // A body for `collider` as `body` says (Static, Kinematic or Dynamic), with the entity at `pose`.
    PhysicsBody AddBody(Entity entity, in Collider collider, in RigidBody body, in Pose pose);

    // Removes a body or a static, and any shape that was built for it alone.
    void RemoveBody(in PhysicsBody body);

    // A static collider from a convex point cloud (a brush). A default PhysicsBody means it failed.
    PhysicsBody AddHull(Entity entity, ReadOnlySpan<Vector3> points, Vector3 position, byte layer = 0, bool isTrigger = false);

    // A static collider from triangles (terrain), `indices` three per triangle.
    PhysicsBody AddMesh(Entity entity, ReadOnlySpan<Vector3> vertices, ReadOnlySpan<int> indices, Vector3 position, byte layer = 0);

    // Moves a static the engine built (a door, a lift) and refreshes its bounds.
    void MoveStatic(in PhysicsBody body, Vector3 position);

    // Where the body's shape is (its centre, not the entity's origin: see Collider.Center).
    Pose PoseOf(in PhysicsBody body);

    // Puts the body where the entity at `pose` has its collider.
    void SetPose(in PhysicsBody body, in Collider collider, in Pose pose);

    Vector3 VelocityOf(in PhysicsBody body);
    void SetVelocity(in PhysicsBody body, Vector3 velocity);

    bool IsDynamic(in PhysicsBody body);   // moved by physics: its pose is written back to the transform
    bool IsAwake(in PhysicsBody body);

    // ---- Groups, spin and impulses (issue #242, SAGE0134) -----------------------------------------
    //
    // A collision group: bodies sharing a nonzero group never collide with each other (a ragdoll's
    // limbs and the capsule of the character they belong to); 0 is no group. Layers still apply to
    // bodies in different groups.

    // AddBody, with the body in `group` from the start.
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    PhysicsBody AddBody(Entity entity, in Collider collider, in RigidBody body, in Pose pose, int group);

    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    void SetGroup(in PhysicsBody body, int group);
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    int GroupOf(in PhysicsBody body);

    // Radians per second about each world axis.
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    Vector3 AngularVelocityOf(in PhysicsBody body);
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    void SetAngularVelocity(in PhysicsBody body, Vector3 velocity);

    // An impulse (N·s) at a world point: it pushes and, off the centre, spins. Wakes the body.
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    void ApplyImpulse(in PhysicsBody body, Vector3 impulse, Vector3 worldPoint);

    // Stops these bodies (velocities zeroed) and puts them, and whatever is jointed to them, to sleep
    // where they are now, as the backend would once they had rested a while (issue #248: a ragdoll that
    // has settled, or is loaded settled). Their bounds are refreshed first, so a body moved since the last
    // step is found where it is. Anything that wakes a body (a touch, an impulse, a new velocity or pose)
    // wakes them again. Statics and stale handles are ignored.
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    void Sleep(ReadOnlySpan<PhysicsBody> bodies);

    // ---- Joints (issue #242, SAGE0134) ------------------------------------------------------------
    //
    // A joint holds two bodies together (JointDesc says how and where, in each body's shape space);
    // `a` is a body (kinematic or dynamic), `b` a body or a static. Removing a body removes its joints;
    // a joint that breaks (JointDesc.BreakForce) is removed and reported once in JointBroken.

    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    PhysicsJoint AddJoint(in PhysicsBody a, in PhysicsBody b, in JointDesc desc);

    // A joint from `a` to the world: AnchorB is a world point, B's space is world space.
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    PhysicsJoint AddJoint(in PhysicsBody a, in JointDesc desc);

    // Removes a joint; a stale or default handle is ignored.
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    void RemoveJoint(in PhysicsJoint joint);

    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    bool JointExists(in PhysicsJoint joint);

    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    int JointCount { get; }

    // ---- Queries --------------------------------------------------------------------------------
    //
    // Every query takes a LayerMask (default = every layer) and skips triggers unless asked: a trigger
    // has no surface to stop a ray, a sweep or a sword (review #53). `ignore` leaves one entity out,
    // usually the one asking (a swing starts inside its own attacker). A query-only layer (hitboxes,
    // LayerMatrix.QueryOnly, issue #137) is seen only by a mask that names nothing else, and then never
    // the colliders parented to `ignore`: the asker's own hitboxes.

    // The nearest hit along a ray, or Hit = false.
    RayHit Raycast(Vector3 from, Vector3 direction, float maxDistance, LayerMask mask = default,
                   bool includeTriggers = false, Entity ignore = default);

    // A shape cast: sweeps `shape`, placed as for an entity at `from`, and returns the first thing it
    // touches. Something the shape already overlaps where it starts is a hit at distance 0 with
    // StartsInside set (it has no normal, so Normal is -direction), and it wins over anything further
    // along: a sword that starts inside its target hits it. `ignoreInitialOverlaps` turns that off for a
    // caller that keeps its own distance from surfaces (the character controller's skin) and wants only
    // what it is moving into.
    SweepHit Sweep(in Collider shape, in Pose from, Vector3 direction, float maxDistance, LayerMask mask = default,
                   bool includeTriggers = false, Entity ignore = default, bool ignoreInitialOverlaps = false);

    // Entities whose bounds overlap a box, up to results.Length of them (truncated, never thrown). May
    // report ones whose shapes don't quite touch (bounds, not shapes: 10 §4), which suits "what is
    // around here"; Overlap below tests the shapes themselves.
    int OverlapBox(Vector3 center, Vector3 halfExtents, Span<Entity> results, LayerMask mask = default,
                   bool includeTriggers = false, Entity ignore = default);

    // What `shape`, placed as for an entity at `at`, actually intersects (the narrow phase, issue #259),
    // deepest first, up to results.Length of them (truncated to the deepest, never thrown). Each hit says
    // which way out and how far: the character controller's depenetration. Touching is not overlapping:
    // only a positive depth is reported.
    int Overlap(in Collider shape, in Pose at, Span<OverlapHit> results, LayerMask mask = default,
                bool includeTriggers = false, Entity ignore = default);

    // The same for a body or static already in the space, where it is now: what a mover has just moved
    // into (issue #260). Normal is the way out for `body`, so the way out for what it hit is -Normal. The
    // body's own entity is left out.
    int Overlap(in PhysicsBody body, Span<OverlapHit> results, LayerMask mask = default, bool includeTriggers = false);

    // ---- Events (read in PostPhysics; valid until the next step) --------------------------------

    // Trigger volumes entered and left during the last step.
    ReadOnlySpan<TriggerOverlap> TriggerEnter { get; }
    ReadOnlySpan<TriggerOverlap> TriggerExit { get; }

    // Solid contacts that began and ended during the last step, for colliders that ask for them
    // (Collider.ReportContacts; the `body` part's "contacts"). Nothing is tracked for the rest.
    ReadOnlySpan<ContactEvent> ContactBegin { get; }
    ReadOnlySpan<ContactEvent> ContactEnd { get; }

    // Joints that broke during the last step, each once, already removed (JointDesc.BreakForce).
    [System.Diagnostics.CodeAnalysis.Experimental(PhysicsJointsApi.Experimental, UrlFormat = PhysicsJointsApi.Url)]
    ReadOnlySpan<JointBroken> JointBroken { get; }

    // ---- Debug draw (10 §9) ---------------------------------------------------------------------

    // Draws what the backend actually simulates — shapes where it has them, bounds for brush hulls,
    // triggers in magenta, joints as yellow lines between their anchors (issue #242) — within `range` of
    // `around` on the ground plane. PhysicsDebugSystem calls it for `phys_debug 1`, so every backend
    // draws the same way.
    void DrawDebug(DebugDraw debug, Vector3 around, float range);
}
