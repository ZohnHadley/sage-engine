#nullable enable
using System;
using System.Collections.Generic;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// One system's structural changes while its phase runs systems in parallel (issue #288): what it would
// have recorded on the world's command buffer, in the order it recorded it, kept apart so that systems
// on different threads never share a buffer. At the end of the phase the world replays every system's
// log into its buffer **in the phase's system order**, so the buffer the world plays back holds exactly
// what a sequential run would have put there, call for call — the reason parallel results are identical
// rather than merely equivalent. (Friflo plays a buffer back grouped by kind, so playing several buffers
// one after another would not be.)
//
// Reused tick after tick: the op list and each component type's value list keep their capacity, so a
// steady state records and replays without allocating.
internal sealed class SystemCommandLog
{
    private enum Kind : byte { Add, Remove, AddTag, RemoveTag, SetParent, Destroy }

    private struct Op
    {
        public Kind Kind;
        public int Entity;
        public int Other;      // SetParent: the parent; Add: the slot in its value list
        public Replay? Typed;  // Add: the value list; Remove/AddTag/RemoveTag: the type's replayer
    }

    private readonly List<Op> _ops = new();
    private readonly Dictionary<Type, Replay> _values = new();
    private readonly List<Replay> _used = new();

    public int Count => _ops.Count;

    public void Add<T>(int entity, in T component) where T : struct, IComponent
    {
        if (!_values.TryGetValue(typeof(T), out var store)) _values[typeof(T)] = store = new Values<T>();
        var values = (Values<T>)store;
        if (values.Items.Count == 0) _used.Add(values);
        values.Items.Add(component);
        _ops.Add(new Op { Kind = Kind.Add, Entity = entity, Other = values.Items.Count - 1, Typed = values });
    }

    public void Remove<T>(int entity) where T : struct, IComponent =>
        _ops.Add(new Op { Kind = Kind.Remove, Entity = entity, Typed = Removal<T>.Instance });

    public void AddTag<T>(int entity) where T : struct, ITag =>
        _ops.Add(new Op { Kind = Kind.AddTag, Entity = entity, Typed = TagChange<T>.Add });

    public void RemoveTag<T>(int entity) where T : struct, ITag =>
        _ops.Add(new Op { Kind = Kind.RemoveTag, Entity = entity, Typed = TagChange<T>.Remove });

    public void SetParent(int child, int parent) =>
        _ops.Add(new Op { Kind = Kind.SetParent, Entity = child, Other = parent });

    public void Destroy(int entity) => _ops.Add(new Op { Kind = Kind.Destroy, Entity = entity });

    // Records everything on `buffer`, in order, and empties the log.
    public void ReplayInto(F.CommandBuffer buffer)
    {
        for (int i = 0; i < _ops.Count; i++)
        {
            var op = _ops[i];
            switch (op.Kind)
            {
                case Kind.SetParent: buffer.AddChild(op.Other, op.Entity); break;
                case Kind.Destroy: buffer.DeleteEntity(op.Entity); break;
                default: op.Typed!.Record(buffer, op.Entity, op.Other); break;
            }
        }
        Clear();
    }

    public void Clear()
    {
        _ops.Clear();
        for (int i = 0; i < _used.Count; i++) _used[i].Clear();
        _used.Clear();
    }

    private abstract class Replay
    {
        public abstract void Record(F.CommandBuffer buffer, int entity, int slot);
        public virtual void Clear() { }
    }

    private sealed class Values<T> : Replay where T : struct, IComponent
    {
        public readonly List<T> Items = new();
        public override void Record(F.CommandBuffer buffer, int entity, int slot) => buffer.AddComponent(entity, Items[slot]);
        public override void Clear() => Items.Clear();
    }

    private sealed class Removal<T> : Replay where T : struct, IComponent
    {
        public static readonly Removal<T> Instance = new();
        public override void Record(F.CommandBuffer buffer, int entity, int slot) => buffer.RemoveComponent<T>(entity);
    }

    private sealed class TagChange<T> : Replay where T : struct, ITag
    {
        public static readonly TagChange<T> Add = new(true);
        public static readonly TagChange<T> Remove = new(false);
        private readonly bool _add;
        private TagChange(bool add) => _add = add;
        public override void Record(F.CommandBuffer buffer, int entity, int slot)
        {
            if (_add) buffer.AddTag<T>(entity);
            else buffer.RemoveTag<T>(entity);
        }
    }
}
