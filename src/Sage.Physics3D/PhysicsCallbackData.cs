#nullable enable
using System;
using System.Collections.Generic;
using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection;
using Friflo.Engine.ECS;

namespace Sage.Physics3D;

// What Bepu's callbacks are allowed to touch (docs/design/10 §3). Reads (layers, entity mapping,
// materials) happen on worker threads during the step and are only written between steps from the
// main thread. Trigger overlaps are written into per-worker buffers and merged afterwards.
internal sealed class PhysicsCallbackData
{
    private const int Grow = 64;

    private Entry[] _bodies = new Entry[Grow];
    private Entry[] _statics = new Entry[Grow];
    private readonly List<(uint A, uint B)>[] _workerTriggers;

    private readonly HashSet<(uint A, uint B)> _overlapping = new();   // pairs overlapping last step
    private readonly HashSet<(uint A, uint B)> _current = new();
    private readonly List<TriggerOverlap> _entered = new();
    private readonly List<TriggerOverlap> _exited = new();

    public PhysicsCallbackData(int workerCount)
    {
        _workerTriggers = new List<(uint, uint)>[workerCount];
        for (int i = 0; i < workerCount; i++) _workerTriggers[i] = new List<(uint, uint)>();
    }

    public LayerMatrix Layers { get; } = new();

    // Spans, not interfaces: systems read these every tick and foreach over an interface would box
    // an enumerator each time.
    public ReadOnlySpan<TriggerOverlap> Entered => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_entered);
    public ReadOnlySpan<TriggerOverlap> Exited => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_exited);

    private struct Entry
    {
        public Entity Entity;
        public byte Layer;
        public bool Trigger;
        public bool Used;
        public float Friction;
        public float Restitution;
    }

    // ---- Registration (main thread, between steps) ----

    public void RegisterBody(int handle, Entity entity, byte layer, bool trigger, float friction, float restitution) =>
        Register(ref _bodies, handle, entity, layer, trigger, friction, restitution);

    public void RegisterStatic(int handle, Entity entity, byte layer, bool trigger, float friction, float restitution) =>
        Register(ref _statics, handle, entity, layer, trigger, friction, restitution);

    public void UnregisterBody(int handle) { if (handle < _bodies.Length) _bodies[handle] = default; }
    public void UnregisterStatic(int handle) { if (handle < _statics.Length) _statics[handle] = default; }

    private static void Register(ref Entry[] entries, int handle, Entity entity, byte layer, bool trigger, float friction, float restitution)
    {
        if (handle >= entries.Length) Array.Resize(ref entries, Math.Max(handle + 1, entries.Length * 2));
        entries[handle] = new Entry { Entity = entity, Layer = layer, Trigger = trigger, Used = true, Friction = friction, Restitution = restitution };
    }

    // ---- Callbacks (worker threads, during the step) ----

    private ref Entry EntryOf(CollidableReference collidable)
    {
        if (collidable.Mobility == CollidableMobility.Static)
        {
            int handle = collidable.StaticHandle.Value;
            if (handle < _statics.Length) return ref _statics[handle];
        }
        else
        {
            int handle = collidable.BodyHandle.Value;
            if (handle < _bodies.Length) return ref _bodies[handle];
        }
        return ref _missing;
    }

    private Entry _missing;

    public bool ShouldCollide(CollidableReference a, CollidableReference b)
    {
        ref var ea = ref EntryOf(a);
        ref var eb = ref EntryOf(b);
        if (!ea.Used || !eb.Used) return true;              // unknown collidables collide by default
        if (ea.Trigger && eb.Trigger) return false;         // two triggers ignore each other
        return Layers.Collide(ea.Layer, eb.Layer);
    }

    public bool IsTrigger(CollidableReference collidable) => EntryOf(collidable).Trigger;

    public void MaterialFor(CollidablePair pair, out float friction, out float restitution)
    {
        ref var a = ref EntryOf(pair.A);
        ref var b = ref EntryOf(pair.B);
        friction = MathF.Sqrt(MathF.Max(a.Friction, 0.01f) * MathF.Max(b.Friction, 0.01f));
        restitution = MathF.Max(a.Restitution, b.Restitution);
    }

    public void ReportTrigger(int workerIndex, CollidablePair pair)
    {
        if ((uint)workerIndex >= (uint)_workerTriggers.Length) workerIndex = 0;
        _workerTriggers[workerIndex].Add((pair.A.Packed, pair.B.Packed));
    }

    public Entity EntityOf(CollidableReference collidable) => EntryOf(collidable).Entity;

    // Query filter: is this collidable on a layer the query asked for?
    // Queries ask about *solid* things by default: a trigger has no surface to stop a ray, a sweep or
    // a sword, and something that wants overlaps reads the trigger lists (10 §3). Before this, a
    // trigger volume blocked line of sight and a swing could "hit" thin air (review #53).
    public bool Allows(CollidableReference collidable, LayerMask mask, bool includeTriggers)
    {
        ref var entry = ref EntryOf(collidable);
        if (!entry.Used) return true;
        return mask.Has(entry.Layer) && (includeTriggers || !entry.Trigger);
    }

    // ---- Step boundaries (main thread) ----

    public void BeginStep()
    {
        foreach (var list in _workerTriggers) list.Clear();
        _entered.Clear();
        _exited.Clear();
    }

    // Turns this step's overlapping pairs into enter/exit lists (10 §3).
    public void EndStep()
    {
        _current.Clear();
        foreach (var list in _workerTriggers)
            foreach (var pair in list)
            {
                var key = pair.A <= pair.B ? pair : (pair.B, pair.A);
                if (!_current.Add(key)) continue;
                if (!_overlapping.Contains(key) && Resolve(key, out var overlap)) _entered.Add(overlap);
            }

        foreach (var key in _overlapping)
            if (!_current.Contains(key) && Resolve(key, out var overlap)) _exited.Add(overlap);

        _overlapping.Clear();
        foreach (var key in _current) _overlapping.Add(key);
    }

    // The trigger side first, then what entered it. Pairs whose entities are gone are dropped.
    private bool Resolve((uint A, uint B) key, out TriggerOverlap overlap)
    {
        var a = new CollidableReference { Packed = key.A };
        var b = new CollidableReference { Packed = key.B };
        ref var ea = ref EntryOf(a);
        ref var eb = ref EntryOf(b);
        overlap = ea.Trigger ? new TriggerOverlap(ea.Entity, eb.Entity) : new TriggerOverlap(eb.Entity, ea.Entity);
        return ea.Used && eb.Used && !overlap.Trigger.IsNull && !overlap.Other.IsNull;
    }
}
