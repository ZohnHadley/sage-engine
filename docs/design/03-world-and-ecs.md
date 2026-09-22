# 03 — World and ECS

## 1. Purpose and scope
The simulation's object model:
- `World`, entities, components and queries;
- systems and schedules;
- command buffers;
- per-world resources;
- the transform hierarchy.

Not in scope: messaging (04), serialization and prefab file formats (09), rendering's copy of the data (06).

## 2. Research basis
- Components as data, systems as logic, singleton components for global state: Overwatch (survey §2.8).
- Archetype storage for fast multi-component iteration; frequently toggled state as fields, not tags (Mertens): survey §3.2.
- The hybrid model: ECS for gameplay, heavy subsystems own their data (UE Actors + Mass, Godot servers): survey §2.2, §2.4.
- Schedules with declared access, fixed + frame: Bevy (survey §2.5).
- Generational ids: Bitsquid (survey §2.8).
- Transform hierarchy as a narrow feature (parent + propagation pass), not the main object model: survey §3.2.

## 3. Concepts

### 3.1 Storage decision: requirements first, then a spike
Friflo.Engine.ECS already provides ids, archetype storage, queries, command buffers, hierarchy and JSON serialization. Writing all of that ourselves is only justified if Friflo fails these requirements. The **spike** (ARCHITECTURE D4) checks each one:

| # | Requirement | Why |
|---|---|---|
| E1 | Stale-handle detection: a handle to a deleted entity must be detectably invalid, even if the slot is reused | Dangling references are the most common ECS bug |
| E2 | Stable `PersistentId` per placed/saved entity, independent of runtime ids | Maps, saves and quests reference entities across sessions |
| E3 | Deferred structural changes (command buffer) usable during iteration | Spawning/killing during a query must be safe |
| E4 | Multi-component queries with no per-iteration allocation | The GC rule (02 §4.6) |
| E5 | Structural change notifications (added/removed) | Messaging (04) and editor updates |
| E6 | Parent/child hierarchy | Riders, attached weapons, sprite groups |
| E7 | Serialization hook we control (our attributes + source generator, 09) | One reflection source for save/prefab/inspector |
| E8 | Several independent worlds in one process | Overworld/battle, editor edit/play, tests |
| E9 | Works with class-free, struct components | Pooled, cache-friendly, easy to snapshot |

- **If Friflo passes** (it did, see the verdict below): `World` wraps a Friflo `EntityStore` **thinly**. It owns lifetime, ids, spawning, resources and schedules, but **queries expose Friflo's own query types** rather than re-wrapping them, so we keep its performance.
- **If Friflo had failed:** we would have kept the old approach, a `Dictionary<Type, …>` per entity, behind the same `World` API. Then we move to our own archetype storage only if profiling demands it.

**Spike verdict (2026-09-22, Friflo.Engine.ECS 3.6.0, MIT): adopted.** Measured, not taken from the docs:

| # | Result |
|---|---|
| E1 | ✓ Deleted slots are reused, but the old handle reports `IsNull` and compares unequal to the new one |
| E2 | ✓ Friflo has 64-bit PIDs; we keep our own 128-bit `PersistentId` in a `Persistent` component with an index in `World` |
| E3 | ✓ `CommandBuffer`; a direct structural change inside a query loop **throws** (useful protection) |
| E4 | ✓ Iterating `query.Chunks` over 10k entities: **0 bytes** allocated. `ForEachEntity` with a lambda allocated 88 B per 100 calls, so engine hot paths use chunks |
| E5 | ✓ with a gap: deleting an entity raises only `OnEntityDelete`, not per-component removals. The entity is still alive with its components inside that handler (direct and `CommandBuffer` deletes alike), so `World` reports each removal there, then the destruction |
| E6 | ✓ `AddChild` / `Parent` / `ChildEntities` |
| E7 | ✓ `GetComponent<T>()` returns `ref T`, so our generated serializers can read/write components directly; Friflo's own JSON serializer goes unused |
| E8 | ✓ Independent `EntityStore`s |
| E9 | ✓ Components are structs implementing `Friflo.Engine.ECS.IComponent` |

