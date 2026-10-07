# 05 — Assets, VFS and Data Records

## 1. Purpose and scope
Where content comes from and how it's loaded:
- the **virtual file system** (mounts, priorities, the writable user folder);
- **assets** (textures, meshes, sprite sheets, sounds, effects): identity, handles, async loading, lifetime, hot reload, placeholders;
- **data records** (items, spells, materials, input maps, prefabs…): the one pipeline for every definition, with load-order merging.

Not in scope: what the renderer does with a texture (06), effect compilation details (07), map and save files (09), mod management UI (17).

## 2. Research basis
- Layered search paths / paks: Quake 3 pk3 + `fs_game`, Source `gameinfo.txt` SearchPaths (survey §1.1, §1.3).
- Content as records with load order, and why whole-record overrides ("rule of one") cause mod conflicts: Bethesda (survey §1.5).
- Additive mods instead of whole-module replacement: Warband (survey §1.6).
- Authoring vs runtime formats; stable identity (Unity GUIDs, Godot paths + `.uid`); handles for hot reload: survey §2.3, §2.4, §3.4.
- Data-driven templates: Dungeon Siege (survey §3.2).
- MonoGame specifics (verified 2026-09): `Texture2D.FromStream` uses StbImageSharp (png/jpg/bmp/gif, and tga too, despite its documentation: "What can be hot reloaded" below) with an optional premultiply processor. 3.8.5 adds a code-centric C# content builder (survey §3.7).

## 3. Concepts

