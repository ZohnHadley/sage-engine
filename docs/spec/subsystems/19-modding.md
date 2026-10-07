# 19 · Modding

> Status: built (phase 4j, then phase 9, 2026-10-07). Manifests, discovery, load order, record patching with keyed list merge, `$remove` and `replace`, conflict reports (per field, per key, per asset), saves that name their mods, a mods screen, `sage mods` (with `pack` and `build`), a mod template, `.sagemod` packages over a hardened zip mount, namespaced mod assets, mod shaders, trusted code mods and the editor's per-field conflict view. Not built: a Workshop service (an interface only), the `sage-mod-code` template, and the kit's mods screen showing the "contains code" flag. Owning assemblies: `Sage.Core` (`Mods/`, the merge and the report), `Sage.Simulation` (`ModManager`, save header), `Sage.UI` (`ModsView`), `Sage.Cli`. Design doc: [17-modding.md](../../design/17-modding.md); user guide: [MODDING.md](../../MODDING.md).

## 1. Purpose and scope

Modding lets people who did not make a game change it: add weapons, rebalance a trader, translate, re-skin.
A Sage mod is **data**: a folder with a `mod.json`, record files, string tables and assets. The game
mounts it after its own content, so its records patch the game's the way the game's patch the engine's.
No code and no build step are needed, and a mod never copies a record to change it.

Phase 9 added code mods (trusted assemblies in a collectible load context, flagged, never auto-downloaded), `.sagemod`
zips and shader support. A code mod that does not fit (no `[Plugin]` of its id, a missing dll) is refused with a reason
and the game still starts.

Not in scope: sandboxing code (.NET cannot do it, so code mods are marked and never auto-downloaded), and a workshop or download service (`Sage.Client.IWorkshop` is the interface; no service ships). The editor for
modders is [18](18-editor.md)'s (#375): `sage package --editor` ships it beside a Shipping game, and
`ed_mod <id>` saves into that mod only, a game's level as a placements patch.

## 2. Responsibilities

- Read `mod.json` strictly (an unknown key is an error) and validate ids and version ranges.
- Discover mods in the game's `mods/` folder and the player's `user://mods`.
- Order mods by dependencies, `loadAfter`, `loadBefore`, then the player's order, then id; refuse broken ones with a reason.
- Keep the player's choices in `user://mods.json` and apply them at the next start.
- Mount each active mod's folder as `mods/<id>` after the game, and merge records by patch.
- Record **merge provenance** for every field and build a conflict report on demand and at start.
- Write `user://logs/mod_report.txt` at every start.
- Name the active mods and versions in every save header, and warn on a mismatch at load.
- Hot reload a mod's data in a dev build, and say "restart to apply" for `mod.json`.
- Provide `sage mods` and `sage validate --mods` for checking a mod without playing, and the `sage-mod-data` template.

Not responsible for: the record store and VFS themselves ([06](06-assets-and-content.md)), how the save
reconciles missing content ([15](15-saves.md)), or the screen toolkit ([13](13-ui.md)).

## 3. Placement and dependencies

