#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// A system that says what it touches (docs/design/03 §3.5, issue #288), so the world may run it at the
// same time as others of its phase that touch nothing it writes:
//
//     [System("mygame.regen", Phase.Gameplay)]
//     public sealed class RegenSystem : IDeclaresAccess
//     {
//         public void Declare(SystemAccess access) => access.Reads<Stats>().Writes<Health>().Sends<Healed>();
//         public void Run(in SystemContext ctx) { … }
//     }
//
// Declare is called once, when the system is added to a world (or replaces another). A system that
// does not implement this runs as it always has: alone, in its place in the phase's order.
public interface IDeclaresAccess : ISystem
{
    void Declare(SystemAccess access);
}

// What one system reads and writes (issue #288): components, game events, world resources — or
// everything (Exclusive). Two systems of a phase **conflict** when one writes what the other reads or
// writes (a component or resource type), when one sends an event type the other sends or reads, when
// either is exclusive, or when one is ordered against the other (Before/After). Conflicting systems run
// one after the other in the phase's order; the others may run at the same time. Since every pair whose
// order could matter keeps it, the results are the same as running the phase sequentially.
//
// What needs no declaration:
//   - **structural changes recorded on `ctx.Commands`** (adding or removing components and tags,
//     destroying): nothing sees them until the end of the phase, and the world replays every system's in
//     the phase's order. A structural change made *directly* (World.Create, Entity.AddComponent,
//     World.FlushCommands) is not allowed in a parallel system: declare it Exclusive.
//   - tags (only structural changes alter them), and whether an entity has a component.
//   - reading events of a type nobody in the phase sends (readers each keep their own cursor).
//
// **Checked in dev builds** (`sys_access_check`): every component, resource and event a declared system
// touches through the world while it runs is compared with its declaration, and a type it did not declare
// is reported (`world.Systems.AccessViolations`, an error naming the system and the type); a component
// declared Reads that changes while only its readers ran is reported too. What the check cannot see is a
// resource the system took in its constructor and keeps: declare those as well, since the scheduler
// trusts the declaration.
public sealed class SystemAccess
{
    private F.ComponentTypes _reads;
    private F.ComponentTypes _writes;
    private readonly HashSet<Type> _eventReads = new();
    private readonly HashSet<Type> _eventSends = new();
    private readonly HashSet<Type> _resourceReads = new();
    private readonly HashSet<Type> _resourceWrites = new();
    private readonly List<ComponentProbe> _readProbes = new();
    private readonly List<Type> _componentReads = new();
    private readonly List<Type> _componentWrites = new();

    internal SystemAccess(World world) => World = world;

    // The world the system is being added to: for a declaration that depends on what it has.
    public World World { get; }

    // Conflicts with every other system: runs alone, in its place.
    public bool IsExclusive { get; private set; }

    // Reads T's values (and may look at any entity's).
    public SystemAccess Reads<T>() where T : struct, IComponent
    {
        if (_reads.Has<T>() || _writes.Has<T>()) return this;
        _reads.Add<T>();
        _componentReads.Add(typeof(T));
        if (BuildInfo.IsDevBuild) _readProbes.Add(World.ProbeOf<T>());
        return this;
    }

    // Writes T's values (and so reads them too).
    public SystemAccess Writes<T>() where T : struct, IComponent
    {
        if (_writes.Has<T>()) return this;
        if (_reads.Has<T>())
        {
            _reads.Remove<T>();
            _componentReads.Remove(typeof(T));
            _readProbes.RemoveAll(p => p.Type == typeof(T));
        }
        _writes.Add<T>();
        _componentWrites.Add(typeof(T));
        return this;
    }

    // Reads game events of type T (04 §3.2) through its own reader.
    public SystemAccess ReadsEvents<T>(Schedule schedule = Schedule.Fixed) where T : struct
    {
        World.Events.Queue<T>(schedule);   // made now, on the world's thread, never by a parallel send
        _eventReads.Add(typeof(T));
        return this;
    }

    // Sends game events of type T.
    public SystemAccess Sends<T>(Schedule schedule = Schedule.Fixed) where T : struct
    {
        World.Events.Queue<T>(schedule);
        _eventSends.Add(typeof(T));
        return this;
    }

    // Reads the world resource T (or any shared object the system holds, named by its type).
    public SystemAccess ReadsResource<T>() where T : class
    {
        if (!_resourceWrites.Contains(typeof(T))) _resourceReads.Add(typeof(T));
        return this;
    }

