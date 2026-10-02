# 15 · Saves

> Status: built and heavily tested (format 4, four golden saves); thumbnails, compression, asynchronous writing and a few "Limits" remain. Owning assemblies: `Sage.Simulation` (`Content`), `Sage.Core` (declarations). Design doc: [09-serialization-and-saves](../../design/09-serialization-and-saves.md).

## 1. Purpose and scope

Saves let a player stop anywhere and continue, and let a designer, an update or a mod change content afterwards without losing or doubling anything. The model is a diff against content: the game's own records, scenes and maps are the baseline and are never saved; a save stores only what differs, plus the stable ids needed to match it back.

It deliberately does not do the following. Content loading and records are [06-assets-and-content](06-assets-and-content.md). Cell dormancy and streaming decide when state is written for a sleeping area, see [14-world-and-streaming](14-world-and-streaming.md); this sheet defines the entry format they reuse. Slot screens belong to [13-ui](13-ui.md) and the kit. Mod load order and the mod list come from [19-modding](19-modding.md).

## 2. Responsibilities

- Write and read slots under `user://saves/<slot>/`: a JSON header and one world file per world.
- Key every component, tag and saved resource by its stable id with a version, never by C# name.
- Give every persistent entity a stable id, including everything content places, and track tombstones per source.
- Save an entity as a diff against its prefab as spawned; load by applying the current prefab, then the diff.
- Reconcile on load: content added after the save appears, content the game destroyed stays destroyed.
- Persist runtime spawns (drops, summons, projectiles, `ent_spawn`) unless the prefab says `"persist": false`.
- Read the whole save before changing any world, so a load cannot half-happen.
- Keep what this game cannot read: placeholders for missing prefabs, unknown components, tags and resources.
- Run upgraders for old component, resource and file-format versions.
- Quick-save, quick-load and rotating autosave at a tick boundary; record plugins, content and mods in the header.

