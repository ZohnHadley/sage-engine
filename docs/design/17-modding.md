# 17 — Modding (short)

## 1. Purpose and scope
How mods are packaged, ordered, loaded and reported. Mods can:
- **shadow assets** by path;
- **add and patch records per field**;
- add maps and shaders;
- (trusted) add **C# assemblies**.

Decision D3: **data mods + trusted C# mods**; no sandboxed scripting.

## 2. Research basis
- Quake 3 `fs_game` + pk3 and Source SearchPaths: layered mounts (survey §1.1, §1.3).
- Bethesda plugins and the whole-record "rule of one" → conflicts, LOOT, xEdit, bashed patches (survey §1.5).
- Warband mods replace the whole module and can't be combined (survey §1.6).
- Skyrim Papyrus script state baked into saves (survey §1.5).
- Modern .NET has no sandbox for loaded assemblies. Quake 3 used a VM for downloadable code for this reason (survey §1.1).

## 3. Key decisions
- **A mod** is a folder or `.pak` under `mods/<id>/` with a `mod.json`:
  ```json
  { "id": "better_goblins", "name": "Better Goblins", "version": "1.2.0", "author": "…",
    "game": "sandbox", "gameVersion": ">=0.3",
    "dependencies": [ { "id": "core_patch", "version": ">=1.0" } ],
    "loadAfter": ["hd_textures"], "assemblies": [] }
  ```
  The mod `id` is its **record namespace** (05 §3.5).
