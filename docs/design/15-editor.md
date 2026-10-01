# 15 — Editor (short)

## 1. Purpose and scope
The Sage editor is a **separate host** (`Sage.Editor` exe) that loads the same engine, framework and game module as the game. It edits **documents** (maps, prefab records, material and other records) through a **command log** with undo/redo. It includes play-in-editor. Expanded when roadmap Phase 6 starts; a minimal version is part of v1.

## 2. Research basis
- Authoring data vs runtime data: Unity baking, O3DE editor components, Bitsquid compiled resources (survey §2.3, §2.6, §2.8).
- A central document model with commands gives undo/redo, copy/paste and prefab overrides "for free" (Our Machinery's The Truth, survey §2.8).
- Generated inspectors from one declaration (Unreal property panels from `UPROPERTY`, survey §2.2).
- Use proven external tools where they're better: TrenchBroom for brushes (ARCHITECTURE §7).

## 3. Key decisions
- **Documents are the source of truth; the edit world is derived.** Opening a map loads its files into a document model (per-sector entity lists with prefab + overrides, 09 §3.4). An **edit world** is built from it (the "bake" step, v1: plain instantiation). Edits change the *document*; the affected entities in the edit world are re-instantiated.
- **Command log:** every change is a command with `Do`/`Undo` and a description (`Move 3 entities`, `Set Health.Max 100 → 120`). Undo/redo stacks per document; dirty tracking; save writes JSON. Commands are also the unit of "recent changes" and future collaboration.
- **Panels (ImGui):**
  - viewport (the editor camera rig: today's `DevCamera` behaviour on `Move`/`Look` actions in the `Editor` context, 08). *Stub since #81:* `ed_viewport` shows the free camera — a `DebugCamera` entity — drawn into the render target `editor` in an ImGui window (06 "As built (the editor's cameras)");
  - outliner (spaces → sectors → entities);
  - inspector (generated metadata, 09 §3.2: typed editors, ranges, categories, prefab-override highlighting, "revert to prefab");
  - record browser/editor (form view + raw JSON, showing which mod/file each field came from, 05);
  - asset browser (VFS with mount/override info);
  - log panel (02);
  - console;
  - I/O link view (04).
- **Gizmos:** translate/rotate/scale with grid and angle snapping. Picking by physics raycast (10) with a render-bounds fallback.
- **Play-in-editor:** serialize the edit world → build a fresh play world → possess a player pawn → run → **Stop** discards the play world (edits made during play are not kept, as in Unity/UE).
- **External tools:**
  - export a **TrenchBroom FGD** generated from the game's prefabs (**done**, F16);
  - import `.map` brush geometry as meshes + colliders (**done**, F16 — see "As built" below);
  - Blender → glTF for models.

## 4. API sketch
```csharp
public interface IEditorCommand { string Description { get; } void Do(EditorDocument doc); void Undo(EditorDocument doc); }
public sealed class EditorDocument { public VirtualPath Path { get; } public bool Dirty { get; } public void Execute(IEditorCommand c); public void Undo(); public void Redo(); public void Save(); }
public interface IEditorPanel { string Title { get; } void Draw(EditorContext ctx); }   // games/mods add panels from a MyGame.Editor assembly (01 §3.1)
```

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `src/Sage.Editor/Screens/EditorUI.cs` (menu bar; stubs; `game.Exit()`) | The editor host's menu: New/Open/Save map documents; Exit |
| `src/Sage.Editor/Screens/EntityOutlinerWindow.cs` and `EntityInspectorWindow.cs` (were `EntityContextMenuUI.cs`: tree over the world, reflection field editing, Delete via `World.Destroy`) | Outliner + inspector (generated metadata; Delete becomes a command) |
| `src/Sage.Editor/Camera/DevCamera.cs` | The editor camera rig (actions-based, 08) |
| `src/Sage.Editor/DevTools.cs` (was `EditorManager.cs`: owns the dev windows and camera) | Host window cvars (01) + editor state |

## 10a. As built: the `.map` importer (F16, 2026-09-24)

**The editor arrived from the other end.** F28's editor is still unwritten, and this is the reason it
can stay unwritten for a while: TrenchBroom already edits brushes well, so the engine learned to *read*
what it writes instead. A room with a doorway took a day rather than an editor.

**A brush is planes, not polygons**, and that is the whole reason this format is worth keeping. Each face
is a plane written as three points; the solid is the intersection of the half-spaces behind them, so the
polygons are worked out on load by clipping each plane against all the others (`BrushGeometry`). Nothing
a mapper does can make such a solid open, inside out or non-convex — which is worth more to physics than
any mesh format, because it means every brush can be a **convex hull** (`PhysicsSpace.AddHull`) rather
than a triangle soup. A mesh collider is a surface, and a player who ends up behind one falls through the
world; a hull pushes them out.
(tests: ACubeOfSixPlanesBecomesSixFacesAndEightCorners, TheSolidIsWhereEveryPlaneSaysItIs,
EveryFaceFacesOutOfTheBrush, EveryBrushBecomesOneStaticHull)

| Piece | Where | What it does |
|---|---|---|
| `MapFile` | `src/Sage.Simulation/Levels/MapFile.cs` | The text: entities, keys, brushes, faces. Standard, **Valve 220** and Quake 2/3 dialects; `//` comments and faces split across lines; every error names the line |
| `BrushGeometry` | `src/Sage.Simulation/Levels/BrushGeometry.cs` | Planes → convex polygons (clip, weld, triangulate), texture coordinates for both dialects, and `MapSpace`: 32 units to the metre, Z-up to Y-up, north to −Z |
| `MapRecord` / `MapLevel` / `MapLoader` | `src/Sage.Simulation/Levels/MapLevel.cs` | The record that names a `.map`, the loaded level, and loading it: brushes built, hulls into physics (`PrePhysics`), entities spawned |
| `MapMeshSystem` | `src/Sage.Client/Rendering/MapMesh.cs` | **One mesh per texture** (not per brush), built in `FrameUpdate`, disposed when the level unloads |
| `FgdExport` | `src/Sage.Simulation/Levels/FgdExport.cs` | `fgd_export` writes TrenchBroom an entity definition file from the prefab records |

- **The split is terrain's** (14 §3), deliberately: the simulation owns the parsed geometry in metres,
  physics and the client each build from it once and set a flag. So a headless server has the level's
  collision and no meshes, and neither half waits on an event.
- **Placement can fail, and failing is not an error.** A level that stands on terrain (`onTerrain`) loads
  while the scene is being built, a tick or two before streaming has generated the ground under it —
  asking for a height then gets a confident zero, which is how the hut first appeared at sea level under
  a hillside. It now waits for the sector it stands on, and both builders skip a level with nowhere to be.
  (test: ALevelLoadsItsBrushesAndStandsThemWhereTheRecordSays)
- **`classname` is a prefab id.** `watcher` in a map is `sandbox:watcher`, with `origin`, `angle`
  (Quake's degrees counter-clockwise from east, a quarter turn from the engine's yaw) and `targetname`.
  A classname with no prefab — `info_player_start` is the one every game has — is left for the game to
  read off the level, and **named** in the log, not refused: "left for the game: info_player_start"
  rather than a count, because a count tells you something was ignored and not what. (`light` used to be
  the second example here and is a prefab now, F2.)
- **What "left for the game" actually means.** `MapLevel.TryFindPoint(classname, out at, out yaw)`, or
  `MapLoader.TryFindPoint(world, …)` to ask a whole world rather than one level. It was a promise
  before it was a method: `PointEntities` hands out raw map coordinates, and both conversions between
  those and a place in the world (the axis swap, and Quake's quarter turn) need the level's `MapSpace`
  and `Position`, which are the engine's. A game could not have done it.
  (test: AClassnameWithNoPrefabIsStillSomethingTheGameCanFind)
  - **An unplaced level says no rather than guessing.** A level standing on terrain does not know its
    height until the ground under it exists, and answering before then would be the map's own
    coordinates dressed as the world's.
    (test: ALevelThatDoesNotKnowWhereItIsYetSaysSoRatherThanGuessing)
  - **`fgd_export` offers it**, next to `worldspawn` and the prefabs, as a person-sized box with
    `angle` — a mapper who cannot place it from the editor will not place it at all. It is the one
    classname in that file which is not a prefab, and the test that says every advertised classname
    exists now names the exemption rather than dropping the rule.
    (test: EverythingTheFgdAdvertisesActuallyExists)
  - **`map_goto [classname]`** stands the player there **facing the way the marker faces**, which is
    what a mapper testing a room wants and what makes the marker do something. The facing goes through
    `PlayerInput.RequestView` (08 "As built (asking the player to face somewhere)") rather than onto the
    pawn: the first version set the pawn's rotation, which the character controller overwrites from the
    view angles a tick later, so it landed you in the right room looking the wrong way and said nothing
    about it. The engine still does not *spawn* you there: where a game starts
    its player is the game's decision (the Sandbox's is a scene placement), and a level is not placed
    yet when the player is spawned anyway.
- **A texture name is a material id**: `wall` in `sandbox:hut` looks for `sandbox:wall`, and a texture
  with no material draws plain rather than as the error checkerboard, because blocking a room out with
  untextured brushes is working rather than a mistake.
- **Texture coordinates need a texture size**, which is a client fact the simulation cannot see, so
  `MapSpace` assumes 64 — what nearly every Quake-era texture is, and what TrenchBroom itself assumes
  when it cannot find one. A 128-pixel image tiles twice as often as the editor showed; the fix, when it
  matters, is a size on the material record.
- **Unloading gives everything back**: the brushes' entities, their statics, their hull shapes (physics
  used to leak a shape per static — the list of them was write-only), their GPU buffers, **and the
  entities the level spawned**. That last one is easy to forget and obvious afterwards: the Sandbox
  reloads its maps whenever a record file is saved, so a watcher that outlived its walls meant another
  watcher standing in the field per save. Geometry and spawned entities carry *different* markers,
  because what a level spawns is an ordinary entity of any shape, mesh and all. Terrain still leaks its
  chunk meshes on sector unload; a level that a mapper reloads every few seconds could not.
  (tests: UnloadingALevelTakesItsGeometryWithIt, ReloadingALevelDoesNotLeaveTheOldOneStandingInIt,
  UnloadingLeavesTheClientsOwnEntitiesForTheClientToFree)
