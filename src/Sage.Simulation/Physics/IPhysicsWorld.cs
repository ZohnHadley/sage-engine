#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// What the simulation asks of a world's physics without naming its engine (REDESIGN §3.1, issue #24;
// the seed #30 grows into the full query and body API). The 3D plugin (Sage.Physics3D, Bepu) installs
// its space under this interface too, so levels, origin rebasing, entity I/O and the scale commands
// work with whichever physics a game loads, and a world with none simply has no resource.
public interface IPhysicsWorld
{
    int BodyCount { get; }
    int StaticCount { get; }
    double LastStepMilliseconds { get; }

    // Trigger volumes entered and left during the last step (read in PostPhysics).
    ReadOnlySpan<TriggerOverlap> TriggerEnter { get; }
    ReadOnlySpan<TriggerOverlap> TriggerExit { get; }

    // A static collider from a convex point cloud (a brush). A default PhysicsBody means it failed.
    PhysicsBody AddHull(Entity entity, ReadOnlySpan<Vector3> points, Vector3 position, byte layer = 0, bool isTrigger = false);

    // Moves everything by `offset` when the floating origin shifts (14).
    void Rebase(Vector3 offset);
}
