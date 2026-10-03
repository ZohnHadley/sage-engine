# 18 · Editor

> Status: partly built. Phase 10a is complete and tested headlessly: a designer can place, move, turn, tune and wire prefabs, play the level and save it. The editor is a placement tool, not yet a level-authoring tool: brushes, an asset browser, multi-select and scaling are planned (phase 10b). Owning assemblies: `Sage.Editing`, `Sage.Editor`, `Sage.Host` (the `-edit` mode). Design doc: [15-editor.md](../../design/15-editor.md); user guide: [EDITOR.md](../../EDITOR.md).

## 1. Purpose and scope

The editor lets designers build levels without writing C# or JSON. It is a **mode of the game's own
executable**: `Sage.Host -game <folder> -edit` loads the game exactly as a run does, with the same content,
plugins and rules, and shows the level without playing it. What it saves is the file the game loads, so
there is no export step and no editor-only project database.

The model of the editor (documents, commands, undo, picking, forms, wiring, problems, play sessions) is
headless in `Sage.Editing` and is tested through `HeadlessApp`. The ImGui panels in `Sage.Editor` only draw
what the model decides. The dev tools (console window, entity outliner, entity inspector, stat overlay,
log panel) share the same host and are present in Debug and Development builds only.

Not in scope: shipping the editor to players of a Shipping game (planned, #375), writing game rules,
authoring animation graphs or shaders, and a standalone editor executable.

## 2. Responsibilities

- Hold one open **document**, a `placements` record, and edit it only through undoable commands.
- Save the document back into the file it came from, changing only what changed and keeping comments, key order and the other records in the file.
- Pick entities with rays, show gizmos, snap, and keep the selection on a placement across respawns.
- Offer the prefabs that can be placed, and place them on the surface under the cursor.
- Edit one field of one placement as an **override** with provenance, never by changing the prefab.
- Browse and edit any record as a form over its JSON, saving into the owning file or as a patch.
- Wire entity outputs to inputs, offering only inputs the target takes.
- Play the level in a second real world and throw it away on Stop.
- List problems from content loading, mod conflicts and the open document.
- Expose every action as a console command, so scripts and tests press what a person presses.

Not responsible for: record loading and merging ([06](06-assets-and-content.md)), the entity I/O model
([05](05-events-and-logic.md)), mod patch semantics ([19](19-modding.md)), or drawing the level
([07](07-rendering.md)).

## 3. Placement and dependencies

| Assembly | Layer | May reference |
|---|---|---|
| `Sage.Editing` | Base, headless, one namespace `Sage.Editing` (`SageBaseAssembly`) | `Sage.Simulation` and below. Never MonoGame, never a kit. Every type is `[Experimental("SAGE0133")]` while phase 10a settles. |
| `Sage.Editor` | MonoGame and ImGui host side | `Sage.Client`, `Sage.Editing`, the third-party ImGui binding |
| `Sage.Host` | The executable | Parses `-edit`, makes the edit world, owns `Game1` |

`Sage.Editing` is not a plugin: it has no `[Plugin]` and registers nothing at module Init. The host's
`DevTools` constructs an `EditDocument` and registers the `ed_*`, `doc_*` and problem commands against the
console. A Shipping build references neither `Sage.Editor` nor ImGui (guarded by `SAGE_DEV`).

## 4. Interfaces

| Type | Role | File |
|---|---|---|
| `IEditorCommand` | One edit: `Description`, `Do`, `Undo`, `TryMerge` (a drag is one command) | `src/Sage.Editing/IEditorCommand.cs` |
| `CommandLog` | Undo and redo, capacity (500 by default), the saved position that decides Dirty | `CommandLog.cs` |
| `EditDocument` | The open placements document: open, new, new level, save, close, execute, find a placement, spawn and respawn entities | `EditDocument.cs` |
| `AddPlacement`, `RemovePlacement`, `SetPlacement`, `SetOverride`, `ClearOverride`, `SetOutputs`, `SetRecordValue` | The concrete commands (add, remove, move, override, revert, wire, set a record field) | `PlacementCommands.cs`, `RecordDocument.cs` |
| `EditorCommands`, `InspectorCommands`, `PaletteCommands`, `PlayCommands`, `ProblemCommands` | Registration of the console commands over the document | `*Commands.cs`, `ProblemList.cs` |
| `EditTarget` | What `-edit [scene-or-placements]` opens | `EditTarget.cs` |
| `EditorSelection` | Selection by placement, surviving respawns | `EditorSelection.cs` |
| `EditorRay`, `ViewportCamera`, `EditorPicking` | Ray from a pixel, and picking colliders (with a fallback sphere for entities with none) | `EditorRay.cs`, `ViewportCamera.cs`, `EditorPicking.cs` |
| `TranslateGizmo`, `RotateGizmo`, `GizmoMath`, `Snap`, `GizmoDrag`, `ViewportTools` | Axis and plane move, yaw ring, snapping, drag to one command | `*Gizmo.cs`, `ViewportTools.cs` |
| `PrefabPalette`, `Placing` | Offered prefabs by namespace, placement on a surface | `PrefabPalette.cs`, `Placing.cs` |
| `InspectorModel`, `InspectorGroup`, `InspectorRow`, `InspectorValue` | Form rows with range, unit, tooltip, override mark and provenance | `InspectorModel.cs`, `InspectorValue.cs` |
| `RecordDocument`, `RecordEditor` | The record browser's document and its own undo | `RecordDocument.cs`, `RecordEditor.cs` |
| `WiringModel`, `Wiring` | Outputs on offer, valid inputs, add and edit a wire | `Wiring.cs` |
| `PlaySession` | Play and Stop over a second world | `PlaySession.cs` |
| `ProblemList` | Load problems, mod conflicts and document problems, by file | `ProblemList.cs` |
| `LogView` | The log filtered by level and category | `LogView.cs` |
| `DevTools` | Host-side owner of ImGui, the free camera, console, panels and layout | `src/Sage.Editor/DevTools.cs` |
| `JsonFileEdit` (in `Sage.Core`) | The comment-preserving JSONC splice used by every save | `src/Sage.Core/Content/JsonFileEdit.cs` |

**Console.** The document: `doc_open`, `doc_new`, `doc_level`, `doc_save`, `doc_close`, `doc_status`,
`ed_undo`, `ed_redo`, `ed_history`. Placing and selecting: `ed_palette`, `ed_place`, `ed_select`,
`ed_move`, `ed_rotate`, `ed_delete`, `ed_duplicate`, `ed_frame`, `ed_gizmo`, `ed_snap`, `ed_grid`,
`ed_angle`. Tuning: `ed_set`, `ed_revert`, `ed_inspect`. Wiring: `ed_wire`, `ed_unwire`, `ed_wires`.
Records: `ed_rec_open`, `ed_rec_get`, `ed_rec_set`, `ed_rec_undo`, `ed_rec_redo`, `ed_rec_save`,
`ed_rec_close`. Play and screen: `ed_play`, `ed_stop`, `ed_problems`, `ed_layout`, `ed_viewport`,
`cam_set`, `ent_select`. Dev tools: `toggleconsole`, `stat`, `clear`. The full table is in
[EDITOR.md](../../EDITOR.md) §9.

**Events.** `EditDocument.Changed` and `Respawned`, `CommandLog.Changed`, and the play session's
`Started` and `Stopped`. The model publishes no world events.

## 5. Data model

The editor invents no file format. It edits:

- the `placements` record (`PlacementsRecord`, a list of `Placement`: `prefab`, `at`, `yaw`, `name`, `id`, `relativeTo`, `overrides`, `outputs`) and the `scene` record that names it;
- any other record, as a form over its JSON, saved in place or as a patch under the game's `data/patches/`;
- the free camera and viewport, which are camera entities in the edit world.

Documents carry placement identity across respawns (every edit respawns the placement it touched), so
selection and overrides stay attached. A new level writes a `scene` and a `placements` file into the game's
`data/` folder on the first save. The editor's panel layout is ImGui's own ini, reset by `ed_layout`.

## 6. Lifecycle and data flow

`-edit` makes an **edit world** with `CreateEditWorld`: placed with the scene, physics mirrored for picking,
no player, no Fixed system runs (test: AnEditWorldRunsNoFixedSystemButItsFrameSystemsAndTransformsRun). The
free camera is bound in the `Editor` input context only.

An edit flows: gesture or console, then an `IEditorCommand`, then `CommandLog.Execute`, then the document
respawns the touched placement, then `Changed` refreshes the panels. Save splices the changed values into
the file text with `JsonFileEdit` and marks the log's saved position. Play builds a second world from the
document in memory (unsaved work plays), moves the player below the free camera, and hands the host the
screen and input. Stop destroys that world; the document and its history are untouched.

## 7. Threading, memory and performance

Everything runs on the main thread. The model allocates on edit gestures, which is acceptable; the
per-frame panels are not yet allocation-free: the outliner builds a label per entity per frame and the log
panel reformats each line (#374). Command history is capped (default 500) and forgets the oldest entries
with their save point (test: TheCapForgetsTheOldestCommandsAndASavePointWithThem).

## 8. Errors and diagnostics

The Problems panel and `ed_problems` list record errors with file and line, mod conflicts from the content
report, and document problems: a placement whose prefab is gone, a wire to a target not in the document,
duplicate names or ids (test: AMissingPrefabAndADanglingWireAreListedWithThePlacements_AndFixingThemClears).
Bad `ed_set` values are refused with the field's range or the valid names. A `-edit` name that is neither a
scene nor a placements document logs an error and opens the start scene. In a Shipping build `-edit` is
ignored with a warning.

The ImGui panels have no unit tests: only the smoke run touches them (#371).

## 9. Requirements

| ID | Requirement | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-EDIT-01 | Every document change shall be an undoable command, with merged drag gestures and a dirty flag that follows the save point. | Must | Done | test: UndoAndRedoWalkTheLogAndANewCommandDropsTheRedoStack |
| REQ-EDIT-02 | Saving shall change only what changed and keep comments, order and neighbouring records. | Must | Done | test: SavingChangesOnlyWhatChangedInTheFile |
| REQ-EDIT-03 | The editor shall run as a mode of the game executable on the game's own content and rules. | Must | Done | test: TheEditArgumentNamesASceneOrADocument |
| REQ-EDIT-04 | A designer shall place prefabs from a palette, with unique names, on the surface under the cursor. | Must | Done | test: PlacingOnAColliderLandsOnItsTopAndOnEmptyGroundAtYZero |
| REQ-EDIT-05 | The editor shall move and yaw-turn a selection with snapping gizmos. | Must | Done | test: ADragOnAPlaneMovesBothAxesAndGridSnapsTheResult |
| REQ-EDIT-06 | Field changes shall be per-placement overrides that can be reverted and show where each value came from. | Must | Done | test: AnOverrideIsOneFieldOfOneComponentOrPartAndRevertsToThePrefab |
| REQ-EDIT-07 | Wiring shall offer only the outputs and inputs that exist, with undo. | Must | Done | test: AnInputTheTargetCannotTakeIsRefusedAndTheReasonListsWhatItTakes |
| REQ-EDIT-08 | A level shall be playable in the editor in a second world that Stop discards. | Must | Done | test: PlayThenStopLeavesTheDocumentAndTheEditWorldAsTheyWere |
| REQ-EDIT-09 | A designer shall build a level and the game shall load what was saved, with no JSON typed. | Must | Done | test: ADesignerBuildsALevelFromPrefabsInTheEditorAndTheGameLoadsWhatTheySaved |
| REQ-EDIT-10 | The editor shall list problems with file and line and open the offending record or placement. | Should | Done | test: ARecordFileWithAnErrorShowsWithItsFileAndLine_AndARowOpensTheRecord |
| REQ-EDIT-11 | Designers shall author walls, rooms and floors from brushes with collision, saved as data. | Must | Not started | #61 |
| REQ-EDIT-12 | Designers shall pick models, textures and sounds from an asset browser with previews. | Must | Not started | #366 |
| REQ-EDIT-13 | The editor shall support multi-select, box select, scale and full three-axis rotation. | Must | Not started | #367 |
| REQ-EDIT-14 | The inspector shall edit nested objects and lists and override components the prefab does not name. | Must | Not started | #368 |
| REQ-EDIT-15 | The editor shall offer a behaviour-tree view and a conditions and actions form. | Should | Not started | #369, #370 |
| REQ-EDIT-16 | The editor shall sculpt and paint terrain and support prefab revert and nesting. | Should | Not started | #372 |
| REQ-EDIT-17 | The ImGui panels shall be covered by tests or a driven smoke run. | Should | Not started | #371 |
| REQ-EDIT-18 | A play session shall have its own log, user folder and crash reporter. | Should | Not started | #49 |

## 10. Open work

**Milestone 8, editor part 2 (epic #365)**

- #366 10b-1 Asset browser and material preview (P1)
- #367 10b-2 Multi-select, box select, scale and full 3-axis rotation (P1)
- #368 10b-3 Inspector: nested objects, lists, overrides of unnamed components (P1)
- #369 10b-4 Behaviour-tree and AI graph view (P2)
- #370 10b-5 Conditions and actions editor, and a `requires` form (P2)
- #371 10b-6 Test the ImGui editor panels (P2)
- #372 10b-7 Terrain tools and prefab revert and nesting (P2)
- #373 10b-8 Quoted JSON for `ed_set` and the console (P3)
- #374 10b-9 Per-frame allocations in the dev tools (P3)
- #375 10b-10 Several documents open at once, and a standalone editor for modders (P3)

**Existing issues kept**

- #61 Brushes and block-out in the editor (P1)
- #49 Per-app log, user folder and crash reporter (P2)

Related: #362 4p-6 skeletal animation coverage and an editor preview (P2), #276 4m-2 entity I/O editor link view (done: `WiringModel.Links`, test: TheLinkViewFansAGroupWireOutToEveryMember_AndShowsWhatReachesAnEntity), #301 R1-9 in-game problems list.

## 11. References

- [SRS](../SRS.md) §6; sheets [04 ECS and scenes](04-ecs-and-scenes.md), [05 Events and logic](05-events-and-logic.md), [06 Assets](06-assets-and-content.md), [19 Modding](19-modding.md), [20 Tooling](20-tooling-and-release.md).
- [design/15-editor.md](../../design/15-editor.md): §10d (write-back), §10e (command log), §10f (`-edit`), §10l (play), §10m (problems), §11 (scope).
- [EDITOR.md](../../EDITOR.md): §1 opening, §8 saving, §9 commands, §11 what is not built.
- [REDESIGN.md](../../REDESIGN.md) §4.6 and §5 (phases 10a, 10b).
