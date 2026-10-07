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
  - asset browser (VFS with mount/override info; **built**, #366, §10o);
  - log panel (02);
  - console;
  - I/O link view (04).
- **Gizmos:** translate/rotate/scale with grid and angle snapping. Picking by physics raycast (10) with a render-bounds fallback.
- **Play-in-editor:** serialize the edit world → build a fresh play world → possess a player pawn → run → **Stop** discards the play world (edits made during play are not kept, as in Unity/UE).
- **The editor for modders** is not a separate executable (#375, §10x): `sage package --editor` puts the
  Development host, which has the editor, in an `editor/` folder beside a Shipping game, with `edit.sh` and
  `edit.cmd` to open it on that game.
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
- Since #223 (§10i) the inspector edits the document: a field is a `SetOverride`, the placement's own
  fields a `SetPlacement`.

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

**Not built:** a gizmo for an entity the document did not place (it is marked, not movable). Multi-select,
box select, local-space gizmos, rotation about X and Z and scaling came with #367 (§10p).

## 10i. As built: the inspector on the document (#223, 2026-10-01)

The inspector used to change the live struct (box it, set a field, write it back), which lasted until the
next re-spawn and never reached a file. **On a placed entity an edit is now an override**: a
`SetOverride` on the document (§10e), saved with the placement; "revert to prefab" is a `ClearOverride`.
What the panel shows and does is **`InspectorModel`** (`src/Sage.Editing/InspectorModel.cs`), headless and
tested; `EntityInspectorWindow` only draws it.

| Piece | Where | What it does |
|---|---|---|
| `InspectorModel`, `InspectorGroup`, `InspectorRow` | `src/Sage.Editing/InspectorModel.cs` | One group per component or part, one row per field: value, overridden, provenance, editable; `Set`, `Revert`, `TrySet`, `TryRevert` |
| `InspectorValue` | `src/Sage.Editing/InspectorValue.cs` | Typed text to the JsonNode an override writes, by the field's `ValueKind` |
| `InspectorCommands` | `src/Sage.Editing/InspectorCommands.cs` | `ed_set`, `ed_revert`, `ed_inspect` |
| `EntityInspectorWindow` | `src/Sage.Editor/Screens/` | The placement form, the rows, the override mark, revert buttons, provenance on hover |

- **How a field maps to an override.** A component the prefab names in `components` is a group whose
  rows write `overrides.components.<the prefab's key>.<field's JSON name>` (the key as the prefab spells
  it, `timer` or `sage:timer`); its value is the live entity's, which is the prefab's with the overrides
  merged in. A part the prefab names in `parts` is a group of the part's options (its `[PrefabPart]`
  class), writing `overrides.parts.<key>.<field>`; its value is the prefab's body with the placement's
  merged over it, read as the part reads it, so `body.radius` resizes the capsule the part builds. A
  component the prefab does **not** name (a part's collider, the engine's name and placement tags) is
  shown read-only, since an override would add a copy the part then fights; `Transform` is the
  placement's `at` and `yaw`. (tests: EdSetWritesAnOverrideTheRespawnedEntityHasAndASaveAndReopenKeep,
  PartsArePartOptionsAndThePlacementsOwnFieldsAreAForm)
- **Overrides survive.** `ed_set gate timer.interval 7` writes `{"timer":{"interval":7}}` on the
  placement, the re-spawned entity has 7, and after `doc_save`, a reload and `doc_open` it still does.
  `ed_revert gate timer.interval` takes it away (an emptied body and overrides go with it); both undo and
  redo, and a revert of what is not overridden is refused. (tests:
  EdSetWritesAnOverrideTheRespawnedEntityHasAndASaveAndReopenKeep, EdRevertGoesBackToThePrefabAndUndoRedoWalkBoth)
- **A drag is one undo**: a widget's frames are `SetOverride`s of one field, which merge, and the panel
  calls `EndMerge` when no item is active, as the transform path does; each console `ed_set` is an edit
  of its own. (test: ADragOverAFieldIsOneUndo)
- **Provenance per field**: "this placement (<document>)" when it is overridden; else the last of the
  prefab's writes (`RecordStore.Writes`, 4j-2) whose path holds the field — the defining file and line, a
  base prefab's (`via base`), or a mod's patch, with its mount (`InspectorRow.SetBy`); "default" when the
  prefab, merged from all its files, does not write the field at all. (test:
  ProvenanceNamesThePrefabsFileAndAPatchingMount)
- **The placement's own fields are a form**: at, yaw, name and frame (`relativeTo`, "(document)" for
  none), each a `SetPlacement`; `ed_set <name> at 1 2 3` (or `yaw`, `name`, `relativeTo`) does the same.
- **Values typed at the console** are read by the field's shape (`InspectorValue.TryParse`): numbers and
  whole numbers inside the field's Min..Max, bools (`true`, `yes`, `on`, `1`), vectors as `x y z`, `x,y,z` or
  `[x, y, z]`, enums by name in any case, record ids checked against the records of the type the field
  names (a bare name another namespace has is written in full; `none` is no record), strings without
  their quotes, and JSON for lists, maps and objects. A bad value is a console warning and no edit.
  (test: ConsoleValuesAreReadByTheFieldsShape)
- **An entity the document did not place** (one the game spawned, a prefab's child, anything when no
  document is open) has nowhere to save an edit: the model is read-only and `ed_set` refuses it. The
  ImGui panel **edits those live, as before, under an "Edited live: not saved" note**, so a developer
  tweaking a running game still can; the choice keeps the dev tools of a play run what they were.
  (test: AnEntityTheDocumentDidNotPlaceIsReadOnly)

Nested object fields, list editing in the panel and overriding a component the prefab does not name came
with #368 (§10q); a JSON value with double quotes passes the console in single quotes since #373 (§10v).

## 10j. As built: the record browser and forms (#224, 2026-10-01)

**Records are edited as JSON, saved into the file they came from.** `RecordDocument` (Sage.Editing) is one
record open: `RecordStore.RawJson` (the definition and each patch merged, comments gone: what the files
say, not the built object with its defaults and bases), a working copy, and a `CommandLog` of its own, so
its undo is apart from the placements document's. `RecordEditor` holds the one that is open and the
`ed_rec_*` console commands; the ImGui "Records" panel (`Sage.Editor`, docked as a tab beside the
inspector) only draws and calls them.

- **A field is set by a command**, `SetRecordValue`, at a path such as `components.timer.interval` or
  `items[2]` (a name matches ignoring case; an index may be one past the end of its list, which adds an
  element). Undo puts back the old value, or removes the field and the objects it made on the way, and a
  drag of one field is one edit. (test: ARecordIsOpenedAsItsJsonAndANestedFieldIsSetAndUndone,
  AFieldTheRecordDidNotHaveIsRemovedAgainByUndoAndTheObjectsItMadeWithIt, ADragOfOneFieldIsOneEditAndAnUnreachablePathChangesNothing)
