# 04 · ECS, scenes and prefabs

> Status: mostly built. Worlds, entities, components with stable ids, tags, typed queries, command buffers, the hierarchy, transforms with interpolation and large-world rebasing, per-world resources, prefabs with parts, children and per-placement overrides, scenes, placements and persistent ids all work and are tested. Queued add and remove events (#282), overrides surviving a sector handoff (#279) and systems that declare their access running in parallel (#288) are built; revert-to-prefab beyond the editor is open. Owning assembly: `Sage.Simulation` (`ECS/`, `Content/`), with the declarations in `Sage.Core` and the generators in `Sage.Generators`. Design docs: [03 world and ECS](../../design/03-world-and-ecs.md), [09 serialization and saves](../../design/09-serialization-and-saves.md), [05 assets and VFS](../../design/05-assets-and-vfs.md) (prefabs, scenes).

## 1. Purpose and scope

This is the data model every game is made of: a world holds entities, an entity holds components and tags, systems query them. On top of that sit the things designers place: prefabs (what a thing is), placements (where it is) and scenes (what a world starts with). Entities that must outlive a session or a sector get a persistent id.

The ECS storage is Friflo, wrapped behind Sage's own `Entity`, `IComponent`, `ITag`, `Query<...>` and `EntityCommands`, so no Friflo type appears in public API and the storage can change (SAGE0050, test: NamingFrifloOutsideTheEcsImplementationIsABuildError). The subsystem does not run systems (the loop does, [03 app and loop](03-app-and-loop.md)), does not write saves ([15 saves](15-saves.md)), does not define events and wires ([05 events and logic](05-events-and-logic.md)), and does not load or validate record files ([06 assets and content](06-assets-and-content.md)).

## 2. Responsibilities

- Worlds: create, destroy and hold entities, resources, systems, the command buffer, the event queues and the hierarchy; edit worlds and play worlds for the editor.
- Entities and handles: a `Entity` is valid for one world and session; a stale handle is detected even when its id is reused (test: StaleHandle_IsDetected_EvenWhenTheIdIsReused).
- Components: plain structs with a stable id (`[Component("ns:name")]`), a saved-shape `Version`, `FormerNames` and `[Upgrade]` methods; tags (`[Tag("ns:name")]`); `[Property]`, `[Transient]`, `[RecordRef]`, `[AssetKind]` metadata.
- Queries: typed queries over one to five components with tag filters, chunk iteration, and an untyped `QueryAll`.
- Deferred changes: `world.Commands` for structural changes applied at the end of the phase; `Deferred<T>` for a system's own deferred work.
- Hierarchy and transforms: `Transform` (local), `GlobalTransform` (current and previous pose), propagation after PostPhysics and Late, `Teleport`, interpolation for rendering, and sector rebasing for large worlds.
- Resources: per-world singletons by type.
- Prefabs: `prefab` records with `components`, `parts`, `tags`, `children`, `base` inheritance and `persist`; `world.Spawn(prefab, ...)`.
- Parts: `[PrefabPart]` classes whose public fields are the options; applied in one order.
- Placements and scenes: `placements` and `scene` records, `Placement` with `overrides`, frames (`World`, `Origin`, `Ground`), the start scene from `game.json`, `Scenes.Load`.
- Persistent ids: `Persistent` component, `PersistentId`, `world.MakePersistent`, `world.Resolve`, ids derived from names for authored placements.

Not responsible for: what any component means, the save diff and reconcile algorithm, streaming rings and cells ([14 world and streaming](14-world-and-streaming.md)), or the editor's document model ([18 editor](18-editor.md)).

## 3. Placement and dependencies

Everything here lives in `Sage.Simulation`, namespace `Sage.Simulation`, with no MonoGame (SAGE0024) and no kit (SAGE0025). Attributes (`ComponentAttribute`, `TagAttribute`, `PropertyAttribute`, `PrefabPartAttribute`, `UpgradeAttribute`) are in `Sage.Core`. `Sage.Gameplay`, `Sage.Physics3D`, `Sage.UI`, `Sage.Editing` and kits declare their own components and parts on this base. Prefab, scene and placements records belong to the engine (`Plugin = RegistrationOwners.Core`), so a game with `"plugins": []` still has them. Components are registered by the generator for the plugin that owns them, and `ComponentSchema` builds the id-to-type table once, after the modules ran `Init`.

## 4. Interfaces

| Type | File | Role |
|---|---|---|
| `World` | `src/Sage.Simulation/ECS/World.cs` | `Create`, `Destroy`, `IsAlive`, `Get<T>`, `TryGet<T>`, `Has<T>`, `Add<T>`, `Remove<T>`, `Teleport`, `SetParent`, `ClearParent`, `Query<...>`, `QueryAll`, `AddSystem`, `RemoveSystem`, `Systems`, `Commands`, `FlushCommands`, `Resources`, `Paused`, `Editing`, `RunFixed`, `RunFrame`, `Resolve`, `MakePersistent`; events `EntitySpawned`, `ComponentAdded`, `ComponentRemoved`, `EntityDestroyed`. |
| `Entity` | `ECS/Api/Entity.cs` | Handle with `GetComponent`, `AddComponent`, `Tags`, `Name`, `Parent`, `ChildEntities`. `IComponent` and `ITag` are the marker interfaces. |
| `Query<T1..T5>`, `Query`, `Chunks` | `ECS/Api/Query.cs` | Typed queries with `AllTags`, `AnyTags`, `WithoutAllTags`, `WithoutAnyTags`, `WithoutComponent`, and allocation-free chunk iteration. |
| `EntityCommands` | `ECS/Api/EntityCommands.cs` | `Add`, `Remove`, `AddTag`, `RemoveTag`, `SetParent`, `Destroy`. |
| `Deferred<T>` | `ECS/Deferred.cs` | `Add`, `Drain`: work a system defers past its own query loop. |
| `Transform`, `GlobalTransform`, `Pose`, `TransformMath` | `ECS/Components/` | Local TRS, current and previous world pose, `LookAt`, `Billboard`. Forward is -Z, up is +Y. |
| `WorldResources` | `ECS/WorldResources.cs` | `Add` (throws on a duplicate), `Replace`, `Set`, `Get`, `GetOrAdd`, `TryGet`, `Remove`; disposable resources are disposed on replace and on world disposal (test: ReplacingDisposesWhatItReplaced). |
| `PersistentId`, `Persistent` | `ECS/PersistentId.cs` | `New`, `TryParse`, `FromName`; component `sage:persistent`. |
| `ComponentSchema` | `Content/ComponentSchema.cs` | `ComponentIds`, `TagIds`, `TryComponent`, `Add` from JSON, `Read`, `Write`, `ComponentsOf`. |
| `PrefabRecord`, `IPrefabPart`, `PrefabPartContext`, `PrefabExtensions.Spawn` | `Content/Prefab.cs` | Prefab body, the part contract, and the one way to place a thing. |
| `PrefabOverrides` | `Content/PrefabOverrides.cs` | Component and part bodies merged field by field into a copy of the prefab. |
| `Placement`, `PlacementsRecord`, `SceneRecord`, `Scenes` | `Content/Placements.cs`, `Content/Scenes.cs` | What stands where; `Scenes.Load(world, id)`, `Clear`, `PlayerStart`, `SpawnPlayer`, `Start`. |

**Console commands:** `ent_list`, `ent_spawn`, `ent_destroy`, `ent_dump` (marks overridden fields with `*`), `ent_types`, `scene_load`, `pause`.

**Events published:** `EntitySpawned`, `ComponentAdded`, `ComponentRemoved` and `EntityDestroyed` are plain C# events on `World`, raised immediately and in a documented order (test: Notifications_FollowTheDocumentedOrder); `BufferedDestroy` reports the same notifications (test: BufferedDestroy_ReportsTheSameNotifications). The queued typed event bus is in the events sheet.

## 5. Data model

**Engine components and tags** declared in this assembly: `sage:transform`, `sage:global_transform` (transient), `sage:persistent`, `sage:from_placements`, `sage:from_prefab`, `sage:prefab_overridden` (transient), `sage:prefab_child_key`, `sage:cell`, `sage:sector_owned`, `sage:save_placeholder`, `sage:unknown_saved_data`, `sage:from_map`, `sage:map_geometry`, `sage:map_solid`; tags `sage:from_parent_prefab`, `sage:from_scene`. Other assemblies add theirs (for example `sage:rigid_body`, `sage:camera`, `sage:timer`); the registry dump lists them all. A component id is `namespace:name` in lower case, unique, and stable: a rename keeps the old id in `FormerNames`, and a saved-shape change bumps `Version` with an `[Upgrade(n)]` method (SAGE0004 to SAGE0007; golden saves under `tests/Sage.Tests/Content/Saves` must keep loading).

**Records:**

| Record | Fields that matter |
|---|---|
| `prefab` | `name`, `components` (by component id), `tags`, `parts` (by part id), `children` (prefabs placed inside, parented; self-containing or deeper than 8 is a load error), `persist` (whether a runtime spawn is saved), `base` (record inheritance). |
| `placements` | `origin`, `relativeTo`, `place` (a list of `Placement`). |
| `scene` | `maps`, `placements`, `origin`, `relativeTo`, `player`, `place`, `environment`, `streamed`, `terrain`, `space` (Exterior or Interior). |
| `Placement` | `prefab`, `at`, `yaw`, `name`, `id`, `relativeTo`, `outputs` (wires), `overrides` (`components`, `parts`). |

An `id` on a placement is its identity in saves; left out, one is derived from its place in the list. Prefab parts shipped by the engine and kits are listed in [MAKING_A_GAME](../../MAKING_A_GAME.md) §3 ("Every prefab part the engine provides"); a part is declared with `[PrefabPart("name", Plugin = "id")]` and registered by generated code.

**Large worlds:** the simulation runs in the local frame of one 1024 m sector (`Origin`); positions stay small floats and the origin moves between ticks. Terrain and saves convert to absolute coordinates.

## 6. Lifecycle and data flow

A world is created by `SageApp.CreateWorld`: modules install resources and systems in `OnWorldCreated`, the rules start, and the start scene places its content. Each Fixed tick runs the phases in order ([03 app and loop](03-app-and-loop.md)); structural changes in `world.Commands` apply at the end of each phase; transforms propagate after PostPhysics and Late, so by the end of PostPhysics every `GlobalTransform` is settled; each Fixed tick keeps the previous pose, and the Extract phase interpolates by alpha (test: Propagation_KeepsPreviousAndCurrent_ForInterpolation). `Teleport` snaps both poses (test: Teleport_SnapsBothPoses).

Spawning a prefab applies everything it says in one order: the body (after `base` inheritance, and after any per-placement overrides are merged into a copy of it), its components, tags and parts, and its children, which are parented to it and destroyed with it. A failing component or part costs itself, not the entity. A scene's placements get persistent ids derived from their id or list position, so the same placement has the same identity every run; a runtime spawn gets a new id if its prefab has `persist` true. `Scenes.Load` clears the previous scene, places the new one and moves the player; a hot reload of a scene or placements record re-places without duplicating or losing the player (test: HotReloadRespawnsTheSceneWithoutDuplicatingOrLosingThePlayer).

Save and load: only entities with `Persistent` are saved, as a diff against the content that placed them; on load, entities are reconciled by persistent id. Children of a prefab are not duplicated by a save and load (test: APrefabsChildrenAreNotDuplicatedByASaveAndLoad). `world.Resolve(id)` returns the live entity or null, and follows destruction (test: PersistentIds_ResolveAndFollowDestruction).

## 7. Threading and memory

A world is single-threaded: its structure may be touched only from the thread that runs its phases, and not from inside a query loop; changes made during a loop go through `world.Commands` or `Deferred<T>`. `GetComponent` returns a `ref`; a missing component throws in every build (test: Get_ReturnsRef_MissingComponentThrowsInEveryBuild). Chunk iteration allocates nothing (test: ChunkIteration_DoesNotAllocate), and a steady tick and frame of the core systems allocate nothing (test: SteadyStateTicksAndFrames_DoNotAllocate). Spawning, loading and placing allocate by nature and are done at tick boundaries, never inside phases. A world holds no static state of its own; two worlds in one engine have separate resources and systems, but share the engine's registries.

## 8. Errors and diagnostics

Content problems are load errors with file and line, never play-time crashes: a prefab body, a component field name and a placement with no prefab are checked at load (tests: PrefabBodies_AreCheckedAtLoad_AtTheirLines, APlacementWithNoPrefab_IsAContentError), and `sage validate` reports the same. A bad `Add` (duplicate component) is refused with a message (test: Add_RefusesDuplicates_Remove_ValidatesFirst). A prefab that no longer exists leaves a placeholder in a save until it returns. Debug: `ent_list`, `ent_dump <entity>`, `ent_types`, the inspector in the editor, and `scene_load`.

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-ECS-01 | Create, query, change and destroy entities with typed components and tags, without exposing the storage library. | Must | Done | test: TypedQueries_TrackMembershipLive; SAGE0050 |
| REQ-ECS-02 | Detect a stale entity handle even when its id is reused. | Must | Done | test: StaleHandle_IsDetected_EvenWhenTheIdIsReused |
| REQ-ECS-03 | Give every component and tag a stable `namespace:name` id, with versioned saved shape, former names and upgraders. | Must | Done | SAGE0004 to SAGE0007; golden saves |
| REQ-ECS-04 | Apply structural changes made during a loop at the end of the phase, with the same notifications as immediate ones. | Must | Done | test: BufferedChanges_ApplyAtTheEndOfEachPhase |
| REQ-ECS-05 | Maintain a parent and child hierarchy with composed, interpolated world poses. | Must | Done | test: Propagation_ComposesThroughTheHierarchy |
| REQ-ECS-06 | Keep positions precise across hundreds of kilometres by rebasing the origin between ticks. | Must | Done | `src/Sage.Simulation/World/Origin.cs`; streaming tests |
| REQ-ECS-07 | Hold per-world singleton resources, refusing a duplicate add and disposing what is replaced. | Must | Done | test: InstallingTheSameResourceTwiceIsAnError |
| REQ-ECS-08 | Define a thing in data as a prefab of components, tags and declared parts, with record inheritance. | Must | Done | `src/Sage.Simulation/Content/Prefab.cs` |
| REQ-ECS-09 | Place prefabs inside prefabs and override one field of one component or part per placement. | Must | Done | test: APlacementsOverridesMergeIntoACopyOfThePrefab |
| REQ-ECS-10 | Boot a world from a scene record naming maps, placements and a player start, from `game.json`, with no C#. | Must | Done | test: AGameWithNoCodeBootsIntoItsScene |
| REQ-ECS-11 | Give persistent entities ids that survive sessions, and derive stable ids for authored placements. | Must | Done | test: PersistentIds_ResolveAndFollowDestruction |
| REQ-ECS-12 | Keep a placed entity's overrides and wires after it leaves its sector and returns. | Must | Done | test: AHouseWithAnOverriddenMachineRoundTripsTheEditorASaveAndASectorCrossing |
| REQ-ECS-13 | Offer revert-to-prefab and nested-prefab editing for designers. | Should | Partial (override and revert in the editor, no nesting) | #368, #372 |
| REQ-ECS-14 | Let a placement override a part's state machine and ship an engine prefab for it. | Should | Done | a placement overrides `machine` (#279); the engine prefab `sage:logic_state_machine` (#280); test: AHouseWithAnOverriddenMachineRoundTripsTheEditorASaveAndASectorCrossing, TheEnginePrefabRunsTheMachineAPlacementNames_AndAStatesOutputsAreWirable |
| REQ-ECS-15 | Publish queued `Added<T>` and `Removed<T>` events instead of immediate C# callbacks. | Should | Done | `src/Sage.Simulation/ECS/Events/StructuralEvents.cs`; test: AReactiveSystemSeesOneAddAndOneRemovePerEntity, ReadingAddsAndRemovesAllocatesNothingPerTick |
| REQ-ECS-16 | Apply a changed prefab to live unmodified instances, by a declared reload policy. | Could | Done (#287): changed fields, added components and tags follow; removed components, parts and children wait for the next spawn | `[Record(Reload = ReloadPolicy.Live)]` on `prefab` and `scene`, `src/Sage.Simulation/Content/PrefabReload.cs`; test: EditingAPrefabUpdatesTheFieldsNoOneChangedOnEveryLiveInstance, InTheSandboxEditingAPrefabUpdatesItsUnmodifiedInstances, RecordTypesDeclareTheirReloadPolicy, ASaveAfterAReloadDiffsAgainstTheNewPrefab, AValueALoadPutBackIsKeptByAReload, EachReloadFollowsFromTheLastOne |
| REQ-ECS-17 | Run non-conflicting systems in parallel from declared access. | Could | Done (#288): `IDeclaresAccess`; a dev build reports what a declared system touched without declaring it | test: DeclaredSystemsRunInParallelWithResultsIdenticalToSequential, AnExplicitOrderIsKeptAndAnExclusiveSystemRunsAlone, ADeclaredSystemTouchingWhatItDidNotDeclareIsReported, ASystemThatThrowsInAParallelStageThrowsOutOfTheTickAndLeavesTheWorldUsable |
| REQ-ECS-18 | Cover hierarchy destruction during a query, two worlds with a save, and sleeping-entity references with tests. | Should | Done | test: DestroyingAParentInsideAQueryIsDeferred_AndTheLoopStillSeesItsChildren, TwoWorldsKeepTheirOwnClocksAndEntities_AcrossASaveAndALoad, AWireToASleepingEntityDoesNothing_ThenWorksOnceItWakes |
| REQ-ECS-19 | Remove unsaved live entities on load so a load cannot leave strays. | Must | Done | test: ALoadRemovesTheUnsavedSpawnsItDoesNotName, ALoadLeavesWhatTheEngineAndTheGameMadeForThemselves |

## 10. Open work

Milestone 8, 10b (Editor, part 2), done 2026-10-07 (sheet [18](18-editor.md)):
- ~~#368 10b-3 Inspector: nested objects, lists, and overrides of components the prefab does not name~~ done: an added component is an ordinary `overrides.components` body, empty at first, which spawning merges at the component's defaults (test: ALightIsAddedToOnePlacedCrateAndASaveKeepsIt)
- ~~#372 10b-7 Terrain tools and prefab revert/nesting~~ done: `ed_make_prefab` nests placements into a new prefab's `children` (test: MakePrefabNestsPlacementsIntoANewPrefabPlacedWhereTheyStood)
- #367 10b-2 gave a placement `pitch`, `roll` and `scale`, which a placement's spawn applies everywhere it spawns; a prefab's `children` still have only a yaw (test: AYawOnlyFileLoadsAsBeforeAndPitchRollAndScaleSaveAndLoad)

## 11. References

- [Design 03](../../design/03-world-and-ecs.md) §3.1a Sage's vocabulary over Friflo, §3.2 components and ids, §3.3 entity identity, §3.4 resources, §3.5 systems and phase contracts, §3.6 transforms and large-world coordinates.
- [Design 09](../../design/09-serialization-and-saves.md) §3.5 what is saved, and its overrides, diff and reconcile sections; [design 05](../../design/05-assets-and-vfs.md) §3.5 prefabs and scenes, overrides and nesting; [design 14](../../design/14-world-streaming.md) (cells, sectors and their limits).
- [REDESIGN](../../REDESIGN.md) §3.5 own the ECS vocabulary, §4.5 saves that survive content changes.
- [MAKING_A_GAME](../../MAKING_A_GAME.md) §3 (records, components have ids, one placement that differs, prefab parts), §8 (saving); [EDITOR](../../EDITOR.md).
- Siblings: [03 app and loop](03-app-and-loop.md), [05 events and logic](05-events-and-logic.md), [14 world and streaming](14-world-and-streaming.md), [15 saves](15-saves.md), [18 editor](18-editor.md).
