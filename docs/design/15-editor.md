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
  - viewport (the editor camera rig: today's `DevCamera` behaviour on `Move`/`Look` actions in the `Editor` context, 08);
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
| `MapFile` | `src/Sage.Engine/World/MapFile.cs` | The text: entities, keys, brushes, faces. Standard, **Valve 220** and Quake 2/3 dialects; `//` comments and faces split across lines; every error names the line |
| `BrushGeometry` | `src/Sage.Engine/World/BrushGeometry.cs` | Planes → convex polygons (clip, weld, triangulate), texture coordinates for both dialects, and `MapSpace`: 32 units to the metre, Z-up to Y-up, north to −Z |
| `MapRecord` / `MapLevel` / `MapLoader` | `src/Sage.Engine/World/MapLevel.cs` | The record that names a `.map`, the loaded level, and loading it: brushes built, hulls into physics (`PrePhysics`), entities spawned |
| `MapMeshSystem` | `src/Sage.Client/Rendering/MapMesh.cs` | **One mesh per texture** (not per brush), built in `FrameUpdate`, disposed when the level unloads |
| `FgdExport` | `src/Sage.Engine/World/FgdExport.cs` | `fgd_export` writes TrenchBroom an entity definition file from the prefab records |

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
a game.

| Piece | Where | What it does |
|---|---|---|
| `Placement` / `PlacementsRecord` | `src/Sage.Engine/Content/Placements.cs` | The document's content, and spawning it into a world, reading it back, clearing it |
| `EditorDocument` | `src/Sage.Engine/Content/EditorDocument.cs` | Open, save, close, dirty. **Engine-side on purpose**: it is records and files with no screen in it, which is what makes it testable |
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

## 11. v1 scope vs later
- **v1 (minimal, for building the vertical slice):**
  - ~~open/save a map document~~ **done (F28)**, as a placements document;
  - ~~the outliner~~ **done**;
  - ~~an editable inspector~~ **done**, from reflection until 09's generator;
  - place a prefab;
  - the translate gizmo;
  - undo/redo;
  - play-in-editor;
  - log and console panels;
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
