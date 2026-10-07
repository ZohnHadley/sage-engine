# 18 · Editor

> Status: built through phase 10b. Phase 10a (#215) and phase 10b (#365) are complete and tested headlessly: a designer can place, move, turn on three axes, scale, tune and wire prefabs, select several, block out rooms from brushes, sculpt and paint terrain, pick assets from a browser, author conditions and AI graphs in forms, keep several documents open in tabs, play the level and save it; a modder can do the same beside a Shipping game, saving into their mod. Owning assemblies: `Sage.Editing`, `Sage.Editor`, `Sage.Host` (the `-edit` mode). Design doc: [15-editor.md](../../design/15-editor.md); user guide: [EDITOR.md](../../EDITOR.md).

## 1. Purpose and scope

The editor lets designers build levels without writing C# or JSON. It is a **mode of the game's own
executable**: `Sage.Host -game <folder> -edit` loads the game exactly as a run does, with the same content,
plugins and rules, and shows the level without playing it. What it saves is the file the game loads, so
there is no export step and no editor-only project database.

The model of the editor (documents, commands, undo, picking, forms, wiring, problems, play sessions) is
headless in `Sage.Editing` and is tested through `HeadlessApp`. The ImGui panels in `Sage.Editor` only draw
what the model decides. The dev tools (console window, entity outliner, entity inspector, stat overlay,
log panel) share the same host and are present in Debug and Development builds only.

Not in scope: writing game rules, authoring animation graphs or shaders, and a standalone editor
executable. Modders get the editor beside a Shipping game as the Development host packaged in an `editor/`
folder (`sage package --editor`, #375), not as a new executable.

## 2. Responsibilities

- Hold open **documents**, `placements` records in tabs of their own (#375), and edit them only through undoable commands.
- Save the document back into the file it came from, changing only what changed and keeping comments, key order and the other records in the file.
- Pick entities with rays, show gizmos, snap, and keep the selection on a placement across respawns.
- Offer the prefabs that can be placed, and place them on the surface under the cursor.
- Edit one field of one placement as an **override** with provenance, never by changing the prefab; nested fields and lists, and components the prefab does not name, too (#368).
- Block out rooms from brushes (#61) and sculpt, paint and flood terrain (#372).
- Browse assets with previews, pick them into fields and rename them (#366).
- Author conditions, actions and AI graphs as forms (#369, #370).
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
| `EditorSelection` | Selection by placement, several at once (#367), surviving respawns | `EditorSelection.cs` |
| `EditorRay`, `ViewportCamera`, `EditorPicking` | Ray from a pixel, and picking colliders (with a fallback sphere for entities with none) | `EditorRay.cs`, `ViewportCamera.cs`, `EditorPicking.cs` |
| `TranslateGizmo`, `RotateGizmo`, `ScaleGizmo`, `GizmoMath`, `Snap`, `GizmoDrag`, `ViewportTools` | Axis and plane move, three rotate rings, scale, world or local space, snapping, a drag of a group as one command (#367) | `*Gizmo.cs`, `ViewportTools.cs`, `GroupEdits.cs` |
| `PrefabPalette`, `Placing` | Offered prefabs by namespace, placement on a surface | `PrefabPalette.cs`, `Placing.cs` |
| `InspectorModel`, `InspectorGroup`, `InspectorRow`, `InspectorValue` | Form rows with range, unit, tooltip, override mark and provenance | `InspectorModel.cs`, `InspectorValue.cs` |
| `RecordDocument`, `RecordEditor` | The record browser's document and its own undo | `RecordDocument.cs`, `RecordEditor.cs` |
| `WiringModel`, `Wiring` | Outputs on offer, valid inputs, add and edit a wire | `Wiring.cs` |
| `PlaySession` | Play and Stop over a second world | `PlaySession.cs` |
| `ProblemList` | Load problems, mod conflicts and document problems, by file; the load problems and conflicts are `Sage.Core`'s `ContentProblems`, which the `problems` command and the dev overlay's badge read too (#301) | `ProblemList.cs` |
| `LogView`, `EntityLabelCache` | The log filtered by level and category, each line formatted once; outliner labels built once (#374) | `LogView.cs`, `EntityLabelCache.cs` |
| `AssetBrowser`, `AssetPicking`, `AssetReferences`, `AssetRename`, `AssetCommands` | The asset browser, picking into fields and the viewport, references and rename (#366) | `Asset*.cs` |
| `AIGraph`, `AIGraphCommands` | State machines, schedules and routines as trees, edited and highlighted live (#369) | `AIGraph*.cs` |
| `VocabularyCatalog`, `VocabularyForm`, `VocabularyEditor` | A form over any vocabulary: a wire's `requires`, a record's conditions and actions (#370) | `VocabularyForm.cs`, `VocabularyEditor.cs` |
| `TerrainDocument`, `TerrainCommands`, `PrefabCommands` | Terrain sculpt, paint and water with their own undo; revert all and nesting into a prefab (#372) | `Terrain*.cs`, `PrefabCommands.cs` |
| `BlockoutTools` | Brushes placed, resized, reshaped and painted (#61) | `Blockout.cs` |
| `PanelTour`, `EditorStatus` | `ed_panel`, which CI uses to draw every panel; the status bar's text (#371) | `PanelTour.cs`, `EditorStatus.cs` |
| `EditorWorkspace`, `WorkspaceCommands` | Document tabs, each in its own edit world, and the mod saves go into (#375) | `EditorWorkspace.cs`, `WorkspaceCommands.cs` |
| `DevTools` | Host-side owner of ImGui, the free camera, console, panels and layout | `src/Sage.Editor/DevTools.cs` |
| `JsonFileEdit` (in `Sage.Core`) | The comment-preserving JSONC splice used by every save | `src/Sage.Core/Content/JsonFileEdit.cs` |

**Console.** The document: `doc_open`, `doc_new`, `doc_level`, `doc_save`, `doc_close`, `doc_status`,
`ed_undo`, `ed_redo`, `ed_history`. Placing and selecting: `ed_palette`, `ed_place`, `ed_select`,
`ed_move`, `ed_rotate`, `ed_delete`, `ed_duplicate`, `ed_frame`, `ed_gizmo`, `ed_snap`, `ed_grid`,
`ed_angle`. Tuning: `ed_set`, `ed_revert`, `ed_inspect`. Wiring: `ed_wire`, `ed_unwire`, `ed_wires`.
Records: `ed_rec_open`, `ed_rec_get`, `ed_rec_set`, `ed_rec_undo`, `ed_rec_redo`, `ed_rec_save`,
`ed_rec_close`. Play and screen: `ed_play`, `ed_stop`, `ed_problems`, `ed_layout`, `ed_viewport`,
`cam_set`, `ent_select`. Phase 10b added selection and gizmo commands (`ed_select_add`, `ed_select_all`,
`ed_nudge`, `ed_turn`, `ed_scale`, `ed_space`, `ed_scalestep`), nested inspector edits (`ed_add`,
`ed_remove`, `ed_reorder`, `ed_add_component`, `ed_remove_component`), assets (`ed_assets`,
`ed_asset_refs`, `ed_asset_pick`, `ed_asset_place`, `ed_asset_rename`, `ed_rec_live`), AI graphs
(`ed_ai_*`), the conditions form (`ed_vocab*`), terrain (`ed_terrain`, `ed_sculpt`, `ed_paint`,
`ed_water`, `ed_terrain_save`, `ed_terrain_undo`, `ed_terrain_redo`, `ed_terrain_history`), prefabs
(`ed_revert_all`, `ed_make_prefab`), brushes (`ed_brush*`), `ed_panel`, and tabs (`ed_tabs`, `ed_tab`,
`ed_tab_open`, `ed_tab_new`, `ed_tab_close`, `ed_mod`). Dev tools: `toggleconsole`, `stat`, `clear`. The
full table is in [EDITOR.md](../../EDITOR.md) §9.

**Events.** `EditDocument.Changed` and `Respawned`, `CommandLog.Changed`, and the play session's
`Started` and `Stopped`. The model publishes no world events.

## 5. Data model

The editor invents no file format. It edits:

- the `placements` record (`PlacementsRecord`, a list of `Placement`: `prefab`, `at`, `yaw`, `pitch`, `roll`, `scale`, `name`, `id`, `relativeTo`, `overrides`, `outputs`) and the `scene` record that names it;
- a terrain's sculpt, paint and water, in a `.sterrain` file the `terrain` record's `sculpt` names (#372);
- a mod's patch of another's placements document, `<mod>/data/patches/placements_<ns>_<name>.json`, when the editor saves into a mod (#375);
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

Everything runs on the main thread. The model allocates on edit gestures, which is acceptable. The
outliner and log panel allocate nothing per frame once their text exists: labels and lines are formatted
once and drawn through a list clipper (#374, test: OutlinerAndLogPanelFramesAllocateNothingOnceTheirTextExists);
the console window still formats its lines. Command history is capped (default 500) and forgets the oldest entries
with their save point (test: TheCapForgetsTheOldestCommandsAndASavePointWithThem).

## 8. Errors and diagnostics

The Problems panel and `ed_problems` list record errors with file and line, mod conflicts from the content
report, and document problems: a placement whose prefab is gone, a wire to a target not in the document,
duplicate names or ids (test: AMissingPrefabAndADanglingWireAreListedWithThePlacements_AndFixingThemClears).
Bad `ed_set` values are refused with the field's range or the valid names. A `-edit` name that is neither a
scene nor a placements document logs an error and opens the start scene. In a Shipping build `-edit` is
ignored with a warning.

The ImGui panels have no unit tests, but CI draws every one of them in the real host: `ed_panel all` in
`tools/editor_smoke.sh` and the Sandbox's `-edit` smoke run, where a panel that throws fails the run (#371,
test: AllBringsEveryPanelForwardInTurnOpeningEachOnItsTurn). Their input handling is not driven.

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
| REQ-EDIT-11 | Designers shall author walls, rooms and floors from brushes with collision, saved as data. | Must | Done | #61, test: ARoomOfBrushesIsBuiltSavedAndReloadedWithItsCollidersAndMeshes |
| REQ-EDIT-12 | Designers shall pick models, textures and sounds from an asset browser with previews. | Must | Done | #366, test: ATexturePickedForAMaterialShowsInTheGameAtOnceAndIsUndoable |
| REQ-EDIT-13 | The editor shall support multi-select, box select, scale and full three-axis rotation. | Must | Done | #367, test: TenCratesTurnAndScaleTogetherAndUndoPutsEveryOneBack, test: ABoxSelectsWhatTheCameraDrawsInsideIt |
| REQ-EDIT-14 | The inspector shall edit nested objects and lists and override components the prefab does not name. | Must | Done | #368, test: AnNpcsInventoryListIsEditedRowByRowAndASaveKeepsIt, test: ALightIsAddedToOnePlacedCrateAndASaveKeepsIt |
| REQ-EDIT-15 | The editor shall offer a behaviour-tree view and a conditions and actions form. | Should | Done | #369, #370, #387 (state machines, schedules, routines and, since #387, `behaviour_tree` records), test: AStateIsAddedAndMovedAndBothAreUndone, test: ABehaviourTreeIsATreeOfNodesEditedWithUndoAndTheNodeACreatureRunsIsActive, test: AWiresRequiresIsAuthoredFromTheFormEachEditOneUndo |
| REQ-EDIT-16 | The editor shall sculpt and paint terrain and support prefab revert and nesting. | Should | Done | #372, test: ASculptedHillAPaintedPathAndALakeAreSavedAndTheStreamedWorldShowsThem, test: AStrokeOfManyDabsIsOneUndoStepAndRedoPutsItBack, test: MakePrefabNestsPlacementsIntoANewPrefabPlacedWhereTheyStood, test: RevertAllTakesEveryOverrideAwayAsOneUndoStep |
| REQ-EDIT-17 | The ImGui panels shall be covered by tests or a driven smoke run. | Should | Done | #371, `tools/editor_smoke.sh`, test: AllBringsEveryPanelForwardInTurnOpeningEachOnItsTurn |
| REQ-EDIT-18 | A play session shall have its own log, user folder and crash reporter. | Should | Done | #49, test: TwoAppsInOneProcess_LogToSeparateSinks_WithSeparateLevels, test: UserFolderAndCrashSections_ArePerApp |
| REQ-EDIT-19 | The console shall take a JSON argument with double quotes in it unchanged. | Could | Done | #373, test: SingleQuotes_TakeAJsonArgumentRaw |
| REQ-EDIT-20 | The outliner and log panel shall not allocate per frame in steady state. | Could | Done | #374, test: OutlinerAndLogPanelFramesAllocateNothingOnceTheirTextExists |
| REQ-EDIT-21 | Several documents shall be open at once, each with its own undo history. | Could | Done | #375, test: EachTabIsADocumentInAWorldOfItsOwnWithItsOwnUndo |
| REQ-EDIT-22 | Modders shall get the editor beside a Shipping game and save into their own mod, a game level as a patch. | Could | Done | #375, test: AModderSavesBothTabsIntoTheModAndTheGamesLevelAsAPatch, test: WithTheEditorAPackageHasADevelopmentHostInEditorAndLaunchersOnTheSameGame |

## 10. Open work

**Milestone 8, editor part 2 (epic #365): done, 2026-10-07.** #366 to #375, #61 and #49 each merged
(PRs #536 to #547); design 15 §10o to §10z is the as-built record. What is left, from those sections' "Not
built" notes:

- Brushes: CSG, per-face texture offset and rotation, vertex editing, brush entities, lightmaps for
  editor brushes, an export to `.map`.
- Assets: mesh thumbnails and a rendered material ball.
- Placements: pitch, roll and scale on far proxies, travel entries and a prefab's `children`; colliders
  scaled by a placement's scale.
- Terrain: water volumes drawn by the client.
- Tabs: record documents in tabs, a patch that removes a game record, a remembered `ed_mod` target.
- Panels: driving their input (clicks, drags) in CI.

Related: #362 4p-6 skeletal animation coverage and an editor preview (done: the **Animation** window over `AnimationPreview` and the `anim_preview` commands, [EDITOR](../../EDITOR.md) §3, test: AGraphRunsWithTheParamsAndStatesThePreviewSets, test: TheAnimPreviewCommandsDriveThePreview), #276 4m-2 entity I/O editor link view (done: `WiringModel.Links`, test: TheLinkViewFansAGroupWireOutToEveryMember_AndShowsWhatReachesAnEntity), #301 R1-9 in-game problems list (done: `problems` and the dev overlay's badge share `ContentProblems` with this panel, test: ProblemsListsTheSameEntriesAsValidate).

## 11. References

- [SRS](../SRS.md) §6; sheets [04 ECS and scenes](04-ecs-and-scenes.md), [05 Events and logic](05-events-and-logic.md), [06 Assets](06-assets-and-content.md), [19 Modding](19-modding.md), [20 Tooling](20-tooling-and-release.md).
- [design/15-editor.md](../../design/15-editor.md): §10d (write-back), §10e (command log), §10f (`-edit`), §10l (play), §10m (problems), §10o to §10z (phase 10b), §11 (scope).
- [EDITOR.md](../../EDITOR.md): §1 opening, §8 saving, §9 commands, §11 what is not built.
- [REDESIGN.md](../../REDESIGN.md) §4.6 and §5 (phases 10a, 10b).
