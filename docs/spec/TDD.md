# Sage · Technical Design Document

> Version 1, 2026-10-02. The engine's architectural blueprint: how the assemblies, the loop, memory,
> threads and subsystems fit together, as the code stands today, with what is planned marked as planned.
> Requirements are in [SRS.md](SRS.md); this document must agree with it.

## 1. Purpose, audience and related documents

This document is for engine developers, kit authors and reviewers who need to know **how Sage is built**
before they change it. It describes the structure that every subsystem shares (boot, loop, ECS,
communication, content, presentation, threads, memory, persistence, diagnostics, tests) and names the
real types and files that implement it.

| Document | What it answers |
|---|---|
| [SRS.md](SRS.md) | What the engine must do and how well (goals, platforms, performance, quality, scope). |
| **TDD.md** (this) | How the engine is built: architecture, data flow, threading, memory. |
| [API.md](API.md) | The contract game and kit code programs against. |
| [Subsystem sheets](subsystems/README.md) | One sheet per subsystem with numbered requirements, status and open issues. |
| [`../design/`](../design/00-index.md) | The long narrative design of each subsystem, with research and history. |
| [`../../ARCHITECTURE.md`](../../ARCHITECTURE.md) | The original overview and the D1–D13 decisions (§8). |
| [`../REDESIGN.md`](../REDESIGN.md) | The plan: target architecture (§3), roadmap (§5), owner decisions (§6). |

Where this document and the code disagree, the code is right and this document has a bug.

## 2. Architectural overview

### 2.1 Layers and allowed references

Sage is split into assemblies by layer, with one namespace per assembly (`Sage.Core`, `Sage.Simulation`,
and so on). References point down only.

```
  games / mods        MyGame (sim) . MyGame.Client . data mods (mod.json)
        |
  kits (optional)     Sage.Kits.Rpg  <-  Sage.Kits.Rpg.Client
        |                    |                  |
  ----- base engine, headless (SageSimulationOnly, no MonoGame) -------------------------
        |
        Sage.Gameplay      attributes, effects, combat, items, AI, factions, quests, dialogue;
        |   \              BasePlugins.All() (the one list of base plugins)
        |    Sage.UI       retained widgets, layout, focus, ui_* records (on Simulation)
        |   /
        Sage.Physics3D     Bepu space, character movement, behind IPhysicsWorld
        |
        Sage.Simulation    Engine, World/ECS facade, SageApp, HostLoop, scheduler, events, entity I/O,
        |                  prefabs, scenes, saves, streaming, animation, audio mixer, render data
        Sage.Core          cvars/console, Log, diagnostics, VFS, records, plugins, declarations, metadata
        |
        Sage.Editing       the editor's headless model: documents, commands, undo (on Simulation)
  ----- MonoGame side -----------------------------------------------------------------
        Sage.Client        renderer and passes, ContentService, input devices, audio backend, UI drawing
        Sage.Editor        ImGui dev tools and editor panels (on Client + Editing)
        Sage.Host          the game executable (on Client + Editor; Shipping strips the editor)
  ----- tools -------------------------------------------------------------------------
        Sage.Cli           `sage validate | schema | mods`, headless (on Gameplay)
        Sage.Generators    source generators and analyzers (referenced as an analyzer by all)
        Sage.Testing       HeadlessApp, MountFixture, EventProbe (tests and games)
        sdk/               Sage.Sdk (MSBuild SDK), Sage.Player, Sage.Templates
```

