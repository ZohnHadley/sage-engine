#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// Queries (docs/design/03 §3.3, issue #25): every entity with the given components, as Sage structs
// over Friflo's archetype queries. A query is a view kept up to date by the world, so create it once
// (a system's constructor) and keep it; creating one allocates, iterating one does not.
//
//     private readonly Query<Transform, Health> _alive;          // field
//     _alive = world.Query<Transform, Health>();                 // constructor
//     foreach (var (transforms, healths, entities) in _alive.Chunks)   // Run: zero allocations
//     {
//         var t = transforms.Span; var h = healths.Span;
//         for (int n = 0; n < t.Length; n++) { ... entities.EntityAt(n) ... }
//     }
//
// `query.Entities` walks entity by entity (tools, tests, rare work); `.Entities.ToEntityList()` takes a
// copy, for a loop that changes the world as it goes. Inside a query loop, structural changes throw:
// record them on `world.Commands` (EntityCommands).
//
// Filters (AllTags, WithoutAllTags, ...) change the query they are called on and return it, so they
// belong where the query is created.

// Every entity, whatever its components (World.QueryAll): tools, the editor, console commands.
public readonly struct Query
{
    private readonly F.ArchetypeQuery _raw;
    internal Query(F.ArchetypeQuery raw) => _raw = raw;

    public int Count => _raw.Count;
    public QueryEntities Entities => new(_raw.Entities);

    public Query AllTags(in Tags tags) { _raw.AllTags(tags.Raw); return this; }
    public Query AnyTags(in Tags tags) { _raw.AnyTags(tags.Raw); return this; }
    public Query WithoutAllTags(in Tags tags) { _raw.WithoutAllTags(tags.Raw); return this; }
    public Query WithoutAnyTags(in Tags tags) { _raw.WithoutAnyTags(tags.Raw); return this; }
    public Query WithoutComponent<TC>() where TC : struct, IComponent
    {
        _raw.WithoutAllComponents(F.ComponentTypes.Get<TC>());
        return this;
    }
}

// Entities with a T1.
public readonly struct Query<T1>
    where T1 : struct, IComponent
{
    private readonly F.ArchetypeQuery<T1> _raw;
    internal Query(F.ArchetypeQuery<T1> raw) => _raw = raw;

    // How many entities match now.
    public int Count => _raw.Count;

    // The matching entities chunk by chunk: a span per component and the entities, for hot loops.
    public QueryChunks<T1> Chunks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Chunks);
    }

    // The matching entities one at a time.
    public QueryEntities Entities => new(_raw.Entities);

    public Query<T1> AllTags(in Tags tags) { _raw.AllTags(tags.Raw); return this; }
    public Query<T1> AnyTags(in Tags tags) { _raw.AnyTags(tags.Raw); return this; }
    public Query<T1> WithoutAllTags(in Tags tags) { _raw.WithoutAllTags(tags.Raw); return this; }
    public Query<T1> WithoutAnyTags(in Tags tags) { _raw.WithoutAnyTags(tags.Raw); return this; }

    // Leaves out entities that have a TC as well.
    public Query<T1> WithoutComponent<TC>() where TC : struct, IComponent
    {
        _raw.WithoutAllComponents(F.ComponentTypes.Get<TC>());
        return this;
    }
}

public readonly struct QueryChunks<T1>
    where T1 : struct, IComponent
{
    private readonly F.QueryChunks<T1> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal QueryChunks(F.QueryChunks<T1> raw) => _raw = raw;

    // How many entities the chunks hold.
    public int Count => _raw.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator<T1> GetEnumerator() => new(_raw.GetEnumerator());
}

public struct ChunkEnumerator<T1> : IDisposable
    where T1 : struct, IComponent
{
    private F.ChunkEnumerator<T1> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkEnumerator(F.ChunkEnumerator<T1> raw) => _raw = raw;

    public Chunks<T1> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext() => _raw.MoveNext();

    // Ends the loop: a query being iterated refuses structural changes until then.
    public void Dispose() => _raw.Dispose();
}

