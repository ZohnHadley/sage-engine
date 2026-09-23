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
// - Structural changes inside a query loop throw; use `Commands` for those. They're applied at the
//   end of every phase (and by FlushCommands()).
// - Systems run in schedules and phases (03 §3.5): the host calls RunFixed once per simulation tick
//   and RunFrame once per rendered frame (01 §5.2).
//
// Structural notifications (04 §3.3), raised immediately on the world's thread:
//   EntitySpawned  before any ComponentAdded for that entity
//   ComponentRemoved for every component, then EntityDestroyed, when an entity is destroyed
//   (also for deletes played back from a command buffer)
public sealed class World : IDisposable
{
    private readonly EntityStore _store;
    private readonly Dictionary<PersistentId, Entity> _persistent = new();
    private readonly SystemScheduler _scheduler = new();
    private readonly TransformPropagation _propagation;
    private CommandBuffer? _commands;
    private readonly GameEvents _events;
    private readonly PhaseContracts _contracts;
    private readonly DebugDraw _debugDraw;
    private readonly MessageLog _messages;
    private TickTime _lastTick;
    private long _frame;

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
        _propagation = new TransformPropagation(this);
        // Resources every world has (headless ones too, so simulation code can rely on them).
        Resources.Set(new ActiveCamera());
        Resources.Set(new RenderEnvironment());
        Resources.Set(new Terrain());
        Resources.Set(new PlayerInput());
        _contracts = new PhaseContracts(this);   // what each phase promises, checked in dev (03 §3.5)
        _events = new GameEvents();          // the one place gameplay facts cross systems (04 §3.2)
        if (engine != null) _events.UseCVars(engine.Core.EventMaxAge, engine.Core.EventTrace);
        Resources.Set(_events);
        _debugDraw = new DebugDraw();        // always there, so `world.Debug()` needs no null check (06 §3.2)
        Resources.Set(_debugDraw);
        _messages = new MessageLog(_events); // and `world.Say(...)` works with or without a HUD (13 §3)
        Resources.Set(_messages);
    }

    // The underlying store, for engine code (editor listing, serializers). Game code uses the API below.
    internal EntityStore Store => _store;

    public int EntityCount => _store.Count;

    // Fixed schedule systems with RunCondition WhenNotPaused (the default) are skipped while paused;
    // Frame systems (camera, UI, rendering) keep running (01 §5.2).
    public bool Paused { get; set; }

    public long Tick => _lastTick.Tick;
    public double SimTime => _lastTick.SimTime;

    // ---- Entities -------------------------------------------------------------------------------

    // Every entity starts with a Transform (identity unless given), a GlobalTransform at the same pose
    // (so it doesn't interpolate in from the origin) and, when given, a name.
    // Outside query loops only; inside one, use Commands.
    public Entity Create(string? name = null) => Create(Transform.Identity, name);

    public Entity Create(in Transform transform, string? name = null)
    {
        var entity = _store.CreateEntity();
        entity.AddComponent(transform);
        entity.AddComponent(GlobalTransform.At(Pose.FromLocal(transform)));
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

    // Moves a root entity without interpolating the jump (spawn points, doors, fast travel): sets
    // Transform and both poses of GlobalTransform. Children follow at the next propagation.
    public void Teleport(Entity entity, in Transform transform)
    {
        if (!Has<Transform>(entity)) { Assert.Ensure(false, $"Teleport: {Describe(entity)} has no Transform"); return; }
        Get<Transform>(entity) = transform;
        if (Has<GlobalTransform>(entity) && entity.Parent.IsNull)
            Get<GlobalTransform>(entity) = GlobalTransform.At(Pose.FromLocal(transform));
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

    public ArchetypeQuery<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : struct, IComponent where T2 : struct, IComponent
        where T3 : struct, IComponent where T4 : struct, IComponent => _store.Query<T1, T2, T3, T4>();

    // Every entity (editor, tools, debug commands).
    public ArchetypeQuery QueryAll() => _store.Query();

    // ---- Systems and schedules ---------------------------------------------------------------------

    // Registers a system in a phase. `before`/`after` name system types in the same phase; otherwise
    // systems run in registration order. A cycle throws.
    public SystemInfo AddSystem(ISystem system, Phase phase, RunCondition condition = RunCondition.Default,
        Type[]? before = null, Type[]? after = null)
    {
        var info = _scheduler.Add(system, phase, condition, before ?? Type.EmptyTypes, after ?? Type.EmptyTypes);
        Log.Debug(LogCat.World, $"System {info.Name} added to {phase} in '{Name}'");
        return info;
    }

    public bool RemoveSystem(ISystem system) => _scheduler.Remove(system);

    public IEnumerable<SystemInfo> Systems => _scheduler.All;

    // Gameplay facts between systems (04 §3.2). Get a reader once, in a constructor, and keep it.
    public GameEvents Events => _events;

    // What a phase guarantees, checked in dev builds rather than described in a comment (R16).
    public PhaseContracts Contracts => _contracts;

    // One simulation tick: copy poses for interpolation, then every Fixed phase in order, applying
    // buffered structural changes after each and propagating transforms after PostPhysics and Late.
    public void RunFixed(float dt)
    {
        _lastTick = new TickTime(_lastTick.Tick + 1, dt, _lastTick.SimTime + dt);
        Log.SetTick(_lastTick.Tick);
        _propagation.BeginTick();
        _events.NowTick = _lastTick.Tick;
        _debugDraw.BeginTick();                   // momentary debug shapes are this tick's (06 §3.2)

        var frame = new FrameTime(_frame, 0, 1, 0);
        for (var phase = Phase.Commands; phase < PhaseInfo.FirstFrame; phase++)
        {
            RunPhase(phase, _lastTick, frame);
            if (_contracts.Count > 0) _contracts.AfterPhase(phase);
            if (phase == Phase.PostPhysics || phase == Phase.Late)
            {
                using var _ = Profiler.Begin("Fixed.TransformPropagation");
                _propagation.Propagate();
            }
        }

        _messages.Take();   // the log keeps up with the ticks; frames only age it (13 §3)

        // Once every Fixed system has had its turn: drop what every reader has passed (04 §3.2).
        // Never mid-phase — readers hold sequence numbers, and moving the queue under one would skip
        // events for a system that hasn't run yet this tick.
        _events.EndOfSchedule(Schedule.Fixed, _lastTick.Tick);
    }

    // One rendered frame: FrameUpdate, Extract, Render, Overlay. `alpha` interpolates between the
    // previous and the current tick (GlobalTransform.Interpolated).
    public void RunFrame(float dt, float alpha, double realTime = 0)
    {
        var frame = new FrameTime(++_frame, dt, alpha, realTime);
        _messages.Advance(dt);               // messages age in display time, not ticks (13 §3)
        for (var phase = PhaseInfo.FirstFrame; phase <= Phase.Overlay; phase++)
            RunPhase(phase, _lastTick, frame);

        _events.EndOfSchedule(Schedule.Frame, _lastTick.Tick);
    }

    private void RunPhase(Phase phase, in TickTime tick, in FrameTime frame)
    {
        var systems = _scheduler.In(phase);
        if (systems.Count == 0 && !HasPendingCommands) return;

        using (Profiler.Begin(_scheduler.PhaseProfileName(phase)))
        {
            var ctx = new SystemContext(this, phase, tick, frame);
            for (int i = 0; i < systems.Count; i++)
            {
                var s = systems[i];
                if (!s.Enabled || !ShouldRun(s)) continue;
                using var _ = Profiler.Begin(s.ProfileName);
                s.System.Run(ctx);
            }
            FlushCommands();
        }
    }

    private bool ShouldRun(SystemInfo s) => s.Condition switch
    {
        RunCondition.Always => true,
        RunCondition.WhenNotPaused => !Paused,
        RunCondition.DevOnly => BuildInfo.IsDevBuild,
        _ => PhaseInfo.ScheduleOf(s.Phase) == Schedule.Frame || !Paused,
    };

    // ---- Deferred structural changes -------------------------------------------------------------

    // Record structural changes here while iterating a query; they're applied at the end of the
    // current phase (or by FlushCommands()).
    public CommandBuffer Commands => _commands ??= CreateCommandBuffer();

    private bool HasPendingCommands => _commands != null &&
        (_commands.EntityCommandsCount > 0 || _commands.ComponentCommandsCount > 0 ||
         _commands.TagCommandsCount > 0 || _commands.ChildCommandsCount > 0);

    public void FlushCommands()
    {
        if (HasPendingCommands)
            _commands!.Playback();
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