- **A level follows a rebase** (R6). `MapLevel.Position` is a world position held outside the ECS, which
  is exactly the category of thing the rule was written for: the walls have transforms and move
  themselves, but the level would go on handing out the position it had before the world shifted, and
  anything built from it afterwards would be a sector out. (test: ALevelFollowsTheWorldWhenTheOriginMoves)

**Dogfood:** `games/Sandbox/content/maps/hut.map` — eight brushes, a doorway, stone/plank/thatch
textures, all generated by `games/Sandbox/tools/make_hut.py`, so the repository still ships no art it did
not make. `scene.json` names it in `"maps"`, and a record reload rebuilds it.

**Solid entities (F17, 2026-09-24).** Brushes with a `classname` are no longer skipped: they become an
entity that *owns* its geometry. Its meshes are children of it, so moving the entity moves them through
transform propagation, and its collision is **one convex hull for the whole entity** — exact for a door
drawn as a box, and an over-approximation for an L-shaped one drawn as a single entity, which collides
across the inside of the L. Draw that as two entities. The entity is **not rotated** by its `angle` key,
unlike a point entity: its geometry is already where the mapper drew it, and turning the entity turned
the mesh away from the hull that stayed put (which is what a door two feet wide and facing sideways
looked like the first time). A `"trigger" "1"` key makes the hull a trigger volume instead of a solid — and the client does not draw
one, which is the point of a trigger: it is a shape you walk into, not a thing you look at.

