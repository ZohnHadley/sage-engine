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

- **If Friflo passes** (it did, see the verdict below): `World` wraps a Friflo `EntityStore` **thinly**. It owns lifetime, ids, spawning, resources and schedules, but **queries expose Friflo's own query types** rather than re-wrapping them, so we keep its performance. *Superseded by issue #25 (§3.1a): the types are Sage's now, zero-cost structs over Friflo's, so the performance is kept and Friflo stays out of every public signature.*
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
- **Naming.** Friflo's `Entity`/`IComponent`/`EntityStore` replaced Sage's old classes of the same names; our old `Entity` class and `IComponent` interface were deleted. Since issue #25 the names are Sage's again, over Friflo (§3.1a).

### 3.1a As built: Sage's own vocabulary over Friflo (issue #25, 2026-09-29)

Friflo was in every public signature — `Entity`, `IComponent`, `ArchetypeQuery`, `CommandBuffer`,
`Tags`, `EntityName` — and games had a global `using Friflo.Engine.ECS` plus a `Transform` alias to beat
Friflo's own `Transform`. So Friflo 3.6's API was every game's contract (REDESIGN §3.5). Now
`Sage.Simulation` owns the vocabulary, in `src/Sage.Simulation/ECS/Api`:

| Sage | Over Friflo's | Notes |
|---|---|---|
| `Entity` | `Entity` | One-field struct; the component, tag and hierarchy calls forward. `Name` is a string (Friflo's `EntityName` component underneath); `Components` lists them for tools |
| `IComponent`, `ITag` | `IComponent`, `ITag` | Sage's **extend** Friflo's (below) |
| `Query`, `Query<T1..T5>` | `ArchetypeQuery…` | `Chunks` (`Chunk<T>.Span`, `ChunkEntities.EntityAt`), `Entities`, `Count`, `AllTags`/`AnyTags`/`WithoutAllTags`/`WithoutAnyTags`, `WithoutComponent<T>()` |
| `Tags` | `Tags` | `Tags.Get<T…>()`, `Has`, `HasAll`, `HasAny`; enumerates tag types |
| `EntityCommands` | `CommandBuffer` | `world.Commands` / `ctx.Commands`: `Add`, `Remove`, `AddTag`, `RemoveTag`, `SetParent`, `Destroy` |

- **Zero cost.** Each type is a struct holding the Friflo value (or, for a query, the reference) and
  forwarding to it, inlined; the chunk loop is the same loop. Measured with the scale run of
  [scale-2026-09-24](../history/scale-2026-09-24.md) (Sandbox + `scale_spawn 2000 tree`, Development,
  Linux, 4 CPUs), before and after built side by side and run interleaved, medians of five runs:

  | | before | after |
  |---|---|---|
  | Fixed.TransformPropagation | 0.011 ms | 0.011 ms |
  | Fixed.PrePhysics (CharacterMovementSystem) | 0.012 ms | 0.014 ms |
  | Fixed.Gameplay | 0.015 ms | 0.015 ms |
  | Fixed.Physics | 0.099 ms | 0.101 ms |
  | Frame.Extract | 0.289 ms | 0.284 ms |
  | `scale_spawn 2000 tree`, cold / warm | 19 / 11 ms | 18.5 / 10 ms |

  Every difference is inside the run-to-run spread, and the allocation tests (a whole tick allocates
  nothing) pass unchanged.
- **Why Sage's `IComponent` extends Friflo's.** Friflo finds components by scanning loaded assemblies for
  its own interface, and every generic call into it is constrained on it. A marker that did not extend
  it would need a boxing or reflective bridge on every call. The cost: Friflo's assembly is a
  compile-time reference of every game (a derived interface needs its base to compile), so "Friflo is
  private" is enforced as *nobody names it*: no `Friflo.*` type in a public signature of the base
  (test: NoPublicTypeInTheBaseEngineExposesFriflo) (test: NoPublicTypeInTheClientExposesFriflo), and
  SAGE0050 for a Friflo type or namespace written anywhere but `Sage.Simulation`
  (test: NamingFrifloOutsideTheEcsImplementationIsABuildError). The schema scan still finds the same 45
  components and 7 tags for the Sandbox (test: TheEcsSchemaHasTheComponentsOfEveryBaseAssembly).
- **Inside `Sage.Simulation`**, the implementation (`World`, `ComponentSchema`, the save serializer,
  `EcsSchema`) names Friflo as `F.` (`using F = Friflo.Engine.ECS;`). `ComponentSchema` answers in
  `System.Type` (`TryComponent`, `TryResolveComponent`, `Add`, `Write`, `Read`, `AddTag`) rather than
  Friflo's `ComponentType`/`TagType`.
- **Three "commands", three names:** `EntityCommands` (this), `ConsoleCommand` (the console;
  `WorldConsoleCommands` and `ScaleConsoleCommands` register them) and `PlayerCommand` (input).
- **Unchanged:** component ids, saves (the golden saves load), prefabs, the registry dump, the JSON Schemas.
- `tools/migrate_ecs_api.py` rewrites code written against the old names (idempotent; it is how the
  engine and games were migrated).

The rest of this doc is written against the `World` API.

### 3.2 Components
- **Plain data**, preferably structs. No methods beyond small pure helpers, no references to services, no `Entity` object references. Use `EntityRef` for links to other entities.
- One component type per entity (archetype storage enforces it; `World.Add` refuses a second one; review item #13).
- **Tags** (`ITag` structs, stored in the archetype, not as components) are for rarely changing classification (`IsPlayer`, `Static`). State that flips often (stunned, burning, open/closed) is a **field**, because adding or removing a component moves the entity between archetypes (Mertens).
- Math helpers such as `LookAt`/`Billboard` live in the static `TransformMath` class (done in step 3). The component stays data (TODO R4).
- **Names** are `entity.Name`, a string (`World.Create("bunny")` sets it); underneath it is Friflo's built-in `EntityName` component.

### As built (component ids, issue #16, 2026-09-28)
- **Every component and tag has a stable id**, `[Component("sage:ai_state")]` / `[Tag("sage:player_controlled")]`:
  `namespace:name` in the record id alphabet, matched exactly (case-sensitively). It is what a prefab
  writes, what a save is keyed by (09 "As built (stable ids and versions)") and what `ent_dump`,
  `ent_types` and the editor's inspector show. Before this a component was its bare C# type name,
  matched ignoring case, so two assemblies that each declared `Health` silently overwrote each other.
  Every engine and game component has one (test: EveryEngineAndGameComponentHasAStableId).
- **Sage.Generators writes each assembly's id table** (`ComponentGenerator`, an `IGeneratedComponents`
  marked on the assembly), which `ComponentSchema` reads for every assembly in Friflo's schema. An
  `IComponent` or `ITag` struct without an id is a build error, `SAGE0004`; a malformed id, a `Version`
  below 1 or the wrong interface is `SAGE0005`; two types with one id in an assembly, `SAGE0006`; a bad
  `[Upgrade]` method, `SAGE0007` (tests: AHalfDeclaredComponentIsABuildError,
  TwoComponentsWithOneIdAreABuildError, TheGeneratorWritesTheAssemblysIdTable).
- **Components are per assembly, not per plugin.** A component type is in Friflo's schema whether or not
  its plugin is loaded, so its id is too; nothing is registered. Two types claiming one id across
  assemblies (two mods) stops the engine the first time the schema is used, naming both (test:
  TwoTypesClaimingOneIdIsAnErrorNamingBoth). An assembly built without the generator (tests, tools) is
  read by reflection over the same attributes (test: ComponentTypesFromOtherAssembliesAreFoundById).
- **Friflo's own components** (`EntityName`, `TreeNode`, `UniqueEntity`, `Position`, `Rotation`,
  `Scale3`, its own `Transform`, the `Disabled` tag) share the schema but have no id: content cannot
  name them, saves do not write them, and Friflo's `Transform` no longer collides with Sage's by name
  (test: EveryComponentOnAnEntityCanBeFoundById).
