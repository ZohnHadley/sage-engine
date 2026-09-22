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
  - export a **TrenchBroom FGD** generated from component metadata;
  - later, import `.map` brush geometry as meshes + colliders (HL1-like games, F16);
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
| `engine/Classes/Screens/EditorUI.cs` (menu bar; stubs; `game.Exit()`) | The editor host's menu: New/Open/Save map documents; Exit |
| `engine/Classes/Screens/EntityContextMenuUI.cs` (tree, per-frame reflection property dump, Delete via context menu) | Outliner + inspector (generated metadata; Delete becomes a command) |
| `engine/Classes/EnginClasses/CameraClasses/DevCamera.cs` | The editor camera rig (actions-based, 08) |
| `engine/Classes/EditorManager.cs` (window size, camera holder) | Host window cvars (01) + editor state |

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
  - FGD export and `.map` import;
  - prefab override UI;
  - the rename/refactor command for asset paths (05 §3.2);
  - multi-document tabs.

## 14. Build steps
1. Editor host + document model + command log + undo/redo (TODO F28, F30).
2. Outliner + generated inspector (with 09 step 2) (TODO F28).
3. Placement + translate gizmo + picking (TODO F29).
4. Play-in-editor.
5. Later panels and external tool bridges (TODO F16, F31, F32).
