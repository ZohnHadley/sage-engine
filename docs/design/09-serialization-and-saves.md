# 09 — Serialization and Saves

## 1. Purpose and scope
One declaration per type drives every place data is read or written:
- prefab and record JSON;
- map files;
- save games;
- the editor inspector;
- the I/O dispatch tables.

This doc defines:
- the **attribute contract**;
- the **source generator**;
- the **save model** (what's saved, where, how it's versioned);
- the **map file layout**.

Not in scope: the record merge rules (05), I/O semantics (04), streaming (14).

## 2. Research basis
- **One reflection source** (Unreal UObject/UHT) vs **Source's four declarations** of the same field (DATADESC, SendTable, prediction table, FGD): survey §1.3, §1.4, §2.2.
- **Compile-time generation instead of runtime reflection** in C#: source generators (MemoryPack, MessagePack-CSharp, Friflo's query generator). Runtime reflection allocates, is slow and hostile to AOT/trimming (survey §3.5).
- **Save versioning** like protobuf: stable field tags, never reused, explicit upgraders (survey §3.5).
- **Never save behaviour/script state:** Skyrim Papyrus orphaned-script saves (survey §1.5).
- **One file per placed entity** for merge-friendly maps: UE World Partition's One File Per Actor (survey §2.2).

> **Nothing has pressure-tested the component shapes yet (engine review 2026-09-23, item 4 → F27).**
> `Inventory.Items` and `ActiveEffects.Effects` are lists; `AIState.Target` and `ActiveEffect.Source`
> are `Entity` handles; several components hold `RecordId`s. Entity references need stable ids across
> a save and lists need ownership and versioning rules — so either build saves sooner than the roadmap
> says, or fix the contract here now so that new components are born compliant.

## 3. Concepts

### 3.1 Attribute contract