- **Load order:** `user://mods.json` lists enabled mods in order. At boot, the order is validated and auto-sorted by `dependencies` and `loadAfter`/`loadBefore` (topological sort; the user's order breaks ties). Missing dependencies disable the mod with an error. Mods are mounted after the game (05 §3.1).
- **Additive by design:** mods patch records per field (`"patch": true`, list `+`/`-` ops) instead of replacing whole records or modules. Two mods that touch different fields of the same item both work.
- **C# mod assemblies (trusted):**
  - loaded into an `AssemblyLoadContext`;
  - they implement `IModule` (01) and register systems, records, loaders and cvars like any module;
  - the mod list UI marks them "contains code";
  - they are **never auto-downloaded** (the multiplayer-later rule, 05 §12);
  - no sandbox is promised.
- **Conflict report:** at boot, the `Mods` log category plus `user://logs/mod_report.txt` lists, per mod:
  - which asset paths it shadows, and from whom;
  - which records it patches, field by field;
  - non-patch redefinitions (errors);
  - missing references.

  Also available through the `mod_conflicts` command.
- **Saves** record the mod list + content hashes (09 §3.6). Removing a mod warns but still loads: unknown components are skipped, and missing records become placeholders. There's no orphaned script state, because behaviour is never saved.

## 4. API sketch
```csharp
public sealed class ModManifest { public string Id, Name, Version; public string Game; public VersionRange GameVersion; public List<ModDependency> Dependencies; public List<string> LoadAfter, LoadBefore, Assemblies; }
public sealed class ModManager                     // Engine service, used during boot (01 step 4)
{
    public IReadOnlyList<ModManifest> Available { get; }
    public IReadOnlyList<ModManifest> LoadOrder { get; }   // validated and sorted
    public ModReport Report { get; }                        // shadows, patches, errors
}
```

## 11. v1 scope vs later
- **v1:**
  - the `mods/` folder;
  - `mod.json`;
  - `user://mods.json` load order with a topological sort;
  - VFS mounting;
  - record patches;
  - the conflict report + `mod_conflicts`;
  - the mod list in the save header;
  - log category `Mods`.
- **Later:** C# mod assemblies, an in-game mod manager UI, `.pak` packaging tool, Steam Workshop, per-field conflict UI in the editor (15).

## 14. Build steps
1. `ModManager`: discovery, manifests, load order, mounting (with 05; F37; built in 4j-1 and 4j-3, "As built" below). *The pieces it builds on exist since migration step 5: VFS mounts with shadowing, per-mount record namespaces, the per-field patch merge and `rec_get`'s per-field origin (05 §3.6). `game.json` already has `modsDirectory`.*
2. Conflict report (depends on the record merge reporting, 05).
3. Save header mod list (with 09).
4. C# mod assemblies (later).

### As built (merge provenance and the conflict report, 2026-10-01 — issue 4j-2, F37)

Build step 2. Code: `src/Sage.Core/Content/RecordStore.cs` (the merge, `Writes`, `Describe`, the
`mod_conflicts` command), `ContentReport.cs`, `VirtualFileSystem.Shadows`; tests in
`tests/Sage.Tests/Content/ContentReportTests.cs`. Experimental API, SAGE0132 (MAKING_A_GAME §10b).

- **Every write is kept.** The merge records each write to a record as a path, the file (and so the
  mount and line) and an op: `define` for each top-level field of a definition, and for a patch `set`,
  `add` (`field+`), `remove` (`field-`) or `disable`, at the deepest path the patch names —
  `"stats": { "agility": 4 }` is a set of `stats.agility`. `RecordStore.Writes(type, id)` gives them in
  the order they apply, a base's first with `Via` naming the base, and a disabled record keeps its own.
  This replaced the old "last file per top-level field" table, which is now worked out from the writes,
  so `rec_get` and errors' file:line read the same data; the records themselves are untouched.
- **`rec_get` shows every writer** of a field that more than one file wrote, each with its file:line,
  under the field's `<-` line (test: TwoModsSetTheSameField_TheLaterWins_AndItIsAConflict), and says
  "(via base …)" for an inherited one (test: AnInheritedWriteSaysViaBase). A disabled record says who
  disabled it.
- **Which mounts are mods:** a mount named `mods/<id>` (`ContentReport.ModMountPrefix`,
  `IsModMount`). Phase 4j's boot (4j-3) mounts each mod that way; the engine, a kit, the game and a
  `--mounts` folder are not mods.
- **A conflict** (decision 5) is a `set` by one mod overlapping another mod's write of the same path
  (a path or anything inside it), reported once at the shorter path:
  `test_item sandbox:sword name: better_blades, rival_trade; rival_trade won`. A nested path is
  reported at its depth (test: ANestedPathIsReportedAtTheDepthThePatchNamed). Two mods' `+`/`-` on one
  list never conflict (test: TwoModsAddingToOneList_IsNotAConflict). One mod's `disabled` against
  another's patch is one (test: AModsDisableAgainstAnothersPatch_IsAConflict). A mod writing over the
  game, engine or a kit is an override, listed and never a conflict (test:
  AModPatchingTheGameIsAnOverride_NotAConflict). Two mods shipping one asset path is one; a mod
  shadowing the game's asset is listed. Record files, string tables and a root `mod.json`/`game.json`
  are merged or read per mount, so they are never "shadowed" (test:
  TwoModsShippingOneTexture_IsAConflict_AModShadowingTheGameIsNot).
- **`ContentReport.Build(records, vfs)`** groups by mount: records added, records patched with each
  write (marked "overrides game" where a mod patches what is not a mod's), redefinitions (still load
  errors, applied as patches), skipped patches of records never defined, and the assets it shadows and
  in whom (test: TheReportListsAdditionsRedefinitionsAndSkippedPatches_ByMount). Conflicts are
  warnings. **`mod_conflicts [mount]`** prints it, or one mount's part (a mount's name or a mod's id);
  it is registered with the record commands, so it answers with no mods — a game patching the engine
  (test: ModConflicts_RunsWithNoMods).
- **Built when asked**, from the last load's writes and the VFS as it now is, so it is never stale
  after a reload and costs the load nothing. Keeping the writes costs the load next to nothing: on the
  Sandbox (176 records) a reload takes and allocates what it did before (about 6 MB in a Debug build),
  because the writes replaced two per-record dictionaries, and the report takes tens of milliseconds,
  most of it walking every mount's files (test: TheSandboxsRecordsReloadWithinBudget).
- **Not done here:** missing references stay load errors at their line rather than report lines;
  string-table keys two mods both set are not reported; the boot summary and
  `user://logs/mod_report.txt` are 4j-3's, and `sage mods` is 4j-5's.

## As built (mod manifests and the load order, issue 4j-1)

- **Code:** `src/Sage.Core/Mods/` (`ModManifest.cs`, `ModLoadOrder.cs`, `ModList.cs`), `GameManifest.Version`, and an
  empty `Engine.Mods` slot (a `ModLoadResult`) that 4j-3 fills at boot. All of it is experimental, SAGE0132
  (MAKING_A_GAME §10b). Nothing mounted a mod yet; 4j-3 does (below).
- **`mod.json`** is read as strictly as `game.json`: an unknown key is an error (test: AnUnknownKeyInAModJsonIsAnErrorAsInGameJson).
  Fields: `id`, `name`, `version`, `author`, `description`, `game`, `gameVersion`, `sage`, `dependencies` (`{ "id": "range" }`),
  `loadAfter`, `loadBefore`, `incompatible` (test: AManifestReadsEveryField). The id follows the namespace rules, and a
  bad id, version or range is an error naming the file (test: ABrokenManifestIsAnErrorThatSaysWhat). `assemblies` and
  `"kind": "code"` are known only so a code mod is refused by name.
- **`game.json` gains an optional `version`**, which `gameVersion` is checked against; a malformed one is an error
  (test: GameJsonTakesAVersionAndRejectsAMalformedOne). Without it the `gameVersion` check is skipped and
  `ModLoadResult.Notes` says so (test: WithoutAGameVersionTheCheckIsSkippedWithANote).
- **`ModLoadOrder.Resolve(found, userList, game, engine, reservedIds)`** returns the active mods in order, the refused with
  a reason each, the disabled, and notes. It never throws for a mod's fault. The sort is a stable topological sort over
  dependencies, `loadAfter` and `loadBefore` (a name that is not installed is ignored by the last two); among the mods free
  to go next the user's order decides, then the id (tests: WithNoConstraintsTheOrderIsTheUsersThenTheId,
  ADependencyLoadsBeforeItsDependentWhateverTheUsersOrderSays, LoadAfterAndLoadBeforeOrderModsAndIgnoreOnesThatAreNotThere,
  TheSortIsStableAndFreeModsKeepTheUsersOrderAroundAConstraint, AModTheListDoesNotKnowIsOnAndGoesAtTheEnd,
  ADisabledModIsListedAndNotActive).