The four keys the importer reads on a map entity are now `origin`, `angle`, `targetname` and `trigger`,
plus any key named after an output (04 §3.4).

**Per-entity keys (issue #18).** A point or brush entity whose classname is a prefab may also set any
field of the prefab's parts and components: `"light.range" "12"`, `"body.mass" "20"` —
`<section>.<field>`, the section as the prefab writes it (a namespaced one with its colon as a dot).
`PrefabKeys` works the keys out from the prefab and the metadata table, so `fgd_export` offers exactly
the keys the importer applies, each typed (`integer`, `float`, `choices` for an enum or a flag, `color1`
for a colour) with the prefab's value as its default and a description made of the field's tooltip,
unit, range and the record type it names (test: TheSandboxFgdHasTypedDescribedKeysForItsPrefabs). The
value is applied to a copy of the prefab for that entity alone; one that does not read as its field, or
is outside its `[Property]` range, is an error naming the key and the line, and the prefab's value
stands (test: AMapEntitysKeysSetItsPrefabsFieldsForThatEntityAlone). Every default the FGD offers reads
back as its field (test: EveryKeyTheFgdOffersReadsBackItsOwnDefault). A `[Transient]` field is never
offered. The FGD's list of outputs comes from `engine.Outputs`, which each plugin declares beside the
code that fires them.

**Not built:** lightmaps; a save of a map entity keeps its prefab id, not its per-entity keys (the map
puts them back when it loads); and **kinematic** movers — a door is a moving *static*, which
does not push what is leaning on it.

## 10b. As built: documents, the outliner and the inspector (F28, 2026-09-24)

**What a document is** was the decision. Brushes are TrenchBroom's (§10a); what a thing *is* belongs to
its prefab; what is left — and what nothing else could edit — is **where things stand**. So a document is
a `placements` record: prefab, position, yaw, name. The engine owns the type rather than the editor, so
the editor is not the only thing that can read what it wrote: `world.SpawnPlacements(id)` is one line in
a game, and a scene loads one by naming it (`"placements": ["yard"]`, issue #29). Since #29 a placement
is the one format scenes use too, with an optional `relativeTo` (`World`, `Origin`, `Ground`) and a
document-wide `origin` and `relativeTo`; a save writes positions back in the document's frame, so a
placement that named a frame of its own comes back in the document's.

| Piece | Where | What it does |
|---|---|---|
| `Placement` / `PlacementsRecord` | `src/Sage.Simulation/Content/Placements.cs` | The document's content, and spawning it into a world, reading it back, clearing it |
| `EditorDocument` | was `src/Sage.Simulation/Content/EditorDocument.cs` | Open, save, close, dirty. **Engine-side on purpose**: it is records and files with no screen in it, which is what makes it testable. Replaced by `EditDocument` in `Sage.Editing` (#217, §10e) |
| `EntityOutlinerWindow` / `EntityInspectorWindow` | `src/Sage.Editor/Screens/` | List and select; edit a component by boxing it, changing a field and writing it back |
| `DevTools` | `src/Sage.Editor/DevTools.cs` | Everything a developer sees, in one class the host holds behind `SAGE_DEV` |

- **A save goes back to the file the record came from** (`RecordStore.FileOf`), not to a file named after
  the record. Writing `sandbox:yard` to `yard.json` when it lives in `placements.json` leaves two
  definitions of one id, which is the single thing the record loader cannot sort out for itself.
  (tests: SavingWritesWhereTheRecordCameFromRatherThanSomewhereNew, WhatIsSavedIsWhatAGameWillLoad)
- **It writes the record store's own JSON dialect.** With default options the first save produced
  `"Prefab"` and `{"X":518,…}` — a file the loader would read only by luck and a person would not
  recognise as a record at all.
- **A mount answers `WritablePath`** for a file that does not exist yet, because `PhysicalPath` resolves
  files that do, and a new document is precisely a file that does not. A zip mount says no.
- **Every menu item is a console command too** — `doc_open`, `doc_save`, `ent_select` and the rest. A
  menu a script cannot press is a feature that cannot be checked the way everything else here is, and
  this feature was verified by pressing those commands and reading the file afterwards.
- **The host split is a compile-time fact.** A Shipping build references neither `Sage.Editor` nor ImGui:
  three DLLs beside the exe where a dev build has six. It is not a separate *executable*, which this doc
  sketched — that is worth doing when the editor wants its own worlds for play-in-editor, its own input
  maps and a window laid out for editing, and none of those exist yet. What a second exe would buy today
  is exactly what this buys.

**Not built:** gizmos and picking (F29), undo/redo and the command log (F30), placing a prefab from the
editor, per-entity overrides, play-in-editor, and an inspector generated from declared metadata rather
than reflection (09 §3.2). Editing runs against the live world, so `pause 1` before moving things — the
simulation will otherwise drop a crate while you inspect it.

## 10c. As built: picking and gizmo maths (10-5, #220)

`Sage.Editing`, headless, System.Numerics only: what the viewport decides, so `Sage.Editor` only draws it.

- **A ray from a camera and a pixel.** `EditorPicking.RayFrom(CameraPose, fovY, viewportSize, screenPoint)`
  follows `CameraMath`'s conventions (looks down -Z, screen right is +X, pixel origin top left) and passes
  through the point `CameraMath.View` and `Perspective` project to that pixel (test:
  ARayAgreesWithTheViewAndProjectionMatrices). `RayFromOrthographic` gives parallel rays (test:
  AnOrthographicRayIsParallelAndOffsetByThePixel).
- **Picking.** `EditorPicking.Pick(world, ray)` takes the nearest of the physics raycast and a fallback
  for entities with no `Collider` (test: PickingTakesTheNearestColliderAndReportsWhereTheRayMetIt). Headless
  code has no render mesh bounds (the client loads meshes), so the fallback is a sphere of
  `EditorPicking.FallbackRadius` (0.35 m) at the entity's position; a collider in front still wins (test:
  AnEntityWithNoColliderIsPickedByItsFallbackSphereAndLosesToANearerCollider). A world with no physics
  plugin picks by the fallback alone (test: PickingWorksInAWorldWithNoPhysicsAtAll).
- **Translate gizmo.** `TranslateGizmo.HitTest` finds the X, Y, Z axis or XY, XZ, YZ plane handle, the
  planes winning where they sit between axes (test: TheTranslateGizmoFindsAxisAndPlaneHandles). `Drag`
  turns two rays into a delta in origin space, with grid snapping applied to the result on the moved axes
  (tests: ADragAlongAnAxisMovesOnlyThatAxisByHowFarThePointerWent,
  ADragOnAPlaneMovesBothAxesAndGridSnapsTheResult). A ray parallel to the axis or plane, or a plane behind
  the camera, gives no delta (test: ADragHasNoAnswerForARayParallelToItsAxisOrPlaneOrAPlaneBehindTheCamera).
- **Rotate about Y.** `RotateGizmo.HitTest` and `Drag`: the angle is positive as
  `Quaternion.CreateFromAxisAngle(UnitY, a)` turns, takes the short way over the seam, and snaps to
  `stepDegrees` (tests: TheRotateRingIsHitOnItsCircleOnly,
  ARotateDragGivesTheAngleAboutYInTheDirectionQuaternionsTurn, ARotateDragHasNoAnswerOnTheAxisOrParallelToThePlane).
- **Screen-constant size.** `GizmoMath.ScreenConstantSize` (test: AGizmoKeepsItsPixelSizeAtAnyDistance);
  `Snap.ToGrid` and `Snap.Angle` (test: SnapRoundsToTheGridAndTheAngleStepAndZeroMeansOff).

## 10d. As built: file-preserving write-back (#218, 2026-10-01)

A save used to serialise the document's record and write the file whole, so it lost the comments, the
key order, the one-line vectors and any record kept beside the document (REDESIGN §4.6). **`JsonFileEdit`**
(`src/Sage.Core/Content/JsonFileEdit.cs`, SAGE0133) edits a record file's text instead: a small JSONC
parser keeps where every value starts and ends, an edit splices one value's text, and the file is
parsed again before the next. It finds a record by `type` and `id` in a file holding one record or an
array of them (a bare id means the mount's namespace), and it replaces, adds or removes a record, or
sets or removes a value at a path inside one (`place[3].at`, `overrides.components.health.max`).

- **Nothing changed, nothing rewritten:** a record set to what the file already says leaves the text
  byte for byte, byte order mark and line endings included. Numbers compare as values, so `1` is `1.0`.
  (tests: AFileNothingChangedInIsWrittenBackByteForByte, SavingKeepsTheFilesByteOrderMarkAndLineEndings)
- **Only the difference is written.** Setting an object or array, or a whole record, compares it with
  the file key by key and element by element, so a moved placement is the number that moved: one line
  in a file with one-line vectors, one number in a file that spreads them over lines. (tests:
  MovingOnePlacementChangesOneLine, AVectorWrittenOverManyLinesChangesOnlyTheNumberThatMoved)
- **Comments, key order and the other records stay**, a removed value takes its own lines with it, and
  a value added to a list goes after the last one's comment rather than before it.
  (tests: CommentsAndTheOtherRecordsStay, RecordsAreReplacedAddedAndRemovedInPlace,
  ValuesAreSetAndRemovedAtAPathInsideARecord)
- **New values are in the record store's dialect** (its converters, camel case, enums by name, nulls
  left out), arrays of numbers and strings on one line and objects indented like their neighbours.
  (test: NewValuesAreWrittenInTheRecordStoresDialect)
- **`PatchRecord(before, after)` is what the document saves with.** A program's record spells out
  fields the file leaves at their defaults (`"yaw": 0`, `"outputs": []`), so comparing it with the file
  would write them all. The document (`EditDocument` since #217, §10e) keeps it as it was opened or last saved and writes
  only what changed since; a document the file does not hold (a new one) is added, or makes the file.
  (tests: APatchWritesOnlyWhatChangedBetweenTwoVersions, SavingChangesOnlyWhatChangedInTheFile)

**Not built:** comments above a removed value stay where they were, and a list's element is matched by
its index, so removing one from the middle rewrites the ones after it. A record merged from patches in
other mounts is written to the file that defined it; a patch file of its own is the record editor's
(#224).

## 10e. As built: the command log and the document (issue #217, phase 10a, 2026-10-01)

**The document is the source of truth and the world is derived from it** (§3), which F28's
`EditorDocument` had the other way round: it spawned a record into the world and saved by reading the
world back. `EditDocument` (`src/Sage.Editing/EditDocument.cs`) copies the `placements` record when it
opens, and every change is a command that changes the copy and then re-spawns **only the placements it
touched**; saving writes the copy. Nothing is read back from the world, so what the editor shows is what
a game loading the file would place — a re-spawn is a load of that one placement
(`world.SpawnPlacement`, beside `SpawnPlacements`; `DespawnPlacement` takes one out without a tombstone).

| Piece | Where | What it does |
|---|---|---|
| `IEditorCommand` | `src/Sage.Editing/IEditorCommand.cs` | `Description`, `Do`, `Undo`, and `TryMerge` (default: never) |
| `CommandLog` | `src/Sage.Editing/CommandLog.cs` | Execute, undo, redo, merging, a cap, `SavedPosition`, `Dirty`, `Changed` |
| `EditDocument` | `src/Sage.Editing/EditDocument.cs` | Open, new, new level, close, save; the entity ↔ placement map |
| `AddPlacement`, `RemovePlacement`, `SetPlacement`, `SetOverride`, `ClearOverride`, `SetOutputs` | `src/Sage.Editing/PlacementCommands.cs` | The edits a document takes |
| `EditorCommands` | `src/Sage.Editing/EditorCommands.cs` | `doc_new`, `doc_level`, `doc_open`, `doc_save`, `doc_close`, `doc_status`, `ed_undo`, `ed_redo`, `ed_history` |

- **One list and a position in it.** A new command drops the redo stack; a capped log forgets its oldest
  command. **Dirty is "the position is not the saved one"**, not a flag: undoing back to the save makes a
  document clean, undoing past it makes it dirty, and a save point that was capped away or dropped with a
  redo stack leaves it dirty until the next save.
  (tests: UndoAndRedoWalkTheLogAndANewCommandDropsTheRedoStack, ADocumentIsDirtyAgainAfterAnUndoPastTheSavePoint,
  TheCapForgetsTheOldestCommandsAndASavePointWithThem, ADocumentSaysWhenItHasUnsavedWork)
- **A drag is one command**: a command done straight after another of the same target folds into it
  (`TryMerge`) until `EndMerge` (the mouse came up, or an undo), and nothing merges into the command at
  the save point. (test: ADragsFramesAreOneCommandUntilTheGestureEnds)
- **Each command undoes and redoes exactly, re-spawning one placement.** `SetPlacement` sets `at`, `yaw`,
  `name` and `relativeTo` (`PlacementFields`); `SetOverride`/`ClearOverride` set or take away one field of
  one component or part in the shape a placement writes (`"overrides": { "components": { "timer":
  { "interval": 7 } } }`), on the body the prefab already names under another spelling, and an emptied
  body or overrides go with the last field; `SetOutputs` replaces the wires.
  (tests: AddingAPlacementSpawnsItAndUndoTakesItAway, RemovingAPlacementAndUndoingPutsItBackWhereItWas,
  SetPlacementReSpawnsOnlyThePlacementItChanged, AnOverrideIsOneFieldOfOneComponentOrPartAndRevertsToThePrefab,
  SettingOutputsWiresThePlacedEntityAndUndoUnwiresIt, AReSpawnTakesThePrefabsChildrenWithIt)
- **A re-spawn keeps its placement's identity.** Commands hold the `Placement` object, and the document
  maps it to its entity both ways; `Respawned(old, new)` lets a selection follow it (DevTools' does), and
  the new entity has the persistent id the old one had. A placement the editor adds gets a stable `id`
  (its name, else its prefab's, made unique), so its identity in saves does not hang on its index; one
  without an `id` that an insertion or removal moved is re-spawned with the id a load would now give it.
  (test: AReSpawnKeepsItsPlacementIdentity)
- **A new level is a scene naming a new placements document** (`doc_level crypt`: `crypt` places
  `crypt_placements`); the first save writes both, each a file of its own.
  (test: ANewLevelIsASceneNamingANewPlacementsDocument)
- **Save has one writer** (`EditDocument.WriteRecord`, on §10d's `JsonFileEdit`): the document keeps
  its record as last opened or saved and `PatchRecord`s only what changed since into the file it came
  from, so comments, layout, the other records and the defaults the file leaves out stay; a document the
  file does not hold yet (a new one, a new level's scene) is set whole.
  (tests: WhatIsSavedIsWhatAGameWillLoad, SavingChangesOnlyWhatChangedInTheFile)
- **The console drives it**: the `doc_*` commands go through the document, and `ed_undo [n]`, `ed_redo
  [n]` and `ed_history` through its log; they live in `Sage.Editing` so a headless test presses them.
  (test: TheConsoleOpensUndoesRedoesAndListsTheHistory)
- Until the inspector edits overrides (#223), what it changes on the selected entity is kept only for the
  placement's position and yaw, as a `SetPlacement`; other component edits are not saved, as before.

## 10f. As built: the editor mode of the host, `-edit` (#219, 2026-10-01)

**The editor is a mode of the dev host** (phase 10a decision 2): `Sage.Host -game <folder> -edit
[placements-or-scene]` boots the game exactly as a run does, then makes an **edit world** where the main
world would have been, opens the document, and lays the screen out for editing. A Shipping build has no
editor and says so (`-edit` is ignored with a warning); a dev run without `-edit` is what it was.

- **An edit world runs no Fixed system** but an `EvenWhenEditing` one (below), `Always` included: nothing walks,
  falls, thinks or counts the hours. Its ticks still propagate transforms, so a placement an edit moves
  moves on screen, and every Frame system (cameras, extraction, drawing, the UI) runs as in any world.
  The tick boundary's travel, passing time and saves wait too; a streamed scene's sectors are still placed.
  It is a flag the world is made with (`World.Editing`, set by `Engine.CreateEditWorld` /
  `SageApp.CreateEditWorld`), not `pause`: a paused world is a game waiting to go on, and play-in-editor
  (#226) makes a second, real world rather than unpausing this one.
  (test: AnEditWorldRunsNoFixedSystemButItsFrameSystemsAndTransformsRun,
  APlayWorldBesideAnEditWorldStillPlays)
- **Physics is mirrored, not stepped.** A system that only copies the world into a service the editor
  reads says `RunCondition.EvenWhenEditing`, which runs it in an edit world as well: `sage.physics.sync`
  (colliders get bodies, kinematic poses follow) and `sage.physics.terrain` (the ground's collision). So
  `EditorPicking.Pick` raycasts an edit world as it does a played one, and a dynamic crate hangs where it
  was placed, because `sage.physics.step` does not run. (test: TheEditorPicksCollidersInAnEditWorldWhereNothingFalls)
- **It is the scene as its files say**, placed by the scene service as a run would place it, with the
  game's modules and rules installed, but the rules are never *started*: no player, and nothing a game's
  `OnWorldStarted` adds, nor after a hot reload or a `scene_load`, which place it again without one. The
  free camera starts at the scene's player start.
  (test: AnEditWorldIsPlacedWithTheSceneButHasNoPlayer)
- **What `-edit` opens** (`EditTarget`, Sage.Editing): nothing named, the start scene and the first
  placements document it names; a scene, that scene and its first document; a placements document, that
  document in the first scene that names it, else in the start scene (the Sandbox's `-edit yard`). A name
  that is neither is an error in the log, and the start scene opens instead.
  (test: TheEditArgumentNamesASceneOrADocument)
  The document is #217's `EditDocument` on the edit world, opened in one place (`DevTools.BeginEditing`);
  from there its commands, `doc_*` and `ed_undo`/`ed_redo` act on it, and the status bar shows its title.
- **The layout is ImGui's docking.** ImGui.NET 1.90 is built from the docking branch, so `DockSpace` is
  there; the DockBuilder that sets up a default arrangement is ImGui's internal API with no C# binding, so
  its six functions are declared against the cimgui library ImGui.NET already loads (`EditorLayout`). The
  outliner is on the left, the inspector on the right, the **log panel** and the console are tabs along
  the bottom, and a **status bar** under them says the document, saved or modified, the selection, the
  world and the camera. Panels can be dragged and re-docked; `ed_layout` (View → Reset layout) puts them
  back.
- **The viewport is the screen itself.** The dock space's central node is a pass-through: the world the
  renderer drew on the back buffer shows in the middle, and the mouse there is the free camera's, because
  ImGui does not capture it over the hole. No second camera, no texture to bind, no size to keep in step;
  `ed_viewport`'s window is still there for a second view. The panels cover the picture's edges rather
  than squeezing it, which a picking tool (#221) has to allow for.
- **The `Editor` input context** has a map now (`engine_content/data/input.json`, `editor`): `EditorMove`
  on WASD, the console key and Escape. In the editor it is on and `Gameplay` is off, so a scene's screen
  keys and a pawn's actions never fire; in a run it is off, so the editor's keys never reach a game (it
  used to be set on with nothing bound). The free camera flies on `EditorMove` in the editor, needs no
  pawn, and turns on a right-drag as before. Escape does not quit the editor; File → Exit or `quit` does.
- **The log panel** shows the log's ring (`Log.Ring`, the last 2000 lines) at or above a level, without
  the categories hidden, with "only this one" per category. What to show is `LogView` (Sage.Editing),
  re-filtered only when the ring or the filter changes. (test: TheLogPanelFiltersTheRingByLevelAndCategory)
- **CI runs it**: `tools/smoke_run.sh` passes `SAGE_SMOKE_ARGS` before the commands, and the Linux job
  runs the Sandbox with `-edit yard`, selecting a crate, flying, resetting the layout and opening the
  viewport window.

## 10g. As built: the prefab palette and placing (#222)

`Sage.Editing` holds the model; the ImGui panel (`PalettePanel`, Sage.Editor) only draws it and passes the
viewport click on.

- **The palette.** `PrefabPalette` lists every `prefab` record by namespace, narrowed by a search: each
  whitespace-separated word must appear, in any case, in the prefab's full id, and a namespace with no match
  is left out. Abstract prefabs (templates a `base` names) are not offered; a prefab that is only a child of
  another is (there is no flag for it). `Arm` / `Disarm` hold the prefab the next click places.
  (test: ThePaletteListsPrefabsByNamespaceAndTheSearchNarrowsThem)
- **Where a click lands.** `Placing.Surface(world, ray)` is the first collider the physics raycast finds
  (terrain included, since the edit world mirrors it), else the plane y = 0; null for a ray that meets
  neither. It deliberately ignores `EditorPicking.Pick`'s fallback spheres: a prefab put down by clicking
  on another's marker would sit on a sphere nothing draws.
  (test: PlacingOnAColliderLandsOnItsTopAndOnEmptyGroundAtYZero)
- **Placing.** `Placing.Place(document, prefab, at, yaw, name)` is one `AddPlacement`, so it is one undo.
  The name is the wanted one or the prefab's, made unique among placements' names and ids with `_2`, `_3`
  ...; `PlaceAt` does the same on a ray's surface. `at` is in the document's frame (absolute metres unless
  it says `relativeTo`); the edit world has no origin shift, so a click's point is used as it is.
  (test: EdPlaceAddsAPlacementWithAUniqueNameAndUndoRemovesIt)
- **Console.** `ed_palette [search]` prints the list; `ed_place <prefab> [x y z] [yaw] [name]` places it.
  With no position it places at the origin (headless there is no camera to stand in front of); `x y z` is
  all three or none, then an optional yaw in degrees, then the name (the rest of the line).
- **The panel.** A "Palette" tab beside the outliner: search box, a tree by namespace, click a prefab to arm
  it (click again to disarm). While armed the panel says so; the next left click ImGui does not want (the
  viewport is the dock space's hole) places the prefab and selects its entity, and Escape cancels. It stays
  armed, to place another.

## 10h. As built: selecting and moving in the viewport (#221, 2026-10-01)

A click selects, the selection's gizmo is drawn over the picture and dragged, and the editor's keys
press the editor's commands. What any of it *does* is `Sage.Editing`'s, so a test presses it through the
console; `ViewportGizmo` (`src/Sage.Editor/Screens/`) only reads the mouse and draws.

| Piece | Where | What it does |
|---|---|---|
| `EditorSelection` | `src/Sage.Editing/EditorSelection.cs` | What the outliner, inspector, gizmo and `ed_delete` agree is selected; it moved here from `Sage.Editor` |
| `ViewportTools` | `src/Sage.Editing/ViewportTools.cs` | Snapping, the gizmo mode, move/turn/delete/duplicate, and their `ed_*` commands |
| `GizmoDrag` | same file | One drag of a handle: a `SetPlacement` per frame, merged into one |
| `ViewportCamera` | `src/Sage.Editing/ViewportCamera.cs` | The screen's camera: a ray through a pixel, the pixel of a point, a gizmo's size |
| `ViewportGizmo` | `src/Sage.Editor/Screens/ViewportGizmo.cs` | Picking on a click, the gizmo and its drag, the keys, a small toolbar |

- **A selection of something the document placed holds the placement, not the entity.** Every edit
  re-spawns what it touched, so the entity a click found is gone after the first nudge; the selection's
  entity is always the one the document spawned last, and it clears when the placement leaves the
  document (a delete, an undone add) or the document closes. Anything else (a scene's own entity) is
  held as the entity. A click selects the placement an entity belongs to, so a prefab's child selects
  the placed thing; an outliner row selects exactly its entity.
  (test: TheConsoleSelectsByNameAndTheSelectionFollowsAReSpawn)
- **The console drives it**: `ed_select [name]` (a placement by name or id, else an entity by name;
  nothing clears), `ed_move [name] x y z` (in the document's frame), `ed_rotate [name] yaw` (degrees,
  kept in (-180, 180]), `ed_delete [name]` and `ed_duplicate [name]`, each on the selection when no name
  is given. **Each is one undo step**: the merge is closed before and after, so two moves of one thing
  are two steps and a drag after a move does not fold into it.
  (tests: MovingAndRotatingFromTheConsoleAreAnUndoStepEach, DeletingClearsTheSelectionAndUndoPutsThePlacementBack)
- **A duplicate is a copy straight after the original, where it stands, with a name and id of its own**
  (`corner` → `corner_2`, `corner_2` → `corner_3`), and it is selected, so a drag that follows moves the
  copy. (test: DuplicatingMakesACopyWithANameOfItsOwnAndSelectsIt)
- **A drag is one `SetPlacement`.** `GizmoDrag.Begin` closes the merge; each `Update` measures from where
  the drag began (`TranslateGizmo.Drag` / `RotateGizmo.Drag`, §10c) and does a `SetPlacement` only when
  the result moved, which merges into the last; `End` closes it on mouse up. A move snaps the result to
  the grid on the moved axes, a turn snaps its angle. (test: ADragIsOneSetPlacementAndSnapsToTheGrid)
- **Snapping is `ed_snap [0|1]`, `ed_grid <metres>` (0.5) and `ed_angle <degrees>` (15)**, on by default,
  and `ed_gizmo move|rotate`. They are commands, not cvars: a tool registered once a world exists comes
  after `config.cfg` is read, and the cvar seal refuses it. (test: TheSnappingAndTheGizmoAreSetFromTheConsole)
- **The viewport is the screen** (§10f), so picking needs no rectangle: the pixel under the mouse is a
  pixel of the free camera's picture, and the mouse is the viewport's wherever ImGui does not want it
  (the dock's pass-through middle). `ViewportCamera` takes the main view's pose and field of view and
  the display's size; the gizmo is drawn on ImGui's background draw list (over the world, under the
  panels) at the pixels `ToScreen` gives, which agree with the ray `RayThrough` casts back.
  (test: AViewportCameraProjectsAPointToThePixelWhoseRayPassesThroughIt)
- **The editor's own cameras are not picked.** A `DebugCamera` entity is driven through its
  `CameraPose`, so its transform stays at the origin, where its fallback sphere would win a click on
  anything there; `EditorPicking.PickWhere` takes a filter and the viewport leaves out the free and
  viewport cameras. (test: AClickPicksWhatTheViewportAcceptsAndSelectsItsPlacement)
- **Keys** (never while ImGui has the keyboard, so typing in the console is safe): Delete (`ed_delete`),
  Ctrl+D (`ed_duplicate`), Ctrl+Z and Ctrl+Y or Ctrl+Shift+Z (`ed_undo`, `ed_redo`), F (`ed_frame`: the
  free camera moves back along its own view until the selection is in front of it, 6 m away), **G and R
  for move and rotate** — not W and E, which fly the camera (`EditorMove` is WASD). A toolbar at the
  top of the picture sets the gizmo and the snapping too. The outliner's Delete on a placed entity is
  `ed_delete` now, so it is undoable and saved, rather than destroying the entity behind the document.

**Not built:** multi-select and box select; local-space gizmos and rotation about X and Z (a placement
has only a yaw); scaling; a gizmo for an entity the document did not place (it is marked, not movable).
## 11. v1 scope vs later
- **v1 (minimal, for building the vertical slice):**
  - ~~open/save a map document~~ **done (F28)**, as a placements document;
  - ~~the outliner~~ **done**;
  - ~~an editable inspector~~ **done**, from reflection until 09's generator;
  - ~~place a prefab~~ **done (#222)**;
  - the translate gizmo;
  - undo/redo;
  - play-in-editor;
  - ~~log and console panels~~ **done (#219)**, docked in the editor mode;
  - log category `Editor`.
- **Later:**
  - rotate/scale gizmos;
  - record editor;
  - asset browser;
  - I/O link view;
  - terrain tools;
  - prefab override UI;
  - the rename/refactor command for asset paths (05 §3.2);
  - multi-document tabs.

## 14. Build steps
1. Editor host + document model + command log + undo/redo (TODO F28, F30).
2. Outliner + generated inspector (with 09 step 2) (TODO F28).
3. Placement + translate gizmo + picking (TODO F29).
4. Play-in-editor.
5. Later panels and external tool bridges (TODO F31, F32). The `.map` bridge (F16) is built and did
   not need the editor — see below.

## 10i. As built: the problems panel (#227, 2026-10-01)

What is wrong with the content and the open document, in one list grouped by file, and a count in the
status bar. The list is `ProblemList` (`src/Sage.Editing/ProblemList.cs`); `ProblemsPanel`
(`src/Sage.Editor/Tools/ProblemsPanel.cs`) only draws it, as a tab beside Log and Console.

- **It reads what the running engine already found; nothing is booted.** `RecordStore` now keeps the last
  load's error and warning messages (`LoadErrors`, `LoadWarnings`), which carry the loader's
  `mount:path:line:col`; a row splits that into file, line and, when the message is about a record, its
  id. The content report's conflicts between mods are warnings in the file of the mod that won.
  (tests: ARecordFileWithAnErrorShowsWithItsFileAndLine_AndARowOpensTheRecord, AModConflictShowsFromTheContentReport)
- **The open document is checked live**: a placement whose prefab is missing or unnamed (error), a wire
  whose target is no placement of the document (warning; `!self` and its kin are fine), two placements
  with one name (warning) or one id (error). Each row carries the placement, so a click selects it.
  These are read again on every change to the document (a command, an undo, a save); the loader's rows
  come again on `Records.Reloaded`. While a document is open its own rows from the loader are left out:
  the live checks stand for them, and would otherwise go stale after the first edit.
  (tests: AMissingPrefabAndADanglingWireAreListedWithThePlacements_AndFixingThemClears, DuplicateNamesAndIdsAreListed, AReloadRefreshesAndARowSelectsItsPlacement)
- **A click**: `ProblemList.Activate` selects a placement row through `EditorSelection` and hands a record
  row to the panel's `OpenRecord` hook (TODO #224: DevTools wires the record browser to it).
- **`ed_problems`** prints the summary, then each file and its problems. (test: TheConsolePrintsTheProblemsByFile)
