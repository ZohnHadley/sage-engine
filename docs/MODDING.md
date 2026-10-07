# Modding a Sage game

This is for people who change a game someone else made: new weapons, a rebalanced trader, a translation, a
re-skinned texture. A Sage mod is **data**: a folder of record files, string tables and assets, with a
`mod.json` that says what it is. The game finds it, mounts it after its own content, and your records
patch the game's the way the game's patch the engine's. No code, no build step, no tool to install. A mod
can also be handed out as one `.sagemod` file (§8a), ship shaders (§8b), and, when records are not enough,
carry C# code (§8c), which is **trusted and not sandboxed**.

If you are making the game rather than modding it, read [MAKING_A_GAME.md](MAKING_A_GAME.md) first; most of
what it says about records holds for a mod too. The design behind this page is
[design/17-modding.md](design/17-modding.md), and the worked example is `tests/games/mods` (§10).

---

## 1. What a mod looks like

```
better_blades/                the mod's folder; its name doesn't matter, its id does
  mod.json                    what the mod is (§2)
  data/                       record files, any names, any subfolders
    falchion.json
  strings/en/                 string tables, merged key by key with the game's
    items.json
  textures/falchion.png       assets, at the paths records name
```

A mod can also be a single **`.sagemod`** file: a zip with `mod.json` at its root and the same layout (§8a).

The folder **is** the mod's mount root: `data/`, `strings/`, `textures/`, `sounds/`, `models/` sit at its
top, exactly where they sit in a game's `content/`. There is no `content/` folder inside a mod.

`sage new mod-data -n MyMod -o MyMod --game <game folder>` makes one (MAKING_A_GAME §2; `dotnet new
sage-mod-data -o MyMod` does the first half): a `mod.json` naming the game, a prefab, a weapon and a patch of the
game's player, ready to edit. With `--game <game folder>` it also writes the game's JSON Schemas into the mod's
`schemas/` (including this mod's own ids), validates the mod against the game, and the folder carries
`.vscode/settings.json`, which maps `data/**/*.json` and `mod.json` onto those schemas, and `.vscode/tasks.json`,
which has tasks for `sage validate`, `sage mods` and `sage schema` against the game (the template's `gameDir`
parameter is the path from the mod to the game folder; test:
New_ModData_WritesAModThatValidatesAgainstTheGame_WithItsSchemasMapped). Write the schemas again when the game
changes, since they describe the version of the game you have; `schemas/` is git-ignored.

## 2. `mod.json`

```jsonc
{
  "id": "better_blades",            // required: your record namespace (lower case, digits, _ . -)
  "name": "Better Blades",          // shown in mod lists; the id when left out
  "version": "1.0.0",               // SemVer; saves record it
  "author": "you",
  "description": "A falchion, sold by the village trader.",
  "game": "village",                // the game's id (game.json "id"); another game refuses the mod
  "gameVersion": "^1.0",            // a range against game.json "version"; skipped if the game has none
  "sage": ">=0.1",                  // the engine versions the mod was made for
  "dependencies": { "core_fixes": "^2.0" },   // mods that must be active, loaded before this one
  "loadAfter": ["other_mod"],       // order only: load after these when they are there
  "loadBefore": ["another_mod"],    // and before these
  "incompatible": ["rival_mod"],    // never with these: the later of the two is refused
  "kind": "data",                   // "data" (the default) or "code" (§8c)
  "assemblies": []                  // a code mod's dlls, relative to the folder: ["bin/{config}/MyMod.dll"]
}
```

- It is read **strictly**, like `game.json`: a key it doesn't know is an error, so a typo (`"dependancies"`)
  never silently does nothing. Comments and trailing commas are fine.
- **The id is your namespace.** Everything your `data/` defines is `better_blades:something`. It may not be
  `sage`, the game's id, or a kit's content namespace (`rpg`).
- Every field but `id` is optional; `version` defaults to `0.0.0`.
- **A mod.json that contradicts itself is an error**, and the mod is refused with the message: its own id in
  `dependencies`, `loadAfter`, `loadBefore` or `incompatible`, or one mod named both as a dependency and as
  `incompatible` (test: AModJsonThatContradictsItselfIsAnError).
- **`kind` and `assemblies`** go together: `"kind": "code"` needs `assemblies`, and `"kind": "data"` may not
  have any. Each path is relative, inside the mod's folder and ends in `.dll`; `{config}` is the build
  configuration, as in `game.json` (tests: AnAssemblyOutsideTheModOrMislabelledIsAManifestError,
  KindCodeWithNoAssembliesIsRefused).
- VS Code checks it as you type: `sage schema` writes `mod.schema.json`, and the repository's
  `.vscode/settings.json` (or the one a `sage-mod-data` mod carries) maps it onto every `mod.json`.

## 3. Where mods go, and turning them on and off

A game looks in two places when it starts:

- **the game's own `mods/` folder** (game.json's `modsDirectory`), for mods that ship with the game or that
  you are developing beside it;
