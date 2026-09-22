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

- **If Friflo passes:** `World` wraps a Friflo `EntityStore` **thinly**. It owns lifetime, ids, spawning, resources and schedules, but **queries expose Friflo's own query types** rather than re-wrapping them, so we keep its performance.
- **If Friflo fails (e.g. E7 conflicts with its serializer):** we keep today's approach, a `Dictionary<Type, …>` per entity, behind the same `World` API. Then we move to our own archetype storage only if profiling demands it.

The rest of this doc is written against the `World` API, which doesn't change with the choice.

### 3.2 Components
- **Plain data**, preferably structs. No methods beyond small pure helpers, no references to services, no `Entity` object references. Use `EntityRef` for links to other entities.
- One component type per entity (as today's `Dictionary<Type, …>` already enforces; review item #13 in `docs/history/code-review-log.md`).
- **Tags** (zero-size components) are for rarely changing classification (`IsPlayer`, `Static`). State that flips often (stunned, burning, open/closed) is a **field**, because adding or removing a component moves the entity between archetypes (Mertens).
- Math helpers such as today's `LookAt`/`Billboard` move to a static `TransformMath` class. The component stays data (TODO R4).

### 3.3 Entity identity
| Type | What | Lifetime |
|---|---|---|
| `EntityRef` | Runtime handle (index + generation, or the library's equivalent) | One world, one session |
| `PersistentId` | 128-bit id (GUID) stored in a `Persistent` component | Forever. Assigned when the entity is placed in a map, or when a runtime-spawned entity is marked persistent (dropped items, recruited NPCs). Entities without it are never saved (09 §3.5) |

`World.Resolve(PersistentId)` maps back to an `EntityRef` through an index maintained by structural notifications.

### 3.4 Resources (per-world singletons)
State that isn't per-entity lives in world **resources**, the equivalent of Overwatch's singleton components: `GameRules`, the physics space, the current `TickTime`, the origin sector, the event queues (04). Accessed as `world.Resources.Get<T>()`. No static singletons (TODO R1).

### 3.5 Systems and schedules
- A system is a class implementing `ISystem`, registered into a **schedule** and **phase**:
  - `Schedule.Fixed`, at `sim_tickrate` (01 §5.2): `Commands → PrePhysics → Physics → PostPhysics → Gameplay → AI → Animation → EntityIO → Late`.
  - `Schedule.Frame`, once per rendered frame: `FrameUpdate → Extract → Render → Overlay`.
- Ordering within a phase uses **system sets** and `Before`/`After` constraints. Otherwise registration order applies, deterministically.
- Systems **declare the components they read and write, and the game events they read and send** (`ReadsEvents<T>`/`SendsEvents<T>`, 04 §4). v1 runs systems sequentially and uses the declarations for validation (a dev check flags an undeclared write or event) and to create event readers/writers. Later, the scheduler can run non-conflicting systems in parallel (Bevy).
- **Run conditions:** `WhenNotPaused` (default for gameplay), `Always` (camera, UI), `DevOnly`.
- **Command buffer flush points:** the end of every phase. Structural changes made during a phase become visible in the next phase.

### 3.6 Transform hierarchy and large-world coordinates
| Component | Fields | Written by |
|---|---|---|
| `Transform` | `Vector3 LocalPosition`, `Quaternion LocalRotation`, `Vector3 LocalScale` (relative to the parent, or to the entity's sector if it's a root) | gameplay, physics sync |
| `SectorCoord` (root entities only) | `int X, Z`: which 1024 m sector the root's `LocalPosition` is relative to (14) | streaming, movement when crossing a sector edge |
| `Parent` / `Children` | `EntityRef` / list of `EntityRef` | the hierarchy API only |
| `GlobalTransform` | `Pose Current`, `Pose Previous` (a `Pose` is position + rotation + scale), relative to the world's **origin sector** | `TransformPropagationSystem` only |

- **`TransformPropagationSystem`** runs at the end of `PostPhysics` and `Late`. It walks roots → children and computes `GlobalTransform.Current` relative to `world.Resources.Get<Origin>().Sector`. At the start of each tick (in `Commands`), `Current` is copied to `Previous`, so Extract can interpolate (06). This replaces today's stub `TransformSystem` (TODO #35).
- Poses are stored as position/rotation/scale rather than matrices, because rendering interpolates them (lerp position and scale, slerp rotation). Interpolating matrices component-wise distorts rotations. Matrices are built at extract time.
- When the origin sector changes (14), all `GlobalTransform` positions (including `Previous`) are shifted by the same offset, so interpolation doesn't jump.

## 4. Public API sketch

```csharp
public readonly struct EntityRef : IEquatable<EntityRef> { public static readonly EntityRef None; }
public readonly struct PersistentId : IEquatable<PersistentId> { public static PersistentId New(); }

public sealed class World
{
    public string Name { get; }
    public Engine Engine { get; }
    public WorldResources Resources { get; }

    // Entities
    public EntityRef Create();                                    // outside iteration only (else use a CommandBuffer)
    public EntityRef Spawn(RecordId prefab, in Transform at, SectorCoord sector);  // prefab records (09)
    public void Destroy(EntityRef e);                             // deferred to the next flush point
    public bool IsAlive(EntityRef e);
    public EntityRef Resolve(PersistentId id);                    // EntityRef.None if not loaded

    // Components
    public ref T Get<T>(EntityRef e) where T : struct;            // throws in dev builds (Log.Error + Ensure) if missing
    public bool TryGet<T>(EntityRef e, out T value) where T : struct;
    public bool Has<T>(EntityRef e) where T : struct;
    public void Add<T>(EntityRef e, in T component) where T : struct;     // immediate outside iteration
    public void Remove<T>(EntityRef e) where T : struct;

    // Hierarchy
    public void SetParent(EntityRef child, EntityRef parent, bool keepWorldTransform = true);

    // Queries (type depends on the storage decision; e.g. Friflo's ArchetypeQuery<T1,T2>)
    public Query<T1, T2> Query<T1, T2>() where T1 : struct where T2 : struct;   // cached; no per-call allocation

    // Scheduling
    public void AddSystem(ISystem system, Phase phase, Action<SystemOrdering>? ordering = null);
    public void RunFixed(float dt);                               // called by the host (01)
    public void RunFrame(float dt, float alpha);
}

public interface ISystem
{
    void Declare(SystemAccess access);         // access.Reads<Transform>().Writes<GlobalTransform>()
    void Run(in SystemContext ctx);
}

public readonly ref struct SystemContext
{
    public World World { get; }
    public TickTime Tick { get; }              // Fixed schedule
    public FrameTime Frame { get; }            // Frame schedule
    public CommandBuffer Commands { get; }     // structural changes, flushed at the end of the phase
}

public sealed class CommandBuffer
{
    public EntityRef Create();                 // provisional ref, valid after flush
    public void Destroy(EntityRef e);
    public void Add<T>(EntityRef e, in T c) where T : struct;
    public void Remove<T>(EntityRef e) where T : struct;
    public void Spawn(RecordId prefab, in Transform at, SectorCoord sector);
}
```

Example system:
```csharp
sealed class DamageOverTimeSystem : ISystem                // Gameplay phase
{
    public void Declare(SystemAccess a) => a.Reads<Burning>().Writes<Health>();
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
Create/Spawn ──► (flush point) ──► Added notifications (04) ──► visible to queries
Destroy ─────► (flush point) ──► Removed notifications for each component, then EntityDestroyed
                                  ──► generation bump: old EntityRefs now fail IsAlive
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
- Structural change inside a query without a command buffer: dev `Assert.Dev` failure naming the system.
- Adding a component type twice: `Ensure` failure; the second add is ignored (today it throws `ArgumentException`).

## 9. Debug and tooling hooks
- **Commands:**
  - `ent_list [filter]`
  - `ent_dump <id|name>` (all components via the generated inspector metadata)
  - `ent_kill`
  - `sys_list` (systems per phase with ms)
  - `sys_toggle <name>` (DevOnly)
- **Overlay:** entity count per world, archetype count, command buffer ops per tick.
- **Editor:** outliner and inspector (15), fed by structural notifications.
- **Log category:** `World`. `log_level world trace` logs every structural change (with the system that caused it).

## 10. Mapping from today's code
| Today (`src/...`) | Becomes |
|---|---|
| `Sage.Engine/ECS/EntityContext.cs` (singleton, random `long` ids, `EntitiesDict`, string-keyed groups) | `World` (per-world, `EntityRef` + `PersistentId`, no public mutable dictionaries: fixes TODO #15, #28's replacement, #38) |
| `Sage.Engine/ECS/Entity.cs` (class with `Dictionary<Type, IComponent>`) | `EntityRef` + world storage. `getComponent<T>` → `World.Get<T>`; `hasComponent(string)` → `Has<T>` |
| `Sage.Engine/ECS/Listeners/EntityContextListener.cs` + `ArchetypeView.cs` | Cached queries (membership) + structural notifications (04) for enter/exit edges |
| `Sage.Engine/ECS/Interfaces/IComponent.cs`, `IComponentSystem.cs`, `IEngineSystem.cs` | Struct components (a marker interface is optional depending on the storage choice); `ISystem` |
| `Sage.Engine/ECS/Components/ComponentTransform.cs` (System.Numerics; `LookAt`/`Billboard` methods) | `Transform` data + `TransformMath` static helpers; `GlobalTransform` computed by propagation |
| `Sage.Client/Rendering/ComponentMeshRenderer.cs` (holds a MonoGame `Model`, so it lives in the client for now) | `MeshRenderer { AssetPath Mesh; RecordId Material; }` and `SpriteRenderer { AssetPath Sheet; RecordId Material; … }` (06). A MonoGame type can't live in simulation (01 §3.1). `AssetPath` is an unloaded, interned path (05), so the simulation never loads render data; the client resolves it to GPU resources |
| (the `TransfomSystem.cs` stub, deleted 2026-09-22) | `TransformPropagationSystem` |
| `Sage.Client/Rendering/ModelRendererSystem.cs` | Extract system in the client (06) |

## 11. v1 scope vs later
- **v1:**
  - the Friflo spike and decision;
  - `World`, `EntityRef`, `PersistentId`, components, cached queries, `CommandBuffer`, resources;
  - Fixed/Frame schedules with phases and ordering;
  - access declarations (validation only);
  - `Transform`/`SectorCoord`/`GlobalTransform` + propagation;
  - hierarchy;
  - dev commands.
- **Later:** parallel system execution from the access declarations; several active worlds; per-system profiling in the editor.

## 12. Multiplayer-later notes
- Components stay data (readiness rule 3). `EntityRef` never crosses the network; a `NetId` component is added then.
- Systems will gain a `Server`/`Client`/`Shared` tag. Today every system is implicitly `Shared`.

## 13. Open questions
- Does Friflo's own JSON serializer satisfy E7, or does it conflict with our generated serializer? The spike decides (see 09).
- Should `Get<T>` in `Shipping` throw instead of returning a dummy? Current choice: don't crash players' games over a gameplay bug. It's logged loudly instead.

## 14. Build steps
1. Friflo spike against E1–E9, with a written verdict (ARCHITECTURE D4; TODO R5).
2. `World` + `EntityRef` + resources; remove the static singletons (TODO R1, #15).
3. Schedules, phases, `ISystem`, `CommandBuffer` (TODO R2, R4).
4. `Transform`/`SectorCoord`/`GlobalTransform` + `TransformPropagationSystem`; move `LookAt`/`Billboard` to `TransformMath` (TODO #35, R4, R6).
5. Replace string-keyed groups and `ArchetypeView` with typed queries (TODO #38).