- **Bare names in a prefab** resolve in the prefab's own namespace, then in `sage` — so a game's files
  write `"hop"` and `"collider"`, and a game component deliberately shadows an engine one of the same name
  (test: ABareNameMeansTheFilesNamespaceThenSage). Anything else is written in full. One trap: `base`
  inheritance merges prefab JSON by key *before* names are resolved, so a derived prefab that patches
  one field of a base's component must spell the key as the base does (`"ai_state"` both times, not
  `"sage:ai_state"` in one), or its entry replaces the base's whole component instead of merging. A name that
  resolves nowhere is an error listing ids it might have meant in other namespaces, and one spelt the
  pre-#16 way (a C# type name, with capitals) is an error naming the id to write — a load error rather
  than a deprecation warning, because every shipped file was migrated with the change and a warning
  that still applied the component would keep the old spelling alive (test:
  AnOldTypeNameOrAMissingNameSaysWhatToWrite).

### 3.3 Entity identity
| Type | What | Lifetime |
|---|---|---|
| `EntityRef` | Runtime handle. **Realised as Sage's `Entity` struct** over Friflo's (store + id + revision; issue #25); code uses the type `Entity`. `IsNull` / `World.IsAlive` detect deleted entities | One world, one session |
| `PersistentId` | 128-bit id (GUID) stored in a `Persistent` component | Forever. Assigned when the entity is placed in a map, or when a runtime-spawned entity is marked persistent (dropped items, recruited NPCs). Entities without it are never saved (09 §3.5) |

`World.Resolve(PersistentId)` maps back to an `EntityRef` through an index maintained by structural notifications.

### 3.4 Resources (per-world singletons)
State that isn't per-entity lives in world **resources**, the equivalent of Overwatch's singleton components: `GameRules`, the physics space, the current `TickTime`, the origin sector, the event queues (04). Accessed as `world.Resources.Get<T>()`. No static singletons (TODO R1).

**Core or plugin (issue #13).** A world comes with only what every game needs: the origin (the frame every absolute position converts through), the event queues, records, `DebugDraw`, `MessageLog`, `Weather` and `RenderEnvironment`. Everything else is installed by the plugin it belongs to: `PhysicsSpace` by `sage.physics3d`, `Terrain` by `sage.streaming`, `PlayerInput` and `ActiveCamera` by `sage.gameplay.character` (and the camera by the client too, through `Resources.GetOrAdd`, since either may be loaded without the other). A game from `"plugins": []` has none of them, ticks, and answers console commands (test: NoPluginsAtAllIsAGameWithNoPhysicsAndNoGameplay); CI runs one in the real host (`tests/games/no-plugins`).

### 3.5 Systems and schedules

- A system is a class implementing `ISystem`, registered into a **schedule** and **phase**:
  - `Schedule.Fixed`, at `sim_tickrate` (01 §5.2): `Commands → PrePhysics → Physics → PostPhysics → Gameplay → AI → Animation → EntityIO → Late`.
  - `Schedule.Frame`, once per rendered frame: `FrameUpdate → Extract → Render → Overlay`.
- Ordering within a phase: `Before`/`After` constraints naming other systems **by id** (a stable topological sort; a cycle throws at registration). Otherwise registration order applies, deterministically. *(Built in step 4 with type constraints; ids since issue #17, see "As built (declared systems)" below. "System sets" were dropped for now.)*
- **Access declarations** (components read/written, game events read/sent, 04 §4) are **not built yet**. The event bus (R13) landed without them — a system asks for its readers in its constructor — so they are now purely the parallel scheduler's concern. What a phase *guarantees* is covered instead by contracts (below, R16), which watch the value rather than trust a declaration. v1 runs systems sequentially.
- **Run conditions:** `Default` (= `WhenNotPaused` in Fixed phases, `Always` in Frame phases), `WhenNotPaused`, `Always`, `DevOnly`. `World.Paused` (console `pause`) skips `WhenNotPaused` systems; ticks still run. Since issue #283 the flag is the world's `WorldTime.Paused`, and a slowed or hit-stopped world's real ticks with no step due skip them the same way (held passes, 01 §5.2): `Always` is the declared opt-out for a system that runs on real time.
- **Command buffer flush points:** the end of every phase. Structural changes made during a phase become visible in the next phase.
- Every phase and system is wrapped in a profiler scope (`Fixed.Gameplay`, `Fixed.Gameplay/FaceCameraSystem`), shown by `stat frame` and `sys_list`.
- **Steady state allocates nothing**: a test runs 100 ticks + frames with systems and measures 0 bytes.

### As built (declared systems, 2026-09-28 — issue #17)

A system was added with `world.AddSystem(new X(...), Phase.Y, before: new[] { typeof(Z) })`: no stable
id, constraints that named CLR types from other modules, a constraint naming a system in another phase
silently ignored, and `RemoveSystem` leaving the system's event readers registered, so its queues were
held until `ev_maxage` dropped them with a warning naming a system that no longer existed. Now
(`src/Sage.Simulation/ECS/Systems/SystemDeclarations.cs`, `SystemScheduler.cs`, `WorldSystems.cs`):

```csharp
[System("sage.ai.think", Phase.Commands, After = new[] { "sage.character.player_control" })]
public sealed class AIThinkSystem : ISystem { … }

world.AddSystem(new AIThinkSystem(world, records, tasks, actions));   // id, phase and order from the attribute
```

- **The attribute declares; the module constructs.** Systems take cvars, the renderer and records
  through their constructors, which generated code could not supply, so modules keep building them and
  `AddSystem(system)` reads the id, phase, `Before`/`After` and `Condition` from `[System]` (test:
  ADeclaredSystemTakesItsIdPhaseAndOrderFromTheAttribute). What Sage.Generators emits is the list of each
  assembly's declared system types (`IGeneratedSystems`), so the engine knows every id a loaded assembly
  declares, including systems nobody added to this world; an assembly built without the generator (the
  test assembly) is read by reflection instead. An undeclared system — a test's probe — is added with
  an explicit phase (`AddSystem(system, Phase.Late)`) and may be given an id by hand; a declared one
  cannot be added that way, so its phase is written once (test:
  OneIdOncePerWorld_AndADeclaredSystemCannotBeAddedAsUndeclared).
- **Every engine, client and Sandbox system is declared** (ids `sage.<area>.<name>`, `sandbox.<name>`),
  and each was added by its own plugin (test: EverySystemInTheSandboxIsDeclaredAndOwned). The three
  client systems added only sometimes (map meshes when `sage.maps` is loaded, dialogue when the
  factions plugin is, asset reload when a watcher exists) still are.
- **A constraint across phases is an error**, thrown when the system is added — against a system
  already in the world, or against the phase the catalog says an absent one is declared in (test:
  AConstraintAcrossPhasesIsAnError); between two systems of one assembly it is a build error,
  `SAGE0013` (test: ASystemOrderedAgainstOneInAnotherPhaseIsACompileError).
- **An id nobody declares** — no loaded assembly has it — is a typo or a plugin that is not installed:
  an exception in dev builds, logged and ignored in Shipping. `"?id"` marks a soft dependency on a
  plugin that may be absent (test: AConstraintNamingAnIdNothingDeclaresIsAnErrorUnlessMarkedOptional).
  An id that is declared but not in this world is no constraint: its plugin is turned off, or its module
  added it conditionally (test: AConstraintNamingADeclaredSystemThatIsNotHereIsNoConstraint). Checking
  against what is *declared* rather than what is *added* is what makes this decidable when each system
  is added, whatever order plugins add them in.
- **Removing a system releases what it holds.** `world.Events.Reader<T>(this)` records the system as the
  reader's owner; `RemoveSystem` releases every reader it owns and disposes it if it is `IDisposable`
  (and a world disposes its systems) (test: RemovingASystemReleasesItsEventReadersAndDisposesIt).
- **`world.Systems.Replace(id, system)` and `Disable(id)`** let a plugin change a system it did not add.
  A replacement takes the slot — id, phase, order, run condition — and the old system is retired as by
  `RemoveSystem` (test: ReplaceKeepsTheSlotAndRetiresTheOldSystem). A disabled system keeps its slot, so
  constraints naming it still hold, never runs, and has its readers released; `sys_toggle` will not
  turn it back on (test: DisableTurnsASystemOffForGoodAndLetsGoOfItsReaders). Both are logged, and
  recorded in the `RegistrationLedger`, against the plugin whose code called them (test:
  ReplaceAndDisableAreRecordedAgainstThePluginThatDidThem).
- **`sys_list`** prints each system's phase, average time, id, type and owning plugin, with who
  replaced or disabled it; `sys_toggle` takes an id or a type name.
- **Not done here:** generated construction, access declarations (below), and system *sets*. Replace
  and Disable act on one world; a plugin that wants it everywhere does it in every `OnWorldCreated`.

### As built (deferred work and phase contracts, 2026-09-23 — R14, R16)

Both came out of the engine review (items 2 and 7).

**`Deferred<T>`** (`src/Sage.Simulation/ECS/Deferred.cs`). Touching another entity's components inside a
query throws, so a system that reacts to what its loop found has to collect the work and run it
afterwards. Three features had grown the same four lines to do it: a `List<T>` field, `Clear()` at
the top of `Run`, `Add` in the loop, `foreach` after.

The four lines were not the problem — the `Clear()` was. Forget it and last tick's work runs again,
which reads as a gameplay bug rather than a bookkeeping one. `Deferred<T>.Drain()` hands over
everything queued *and empties the queue in one step*, so there is no clear to forget and no way to
run the same work twice. Work added while draining waits for the next drain, which is the difference
between "runs later" and "runs forever". Melee hits, interactions and deaths all use it.

It is not `EntityCommands`: that defers *structural* changes and the world plays them back at the end
of every phase. This defers a system's own work, with its own data, to a point the system chooses.

**`PhaseContracts`** (`src/Sage.Simulation/ECS/Systems/PhaseContracts.cs`). `before:`/`after:` fixes the
order systems run in; what it cannot express is the promise downstream code depends on — "by the end
of Commands, `PawnIntent` is what this tick will act on". That lived in a comment, and review #48 is
what it cost: `AIThinkSystem` wrote intent in the AI phase while `CharacterMovementSystem` consumed
it in PrePhysics, so every creature acted a tick late, and the comment beside it claimed the
opposite. A code review and a docs audit both missed it because both read the comment.

```csharp
world.Contracts.FinalAfter<PawnIntent>(Phase.Commands);   // CharacterModule declares this
```

In a dev build the world copies every instance at the end of that phase and compares after each later
Fixed phase, reporting the entity and the **phase that wrote it**. `Contracts.Violations` is a count a
test can assert is zero — which is the point: `NothingWritesPawnIntentAfterTheCommandsPhase` in
`AITests` fails if `AIThinkSystem` moves back to `Phase.AI`, verified by doing exactly that.

Costs a copy per guarded component per entity per tick, and nothing in Shipping or with no contracts
declared. It is opt-in per component on purpose: guard the small things others build on (an intent, a
command), not a transform half the engine writes by design. A component that appears mid-tick has no
baseline and is not reported.

**Still prose:** "transforms are settled after PostPhysics" — `Transform` is written all over the
place by design, so guarding it needs a contract that says *which* systems may, which is the access
declaration that comes with the parallel scheduler.

### 3.6 Transform hierarchy and large-world coordinates

**Directions and angles (`SageMath`, one convention for the whole engine).** +Y is up. An entity's
front is its local **-Z** (`TransformMath.Forward`), as in MonoGame. **Yaw** is the rotation about +Y
in radians: 0 faces -Z, positive turns counter-clockwise seen from above (the sense of
`Quaternion.CreateFromYawPitchRoll`), so +90° faces -X. **Pitch** is positive looking up.
`PawnIntent.Yaw`, `PlayerCommand.ViewYaw`, a placement's `yaw`, AI steering and sprite direction
groups all mean this same angle, and `SageMath` owns the helpers that convert between a yaw, a
direction and a rotation (`ForwardFromYaw`, `RotationFromYaw`, `YawOf`, `YawTo`, `WrapPi`, `WrapTau`,
`TurnToward`, `InCone`). Subsystems keeping private copies of those helpers is how the sprite system
came to read +Z as "front" while everything else read -Z (review #43): don't.

| Component | Fields | Written by |
|---|---|---|
| `Transform` | `Vector3 LocalPosition`, `Quaternion LocalRotation`, `Vector3 LocalScale` (relative to the parent, or to the entity's sector if it's a root) | gameplay, physics sync |
| `SectorCoord` | `int X, Z`: a 1024 m sector of the world, keyed absolutely (14). **As built (2026-09-24, R6):** roots are relative to the world's **origin sector** rather than carrying a sector each — `Origin` holds it for the whole world and `world.Rebase` moves everything at once, which is cheaper than a per-entity sector and gives the same precision. A per-entity `SectorCoord` returns if entities ever need to exist outside the loaded rings | `Origin`, streaming |
| `Parent` / `Children` | Friflo's built-in hierarchy, as `Entity.Parent` and `Entity.ChildEntities` | `World.SetParent` / `ClearParent` only |
| `GlobalTransform` | `Pose Current`, `Pose Previous` (a `Pose` is position + rotation + scale), relative to the world's **origin sector** (today: the origin) | transform propagation only |

- **Transform propagation** (built in step 4, `ECS/Systems/TransformPropagation.cs`) is run by `World` itself at the end of `PostPhysics` and `Late`, not registered as a user system. It walks roots → children and computes `GlobalTransform.Current` (relative to `world.Resources.Get<Origin>().Sector` once sectors exist). At the start of each tick, before `Commands`, `Current` is copied to `Previous`, so rendering can interpolate (06). This replaced the old stub `TransformSystem` (TODO #35).
- `World.Create` gives every entity a `GlobalTransform` at its spawn pose (so it doesn't slide in from the origin), and `World.Teleport` moves a root without interpolating the jump.
- Poses are stored as position/rotation/scale rather than matrices, because rendering interpolates them (lerp position and scale, slerp rotation). Interpolating matrices component-wise distorts rotations. Matrices are built at extract time.
- When the origin sector changes (14), all `GlobalTransform` positions (including `Previous`) are shifted by the same offset, so interpolation doesn't jump.

## 4. Public API sketch

Built in steps 3 and 4 (`src/Sage.Simulation/ECS/World.cs`, `ECS/Systems/*`).
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

    // Entities (Sage `Entity` handles, issue #25)
    public Entity Create(string? name = null);                    // identity Transform + GlobalTransform (+ its Name)
    public Entity Create(in Transform transform, string? name = null);
    public void Destroy(Entity e);                                // immediate; inside a query loop use Commands
    public bool IsAlive(Entity e);                                // false for deleted/stale handles and other worlds' entities
    public Entity Resolve(PersistentId id);                       // default (IsNull) if not loaded
    public PersistentId MakePersistent(Entity e);
    public void Teleport(Entity e, in Transform t);               // move a root without interpolating the jump

    // Components
    public ref T Get<T>(Entity e) where T : struct, IComponent;   // missing: Ensure, then throws (every build, since #31)
    public bool TryGet<T>(Entity e, out T value) where T : struct, IComponent;
    public bool Has<T>(Entity e) where T : struct, IComponent;
    public bool Add<T>(Entity e, in T component) where T : struct, IComponent;   // false (+ Ensure) if already present
    public bool Remove<T>(Entity e) where T : struct, IComponent;                // false, no side effects, if absent

    // Hierarchy
    public void SetParent(Entity child, Entity parent);
    public void ClearParent(Entity child);

    // Queries: Sage structs over Friflo's (§3.1a). Cache them; iterate .Chunks in hot paths.
    public Query<T1> Query<T1>();   /* up to <T1,T2,T3,T4,T5> */
    public Query QueryAll();

    // Deferred structural changes (over Friflo's CommandBuffer, reused). Applied at the end of every phase.
    public EntityCommands Commands { get; }
    public void FlushCommands();

    // Structural notifications (04 §3.3), immediate, on the world's thread.
    public event Action<Entity>? EntitySpawned;
    public event Action<Entity, Type>? ComponentAdded, ComponentRemoved;
    public event Action<Entity>? EntityDestroyed;

    // Scheduling
    public SystemInfo AddSystem(ISystem system, RunCondition? condition = null);   // declared: [System(id, phase)]
    public SystemInfo AddSystem(ISystem system, Phase phase, RunCondition condition = RunCondition.Default,
                                string? id = null, string[]? before = null, string[]? after = null);   // undeclared
    public bool RemoveSystem(ISystem system);                     // releases its event readers, disposes it
    public WorldSystems Systems { get; }                          // id, phase, owner, Enabled; Replace(id, s), Disable(id)
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
    public EntityCommands Commands { get; }    // structural changes, applied at the end of the phase
}

// EntityCommands: Add<T>(e, c), Remove<T>(e), AddTag<T>(e), RemoveTag<T>(e), SetParent(child, parent),
// Destroy(e). Prefab spawning (`Spawn(RecordId prefab, …)`) is added on top with records (05, step 5).
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
Create ──► EntitySpawned ──► ComponentAdded (Transform, GlobalTransform, the name, …) ──► visible to queries
Destroy ─► ComponentRemoved for each component ──► EntityDestroyed ──► slot freed; old handles report IsNull
(Commands.* record instead; the same notifications fire at FlushCommands.)
```
World creation: resources installed by modules (physics space, origin, event queues) → `IGameModule.OnWorldCreated` loads the map → `IGameModule.CreateRules` supplies `GameRules` (issue #13). Resources go in with `Resources.Add`, which refuses a second of one type, or `Replace`, which swaps one on purpose and disposes the old (test: InstallingTheSameResourceTwiceIsAnError). World destruction: all entities destroyed (notifications suppressed; one `WorldDestroying` signal instead), then resources disposed in reverse order.

## 6. Threading and memory
- Worlds are single-threaded in v1 (main thread). Systems may use jobs over **copied input spans**, writing results that a later system applies. Jobs never touch world structure.
- Queries, command buffers and hierarchy walks don't allocate in steady state. Command buffers reuse their backing arrays. `Children` lists are pooled.
- The `PersistentId` index is a `Dictionary<PersistentId, EntityRef>` updated only by structural notifications.

## 7. File formats
None of its own. Prefabs, maps and saves use the serializer (09); prefab definitions are records (05).

## 8. Errors and fallbacks
- `Get<T>` on a missing component: `Assert.Ensure` fails (logged once per call site with the entity's name and `PersistentId`), then it throws, in every configuration. Until issue #31 `Shipping` returned a shared zeroed dummy rather than crash the game, but a write through it went nowhere and the next caller read the last one's leftovers (REDESIGN §3.5); code that may meet an entity without the component asks with `TryGet` or `Has` (test: Get_ReturnsRef_MissingComponentThrowsInEveryBuild).
- Using a stale `EntityRef`: `IsAlive` is false. `Get` behaves like the missing-component case.
- Structural change inside a query without a command buffer: Friflo throws immediately (all builds).
- Adding a component type twice: `Ensure` failure; `Add` returns false and the existing data is kept.
- Removing a component the entity doesn't have: `Remove` returns false with no side effects (review #33).

## 9. Debug and tooling hooks
- **Commands:**
  - `ent_list [filter]` (**built, step 3**; registered by the host for the main world)
  - `ent_dump <id|name>` (all components via the generated inspector metadata) — **built**, by
    reflection for now; components are printed by stable id, and `ent_types` lists the ids (issue #16)
  - `ent_kill`
  - `sys_list` (systems per world and phase with average ms; ids and owning plugins since issue #17) — **built, step 4**
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
| `ComponentMeshRenderer` class → `ModelRenderer` struct (step 3) → **`MeshRenderer { AssetPath Mesh; RecordId Material; byte Layer; }` in Sage.Simulation (done, step 6)** | `MeshRenderer` done; and `SpriteRenderer { AssetPath Sheet; RecordId Material; … }` (06). A MonoGame type can't live in simulation (01 §3.1). `AssetPath` is an unloaded, interned path (05), so the simulation never loads render data; the client resolves it to GPU resources |
| (the `TransfomSystem.cs` stub, deleted 2026-09-22) | **Done (step 4):** transform propagation, run by `World` after `PostPhysics` and `Late` |
| `Sage.Client/Rendering/ModelRendererSystem.cs` | **Done (step 6):** `CameraExtract` + `MeshExtract` + `RenderSystem` (06 §3.11). Every `World` then also had `RenderEnvironment`, `PlayerInput`, `Terrain` (14) and — with the physics module — `PhysicsSpace` (10) resources, next to `ActiveCamera`; since issue #13 only `RenderEnvironment` is core, the rest come from their plugins (§3.4) |

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
- ~~Should `Get<T>` in `Shipping` throw instead of returning a dummy?~~ Decided in #31: it throws in every build (above).

## 14. Build steps
1. ✓ Friflo spike against E1–E9, with a written verdict (§3.1; ARCHITECTURE D4; TODO R5).
2. ✓ `World` + handles + resources + `Engine`; static singletons removed (TODO R1, #15).
3. ✓ Schedules, phases, `ISystem`, per-phase `CommandBuffer` flushing (step 4; TODO R2, R4).
4. ✓ `GlobalTransform` + propagation (step 4). `SectorCoord` + origin rebasing with TODO R6. (✓ `Transform` struct + `TransformMath` done in step 3.)
5. ✓ Typed queries replace string-keyed groups and `ArchetypeView` (TODO #38).
