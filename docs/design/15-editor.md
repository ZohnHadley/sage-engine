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
| `src/Sage.Editor/Screens/EntityContextMenuUI.cs` (tree over `World.QueryAll()`, per-frame reflection field dump, Delete via `World.Destroy`) | Outliner + inspector (generated metadata; Delete becomes a command) |
| `src/Sage.Editor/Camera/DevCamera.cs` | The editor camera rig (actions-based, 08) |
| `src/Sage.Editor/EditorManager.cs` (window size, camera holder) | Host window cvars (01) + editor state |

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
- **`classname` is a prefab id.** `watcher` in a map is `sandbox:watcher`, with `origin`, `angle`
  (Quake's degrees counter-clockwise from east, a quarter turn from the engine's yaw) and `targetname`.
  A classname with no prefab — `info_player_start`, `light` — is left for the game to read off the level,
  and logged, not refused.
- **A texture name is a material id**: `wall` in `sandbox:hut` looks for `sandbox:wall`, and a texture
  with no material draws plain rather than as the error checkerboard, because blocking a room out with
  untextured brushes is working rather than a mistake.
- **Texture coordinates need a texture size**, which is a client fact the simulation cannot see, so
  `MapSpace` assumes 64 — what nearly every Quake-era texture is, and what TrenchBroom itself assumes
  when it cannot find one. A 128-pixel image tiles twice as often as the editor showed; the fix, when it
  matters, is a size on the material record.
- **Unloading gives everything back**: the brushes' entities, their statics, their hull shapes (physics
  used to leak a shape per static — the list of them was write-only) and their GPU buffers. Terrain still
  leaks its chunk meshes on sector unload; a level that a mapper reloads every few seconds could not.

**Dogfood:** `games/Sandbox/content/maps/hut.map` — eight brushes, a doorway, stone/plank/thatch
textures, all generated by `games/Sandbox/tools/make_hut.py`, so the repository still ships no art it did
not make. `scene.json` names it in `"maps"`, and a record reload rebuilds it.

**Not built:** lightmaps; solid *entities* (doors, platforms — brushes with a classname, which want
F17's entity I/O and are skipped for now rather than welded into the world); per-entity keys beyond the
three above, which want 09's declared metadata so the FGD can offer keys the importer actually reads.

## 11. v1 scope vs later
- **v1 (minimal, for building the vertical slice):**
  - open/save a map document;
  - the outliner;
  - an editable inspector;
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