// One chunk: a span per component, and its entities. Deconstructs as
// `var (c1, entities) = chunk`.
public readonly struct Chunks<T1>
    where T1 : struct, IComponent
{
    public readonly Chunk<T1> Chunk1;
    public readonly ChunkEntities Entities;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunks(in F.Chunks<T1> raw)
    {
        Chunk1 = new(raw.Chunk1);
        Entities = new(raw.Entities);
    }

    public int Length => Entities.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out Chunk<T1> chunk1, out ChunkEntities entities)
    {
        chunk1 = Chunk1;
        entities = Entities;
    }
}

// Entities with all of T1, T2.
public readonly struct Query<T1, T2>
    where T1 : struct, IComponent where T2 : struct, IComponent
{
    private readonly F.ArchetypeQuery<T1, T2> _raw;
    internal Query(F.ArchetypeQuery<T1, T2> raw) => _raw = raw;

    // How many entities match now.
    public int Count => _raw.Count;

    // The matching entities chunk by chunk: a span per component and the entities, for hot loops.
    public QueryChunks<T1, T2> Chunks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Chunks);
    }

    // The matching entities one at a time.
    public QueryEntities Entities => new(_raw.Entities);

    public Query<T1, T2> AllTags(in Tags tags) { _raw.AllTags(tags.Raw); return this; }
    public Query<T1, T2> AnyTags(in Tags tags) { _raw.AnyTags(tags.Raw); return this; }
    public Query<T1, T2> WithoutAllTags(in Tags tags) { _raw.WithoutAllTags(tags.Raw); return this; }
    public Query<T1, T2> WithoutAnyTags(in Tags tags) { _raw.WithoutAnyTags(tags.Raw); return this; }

    // Leaves out entities that have a TC as well.
    public Query<T1, T2> WithoutComponent<TC>() where TC : struct, IComponent
    {
        _raw.WithoutAllComponents(F.ComponentTypes.Get<TC>());
        return this;
    }
}

public readonly struct QueryChunks<T1, T2>
    where T1 : struct, IComponent where T2 : struct, IComponent
{
    private readonly F.QueryChunks<T1, T2> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal QueryChunks(F.QueryChunks<T1, T2> raw) => _raw = raw;

    // How many entities the chunks hold.
    public int Count => _raw.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator<T1, T2> GetEnumerator() => new(_raw.GetEnumerator());
}

public struct ChunkEnumerator<T1, T2> : IDisposable
    where T1 : struct, IComponent where T2 : struct, IComponent
{
    private F.ChunkEnumerator<T1, T2> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkEnumerator(F.ChunkEnumerator<T1, T2> raw) => _raw = raw;

    public Chunks<T1, T2> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext() => _raw.MoveNext();

    // Ends the loop: a query being iterated refuses structural changes until then.
    public void Dispose() => _raw.Dispose();
}

// One chunk: a span per component, and its entities. Deconstructs as
// `var (c1, c2, entities) = chunk`.
public readonly struct Chunks<T1, T2>
    where T1 : struct, IComponent where T2 : struct, IComponent
{
    public readonly Chunk<T1> Chunk1;
    public readonly Chunk<T2> Chunk2;
    public readonly ChunkEntities Entities;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunks(in F.Chunks<T1, T2> raw)
    {
        Chunk1 = new(raw.Chunk1);
        Chunk2 = new(raw.Chunk2);
        Entities = new(raw.Entities);
    }

    public int Length => Entities.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out Chunk<T1> chunk1, out Chunk<T2> chunk2, out ChunkEntities entities)
    {
        chunk1 = Chunk1;
        chunk2 = Chunk2;
        entities = Entities;
    }
}

