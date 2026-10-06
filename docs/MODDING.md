# Modding a Sage game

This is for people who change a game someone else made: new weapons, a rebalanced trader, a translation, a
re-skinned texture. A Sage mod is **data**: a folder of record files, string tables and assets, with a
`mod.json` that says what it is. The game finds it, mounts it after its own content, and your records
patch the game's the way the game's patch the engine's. No code, no build step, no tool to install.

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
  "incompatible": ["rival_mod"]     // never with these: the later of the two is refused
}
```

- It is read **strictly**, like `game.json`: a key it doesn't know is an error, so a typo (`"dependancies"`)
  never silently does nothing. Comments and trailing commas are fine.
- **The id is your namespace.** Everything your `data/` defines is `better_blades:something`. It may not be
  `sage`, the game's id, or a kit's content namespace (`rpg`).
- Every field but `id` is optional; `version` defaults to `0.0.0`.
- VS Code checks it as you type: `sage schema` writes `mod.schema.json`, and the repository's
  `.vscode/settings.json` (or the one a `sage-mod-data` mod carries) maps it onto every `mod.json`.

## 3. Where mods go, and turning them on and off

A game looks in two places when it starts:

- **the game's own `mods/` folder** (game.json's `modsDirectory`), for mods that ship with the game or that
  you are developing beside it;
- **`user://mods`**, the player's own: `<repo>/user/<game id>/mods` in a dev build, otherwise
  `%LOCALAPPDATA%/Sage/<game id>/mods` (or the platform's equivalent).

Each subfolder with a `mod.json` is a mod; one without is skipped with a warning. The same id in both places
is an error in the log, and the player's copy is used.

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
`gameVersion` or `sage`; an id that is taken; a `mod.json` that can't be read; and asking for code (§9).
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

**Strings** merge key by key: a mod's `strings/en/items.json` with one key changes that line and leaves
the rest of the game's table. **Assets** are replaced by path: a mod shipping `textures/hut_wall.png`
replaces the game's, and every record that names that path gets yours. That holds for a game that ships
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

## 6. Conflicts and the report

When two mods change the same thing, the later wins and the game tells you. A **conflict** is:

- two mods setting the same field (or one setting a field inside what another set):
  `prefab village:trader name: better_blades, rival_trade; rival_trade won`;
- one mod's `disabled` against another's patch of the same record;
- two mods shipping the same asset path:
  `asset textures/falchion.png: better_blades, rival_trade; rival_trade won`.

Two mods adding to one list with `+` is **not** a conflict, and neither is a mod changing the game, the
engine or a kit — that is an **override**, which is what a mod is for; the report lists it as one.
Conflicts are warnings: the game still runs, with the later mod's values.

Where to read it:

- **`mod_conflicts [mod]`** at the console: the conflicts, then per mount what it added, what it patched
  (field by field, an override of the game marked), what it shadowed, and any redefinitions or skipped
  patches. With a mod's id, only that mod's part.
- **`user://logs/mod_report.txt`**, written at every start: the load order, the refused mods with their
  reasons, then the same report.
- **`rec_get <type> <id>`** shows every file that wrote a field, with its line, and the one that won.
- **`vfs_which <path>`** says which mount an asset comes from; **`vfs_mounts`** lists them, mods last.
- **`sage mods <game>`** prints all of it without starting the game (§8).

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
sage mods <game> --mods <mod folder>        # the order, refusals and the report; exits 1 on a refusal or an error
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

## 9. What is not supported (yet)

These are phase 9's (docs/REDESIGN.md §5):

- **Code mods.** A mod with `"assemblies"` or `"kind": "code"` is refused with "code mods are phase 9".
  .NET can't sandbox an assembly, and the engine's tables of components and systems are built before
  content loads.
- **Packed mods** (`.sagemod` zips): a mod is a folder.
- **Namespaced asset paths.** Asset paths are global: two mods shipping one path is a conflict that the
  later wins, and there is no way for both to have their own `textures/falchion.png`. Give your assets
  paths of their own (`textures/better_blades/falchion.png`) unless you mean to replace someone's.
- **`"$remove"`, `"replace": true` and keyed list merge**: a list entry is removed by writing it out in full
  in `field-`, and an entry can't be changed in place.
- Workshop and download support, and a per-field conflict view in the editor.

## 10. The worked example

`tests/games/mods` is a village with a trader whose `inventory` part is his stock, a straw dummy and a
player, and two mods in its `mods/` folder:

- `better_blades` adds a falchion (an `attack`, an `item` and its ground sprite), puts it in the trader's
  stock with `items+`, and renames him;
- `rival_trade` adds a spear the same way, renames him too, places a stall with `place+`, has a cart to
  spawn, and ships its own `textures/falchion.png`.

`sage mods tests/games/mods` shows the order and the two conflicts (the trader's name and the texture,
both won by `rival_trade`, which loads later by id), and `tests/Sage.Tests/Games/ModsExitTests.cs` plays
it: the falchion bought from the trader through the RPG kit's shop and swung, the order reversed with
`mods.json`, a mod switched off, and a save carried across.
