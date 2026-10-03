#nullable enable
using System;
using System.Collections.Generic;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// Structural changes as game events (04 §3.3, issue #282): `Added<T>` when an entity gains a T, and
// `Removed<T>` when it loses one, read with a cursor like any other game event:
//
//     _added   = world.Events.Reader<Added<Health>>(this);
//     _removed = world.Events.Reader<Removed<Health>>(this);
//     foreach (ref readonly var ev in _added.Read())   { ... ev.Entity ... }
//     foreach (ref readonly var ev in _removed.Read()) { ... ev.Entity, ev.Value ... }
//
// Before this a system could only hear of one through `World.ComponentAdded`/`ComponentRemoved`, plain
// C# events that run its code in the middle of whoever made the change — the immediate callback 04 §3.1
// rules out. Those stay, for the world's own bookkeeping (indexes, the editor); gameplay reads these.
//
// The rules:
// - **Opt-in per component type.** Nothing is published for a T until a queue of `Added<T>` or
//   `Removed<T>` exists (asking for a reader makes one). A type nobody reads costs one array lookup per
//   change; a world with no structural readers at all costs one null check.
// - **Every real add and every real remove.** Adding a T an entity already has replaces its value: that
//   is an update, not an `Added<T>`. Destroying an entity sends a `Removed<T>` for each T it had, before
//   `World.EntityDestroyed`; changes played back from `world.Commands` are sent at playback, which is the
//   end of the phase they were recorded in.
// - **Sent when the change happens, read when the reader runs.** A reader later in the tick sees this
//   tick's changes; one earlier sees them next tick. An add and a remove in the same tick are both
//   delivered, each in its own queue. `Sequence` puts them in one order across both (and across types):
//   it counts up per world, so a reader that must know whether a T was added then removed or removed then
//   added (a component replaced in one tick) compares the two. Most readers need not: what the entity has
//   *now* is `world.Has<T>(entity)`.
// - **A statement about the past.** `Removed<T>.Entity` may be dead by the time it is read (it was
//   destroyed, which is why the component went), so `Value` carries the component as it was when it went.
// - **Fixed or Frame.** The changes go to whichever of the two queues exists, so a display-rate reader
//   (`Reader<Added<T>>(this, Schedule.Frame)`) sees the changes from every tick and from frame-side code.
// - **Allocation-free once warm**: the queues grow to their high-water mark and stop.

// An entity gained a T (it did not have one before).
[GameEvent]
public readonly record struct Added<T>(Entity Entity, long Sequence) : IStructuralEvent where T : struct, IComponent
{
    void IStructuralEvent.Attach(StructuralEvents hub, object queue) => hub.Tap<T>().Attach((EventQueue<Added<T>>)queue);
}

// An entity lost its T: removed, or the entity was destroyed. `Value` is the component as it was.
[GameEvent]
public readonly record struct Removed<T>(Entity Entity, T Value, long Sequence) : IStructuralEvent where T : struct, IComponent
{
    void IStructuralEvent.Attach(StructuralEvents hub, object queue) => hub.Tap<T>().Attach((EventQueue<Removed<T>>)queue);
}

// What lets GameEvents wire a new queue of a structural event to the world's changes without knowing T.
internal interface IStructuralEvent
{
    void Attach(StructuralEvents hub, object queue);
}

// The world's side: one tap per component type somebody reads, found by Friflo's struct index.
internal sealed class StructuralEvents
{
    private readonly GameEvents _events;
    private ComponentTap?[] _byIndex = Array.Empty<ComponentTap?>();
    private readonly List<ComponentTap> _taps = new();
    private long _sequence;

    internal StructuralEvents(GameEvents events) => _events = events;

    internal bool Any => _taps.Count > 0;
    internal long NextSequence() => ++_sequence;
    internal long NowTick => _events.NowTick;

    internal ComponentTap<T> Tap<T>() where T : struct, IComponent
    {
        int index = F.EntityStore.GetEntitySchema().ComponentTypeByType[typeof(T)].StructIndex;
        if (index >= _byIndex.Length) Array.Resize(ref _byIndex, Math.Max(index + 1, _byIndex.Length * 2));
        if (_byIndex[index] is ComponentTap<T> existing) return existing;
        var tap = new ComponentTap<T>(this);
        _byIndex[index] = tap;
        _taps.Add(tap);
        return tap;
    }

    internal void OnAdded(in F.ComponentChanged change)
    {
        if (change.Action != F.ComponentChangedAction.Add) return;   // a replaced value is not an add
        if (Find(change.ComponentType.StructIndex) is { } tap) tap.Added(change);
    }

    internal void OnRemoved(in F.ComponentChanged change)
    {
        if (Find(change.ComponentType.StructIndex) is { } tap) tap.Removed(change);
    }

    // An entity is being deleted, still alive with all its components.
    internal void OnDeleting(F.Entity entity)
    {
        for (int i = 0; i < _taps.Count; i++) _taps[i].Deleting(entity);
    }

    private ComponentTap? Find(int index) => (uint)index < (uint)_byIndex.Length ? _byIndex[index] : null;
}

internal abstract class ComponentTap
{
    internal abstract void Added(in F.ComponentChanged change);
    internal abstract void Removed(in F.ComponentChanged change);
    internal abstract void Deleting(F.Entity entity);
}

internal sealed class ComponentTap<T> : ComponentTap where T : struct, IComponent
{
    private readonly StructuralEvents _hub;
    private EventQueue<Added<T>>? _addedFixed, _addedFrame;
    private EventQueue<Removed<T>>? _removedFixed, _removedFrame;

    internal ComponentTap(StructuralEvents hub) => _hub = hub;

    internal void Attach(EventQueue<Added<T>> queue)
    {
        if (queue.Schedule == Schedule.Fixed) _addedFixed = queue; else _addedFrame = queue;
    }

    internal void Attach(EventQueue<Removed<T>> queue)
    {
        if (queue.Schedule == Schedule.Fixed) _removedFixed = queue; else _removedFrame = queue;
    }

    internal override void Added(in F.ComponentChanged change)
    {
        if (_addedFixed == null && _addedFrame == null) return;
        var ev = new Added<T>(change.Entity.AsSage(), _hub.NextSequence());
        _addedFixed?.Send(ev, _hub.NowTick);
        _addedFrame?.Send(ev, _hub.NowTick);
    }

    internal override void Removed(in F.ComponentChanged change) => Send(change.Entity, change.OldComponent<T>());

    internal override void Deleting(F.Entity entity)
    {
        if ((_removedFixed != null || _removedFrame != null) && entity.TryGetComponent(out T value))
            Send(entity, value);
    }

    private void Send(F.Entity entity, in T value)
    {
        if (_removedFixed == null && _removedFrame == null) return;
        var ev = new Removed<T>(entity.AsSage(), value, _hub.NextSequence());
        _removedFixed?.Send(ev, _hub.NowTick);
        _removedFrame?.Send(ev, _hub.NowTick);
    }
}
