#nullable enable
using System.Numerics;

namespace Sage.Simulation;

// Physics as game events (issue #269, 10 §3, 04 §3.2): what the step's trigger and contact buffers
// (IPhysicsWorld.TriggerEnter/TriggerExit/ContactBegin/ContactEnd) said, sent on the world's event bus
// in PostPhysics by the physics plugin, so a system that wants them in code — footsteps, impact sounds,
// fall damage, a pressure plate's script — reads them with a cursor (`world.Events.Reader<Collided>(this)`)
// instead of a span that is gone after the next step. The entity-I/O outputs a mapper wires
// (OnStartTouch/OnEndTouch) are unchanged; these are the same facts for code.
//
// Contacts are still opt-in: only a collider with ReportContacts (the `body` part's "contacts") is
// tracked, so a crowded world pays nothing for the pairs nobody asked about.

// Something entered a trigger volume during the last step.
[GameEvent]
public readonly record struct TriggerEntered(Entity Trigger, Entity Other);

// Something left a trigger volume (or was destroyed in it) during the last step.
[GameEvent]
public readonly record struct TriggerExited(Entity Trigger, Entity Other);

// Two solid colliders began touching, at least one of which asked for contacts. Normal points from B
// toward A and Point is where they touch (ContactEvent). Speed is how fast they were closing along the
// normal (m/s) and Impulse the impulse it takes to stop that (N·s, closing speed times the pair's
// effective mass; 0 against something that cannot be moved by it, a kinematic body on a static): what an
// impact sound scales its volume by and what fall or crush damage is worked out from. It is an estimate
// made when the contact begins, not the solver's accumulated impulse.
[GameEvent]
public readonly record struct Collided(Entity A, Entity B, Vector3 Point, Vector3 Normal, float Impulse, float Speed)
{
    // The other side of the contact, for a reader that knows which entity it cares about.
    public Entity OtherThan(Entity self) => self == A ? B : A;
}

// Two colliders that had Collided stopped touching (lifted off, pushed apart, or one was removed).
[GameEvent]
public readonly record struct CollisionEnded(Entity A, Entity B)
{
    public Entity OtherThan(Entity self) => self == A ? B : A;
}