- **`user://mods`**, the player's own: `<repo>/user/<game id>/mods` in a dev build, otherwise
  `%LOCALAPPDATA%/Sage/<game id>/mods` (or the platform's equivalent).

Each subfolder with a `mod.json` is a mod; one without is skipped with a warning. So is each **`.sagemod`**
file (any case of the extension) lying beside the folders; `-mods` and `sage validate --mods` can name one too
(test: ASagemodBesideTheModFoldersIsFoundMountedAndListed_AndAHostileOneIsRefusedWithTheReason). The same id in
both places is an error in the log, and the player's copy is used. `mod_list` and `sage mods` mark a packed mod
"(packed)".

**New mods are on**, and go at the end of the order. The player's choices live in `user://mods.json`:

```json
{ "order": ["rival_trade", "better_blades"], "disabled": ["old_mod"] }
```

The console changes it — `mod_list`, `mod_order`, `mod_enable <id>`, `mod_disable <id>`,
`mod_move <id> <n>` — and so does a game's mods screen. **Changes apply at the next start**: mods are
mounted once, before anything loads, and nothing is unmounted while the game runs. The host also takes
`-mods <dir>[,<dir>]` (exactly those folders, in that order, ignoring the list) and `-nomods` (none).

## 4. Load order

The mods that load are put in order by:

1. **`dependencies`, `loadAfter` and `loadBefore`**: a mod always loads after what it depends on and after
   what it says to load after; a name in `loadAfter`/`loadBefore` that isn't installed is ignored;
2. **the player's order** in `mods.json`, among mods free to go next;
3. **the id**, alphabetically, for the rest.

**The later mod wins** where two change the same thing. A constraint beats the player's order: if
`rival_trade` said `"loadAfter": ["better_blades"]`, no list could put it first.

A broken mod is **refused, with a reason, and the game still starts**. A mod is refused for: a dependency
that is missing, outside its range or switched off (and then whatever depended on it); being in a cycle
of dependencies or `loadAfter`/`loadBefore`; being `incompatible` with an earlier mod; the wrong `game`,
`gameVersion` or `sage`; an id that is taken; a `mod.json` that can't be read or contradicts itself; a `.sagemod` the
loader will not open (§8a); and code that does not fit (§8c).
The log's `Mods` category has one summary line at start, then a warning per refusal.

## 5. Adding and changing records

**Adding** is writing records, exactly as a game does (MAKING_A_GAME §3). A bare id is in your namespace:

```jsonc
[
  { "type": "attack", "id": "falchion_cut", "damage": 15, "damageType": "sage:physical",
    "reach": 2.4, "radius": 0.35, "arcDegrees": 90, "windupTime": 0.15, "recoverTime": 0.2, "cooldown": 0.5 },
  { "type": "item", "id": "falchion", "label": "falchion", "slot": "MainHand", "attack": "falchion_cut", "value": 30 }
]
```

Here `falchion` is `better_blades:falchion` and its `attack` is `better_blades:falchion_cut`; the damage type
is the engine's, written in full.

**Changing** the game's records is a **patch**: the same type and id, written in full, with
`"patch": true`, and only the fields you name.

```jsonc
{
  "type": "prefab", "id": "village:trader", "patch": true,
  "name": "Hilde the bladesmith",                                    // a value: replaced
  "parts": { "inventory": { "items+": [ { "item": "falchion" } ] } } // an object: merged, field by field
}
```

- **Values are replaced**, and **objects merge** recursively: `parts.inventory` above keeps the trader's
  `capacity` and everything else he has.
- **Lists are replaced** — unless you write `field+` to append (`"items+": [...]`) or `field-` to remove the
  entries equal to those you give (`"tags-": ["sage:element.fire"]`). These work at any depth, and two mods'
  `+`/`-` on one list both apply and never conflict.
- **Some lists are keyed**, and a patch edits one entry without restating the list (below).
- **`"disabled": true`** in a patch removes the record altogether.
- **A bare id inside a patch is yours**, even in someone else's record: `{ "item": "falchion" }` in the patch
  above is `better_blades:falchion`. Write the game's ids in full (`"village:lantern"`).
- **Redefining** a record that exists (the same id without `"patch": true`) is an error: it would wipe the
  original. The loader says so with both files, and applies yours as a patch so the game still runs.
- A patch of a record nobody defined is a warning and is skipped, so a patch of an optional mod's record
  does nothing when that mod is absent.
- `"place+"` on the game's `scene` adds placements, so a mod can put things in the world.
- `"live": true` in a patch of a game's scene keeps it placed and simulated while the player is elsewhere
  (issue #291; `space_live_max` bounds how many), and `"environment": { "gravityScale": 0.5 }` gives it its own
  gravity. Followers (the `follower` part) and streaming sources (`streaming_ring`) are parts a prefab patch can add
  (issue #290).

**Changing one entry of a list.** A list of objects can name the field its entries are told apart by
(`[ListKey("field")]` on the field in C#). The lists that do: a prefab's `inventory.items` (key `item`), a
`quest`'s `stages` (`id`) and a `dialogue`'s `nodes` (`id`). In a *patch*, a list value for one of these merges
by key instead of replacing: an entry whose key is there is merged into that entry field by field (nested
objects, `+`/`-` and deeper keyed lists work), an entry with a new key or none is appended, and the rest stay.

```jsonc
{ "type": "prefab", "id": "village:trader", "patch": true,
  "parts": { "inventory": { "items": [
    { "item": "lantern", "count": 3 },          // the entry keyed village:lantern: only count changes
    { "item": "bread", "$remove": true },       // the entry keyed village:bread is taken out
    { "item": "falchion" }                      // a new key: appended
  ] } } }
```

- A bare key is the record's namespace (`lantern` is `village:lantern` in the village's content); a foreign
  mod's entry is written in full (R11). Several entries with one key are all edited.
- **`"$remove": true`** in a keyed entry removes the entries with that key.
- **`"replace": true`** in any object of a patch, the record itself included, replaces that object with what
  the patch writes instead of merging into it. A record-level replace keeps `base`, an entry-level one keeps
  its key.
- Definitions and `base` inheritance are unchanged (a list in a child record still replaces its base's), and
  so are lists with no key: they replace, or take `+` and `-`. A scene's and placements' `place` lists are
  deliberately **not** keyed (a placement may have no id; the editor's patch restates the whole list).

(Tests: APatchChangesOneEntryOfAKeyedListByKey_WithoutRestatingTheList,
ABareKeyIsTheRecordsNamespace_AndAnEntryWithANewKeyIsAdded,
DollarRemoveTakesAnEntryOutByKey_AndAgainstAnotherModsEditOfItIsAConflict,
ReplaceTrueReplacesAWholeObject_ItsKeyedListsIncluded_OrTheWholeRecord,
AModChangesTheTradersPriceForOneStockItem_AndAnotherModsEditOfAnotherItemIsNoConflict.)

**Strings** merge key by key: a mod's `strings/en/items.json` with one key changes that line and leaves
the rest of the game's table. **Assets** a mod ships at a path the game, the engine or a kit also has are
replaced by path: a mod shipping `textures/hut_wall.png` replaces the game's, and every record that names that
path gets yours (§6 "Assets of your own, and replacing on purpose" for the rest). That holds for a game that ships
cooked files too (`sage package` writes a `.sgtex` beside each texture and a `.sgmesh` beside each model, design 05
§7): the client reads a cooked file only when its mount is at least as high as the loose file's, so **a mod's
loose file overrides a game's cooked one below it** (test:
TheCookedFileStandsInForTheLooseOneInItsMountAndALaterMountsLooseFileWins). A mod may ship `.sgtex` and `.sgmesh` files of its own, under the same rules, but usually leaves its assets
loose, as a game's `mods/` folder does when it is packaged (the cook does not touch it); sounds may be `.wav` or `.ogg`.
Textures may be `.png`, `.jpg` or `.tga`, each getting its mipmaps as it loads (issue #317) (test:
ATgaFromAModFolderLoadsPremultipliedWithItsMipsAndIsCooked), and models `.glb` within the engine's glTF subset
(MAKING_A_GAME "Model formats", issue #321): a model the engine cannot read fails with an error naming the file
and the feature. Since phase 4n a mod can patch a game's look the same way as anything else: a `material` patch
can add a `normalMap` or an `emissive` glow, a `light` part patch can make a lamp flicker (`pattern`), and new
`decal`, `mesh_lod`, `water_surface`, `terrain_material` and `weather_pattern` records are records like any other.

Phase 4o added sound and controls the same way. A mod can add `music` records (an `.ogg` track with optional
layers and loop points, streamed as it plays) and switch the world's music with a `play_music` action, a
`PlayMusic` wire or a scene's `environment.music`; a `sound` patch can give a sound a `rolloff`, a cone or
`"stream": true`; `reverb` records and `reverb_zone` parts re-room a place; a `cue` or `damage_type` patch can add
a `rumble` or a `sound`, an `effect` patch can add `appliedCues`, `tickCues` and `removedCues`, and a
`physics_material` patch can add a `land` or a `jump` cue. A mod's `input_map` adds bindings like any other map. The
player's own rebinds (`user://input.json`) are patches mounted after every mod, so a player's choice wins over a
mod's binding, and a rebound action is emptied in your map too; `bind_reset` brings the mod's own back (MAKING_A_GAME
"Sound, music and controls").

Phase 4q made screens moddable the same way (MAKING_A_GAME §7). A mod can add a whole screen with a working
button from records alone: a `ui_layout` whose buttons have `actions` (`open_screen`, `close_screen`, a `command`
line, or any action), a part of another layout brought in with `include` and its `params`, and a `screen` over the
`ui_world` view-model, which reads and writes the world's variables
(test: AModAddsAScreenWithAWorkingButtonAndAReusablePartFromRecordsAlone). It can add a `ui_option` to the options
screen, or patch or disable one of the game's; give a chest a `use_screen`; open a shop from a dialogue option
with `open_screen`; patch the RPG kit's default keys (`input_map` `rpg:ui` and `rpg:gameplay`); give an item a
picture (`rpg_item` `icon`) or a scene a map (`area_map`); and offer a high-contrast `ui_style_set`. A translation is
a `strings/<code>/` folder and a `language` record naming its direction and fonts; `loc_check` says what it
still lacks (test: ValidateReportsWhatATranslationLacks).

Phase 4f's progression and economy are records too, patched like any other: a mod can rebalance a `merchant`
(its `markup`, `pays` or `restock`), add to the `loot_table` it restocks from with `"entries+": [...]`, put a new
item on a creature's leveled list, lock a chest with a `container` part, add an `equip_slot` (a helmet needs no
code), or add a `skill`, a `levelling` step or a `perk` to a game built on the RPG kit. A trader without a
`merchant` record, like the village trader below, still sells at the kit's stub prices.

Phase 4r's AI, combat and narrative are records as well, with no code. A mod can give a creature a `behaviour_tree`
(`"ai_state": { "tree": "..." }` on a prefab patch) or `utility` options on its `ai_profile`, using any task the game
has (`Strafe`, `RetreatToRange`, `TakeCover`, `Flee`, `HealSelf`, `Flank`, `CallForHelp`, besides the schedules' own)
and, if it adds a task or an `ai_measure` in code, those too; put a `squad` part on a pack; widen or silence what
creatures hear (`hearing` on a profile, `noise` and `schedules.investigate` on the conventions, an attack's
`noiseRadius`); add a `crime`, a `guard` or an `owned_place`, or a rung to a `faction_ranks` ladder; give an attack
a `block`, `knockback`, `cleave` or a `damage` execution (a poison); lines for a creature in a `barks` record, a
greeting or a speaker's own topics on a `dialogue`; and a timer, a failure or a `volume` or `topic` objective on a
`quest` stage. A list field the mod extends with `+` keeps the game's entries, as with a loot table.

Phase 10b gave modders the editor (issue #375). A game packaged with `sage package <game> --out <dir>
--editor` has the editor beside its Shipping host, in `editor/`, and `edit.sh` / `edit.cmd` open it on the
game. `./edit.sh [level] +ed_mod <your mod>` makes your mod's folder the only one the editor writes: a level
your mod defines saves in place, a new level is made in your mod, and the game's own level is saved as a
`"patch": true` placements record in `<your mod>/data/patches/placements_<namespace>_<name>.json`, holding
its whole list of placements (ids and record names in full), which the loader applies over the game's like
any other patch. The game's files are never written ([EDITOR.md](EDITOR.md) §8; test:
AModderSavesBothTabsIntoTheModAndTheGamesLevelAsAPatch). A brush is a placement too, so a mod can add walls to
a level with `place+` (test: AModAddsBrushesToALevelByPatchingItsDocument).

## 6. Conflicts and the report

When two mods change the same thing, the later wins and the game tells you. A **conflict** is:

- two mods setting the same field (or one setting a field inside what another set):
  `prefab village:trader name: better_blades, rival_trade; rival_trade won`;
- one mod's `disabled` against another's patch of the same record;
- two mods replacing the same asset of the game or of another mod (§6 below):
  `asset textures/lantern.png: better_blades, rival_trade; rival_trade won`;
- two mods editing the same entry of a keyed list, reported per key:
  `test_shop village:shop stock[village:lantern].price: cheap_lanterns, dear_lanterns; dear_lanterns won`.
  Different keys never conflict, nor does an `items+` by one mod and a keyed edit by another; a whole-record
  `"replace": true` against another mod's edit is a conflict at `(record)`.

Two mods adding to one list with `+` is **not** a conflict, and neither is a mod changing the game, the
engine or a kit — that is an **override**, which is what a mod is for; the report lists it as one.
Conflicts are warnings: the game still runs, with the later mod's values.

### Assets of your own, and replacing on purpose

A bare asset path in your records (any asset field, a patch included) that **your mod ships** and no
non-mod mount (the engine, a kit, the game) has is **your own asset**: at load it becomes `mod_id:path`
(`better_blades:textures/falchion.png`). Two mods that ship `textures/falchion.png` each see their own: no
conflict, nothing shadowed. A bare lookup from anywhere else (the game, another mod) still finds the later
mod's file. Any record may name a namespace's file in full, `ns:path`: another mod's own asset, or the
game's (`village:textures/x.png`). One `ns:` prefix is allowed and no more (`c:/x` and `a:b:c` are refused).

To **replace** a file on purpose:

- **implicitly**, as before: a file at a path the game, engine or kit has replaces it for everyone, and the
  report says `overrides textures/lantern.png in village/content`; two mods doing so are a conflict;
- **explicitly**: a file at `<your mod>/@<ns>/<path>` replaces namespace `ns`'s `<path>`: another mod's own
  asset (`@better_blades/textures/falchion.png`, reported `overrides better_blades:textures/falchion.png in
  better_blades`) or the game's (`@village/textures/lantern.png`, which also answers the bare path). Two
  mods replacing one mod's own asset are a conflict (`asset better_blades:textures/falchion.png: rival_trade,
  third; third won`).

(Tests: TwoModsShippingOnePath_EachSeeTheirOwn_AndItIsNoConflict, AVirtualPathMayNameANamespace,
AModReplacingTheGamesFile_IsAnOverride_AndTwoAreAConflict,
AnAtNamespaceFolderReplacesThatNamespacesFileOnPurpose,
ModsExit_EachModSeesItsOwnFalchionTexture_AndTheGamesLanternPictureIsReplacedOnPurpose.) A hot reload of a
file under `@ns/` reloads that literal path, not the namespaced asset that uses it: restart to see it.

Where to read it:

- **`mod_conflicts [mod]`** at the console: the conflicts, then per mount what it added, what it patched
  (field by field, an override of the game marked), what it replaced, and any redefinitions or skipped
  patches. With a mod's id, only that mod's part.
- **`user://logs/mod_report.txt`**, written at every start: the load order, the refused mods with their
  reasons, then the same report.
- **`rec_get <type> <id>`** shows every file that wrote a field, with its line, and the one that won.
- **`vfs_which <path>`** says which mount an asset comes from; **`vfs_mounts`** lists them, mods last.
- **`sage mods <game>`** prints all of it without starting the game (§8).
- **The editor's Records tab** shows a conflicted record's fields one by one, with each mod's value and the
  winner ([EDITOR.md](EDITOR.md) "Records"; console `ed_rec_conflicts`).
- A mod with code is marked `[contains code: not sandboxed]` in `mod_list`, the boot report and `sage mods`
  (§8c).

## 7. Saves

A save's header lists the mods that were active, with their versions. Loading a save with different mods
**warns and loads anyway**:

- `mod 'rival_trade' 1.0.0 is not active` — it was on then and is not now;
- `mod 'better_blades' was 1.1.0 and is 1.2.0`;
- `mod 'x' was not active then`.

What a missing mod's records made is handled the way any content change is: something the game spawned
from a mod's prefab is kept as an **inert placeholder**, and written back unchanged by the next save, so
turning the mod back on brings it back; something a mod's scene patch *placed* is content that is no longer
placed, and is dropped. Nothing is doubled. The load menu shows the same mismatch lines before you load. From
the console, `save_report <slot>` lists them too, with every component and resource the save holds
against this build: current, to be upgraded, written by a newer game, or unknown here (kept as data;
issue #285).

## 8. Checking a mod without playing

`sage` (src/Sage.Cli) runs the game's whole content load headlessly, with every check the game makes:

```bash
sage validate <game> --mods <mod folder>    # the game with your mod; exits 1 on any error
sage validate <game> --mods <folder of mods> # every subfolder with a mod.json
sage validate <game> --game-mods            # the game's own mods/ folder
sage validate <game> --mods <mod>.sagemod   # a packed mod
sage mods <game> --mods <mod folder>        # the order, refusals and the report; exits 1 on a refusal or an error
sage mods pack <mod folder> [--out <file.sagemod>] [--engine-content <dir>]   # one distributable file (§8a)
sage mods build <mod folder> [--engine-content <dir>]   # compile the mod's shaders beside their sources (§8b)
sage schema <game> --mods <mod folder> --out schemas   # JSON Schemas that know your ids too
```

The mods are ordered and mounted exactly as the game does it, by the same code. Conflicts are warnings
here too; a broken reference, an unknown field or a missing asset is an error at its file and line. Run
`sage mods` before you publish.

In the running game, `problems` at the console lists the same errors and warnings from the load it made,
with file and line, and the conflicts between mods as warnings, in any build (issue #301).

While the game runs (a dev build), a saved change to your `data/`, `strings/` or assets **hot reloads**, as
the game's own do; a change to `mod.json` says "restart to apply". A patch to a prefab reaches what is already spawned
from it (issue #287): every field the game has not changed takes the new value, and a placement's own
overrides are kept; parts, children and removed components wait for the next spawn. Other record types
reach only what is built after the reload, unless their `[Record]` says `Reload = ReloadPolicy.Live`.

## 8a. Packing a mod: `.sagemod`

`sage mods pack <mod folder> [--out <file.sagemod>]` writes the mod as **one file**: a zip with `mod.json` at its
root, laid out as the folder is. The default name is `<id>-<version>.sagemod` in the current folder. The mod's
`mod.json` is checked first, dot files and dot folders are left out, a symbolic link or an unsafe path refuses
the pack, and the bytes are the same each time (entries sorted, a fixed timestamp), so a rebuilt package is
byte-identical. The package is read back as a mount before it is moved into place (tests:
PackWritesASagemodThatLoads_TheSameBytesEachTime_WithoutDotFiles, PackRefusesABrokenModAndALinkWritingNothing).

A packed mod is found, ordered, mounted and reported like a folder (`mods/<id>`), and a game and its mods load
**identically as folders and as zips**: the same active mods, the same merged records, the same winning file
for every path (test: AGameAndItsModsLoadIdenticallyAsFoldersAndAsZips). Differences: a zip is read-only, so
there is no hot reload of its files and the editor never writes into it.

**The loader refuses a hostile package** with the rule it broke (`<file>: refused: <rule>`), and the other mods
load. Refused: an absolute path, `..` (zip-slip), `.` or empty segments, `:` and control characters in a name,
a symbolic link, two entries that are one path ignoring case, more than 200,000 entries, a file over 1 GiB,
over 16 GiB in all, a file over 1 MiB that shrinks more than 500 to 1 (a zip bomb), and a file longer than its
header says. A file that is not a zip is "not a zip archive, or is damaged". The limits are the engine's; a
game cannot change them (tests: AnEntryWhosePathLeavesTheArchiveIsRefusedWithTheRule,
TwoEntriesThatAreOnePathIgnoringCaseAreRefused, ASymbolicLinkEntryIsRefused, TooManyEntriesAreRefused,
AFileOverTheSizeLimitIsRefused, FilesOverTheTotalLimitAreRefused, AZipBombIsRefusedByItsRatio,
AFileLongerThanItsHeaderSaysIsRefusedWhenRead, AFileThatIsNotAZipIsRefusedAsDamaged).

A game can mount a zip too: a `game.json` mount that names an existing file is read as a zip and mounted as
`<game>/<name without the extension>`; a refused one is skipped with an error and the game boots (test:
AGameMountThatIsAZipIsMountedUnderTheFoldersName_AndAHostileOneIsSkippedWithAnError).

**A mod with shaders or code is built before it is packed.** Packing compiles every shader into the package
(§8b), and a code mod's assemblies must exist: build the mod, then pack it. The package takes `bin/` (every
configuration built, so `bin/{config}/X.dll` works in a host of any configuration) and leaves the root `obj/`
out; a code mod built in no configuration is refused ("is not built (build the mod, then pack it)").

## 8b. Shaders in a mod

Put an effect in the mod's `shaders/` (`shaders/glow.fx`) and name its compiled form, `shaders/glow.mgfxo`, in a
`material`, as a game does. An effect may `#include "common.fxh"` and the headers of the mounts below it (the
engine's, the game's); the mod's own headers win.

- **In a dev build** the game compiles a folder mod's (and the game's) `.fx` when it loads, if the `.mgfxo`
  beside it is missing or older than the effect or a header of its folder, and again when you save an edit;
  the new effect is swapped in. The output goes beside the source **in the mod's own folder**, never over the
  game's `.mgfxo`. A compile error is an Error in the `Shaders` log with the compiler's file and line, the last
  good `.mgfxo` is kept, and with none the material draws as flat magenta (`sage:error`) until it compiles.
  Where the compiler cannot run (Linux without Wine, `MGFXC_WINE_PATH`) that is said once and nothing compiles
  (tests: ADevBuildCompilesAModsEffectsOnLoad_AndAgainWhenOneIsEdited, AModsEffectIncludesTheEnginesHeaders_AndItsOwnWin,
  AFailedCompileKeepsTheCompiledFile_AndItsMessageNamesTheModsFile,
  AModsEffectThatNeverCompiled_LeavesItsMaterialsOnSageError,
  WhereTheCompilerCannotRun_NothingIsCompiled_AndItIsSaidOnce).
- **`sage mods build <mod>`** compiles only the out-of-date effects, beside their sources. It exits 1 on a
  compile error or where the compiler cannot run (test: BuildingAModCompilesOnlyWhatIsOutOfDate_BesideItsSource).
- **`sage mods pack`** compiles every effect into the package, and the `.fx` source travels with it; the mod
  folder is not written to. Where the compiler cannot run, an up-to-date `.mgfxo` beside its source is packed
  as it is, and a stale or missing one fails the pack, saying to `sage mods build` where it can. A compile
  error fails the pack (tests: PackingAModCompilesEveryEffectIntoThePackage,
  WithoutACompiler_AnUpToDateCompiledFileIsPacked_AndAStaleOneRefusesThePack,
  AnEffectThatDoesNotCompileStopsThePack_SayingWhy, APackageWithAnEffectButNoCompiledFileIsRefused).
- **A packed mod's effects are not compiled when it is played**: a zip's `.fx` is never compiled, so a package must
  carry its `.mgfxo`, and a content check counts a `.mgfxo` as present through its `.fx` only for a folder mod
  (test: AFolderModsEffectSourceMakesItsCompiledPathItsOwn_AZipsDoesNot).

## 8c. Code mods: trusted, not sandboxed

A mod may carry C#. Its `mod.json` says `"kind": "code"` and lists its dlls in `"assemblies"`
(`["bin/{config}/SmithsGuild.dll"]`). **Read this before installing one:**

- **A code mod is not sandboxed.** .NET cannot confine an assembly, so the mod runs with the game's full
  trust: it can read your files, use the network and do anything the game can. Install code mods only from
  people you trust, as you would a program.
- **Nothing is ever downloaded or run for you.** The engine has no download path at all; a mod is a folder or
  a `.sagemod` the player puts in place. A Workshop subscription (§9) is never consent to run code.
- **It is flagged.** `mod_list`, the boot mod report and `sage mods` mark the mod
  `[contains code: not sandboxed]`, `sage mods` adds "(N with code, not sandboxed)" to its summary, and the
  `Mods` log says at boot that the mod is loaded "with the game's full trust, not sandboxed". (The kit's mods
  screen does not show the flag yet.)

How it works, for the one writing it. Each code mod gets its own collectible `AssemblyLoadContext`
(`mod:<id>`), loaded before any module's `Init` and before any world exists, so before the registries seal
and the ECS schema is built. `Sage.*` and whatever the host has loaded are shared with it; its other
dependencies resolve from its assemblies' folders (or from its package). A packed code mod loads its dlls from
the archive (with the `.pdb` beside, when packed) and its `Assembly.Location` is empty. The example, and the
template for what to write, is `tests/games/code-mod` (mod `smiths_guild`: a record type, a saved resource with
an `[Upgrade]`, a console command and a patch of the village trader) (tests:
TheExampleCodeModAndADataModLoad_AndModConflictsReportsTheirConflict, ValidateRunsTheCodeMod).

- **Modules.** A code mod's `IModule`s must be `[Plugin("<mod id>")]` or `[Plugin("<mod id>.<name>")]`: its
  records, parts, systems and commands are then owned by that plugin (`plugins smiths_guild`). They are added
  after every other module, the game's included, so `Init` runs after the game's unless `[RequiresPlugin]`
  orders it otherwise (test: ACompiledCodeModRunsItsInitUnderItsOwnPluginId_AfterTheGame).
- **Refused, with a reason, and the game still boots** (whatever depends on the mod goes too): a module with no
  `[Plugin]`, a plugin id outside the mod's or already taken, an `IGameModule` (a mod is not a game),
  `[RequiresPlugin]` on a plugin that is not loaded or at the wrong version or a `sage` range not met,
  `IModule.Dependencies` not loaded, a dll that is not there ("is not there (build the mod"), no public
  `IModule`, and code that throws while loading (tests: ACodeModWhoseModulesDoNotFitIsRefused_AndTheGameBoots,
  ACodeModWhoseAssemblyIsMissingOrHasNoModuleIsRefused,
  ACodeModIsOrderedLikeAnyMod_AndKindCodeWithNoAssembliesIsRefused). A packed code mod whose dll is not in its
  package is refused ("its assembly X is not in its package").
- **Components and tags need the host.** ECS components and tags a code mod declares exist only when the process
  builds its component schema after the mod is loaded, which the host always does. In a process whose schema is
  already built (a second app, a test), such a mod is refused ("declares ECS components or tags") (test:
  ACodeModWithComponentsIsRefusedOnceTheProcessHasBuiltItsSchema). The schema keeps a component type for the
  life of the process, so a mod that declares components is never unloaded.
- **Unloading.** The contexts are unloaded when the app is disposed, after shutdown (test:
  TheCodeModsContextUnloadsWhenTheAppIsDisposed).
- **Saves.** The mod's `[SavedResource]` round-trips. A save made with the code mod loads without it (the
  resource is kept as unknown data, and the header says "mod 'smiths_guild' 1.0.0 is not active") and the
  resource is back when the mod is; the mod's own `[Upgrade]` runs from its assembly (tests:
  SavesSurviveRemovingTheCodeModAndPuttingItBack, ACodeModsOldSaveIsUpgradedByItsOwnUpgrader).
- **Checking one.** `sage validate` and `sage mods` run the mod's `Init` and the validation world:
  `sage validate tests/games/mods --game-mods --mods tests/games/code-mod`.

The phase's exit, as built: `better_blades`, `rival_trade` and `smiths_guild`, all packed as `.sagemod`, load
together; the code mod runs from its archive; `mod_conflicts` reports `prefab village:trader name:
better_blades, rival_trade, smiths_guild; smiths_guild won`; each data mod reads its own `falchion.png`; and a
save made with the packed code mod survives removing it and putting it back (test:
PackedDataAndCodeModsLoad_Conflict_AndSavesSurviveTogglingThePackedCodeMod).

## 9. What is not supported (yet)

- **Workshop and downloads.** `Sage.Client.IWorkshop` is an interface only (list what a store has installed,
  publish a package); no service ships and nothing is downloaded. A store's own client installs items into a
  folder it owns, and they go through the same checks as any mod folder.
- **Hot reload of a packed mod** (a zip is read-only) and of a file under `@ns/`.
- **Keys on every list.** Only `inventory.items`, a quest's `stages` and a dialogue's `nodes` are keyed; scene
  and placement `place` lists replace.
- **Shaders in a zip compile at run time.** Pack them compiled (§8b).
- **A sandbox for code.** There is none and there will not be one (§8c).
- **The kit's mods screen** does not yet show the "contains code" flag; the console and `sage mods` do.

## 10. The worked example

`tests/games/mods` is a village with a trader whose `inventory` part is his stock, a straw dummy and a
player, and two mods in its `mods/` folder:

- `better_blades` adds a falchion (an `attack`, an `item` and its ground sprite), puts it in the trader's
  stock with `items+`, and renames him;
- `rival_trade` adds a spear the same way, renames him too, places a stall with `place+`, has a cart to
  spawn, has its own `rival_trade:falchion` item from its own `textures/falchion.png`, and replaces the game's
  lantern picture on purpose with `@village/textures/lantern.png`.

A third mod, `tests/games/code-mod` (`smiths_guild`, §8c), is the code mod: it declares a record type, a saved
resource and a command, and renames the trader as well.

`sage mods tests/games/mods` shows the order and the one conflict (the trader's name, won by `rival_trade`,
which loads later by id; the two falchion textures are each mod's own and clash with nothing), and
`tests/Sage.Tests/Games/ModsExitTests.cs` plays it: the falchion bought from the trader through the RPG kit's
shop and swung, the order reversed with `mods.json`, a mod switched off, and a save carried across. With the
code mod added and all three packed (`sage mods pack`), the trader's name is a conflict of all three and
`smiths_guild` wins (test: PackedDataAndCodeModsLoad_Conflict_AndSavesSurviveTogglingThePackedCodeMod).