- **A console value is a JSON literal** (`ed_rec_set hp 120`, `name "Bob"`, `at [1,2,3]`, `tags {a: 1}`),
  and anything that is not one is a string (`name Bob`). The console takes double quotes off an argument,
  so in a list or an object a bare word is a string as well (`[a, b]` is two). This is a private parser of
  Sage.Editing's, not the inspector's by `ValueKind` (#223): the record's JSON is untyped here, so the
  literal says what it is. (test: AConsoleValueIsAJsonLiteralOrElseAString)
- **Save writes where the record came from.** A record defined in a file of the game's own mount is
  changed in place with `JsonFileEdit.PatchRecord`: comments, key order and the neighbouring records stay.
  A record defined anywhere else (the engine's content, a kit, a mod) is saved the way a mod changes one:
  a `"patch": true` record in the game's own `data/patches/<type>_<namespace>_<name>.json` naming only
  what changed (objects merge field by field, a list is written whole), with the log saying the record is
  not the game's. A second save adds to that patch, or to a patch file of the record the game already had.
  A patch cannot remove a field, so a removal is reported and left. Either way the records are reloaded
  (the hot reload path), the reloaded record becomes the saved copy, and a later file that patches the
  value again is warned about. (test: ASaveGoesIntoTheFileTheRecordCameFromAndKeepsItsComments,
  ARecordFromAnotherMountIsSavedAsAPatchInTheGamesFolderAndTheMergedRecordShowsIt)
- **The game's mount** is the last folder mount that is neither the engine's (`sage`) nor a mod's: where
  a patch goes.
- **Provenance per field** is `RecordStore.Writes`: `RecordDocument.Provenance(path)` is the last write to
  the field or into it (`set components.timer.interval  game:data/things.json:5:22`), shown in the form's
  tooltips, and `Sources()` lists the files that wrote the record. `MetaAt(path)` walks the type's metadata
  (an object's fields, a list's item) for the range, tooltip, enum values and record type the form's
  widgets follow. (test: AFormKnowsAFieldsDeclarationAndWhereItCameFrom)
- **Console**: `ed_rec_open <type> <id>`, `ed_rec_set <path> <value>`, `ed_rec_get [path]`, `ed_rec_save`,
  `ed_rec_undo`, `ed_rec_redo`, `ed_rec_close`, `ed_rec_conflicts`; Ctrl+Z / Ctrl+Shift+Z / Ctrl+S act on the Records panel
  while it has the focus. (test: TheConsoleOpensSetsUndoesAndSavesARecord)
- **Per-field mod conflicts (#401, 2026-10-07).** `RecordConflicts.Find(engine, type, id)` (and an overload taking a built
  `ContentReport`) returns the open record's `FieldConflict`s (Kind, Path, keyed where a list is, e.g.
  `parts.inventory.items[village:lantern].count`; Winner; Current; Line; Contributions), each with its
  `FieldContribution`s (Mount, IsMod, Op, Path, the Value read back from the writing file with `RecordWrite.Written()`, At, Via,
  Wins). `RecordEditor.Conflicts` caches them for the open record, recomputed after an open or `Records.Reloaded`. The
  Records panel draws a "Mod conflicts (N)" header above the form and a table per field (mod, op and value, path; the winner
  green; hover for file:line:column and base), and `ed_rec_conflicts` prints the same. (tests:
  OpeningAConflictedRecord_ShowsEachModsValueForEachField_AndTheWinner, TwoModsEditingDifferentKeys_AreNoConflict_AndNothingOpenHasNone,
  AModDisablingARecordAnotherPatched_ShowsBothWrites)
- **The form is drawn over the JSON**, not with the inspector's widgets: those edit a field of a boxed
  struct (`FieldMetadata.Set`), which a JSON value is not. Both read the same metadata, so ranges, units,
  enums and record pickers agree.

## 10k. As built: wiring entity I/O (#225)

`Sage.Editing` holds the model and the console; the ImGui "I/O" panel (`WiringPanel`, Sage.Editor) only
draws them and passes the viewport click on.

| Piece | Where | What it does |
|---|---|---|
| `Wiring` | `src/Sage.Editing/Wiring.cs` | `Add`, `Update`, `Remove`, `Check`, `NameIt`; `ed_wire`, `ed_unwire`, `ed_wires` |
| `WiringModel` | same file | One placement's wires, the outputs on offer, the inputs a target takes, and `Lines` for the viewport |
| `WiringPanel` | `src/Sage.Editor/Tools/WiringPanel.cs` | The panel (a tab beside the inspector), the pick-a-target flow, the lines |

- **Every edit is a `SetOutputs`** (§10e) with the wires as the placement would then hold them, so each is
  one undo step and the placement's entity is re-spawned carrying the new `IOConnections`.
  `Remove` takes several wires in one step. (tests: EdWireAddsAConnectionToTheDocumentAndTheRespawnedEntity,
  WiringUndoesAndRedoesAsOneStepAndUnwireTakesWiresAway, AWireIsEditedInPlaceAndTheOutputsOnOfferAreDeclaredOrUsedInTheDocument)
- **A wire finds its target by name**, so `Wiring.Check` resolves the name to a placement of the document, else
  any entity of the world, and asks `EntityInputs.Takes` of *that entity*: an input only a component takes
  (`Toggle` is a relay's) is refused for a target without the component, and the refusal lists what the target
  does take. `!self`, `!activator` and `!caller` are not checked against a target; an input only content
  listens for (a state machine's `on`) is accepted, as nothing here says who hears it. A refused wire changes
  nothing and logs nothing to the history. (test: AnInputTheTargetCannotTakeIsRefusedAndTheReasonListsWhatItTakes)
- **A target with no name is refused**, and the panel offers `Wiring.NameIt`: a name from its prefab, made
  unique, as a `SetPlacement` step of its own. (test: ATargetWithNoNameCannotBeWiredUntilItIsNamed)
- **Outputs are free text with suggestions.** Nothing says which outputs one component can fire; the engine's
  declared `EntityOutputs` are offered with their descriptions, then the names the document's wires already
  use. `requires` is kept as it is (shown, not edited: a condition has no form yet).
- **Console**: `ed_wire <from> <output> <to> <input> [delay] [value]` (the value is the rest of the line),
  `ed_unwire <from> <index|output>` (the number `ed_wires` prints, or every wire of an output) and
  `ed_wires <name>`.
- **The panel**: the selection's wires, each with its delay and value editable (applied when the field lets
  go, so typing is one undo step), then the add flow: an output (text and a combo), "pick target" (the next
  viewport click or an outliner row, Esc cancels; the wired placement stays selected, and the gizmo leaves
  the click alone), an input combo of what the target takes, a delay and a value. Lines are drawn on the
  background draw list between each wired pair of the document (`WiringModel.Lines`, projected by
  `ViewportCamera.ToScreen`), the selection's in and out bright, with an arrowhead and the wire's output and
  input. (test: EdWireAddsAConnectionToTheDocumentAndTheRespawnedEntity, for the lines' ends)
- **A wired edit plays**: the wire is in the entity the document spawned, so firing the output ticks the
  input through. (test: AWiredPlateFiresTheDoorOnceTheEditHasRespawnedIt)

## 10l. As built: play-in-editor (#226, 2026-10-01)

**Play makes a second, real world; Stop throws it away** (phase 10a decision 2). The edit world is never
unpaused: whatever a play does (a door opened, a crate knocked over, a goblin killed) happens in another
world, and that world is destroyed. `PlaySession` (`src/Sage.Editing/PlaySession.cs`) is the model; the
host only chooses which world it draws and sends input to.

- **The play world is built as the host builds its main world**, by `Engine.CreatePlayWorld(name, scene,
  document, record)`: every module furnishes it, the game's rules are made and *started* (so it has its
  player and whatever `OnWorldStarted` adds), and its Fixed systems run. Its scene is the one the edit
  world shows (a new level's own scene once it is a record).
- **The document plays as it is in memory, not as its file says.** The record is the editor's copy
  (#217), so an unsaved placement, override or wire plays, and the record store and the file are not
  touched. The world carries it as a resource (`PlayedDocument`), and wherever its scene would place that
  document from the store (`Scenes.Place`, a streamed scene's buckets, a hot reload's re-placing) it places
  the copy; a document the scene does not name is placed beside it, as the editor showed it.
  (test: AWireMadeInTheEditorFiresInPlay, ADocumentTheSceneDoesNotNamePlaysBesideIt)
- **Stop leaves everything as it was**: the document's record, its history and its dirty state, the edit
  world's entities, the store. (test: PlayThenStopLeavesTheDocumentAndTheEditWorldAsTheyWere)
- **The player** is the one the rules spawned at the scene's start. Given a `PlayStart` (an eye and a yaw
  in degrees; the host passes the free camera) it is moved onto the first surface below the eye
  (`Placing.Surface` on the edit world, whose physics mirrors the scene), as high above it as the scene's
  own start stands above the ground below *that* (so a pawn whose origin is its middle starts a metre up),
  and faces the camera's way (`PlayerInput.RequestView`). Headless, with no camera, it stays at the start.
- **Console**: `ed_play [x y z [yaw]]` (from the free camera, or from that eye) and `ed_stop`. Both refuse
  outside an edit world, and a second `ed_play` while playing is refused rather than stacking worlds.
- **In the host** (`PlayBar`, `src/Sage.Editor/Tools/`): a Play button under the gizmo's toolbar and
  **Ctrl+P** play; while playing the play world has the screen (`Renderer.ScreenWorld`), the host's input
  goes to it (`Gameplay` on, `Editor` off, and the player's commands go to its `PlayerInput`), the free
  camera waits, and the editor's panels give way to a slim bar with a Stop button. **Escape** (the host's
  `Menu` action, which quits a game run), Ctrl+P and the button all stop; F5 stays the game's quick-save.
  The console closes on the way in, because an open console takes the keyboard from the game; `~` opens it
  over the game, and it opens again on the way out if it was open.

**Not built:** simulating in the edit world (Unity's "keep changes"); playing from a selected entity; a
second viewport showing the edit world while playing.
## 10m. As built: the problems panel (#227, 2026-10-01)

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
  row to the panel's `OpenRecord` hook (DevTools sets it to the record browser's `OpenById`, §10j).
- **`ed_problems`** prints the summary, then each file and its problems. (test: TheConsolePrintsTheProblemsByFile)

## 10n. As built: the phase 10a exit (#228)

**A designer builds a level from prefabs (place, tune, wire, undo, play, save) without touching JSON, and
the file they saved loads in the game** (REDESIGN §5 row 10). `tests/games/editor` is the proof: a game
with no C# and no kit, four prefabs on engine parts only (a pressure plate, a `body` trigger; a door, a
`body` with a `mover`; a crate; a `light`), and an empty level, the scene `level` with a floor and a player
start naming `level_placements`, which holds a hand-written comment and an empty `place`.
`EditorExitTests` (`tests/Sage.Tests/Editing/`) drives the editor only through its console commands, on a
copy of the game in a temporary folder, with the same command sets DevTools registers and the edit world
`-edit level` makes. The guide for designers is [docs/EDITOR.md](../EDITOR.md).

- **End to end**: `doc_open level_placements`, `ed_place` the plate and the door, `ed_move` and
  `ed_rotate` both (the door through the selection), `ed_set door mover.seconds 0.5`, `ed_wire plate
  OnStartTouch door Open`; `ed_undo 2` takes the wire and the override away from the document and the
  world, `ed_redo 2` puts them back. `ed_play` plays the unsaved level; the player put on the plate opens
  the door in half a second (the override, not the prefab's two). `ed_stop` leaves the document, its
  history and the edit world as they were; `doc_save` writes the file the document came from, with no
  problem listed. The saved file still holds the comment above the list it now fills, and a plain boot
  (`HeadlessApp.ForGame`, no editor) loads the same placements, override and wire, and the plate opens the
  door there too. (test: ADesignerBuildsALevelFromPrefabsInTheEditorAndTheGameLoadsWhatTheySaved)
- **A level from nothing**: `doc_level storeroom`, a crate and a lamp placed, the lamp's `light.range`
  overridden, the crate duplicated and moved, saved; a game started in `editor:storeroom` has them where
  the editor put them. (test: ANewLevelMadeInTheEditorIsASceneTheGameBoots)
- **The game is clean**: it validates with no error and no warning of its own, the palette offers the
  four prefabs, and `-edit level_placements` names the document in the scene that places it.
  (test: TheEditorGameValidatesAndOffersItsPrefabs)
- **One fix.** `ed_set door mover.seconds` was refused: the `mover` part builds a `sage:mover` component,
  and `InspectorModel.Find` took the first group the name matched, the component, which the inspector
  shows read-only. Without a `components.` or `parts.` prefix a row that can be edited now wins, so a
  part's field is reached by its part's name. (test: ADesignerBuildsALevelFromPrefabsInTheEditorAndTheGameLoadsWhatTheySaved)
- **CI**: `sage validate tests/games/editor`, the game in the `sage schema` list (and `SchemaTests`), and
  a smoke run of the host with `-edit level` on it that places, overrides, wires, undoes and redoes, plays
  for a second, stops and lists the problems, without saving; the Sandbox's `-edit yard` run stays.

## 10o. As built: the asset browser and material preview (#366, 2026-10-07)

Models, textures and sounds were named by typing a path into a record. The **Assets** panel
(`src/Sage.Editor/Tools/AssetsPanel.cs`, a tab beside Log and Problems) lists them; what it shows and does
is `Sage.Editing`'s `AssetBrowser`, `AssetPicking`, `AssetReferences` and `AssetRename`, headless and tested.

- **The listing.** `AssetBrowser` lists every asset in the VFS by kind (`AssetKinds`: texture, mesh, sound,
  map, font, shader), with the mount that provides it, a mod by its id, and the mounts it shadows; it filters
  by kind, by mount and by search words. A texture has a thumbnail; a selected asset shows its mount, what it
  shadows, its size, a large preview, Play for a sound, "Place in the document", "Find references" and
  Rename. (test: TheBrowserListsAssetsByKindMountAndModWithWhatEachShadows)
- **Picking.** An asset dragged onto an inspector field marked `[AssetKind]` is a `SetOverride`, undone with
  `ed_undo`; onto a Records-panel string field that names an asset, a record edit; onto a slot of the
  Records panel's **Material preview** strip (`AssetPicking.Slots`: albedo `params.Albedo`, normalMap,
  specularMap, emissiveMap, environmentMap, effect), the material's field. Dropped in the viewport it is
  placed: the first prefab whose record names it (`AssetPicking.PrefabFor`), else the engine's
  `sage:static_mesh` (a bare `mesh_renderer`, `engine_content/data/editor.json`) with the model as a
  `mesh_renderer.mesh` override; one undo step. (test: AModelPickedIntoAPlacementsFieldIsAnOverrideAndDropsInTheViewportPlaceIt)
- **The records panel is live.** `RecordEditor.Live` (on by default) shows every edit, undo and redo of the
  open record in the game before a save, through `RecordStore.Preview(type, id, fields, out error)` and its
  `Previewed` event; the renderer drops its cached materials when a `material` is previewed, so a texture
  picked for a material is on the wall at once. Closing unsaved puts back what the files say
  (`RecordDocument.Revert`); a preview that does not build leaves the record as it was, with the reason in
  `RecordDocument.PreviewError`. (tests: ATexturePickedForAMaterialShowsInTheGameAtOnceAndIsUndoable,
  APreviewThatDoesNotBuildLeavesTheRecordAsItWasAndAReloadTakesAPreviewBack)
- **Rename** (05 §3.2). `AssetRename.Plan` and `Apply` move the file, and its cooked `.sgtex` or `.sgmesh`,
  inside the game's own folder, rewrite every quoted occurrence of the path in the game's `.json` and `.map`
  files (comments and layout kept), reload the records and reopen the open document and record. It is
  refused when the asset is the engine's, a kit's or a mod's, when the new path is taken or changes the
  extension, when a file outside the game's folder names the asset, or when an open document or record has
  unsaved edits. It is not on an undo history; renaming back is the undo.
  (tests: RenamingAnAssetMovesItAndItsCookedFileAndRewritesWhatNamesIt,
  ARenameIsRefusedWhenTheAssetOrAReferenceIsNotTheGamesOrSomethingIsUnsaved)
- **Console**: `ed_assets`, `ed_asset_refs`, `ed_asset_pick`, `ed_asset_place`, `ed_asset_rename` and
  `ed_rec_live` (`AssetCommands`). (test: TheConsoleListsPicksPlacesAndRenamesAssets)

**Not built:** mesh thumbnails (a model shows a labelled box; only textures have pictures) and a rendered
material ball (the preview is the material's texture slots plus the live world).

## 10p. As built: multi-select, box select, scale and three-axis rotation (#367, 2026-10-07)

- **The placement record** gains `pitch` and `roll` (degrees, applied as `Quaternion.CreateFromYawPitchRoll`:
  roll about the thing's -Z first, then pitch about X, then yaw) and `scale` (a vector, default `[1, 1, 1]`;
  every axis must be above zero, a content error otherwise). There is no version bump: all three default to
  what a yaw-only placement meant, so old files load unchanged, and a save leaves them out at 0, 0 and
  `[1, 1, 1]`, so a yaw-only placement is written as before. They apply wherever a placement spawns
  (placements documents, a scene's `place` and player, streamed sectors), and a placement's scale multiplies
  the prefab's own Transform scale. `PlacementExtensions.PlacementRotation` builds the rotation;
  `SageMath.YawPitchRollOf` takes yaw, pitch and roll back out of one (at ±90° of pitch it is all yaw), and
  `ReadPlacements` reads all three back. (tests: AYawOnlyFileLoadsAsBeforeAndPitchRollAndScaleSaveAndLoad,
  AScaleOfZeroIsAContentErrorAndTheInspectorRefusesIt, AFullRotationComesBackAsTheYawPitchAndRollThatMadeIt)
- **The selection is several placements.** `EditorSelection.Placements` is in pick order; the last one is
  `Placement`, the one the inspector and the gizmo show. `Select`, `Add`, `Toggle`, `Contains`, `PlacedOf`
  and `SelectPlaced(entity, toggle)` change it; what leaves the document leaves the selection. Ctrl+click in
  the viewport or the outliner toggles. (test: SelectingSeveralTogglesAddsAndDropsWhatLeavesTheDocument)
- **Box select.** A drag on empty picture draws a rectangle; on release `EditorPicking.PlacementsInBox`
  selects the placements the camera draws inside it (Ctrl or Shift adds). (test: ABoxSelectsWhatTheCameraDrawsInsideIt)
- **Gizmos.** `GizmoMode.Scale` (T) is axis boxes and a centre handle (`GizmoHandle.All`); the factor is how
  much further from the centre the pointer is, snapped to `ScaleStep` (0.1 by default, never below 0.01).
  The rotate gizmo has three rings, X, Y and Z. `GizmoSpace` is World or Local (L, the toolbar,
  `ed_space`): in local space the move and rotate gizmos follow the placement's own axes, and a local-space
  move snaps the distance moved to the grid. Scale is always local. (test: LocalSpaceTurnsTheMoveGizmoWithThePlacement)
- **Group edits are one undo step each.** `SetPlacements` (merged over a drag) and `CommandGroup` (a delete
  or duplicate of several); `ViewportTools.MoveBy`, `RotateBy`, `ScaleBy`, `Delete` and `Duplicate` take a
  list. A group turns and scales about the last-selected placement, where the gizmo is. A yaw-only placement
  turned about the vertical keeps an exact yaw. `PlacementFields` gains `Pitch`, `Roll`, `Scale`, `Rotation`
  and `WithRotation`. (tests: AGroupDragOfTheRingsAndTheScaleGizmoIsOneUndoStepEach,
  TenCratesTurnAndScaleTogetherAndUndoPutsEveryOneBack: ten crates turned, tipped and scaled, and three undos
  put every one back)
- **Console**: `ed_select_add`, `ed_select_all`, `ed_nudge`, `ed_turn`, `ed_scale`, `ed_space`,
  `ed_scalestep`; `ed_gizmo scale`; `ed_delete` and `ed_duplicate` with no name act on the whole selection;
  `ed_set` takes `pitch`, `roll` and `scale`. The inspector's placement form has all three.

**Not built:** far proxies and a travel `SceneEntry` still use only the yaw; a prefab's `children` still
have only a yaw; physics colliders are not scaled by a placement's scale (the transform is); a `.map`
entity's pitch and roll are unchanged.

## 10q. As built: nested fields, lists and added components in the inspector (#368, 2026-10-07)

- **Lists, maps and nested objects are rows under their field** (`InspectorRow.Children`, `Parent`, `Top`,
  `Name`, `Index`, `Key`), with paths like `inventory.items[1].count` that `InspectorModel.Find` takes. An
  edit of a row inside a field rewrites the whole top-level field with the change made in it: one
  `SetOverride`, so a drag over a nested number merges into one undo step, and reverting a nested row
  reverts the field. (tests: AnNpcsInventoryListIsEditedRowByRowAndASaveKeepsIt,
  ANestedDragIsOneUndoAndBadListEditsChangeNothing)
- **Add, remove and reorder.** `InspectorModel.AddItem` (the element type's default, or a value; a map's
  entry needs a key), `RemoveItem` and `MoveItem`, each its own undo step. The panel draws a tree node per
  list, map or object, up and down arrows and `x` per element, and `+ add` at the end (for a map, a key box
  and add). (test: AnNpcsInventoryListIsEditedRowByRowAndASaveKeepsIt)
- **A component the prefab does not name, added to one placement.** `InspectorModel.AddableComponents()`
  offers the registered components the entity lacks (not Transform, not `[Transient]` ones);
  `AddComponent(id)` writes an empty body, `overrides.components["sage:point_light"] = {}`, through the new
  `SetOverrideBody` command, which spawning merges as the component at its defaults. Its group is
  `InspectorGroup.AddedByPlacement`, noted "added by this placement", and its fields are editable;
  `ClearOverride` keeps the empty body, so reverting its last field does not remove it; `RemoveComponent`
  takes it off with its overrides. Removing one of the prefab's own components is refused, and so is adding
  one the entity already has (a part's). The panel has an "Add component..." combo at the bottom and a
  "remove" button in an added group's header. (test: ALightIsAddedToOnePlacedCrateAndASaveKeepsIt)
- **Console**: `ed_add`, `ed_remove`, `ed_reorder`, `ed_add_component`, `ed_remove_component`; `ed_set`
  takes a nested path; `ed_inspect` prints a list's elements and their fields indented under it
  (`inventory.items = [3] *`, `inventory.items[0].count = 7`). (test: ANestedDragIsOneUndoAndBadListEditsChangeNothing)

## 10r. As built: the AI graph view (#369, 2026-10-07)

The AI graphs are state machines, schedules, routines and, since #387, behaviour trees, and that is what the
view shows. `AIGraph` (`src/Sage.Editing/AIGraph.cs`) is the headless model, `AIGraphCommands`
its console, and the **AI Graph** panel (`src/Sage.Editor/Tools/AIGraphPanel.cs`, a tab beside the
inspector) draws it. It reads the record open in the Records panel (`RecordEditor.Current`) when that is a
`state_machine` (a tree of states, nested and parallel, transitions as edges, the machine's own transitions
under "(any)"), an `ai_schedule` (a list of tasks), a `routine` (a list of entries) or, since #387, a `behaviour_tree` (below).

- **Edits are commands in the record document's own history** (`ed_rec_undo`, the Records panel's Undo):
  add, remove, move among siblings, reparent (state machines only), rename. Renaming a state renames every
  `to` and `initial` naming it; a state added to an empty machine, or under a plain state, becomes its
  `initial`; removing a state drops the transitions to it and moves an `initial` that named it.
  (tests: AStateIsAddedAndMovedAndBothAreUndone, AStateIsReparentedRenamedAndRemovedKeepingTheMachineLoadable,
  AScheduleIsAListOfTasksEditedWithUndoAndTheTaskAnAgentIsOnIsActive)
- **Live highlight.** `AIGraph.Runners(world)` and `Active(world, entity)` give every state a machine is in
  (outer, inner and parallel regions) and the task an agent's `sage:ai_state` is on, read through the component ids
  `sage:state_machine`, `sage:ai_state` and `sage:routine` and their declared fields, so `Sage.Editing` does
  not reference the gameplay assembly. The panel watches the play world while playing, else the edit world;
  the first runner is shown, and a picker chooses another. A routine has no live node (the entry in force is
  the gameplay clock's arithmetic). (test: WhilePlayingTheViewShowsTheStatesTheGuardIsIn)
- **Saving a move.** `JsonFileEdit.PatchRecord` now writes an object whole, in its new order, when its keys
  were reordered or a new key was put before an existing one, so a moved state is saved in its new place in
  the record's own file. A patch-file save still cannot reorder: objects merge. (test: AMovedStateIsSavedInItsNewPlace)
- **Console**: `ed_ai_tree`, `ed_ai_add`, `ed_ai_remove`, `ed_ai_move`, `ed_ai_reparent`, `ed_ai_rename`,
  `ed_ai_active`; a node is a state's name, a path, or a list index (`2` or `#2`).
  (test: TheConsoleAddsMovesAndUndoesANodeOfTheOpenRecord)
- **Behaviour trees (#387).** `AIGraphKind.BehaviourTree` draws a `behaviour_tree` record as its nodes (a task with
  its arguments, a composite, a decorator, a blackboard `set` or `check`, and any `[when ...]` guard, as in
  `fight: MoveToTarget  distance 2  [when SeeEnemy]`); the running node and its ancestors are active, read from
  `sage:ai_state`'s `tree` and `taskIndex` (the task node counted depth first). Add (into a composite), remove,
  move among siblings and rename (a task node's rename changes its task, any other gets a `name`) are undoable
  steps in the record's history; reparenting is refused. (test: ABehaviourTreeIsATreeOfNodesEditedWithUndoAndTheNodeACreatureRunsIsActive)

## 10s. As built: the conditions and actions form (#370, 2026-10-07)

A wire's `requires` and a dialogue option's conditions were typed as JSON. The **Conditions** panel
(`src/Sage.Editor/Tools/VocabularyPanel.cs`, a tab beside the inspector) is a form over any open
vocabulary (04, 16 §3), opened from the I/O panel's per-wire **requires** button and from the Records
panel's `<field>...` buttons, which appear on every object for each field that holds conditions, actions or
any vocabulary entry, written yet or not. The model is `VocabularyCatalog`, `VocabularyForm` and
`VocabularyEditor` in `Sage.Editing`.

- **The catalog** lists a vocabulary's registered entries with their declared settings: field metadata,
  `[EntryValue]`, record type, range, default and a nested vocabulary. (test: TheCatalogListsAVocabularysEntriesWithTheirDeclaredSettings)
- **The form.** Each entry is a searchable picker of the vocabulary's ids (owner shown, settings as a
  tooltip); each setting is a widget by its metadata (a bool a checkbox, an enum a combo, a record id a
  combo of that record type, else text read by its kind with range checks); `reset` takes a setting back to
  its default; a nested setting (`all`, `any` and `not`'s `of`) gets rows of its own and a `+ of` picker; `x`
  removes an entry. Edits write the long form (`{ "condition": "has_item", "item": "key_iron" }`);
  shorthand and bare ids are read as it.
- **One undo step per edit** in the owner's history: the record's (`ed_rec_undo`), or the placements
  document's for a wire (`ed_undo`, a `SetOutputs`). A wire's `requires` is held as the object the game
  reads, so a value the reader refuses is refused by the form. (tests: AWiresRequiresIsAuthoredFromTheFormEachEditOneUndo,
  ADialogueConditionIsAuthoredSavedAndReadByTheGame)
- **Live checks**: an unknown id (with a did-you-mean), a setting the entry does not declare, a record that
  does not exist, a number outside the field's Min and Max, an enum name that is not a member, then whatever
  the game's reader refuses. The problems panel lists the open form's problems live (`IProblemSource`,
  `ProblemList.Add`) and checks every wire's `requires` in the open document always. (tests:
  TheFormRefusesWhatTheEntryDoesNotDeclare, ShorthandAndBareIdsReadAsTheLongFormAndProblemsReachTheProblemsPanel)
- **Console**: `ed_vocab`, `ed_vocab_rec`, `ed_vocab_wire`, `ed_vocab_show`, `ed_vocab_pick`,
  `ed_vocab_add`, `ed_vocab_param`, `ed_vocab_remove`, `ed_vocab_close`; the form closes with its record.
  (test: TheConsolePressesEveryButtonOfTheForm)

## 10t. As built: every panel drawn in the real host (#371, 2026-10-07)

The ImGui panels had no tests; the smoke run only opened the editor. Now **every panel is drawn in front in
the real host by CI**: `ed_panel all` (`PanelTour` in `Sage.Editing`) brings each panel listed by
`DevTools.ListPanels()` to the front in turn for three frames, opening the ones behind a cvar or toggle
first (Console, Viewport, Visual log), and logs `ed_panel: <title> (i/n)` for each. A panel that throws
crashes the host and fails CI (checked by a probe throw in a hidden docked panel: the old run passed, the
new one failed). `tools/editor_smoke.sh` runs `-edit level` on a copy of `tests/games/editor`, places a
plate, a door and a crate, undoes the crate, overrides the door's `mover.seconds`, wires the plate to the
door, tours the panels, plays, stops and saves, then checks the saved file (exactly the plate and the door,
the override, the wire, the hand-written comment kept); the Sandbox's `-edit yard` smoke run also ends
with the tour. The status bar's text is `EditorStatus.Line`, headless now.
(tests: AllBringsEveryPanelForwardInTurnOpeningEachOnItsTurn, OnePanelIsNamedByItsTitleOrItsUniqueStart,
ANewRequestReplacesTheRestAndTitlesAreListedOnce, TheCommandShowsPanelsOnlyInTheEditor,
TheStatusLineSaysTheDocumentTheSelectionTheWorldAndTheCamera)

A new panel adds one line to `DevTools.ListPanels()` (and its dock line in `EditorLayout.BuildDefault`),
and CI then draws it. **Not built:** a panel's input handling (clicks and drags) is not driven; only each
panel's Draw, with the state the script set up.

## 10u. As built: terrain tools, revert all and nesting (#372, 2026-10-07)

- **Sculpt in the base engine** (`TerrainSculpt`, `SculptSector`, `src/Sage.Simulation/World/TerrainSculpt.cs`,
  SAGE0129): per sector, height offsets over the generator (129 by 129), paint (a byte per vertex for each of
  layers 0 to 3, laid over the `terrain_material`'s rule weights; a cell's surface layer is a layer painted
  at least half) and a water surface (absolute metres). It is saved as a deflated `.sterrain` file. The
  `terrain` record's new `sculpt` field names it; empty means `terrain/<record name>.sterrain` if there is
  one; a named file that is missing or unreadable is a logged error, and the ground is the generator's
  alone. Every generation applies the sculpt (the main thread, jobs and the far ring's coarse copies) from an
  immutable snapshot, and edge normals read the neighbours' sculpt, so there is no seam. `Terrain.Refresh`
  rebuilds only what carries the new `sage:terrain_built` tag (chunk meshes, collision chunks, sculpt
  water); the system `sage.streaming.terrain_water` places a `water_volume` over a sector with water.
  (tests: AHillOnASectorEdgeHasNoSeam, TheSculptFileRoundTripsAndOneThatIsNotASculptIsRefused)
- **The editor's model.** `TerrainDocument` has its own undo history; a stroke (`BeginStroke`, `Dab`,
  `EndStroke`) is one undo step; tools are Raise, Lower, Smooth, Flatten and Paint; `SetWater` sets a
  sector's water; `Save` writes the `.sterrain` into the mount its file is in, else the terrain record's
  namespace's mount. The **Terrain** panel (beside the outliner) has the tool, radius, strength, layer,
  flatten height, the camera sector's water, undo, redo and save; armed, a left drag in the viewport is one
  stroke. (tests: ASculptedHillAPaintedPathAndALakeAreSavedAndTheStreamedWorldShowsThem,
  AStrokeOfManyDabsIsOneUndoStepAndRedoPutsItBack, LowerFlattenAndSmoothMoveTheGroundTowardTheirTargets)
- **Console**: `ed_terrain`, `ed_sculpt`, `ed_paint`, `ed_water`, `ed_terrain_save`, `ed_terrain_undo`,
  `ed_terrain_redo`, `ed_terrain_history`. (test: TheConsoleSculptsPaintsSetsWaterUndoesAndSaves)
- **Revert all** (REDESIGN F31's "revert to prefab"): `RevertPlacement`, `PrefabCommands.RevertAll`,
  `ed_revert_all` and the inspector's "revert all" button take every override of a placement away as one
  undo step. (test: RevertAllTakesEveryOverrideAwayAsOneUndoStep)
- **Nesting.** `PrefabCommands.MakePrefab` (`ed_make_prefab <id> [placement...]`, the selection when none
  is named) nests placements into a new prefab's `children`, writes it to `data/<name>.json` in the game's
  mount, adds it to the store and places it where the first stood, as one undo step. It refuses a pitch,
  roll or scale, mixed frames, and wires from or to the placements. (tests: MakePrefabNestsPlacementsIntoANewPrefabPlacedWhereTheyStood,
  MakePrefabRefusesWiredPlacements)

**Not built:** the client does not draw water volumes (physics and swimming only); far-ring coarse copies
already loaded keep their old ground until they are generated again; paint shows only with a
`terrain_material`.

## 10v. As built: single-quoted console arguments (#373, 2026-10-07)

A JSON value with double quotes could not pass the console's tokenizer. A token that starts with a single
quote is now taken raw up to the closing quote: double quotes, backslashes, `;` and `//` included, and `\'`
is a quote. A lone or unclosed `'` stays literal, and double quotes behave as before; `CommandLine.Quote`
double-quotes a value that starts with `'`. So `ed_set door loot '{"id":"x"}'` and
`ed_add trader inventory.items '{"item":"sandbox:knife","count":4}'` work.
(tests: SingleQuotes_TakeAJsonArgumentRaw, SingleQuotes_OldInputsKeepTheirMeaning,
AQuotedJsonObjectIsAnElementOrAWholeValueFromTheConsole)

## 10w. As built: dev panels that do not allocate per frame (#374, 2026-10-07)

The outliner built a label per entity per frame and the log panel formatted every line each frame.
`EntityLabelCache` (`Label`, `Prune`, `Count`) builds an entity's label once and again only when its name
changes, and `LogView.Texts` keeps one formatted string per shown line, parallel to `Lines`. The outliner
and the log panel draw them through `ImGuiListClipper` (the outliner clips only while no row is expanded,
since rows then differ in height). The console window still formats its lines; it was not in scope. The
clipper itself is checked only by the smoke run. (tests: OutlinerAndLogPanelFramesAllocateNothingOnceTheirTextExists,
OutlinerLabelsAreBuiltOnceAndRebuiltOnlyWhenTheNameChanges, LogLinesAreFormattedOnceAsTheRingSlides)

## 10x. As built: document tabs, and the editor for modders (#375, 2026-10-07)

- **Tabs.** `EditorWorkspace` holds several placements documents, each in an edit world of its own
  (`Engine.CreateEditWorld`, placed with the scene that names the document, else the start scene) with its
  own undo history. One tab is active: `doc_*`, `ed_undo` and `ed_redo` and the tools act on it, and its
  world is `Renderer.ScreenWorld`. The first tab is the host's `-edit` world, never destroyed (closing it
  closes its document); later tabs make and destroy their own worlds. Opening a document that is open
  switches to its tab; an empty active tab in the same scene is reused; closing a tab with unsaved changes is
  refused unless forced. The free camera keeps its pose per tab, and switching tabs while playing stops play
  first. The ImGui strip across the top of the dock space (`DocumentTabs`) has an unsaved dot, close buttons
  and a "+" popup (open in a new tab, a new document, the "Save into" target). (tests:
  EachTabIsADocumentInAWorldOfItsOwnWithItsOwnUndo, TheTabCommandsOpenSwitchListCloseAndPickTheMod)
- **Saving into a mod.** With `ed_mod <id>` (a loaded mod) the mod's folder is the only one written
  (`EditDocument.Target`, `SavesAsPatch`, `SavedTo`): a document defined in the mod saves in place; a new
  document is made in the mod's namespace and file; a document defined elsewhere, such as the shipped game's
  level, is not written but saved as a `"patch": true` placements record in
  `<mod>/data/patches/placements_<ns>_<name>.json` (the whole `place` list, with `origin` and `relativeTo`
  and ids in full), changed in place by later saves; a new document in another namespace is refused. The
  title and the tab say "(patch)". Record ids inside a patched placement's overrides are written in full with
  the prefab's namespace (`OverrideIds`, found through the generated metadata). With no target, saving is as
  before. An existing record is now saved into the file of the mount that defined it (`RecordStore.MountOf`),
  not the first mount with a file of that name. (tests: AModderSavesBothTabsIntoTheModAndTheGamesLevelAsAPatch,
  WithoutAModTargetTheGamesLevelSavesInPlaceAsBefore,
  WhileModdingANewDocumentIsTheModsAndOneOfTheGamesNamespaceIsNotSaved,
  AnOverridesBareRecordIdSavedAsAModPatchStillNamesTheGamesRecord)
- **The editor beside a Shipping game.** `sage package <game> --out <dir> --editor [--editor-host <dir>]`,
  or `Sage.Sdk`'s `-p:SagePackageEditor=true` (`SageEditorHostDirectory`), keeps the Shipping host at the
  top of the package and adds a Debug or Development host in `editor/` (without the `sage` CLI) with
  `edit.sh` and `edit.cmd`, which run `editor/Sage.Host -game game -edit` with the arguments given. A modder
  runs `./edit.sh [level] +ed_mod <their mod>`. A Shipping editor host, a missing one, or the game's own host
  given as the editor host is refused. It is not a separate executable: the modders' editor is the
  Development host, packaged beside the game. CI's Linux job packages the template game with the editor and
  opens it (`tools/smoke_run.sh --packaged-editor`). (tests: WithTheEditorAPackageHasADevelopmentHostInEditorAndLaunchersOnTheSameGame,
  AnEditorHostWithoutTheEditorOrTheGamesOwnHostIsRefused)
- **Console**: `ed_tabs`, `ed_tab`, `ed_tab_open`, `ed_tab_new`, `ed_tab_close`, `ed_mod`
  (`WorkspaceCommands`).

**Not built:** tabs hold placements documents only (a record keeps its own panel); a patch cannot remove a
game record, only replace its list; the editor package is a second full host, with the Development
configuration's dev cvars and commands (there is no Shipping-with-editor configuration); the `ed_mod`
target is chosen per session, not remembered; on Windows `edit.cmd` is checked by the unit test only.

## 10y. As built: blockout brushes (#61, 2026-10-07)

Walls, floors and ramps are drawn in the editor now. **A brush is a placement** of one of three engine
prefabs, `sage:brush` (a 2 by 2 by 2 box), `sage:wedge` (2 by 1 by 2) and `sage:cylinder` (1 by 2 by 1, 12
sides), in `engine_content/data/blockout.json`, so the palette offers them, and moving, turning, scaling,
multi-select, duplicate, delete, undo, save with comments kept, play and a mod's `place+` patch are a
placement's (test: AModAddsBrushesToALevelByPatchingItsDocument). The scale gizmo resizes a brush: its
scale multiplies `size`. The `.map` importer, its mesh system and `fgd_export` are unchanged; TrenchBroom
stays a one-way path in.

- **The `brush` part** (`BrushPart`, `src/Sage.Simulation/Levels/Blockout.cs`, SAGE0121): `shape` (Box,
  Wedge or Cylinder), `size`, `sides` (3 to 64), `material` for the whole brush, a material per face
  (`top`, `bottom`, `north` (-Z), `south` (+Z), `east` (+X), `west` (-X), `side` for a cylinder's round
  faces), `textureScale` (metres per repeat; UVs are world-aligned, so neighbouring brushes continue each
  other's texture), `layer` and `surface`. A wedge is a ramp rising to its north side. A placement's `at`
  is the middle of the brush's floor. (tests: EachShapeIsAClosedConvexSolidWithItsFacesFacingOut,
  AWedgeRisesNorthAndATurnedBrushTurnsItsHull)
- **What it becomes**: a static convex hull (`IPhysicsWorld.AddHull`, turned by the placement's rotation),
  a convex polygon per face (`LevelFace`, as a `.map` brush's) on the `[Transient]` component
  `sage:blockout_brush`, and in the client one mesh per material from `BlockoutMeshSystem`
  (`sage.client.blockout_mesh`); a headless server has the collision only.
  (test: ARoomOfBrushesIsBuiltSavedAndReloadedWithItsCollidersAndMeshes)
- **Checks.** `sage validate` checks a brush body as part options (material records exist, size above zero
  on every axis, 3 to 64 sides, a positive `textureScale`) and a face material the shape has no face for ("a
  wedge has no south face"); a placement's override is checked merged with its prefab's `brush` part.
  (test: ALevelsBrushesAreCheckedWhenContentLoads)
- **Editing.** The **Brushes** panel (a tab beside the palette) arms Box, Wedge or Cylinder (a click stands
  one on the surface, snapped), sets a size and material for new brushes, paints faces ("Paint faces with
  this material": click a face, Shift+click the whole brush), shows the grid, and edits the selected brush's
  shape, sides, size and face materials, each one undo step. The console is `ed_brush`, `ed_brush_size`,
  `ed_brush_shape`, `ed_brush_material` and `ed_brushes` (`BlockoutTools`). (tests: BrushEditsAreUndoneAndRedone,
  PaintingAFaceGivesTheFaceUnderTheClickItsMaterial)

**Not built:** CSG (subtracting one brush from another), per-face texture offset and rotation, vertex
editing, brush entities (a brush that is a door or trigger: use a prefab with its own collider), lightmaps
for editor brushes, and an export to `.map`. A brush nested as a prefab's child is built at its local
transform.

## 10z. As built: a play session's own log, user folder and crash report (#49, 2026-10-07)

The editor's play world is a second app in the process, and its log lines, levels and once-only keys used
to be the editor's. Each app now has an `AppEnvironment` (Core): its own `Logger`, user folder and crash
report sections, current for the code it runs, with every line also reaching the parent's sinks, so the
editor's log file still sees its play session. Design 02 §3.4 has the details.
(tests: TwoAppsInOneProcess_LogToSeparateSinks_WithSeparateLevels, AnAppsLinesAlsoReachTheLogItWasMadeUnder,
UserFolderAndCrashSections_ArePerApp)

## 11. v1 scope vs later

**Phase 10a is done (#215, exit #228, §10n)**: the v1 list below is built. **Phase 10b is done too (#365,
§10o to §10z, 2026-10-07)**: brushes, the asset browser, the AI graph view, the conditions and actions
form, multi-select and scaling, nested inspector fields, terrain tools, document tabs and the editor for
modders.

- **v1 (minimal, for building the vertical slice):**
  - ~~open/save a map document~~ **done (F28)**, as a placements document;
  - ~~the outliner~~ **done**;
  - ~~an editable inspector~~ **done**, from reflection until 09's generator;
  - ~~place a prefab~~ **done (#222)**;
  - ~~the translate gizmo~~ **done (#221)**, with picking;
  - ~~undo/redo~~ **done (#217)**, the command log;
  - ~~play-in-editor~~ **done (#226)**;
  - ~~log and console panels~~ **done (#219)**, docked in the editor mode;
  - ~~log category `Editor`~~ **done**.
- **Later:**
  - ~~rotate~~ **done (#221)**, about Y; ~~scale gizmos~~ and rotation about X and Z **done (#367, §10p)**;
  - ~~record editor~~ **done (#224)**, the record browser;
  - ~~asset browser~~ **done (#366, §10o)**, with a material preview;
  - ~~I/O link view~~ **done (#225)**, the I/O panel and wire lines;
  - ~~terrain tools~~ **done (#372, §10u)**: sculpt, paint and water;
  - ~~prefab override UI~~ **done (#223)**, the inspector on the document;
  - ~~the rename/refactor command for asset paths (05 §3.2)~~ **done (#366, §10o)**, `AssetRename`;
  - ~~multi-document tabs~~ **done (#375, §10x)**;
  - ~~brushes and block-out geometry~~ **done (#61, §10y)**, as placements of brush prefabs;
  - ~~a behaviour-tree view and a conditions and actions editor~~ **done (#369, #370; §10r, §10s)**, an AI
    graph view over state machines, schedules, routines and (#387) behaviour trees, and a form over any vocabulary;
  - ~~prefab revert-all and nesting~~ **done (#372, §10u)**;
  - still later: CSG and vertex editing for brushes, mesh thumbnails, editing an `anim_graph` as a graph,
    and driving the panels' input in CI.

## 14. Build steps
1. Editor host + document model + command log + undo/redo (TODO F28, F30).
2. Outliner + generated inspector (with 09 step 2) (TODO F28).
3. Placement + translate gizmo + picking (TODO F29).
4. Play-in-editor.
5. Later panels and external tool bridges (TODO F31, F32). The `.map` bridge (F16) is built and did
   not need the editor — see below.