// Entities with all of T1, T2, T3.
public readonly struct Query<T1, T2, T3>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
{
    private readonly F.ArchetypeQuery<T1, T2, T3> _raw;
    internal Query(F.ArchetypeQuery<T1, T2, T3> raw) => _raw = raw;

    // How many entities match now.
    public int Count => _raw.Count;

    // The matching entities chunk by chunk: a span per component and the entities, for hot loops.
    public QueryChunks<T1, T2, T3> Chunks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Chunks);
    }

    // The matching entities one at a time.
    public QueryEntities Entities => new(_raw.Entities);

    public Query<T1, T2, T3> AllTags(in Tags tags) { _raw.AllTags(tags.Raw); return this; }
    public Query<T1, T2, T3> AnyTags(in Tags tags) { _raw.AnyTags(tags.Raw); return this; }
    public Query<T1, T2, T3> WithoutAllTags(in Tags tags) { _raw.WithoutAllTags(tags.Raw); return this; }
    public Query<T1, T2, T3> WithoutAnyTags(in Tags tags) { _raw.WithoutAnyTags(tags.Raw); return this; }

    // Leaves out entities that have a TC as well.
    public Query<T1, T2, T3> WithoutComponent<TC>() where TC : struct, IComponent
    {
        _raw.WithoutAllComponents(F.ComponentTypes.Get<TC>());
        return this;
    }
}

public readonly struct QueryChunks<T1, T2, T3>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
{
    private readonly F.QueryChunks<T1, T2, T3> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal QueryChunks(F.QueryChunks<T1, T2, T3> raw) => _raw = raw;

    // How many entities the chunks hold.
    public int Count => _raw.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator<T1, T2, T3> GetEnumerator() => new(_raw.GetEnumerator());
}

public struct ChunkEnumerator<T1, T2, T3> : IDisposable
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
{
    private F.ChunkEnumerator<T1, T2, T3> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkEnumerator(F.ChunkEnumerator<T1, T2, T3> raw) => _raw = raw;

    public Chunks<T1, T2, T3> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext() => _raw.MoveNext();

    // Ends the loop: a query being iterated refuses structural changes until then.
    public void Dispose() => _raw.Dispose();
}

// One chunk: a span per component, and its entities. Deconstructs as
// `var (c1, c2, c3, entities) = chunk`.
public readonly struct Chunks<T1, T2, T3>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
{
    public readonly Chunk<T1> Chunk1;
    public readonly Chunk<T2> Chunk2;
    public readonly Chunk<T3> Chunk3;
    public readonly ChunkEntities Entities;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunks(in F.Chunks<T1, T2, T3> raw)
    {
        Chunk1 = new(raw.Chunk1);
        Chunk2 = new(raw.Chunk2);
        Chunk3 = new(raw.Chunk3);
        Entities = new(raw.Entities);
    }

    public int Length => Entities.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out Chunk<T1> chunk1, out Chunk<T2> chunk2, out Chunk<T3> chunk3, out ChunkEntities entities)
    {
        chunk1 = Chunk1;
        chunk2 = Chunk2;
        chunk3 = Chunk3;
        entities = Entities;
    }
}

// Entities with all of T1, T2, T3, T4.
public readonly struct Query<T1, T2, T3, T4>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent
{
    private readonly F.ArchetypeQuery<T1, T2, T3, T4> _raw;
    internal Query(F.ArchetypeQuery<T1, T2, T3, T4> raw) => _raw = raw;

    // How many entities match now.
    public int Count => _raw.Count;

    // The matching entities chunk by chunk: a span per component and the entities, for hot loops.
    public QueryChunks<T1, T2, T3, T4> Chunks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Chunks);
    }

    // The matching entities one at a time.
    public QueryEntities Entities => new(_raw.Entities);

    public Query<T1, T2, T3, T4> AllTags(in Tags tags) { _raw.AllTags(tags.Raw); return this; }
    public Query<T1, T2, T3, T4> AnyTags(in Tags tags) { _raw.AnyTags(tags.Raw); return this; }
    public Query<T1, T2, T3, T4> WithoutAllTags(in Tags tags) { _raw.WithoutAllTags(tags.Raw); return this; }
    public Query<T1, T2, T3, T4> WithoutAnyTags(in Tags tags) { _raw.WithoutAnyTags(tags.Raw); return this; }

    // Leaves out entities that have a TC as well.
    public Query<T1, T2, T3, T4> WithoutComponent<TC>() where TC : struct, IComponent
    {
        _raw.WithoutAllComponents(F.ComponentTypes.Get<TC>());
        return this;
    }
}

public readonly struct QueryChunks<T1, T2, T3, T4>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent
{
    private readonly F.QueryChunks<T1, T2, T3, T4> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal QueryChunks(F.QueryChunks<T1, T2, T3, T4> raw) => _raw = raw;

    // How many entities the chunks hold.
    public int Count => _raw.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator<T1, T2, T3, T4> GetEnumerator() => new(_raw.GetEnumerator());
}

