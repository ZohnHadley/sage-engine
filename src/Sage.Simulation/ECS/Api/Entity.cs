#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// The ECS vocabulary is Sage's (REDESIGN §3.5, issue #25): games and the base engine name `Entity`,
// `IComponent`, `ITag`, `Tags`, `Query<…>` and `EntityCommands`, never a `Friflo.*` type. Friflo is the
// storage underneath (decision D4), and these are thin structs over it: one field each, every member a
// forwarding call the JIT inlines, so they cost what Friflo costs (docs/history/scale-2026-09-24.md).
//
// **Why IComponent and ITag extend Friflo's.** Friflo finds component types by scanning the loaded
// assemblies for structs implementing *its* IComponent/ITag, and every generic call into it
// (`GetComponent<T>`, `Query<T>`) is constrained on them. A Sage marker that did not extend Friflo's
// could not be passed to either without a boxing, reflection-per-call bridge, which is the opposite of
// zero cost. So Sage's markers inherit Friflo's: the schema scan finds a game's components as before
// (an assembly implementing Sage's IComponent also references Friflo's, which is what the scan checks),
// and the generic calls forward unchanged. The price is that Friflo's assembly is a compile-time
// reference of every game (a derived interface needs its base), so "Friflo is private" means: no
// `Friflo.*` type in any public signature of the base, and no `using Friflo` for games
// (games/Directory.Build.props).

// A component: a struct of data on an entity. Declare it with a stable id:
//     [Component("mygame:health")] public struct Health : IComponent { public float Value; }
public interface IComponent : F.IComponent { }

// A tag: a component with no data, for filtering queries.
//     [Tag("mygame:boss")] public struct Boss : ITag { }
public interface ITag : F.ITag { }

// A handle to an entity: its world, its id and a revision, so a handle to a destroyed entity reports
// IsNull even when the id is reused (03 §3.1, requirement E1). `default` is the null entity.
//
// Structural changes (adding or removing a component or tag, destroying) are not allowed while a query
// over the entity's world is being iterated: record them on `world.Commands` (EntityCommands) instead.
public readonly struct Entity : IEquatable<Entity>
{
    internal readonly F.Entity Raw;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity(F.Entity raw) => Raw = raw;

    // Unique within its world while the entity lives.
    public int Id => Raw.Id;

    // True for `default` and for a handle whose entity has been destroyed.
    public bool IsNull => Raw.IsNull;

    // ---- Components -------------------------------------------------------------------------------

    // A reference to the component; throws when the entity has none (World.Get<T> says who).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetComponent<T>() where T : struct, IComponent => ref Raw.GetComponent<T>();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryGetComponent<T>(out T value) where T : struct, IComponent => Raw.TryGetComponent(out value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool HasComponent<T>() where T : struct, IComponent => Raw.HasComponent<T>();

    // Adds the component, or replaces its value when the entity already has one. Returns true when it
    // was added. (World.Add refuses to replace, on purpose; this is the plain operation.)
    public bool AddComponent<T>(in T component) where T : struct, IComponent => Raw.AddComponent(component);

    public bool AddComponent<T>() where T : struct, IComponent => Raw.AddComponent<T>();

    public bool RemoveComponent<T>() where T : struct, IComponent => Raw.RemoveComponent<T>();

    // Every component on the entity, boxed: for inspectors and dumps, not for gameplay.
    public EntityComponents Components => new(Raw.Components);

    // ---- Tags -------------------------------------------------------------------------------------

    public Tags Tags => new(Raw.Tags);

    public bool AddTag<T>() where T : struct, ITag => Raw.AddTag<T>();
    public bool RemoveTag<T>() where T : struct, ITag => Raw.RemoveTag<T>();
    public bool AddTags(in Tags tags) => Raw.AddTags(tags.Raw);
    public bool RemoveTags(in Tags tags) => Raw.RemoveTags(tags.Raw);

    // ---- Name -------------------------------------------------------------------------------------

    // The entity's name (prefab placements, `ent_list`, entity I/O targets), or null. Written on the
    // entity itself in a save, not as a component (09 §3.4). Setting null or "" removes it.
    public string? Name
    {
        get => Raw.TryGetComponent<F.EntityName>(out var name) ? name.value : null;
        set
        {
            if (string.IsNullOrEmpty(value)) Raw.RemoveComponent<F.EntityName>();
            else Raw.AddComponent(new F.EntityName(value));
        }
    }

    // ---- Hierarchy --------------------------------------------------------------------------------

    // The parent, or the null entity for a root.
    public Entity Parent => new(Raw.Parent);
    public int ChildCount => Raw.ChildCount;
    public EntityChildren ChildEntities => new(Raw.ChildEntities);

    // Prefer World.SetParent / ClearParent, which check both entities belong to the world.
    public void AddChild(Entity child) => Raw.AddChild(child.Raw);
    public bool RemoveChild(Entity child) => Raw.RemoveChild(child.Raw);

    // Destroys the entity now. Prefer World.Destroy (which checks it is alive), or
    // world.Commands.Destroy inside a query loop.
    public void DeleteEntity() => Raw.DeleteEntity();

    // ---- Identity ---------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(Entity other) => Raw == other.Raw;
    public override bool Equals(object? obj) => obj is Entity other && Equals(other);
    public override int GetHashCode() => Raw.GetHashCode();
    public static bool operator ==(Entity a, Entity b) => a.Raw == b.Raw;
    public static bool operator !=(Entity a, Entity b) => a.Raw != b.Raw;
    public override string ToString() => Raw.ToString();
}