| Attribute | On | Meaning |
|---|---|---|
| `[Component("health", Version = 1)]` | struct | Registers a component. The name is the stable identity in files (never the C# type name, so types can be renamed or moved). **As built (issue #16):** a namespaced id, `[Component("sage:health", Version = 1)]`, and `[Tag("sage:hostile")]` for tags — see "As built (stable ids and versions)" |
| `[Saved(1)]` | field | Persisted in saves and maps, with a **stable tag** (int). Tags are never reused, even after a field is deleted |
| `[Property(Min=…, Max=…, Category=…, Tooltip=…)]` | field | Shown and editable in the inspector (15). A field can be `[Property]` without being `[Saved]` (runtime-only debug values) |
| `[Transient]` | field | Explicitly never saved (caches, handles to subsystem data like physics bodies) |
| `[Record("item")]` | class | A record schema (05 §3.5); its fields use `[Property]`/`[Saved]` the same way |
| `[GameEvent]` | struct | Game event (04) |
| `[Output]` / `[Input("Open")]` | field / static method | Entity I/O (04) |
| `[SavedResource("time_of_day", Version = 1)]` | class/struct | Per-world state saved with the world (`GameRules` state, quest log, calendar). **As built (issue #20):** the name and a `Version`, with `[Upgrade]` methods as for a component |

A field with neither `[Saved]` nor `[Transient]` gets a **compile-time warning** from the generator, so nothing is left out of saves by accident.

### 3.2 The source generator (`Sage.Generators`)
A Roslyn incremental generator, referenced as an analyzer by every engine assembly (`Sage.Core` … `Sage.Gameplay`) and game assembly. For each annotated type it emits:

| Output | Used by |
|---|---|
| JSON reader/writer (`Utf8JsonReader`/`Utf8JsonWriter`, with line info on errors) | prefab and record files, maps, editor documents, save debug dumps |
| Binary reader/writer (tagged fields) | saves |
| Inspector metadata: field list, types, ranges, categories, and ref getters/setters (no reflection) | editor inspector (15), `ent_dump` (03) |
| I/O dispatch table: output ids, input name → method | entity I/O (04) |
| Record validator (types, ranges, reference checks) | `RecordStore` (05) |
| `Registration.g.cs`, with a single `RegisterAll(ModuleRegistry)` | called by each assembly's module `Init` |

Generator diagnostics (compile errors): a duplicate `[Saved]` tag, a duplicate component name, an unsupported field type, a missing `[Saved]`/`[Transient]` decision (warning).

### 3.3 Supported field types
- Primitives, `string`, enums.
- `System.Numerics` `Vector2/3/4`, `Quaternion`.
- `Rgba`, `SectorCoord`, `AssetPath` (written as the path string), `RecordId` (written as `ns:name`).
- `EntityRef`: written as the target's `PersistentId`. The target must have one; otherwise a dev warning, and the reference is written as null.
- Nested structs marked `[Serializable]`, fixed-size arrays, and pooled small lists.

Deliberately **not** supported: object references, delegates, `Type`, polymorphic "any object" fields. Loading therefore never instantiates arbitrary .NET types from a file, which also closes the class of deserialization exploits `BinaryFormatter` was known for.

### 3.4 Maps
A map is a folder:
```
maps/<name>/
  map.json                 header: display name, spaces (exterior + interiors), settings, default spawn, generator seed
  sectors/<x>_<z>.json     placed entities per 1024 m sector (exterior)
  interiors/<id>.json      placed entities per interior space (14)
```
- Each placed entity: `persistentId`, optional `name` (for I/O), `prefab` (`RecordId`), the components that **override** the prefab (only differing fields), and `io` connections (04 §7).
- **Built (F31, 2026-09-23):** the prefab half. `world.Populate(entity, record, id)` applies a prefab
  to an entity that already exists, which is what a map load needs after it has created one with its
  saved id. Component data is read by component id through `ComponentSchema`, the same path a save's
  per-component data will take — written against reflection now, against the generated readers later
  (§3.2), with the record pipeline's JSON dialect either way. See 05 "As built (prefabs)".
- Per-sector files match streaming (14) and keep map diffs small in git. **Later:** one file per placed entity (UE One File Per Actor) if team editing makes per-sector files conflict.

### 3.5 Saves: what's saved
- **Baselines** are the map files plus procedural generation from the seed (Daggerfall-style). They're never saved, only referenced by map name + seed + content hashes.
- **Rule: every sector (or interior) that has been loaded during the playthrough is saved in full**, meaning all its entities that have a `Persistent` component. Unvisited sectors cost nothing. This is simpler and more robust than tracking field-level deltas. Finer change tracking can come later if saves get large.
- **Destroyed baseline entities** in a saved sector are recorded as tombstones (their `PersistentId`). On load they're skipped when the baseline is instantiated.
- **Runtime-spawned entities** that should persist (dropped items, recruited NPCs) get a `Persistent` component when spawned. Everything else (projectiles, effects, corpses on a timer) is simply not saved.
- **Saved resources** (`[SavedResource]`): game rules state, time, quest log, faction standings, the
  spellbook of composed spells (16 §3.3). Built 2026-09-23 — see "As built (saved resources)".
- **Never saved:**
  - system state (systems are stateless by rule, 03);
  - delegates, coroutines or scripts;
  - subsystem internals (physics bodies are rebuilt from components on load, 10);
  - render state.

  AI "what am I doing" state is component data (current schedule record id + task index, 16), so it's saved like any other field.

### 3.6 Saves: format and versioning
```
user://saves/<slot>/
  header.json      format version, game id + version, mod list with content hashes, map, seed,
                   play time, real time, player location, thumbnail file name
  world_<name>.bin tagged binary: [saved resources] [per sector/interior: tombstones + entities]
  thumb.png
```
- **Binary blocks** are `(componentName, version, length, tagged fields…)`:
  - unknown components are skipped (a removed mod's data doesn't break the load; it's logged);
  - unknown tags are skipped;
  - missing tags get default values.
- **Upgraders** handle changes that tags alone can't express (splitting a field, changing units): `[Upgrade("health", from: 1)] static void V1ToV2(ref SavedFields f)`. They're chained `v1→v2→v3` at load.
- **Mod mismatch** between the save header and the current load order: the player is warned. The load continues if possible; missing records resolve to placeholders (05 §8).
- Saves are written to a temp folder, then renamed into place, so a crash mid-save never corrupts the previous save.
- Autosaves rotate (`save_autosave_count`, default 3).

### As built (saves v1, 2026-09-23 — F27)

- **Code:** `src/Sage.Simulation/Content/SaveSystem.cs` (slots, header, temp+rename), `SaveSerializer.cs`
  (components in and out), `SaveJson.cs` (the dialect and its three world-aware converters),
  `SaveAttributes.cs` (`[Transient]`, `FromPrefab`). Tests in `tests/Sage.Tests/Content/SaveTests.cs`.
  Console: `save [slot]`, `load [slot]`, `saves`.

**Four deviations from the plan above, each deliberate.**

| Planned | Built | Why |
|---|---|---|
| `[Saved(tag)]` opt-in | **opt-out**: everything public except `[Transient]` | There is no generator to warn about a field that decided neither way, so opt-in would silently drop a field added next month. Losing the player's inventory is worse than writing a number that could have been recomputed |
| tagged binary | **JSON** | Field names are the stable identity for now. Binary is a format change, which is what §3.6's upgraders are for — and a save you can read in a text editor is worth a lot while the save system is the thing being debugged |
| visited-sector rule + tombstones | **every persistent entity, in full** | A baseline here is the game's own records, not map files. Load recreates from the file, so a destroyed entity's tombstone is simply its absence. Both come back with maps (§3.4), because that is when a baseline starts being re-instantiated underneath the save |
| the source generator | **reflection** | As with records (05 §3.5), and it goes the same way |

- **An entity is rebuilt by spawning its prefab, then laying the saved components over the top.** That
  is what `FromPrefab` records and why `Populate` was split out in F31: a character's capsule,
  controller and intent come back because the prefab puts them there, not because they were saved.
- **Loading is two passes** — every entity with its identity first, then all the state. With every
  entity already present, a reference resolves as it is *read*, so there is no fix-up list and a
  reference nested three deep (`ActiveEffect.Source`, inside a `List<>`, inside a component) works
  like any other field. The first version of this walked a component's own fields looking for
  `Entity`, which looked reasonable and silently wrote that one as null.
- **Three things are saved by name, not by the number they are stored as**, because each number is
  assigned in record load order and means something else after a content change:
  - an entity → its `PersistentId`;
  - `Attributes` → `{ "sage:health": 65 }` (base values only — current ones are recomputed);
  - `GameplayTags` → a list of tag ids (owned only — granted ones return with their effects).

  There is a test that adds an attribute record *between* save and load, shifting every index, because
  a round trip inside one process proves nothing about this.
- **Not written at all:** anything `[Transient]` (a ground normal, an input mask, a mid-swing timer),
  `GlobalTransform` (derived, and its `Previous` is a one-tick render snapshot), `PhysicsBody` (a Bepu
  handle, rebuilt from `Collider`/`RigidBody`), and `EntityName` (a name is a string on the entity, not
  a component that also carries its own UTF-8 bytes).
- **`Remaining = +∞`** is how an everlasting effect says so, so the dialect allows named floating-point
  literals. Without that, one such effect failed the *whole* component silently.
- **The origin sector is in the file** (R6, 2026-09-24). Every position written is in **origin space**,
  so a world file starts with the sector it was relative to, and loading rebases the world to it before
  placing anything. Without that, a save taken a hundred kilometres out would load its entities into
  the starting sector — the numbers would look right and the player would be standing on somebody
  else's ground.
- **Not done here:** maps, sectors, tombstones, binary, thumbnails, autosave rotation, and the mod list
  in the header (the plugins and content arrived with issue 4i-2: "As built (a load that cannot
  half-happen)"). (Upgraders and stable ids came with issue #20: "As built (stable ids and versions)".) `GameRules` state is still not saved — the mechanism exists now, and
  nothing in the Sandbox's rules has state worth keeping yet.

### As built (stable ids and versions, issues #16 and #20, 2026-09-28)

Until this, a save keyed each component by its **C# type name**, and read back any JSON it was given
ignoring unknown fields: renaming a struct dropped its data from every save, renaming a field dropped
that field, and nothing said so. And `SaveSystem` refused any save whose `formatVersion` differed,
because no upgrader existed.

- **Components are saved under their stable id, each with a version** (save format 2):
  `"sage:transform": { "version": 1, "data": { … } }`, tags as a list of ids, saved resources as
  `"journal": { "version": 1, "data": { … } }` (test: ASaveIsKeyedByStableIdsWithAVersionPerEntry).
  The id comes from `[Component]` (03 "As built (component ids)"), so renaming the type is free.
- **`[Upgrade(fromVersion)] static void X(ref JsonObject o)`** on the type rewrites an older shape;
  a type's upgraders run in order, oldest first, from the saved version to the declared `Version`, each
  on the last one's output (test: UpgradersRunInOrderFromTheSavedVersion). The helpers are
  `RenameField`, `RemoveField` and `MoveField` (dotted paths), matching a field the way the dialect
  reads it: the exact name, then ignoring case (test: TheUpgradeHelpersRenameRemoveAndMove). Upgraders
  are found by reflection when an old entry is first read — they may be private, being the type's past
  rather than its API — and Sage.Generators checks their signature and version (`SAGE0007`). The same
  works on a `[SavedResource]`, whose attribute gained `Version`.
- **A field the type no longer has is an error**, naming the component, the field and the upgrader to
  write; that component keeps what the prefab gave it and the rest of the world loads (test:
  WithoutAnUpgraderTheErrorNamesTheComponentAndTheField). The save dialect disallows unmapped members,
  so this holds for fields of nested types too. It also means a field later marked `[Transient]` needs an
  upgrader that removes it from old saves.
- **`[Component(FormerNames = …)]`** lists ids (or, for format 1 saves, C# type names) a component used
  to be saved under, which is how a component's *id* is renamed.
- **`FormatVersion` is 2, and older formats are upgraded, not refused.** A world file is brought up to
  date in memory by a chain of format upgraders before anything reads it; a format newer than the build
  is still refused (test: ASaveFromANewerFormatIsRefused). Format 1 → 2 maps each C# type name to its
  id (ignoring case, as format 1 was read) and wraps every entry as version 1. **That step, and the
  type-name lookup behind it, are for one release**: then `SaveSystem.OldestReadableFormat` becomes 2
  and both go.
- **Golden saves** (REDESIGN §4.5): `tests/Sage.Tests/Content/Saves/format1` was written by main before
  this change (commit 77c57f0 records it) and `format2` by this format, from the same scene: a hero with
  an inventory, equipment, a spell and a curse from a goblin, a goblin mid-chase, a lamp, and the
  journal, reputation and weather. Both load, with every value checked — including the lamp, whose
  type *and* one field have been renamed since format 1 was written (tests:
  AGoldenSaveFromBeforeStableIdsStillLoads, AGoldenSaveInTheCurrentFormatLoads). A new format adds a
  folder; `GoldenSaveTests` says how to write one.
- **Never written, by type rather than by name:** a component or tag type marked `[Transient]`
  (`GlobalTransform`, `PhysicsBody`, `IOConnections`, the Sandbox's `FromScene`), the two the entity
  record carries itself (`Persistent`, `FromPrefab`), and Friflo's own components, which have no id.
  The skip list used to be C# names, which a rename would have silently emptied.
- **Fixed on the way:** an entity saved with a name but no prefab threw on load (Friflo's `Name` setter
  needs the component to exist). No test had saved one; the golden save's lamp is one.

### As built (runtime spawns persist, issue 4i-4, 2026-09-30)

A save kept only what a scene, a map or `MakePersistent` had given an id, so a dropped sword, a
summoned imp or an `ent_spawn` crate vanished on load.

- **`world.Spawn` of a prefab gives the entity a new `PersistentId`**, unless the prefab says
  `"persist": false` (`PrefabRecord.Persist`, in the schema; SAGE0131). Test: APrefabThatSaysPersistFalseIsNotSaved
  (the saved world file names the crate and not the spark). `ent_spawn` and the summon effect go through
  `Spawn`, so both are saved (tests: AnEntSpawnedCrateSurvivesASave, ASummonedCreatureSurvivesASave).
- **Dropped items and pickups** (`Items.SpawnPickup`, which `Drop` uses) are made with `Create`, so they
  call `MakePersistent` themselves (test: ADroppedItemSurvivesASave). Projectiles already did.
- **A prefab's children never get an id of their own**: only the root of a spawn does; the children it
  placed (`FromParentPrefab`) are re-spawned by the parent on load, so saving twice and loading twice
  leaves one cart and its two lamps (test: APrefabsChildrenAreNotDuplicatedByASaveAndLoad). Since 4i-3
  each child has an id *derived* from the root's (never a random one), so its state is saved and a load
  lays it onto the child the parent re-spawned ("As built (a load that reconciles)").
- **Spawns that get their identity elsewhere skip it**: a scene's placements and player, a placements
  document, a map's entities, the player camera and a saved entity on load use an internal
  `SpawnWithoutId`, so nothing is saved twice and no stale id is indexed. Adding a `Persistent` to an
  entity that already has one (`world.Add`) now replaces the id and re-indexes it, where it used to be an
  Ensure failure, because a game that spawns then names its entity is common.
- **Side effect**: a pawn spawned at runtime is saved now, so its player camera (whose id derives from the
  pawn's) is too; a pawn that should not be saved says `"persist": false`.
- Cue and particle effects are not prefab spawns in the base engine; a game's effect prefab opts out with
  `"persist": false`. A load does not yet remove an unsaved entity that is in the world already (4i-3).

### As built (a load that cannot half-happen, issue 4i-2, 2026-09-30)

REDESIGN §4.5 found three ways a load lost a playthrough: it destroyed and rebuilt one world while the
next was still unread, so a corrupt second file left the first rebuilt and the rest untouched (and a
malformed record id's `FormatException` escaped the catch altogether); an entity whose prefab was gone
was dropped; and a component or tag with no type here was skipped, then missing from the next save.

- **Read everything, then change anything.** `SaveSystem.Load` reads the header and every world file,
  parses and format-upgrades each, and reads every entity's id, prefab and name before any world is
  touched. Anything wrong there (bad JSON, no `"entities"`, a malformed prefab id, a value of the wrong
  kind) refuses the whole save and says "nothing was changed"; every world is as it was (tests:
  ACorruptWorldFileLeavesEveryWorldAsItWas, AMalformedPrefabIdIsRefusedBeforeAnythingChanges). The
  catch is wide on purpose, because nothing has changed yet when it runs.
- **A missing prefab loads as a placeholder**: an entity with a `Transform` where it stood, its
  persistent id, and `SavePlaceholder` (the prefab id and the saved entity as JSON text). It is disabled,
  so no query and no system sees it; what referred to it by id still does, and the next save writes its
  saved JSON back unchanged. When the prefab comes back, so does the entity, with its state (test:
  AnEntityWhosePrefabIsGoneIsKeptAsAPlaceholderUntilItComesBack). Saving and clearing include disabled
  persistent entities, so a second load replaces placeholders rather than doubling them.
- **A component, tag or saved resource this game has no type for is kept**: `UnknownSavedData` holds an
  entity's unknown component entries (`{ "version", "data" }` as saved) and tag ids, and the save
  merges them back; what the entity has now wins over a kept entry of the same id. Unknown resources are
  kept per world the same way (test: AnUnknownComponentTagAndResourceAreWrittenBackUnchanged).
- **The header lists `plugins`** (each runtime plugin's id and version) **and `content`** (each mount's
  name and record namespace, in priority order). A load compares them with what is loaded now, warns
  with one line per difference, and loads anyway; `SaveSlot.Plugins`, `Content` and `Mismatches` give a
  menu the same (test: TheHeaderListsPluginsAndContentAndAMismatchWarnsButLoads). A header from before
  this has neither list and compares as matching. Content is listed by mount, not hashed: hashing every
  file at every save costs too much for what a warning needs.
- **The format stays 2**: the new header keys are ignored by older readers, and both golden saves load
  unchanged. Format 3 comes with reconciling loads (4i-3). All of this is experimental (SAGE0131).

### As built (a load that reconciles, issue 4i-3, 2026-09-30)

Until this a load destroyed every persistent entity and rebuilt only what the file listed — the v1
deviation "every persistent entity, in full" above. So a placement added to the content after the save
was destroyed by the load and never came back; entities from placements documents and `.map`s had no
id at all, so a map pickup the player had taken was back on the floor after a load (while also in the
pack), and a map counter's value was lost.

- **Everything content places has a stable id** (`ContentBaseline.cs`, `ContentIds`): a scene
  placement keeps the formula it always had (`scene:<scene>:<index>:<prefab>`, so the golden saves and
  any format 2 save still find it); a placements document's is `placements:<doc>:<index>:<prefab>`; a
  `.map` entity's is its `id` key, else its `targetname` when no other entity in the level shares it,
  else the level and its index. A placement's authored **`id`** (new, `Placement.Id`) wins over the
  derived one, so reordering the list moves nothing (test: AnAuthoredIdSurvivesTheContentBeingReordered);
  a GUID is used as written, other text is scoped to its source. Two placements with one id, or two
  children of one prefab with one name, are content errors. A **prefab's child** gets its parent's id
  plus its key (`name:<name>`, else `<index>:<prefab>`, held by the transient `PrefabChildKey`), assigned
  whenever the parent gets one (`ContentIds.Assign`). No id is shared, before a save or after a load
  (test: NoPersistentIdIsDuplicated).
- **By source.** Each id is recorded against what placed it — `scene:<id>`, `placements:<id>`,
  `map:<id>`, and later a streamed sector (4g) — and a save writes, per source, a **tombstone** for every
  one the game destroyed. A source that is cleared (a hot reload, `scene_load`, a level unloaded) keeps
  its dead as pending tombstones, and a source placed again removes its own; a source not placed yet (a
  level waiting for the ground under it) keeps the save's state for it pending, a save writes that back
  unchanged, and it is laid on when the level spawns (test:
  StateForALevelNotYetPlacedWaitsForItAndIsWrittenBack).
- **A load reconciles.** After the atomic read (4i-2), each world: (1) destroys what the game made
  (persistent entities no live source placed: the player, drops, placeholders); (2) clears what content
  placed and places the save's scene again from the content *as it is now* (`Scenes.Replace`), with the
  save's tombstones waiting for their sources, and spawns every placeable level's entities at once;
  (3) matches each saved entity by id — one content placed again gets the saved state laid on (its tags
  exactly the save's, its components over the content's, and its transform through `World.Teleport` so
  a physics body starts where it was saved); a saved one whose source is live but no longer places it
  is dropped; everything else is spawned from its prefab as before, and **a prefab's children are found
  under their parent by their derived ids, never spawned a second time** (test:
  APrefabsChildrenAreNotSpawnedTwice); (4) the scene's player, then the saved resources and AfterLoad.
  Tests: AnEntityAddedToTheContentAfterASaveIsThereAfterTheLoad, AKilledPlacedEntityStaysDeadAcrossAReboot
  (a fresh app), AMapPickupTakenBeforeTheSaveIsNotBack, AMapLogicCounterAtTwoOfThreeIsAtTwoAfterTheLoad.
- **Hot reload shares the path**: `Scenes.Respawn` is `Scenes.Replace` too, so a reload places the new
  content and what the game destroyed stays destroyed (test: AHotReloadLeavesTheDeadDead). It does not
  lay the live state back over the new content: a reload is for seeing an edit, and the old health laid
  over a prefab whose health was just changed would hide it.
- **Format 3**: a world file has its `scene`, each content-placed entity its `source`, a prefab child the
  game spawned its `parent`, and `tombstones` by source. **From2** marks an older file "absent means
  destroyed" — it listed every persistent entity there was — so a scene placement it does not list is
  removed after the scene is placed again (a child had no id then, and is not counted); what it lists is
  found by id (test: AFormat2SaveOfASceneKeepsItsDeadDead). The format 1 and 2 golden saves still load.
- **Not yet:** components are still written in full, so a rebalance does not reach a placed entity from
  an existing save, and a component the save lacks is left as the content made it rather than removed
  (both since closed by 4i-5, below); `sage validate` does not yet warn about index-derived ids (the plan's decision 4),
  and the FGD does not offer the `id` key.

### As built (save what changed, issue 4i-5, 2026-09-30)

Every public field of every component was written in full, so a prefab or a record rebalanced after a
save never reached an entity from it: the old goblin's health came back over the new prefab's (the 4i
plan's gap 4). Decision 1 of the plan: diff against the prefab as it was spawned, rather than make every
saved field opt-in.

- **The baseline** (`SaveDiff.cs`): a prefab spawn notes the entity's components, serialized in the save
  dialect, once its prefab — with the placement's or the map's overrides, or the parent prefab's body
  for a child — is applied, and before a name, an id or children are added (`SaveSystem.NoteSpawned`,
  held by `FromPrefab`). It is taken once per prefab and overrides in each world and shared; after the
  records are loaded again the next spawn takes a new one, and what was spawned before keeps its own.
- **A save writes what differs**: an entity with a baseline says `"diff": true`; a component the game
  left alone is not written; of one it changed, only the top-level fields that differ are (a list, a
  dictionary or a nested struct is one field, written whole); a component the prefab did not give it is
  written in full; and **`"removed"`** lists the baseline's components the entity no longer has. Tags
  are written in full. The transform is always written whole (test: ASaveWritesOnlyTheFieldsTheGameChanged).
- **A load applies the current prefab, then the diff**: the entity is spawned (or placed again by
  content, 4i-3) from the prefab as it is now; each saved entry is laid over it field by field
  (`SaveDiff.Merge`, through the dialect's own field contract); `removed` is taken off again; its tags
  are the save's exactly. So an untouched goblin gets the raised prefab hit points and the raised
  record health, and a damaged one keeps its damage and gets the new maximum (test:
  AnUntouchedGoblinGetsTheRaisedPrefabHealthAndADamagedOneKeepsItsOwn). Overrides and a child's body are
  part of the baseline: an overridden field stays overridden and the rest follow the prefab (test:
  OverridesAndAChildsBodyArePartOfWhatIsDiffedAgainst). A component the game added and one it took off
  both come back as it left them, on a placed and on a spawned goblin, and so does a tag it took off
  (test: AnAddedAndARemovedComponentRoundTrip); a save of a loaded game is the same diff again (test:
  ASaveOfALoadedGameStaysADiff).
- **A runtime spawn is rebuilt where it was saved** (its saved position and rotation), not at the origin,
  so a part that reads its placement reads what it read when the diff was taken.
- **The format stays 3**: only an entity marked `diff` is merged; any other entry replaces its component
  as before, so formats 1, 2 and 3 without diffs read as they did. A format 3 golden save written as
  diffs is committed and loads (test: AGoldenSaveInTheCurrentFormatLoads), beside formats 1 and 2 (tests:
  AGoldenSaveFromBeforeStableIdsStillLoads, AGoldenSaveInFormat2Loads).
- **Limits**: the baseline is shared, so a value a part derives from the placement (a mover's closed
  position) differs from the first spawn's on every other placement and is written, and does not follow a
  rebalance. An `[Upgrade]` method sees only the fields a diff wrote. A placed entity's transform is
  always written, so moving a placement in the content does not move one from an existing save. A
  component whose JSON is not an object of fields (a custom converter) is written whole when it differs.

### As built (quick-save and autosave, issue 4i-6, 2026-10-01)

`save` and `load` ran whenever they were called, and the only way in was the console or the Sandbox's
menu. Now a save can be asked for from anywhere and still never runs mid-tick, F5 and F9 quick-save and
quick-load, and autosaves rotate (`SaveRequests.cs`, experimental SAGE0131).

- **A save runs at a tick boundary.** `RequestSave`, `RequestLoad`, `QuickSave`, `QuickLoad` and
  `Autosave` asked for during a world's tick (a system, a trigger, a key read in Commands) are queued and
  run when that tick ends: `World.RunFixed` calls the save system once every phase is done. Asked for
  between ticks (the console, a menu) they are already at a boundary and run at once. `Save` and `Load`
  called mid-tick become requests the same way, and return true for "asked". So a save asked for in
  Gameplay is not written by Late, and what Late changes is in it (test:
  ASaveAskedForMidTickRunsAtTheTickBoundary).
- **Several in one tick: the saves first, then one load.** Every request was made about the tick that
  just ran, so the saves record it and the load replaces it. Two saves to one slot are one write (the
  later kind wins), two loads are the last asked for, and there is one autosave at most (test:
  SavesAskedForInOneTickRunBeforeTheLoad).
- **F5 and F9** are the engine's `QuickSave` and `QuickLoad` button actions, registered by the Engine and
  bound in engine content's `gameplay` and `ui` input maps. A game moves them with its own `input_map` or
  a patch of those (plan decision 7). `QuickSaveKeysSystem` (`sage.saves.quick_keys`, Commands) reads
  them from the tick's `PlayerCommand`, not from a pawn, so they work with no player and while a screen is
  open. They write and read the `quick` slot, as the `quicksave` and `quickload` commands do; `save` with
  no slot is a quick-save (test: AQuickSaveThenAQuickLoadRoundTrips).
- **Autosaves** go into `autosave1`…`autosaveN` (`save_autosave_slots`, default 3): the first slot that
  does not exist, then the one written longest ago (test: AutosavesRotateAndReuseTheOldestSlot). One is
  taken every `save_autosave_interval` seconds of simulation time (default 300; 0 turns the timer off),
  counted on the engine's first world from the last save or load of any kind. Another is taken at the end
  of the first tick after `Scenes.Load` (`scene_load`), so it records the new scene settled, and once
  however many worlds changed. A load is not a scene change. `save_autosave 0` stops both;
  `Autosave()` and the `autosave` command still work (test: AnAutosaveRunsOnASceneChangeAndOnTheTimer).
- **A slot says its kind.** The header has `"kind"` (`quick`, `auto` or `manual`), `SaveSlot.Kind` reads
  it, and `saves` lists it. A header from before has none, and that reads as Manual (test:
  SlotsReportTheirKind).
- **`Delete(slot)`** (and `save_delete`) removes a slot's folder and its staging folder, and the listing
  follows (test: DeletingASlotRemovesIt).
- **The Sandbox's main menu** gives each row Overwrite and Delete buttons and shows a quick-save's or an
  autosave's kind. A click on the rest of the row still loads it (test: TheMainMenuOverwritesAndDeletesASlot).
- **Limits.** A tick boundary is per world: with two worlds, a request runs when the world that asked
  ends its tick, which is a boundary for that world and the one between ticks for the other. F5 and F9
  are read in a tick, so a game paused with no ticks does not hear them; the console and a menu run
  between ticks and are not held up.
  The slot names are fixed (`quick`, `autosave<n>`); a slot the player names `autosave2` takes part in
  the rotation.

### As built (the 4i exit game, issue 4i-7, 2026-10-01)

Phase 4i's exit (REDESIGN §5, issue #167): save anywhere, change a prefab and a record, load: nothing
lost and nothing duplicated. `tests/games/saves` proves it headless, with no C#
(`tests/Sage.Tests/Games/SavesExitTests.cs`).

- **The game.** A scene, `camp`, places three goblins with authored ids (`doomed`, `wounded`, `spared`)
  and a floor, and loads a `.map` (`maps/camp.map`, hand-written, no brushes) with a ring on the floor
  and a `logic_counter` tally, each with an `id` key. The ring's `OnPickedUp` and the doomed goblin's
  `OnDeath` (a scene placement's wire to a map entity) each add one to the tally. A goblin is the
  engine's character and attributes with an instant effect, `goblin_build`, that sets its health to 30;
  the player carries a sword (12 a swing), a bow whose arrow flies straight at 15 m/s for 300 m, arrows
  and bread. Plugins: maps, entity I/O, character, attributes, combat and items, and the RPG kit for
  the main hand.
- **The patch.** `tests/games/saves/rebalance/` is not one of the game's mounts: mounted after it in
  its own namespace, as a mod or an update is, it patches the goblin prefab to a new effect that sets
  60, and adds a fourth goblin (`newcomer`) to the scene with `place+`.
- **The run.** Through `PlayerCommand`s, as the client's input is: three swings kill the doomed goblin
  (12, 12, 6), one leaves the wounded one at 18, the spared one is left alone, Use takes the ring (the
  tally is at 2), bread is dropped, an arrow is loosed into the sky, and F5 quick-saves. The save
  writes the spared goblin with no `sage:attributes` (nothing changed, 4i-5), the wounded one's, and a
  tombstone for the ring under `map:saves:camp`. A fresh app with the patch boots (every goblin at 60,
  the newcomer placed, the ring back on the floor) and F9 quick-loads (test:
  SavesExit_AQuickSaveSurvivesARebalanceWithNothingLostOrDoubled):
  - every persistent id the save had is there once, and the newcomer's: nothing doubled, nothing lost;
  - the doomed goblin is dead (the dead tag, 0 health), the wounded one keeps 18, the spared one has
    the patch's 60, and so does the newcomer;
  - the ring is in the pack and not on the floor; the dropped bread is where it fell, and the pack has
    the one left;
  - the arrow is back with its id, where it was, still the player's, and flies on after the load;
  - the tally is at 2, and an `Add` takes it to 3.
- **Validated** alone and with the patch (test: TheSavesGameValidates); CI runs `sage validate` both
  ways and includes the game in `sage schema`.
- **No engine change was needed.** Two things the exit showed that are not 4i's: a data-only game whose
  map or placements carry wires must list `sage.gameplay.io`, and nothing (`sage validate` included)
  says so when it does not, so the wires silently never fire; and a test that leaves a `PlayerCommand`
  in `PlayerInput` with a button pressed presses it again on the next tick (F9 then loaded twice).

### As built (saved resources, F21/F27, 2026-09-23)
A world is not only its entities. The first thing that proved it was the spellmaker (16 §3.3): the
spells a player composed are the *world's*, not any one entity's.

- **`[SavedResource("name")]`** on a world-resource class (03 §3.4), registered for the plugin that owns
  it (`Plugin = "…"`, issue #16) by generated code just before that plugin's `Init` — so a mod's resource
  is only in the save when the plugin that reads it back is loaded. The attribute's *name* is the key in
  the file, so renaming the class is free.
- Written into the world file beside `entities`, read back in the same JSON dialect, and installed with
  `world.Resources.Set`.
- **A load replaces every registered resource, including the ones the file says nothing about.** Absent
  means "you had none", not "keep what you have" — otherwise a spell composed since the save would
  survive loading, which is a different game.
- **`ISavedResource.AfterLoad(world)`** for a resource whose data implies something to rebuild. The
  spellbook's drafts become `ability` records again through exactly the code path that composes one in
  the first place, so **no `AbilityRecord` is ever serialized** and a rebalanced effect changes a
  player's old spell instead of being frozen into it (records made at run time: 05 "As built").

## 4. Public API sketch

```csharp
[AttributeUsage(AttributeTargets.Struct)] public sealed class ComponentAttribute : Attribute { public ComponentAttribute(string name); public int Version { get; init; } = 1; }
[AttributeUsage(AttributeTargets.Field)]  public sealed class SavedAttribute : Attribute { public SavedAttribute(int tag); }
[AttributeUsage(AttributeTargets.Field)]  public sealed class TransientAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Field)]  public sealed class PropertyAttribute : Attribute { public float Min, Max; public string? Category, Tooltip; }

[Component("health", Version = 1)]
public struct Health
{
    [Saved(1), Property(Min = 0)] public float Current;
    [Saved(2), Property(Min = 1)] public float Max;
    [Transient] public float LastDamageTime;
}

public sealed class SaveSystem                             // per Engine; as built (2026-09-23)
{
    public string Root { get; set; }                       // default user://saves; settable for tools and tests
    public bool Save(string slot);                         // every world in the engine, at a tick boundary
    public bool Load(string slot);                         // rebuilds each persistent entity from its prefab + saved state
    public bool Exists(string slot);
    public IEnumerable<(string Slot, DateTime SavedUtc, int Entities)> List();
    public void RegisterResource<T>() where T : class, new();   // what the generated registration calls (issue #16)
}                                                          // richer result types (SaveResult/LoadResult) when a caller needs them

public interface ISavedResource                           // optional, for a resource that implies more
{
    void AfterLoad(World world);                          // e.g. a spellbook's drafts become records again (16 §3.3)
}

public interface IComponentSerializer<T> where T : struct  // generated
{
    void WriteJson(Utf8JsonWriter w, in T value);
    bool ReadJson(ref Utf8JsonReader r, ref T value, in SerializeContext ctx);   // ctx: file, line, EntityRef resolution
    void WriteBinary(ref BinaryWriterRef w, in T value);
    void ReadBinary(ref BinaryReaderRef r, ref T value, int version, in SerializeContext ctx);
}
```

## 5. Lifecycle

```
save:  (tick boundary) → header → saved resources → for each visited sector/interior:
         tombstones + persistent entities (prefab id + [Saved] fields of every component) → temp → rename
load:  read header (check game, mods) → destroy worlds → create world → instantiate map baselines for
         saved sectors, skipping tombstones → apply saved entities (by PersistentId: overwrite or create)
         → saved resources → resolve EntityRefs from PersistentIds → subsystems rebuild (physics bodies, 10)
         → GameRules.OnLoaded
```

## 6. Threading and memory
- Saving snapshots component data on the main thread at a tick boundary (fast: a copy into pooled buffers). Compression and disk writing then run as a background job, so the game doesn't hitch.
- Loading happens behind a loading screen. Parsing can run in jobs; instantiation runs on the main thread.
- Generated serializers don't allocate for primitive and struct fields. Strings allocate on read (acceptable at load time).

## 7. File formats
Summarised above:
- JSON for authored data (prefab/record bodies, maps, editor documents);
- tagged binary + a JSON header for saves;
- `save_dump <slot>` converts a save to JSON for debugging.

## 8. Errors and fallbacks
| Failure | Behaviour |
|---|---|
| JSON parse/type error in a map or prefab | `Error` with file:line:field; that entity (or record) is skipped, the rest loads |
| `EntityRef` to an entity without a `PersistentId` at save time | Dev warning naming both entities; saved as null |
| Save load: an unknown component (mod removed) | Skipped, listed once in the log (`Save`, `Warn`) |
| Save load: a missing upgrader for an old version | That component uses defaults; `Error` logged |
| Corrupt save | Load aborted cleanly, `Error`; the game stays where it was |
| Crash during save | The previous save is untouched (temp + rename) |

## 9. Debug and tooling hooks
- **Commands:**
  - `save <slot>`, `load <slot>`;
  - `save_dump <slot>` (JSON);
  - `ser_check` (round-trips every component type through JSON and binary, and reports differences; dev);
  - `ent_dump` uses the generated inspector metadata (03).
- **Cvars:** `save_autosave_count`, `save_compress`.
- **Log category:** `Save`.

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `EntityOutlinerWindow` inspector: runtime reflection over components, and a label string per listed entity per frame (allocating — open bug #41) | Generated inspector metadata (15) |
| `EditorUI` File → New/Open/Save stubs | Map documents (15) + `SaveSystem` |
| Random `long` ids (old `EntityContext.createEntity`) | **Done (step 3):** Friflo handles at runtime + `PersistentId`/`Persistent` for anything saved or placed (03) |

## 11. v1 scope vs later
- **v1:**
  - the attributes;
  - the generator (JSON, binary, inspector metadata, registration, I/O tables, record validation);
  - maps as per-sector/interior JSON;
  - saves with the visited-sector rule, tombstones, saved resources, tagged binary, a header with mods, temp+rename;
    **As built (2026-09-23):** saved resources, the header and temp+rename are in; the visited-sector
    rule and tombstones wait for maps to exist at all; tagged binary and the mod list in the header are
    deliberately deferred (see the deviations above). What replaced the first two is simpler: every
    persistent entity written in full, so a destroyed one's tombstone is its absence;
  - `ser_check`.
- **Later:** finer change tracking, one-file-per-entity maps, save thumbnails, compression tuning.
  (Upgraders arrived with the first format change, issue #20: "As built (stable ids and versions)".)

## 12. Multiplayer-later notes
The same generated metadata gains a `[Replicated]` flag and a quantization hint, producing delta encoders for snapshots. That's the whole reason to have one declaration (readiness rule 7).

## 13. Open questions
- If Friflo is adopted (03 §3.1), does its component storage let our generated serializers read and write components directly (by ref)? The spike must confirm (requirement E7). Friflo's own JSON serializer would then go unused.
- Binary compression: none, Deflate (built in), or a faster codec? Start with `System.IO.Compression` Brotli/Deflate at the fastest level; measure.

### Sharp edge, closed: `[SavedResource]` used to save nothing on its own
The attribute named a resource in the save file but did not put it there: a type was only written and
restored once something called `Engine.Saves.RegisterResource<T>()` — and reputation, the journal and the
weather were all marked saved and none of them were (found by F40's second pass, 2026-09-24). Since issue
#16 the attribute is the registration: Sage.Generators writes the call for the owning plugin
(`Plugin = RegistrationOwners.Core` for the engine's `Weather`), test: TheSandboxRegistersTheRecordTypesItDidBeforeByTheSamePlugins.

There is no warning for this, because nothing can tell the difference between "a resource the game does
not want saved" and "a resource somebody forgot to register". The test is the check: a save round trip
per saved resource, asserting the value comes back.

## 14. Build steps
1. Attributes + `Sage.Generators` with JSON read/write and registration; the `ser_check` round-trip test (TODO R11, R4).
2. Inspector metadata output (with 15).
3. Map files: per-sector JSON, `PersistentId`, prefab overrides (TODO F27, with 14).
4. ~~`SaveSystem`: header, saved resources, temp+rename~~ **Done 2026-09-23** (TODO F27, "As built (saves v1)"): as JSON, with every persistent entity written in full. The visited-sector rule and tombstones arrive with maps; tagged binary is a format change, which is what the upgraders in §3.6 are for.
5. I/O dispatch tables (with 04, TODO F17).
