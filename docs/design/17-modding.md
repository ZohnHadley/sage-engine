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
  `sage validate` and `sage schema` pass an empty list for now: `--mounts` stays how they take a mod until 4j-5.
- **Console:** `mod_list` (active in load order, switched off, refused with the reason), `mod_order` (this run's order, the
  player's order and the next start's), and `mod_enable <id>`, `mod_disable <id>`, `mod_move <id> <n>`, which write
  `user://mods.json` and say "applies at next start": nothing is remounted now, since the VFS has no unmount (decision 3).
  A mod the list did not name joins it where it loads now, so a change does not reorder the rest; an unknown id is an
  error naming the mods there are (test: TheConsoleWritesThePlayersListForTheNextStartAndChangesNothingNow).
  `ModManager.Enable` / `Disable` / `Move` / `Next` are the same for a mods screen (4j-6).
- **`user://logs/mod_report.txt`** is written once records have loaded: the summary, the load order with folders, the
  switched off, the refused and the notes (test: TheBootWritesAModReport). The per-mod content report (records added and
  patched, conflicts, shadowed assets) is 4j-2's `ContentReport`, which goes into `ModManager.WriteBootReport`, the one
  seam left for it.
- **Hot reload:** a mod is a folder mount like the game's, so `RecordHotReload` and the client's asset watchers cover its
  `data/`, `strings/` and assets (test: EditingAModsRecordHotReloadsIt). In a dev build, a changed or new `mod.json`
  warns "restart to apply" once per file (`ModManager.WatchManifests`, started by the host beside `RecordHotReload`;
  test: AChangedModJsonSaysRestartToApply).
- **Saves** list `Engine.Mods.Active` (4j-4), which the boot now fills; their mounts are the `mods/<id>` the save header
  leaves out of `content`.
