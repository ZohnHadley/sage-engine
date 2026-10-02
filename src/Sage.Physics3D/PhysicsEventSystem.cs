#nullable enable
namespace Sage.Physics3D;

// PostPhysics: the step's trigger and contact buffers become game events (issue #269, PhysicsEvents.cs),
// so a system reads TriggerEntered/TriggerExited/Collided/CollisionEnded with its own cursor instead of
// a span that the next step clears. Right after the write-back, so a reader later in PostPhysics finds
// the bodies where the events happened. Events are structs in a queue that grows to its high-water mark:
// nothing is allocated per tick.
[System("sage.physics.events", Phase.PostPhysics, After = new[] { "sage.physics.write_back" })]
internal sealed class PhysicsEventSystem : ISystem
{
    private readonly IPhysicsWorld _space;

    public PhysicsEventSystem(IPhysicsWorld space) { _space = space; }

    public void Run(in SystemContext ctx)
    {
        var events = ctx.World.Events;
        foreach (var overlap in _space.TriggerEnter) events.Send(new TriggerEntered(overlap.Trigger, overlap.Other));
        foreach (var overlap in _space.TriggerExit) events.Send(new TriggerExited(overlap.Trigger, overlap.Other));
        foreach (var contact in _space.ContactBegin)
            events.Send(new Collided(contact.A, contact.B, contact.Point, contact.Normal, contact.Impulse, contact.Speed));
        foreach (var contact in _space.ContactEnd) events.Send(new CollisionEnded(contact.A, contact.B));
    }
}
