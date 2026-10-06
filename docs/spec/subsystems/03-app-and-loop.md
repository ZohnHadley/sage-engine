# 03 · App and game loop

> Status: built. One boot path, plugins, sealed registries, generated registration, `game.json`, the fixed-tick loop and the declared-system scheduler all work and are tested; systems that declare their access run in parallel stages (#288), and a game packages into a folder a player runs (#293). Game-assembly hot reload and multiple simultaneously visible worlds are not built. Owning assemblies: `Sage.Simulation` (`App/`, `ECS/Systems/`), `Sage.Core` (`GameManifest`, `Declarations/`), `Sage.Generators`, `Sage.Host`. Design docs: [01 host and modules](../../design/01-host-and-modules.md), [REDESIGN](../../REDESIGN.md) §3.2 to §3.4.

## 1. Purpose and scope

This subsystem decides how an engine process comes to exist and how it advances. A single class, `SageApp`, boots the game executable, the editor, a dedicated server, a tool and every test through the same ordered stages. Plugins (`IModule`, `IGameModule`) are the unit of registration. Declarations on types (`[Record]`, `[SavedResource]`, `[PrefabPart]`, `[System]`, `[Component]`) are turned into registration code by `Sage.Generators`. Registries close at fixed points so a late registration is an error, not silence. `HostLoop` and `FixedStepClock` run whole fixed ticks for every world and then one frame; `Phase` and `SystemScheduler` order the systems inside each.

It does not draw or poll devices (the host does, [01 platform](01-platform.md)), does not define what a world holds ([04 ecs and scenes](04-ecs-and-scenes.md)), and does not decide what any system does.

## 2. Responsibilities

- Run the boot stages in order and fail loudly out of order: `Create`, `Register`, `Configure`, `LoadContent`, `Start`, then worlds (`AppStage`: Created, Registered, Configured, ContentLoaded, Started, Running, Shutdown) (test: TheStagesRunInOrderAndSayWhenTheyDoNot).
- Choose the plugin set from `game.json` (`plugins`, `modules.disable`, `kits`, `modules.add`), resolve `[RequiresPlugin]` and `Dependencies`, order by dependency, and call `Init`, `Start`, `OnWorldCreated` and `Shutdown`.
- Mount content in order: engine content, plugin content, game mounts, mods; later wins.
- Register what the generator found, per plugin, just before that plugin's `Init`.
- Seal registries at the stage named in section 6, and report a late registration at the line that made it (analyzer SAGE0020 and a runtime exception).
- Parse and validate `game.json`, locate it, and check the engine version range.
- Create and destroy worlds; create an edit world and a play world for the editor.
- Advance every world: a clamped fixed-timestep accumulator, 0 to N ticks, then one Frame schedule with an interpolation alpha.
- Order and run systems by phase, with declared before and after constraints, run conditions, replacement and disabling.
- Record which plugin registered or changed what (`RegistrationLedger`), for `plugins` and the registry dump.

Not responsible for: window and device polling, the content of any phase (the systems), or mods' own contents ([19 modding](19-modding.md); the app only mounts them).

## 3. Placement and dependencies

| Part | Assembly | Notes |
|---|---|---|
| `GameManifest`, `PluginAttribute`, `RequiresPluginAttribute`, `PluginContentAttribute`, `RegistrationSeal`, `RegistrationLedger`, `FixedStepClock` | `Sage.Core` | No MonoGame. |
| `SageApp`, `SageAppOptions`, `Engine`, `ModuleManager`, `IModule`, `IGameModule`, `HostLoop`, `World`, `Phase`, `SystemScheduler`, `SystemAttribute`, `RegistryDump` | `Sage.Simulation` | No MonoGame (SAGE0024). |
| `BasePlugins` | `Sage.Gameplay` | The one list of base plugins a host passes as `AvailablePlugins`. |
| Generators and analyzers | `Sage.Generators` | Compile-time only: registration, vocabulary, metadata, component and part generators; SAGE00xx and SAGE01xx diagnostics. |
| `Program`, `Game1` | `Sage.Host` | Decides what the app is made of (host modules, kit clients, mods, launch commands). |

The base plugins, by id: `sage.physics3d`, `sage.streaming`, `sage.maps`, the `sage.gameplay.*` family (abilities, ai, animation, attributes, character, combat, dialogue, factions, io, items, lights, movers, quests) and the UI module; the host adds `sage.client`; a kit adds `sage.kits.rpg` and `sage.kits.rpg.client`. The base never references a kit (SAGE0025, test: NoBaseAssemblyReferencesALayerAboveIt). `Sage.Simulation` cannot name Gameplay, which is why the host passes `AvailablePlugins` in.

## 4. Interfaces

| Type | File | Role |
|---|---|---|
| `SageApp`, `SageAppOptions`, `AppStage` | `src/Sage.Simulation/App/SageApp.cs` | `Create`, `Register`, `Configure`, `LoadContent`, `Start`, `CreateWorld`, `CreateEditWorld`, `RunLaunchCommands`, `Boot` (all of them, for headless), `Dispose`. `HostKind`: Game, Server, Editor, Tool. |
| `IModule`, `IGameModule`, `ModuleContext` | `src/Sage.Simulation/App/Modules.cs` | `Init` (register only), `Start` (records and GPU exist), `OnWorldCreated(World)`, `Shutdown`. `ctx.Get<T>()` is limited to declared dependencies and host services; `ctx.Provide<T>()` publishes a service. `IGameModule.CreateRules(World)` supplies the game's rules. |
| `Engine` | `src/Sage.Simulation/App/Engine.cs` | Holds `CVars`, `Vfs`, `Records`, `Modules`, `Scenes`, `Saves`, `Prefabs`, `Components`, `Actions`, `Vocabularies`, `Worlds`; `CreateWorld`, `CreateEditWorld`, `CreatePlayWorld`, `DestroyWorld`. |
| `HostLoop` | `src/Sage.Simulation/App/HostLoop.cs` | `Update(realDt, tickRate, maxFrameTime, timeScale, beforeTick)` and `Frame()`; `RealTime`, `Last`, `ResetAccumulator`. |
| `Phase`, `Schedule`, `RunCondition` | `src/Sage.Simulation/ECS/Systems/ISystem.cs` | See section 6. |
| `ISystem`, `SystemContext`, `SystemAttribute` | `ECS/Systems/ISystem.cs`, `SystemDeclarations.cs` | `[System("id", Phase.X, Before = ..., After = ..., Condition = ...)]`, added with `world.AddSystem(new X(...))`. |
| `WorldSystems` | `ECS/Systems/WorldSystems.cs` | `world.Systems.Replace(id, system)` and `Disable(id)`, both recorded against the calling plugin; `Parallel`, `Threads` and `AccessCheckLevel` (per-world overrides of the cvars), `AccessViolations`, `AccessReports` (#288). |
| `IDeclaresAccess`, `SystemAccess` | `ECS/Systems/SystemAccess.cs`, `ParallelSystems.cs` | `Declare(access)`: `Reads<T>`, `Writes<T>`, `ReadsEvents<T>`, `Sends<T>`, `ReadsResource<T>`, `WritesResource<T>`, `Exclusive()`; `ConflictsWith`. A declared system may run at the same time as others of its phase it does not conflict with (#288). |
| `GameManifest` | `src/Sage.Core/GameManifest.cs` | `Locate`, `Load`. |
| `RegistryDump` | `src/Sage.Simulation/App/RegistryDump.cs` | Everything registered, as JSON, for `-dump-registry` and `tools/check_docs.py`: commands, cvars, records, components, systems, entity inputs and outputs, game events (`gameEvents`, #298), input actions and vocabularies. |

**`game.json` keys:** `name`, `id`, `assembly` (with `{config}`), `mounts`, `modsDirectory`, `modules.add`, `modules.disable`, `plugins`, `kits`, `scene`, `sage` (version range), `version`. A misspelt key is an error (test: AMisspeltGameJsonKeyIsAnError).

**Console commands:** `plugins`, `modules`, `sys_list`, `sys_toggle`, `ev_stats`, `vfs_mounts`, `quit`, `pause`, `time_scale` (the clock's hours), `world_speed`, `hit_stop` (the world's time, #283).

**Launch:** `Sage.Host -game <folder> [-mods ...] [-nomods] [-edit [doc]] [-dump-registry file] [+statement ...]` (options read by `HostOptions`, under test: sheet 01, #298). There is no implicit game: without `-game` or a `game/` folder beside the executable the host stops and lists the games it can see.

## 5. Data model

No records or components. Declarations this subsystem owns the meaning of:

| Declaration | Meaning |
|---|---|
| `[Plugin("id", "version")]` | A module's stable id and version; without it the id is the class name and the version 0.0.0. |
| `[RequiresPlugin("id", "range")]` | A dependency by id and npm-style range; the engine itself is `sage`. |
| `[PluginContent("ns")]` | Embedded `content/` mounted as one read-only mount under that record namespace. |
| `[Record("type", Plugin = "id")]`, `[SavedResource("name", Plugin = "id")]`, `[PrefabPart("name", Plugin = "id")]`, vocabulary entries | Registered by generated code for the owning plugin; `Plugin` may be left out when the assembly has exactly one `[Plugin]`. |
| `[System("id", Phase.X)]` | Id, phase and ordering; ids are namespaced, for example `sage.ai.think`. |
| `[GeneratedRegistrations]` | Assembly attribute naming the generated `IGeneratedRegistrations` and `IGeneratedSystems`. |

Engine-owned records and parts use `Plugin = RegistrationOwners.Core`, so a game with `"plugins": []` still has prefabs, scenes and placements.

## 6. Lifecycle and data flow

**Boot** (`SageApp`): `Create` builds the engine, mounts content, picks and orders modules, loads the game assembly. `Register` runs every module's `Init` (the generator's registrations first), then the engine's own commands. `Configure` reads `config.cfg`. `LoadContent` loads and validates all records against the registered types, then picks the start scene. `Start` runs each module's `Start`. `CreateWorld` runs every module's `OnWorldCreated`, then `IGameModule.CreateRules`, then the rules' `OnWorldStarted`. `RunLaunchCommands` runs last, with the world up.

**Seals:**

| Registry | Sealed when | Late registration |
|---|---|---|
| Modules | after every `Init` | exception when a module is added late |
| CVars and commands | `Configure` (config.cfg read) | exception; the saved value would never apply |
| Input actions, vocabularies | `LoadContent` | records could not name them |
| Record types | records loaded | JSON would be dead |
| Prefab parts, entity inputs and outputs | first world created | placed content would miss them |
| Screens, render passes, item slots | the owning module's `Start` | see their sheets |

Tests: ACvarRegisteredAfterConfigCfgIsAnError, ARecordTypeOrActionRegisteredAfterContentLoadedIsAnError, AModuleAddedAfterInitOrAPartAfterTheFirstWorldIsAnError. Registering in `Start`, `OnWorldCreated`, `CreateRules` or a system is also a build error (test: RegisteringACvarInStartIsABuildError).

**Each frame** (`HostLoop`): `FixedStepClock.Advance` computes `min(realDt, sim_maxframetime)`, accumulates, and yields whole real ticks of `1/sim_tickrate` plus an alpha (test: Clock_RunsWholeTicks_AndCarriesTheRemainder). Every world runs each real tick in creation order; its `WorldTime` (#283) turns the tick into as many constant-length simulation steps as its scale, `host_timescale`, pause and hit-stop make due, none to `WorldTime.MaxStepsPerTick` (test: HalfScaleHalvesTimerTweenAndClockProgress, TheHostLoopsTimeScaleRoutesThroughWorldTime), with `beforeTick` handing the player's world its command before each step; a world created during a tick starts on the next (test: AWorldCreatedDuringATickStartsOnTheNextOne). Then `Frame()` runs every world's frame schedule.

**Phases:**

| Schedule | Phases, in order |
|---|---|
| Fixed (once per tick) | Commands, PrePhysics, Physics, PostPhysics, Gameplay, AI, Animation, EntityIO, Late |
| Frame (once per rendered frame) | FrameUpdate, Extract, Render, Overlay |

Within a phase, systems run in dependency order from `Before` and `After` (same-phase ids only; a cross-phase id is an error, an unknown id is an error in dev builds and logged in Shipping, `?id` marks a soft dependency), with a stable sort so ties keep registration order; cycles throw. Structural changes recorded in `world.Commands` apply at the end of every phase. Transform propagation runs after PostPhysics and after Late. Run conditions: `Default` (Fixed systems skip while paused, Frame systems always run), `WhenNotPaused`, `Always`, `DevOnly`, `EvenWhenEditing`; an edit world runs no Fixed system but an `EvenWhenEditing` one. After the phases, at the tick boundary and never inside them: a requested travel, a requested time pass, a requested save or load, and streamed-sector placement. `PhaseContracts` (`FinalAfter<T>`) check in dev builds that nothing writes a component after the phase that settled it (test: WritingAfterTheDeclaredPhaseIsReported).

**Shutdown:** worlds first (their systems belong to modules), then modules in reverse dependency order, then archived cvars; safe to call twice.

## 7. Threading and memory

The loop and every registration run on the main thread. A system that implements `IDeclaresAccess` says what it reads and writes (`SystemAccess`: components, game events, resources, or `Exclusive`), and each phase is grouped into stages: a system goes one stage past the latest earlier system it conflicts with by data, events, resources or an explicit `Before`/`After`, and an undeclared or exclusive system has a stage of its own in its old place. A stage's systems run at once on the world's thread and per-world workers (`sys_parallel`, `sys_threads`); each records structural changes on its own log, replayed in phase order, so results match a sequential run (test: DeclaredSystemsRunInParallelWithResultsIdenticalToSequential, ParallelResultsMatchSequentialAcrossASaveAndALoad; #288). No engine system declares its access yet, so today's phases still run one system at a time. Boot allocates freely. A steady-state tick and frame allocate nothing through the loop and scheduler (test: SteadyStateTicksAndFrames_DoNotAllocate): the system lists are indexed, not enumerated, and `beforeTick` is a cached delegate. Several apps may exist in one process (tests do), but they share the static `Log` and `UserPaths` (#49).

## 8. Errors and diagnostics

Boot failures are exceptions the host reports, writes a crash report for and exits non-zero on: a missing `game.json` (the message lists the games it found), a game assembly that was not built (it says to build), a plugin that does not exist or is too old (it says which), a `plugins` pattern that matches nothing, an unknown `modules.disable` entry (a warning), a start scene that is not a scene record (it lists the scenes). A stage out of order says which stage was expected. Dependency cycles name the cycle. Build-time problems are the SAGE diagnostics listed in [MAKING_A_GAME](../../MAKING_A_GAME.md) §10a. Each stage logs under `LogCat.Host` or `LogCat.Modules`.

Debug: `plugins` (each plugin, version, what it registered), `modules`, `sys_list` (every system, phase, order, owner, replaced or disabled by, and its declared access), `sys_access_check` (dev builds: what a declared system touched without declaring it, in `world.Systems.AccessViolations` and `AccessReports`), `sys_toggle <id>`, `ev_stats`, `stat frame`, `-dump-registry`.

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-LOOP-01 | Boot the game, editor, server, tools and tests through one ordered path that rejects out-of-order stages. | Must | Done | test: TheStagesRunInOrderAndSayWhenTheyDoNot |
| REQ-LOOP-02 | Boot a bare engine and a no-code game headlessly from its `game.json`. | Must | Done | test: HelloBootsHeadlesslyFromItsOwnManifest |
| REQ-LOOP-03 | Choose, order and initialise plugins by id and version range, and name any missing or too-old dependency. | Must | Done | test: AMissingOrTooOldDependencySaysWhich |
| REQ-LOOP-04 | Register declared records, resources, parts and systems through generated code owned by a plugin, with no hand registration for them. | Must | Done | `src/Sage.Generators/RegistrationGenerator.cs`; test: EveryRegistrationIsRecordedAgainstItsPlugin |
| REQ-LOOP-05 | Turn a registration made after its registry sealed into an error at the offending line. | Must | Done | test: RegisteringACvarInStartIsABuildError |
| REQ-LOOP-06 | Run a fixed tick at `sim_tickrate` with a clamped catch-up and interpolated frames, for every world. | Must | Done | test: TheLoopTicksEveryWorldNotJustTheFirst |
| REQ-LOOP-07 | Order systems by declared id and constraints, rejecting cycles, cross-phase and unknown ids. | Must | Done | test: AConstraintAcrossPhasesIsAnError |
| REQ-LOOP-08 | Let a game or mod replace or disable an engine system by id and record who did. | Must | Done | test: ReplaceKeepsTheSlotAndRetiresTheOldSystem |
| REQ-LOOP-09 | Check phase guarantees in dev builds, not in comments. | Should | Done | test: WritingAfterTheDeclaredPhaseIsReported |
| REQ-LOOP-10 | Reject unknown `game.json` keys and report a bad manifest with its path. | Must | Done | test: AMisspeltGameJsonKeyIsAnError |
| REQ-LOOP-11 | Run non-conflicting systems of a phase in parallel from declared access, with identical results. | Could | Done (#288): `IDeclaresAccess`, stages, per-system command logs; checked in dev builds (`sys_access_check`) | test: DeclaredSystemsRunInParallelWithResultsIdenticalToSequential, ConflictingSystemsKeepTheirOrderAndAnUndeclaredOneRunsAlone, TwoSystemsThatDoNotConflictRunAtTheSameTime, ParallelStagesAllocateNothingInSteadyState |
| REQ-LOOP-12 | Run a time scale, pause and hit-stop per world. | Should | Done | `WorldTime`: constant-length steps, more or fewer per real tick; `host_timescale` goes through it; test: AScaledWorldIsDeterministic, TheHostLoopsTimeScaleRoutesThroughWorldTime |
| REQ-LOOP-13 | Offer queued `Added<T>`/`Removed<T>` events and engine signals (scene loaded, world created, paused). | Should | Done: `Added<T>`/`Removed<T>` and `engine.Signals` (world created, scene loaded, world destroying, paused and resumed, raised on the first tick that sees the change) | test: AReactiveSystemSeesOneAddAndOneRemovePerEntity, EngineSignalsSayAWorldWasCreatedAndASceneLoaded |
| REQ-LOOP-14 | Put several worlds on screen at once (split-screen, secondary views). | Could | Partial (#323): split screen is camera slots on one target, each with its own view, shadows and viewmodel; another world reaches the screen only through a render target its views draw into | test: EachSplitScreenSlotResolvesItsOwnCamera_AndTheLowestIsTheMainView, ARenderTargetCameraViewGetsShadowCasterViewsOfItsOwn |
| REQ-LOOP-15 | Run two interior spaces or worlds live at once. | Could | Done (#291): spaces in one world; a `live` scene the player leaves is held and simulated beside the next | test: TwoInteriorsAreLiveAtOnceBesideTheirExterior, EachSpaceFallsAtItsOwnGravity |
| REQ-LOOP-16 | Load a code mod assembly into a collectible load context, ordered against the game's plugins. | Should | Not started | #396 |
| REQ-LOOP-17 | Package a game that boots from its folder with Release treated as Shipping. | Must | Done (#293, #294): the host finds `game/` beside it with no `-game` | test: APackageIsTheShippingHostWithTheGameBesideItAndNothingElse, ThePackagedHelloAndSceneOnlyGamesLoadAndValidateFromTheOutputFolder, AReleaseBuildIsShippingAndWritesTheShippingFolder |
| REQ-LOOP-18 | Keep the base free of kits and of MonoGame. | Must | Done | SAGE0024, SAGE0025; test: EveryBaseAssemblyIsSimulationOnly |

## 10. Open work

Milestone 4, 4n (Rendering and assets): #323 is done (2026-10-06); several worlds straight onto the screen at once has no issue yet.

Milestone 11, 9 (Code mods and packaging):
- #396 9-1 Code mods: trusted assemblies in a collectible load context (P1)

#16 (generated registration), #2 (Phase 0) and #6 are closed; the publish smoke test #6 asked for is CI's package-and-run step (#293).

## 11. References

- [Design 01](../../design/01-host-and-modules.md) §3 concepts, §5.1 boot sequence, §5.2 main loop, §5.3 worlds.
- [REDESIGN](../../REDESIGN.md) §3.2 one boot path, §3.3 plugins, §3.4 declarations.
- [MAKING_A_GAME](../../MAKING_A_GAME.md) §2 (`game.json`), §6 (modules, phases and systems), §10 and §10a.
- Siblings: [01 platform](01-platform.md), [02 core services](02-core-services.md), [04 ecs and scenes](04-ecs-and-scenes.md), [19 modding](19-modding.md); [TDD](../TDD.md).