- **Refusals**, each with its reason: a missing dependency, one outside its range, or one the user switched off, and then
  whatever depended on the refused mod (tests: AMissingDependencyRefusesTheModAndItsDependents,
  ADependencyOutsideItsRangeIsRefused, ADependencyTheUserSwitchedOffSaysSo); a cycle, naming its members, while mods that
  merely wait on a cycle through `loadAfter` still load (test: ACycleRefusesItsMembersAndNamesThemButNotInnocentBystanders);
  incompatibility with an earlier mod, either side naming the other, so the later one is out (test:
  AModIncompatibleWithAnEarlierOneIsRefusedAndTheEarlierOneStays); the wrong `game`, `gameVersion` or `sage` (test:
  TheWrongGameGameVersionOrEngineRefusesAMod); code (test: AModThatAsksForCodeIsRefused); an id that is `sage`, the game's or
  a kit's content namespace (test: AModMayNotTakeTheEnginesTheGamesOrAKitsNamespace); and a second mod with an id already
  found (test: TheSameIdTwiceRefusesTheSecond). The caller passes the kits' content namespaces as `reservedIds`.
- **`ModList`** is `user://mods.json`, `{ "order": [...], "disabled": [...] }`, saved through a temp file and a move (test:
  ModListRoundTripsAndAMissingFileIsTheDefault). A file that cannot be read is the default list and a warning, never a
  crash (test: ABadModsJsonFallsBackToTheDefaultWithAWarning).

## As built (mods at boot, issue 4j-3)

- **Code:** `src/Sage.Simulation/App/ModManager.cs`, `SageApp.MountMods`, `SageAppOptions.Mods` / `UserModsDirectory` /
  `ModListFile` / `ModReportFile`, `Engine.ModManager`, the host's `-mods` / `-nomods` (`Sage.Host/Program.cs`), and
  `HeadlessApp.WithMods` / `WithUserMods` / `WithModReport`. Experimental, SAGE0132.