Not responsible for: what a slot menu looks like, thumbnails (open, #285), deciding which gameplay state is worth saving (each type declares it), or cloud storage.

## 3. Placement and dependencies

`SaveSystem` is a partial class in `src/Sage.Simulation/Content` (`SaveSystem.cs`, `SaveRequests.cs`) and is owned by `Engine` (`Engine.Saves`). Companions are `SaveSerializer`, `SaveJson`, `SaveDiff`, `SaveAttributes`, `ContentBaseline` (ids and tombstones), `Cells` and `QuickSaveKeysSystem`. It is simulation-only and references nothing above `Sage.Simulation`. Declarations (`[Transient]`, `[Upgrade]`, `[SavedResource]`, `[Component(Version, FormerNames)]`) are in `Sage.Core`. Generated registration by `Sage.Generators` supplies the type lists. Most of the public surface is `[Experimental("SAGE0131")]`, the header's mod list is SAGE0132.

## 4. Interfaces

| Type or command | Role |
|---|---|
| `SaveSystem` | `Save(slot, kind)`, `Load(slot)`, `RequestSave`, `RequestLoad`, `QuickSave`, `QuickLoad`, `Autosave`, `Delete`, `Slots`, `Rescan`. Format constants `FormatVersion` (4) and `OldestReadableFormat` (1). |
| `SaveSlot`, `SaveKind`, `SavedMod` | A slot read from its header alone: name, time, format, game, engine version, `Kind` (manual, quick, auto), `CanLoad`, plugins, content, mods, `Mismatches`. |
| `Persistent`, `PersistentId` | The stable identity of a saved entity (`src/Sage.Simulation/ECS/PersistentId.cs`). |
| `FromPrefab`, `SavePlaceholder`, `UnknownSavedData` | Components the save system itself adds: the spawn baseline key, an inert stand-in for a missing prefab, kept unknown data. |
| `[Transient]`, `[Upgrade(fromVersion)]` | Opt a field or type out; rewrite an older shape. Helpers `RenameField`, `RemoveField`, `MoveField`. |
| `QuickSaveKeysSystem` | `sage.saves.quick_keys` (Commands): F5 and F9 from the tick's `PlayerCommand`. |

Console: `save [slot]`, `load [slot]`, `saves`, `quicksave`, `quickload`, `autosave`, `save_delete <slot>`. Cvars: `save_autosave`, `save_autosave_slots` (default 3), `save_autosave_interval` (seconds, default 300, 0 off). Actions: `QuickSave` and `QuickLoad`, bound in engine content and movable by a game.

## 5. Data model

A slot is a folder: `header.json` (format version, game id and version, engine version, `kind`, `plugins`, `content`, `mods`, saved time, scene) and one world file per world. A world file holds the origin sector, `scene`, `entities`, `tombstones` by source, `dormant` cells by source, and saved resources.

An entity entry has its persistent id, prefab id, name, `source` when content placed it, `parent` for a prefab child the game spawned, `diff: true` when it is a diff, `removed` for baseline components it lost, tags in full, and components as `"sage:id": { "version": n, "data": { ... } }`. Ids derive from the source: `scene:<scene>:<index>:<prefab>`, `placements:<doc>:<index>:<prefab>`, a `.map` entity's `id`, `targetname` or index, a placement's authored `id` when given, and a prefab child's id is its parent's plus its key.

Not written: `[Transient]` fields and types (`GlobalTransform`, `PhysicsBody`, `IOConnections`), `Persistent` and `FromPrefab` themselves, system state, delegates and render state. Attributes are saved by id and base value, tags by id, entity references by persistent id. Saved resources declared with `[SavedResource("id", Version = n)]` include `clock`, `travel`, `offscreen`, the journal, reputation and others.

## 6. Lifecycle and data flow

A save, load or autosave asked for during a tick is queued and runs when the tick ends (`World.RunFixed` calls the save system after every phase); asked for between ticks it runs at once. Several in one tick resolve as the saves first, then one load.

Save: capture each world, write to `<slot>.writing`, then rename into place. A folder ending in `.writing` is never listed as a slot. Load:

1. Read the header and every world file, upgrade formats in memory, read every entity's id, prefab and name. Any fault refuses the whole save and changes nothing.
2. Destroy what the game made (the player, drops, placeholders).
3. Place the saved scene again from current content, with tombstones waiting for their sources.
4. Match saved entities by id: lay the diff on content that placed one, drop one whose source no longer places it, spawn the rest from their prefabs, find prefab children under their parent.
5. Place the scene's player, then saved resources, then run `AfterLoad`.

Autosaves go to the first free `autosaveN`, then the oldest; one is taken on a timer and one after a scene change settles. A slot that is a different `Kind` is read from its header.

## 7. Threading, memory and performance

Saving and loading are synchronous on the main thread at a tick boundary. Parsing and writing are not on jobs yet, so a very large world can hitch (REQ-PERF-07 in the [SRS](../SRS.md); #285). The slot list is read once and cached because a menu reads it every frame (test: `SaveSlotsListTheHeadersNewestFirstAndAreReadAgainOnlyWhenTheyChange`). A save is not part of a steady-state tick, so the zero-allocation rule does not cover it.

## 8. Errors and diagnostics

| Failure | Behaviour |
|---|---|
| Corrupt or malformed world file | Whole load refused, "nothing was changed" (test: `ACorruptWorldFileLeavesEveryWorldAsItWas`). |
| Format newer than this build | Refused (test: `ASaveFromANewerFormatIsRefused`). |
| Field the type no longer has, no upgrader | Error naming component, field and the upgrader to write; that component keeps the prefab's values (test: `WithoutAnUpgraderTheErrorNamesTheComponentAndTheField`). |
| Prefab missing | Placeholder, written back unchanged, restored when the prefab returns (test: `AnEntityWhosePrefabIsGoneIsKeptAsAPlaceholderUntilItComesBack`). |
| Unknown component, tag or resource | Kept and written back (test: `AnUnknownComponentTagAndResourceAreWrittenBackUnchanged`). |
| Plugin, content or mod mismatch | One warning line per difference; the load goes ahead (test: `TheHeaderListsPluginsAndContentAndAMismatchWarnsButLoads`). |
| Crash during a write | The previous save is intact (temp folder then rename). |

Log category `Save`. `saves` lists slots with kind, time and entity count. Two placements with one id, or two prefab children with one name, are content errors.

## 9. Requirements

| ID | Requirement | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-SAVE-01 | Components, tags and resources shall be saved by stable id with a version, so a C# rename loses nothing. | Must | Done | test: `ASaveIsKeyedByStableIdsWithAVersionPerEntry` |
| REQ-SAVE-02 | Old component shapes shall load through ordered `[Upgrade]` methods; a missing upgrader shall be a named error. | Must | Done | test: `UpgradersRunInOrderFromTheSavedVersion` |
| REQ-SAVE-03 | Every committed save format shall have a golden save that loads. | Must | Done | tests: `AGoldenSaveFromBeforeStableIdsStillLoads`, `AGoldenSaveInFormat2Loads`, `AGoldenSaveInFormat3Loads`, `AGoldenSaveInTheCurrentFormatLoads` |
| REQ-SAVE-04 | A load shall read everything before changing anything. | Must | Done | test: `ACorruptWorldFileLeavesEveryWorldAsItWas` |
| REQ-SAVE-05 | Content-placed entities shall have stable ids, and destroyed ones shall stay destroyed across rebalances and reboots. | Must | Done | test: `AKilledPlacedEntityStaysDeadAcrossAReboot`, `NoPersistentIdIsDuplicated` |
| REQ-SAVE-06 | A save shall be a diff, so a prefab or record rebalance reaches an existing save. | Must | Done | test: `AnUntouchedGoblinGetsTheRaisedPrefabHealthAndADamagedOneKeepsItsOwn` |
| REQ-SAVE-07 | Runtime spawns shall persist unless their prefab opts out. | Must | Done | test: `ADroppedItemSurvivesASave`, `APrefabThatSaysPersistFalseIsNotSaved` |
| REQ-SAVE-08 | A missing prefab, component, tag or resource shall be kept, not lost, and restored when it returns. | Must | Done | test: `AnEntityWhosePrefabIsGoneIsKeptAsAPlaceholderUntilItComesBack` |
| REQ-SAVE-09 | Saves, loads and autosaves shall run at a tick boundary, with quick-save and rotating autosave. | Must | Done | test: `ASaveAskedForMidTickRunsAtTheTickBoundary`, `AutosavesRotateAndReuseTheOldestSlot` |
| REQ-SAVE-10 | The header shall list plugins, content and active mods, and a mismatch shall warn without refusing. | Must | Done | test: `AModAtAnotherVersionAndAModNotThereBeforeAreSaid` |
| REQ-SAVE-11 | A write shall be atomic. | Must | Done | `src/Sage.Simulation/Content/SaveSystem.cs` (staging folder and move) |
| REQ-SAVE-12 | Save, change a prefab and a record, load: nothing lost, nothing doubled, in a game with no C#. | Must | Done | test: `SavesExit_AQuickSaveSurvivesARebalanceWithNothingLostOrDoubled` |
| REQ-SAVE-13 | A load shall remove live unsaved entities, make derived values follow placement changes, and resolve references to sleeping entities. | Must | Done | tests: `ALoadRemovesTheUnsavedSpawnsItDoesNotName`, `ALoadLeavesWhatTheEngineAndTheGameMadeForThemselves`, `AnUntouchedDoorFollowsItsPlacementAndARebalance`, `AReferenceToASleepingEntityIsKeptAndResolvesWhenItWakes` |
| REQ-SAVE-14 | Nested prefabs and per-placement part overrides shall survive a save across sectors. | Should | Done | test: `AHouseWithAnOverriddenMachineRoundTripsTheEditorASaveAndASectorCrossing` |
| REQ-SAVE-15 | Saves shall offer thumbnails, optional compression, an asynchronous write and named slots, and quick-save keys shall work while paused. | Should | Not started | #285 |
| REQ-SAVE-16 | A save-version report shall say what a save would lose or change under the current content and mods. | Could | Not started | #285 |
| REQ-SAVE-17 | A pre-release save-format window shall be closed: format 1 shall stop being read once the first release ships. | Could | Not started | #295 (first release) |
| REQ-SAVE-18 | Tests shall cover timers and tweens across a save, and hierarchy and multi-world saves. | Should | Not started | #286 |

## 10. Open work

Milestone 2 (epic #274).

- #285 4m-11 Saves: thumbnails, compression, async write, named slots and a save-version report (P2)
- #286 4m-12 Tests: hierarchy, multi-world, timers and tweens across a save (P2)

Related: #396 9-1 code mods (P1) changes what a save must survive; #398 9-3 namespaced mod assets (P1).

## 11. References

- [09-serialization-and-saves](../../design/09-serialization-and-saves.md): the format, stable ids, reconcile, diff, quick-save, cells, mods in the header.
- [REDESIGN](../../REDESIGN.md) §4.5 and phase 4i; [MAKING_A_GAME](../../MAKING_A_GAME.md) §10b (SAGE0131, SAGE0132).
- Siblings: [04-ecs-and-scenes](04-ecs-and-scenes.md), [14-world-and-streaming](14-world-and-streaming.md), [19-modding](19-modding.md), [13-ui](13-ui.md); parent [SRS](../SRS.md).