public struct ChunkEnumerator<T1, T2, T3, T4> : IDisposable
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent
{
    private F.ChunkEnumerator<T1, T2, T3, T4> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkEnumerator(F.ChunkEnumerator<T1, T2, T3, T4> raw) => _raw = raw;

    public Chunks<T1, T2, T3, T4> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext() => _raw.MoveNext();

    // Ends the loop: a query being iterated refuses structural changes until then.
    public void Dispose() => _raw.Dispose();
}

// One chunk: a span per component, and its entities. Deconstructs as
// `var (c1, c2, c3, c4, entities) = chunk`.
public readonly struct Chunks<T1, T2, T3, T4>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent
{
    public readonly Chunk<T1> Chunk1;
    public readonly Chunk<T2> Chunk2;
    public readonly Chunk<T3> Chunk3;
    public readonly Chunk<T4> Chunk4;
    public readonly ChunkEntities Entities;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunks(in F.Chunks<T1, T2, T3, T4> raw)
    {
        Chunk1 = new(raw.Chunk1);
        Chunk2 = new(raw.Chunk2);
        Chunk3 = new(raw.Chunk3);
        Chunk4 = new(raw.Chunk4);
        Entities = new(raw.Entities);
    }

    public int Length => Entities.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out Chunk<T1> chunk1, out Chunk<T2> chunk2, out Chunk<T3> chunk3, out Chunk<T4> chunk4, out ChunkEntities entities)
    {
        chunk1 = Chunk1;
        chunk2 = Chunk2;
        chunk3 = Chunk3;
        chunk4 = Chunk4;
        entities = Entities;
    }
}

// Entities with all of T1, T2, T3, T4, T5.
public readonly struct Query<T1, T2, T3, T4, T5>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent where T5 : struct, IComponent
{
    private readonly F.ArchetypeQuery<T1, T2, T3, T4, T5> _raw;
    internal Query(F.ArchetypeQuery<T1, T2, T3, T4, T5> raw) => _raw = raw;

    // How many entities match now.
    public int Count => _raw.Count;

    // The matching entities chunk by chunk: a span per component and the entities, for hot loops.
    public QueryChunks<T1, T2, T3, T4, T5> Chunks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Chunks);
    }

    // The matching entities one at a time.
    public QueryEntities Entities => new(_raw.Entities);

    public Query<T1, T2, T3, T4, T5> AllTags(in Tags tags) { _raw.AllTags(tags.Raw); return this; }
    public Query<T1, T2, T3, T4, T5> AnyTags(in Tags tags) { _raw.AnyTags(tags.Raw); return this; }
    public Query<T1, T2, T3, T4, T5> WithoutAllTags(in Tags tags) { _raw.WithoutAllTags(tags.Raw); return this; }
    public Query<T1, T2, T3, T4, T5> WithoutAnyTags(in Tags tags) { _raw.WithoutAnyTags(tags.Raw); return this; }

    // Leaves out entities that have a TC as well.
    public Query<T1, T2, T3, T4, T5> WithoutComponent<TC>() where TC : struct, IComponent
    {
        _raw.WithoutAllComponents(F.ComponentTypes.Get<TC>());
        return this;
    }
}

public readonly struct QueryChunks<T1, T2, T3, T4, T5>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent where T5 : struct, IComponent
{
    private readonly F.QueryChunks<T1, T2, T3, T4, T5> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal QueryChunks(F.QueryChunks<T1, T2, T3, T4, T5> raw) => _raw = raw;

    // How many entities the chunks hold.
    public int Count => _raw.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ChunkEnumerator<T1, T2, T3, T4, T5> GetEnumerator() => new(_raw.GetEnumerator());
}

public struct ChunkEnumerator<T1, T2, T3, T4, T5> : IDisposable
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent where T5 : struct, IComponent
{
    private F.ChunkEnumerator<T1, T2, T3, T4, T5> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkEnumerator(F.ChunkEnumerator<T1, T2, T3, T4, T5> raw) => _raw = raw;

    public Chunks<T1, T2, T3, T4, T5> Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_raw.Current);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool MoveNext() => _raw.MoveNext();

    // Ends the loop: a query being iterated refuses structural changes until then.
    public void Dispose() => _raw.Dispose();
}