- **Found in two folders** (plan decision 2): `<game>/<modsDirectory>/*/mod.json` and `user://mods/*/mod.json`, ordered by
  `ModLoadOrder` and the player's `user://mods.json`, and each active mod's folder is mounted after the game's as
  `mods/<id>` in the record namespace `<id>` (test: ModsAreFoundInTheGamesFolderAndThePlayersAndMountedAfterTheGame). A
  folder there without a `mod.json` is skipped with a warning. The same id in both folders is an error in the `Mods` log;
  the player's copy is used and the game's is refused (test: TheSameIdInBothFoldersIsAnErrorAndThePlayersCopyIsUsed).
- **The player's list** orders the mods and switches them off; the last mod wins a field (test:
  ThePlayersListOrdersTheModsAndSwitchesThemOff).
- **A refused mod is not mounted and the game still boots**: an unreadable `mod.json` (refused by its folder's name), code,
  another game, a missing dependency, or a kit's content namespace (the loaded plugins' `[PluginContent]` namespaces are
  passed to `Resolve` as reserved). The `Mods` log gets one summary line, `Mods: 1 active (good 1.0.0), 5 refused, 0
  switched off`, then a warning per refusal with its reason and a line per note (test:
  ARefusedModIsNotMountedAndTheGameStillBoots).
- **Named mods replace discovery**: `-mods <dir>[,<dir>]` (`SageAppOptions.Mods`) loads exactly those folders in the order
  given, without the player's list; `-nomods` (an empty list) loads none (test: NamedModsReplaceDiscoveryAndNoneMeansNone).
  `sage validate` and `sage schema` take `--mods` and `--game-mods` (4j-5, below); `--mounts` stays for a bare patch folder.
- **Console:** `mod_list` (active in load order, switched off, refused with the reason), `mod_order` (this run's order, the
  player's order and the next start's), and `mod_enable <id>`, `mod_disable <id>`, `mod_move <id> <n>`, which write
  `user://mods.json` and say "applies at next start": nothing is remounted now, since the VFS has no unmount (decision 3).
  A mod the list did not name joins it where it loads now, so a change does not reorder the rest; an unknown id is an
  error naming the mods there are (test: TheConsoleWritesThePlayersListForTheNextStartAndChangesNothingNow).
  `ModManager.Enable` / `Disable` / `Move` / `Next` are the same for a mods screen (4j-6).
- **`user://logs/mod_report.txt`** is written once records have loaded: the summary, the load order with folders, the
  switched off, the refused and the notes, then 4j-2's `ContentReport` (what `mod_conflicts` prints): conflicts between
  mods, and per mount the records added and patched, a mod's override of the game marked (test: TheBootWritesAModReport).
- **Hot reload:** a mod is a folder mount like the game's, so `RecordHotReload` and the client's asset watchers cover its
  `data/`, `strings/` and assets (test: EditingAModsRecordHotReloadsIt). In a dev build, a changed or new `mod.json`
  warns "restart to apply" once per file (`ModManager.WatchManifests`, started by the host beside `RecordHotReload`;
  test: AChangedModJsonSaysRestartToApply).
- **Saves** list `Engine.Mods.Active` (4j-4), which the boot now fills; their mounts are the `mods/<id>` the save header
  leaves out of `content`.

## As built (`sage` with mods, the schema and the template, issue 4j-5)

- **Code:** `src/Sage.Cli/Program.cs` (`validate`, `schema`, `mods`), `ValidateOptions.Mods` / `GameMods` and
  `ValidationReport.Mods` / `ModLines` / `ReportLines` / `Conflicts` (`ContentValidation`), `ManifestSchemas` in
  `RecordSchemas.cs`, and the `sage-mod-data` template. Experimental, SAGE0132.
- **`--mods <dir> ...`** names a mod (a folder with a `mod.json`) or a folder of mods (its subfolders with one; the rest are
  skipped with the boot's warning). `--game-mods` adds the game's own `modsDirectory`. The folders go to `SageApp` as
  `SageAppOptions.Mods`, so the mods are read, ordered (dependencies, `loadAfter`, `loadBefore`, then the order given),
  refused and mounted after the game's as `mods/<id>` exactly as a boot does it, not by a second implementation (test:
  AModNamedWithMods_IsReadOrderedAndMounted_AfterTheGame, AFolderOfMods_HoldsEachSubfolderWithAModJson_AndSkipsTheRest,
  GameMods_AreTheModsInTheGamesOwnModsDirectory, TheOrderIsTheModManagers_NotTheOrderOnTheCommandLine). A refused mod is a
  warning in the log and is listed with its reason; the game still validates (test:
  ARefusedMod_IsReportedWithItsReason_AndTheGameStillValidates). A data error in a mod's records is an error (test:
  ADataErrorInAMod_FailsValidation).
