#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// One simulation (docs/design/03-world-and-ecs.md): entities, components, queries, resources.
// A thin wrapper over a Friflo.Engine.ECS EntityStore (decision D4, spike in 03 §3.1), in Sage's own
// vocabulary (ECS/Api, issue #25): no Friflo type appears in its public surface.
//
// - Entity handles are Sage's `Entity` (the design's "EntityRef"): world + id + revision.
//   A handle to a deleted entity reports IsNull even if the id is reused (requirement E1).
// - Components are structs implementing Sage's IComponent; tags implement ITag.
// - Queries are `Query<T1..T5>`, zero-cost structs over Friflo's archetype queries.
//   Create a query once (e.g. in a system's constructor) and keep it; iterate `query.Chunks`
//   in hot paths (zero allocations), `query.Entities` elsewhere.
// - Structural changes inside a query loop throw; record them on `Commands` (EntityCommands).
//   They're applied at the end of every phase (and by FlushCommands()).
// - Systems run in schedules and phases (03 §3.5): the host calls RunFixed once per simulation tick
//   and RunFrame once per rendered frame (01 §5.2).
//
// Structural notifications (04 §3.3), raised immediately on the world's thread, for the world's own
// bookkeeping; gameplay reads the queued `Added<T>`/`Removed<T>` instead (StructuralEvents.cs, #282):
//   EntitySpawned  before any ComponentAdded for that entity
//   ComponentRemoved for every component, then EntityDestroyed, when an entity is destroyed
//   (also for deletes played back from a command buffer)
public sealed class World : IDisposable
{
    private readonly F.EntityStore _store;
    private readonly Dictionary<PersistentId, Entity> _persistent = new();
    private readonly SystemScheduler _scheduler;
    private readonly TransformPropagation _propagation;
    private EntityCommands? _commands;
    private readonly GameEvents _events;
    private readonly StructuralEvents _structural;
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
        _store = new F.EntityStore();
        _store.OnEntityCreate += e =>
        {
            AccessCheck.Structural();
            EntitySpawned?.Invoke(e.Entity.AsSage());
        };
        _store.OnComponentAdded += OnComponentAdded;
        _store.OnComponentRemoved += OnComponentRemoved;
        _store.OnEntityDelete += OnEntityDelete;
        _propagation = new TransformPropagation(this);
        // The system ids every loaded assembly declares: the engine's, which knows every plugin's, or
        // for a bare world one of its own that learns each assembly as its systems arrive (issue #17).
        var catalog = engine?.SystemCatalog ?? new SystemCatalog();
        catalog.Include(typeof(World).Assembly);
        _scheduler = new SystemScheduler(catalog);
        Systems = new WorldSystems(this, _scheduler);
        // Resources every world has, headless ones too. What only some games have is installed by the
        // plugin it belongs to (issue #13): Terrain by sage.streaming; ActiveCamera and PlayerInput by
        // sage.gameplay.character (a player to look through and to command) and the client (a view to
        // draw). A world from `"plugins": []` has none of them.
        Resources.Add(new RenderEnvironment());
        // What the sky is doing belongs to the world, not to whoever draws it (06 §3.13): a headless
        // server can be rained on, and a save carries the storm you walked into.
        Resources.Add(new Weather { Current = WeatherRecord.Clear, Target = WeatherRecord.Clear });
        // Which sector the simulation is running in (R6, 14 §3). Core rather than streaming's: it is the
        // frame every absolute position converts through (placements, saves, levels), and without
        // streaming it simply never moves from sector zero.
        Resources.Add(new Origin());
        _contracts = new PhaseContracts(this);   // what each phase promises, checked in dev (03 §3.5)
        _events = new GameEvents();          // the one place gameplay facts cross systems (04 §3.2)
        _structural = new StructuralEvents(_events);   // and Added<T>/Removed<T> on it, for whoever reads them (#282)
        _events.Structural = _structural;
        if (engine != null)
        {
            _events.UseCVars(engine.Core.EventMaxAge, engine.Core.EventTrace);
            Resources.Add(engine.Records);   // effects, attacks and items all look records up per world
        }
        Resources.Add(_events);
        _debugDraw = new DebugDraw();        // always there, so `world.Debug()` needs no null check (06 §3.2)
        Resources.Add(_debugDraw);
        _messages = new MessageLog(_events); // and `world.Say(...)` works with or without a HUD (13 §3)
        Resources.Add(_messages);
    }

    // The underlying store, for engine code (editor listing, serializers). Game code uses the API below.
    internal F.EntityStore Store => _store;

    public int EntityCount => _store.Count;

    // Fixed schedule systems with RunCondition WhenNotPaused (the default) are skipped while paused;
    // Frame systems (camera, UI, rendering) keep running (01 §5.2). The world's time's flag (`WorldTime`,
    // issue #283), so it is saved, and slowed or hit-stopped time skips them the same way.
    public bool Paused
    {
        get => WorldTime.Of(this).Paused;
        set => WorldTime.Of(this).Paused = value;
    }

    // An **edit world** (phase 10a, issue #219, 15 §3): built from a document for the editor to show, not
    // to play. No Fixed-schedule system runs in it, whatever its run condition — nothing walks, falls,
    // thinks or counts the hours — while its ticks still propagate transforms (an edited placement moves)
    // and every Frame system (cameras, extraction, rendering, the UI) runs as in any world. The tick
    // boundary's travel, passing time and saves are a play world's, so they wait too. Set when the world
    // is made (`Engine.CreateEditWorld`) and never changed: play-in-editor makes a second world to play.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor (phase 10a)
    public bool Editing { get; internal set; }

    public long Tick => _lastTick.Tick;
    public double SimTime => _lastTick.SimTime;
    internal TickTime LastTick => _lastTick;

    // ---- Entities -------------------------------------------------------------------------------

    // Every entity starts with a Transform (identity unless given), a GlobalTransform at the same pose
    // (so it doesn't interpolate in from the origin) and, when given, a name.
    // Outside query loops only; inside one, use Commands.
    public Entity Create(string? name = null) => Create(Transform.Identity, name);

    public Entity Create(in Transform transform, string? name = null)
    {
        var entity = _store.CreateEntity().AsSage();
        entity.AddComponent(transform);
        entity.AddComponent(GlobalTransform.At(Pose.FromLocal(transform)));
        if (name != null)
            entity.Name = name;
        return entity;
    }

    // Outside query loops only; inside one, use Commands.Destroy(entity).
    public void Destroy(Entity entity)
    {
        if (!IsAlive(entity))
        {
            Assert.Ensure(false, $"World.Destroy: entity {entity.Id} is not alive in world '{Name}'");
            return;
        }
        entity.DeleteEntity();
        DestroyOrphans();
    }

    // Children a prefab placed (FromParentPrefab) go with their parent, however it was destroyed —
    // directly or from a command buffer. Friflo leaves a deleted entity's children alive as roots, so
    // OnEntityDelete notes them and they are destroyed once the parent's delete is done (phase 4i).
    // Children a game parented by hand are not tagged and outlive the parent, as they always have.
    private List<Entity>? _orphans;

    private void NoteOrphans(Entity parent)
    {
        if (parent.ChildCount == 0) return;
        foreach (var child in parent.ChildEntities)
            if (child.Tags.Has<FromParentPrefab>()) (_orphans ??= new List<Entity>()).Add(child);
    }

    private void DestroyOrphans()
    {
        if (_orphans is not { Count: > 0 } orphans) return;
        _orphans = null;
        foreach (var child in orphans)
            if (IsAlive(child)) Destroy(child);
    }

    public bool IsAlive(Entity entity) => !entity.IsNull && entity.Raw.Store == _store;

    public static string Describe(Entity entity) =>
        entity.IsNull ? "(null entity)"
        : entity.Name is { } n ? $"{n} ({entity.Id})"
        : $"entity {entity.Id}";

    // ---- Components -----------------------------------------------------------------------------

    // The component, by reference, to read or write. A missing one throws in every build: Shipping used
    // to hand back a shared zeroed dummy, so a write went nowhere and the next caller read the last
    // one's leftovers (REDESIGN §3.5, issue #31). Code that can meet an entity without it asks first:
    // TryGet (a copy) or Has.
    public ref T Get<T>(Entity entity) where T : struct, IComponent
    {
        if (IsAlive(entity) && entity.HasComponent<T>())
            return ref entity.GetComponent<T>();
        Assert.Ensure(false, $"{Describe(entity)} has no {typeof(T).Name}");
        throw new InvalidOperationException($"{Describe(entity)} has no {typeof(T).Name} (world '{Name}'); " +
                                            $"use TryGet or Has where it may be missing");
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
        if (typeof(T) == typeof(Persistent) && entity.HasComponent<T>())
        {
            // A runtime spawn already has a fresh id (phase 4i); a scene, a map or a load gives it its own.
            ref var held = ref entity.GetComponent<Persistent>();
            if (_persistent.TryGetValue(held.Id, out var indexed) && indexed == entity) _persistent.Remove(held.Id);
            held = System.Runtime.CompilerServices.Unsafe.As<T, Persistent>(ref System.Runtime.CompilerServices.Unsafe.AsRef(in component));
            IndexPersistent(entity);
            return true;
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

    public Query<T1> Query<T1>()
        where T1 : struct, IComponent => new(_store.Query<T1>());

    public Query<T1, T2> Query<T1, T2>()
        where T1 : struct, IComponent where T2 : struct, IComponent => new(_store.Query<T1, T2>());

    public Query<T1, T2, T3> Query<T1, T2, T3>()
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent => new(_store.Query<T1, T2, T3>());

    public Query<T1, T2, T3, T4> Query<T1, T2, T3, T4>()
        where T1 : struct, IComponent where T2 : struct, IComponent
        where T3 : struct, IComponent where T4 : struct, IComponent => new(_store.Query<T1, T2, T3, T4>());

    public Query<T1, T2, T3, T4, T5> Query<T1, T2, T3, T4, T5>()
        where T1 : struct, IComponent where T2 : struct, IComponent where T3 : struct, IComponent
        where T4 : struct, IComponent where T5 : struct, IComponent => new(_store.Query<T1, T2, T3, T4, T5>());

    // Every entity (editor, tools, debug commands).
    public Query QueryAll() => new(_store.Query());

    // Every persistent entity, disabled ones included: what a save writes and a load clears. A save's
    // placeholder (SavePlaceholder, issue 4i-2) is disabled so that no query sees it, and still has to
    // be written back and cleared.
    internal QueryEntities PersistentIncludingDisabled() => new(_store.Query<Persistent>().WithDisabled().Entities);

    // Every runtime spawn no save names (Unsaved, 4m-4), disabled ones included: what a load takes away.
    internal QueryEntities UnsavedIncludingDisabled() =>
        new(_store.Query().AllTags(F.Tags.Get<Unsaved>()).WithDisabled().Entities);

    // Takes an entity out of every query (Friflo's Disabled tag), or puts it back.
    internal static void SetEnabled(Entity entity, bool enabled) { var raw = entity.Raw; raw.Enabled = enabled; }

    // ---- Systems and schedules ---------------------------------------------------------------------

    // Adds a declared system (issue #17): its id, phase, order and run condition come from its
    // [System] attribute, so the module that builds it says only what it is built from.
    //
    //     world.AddSystem(new AIThinkSystem(world, records, tasks, actions));
    //
    // `condition` overrides the declared one. A constraint across phases, or naming an id nothing
    // declares, throws here (SystemScheduler).
    public SystemInfo AddSystem(ISystem system, RunCondition? condition = null)
    {
        var declared = SystemDeclaration.Of(system.GetType())
            ?? throw new InvalidOperationException($"{system.GetType().Name} has no [System(\"id\", Phase.X)]: declare it, " +
                                                   "or add it unnamed with AddSystem(system, phase)");
        return Add(system, declared.Id, declared.Phase, condition ?? declared.Condition, declared.Before, declared.After);
    }

    // Adds a system that is not declared: a test's probe, a tool's one-off. `id` is optional — without
    // one nothing can order against it, replace it or disable it. A declared type goes through the
    // overload above, so that the attribute is the one place its phase is written.
    public SystemInfo AddSystem(ISystem system, Phase phase, RunCondition condition = RunCondition.Default,
        string? id = null, string[]? before = null, string[]? after = null)
    {
        if (SystemDeclaration.Of(system.GetType()) is { } declared)
            throw new InvalidOperationException($"{system.GetType().Name} is declared [System(\"{declared.Id}\", Phase.{declared.Phase})]: " +
                                                "add it with AddSystem(system), which reads its phase and order from the declaration");
        return Add(system, id, phase, condition, before ?? Array.Empty<string>(), after ?? Array.Empty<string>());
    }

    private SystemInfo Add(ISystem system, string? id, Phase phase, RunCondition condition, string[] before, string[] after)
    {
        var ledger = Engine?.Registrations;
        var access = Declare(system);
        var info = _scheduler.Add(system, id, phase, condition, before, after, ledger?.Owner ?? "host", access);
        if (id != null) ledger?.Record("system", id);
        Log.Debug(LogCat.World, $"System {id ?? info.Name} ({info.Name}) added to {phase} in '{Name}' by {info.Owner}");
        return info;
    }

    // Takes a system out of the world and lets go of what it held: its event readers, so the queues it
    // read stop waiting for it (before issue #17 they stayed pinned until ev_maxage, and the warning then
    // named a system that no longer existed), and the system itself when it is IDisposable.
    public bool RemoveSystem(ISystem system)
    {
        if (_scheduler.Remove(system) is not { } info) return false;
        Retire(system);
        Log.Debug(LogCat.World, $"System {info.Id ?? info.Name} removed from '{Name}'");
        return true;
    }

    // What a system declares it touches (issue #288), asked once, when it joins the world.
    internal SystemAccess? Declare(ISystem system)
    {
        if (system is not IDeclaresAccess declares) return null;
        var access = new SystemAccess(this);
        declares.Declare(access);
        if (BuildInfo.IsDevBuild) AccessCheck.On = true;
        return access;
    }

    // One checksum per component type a declaration reads (dev builds): see ComponentProbe.
    private readonly Dictionary<Type, ComponentProbe> _probes = new();

    internal ComponentProbe ProbeOf<T>() where T : struct, IComponent
    {
        if (!_probes.TryGetValue(typeof(T), out var probe)) _probes[typeof(T)] = probe = new ComponentProbe<T>(this);
        return probe;
    }

    internal void Retire(ISystem system)
    {
        _events.Release(system);
        (system as IDisposable)?.Dispose();
    }

    // The systems in this world, by phase; and replacing or disabling one by id (issue #17).
    public WorldSystems Systems { get; }

    // Gameplay facts between systems (04 §3.2). Get a reader once, in a constructor, and keep it.
    public GameEvents Events => _events;

    // What a phase guarantees, checked in dev builds rather than described in a comment (R16).
    public PhaseContracts Contracts => _contracts;

    // One simulation tick: copy poses for interpolation, then every Fixed phase in order, applying
    // buffered structural changes after each and propagating transforms after PostPhysics and Late.
    // True while RunFixed is running this world's phases: what a save requested now waits for (4i-6).
    internal bool InFixedTick { get; private set; }

    // A `Time.Pass` asked for during the tick, run at its boundary (issue 4g-2); null when none.
    internal TimePassRequest? PendingTimePass;

    // A `Travel.To` asked for during the tick, run at its boundary (issue 4g-5); null when none.
    internal TravelRequest? PendingTravel;

    // One real tick of `dt` seconds (issue #283): as many simulation steps of `dt` as the world's time
    // (`WorldTime`: its scale, pause and hit-stop) says are due, each a full pass of the Fixed phases with
    // its tick boundary; or, when none is, one held pass in which only the systems that run on real time
    // (`RunCondition.Always`) run. At scale 1, unpaused, that is exactly one step, as it always was.
    public void RunFixed(float dt) => RunFixed(dt, null);

    // The pause state last raised as an engine signal (EngineSignals.RaisePaused).
    private bool _pauseSignalled;

    // The same, calling `beforeStep` before every step (the host hands the player's command over there),
    // and before a held pass when the world is paused: a paused world drops what it is handed, as it
    // always did, while a slowed or hit-stopped one keeps a press for its next step.
    public void RunFixed(float dt, Action<World>? beforeStep)
    {
        var time = WorldTime.Of(this);
        if (time.Paused != _pauseSignalled)
        {
            // Paused or resumed since the last tick, by whoever set the flag (or a loaded save): say so once.
            _pauseSignalled = time.Paused;
            if (Engine != null) Engine.Signals.RaisePaused(this, time.Paused);
            else EngineSignals.SendPaused(this, time.Paused);
        }
        int steps = time.BeginTick(dt);
        if (steps == 0)
        {
            if (time.Paused) beforeStep?.Invoke(this);
            RunPass(time, step: false, first: true, dt);
            return;
        }
        for (int i = 0; i < steps; i++)
        {
            // A load at a step's boundary replaced the world's time (the saved `time` resource): the
            // loaded world starts on the next real tick, not with the steps the old one still owed.
            if (i > 0 && !ReferenceEquals(Resources.TryGet<WorldTime>(out var now) ? now : null, time)) break;
            beforeStep?.Invoke(this);
            RunPass(time, step: true, first: i == 0, dt);
        }
    }

    private void RunPass(WorldTime time, bool step, bool first, float dt)
    {
        time.BeginPass(step, first, dt);
        _held = !step;
        InFixedTick = true;
        try { RunFixedPhases(step ? dt : 0f, step); }
        finally { InFixedTick = false; _held = false; }

        // A door used or a journey asked for (issue 4g-5) first: the scene changes, and the hours it took
        // join any other time asked to pass this tick.
        // None of it in an edit world (issue #219): it is a document on screen, not a game in progress.
        if (!Editing)
        {
            if (PendingTravel != null) Travel.Run(this);
            // Time asked to pass (issue 4g-2) goes next, so a save at this boundary has the new time.
            if (PendingTimePass != null) Time.Run(this);

            // The tick boundary (issue 4i-6): a save or a load asked for during the tick runs now, with
            // every phase of it done, and the autosave clock advances. Nothing to do costs a comparison.
            Engine?.Saves.TickEnded(this, _lastTick.Dt);   // autosaves count simulated seconds
        }
        // And a streamed scene places, and puts to sleep, the sectors the ring reached or left (4g-3): work
        // that allocates, so never inside the phases.
        Engine?.Scenes.TickEnded(this);
    }

    // True during a held pass (no step due): only real-time systems run.
    private bool _held;

    private void RunFixedPhases(float dt, bool step)
    {
        _lastTick = new TickTime(_lastTick.Tick + 1, dt, _lastTick.SimTime + dt);
        Log.SetTick(_lastTick.Tick);
        // Interpolation is between the last two *steps*: a held pass keeps the pose the frame came from.
        if (step) _propagation.BeginTick();
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
    // At a world speed other than 1 the alpha is the world's own (WorldTime.Alpha): how far it is between
    // its last step and its next.
    public void RunFrame(float dt, float alpha, double realTime = 0)
    {
        if (Resources.TryGet<WorldTime>(out var time) && time != null) alpha = time.Alpha(alpha);
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
            if (systems.Count > 1 && ParallelEnabled && _scheduler.Plan(phase) is { HasParallelism: true } plan)
            {
                RunStages(plan, systems, phase, tick, frame);
            }
            else
            {
                int check = AccessCheckLevel;
                var ctx = new SystemContext(this, phase, tick, frame);
                for (int i = 0; i < systems.Count; i++)
                {
                    var s = systems[i];
                    if (!s.Enabled || !ShouldRun(s)) continue;
                    using var _ = Profiler.Begin(s.ProfileName);
                    if (check > 0 && s.Access != null) RunChecked(s, ctx, check);
                    else s.System.Run(ctx);
                }
            }
            FlushCommands();
        }
    }

    // ---- Systems side by side (issue #288) ---------------------------------------------------------

    private SystemWorkers? _workers;
    private bool _logging;                          // a staged phase is running: structural changes go to logs
    private IReadOnlyList<SystemInfo>? _loggedSystems;

    // sys_parallel, or the world's own setting; never without a way to keep Friflo's loop count right.
    private bool ParallelEnabled =>
        SystemWorkers.LoopCounter != null && (Systems.Parallel ?? Engine?.Scheduling?.Parallel.Value ?? true);

    // sys_access_check in dev builds (0 off, 1 touches, 2 touches and read-only values); 0 in Shipping.
    private int AccessCheckLevel =>
        !BuildInfo.IsDevBuild ? 0 : Systems.AccessCheckLevel ?? Engine?.Scheduling?.AccessCheck.Value ?? 2;

    private int WorkerCount
    {
        get
        {
            int n = Systems.Threads ?? Engine?.Scheduling?.Threads.Value ?? 0;
            return n > 0 ? n : Math.Clamp(Environment.ProcessorCount - 1, 0, 7);
        }
    }

    // The phase's stages, for tests and tools.
    internal PhasePlan PlanOf(Phase phase) => _scheduler.Plan(phase);

    // A declared system run alone, checked against its declaration.
    private void RunChecked(SystemInfo s, in SystemContext ctx, int check)
    {
        var run = s.Run ??= new SystemRun(this, s);
        var probes = check >= 2 ? s.Access!.ReadProbes : Array.Empty<ComponentProbe>();
        for (int p = 0; p < probes.Count; p++) probes[p].Before = probes[p].Checksum();
        var previous = AccessCheck.Current;
        AccessCheck.Current = run;
        run.Validate = true;
        try { s.System.Run(ctx); }
        finally
        {
            run.Validate = false;
            AccessCheck.Current = previous;
        }
        for (int p = 0; p < probes.Count; p++)
            if (probes[p].Checksum() != probes[p].Before)
                Systems.ReportAccess(s, "changed a component it declared only reading", probes[p].Type);
    }

    // A phase in stages: each stage's systems at the same time, on the workers and this thread; every
    // system's structural changes on its own log, replayed into the world's buffer in the phase's order
    // at the end, so the buffer plays back what a sequential run would have recorded.
    private void RunStages(PhasePlan plan, IReadOnlyList<SystemInfo> systems, Phase phase, in TickTime tick, in FrameTime frame)
    {
        int check = AccessCheckLevel;
        var counter = SystemWorkers.LoopCounter!;
        int loops = counter.Get(_store);
        _commands ??= new EntityCommands(_store.GetCommandBuffer());
        _loggedSystems = systems;
        _logging = true;
        Exception? error = null;
        try
        {
            for (int k = 0; k < plan.Stages.Length && error == null; k++)
            {
                var members = plan.Stages[k];
                var batch = plan.Scratch;
                int n = 0;
                for (int i = 0; i < members.Length; i++)
                {
                    var s = members[i];
                    if (!s.Enabled || !ShouldRun(s)) continue;
                    var run = s.Run ??= new SystemRun(this, s);
                    run.Validate = check > 0 && s.Access != null;
                    batch[n++] = run;
                }
                if (n == 0) continue;

                var probes = check >= 2 ? plan.StageProbes[k] : Array.Empty<ComponentProbe>();
                for (int p = 0; p < probes.Length; p++) probes[p].Before = probes[p].Checksum();

                if (n == 1) batch[0]!.Execute(phase, tick, frame, onWorker: false);
                else
                {
                    var workers = _workers;
                    if (workers == null || workers.Count != WorkerCount)
                    {
                        workers?.Dispose();
                        _workers = workers = new SystemWorkers(WorkerCount, Name);
                    }
                    workers.Run(batch, n, phase, tick, frame);
                    counter.Set(_store, loops);
                }

                for (int i = 0; i < n; i++)
                {
                    var run = batch[i]!;
                    batch[i] = null;
                    run.Validate = false;
                    Profiler.Record(run.Info.ProfileName, run.Ticks);
                    if (run.Bytes != 0) Interlocked.Add(ref Systems.WorkerAllocatedBytes, run.Bytes);
                    error ??= run.Error;
                    run.Error = null;
                }

                for (int p = 0; p < probes.Length; p++)
                {
                    if (probes[p].Checksum() == probes[p].Before) continue;
                    foreach (var s in members)
                        if (s.Access?.ReadsComponent(probes[p].Type) == true)
                            Systems.ReportAccess(s, "ran while a component it declared only reading changed, and nothing running declared writing it", probes[p].Type);
                }
            }
        }
        finally
        {
            _logging = false;
            ReplayLogs(systems);
            _loggedSystems = null;
        }
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private void ReplayLogs(IReadOnlyList<SystemInfo> systems)
    {
        for (int i = 0; i < systems.Count; i++)
            if (systems[i].Run is { HasLog: true } run) run.Log.ReplayInto(_commands!);
    }

    // An edit world runs no Fixed system but an `EvenWhenEditing` one, not even an `Always` one (issue #219).
    private bool ShouldRun(SystemInfo s) => Editing && PhaseInfo.ScheduleOf(s.Phase) == Schedule.Fixed
        ? s.Condition == RunCondition.EvenWhenEditing
        : s.Condition switch
    {
        RunCondition.Always => true,
        RunCondition.WhenNotPaused => PhaseInfo.ScheduleOf(s.Phase) == Schedule.Frame ? !Paused : !_held,
        RunCondition.DevOnly => BuildInfo.IsDevBuild,
        _ => PhaseInfo.ScheduleOf(s.Phase) == Schedule.Frame || !_held,
    };

    // ---- Deferred structural changes -------------------------------------------------------------

    // Record structural changes here while iterating a query; they're applied at the end of the
    // current phase (or by FlushCommands()).
    //
    // While a phase runs systems side by side (issue #288), each system records on a log of its own, which
    // the world replays here in the phase's order: the same changes, in the same order, as one buffer.
    public EntityCommands Commands =>
        _logging && AccessCheck.Current is { } run && run.World == this
            ? run.Log
            : _commands ??= new EntityCommands(_store.GetCommandBuffer());

    private bool HasPendingCommands => _commands is { HasPending: true };

    public void FlushCommands()
    {
        if (_logging)
        {
            // An exclusive system flushing mid-phase: everything before it in the phase has run, so its
            // logs go in first, in order, as their changes would have been in the buffer.
            if (AccessCheck.Current is { OnWorker: true } worker)
                throw new InvalidOperationException($"{worker.Info.Id ?? worker.Info.Name} flushed the world's commands while running " +
                                                    "beside other systems: declare it Exclusive, or leave the flush to the end of the phase.");
            ReplayLogs(_loggedSystems!);
        }
        if (HasPendingCommands)
            _commands!.Playback();
        DestroyOrphans();
    }

    // ---- Persistent ids ---------------------------------------------------------------------------

    public Entity Resolve(PersistentId id) => _persistent.TryGetValue(id, out var e) && IsAlive(e) ? e : default;

    // The entity a held reference means now (issue 4m-4): itself while it lives; for a handle to one that
    // went to sleep with its cell (or was taken by a handoff), the entity with its persistent id once it is
    // back, and the null entity while it sleeps; the null entity for one that is simply gone. A component
    // that keeps an entity across ticks (an effect's source, a target) reads it through this.
    public Entity Resolve(Entity reference)
    {
        if (IsAlive(reference)) return reference;
        return SleepingHandles.TryGetId(this, reference, out var id) ? Resolve(id) : default;
    }

    // Adds a Persistent component with a new id (runtime-spawned entities that should be saved), and the
    // cell it was made in (phase 4g-1, InCell): it goes dormant with that cell and comes back with it.
    public PersistentId MakePersistent(Entity entity)
    {
        if (TryGet(entity, out Persistent existing))
            return existing.Id;
        var id = PersistentId.New();
        if (entity.Tags.Has<Unsaved>()) entity.RemoveTag<Unsaved>();   // saved from now on (4m-4)
        Add(entity, new Persistent { Id = id });
        Cells.Join(this, entity);
        return id;
    }

    // ---- Notifications ----------------------------------------------------------------------------

    private void OnComponentAdded(F.ComponentChanged change)
    {
        AccessCheck.Structural();
        var entity = change.Entity.AsSage();
        if (change.Type == typeof(Persistent))
            IndexPersistent(entity);
        if (_structural.Any) _structural.OnAdded(change);
        ComponentAdded?.Invoke(entity, change.Type);
    }

    private void OnComponentRemoved(F.ComponentChanged change)
    {
        AccessCheck.Structural();
        if (change.Type == typeof(Persistent))
            _persistent.Remove(change.OldComponent<Persistent>().Id);
        if (_structural.Any) _structural.OnRemoved(change);
        ComponentRemoved?.Invoke(change.Entity.AsSage(), change.Type);
    }

    // Friflo raises only OnEntityDelete when an entity is deleted, while it is still alive with all
    // its components. Report each component's removal first, then the destruction (spike, 03 §3.1).
    private void OnEntityDelete(F.EntityDelete delete)
    {
        AccessCheck.Structural();
        var entity = delete.Entity.AsSage();
        if (entity.TryGetComponent<Persistent>(out var p))
            _persistent.Remove(p.Id);
        if (_structural.Any) _structural.OnDeleting(delete.Entity);
        if (ComponentRemoved != null)
        {
            foreach (var component in entity.Components)
                ComponentRemoved.Invoke(entity, component.Type);
        }
        EntityDestroyed?.Invoke(entity);
        NoteOrphans(entity);
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
        foreach (var s in _scheduler.All)
            (s.System as IDisposable)?.Dispose();   // what a system took in its constructor (issue #17)
        _workers?.Dispose();
        _workers = null;
        Resources.DisposeAll();
        Log.Debug(LogCat.World, $"World '{Name}' destroyed");
    }
}