Two things the spike taught about Friflo that the engine now handles:
- **Schema scan.** Friflo builds its component list once, the first time a store is used, from the assemblies loaded *at that moment*, and prints "Assemblies loaded: …" to stdout. `EcsSchema.EnsureInitialized` triggers it on the first `World` and redirects that line into the log. Rule: **every assembly defining component types must be loaded before the first `World` is created** (the host forces `Sage.Client`/`Sage.Editor` in; module loading does it for games and mods from migration step 5).
- **Naming.** Friflo's `Entity`/`IComponent`/`EntityStore` replace Sage's old classes of the same names; our old `Entity` class and `IComponent` interface were deleted.

The rest of this doc is written against the `World` API.

### 3.2 Components
- **Plain data**, preferably structs. No methods beyond small pure helpers, no references to services, no `Entity` object references. Use `EntityRef` for links to other entities.
- One component type per entity (archetype storage enforces it; `World.Add` refuses a second one; review item #13).
- **Tags** (Friflo `ITag` structs, stored in the archetype, not as components) are for rarely changing classification (`IsPlayer`, `Static`). State that flips often (stunned, burning, open/closed) is a **field**, because adding or removing a component moves the entity between archetypes (Mertens).
- Math helpers such as `LookAt`/`Billboard` live in the static `TransformMath` class (done in step 3). The component stays data (TODO R4).
- **Names** use Friflo's built-in `EntityName` component (`World.Create("bunny")` adds it).

### 3.3 Entity identity
| Type | What | Lifetime |
|---|---|---|
| `EntityRef` | Runtime handle. **Realised as Friflo's `Entity` struct** (store + id + revision); code uses the type `Entity`. `IsNull` / `World.IsAlive` detect deleted entities | One world, one session |
| `PersistentId` | 128-bit id (GUID) stored in a `Persistent` component | Forever. Assigned when the entity is placed in a map, or when a runtime-spawned entity is marked persistent (dropped items, recruited NPCs). Entities without it are never saved (09 §3.5) |

`World.Resolve(PersistentId)` maps back to an `EntityRef` through an index maintained by structural notifications.

### 3.4 Resources (per-world singletons)
State that isn't per-entity lives in world **resources**, the equivalent of Overwatch's singleton components: `GameRules`, the physics space, the current `TickTime`, the origin sector, the event queues (04). Accessed as `world.Resources.Get<T>()`. No static singletons (TODO R1).

### 3.5 Systems and schedules
- A system is a class implementing `ISystem`, registered into a **schedule** and **phase**:
  - `Schedule.Fixed`, at `sim_tickrate` (01 §5.2): `Commands → PrePhysics → Physics → PostPhysics → Gameplay → AI → Animation → EntityIO → Late`.
  - `Schedule.Frame`, once per rendered frame: `FrameUpdate → Extract → Render → Overlay`.
- Ordering within a phase: `before:`/`after:` constraints naming other system types (a stable topological sort; a cycle throws at registration). Otherwise registration order applies, deterministically. *(Built in step 4. "System sets" were dropped for now: type constraints cover today's needs.)*
- **Access declarations** (components read/written, game events read/sent, 04 §4) are **not built yet**. They arrive with the event bus (whose readers/writers they create) and matter again for a parallel scheduler. v1 runs systems sequentially.
- **Run conditions:** `Default` (= `WhenNotPaused` in Fixed phases, `Always` in Frame phases), `WhenNotPaused`, `Always`, `DevOnly`. `World.Paused` (console `pause`) skips `WhenNotPaused` systems; ticks still run.
- **Command buffer flush points:** the end of every phase. Structural changes made during a phase become visible in the next phase.
- Every phase and system is wrapped in a profiler scope (`Fixed.Gameplay`, `Fixed.Gameplay/FaceCameraSystem`), shown by `stat frame` and `sys_list`.
- **Steady state allocates nothing**: a test runs 100 ticks + frames with systems and measures 0 bytes.

### 3.6 Transform hierarchy and large-world coordinates
| Component | Fields | Written by |
|---|---|---|
| `Transform` | `Vector3 LocalPosition`, `Quaternion LocalRotation`, `Vector3 LocalScale` (relative to the parent, or to the entity's sector if it's a root) | gameplay, physics sync |
| `SectorCoord` (root entities only) | `int X, Z`: which 1024 m sector the root's `LocalPosition` is relative to (14). **Not built yet** (arrives with large-world coordinates, TODO R6); until then roots are relative to the world origin | streaming, movement when crossing a sector edge |
| `Parent` / `Children` | Friflo's built-in hierarchy (`Entity.Parent`, `Entity.ChildEntities`) | `World.SetParent` / `ClearParent` only |
| `GlobalTransform` | `Pose Current`, `Pose Previous` (a `Pose` is position + rotation + scale), relative to the world's **origin sector** (today: the origin) | transform propagation only |

- **Transform propagation** (built in step 4, `ECS/Systems/TransformPropagation.cs`) is run by `World` itself at the end of `PostPhysics` and `Late`, not registered as a user system. It walks roots → children and computes `GlobalTransform.Current` (relative to `world.Resources.Get<Origin>().Sector` once sectors exist). At the start of each tick, before `Commands`, `Current` is copied to `Previous`, so rendering can interpolate (06). This replaced the old stub `TransformSystem` (TODO #35).
- `World.Create` gives every entity a `GlobalTransform` at its spawn pose (so it doesn't slide in from the origin), and `World.Teleport` moves a root without interpolating the jump.
- Poses are stored as position/rotation/scale rather than matrices, because rendering interpolates them (lerp position and scale, slerp rotation). Interpolating matrices component-wise distorts rotations. Matrices are built at extract time.
- When the origin sector changes (14), all `GlobalTransform` positions (including `Previous`) are shifted by the same offset, so interpolation doesn't jump.

## 4. Public API sketch

Built in steps 3 and 4 (`src/Sage.Engine/ECS/World.cs`, `ECS/Systems/*`).
```csharp
public readonly record struct PersistentId(Guid Value) { public static PersistentId New(); }
public struct Persistent : IComponent { public PersistentId Id; }

public sealed class World : IDisposable
{
    public World(string name, Engine? engine = null);             // normally via Engine.CreateWorld
    public string Name { get; }
    public Engine? Engine { get; }
    public WorldResources Resources { get; }
    public int EntityCount { get; }

    // Entities (Friflo `Entity` handles)
    public Entity Create(string? name = null);                    // identity Transform + GlobalTransform (+ EntityName)
    public Entity Create(in Transform transform, string? name = null);
    public void Destroy(Entity e);                                // immediate; inside a query loop use Commands
    public bool IsAlive(Entity e);                                // false for deleted/stale handles and other worlds' entities
    public Entity Resolve(PersistentId id);                       // default (IsNull) if not loaded
    public PersistentId MakePersistent(Entity e);
    public void Teleport(Entity e, in Transform t);               // move a root without interpolating the jump

    // Components
    public ref T Get<T>(Entity e) where T : struct, IComponent;   // missing: Ensure; throws in dev builds, dummy in Shipping
    public bool TryGet<T>(Entity e, out T value) where T : struct, IComponent;
    public bool Has<T>(Entity e) where T : struct, IComponent;
    public bool Add<T>(Entity e, in T component) where T : struct, IComponent;   // false (+ Ensure) if already present
    public bool Remove<T>(Entity e) where T : struct, IComponent;                // false, no side effects, if absent

    // Hierarchy
    public void SetParent(Entity child, Entity parent);
    public void ClearParent(Entity child);

    // Queries: Friflo's own types, returned as-is. Cache them; iterate .Chunks in hot paths.
    public ArchetypeQuery<T1> Query<T1>();   /* also <T1,T2> and <T1,T2,T3> */
    public ArchetypeQuery QueryAll();

    // Deferred structural changes (Friflo CommandBuffer, reused). Applied at the end of every phase.
    public CommandBuffer Commands { get; }
    public void FlushCommands();

    // Structural notifications (04 §3.3), immediate, on the world's thread.
    public event Action<Entity>? EntitySpawned;
    public event Action<Entity, Type>? ComponentAdded, ComponentRemoved;
    public event Action<Entity>? EntityDestroyed;

    // Scheduling
    public SystemInfo AddSystem(ISystem system, Phase phase, RunCondition condition = RunCondition.Default,
                                Type[]? before = null, Type[]? after = null);
    public bool RemoveSystem(ISystem system);
    public IEnumerable<SystemInfo> Systems { get; }               // name, phase, condition, Enabled (sys_toggle)
    public void RunFixed(float dt);                               // one tick: every Fixed phase
    public void RunFrame(float dt, float alpha, double realTime = 0);   // one frame: every Frame phase
    public bool Paused { get; set; }
    public long Tick { get; }  public double SimTime { get; }
}

public interface ISystem
{
    void Run(in SystemContext ctx);
    // void Declare(SystemAccess access);   // later, with the event bus / parallel scheduler (§3.5)
}

public readonly ref struct SystemContext
{
    public World World { get; }
    public Phase Phase { get; }
    public TickTime Tick { get; }              // Fixed schedule (the last tick, in Frame phases)
    public FrameTime Frame { get; }            // Frame schedule: Dt, Alpha (interpolation), Frame, RealTime
    public CommandBuffer Commands { get; }     // structural changes, applied at the end of the phase
}

// CommandBuffer is Friflo's (CreateEntity, DeleteEntity(id), AddComponent<T>(id, c), RemoveComponent<T>(id),
// AddChild, Playback). Prefab spawning (`Spawn(RecordId prefab, …)`) is added on top with records (05, step 5).
```

Example system:
```csharp
sealed class DamageOverTimeSystem : ISystem                // Gameplay phase
{
    // (with access declarations, later: public void Declare(SystemAccess a) => a.Reads<Burning>().Writes<Health>();)
    public void Run(in SystemContext ctx)
    {
        // for each (ref Health h, in Burning b): h.Current -= b.DamagePerSecond * ctx.Tick.Dt;
        //   if h.Current <= 0: send a Died game event (04); ctx.Commands.Destroy(...) later, via GameRules
    }
}
```

Note: making sprites face the camera (today's per-frame `Billboard` call in `Game1.Update`, TODO #26) is **not** a simulation system. It's presentation: the renderer orients sprite quads at extract (06 §3.8) and draws them with `sprite.fx` (07). The simulation only stores which way the creature faces (its yaw), which the renderer uses to pick the 8-direction frame.

## 5. Lifecycle
```
Create ──► EntitySpawned ──► ComponentAdded (Transform, EntityName, …) ──► visible to queries
Destroy ─► ComponentRemoved for each component ──► EntityDestroyed ──► slot freed; old handles report IsNull
(Commands.* record instead; the same notifications fire at FlushCommands.)
```
World creation: resources installed by modules (physics space, origin, event queues) → `IGameModule.OnWorldCreated` installs `GameRules` and loads the map. World destruction: all entities destroyed (notifications suppressed; one `WorldDestroying` signal instead), then resources disposed in reverse order.

## 6. Threading and memory
- Worlds are single-threaded in v1 (main thread). Systems may use jobs over **copied input spans**, writing results that a later system applies. Jobs never touch world structure.
- Queries, command buffers and hierarchy walks don't allocate in steady state. Command buffers reuse their backing arrays. `Children` lists are pooled.
- The `PersistentId` index is a `Dictionary<PersistentId, EntityRef>` updated only by structural notifications.

## 7. File formats
None of its own. Prefabs, maps and saves use the serializer (09); prefab definitions are records (05).

## 8. Errors and fallbacks
- `Get<T>` on a missing component: `Assert.Ensure` fails (logged once per call site with the entity's name and `PersistentId`). It then throws in `Development`/`Debug`, and returns a zeroed dummy in `Shipping` rather than crashing the game.
- Using a stale `EntityRef`: `IsAlive` is false. `Get` behaves like the missing-component case.
- Structural change inside a query without a command buffer: Friflo throws immediately (all builds).
- Adding a component type twice: `Ensure` failure; `Add` returns false and the existing data is kept.
- Removing a component the entity doesn't have: `Remove` returns false with no side effects (review #33).

## 9. Debug and tooling hooks
- **Commands:**
  - `ent_list [filter]` (**built, step 3**; registered by the host for the main world)
  - `ent_dump <id|name>` (all components via the generated inspector metadata)
  - `ent_kill`
  - `sys_list` (systems per world and phase with average ms) — **built, step 4**
  - `sys_toggle <name>` (DevOnly) — **built, step 4**
  - `pause` — **built, step 4**
- **Overlay:** entity count per world, archetype count, command buffer ops per tick.
- **Editor:** outliner and inspector (15), fed by structural notifications.
- **Log category:** `World`. `log_level world trace` logs every structural change (with the system that caused it).

## 10. Mapping from today's code
| Today (`src/...`) | Becomes |
|---|---|
| `EntityContext` (singleton, random `long` ids, `EntitiesDict`, string-keyed groups) | **Done (step 3):** `World` over Friflo; stale-detecting handles + `PersistentId`; no public mutable dictionaries (fixed #15, #38) |
| `Entity` class (`Dictionary<Type, IComponent>`) | **Done (step 3):** Friflo `Entity` + `World.Get/TryGet/Has/Add/Remove<T>` |
| `EntityContextListener` + `ArchetypeView` | **Done (step 3):** typed `ArchetypeQuery` (membership) + `World` notifications |
| `IComponent` interface | **Done (step 3):** Friflo's `IComponent` on struct components. `IComponentSystem`/`IEngineSystem` replaced by `ISystem` (step 4) |
| `ComponentTransform` class (`LookAt`/`Billboard` methods) | **Done (step 3):** `Transform` struct (`LocalPosition/LocalRotation/LocalScale`, `Transform.Identity`) + `TransformMath`. `SectorCoord`/`GlobalTransform` + propagation in step 4 |
| `ComponentMeshRenderer` class → **`ModelRenderer` struct** (step 3; still holds a MonoGame `Model`, so it lives in `Sage.Client`) | `MeshRenderer { AssetPath Mesh; RecordId Material; }` and `SpriteRenderer { AssetPath Sheet; RecordId Material; … }` (06). A MonoGame type can't live in simulation (01 §3.1). `AssetPath` is an unloaded, interned path (05), so the simulation never loads render data; the client resolves it to GPU resources |
| (the `TransfomSystem.cs` stub, deleted 2026-09-22) | **Done (step 4):** transform propagation, run by `World` after `PostPhysics` and `Late` |
| `Sage.Client/Rendering/ModelRendererSystem.cs` (now one instance per world, iterating `Query<Transform, ModelRenderer>().Chunks`) | Extract system in the client (06) |

## 11. v1 scope vs later
- **v1** (✓ = built in migration step 3):
  - ✓ the Friflo spike and decision;
  - ✓ `World`, handles, `PersistentId`, struct components, typed queries, `CommandBuffer`, resources, notifications;
  - ✓ hierarchy (`SetParent`/`ClearParent`);
  - ✓ `Transform` + `TransformMath`;
  - ✓ Fixed/Frame schedules with phases, `before`/`after` ordering, run conditions, per-phase command flushing (step 4);
  - ✓ `GlobalTransform` + propagation + interpolation (step 4); `SectorCoord` with R6;
  - access declarations (with the event bus);
  - dev commands: ✓ `ent_list`, `sys_list`, `sys_toggle`, `pause`; `ent_dump`, `ent_kill` later.
- **Later:** parallel system execution from the access declarations; several active worlds; per-system profiling in the editor.

## 12. Multiplayer-later notes
- Components stay data (readiness rule 3). `EntityRef` never crosses the network; a `NetId` component is added then.
- Systems will gain a `Server`/`Client`/`Shared` tag. Today every system is implicitly `Shared`.

## 13. Open questions
- ~~Does Friflo's own JSON serializer satisfy E7?~~ **Decided by the spike:** `GetComponent<T>` returns `ref T`, so our generated serializers (09) read and write components directly; Friflo's serializer is unused.
- Should `Get<T>` in `Shipping` throw instead of returning a dummy? Current choice: don't crash players' games over a gameplay bug. It's logged loudly instead.

## 14. Build steps
1. ✓ Friflo spike against E1–E9, with a written verdict (§3.1; ARCHITECTURE D4; TODO R5).
2. ✓ `World` + handles + resources + `Engine`; static singletons removed (TODO R1, #15).
3. ✓ Schedules, phases, `ISystem`, per-phase `CommandBuffer` flushing (step 4; TODO R2, R4).
4. ✓ `GlobalTransform` + propagation (step 4). `SectorCoord` + origin rebasing with TODO R6. (✓ `Transform` struct + `TransformMath` done in step 3.)
5. ✓ Typed queries replace string-keyed groups and `ArchetypeView` (TODO #38).
