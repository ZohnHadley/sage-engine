#nullable enable
using System;
using System.Collections.Generic;
using Friflo.Engine.ECS;

namespace sage_engine;

// One simulation (docs/design/03-world-and-ecs.md): entities, components, queries, resources.
// A thin wrapper over a Friflo.Engine.ECS EntityStore (decision D4, spike in 03 §3.1):
//
// - Entity handles are Friflo's `Entity` struct (the design's "EntityRef"): store + id + revision.
//   A handle to a deleted entity reports IsNull even if the id is reused (requirement E1).
// - Components are structs implementing Friflo's IComponent.
// - Queries are Friflo's ArchetypeQuery types, returned as-is so nothing is lost to wrapping.
//   Create a query once (e.g. in a system's constructor) and keep it; iterate `query.Chunks`
//   in hot paths (zero allocations), `query.Entities` elsewhere.
// - Structural changes inside a query loop throw; use `Commands` + FlushCommands() for those.
//
// Structural notifications (04 §3.3), raised immediately on the world's thread:
//   EntitySpawned  before any ComponentAdded for that entity
//   ComponentRemoved for every component, then EntityDestroyed, when an entity is destroyed
//   (also for deletes played back from a command buffer)
public sealed class World : IDisposable
{
    private readonly EntityStore _store;
    private readonly Dictionary<PersistentId, Entity> _persistent = new();
    private CommandBuffer? _commands;

    public string Name { get; }
    public Engine? Engine { get; }
    public WorldResources Resources { get; } = new();

    public event Action<Entity>? EntitySpawned;
    public event Action<Entity, Type>? ComponentAdded;
    public event Action<Entity, Type>? ComponentRemoved;
    public event Action<Entity>? EntityDestroyed;

    // Engine.CreateWorld is the normal way to make a world; tools and tests may create one directly.
    public World(string name, Engine? engine = null)
    {
        EcsSchema.EnsureInitialized();
        Name = name;
        Engine = engine;
        _store = new EntityStore();
        _store.OnEntityCreate += e => EntitySpawned?.Invoke(e.Entity);
        _store.OnComponentAdded += OnComponentAdded;
        _store.OnComponentRemoved += OnComponentRemoved;
        _store.OnEntityDelete += OnEntityDelete;
    }

    // The underlying store, for engine code (editor listing, serializers). Game code uses the API below.
    internal EntityStore Store => _store;

    public int EntityCount => _store.Count;

    // ---- Entities -------------------------------------------------------------------------------

    // Every entity starts with a Transform (identity) and, when given, a name.
    // Outside query loops only; inside one, use Commands.
    public Entity Create(string? name = null) => Create(Transform.Identity, name);

    public Entity Create(in Transform transform, string? name = null)
    {
        var entity = _store.CreateEntity();
        entity.AddComponent(transform);
        if (name != null)
            entity.AddComponent(new EntityName(name));
        return entity;
    }

    // Outside query loops only; inside one, use Commands.DeleteEntity(entity.Id).
    public void Destroy(Entity entity)
    {
        if (!IsAlive(entity))
        {
            Assert.Ensure(false, $"World.Destroy: entity {entity.Id} is not alive in world '{Name}'");
            return;
        }
        entity.DeleteEntity();
    }

    public bool IsAlive(Entity entity) => !entity.IsNull && entity.Store == _store;

    public static string Describe(Entity entity) =>
        entity.IsNull ? "(null entity)"
        : entity.TryGetComponent<EntityName>(out var n) ? $"{n.value} ({entity.Id})"
        : $"entity {entity.Id}";

    // ---- Components -----------------------------------------------------------------------------

    // Missing component: Ensure fails (logged once per call site); dev builds throw, Shipping returns
    // a zeroed dummy rather than crashing the player's game (03 §8).
    public ref T Get<T>(Entity entity) where T : struct, IComponent
    {
        if (IsAlive(entity) && entity.HasComponent<T>())
            return ref entity.GetComponent<T>();
        Assert.Ensure(false, $"{Describe(entity)} has no {typeof(T).Name}");
        if (BuildInfo.IsDevBuild)
            throw new InvalidOperationException($"{Describe(entity)} has no {typeof(T).Name} (world '{Name}')");
        Dummy<T>.Value = default;
        return ref Dummy<T>.Value;
    }

    public bool TryGet<T>(Entity entity, out T value) where T : struct, IComponent
    {
        if (IsAlive(entity))
            return entity.TryGetComponent(out value);
        value = default;
        return false;
    }

    public bool Has<T>(Entity entity) where T : struct, IComponent => IsAlive(entity) && entity.HasComponent<T>();