### 3.1 Virtual file system
- A **mount** is a folder or a `.pak` (a renamed zip, like Quake 3's pk3), with a priority. Later mounts **shadow** earlier ones for the same path.
- **Mount order:** engine content → framework content → `game.json` mounts → mods in load order (17) → (dev only) `dev_override/`.
- **`VirtualPath`:** forward slashes, lower-case, no leading slash: `textures/creatures/goblin.png`. Case-insensitive lookups, so Windows and Linux behave the same.
- **Enumeration merges across mounts:** `Vfs.Enumerate("data/items", "*.json")` returns every matching file from every mount, in mount order. That's how records from all mods are found.
- **`user://`** is a separate, **writable** root for config, saves, logs and screenshots:
  - dev builds: `<repo>/user/<gameid>/`;
  - otherwise: `%LOCALAPPDATA%/Sage/<gameid>/` (Windows) or the XDG equivalent.

  Read mounts are never written to (Source's `mod_write` and Godot's `user://` separate these the same way).

### 3.2 Asset identity
- **Identity = `VirtualPath`.** A mod replaces the goblin texture by shipping the same path. No manifest is needed (Quake/Source behaviour).
- **`AssetPath`** is an interned `VirtualPath` (an `int` into a global path table). It's cheap to store in components and compare, and it doesn't load anything (03 §10).
- **Renames** are handled by the editor's refactor command (15), which rewrites references in records, prefabs and maps. If renames become painful, add Godot-4-style `.uid` sidecar files later: a stable id per asset that resolves to its current path. References stay path-based either way.

### 3.3 Handles, scopes and lifetime
- **`AssetRef<T>`** is a small struct (slot index + generation) pointing at a loaded (or loading) asset. It is **not** ref-counted by itself; copying it is free.
- **`AssetScope`** owns loads. Loading "into" a scope adds a reference; disposing the scope releases all its references at once. Typical scopes:
  - `Engine` (fonts, error placeholders, core shaders: never released);
  - per `World`;
  - per level/sector (14);
  - per UI screen.
- **Lifetime:**
  1. While any scope holds it, the asset stays loaded.
  2. At zero references it moves to an LRU cache (budget `asset_cache_mb`, default 256).
  3. It's evicted when the budget is exceeded.
  4. A handle to an evicted asset reports `Unloaded`, and `Get` returns the placeholder with a dev warning.
- **States:** `Loading → Loaded | Failed`, then later `Unloaded`. Consumers either check `IsReady` or just draw. Loading and failed assets render as placeholders, so nothing ever has to null-check.

**As built (asset scopes, 2026-10-06, issue #308).** Not the `AssetServer` and `AssetRef<T>` sketched above,
but the part of it that frees memory. The bookkeeping is headless in `Sage.Simulation`
(`Content/AssetScopes.cs`: `AssetTable`, `AssetReleases`, `UploadBudget`, `SectorAssets`); the client's
`ContentService` loads meshes and textures by id into it, and `AssetScopeSystem` (`sage.client.asset_scopes`,
first in FrameUpdate) is the frame's safe point where things are freed. Tests:
`tests/Sage.Tests/Streaming/AssetScopeTests.cs`.
- **Four scopes:** `Engine` (placeholders, engine shaders: never released), `Game` (kept for the process:
  the viewmodel, a world that does not stream, the post chain; `ContentService.LoadTexture` loads here),
  `Sector` (a streaming world's content) and `Ui` (a screen's pictures). Asked for at two, the stronger
  wins, and ids stay stable (test: TheTableKeepsIdsStableAndEvictsByScope).
- **Release.** `SectorAssets` counts how many live entities hold each mesh and texture; when a sector
  unloads, what it let go of is freed at the next safe point unless a live entity in any world still holds
  it (test: ASectorsAssetsAreReleasedWhenItUnloadsAndSharedOnesAreKept), or something takes it up again
  first (test: AnAssetTakenUpAgainBeforeTheSafePointIsKept). A `Ui` texture no screen drew for 300 frames
  (`AssetScopes.UiKeepFrames`) is evicted. Loading and unloading fifty sectors returns the texture and mesh
  counts to their baseline (test: LoadingAndUnloadingFiftySectorsReturnsTextureAndMeshCountsToBaseline).
- **Upload budget.** `Sector` loads keep to `asset_upload_ms` a frame (Archive, default 2, 0 to 100; 0 is
  no budget); the first load of a frame always starts, and what does not fit waits for the next frame
  (test: TheUploadBudgetSpreadsStreamedLoadsOverFrames). `asset_list [filter]` lists every mesh and texture
  with its scope, holders and size, then what was evicted and what the budget deferred.
- **Not yet:** sounds and effects are never evicted; material texture loads are not budgeted; decoding runs
  on the main thread; there is no `AssetRef` handle, and a `MeshHandle` used after `DestroyMesh` may draw
  whatever reused its slot; no LRU cache keeps a released asset warm (`asset_cache_mb` is not a cvar).

### 3.4 Loaders and the sim/client split
- An `IAssetLoader<T>` is registered by a module for one or more extensions. It has two stages:
  1. **Decode:** worker thread, pure CPU (parse glTF, decode PNG, read WAV), producing a CPU-side payload.
  2. **Finalize:** main thread; e.g. create the `Texture2D`/`VertexBuffer`. Runs from a **budgeted upload queue** (`asset_upload_ms`, default 2 ms per frame) so streaming never hitches the frame.
- **Simulation-side** loaders live in `Sage.Simulation`: collision meshes, navmeshes, heightmap data, animation clips.
- **Client-side** loaders live in `Sage.Client`: textures, GPU meshes, sprite sheets, effects, sounds.

  A headless run (tests, a future server) simply has no client loaders registered, and render-only `AssetPath`s in components are never resolved.

**v1 asset types:**

| Type | Source format | Loader | Notes |
|---|---|---|---|
| `Texture` (MonoGame `Texture2D`) | `.png`, `.jpg` | `Texture2D.FromStream` + `PremultiplyAlpha` processor, or the cooked `.sgtex` beside it (§7) | `.tga` loads too (StbImageSharp) and cooks since #317 |
| `SpriteSheetData` | **built as the `sprite_sheet` record** (§3.5), not a `.sheet.json` asset (12 "As built") | **sim** | Timings and events are needed by the simulation (melee "hit" frames, 12). The texture is loaded separately |
| `SpriteSheet` | `.png` + its `SpriteSheetData` | client | the Daggerfall-style creature/NPC sprites (06, 12) |
| `Mesh` | `.glb` | SharpGLTF → our vertex/index buffers, one part per primitive | **built (R12)**: `MeshGeometry.ReadGlb` (`Sage.Simulation`, headless; the client's `GltfLoader` until issue #302 moved it, §10), or the cooked `.sgmesh` beside the file (§7); node transforms baked in, winding flipped once on load. **Binary only** — a text `.gltf` keeps its buffers in files beside it, which a mount cannot always hand over, so it is refused rather than half-loaded (§8). Material slots still to come |
| `Effect` | `.mgfxo` (compiled from `.fx`, 07) | `new Effect(device, bytes)` | |
| `Sound` | `.wav`, `.ogg` | `SoundEffect.FromStream`; an `.ogg` is decoded whole to PCM by `OggVorbis` (NVorbis, issue #302) | **built:** mono or stereo, up to 600 s per file, anything else refused (11 §3); streaming long audio is not built |
| `Heightmap` | 16-bit `.png` or `.r16` | sim | terrain (14) |
| `CollisionMesh` | `.glb` (a collision node) | sim | physics (10) |
| `Level` | `.map` (TrenchBroom) | **sim** | **built (F16)**: `MapFile` + `BrushGeometry` read it engine-side, straight off a mount — brushes become hulls and meshes, `classname` becomes a prefab (15 §10a). A level is named by a `map` record, so a mod replaces one like any other file |

### 3.5 Data records
One pipeline for **every** definition: items, spells, creatures, factions, loot tables, materials (07), input maps (08), sprite animation sets, and **prefabs** (a prefab record's body is component data, parsed by the serializer from 09).

### As built (declared record types, 2026-09-28 — issue #16)
- A record type is **declared, not registered**: `[Record("item", Plugin = "sage.gameplay.items")]`. `src/Sage.Generators` (a Roslyn incremental generator every engine and game project takes as an analyzer) writes each assembly's registrations, grouped by owning plugin, and the module manager runs a plugin's just before its `Init` — so a type exists for exactly the plugins that are loaded (test: APluginThatIsNotLoadedRegistersNothing), with the same seals and ledger as a hand-written call.
- An assembly with one `[Plugin]` (a game) owns what it declares without saying so. Otherwise a declaration without `Plugin` is a build error (`SAGE0001`), as is one the registration can't construct (`SAGE0002`) and two of one id (`SAGE0003`) (test: WithSeveralPluginsADeclarationMustSayWhichOwnsIt). The engine's own types use `RegistrationOwners.Core` and are registered when the `Engine` is made.
- Record type names are still the short names (`"type": "item"`); namespaced type ids, components and systems are the rest of Phase 2 (REDESIGN §3.4).

### As built (records made at run time, 2026-09-23 — F21)
Not every record comes from a file. A spell the player composed in the spellmaker (16 §3.3) is an
`ability` record that no mount defines, and later a generated quest will be the same shape.

- `records.AddRuntime<T>(id, record)` / `RemoveRuntime<T>(id)` / `RuntimeRecords`. They are kept in a
  layer of their own and **re-applied after every load and hot reload**, because a reload rebuilds the
  record set from disk and there is nothing on disk to rebuild a player's spell from.
- `RemoveRuntime` only withdraws what was added at run time, so it can never take a record a file
  defines with it.
- **It is a cache, not a store of record.** The data behind such a record lives in the save (09
  "As built (saved resources)"), and the record is composed from it again on load. Nothing serializes
  a record.
- Composed records take a **namespace of their own** (`custom:`, or the RPG kit's `rpg_conventions`
  `spellNamespace` since #27), so nothing a player made can shadow content, and `rec_list` shows at a
  glance which is which.

### As built (prefabs, 2026-09-23 — F31)

- **Code:** `src/Sage.Simulation/Content/Prefab.cs` (`PrefabRecord`, `IPrefabPart`, `PrefabPartAttribute`,
  `PrefabRegistry`, `world.Spawn`), `ComponentSchema.cs` (components and tags by stable id, issue #16),
  `src/Sage.Gameplay/PrefabParts.cs` (the parts the engine's plugins declare). Tests in
  `tests/Sage.Tests/Gameplay/PrefabTests.cs` and `PrefabPartTests.cs`.
- **A prefab is a record**, so `base` inheritance, per-field patching, load order, validation and hot
  reload all come from §3.5 for nothing. A `goblin_chief` can `base` a `goblin` and override one
  field of one component, because the merge runs on the JSON tree before anything is deserialized.
- **Its body is two halves.** `"components"` is component data by component id, applied as written
  (by C# type name until issue #16; a bare name is the prefab's namespace, then `sage`: 03 "As built
  (component ids)").
  `"parts"` are named setups a **plugin declared** — for the cases that are not one component.

  ```json
  { "type": "prefab", "id": "goblin", "base": "creature", "name": "goblin",
    "components": { "sprite_renderer": { "sheet": "goblin", "size": [1.6, 1.9] } },
    "tags": ["hostile"],
    "parts": { "character": { "layer": "enemy" }, "attributes": {},
               "melee": { "attack": "claw" }, "effects": ["tough_hide"] } }
  ```

- **Why two halves and not one.** A `SpriteRenderer` is data. "Make this a character" is a collider,
  a controller, an intent and a pawn that have to agree about radius, height and layer, so it is a
  part `CharacterModule` owns. The test is whether a game could get it wrong by writing the components
  itself: if yes, it is a part. This is what stops the record becoming the god-object the Sandbox's
  `spawn` record was — **adding a feature declares a part, it does not edit this record.**
- **Games and mods declare their own** the same way the engine does (see "As built (declared parts)"
  below): `[PrefabPart("loot")] public sealed class LootPart : IPrefabPart { … }`.
- **Component names come from the ECS schema**, which Friflo builds by scanning loaded assemblies, so
  a component type is addressable the moment it is declared — nothing to register, and a game's own
  components work. The schema is read on **first use**, never when the `Engine` is constructed, because
  the host loads the game assembly after that (03 §3.1). Names match case-insensitively.
- **Bare record ids inside a body** (`"attack": "claw"`) mean one in the prefab's own namespace, the
  same rule record fields follow. The pipeline qualifies its own fields at merge time (review #56) but
  cannot see inside a component body, whose shape isn't known until the component type is, so `Populate`
  sets the parse namespace around the whole build.
- **Order:** components, then tags, then parts in the registry's declared order (below; until issue
  #17 it was the order modules registered them). The placement transform is set **before** the body (parts read it — `character` seeds the pawn's yaw
  from it, review #43) **and again after**, so a prefab may carry a `Transform` for a scale it always
  wants without deciding where this one went.
- **Failure is local.** An unknown component, tag or part costs that one thing and says so with the
  prefab id; the rest of the entity still comes up. An unknown *prefab* costs the spawn.
- **Known limitation:** parts run after components, so a part wins where both touch the same
  component — a prefab cannot shrink the capsule `character` builds by writing a `Collider`. The
  alternative (components last, as overrides) would let data silently break a character, so parts win
  until a part needs options for it. *Since phase 4i a placement overrides the part's options instead
  ("As built (overrides and nesting)" below): a `body` capsule is resized through `body`'s `radius`;
  a character's capsule is its movement profile's, which `character.profile` picks.*
- **Console:** `ent_spawn <prefab> [x y z] [yaw]` places one (3 m in front of the camera by default),
  `ent_dump <id|name>` prints every component on an entity with its non-default fields, and
  `ent_types [filter]` lists the names a prefab can use.
- **The Sandbox and the importer moved onto it the same day.** `SpawnRecord` (twenty fields, one per
  engine feature, with a hundred-line "if it has a sheet… else if it has a box mesh… else if it is a
  character" behind it) is gone; the Sandbox now has a four-field `scene` record of placements and
  calls `world.Spawn` once. The Daggerfall importer emits `prefab` records instead of game-shaped
  ones, and adds its placements with a **patch** (`"place+"`) on the scene it does not own — which is
  §3.5's list-append doing exactly the job it was designed for. The Sandbox declares two parts of
  its own, `box_mesh` (it needs the renderer, which is client-side) and `hop`.
- **Not done here:** placement/map files and overrides per placed entity (F27 §3.4), "revert to
  prefab" in the editor (15), and nested prefabs. *Overrides and nesting: "As built (overrides and
  nesting)" below.* ~~A `scene` record is a game's own until then.~~
  Since issue #29 `scene` is the engine's (REDESIGN §4.1 "As built"): `game.json`'s `"scene"` names the
  one every world starts in, the engine places it and respawns it on reload keeping the player, and its
  placements are the same format as the editor's `placements` (test: AGameWithNoCodeBootsIntoItsScene).

### As built (overrides and nesting, 2026-09-30 — issue 4i-1, F31)

The half of F31 that was left: one placed thing that differs from its prefab, and prefabs inside
prefabs. Code: `src/Sage.Simulation/Content/PrefabOverrides.cs` (`PrefabOverrides`, `PrefabChild`,
`PrefabOverriding`), `Prefab.cs` (`Spawn`), `Levels/PrefabKeys.cs`; tests in
`tests/Sage.Tests/Gameplay/PrefabOverrideTests.cs`. Experimental API, SAGE0131 (MAKING_A_GAME §10b).

- **A placement's `overrides`** — in a scene, a `placements` document, the scene's `player`, or a
  prefab's child — are component and part bodies, merged field by field into a *copy* of the prefab for
  that one entity, the same merge a child prefab's body gets over its `base`:

  ```json
  { "prefab": "post", "at": [3, 0, 0],
    "overrides": { "components": { "sprite_renderer": { "size": [1, 1] } },
                   "parts": { "body": { "radius": 0.25 }, "light": { "range": 12 } } } }
  ```

  Part options are overridden like component fields, so the part still builds what it builds, from the
  placement's numbers — which is how a capsule is resized without writing a `Collider` behind the part's
  back. A component is matched however the prefab spells it (`sprite_renderer` and
  `sage:sprite_renderer` are one), a field whichever spelling it used; a bare shorthand value
  (`"faction": "beasts"`) becomes its object before an object merges in. The record is never touched
  (test: APlacementsOverridesMergeIntoACopyOfThePrefab). A bare record id inside an override means the
  prefab's namespace, as in the body it merges into.
- **A `.map`'s per-entity keys are overrides of the same shape.** `PrefabKeys.Overrides` reads
  `"light.range" "12"` into `{ "parts": { "light": { "range": 12 } } }`, and the map spawn goes through
  the same merge, so the two cannot drift (test: MapKeysAreOverridesOfTheSameShape).
- **Checked at load**, as a prefab's own body is (issue #22): every override body is read against its
  component or part at its file:line:column, and a spawn does not say it again (test:
  OverridesAreCheckedAtLoad).
- **`children`** on a prefab place other prefabs inside it — `prefab`, `at` and `yaw` in the parent's
  frame, `name`, and `overrides` of their own. They are spawned with the parent, parented to it with
  `World.SetParent`, start where the parent put them (both halves of their `GlobalTransform` are set, so
  the first frame does not interpolate them in from the parent's origin) and nest (test:
  APrefabsChildrenSpawnParentedWhereItPutsThem). They are tagged `sage:from_parent_prefab`, and a tagged
  child is **destroyed with its parent** however the parent goes — `World.Destroy`, a command buffer, a
  scene reload — while a child a game parents by hand still outlives it, as before (test:
  APrefabsChildrenAreDestroyedWithIt).
- **A prefab that contains itself**, however far down, **or nests deeper than 8** is a load error at its
  `children`, naming the chain; spawning one anyway stops at the limit (test:
  APrefabThatContainsItselfOrNestsTooDeepIsALoadError). The check follows other prefabs through
  `RecordCheck.TryGet`, which a record check may now use to read the content being loaded.
- **`ent_dump`** marks a component field an override wrote with `*` (shown even when it equals the
  default), and lists overridden part options on a line of their own (test:
  EntDumpMarksOverriddenFields). The entity keeps its overrides on `sage:prefab_overridden` ([Transient]),
  which is also how the editor's `ReadPlacements` writes a placement back with its overrides as written
  rather than flattened or lost (test: ReadPlacementsKeepsOverrides).
- **Not done here:** saves. A save still identifies only scene placements and the player, and saves every
  public field, so an override survives a load through the component data it produced, not as an
  override; children are not saved but come back when their parent is spawned by the load. Stable ids
  for children and map entities are 4i-3, saving a diff against the prefab as placed is 4i-5. Overrides
  do not add or remove tags.

### As built (hot reload reaches live instances, 2026-10-03 — issue #287)

A record reload updated the records in place, but a prefab's component values had been *copied* into
every entity spawned from it, so editing a prefab while the game ran changed only the next spawn. Code:
`src/Sage.Simulation/Content/PrefabReload.cs` (`SaveSystem.ReloadPrefabInstances`, `PrefabDelta`), the
`Reload` property of `[Record]` and `ReloadPolicy` in `src/Sage.Core/Content/RecordId.cs`,
`RecordStore.ReloadPolicyOf`; tests in `tests/Sage.Tests/Content/PrefabReloadTests.cs`.

- **A record type declares its reload policy**: `[Record("x", Reload = ReloadPolicy.Live)]` or
  `NextSpawn`, the default. Records are updated in place either way, so whatever looks one up sees the new
  values; the policy is about values copied out of a record into the world. `prefab` and `scene` are
  `Live`; every other engine type is `NextSpawn` (test: RecordTypesDeclareTheirReloadPolicy).
- **A scene is placed again** (`Scenes.Respawn`, as since issue #29). **A prefab's live instances** (runtime
  spawns, the rules' player, prefab children) **follow the edit field by field**, where the field is
  *unmodified*: still equal to the entity's spawn baseline, the same test a save diff uses. So a value code
  or the game set, one a load laid back and one a placement's or map's override owns are kept, while the
  same component's other fields follow (tests: EditingAPrefabUpdatesTheFieldsNoOneChangedOnEveryLiveInstance,
  AValueALoadPutBackIsKeptByAReload, InTheSandboxEditingAPrefabUpdatesItsUnmodifiedInstances). A
  `[Transient]` component has no baseline; its field is unmodified when it still equals the old body.
- **What changed** is read from the body each entity was spawned from, which the spawn baseline now keeps
  (`SpawnBaseline.Body` and `Tags`), against the body now, since the record itself has already been
  overwritten. Compared at a component's top level, as a save diffs: a list or a nested struct is one value.
- **What follows a reload:** a changed field of a component the prefab writes, a component the prefab now
  writes that the entity lacks (added whole), and tags the prefab added or dropped. **What waits for the
  next spawn:** a component the prefab no longer writes, `parts` (read once, at spawn), `children`, `name`,
  `persist`, and the `Transform`. A reload fires no `Spawned`.
- **Afterwards each instance is rebased** onto the new prefab, so a save diffs against it and a second
  reload follows from the first (tests: ASaveAfterAReloadDiffsAgainstTheNewPrefab, EachReloadFollowsFromTheLastOne).
- **Not done here:** parts, children and removed components on live instances; a scene's own placements
  are still placed again rather than followed; every other record type is left `NextSpawn`; the editor's
  per-field revert is #372.

### As built (declared parts, 2026-09-28 — issue #17)

Parts used to be string-keyed delegates (`Prefabs.Register("light", PrefabParts.Light)`) whose options
were private classes, so no tool could list them; they ran in module registration order, and a name
registered twice was replaced with a warning. Now a part is a declaration, like a record type
(REDESIGN §3.4):

```csharp
[PrefabPart("pickup", Plugin = "sage.gameplay.items", After = new[] { "sprite", "body" })]
public sealed class PickupPart : IPrefabPart
{
    public RecordId Item;
    public int Count = 1;
    public void Apply(in PrefabPartContext ctx) { … }
}
```

- **The class is the options.** Its public fields are what a prefab may write under the part's key,
  read into a new instance for every entity (case-insensitively, with the record JSON options, so
  bare ids resolve in the prefab's namespace as before). `ent_types` lists every part with its fields,
  its plugin and its `After` (test: EntTypesListsEachPartWithItsOptions). `Shorthand = "Id"` lets a
  bare value stand for the object — `"faction": "beasts"` (test: ABareValueFillsTheShorthandField).
- **Registered by generated code** for the owning plugin, just before that plugin's `Init`
  (`src/Sage.Generators/PartGenerator.cs`, a second `IGeneratedRegistrations` beside the records'
  one). `Plugin = "id"`, or the assembly's only `[Plugin]`; otherwise `SAGE0010`. A part type that is
  not an `IPrefabPart`, has no public parameterless constructor or is private is `SAGE0011`; two
  parts with one id in an assembly is `SAGE0012`. Every part the engine and the Sandbox ship is
  declared this way, by the plugin it belongs to (test: EveryPartInTheSandboxIsDeclaredByItsPlugin).
- **Order, decided and written down:** a part runs after the parts its `After` names, and otherwise in
  id order (a stable topological sort, `PrefabRegistry.Parts`). Not JSON order — a `base` prefab's parts
  merge in ahead of the child's, so the order a file shows is an accident of inheritance — and not
  module order, which moved whenever a game turned a plugin off (test:
  PartsRunInTheirDeclaredOrderNotTheOrderTheyAreWritten). The engine's constraints are the real ones:
  `effects` after `attributes` (an effect never adds the components it changes) and `pickup` after
  `sprite` and `body` (it adds its own only when there are none). An `After` naming a part that is not
  installed is no constraint; a cycle is an error when the order is first needed (test:
  PartsThatWaitForEachOtherAreAnError).
- **One id, one part.** A second registration of an id is an exception naming both types and plugins
  (test: TwoPartsWithOneIdAreAnError).
- **State a part needs** comes from its context, not a module's fields: `ctx.Get<T>()` returns a
  service under the part's plugin's `ModuleContext.Get` rule (test:
  APartGetsServicesFromItsContextUnderItsPluginsRules). The Sandbox's `box_mesh` gets the renderer this
  way, where it used to close over a `_renderer` its module set in `Start`. `ctx.Error`/`ctx.Warn`
  prefix the prefab and the part, which every part used to spell out by hand.
- `Prefabs.Optional(name)` is unchanged: the simulation half declares that a client-only part may be
  missing headless.
- **Not done here:** namespaced part ids (`sage:light`, as REDESIGN §3.4 sketches) — ids stay the keys
  prefabs already write. Their metadata is "As built (metadata)", and their schemas "As built (JSON
  Schemas)".

### As built (metadata, 2026-09-28 — issue #18)

Every declaration — component, tag, record type, saved resource, prefab part, and the objects nested in
their fields — has a row in a **metadata table** (`src/Sage.Core/Declarations/Metadata.cs`), written per
assembly by `src/Sage.Generators/MetadataGenerator.cs`:

- **Shape.** `TypeMetadata` is the kind, the stable id, the CLR type and the fields; `FieldMetadata` is
  the C# name, the JSON name, the type, a `ValueKind` (Bool, Integer, Number, String, Enum, Vector2/3/4,
  Quaternion, RecordId, AssetPath, Entity, List, Map, Object, Json, Other — each maps to one JSON Schema
  type, for #21), `Min`/`Max`/`Unit`/`Tooltip`/`Category` from `[Property]`, the enum's names, the record
  type a `RecordRef<T>` names through T's `[Record]` (or, on a plain `RecordId`, `[RecordRef("item")]`;
  on a `RecordRef<T>` that attribute is `SAGE0041`), the asset kind from `[AssetKind("texture")]`, `[Transient]`, a list's
  `Item` and an object's `Fields`. A field's default is what a new instance holds (test:
  AFieldCarriesItsRangeUnitTooltipAndDefault); references and nesting carry through lists (test:
  ReferencesAndNestedShapesAreDescribed).
- **Generated, not reflected.** Each field gets a getter and a setter as plain code; a boxed struct is
  written in place (`Unsafe.Unbox`), which is how the inspector edits a component (test:
  AGeneratedSetterWritesABoxedComponentInPlace). The engine's and the Sandbox's tables are generated
  (test: TheEngineAndTheSandboxCarryGeneratedTables). An assembly built without the generator — the test
  assembly — is read by `Metadata.Reflect` over the same attributes (test:
  AnAssemblyBuiltWithoutTheGeneratorIsReadByReflection), and the two agree field for field on every
  engine declaration (test: TheGeneratedTableMatchesReflectionForEveryEngineDeclaration).
- **Checked.** A range on a field that is not a number or a vector (`SAGE0040`), a `[RecordRef]` on one
  that is not a `RecordId` (`SAGE0041`) and an `[AssetKind]` on one that is not an `AssetPath`
  (`SAGE0042`) are build errors (test: AnAttributeOnTheWrongKindOfFieldIsABuildError), and every
  `[RecordRef]` names a record type something declares (test: EveryRecordRefNamesADeclaredRecordType).
- **Annotated:** attributes, `Collider`, `RigidBody`, `CharacterController`, `PointLight` and the
  `light` part, `Melee`, `Inventory`, `AIState`, every engine part's options, the asset paths of the
  render and audio records, and the Sandbox's `hop`, scene and `box_mesh`.
- **Used by** the editor's inspector (ranges clamp the drags, units show in the number, tooltips on
  hover, fields grouped by category, enums and record references as dropdowns), `ent_dump` (JSON names
  and units, defaults skipped) and `ent_types` (each part's fields with types, units, ranges and
  references) (test: TheConsoleDescribesFieldsFromTheTable), the FGD's per-entity keys (15 "As built")
  and the registry dump (`RegistryDump`, REDESIGN §4.8), which lists every command, cvar, record type,
  component, tag, saved resource, part, system, entity input and output and input action with its owner
  and fields (test: TheRegistryDumpListsEverythingRegisteredWithItsOwner). `tools/check_docs.py` reads
  that dump instead of scanning the source (test: ANameRegisteredAnyWayAtAllIsInTheDump).
- **Not done here:** the record forms an editor would build from the table. JSON Schemas are "As built
  (JSON Schemas)" below; `RecordRef<T>` is §3.6's strict loading.

### As built (JSON Schemas, 2026-09-29 — issue #21)

Record files are checked in the editor, before the game runs (REDESIGN §4.2): `sage schema` writes JSON
Schemas from the metadata table and the loaded content, the repository commits them in `schemas/`, and
`.vscode/settings.json` maps them onto every `data/**/*.json` (as `jsonc`, for the comments). How to use
it is MAKING_A_GAME §3, "Editing records in VS Code".

- **Layout** (`src/Sage.Simulation/Content/RecordSchemas.cs`, draft-07): `record.schema.json` is the root —
  one record or an array of them, each sent by an `if`/`then` on its `"type"` to `<type>.schema.json`,
  one per record type, which has the type's fields plus `type`, `id`, `base`, `patch`, `abstract`,
  `disabled`, `$schema`, and `field+`/`field-` beside every list at any depth (the merge applies them
  anywhere). `additionalProperties: false` everywhere a type is known, so a misspelt field is an error
  (test: AMisspeltComponentFieldInASandboxPrefab_FailsTheSchema, test:
  MistakesInPartsReferencesAndTypes_FailTheSchema); everything the loader accepts passes (test:
  WhatRecordFilesMayWrite_PassesTheSchema), and so does every shipped data file (test:
  EveryShippedDataFile_ValidatesAgainstTheCommittedSchemas).
- **Prefab bodies:** `prefab-components.schema.json` keys every component by id *and* by bare name,
  a bare name that two namespaces share being either one (`anyOf`), since a bare name is the file's
  namespace, then `sage`; `prefab-parts.schema.json` keys every part by id, its shorthand accepted as a
  bare value; `tags` are tag ids, full or bare.
- **From the metadata:** a field's `description` is its tooltip, unit, range, record or asset kind,
  category, C# type and default (test: HoveringAComponentField_ShowsItsTooltipUnitAndRange); `minimum`/
  `maximum`, `enum` for enums and `default` for scalars. `ValueKind` maps one to one: vectors are
  fixed-length number arrays, a list is `items`, a map is `additionalProperties`, an object nests
  (deeper than the table goes, by reflection), JSON and entities accept anything. A value with its own
  converter takes its shape from `[SchemaShape]` on the converter (a colour's `"#RRGGBB"` or `[r, g, b]`,
  an AI task, a material param); one without is open.
- **Ids from content:** `ids.schema.json` holds, per record type, the ids the content loaded (full and
  bare) — what a `RecordRef<T>` or `[RecordRef]` field and `"base"` (abstract records too) may name —
  and the tag ids; a type with no records loaded checks only the id's form. Mounted mods add theirs
  (test: IdEnumsComeFromTheLoadedContent_ModsIncluded).
- **`sage schema <game> [<game> …] [--out dir] [--mounts …] [--client Sage.Client.dll]`** boots each game
  through `ContentValidation` (its `Inspect` hook hands over the loaded engine) into one `SchemaCatalog`:
  every declaration in the engine's and the plugins' assemblies (a record type whose plugin is off still
  has a schema), the registered parts and optional names, and the ids. A game's client half and the
  engine's `Sage.Client` are not loaded headless; their assemblies are *read* for their metadata
  (`AddAssemblyFile`), so `box_mesh`, `audio` and `particles` are described (test:
  APrefabPartsOptions_AreDescribedFromItsDeclaration).
- **Deterministic:** sorted where the order is not the declaration's, `\n` line endings, identical from
  Debug, Development and Shipping builds. CI regenerates `schemas/` and fails on any difference, and a
  test compares the committed files with what this process generates (test:
  TheCommittedSchemas_AreWhatSageSchemaWrites); `.gitattributes` keeps them `\n` on Windows.
- **Validated without a new dependency:** `tests/Sage.Tests/Content/SchemaValidator.cs` implements the
  draft-07 keywords the generator uses and refuses a schema that uses any other (test:
  TheSchemas_UseOnlyWhatTheValidatorChecks_AndEveryRefResolves); Python's `jsonschema` agreed with it on
  every shipped file. The mapping is checked too (test:
  TheWorkspaceSettings_MapTheRootSchemaOntoEveryDataFile_AsJsonWithComments).
- **Not done here:** names are matched ignoring case at load but exactly in a schema (content is
  written in camel case; the Sandbox's `placements.json` was the one file that was not, and now is);
  a bare id that exists only in another namespace is not underlined; asset paths are not enumerated.

### As built (asset hot reload, 2026-09-23 — part of F32)

- **Code:** `src/Sage.Client/Assets/AssetHotReload.cs` (the watcher), `ContentService.Reload` and its
  `Reloaded` event, `VirtualFileSystem.VirtualPathOf` (disk path → virtual path). Tests for the path
  mapping in `tests/Sage.Tests/Content/ContentTests.cs`.
- **What it is for.** Records have hot-reloaded since R11; this is the other half. Regenerate a
  sprite sheet, alt-tab, and it is on the creature — which is most of what dressing a game in art
  consists of, and the Daggerfall import made the absence obvious.
- **Individually, not wholesale.** A record reload rebuilds every record because they are cheap and
  entangled. A texture is a GPU object with holders, so only the file that changed is reloaded.
- **Holders re-resolve.** The renderer keeps textures in a table by index and a built material keeps
  them by reference, so both listen: the table entry is repointed and `MaterialCache.Invalidate()`
  rebuilds materials lazily (which also picks up a reloaded effect, since its bindings are dropped).
  Anything that calls `LoadTexture` every frame — the HUD — needs nothing.
- **A failed reload keeps the old asset.** The new file is loaded before the old object is disposed,
  so a half-written PNG or a typo leaves what was on screen rather than a black square.
- **Debounced 200 ms**, like records: an art tool writing eight files produces one reload, and a file
  still being written is not read.
- **Compiled effects reload too** (`.mgfxo`), because the engine mount *is* where `dotnet-mgfxc`
  writes — recompile and the shader swaps. Running `mgfxc` when a `.fx` changes is still the build's
  job (07 §3.1); that is the rest of F32.
- **Console:** `asset_reload [path]` forces one (or all), `asset_list` shows what is loaded.
  `asset_hotreload` turns the watcher off; it defaults to on in a dev build with `developer 1`.
- **Almost everything cached can be reloaded (R12).** This bullet used to list what could not: `.xnb`
  models, fonts and textures belonged to MonoGame's `ContentManager`, which kept its own cache and
  handed back *the same instance*, so reloading one would have destroyed the live asset. There is no
  `ContentManager` any more — every asset is a file the engine reads itself — so textures, effects and
  the font all reload, and "rebuild the content to change it" went with the thing it described. **One
  refusal was left:** a sound, because the mixer holds instances of it. Issue 4h-3 removed it (see
  "As built (hot reload of meshes, sounds and shaders, issue 4h-3)" below).
- **A font is a texture, and reloads as one.** `LoadFont` goes through `LoadTexture`, so a font's path
  is always a texture entry too and always takes the texture branch of `Reload`; that branch drops the
  `BitmapFont` wrapper with it, and `UiRenderSystem` asks for the font every frame instead of holding
  one. Both halves are needed, and the bug that taught us so is worth keeping: a holder that keeps the
  old wrapper draws from a **disposed** texture, which GL renders as black boxes where the text was,
  with nothing logged anywhere.
- **Not done here:** the asset *server* with scopes, async loading and ref-counting (§3.3); scopes and release came with #308 ("As built (asset scopes)" in §3.3), async loading has not.

### As built (hot reload of meshes, sounds and shaders, issue 4h-3)

- **Meshes reload in place.** `Renderer.ReloadMesh` reads the `.glb` again and puts the new `MeshData` in
  the *same slot* of the mesh table, so every id in a material, a component or an extracted item draws
  the new geometry; the old buffers are disposed. A file that cannot be read keeps the old mesh. Reload
  runs in FrameUpdate, before Extract and Render, so no snapshot holds an index into the old parts.
  `ContentService.Reload` hands a `.glb` to the renderer through `ModelReloader`. Proved by the smoke
  run: `+asset_reload models/pistol.glb` on the Sandbox logs `Reloaded model`.
- **Sounds reload, and their voices follow.** Disposing a `SoundEffect` disposes the instances made from
  it, so `ContentService.Reload` raises `SoundReplacing` first: each world's backend drops the instances
  made from that file (`IAudioBackend.Release`) and its mixer calls `AudioMixer.Invalidate(path)`. The
  mixer's rule is the testable part: voices on that file stop if they are one-shots (restarting a
  footstep would replay a sound nobody asked for), loops are marked not started so the backend begins
  them again from the new file, and voices on other files are untouched (test:
  ReplacingAFileStopsItsOneShotsRestartsItsLoopsAndLeavesOthersAlone). A sound that fails to load keeps
  the old one. The backend half needs an audio device: the smoke run has none, so the MonoGame side is
  checked by reading, not by a run.
- **Shader source recompiles where mgfxc can run.** `ShaderRecompiler` watches a game's `shaders/` folder
  and, in a checkout, `engine_content/shaders/` (found by walking up from the executable; the build does
  not copy sources). After 200 ms of quiet it works out which effects are affected from the `#include`
  lines (`ShaderIncludes.Dependents`: a changed `.fxh` recompiles every `.fx` that reaches it, through
  nested includes and `..`, comments ignored, cycles end) (test: AChangedHeaderRecompilesEveryEffectThatReachesIt)
  (test: IncludesAreReadFromTheLinesThatAreNotComments) (test: AnIncludeCycleEnds), runs `dotnet mgfxc` on a
  background task, one compile at a time, into a temporary file, and moves it over the `.mgfxo` the VFS
  serves. `AssetHotReload` already watches `.mgfxo`, so the swap is the path every other effect takes. A
  compile error logs mgfxc's output under `Shaders` and keeps the old shader.
- **Where mgfxc cannot run** (Linux and macOS without `MGFXC_WINE_PATH`) it logs once under `Shaders`
  ("Shader source changed but mgfxc needs Wine ...") and does nothing else; touching `common.fxh`
  repeatedly during a smoke run logs it once.
- **Switching it on:** `asset_hotreload` (default on only with `developer 1` in a dev build) gates all of
  this, and `.glb` and `.wav` are now in the watcher's extensions.

### What can be hot reloaded, and what formats load

Checked against the decoders compiled into MonoGame 3.8.x DesktopGL (StbImageSharp) and confirmed by
loading one of each through the sprite pipeline.

| Kind | Loose file | Hot reload | Notes |
|---|---|---|---|
| Texture | `.png` `.jpg` `.bmp` `.tga` `.gif` `.psd` `.hdr` | yes | `Texture2D.FromStream`. GIF gives frame 0; HDR is tone-mapped to 8-bit; 16-bit PNG is truncated to 8 |
| Compiled effect | `.mgfxo` | yes | the engine mount *is* where `dotnet-mgfxc` writes, so recompiling swaps the shader |
| Model | `.glb` | yes (issue 4h-3) | `MeshGeometry.ReadGlb` (SharpGLTF) reads it off the mount, or its cooked `.sgmesh` stands in for it (§7); the renderer swaps the mesh in its own slot, so every id already handed out draws the new geometry. Text `.gltf` is not read at all |
| Sound | `.wav` (`.ogg` loads but is not watched) | yes (issue 4h-3) | `SoundEffect.FromStream` (11 §3). The backends drop their instances first and the mixers stop one-shots and restart loops (see "As built (hot reload of meshes, sounds and shaders, issue 4h-3)") |
| Shader source | `.fx` `.fxh` | yes, where mgfxc runs | recompiled to the `.mgfxo` the VFS serves, which then reloads as a compiled effect; a changed header recompiles the effects that include it |
| Font | `.png` atlas | yes | a texture with a glyph grid over it (13 §3), so reloading the image reloads the font |
| `.tif` `.dds` `.webp` | — | — | **no runtime decoder** in StbImageSharp; convert to PNG (there is no MGCB left to take them through) |

Two MonoGame details worth knowing, because its own documentation states both backwards: **TGA does
load** at runtime, and **TIFF and DDS do not**. The stale comment dates from XNA, before MonoGame
moved `FromStream` onto StbImageSharp.

`FromStream` returns straight alpha and zeroes the colour of fully transparent pixels; the loader
premultiplies afterwards (07 §13), which is what the blend states expect and is also what stops
transparent texels bleeding dark halos into sprite edges under bilinear filtering.

### When an asset is missing or broken

Verified by deleting and corrupting files while the game ran. Nothing here stops the frame:

| What | What happens |
|---|---|
| Texture missing at load | one warning naming the path; the material draws the **checker placeholder** (texture id 0) |
| Mesh missing at load | one warning; the **error mesh** is drawn — a 1 m cube, never a silent gap (06 §8) |
| Effect or material broken | the material fails to build and `sage:error` is drawn in its place |
| Record id that does not resolve | `Ensure` fails once and a default-constructed record is returned |
| Prefab id that does not resolve | the spawn is refused and logged; nothing else in the scene is affected |
| Font missing | logged once; the HUD draws its bars and crosshair without text |
| **Reload** of a corrupt or half-written file | the decode fails, **the copy already loaded stays on screen**, and it says so |
| **Delete** while running | not reloaded at all: what is loaded stays. Said once, because "I deleted it and nothing happened" should not be a mystery. Export tools that write by delete-and-replace, and `git checkout`, both look like this |

The rule behind the table: a missing asset costs *that asset*, drawn as something obviously wrong, and
never the frame or the entity. The placeholder is deliberately ugly so that it reads as a bug rather
than as art.

- **Files:** `data/**/*.json` in any mount. A file holds an array of records:
  ```json
  [
    { "type": "item", "id": "iron_sword", "base": "sword_base",
      "name": "@items.iron_sword.name", "damage": 8, "weight": 3.5, "tags": ["blade", "iron"] },
    { "type": "material", "id": "goblin_sprite", "effect": "shaders/sprite.mgfxo",
      "technique": "Unlit", "pass": "AlphaTested", "params": { "Albedo": "textures/creatures/goblin.png", "Tint": [1,1,1,1], "AlphaCutoff": 0.5 } }
  ]
  ```
- **`RecordId` = `namespace:name`.** A bare `id` gets the namespace of the mount that defines it: the game's `id` from `game.json`, a mod's id, or `sage` for engine content. **A bare reference means the namespace of the file it is written in**, including inside a patch of another namespace's record: a mod that points an engine record at one of its own writes `"attackEffect": "claw"` and gets `mymod:claw` (R11, issue #22; until 2026-09-28 it got `sage:claw`, and the workaround was to write ids in full, which still works) (test: Patch_BareIdsMeanThePatchingFilesNamespace). The loader writes such ids out in full before merging, guided by the record type's `RecordId` fields, so a list operation compares like with like (`"spells-": ["sage:frost"]` removes an engine record's bare `"frost"`).
- **`base`, `type`, `id`, `patch`, `disabled` and `abstract` are reserved**: a record that wants a field of its own by one of those names has to call it something else (the engine's `attribute` records use `start`, not `base`).
- **`base`:** single inheritance of field values from another record of the same type (Dungeon Siege templates). It's resolved after merging. **`"abstract": true`** marks a template: it can be a `base` but never becomes a record itself (so `creature_base` doesn't spawn), and the flag isn't inherited.
- **Localized text** is a key (`@items.iron_sword.name`) into string tables, not inline text (ARCHITECTURE §4.7).
- **Load-order merge, per field, not whole-record:**
  1. Records are read in mount order. Within one mount, every definition is read before any patch, so file names don't matter.
  2. The first definition of an id creates the record.
  3. A later record with `"patch": true` and the same id **merges field by field**: scalars overwrite, objects merge recursively, lists are replaced unless patched with `"tags+": [...]` (append) / `"tags-": [...]` (remove).
  4. A later *non-patch* definition of an existing id is an error (it would silently wipe the earlier one, which is Bethesda's "rule of one" problem). It's logged with both files, and the patch wins so the game still runs.
  5. `"disabled": true` in a patch removes a record.
- **Typed schemas:** each record type is a C# class with `[Record("item")]`. A source generator (09) produces the JSON reader and validator. After all files are merged:
  - unknown fields → `Error` with the nearest known name (catches typos; a `Warn` until issue #22);
  - wrong types / out-of-range values → `Error` (the record is skipped);
  - `RecordId`/`AssetPath` references to things that don't exist → `Error` with the file and line.
- **`RecordStore`** holds merged, validated records per type. Lookups by `RecordId` are dictionary-based. `RecordRef<T>` is a typed reference, checked against records of its type (as built: an id with a type, not yet a cached index).
- **Hot reload** (`rec_hotreload`, `DevOnly`, default on when `developer` ≥ 1): a changed `.json` re-runs load + merge + validate for that record type, replaces entries **in place** (so `RecordRef`s stay valid), then raises `RecordsReloaded(RecordType)` (04).

### 3.6 As built (migration step 5)
- **Code:** `src/Sage.Core/Content/` (`VirtualFileSystem.cs`, `RecordId.cs`, `RecordStore.cs`, `RecordHotReload.cs`); tests in `tests/Sage.Tests/Content/ContentTests.cs`.
- **VFS:** folder mounts only. Priority is mount order (`Mount(IMount)` has no priority argument). The host mounts engine content (`<exe>/Content`, namespace `sage`), then the game's `game.json` mounts (namespace = game id). `user://` is `UserPaths` (02), not a VFS root yet. `VirtualPath.Parse` rejects `.`, `..` and `:`.
- **Records:** the rules above, deserialized with `System.Text.Json` reflection (fields and properties, case-insensitive names, comments and trailing commas allowed, `[x,y,z]` vectors, enums as strings). The generated readers (09) replace it later with the same rules. Differences from the design:
  - errors name `file:line:column` (`sandbox:data/scene.json:385:5`) since 2026-09-28 (issue #22): the record, or the property a problem is in — the file that *set* that field, when a patch or a `base` did — or the element of a list. Positions are worked out only when there is an error to report; a clean load just keeps each file's bytes until it finishes (test: RecordErrors_SayFileLineAndColumn, test: AnErrorInAPatchedField_PointsAtThePatch, test: InvalidJson_SaysLineAndColumn);
  - **colours** are `"#RRGGBB"`, `"#RRGGBBAA"` or `[r, g, b(, a)]` in whole numbers 0-255 on any field marked `[JsonConverter(typeof(ColourJsonConverter))]` (`damage_type.colour`, `particle.colourStart`/`colourEnd`); a plain number is still read as the packed value (red in the low byte, as MonoGame's `Color.PackedValue`), and 0-1 fractions are an error (test: Colours_ReadAsHex_Arrays_OrPackedNumbers, test: Colours_ThatAreNotColoursAreErrorsAtTheirLine);
  - no range checks yet (they come with `[Property]` attributes, 09);
  - **strict loading, the rest of issue #22 (2026-09-28):**
    - **unknown fields are errors at any depth**, with the nearest known name by edit distance (`unknown field 'agilty' in 'stats'; did you mean 'agility'?`), and the record is skipped (test: UnknownFields_AtAnyDepth_AreErrorsWithTheNearestName). The check walks the JSON beside the type's System.Text.Json contract, so a member with its own converter (a colour, an AI task) is a leaf; the options also carry `UnmappedMemberHandling.Disallow` as the backstop for anything read later. `"$schema"` is reserved for editors (#21);
    - **`RecordRef<T>`** is a reference to one record type: the same string in a file, and in code it converts both ways with `RecordId`, compares with one and exposes `Target` for tools (#18). The load checks it against records *of that type*, and says what the id is when it names another (`(sandbox:sword is a test_item)`), or the nearest id; a plain `RecordId` still means any record (test: RecordRefs_AreCheckedByType). References are found at any depth — a list of objects holding them included — and one to a record type this host did not register (a sound on a server) is not checked. The engine's record fields and part options that point at one kind of record are `RecordRef<T>`;
    - **`AssetPath` values must be in a mount**: a warning in a dev build, an error when `MissingAssetsAreErrors` is set (`sage validate`), not looked at in Shipping. A `.mgfxo` counts as present when its `.fx` source is, since the build makes one from the other (test: AssetPaths_MustBeInAMount_AWarningInDev_AnErrorWhenValidating);
    - **`RecordStore.Get<T>` throws in a dev build** for a missing record (`no test_item record sandbox:swrod; did you mean 'sandbox:sword'?`); the blank `new T()` is Shipping's safety net only, logged once per id (test: AMissingRecord_StopsADevBuild_InsteadOfHandingBackABlankOne);
    - **checks per record type**: `RecordStore.AddCheck<T>((record, check) => …)`, added in a plugin's `Init`, runs after every load with the built records; `RecordCheck` reports at a path inside the record (`check.Error("Tasks[2]", …)` → that line) and offers the load's own field and reference checks for data only the check can type. The engine adds one for prefabs (§ prefabs below), `AIModule` one for schedules' interrupt names (test: AISchedules_NameTheirLineForAnUnknownInterruptOrTask). `RecordStore.Where(type, id, path)` gives the same `file:line:column` after the load, for problems only a world can judge (an AI task name);
    - **prefab bodies are checked at load**, not at the first spawn: each component by id (with the nearest id), its fields, each tag, each part by id (a part declared `Optional` is skipped), its options (shorthand included), and every record and asset they name. A prefab with problems still loads and spawns without the bad pieces, and the spawn logs them at Debug, since the load said so already (test: PrefabBodies_AreCheckedAtLoad_AtTheirLines). `AddBodyTypes<T>` tells the loader what type a keyed body is, so bare ids inside a patched prefab's components and parts mean the patching file's namespace too (test: APatchedPrefabBody_QualifiesBareIdsInThePatchersNamespace);
    - **`sage validate <game> [--mounts dir[=ns] …]`** (`src/Sage.Cli`, headless, `ContentValidation.Run`): boots the game as a dedicated server would, plus the client's record types, with missing assets as errors, through the game's first world, and exits 1 on any error. CI runs it on Hello, the Sandbox and `tests/games/no-plugins` (test: Validate_TheSandboxAndHelloHaveNoErrors, test: Validate_AModMountWithMistakesFails_NamingEachOne). Options of parts only a client declares are not read there; the real host's load checks them;
  - hot reload re-runs the whole load (all types; it takes about 1 ms today) and raises a plain `RecordStore.Reloaded` event, not `EngineSignals.RecordsReloaded(RecordType)` (04). Instances of records that still exist are updated in place. (Since issue #287 a prefab's live instances follow too: "As built (hot reload reaches live instances)".)
- **Hot reload** (`RecordHotReload`, dev builds): a `FileSystemWatcher` on each folder mount's `data/`, polled from the main thread and reloaded after 200 ms of quiet, while `rec_hotreload` is on. `games/Sandbox` respawns its scene on reload, so editing `content/data/scene.json` updates the running game.
- **Assets — as built (R12, 2026-09-24):** `ContentService` (`src/Sage.Client/Assets/ContentService.cs`) loads everything through the VFS, so any asset can come from any mount and be shadowed like any other file:
  - models: `.glb`, read by `MeshGeometry.ReadGlb` (SharpGLTF; `GltfLoader` until issue #302) into vertex and index buffers the renderer owns and disposes, or from a cooked `.sgmesh` (§7);
  - compiled effects (`.mgfxo`, step 6);
  - `.png`/`.jpg` textures via `Texture2D.FromStream`, premultiplied (step 6);
  - `.wav` sounds via `SoundEffect.FromStream`, and `.ogg` decoded to PCM first (11 §3);
  - the UI font, which is a texture with a glyph grid over it (13 §3).

  **Nothing is built by a content pipeline any more.** `ContentService.Open(path)` hands a mount's bytes to whoever knows the format, which is what lets a mod drop in a model or a game ship art made this morning; `Content.mgcb`, `MonoGame.Content.Builder.Task`, the `dotnet-mgcb*` tools, `VfsContentManager` and every `.xnb` branch are gone, and `dotnet-mgfxc` (07 §3.1) is the only build-time content step left. Everything is cached for the process. `AssetPath` (the interned path, §3.2) exists since step 6 and is what `MeshRenderer` and material records store. `AssetServer`, `AssetRef`, scopes and async loading are still to be built (§14 step 2).
- **Engine content** is `engine_content/` in the repo. The build copies it into the exe's `Content/`, the `engine` mount (07 §3.6).

### 3.7 Localisation: string tables (decision D3; as built 2026-09-30, issue #96)
Player-facing text in content is a key, `@ns.key`, not a sentence (§3.5); the text lives in string
tables, one set per language, and is looked up when it is shown (`Localisation`, `src/Sage.UI`,
13 "As built (style and layout records)").

- **Files:** `strings/<lang>/**/*.json` in any mount, `<lang>` being what the `lang` cvar says (`en` by
  default, archived). A file's path under the language folder is its **namespace**:
  `strings/en/ui.json` holds `@ui.*`, `strings/en/items/weapons.json` holds `@items.weapons.*`. A file is
  an object; nested objects add to the key:
  ```json
  { "inventory": { "title": "Inventory", "weight": "Weight {current} / {max}" },
    "arrows": { "zero": "no arrows", "one": "{count} arrow", "other": "{count} arrows" } }
  ```
  `@ui.inventory.title` is "Inventory". Comments and trailing commas are allowed, as in record files.
- **Mount order:** every mount's tables are read in order and a later one's key wins, key by key, so a
  mod retranslates one line without copying the file (test: StringTablesNestFillPlaceholdersAndFallBackToEnglish).
- **Placeholders** are `{name}`, filled by name (`Localisation.Format(key, ("current", 3))`, or a
  layout node's `args` from its view-model); `{{` and `}}` are braces; one nobody fills stays as written.
  Numbers are written invariantly (`3.5`, never `3,5`) unless the placeholder gives a format: since #345
  `{gold:n0}` is written the language's way, "1,234" in English and "1.234" in German
  (test: APlaceholderWithAFormatIsWrittenTheLanguagesWay). Since #352 `{action:Use}` is the key or pad button
  the player presses for an action now (13 "As built (input glyphs in prompts)").
- **Plural forms:** an object whose keys are CLDR categories — `zero`, `one`, `two`, `few`, `many`,
  `other`, with `other` required — is one key's forms, chosen by the `count` argument with the
  language's rule — since #345 CLDR's rule for each language games ship in, English's for the rest
  (test: PluralFormsFollowTheCldrRuleOfEachLanguage); `zero`, when a table has it, is used for 0 in any language.
- **Fallback and missing keys:** a key the language lacks is looked up in English; a key no table has is
  shown **as the key itself** and logged once (test: AMissingKeyShowsTheKeyItselfAndWarnsOnce), and dev
  builds and `sage validate` warn about every `@key` any record names that no table has, at its line
  (test: ValidateWarnsAboutMissingKeys). A language no mount has tables for is English, and says so.
  `@@` at the start of a text is a literal `@`.
- **Hot reload:** record hot reload watches `strings/` beside `data/`; the tables are read again with the
  records, and open screens are rebuilt (test: ChangingARecordReLaysOutAnOpenScreen). `loc <key> [count]`
  prints what a key shows.
- **Fonts and scripts** (since #338 and #345): a style names a TTF or OTF; a `language` record
  (`name`, `code`, `direction`, `fonts`) names the fonts that stand behind every style's for
  the characters it lacks, and says which way the language runs; Arabic is joined, a line is reordered for right
  to left, and a right-to-left screen is mirrored (13 "As built (non-Latin scripts and complete translations)").
  `loc_check` and `sage validate` compare each language's tables with English's and its characters with its
  fonts (test: ValidateReportsWhatATranslationLacks).

## 4. Public API sketch

```csharp
public readonly struct VirtualPath { public static VirtualPath Parse(string s); }   // normalized
public readonly struct AssetPath   { public static AssetPath Intern(VirtualPath p); public VirtualPath Path { get; } }

public sealed class VirtualFileSystem
{
    public void Mount(IMount mount, int priority);                 // FolderMount, PakMount
    public bool Exists(VirtualPath path);
    public Stream Open(VirtualPath path);                          // highest-priority match
    public IMount? Which(VirtualPath path);                        // which mount provides it (for logs/tools)
    public IEnumerable<(VirtualPath, IMount)> Enumerate(VirtualPath dir, string pattern);  // all mounts, mount order
    public string UserRoot { get; }                                // user:// on disk
}

public readonly struct AssetRef<T> where T : class
{
    public AssetState State { get; }                               // Loading, Loaded, Failed, Unloaded
    public bool IsReady { get; }
    public T Get();                                                // the asset, or its placeholder if not ready
}

public sealed class AssetScope : IDisposable { public string Name { get; } }

public sealed class AssetServer
{
    public AssetScope CreateScope(string name);
    public AssetRef<T> Load<T>(AssetPath path, AssetScope scope) where T : class;   // async; returns immediately
    public AssetRef<T> LoadNow<T>(AssetPath path, AssetScope scope) where T : class; // blocking (boot, tools)
    public void RegisterLoader<T>(IAssetLoader<T> loader) where T : class;
    public void SetPlaceholder<T>(T placeholder) where T : class;
}

public interface IAssetLoader<T> where T : class
{
    IReadOnlyList<string> Extensions { get; }
    object Decode(Stream data, VirtualPath path);                 // worker thread; CPU only
    T Finalize(object decoded);                                   // main thread; GPU work allowed (client loaders)
    void Replace(T existing, object decoded);                     // hot reload in place
}

public readonly struct RecordId   { public string Namespace { get; } public string Name { get; } }
public readonly struct RecordType { public string Name { get; } }
public readonly struct RecordRef<T> where T : class { public T Value { get; } }

[AttributeUsage(AttributeTargets.Class)] public sealed class RecordAttribute : Attribute { public RecordAttribute(string type); }

public sealed class RecordStore                                   // as built (2026-09-23)
{
    public void Register<T>() where T : class, new();             // a [Record] type; generated code calls it (issue #16)
    public T Get<T>(RecordId id) where T : class, new();          // dev: throws on miss; Shipping: placeholder record
    public bool TryGet<T>(RecordId id, out T value) where T : class;
    public IReadOnlyList<T> All<T>() where T : class;
    public IEnumerable<RecordId> Ids(string type);
    public bool Exists(RecordId id);
    public RecordId Resolve(string type, string text);            // a bare name from a console command;
                                                                  // warns "there is no item called 'x'" and
                                                                  // returns empty rather than a blank id
    public void Load(VirtualFileSystem vfs);
    public void Reload();                                        // hot reload; keeps existing instances
    public event Action? Reloaded;

    // Records made rather than loaded (F21's spellmaker). Re-applied after every load.
    public void AddRuntime<T>(RecordId id, T record) where T : class;
    public bool RemoveRuntime<T>(RecordId id) where T : class;
    public IEnumerable<(string Type, RecordId Id, object Record)> RuntimeRecords { get; }
}
// No `RecordRef<T>` yet: a reference is a `RecordId` plus a lookup. The typed handle waits for the
// generator (R11's "left"), which is also what would validate it.
```

## 5. Data flow

```
boot:  VFS mounts ──► RecordStore: enumerate data/**/*.json → parse → merge (mount order) → resolve base → validate
play:  system asks Load<Texture>(path, levelScope)
         └► AssetRef (Loading) returned immediately; placeholder drawn
         └► job: VFS.Open → loader.Decode (worker)
         └► main-thread upload queue (≤ asset_upload_ms per frame) → loader.Finalize → Loaded
dev:   FileSystemWatcher (folder mounts) → debounce 200 ms → Decode → Replace in place
         → EngineSignals.AssetReloaded / RecordsReloaded → consumers refresh caches
unload: scope.Dispose() → refcounts drop → LRU cache → evict over budget
```

## 6. Threading and memory
- `Decode` runs on the job system. `Finalize`/`Replace` run on the main thread (the MonoGame `GraphicsDevice` rule).
- `VirtualFileSystem` reads are thread-safe; `Mount` happens only during boot or mod changes.
- The path intern table is append-only and lock-free for reads.
- Records are immutable during a tick. Hot-reload replacement happens at frame start, between ticks.
- Budgets: `asset_upload_ms` (per-frame GPU upload time) and `asset_cache_mb` (LRU).

## 7. File formats
| File | Format |
|---|---|
| `.pak` | zip (stored or deflate), paths inside = virtual paths |
| `data/**/*.json` | record arrays (§3.5) |
| `*.sheet.json` | sprite sheet: frame rects, pivot, animations, 8-direction frame groups (06, 12) |
| `strings/<lang>/**/*.json` | localisation tables: key → text or plural forms; the file's path is the key's namespace (§3.7) |
| `<path>.sgmesh`, `<path>.sgtex` | **Cooked formats, built (issue #302), below** |

**As built (cooked formats, 2026-10-05, issue #302).** `sage cook <game> [--force] [--clean]` (`Sage.Cli/GameCook.cs`) writes a cooked file *beside* each loose one, under the loose file's path plus an extension: `models/bunny.glb.sgmesh` (the vertex and index arrays as the GPU takes them, and a skin's matrices) and `textures/wall.png.sgtex` (premultiplied RGBA, or BC1 for an opaque texture and BC3 for one with alpha, from `BlockCompression`). Both start with four magic bytes, a format version, and the loose file's length and FNV-1a hash, which say which file it was cooked from. Nothing names a cooked file: records, prefabs and maps keep naming the `.glb` and the `.png`, and the loose files stay (the simulation reads skeletons and clips from the `.glb`, and `sage validate` checks the paths). `sage package` cooks the package's copies unless `--no-cook`; the game's own folder is not written to by it. The reading side is `CookedAssets` in `Sage.Simulation`, so tests can use it headless; the client's `ContentService` asks it first.
- **When one is used.** A cooked file stands in for its loose file only when its mount is at least as high as the loose file's, so a mod's loose `wall.png` (a later mount) beats the game's cooked `wall.png.sgtex`, which was made from the file the mod replaced (§3.2: the last mount wins, cooked or not) (test: TheCookedFileStandsInForTheLooseOneInItsMountAndALaterMountsLooseFileWins). A cooked file that cannot be read, or that was cooked from a file of another length (checked only where the mount is a folder that can say so), is a warning once and the loose file is read (test: AnUnreadableOrOutOfDateCookedFileFallsBackToTheLooseOneWithAWarning). Staleness is by length, not by hash, on a folder mount; `sage cook` itself skips a file only when length and hash both match.
- **Round trips.** A cooked mesh reads back as exactly the geometry of its `.glb` (test: ACookedMeshReadsBackAsTheGeometryOfItsGlb); a texture cooks to blocks when it can and to exact premultiplied pixels when it cannot, or when game.json's `"cook": { "compress": false }` or an `"uncompressed"` glob says so (tests: ATextureCooksToBlocksWhenItCanAndToExactPremultipliedPixelsWhenItCannot, GameJsonCookSettingsAreReadAndAMisspeltKeyIsRefused, UncompressedPatternsAreGlobsInsideAMount); block compression is exact on flat blocks and close on gradients (test: BlockCompressionIsExactOnFlatBlocksAndCloseOnGradients). A graphics device that does not sample DXT1/DXT5 gets the blocks decoded to RGBA as they load (a warning once). `sage cook` skips what is up to date (test: CookWritesACookedFileBesideEveryModelAndTextureAndSkipsWhatIsUpToDate), and `sage package` cooks (test: APackagesModelsAndTexturesAreCookedBesideTheLooseFilesUnlessCookIsOff).
- **What it buys, measured** on the Sandbox's own models and textures: loading cooked takes about 0.05 ms against about 10 ms loose (no glTF parse, no PNG inflate, no premultiply), allocates about 322 KB against 2143 KB, and the textures take about 217 KB of GPU memory against 902 KB as RGBA; the test asserts at least twice as fast, at most half the allocation and at most a third of the memory (test: CookedAssetsLoadFasterWithLessAllocationAndTexturesTakeLessMemory). The numbers are from one machine; the test is what holds.
- **Not built:** cooking the engine's `Content/` or a game's `mods/` (a package's mods stay loose); formats other than `.glb` and `.png`/`.jpg`/`.jpeg` (and `.tga` since #317); dropping the loose files from a package; a hash check at load (a folder mount is checked by length); `.pak` mounts (#397). Block-compressed `SetData` on a Direct3D device is untested (CI's client run is on Linux).


**As built (mipmaps and TGA, 2026-10-06, issue #317).** Every texture gets its mip chain: loose files when
they load (`TextureMips`, box-filtered premultiplied pixels, each side halving down to one texel), cooked
ones from the `.sgtex`, which is version 2 and stores the chain per level, uncompressed or as BC1/BC3
blocks; a `.sgtex` from before mips is stale and the loose file loads instead, so cook again. `.tga` loads
and cooks like `.png` (test: AMipChainHalvesEachSideDownToOneTexelAndBoxFiltersPremultipliedPixels) (test: ATgaFromAModFolderLoadsPremultipliedWithItsMipsAndIsCooked). How the mips are sampled
(`r_anisotropy`) is 07 §3.8.

**As built (meshes, 2026-10-06, issues #410 and #321).** A mesh vertex carries a tangent and a colour
(`MeshVertex`, 52 bytes; `SkinnedMeshVertex`, 72), and a part may keep a second UV set (`Uv1`, for
lightmaps). The `.sgmesh` is version 3 (version 2 added the tangent and colour, version 3 the second UV
set); an older one is refused with a warning and the `.glb` is read.

## 8. Errors and fallbacks
| Failure | Behaviour |
|---|---|
| Missing asset | `Warn` once per path with the mount search list; the placeholder is used (checkerboard texture, error mesh — a red "ERROR" box like Source's error model — or a silent sound) |
| Decode failure | `Error` with the exception and the mount that provided the file; placeholder |
| Missing record | a reference to it is a load error at its line; `Get<T>` of one throws in a dev build, and only Shipping hands back a placeholder record (type defaults, logged once) to keep the game running |
| Record validation error | Record skipped, `Error` with file:line:field |
| Non-patch redefinition | `Error` naming both files; the later one is treated as a patch |
| Corrupt `.pak` | Mount skipped, `Error`; boot continues |

### The glTF subset (as built, 2026-10-06, issue #321)
A model is held to one table, `GltfSubset.Features` in `src/Sage.Simulation/Content/GltfSubset.cs`, which
the check reads before SharpGLTF decodes anything. A file with a rejected feature does not load: one error
per feature, naming the file and the feature, such as `Model 'x': glTF feature 'draco' is not
supported: the file uses Draco mesh compression ...` (test: AFileWithAnUnsupportedFeatureFailsWithOneErrorNamingTheFileAndTheFeature),
and every rejected row has a test (test: EachRejectedFeatureOfTheTableHasATestOrIsTheNoGeometryCase). What
is read past is a warning. The second UV set and the colours are read
(test: TheSecondUvSetAndTheColoursAreRead) and survive the cook (test: TheSecondUvSetSurvivesTheCook).
The guides copy this table (`GltfSubset.Table()`); MAKING_A_GAME "Model formats" has it too.

| Feature | Support | What it covers |
|---|---|---|
| `glb` | Supported | Binary glTF 2.0 (.glb), buffers embedded |
| `scene-graph` | Supported | Default scene (or every root node), node hierarchy and transforms (baked into the vertices of a rigid mesh) |
| `positions-normals-uv0` | Supported | POSITION, NORMAL, TEXCOORD_0, TANGENT (worked out from TEXCOORD_0 when absent), triangle indices (any index width) |
| `color-0` | Supported | COLOR_0 (vec3 or vec4, float or normalised integer), white without one |
| `texcoord-1` | Supported | TEXCOORD_1: a second UV set (lightmaps), kept beside the vertices as `Uv1` |
| `skinning` | Supported | One skin of up to 64 joints, JOINTS_0 and WEIGHTS_0 (four influences per vertex) |
| `primitive-triangles` | Supported | Primitive modes TRIANGLES, TRIANGLE_STRIP and TRIANGLE_FAN |
| `morph-targets` | Supported | Morph targets (blend shapes): POSITION and NORMAL deltas, named by the mesh's extras.targetNames, at the mesh's weights; a skinned mesh's are animated by clips' weight tracks (TANGENT deltas are not read) |
| `materials` | Ignored | The file's materials and textures: the material record named by whatever draws the mesh decides |
| `animations` | Ignored | Animation clips are read by the animation reader, not the mesh reader; cameras and lights are not read |
| `extra-attribute-sets` | Ignored | TEXCOORD_2 and up, COLOR_1 and up: dropped, with a warning |
| `not-gltf` | Rejected | A file that is not glTF 2.0 (bad header, broken JSON chunk) |
| `text-gltf` | Rejected | Text .gltf with its buffers in separate files |
| `external-buffers` | Rejected | A buffer stored in a separate file (a `uri` that is not a data: URI) |
| `sparse-accessors` | Rejected | Sparse accessors |
| `draco` | Rejected | KHR_draco_mesh_compression |
| `meshopt` | Rejected | EXT_meshopt_compression / KHR_meshopt_compression |
| `required-extension` | Rejected | Any other extension the file lists in extensionsRequired |
| `primitive-mode` | Rejected | Primitive modes POINTS, LINES, LINE_LOOP and LINE_STRIP |
| `more-influences` | Rejected | More than four joint influences per vertex (JOINTS_1 / WEIGHTS_1) |
| `too-many-joints` | Rejected | A skin of more than 64 joints |
| `multiple-skins` | Rejected | More than one skin |
| `no-geometry` | Rejected | A file with no drawable triangle primitive |

*Since #363 (phase 4p, 2026-10-06)* morph targets are read rather than rejected: one list per model by name across its meshes, a rigid mesh baked at its rest weights, a skinned one morphed on the CPU each frame its weights change (design 12 "As built (morph targets)"); the cooked `.sgmesh` is format 4, and an older cook is refused and the loose `.glb` read until it is re-cooked (test: ACookedMeshKeepsItsMorphTargets).

Extensions a file may mark required and still load: `KHR_materials_unlit`, `KHR_materials_emissive_strength`,
`KHR_texture_transform`, `KHR_mesh_quantization`, `KHR_lights_punctual`.

## 9. Debug and tooling hooks
- **Cvars:** `asset_upload_ms` (built, #308), `asset_cache_mb` (not built), `asset_hotreload`, `rec_hotreload` (both `DevOnly`; default = `developer` ≥ 1), `vfs_log_overrides`.
- **Commands** (built in step 5: `vfs_which`, `vfs_ls`, `vfs_mounts`, `rec_get`, `rec_list`, `rec_reload`, and the `rec_hotreload` cvar):
  - `vfs_which <path>`, `vfs_ls <dir>`, `vfs_mounts`;
  - `asset_list [filter]` (scope, holders, size; built in #308);
  - `asset_reload <path|*>`;
  - `rec_get <type> <id>` (dumps the merged record *with the file each field came from*);
  - `rec_list <type>`, `rec_reload`.
- **Overlay:** loads in flight, upload ms used, cache size.
- **Log categories:**
  - `VFS`: mounts, and overrides when `vfs_log_overrides` is on (which mod shadows which path);
  - `Assets`: load, fail, evict, reload;
  - `Records`: merge report, validation, patches applied per mod.

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `src/Sage.Client/Assets/UtilAssets.cs` (static `stanfordBunny`) | **Removed (step 5):** the Sandbox loads models through `ContentService` (VFS-backed, §3.6). Since #308 meshes and textures are loaded by id at a scope (§3.3); `AssetRef` is not built |
| `src/Sage.Host/Content/Content.mgcb` + `stanford_bunny.fbx` | **Both deleted (R12, done).** Models load at runtime from `.glb` via SharpGLTF (`MeshGeometry.ReadGlb`, once the client's `GltfLoader`; issue #302 moved it into `Sage.Simulation`, leaving `VertexSkinned` and `VertexLayouts` in the client). The Sandbox's placeholder is `games/Sandbox/content/models/bunny.glb`, generated by `games/Sandbox/tools/make_placeholder_model.py` — like the sprites and the sounds, the repository ships no art it did not make. Shaders compile with `dotnet-mgfxc` (07) |
| The FreeImage failure that removed `light.png` (review #9, 2026-09-22) | Textures load with `Texture2D.FromStream` (StbImageSharp, no FreeImage) |
| `Content.RootDirectory = "Content"` in `Game1` | VFS mounts from `game.json`. **Done (step 5)** for game content; **the line itself is gone (R12)** — nothing uses MonoGame's `ContentManager` any more, not the host, the client, or the vendored ImGui renderer |

## 11. v1 scope vs later
- **v1:**
  - VFS with folder mounts + `user://`;
  - `AssetPath` interning;
  - `AssetServer` with async decode + budgeted upload;
  - scopes + LRU;
  - placeholders;
  - loaders for Texture, SpriteSheet, Mesh (glTF), Effect, Sound (wav), Heightmap;
  - hot reload for folder mounts;
  - `RecordStore` with namespaces, `base`, patch merge, generated validation, hot reload;
  - localization keys (English table only) — **built** (§3.7, issue #96).
- **Done since:** cooked formats, a cook tool (`sage cook`, not MonoGame's builder) and texture compression (BC1/BC3), and `.ogg` sounds decoded whole (issue #302, §7, 11 §3).
- **Done in 4n (2026-10-06):** asset scopes, release and an upload budget (#308), mipmaps and `.tga` (#317), the glTF subset (#321).
- **Later:** `.pak` mounts (zip mounts, #397), `.uid` sidecars (if needed). (OGG streaming is built: #326, 11 "As built (OGG streaming)".)

## 12. Multiplayer-later notes
A server needs the same records and simulation assets. The mod list plus record hashes will be compared on connect (as Quake 3's `sv_pure` checks paks). Only data and asset mods may be auto-downloaded; code mods never are (17).

## 13. Open questions
- ~~OGG decoding~~ **Decided and built (issue #302):** NVorbis decodes an `.ogg` whole to PCM for `SoundEffect` (11 §3). *Streaming* long music is built too (#326): our own `PcmStreamer` feeds a `DynamicSoundEffectInstance`, so MonoGame's per-platform `Song` support does not matter (11 "As built (OGG streaming)").
- JSON library: `System.Text.Json` source-generated readers vs a custom reader that keeps line numbers for error messages. Leaning towards **`Utf8JsonReader` + a generated per-type reader** (fast, with position info for errors).

### Namespaces and inheritance
A bare id means "in this file's namespace", and that rule follows the *value*, not the record that
ends up holding it: a base in another namespace wrote `"damageType": "physical"` meaning
`sage:physical`, and a game record inheriting it gets `sage:physical`, not `sandbox:physical`. The
merge qualifies inherited ids using the record type's own fields, so only ids are rewritten — paths,
labels and clip names are left exactly as the base wrote them (review #56).

## 14. Build steps
1. ~~VFS (folder mounts) + `game.json` mounts (with 01)~~ **Done 2026-09-22** (ARCHITECTURE §7 step 5). `user://` as a VFS root is still to do.
2. `AssetPath`, `AssetServer`, scopes, placeholders, texture + glTF + effect loaders; convert the bunny to `.glb`; fix TODO #9. **Half done:** `AssetPath` (step 6), the texture, effect, sound, font and glTF loaders, the bunny and #9 all came with **R12 (2026-09-24)**. Scopes, counting and eviction for meshes and textures came with #308 (§3.3); what is left of the `AssetServer` is async decoding, `AssetRef` handles and the release of sounds and effects.
3. Async decode + budgeted upload queue (with the 02 job system).
4. `RecordStore`: parsing, namespaces, `base`, patch merge, validation (TODO R11). **v1 done in step 5** with `System.Text.Json` reflection (§3.6); 09's generator replaces it later.
5. Hot reload for assets and records. **Records done in step 5**; assets come with the `AssetServer`.