// The components on one entity (Entity.Components): the count, and each as its type and boxed value.
public readonly struct EntityComponents : IEnumerable<EntityComponent>
{
    private readonly F.EntityComponents _raw;
    internal EntityComponents(F.EntityComponents raw) => _raw = raw;

    public int Count => _raw.Count;

    public Enumerator GetEnumerator() => new(_raw.GetEnumerator());
    IEnumerator<EntityComponent> IEnumerable<EntityComponent>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<EntityComponent>
    {
        private F.ComponentEnumerator _raw;
        internal Enumerator(F.ComponentEnumerator raw) => _raw = raw;
        public EntityComponent Current => new(_raw.Current);
        object IEnumerator.Current => Current;
        public bool MoveNext() => _raw.MoveNext();
        public void Reset() => _raw.Reset();
        public void Dispose() => _raw.Dispose();
    }
}

// One component of an entity, for tools: its type and its value, boxed.
public readonly struct EntityComponent
{
    private readonly F.EntityComponent _raw;
    internal EntityComponent(F.EntityComponent raw) => _raw = raw;

    public Type Type => _raw.Type.Type;

#pragma warning disable CS0618   // Friflo marks the boxed Value obsolete in favour of GetComponent<T>(); a
                                 // tool only knows the type at run time, so it needs the box.
    public object? Value => _raw.Value;
#pragma warning restore CS0618

    public override string ToString() => _raw.ToString();
}

// An entity's children (Entity.ChildEntities), in order.
public readonly struct EntityChildren : IEnumerable<Entity>
{
    private readonly F.ChildEntities _raw;
    internal EntityChildren(F.ChildEntities raw) => _raw = raw;

    public int Count => _raw.Count;
    public Entity this[int index] => new(_raw[index]);

    public Enumerator GetEnumerator() => new(_raw.GetEnumerator());
    IEnumerator<Entity> IEnumerable<Entity>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public struct Enumerator : IEnumerator<Entity>
    {
        private F.ChildEnumerator _raw;
        internal Enumerator(F.ChildEnumerator raw) => _raw = raw;
        public Entity Current => new(_raw.Current);
        object IEnumerator.Current => Current;
        public bool MoveNext() => _raw.MoveNext();
        public void Reset() => _raw.Reset();
        public void Dispose() => _raw.Dispose();
    }
}

// A set of tag types: what an entity has (Entity.Tags) and what a query filters on (Query.AllTags).
//     world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>())
public readonly struct Tags : IEquatable<Tags>, IEnumerable<Type>
{
    internal readonly F.Tags Raw;
    internal Tags(in F.Tags raw) => Raw = raw;

    public static Tags Get<T>() where T : struct, ITag => new(F.Tags.Get<T>());
    public static Tags Get<T1, T2>() where T1 : struct, ITag where T2 : struct, ITag => new(F.Tags.Get<T1, T2>());
    public static Tags Get<T1, T2, T3>() where T1 : struct, ITag where T2 : struct, ITag where T3 : struct, ITag
        => new(F.Tags.Get<T1, T2, T3>());

    public int Count => Raw.Count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>() where T : struct, ITag => Raw.Has<T>();
    public bool Has<T1, T2>() where T1 : struct, ITag where T2 : struct, ITag => Raw.Has<T1, T2>();
    public bool HasAll(in Tags tags) => Raw.HasAll(tags.Raw);
    public bool HasAny(in Tags tags) => Raw.HasAny(tags.Raw);

    // The tag types in the set.
    public IEnumerator<Type> GetEnumerator()
    {
        foreach (var tag in Raw) yield return tag.Type;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(Tags other) => Raw.Equals(other.Raw);
    public override bool Equals(object? obj) => obj is Tags other && Equals(other);
    public override int GetHashCode() => Raw.GetHashCode();
    public static bool operator ==(Tags a, Tags b) => a.Equals(b);
    public static bool operator !=(Tags a, Tags b) => !a.Equals(b);
    public override string ToString() => Raw.ToString();
}

// Between the two vocabularies, for the engine's own implementation files.
internal static class EcsInterop
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Entity AsSage(this F.Entity entity) => new(entity);
}