    // Adds the component. Adding a type the entity already has is an Ensure failure and is ignored
    // (it never silently replaces data); use Get<T>() to change an existing component.
    public bool Add<T>(Entity entity, in T component) where T : struct, IComponent
    {
        if (!IsAlive(entity))
        {
            Assert.Ensure(false, $"World.Add<{typeof(T).Name}>: entity {entity.Id} is not alive");
            return false;
        }
        if (entity.HasComponent<T>())
        {
            Assert.Ensure(false, $"World.Add: {Describe(entity)} already has {typeof(T).Name}");
            return false;
        }
        entity.AddComponent(component);
        return true;
    }

    // Removes the component. Returns false, with no side effects, if the entity doesn't have it
    // (review item #33: validate before mutating).
    public bool Remove<T>(Entity entity) where T : struct, IComponent
    {
        if (!IsAlive(entity) || !entity.HasComponent<T>())
            return false;
        entity.RemoveComponent<T>();
        return true;
    }

    // ---- Hierarchy ------------------------------------------------------------------------------

    public void SetParent(Entity child, Entity parent)
    {
        if (!IsAlive(child) || !IsAlive(parent))
        {
            Assert.Ensure(false, "World.SetParent: both entities must be alive in this world");
            return;
        }
        parent.AddChild(child);
    }

    public void ClearParent(Entity child)
    {
        if (IsAlive(child) && !child.Parent.IsNull)
            child.Parent.RemoveChild(child);
    }

    // ---- Queries --------------------------------------------------------------------------------
    // Cache the returned query; creating one allocates.

    public ArchetypeQuery<T1> Query<T1>()
        where T1 : struct, IComponent => _store.Query<T1>();

    public ArchetypeQuery<T1, T2> Query<T1, T2>()
        where T1 : struct, IComponent where T2 : struct, IComponent => _store.Query<T1, T2>();

    public ArchetypeQuery<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent => _store.Query<T1, T2, T3>();

    // Every entity (editor, tools, debug commands).
    public ArchetypeQuery QueryAll() => _store.Query();

    // ---- Deferred structural changes -------------------------------------------------------------

    // Record structural changes here while iterating a query; they apply at FlushCommands().
    // (Flush points move to the end of every schedule phase in migration step 4.)
    public CommandBuffer Commands => _commands ??= CreateCommandBuffer();

    public void FlushCommands()
    {
        if (_commands != null && (_commands.EntityCommandsCount > 0 || _commands.ComponentCommandsCount > 0 ||
                                  _commands.TagCommandsCount > 0 || _commands.ChildCommandsCount > 0))
            _commands.Playback();
    }

    private CommandBuffer CreateCommandBuffer()
    {
        var cb = _store.GetCommandBuffer();
        cb.ReuseBuffer = true;
        return cb;
    }

    // ---- Persistent ids ---------------------------------------------------------------------------

    public Entity Resolve(PersistentId id) => _persistent.TryGetValue(id, out var e) && IsAlive(e) ? e : default;

    // Adds a Persistent component with a new id (runtime-spawned entities that should be saved).
    public PersistentId MakePersistent(Entity entity)
    {
        if (TryGet(entity, out Persistent existing))
            return existing.Id;
        var id = PersistentId.New();
        Add(entity, new Persistent { Id = id });
        return id;
    }

    // ---- Notifications ----------------------------------------------------------------------------

    private void OnComponentAdded(ComponentChanged change)
    {
        if (change.Type == typeof(Persistent))
            IndexPersistent(change.Entity);
        ComponentAdded?.Invoke(change.Entity, change.Type);
    }

    private void OnComponentRemoved(ComponentChanged change)
    {
        if (change.Type == typeof(Persistent))
            _persistent.Remove(change.OldComponent<Persistent>().Id);
        ComponentRemoved?.Invoke(change.Entity, change.Type);
    }

    // Friflo raises only OnEntityDelete when an entity is deleted, while it is still alive with all
    // its components. Report each component's removal first, then the destruction (spike, 03 §3.1).
    private void OnEntityDelete(EntityDelete delete)
    {
        var entity = delete.Entity;
        if (entity.TryGetComponent<Persistent>(out var p))
            _persistent.Remove(p.Id);
        if (ComponentRemoved != null)
        {
            foreach (var component in entity.Components)
                ComponentRemoved.Invoke(entity, component.Type.Type);
        }
        EntityDestroyed?.Invoke(entity);
    }

    private void IndexPersistent(Entity entity)
    {
        var id = entity.GetComponent<Persistent>().Id;
        if (_persistent.TryGetValue(id, out var other) && IsAlive(other) && other != entity)
            Log.Error(LogCat.World, $"Duplicate PersistentId {id} on {Describe(entity)} and {Describe(other)}");
        _persistent[id] = entity;
    }

    public void Dispose()
    {
        Resources.DisposeAll();
        Log.Debug(LogCat.World, $"World '{Name}' destroyed");
    }

    private static class Dummy<T> where T : struct { public static T Value; }
}