`Sage.Gameplay` references `Sage.UI` only because `BasePlugins.All()` (`src/Sage.Gameplay/BasePlugins.cs`)
names `UiModule`; `Sage.Client` references both. The base engine (Core, Simulation, Physics3D, Gameplay,
UI, Editing, plus Client and Editor for the kit rule) never references a kit. The reasons Engine, World and
`SageApp` live in Simulation rather than Core are in REDESIGN §3.1 (the status note for #24).

### 2.2 How the boundary is enforced

| Rule | Mechanism |
|---|---|
| No MonoGame type in a simulation assembly | SAGE0024, `SimulationOnlyAnalyzer.cs`, driven by `<SageSimulationOnly>` (default true in `src/Directory.Build.props`; Client, Editor and Host opt out). |
| No kit in the base | SAGE0025, `KitReferenceAnalyzer.cs`, driven by `<SageBaseAssembly>` (test: ABaseAssemblyThatUsesTheRealRpgKitIsABuildError). |
| Friflo is an implementation detail | SAGE0050, `EcsVocabularyAnalyzer.cs`; only `Sage.Simulation` sets `<SageEcsImplementation>` (test: NamingFrifloOutsideTheEcsImplementationIsABuildError). |
| No layer references the one above it | Reflection test over the built assemblies (test: NoBaseAssemblyReferencesALayerAboveIt). |
| Registration only in `Init` | SAGE0020, `RegistrationStageAnalyzer.cs` (test: RegisteringACvarInStartIsABuildError). |
| Declared public API | `Microsoft.CodeAnalysis.PublicApiAnalyzers`, RS0016/RS0017 as errors; `PublicAPI.*.txt` per base assembly and both kit halves ([`../RELEASING.md`](../RELEASING.md)). |
| Warnings are errors | Solution-wide; suppression only with a scoped `#pragma` and a reason. |

The full list of SAGE diagnostics is MAKING_A_GAME §10a; unstable areas carry `[Experimental("SAGE01xx")]`
ids listed in MAKING_A_GAME §10b ([`../MAKING_A_GAME.md`](../MAKING_A_GAME.md)).

## 3. Platform abstraction

Sage does not talk to an operating system or graphics API directly. MonoGame DesktopGL 3.8.5.1 is the
platform layer (decision D13), and the simulation sees none of it.

| Concern | MonoGame provides | Sage wraps it in | Headless replacement |
|---|---|---|---|
| Window, message pump, frame callbacks | `Game`, `GraphicsDeviceManager` | `Game1` (`src/Sage.Host/Game1.cs`), which owns a `HostLoop` and sets `IsFixedTimeStep = false` | The test or tool drives `HostLoop` or `World.RunFixed` itself |
| Graphics device | `GraphicsDevice` | `ClientHost` (`src/Sage.Client/ClientModule.cs`), provided to modules before `Start` | None: no client module is added |
| Textures, effects, sounds, fonts | `Texture2D`, `Effect`, `SoundEffect` | `ContentService` reading bytes from the VFS | Records still load; nothing GPU-side exists |
| Keyboard, mouse, gamepad | `Keyboard`, `Mouse`, `GamePad` | `InputDevices` → `InputActions` → `CommandLatch` → `PlayerCommand` | A test writes `PlayerInput.Command` directly |
| Audio output | `SoundEffect` instances | `IAudioBackend` (`src/Sage.Client/Audio/AudioBackend.cs`), fed by the simulation's `AudioMixer` | The mixer still decides every voice; nothing plays |
| Time | `GameTime` | `FixedStepClock`, `TickTime`, `FrameTime` (`src/Sage.Core/Time.cs`) | Same types, advanced by the caller |
| File roots | none | `UserPaths` (writable `user://`) and `VirtualFileSystem` (read-only mounts) | `UserPaths.Initialize(id, overrideRoot)` for tests and tools |

Systems never read `DateTime.Now`, `GameTime` or a device. Everything they need arrives through
`SystemContext` (tick, frame, world) or a world resource, which is what lets the same simulation run in a
window, in `sage validate`, and in a parallel test run. `UserPaths` resolves to `<repo>/user/<gameId>/`
in dev builds and to the platform's local application data folder otherwise. The platform sheet is
[subsystems/01-platform.md](subsystems/01-platform.md).

## 4. Boot sequence and the app

Every host boots through one path, `SageApp` (`src/Sage.Simulation/App/SageApp.cs`), described by
`SageAppOptions`. The game executable, the editor mode, `sage`, and `HeadlessApp` all call it, so tests run
the configuration that ships.

| Stage (`AppStage`) | Method | What happens | Seals |
|---|---|---|---|
| Created | `SageApp.Create` | Cvars and `Engine`; engine content mounted as namespace `sage`; plugins chosen from `AvailablePlugins` per game.json `plugins`, kits loaded (`ModuleManager.LoadKit`), host modules added, the game assembly loaded (before any `World`, because the ECS schema is built from loaded assemblies); plugin content (`[PluginContent]`), game mounts and mods mounted in that order | none |
| Registered | `Register` | Each module's generated registrations, then its `Init`, in dependency order; engine console commands | modules |
| Configured | `Configure` | `config.cfg` executed once every cvar exists | cvars |
| ContentLoaded | `LoadContent` | Records loaded from every mount; mod report written; start scene checked | actions, vocabularies, record types |
| Started | `Start` | Each module's `Start` (records and GPU are available) | render passes (client) |
| Running | `CreateWorld` / `CreateEditWorld` | `Engine.CreateWorld`: each module's `OnWorldCreated`, then `IGameModule.CreateRules` | none |
| Running | `RunLaunchCommands` | `+command` arguments, once a world exists | none |

Each step checks the one before it, so a host that skips or repeats a stage throws at once. Registries
are guarded by `RegistrationSeal` (`src/Sage.Core/RegistrationSeal.cs`): registering a cvar, record
type, action or vocabulary entry after its seal throws, and doing it in `Start`, `OnWorldCreated`,
`CreateRules` or a system is a build error (SAGE0020). `Boot()` walks a headless app through every stage in
one call.

**Plugins.** A plugin is an `IModule` with `[Plugin("id", "version")]` and optional
`[RequiresPlugin("id", range)]` (`src/Sage.Core/Declarations/Plugins.cs`). Its lifecycle is `Init`
(register only), `Start`, `OnWorldCreated(World)`, `Shutdown` (reverse order). `ModuleKind` (Runtime,
Editor, Tool) is enforced against `HostKind`. The plugin id `sage` is the engine itself, and game.json's
`"sage"` range is checked against the MinVer version when the manifest loads.

**Generated registration.** `Sage.Generators` emits, per assembly, a class marked
`[GeneratedRegistrations]` that registers every `[Record]`, `[Component]`/`[Tag]`, `[PrefabPart]`,
`[SavedResource]` and vocabulary entry for its owning plugin. `ModuleManager.InitAll` runs a plugin's
generated registrations immediately before its `Init`. Systems are not registered globally: a `[System]`
declaration feeds the `SystemCatalog`, and a module adds the instance per world with
`world.AddSystem(new X(...))`, which reads id, phase, ordering and run condition from the attribute.

**game.json** (`GameManifest`, `src/Sage.Core/GameManifest.cs`) names the game id (also its record
namespace), version, `sage` range, mounts, assembly and `modules.add`, `plugins`, `modules.disable`,
`kits`, mods folder and start `scene`. The host never guesses a game: without `-game` or a `game/`
folder beside the executable it stops with an error. See [subsystems/03-app-and-loop.md](subsystems/03-app-and-loop.md).

## 5. The main loop

### 5.1 Fixed tick, frames and interpolation

`HostLoop` (`src/Sage.Simulation/App/HostLoop.cs`) is the simulation half of the main loop and contains no
MonoGame. A windowed host calls `Update(realDt, ...)` from `Game.Update` and `Frame()` from `Game.Draw`;
a headless host calls both from its own loop.

1. `FixedStepClock.Advance` clamps the real frame time to `sim_maxframetime` (0.25 s default), adds it to an
   accumulator (unscaled since #283: `host_timescale` goes to each world's `WorldTime.HostScale`), and returns how many whole ticks of `1 / sim_tickrate`
   (60 Hz default) to run, plus `Alpha`, the remainder as a fraction of a tick.
2. For each tick, every world in `Engine.Worlds` runs `World.RunFixed(dt)` in creation order, which its
   `WorldTime` turns into zero or more simulation steps of exactly `dt` (its scale, pause and hit-stop;
   design 01 §5.2). Before each step, the host's `beforeTick` hands the player's world one `PlayerCommand`
   sampled from `CommandLatch`, so a pressed edge belongs to exactly one step. A world created during a tick starts on the next one.
3. `Frame()` runs `World.RunFrame(dt, alpha, realTime)` once per world.

`GlobalTransform` stores the previous and current pose; propagation copies poses at the start of each tick
and recomputes after `PostPhysics` and `Late`. Frame-schedule consumers interpolate with `Alpha`, while
cameras run at display rate. Paused worlds skip Fixed systems whose run condition is the default; Frame
systems keep running. The steady-state loop allocates nothing (test: SteadyStateTicksAndFrames_DoNotAllocate).

### 5.2 Tick boundary

After the phases of `RunFixed`, outside any system: a pending `Travel.To`, then a pending `Time.Pass`, then
`SaveSystem.TickEnded` (requested saves and loads, autosave clock), then `Scenes.TickEnded` (streamed
sectors placed or put to sleep, work that allocates and so never runs inside a phase). An edit world skips
travel, time and saves.

### 5.3 Phases

`Phase` (`src/Sage.Simulation/ECS/Systems/ISystem.cs`) is a fixed enum. Within a phase, systems are ordered
by `Before`/`After` on system ids, then by registration order (`SystemScheduler`); a constraint across
phases is a build error (SAGE0013) and a runtime error. `EntityCommands` are played back at the end of
every phase.

| # | Phase | Schedule | What runs there today (examples of declared ids) |
|---|---|---|---|
| 1 | `Commands` | Fixed | Player control and AI decisions write pawn intent: `sage.character.player_control`, `sage.ai.think`, `sage.ai.offscreen`, `sage.combat.recoil`, camera input, the world clock, quick-save keys. |
| 2 | `PrePhysics` | Fixed | Intent becomes motion and bodies are synced: `sage.character.move`, `sage.physics.sync`, `sage.physics.joints`, `sage.physics.terrain`, `sage.maps.collision`, hit locations. |
| 3 | `Physics` | Fixed | `sage.physics.step` (Bepu `Timestep`). |
| 4 | `PostPhysics` | Fixed | `sage.physics.write_back`, `sage.physics.joint_breaks`, `sage.io.triggers`; then transform propagation. |
| 5 | `Gameplay` | Fixed | Abilities, projectiles, melee, reload, effects and deaths, items, movers, factions, quests, I/O bridges (`sage.io.damage`, `sage.io.deaths`). |
| 6 | `AI` | Fixed | Reserved; no engine system uses it now (AI moved to `Commands` so a decision acts in the same tick). |
| 7 | `Animation` | Fixed | `sage.animation.sprites`, `sage.animation.sprite_graph`, the skeletal animator, viewmodels. |
| 8 | `EntityIO` | Fixed | Logic timers, tweens, state machines, camera blends, then `sage.io.dispatch` delivers queued inputs. |
| 9 | `Late` | Fixed | Aim and foot IK, attachments, sky, streaming (`sage.streaming.sectors`, `sage.streaming.owners`), debug views; then transform propagation. |
| 10 | `FrameUpdate` | Frame | Camera rigs and `CameraDirector`, client audio, particles, weather, terrain and map meshes, asset hot reload. |
| 11 | `Extract` | Frame | `sage.client.extract.camera` first, then meshes, skinned meshes, sprites, lights, particles, debug, viewmodel and pass extracts. |
| 12 | `Render` | Frame | `sage.client.render` draws the snapshot through the pass list. |
| 13 | `Overlay` | Frame | Game UI (`sage.client.ui`), legacy screens, floating text; the dev tools' ImGui layer draws after the frame. |

`RunCondition` adds `Always`, `WhenNotPaused`, `DevOnly`, and the experimental `EvenWhenEditing` for
systems that must mirror state in an edit world. Phase guarantees are asserted in dev builds by
`world.Contracts.FinalAfter<T>(phase)` (`PhaseContracts.cs`), not just described.

### 5.4 Several worlds

`Engine` owns a list of worlds; `HostLoop` ticks and frames all of them. Uses today: the main world, the
editor's edit world (`Engine.CreateEditWorld`, no Fixed systems run), and the play world that
play-in-editor creates beside it. Exactly one world draws to the screen (`Renderer.ScreenWorld`); others
keep only their views into render targets. Each world has its own physics space, event queues, resources
and origin; worlds share the `Engine`'s registries, records and cvars.

## 6. ECS and world model

`World` (`src/Sage.Simulation/ECS/World.cs`) wraps a Friflo `EntityStore` (decision D4) behind Sage's own
vocabulary in `src/Sage.Simulation/ECS/Api/`: `Entity`, `IComponent`, `ITag`, `Tags`, `Query<T1..T5>`
(with `Chunks` for allocation-free iteration) and `EntityCommands`.

- **Entities** are handles (world, id, revision); a handle to a destroyed entity reports `IsNull` even if
  the id is reused. Every entity starts with `Transform` and `GlobalTransform`.
- **Components** are plain structs with a stable id, `[Component("ns:name", Version = n)]` or `[Tag(...)]`;
  a struct without one is SAGE0004. Saves and prefabs use the id, never the C# name. `Get<T>` returns a
  `ref` and throws on a missing component in every build (test: Get_ReturnsRef_MissingComponentThrowsInEveryBuild).
- **Queries** are created once (usually in a system constructor) and kept; creating one allocates.
- **Structural changes** inside a query loop go to `world.Commands` (`EntityCommands`), played back at the
  end of the phase. Structural notifications (`EntitySpawned`, `ComponentAdded`, `ComponentRemoved`,
  `EntityDestroyed`) are raised synchronously on the world's thread.
- **Hierarchy**: `SetParent`/`ClearParent`; children a prefab placed (`FromParentPrefab`) are destroyed
  with their parent. Positions are relative to the world's origin sector (`Origin`); a rebase shifts every
  pose, the physics space and the camera together.
- **Resources** are per-world singletons in `WorldResources`: `Add` throws if present, `Replace` is
  deliberate, `GetOrAdd` for lazily created ones. Every world gets the `RecordStore`, `EntityIO`, debug
  draw and message log; plugins add the rest (terrain, `PlayerInput`, `ActiveCamera`, the physics space).
- **Prefabs, scenes, placements**: a `prefab` record (`PrefabRecord`, `Prefab.cs`) lists components and
  parts (`[PrefabPart]`) and may nest; a `scene` record (`Scenes.cs`) is what a world starts with (maps,
  placements documents, player start, weather); a `placements` document (`Placements.cs`) says where
  things stand, each with per-instance `overrides` and I/O `outputs`.
- **Persistent ids**: `PersistentId` (a GUID, or `FromName` for content-placed things so the id is the
  same every run) identifies an entity across sessions; `world.Resolve(id)` maps it back to a handle.

Detail: [subsystems/04-ecs-and-scenes.md](subsystems/04-ecs-and-scenes.md) and
[`../design/03-world-and-ecs.md`](../design/03-world-and-ecs.md).

## 7. Communication between systems

Sage has four mechanisms, each for a different job ([`../design/04-events-and-messaging.md`](../design/04-events-and-messaging.md)).

| Mechanism | Type | Use it for | Rules |
|---|---|---|---|
| Direct calls through resources and services | `WorldResources`, `ModuleContext.Get<T>()`, `IPhysicsWorld` | Asking a question that needs an answer now (a raycast, "what is in reach", a record lookup). | Services must come from a declared dependency or the host, or `Get` throws. Gameplay reaches physics only through `IPhysicsWorld`. |
| Typed game events | `GameEvents`, `EventQueue<T>`, `EventReader<T>` (`ECS/Events/GameEvents.cs`) | Facts one system tells others: `Damaged`, `Died`, `ItemPickedUp`. | One queue per event type per schedule. Each reader has its own cursor, so nothing is seen twice or missed, even across catch-up ticks. An event is dropped once every reader has passed it; `ev_maxage` drops stale ones with a warning naming the slow reader. Pruning happens at the end of a schedule, never mid-phase. Readers are owned (`Reader<T>(this)`) and released when their system is removed. |
| Entity I/O | `EntityInputs`, `EntityOutputs`, `EntityIO` (`Logic/EntityIO.cs`) | Level logic wired in data: a button opens a door, a trigger starts an ambush. | Outputs fire into a queue with optional delay; `sage.io.dispatch` delivers in `Phase.EntityIO`. Connections are resolved when the level loads, and an unknown input is a load error naming the map and entity. Routed handlers per component, then a global one. |
| Conditions and actions | `ICondition`, `IAction` (`Logic/Conditions.cs`), declared with `[Condition("id")]`, `[Action("id")]` | "May this happen?" and "what happens?" anywhere content needs it: dialogue, quests, triggers, state machines, widgets. | Open vocabularies registered per plugin; parsed once at load; evaluating allocates nothing (test: EvaluatingConditionsAllocatesNothing). Experimental, SAGE0120. |

Engine plumbing between ticks uses plain C# events (`ContentService.Reloaded`, `RecordStore.Reloaded`).
Presentation never writes gameplay components: input reaches the simulation only as `PlayerCommand`
(ARCHITECTURE §2 rule 3). Detail: [subsystems/05-events-and-logic.md](subsystems/05-events-and-logic.md).

## 8. Content pipeline

**VFS** (`src/Sage.Core/Content/VirtualFileSystem.cs`). A `VirtualPath` is lower case, forward slashes,
no `..`; it is an asset's identity. Mounts are ordered, later wins: engine content (`sage`), plugin content
embedded in assemblies (`AssemblyContentMount`), the game's folders (`FolderMount`, game id namespace),
then active mods (`mods/<id>`, namespace `<id>`). Each mount gives bare record ids in its files a
namespace. `vfs_which` says which mount serves a path and what it shadows. Zip mounts are planned (#397).

**Records** (`RecordStore.cs`). Every definition (items, prefabs, scenes, materials, input maps, AI
schedules, UI layouts) is a JSON or JSONC record with `type` and namespaced `id`, declared in C# with
`[Record("type")]`. Load order is mount order; within a mount, definitions before patches. Patches merge
field by field (`field+`, `field-`, `disabled`); `base` inheritance and `abstract` templates resolve after
merging; a second non-patch definition is an error naming both places. Loading is strict: unknown fields,
wrong types and dangling references are errors at `mount:path:line:col`, kept in `RecordStore.LoadErrors`.
`[RecordRef("type")]` on a `RecordId` field and `[AssetKind("kind")]` on an `AssetPath` say what a field
points at, so the loader and `sage validate` can check it. Every write is kept with its source, which is
what the mod conflict report (`ContentReport`) is built from.

**Metadata and schemas.** `[Property(Min, Max, Unit, Tooltip, Category)]` feeds a generated metadata table
that the inspector, `ent_dump`, the FGD export, the registry dump (`RegistryDump.cs`, `-dump-registry`) and
the JSON Schemas read. `sage schema` writes `schemas/` from it; CI diffs the result
(test: EveryShippedDataFile_ValidatesAgainstTheCommittedSchemas).

**Assets on the client.** `ContentService` (`src/Sage.Client/Assets/ContentService.cs`) loads textures
(PNG, JPG, or the cooked `.sgtex`), compiled effects (`.mgfxo`), sounds (WAV, OGG) and the bitmap font from VFS streams; the renderer
keeps meshes read by `MeshGeometry.ReadGlb` (`.glb`, or the cooked `.sgmesh`; #302) in its own table. Components hold `AssetPath`, an interned id,
never a loaded object. A failed load logs once and the caller draws a placeholder. `dotnet-mgfxc` for
`.fx` is the only build-time content step (decision D10).

**Hot reload** (dev builds). `RecordHotReload` and `AssetHotReload` use `FileSystemWatcher`, whose events
only mark files changed; the main thread polls once per frame and reloads after 200 ms of quiet. Records
update in place (instances survive); a record type's `[Record(Reload = ...)]` says whether what was built from it follows too, and a prefab's live instances do, field by field where the game did not change the field (#287). A reloaded texture or sound disposes the old object after the new one
loads. `ShaderRecompiler` runs mgfxc on a background task when `.fx` sources change, where mgfxc can run.

**Mods.** `ModManager` discovers `mod.json` folders, orders them (`ModLoadOrder`, `user://mods.json`),
mounts them after the game, and writes `user://logs/mod_report.txt`. Data mods are built; code mods are
phase 9 (#396). Detail: [subsystems/06-assets-and-content.md](subsystems/06-assets-and-content.md),
[subsystems/19-modding.md](subsystems/19-modding.md).

## 9. Simulation to presentation

Rendering, audio and UI read the simulation and never write it.

```
 Fixed ticks (simulation)            Frame (presentation, main thread)
 components, GlobalTransform  --->   Extract: CameraExtract -> ViewSource -> RenderView(s)
 RenderEnvironment, Lights,          MeshExtract, SpriteExtract, LightExtract, ... -> RenderSnapshot
 AudioMixer voices, UiRoot           Render: Renderer draws passes per RenderStage from the snapshot only
                                     Overlay: UI plan drawn by UiDraw/WidgetRenderer
                                     FrameUpdate: AudioSystem plays mixer voices via IAudioBackend
```

- **Extract** is the only code that reads simulation components for drawing. It writes a pooled
  `RenderSnapshot` (`src/Sage.Client/Rendering/RenderSnapshot.cs`): `PooledList<T>` buffers that keep their
  capacity, camera-relative interpolated transforms, 64-bit sort keys, frustum culling.
- **Views.** A world can have several views (split screen, minimap, mirror) from camera entities through
  `ViewSource`; each view's items are planned by `RenderViewPlan` (test: Planning_DoesNotAllocate).
- **Passes.** `RenderStage` (`Shadow`, `Opaque`, `AlphaTested`, `Sky`, `Transparent`, `Debug`,
  `PostProcess`, `Overlay`) orders an `IRenderPass` registry (`RenderPasses`, `RenderPassApi.cs`). A pass is
  declared with `[RenderPass("ns:id", RenderStage.X)]`, added in a module's `Init`, sealed in the client's
  `Start`, and gets a `RenderContext` in `Extract` and `Draw`. One pass instance serves every world.
- **Audio.** `AudioMixer` (`src/Sage.Simulation/Audio/AudioMixer.cs`) is a world resource that decides
  every voice, its bus and position in origin space; the client's backend only plays what it is told.
- **UI.** A screen's contents are simulation data (`Panel`, view models; `Sage.UI` widgets and layout,
  headless); the client draws the layout plan.

Keeping MonoGame out of the simulation is what makes headless tests, a dedicated server and a future
backend switch (DesktopVK) possible; the snapshot is the seam. Detail: [subsystems/07-rendering.md](subsystems/07-rendering.md),
[subsystems/11-audio.md](subsystems/11-audio.md), [subsystems/13-ui.md](subsystems/13-ui.md).

## 10. Threading model

### 10.1 Today

| Thread | What runs there | Shares with the main thread |
|---|---|---|
| **Main** (`"main"`) | Everything in §5: every world's Fixed and Frame schedules, all systems (but see the scheduler's workers below), command playback, events, saves and loads, record and asset loading, GPU work, hot-reload polling, the console. | Owner of all world and engine state. |
| **Bepu workers** | Inside `sage.physics.step`, `PhysicsSpace` passes a `ThreadDispatcher` (processor count minus one) to `Simulation.Timestep`. The step is synchronous: the main thread waits for it. | Bepu's own buffers only; `Simulation.Deterministic = true`. |
| **`log-writer`** | `Log` producers enqueue into a `ConcurrentQueue`; a background thread feeds the sinks (file, stdout, ring, console). | The queue only. |
| **Scheduler workers** | Per world (`sys_threads`; default one fewer than the cores, at most 7), with the world's own thread: the systems of a parallel stage, those that declare their access (`IDeclaresAccess`) and conflict with nothing else in it (#288). Each records structural changes on its own log, replayed in phase order; the stage ends before the next starts. No engine system declares its access yet. | The world, under the declarations: a dev build reports what a declared system touched without declaring it (`sys_access_check`). |
| **Thread pool** | `FileSystemWatcher` callbacks (they only set flags); `ShaderRecompiler`'s mgfxc task (writes a file the main thread then reloads); a save's write (#285: building the entities' JSON from component columns copied on the tick, compressing and writing, one save after another). | A flag or a file; a save's snapshot shares nothing with the world. |
| **MonoGame/OpenAL** | The audio device's own mixing. | Through `SoundEffect` instances, driven from the main thread. |

A test may tick a world on its own thread; several tests do so in parallel. That is safe because worlds
share no mutable state, and the process-wide pieces are either thread-safe (`Log`, the `AssetPath`
intern table) or per thread (`Profiler` tables, `[ThreadStatic]` scratch buffers).

### 10.2 Rules

1. A world is touched by one thread at a time, except by a parallel stage's systems, which touch only what they declared (#288). No system starts a thread or a task.
2. Structural changes during iteration go through `EntityCommands`; nothing mutates world structure from
   outside the phase that owns it.
3. Cross-system facts go through event queues read in a later phase, never through callbacks into
   another system.
4. Background work produces data (a file, a buffer) that the main thread applies at a defined point (a
   frame poll or the tick boundary).
5. `GraphicsDevice` is used only on the main thread.
6. Allocation is measured per thread (`GC.GetAllocatedBytesForCurrentThread`), so work a worker does is
   invisible to the zero-allocation tests (§11).

### 10.3 Planned (not built)

| Plan | Milestone | Issue |
|---|---|---|
| A `JobSystem` over the thread pool and async asset decode ([`../design/02-core-services-and-logging.md`](../design/02-core-services-and-logging.md) §4.5, 05 §6); the GPU upload budget is built (#308), decoding is still on the main thread | later | none filed |
| A render thread handed a copy of the snapshot (the snapshot is value data for this reason) | later | none filed |

## 11. Memory model

**Allocation rules.** A steady-state tick and frame allocate nothing on the managed heap (SRS REQ-PERF-02
and 03). The techniques:

- components are structs in Friflo chunks; hot paths iterate `Query.Chunks`;
- queries, readers and command buffers are created once and reused; `EntityCommands` reuses its buffer;
- event queues grow to their high-water mark and stay (`EventQueue<T>.Send`);
- the render snapshot and similar per-frame lists are `PooledList<T>` cleared, not reallocated;
- time, context and command types are value types (`TickTime`, `FrameTime`, `SystemContext` as a
  `ref struct`, `PlayerCommand`, `RenderView`);
- `AssetPath` is an interned `int`: an append-only table, locked on insert, read lock-free;
- `Log` calls whose category is off format nothing; Trace and Debug are compiled out of Shipping.

The rules are proved by Measurements tests, for example the 2,000-entity scale world
(test: AFullWorldStillAllocatesNothingPerTick), which allows a small slack for a one-off 40-byte object
seen in one window and not the other on a loaded machine. The physics step allocates nothing on any thread
(test: ASteadyStateStepAllocatesNothingOnAnyThread); the 40 bytes a tick once blamed on Bepu's profiler were
our own Stopwatch (#273). The test process sets
`<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection>` (`tests/Sage.Tests/Sage.Tests.csproj`)
because a background GC miscounts the per-thread allocation figure.

**Ownership and lifetime today.**

| Resource | Owner | Lifetime |
|---|---|---|
| Records | `RecordStore` on the `Engine` | Process; hot reload updates instances in place; runtime records (`AddRuntime`) survive reload. |
| Entities and components | Each `World` | Until destroyed or the world is disposed. |
| Physics bodies and shapes | `PhysicsSpace` per world (Bepu `BufferPool`) | The world. |
| Textures | `ContentService`'s `AssetTable` | By scope (#308): `Engine` and `Game` for the process; `Sector` until no live entity in any world holds it after its sectors unload; `Ui` until no screen has drawn it for 300 frames. Freed at the frame's safe point (`sage.client.asset_scopes`). |
| Effects, sounds, fonts | `ContentService` | Cached for the process, disposed with the service; never unloaded while running. |
| Meshes | `Renderer` mesh table (an `AssetTable`) | By scope, as textures; built meshes (terrain chunks, brushes) by their owner (`DestroyMesh`). |
| Skeletons and clips | `GltfAnimationReader` on the `Engine` | Process, cached by path. |
| Render targets | `RenderTargetPool` in the client | Reused across frames. |

**Asset scopes (4n, #308), built.** Meshes and textures carry a scope (engine, game, sector, UI; the stronger
of two wins), sectors count who holds them (`SectorAssets`, #277), and what is released is freed at the
frame's safe point, so GPU memory returns to baseline when content unloads (SRS REQ-PERF-06; test:
LoadingAndUnloadingFiftySectorsReturnsTextureAndMeshCountsToBaseline). Streamed loads keep to
`asset_upload_ms` a frame. There is no LRU cache or memory budget (`asset_cache_mb` was not built), and no
`AssetRef` handle: ids in an `AssetTable` stay stable instead.

## 12. Persistence

Saves are written and read by `SaveSystem` (`src/Sage.Simulation/Content/SaveSystem.cs`), at the tick
boundary only.

- **Layout.** A slot is a folder under `user://saves/` with `header.json` (format version, kind, worlds,
  plugins with versions, content mounts, active mods) and one `world_<name>.json` per world. Format version
  is `SaveSystem.FormatVersion` (4); formats 1 to 3 are upgraded in memory on load, and a newer one is refused.
- **Identity.** Components and saved resources are keyed by stable id and carry their own `version`;
  entity references are written by `PersistentId`, attributes and tags by name.
- **Diff against content.** An entity spawned from a prefab is written as a diff against the prefab as
  spawned (`SaveDiff`), so a rebalance reaches what the player never changed.
- **Reconcile on load.** Content is placed again as it is now; the save's **tombstones** remove what the
  game destroyed; saved state is laid onto what matches by id (`ContentBaseline`); `dormant` holds state of
  scenes and cells not in the world.
- **Upgraders.** `[Upgrade(n)]` methods on a component, and saved-resource upgraders, rewrite older shapes;
  renaming a saved field requires a `Version` bump. Golden saves in `tests/Sage.Tests/Content/Saves/`
  (formats 1 to 4) must load (test: AGoldenSaveInTheCurrentFormatLoads).
- **Atomic write.** The slot is written to a staging folder and moved into place; on any exception the
  staging folder is deleted and the previous save is untouched. `config.cfg`, `mods.json` and edited record
  files use write-to-`.tmp` then move.
- **World resources** marked `[SavedResource]` are saved beside entities and rebuilt through
  `ISavedResource.AfterLoad`. Behaviour or script state is never saved.
- **Snapshot on the tick, write in the background** (#285). The tick pays for the snapshot; serialising, gzip (`save_compress`), the thumbnail's PNG and the files are written on a thread-pool thread, and reading a slot waits for writes in flight. A slot carries a title and, from the client, a thumbnail; `save_report` says what a load would upgrade. The snapshot copies component columns per archetype (`SaveCapture`) and the writer builds the JSON, so a 10k-entity snapshot is about 3 ms (Debug).

A load removes the live runtime spawns no save names (`Unsaved`, #278).
Detail: [subsystems/15-saves.md](subsystems/15-saves.md), [`../design/09-serialization-and-saves.md`](../design/09-serialization-and-saves.md).

## 13. Error handling and diagnostics

| Concern | Mechanism |
|---|---|
| Content errors | Logged under `Records` with `mount:path:line:col`, counted in `RecordStore.ErrorCount`, listed in `LoadErrors`; the bad record is skipped. `sage validate` exits 1 on any. The editor's `ProblemList`, the `problems` command and the dev overlay's badge (`ui_problems`) read the same `ContentProblems` (#301). |
| Mod conflicts | `ContentReport` (provenance of every field write), `mod_conflicts`, `mod_report.txt`. |
| Logging | `Log.Trace..Fatal` with `LogCat` categories (Core, Host, Modules, VFS, Assets, Records, Render, World, Events, Physics, Audio, Save, AI, Gameplay and more); per-category levels with `log_level`; rate limiting with `Log.Every`; sinks for file, stdout and a ring buffer that the in-game console and the editor's log view read. |
| Asserts | `Assert.Dev`, `Ensure`, `Check` (`src/Sage.Core/Diagnostics/Assert.cs`). |
| Crashes | `CrashReporter` writes `user://logs/crash-*.txt` on `Log.Fatal`, failed `Check` and unhandled exceptions: exception, log tail, sections added by the host (non-default cvars, modules, mounts, mods). |
| Console and cvars | `CVarRegistry` with flags (`Archive`, `Cheat`, `DevOnly`); `developer` sets dev defaults; Shipping keeps a restricted console (`con_enable`, cheats behind `sv_cheats`). The registry dump lists every command and cvar. |
| Profiling | `Profiler.Begin` scopes; the world wraps every phase and system automatically; `stat`, `sys_list` and `sys_toggle` read them; tables are per thread. `trace_start`/`trace_dump` capture every scope on every thread as a Chrome trace; `WorkStats` counts jobs, loads and uploads for `stat render` and `stat assets` (#300). |
| Visual log | `world.VisualLog()` keeps debug shapes per tick and category (`vlog_record`, `vlog_ticks`, `vlog_show`), scrubbed by `vlog_at`, `vlog_step`, `vlog_list` and the editor's Visual log window (#300). |
| Runtime introspection | `ent_dump`, `rec_get`, `vfs_which`, `ev_stats`, `r_passes`, `scale_report`. |

The console has Up/Down history and Tab completion of commands, cvars, cvar values and record ids (`ConsoleInput`, #299).
Detail: [subsystems/02-core-services.md](subsystems/02-core-services.md).

## 14. Testing architecture

- **`HeadlessApp`** (`tests/Sage.Testing/HeadlessApp.cs`) builds a `SageApp` without a client through the
  same stages as the host: `Bare()`, `Gameplay()`, `Simulation()`, `ForGame(dir)`. Tests never construct
  `Engine` by hand. `Sage.Testing` also has `MountFixture` (content in a temp mount), `EventProbe`,
  `CaptureSink` and skeleton builders, and games can use it too.
- **Parallel by default.** Tests that change process-wide state or measure time or allocation go in the
  `ProcessWideState` or `Measurements` collections (`tests/Sage.Tests/Core/TestSupport.cs`), which disable
  parallelism.
- **Exit games** in `tests/games/` (scene-only, camera-cut, scripted-sequence, topics, skeletal, weapons,
  saves, open-world, mods, editor, no-plugins) are data-first games that prove a phase's exit criterion
  headlessly; `tests/games/render-check` is the fixed scene CI's `drawing` job reads back (#318).
- **Analyzer tests** compile small sources and expect each SAGE id; layering tests read the built
  assemblies.
- **Golden saves** for every format version must keep loading.
- **Smoke runs** (`tools/smoke_run.sh`) start the real host under Xvfb with `host_exitafter` and check the
  log; this is the only automated check of `Sage.Client`, which tests may not reference. The drawing path is
  checked by CI's `drawing` job (#318): the Linux host draws `tests/games/render-check` with the shaders the
  Windows job compiled, and `r_pixelcheck` checks regions of the frame it reads back.
- **Docs are checked**: `tools/check_docs.py` verifies links, cited tests and counts against the registry
  dump; schema output is diffed in CI.

## 15. Extensibility points

| A game or kit adds | Declaration or API |
|---|---|
| Plugin | `[Plugin("id", "ver")]` on an `IModule`; `[RequiresPlugin]`; game.json `plugins`, `kits`, `modules.add` |
| Component, tag | `[Component("ns:name")]`, `[Tag("ns:name")]`, fields with `[Property]` / `[Transient]` |
| Record type | `[Record("type")]` class; `[RecordRef]`, `[AssetKind]` on fields; `RecordStore.AddCheck<T>` for custom checks |
| Prefab part | `[PrefabPart("ns:name")]` |
| Saved world state | `[SavedResource]` with `ISavedResource`; `[Upgrade(n)]` |
| System | `[System("id", Phase.X, After/Before)]` + `world.AddSystem`; swap or switch off another plugin's with `world.Systems.Replace(id, s)` / `Disable(id)` |
| Game rules | `IGameModule.CreateRules(World)` returning a `GameRules` |
| Event | a struct sent with `world.Events.Send`, read with `Reader<T>(this)` |
| Entity input or output | `EntityInputs.Register` / `Register<T>`, `EntityOutputs.Declare` (in `Init`) |
| Vocabulary word | `[Condition("id")]`, `[Action("id")]`, or a new `[Vocabulary]` with its entry attribute |
| Render pass | `[RenderPass("ns:id", RenderStage.X)]` on an `IRenderPass`, added to `RenderPasses` in `Init`; an engine pass is replaced or switched off by id with `RenderPasses.Replace` / `Disable` (#322) |
| UI | a `Widget` subclass in code; screens by id in the client's `ScreenRegistry`; `ui_layout`, `ui_style`, `screen` records (new widget types from data are 4q) |
| Console command, cvar | `CVarRegistry.RegisterCommand` / `Register` in `Init` |
| Input action | `Engine.Actions.Register` in `Init`; bindings in `input_map` records |
| Content | mounts in game.json; data mods with `mod.json` |

## 16. Key architectural decisions

| Id | Decision | Status |
|---|---|---|
| D1 | Multiplayer later; keep the readiness rules (ARCHITECTURE §4.9) | Holding |
| D2 | `System.Numerics` in simulation; MonoGame types only on the client | Built, enforced (SAGE0024) |
| D3 | C# game modules, data mods and trusted C# mods; no sandboxed scripting | Under review (REDESIGN §4.3, §6 item 1) |
| D4 | Friflo.Engine.ECS behind Sage's own types | Built (#25, SAGE0050) |
| D5 | BepuPhysics v2 plus own kinematic character controller | Built |
| D6, D7 | In-engine editor as a separate host mode around a document model; `.map` import until editor brushes | 10a built; brushes #61 |
| D8 | Own game UI on `Sage.UI`; ImGui for dev tools only | Built; legacy stack retires in #350 |
| D9 | MIT licence | Decided (ARCHITECTURE §8; REDESIGN §6 item 5 predates it) |
| D10, D11 | Runtime PNG/glTF/WAV loaders, no MGCB; glTF over FBX | Built |
| D13 | MonoGame 3.8.5.1 DesktopGL; revisit DesktopVK later | Built |
| R§3.2 | One boot path, sealed stages | Built (#10) |
| R§3.3 | Plugins with string ids and SemVer ranges; system ids with replace/disable | Built (#12, #17) |
| R§3.4 | Source generator as the single declaration registry | Built (#16, #18, #19, #21) |
| R§3.6 | Declared public API, MinVer versions, `[Experimental]` ids | Built (#31); 0.1.0's API frozen in Shipped and a release workflow built (#295), the tag itself pending |
| R§0.5 | Base engine first, genre rules in kits | Built (#26, #27) |
| R§6 | Open owner decisions: scripting language, editor for modders, platforms, distribution | Open (SRS §10) |

## 17. Known architectural risks and debts

| Risk or debt | Effect | Plan |
|---|---|---|
| Per-process statics: `UserPaths`, `Log`, `CrashReporter` | Two apps in one process (an editor hosting a server, two test apps) share one log, user folder and crash report. | #49 (Stage E); rises if a server or multiplayer starts. |
| ~~Asset memory is freed only for meshes~~ | Retired by #308: meshes and textures are released with their scope; sounds and effects (a handful each) stay for the process. | Done (4n, #308). |
| ~~Single-threaded scheduler~~ | Systems that declare their access run in parallel stages since #288; no engine system declares its access yet, so the engine's phases still run one system at a time. | Done (4m, #288); declaring the engine's systems has no issue yet. |
| ~~Save snapshots on the tick~~ | Retired by #285: the tick copies component columns (about 3 ms at 10k entities, Debug) and the writer builds the JSON. | Done (4m, #285). |
| ~~Bepu allocates about 40 B a tick~~ | Retired by #273: it was our Stopwatch, and the step allocates nothing. | Done (4l, #273). |
| Two UI stacks (legacy `Panel`/`Screen` plus immediate HUD, and `Sage.UI` widgets) | Two ways to build a screen; features land in one or the other. | 4q, #350. |
| Records and saves use reflection, not generated readers | Slower loads; not trim or AOT safe. | Generator follow-up; AOT out of scope (SRS §9). |
| Determinism is by construction only | No cross-run check in general; a recorded `.sagedemo` replays to the same world hash where the save holds everything the simulation reads (#333), but not in the real Sandbox client (camera rig, `FaceCameraSystem`, `HopSystem`). | 4l (#273). |
| Default `AssemblyLoadContext` for game and kit assemblies | Code mods cannot be unloaded or isolated. | 9, #396. |
| No archive mounts | A game ships as a folder (`sage package`, #293), its mounts copied as folders. | Archive mounts, 9, #397. |
| ~~Render pass registry cannot replace or disable an engine pass~~ | Retired by #322: `Replace`/`Disable` by id, applied at the seal. | Done (4n, #322). |
| Dev tools allocate per frame | Frame zero-allocation holds only with the overlay closed. | 10b. |