- **`sage mods <game> [--mods ...]`** prints the load order and the refusals (`ModManager.ListLines`), then the content
  report (`ContentReport.Lines`: what each mount added and patched, conflicts between mods, shadowed assets), then the
  log's warnings and errors, and a summary line. It always includes the game's own mods. It exits 1 on a refused mod or a
  content error; a conflict is a warning and exits 0 (test: TwoModsSettingOneField_ConflictAsAWarning_NotAnError).
- **The schemas:** `sage schema` also writes `mod.schema.json` and `game.schema.json` (the keys the two loaders read; an
  unknown key is refused, as the loaders refuse it). `.vscode/settings.json` maps them onto `**/mod.json` and
  `**/game.json` (test: TheModSchema_AcceptsTheTemplatesModJson_AndRefusesAnUnknownKey,
  TheGameSchema_AcceptsEveryGamesGameJson, TheWorkspaceSettings_MapModJsonAndGameJsonOntoTheirSchemas).
- **The template** `sage-mod-data` is no longer a stub: `dependencies` is `{}`, and its `data/` has a new prefab, a weapon
  (an `attack` and an `item` that carries it) and a patch of the game's `player`. It loads in a game made from `sage-game`
  and reports no conflict (test: TheTemplateMod_LoadsInAGame_AddsItsWeapon_AndPatchesThePlayer). CI's template step validates
  it with `--mods`, runs `sage mods`, puts it in the game's `mods/` and checks `--game-mods`, and checks that a `mod.json`
  with an unknown key fails. Since issue #297 the template is made by `sage new mod-data --game <game folder>`, which
  names the mod for the game, writes the game's schemas into the mod's `schemas/` and validates it against the game; its
  `.vscode/settings.json` maps `data/**/*.json` and `mod.json` onto those schemas and its `.vscode/tasks.json` runs
  `sage validate`, `sage mods` and `sage schema` against the game (test:
  New_ModData_WritesAModThatValidatesAgainstTheGame_WithItsSchemasMapped).

## As built (the mods screen, issue 4j-6)

- **`ModsView`** (`Sage.UI`, `[ViewModel("ui_mods")]`, SAGE0132) is a view of `Engine.ModManager`; it keeps no state of the
  player's choices and writes only through `ModManager.Enable` / `Disable` / `Move`, the calls the console's `mod_enable`,
  `mod_disable` and `mod_move` make. The kit's `rpg:mods` screen and layout (`src/Sage.Kits.Rpg/content/data/ui_screens.json`,
  strings `rpg.mods.*`) is the screen; `RpgKitModule.ModsScreen` is its id, and the Sandbox's F10 menu has a Mods button that
  opens it (or `ui_open rpg:mods`).
- **Rows are what the next start would load** (`ModManager.Next()`): the active in load order, then the switched off, then the
  refused with their reasons; a refused mod has no switch. Turning a mod off or on and moving it Earlier or Later write
  `user://mods.json` and the screen says "Restart to apply" (`RestartNeeded`); this run is unchanged (test:
  TheModsScreenTogglesAndReordersAndWritesTheListForTheNextStart, which also boots again from that list and sees the new order
  win). Where the order is fixed by a dependency, a move that changes nothing says so (`@rpg.mods.pinned`).
- **A conflicts tab** lists `ContentReport.Build(...).Conflicts`, the lines `mod_conflicts` prints (test:
  TheConflictsTabListsWhatTwoModsBothWrite).
- **With no user folder** (an app that keeps no `mods.json`) a change is refused with a message and nothing is changed (test:
  WithNoUserFolderAChangeIsRefusedAndSaysSo).