// One chunk: a span per component, and its entities. Deconstructs as
// `var (c1, c2, c3, c4, c5, entities) = chunk`.
public readonly struct Chunks<T1, T2, T3, T4, T5>
    where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent where T4 : struct, IComponent where T5 : struct, IComponent
{
    public readonly Chunk<T1> Chunk1;
    public readonly Chunk<T2> Chunk2;
    public readonly Chunk<T3> Chunk3;
    public readonly Chunk<T4> Chunk4;
    public readonly Chunk<T5> Chunk5;
    public readonly ChunkEntities Entities;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunks(in F.Chunks<T1, T2, T3, T4, T5> raw)
    {
        Chunk1 = new(raw.Chunk1);
        Chunk2 = new(raw.Chunk2);
        Chunk3 = new(raw.Chunk3);
        Chunk4 = new(raw.Chunk4);
        Chunk5 = new(raw.Chunk5);
        Entities = new(raw.Entities);
    }

    public int Length => Entities.Length;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Deconstruct(out Chunk<T1> chunk1, out Chunk<T2> chunk2, out Chunk<T3> chunk3, out Chunk<T4> chunk4, out Chunk<T5> chunk5, out ChunkEntities entities)
    {
        chunk1 = Chunk1;
        chunk2 = Chunk2;
        chunk3 = Chunk3;
        chunk4 = Chunk4;
        chunk5 = Chunk5;
        entities = Entities;
    }
}

// One component's values in a chunk. Index `Span` in a loop; `this[n]` is the same by reference.
public readonly struct Chunk<T> where T : struct, IComponent
{
    private readonly F.Chunk<T> _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Chunk(in F.Chunk<T> raw) => _raw = raw;

    public Span<T> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _raw.Span;
    }

    public int Length => _raw.Length;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _raw[index];
    }

    public override string ToString() => _raw.ToString();
}

// The entities of a chunk, in the same order as its component spans.
public readonly struct ChunkEntities : IEnumerable<Entity>
{
    private readonly F.ChunkEntities _raw;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ChunkEntities(in F.ChunkEntities raw) => _raw = raw;

    public int Length => _raw.Length;

    // The entity ids, and one id by index.
    public ReadOnlySpan<int> Ids => _raw.Ids;
    public int this[int index] => _raw[index];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity EntityAt(int index) => new(_raw.EntityAt(index));

    public Enumerator GetEnumerator() => new(_raw.GetEnumerator());
    IEnumerator<Entity> IEnumerable<Entity>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<Entity>
    {
        private F.ChunkEntitiesEnumerator _raw;
        internal Enumerator(F.ChunkEntitiesEnumerator raw) => _raw = raw;
        public Entity Current => new(_raw.Current);
        object IEnumerator.Current => Current;
        public bool MoveNext() => _raw.MoveNext();
        public void Reset() => _raw.Reset();
        public void Dispose() => _raw.Dispose();
    }
}

// A query's entities one by one (Query.Entities).
public readonly struct QueryEntities : IEnumerable<Entity>
{
    private readonly F.QueryEntities _raw;
    internal QueryEntities(F.QueryEntities raw) => _raw = raw;

    public int Count => _raw.Count;

    // A copy, safe to iterate while the loop changes the world (destroys, adds components).
    public List<Entity> ToEntityList()
    {
        var list = new List<Entity>(_raw.Count);
        foreach (var entity in _raw) list.Add(new Entity(entity));
        return list;
    }

    public Enumerator GetEnumerator() => new(_raw.GetEnumerator());
    IEnumerator<Entity> IEnumerable<Entity>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<Entity>
    {
        private F.EntitiesEnumerator _raw;
        internal Enumerator(F.EntitiesEnumerator raw) => _raw = raw;
        public Entity Current => new(_raw.Current);
        object IEnumerator.Current => Current;
        public bool MoveNext() => _raw.MoveNext();
        public void Reset() => _raw.Reset();
        public void Dispose() => _raw.Dispose();
    }
}