| Item | Value |
|---|---|
| Manifest, order, list | `src/Sage.Core/Mods/`: `ModManifest`, `ModLoadOrder` (`Resolve`, `ModLoadResult`, `RefusedMod`), `ModList` |
| Merge and report | `src/Sage.Core/Content/`: `RecordStore` (provenance, `mod_conflicts`, `rec_get`), `ContentReport`, `VirtualFileSystem` (`vfs_which`, `vfs_mounts`) |
| Discovery and choices | `src/Sage.Simulation/App/ModManager.cs`: finds, resolves, enables, disables, moves, watches manifests |
| Save header | `src/Sage.Simulation/Content/SaveSystem.cs` |
| Mods screen | `src/Sage.UI/ModsView.cs` (view-model `ui_mods`); the screen `rpg:mods` is in the RPG kit's content |
| CLI | `src/Sage.Cli`: `sage mods`, `sage validate --mods`, `sage schema --mods` |
| Template | `sdk/Sage.Templates/content/sage-mod-data`: `sage new mod-data --game <game folder>` writes its schemas and validates it against the game (#297); its `.vscode/` maps the schemas and has `sage validate`, `mods` and `schema` tasks |

The manifest and merge live in `Sage.Core`, below the simulation, so the CLI and the host share them.
The mod model is experimental (`SAGE0132`, MAKING_A_GAME §10b) and may change before 1.0. No plugin id
registers it: it is part of the boot path (`SageApp`), with the host options `-mods <dir>[,<dir>]` and
`-nomods`.

## 4. Interfaces

| Type or command | Role |
|---|---|
| `ModManifest.Load(folderOrSagemod)` | Reads and validates `mod.json` (a folder or a `.sagemod`: `IsPackage`); `AssemblyPaths` for a code mod; refuses a self-contradicting one |
| `ModLoadOrder.Resolve(found, userList, game, engineVersion, ...)` | Returns active, refused (with reasons), disabled and notes |
| `ModList` | The player's `{ order, disabled }`, loaded with a warning on a bad file |
| `ModManager` | `Found`, `Next()`, `Enable`, `Disable`, `Move`, `Summary`, `ListLines`, `RestartNeeded`, `WatchManifests`; for code mods `LoadCode`, `UnloadCode`, `CodeOf(id)`, `ContainsCode(mod)`, `ContainsCodeFlag` |
| `ModPackage.Pack`, `ShaderBuild.PackMod`, `ZipMount` | `sage mods pack` and `build`; the hardened read-only zip mount (`mods/<id>`) |
| `RecordConflicts.Find` | The editor's per-field conflict view (`FieldConflict`, `FieldContribution`) |
| `IWorkshop` (`Sage.Client`) | `Name`, `Available`, `Installed()`, `PublishAsync`: an interface only |
| `ContentReport.Build(records, vfs)` | Per-mount additions, patches, shadows, redefinitions, and `Conflicts`; a mod mount is one named `mods/<id>` |
| `ModsView` | Mods tab and conflicts tab, with toggle, earlier and later buttons |

**Console.** `mod_list`, `mod_order`, `mod_enable <id>`, `mod_disable <id>`, `mod_move <id> <n>`,
`mod_conflicts [mod]`, `rec_get <type> <id>`, `vfs_which <path>`, `vfs_mounts`, and in the editor `ed_rec_conflicts`. **CLI.** `sage mods <game>
[--mods dir ...]` prints order, refusals and the report and exits 1 on a refusal or a content error; a
conflict is a warning. `sage mods pack <mod> [--out f.sagemod] [--engine-content dir]` and `sage mods build <mod>` package a mod and compile its shaders. **Host.** `-mods`, `-nomods`.

## 5. Data model

`mod.json` fields: `id` (required, the mod's record namespace: lower case letters, digits, `_`, `.`, `-`),
`name`, `version` (SemVer, default `0.0.0`), `author`, `description`, `game`, `gameVersion`, `sage`,
`dependencies` (id to range), `loadAfter`, `loadBefore`, `incompatible`, and `kind` (`data` or `code`) with `assemblies` (a code mod's dlls, relative, inside the mod, `{config}` allowed). A mod id may not be `sage`, the game's id or a kit's
content namespace.

Record patches are `{ "type": ..., "id": ..., "patch": true, ... }`. Values replace, objects merge, lists
replace unless written `field+` (append) or `field-` (remove entries equal to those given), a `[ListKey]` list (`inventory.items`, a quest's `stages`, a dialogue's `nodes`) merges by key, `"$remove": true` removes a keyed entry, `"replace": true` replaces an object instead of merging, `"disabled":
true` removes the record, and `place+` on a scene adds placements. A bare id inside a mod's patch is the
mod's own. Strings merge key by key. A mod's own assets are `mod_id:path`; `<mod>/@<ns>/<path>` replaces a namespace's file; other paths replace by path.

`user://mods.json` is `{ "order": [...], "disabled": [...] }`. A save header carries `mods`, each with id and
version.

## 6. Lifecycle and data flow

At boot, before any content loads: discover, resolve order, mount, load records, build provenance. Mods are
never unmounted while the game runs, so enable, disable and reorder apply at the **next start** (the screen
and console say so). The later mod wins; a constraint beats the player's order. A broken mod is refused and
the rest load. A dev build watches mod files and hot reloads data and strings (test:
EditingAModsRecordHotReloadsIt); a `mod.json` change only reports that a restart is needed.

On save, the header lists the active mods. On load, a mismatch is a warning, never a failure: something a
missing mod's prefab spawned is kept as an inert placeholder and written back unchanged, so re-enabling the
mod restores it, and nothing is doubled (test: ASaveListsItsModsAndLeavesTheirMountsOutOfContent).

## 7. Threading, memory and performance

Discovery and merging run once at boot on the main thread. Record reload after a hot edit has a budget test
(`TheSandboxsRecordsReloadWithinBudget`, in the Measurements collection). The mods screen builds rows when it
opens and after its own buttons, so refresh allocates nothing once warm.

## 8. Errors and diagnostics

Refusal reasons are specific: missing, out-of-range or disabled dependency, a cycle, `incompatible`, wrong
game, game version or engine, a taken id, an unreadable `mod.json`, a reserved namespace, or code. Log
category `Mods` has a summary line then a warning per refusal. A redefinition (same id without `patch`) is
an error that names both files and applies as a patch. A hostile `.sagemod` is refused with the rule it broke (`<file>: refused: <rule>`) and the rest load; a code mod's refusals are listed in MODDING §8c. A patch of a record nobody defined is a skipped
warning. Conflicts are warnings: two mods setting one field (one keyed-list entry, reported per key), one mod's `disabled` against another's patch,
and two mods replacing one asset (two mods shipping the same bare path are not: each has its own). Two `+` on one list, and a mod changing the game or engine, are not
conflicts (the latter is an override). `mod_conflicts`, `mod_report.txt` and `sage mods` show the same data,
with file and line from `rec_get`.

## 9. Requirements

| ID | Requirement | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-MOD-01 | A mod shall be a folder with a strictly read `mod.json` naming its id, version and constraints. | Must | Done | test: AnUnknownKeyInAModJsonIsAnErrorAsInGameJson |
| REQ-MOD-02 | Mods shall be discovered in the game's folder and the player's, with the player's copy winning a duplicate id. | Must | Done | test: TheSameIdInBothFoldersIsAnErrorAndThePlayersCopyIsUsed |
| REQ-MOD-03 | Load order shall follow dependencies and constraints, then the player's order, then id, and broken mods shall be refused with a reason. | Must | Done | test: ADependencyLoadsBeforeItsDependentWhateverTheUsersOrderSays |
| REQ-MOD-04 | Records shall be patchable by a mod without copying them, with append and remove on lists. | Must | Done | test: TwoModsAddingToOneList_IsNotAConflict |
| REQ-MOD-05 | Conflicts shall be reported per field and per asset, and overrides of the game shall not count as conflicts. | Must | Done | test: AModPatchingTheGameIsAnOverride_NotAConflict |
| REQ-MOD-06 | Saves shall name their mods, warn on a mismatch and keep a missing mod's entities as placeholders. | Must | Done | test: ALoadWithoutAModWarnsNamingItAndKeepsTheModsEntityAsAPlaceholder |
| REQ-MOD-07 | Players shall switch and reorder mods in a screen and at the console, applying at next start. | Should | Done | test: TheConsoleWritesThePlayersListForTheNextStartAndChangesNothingNow |
| REQ-MOD-08 | A modder shall check a mod headlessly with `sage mods` and `sage validate --mods`. | Must | Done | test: ARefusedMod_IsReportedWithItsReason_AndTheGameStillValidates |
| REQ-MOD-09 | A mod template shall build a working mod. | Should | Done | test: TheTemplateMod_LoadsInAGame_AddsItsWeapon_AndPatchesThePlayer |
| REQ-MOD-10 | The exit game shall show two conflicting mods, a reversed order, a disabled mod and a save across them. | Must | Done | test: ModsExit_ModConflictsReportsTheName_AndNeitherFalchionTexture |
| REQ-MOD-11 | A mod shall be able to ship trusted code in a collectible load context, loaded before registries seal. | Must | Done (#396): modules under the mod's own plugin id, refused with a reason when they do not fit, unloaded at dispose, saves and upgraders survive | test: TheExampleCodeModAndADataModLoad_AndModConflictsReportsTheirConflict, ACompiledCodeModRunsItsInitUnderItsOwnPluginId_AfterTheGame, ACodeModWhoseModulesDoNotFitIsRefused_AndTheGameBoots, TheCodeModsContextUnloadsWhenTheAppIsDisposed, SavesSurviveRemovingTheCodeModAndPuttingItBack |
| REQ-MOD-12 | A mod shall be distributable as a hardened `.sagemod` zip (no zip-slip, size and count limits). | Must | Done (#397) | test: AnEntryWhosePathLeavesTheArchiveIsRefusedWithTheRule, AZipBombIsRefusedByItsRatio, PackWritesASagemodThatLoads_TheSameBytesEachTime_WithoutDotFiles, AGameAndItsModsLoadIdenticallyAsFoldersAndAsZips |
| REQ-MOD-13 | Two mods shall each be able to ship the same asset path without clashing. | Must | Done (#398): a mod's own asset is `mod_id:path`; `@ns/` replaces on purpose | test: TwoModsShippingOnePath_EachSeeTheirOwn_AndItIsNoConflict, AnAtNamespaceFolderReplacesThatNamespacesFileOnPurpose, ModsExit_EachModSeesItsOwnFalchionTexture_AndTheGamesLanternPictureIsReplacedOnPurpose |
| REQ-MOD-14 | Patches shall merge keyed lists, support `$remove` and `replace`, with conflicts per key. | Must | Done (#399) | test: APatchChangesOneEntryOfAKeyedListByKey_WithoutRestatingTheList, DollarRemoveTakesAnEntryOutByKey_AndAgainstAnotherModsEditOfItIsAConflict, ReplaceTrueReplacesAWholeObject_ItsKeyedListsIncluded_OrTheWholeRecord, TwoModsEditingDifferentKeysOfOneList_DoNotConflict_AndTheSameKeyIsAConflictAboutThatKey, AModChangesTheTradersPriceForOneStockItem_AndAnotherModsEditOfAnotherItemIsNoConflict |
| REQ-MOD-15 | Mods shall ship shaders, with compile on reload in dev builds and a precompile step. | Should | Done (#400), headless with a fake compiler; the on-screen reload of a mod's `.fx` needs Windows or Wine and was not run | test: ADevBuildCompilesAModsEffectsOnLoad_AndAgainWhenOneIsEdited, AFailedCompileKeepsTheCompiledFile_AndItsMessageNamesTheModsFile, PackingAModCompilesEveryEffectIntoThePackage, BuildingAModCompilesOnlyWhatIsOutOfDate_BesideItsSource |
| REQ-MOD-16 | Mod tooling shall check dependency versions, show per-field conflicts in the editor and allow a workshop adapter. | Could | Done (#401): the checks existed and a self-contradicting `mod.json` is now an error; the conflict view is built; the Workshop is an interface only | test: OpeningAConflictedRecord_ShowsEachModsValueForEachField_AndTheWinner, AModJsonThatContradictsItselfIsAnError |
| REQ-MOD-17 | A mod with code shall be flagged "contains code" wherever mods are listed, shall be documented as not sandboxed, and shall never be downloaded by the engine. | Must | Done (#396): `[contains code: not sandboxed]` in `mod_list`, the boot report and `sage mods`; the kit's mods screen does not show it yet | test: TheExampleCodeModAndADataModLoad_AndModConflictsReportsTheirConflict |
| REQ-MOD-18 | `sage validate` and `sage mods` shall run a code mod and a packed one as the game would. | Should | Done (#396, #397) | test: ValidateRunsTheCodeMod, ASagemodNamedOnTheCommandLineValidates, PackedDataAndCodeModsLoad_Conflict_AndSavesSurviveTogglingThePackedCodeMod |

## 10. Open work

**Milestone 11, code mods and packaging (epic #395): done (2026-10-07).** #396 9-1 code mods, #397 9-2 zip mounts and
`.sagemod`, #398 9-3 namespaced assets, #399 9-4 keyed list merge, #400 9-5 mod shaders, #401 9-6 distribution aids
(PRs #568 to #573). Left from them, with no issue yet: the `sage-mod-code` template, a Workshop implementation, the kit's
mods screen showing the code flag, hot reload of a file under `@ns/` (it reloads the literal path), `sage package`
writing zips, and a Windows run of the on-screen `.fx` reload.

Related: #375 10b-10 (done: the editor for modders, test: AModderSavesBothTabsIntoTheModAndTheGamesLevelAsAPatch), #277 4m-3 (per-sector asset scopes), #308 4n-4 (asset scopes).

## 11. References

- [SRS](../SRS.md) §7 (REQ-QUAL-08); sheets [06 Assets](06-assets-and-content.md), [15 Saves](15-saves.md), [13 UI](13-ui.md), [20 Tooling](20-tooling-and-release.md).
- [design/17-modding.md](../../design/17-modding.md) (the plan and the 4j build notes).
- [MODDING.md](../../MODDING.md): §2 `mod.json`, §4 load order, §5 patches, §6 conflicts, §8a to §8c packing, shaders and code, §9 not supported.
- [REDESIGN.md](../../REDESIGN.md) §5 (phases 4j and 9), MAKING_A_GAME §2 (the template).