    // Changes the world resource T (DebugDraw, MessageLog, a random number generator...).
    public SystemAccess WritesResource<T>() where T : class
    {
        _resourceReads.Remove(typeof(T));
        _resourceWrites.Add(typeof(T));
        return this;
    }

    // Conflicts with everything: for a system that makes structural changes directly, or touches more
    // than it can name.
    public SystemAccess Exclusive()
    {
        IsExclusive = true;
        return this;
    }

    public IReadOnlyList<Type> ComponentReads => _componentReads;
    public IReadOnlyList<Type> ComponentWrites => _componentWrites;
    public IReadOnlyCollection<Type> EventReads => _eventReads;
    public IReadOnlyCollection<Type> EventSends => _eventSends;
    public IReadOnlyCollection<Type> ResourceReads => _resourceReads;
    public IReadOnlyCollection<Type> ResourceWrites => _resourceWrites;

    // Whether the two may not run at the same time (ordering constraints aside: the scheduler adds those).
    public bool ConflictsWith(SystemAccess other)
    {
        if (IsExclusive || other.IsExclusive) return true;
        if (_writes.HasAny(other._writes) || _writes.HasAny(other._reads) || other._writes.HasAny(_reads)) return true;
        if (_eventSends.Overlaps(other._eventSends) || _eventSends.Overlaps(other._eventReads) ||
            other._eventSends.Overlaps(_eventReads)) return true;
        return _resourceWrites.Overlaps(other._resourceWrites) || _resourceWrites.Overlaps(other._resourceReads) ||
               other._resourceWrites.Overlaps(_resourceReads);
    }

    // ---- What the dev-build check asks -------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Touches<T>() where T : struct, IComponent => _reads.Has<T>() || _writes.Has<T>();

    internal bool ReadsComponent(Type type) => _componentReads.Contains(type);
    internal bool TouchesResource(Type type) => _resourceReads.Contains(type) || _resourceWrites.Contains(type);
    internal bool ReadsEvent(Type type) => _eventReads.Contains(type) || _eventSends.Contains(type);
    internal bool SendsEvent(Type type) => _eventSends.Contains(type);

    // The components declared read-only whose values can be compared before and after (dev builds).
    internal IReadOnlyList<ComponentProbe> ReadProbes => _readProbes;

    public override string ToString()
    {
        if (IsExclusive) return "exclusive";
        var parts = new List<string>();
        void Part(string what, IEnumerable<Type> types)
        {
            var names = types.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (names.Count > 0) parts.Add($"{what} {string.Join(", ", names)}");
        }
        Part("reads", _componentReads);
        Part("writes", _componentWrites);
        Part("reads events", _eventReads);
        Part("sends", _eventSends);
        Part("reads resources", _resourceReads);
        Part("writes resources", _resourceWrites);
        return parts.Count == 0 ? "nothing" : string.Join("; ", parts);
    }
}

// A checksum of every value of one component type in a world (dev builds, issue #288): taken before and
// after systems that only declared reading it ran, so a write the declaration did not admit is reported
// instead of becoming a race. Types holding references (strings, arrays) are not compared: their bytes
// move with the garbage collector.
internal abstract class ComponentProbe
{
    public abstract Type Type { get; }
    public abstract ulong Checksum();
    internal ulong Before;
}

internal sealed class ComponentProbe<T> : ComponentProbe where T : struct, IComponent
{
    private readonly Query<T> _query;
    private static readonly bool Comparable = !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    public ComponentProbe(World world) => _query = world.Query<T>();

    public override Type Type => typeof(T);

    public override ulong Checksum()
    {
        if (!Comparable) return 0;
        ulong hash = 14695981039346656037UL;
        foreach (var (chunk, _) in _query.Chunks)
        {
            var bytes = MemoryMarshal.AsBytes(chunk.Span);
            var words = MemoryMarshal.Cast<byte, ulong>(bytes);
            for (int i = 0; i < words.Length; i++) hash = (hash ^ words[i]) * 1099511628211UL;
            for (int i = words.Length * sizeof(ulong); i < bytes.Length; i++) hash = (hash ^ bytes[i]) * 1099511628211UL;
            hash = (hash ^ (ulong)bytes.Length) * 1099511628211UL;
        }
        return hash;
    }
}