- **Refreshing allocates nothing**: the rows are made when the screen opens and after its own buttons, not per frame (test:
  RefreshingTheRpgScreensAllocatesNothing, which now includes `rpg:mods`). A console `mod_enable` while the screen is open
  shows when it is opened again.

## As built (the 4j exit game, issue 4j-7)

Phase 4j's exit: a data mod that adds a weapon and patches a trader loads, and its conflicts are reported.
**No engine change was needed**; the game, its two mods and the test are data and a test only. Code:
`tests/games/mods` (game id `village`, version 1.0.0, on the RPG kit for its shop screen and its hands),
`tests/Sage.Tests/Games/ModsExitTests.cs`; for modders, [docs/MODDING.md](../MODDING.md).

- **The game:** a trader (an NPC whose `inventory` part is his stock: a lantern and bread; his placement has
  no name, so he is called what his prefab is called), a straw dummy with 40 health and a player with empty
  hands. Its `mods/` folder (the default `modsDirectory`) holds `better_blades` and `rival_trade`, which the
  boot finds and mounts after the game as `mods/<id>`; with no constraint between them they load by id, so
  `rival_trade` is last. It validates alone and with `--game-mods` (test: TheModsGameValidates_WithItsMods).
- **better_blades** adds `better_blades:falchion_cut` (an `attack`), `better_blades:falchion` (an `item` for
  the kit's `MainHand`, worth 30) with its ground sprite on `textures/falchion.png`, and patches
  `village:trader` with `parts.inventory.items+` and a `name`. **rival_trade** adds a spear the same way,
  renames the trader too, places a stall with the scene's `place+`, has a cart nothing places, and ships its
  own `textures/falchion.png`.
- **Bought, wielded, swung:** the trader stocks lantern, bread, falchion and spear (each mod's `items+`
  merged); the player buys the falchion through the kit's `rpg:shop` (`ShopView`) with a gamepad, for 30 by
  the stub price rule, equips it in the main hand, and one cut takes the dummy from 40 to 25
  (test: ModsExit_TheFalchionIsBoughtFromTheTrader_Wielded_AndLandsAHit).
- **The report:** `mod_conflicts` says `prefab village:trader name: better_blades, rival_trade; rival_trade won`
  and `asset textures/falchion.png: better_blades, rival_trade; rival_trade won`, lists each mod's patch of the
  trader as an override of the game, and says nothing of the two `items+` (test:
  ModsExit_ModConflictsReportsTheNameAndTheTexture). With the player's `mods.json` ordering `rival_trade`
  first, `better_blades` wins both (test: ModsExit_WithTheOrderReversed_TheOtherModWins); with `rival_trade`
  switched off, it is not mounted, its spear and stall are gone and there are no conflicts (test:
  ModsExit_WithRivalTradeSwitchedOff_ItsItemIsGoneAndNothingIsReported).
- **A save across a mod change:** saved with both mods after the purchase and an `ent_spawn rival_trade:cart`,
  then loaded in a fresh app with `rival_trade` switched off: the slot lists both mods and says
  `mod 'rival_trade' 1.0.0 is not active`, the load warns and happens, the cart is a placeholder that keeps
  its prefab id, the stall (content the scene no longer places) is dropped, every other saved id is there
  once, and the falchion is once in the bag and not back in the stock
  (test: ModsExit_ASaveWithBothLoadedWithOne_WarnsKeepsThePlaceholderAndDoublesNothing).
- **CI** validates the game alone and with `--game-mods`, runs `sage mods tests/games/mods`, includes it in
  `sage schema`, and smoke-runs it in the host with `mod_list`, `mod_conflicts` and the shop screen opened.
- **Differences from the plan:** the game's id is `village`, not `mods`: a game's mounts are named
  `<game id>/<folder>`, so a game called `mods` would have a mount `mods/content` that the report would take
  for a mod. `rival_trade` has no `loadAfter: better_blades`, since a constraint beats the player's order and
  the order could then not be reversed by `mods.json`; it loads last by id instead. The placeholder is a
  runtime spawn of the mod's prefab (the cart), because an entity a mod's scene patch placed is content that
  is no longer placed once the mod is gone, and 4i drops those rather than keeping them. Phase 4f's trader
  record is not filed, so the mods patch the NPC's `inventory` part.
