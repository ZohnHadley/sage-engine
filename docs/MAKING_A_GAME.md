# Making a game with Sage

This is the practical guide: what you write, where it goes, and in what order, using the engine **as it
is today** rather than as it is planned. Everything here is something the Sandbox already does — if a
thing is not in this guide, assume the engine does not do it yet, and check the "What is not here yet"
section at the end before building around it.

The design docs in [`docs/design/`](design/00-index.md) explain *why* each system is shaped the way it
is. This explains how to use them.

**There is a runnable example of everything in §2–§4**: [`games/Hello`](../games/Hello) is the smallest
game this engine can run — four files and 47 lines of code — and it is built by the solution and <!-- counts: files games/Hello, code games/Hello -->
exercised by the test suite, so it cannot quietly stop working. Read it alongside this, or start a
game of your own from the `sage-game` template (§2):

```bash
dotnet run --project games/Hello -c Development    # Sage.Sdk's `dotnet run`: the host with -game games/Hello
```

---

## 1. What you supply, and what the engine already does

The engine gives you: a fixed-tick simulation with an ECS, physics, a character controller, terrain,
brush levels, AI, combat, items, abilities, factions, dialogue, quests, particles, weather, audio, a
console, saves, hot reload, and a renderer. **It gives you no game.** What a game supplies is:

| You write | In what form |
|---|---|
| What exists in your world | **records** — JSON files under your game's `content/data/` |
| What things are made of | **prefabs** — records that list components and parts |
| Where things are | a **scene** record (the engine places it), and/or `.map` levels drawn in TrenchBroom |
| Rules that are yours | a small amount of **C#** in your game module — or none: a game can be only `game.json` and records (§4) |
| How it looks on screen | your **HUD** and screens, in your game's client half |
| Art and sound | PNG, WAV or OGG, `.glb` — read at runtime, no build step (`sage package` cooks models and textures, §2 "Shipping it") |

The engine assumes no genre. What a family of games shares beyond that is a **kit**: `Sage.Kits.Rpg`
(the readied spell and its Cast button, the spellmaker, two hands to hold things in, the bag, spellbook,
journal and conversation screens) is the one there is, and a game opts in with one line of `game.json`
(§2, "Kits").

The split that matters: **your simulation half references the base engine only** (`Sage.Core`, `Sage.Simulation`,
`Sage.Physics3D`, `Sage.Gameplay`), and any kit it is built on — no MonoGame — so it can
be tested headlessly and could run on a server one day. Anything that needs a screen goes in a second
project, your *client half*.

---

## 2. The shape of a game

A game is a folder with a `game.json`, its content, and (usually) C# built with **`Sage.Sdk`**, the
engine's MSBuild SDK (issue #32). It lives anywhere: nothing of it has to be inside this repository.
Start from a template:

```bash
tools/pack_sdk.sh ~/sage-feed --build                        # once, in this repository: SDK, Player, templates
dotnet new install ~/sage-feed/Sage.Templates.*.nupkg        # once per machine (the version is the engine's)
dotnet new sage-game -n YourGame -o ~/games/YourGame --feed ~/sage-feed
cd ~/games/YourGame
dotnet run                                                   # builds both halves, starts the host on this folder
```

There is no public feed yet (REDESIGN §6 decision 7), so `pack_sdk.sh` packs one on your machine:
`Sage.Sdk`, `Sage.Player` (the host and the `sage` CLI, built in Debug, Development and Shipping) and
`Sage.Templates`. `--feed` writes a `nuget.config` that names it; leave it out if the folder is already
a package source (`dotnet nuget add source ~/sage-feed -n sage`). `--build` builds what it packs; on
Linux that is without shaders (`mgfxc` needs Wine), so that Player boots and runs a game but draws
nothing — pack on Windows, or after a Windows build, to see one. The three packages carry the engine's
version, from git tags (`0.1.0-alpha.0.N` until 0.1.0 is tagged; [RELEASING.md](RELEASING.md)), and the
templates name that `Sage.Sdk/<version>` in the projects they make.

The templates:

| Template | What you get |
|---|---|
| `sage-game` | A simulation half and a client half, `game.json`, a scene with a player on generated hills, and a HUD that shows what the rules say |
| `sage-game-data` | No C# at all: `game.json` and records; the engine places the scene and spawns the player (like `tests/games/scene-only`) |
| `sage-game-client` | A client half (a HUD, screens, anything with a screen) for a game that has none, such as a `sage-game-data` game: a project in the game's `Client/` folder, below |
| `sage-mod-data` | A data mod: `mod.json`, a new prefab and weapon, and a patch of the game's `player`, with `.vscode/` settings that map its records and `mod.json` onto the game's schemas and tasks that validate it against the game. Put it in the game's `mods/` folder; check it against a game with `sage mods <game> --mods <mod>`. [docs/MODDING.md](MODDING.md) is the modder's guide |

**`sage new` and `sage run`** (issue #297) are the `sage` command line's way to the same two steps. `sage new
<template> [-o <dir>] [-n <name>] [--game <game folder or id>] [--dry-run] [<dotnet new options>]` is `dotnet
new` with the template's short name (`game`, `game-data`, `game-client`, `mod-data`); what it does not know
(`--feed`, `--sdkVersion`) goes on to `dotnet new`, and a template it does not know exits 2. With `--game`
it does the rest of the work a template needs from the game it is for:

```bash
sage new game -n YourGame -o ~/games/YourGame --feed ~/sage-feed
sage new mod-data -n better_swords -o ~/mods/better_swords --game ~/games/YourGame   # schemas written, validated against the game
sage new game-client --game ~/games/YourData                                          # Client/ made, game.json loads its dll
sage run ~/games/YourGame                                                             # build and start it
sage run ~/games/YourGame -- +sv_cheats 1 +map crypt                                  # after the dash: the host's arguments
```

`sage new mod-data --game <game folder>` names the mod for the game, writes the schemas its `.vscode/settings.json`
maps (`schemas/`, from `sage schema <game> --mods <mod>`) and validates the mod against the game (test:
New_ModData_WritesAModThatValidatesAgainstTheGame_WithItsSchemasMapped). `sage new game-client --game <game
folder>` makes the client half in the game's `Client/` folder, named for the game, and adds its dll to
`game.json`'s `"modules": { "add": [...] }` as `Client/bin/{config}/<Name>.Client.dll`, keeping the rest of the
file as written (test: New_GameClient_MakesTheClientHalf_AndGameJsonLoadsIt_KeepingWhatWasWritten). The client
half's project builds the game folder's own project first, and the game folder's project leaves `Client/`
to it, so `dotnet run` in `Client/` builds both halves and starts the host on the game folder. `sage run
<game> [--config c] [--host dir] [--dry-run] [-- <host arguments>]` is that `dotnet run`: on the project in
`<game>/Client` when there is one, else the project in `<game>` (test:
Run_ASageSdkGame_IsDotnetRunOnItsProject_WithTheHostArgumentsAfterTheDash), and for a folder with no SDK
project (a data-only game with no client half, a package's `game/`, the Sandbox) the host of that
configuration started with `-game <game>` (test: Run_AFolderWithNoSdkProject_StartsTheHostOnIt). `--dry-run`
prints the command instead of running it. `sage new` needs the templates installed (`dotnet new install`, above); `sage run` does not.

`sage-game` makes this:

```
YourGame/                       the game folder, and the client half
    YourGame.Client.csproj      <Project Sdk="Sage.Sdk/<version>">: the engine, Sage.Client and MonoGame
    YourGameClientModule.cs     HUD, screens, anything with a screen
    game.json                   the manifest the host reads
    content/
        data/*.json             records: prefabs, items, materials, scene…
        textures/*.png  audio/*.wav|ogg  models/*.glb  maps/*.map  shaders/*.fx
    .config/dotnet-tools.json   mgfxc, for the game's own shaders
    Simulation/                 the simulation half
        YourGame.csproj         <SageSimulationOnly>true</SageSimulationOnly>: the base engine only
        YourGameModule.cs       your IGameModule
```

The simulation half references the base engine only, so a MonoGame type there is a build error
(SAGE0024) and its rules can be tested headlessly. The client half is the project in the game folder, so
`dotnet run` there starts the game; a dedicated server would leave it out.

**What the SDK gives a project.** `<Project Sdk="Sage.Sdk/<version>">` and a `RootNamespace` are all a
project needs:

- `net8.0` (rolling forward to a newer runtime), nullable, implicit usings, and the engine's namespaces
  as global usings: `Sage.Core`, `Sage.Simulation`, `Sage.Physics3D`, `Sage.Gameplay`, and `Sage.Client`
  in a client half. The ECS is in there: `Entity`, `IComponent`, `ITag`, `Tags`, `Query<…>` and
  `EntityCommands` are `Sage.Simulation`'s (§6).
- The Debug, Development and Shipping configurations. `Release` (what `dotnet publish` builds by default) is
  Shipping and writes `bin/Shipping`, so `{config}` and the packed Player's Shipping host agree (issue #294).
- The engine, **compile-time only**: the assemblies of the `Sage.Player` package's host for your
  configuration, so a game compiles against exactly what runs it, and nothing of the engine is copied
  into your `bin/`, which holds your dll and nothing else. Add other references as usual.
- The Sage generators and analyzers, so `[Record]`, `[System]`, `[PrefabPart]` and friends register
  themselves and every SAGE diagnostic (§10a) applies, SAGE0050 included.
- `bin/<config>/YourGame.dll`, with no target framework in the path, and a check after each build that
  `game.json` loads what was built: every `assembly` and `modules.add` path must exist, and the project's
  own dll must be one of them (SAGE0110, SAGE0111; the message says what to write), and every kit the
  project references (`<SageKit Include="sage.kits.rpg" />`, "Kits" below) is in `"kits"` (SAGE0114).
- Every `.fx` in the project compiled with `mgfxc` to an `.mgfxo` beside it — so it is in a mounted
  folder, and a material names it as `shaders/name.mgfxo` — on Windows; `-p:SageSkipShaders=true` skips it.
- `dotnet run`: the Player host of that configuration with `-game <this folder>`; anything after `--` is
  passed on (`dotnet run -- +sv_cheats 1 +god 1`). `dotnet msbuild -t:SageValidate` runs `sage validate` on
  the folder (§9) with the CLI of that configuration.

Switches, in the `.csproj`: `<SageKit Include="…" />` (a kit, below), `SageSimulationOnly`, `SageStrictSaves` (§10a), `SageDataOnly` (no C#, no
engine references: `sage-game-data`), `SageSkipShaders`, and `SageGameDirectory` (the folder `dotnet run`
passes, by default the project's own when it has a `game.json`).

**In this repository** `games/Hello` is built with the SDK too, as the proof that it works. MSBuild can
only resolve `Sdk="…"` from the .NET SDK or a NuGet feed, so an in-repository game imports the SDK's two
files by path, which is what the attribute does:

```xml
<Project>
  <Import Project="..\..\sdk\Sage.Sdk\Sdk\Sdk.props" />
  <PropertyGroup> … </PropertyGroup>
  <Import Project="..\..\sdk\Sage.Sdk\Sdk\Sdk.targets" />
</Project>
```

From its source tree (`sdk/Sage.Sdk/Sdk`) the SDK references the engine projects in `src/` instead of a
package, and `dotnet run --project games/Hello -c Development` uses `src/Sage.Host`'s build. The Sandbox
and its client half still take the same settings from `games/Directory.Build.props`, which the SDK
replaces for a game that uses it.

### `game.json`

```json
{
  "name": "Your Game",
  "id": "yourgame",
  "assembly": "Simulation/bin/{config}/YourGame.dll",
  "mounts": ["content"],
  "modsDirectory": "mods",
  "scene": "main",
  "sage": "^0.1",
  "modules": {
    "disable": [],
    "add": ["bin/{config}/YourGame.Client.dll"]
  }
}
```

- **`id` is your record namespace.** Everything your game defines is `yourgame:something`, and that is
  how mods and the engine tell your records from `sage:`'s.
- `{config}` is replaced with the host's configuration name (Debug, Development or Shipping; a `-c Release`
  build is Shipping and writes `bin/Shipping`), so one manifest works for all of them. Paths are relative to the game folder.
- `mounts` are folders layered over the engine's own content, **later wins** — which is how a game (or a
  mod) replaces an engine texture without touching it.
- `assembly` is optional: a game made only of data and engine plugins leaves it out.
- `scene` is the scene record every world starts in (§4).
- `title` (optional, issue #342) is a `screen` record shown before the world starts — `"rpg:title"`, the RPG
  kit's — in a host with a window: the world waits behind it, paused, with no scene placed and its rules not
  started, until the player chooses New game (or the console's `new_game`) or loads a save. `-notitle` on the
  host's command line skips it; a test, a server and `sage validate` start at once but still check the name
  (§7 "Menus").
- `modsDirectory` (default `mods`) is the folder of your game's data mods, each a folder with a `mod.json`;
  players add theirs in `user://mods`. `version` (optional, `"1.2.0"`) is what a mod's `gameVersion` is
  checked against. Mods are mounted after your content, so they patch your records as you patch the
  engine's: [docs/MODDING.md](MODDING.md) is what to tell your modders.
- `plugins` (optional) picks which of the engine's plugins the game uses, by id — `"sage.physics3d"`, or
  `"sage.gameplay.*"` for a family — and whatever they require comes with them, so
  `["sage.gameplay.items"]` also brings attributes, combat, characters and physics. Left out, the game gets
  all of them; `[]` is a game with no physics and no gameplay — and no terrain, camera or player input,
  which belong to `sage.streaming` and `sage.gameplay.character`. The console's `plugins` command lists every
  plugin with its version and what it needs, and `plugins <id>` what that one registered.
- `modules.disable` switches off engine modules by class name or plugin id. A game with no AI drops
  `AIModule` (or `sage.gameplay.ai`) and nothing else changes; a name that matches nothing is a warning.
  Factions, quests and dialogue are three plugins (`sage.gameplay.factions`, `sage.gameplay.quests`,
  `sage.gameplay.dialogue`) and any one of them can go on its own: nothing calls into them, a death
  reaches them as a `Died` event, and dialogue treats a missing one as "standing 0" or "not on that quest"
  (test: EachNarrativePluginCanBeSwitchedOffAlone). Content for a plugin that is off — its record types —
  is skipped with a warning; its prefab parts are errors, so leave them out of that game's prefabs.
- `kits` (optional) names the kits the game is built on, by plugin id — `["sage.kits.rpg"]`; see
  "Kits" below. Left out, the game has none.
- `sage` (optional) is the engine versions the game was made for, as a range (`"^0.1"`, `">=0.1 <0.3"`):
  on an engine outside it the game stops at load with both versions in the message, instead of with
  whatever broke first. `[RequiresPlugin("sage", "^0.1")]` on a module says the same for a plugin or kit
  (§4, [RELEASING.md](RELEASING.md) §3); `version` in the console prints the engine's.
- A key the engine does not know is an error, so a misspelt `"mount"` stops the game at once instead of
  quietly loading nothing.

### Kits

A **kit** is the shared rules and screens of a family of games, built on the base engine's public API
and never part of it (issue #27): the base must not assume a genre, so a platformer does not carry a
spellbook. `Sage.Kits.Rpg` is the action-RPG kit (Daggerfall, Morrowind, S.T.A.L.K.E.R.):

| | `sage.kits.rpg` (`Sage.Kits.Rpg`, simulation) | `sage.kits.rpg.client` (`Sage.Kits.Rpg.Client`) |
|---|---|---|
| Casting | the **readied spell** — `world.Ready(e, id)`, `world.Readied(e)` — which the `Cast` button fires; `ready`, `spells` | |
| Spellmaker | `Spellmaker.Compose`, the saved `spellbook`, `spell_make`/`spell_list`/`spell_forget`/`spell_effects`; the `rpg:spellmaker` screen | |
| Things you carry | the two hands, `MainHand` and `OffHand`, as equipment slots; `inv` | |
| Screens | `rpg:spellbook`, `rpg:bag`, `rpg:dialogue` and `rpg:spellmaker` over `SpellbookView`, `BagView`, `DialogueView` and `SpellmakerView`, whose rows are `GameplayPanels`' and the rules' (#350); the `Spellbook`, `Spellmaker`, `Journal` and `Rest` actions with default keys (B, M, J, T) in its `rpg:ui` and `rpg:gameplay` input maps (#354), bound to `rpg:spellbook`, `rpg:spellmaker`, `rpg:journal` and `rpg:rest`; using somebody with a `dialogue` opens `rpg:dialogue` | nothing since #350; a client module may still depend on it |
| Screens from records (#98) | `rpg:inventory` (a grid with weight), `rpg:equipment`, `rpg:loot` (take all), `rpg:topics`: `screen`/`ui_layout` records in the kit's content over `InventoryView`, `EquipmentView`, `LootView`, `TopicsView`; `ItemGrid`, the `rpg_item` record (`grid` footprint, `icon` picture since #346) and the saved `rpg:item_grid` | drawing them is #97's |
| Journal, map, shop (#99, #349) | `rpg:journal` over `rpg_journal` (`JournalView`: a line per quest, stage and objective, the stages it moved on from, tracking), `rpg:map` over `rpg_map` (`MapView`: the scene's `area_map` picture under fog the saved `map_discovery` lifts, markers round the player, north up, zoom and pan, a tracked quest's targets) over the `sage:map_marker` component, and `rpg:shop` over `rpg_shop` (`ShopView`: two grids; at an entity with the base's `merchant` part a move across is a trade, with money, the merchant's purse and refusals in words (#380), and at one without, the `StubPriceRule` shell) | |
| Menus (#342, #339) | `rpg:title`, `rpg:pause` (`"pauses": true`), `rpg:save` and `rpg:load` over `rpg_title`, `rpg_pause`, `rpg_save` and `rpg_load`; `rpg:options` over the base's `ui_options`, with the kit's `ui_option` records; `rpg:controls` | |
| From play (#344, #378) | the `use_screen` part (Use on it opens its screen about it), and any of the base's containers, bodies included, which open the loot screen | |
| Skills and levels (#377) | the `skill` record (a rank and its use-XP, each an attribute; `governing`, `rate`, `growth`, `levelling`, `levelPoints`) and the `levelling` record (`level`, `points`, `rate`, `growth`, `attributeBonus`, `effects`), raised from the base's `attribute_gain`; `SkillRaised`, `LevelledUp`; `Skills.RankOf`, `ExperienceOf`, `Practise`; `skills`, `practise` | |
| Perks and traits (#381) | the `perk` record (`effects`, `requires`, `cost`, `trait`), the `perks` part, the conditions `has_perk`, `skill` and `level`, the action `grant_perk`, `PerkGranted`; `Perks.CanPick`, `Pick`, `Grant`; `perks`, `perk`, `grant_perk`; the `rpg:perks` screen (no key: `ui_open rpg:perks`) | |
| Its words | an optional `rpg_conventions` record: `spellNamespace` (`"custom"`), `castAction` (`"Cast"`), `inventoryGrid` (`[8, 6]`), `lootScreen` (`rpg:loot`) and `perkPoints` (the attribute perks are bought with); the `rpg` string table | |

To build on it, name it in `game.json` and reference it, compile-time only, like the base. With
`Sage.Sdk` that is one item in each project that uses it; in a client half it brings the kit's client
half too, and the kit's namespace becomes a global using:

```json
"kits": ["sage.kits.rpg"]
```

```xml
<!-- Simulation/YourGame.csproj and YourGame.Client.csproj -->
<ItemGroup>
  <SageKit Include="sage.kits.rpg" />
</ItemGroup>
```

The build checks that `game.json` names every kit the project in the game folder is built on
(SAGE0114): a kit that is referenced but not named compiles and is then never loaded. A project
without the SDK (the Sandbox, for now) references the kit's projects by path instead —
`<ProjectReference Include="..\..\src\Sage.Kits.Rpg\Sage.Kits.Rpg.csproj" Private="false" />`, and
`Sage.Kits.Rpg.Client` too in a client half.

The host finds a kit by its id: `sage.kits.rpg` is `Sage.Kits.Rpg.dll` (each part capitalised), looked
for beside your game's assembly, its `modules.add` assemblies and the host itself — the kits ship
beside `Sage.Host` and `sage`, built by `build/Sage.Kits.targets`. It is loaded before your assemblies,
and a host with a window loads its client half, `Sage.Kits.Rpg.Client.dll`, too; `sage validate`, a
server or a test loads the simulation half only. A kit that is not there stops the game with a message
naming the file and where it looked (test: AKitThatIsNotThereIsAnErrorThatSaysWhere). `games/Sandbox`
is built on the RPG kit; `games/Hello` is not, and runs on the base alone
(test: HelloHasNoKitAndTheSandboxHasTheRpgKit).

The kit's screens open on its own default keys (issue #354): its `rpg:ui` and `rpg:gameplay` input maps bind
`Spellbook` (B), `Spellmaker` (M), `Journal` (J) and `Rest` (T) in both contexts, so the key that opens a screen
closes it (test: ABareKitGameHasTheKitsDefaultKeysInBothContexts). For other keys, patch those maps — `{ "type":
"input_map", "id": "rpg:ui", "patch": true, "actions": { "Journal": [ { "key": "K" } ] } }` — as you patch any
record (test: ADefaultMapIsPatchedByAGameLikeAnyRecord); which screen a key opens beyond those is still your
client module's (§7). The kit's words have defaults, and a game that wants others adds one `rpg_conventions` record in its own namespace
(test: AGameChoosesTheNamespaceItsComposedSpellsLiveIn).

**A kit carries content** (issue #98): `[PluginContent("rpg")]` on its module mounts the files its
assembly embeds under `content/` with the record namespace `rpg`, after engine content and before your
mounts — so the RPG kit's screens, layouts, styles and `strings/en/rpg.json` are there whenever the kit
is, and your game changes them with a patch in its own mount, as it patches engine content:

```json
[ { "type": "ui_layout", "id": "rpg:inventory", "patch": true, "nodes": { "hint": { "visible": false } } },
  { "type": "rpg_item", "id": "rifle", "grid": [4, 2] } ]
```

(test: TheKitsContentIsMountedAndAGamePatchesIt). A kit also brings the base plugins it needs — the RPG
kit needs `sage.ui` — even if `game.json`'s `"plugins"` leaves them out
(test: AKitBringsItsContentAndTheBasePluginsItNeeds). Your own kit does the same with
`<EmbeddedResource Include="content/**/*" LogicalName="content/%(RecursiveDir)%(Filename)%(Extension)" />`.

### Running it

`dotnet run` in the game folder, or the host by hand with the folder. The host doesn't build the game,
so build it first (`dotnet build Sage.sln -c Development` for a game in this repository, or the game's own
project); a stale game dll fails with "Could not load file or assembly":

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/YourGame +sv_cheats 1 +god 1
```

Console commands can be run at startup with `+`. `-notitle` starts the world at once, past `game.json`'s
`"title"` screen; `tools/smoke_run.sh` adds it when it is given commands. **Without `-game` the host does not guess**: it runs
a `game` folder beside the executable (how a packaged game ships) or stops with an error that says what
to pass — and, in this repository, lists its games (test: GameManifest_Locate_WithoutAGameIsAnErrorThatListsTheGames).
Until issue #32 a dev build loaded `games/Sandbox` instead, so a forgotten `-game` ran the wrong game
without a word.

**The console** (`~` in a dev build) keeps what you typed: Up and Down walk back through the last 100 lines
and back to what you were typing, and Tab completes a command or cvar name, then a cvar's values (an enum's
names, `0`/`1`) and record ids (issue #299). `autoexec.cfg` in the user folder, if there is one, runs right
after `config.cfg`, so a line in it wins over a saved value. `host_maxfps` (0 to 1000; 0 uncapped) caps the
frame rate when `r_vsync` is off.

### Shipping it

**`sage package`** (issue #293) writes a folder a player runs: the Shipping host as built, with your game
beside it in `game/` — its assembly and `modules.add` dlls in `game/bin`, its mounts and its own mods copied as
folders, and a `game.json` naming the copies with no `{config}` left. Nothing else of your game folder goes in
(no sources, project files, `obj/` or tools), and neither does the `sage` CLI. Run `Sage.Host` in the folder:
with no `-game` it runs the `game` folder beside it. The folder's content is validated where it was written.

```bash
dotnet build -c Shipping                                       # in your game's folder
dotnet msbuild -t:SagePackage -p:Configuration=Shipping        # into bin/package/Shipping (SagePackageDirectory)
sage package <game folder> --out <dir> [--host <dir>] [--config Shipping] [--no-validate] [--no-cook]   # by hand
```

**Cooking** (issue #302). `sage package` also cooks the package's models and textures: each `.glb` gets a
`.sgmesh` beside it (vertex and index arrays as the GPU takes them) and each `.png`, `.jpg`, `.jpeg` and
(since issue #317) `.tga` a `.sgtex` (premultiplied RGBA, or BC1 or BC3 blocks, an eighth or a quarter of the
memory, with the whole mip chain), and the client reads
those in place of the loose files, so a load skips the glTF parse and the image inflate. Nothing names a cooked
file: records and maps keep naming the `.glb` and the `.png`, and the loose files stay in the package. Your own
game folder is never written to by `package`. `sage package --no-cook` (the SDK's `SageCook=false`) leaves them
loose, and `sage cook <game> [--force] [--clean]` cooks a folder in place (what `package` runs on its copy;
`--clean` removes the cooked files). In a source folder a cooked file stands in for its loose one until it is
cooked again, so an edit to the loose file is not seen until you cook again or `--clean`. A cooked file is read
only when its mount is at least as high as the loose file's, so **a mod's loose file still overrides your cooked
one**, and one that cannot be read, or was cooked from a file of another length (folder mounts only), is a warning
once and the loose file is read. Files cooked before phase 4n are of an older version (a `.sgtex` without
mips, a `.sgmesh` without tangents and colours or a second UV set): they are passed over with a warning and
the loose file is read until you cook again. The cook reports what it bought: on the Sandbox's content the test measures
loading at least twice as fast (about 0.05 ms against 10 ms), allocating under half (about 322 KB against 2143 KB)
and textures at a third of the memory or less (about 902 KB down to 217 KB) (tests:
CookWritesACookedFileBesideEveryModelAndTextureAndSkipsWhatIsUpToDate,
APackagesModelsAndTexturesAreCookedBesideTheLooseFilesUnlessCookIsOff,
CookedAssetsLoadFasterWithLessAllocationAndTexturesTakeLessMemory).

BC textures lose a little: pixel art, UI and fonts want their exact pixels. `game.json` says which:

```jsonc
"cook": { "compress": true, "uncompressed": ["textures/ui/**", "*.png"] }   // both optional; compress defaults to true
```

`uncompressed` patterns are globs (`*`, `**`, `?`) on a path inside its mount; those textures cook to exact
premultiplied RGBA. A misspelt key is refused (tests: GameJsonCookSettingsAreReadAndAMisspeltKeyIsRefused,
UncompressedPatternsAreGlobsInsideAMount). Not cooked: the engine's `Content/`, a game's `mods/` (a player's mods are
loose), any other format, and no mipmaps are made. The Sandbox sets no `"cook"` block, so its pixel art is
block-compressed (BC3); say `"uncompressed": ["textures/**"]` there if that shows.

A `sound` asset may be a `.ogg` as well as a `.wav`: it is decoded whole to PCM when it loads (mono or stereo, up
to ten minutes), so a package can ship the smaller file, or read as it plays when the sound says `"stream": true`
(§4, "Sound, music and controls"; tests: AnOggFileDecodesToPcmMonoOrStereo,
AFileThatIsNotOggVorbisIsRefusedWithInvalidData). There is no WAV-to-OGG step.

Only a Shipping host packages: one with the editor or ImGui in it (Debug, Development) is refused, and so is
a game not built in that configuration or an output folder already in the way, before anything is written
(tests: AHostWithTheEditorOrImGuiInItIsRefused,
AGameNotBuiltInTheConfigurationOrAnOutputFolderInTheWayIsAnErrorThatWritesNothing). `tools/smoke_run.sh
--packaged <dir>` runs a package the way a player would, and CI packages the template game on both jobs and
runs it on Linux. A Shipping host reads the same `config.cfg` a dev build archived, so a name in it that only
a dev build has (an editor cvar) is a note in the log, not a warning. Mounts are not zipped yet (#397).

---

### Model formats

Models are binary glTF 2.0 (`.glb`), read at run time; a text `.gltf` is refused. What the engine reads is
one table (issue #321; design/05 "The glTF subset"): a file with a **Rejected** feature does not load, with one
error per feature naming the file and the feature, and what is **Ignored** is read past (a warning where it
matters). Export from Blender as "glTF Binary (.glb)", without compression, with at most four weights a vertex.

| Feature | Support | What it covers |
|---|---|---|
| `glb` | Supported | Binary glTF 2.0 (.glb), buffers embedded |
| `scene-graph` | Supported | Default scene (or every root node), node hierarchy and transforms (baked into the vertices of a rigid mesh) |
| `positions-normals-uv0` | Supported | POSITION, NORMAL, TEXCOORD_0, TANGENT (worked out from TEXCOORD_0 when absent), triangle indices (any index width) |
| `color-0` | Supported | COLOR_0 (vec3 or vec4, float or normalised integer), white without one |
| `texcoord-1` | Supported | TEXCOORD_1: a second UV set (lightmaps), kept beside the vertices as `Uv1` |
| `skinning` | Supported | One skin of up to 64 joints, JOINTS_0 and WEIGHTS_0 (four influences per vertex) |
| `primitive-triangles` | Supported | Primitive modes TRIANGLES, TRIANGLE_STRIP and TRIANGLE_FAN |
| `morph-targets` | Supported | Morph targets (blend shapes): POSITION and NORMAL deltas, named by the mesh's extras.targetNames, at the mesh's weights; a skinned mesh's are animated by clips' weight tracks (TANGENT deltas are not read) |
| `materials` | Ignored | The file's materials and textures: the material record named by whatever draws the mesh decides |
| `animations` | Ignored | Animation clips are read by the animation reader, not the mesh reader; cameras and lights are not read |
| `extra-attribute-sets` | Ignored | TEXCOORD_2 and up, COLOR_1 and up: dropped, with a warning |
| `not-gltf` | Rejected | A file that is not glTF 2.0 (bad header, broken JSON chunk) |
| `text-gltf` | Rejected | Text .gltf with its buffers in separate files |
| `external-buffers` | Rejected | A buffer stored in a separate file (a `uri` that is not a data: URI) |
| `sparse-accessors` | Rejected | Sparse accessors |
| `draco` | Rejected | KHR_draco_mesh_compression |
| `meshopt` | Rejected | EXT_meshopt_compression / KHR_meshopt_compression |
| `required-extension` | Rejected | Any other extension the file lists in extensionsRequired |
| `primitive-mode` | Rejected | Primitive modes POINTS, LINES, LINE_LOOP and LINE_STRIP |
| `more-influences` | Rejected | More than four joint influences per vertex (JOINTS_1 / WEIGHTS_1) |
| `too-many-joints` | Rejected | A skin of more than 64 joints |
| `multiple-skins` | Rejected | More than one skin |
| `no-geometry` | Rejected | A file with no drawable triangle primitive |

The engine names the material a mesh draws with (a renderer's `material`), not the file. A second UV set
(`TEXCOORD_1`) is kept for lightmaps and the colours for a material's `vertexColors`.

## 3. Records: everything your game *is*

A record is a JSON object with a `type` and an `id`, in any `data/**/*.json` file in any mount. Files can
have comments and trailing commas. A record is **content, and read-only at run time**.

```json
[
  {
    "type": "prefab",
    "id": "goblin",
    "components": { "ai_state": {} },
    "parts": {
      "sprite": { "sheet": "goblin" },
      "body": { "shape": "Capsule", "radius": 0.35, "height": 1.8 },
      "character": {},
      "attributes": {},
      "faction": "goblins"
    }
  }
]
```

Five things to know before you write many:

- **Ids are namespaced.** Inside your files a bare `goblin` means `yourgame:goblin`; write `sage:lit_default`
  to reach the engine's. That holds inside a patch of an engine record too: `"sound": "hit_flesh"` in
  your patch of `sage:physical` is *your* `hit_flesh`.
- **`base` inherits** from another record of the same type, and `"patch": true` merges into one that
  already exists — which is how a mod changes one field of yours without copying the file.
- **Every reference is checked** when records load: a reference to something that does not exist is an
  error naming `file:line:column`, not a surprise at run time. So is a value of the wrong kind, and so is
  **a field the type hasn't got** — `"light": { "color": … }` is an error that says `did you mean
  'colour'?`, at any depth, where it used to be dropped without a word. A record with one is skipped.
  A field typed `RecordRef<T>` (an item's `sound`, an attack's `damageType`, a part's `attack`) must
  name a record of *that* type: a sound pointing at a particle says `(… is a particle)`.
- **Prefab bodies are checked at load, not at the first spawn**: every component by id and every field
  it writes, every part by id and its options, the tags, and every record and asset they name — each
  error at its line, with the nearest real name when it looks like a typo. An asset path (a texture, a
  map, a mesh) that is in no mount is a warning in a dev build and an error under `sage validate` (§9).
- **They hot reload.** Save the file and the running game picks it up, and the engine places the scene
  again — without doubling anything and without touching the player. Edit a prefab and what is already
  spawned from it follows too (issue #287): every field the game has not changed takes the new value, a
  component the prefab now writes is added and its tags follow; a value code set, a load laid back or a
  placement overrides is kept. Parts, children and a component the prefab dropped wait for the next spawn.
  Your own record type says what a reload does to what was built from it with
  `[Record("x", Reload = ReloadPolicy.Live)]`, or `NextSpawn`, the default (`RecordStore.ReloadPolicyOf`);
  `prefab` and `scene` are `Live`.

A game can define **its own record types**. A plain class with public fields and an attribute, and that
is all:

```csharp
[Record("quest_board")]
public sealed class QuestBoardRecord
{
    public List<RecordRef<QuestRecord>> Offers = new();   // must be quests
    public RecordId Sponsor;                              // any record at all
    public float RefreshHours = 24f;
}
```

Prefer **`RecordRef<T>`** to a bare `RecordId` whenever a field points at one kind of record. In a file
it is the same string; in code it converts to and from `RecordId`, compares with one, and
`records.Get(offer)` needs no type argument. `RecordStore.Get<T>` is for records that must exist: a
missing one throws in a dev build, naming the nearest id, and only a Shipping build hands back a blank
record (logged once) to keep a player's game running. Ask `TryGet` when absence is normal.
A plugin can check what a field's type can't say — the way `AIModule` checks interrupt names — with
`Records.AddCheck<T>((record, check) => …)` in its `Init`; `check.Error("Tasks[2]", "…")` lands on
that line.

The engine's source generator (`src/Sage.Generators`, which every game project gets) writes the
registration, and your plugin runs it just before its `Init` — so there is no `Register` call to
forget. An assembly with one `[Plugin]` owns what it declares; one with several says which plugin owns
each type, `[Record("quest_board", Plugin = "yourgame.quests")]`, or the build fails (`SAGE0001`). A
type the registration can't create — abstract, private, no public parameterless constructor — is a build
error too (`SAGE0002`).

### Components have ids too

A component is a struct, and it is named by a **stable id** you choose once — not by its C# name, so you
can rename or move the type without breaking a prefab or a save:

```csharp
[Component("yourgame:health")]
public struct Health : IComponent { public float Value; }

[Tag("yourgame:hostile")] public struct Hostile : ITag { }
```

Every `IComponent` or `ITag` struct in your game's assembly needs one, or the build fails (`SAGE0004`);
an id is `namespace:name` in lower case (`SAGE0005`), and one per type (`SAGE0006`). Components belong
to the assembly, not to a plugin, so there is nothing to register. In a prefab, write the id — or a
**bare name, which means your namespace first and then `sage`**: in your files `"health"` is
`yourgame:health` and `"collider"` is `sage:collider` (unless you declared a `yourgame:collider`, which
then wins). Another mod's component is written in full. A name that matches nothing is an error that
suggests the ids it might have meant, and one spelt the old way (`"AIState"`, a C# type name) names the
id to write instead (`"sage:ai_state"`). `ent_types` lists every id there is.

### Describing fields

A field can say what it is for, and the inspector, `ent_dump`, `ent_types`, the TrenchBroom FGD and the
registry dump all use it:

```csharp
[Component("yourgame:health")]
public struct Health : IComponent
{
    [Property(Min = 0, Unit = "hp", Tooltip = "Current hit points", Category = "Vitals")] public float Value;
    public RecordRef<EffectRecord> OnDeath;               // a reference names its record type
}
```

`Min`/`Max` clamp the inspector's drag and reject an out-of-range map key; `Unit` is printed after the
number; `Tooltip` is the hover text and the FGD key's description; an enum field is a dropdown of its
names; a `RecordRef<T>` field is a dropdown of the records of T's type (a plain `RecordId` can say which
type with `[RecordRef("effect")]`). `[AssetKind("texture")]` names the kind of an `AssetPath`. Every part is optional. A range on a field that is not a number or a vector, a
`[RecordRef]` on one that is not a `RecordId` and an `[AssetKind]` on one that is not an `AssetPath` are
build errors (`SAGE0040`–`SAGE0042`).

`rec_list <type>`, `rec_get <type> <id>` and `rec_reload` in the console are how you check what actually
loaded — and `rec_get` is the fastest way to learn a record's fields, because it prints the merged
result with the file each field came from.

### Every record type there is

The ones a game writes, by what they are for; a game may use as few as it likes. Their fields are documented in the design doc named
beside each group; `rec_get <type> sage:<id>` on one of the engine's own is usually quicker.

| For | Types |
|---|---|
| **Things that exist** (05) | `prefab` — components and parts; `scene` — where things start; `tag` |
| **Look** (06, 07, 12) | `material` — shader, technique, params; `sprite_sheet` — frames, direction groups, animation events; `skeleton_sockets` — named places on a model's skeleton (a joint and an offset) that `bone_attachment` follows; `skeleton_map` — which joints of one rig follow which of another, so a model plays another's clips (issue #360); `mesh_lod` — coarser meshes by distance or screen size, and where a mesh stops drawing, named by a mesh renderer's `lod` (issue #305); `decal` — a mark a cue or a damage type leaves on a surface (issue #306); `terrain_material` — up to four ground textures laid by height and slope (issue #307); `water_surface` — how the top of a water volume looks (issue #411). A `material` can name surface maps too: `normalMap`, `specularMap`, `specular`, `gloss`, `emissiveMap`, `emissive`, `vertexColors`, `environmentMap`, `reflectivity` (issue #410) |
| **Sound** (11) | `sound` — the file, gain, limits, rolloff, cone, doppler, whether it streams; `cue` — the moment a sound is asked for; `music` — a streamed track with layers and loop points; `reverb` — a room's sound; `ui_sounds` — what a screen's actions sound like |
| **Levels** (15) | `map` — a `.map` file, its scale and where it stands; `placements` — prefabs at positions, what the editor writes (§8a) |
| **Movement and bodies** (10, 16) | `movement_profile` — speed, jump, eye height, step, swimming, crouch (`crouchTime`, seconds to go down; the character's collider shrinks with it) and its `mode`: `Walk`, `AirStrafe` (GoldSrc bunny-hopping, tuned by `airStrafeSpeed` and `airStrafeAccelerate`), `Fly` or `Noclip` (`flySpeed`; a character's own `mode` overrides its profile's, and the `noclip` and `fly` cheats toggle the player's, issue #267); `physics_layers` — what collides with what; `physics_material` — what a surface is made of: friction, restitution, the `footstep` and `impact` cues (the impact cue is raised where a shot or a swing meets the surface, issue #306), a bullet `decal` and a `penetration` hint, and the brush `textures` it covers (issue #270) |
| **Fighting** (16) | `attack` — reach, damage, timing, viewmodel, and its `delivery`: a swing, a ray or a projectile; `damage_type`; `effect` — what a hit leaves behind; `attribute` — health and the rest; `attribute_gain` — an attribute that rises on a hit, a hurt, a kill, a cast or a use (issue #377; the RPG kit's skills rise by it); `hit_location` and `hitboxes` — where a strike lands on a body; `hitbox_budget` — which creatures' hitboxes are on: those within its `distance` (m, 50) of a player and among the nearest `maxCreatures` (32); the others' are off and a strike there lands on the body; the conventions' `hitboxBudget` names it (`sage:default_hitbox_budget`; patch it, 0 turns a limit off, and naming none keeps every creature's on; issue #273) |
| **Magic** (16) | `ability` — cost, cast time, payload, cues |
| **Carrying** (16) | `item` — what it is, what it weighs, what equipping it does, its `category` and `durability` (wear, breaking; issue #382); `loot_table` — rolls of weighted entries, nested tables and leveled lists (issue #379); `equip_slot` — a slot items can be worn in, from data (issue #384); `encumbrance` — what a carrier's load does to it (issue #384); `merchant` — a trader's currency, purse, mark-up, what it buys, stock and restock (issue #380) |
| **Minds** (16) | `ai_profile` — sight, memory, speeds; `ai_schedule` — the tasks a creature runs, as `[{ "task": "MoveToTarget", "distance": 1.6 }, "FaceTarget", { "task": "Wait", "seconds": 0.5 }]`; `routine` — what it does when and where, `{ "from": 8, "to": 20, "schedule": "work", "at": "forge" }`, named by the profile's `routine` or a `routine` part, walked with `MoveToAnchor`, `FaceAnchor` and `StayAt` (issue 4g-4); `at` is a placement's `name` or a `.map`'s `targetname`, and an anchor not in the world (a far sector, a map target with no prefab) is walked to where the content has it, one in another scene to the door there; a creature with the `offscreen` part keeps its routine while it is unloaded, round the content's walls (static solid boxes taller than a step; a `mover` is a door unless its `nav_door` is `locked`; a `nav_link` is a way across), from the start in sectors nobody has visited, and through a door (a `load_door`, or a map entity with `load_door.scene` and `load_door.entry` keys) when its anchor is in another scene (issue #284); `nav_area` — ground that costs more or less to cross (`cost` per metre, 1 is ordinary ground), the `forbidden` factions that never path through it, and what makes ground this area: its `surfaces` (physics_materials: a road's gravel, a bog's mud) and `water` (every water volume), or a `nav_area` part's box (issue #271) |
| **People** (16) | `faction` — who hates whom; `dialogue` — lines and choices; `dialogue_topic` — a keyword and its answers; `quest` — stages and objectives |
| **Weather and effects** (06) | `weather` — what falls, wind, fog, light, lightning (issue #311); `weather_pattern` — which weather when, by the clock and the region (issue #311); `particle` — emitters (colliding and running a sprite sheet over a life since issue #310), with colours as `"#RRGGBB"`/`"#RRGGBBAA"` or `[r, g, b, a]` 0-255 |
| **Controls** (08) | `input_map` — actions bound to keys and buttons; `rumble` — a pad's vibration |
| **Screens** (13, §7) | `ui_style` — colours, padding, a font, colours per state, a nine-sliced `image`; `ui_layout` — widgets by name, each naming its parent, with bindings; `screen` — a layout, its view-model and its `sounds` |
| **Your game's words** (§3, below) | `gameplay_conventions` — which attribute is life, which tag is death, the default attack, damage type, profiles and AI schedules, the player's faction, what spells cost, the action names |

### Your game's words: `gameplay_conventions`

The engine's gameplay code knows no record id of its own (issue #26). Which attribute kills you when it
runs out, which tag says you are dead, what a swing does when nobody gave the fighter an attack, which
schedule a creature idles with, which faction the player is in, what a spell made in the spellmaker
costs, which buttons swing, use and jump, and which sprite clips a swing plays (`animations`: the clip,
the frame event that lands the blow, the clip it goes back to) — all of it is one record,
`sage:default_conventions` (`engine_content/data/conventions.json`), and code reads that record. To
change a word, patch it from your content:

```json
{ "type": "attribute", "id": "hp", "start": 30, "max": 30 },
{ "type": "gameplay_conventions", "id": "sage:default_conventions", "patch": true, "health": "hp" }
```

`"hp"` resolves in *your* namespace, as every id in a patch does, so that is `yourgame:hp`, and from then
on it is hp running out that kills (test: AGameRenamesHealthToHpByChangingOneRecord). A field set to `""`
means your game has no such thing: no `health`, and nothing dies. Action names are the one exception to
"data only": actions are registered in `Init`, before content loads, so a name here must be one some
module registered — `Actions.Register("Swing", ActionKind.Button)` in your module, then
`"actions": { "attack": "Swing" }` — and a name nobody registered is a load error
(test: AConventionNamingAnActionNobodyRegisteredIsALoadError). Your C# reads the same record:
`world.Conventions().Health`, never `new RecordId("sage", "health")`
(test: NoGameplayCodeNamesAnEngineRecordId). A sheet whose clips are called something else says so here
(test: AGameNamesTheSpriteClipsCombatPlays). `speedAttribute` names the attribute that paces a character's walk and
run (empty: none), so an `encumbrance` level's effect that lowers it slows a heavy carrier (issue #384)
(test: AnOverweightCharacterWalksLessFarInTheSameTicks). The button that fires the readied spell is the RPG kit's
word, not the base's (`rpg_conventions`, §2 "Kits").

### What things *do*: open vocabularies

Where content says what something does — what a quest stage asks for, what a line of dialogue needs
and does, how a spell reaches its target, what an effect does besides changing numbers, what using an
item does, what a creature knows and how it picks a schedule — it names an **entry of a vocabulary**,
and a plugin (yours included) can add entries (issue #28). Each vocabulary has a key that names the
entry inside an object; an entry that takes no settings can be written as its bare name:

| Vocabulary | Where content uses it | The engine's entries |
|---|---|---|
| `quest_objective` (key `kind`, default `kill`) | a quest stage's `objectives` | `kill`, `have`, `reach`, `talk` |
| `condition` (key `condition`, or its id as a property) | a dialogue option's `conditions`; any `requires` | the base's `all`, `any`, `not`, `var`, `time_between`, `weekday`, `date_between` (the last two: the calendar, issue 4g-2), `season`, `moon_phase`, `on_date` (issue #289), `random`, `entity_exists`, `distance_to`, `in_scene`; gameplay's `has_tag`, `lacks_tag`, `is_alive`, `attribute` (an attribute between `min` and `max`, issue #379), `has_item`, `standing`, `quest`; dialogue's `speaker`; the RPG kit's `has_perk`, `skill` and `level` (issue #381); animation's `anim_param`, `anim_finished` and `anim_window` (issue #359) |
| `action` (key `action`, or its id as a property) | a dialogue option's `actions`; any `then` | the base's `fire`, `set_var`, `add_var`, `spawn_prefab`, `destroy`, `teleport`, `play_sound`, `pass_time`, `load_scene`, `save_game`, `log`, `message`, `wait`, `world_speed`, `hit_stop` (issue #283, §5 "Time"); gameplay's `give_item`, `take_item`, `apply_effect`, `set_tag`, `cue`, `change_standing`, `start_quest`, `set_stage`, `finish_quest`; dialogue's `add_topic`; the RPG kit's `grant_perk` |
| `ability_delivery` | an ability's `delivery` (its `targeting` still names one) | `self`, `touch`, `touch_area`, `area`, `projectile` |
| `hit_delivery` | an attack's `delivery` (empty: `sweep`) | `sweep` (a swing: `reach`, `radius`, `arcDegrees`), `ray` (hitscan to `range`, `pellets` rays), `projectile` (a carrier, the `projectile` prefab, flying `range` at `projectileSpeed`, falling at `projectileGravity`, through `projectilePierce` targets) |
| `effect_execution` (key `execution`) | an effect's `executions` | `knockback`, `teleport`, `summon`, `dispel` |
| `item_use` (key `use`) | an item's `uses`, run in order by `world.UseItem` and `use_item` | `consume`, `read`, `cast`, `repair` (`amount`, `upTo`, `slot`; issue #382) |
| `ai_condition` | an `ai_schedule`'s `interrupts`, an `ai_profile`'s `rules` | `SeeEnemy`, `EnemyInMeleeRange`, `NoEnemy`, `in_routine`, … (the twelve the engine senses) |
| `ai_schedule_selector` | an `ai_profile`'s `selector` | `default` (the engine's choice), `rules` |

```json
{ "type": "quest", "id": "errand", "stages": [
    { "id": "go",    "objectives": [{ "kind": "reach", "at": [410, 0, 96], "radius": 3, "place": "the well" }], "next": "speak" },
    { "id": "speak", "objectives": [{ "kind": "talk", "dialogue": "hermit_talk" }], "done": true } ] },
{ "type": "item", "id": "potion_red", "uses": [{ "use": "consume", "effects": ["mend"] }] },
{ "type": "effect", "id": "shove", "executions": [{ "execution": "knockback", "force": 8, "lift": 2 }] },
{ "type": "ai_profile", "id": "nocturnal", "rules": [{ "when": ["is_night"], "schedule": "prowl" }] }
```

Names match ignoring case, `_` and `-` (`"Kill"` is `kill`, `"see_enemy"` is `SeeEnemy`). A name
nobody registered, or a setting an entry does not have, is a load error at its line with the nearest
real one (test: AnUnknownEntryIsALoadErrorThatSaysTheNearestName), and `sage schema` lists every
registered name and each entry's settings in `schemas/vocabularies.schema.json`
(test: TheCommittedSchemasCheckEntriesAndTheirSettings). Dialogue's older `requires` and `then` objects
still work, and mean exactly the engine's conditions and actions above. §6 says how to add an entry.

**Conditions and actions are one language** (issue #89), the base engine's rather than dialogue's, so a
game with no gameplay plugins has it too. Combine conditions with `all`, `any` and `not`; keep numbers
in world variables (`var`, `set_var`, `add_var`: a name nobody set is 0, and they are saved); send an
entity input with `fire`. Each entry can also be written with its id as the property — its value fills
the entry's main setting, or is its settings when it is an object:

```jsonc
"conditions": [ { "all": [ { "has_item": "key_iron" },
                           { "not": { "var": "alarm", "eq": 1 } },
                           { "any": [ { "quest": "thin_the_wood", "atLeast": "report" },
                                      { "var": "guard_bribed", "min": 1 } ] } ] } ],
"actions":    [ { "fire": "hut_door", "input": "Open", "delay": 0.5 },   // target: a name, !subject or !other
                { "add_var": "doors_opened" },                           // "amount": 1 unless it says
                { "set_stage": { "quest": "thin_the_wood", "stage": "report" } },
                { "action": "give_item", "item": "gold", "count": 20 } ]  // the long form still reads
```

**Words for a game with no code** (issue #275). The base also has what a level needs without C#, and
every word that names an entity names it as a wire does: a name, or `!subject` (the one it is about: the
activator, the player in a conversation) or `!other` (whoever is doing it: the relay, the speaker):

```jsonc
"requires": { "all": [ { "random": 0.25 },                                  // one time in four, from the world's own saved stream
                       { "entity_exists": "boss" },
                       { "distance_to": "altar", "max": 3 },                // from the subject, or "from": "<name>"; "min" too
                       { "in_scene": "crypt" },
                       { "has_tag": "alerted", "entity": "guard" },          // gameplay: any entity, the subject by default
                       { "is_alive": "boss" } ] },                           // gameplay: there and not tagged dead
"then": [ { "message": "The floor shakes.", "kind": "Bad", "seconds": 4 },   // a line for the player
          { "log": "crypt: floor shaking", "level": "Warn" },               // a line in the log
          { "wait": 2 },                                                     // the rest of the list, two seconds later
          { "spawn_prefab": "ghost", "at": "altar", "offset": [0, 1, 0], "name": "ghost" },   // or "position": [x, y, z]
          { "destroy": "!other" },
          { "teleport": "!subject", "to": "crypt_exit" },                    // or "position"; "yaw" to turn
          { "play_sound": "bell", "at": "altar", "volume": 0.8 },            // no "at": heard everywhere
          { "cue": "bell_toll", "at": "altar" },                             // gameplay: the cue's sound and particles
          { "set_tag": "alerted", "target": "guard" },                       // gameplay; "on": false takes it away
          { "pass_time": 8 },                                                // game hours, at the tick's end
          { "load_scene": "crypt", "entry": "crypt_in" },                    // no entry: the scene's start
          { "save_game": "checkpoint" } ]                                    // no slot: an autosave
```

`random` draws one number each time it is asked, so put it on wires, relays and transitions rather than on
something asked every frame. `wait` stops the list and runs the rest later with the same subject and other,
in whatever ran it (a relay, a state's `enter`, a dialogue option, a topic), which has moved on meanwhile;
a list waiting when the game is saved finishes after a load (test: AWaitSavedHalfWayFinishesAfterALoad).
A scene change, a save and time passing happen at the end of the tick, like a load door's
(test: LoadSceneGoesThereAndInSceneSaysSo) (test: PassTimeAndSaveGameRunAtTheTickBoundary).
tests/games/scripted-sequence uses them with no C# (test: ATriggerDoorLiftNpcCounterAndRelayRun_InAGameWithNoCode).

`quest`'s `atLeast` holds at that stage, any later one in the quest's list, or once it is finished.
Gameplay's entries come with the plugin that owns what they ask about (`has_item` with items, `standing`
with factions), not with dialogue (test: GameplayEntriesComeWithTheirPluginsNotWithDialogue). Code asks
and does with `Conditions.Evaluate(world, subject, requires)` and `Conditions.Run(world, subject, then)`
(SAGE0124, §10b) (test: AGameWithoutDialogueReadsAndEvaluatesNestedRequires).

**Topics** (issue #93) are Morrowind's way of talking: a keyword whose answer is the first of its
`infos` whose `requires` (one condition; `all` for several) holds, asked about the player with the
speaker as the other. Put the specific answers first and a catch-all last; `speaker` makes an answer one
NPC's, and `add_topic` teaches the player another topic, which they can then ask anybody about:

```jsonc
{ "type": "dialogue_topic", "id": "the_bridge", "keyword": "the bridge", "known": true,   // everyone knows it
  "infos": [ { "requires": { "standing": "guard", "min": 20 }, "text": "Open to you, friend." },
             { "requires": { "quest": "toll", "atLeast": "paid" }, "text": "You paid. Go on." },
             { "requires": { "speaker": "ferryman" }, "text": "Ask the guard.", "then": [ { "add_topic": "toll" } ] },
             { "text": "Closed." } ] }
```

What the player has learnt is saved on them (`sage:known_topics`). A node option with `"topics": true`
opens the speaker's topics. Code (a screen) lists them with `DialogueTopics.Available(world, speaker,
player, list)` and asks with `DialogueTopics.Ask` (SAGE0124) (test: ATopicLearntFromOneNpcIsAskedOfAnother).
Without the dialogue plugin the records are skipped with a warning and nobody has anything to say.

### One placement that differs, and prefabs inside prefabs

A placement (in a scene, a `placements` document, or a prefab's `children`) can change its prefab for
itself alone with `overrides`: component and part bodies merged field by field into a copy of the
prefab. A prefab's `children` are spawned with it, parented to it at `at`/`yaw` in its frame, and
destroyed with it. A prefab that contains itself, or nests deeper than 8, is a load error; `ent_dump`
marks what was overridden with `*` (SAGE0131, §10b; design 05 "As built (overrides and nesting)").

```jsonc
{ "type": "prefab", "id": "cart", "children": [ { "prefab": "lamp", "at": [0, 1, 0] } ] }
// in a scene's "place": a thinner post with a brighter lamp
{ "prefab": "post", "at": [3, 0, 0],
  "overrides": { "parts": { "body": { "radius": 0.25 }, "light": { "range": 12 } } } }
```

**What a save finds again** (issue 4i-3). Everything content places has an id that is the same every run,
so a load places your content as it is now and lays the save over it: a placement you add after a save
is there after the load, and what the player destroyed stays destroyed. A placement's id comes from its
place in its list unless you write one — `"id": "gate-a"`, unique in the list — and then reordering the
list moves nothing. A `.map` entity uses its `id` key, else its `targetname` when that is unique in the
level. A prefab's children are named by their parent (give two children of one prefab different names).
Design 09 "As built (a load that reconciles)".

### Every prefab part the engine provides

A prefab's `components` block sets components directly, by id (`"sprite_renderer": { … }`); its `parts`
block calls these, which is the usual way, because a part does the assembling for you:

| Part | Gives the entity |
|---|---|
| `body` | a collider and a rigid body — `shape` (Box/Sphere/Capsule), `size` or `radius`/`height`, `mass`, `layer`, `trigger`, `contacts` (report contact begin/end), `surface` (a `physics_material`: what it is made of, issue #270). On a prefab's child, a massless body moves with its parent, and under a parent with a mass it is part of the parent's body (one compound; a ray names the child it hit; issue #268) |
| `joint` | a joint from this (dynamic) body to another entity's, its parent's or the world's, in data (issue #245) — `kind` (Ball/Hinge/Fixed/Distance), `target` (an entity name; empty = the parent if it has a body, else the world), `anchor` (in this entity's space), `targetAnchor` (optional; default: the same point, where they stand), `axis`, `swing`, `twistMin`/`twistMax`, `min`/`max` (a hinge's range), `minDistance`/`maxDistance` (degrees and metres), `breakForce`, `drag`; the `Break` input and the `OnBreak` output (SAGE0134) |
| `character` | the kinematic character controller, and with it the ability to walk (and to swim, in water) |
| `water` | a box of water things float and characters swim in — `size` (full extents, centred on the entity; the top face is the surface), `drag` (2, per second), `buoyancy` (2: lift on a submerged body as a multiple of its weight, so a crate floats half under), `current` (m/s); the `OnEnterWater` / `OnExitWater` outputs (issue #262; design/10 "As built (water and swimming)") |
| `water_surface` | beside `water`: its top face drawn rippling, reflecting and seen through, and the view tinted under it — `look`, a `water_surface` record (a bare value: `"water_surface": "lake"`; empty: `sage:water`) (issue #411) |
| `sprite` | a billboard sprite from a `sprite_sheet` — `sheet`, `material`, `size`, `animation` (a clip to loop), or `graph` (an `anim_graph` whose clips are the sheet's: combat's trigger and `hit` event work through it, issue #119) |
| `skinned_mesh` | a skinned model, bent by its joints on the GPU — `mesh` (a `.glb` with a skin), `material` (empty: `sage:lit_default`; its effect needs a `Skinned` technique), `layer` (issue #117) |
| `animator` | plays an `anim_graph` on a skinned model's skeleton — `graph`, `model` (a skinned `.glb`; left out, the `skinned_mesh` part's `mesh`) (issue #118; §5 "Animation graphs") |
| `bone_attachment` | follows a socket of its parent's skeleton — `socket` (a bare value: `"bone_attachment": "hand_r"`), or `bone` (a joint by name), `offset`, `angles`; sockets are `skeleton_sockets` records (issue #120) |
| `aim_ik` | turns the spine, neck and head toward `AimIk.Pitch`/`Yaw` after the animation — `joints` (hips up: `joint`, `weight`, `pitchLimit`, `yawLimit` in degrees), `weight` (issue #120) |
| `foot_ik` | plants the feet on the ground under them and lowers the hips — `pelvis`, `left`/`right` (`hip`, `knee`, `foot`), `footHeight`, `rayAbove`, `rayBelow`, `maxPelvisDrop`, `weight` (issue #120), `maxFootTilt` (degrees a planted foot turns to the ground's slope, 30) and `pelvisSmoothing` (seconds the hips take to a new height, 0.1) (issue #361) |
| `look_at_ik` | turns the neck and head toward a target or a point — `joints` (as `aim_ik`'s), `eye`, `weight`, `maxAngle` (degrees, 120: further round, it looks ahead), `smoothing` (seconds, 0.15); code sets `LookAtIk.Target` or `Point` (issue #361) |
| `hand_ik` | reaches a hand to a joint of its own skeleton (a grip) or to an entity — `left`/`right` (`shoulder`, `elbow`, `hand`, `bone`, `offset`, `weight`), `weight`; code sets `HandIk.LeftTarget`/`RightTarget` (issue #361) |
| `footsteps` | a step every `stride` metres it walks on the ground (0: only on its animation's events), each raising the `footstep` cue of the `physics_material` underfoot (a ray `reach` metres below the feet) and sending `Footstep` (issue #270); a character that jumps or lands faster than `landSpeed` (2.5 m/s) raises the surface's `jump` and `land` cues; `event` (`footstep`), `left` (`foot_left`) and `right` (`foot_right`) name the animation events that are steps, `""` turns one off (issue #327) |
| `ragdoll` | falls as a ragdoll — on death, a hit, the `Ragdoll` input (an impulse "x y z" as its parameter) or from code — with the bodies and joints of its model's `ragdoll` records, settles (the `OnSettled` output) and gets back up (the `GetUp` input) — `record` (one `ragdoll` record; empty: every one for the model), `onDeath` (true), `hitImpulse` (N·s), `getUpAfter` (seconds after it has come to rest; 0 = only when told; never once it has died), `getUpBack`/`getUpFront` (the animator states it gets up with, `getup_back`/`getup_front`), `getUpFade` (issues #246–#249, SAGE0134; design/12) |
| `viewmodel` | first-person arms on a camera, drawn only from its first-person rig — `record` (a `viewmodel` record; empty: gameplay's attack in hand chooses), `enabled`, `fovY`, `near`, `far` (issue #121) |
| `light` | a lamp — `colour`, `range` in metres, `intensity`, `off` to start it dark (06 §3.9; `TurnOn`/`TurnOff`/`Toggle` switch it, issue 4h-7); `pattern` (a flicker: Quake letters `a`–`z`, or a preset such as `torch`, `candle`, `pulse`, `strobe`, `fluorescent`) at `patternRate` letters a second, changed by `SetPattern`; `cone` and `innerCone` (degrees) make it a spot light along the entity's forward (issue #314); `shadows` makes it cast shadows (issue #315); `baked` bakes it into a level's lightmap (issue #313) |
| `brush` | a block-out solid the editor draws (issue #61): a static convex collider and a mesh per material — `shape` (Box/Wedge/Cylinder; a wedge is a ramp rising to its north, -Z, side), `size` (metres; 2 2 2), `sides` (a cylinder's, 3–64; 12), `material` (the whole brush; empty: `sage:lit_default`), a material per face (`top`, `bottom`, `north`, `south`, `east`, `west`, and a cylinder's `side`), `textureScale` (metres per repeat; UVs are laid in world space), `layer`, `surface`. The entity's position is the middle of its floor, and a placement's scale multiplies `size`. The engine's prefabs `sage:brush`, `sage:wedge` and `sage:cylinder` are one of each |
| `mover` | geometry that slides — `open`, `seconds`, `closeAfter` (F17), `onBlocked`: `Reverse` (default), `Stop` or `Crush` when something it can't push is in the way (#260); `angle`, `axis` (default up) and `pivot` swing it on a hinge, in its own frame (`"angle": 90, "pivot": [-0.5, 0, 0]` is a door hinged on its west edge); `path`, stops after the shut one (a lift's floors, the last is open), `speed` instead of `seconds` (m/s, or deg/s for a door), `locked` (#266) |
| `ladder` | makes its trigger volume a ladder the character controller climbs — `facing` (yaw in degrees of the side a climber stands on, on top of the entity's own: 0 faces -Z, 180 faces +Z), `speed` (m/s; 2.5). Pushing toward the rungs climbs, pulling away climbs down, Jump lets go, and a ledge within step height of the top is stepped onto. The volume is a `body` with `"trigger": true`, or a `"trigger" "1"` brush entity (issue #263) |
| `nav_door` | beside a `mover`: `locked` — creatures do not open it, and while it is not fully open it is a wall to the planner (without it a creature opens a door in its way and waits, #265) |
| `nav_link` | an off-mesh link from here to `end` (metres, world axes) — `kind` (`Walk`, `Jump`, `Drop`, `Ladder`, `Teleport`), `twoWay`, `cost`, `startDisabled`; `Enable`/`Disable` switch it; on a prefab of your own a map places it with `"nav_link.end" "2 -3 0"` (#265) |
| `nav_area` | a box of ground that is a `nav_area` — `area`, `size` (full extents, centred on the entity); it wins over water and over what the ground is made of, and where two overlap the dearer wins (#271) |
| `audio` | a sound it makes on its own — `sound`, `loop`, `volume`; a `sound` with a cone is heard best along the entity's forward |
| `reverb_zone` | a box in which the listener hears a `reverb` preset — `reverb`, `size` (full extents, centred on the entity), `offset`, `priority` (higher wins where zones overlap), `fade` (seconds to blend, 0: 1 s); on a map brush entity the brushes give the size (issue #329) |
| `particles` | an effect it gives off — `effect` |
| `attributes` | health and the rest, from `attribute` records |
| `melee` | an attack it can make — a swing, a shot or a throw, as the attack's `delivery` says |
| `abilities` | spells it can cast |
| `effects` | effects already on it |
| `inventory` | somewhere to put things — `capacity` (kg; 0 = unlimited), `items` (each `item`, `count` and, for one unlike the rest, `instance`: `name`, `effects`, `condition`, `charges`; issue #383), `encumbrance` (an `encumbrance` record, issue #384) |
| `container` | makes it a container you open with Use, adding an inventory if it has none — `locked`, `key` (an item), `respawn` (game hours; 0 = never), `owner` (an entity's name) and `faction`; taking from an owned one raises `Stolen` (issue #378) |
| `loot` | rolls a `loot_table` into its inventory — `table` (a bare value: `"loot": "goblin_loot"`), `when` (`death`, the default, or `spawn`) (issue #379) |
| `merchant` | makes it a trader — a `merchant` record (a bare value: `"merchant": "trader"`), its purse and stock saved with it (issue #380) |
| `pickup` | makes it something you can pick up — `item`, `count`, `instance` (issue #383) |
| `faction` | who it belongs to |
| `dialogue` | something to say |
| `timer` | a timer that fires `OnTimer` — `interval`, `spread` (± seconds, at random), `repeat`, `startOn`, `seed`, `realTime` (count real seconds through a pause, a hit-stop and any world speed, issue #283), `randomMin`/`randomMax` (each wait anywhere between them, instead of `interval` and `spread`); inputs `TimerStart`, `TimerStop`, `TimerReset`, `TimerFire` (fire now) and `TimerAdd` (seconds onto the wait left; #281) (issue #90) |
| `tween` | something a `TweenTo` wire moves, turns or scales — `channel`, `target`, `relative`, `duration`, `ease`, `realTime` (as a timer's, issue #283), `loop` (`Once`, `Restart` or `PingPong`) with `loops` (plays in all; 0 = for ever, `OnTweenLoop` between them), `sequence` (TweenTo parameters that `TweenPlay` plays one after another, `OnTweenStep` as each arrives; #281) (issue #90) |
| `logic_relay` | a relay: `Trigger` asks `requires`, runs `then` and fires `OnTrigger` — `requires`, `then`, `startDisabled`, `sameTick` (issue #91) |
| `logic_counter` | a counter — `start`, `min`, `max` (no limits while max ≤ min), `startDisabled`; `Multiply`, `Divide`, `SetMaxValue` and `SetMinValue` besides `Add`/`Subtract`/`SetValue` (#281) (issue #91) |
| `logic_compare` | a comparison — `value`, `compareValue` (issue #91) |
| `logic_branch` | a remembered true or false — `value` (issue #91) |
| `math_remap` | a number from one range to another — `inMin`, `inMax`, `outMin`, `outMax`, `clamp`, `ease` (issue #91) |
| `logic_multisource` | HL1's multisource, an AND gate — `sources` (1 to 32); `SetSource n`, `ClearSource n`, `ToggleSource n`, `Reset`, `Test` → `OnAllSet`, `OnNotAllSet`, `OnTrue`/`OnFalse` (issue #281) |
| `logic_case` | Source's logic_case — `cases` (up to 16), `choices`, `seed`; `InValue v` → `OnCase01`…`OnCase16` (first match, as text or as a number) or `OnDefault`; `PickRandom` / `PickRandomShuffle` (no repeats until all are dealt) from its own saved stream (issue #281) |
| `logic_auto` | `OnMapSpawn` once, the first tick after it is placed; a load does not fire it again (issue #281) |
| `trigger` | on a trigger volume (`body` with `"trigger": true`): trigger_once / trigger_multiple — `once`, `wait`, `requires` (asked of what entered, as subject), `startDisabled`; `OnTrigger` on entry with what entered as activator; `Enable`/`Disable`/`Toggle` (issue #281) |
| `spawner` | env_entity_maker / point_template — `prefab`, `place` (placements with their wires, measured from the spawner), `limit` (0 = none), `uniqueNames` (`#n` on each spawn's names, and the template's wires between its members follow), `startDisabled`; `Spawn` → `OnSpawned` with the first thing spawned as activator (issue #281) |
| `calendar_event` | fires `OnCalendarEvent` on the day its `calendar_event` record falls — `event` (issue #289; §5 "Days, seasons and the moon") |
| `quest_watch` | fires `OnStageChanged` / `OnQuestFinished` when its quest moves — `quest` (issue #91) |
| `state_machine` | runs a `state_machine` record — `machine` (issue #92; §5 "State machines"); the engine's `sage:logic_state_machine` prefab is this part with no machine, for a placement to name one (issue #280) |

Fifty-two here (the camera parts are in §4; the RPG kit adds `use_screen` and `perks`). `ent_types` in the console lists each with its options, the plugin
that declares it and what it runs after — the options *are* the part's public fields.

**A part is a declared class**, like a record type (issue #17): its public fields are its options, and
`[PrefabPart]` registers it for your plugin before your `Init` runs — there is no call to make.

```csharp
// "glow": { "colour": [1, 0.5, 0.2], "range": 6 }
[PrefabPart("glow")]                          // Plugin = "yourgame.x" when the assembly has several
public sealed class GlowPart : IPrefabPart
{
    public Vector3 Colour = Vector3.One;
    public float Range = 6f;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Range <= 0f) { ctx.Error("needs a positive \"range\""); return; }   // "<prefab>: glow: ..."
        ctx.World.Add(ctx.Entity, new PointLight { Colour = Colour, Range = Range, Intensity = 1f });
    }
}
```

- A new instance is read from the prefab for every entity, so `Apply` can treat its fields as this
  entity's options. `{}` means "this part, with the defaults".
- A part that takes one value can say so: `[PrefabPart("faction", Shorthand = nameof(Id))]` makes
  `"faction": "beasts"` mean `"faction": { "id": "beasts" }`.
- **What a part needs besides its options** — the renderer, a service — it asks `ctx.Get<T>()` for,
  under the same rule as `ModuleContext.Get` (the host's, or a plugin yours depends on). It does not
  capture a module's fields: a part is a declaration, with no module instance behind it. The Sandbox's
  `box_mesh` does this for the renderer.
- **Order:** parts run after the parts their attribute names in `After`, and otherwise in id order —
  never in the order the prefab writes them, which a `base` prefab's merge would decide for you.
  `pickup` runs after `sprite` and `body` because it adds its own only when there are none.
- **One id, one part:** a second part with an id already taken is an error at startup, naming both
  (and `SAGE0012` at build time within one assembly).

The Sandbox has two of its own: `hop` (a creature that bounces, a few lines, in its simulation half) and
`box_mesh` (a plain box mesh, in its *client* half, because building a mesh needs a renderer). A part
whose plugin is missing (a mesh part in a headless run) is declared `Prefabs.Optional(name)` by the half
that is always there, so the same prefab loads with no renderer at all.

### Editing records in VS Code

Open the repository folder in VS Code and every record file (`data/**/*.json`, in engine content, a
game or a mod) is checked as you type, with no extension to install:

- **Autocomplete** for a record's `type`, its fields, component ids and part ids under a prefab's
  `components` and `parts`, enum values, and the ids content defines wherever a field names a record
  (`"base"`, `"sound"`, `"schedule"`…), in full (`"sandbox:creature"`) and bare (`"creature"`).
- **Hover** shows a field's tooltip, unit, range, type and default, from its `[Property]`: over a
  `point_light`'s `range`, "Where the light fades to nothing. Unit: m. At least 0. (float, default 0)".
- **Red underlines** under a misspelt field (`"rnage"`), a component or part that does not exist, a value
  of the wrong type or out of range, and an id nothing defines — before the game runs. `sage validate`
  (§9) is still the final word; the schemas catch the same typos sooner.

It works through two committed pieces: [`schemas/`](../schemas), JSON Schemas generated from the
metadata table and the loaded content, and [`.vscode/settings.json`](../.vscode/settings.json), which maps
`schemas/record.schema.json` onto `**/data/**/*.json` and opens those files as *JSON with comments*
(`jsonc`), so the `//` comments record files use are not errors. A file needs no `"$schema"` of its own
(a record may still carry one, and the loader ignores it; an array file has nowhere to put it).

The schemas come from `sage schema`, which boots each game headlessly as `sage validate` does, and
writes one schema per record type, the prefab `components` and `parts` tables, and `ids.schema.json`
with every id the content loaded — mods included when you mount them:

```bash
dotnet build Sage.sln -c Development -p:SageSkipShaders=true
src/Sage.Cli/bin/Development/net8.0/sage schema games/Sandbox games/Hello tests/games/scene-only tests/games/camera-cut tests/games/scripted-sequence tests/games/topics tests/games/skeletal tests/games/weapons tests/games/saves --out schemas   # this repository's
src/Sage.Cli/bin/Development/net8.0/sage schema games/YourGame --mods mods/better_swords --out schemas
```

`sage schema` also writes `game.schema.json` and `mod.schema.json`, the keys `game.json` and `mod.json` may have, and
`.vscode/settings.json` maps them onto those files: a misspelt key is underlined as the loader would refuse it.

**Regenerate after adding a field, a component, a part or a record**, or the new name is underlined as
unknown; CI regenerates `schemas/` and fails when the committed copy differs. For your own game, write
them into its folder (`--out games/YourGame/schemas`) and point a `json.schemas` entry at that folder
instead. A part only a client half declares (the Sandbox's `box_mesh`) is described from that half's
built assembly, which is read, not run; before the client is built its options are left unchecked.

What a schema cannot know: the loader matches field names ignoring case, the schemas only in the camel
case content is written in (`"range"`, not `"Range"`); and a bare id means the file's own namespace, so
the id lists hold every bare name and a bare id that exists only in another namespace is not underlined.

---

## 4. From empty to walking about

Four things, in this order, and you can walk around your own world.

**1. A world, with ground.** Your module's `OnWorldCreated` is where a world is furnished. Terrain comes
first, because things are placed on top of it. The terrain is the `sage.streaming` plugin's, and a player
to control and look through is `sage.gameplay.character`'s, so a game that uses them says so — then a
`game.json` that leaves one out stops at boot with the reason, instead of crashing here:

```csharp
[Plugin("yourgame", "0.1.0")]
[RequiresPlugin("sage", "^0.1")]                  // the engine versions it is made for: `sage` is the engine
[RequiresPlugin("sage.streaming", ">=0.1")]
[RequiresPlugin("sage.gameplay.character", ">=0.1")]
public sealed class YourGameModule : IGameModule
...

public void OnWorldCreated(World world)
{
    var terrain = world.Resources.Get<Terrain>();
    terrain.Generator = new YourGenerator();      // ITerrainGenerator: you fill a heightfield
    terrain.Seed = 1;
    terrain.Load(SectorCoord.Zero);

    world.AddSystem(new YourOwnSystem(world));    // its id and phase are on the class: see §6
}

// The game's rules for each world. The engine asks once every module has furnished the world, places
// the start scene, then starts them.
public GameRules CreateRules(World world) => new YourRules();
```

**Your generator runs on worker threads** (#277): streaming generates the sectors just past the ring, and
coarse 17x17 copies of the far ring (`stream_far_radius`, default 4 sectors), on the thread pool, so
`Generate` must be a function of its arguments and fill the heightfield at `heights.Spacing`, whatever its
resolution. Make it an `ITerrainSampler` too — `SampleHeight(absoluteX, absoluteZ, seed)`, the same function
`Generate` uses, from `corner + index * spacing` in doubles — and the lighting has no seam where sectors meet.
A prefab placed by a streamed scene is drawn in the far ring if it says how: `"far": { "size": [6, 18, 6] }`
(a box standing on the ground) or `"far": { "mesh": "models/tower_far.glb" }`; raise your camera's `far` to see
that far. `stream_load_budget` (sectors made live a tick while walking) and `stream_jobs` tune the rest.

**More than the player can keep the world loaded** (issue #290, SAGE0129). The player is always a streaming
source; tag anything else `streaming_source` and it keeps the cvars' rings around itself too, or give it the
`streaming_ring` part (`"streaming_ring": { "radius": 0, "farRadius": 0 }`: `radius` 0 to 8 sectors, default 1;
`farRadius` 0 to 16, default none) for rings of its own — on the player, that replaces the cvars'. Where rings
meet the larger wins, and the origin always follows the player. A companion gets the `follower` part
(`"follower": { "distance": 2 }`): every journey — a door, fast travel, `warp` — stands each follower behind the
player in rows, facing the same way, and it stays live where it arrived.

**Interiors that keep going while you are out** (issue #291, SAGE0129). A scene with `"live": true` is held when
the player leaves it instead of going to sleep: the dungeon's skeletons go on patrolling, its crates go on
falling, and going back takes it up as it is. An exterior is held only while the player is inside an
interior. `space_live_max` (default 4) bounds how many are held, the one left longest ago going to sleep
first; 0 is the old swap. `"environment": { "gravityScale": 0.25 }` gives a scene its own gravity, for its
bodies and characters, even while it is held beside another. Followers come through doors with the player.
`spaces` in the console lists what is held; a save keeps it (a held streamed exterior sleeps after a load
until you go back). Spaces share one world and one physics simulation, kept apart by their coordinates, so
put them far apart.

A world resource your module installs goes in with `world.Resources.Add(...)`, which refuses a second
one of the same type; `Replace` is for swapping one on purpose, and disposes the old.

**2. Rules.** `GameRules` is an abstract class with four hooks, and a game overrides the ones it cares
about. Return yours from your game module's `CreateRules`, as above; without one the engine uses
`DefaultGameRules`. By default `OnWorldStarted` calls `SpawnPlayer`, and `SpawnPlayer` spawns the
scene's `player` (step 4) — so a game only overrides them to do something else:

```csharp
public sealed class YourRules : GameRules
{
    public override void OnWorldStarted(World world)
    {
        base.OnWorldStarted(world);                        // the scene's player
        world.Say("Welcome.", MessageKind.Good, 5f);
    }

    // Something's health ran out. The engine raises a `Died` event (a system of yours can read it too);
    // factions and quests have already counted the kill when this is called.
    public override void OnEntityDied(World world, Entity victim, Entity killer)
    {
        // Back to the start: where the scene puts the player, in the simulation's frame.
        if (world.PlayerStart() is { } start) world.Teleport(victim, Transform.At(start));
    }

    public override void OnLoaded(World world) { }          // after a save is loaded
}
```

**3. A player.** It is an ordinary prefab with a `character` part, tagged `PlayerControlled`:

```json
{
  "type": "prefab",
  "id": "player",
  "tags": ["PlayerControlled"],
  "parts": {
    "character": { "layer": "player" },
    "attributes": {},
    "inventory": { "capacity": 60 }
  }
}
```

**Give the character a layer.** A character sweeps against everything *except its own* layer — which is
what lets creatures pass through one another — and terrain collision lives on `default`. Leave a
character there and it walks through the world and falls for ever. The engine warns about it now; that
warning exists because this example fell through its own terrain for an hour.

That tag is what makes the rest of the engine treat it as *the* player: **it gets a camera in its head
automatically** (the character plugin spawns a camera entity from the engine's `sage:player_camera`
prefab, with a first-person rig following the player, and saves it with the player), the cheats act on
it, and the HUD reads it. You do not write a camera. To change the player's view, patch that prefab
(`"camera": { "fovY": 70 }`, or `"third_person_rig": { "distance": 4 }`); to ask whether the screen is
looking out of the player's eyes (a crosshair, a viewmodel), `world.MainViewRig() == CameraRigKind.FirstPerson`.
The player's camera also has a third-person rig over the shoulder that keeps out of walls, switched with the
`ToggleView` action (V, or the right stick's button); the choice is saved. The first-person view does not
draw the player's own body, so give the player a body (a `sprite`, a mesh) and it shows in third person.
**First-person arms** (issue #121, SAGE0126) are a `viewmodel` record — skinned arms modelled in view
space (the eye at the origin, looking down -Z), the `anim_graph` that animates them, a weapon on one of
their `skeleton_sockets` — named by the attack in the player's hands:

```json
{ "type": "viewmodel", "id": "sword_arms", "model": "models/arms.glb", "graph": "arms",
  "weapon": "models/viewmodel_sword.glb", "socket": "hand_r", "offset": [0, 0, 0] },
{ "type": "attack", "id": "sword_swing", "arms": "sword_arms", "viewmodel": "sword_fp" }
```

The player's camera shows them (its `viewmodel` part: `fovY`, `near`, `far` of their own) only in first
person, drawn after the world with depth cleared, so they never sink into a wall. A swing sets the graph's
`attack` trigger and the `Reload` action (R) its `reload` trigger; ammunition is yours. An attack with no
`arms` keeps its sprite `viewmodel`, which the game's HUD draws (`Viewmodels.IsDrawn(world)` says when
not to). The Sandbox's `tools/make_mannequin.py` generates a placeholder pair (and the box mannequin of §5's
"Animation graphs").
To place something where the player is looking from — a sound, an effect, a spawn in front of them —
ask `world.TryGetMainView(out var view)`: the screen's view this frame (the player's camera, a cut, the
editor's free camera), with its position, rotation and projection. `ActiveCamera` holds the same pose.

A camera of your own — a fixed security camera, a top-down or 2D view, a scripted shot — is an entity
with a `camera` part (issue #76; its options are the `sage:camera` component's fields):

```json
{ "type": "prefab", "id": "overhead",
  "parts": { "camera": { "projection": "Orthographic", "orthoHeight": 30, "priority": 10 } } }
```

Place it like anything else; it looks down its -Z from where it stands. Of the enabled cameras drawing
to the screen the highest `priority` wins, and one drawing to the screen takes over from the
player's camera until you disable it (`"enabled": false`, or `Camera.Enabled` from code). `target`
names a render target instead of the screen, `viewport` a part of it (0..1 from the top-left), and
`fovY` is in degrees. Games that name these types in C# opt in to SAGE0123 (§10b).

**Split screen** (issue #323): cameras on one target compete per `slot` (0 to 15), so two players' cameras, each with its own `viewport`, both draw; the screen's lowest slot is the main view. Every view gets the whole pass set, its own sun shadows included (up to `r_shadow_views`, 4, at `r_shadow_view_size` texels a cascade for views other than the main one), and a partner's first-person camera draws its own viewmodel. A camera's `noShadows`, `noViewmodel`, `noSky` and `noDebugLines` turn those passes off for its view — a cheap security monitor, a minimap.

Every view is drawn (issue #77): a camera whose `target` is `"minimap"` draws into a render target of that
name, 512×512 unless code declares another size (`Renderer.DeclareTarget("minimap", 256, 256)`), and a
material shows it with `"params": { "Albedo": "rt:minimap" }` — a mirror, a security monitor, a map on a
table; code puts it on the HUD with `Renderer.FindTarget` and `UiDraw.Image`. Render targets draw before
the screen, so the screen sees this frame's picture.

**Render passes** (issue 4h-1, SAGE0130) are how C# draws something of its own. A pass is a class
declared with its id and its stage, added in your module's `Init` (adding it later is SAGE0020):

```csharp
[RenderPass("mygame:tint", RenderStage.PostProcess)]
sealed class TintPass : IRenderPass
{
    public void Draw(RenderContext ctx) => ctx.DrawFullScreen(ctx.Target("tint_source"));
}
// in Init:  ctx.Get<RenderPasses>().Add(new TintPass());
```

The stages draw in this order: `Shadow` once; then for every view `Opaque`, `AlphaTested`, `Sky`,
`Transparent` and `Debug`; then `PostProcess` on the screen; then `Overlay`, in the Overlay phase. The
engine's own passes are `sage:opaque`, `sage:alpha_tested`, `sage:transparent`, `sage:debug` and `sage:ui`
(the game's UI), so `After = new[] { "sage:ui" }` draws over the HUD. Within a stage, `After`/`Before`
decide, and passes nothing orders keep the order they were added in. A cycle, an id taken twice, or an
`After` naming a pass nobody added (write `"?id"` for an optional plugin's) stops the game at boot.
A pass's `Extract(RenderContext)` runs after the cameras each frame and can add views (`AddView`, which
the world's meshes and sprites are then drawn into) and items (`AddItem`). `r_passes` lists them all.

**Sun shadows** (issue 4h-4, SAGE0130) are the engine's `sage:shadow` pass: `r_shadows 1` turns them on.
They are cascaded (issue #315): the view's first `r_shadow_distance` metres (150) are cut into
`r_shadow_cascades` maps (1 to 3, default 3), finer near the camera, each `r_shadow_size` texels a side
(2048; less if together they would pass 4096). The sun's direction snaps to `r_shadow_sun_step` degrees
(0.25) for its shadows, so their edges hold still as the sun moves. Opaque meshes, terrain and skinned
meshes cast, and so do alpha-tested materials and sprites, cut out by their texture's alpha (a leaf's
shadow has holes); particles and decals do not. A material that should let the light through says
`"castShadows": false`. A custom alpha-tested effect needs a `ShadowCasterAlphaTest` technique of its own
to cast. Lamps cast shadows too, when they ask (below). How dark they are is the sky's `shadow` key (none once the sun
is down); a world with no sky gets full shadows under the default sun.

**The sky and fog** (issue 4h-5, SAGE0130) come from the world's `sky` record. While it has one, the
engine's `sage:sky` pass draws a gradient from each key's `horizon` to its `zenith` behind everything,
hazed to the `fog` colour along the horizon, with the sun's disc and, once the sun is down, stars.
`"fogMode": "Exp2"` on the record gives exp² fog instead of linear (a soft onset, then closing in fast);
it is complete at `fogEnd` like linear fog unless a key sets `fogDensity` (per metre). Opaque and
alpha-tested things wholly past where fog is complete are not drawn, nor are particles (issue #310). A world
with no sky keeps its clear colour and linear fog, as before.

The sky can have more (issue #320): a `moon` picture (`moonSize` in degrees) whose phase follows the calendar's
`moonCycle`, `clouds` (a tiling grey picture; `cloudScale`, `cloudSpeed` in repeats an hour, `cloudDirection`)
shown as far as the weather's `cloudCover` says (0.3 unless a weather sets it), stars that turn once a day about
an axis `starTilt` degrees up, and `hazeAbove`, the fog colour left over the sky above the haze band
(`hazeBand`). A sky that names none of them looks as before.

**Post-processing** (issue 4h-6, SAGE0130) needs no C#: a `post_effect` record names a material whose
effect reads the picture as `Source`, runs by `order` and is switched by a cvar. The engine's are
`sage:grade` (colour grade and exposure, with a night tint from the sky) and `sage:vignette`, drawn by
the `sage:post` pass (PostProcess) when `r_post` is 1; `r_scale 0.5` draws the world at half size with
big pixels, and the UI stays sharp. A game's own PostProcess pass draws after the chain, on the screen.
`r_hdr` (a half-float scene, tonemapped), `r_bloom` and `r_aa` (`off`, `fxaa`, `msaa2`, `msaa4`, `msaa8`) are
off by default and each turns the chain on by itself (issue #316); bloom and the tonemap run before your
effects and FXAA after them. A `post_effect` with `"depth": true` is handed the scene's depth as `SceneDepth`
(with `DepthParams` to turn it into metres): the Sandbox's `haze` effect, switched by its own `r_post_haze`, is
the example.

```json
{ "type": "post_effect", "id": "sepia", "material": "mygame:post_sepia", "order": 150, "cvar": "mygame_sepia" }
```

**Surfaces** (issue #410, SAGE0130). A `lit.fx` material can name a `normalMap` (tangent space, green up),
a `specularMap` with `specular` (0 to 4, 0 = matt) and `gloss` (0 to 1), an `emissiveMap` times `emissive` (a
colour that glows unlit), `vertexColors` (multiply by the model's `COLOR_0`) and an `environmentMap` (a
panorama) with `reflectivity`. A map left out changes nothing, so older materials draw as before. An
`emissiveMap` without an `emissive` colour, a `reflectivity` without an `environmentMap`, or `params` that set
one of these names are load errors. Meshes built in code (brush levels, terrain) have no tangents, so a normal
map does nothing on them.

**Level of detail** (issue #305). A `mesh_lod` record lists coarser meshes and when they take over, by
`distance` (metres) or `screenSize` (the share of the view's height), plus `cullDistance` or `cullScreenSize`
where the mesh stops drawing and a `hysteresis` band (0.1) so it does not flicker at the switch; a mesh
renderer names it with `"lod"`. `r_stats` counts `lod culled` and `lod lowered`. Code can also give each sort
layer a draw distance (`RenderEnvironment.LayerDrawDistance`).

**Decals** (issue #306). A `decal` record (`texture`, `size`, `lifetime`, `fade`, `colour`, `randomRotation`,
`reach`) is laid by a cue that names it, or by a damage type's `decal` on the wall behind what a hit hurt. A
weapon's shot that meets a surface raises its `physics_material`'s `impact` cue, so a bullet hole belongs to the
plaster, not the gun:

```json
{ "type": "decal", "id": "bullet_hole", "texture": "textures/bullet_hole.png", "size": 0.12, "lifetime": 60 },
{ "type": "cue", "id": "bullet_impact", "decal": "bullet_hole", "particles": "dust_puff" },
{ "type": "physics_material", "id": "plaster", "impact": "bullet_impact", "textures": ["plaster*"] }
```

A world keeps `r_decals` of them (256), dropping the oldest; `fx_decal <decal>` lays one where you look. Marks
are flat and unlit, and nothing that moves holds one.

**Lamps** (issue #314). A `light` part's `pattern` flickers it — a preset (`torch`, `candle`, `flicker`,
`pulse`, `strobe`, `fluorescent` and a few more) or Quake's letters (`"mmamammmmammamamaaamammma"`, `a` dark,
`m` as set, `z` double) at `patternRate` letters a second — and the `SetPattern` input changes it (empty: steady).
`cone` (a half-angle in degrees) makes it a spot light along the entity's forward, with full strength inside
`innerCone`. Past 16 lamps a frame the engine looks them up in an 8 m grid.

**Lamp shadows** (issue #315). Give a `light` part `"shadows": true` (the `sage:point_light` component's
`Shadows`) and, with `r_shadows 1`, the lamp casts shadows from the same casters as the sun's. Maps cost, so
only the `r_shadow_lamps` (default 2, at most 4; 0 turns them off) nearest lamps that ask get one each frame;
the rest light without shadows. A spot of 60 degrees or less (a half-angle) draws one view down its cone; a
point light or a wider spot draws a cube of six faces, in 3 x 2 tiles of `r_shadow_lamp_size` texels (512)
in one atlas. A surface looks up at most two of the four lamps that light it, the ones with maps first, and
every lit pixel pays the two lookups, so give shadows to the few lamps that show them. The maps are fitted
from the main view; `r_stats` counts them in `ShadowMaps`. The Sandbox's crypt torches and the hut's lamp cast.

**Lightmaps** (issue #313). A brush level can bake its lamps: give its `map` record a `lightmap` (texels a
metre, up to 32) and mark the lamps that never switch `"baked": true`. The worldspawn faces are baked at load,
with shadows, and cached under `user://cache/lightmaps/`, so the next load reads the file back. A baked lamp
still lights everything else (props, creatures, doors) as an ordinary lamp. Only materials drawn with the
`Default` technique use a lightmap; there is no bounce light yet, and a baked lamp cannot flicker on the baked faces.

```json
{ "type": "map", "id": "hut", "file": "maps/hut.map", "lightmap": 4 }
```

**Water you can see** (issue #411). Put a `water_surface` part beside a `water` volume and its top face is
drawn rippling, reflecting the sky and the shore, showing the bottom, thickening with depth and fading at the
shore; under it the view is tinted and fogged. A `water_surface` record sets the look (`colour`, `fogDensity`,
`reflectivity`, `refraction`, the waves, and `underwaterColour`, `underwaterDensity`, `underwaterTint`);
`sage:water` is the default. `r_water` switches it. It draws in the main view only.

**Weather that knows where you are** (issue #311). The rain stops under a roof (a ray up from the camera to the
first static collider; an interior scene always is covered) and its sound drops to the weather's
`shelteredVolume`. A weather can have lightning (`lightningRate` a minute, `lightningFlash`, a `thunder`
sound and `thunderVolume`). A `weather_pattern` picks the weather every `slotHours` game hours from its `picks`
(each a `weather` with optional `from`/`to` hours, a `region` and a `weight`), deterministically from its
`seed`; a scene's environment names it with `weatherPattern` and the region with `weatherRegion`.

**Wet ground and puddles** (issue #311). The world has one `Weather.Wetness` (0 dry, 1 soaked), saved with the
weather. A `weather` record's `wetting` and `drying` (each per second, 0 to 1) move it by their difference,
blended as the weather changes: `wetting` 0.03 soaks the ground in about half a minute, and the default
`drying` of 0.004 dries it in about four minutes. Open ground that faces the sky then darkens and glosses, and
past 0.4 wet gathers puddles on the flat where a fixed noise mask is high, mirroring the sky. Walls stay dry, so
do floors a brush level's lightmap says are roofed (the Sandbox hut's), and an interior scene shows none. A
`material`'s `weathering` (0 to 1, default 1) scales it, 0 opts out; only opaque materials wet, and skinned
meshes and the viewmodel never do. The Sandbox's rain and storm set `wetting`. Limits: a surface without a
lightmap (terrain, a model) counts as open sky, so an overhang's ground gets wet, a doorway's floor may wet a
little, terrain cannot opt out, and the puddle numbers are fixed.

**Particles** (issue #310). A `particle` record's `collision` (`None`, `Bounce` with `restitution` and
`friction`, or `Die`) sends one ray a frame per particle, at most 512 a frame for the world (`fx_stats` counts
them); `sheetColumns` × `sheetRows` frames (`sheetFrames` of them, 0 = all) run `sheetCycles` times over a
particle's life, `sheetRandomStart` to start each on its own frame. `soft` (metres, 0 = hard) fades a particle
over that distance in front of the opaque geometry behind it, so smoke on the floor shows no hard line (the
Sandbox's `fire_burst` has `soft` 0.3). It reads the scene's depth, which the frame draws for it even with
`r_post` off, and only a Transparent material is soft; anywhere there is no depth (no shaders, a
render-target view, an effect without the soft technique) the particle is drawn hard. `r_stats` ends with
`, soft particles N (M drawn hard)`. A negative `soft` is a load error.

**Drawing many and drawing sharp.** `r_instancing 1` (off by default) draws runs of at least `r_instancing_min`
(8) copies of one mesh with one material as one instanced draw, and long sprite runs as instanced quads (issue
#309); `r_stats` says how many draws it saved. Every texture has its mipmaps (issue #317), and a material's
`Linear` sampler is trilinear and anisotropic up to `r_anisotropy` (4); `Point` keeps the full-size picture, so
pixel art and sprites stay sharp.

**Replacing an engine pass** (issue #322, SAGE0130). In `Init`, `ctx.Get<RenderPasses>().Replace("sage:sky",
new MySky(), "mygame")` swaps the engine's sky for yours, in its slot and under its id, and `Disable(id, by)`
leaves a pass out. Two modules changing one pass, an id nobody added or another stage is a load error naming
them; `r_passes` says who replaced what. `r_snapshot_dump [file]` writes the next frame's passes, views and items
as text, and `DebugDraw.Text3D(at, text, colour)` puts a label in the world for debugging.

**A scripted cut** (issue #80) is the engine's `sage:scripted_camera` prefab — a camera that starts off,
with priority 100, which holds the player still while it is on — placed and named, and wired from
something that happens (§5, "Wiring"):

```json
{ "prefab": "trigger_zone", "at": [0, 1.5, 0], "name": "gate",
  "outputs": [ { "output": "OnStartTouch", "target": "intro_cam", "input": "CameraOn", "parameter": "3", "times": 1 } ] },
{ "prefab": "sage:scripted_camera", "at": [0, 3, -8], "yaw": 180, "name": "intro_cam" }
```

`CameraOn` takes the screen for the parameter's seconds (none: until a `CameraOff`); the camera fires
`OnCameraOn` and `OnCameraOff` as it changes. It is a cut unless you ask for a blend: `"parameter": "3 1.5
SineInOut"` holds for three seconds and eases in from the view the screen had over the first one and a
half (issue #90). Your own kind is a prefab with the two parts, `camera` (`"enabled": false` and a
priority above your rig's) and `scripted_camera` (`holdTime`, the default hold; `lockInput`; `blendTime`
and `blendEase`, the default blend, which is none; `blendOutTime`, a blend back out when it is turned off or
its hold runs out, issue #323). `CameraOff` takes a blend too: `"parameter": "1.5 SineInOut"`. `tests/games/camera-cut` is a game with
no C# that does both kinds of cut.

Two more games with no C# show phase 4b's logic. `tests/games/scripted-sequence` is a Half-Life-style
sequence written only as wiring in a scene: a trigger opens a door (`Open`), the door's `OnFullyOpen`
raises a lift, the lift's `OnFullyOpen` fires `StartDialogue` at an NPC and every step `Add`s to a
`logic_counter` whose `OnHitMax` (at 3) fires a `logic_relay` that runs its `then` and says a line.
`tests/games/topics` has one conditional `dialogue_topic`: the first `info` whose `requires` holds
answers, and a relay in the scene raises the var that changes the answer. Copy either as a start.
The Sandbox has one too: the path to the hut (`hut_path` and `hut_cam` in its `scene.json`), on a physics
layer only the player touches so nothing else fires it.

A tool that flies a camera by hand (the editor's free camera, `cam_free`) uses a `DebugCamera`: a camera
entity it drives each frame with `DebugCamera.Drive`, above every other camera while it overrides the
screen and below every other otherwise. A game rarely needs one.

**4. A scene** — the engine's `scene` record (issue #29): what stands where, where the player starts,
the maps and placements documents to load, and the weather to start in. Name it in `game.json` and every
world starts in it:

```json
// game.json
"scene": "start"

// content/data/scene.json
{
  "type": "scene",
  "id": "start",
  "origin": [512, 0, 512],                 // absolute metres the placements are measured from
  "relativeTo": "Ground",                  // "World" (absolute, the default), "Origin", or "Ground":
                                           // from the origin across, y above the terrain there
  "player": { "prefab": "player", "at": [0, 1, 0] },
  "place": [
    { "prefab": "tree",  "at": [4, 0, -3] },
    { "prefab": "crate", "at": [0, 0, 2], "yaw": 30, "name": "crate by the door",
      "relativeTo": "Origin" }             // one placement can name its own frame
  ],
  "maps": ["tavern"],                      // brush levels (§5)
  "placements": ["yard"],                  // documents the editor saves, same placement format
  "environment": { "weather": "rain" }
}
```

A placement is the same everywhere — `prefab`, `at`, `yaw`, `name`, an optional `relativeTo` and its
entity I/O `outputs` (§5, "Wiring") — in a scene and in the editor's `placements` documents. A placement
can also be tipped and sized: `pitch` and `roll` (degrees; roll about the thing's -Z first, then pitch
about X, then yaw) and `scale` (`[x, y, z]`, every axis above zero, multiplying the prefab's own scale).
All three are optional and default to an upright thing at its own size, so older files mean what they did
(issue #367). Far proxies, travel entries and a prefab's `children` use only the yaw, and a placement's
scale does not scale its physics collider. The engine places the scene once every module has
furnished the world and before the rules start; saving a record file places it again, sweeping only
what the scene put there (its placements, its documents', its levels) and **keeping the player**;
`scene_load <id>` swaps one scene for another and moves the player to the new start. Each placement gets a
persistent id from the scene and its position in `place`, so saves find it again.

**A game with no C# at all** is `game.json` and records: leave out `"assembly"`, pick the plugins, name a
scene. `tests/games/scene-only` is one — a floor, three crates and a player — and CI boots it in the
real host. What it cannot do without code is generate terrain (that is an `ITerrainGenerator`), so its
ground is a prefab.

Run it, and `ent_list` — or the entity outliner in the dev UI — shows what actually spawned.

**That is the whole minimum**: a `.csproj`, a `game.json`, one module, one `GameRules`, a prefab and a
scene record, and a terrain generator of about six lines. No client half is needed to *see* anything — the
engine's own client module draws the world, the crosshair and the dev UI; your client half is for your
HUD and your screens. This guide was checked by building exactly that and walking about in it.

### Sound, music and controls

Sound is records and an event: gameplay says what happened (a cue, a hit, an item used), a record says what it
sounds like, and nothing in C# plays anything. A walk through each piece, in the order a game needs them.

**A sound** (issue #326, #335). A `sound` record names `variations` (`.wav` or `.ogg` files; one is picked each
time, with `volumeJitter` and `pitchJitter`), a `bus` (`Sfx` by default; `Master`, `Music`, `Voice`, `Ui` and
`Ambient` too), `volume`, `maxInstances` (4), `cooldown`, `priority` and `minDistance`/`maxDistance` (full volume
within the first, silent past the second; 0 for the max makes it 2D). Past those: `rolloff` (`linear`, `log` or
`custom` with a `rolloffCurve` of gains from the min to the max, e.g. `[1, 0.6, 0.2, 0]`), a cone (`coneInner`
and `coneOuter` are full angles in degrees about the emitter's forward, 360 = omnidirectional; `coneOuterGain`,
0.25, is the volume beyond the outer one; only an `audio` part's entity has a direction), and `doppler` (a scale
on the pitch shift of things that move; 0 makes the sound immune). `"stream": true` reads an `.ogg` as it
plays instead of decoding it whole, for anything minutes long; a streamed variation that is a `.wav`, or any
variation that is neither, is a load error naming the file. A `.wav` or `.ogg` you save under a folder mount is
hot reloaded: its loops restart and its one-shots stop. `asset_list` shows a streamed one as `sound (streamed)`.
The player's volumes are the archived cvars `snd_volume` (master), `snd_sfx`, `snd_music`, `snd_ui`,
`snd_ambient` and `snd_voice`; also `snd_enabled`, `snd_maxvoices` (32), `snd_doppler` (1; 0 turns doppler off)
and `snd_occlusion_rays` (8; 0 turns occlusion off). `snd_play <sound>` plays a record where you stand (and plays
it again when you save its file), `snd_stats` says what is playing and what was refused, `snd_debug 1` overlays
the voices and the bus levels in the dev tools, and the editor's Audio panel (EDITOR.md) has a meter per bus and
a Play button. `log_level audio trace` says why a sound played or was refused. A sound may carry a `caption`
(text or a `@key`) and a `speaker` (issue #351): with a speaker, or on the `Voice` bus, it is a line of dialogue
shown as a subtitle while the archived `subtitles` cvar is on (it is by default); otherwise it is a caption of the
noise, `[door creaks]`, shown while `captions` is on (off by default). C# says a line with
`world.Resources.Get<Subtitles>().Say(speaker, text)` (test: SoundsCaptionThemselvesAndTheStackShowsThemOnTop).

**Music** (issue #325). A `music` record is a `track` (an `.ogg`; music always streams), `layers` (stems the same
length as the track: `asset`, `volume`, `intensity`, `fade`; a layer is heard while the world's music intensity
is at or above its own), `volume`, `loop` (on), `loopStart` and `loopEnd` (samples per channel, so an intro
before `loopStart` plays once and a tail after `loopEnd` never; 0 for the end of the file), and `fadeIn` and
`fadeOut` in seconds. Music is the world's, and content changes it with data alone: a scene's
`"environment": { "music": "crypt" }` fades to it when the player arrives; the inputs `PlayMusic "<music>
[seconds]"`, `StopMusic "[seconds]"` and `SetMusicIntensity "<0..1>"` on any wire (any entity will do); and the
actions `{ "play_music": "hearth", "fade": 3 }`, `{ "stop_music": 3 }` and `{ "music_intensity": 0.8 }` in a
`then`. From C#, `MusicRules.Play`, `Stop` and `SetIntensity`. A change crossfades equal-power from whatever is
playing, a change mid-fade starts from where it is, and a third track replaces the quieter one. Music voices are
outside `snd_maxvoices` and are never stolen. The track playing and the intensity are saved (the `music` resource;
a load restarts the track from its top, and the system is `sage.world.music`). The cheat commands `music
[id|stop] [seconds]` (no argument says what it is doing) and `music_intensity <0..1>` try it. The Sandbox has
`wander` (drums come in at intensity 0.5) in the open, `hearth` inside the `hut_yard` trigger and `crypt` in the
crypt scene. Not there: ducking, sample-locked stems, a saved position.

**Footsteps and surfaces** (issue #327). A character's `footsteps` part and a `physics_material`'s cues are
under "Every prefab part" and §5 (surfaces); the `footstep`, `land` and `jump` cues of the surface under the foot
are what play. A skeleton steps on its own clips: put `foot_left` and `foot_right` events in an `anim_events`
record for its model and give it `"footsteps": { "stride": 0 }` (the Sandbox's `hut_walker`).

**Effects, attacks and screens make noise too** (issue #330). An `effect` has `appliedCues`, `tickCues` and
`removedCues`, cues raised at its target when it is applied (again on each refresh or stack), on each period of a
periodic one, and when it ends or is dispelled; the old `cues` field is gone. An `attack` may name its own `sound`
for a landed hit in place of its damage type's (the type's, then the conventions' default type's, when it names
none). A `ui_sounds` record has `move` (focus moved by the arrows, D-pad or Tab; not on hover or the first
focus), `select`, `open` and `close`; patch `sage:default_ui_sounds` to give every screen its noises, and name a
set of your own in a `screen`'s `sounds` to change some (a field it leaves empty takes the default). They play on
whatever bus their `sound` names; the HUD and `ui_close all` are silent. The Sandbox's `ui_move` and `ui_select`
are on `Ui`.

**Rooms** (issue #329). A wall muffles a sound: a ray from the listener to each positional voice, at most
`snd_occlusion_rays` a frame in all, and what is solid in the way (not characters, triggers or hitboxes) lowers the
voice to 0.45 of its volume and dulls it, smoothly over a fifth of a second. A reverb is a `reverb` record
(`mix`, `decayTime`, `decayHFRatio`, `density`, `diffusion`, `gain`, `gainHF`, `reflectionsGain`,
`reflectionsDelay`, `lateGain`, `lateDelay`) or one of the engine's `sage:room`, `sage:hall`, `sage:cave` and
`sage:outdoors`, heard while the listener is inside a `reverb_zone` part's box. Occlusion and reverb need
OpenAL's EFX extension, which the desktop backend has; without it the game logs a warning once and plays occlusion
as volume only, with no reverb. `snd_stats` names the zone you are in and the rays cast, and the trace log has
`occl X.XX` on a play, `occlusion <sound> blocked|clear at N m` and `reverb <id> mix ... decay ...` or
`reverb off`. Occlusion is yes or no per ray (no thickness), 2D sounds are never occluded, and zones are boxes.

**Rebinding** (issue #328). Players change controls with the console or the controls screen, and it is saved
in `user://input.json`. `bind <input> <action> [context]` adds an input (`E`, `key:E`, `mouse:Right` or
`pad:A`); `unbind <input> [action] [context]` takes it off one action, or every action in the context;
`bind_reset [action|all] [context]` puts the game's own back. The context is `Gameplay`, `UI`, `Editor` or
`Console` (left out: `Gameplay`, if the action is bound there). The file is record patches, so you never write it: for each context the
player changed, an `input_map` called `user:rebinds_<context>` holds the action's whole list, and a patch on each
map that bound it (yours, a mod's) empties it there. A file that does not parse is ignored with a warning and kept
as `input.json.bad`. An input another action in the context uses is a conflict: `bind` says which action has it and
changes nothing. The RPG kit's `rpg:controls` screen (a `screen` over the view-model `ui_controls`, strings
`rpg.controls.*`) lists every button action by context, captures the next key, mouse button or pad button the
player presses (never Escape, the left mouse button or the pad's B, Back and Start, which cancel and click),
asks to replace or keep on a conflict, and resets one action or all; bind a key to it as to any screen. A game
without the kit makes its own layout over `ui_controls`. The kit's options screen opens it from its controls page
(§7 "Menus"). Axis actions (`Move`, `Look`) cannot be rebound. In code:
`Engine.Rebinds` (`InputRebinds`), `SageAppOptions.InputFile` and, in tests,
`HeadlessAppBuilder.WithUserInput(file)`.

**The mouse** (issue #334). The player's mouse is held (hidden, kept at the centre) while they play, and let go
for a screen, the console, the controls screen's capture, the dev tools and when the window loses focus; the
archived cvar `m_capture 0` turns it off. The log says `Mouse captured` and `Mouse released (reason)`. With the
console closed the dev ImGui windows do not take clicks while the mouse is held; open the console. A smoke run
can check a log: `SAGE_SMOKE_EXPECT` in `tools/smoke_run.sh` is one extended regular expression per line that
must each match a log line, in order, beside `SAGE_SMOKE_COMMANDS`.

**Gamepads** (issue #331). Up to four pads drive up to four local players: `joy_players` (1 to 4, default 1) says
how many have a pad, they are handed out in order, and a pad unplugged and replugged returns to its player.
`pad_list` shows who has which and `pad_assign <player> <pad>` moves one. A pawn names its player with the
`sage:player_slot` component (`index`; none means the first); players 1 and up read only the Gameplay map's pad
bindings. `joy_deadzone` (0 to 0.9) is a floor under every stick's own `deadzone`, radial and rescaled so the
stick still reaches 1, `joy_curve` is `linear`, `quadratic`, `cubic` or an exponent from 0.1 to 8, and `joy_rumble`
(0 to 1) scales rumble. A `rumble` record (`low` and `high`, the heavy and the light motor, 0 to 1; `duration`,
`attack` and `release` in seconds) is named by a `cue`'s or a `damage_type`'s `rumble`: the cue's is felt by the
player whose entity raised it, the damage type's by the player it hurt. Effects add up per player and the motors
are clamped at 1. `in_tap`, `in_hold`, `in_axis`, `in_look`, `in_release` and `in_clear` take a trailing `@N`
(`in_tap Attack @2`) to drive the Nth player. In code: `Engine.Pads` (`PadAssignment`), `Engine.LastDevice`
(`LastUsedDevice`, which a prompt asks whether to say "E" or "A"), `InputGlyphs.Pick`, `StickResponse`,
`ScriptedInput`, `RumbleMixer` and `PlayerInput.SetCommand(player, command)`. The Sandbox ships no rumble. A
prompt shows the player's own button with `{action:Use}` in its text (issue #352, §7 "Text in other languages"):
"E" at the keyboard, "X" on an Xbox pad, "Square" on a PlayStation one, which the pad's name decides unless the
archived `joy_glyphs` says `xbox` or `playstation`.

**Recording a session** (issue #333). `record <name>` saves the world as it is and then each tick's player command
to `user://demos/<name>.sagedemo`; `stop` ends it; `playdemo <name>` loads that save and plays the commands back,
and says whether the world ended with the hash it was recorded with (`world_hash [world]` prints one: FNV-1a over
the world's saved state); `demos` lists the files. A demo is refused when the build, game, plugins, mods or actions
differ from the ones that made it. It replays exactly where the save holds everything the simulation reads; in
the real Sandbox client it does not yet (the camera rig, `FaceCameraSystem` and `HopSystem` read what a save does
not hold), so use it for scripted tests and headless games first.

## 5. Levels: rooms, doors and triggers

Outdoors is terrain; **indoors is brushes**. The engine reads TrenchBroom `.map` files directly
(standard, Valve 220 and Quake 2/3 dialects) — 32 map units to the metre, Z-up becomes Y-up.

Point TrenchBroom at an entity definition file generated from your own prefabs:

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/YourGame +fgd_export +quit 0
```

Then name the level in a record and load it from your scene (`"maps": ["tavern"]`):

```json
{ "type": "map", "id": "tavern", "file": "maps/tavern.map", "at": [520, 0, 490], "onTerrain": true }
```

In the editor:

- **`classname` is a prefab id.** An entity called `goblin` spawns `yourgame:goblin`. `origin`, `angle`,
  `targetname` and `group` (the groups a wire to `@group` reaches, below) are read; a classname with no
  prefab is left for your game to read off the level.
- **Per-entity values**: any field of the prefab's parts and components, as `<part>.<field>` —
  `"light.range" "12"`, `"body.mass" "20"`. The generated FGD lists them for each prefab, typed and
  described, with the prefab's own value as the default; a value that is not a number where one is
  wanted, or out of its range, is an error naming the map line.
- **A face's texture name is a material id**: a face textured `wall` looks for `yourgame:wall`.
- **A face's texture is also a surface** (issue #270): a `physics_material` record lists the textures
  it covers, and a ray or a sweep that hits the face reports it (`RayHit.Surface`), so footsteps, impacts
  and bullet holes come out right. A face no record names takes the `map` record's `"surface"`:

  ```json
  { "type": "physics_material", "id": "wood", "friction": 0.6, "footstep": "step_wood",
    "impact": "impact_wood", "decal": "textures/hole_wood.png", "penetration": 0.4,
    "textures": ["wood*", "door"] },
  { "type": "map", "id": "hut", "file": "maps/hut.map", "surface": "sage:stone" }
  ```

  An exact name wins over a pattern, and a longer pattern over a shorter one; case does not matter. A
  prefab's collider takes one with the `body` part's `"surface"`, terrain by layer with the `terrain`
  record's `"surfaces"` (the first is the ground everywhere a generator paints nothing else), and a
  creature's `footsteps` part raises the `footstep` cue of whatever it walks on, and `land` and `jump` (each
  falling back to `footstep`) when it lands or jumps. The engine's own
  (`sage:default`, `stone`, `wood`, `metal`, `dirt`, `grass`, `flesh`, `glass`) give friction and a
  penetration hint only; the cues and the textures are yours. Ground no record names, and a surface with no
  cue, takes the cues of the `gameplay_conventions` `surface` (`sage:default`), which you patch to say what
  ordinary ground sounds like (the Sandbox's `footsteps.json` patches it with grass).
- **Brushes with a classname become a solid entity** — a door, a lift, a trigger volume — which is an
  ordinary entity that owns its geometry. Give it a prefab with a `mover` part and it moves.
- **`"trigger" "1"`** makes its volume something you walk into rather than against, and it is not drawn.
  A `"trigger" "1"` brush entity whose prefab has a `reverb_zone` part is a room's reverb, sized from its
  brushes (the Sandbox's `hut_room` and `crypt_echo`); a brush entity with that part and no `"trigger"` is a
  load warning (issue #329).
- **A ladder** is a `"trigger" "1"` brush entity whose prefab has a `ladder` part, drawn as the space in
  front of the rungs from the floor to the ledge's top: `"classname" "ladder"`, `"trigger" "1"`,
  `"ladder.facing" "180"` (issue #263).

**Wiring** is what makes a level do anything. An entity fires a named *output*; you wire it in the map to
a named *input* on another entity:

```
"classname" "door"
"targetname" "tavern_door"
"OnUse" "!self,Open"                       target,input[,parameter,delay,times]
```

Outputs the engine fires: `OnUse`, `OnStartTouch` / `OnEndTouch`, `OnFullyOpen` / `OnFullyClosed`, `OnBlocked` (a mover, with what blocked it as the activator), `OnArrived` (a mover at any stop of its path, the stop as its value), `OnLocked` (a locked mover someone tried to open, the activator),
`OnCameraOn` / `OnCameraOff`, `OnEnterWater` / `OnExitWater` (a character and the water it went into), `OnTimer`, `OnTweenDone`, `OnTweenLoop`, `OnTweenStep`, `OnCalendarEvent` (a `calendar_event` part on its day), `OnStateChanged`, a state machine's `OnEnter<state>` / `OnExit<state>` (`OnEnterAlert`; issue #280), an animator's `OnAnimEvent` (the clip event's name as its value), the logic entities' (below) and
gameplay's `OnDeath`, `OnDamaged`, `OnPickedUp`, `OnStageChanged` / `OnQuestFinished`.
Inputs it offers: `Open`, `Close`, `Toggle`, a mover's `Next` / `Previous` / `GoTo` (a stop by number, 0 = shut), `Lock` / `Unlock` and `SetSpeed`, `Kill`, `Say`, `Fire`, `CameraOn` / `CameraOff`, `TimerStart` /
`TimerStop` / `TimerReset` / `TimerFire` / `TimerAdd`, `TweenTo` / `TweenPlay` / `TweenStop`, `SetState`, a light's `TurnOn` / `TurnOff` / `Toggle` / `SetPattern`, the logic entities' and gameplay's
`SetStage`, `StartDialogue`, `GiveItem`, `ApplyEffect`, `SetFaction` — `io_list` prints the live lists
(with the components each short name belongs to), and
your own modules can register more inputs (`engine.Inputs.Register`) and declare the outputs they fire
(`engine.Outputs.Declare(name, what it means)`), which puts them in the FGD. Targets can be a `targetname` or `!self` / `!activator` / `!caller`.

**A wire can reach a group** (issue #276): a target starting with `@` is every entity it picks, each
one delivery. `@lamps` is every entity in the group `lamps` — a map entity's `"group" "lamps hall"` key
(groups separated by spaces or commas), or a prefab's `"sage:io_group": { "names": "lamps" }` component;
`@class:torch` is every entity spawned from the prefab `torch` (any namespace; `@class:ns:torch` for one);
`@tag:ns:id` is every entity with that tag. A group is resolved when the input *arrives*, so a member
spawned during the delay is reached too, and a group with no members then does nothing. An empty `@`
or a tag nobody declares is an error at load; a group with no members in the level is a warning.
`"OnTrigger" "@lamps,TurnOff"` puts out every lamp of the hall from one relay.

**An input may belong to a component** (issue #91). `engine.Inputs.Register<Mover>("Toggle", …)` runs
only at an entity with a `Mover`, beside `Register<LogicBranch>("Toggle", …)` for branches: an input
arriving runs *every* handler whose component the entity has, and a global handler of that name (plain
`Register`) only when none did. So short names — `Enable`, `Toggle`, `SetValue`, `Trigger` — are each
component's own, and a name no component of the target takes is a warning saying which components
would have. `Open`, `Close` and `Toggle` are the mover's this way.

Connections are **checked when the level loads**: a typo names the map file and line rather than a door
that quietly never opens. `map_load`, `map_list`, `map_unload`, `map_goto` (stand where the map's
`info_player_start` says) and `ent_fire <name|@group> <input>` drive it
from the console, `io_trace 1` logs every wire as it fires, and `io_history [name] [count]` prints the
last inputs delivered (the last 256 are kept, with what became of each: delivered, no target, no such
input, nothing took it, or its handler threw — which is logged with the wire, and the rest of the tick's
inputs still arrive).

**Without a map**, a scene or placements document wires a placement the same way, as a list of
`outputs` on the placement that fires them (issue #80): `{ "output": "OnStartTouch", "target":
"intro_cam", "input": "CameraOn", "parameter": "3", "delay": 0, "times": 1 }` — the five Hammer fields
by name. They are checked when content loads (an input nobody registered is an error naming the file and
line; an output no plugin declares is a warning), and a target is found by its placement `name` when the
wire fires, so it may be placed before or after the wire, or spawned later. A map can place an engine
prefab by its full id, `"classname" "sage:scripted_camera"`.

| Input | Parameter | What it does |
|---|---|---|
| `Kill` | | Destroys the entity |
| `Say` | the text | Puts a line in the message log |
| `Fire` | an output's name | Fires one of the entity's own outputs (a relay) |
| `Open` / `Close` / `Toggle` | | Moves a `mover` (a door, a lift) |
| `TurnOn` / `TurnOff` / `Toggle` | | Switches a `light` (a lamp) on or off (issue 4h-7); off, it gives no light. The Sandbox's lamps are a `state_machine` whose `time_between` transitions fire these at sunset and sunrise |
| `SetPattern` | a pattern: a preset name or Quake letters; empty for steady | Changes a lamp's flicker (issue #314) |
| `CameraOn` | `hold [blend [ease]]`, all optional | Turns a camera on, so it wins the screen at its priority; with a hold, off again after it (none: the entity's `scripted_camera.holdTime`, 0 = until `CameraOff`). With a blend time the screen eases into it from the view it had, along the named curve (none: the entity's `blendTime`, 0 = a cut). Fires `OnCameraOn` if it was off |
| `CameraOff` | `blend [ease]`, optional | Turns a camera off; the screen goes back to the next camera, or the player's view, cutting or, with a blend time (or the entity's `scripted_camera.blendOutTime`), easing there along the named curve (issue #323). Fires `OnCameraOff` if it was on |
| `TimerStart` | an interval, in seconds (optional) | Starts a `timer` counting from a full wait; it fires `OnTimer` when the wait runs out, every interval if it repeats |
| `TimerStop` / `TimerReset` | | Stops it; or starts its wait again from full, running or not |
| `TimerFire` | | Fires `OnTimer` now; a running timer then waits again from full (issue #281) |
| `TimerAdd` | seconds (negative takes away) | Adds to the wait left, never below nothing (issue #281) |
| `TweenTo` | `[position\|rotation\|scale\|offset\|turn] [x y z] [seconds] [ease]`, all optional | Moves, turns or scales a `tween` from where it is to the goal (metres; degrees of pitch, yaw, roll; factors); `offset` and `turn` are *by* rather than *to*. Left out, the tween's own `target`, `duration` and `ease`. A last word `once`, `loop` or `pingpong` (`yoyo`) overrides its `loop` (issue #281). Fires `OnTweenDone` on arrival, or after its last loop |
| `TweenPlay` | `once`, `loop` or `pingpong` (optional) | Plays the tween's `sequence` from its first step, each step from where the last left it; `OnTweenStep` (its number) as each arrives (issue #281) |
| `TweenStop` | | Stops a tween where it is |
| `SetState` | a state's name | Sends a `state_machine` to that state now, nested or not (its exits and enters run, with their `OnExit<state>` / `OnEnter<state>`, and `OnStateChanged` fires); already there, nothing |
| `SetAnimParam` | `name value` (a number, true/false, on/off; a trigger's name alone) | Sets an `animator`'s param (issue #118) |
| `AnimTrigger` | a trigger param's name | Sets an `animator`'s trigger: its next tick's transitions with that `on` may take it |
| `Trigger` | handed on (optional) | A `logic_relay`: if enabled and its `requires` holds (of the activator), runs its `then` and fires `OnTrigger` |
| `Enable` / `Disable` | | A relay, a counter, a `trigger` or a `spawner`: takes its other inputs (or entries) again, or ignores them (a relay and a trigger also take `Toggle`) |
| `Add` / `Subtract` | a number (1 if none) | A `logic_counter`: counts, within `min`..`max`; fires `OnChanged` with the value, and `OnHitMax` / `OnHitMin` on reaching a limit |
| `SetValue` | a number; true/false on a branch | Sets a counter (firing as above), a compare's value, a branch, or a remap's input (firing `OnValue` with the result) |
| `Reset` / `GetValue` | | A counter: back to `start`; or fires `OnGetValue` with the value |
| `Multiply` / `Divide` | a number | A counter: times or over it (dividing by 0 is refused with a warning), firing as `Add` does (issue #281) |
| `SetMaxValue` / `SetMinValue` | a number | A counter: new limits, and the count kept inside them (issue #281) |
| `SetSource` / `ClearSource` / `ToggleSource` | a source's number | A `logic_multisource`: `OnAllSet` the moment every source is set, `OnNotAllSet` the moment one is cleared after that; `Reset` clears them all, `Test` fires `OnTrue` or `OnFalse` (issue #281) |
| `InValue` | a value | A `logic_case`: fires `OnCase01`…`OnCase16` for the first case it matches (as text, ignoring case, or as a number), else `OnDefault`; either hands the value on |
| `PickRandom` / `PickRandomShuffle` | | A `logic_case`: fires one of its `OnCase` outputs at random; the shuffle deals each once before any repeats |
| `Spawn` | | A `spawner`: places its prefab or template where it stands, up to its `limit`, and fires `OnSpawned` (issue #281) |
| `SetCompareValue` / `SetValueCompare` / `Compare` | a number (the first two) | A `logic_compare`: sets what it compares with; sets the value and compares; compares — `OnEqual`, or `OnNotEqual` and `OnLess` / `OnGreater` |
| `Test` / `SetValueTest` / `Toggle` / `ToggleTest` | true/false for `SetValueTest` | A `logic_branch`: fires `OnTrue` or `OnFalse` by its value; sets it and tests; flips it; flips it and tests |
| `SetStage` | `quest stage`, or a stage at a `quest_watch` | Moves a quest to a stage, starting it first if it is not on (quests plugin) |
| `StartDialogue` | | Starts the entity's conversation (a `dialogue` part) with the activator, else the player |
| `GiveItem` | `item [count]` | Puts items in the entity's inventory |
| `ApplyEffect` | `effect [magnitude]` | Applies an effect to the entity, with the activator as its source |
| `SetFaction` | a faction, or nothing | Sets the entity's faction, or clears it |

**Time.** A delay counts from the tick the output fired in, wherever in the tick that was (a trigger's
and a relay's arrive together), and stands still while the game is paused. All of it runs on the world's
time (`WorldTime`, issue #283): `{ "world_speed": 0.5 }` in any `then` is bullet time (half as many
simulation steps a second, each the same length, so physics behaves the same; the clock's hours go by at
half speed too), `{ "hit_stop": 0.08 }` freezes the world for that many real seconds, and both are saved
with the game. A `timer` or `tween` with `"realTime": true` counts real seconds instead, through a pause,
a hit-stop and any speed (its outputs are still delivered when the world next runs a step). From the
console: `world_speed [n]`, `hit_stop <seconds>`, `pause`. A `timer` fires on the tick
a delay of the same length would arrive; a `tween` is a pure function of its elapsed time, so it plays
the same every run. The engine's `sage:logic_timer` prefab is a timer and nothing else, stopped until
`TimerStart`:

```json
{ "prefab": "sage:logic_timer", "name": "gusts",
  "outputs": [ { "output": "OnTimer", "target": "flag", "input": "TweenTo", "parameter": "turn 0 20 0 0.4 SineInOut" } ] },
{ "prefab": "yourgame:lift", "name": "lift",
  "outputs": [ { "output": "OnTweenDone", "target": "lift_bell", "input": "Say", "parameter": "Ding." } ] }
```

Parameters are separated by spaces, never commas, because a `.map` wire's fields are comma-separated.

**Days, seasons and the moon** (issue #289, SAGE0129). The world's `calendar` record (the clock's
`Calendar`; none is twelve months, Monday to Sunday) can have leap years (`leapEvery`, `leapSkipEvery`,
`leapRestoreEvery`: 4, 100, 400 is the Gregorian rule; `leapMonth`, 0 = the last, gets the extra day),
`seasons` (`[{ "name": "winter", "month": 12, "day": 1 }, …]`, each to the next one's start; the default
calendar has the four usual ones from 1 March) and a moon (`moonCycle` in days, 29.53 if 0; `moonStart`).
Content asks with `{ "season": { "is": "winter" } }`, `{ "moon_phase": { "anyOf": ["full", "waning_gibbous"] } }`
(eight phases, `new` to `waning_crescent`) and `{ "on_date": { "month": 12, "day": 25 } }` (`year` for one
day only). A festival is a `calendar_event` record, heard by an entity with the `calendar_event` part:

```json
{ "type": "calendar_event", "id": "harvest_fest", "name": "Harvest Festival", "month": 9, "day": 21 },
{ "type": "calendar_event", "id": "full_moon", "name": "Full moon", "month": 0, "moon": "full" },
{ "type": "prefab", "id": "harvest_bell", "parts": { "calendar_event": { "event": "harvest_fest" } } },

// in the scene's place list:
{ "prefab": "harvest_bell", "name": "harvest_bell",
  "outputs": [ { "output": "OnCalendarEvent", "target": "market_gate", "input": "Open" } ] }
```

`OnCalendarEvent` fires once on the day (`year` 0 is every year; `month` 0 with a `moon` is the first day
of each such phase), not again after a load, and once after a rest or a time skip that passed over it.

**State machines** (issue #92). When a thing's *state* matters — a guard that idles, grows alert and
attacks; a door that is locked, shut or open; the steps of a sequence — write it as a `state_machine`
record and give the thing the `state_machine` part:

```json
{ "type": "state_machine", "id": "guard", "initial": "idle",
  "states": {
    "idle":   { "transitions": [ { "to": "alert", "when": { "var": "alarm", "eq": 1 } } ] },
    "alert":  { "enter": [ { "fire": "!self", "input": "Say", "parameter": "Who's there?" } ],
                "transitions": [ { "to": "attack", "after": 3 }, { "to": "idle", "on": "Calm" } ] },
    "attack": { "tags": ["hostile"],
                "transitions": [ { "to": "idle", "on": "Calm", "then": [ { "set_var": "alarm", "value": 0 } ] } ] } } },
{ "type": "prefab", "id": "guard", "parts": { "state_machine": { "machine": "guard" } } }
```

A transition goes `to` a state when everything it names holds: `on` an input arriving (any name — `Calm`
needs no C#: a wire or `ent_fire guard Calm` sends it), `when` a condition, `after` seconds in the state;
the first that matches wins, the state's own before the machine's top-level `transitions` (from any
state). Changing runs the old state's `exit`, the transition's `then` and the new state's `enter`, then
fires `OnStateChanged`, whose wires are handed the new state's name when they have no parameter of their
own. `SetState <name>` jumps. The state is saved by name with the time spent in it; if a save or a hot
reload names a state the machine no longer has, it goes back to `initial` with a warning.

A state can do more (issue #280). `during` is a list of actions run each tick it is in the state, from
the tick after it is entered, before its transitions are tried. A state can hold `states` of its own with
an `initial` (entering it enters that one; its transitions apply in every state inside it), or
`"parallel": true` with `states` that are regions, all entered together and each moving on its own — a
soldier's legs and arms in one `combat` state:

```json
"combat": { "parallel": true, "transitions": [ { "to": "patrol", "on": "Calm" } ],
            "states": {
              "legs": { "initial": "advance", "states": { "advance": { … }, "cover": { … } } },
              "arms": { "initial": "aim", "states": {
                "aim":   { "transitions": [ { "to": "shoot", "after": 0.25 } ] },
                "shoot": { "during": [ { "add_var": "shots" } ] } } } } }
```

Names are unique across the machine, so `to`, `SetState` and a wire name any state, however deep. Each
state fires its own outputs, `OnEnter<state>` and `OnExit<state>` (handed its name; the case of the state
is free, `OnEnterCombat`), so a wire can listen for one state instead of testing `OnStateChanged`'s
value. The activator (who sent the input that last moved the machine, `!activator` in its actions) is
saved with it, by persistent id, and so is every state a nested machine is in. A level that needs a
machine and nothing else places the engine's `sage:logic_state_machine` prefab and names the machine:

```json
{ "prefab": "sage:logic_state_machine", "name": "alarm",
  "overrides": { "parts": { "state_machine": { "machine": "yourgame:alarm" } } },
  "outputs": [ { "output": "OnEnterRinging", "target": "siren", "input": "Open" } ] }
```

(test: NestedStatesMoveInsideTheirParent_AndParallelRegionsMoveOnTheirOwn, ANestedMachineRoundTripsASaveMidState_WithItsActivator, TheEnginePrefabRunsTheMachineAPlacementNames_AndAStatesOutputsAreWirable)

**Animation graphs** (issue #118, SAGE0126). A skinned character's clips are played by an `anim_graph`
record through the `animator` part, beside the `skinned_mesh` that draws it:

```json
{ "type": "anim_graph", "id": "soldier", "initial": "idle", "fade": 0.2,
  "params": { "speed": { "from": "Speed" }, "jump": { "kind": "Trigger" } },
  "states": {
    "idle": { "clip": "idle", "transitions": [ { "to": "move", "when": { "anim_param": "speed", "min": 0.2 } } ] },
    "move": { "blend": { "x": "speed", "points": [ { "clip": "walk", "x": 1.5 }, { "clip": "run", "x": 4 } ] },
              "transitions": [ { "to": "idle", "when": { "anim_param": "speed", "max": 0.1 } } ] },
    "jump": { "clip": "jump", "loop": false, "fade": 0.1, "transitions": [ { "to": "idle", "after": 0.8 } ] } },
  "transitions": [ { "to": "jump", "on": "jump" } ],
  "layers": [ { "name": "upper", "mask": ["spine"], "initial": "none",
                "states": { "none": {}, "wave": { "clip": "wave" } },
                "transitions": [ { "to": "wave", "on": "wave" }, { "to": "none", "after": 2 } ] } ] },
{ "type": "prefab", "id": "soldier",
  "parts": { "skinned_mesh": { "mesh": "models/soldier.glb" }, "animator": { "graph": "soldier" } } }
```

A state plays a `clip`, a `blend` of clips over one float param (`x`) or two (`x` and `y`), or nothing.
Transitions are a state machine's (`on` here is a **trigger** param, `when`, `after`, `then`); entering a
state cross-fades over its `fade` along its `ease` (else the graph's). Params are floats, bools and
triggers; `from` fills one from the body every tick (`Speed`, `VerticalSpeed`, `MoveX`, `MoveY`,
`AimPitch`, `Grounded`, `Crouching`), and wires set them with `SetAnimParam "crouch 1"` and `AnimTrigger
jump`. `aim_pitch` and `aim_yaw` params (degrees) drive the entity's `aim_ik`. A layer blends over the base on the joints its `mask` names and everything below them. States,
times and params are saved by name; a state the graph has lost goes back to its layer's `initial` with a
warning. `anim_debug` in the console shows every animator; `anim_lod_distance` sets how far from the
camera animators start sampling at half and quarter rate.

**More from a graph** (phase 4p, SAGE0126). A state's `rootMotion` (`Rotation`, `Translation` or `Full`;
`None` by default) moves the body by what the clip's root does, so feet do not slide: the graph's
`rootJoint` names the joint (empty: the skeleton's first), and `rootMotionY` lets the root's height move
the body too (a climb). A character controller takes the travel as its move, colliding and stepping
(issue #357). A layer with `"blend": "Additive"` adds what its clips do relative to their first frame over
what is below (a flinch, breathing), where `Override`, the default, replaces it. A blend space's `sync`
names clip events — `["foot_l", "foot_r"]` — that line its clips up instead of their fraction of a pass. A
transition's own `fade` and `ease` win over the state's (anim_graph only; a `state_machine` ignores them)
(issue #358):

```json
"states": {
  "move":  { "blend": { "x": "speed", "sync": ["foot_l", "foot_r"],
                        "points": [ { "clip": "walk", "x": 1.5 }, { "clip": "run", "x": 4 } ] },
             "rootMotion": "Translation" },
  "lunge": { "clip": "lunge", "loop": false, "rootMotion": "Full",
             "transitions": [ { "to": "move", "fade": 0.1, "ease": "Linear", "when": { "anim_finished": "base" } } ] } },
"layers": [ { "name": "flinch", "blend": "Additive", "mask": ["spine"], "initial": "none",
              "states": { "none": {}, "hit": { "clip": "flinch", "loop": false } },
              "transitions": [ { "to": "hit", "on": "flinch" } ] } ]
```

**Faces** (issue #363): a skinned `.glb` with morph targets keeps them by name, and clips' weight tracks
animate them. A layer's `mask` may name morph targets as well as joints (`"mask": ["blink"]`), and the
graph's `morphs` sets a target from a param every tick, `{ "jaw_open": "mouth" }`, so `SetAnimParam
"mouth 0.6"` lip-syncs over whatever the clips do.

**Clips from another rig** (issue #360): a `skeleton_map` lets a model play a clip library made for
another skeleton with the same rest pose, its joints named and ordered as they like:

```json
{ "type": "skeleton_map", "id": "knight_from_mannequin", "source": "models/mannequin.glb",
  "target": "models/knight.glb", "bones": { "Hips": "pelvis", "Spine": "spine", "LeftHand": "hand_l" } }
```

An animator on the target model plays any clip its own model lacks from the source; `scale` (0: the hips'
heights' ratio) and `sampleRate` (30 Hz) tune the copy.

**Swings from four sides, and guards** (issue #359). Give a fighter the `sage:attack_stance` component
(`"components": { "attack_stance": { "input": "Mouse" } }`; `input` is `Manual`, `Mouse` or `Movement`, and
`direction` and `threshold` are there too), and its graph two params,
`{ "from": "AttackDirection" }` (0 none, 1 overhead, 2 right, 3 thrust, 4 left) and `{ "from": "Blocking" }`.
The player chooses the direction by a flick of the mouse or by the movement keys and guards while holding
the **`Block`** action (the right mouse button, the pad's left trigger; `gameplay_conventions`
`actions.block`); the graph picks its swing and guard states with `when` on those params, and each swing
lands on its own clip's `hit`. `{ "anim_window": "hit_start", "close": "hit_end" }` holds between two clip
events, for a combo or a parry window. An AI guards with the `Block` task (`seconds`) and varies its
`MeleeAttack` swings when its stance is `Manual`. What a guard does to a blow is yours (or 4r's).

**Seeing it:** `anim_debug` lists sprite animators as well (their clip, step and frame, issue #364); the
editor's **Animation** window previews any graph or clip (issue #362, [EDITOR](EDITOR.md) §3). The
Sandbox's brute (`games/Sandbox/content/data/brute.json`) is a skinned creature in data to copy.

**Animation events** (issue #119, SAGE0126) are named moments in a clip — the blow lands here, a foot
lands here, the magazine comes out here. A skinned model's come from an `anim_events` record keyed by the
model; a sprite sheet's clips carry theirs (`"events": [ { "frame": 1, "name": "hit" } ]`):

```json
{ "type": "anim_events", "id": "soldier", "model": "models/soldier.glb",
  "clips": { "attack": [ { "time": 0.35, "name": "hit" } ], "walk": [ { "time": 0.1, "name": "footstep" } ] } }
```

The animator raises each once every time its clip crosses it (a loop's wrap included) as the
`AnimationEvent` game event, as its `OnAnimEvent` output, and by setting the graph's trigger param of the
same name, so `{ "to": "reload_end", "on": "mag_in" }` follows the art. **Combat names no clip**: a swing sets
its attack's `trigger` (default: the conventions' `animations.attackTrigger`, "attack") and lands on the
`hit` event, or on `windupTime` when the clip has none. A sprite plays through a graph too — give its
`sprite` part a `graph`; the sheet's clips are the states' clips — and `{ "anim_finished": "base" }` takes
a one-shot back to idle when its clip is done:

```json
{ "type": "anim_graph", "id": "goblin", "initial": "idle", "fade": 0,
  "params": { "attack": { "kind": "Trigger" } },
  "states": { "idle": { "clip": "idle" },
              "attack": { "clip": "attack", "loop": false,
                          "transitions": [ { "to": "idle", "when": { "anim_finished": "base" } } ] } },
  "transitions": [ { "to": "attack", "on": "attack" } ] },
{ "type": "prefab", "id": "goblin", "parts": { "sprite": { "sheet": "goblin", "graph": "goblin" }, "melee": { "attack": "claws" } } }
```

The conventions' old `animations.attack` and `animations.idle` (and an attack's `animation`) are obsolete:
a sprite fighter with an `animation` and no graph is given one that plays them when it first swings.

**A skinned NPC, whole** (issue #122): `tests/games/skeletal` is a game with no C# whose box mannequins
(generated by the Sandbox's `tools/make_mannequin.py`) walk, jog and run their lanes, aim, strike and
reload. Its `npc.json` is the pattern to copy: a base layer that blends walk and run on `speed`, an
`upper` layer masked to `spine` that blends three aim poses on `aim_pitch` while an `aiming` bool is set
and plays one-shot `attack` and `reload` states on triggers (back on `anim_finished`), `anim_events` for
`hit`, `mag_out` and `mag_in`, and a prefab with `skinned_mesh`, `animator`, `aim_ik` and `foot_ik`. Its
`scene.json` moves them with nothing but 4b's tools: a `state_machine` sends `TweenTo` down a wire and
hears the tween's `OnTweenDone`, wired back to the NPC as `Arrived`; `SetAnimParam` and `AnimTrigger`
aim and strike.

Curves are named the way you would expect — `QuadInOut`, `ease_out_bounce`, `SmoothStep`, `Linear`: every
`Ease` value, case and underscores as you like.

**Logic entities** (issue #91) are what a sequence needs between "the player walked in" and "the door
opens": the engine's `sage:logic_relay`, `sage:logic_counter`, `sage:logic_compare`,
`sage:logic_branch` and `sage:math_remap` prefabs (and `sage:logic_timer` and `sage:logic_state_machine`, above), each an entity with nothing but its part (put the part
on your own prefab to change the defaults, or set `logic_counter.max` on a map entity). Three levers, and
the gate opens only if the alarm is off:

```json
{ "prefab": "yourgame:three_levers", "name": "levers",
  "outputs": [ { "output": "OnHitMax", "target": "gate_relay", "input": "Trigger" } ] },
{ "prefab": "yourgame:gate_relay", "name": "gate_relay",
  "outputs": [ { "output": "OnTrigger", "target": "gate", "input": "Open" } ] }
```

with `three_levers` a prefab of `"logic_counter": { "max": 3 }` (each lever wired `OnUse` → `levers.Add`)
and `gate_relay` one of `"logic_relay": { "requires": { "var": "alarm", "eq": 0 }, "then": [ { "set_var":
"gate_opened", "value": 1 } ] }` — the condition and action language of §3. **Values travel:** an output
with a value (`OnChanged`, `OnValue`, `OnDamaged`, `OnStageChanged`) hands it to a wire whose
`parameter` is empty, so `OnChanged` → `check.SetValueCompare` compares the count. Their state — a
count, a branch, a relay disabled — is saved like any component's.

**The rest of Half-Life's set** (issue #281): `sage:logic_multisource` (an AND gate of numbered sources),
`sage:logic_case` (a switch on a value, or a random pick), `sage:logic_auto` (`OnMapSpawn` once, when the
level starts) and `sage:logic_spawner` (env_entity_maker and point_template); a trigger volume with the
`trigger` part is trigger_once (`"once": true`) or trigger_multiple (`"wait": 2`), filtered by `requires`
(asked of what entered); a timer takes `randomMin`/`randomMax`, a tween `loop`, `loops` and a `sequence`
(the table of parts in §3). There is no multi_manager: a `logic_relay` whose `OnTrigger` wires have
delays is one. `tests/games/scripted-sequence`'s `vault` scene is a whole puzzle chain in data, from
`logic_auto` to a spawner. Two things to know:

- **A wire with no parameter of its own takes the output's value.** `OnHitMax` → `door.TweenTo` hands the
  counter's value on, which `TweenTo` reads as seconds; give such a wire a parameter.
- **A spawner's template names prefabs by full id** (`"yourgame:beacon"`) when it is set in an override of
  the engine's `sage:logic_spawner`: a bare id there means one of the engine's own.

**A wire can have a condition** (scenes and placements; a `.map` key has no room for one): `"requires":
{ "var": "power", "eq": 1 }` on an `outputs` entry is asked when the output fires, of the activator
(and the entity firing it as the other); while it fails the wire sends nothing and does not count toward
its `times`.

**Same tick.** A chain of wires takes a tick a hop, which is what keeps a wire that fires itself from
hanging the game. A relay with `"sameTick": true` delivers its `OnTrigger` wires that have no delay in
the same tick instead, so a chain of such relays runs to its end at once; the `io_maxdispatch` budget
still counts every delivery in the tick, so one wired to itself stops at the budget with a warning.

**Gameplay's own**, each there only with its plugin: `SetStage`, `StartDialogue`, `GiveItem`,
`ApplyEffect`, `SetFaction` (table above); `OnDeath` (the victim, the killer as activator), `OnDamaged`
(the one hurt, the attacker as activator, the damage done as its value) and `OnPickedUp` (a pickup, as it
is taken). `OnStageChanged` / `OnQuestFinished` fire on anything with a `quest_watch` part for that quest,
with the stage as the value and the player as the activator.

---

## 6. Where your C# goes: modules, phases and systems

Your own code is smaller than you expect: most of a game is records. What C# you do write goes in one
of two halves, and keeping the split in mind answers most questions about where:

- **Simulation half** — anything that decides something: rules, spawning, AI behaviour you add, what an
  item does, what a quest wants. Testable headlessly, which is the point.
- **Client half** — anything that *shows* something: the HUD, screens, a prefab part that builds a mesh.

Your module is an `IModule` (the game's own also implements `IGameModule`), and the four methods happen
in a fixed order, which matters more than it looks:

- `Init` — **register only**: cvars, console commands, entity inputs, input actions. Records are not
  loaded yet. (Record types, saved resources and prefab parts are declared with attributes and
  registered for you just before `Init`.)
- `Start` — records are loaded and the graphics device exists: read records, load assets.
- `OnWorldCreated` — per world: install resources and add systems to phases.
- `Shutdown` — in reverse order.

Registering a cvar in `Start` instead of `Init` is a real trap: `config.cfg` is executed between the two,
so the saved value is dropped with an "unknown cvar" warning and the setting silently never applies.

**A system is declared too** (issue #17). The attribute gives it a stable id, its phase and its order
against other systems *by id*; your module still builds it, because a system takes what it needs —
cvars, records, a service — through its constructor, and adds it in `OnWorldCreated`:

```csharp
[System("yourgame.tides", Phase.Gameplay, After = new[] { "sage.movers.move" })]
public sealed class TideSystem : ISystem
{
    private readonly EventReader<Damaged> _hits;
    public TideSystem(World world) { _hits = world.Events.Reader<Damaged>(this); }   // `this`: see below
    public void Run(in SystemContext ctx) { /* ... */ }
}

public void OnWorldCreated(World world) => world.AddSystem(new TideSystem(world));
```

- `Before`/`After` order systems **within one phase**. Naming a system in another phase is an error
  when it is added, and so is naming an id nothing declares — a typo, or a plugin that is not installed.
  Write `"?othermod.thing"` for a plugin that may legitimately be absent. An id that is declared but
  not in this world (its plugin is turned off) is simply no constraint.
- Ask for event readers with `this` as the owner, in the constructor: when the system is removed (or
  replaced, or disabled) its readers are released, and a system that is `IDisposable` is disposed.
- To react to a component being gained or lost, read `Added<T>` and `Removed<T>` (issue #282) like any
  other event: `world.Events.Reader<Added<Health>>(this)`. `Removed<T>` carries the component as it was,
  because its entity may be gone by the time you read it; a component that was only given a new value is
  not an `Added<T>`, and a destroyed entity sends a `Removed<T>` for each component it had. Nothing is
  published for a type until somebody asks for a reader of it
  (test: AReactiveSystemSeesOneAddAndOneRemovePerEntity, ATypeNobodyReadsIsNotPublished).
- What happens *to* a world rather than in it comes as engine signals: `engine.Signals` has the C# events
  `WorldCreated`, `SceneLoaded`, `WorldDestroying` and `PauseChanged` for code that is not a system, and a
  system reads the same thing as the `EngineSignal` event (`Kind` is `WorldCreated`, `SceneLoaded`,
  `Paused` or `Resumed`; `Scene` the scene placed). A reader asked for in a system's constructor sees its
  own world's `WorldCreated` (test: EngineSignalsSayAWorldWasCreatedAndASceneLoaded). Pausing a world does
  not raise `Paused` yet.
- A plugin can **replace** or **disable** a system it did not add — `world.Systems.Replace("sage.ai.think",
  new MyThink(world))`, `world.Systems.Disable("sage.effects.tick")` — from `OnWorldCreated`, in a
  plugin that depends on the one it changes. Both are logged against your plugin, and `sys_list` shows
  every system's id, its plugin and who replaced or disabled it.
- A test's probe or a tool's one-off can stay undeclared: `world.AddSystem(probe, Phase.Late)`.
- **A system that says what it touches can run beside others** (issue #288). Implement `IDeclaresAccess`
  and declare it once; systems of a phase that conflict with nothing of each other's run at the same time,
  on the world's own worker threads, and the results are what running them in order gives:

  ```csharp
  [System("yourgame.regen", Phase.Gameplay)]
  public sealed class RegenSystem : IDeclaresAccess
  {
      public void Declare(SystemAccess access) => access.Reads<Stats>().Writes<Health>().Sends<Healed>();
      public void Run(in SystemContext ctx) { /* ... */ }
  }
  ```

  `Reads<T>`, `Writes<T>`, `ReadsEvents<T>`, `Sends<T>`, `ReadsResource<T>`, `WritesResource<T>`, or
  `Exclusive()` for a system that touches everything. Make structural changes on `ctx.Commands` (they need no
  declaration; a direct `world.Add` or `FlushCommands` needs `Exclusive`), and declare a resource you took in
  the constructor too. A dev build checks you: what a declared system touches without declaring it is an
  error in the log, counted in `world.Systems.AccessViolations` (`sys_access_check`). `sys_parallel 0` runs
  every phase in order on one thread, `sys_threads` sets the workers, and `sys_list` shows each declaration.
  A system that declares nothing runs alone, in its place, as before — and the engine's own declare nothing
  yet.

**The ECS is Sage's vocabulary** (issue #25). Components are structs implementing `IComponent` and
tags implement `ITag`, each with a stable id (§3); an `Entity` is a handle that reports `IsNull` once
its entity is gone; a system makes its `Query<…>` once, in its constructor, and walks it chunk by chunk:

```csharp
[System("yourgame.regen", Phase.Gameplay)]
public sealed class RegenSystem : ISystem
{
    private readonly Query<Health> _wounded;
    public RegenSystem(World world) =>
        _wounded = world.Query<Health>().WithoutAllTags(Tags.Get<Asleep>());   // Asleep: your [Tag] struct

    public void Run(in SystemContext ctx)
    {
        foreach (var (healths, entities) in _wounded.Chunks)      // no allocations
        {
            var h = healths.Span;
            for (int n = 0; n < h.Length; n++)
            {
                h[n].Value += ctx.Tick.Dt;
                if (h[n].Value > 200) ctx.Commands.Destroy(entities.EntityAt(n));   // deferred
            }
        }
    }
}
```

Adding or removing a component or tag, or destroying an entity, while a query is being walked throws;
record it on `ctx.Commands` (an `EntityCommands`) and the world applies it at the end of the phase.
`query.Entities` walks entity by entity, and `.Entities.ToEntityList()` takes a copy you may change
the world under. Outside a loop, `world.Get<T>(entity)`, `Add`, `Remove`, `Has`, `TryGet` and
`Destroy` do it directly, and `entity.Name` is the entity's name. The storage underneath is
Friflo.Engine.ECS, and it stays underneath: naming a Friflo type or namespace in your game is a build
error (SAGE0050), so upgrading or replacing it never breaks a game (test: NamingFrifloOutsideTheEcsImplementationIsABuildError)
(test: NoPublicTypeInTheBaseEngineExposesFriflo).

**Asking physics something** goes through the world's `IPhysicsWorld`, never the Bepu space behind it,
so the same code runs when a 2D backend arrives (issue #30):

```csharp
var physics = world.Resources.Get<IPhysicsWorld>();
var mask = LayerMask.All.Except(physics.Layers.Enemy);             // layers by name, not by number
var hit = physics.Raycast(eye, aim, 30f, mask, ignore: self);      // `ignore`: leave the asker out
var swing = physics.Sweep(Collider.Sphere(0.3f), pose, aim, 2f, ignore: self);   // starts inside? Distance 0
foreach (var touch in physics.TriggerEnter) { /* PostPhysics: who entered which trigger */ }
int n = physics.RaycastAll(eye, aim, 30f, hits, mask);             // every hit, nearest first
int near = physics.OverlapSphere(blast, 4f, overlaps);              // what a sphere really touches
```

The same trigger and contact facts arrive on the event bus (issue #269), so a system reads them in code
with a cursor instead of wiring entity I/O: `TriggerEntered`, `TriggerExited`, `Collided` (with
`Impulse` in N·s and `Speed` in m/s: an impact sound's volume, fall damage) and `CollisionEnded`.

```csharp
_hits = world.Events.Reader<Collided>(this);                        // in the constructor
foreach (ref readonly var hit in _hits.Read())                      // in Run
    if (hit.Impulse > 5f) PlayThud(hit.Point, hit.Impulse);
```

A sweep that starts inside something hits it at distance 0 (`StartsInside`), which is what a swing
pressed against its target wants; contact begin/end is reported only for colliders with
`ReportContacts` (the `body` part's `"contacts": true`).

**Adding a word content can use** (issue #28). Every vocabulary in §3's table is declared like a
record: a class, an attribute, and the generator registers it for your plugin just before your `Init`.
Your game senses night, and its creatures and its conversations can then ask for it by name:

```csharp
[AICondition("is_night")]                        // asked every think, after the engine's perception
public sealed class IsNight : IAICondition
{
    public bool Sense(in AIPerception p) => p.World.Resources.Get<Clock>().Hour is < 6 or >= 21;
}

[ItemUse("heal")]                                // "uses": [{ "use": "heal", "amount": 25 }, "consume"]
public sealed class Heal : IItemUse
{
    public float Amount;                         // settings are fields, read from the entry's JSON
    public bool Use(in ItemUse use, out string why) { /* … */ why = ""; return true; }
}
```

The attributes are `[AICondition]`, `[AIScheduleSelector]`, `[QuestObjective]`, `[Condition]`,
`[Action]`, `[AbilityDelivery]`, `[EffectExecution]` and `[ItemUse]`; each class implements the
vocabulary's type (`IAICondition`, `IAIScheduleSelector`, `QuestObjective`, `ICondition`, `IAction`,
`IAbilityDelivery`, `IEffectExecution`, `IItemUse`), is public (or internal) with a public
parameterless constructor, and may carry several attributes to answer to several names. `Plugin =
"id"` says which plugin owns it when your assembly has more than one (SAGE0100). Registration closes
when content loads, like every registry (test: VocabulariesAreSealedWhenContentLoads). Your own
vocabulary is one `[Vocabulary("name")]` on an interface and one attribute class deriving from
`VocabularyEntryAttribute<T>`; a field of that interface type then reads entries from JSON. Worth
knowing about each:

- **Conditions and actions** (`[Condition]`, `[Action]`, in `Sage.Simulation` since issue #89) are asked
  about a `Subject` and an `Other` (`ConditionContext`, `ActionContext`); `why` is what a refusal says, a
  constant string so asking allocates nothing. Mark the setting the shorthand fills with `[EntryValue]`
  (`[Condition("is_hour")] class IsHour : ICondition { [EntryValue] public int Hour; … }` reads
  `{ "is_hour": 21 }`), and read a `requires`/`then` of your own as `ICondition`/`List<IAction>` fields.
- **AI conditions** keep to 64 in all (a creature's conditions are one 64-bit mask); the engine's eleven
  keep their bits and yours take the rest (test: TheEnginesConditionsKeepTheirBitsAndAGamesTakeTheNextFree).
  A profile's `rules` pick a schedule by them without any selector of your own
  (test: AGamesConditionPicksASchedulesThroughAProfilesRules).
- **Quest objectives** either measure progress from the world (`Measure`) or count happenings
  (`Notice`): the engine sends kills, conversations and where the player is; send your own with
  `Quests.Notice(world, new QuestHappening("yourgame:lever", lever, player))`
  (test: ReachTalkAndAGamesObjectiveMoveAQuestAlong).
- **Effect executions** may run inside a system's loop: queue anything structural with
  `execution.Defer(world => …)` (the engine's `summon` and `dispel` do).

Tags go up to 256 and button actions to 128 (test: TagsGoPast64) (test: ActionMask_HoldsButtonsPast64).

---

## 7. The HUD, and screens

The engine draws **no** part of your HUD. It gives you three things and your client half composes them:

- **`UiDraw`** — an immediate-mode queue in screen pixels, from any `FrameUpdate` system:

  ```csharp
  ui.Rect(x, y, width, height, colour);
  ui.Frame(x, y, width, height, colour, thickness);
  ui.Text(x, y, "100 / 100", colour, scale);
  ui.Image(texture, destination, colour);
  var size = ui.Measure(text);        // and ui.Size for the screen, ui.LineHeight for a line
  ```

- **`MessageLog`** — `world.Say("Picked up a sword", MessageKind.Good)` from anywhere in the simulation,
  including a headless server where nothing draws it. Your HUD decides how a message looks.
- **The crosshair**, which is the one thing the engine draws, because combat and Use both aim from the
  middle of the screen: `sage:crosshair`, a HUD layer the client opens in every world, shown while a player's
  rig aims and no window is up, and hidden by `ui_crosshair 0` (a `sage.ui` cvar). Restyle it by patching the
  `sage:crosshair` style, or its layout for another shape (issue #350).

**Screens** — an inventory, a spellbook, a journal — are `screen` records over view-models (below): what a
row says and whether it can be used is a rule, read by the view-model; how it looks is the record's. A game
binds one to an action, and the key that opens it closes it:

```csharp
var widgets = world.Resources.Get<UiScreenStack>();
widgets.Bind(actions.Get("Inventory"), new RecordId("rpg", "bag"));   // a kit's screen, or one of yours
```

While a screen is open it takes the input, so gameplay does not also react. The base client ships no
screen of its own. The RPG kit binds its own keys (B `rpg:spellbook`, M `rpg:spellmaker`, J `rpg:journal`, T
`rpg:rest`) and opens `rpg:dialogue` when somebody with a `dialogue` is used; without a `UiScreenStack` nobody
is talked to. There is no registry of screens by id any more (#350): a screen is a record, so a game adds
one in its content.

### Screens from records, and text in other languages

The retained widgets (`Sage.UI`, 13) can be built from content instead of C#, which is how a screen is
restyled, re-laid out or translated by a designer or a mod — and a running game follows the edit, since
record hot reload rebuilds every open screen. Three record types, from the `sage.ui` plugin:

```json
[
  { "type": "ui_style", "id": "panel", "padding": 6, "background": "#202020C0", "textColour": "#E0E0E0",
    "states": { "focused": { "background": "#3050A0" }, "disabled": { "textColour": "#808080" } } },

  { "type": "ui_layout", "id": "bag", "style": "panel",
    "nodes": {
      "window": { "widget": "stack", "anchors": "center", "spacing": 4 },
      "title":  { "widget": "label", "parent": "window", "text": "@mygame.bag.title" },
      "weight": { "widget": "label", "parent": "window", "text": "@mygame.bag.weight", "args": { "current": "weight", "max": "capacity" } },
      "items":  { "widget": "item_list", "parent": "window", "bind": "items" },
      "item":   { "widget": "label", "parent": "items", "bind": "name" },
      "alarm":  { "widget": "label", "parent": "window", "text": "@mygame.bag.alarm", "visibleIf": { "var": "alarm", "eq": 1 } }
    } },

  { "type": "screen", "id": "bag", "layout": "bag", "viewModel": "bag" }
]
```

- A layout's `nodes` are **flat, by name**; each says its `widget` (`box`, `stack`, `grid`, `label`,
  `image`, `button`, `item_list`, `bar`, `scroll`, and since 4q `slider`, `checkbox`, `dropdown`, `text_field`,
  `tabs` and `view`) and its `parent`. A patch changes one node by name.
- `bind` reads the view-model for the widget's main value (a label's text, a bar's value, a list's
  rows — the list's one child is the template for each row, bound to that row); `bindings` names others
  (`visible`, `enabled`, `max`, `style`, …); `args` fill `{placeholders}`; `visibleIf`/`enabledIf` are
  conditions (§3 "What things do").
- The view-model is a class your plugin declares, whose public fields and properties are the paths:

  ```csharp
  [ViewModel("bag")]
  public sealed class BagView : IViewModel
  {
      public List<ItemRow> Items { get; } = new();
      public float Weight, Capacity;
      public void Refresh(in UiBindContext context) { /* read the player's bag */ }
  }
  ```

  Declare view-models in your game's simulation half, so `sage validate` knows them.
- **Showing it** (#97): every world has a `UiScreenStack`, which the client draws and feeds the `ui`
  input context (D-pad or arrows, Tab, Enter, Escape, the mouse). Bind a key to the screen in your
  client module — the key that opens it closes it — or open it by hand:

  ```csharp
  var widgets = world.Resources.Get<UiScreenStack>();
  widgets.Bind(actions.Get("Bag"), new RecordId("mygame", "bag"));
  widgets.TooltipStyle = "mygame:tooltip";
  widgets.Activated += (layer, widget) => { if (widget.Name == "close") widgets.Close(layer); };
  ```

  `ui_open bag` does the same from the console. A screen opens with its first button focused, fades
  in, and closes on Escape (gamepad B) or a click outside it. `widgets.Push(tree, modal: false)` shows a
  tree you built in C# — a HUD — without taking any input. The Sandbox's `sandbox:status` (`C`) is a
  complete example: `games/Sandbox/content/data/ui.json` and `games/Sandbox/StatusView.cs`.
- **A HUD is a screen record too** (issue #99): `widgets.OpenHud(new RecordId("mygame", "hud"), new
  UiBindContext(world))` opens it as a layer that is drawn under every window, refreshed every frame, and
  never takes a key. Anchor its nodes to the corners (`"anchors": "bottom_left"`, `margin`) and look the
  player up in the view-model each frame, since a load replaces the entity. The Sandbox's `sandbox:hud`
  (`HudView`) is the example, and a frame of it allocates nothing. A node in a `box` can bind `x` and
  `y` (0..1 of the box) to place itself — the map's markers do.
- **A part of a picture** (issue #350): an image's source may end in `#x,y,w,h`, a rectangle in the texture's
  pixels, so a sprite sheet's frame is an image like any other (the Sandbox's first-person hands bind
  `HudView.HandSource` to one). A fragment that is not four numbers draws the whole texture and warns once.
- **A text field holds the keyboard**: while one has focus (`UiScreenStack.Typing`), the keys that open
  screens do nothing, so a letter typed is a letter; Escape still closes the screen.
- **How a style draws** (13 "As built (drawing)"): its `background`, then its `image` (nine-sliced by
  `slice`, in texture pixels, and multiplied by `tint`), then a `border` `borderWidth` wide, then the
  widget's content. Text sizes are in font pixels: `"textScale": 2` is an 18-unit line. A node without a
  style inherits its parent's text colour and scale, but a style's background, image, border and padding
  are drawn only where the style is applied — give a window its style and its labels a text style, and
  the frame is drawn once, round the window.
- **Acting on it** (issue #98): a view-model may implement `Activate(widget, context)` — a confirmed or
  clicked widget, a fixed button by its node name, a row by `UiScreen.RowOf(widget)` — and
  `Back(context)`, returning true when it used Back (put down a held item) so the screen stays open. The
  stack calls both for its top screen (`UiScreen.Handle`, which a tree you host yourself calls with what
  `uiRoot.Update` returned). A screen between two entities passes the second as its context's `Other`,
  which `visibleIf` conditions see as `other`; a `grid` can bind its `columns`. The RPG kit's screens
  (§2 "Kits") are the worked examples:
  `widgets.Open(RpgKitModule.LootScreen, new UiBindContext(world, player, corpse))`.
- **Text is a key**, `@ns.key`, into `strings/<lang>/*.json` (05 §3.7): `strings/en/mygame.json` holds
  `{ "bag": { "title": "Bag", "weight": "{current} / {max} kg" } }`, plural forms are
  `{ "one": "{count} arrow", "other": "{count} arrows" }`, and the `lang` cvar picks the language
  (English fills what it lacks). A missing key shows as the key and is a warning; `sage validate` lists
  every key your records name that no table has.

### Forms, fonts and focus

Phase 4q (#337) filled in what a real menu needs. All of it is fields of the same three records.

- **Form widgets** (issue #340): `slider` (`min`, `max`, `step`), `checkbox` (`checked`), `dropdown`
  (`options`, `selected`), `text_field` (`placeholder`, `maxLength`, `multiline`) and `tabs` (each child a page,
  its `title` the tab's text, `tabStyle` the tabs' style). Each takes focus and works from the D-pad: Left and Right
  step a slider and cycle a dropdown, Confirm opens a dropdown's list. Bind its main value (`bind`) and what the
  player sets is written back to that path of your view-model, after which its `Changed(widget, context)` is
  called; a path that cannot be written is a load warning. A ticked box and the open tab are drawn in their
  style's `selected` state (test: AFormBuiltFromALayoutRecordWritesWhatThePlayerChangesBack).
- **Fonts** (issue #338): a style's `font` may be a `.ttf` or `.otf` in your content, `fontSize` its size in
  virtual units (0 is 9, the engine font's line), rasterised as it is drawn; `fonts/sage.ttf` is the engine's own
  letters as a TTF. A node's `wrap` breaks its text at spaces to fit (and `maxWidth`), and `overflow` is `Clip`
  or `Ellipsis` for what still does not fit (test: AStyleNamesAFontAndASizeAndItsLabelsAreLaidOutInIt)
  (test: ALongLineWrapsAtSpacesToItsWidthAndTheLabelMeasuresEveryLine).
- **Focus** (issue #343): `focusUp`, `focusDown`, `focusLeft` and `focusRight` name the node the D-pad goes to
  from here; `focusScope` makes a node a trap for focus while it shows, which is what a question over a menu is.
  For a yes or no, call `widgets.Confirm(title, message, answered)` — `answered` hears true or false, Cancel has
  focus first, Back cancels — or `widgets.Message(title, message)`; `DialogStyle` and `DialogButtonStyle` on the
  stack style them (test: AConfirmPromptOverAMenuTrapsFocusUntilAnsweredAndGivesItBack).

### Buttons that do things, and parts made once

A screen can work with no C# at all (issues #344, #347):

```json
{ "type": "ui_layout", "id": "tally", "style": "panel",
  "nodes": {
    "window": { "widget": "stack", "anchors": "center", "include": "mygame:titled_window",
                "params": { "title": "@mygame.tally.title" } },
    "count":  { "widget": "label", "parent": "window", "text": "Bells: {n}", "args": { "n": "vars[bells]" } },
    "ring":   { "widget": "button", "parent": "window", "text": "Ring",
                "actions": [ { "add_var": "bells", "amount": 1 }, { "command": "echo ding" } ] },
    "shop":   { "widget": "button", "parent": "window", "text": "Trade", "actions": [ { "open_screen": "rpg:shop" } ] },
    "close":  { "widget": "button", "parent": "window", "text": "Close", "actions": [ { "close_screen": {} } ] }
  } },
{ "type": "screen", "id": "tally", "layout": "tally", "viewModel": "ui_world" }
```

- A node's `actions` run when it is pressed, before the view-model hears of it, with the screen's subject and
  other: any action of the vocabulary, plus `open_screen` (a screen about the same two), `close_screen` (`{}` the
  top window, `{ "all": true }` every one) and `command`, a console line run as typed, where `{path}` is read from
  the pressed row (`$parent.` the scope around it, `$root.` the view-model) and put in as one argument
  (test: ALayoutButtonsActionsRunWhenItIsPressed) (test: AButtonRunsAConsoleCommandFilledFromItsRow).
- `include` puts another layout's nodes inside a node (named `node/inner`), and its `params` fill that layout's
  `{$name}`; the included layout's own `params` are the defaults (test: ALayoutIncludesAnotherWithItsParamsAndActions).
- Paths index: `items[0].name`, `stats[health]`, any C# indexer; `scope` makes a node and what is inside it read
  from a path (test: BindingPathsIndexListsArraysDictionariesAndIndexers).
- `ui_world` is a view-model every game has: the world's variables, `vars[name]`, read and written. A mod adds a
  screen with it from records alone (test: AModAddsAScreenWithAWorkingButtonAndAReusablePartFromRecordsAlone).

### Menus

The RPG kit has the screens round a playthrough (issues #342, #339); a game without the kit makes its own layouts
over the same view-models.

- **The title**, `rpg:title` (New game, Continue, Load, Options, Mods, Quit): name it in `game.json`'s `"title"`
  (§2). In C#, `Engine.BeginGame(world)` starts a waiting world and `Scenes.AtTitle(world)` asks whether it is
  waiting; a test boots there with `HeadlessAppBuilder.AtTitle()` (test: AWorldWaitsAtTheTitleUntilNewGame).
- **The pause menu**, `rpg:pause` (Resume, Save, Load, Options, Quit, which asks first). A `screen` with
  `"pauses": true` stands the world still while it is open; bind it to a key in your client module
  (`widgets.Bind(actions.Get("MainMenu"), RpgKitModule.PauseScreen)`, the Sandbox's F10)
  (test: ThePauseMenuStandsTheWorldStillUntilItCloses).
- **Save and load**, `rpg:save` and `rpg:load`: the slots on disk, newest first, with when and where (a save's
  header now has its `scene`, `SaveSlot.Scene`), asking before overwriting, deleting, or loading over a game
  (test: TheSaveScreenAsksBeforeOverwritingOrDeleting).
- **Options**, `rpg:options`: four pages of `ui_option` records, each a setting over a cvar, staged until Apply.

  ```json
  { "type": "ui_option", "id": "gore", "page": "gameplay", "order": 60, "label": "@mygame.options.gore",
    "kind": "toggle", "cvar": "mygame_gore" },
  { "type": "ui_option", "id": "rpg:fov", "patch": true, "max": 120 },
  { "type": "ui_option", "id": "rpg:render_scale", "patch": true, "disabled": true }
  ```

  `kind` is `slider` (`min`, `max`, `step`, `percent`, `decimals`, `defaultLabel`), `toggle` (`on`, `off`) or
  `choice` (`choices`, each a `label` with a `value` and/or `set`, several cvars at once — a resolution, a quality
  preset — or `"choicesFrom": "languages"`). A setting whose cvar the host does not have is left off; a mistake is
  a load error (test: OptionMistakesAreLoadErrors). Archived cvars are kept in `config.cfg` when the game closes
  (test: AnAppliedSettingSurvivesARestart). The kit's settings include `fov` (the player camera's field of view,
  0 = the camera's own) and `vid_fullscreen` (borderless), both new.

### Loot, talk and trade from play

- **Use opens a screen about what is used** (issue #344): give a chest `"use_screen": "rpg:loot"` (the part also
  makes it usable); a body that dies with an inventory is usable and opens `rpg_conventions`' `lootScreen`, and the
  dead start no conversation (test: UsingAChestWithAUseScreenOpensItsLootAboutIt).
- **A container needs no `use_screen`** (issue #378): `"container": { "locked": true, "key": "cellar_key", "owner":
  "innkeeper", "respawn": 72 }` opens the loot screen for whoever carries the key, refills three days after it was
  taken from, and taking from it raises `Stolen` unless you are the innkeeper or of their faction; a body is an unowned
  container (test: ALockedContainerOpensOnlyForTheKeysCarrier) (test: ABodyIsAContainer_LootedByUseWithoutTheft).
- **A merchant trades** (issue #380): `"merchant": "trader"` beside `"use_screen": "rpg:shop"` makes the shop buy and
  sell for the `merchant` record's currency, which is an item the player carries
  (test: TraderExit_BuysSellsRefusesWithAReason_RestocksAfterADay_AndKeepsItAllAcrossASave).
- **A conversation opens one** with an action on an option: `"actions": [ { "open_screen": "rpg:shop" } ]` sells
  the speaker's goods (test: ADialogueOptionOpensTheShopOverTheSpeakersGoods).
- **The console** too: `ui_open rpg:loot watcher` opens it about the entity called `watcher`.

### Grids, pictures and views

- **Drag and drop** (issue #346): a `draggable` node can be pulled and dropped; the view-model hears
  `DragStart`, `DragMove` and `Drop` (a `UiDrag`: the row, where from, what is under the pointer, or nothing — the
  world). Its `Command(command, target, context)` hears `UiCommand.Rotate` (R, LB), `Split` (F, LT) and `Alternate`
  (Delete, X). A grid child may span cells (`columnSpan`, `rowSpan`), and a button may have an `icon` drawn under
  its text, `iconTurned` a quarter turn; all four can be bound. The kit's inventory does all of it with an
  `rpg_item`'s `icon` (test: AMouseDragsAnItemByItsPictureTurnsItAndDropsAStackOnTheGround).
- **A picture of the world** (issue #348): a `view` node shows a named render target. Give it a `camera` —
  `{ "mode": "Orbit", "distance": 3, "height": 0.9, "rotatable": true }` round the player, or `{ "mode": "TopDown",
  "radius": 40 }` straight down with north up — and it draws its own; `subject` is `player`, `other` or an entity's
  name. An image or a view can bind `fog` (a `UiFogMask`) drawn in `fogColour` over what is not revealed. The
  Sandbox's status window (C) has a turnable paper doll (test: AViewFromALayoutShowsItsTargetWithItsCameraAndBindings).
- **A scene's map** (issue #349): an `area_map` with the scene's id gives the kit's map a picture and fog.

  ```json
  { "type": "area_map", "id": "main", "picture": "textures/map_main.png",
    "from": [448, 448], "to": [576, 576], "cell": 8, "reveal": 16 }
  ```

  `from` is the picture's north-west corner and `to` its south-east, absolute metres X and Z; fog lifts in `cell`
  squares `reveal` metres round the player and is saved. A quest stage's or objective's `target` (an entity's
  name) is marked while the player tracks the quest (test: ATrackedQuestShowsItsTargetOnTheMap). A `box` with
  `clip` cuts off what its children place past its edges, and a child can bind `x`, `y`, `w` and `h` to be a rect
  rather than a point.

### Text in other languages

- **`{action:Use}`** in any text is the key or button for that action on the device in use (issue #352); a string
  table can name each glyph — `strings/en/input.json` with `{ "key": { "Space": "Space bar" }, "playstation":
  { "A": "Cross" } }` is `@input.key.Space` and `@input.playstation.A` — or the built-in names are used
  (test: AStringTableNamesAGlyphAndAnUnboundActionShowsItsName).
- **Plurals and formats** (issue #345) follow CLDR's rule of each language; `{gold:n0}` is "1,234" in English and
  "1.234" in German.
- **A `language` record** per translation, by its code: `{ "type": "language", "id": "ar", "name": "العربية",
  "fonts": ["fonts/naskh.ttf"] }`. `direction` is Auto (from the code), `LeftToRight` or `RightToLeft`; `fonts`
  stand behind every style's font for the characters it lacks. Arabic is joined and reordered, a right-to-left
  screen is mirrored, and Japanese and Chinese wrap between characters
  (test: AJapaneseAndAnArabicTableRenderInTheirOwnFontsAndArabicIsMirrored).
- **`loc_check`**, and `sage validate`, list what each translation lacks: keys, placeholders, plural forms and
  characters its fonts cannot draw (test: ValidateReportsWhatATranslationLacks).

### Accessibility

- `ui_scale` and `ui_text_scale` (0.5 to 3, archived) make the whole UI, or its text, bigger, live; layouts
  reflow (test: UiScaleMakesEverythingBiggerAndReflowsTheLayoutLive).
- A `ui_style_set` swaps styles while the `ui_style_set` cvar names it:

  ```json
  { "type": "ui_style_set", "id": "high_contrast", "label": "@mygame.options.high_contrast", "highContrast": true,
    "swaps": [ { "from": "mygame:button", "to": "mygame:button_hc" } ] }
  ```

  A dev build and `sage validate` warn where text is below 4.5:1 against what is behind it, and with a set marked
  `highContrast` below 7:1 is an error (test: TheLoadSaysWhereTextIsHardToRead).
- Subtitles and captions are a sound's `caption` and `speaker` (§4 "Sound, music and controls"), drawn over every
  screen in the engine's `subtitles` and `captions` styles, which you may patch.

### The Shipping console

A Shipping build has no Dear ImGui. With `con_enable 1` the tilde key drops down the game UI's own console: the
log at `log_console_level`, Page Up and Page Down to scroll, Up and Down for history, Tab to complete (issue #353;
test: Shipping_ConsoleOpensOnlyWithConEnable).

---

## 8. Saving

An entity is saved if it carries `Persistent` — everything else is assumed to be rebuilt from records
and levels when the world loads. For a world-level thing of your own (a quest log, the weather), two
steps, and **both are needed**:

```csharp
[SavedResource("your_state")] public sealed class YourState { … }
```

The attribute is the whole of it: the generator registers the type for your plugin, as it does records.
It used to take a second line, `Saves.RegisterResource<T>()`, and that line was the trap — three
separate resources in this engine were marked saved and none of them were. `save <name>`,
`load <name>` and `saves` drive it from the console, and **`SaveSystem.Slots`** lists the saves for a
menu (issue #99): each slot's name and header (`SavedUtc`, `FormatVersion`, `Game`, `EngineVersion`,
`CanLoad`), newest first, read from disk once and again after a `Save`, a new `Root` or `Rescan()`;
`SlotsVersion` moves when it was read again. The Sandbox's own menu (`ui_open main_menu`, `sandbox:main_menu` over
`MainMenuView`) lists them and loads the one chosen from its `Activate`; F10 is now the RPG kit's pause menu,
whose `rpg:save` and `rpg:load` screens read the same list (§7 "Menus").

**Titles, thumbnails, compression and the background write** (issue #285, SAGE0131). `save <slot> <title…>`
(or `Save(slot, kind, title)`) gives a slot the title a menu shows, `SaveSlot.Title`. A client that sets
`SaveSystem.Thumbnail` hands over the frame's pixels when a save is taken (the host does: the back buffer,
320 pixels wide), and the slot gets `thumbnail.png`, which `SaveSlot.ThumbnailPath` names. `save_compress 1`
writes `world_<name>.json.gz` (`SaveSlot.Compressed`); either kind loads. A save the game takes while it
plays (F5, a trigger's `save_game`, an autosave) is snapshotted on the tick and written on a background
thread (`save_background`, on by default). The snapshot only copies your components' columns, a few
milliseconds for ten thousand entities; a component with an `Entity` field, a list or a converter of your
plugin's is serialised there and then, per entity, so a component thousands of things carry is cheapest
made of plain values and strings. Reading a slot
waits for a write in flight, and `SaveSystem.WaitForWrites()` does it by hand. F5 and F9 work while the game is paused. `save_quick_slot`
and `save_autosave_prefix` rename the quick slot and the autosaves, and the rotation never overwrites a
slot the player saved under an autosave's name. `save_report <slot>` (`SaveSystem.Report`) says what
loading a save would do: each component's and resource's saved version against this build's, as current,
to upgrade (with how many `[Upgrade]` steps), newer or unknown, and the plugins, content and mods that differ.

**What a save holds.** Each component under its id and a version:
`"yourgame:health": { "version": 1, "data": { "Value": 80 } }`; saved resources the same way under
their name. A field marked `[Transient]` is left out, and so is a whole component or tag type marked
`[Transient]` (derived state, handles that mean nothing next session). An entity spawned from a prefab
is saved as **what changed since it was spawned** (4i-5): only the fields that differ from its prefab
(with its placement's overrides), whole components it gained, and `"removed"`, the ones it lost. A load
spawns it from the prefab as it is *now* and lays that over it, so when you rebalance a prefab or a
record, every entity the player never changed picks it up. Its transform is saved, except for an
entity your content placed that is still where it was placed: that one follows the placement when you
move it (4m-4). A component whose values come from where the entity was placed (a door's closed
position, as the mover does) says `[FromPlacement]`, so each entity's is compared with its own and an
untouched one follows its placement too. A prefab with `"persist": false` is not saved, and a load
removes every such spawn still in the world (`Unsaved`; add that tag to an entity you `Create` yourself
and want gone on a load). A component that holds an `Entity` across ticks reads it through
`world.Resolve(entity)`, which finds an entity that went to sleep with its cell once it is back.

**Changing a component after you have shipped.** Renaming the *type* costs nothing: the id is what the
save knows. Adding a field costs nothing either: an old save leaves it at its default. Renaming, moving
or removing a *field* is a new version of the component, with a method that rewrites the old shape:

```csharp
[Component("yourgame:health", Version = 2)]
public struct Health : IComponent
{
    public float Current;   // was Value
    [Upgrade(1)] static void From1(ref JsonObject o) => o.RenameField("Value", "Current");
}
```

Upgraders run in order on load, oldest first (`RenameField`, `RemoveField` and `MoveField`, which takes
dotted paths, do most of it); on a diffed entity an upgrader sees only the fields that were saved. Forget one and the load **says so** — the component and the field are
named, and that component keeps what the prefab gave it — rather than quietly dropping the value, which
is what used to happen. To change a component's *id*, list the old one in `FormerNames`. The build checks
an upgrader's signature and version (`SAGE0007`). The same `Version` and `[Upgrade]` work on a
`[SavedResource]`.

**Entity I/O is saved too** (issue #90): an input still on its way arrives on the tick it would have
(its target found by persistent id, else by name), and a wire with `times` keeps its count — the wiring
itself is the level's and comes back with it. Timers, tweens, camera blends and state machines are components, saved
with their entities. A save from before this loads with nothing on its way and every wire unfired.

Saves from an older engine are upgraded as they load (their `formatVersion` is older); one from a newer
engine is refused. The engine's tests keep a save from each format in `tests/Sage.Tests/Content/Saves`
and load it every run.

---

## 8a. Building levels in the editor

A `placements` record is a list of prefab, position, yaw and name — what stands where — with any fields
that one placement changes from its prefab (`overrides`) and its wires (`outputs`). The editor builds
them: in a dev build, `-edit` opens your game's level in the editor mode of the host.

```bash
dotnet run --project src/Sage.Host -c Development -- -game <your game> -edit [scene|placements]
```

You place prefabs from a palette, select several, move, turn and scale them with gizmos, change a field
for one placement in the inspector (lists and nested objects too, or add a component to it alone), wire
outputs to inputs, block out rooms from brushes, sculpt and paint terrain, pick models and textures from
an asset browser, write conditions in a form, undo any of it, press Play to try it in a real world and
Stop to come back, and save into the file the document came from, comments and all. Every one of those is
a console command too (`ed_place`, `ed_move`, `ed_set`, `ed_wire`, `ed_brush`, `ed_play`, `doc_save` and
the rest), so a script or a test can build a level the way a person does. **[docs/EDITOR.md](EDITOR.md) is the guide**; the
worked example is `tests/games/editor`.

A scene loads a document by naming it (`"placements": ["yard"]`); a game can also spawn one itself:

```csharp
world.SpawnPlacements(new RecordId("yourgame", "yard"));
```

None of this exists in a Shipping build, which contains no editor at all. For modders, `sage package
--editor` puts a Development host with the editor in an `editor/` folder beside your Shipping game, with
`edit.sh` and `edit.cmd` to open it; `ed_mod <their mod>` then saves everything into their mod, your level
as a patch ([EDITOR.md](EDITOR.md) §1 and §8; issue #375).

---

## 9. Checking your game without playing it

Four tools, in the order you should reach for them — after the editor's own checks (§3, "Editing
records in VS Code"), which catch a misspelt field as you type it:

**`sage validate`.** Loads your game headlessly — every mount, every record, every prefab body, every
asset path, then the game's own start-up with its scene placed — and exits non-zero if anything was an
error. CI runs it on every game in the repository; run it before you push, and before you publish a mod:

```bash
dotnet build Sage.sln -c Development -p:SageSkipShaders=true
src/Sage.Cli/bin/Development/net8.0/sage validate games/YourGame
src/Sage.Cli/bin/Development/net8.0/sage validate games/YourGame --mounts mods/better_swords=swords   # a bare patch folder
src/Sage.Cli/bin/Development/net8.0/sage validate games/YourGame --mods mods/better_swords          # a mod: its mod.json is read
src/Sage.Cli/bin/Development/net8.0/sage validate games/YourGame --game-mods                      # the game's own mods folder
src/Sage.Cli/bin/Development/net8.0/sage mods games/YourGame                                      # load order, refusals, conflicts
```

Every problem is one line, `file:line:column: type id: what is wrong`:

```
ERROR Records: mod:data/lamp.json:3:27: prefab swords:lamp: unknown field 'color' in 'parts.light'; did you mean 'colour'?
ERROR Records: mod:data/items.json:6:35: item swords:rock: 'sound' refers to sound sandbox:fire_burst, which doesn't exist (sandbox:fire_burst is a particle)
```

It loads what a dedicated server loads plus the engine client's record types (materials, sounds,
sprite sheets, particles), so a prefab part only your client half declares is skipped there — the real
game's load checks those, at the same lines. `--engine-content <dir>` points at engine content when the
tool can't find `engine_content/` above it.

In a running game, **`problems`** at the console prints the same list from the load it made — errors and
warnings with file and line, then conflicts between mods as warnings — in every build, Shipping included
(issue #301). A dev build's overlay shows a red (errors) or yellow (warnings) count in the corner while
there are any; `ui_problems 0` hides it. The editor's problems panel is the same list ([EDITOR.md](EDITOR.md)).

**Headless tests.** Your simulation half can be ticked in a test with no window and no graphics device.
This is the engine's own habit and the reason the split exists. Reference `tests/Sage.Testing` and your
game's project from a test project, and boot the game from its own `game.json` — the same plugins, mounts
and stages the game executable uses, less the client:

```csharp
using var app = HeadlessApp.ForGame("games/YourGame", new YourGameModule()).Boot("test");
app.World.RunFixed(1f / 60f);
```

Or pick the pieces a test is about, with records written on the spot:

```csharp
using var app = HeadlessApp.Gameplay()                          // physics and every gameplay plugin
    .With(new YourGameModule())
    .File("data/test.json", """[{ "type": "item", "id": "key" }]""", ns: "yourgame")
    .OnRegistered(a => a.Records.Register<TestOnlyRecord>())   // a type no plugin declares, by hand
    .Boot("test");
```

`EventProbe<T>` remembers every event of a type the world raised, and `CaptureSink` what the log said.

**Scripted runs.** Drive the real game from the command line and read the log afterwards. Every one of
these is a console command, so anything you can type you can script:

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/YourGame \
  +sv_cheats 1 +god 1 +log_file_level Trace \
  +wait 15 "+warp 520 500" +wait 2 "+in_look 0 10 0.5" \
  "+in_hold Use 0.3" +wait 2 +screenshot +wait 5 +quit 0
```

`wait` is in seconds. `in_tap <action>` presses for a frame, `in_hold <action> <seconds>` holds it,
`in_axis <action> <x> <y> <seconds>` moves a stick, `in_look <yaw> <pitch> <seconds>` turns the view, and
`in_cursor <x> <y>` moves the pointer. Actions are named in `engine_content/data/input.json` — `Use`,
`Move`, `Attack`, `Inventory` and the rest — **not** keys.

Screenshots land in `user/<game>/screenshots/`, logs in `user/<game>/logs/`. `log_level <category> trace`
turns up one part of the engine: `level`, `events`, `physics`, `ai`, `gameplay`, `assets`…

**Hot reload.** With the game running, save a record file and it reloads; save a PNG and the texture
swaps. `rec_reload`, `asset_reload [path]` and `asset_list` do it by hand.

**Profiling and the visual log** (issue #300; dev builds). `stat frame` shows milliseconds per phase and system,
`stat render` the last frame's draw calls, triangles, items drawn and culled, GPU uploads and thread-pool jobs, and
`stat assets` what is loaded, what loading cost that frame and what is still loading. For a frame that hitches
without showing in `stat frame`, `trace_start [max events]`, play, then `trace_dump [file]` writes a Chrome trace to
`user/<game>/logs/` (a bare name goes there; none gives `trace-<time>.json`) that opens in ui.perfetto.dev: one track
per thread, every phase and system a slice, the parallel systems on the workers they ran on. The buffer is fixed
(262,144 events unless you give a number); what does not fit is dropped and counted in the file and the log.
The **visual log** keeps debug shapes per tick so you can look at the tick that decided something: `vlog_record 1`,
play until it happens, then `vlog_at -120` (120 ticks before the newest), `vlog_step` and `vlog_list [tick]`
(the shapes with their text), with `r_debugdraw 1` to draw them and `vlog_window 1` for a timeline with a slider.
`vlog_show "ai physics"` picks categories and `vlog_ticks` how many ticks are kept (600). The AI debug view and
`phys_debug` record into it; from your own code, `world.VisualLog().Capsule("mygame", ...)` (and `Line`, `Arrow`,
`Box`, `Sphere`, `Circle`, `Cone`) with a category of your own, checking `Recording` first when you build
a text. `WorkStats.JobStarted()`/`JobFinished()` and `LoadStarted()`/`LoadFinished(ticks)` count your own background
work and loads into `stat render` and `stat assets`.

---

## 10. Things that silently do nothing

Most of what costs a newcomer an afternoon in this engine is not an error message — it is something
quietly not happening. The engine's own history is mostly this list, so it is worth reading once:

1. **`[Record]` without `Records.Register<T>()`** used to be dead JSON: the file loaded and the type
   matched nothing. Since issue #16 the attribute *is* the registration (the generator writes it), so
   this can't happen any more — and a `[Record("")]` that no content could name is SAGE0021
   (test: AnEmptyRecordTypeOrResourceNameIsABuildError).
2. **`[SavedResource]` without `Saves.RegisterResource<T>()`** used to save nothing — this exact bug
   shipped three times. Gone the same way: the attribute registers it.
3. **An action that is not `Actions.Register`ed** makes every binding to it ignored (with a warning), and
   `UiScreenStack.Bind` to it warns and does nothing. Register actions in `Init`.
4. **Rules come from `CreateRules`.** Since 2026-09-27 a game module returns its `GameRules` from
   `CreateRules(World)`, which the engine calls at the right moment. The older way — installing them as a
   resource inside `OnWorldCreated` — still works, but set them any later than that and the engine has
   already started `DefaultGameRules` in their place.
5. **`Terrain.Load` without a `Generator`** trips an assert and loads nothing, after which `HeightAt`
   returns 0 everywhere — so everything you place stands at sea level.
6. **A character left on the `default` physics layer falls through the world**, because a character
   ignores its own layer and the terrain is on that one. It warns now, but the symptom is memorable:
   the camera follows the player down and every direction looks like empty sky.
7. **Register in `Init`, never later.** A cvar registered in `Start` used to miss `config.cfg`, and a
   record type, input action, prefab part or module registered late did nothing either. Since
   2026-09-27 these are **errors** at the line that did it (`SageApp` seals each registry at the stage
   after which registering would be too late), so this one no longer happens silently — the rest of
   the list still does. Since issue #19 the plain case is **a build error, SAGE0020**: a registration
   call written in a module's `Start`, `OnWorldCreated` or `CreateRules`, or in a system's constructor
   or `Run` — lambdas inside them included, since they cannot run any earlier — fails to compile
   (test: RegisteringACvarInStartIsABuildError) (test: RegisteringInOnWorldCreatedOrCreateRulesIsABuildErrorLambdasIncluded)
   (test: RegisteringInASystemIsABuildError); a render pass added after `Init` is one too (test: AddingARenderPassAfterInitIsABuildError). A helper method those call is not followed; the run-time
   seal still catches that. A misspelt key in `game.json` is an error too, and `disable` names that
   match no module are warned about.
8. **Prefab parts run in their declared order, not JSON order**: after the parts their `After` names,
   then by id. Since issue #17 that order is written down and a second part with the same id is an
   error, where it used to be module order and a silent replacement. What still bites: a part your
   *client* half declares must be declared `Prefabs.Optional(name)` by your simulation half, or a
   headless run errors on every spawn of that prefab.
9. **Hot reload used to sweep away the player** along with the scene, when each game wrote its own
   respawn loop. Scenes are the engine's now (issue #29): a reload sweeps only what the scene placed and
   keeps the player (test: HotReloadRespawnsTheSceneWithoutDuplicatingOrLosingThePlayer). If you place
   things of your own on `RecordStore.Reloaded`, keep your hands off the player the same way.
10. **`PawnIntent` is final after the `Commands` phase.** Write it later and your input acts a tick late;
    the phase contract will tell you.
11. **`in_tap` presses for one frame**, so two in a row need a `wait` between them, and a HUD string
    rebuilt every frame allocates in the steady state — cache it and rebuild when it changes.
12. **Two assemblies that each declared a `Health` component** used to overwrite each other without a
    word, because a component was its C# name. Since issue #16 it is its id (§3), and two types claiming
    one id stop the engine with both named.
13. **Renaming a component, or one of its fields,** used to drop that data from every existing save
    silently. Since issue #20 a save is keyed by id, and a field the type no longer has is an error that
    tells you to write an `[Upgrade]` (§8).
14. **An `[Upgrade]` method on a type nothing saves** never ran — only a `[Component]`, `[Tag]` or
    `[SavedResource]` has its upgraders called. Since issue #19 that is **a build error, SAGE0022**, and
    so is a saved resource's upgrader with the wrong shape or two upgraders from one version
    (test: AnUpgraderNothingRunsIsABuildError).
15. **A MonoGame type in your simulation half** used to be kept out by habit: one package reference
    and the rules no longer ran headless. A project with `<SageSimulationOnly>true</SageSimulationOnly>`
    (Sandbox and Hello have it) now fails to build on one, **SAGE0024**
    (test: AMonoGameTypeInASimulationOnlyAssemblyIsABuildError).
16. **A misspelt field in content** — `"color"` for `colour`, `"name"` on an item whose field is `label`
    — used to be dropped, leaving the default in its place. Since issue #22 it is an error at its line
    with the nearest real name, in records, component data and part options alike (§3).
17. **A game with no `gameplay_conventions`** — no engine content mounted, or a patch that set `health`
    to `""` — has nothing that counts as life, so nothing dies, a fighter with no attack of its own
    cannot swing, and a hit that names no damage type does nothing (it says so once). The engine's
    content ships the record; a game that mounts its own content instead of the engine's must supply
    one (§3, test: WithoutConventionsNothingIsLifeAndNothingBreaks).

---

## 10a. Build errors: the SAGE diagnostics

The generators and analyzers in `src/Sage.Generators` run in every engine and game project (the
`Directory.Build.props` in `src/` and `games/`, and `Sage.Sdk` for a game built with it), and every
diagnostic they report is an error. One line each, with the fix:

| Id | What | Fix |
|---|---|---|
| SAGE0001 | A `[Record]` or `[SavedResource]` whose plugin cannot be inferred (the assembly has no `[Plugin]`, or several) | Add `Plugin = "<plugin id>"` |
| SAGE0002 | A declared record or saved resource the registration cannot construct (abstract, generic, private, no public parameterless constructor) | Make it a public (or internal) concrete class with `public X()` |
| SAGE0003 | Two declarations of one record type or resource name in an assembly | Rename one |
| SAGE0004 | An `IComponent` or `ITag` struct without `[Component("ns:name")]` / `[Tag(...)]` | Give it a stable id |
| SAGE0005 | A malformed component or tag declaration (bad id, `Version` below 1, wrong interface, private, generic) | Follow the message |
| SAGE0006 | Two components (or two tags) with one id in an assembly | Rename one id |
| SAGE0007 | A component's `[Upgrade]` method with the wrong signature, or upgrading from a version it cannot have | `static void Name(ref JsonObject o)`, from 1 to `Version - 1` |
| SAGE0010 | A `[PrefabPart]` whose plugin cannot be inferred | Add `Plugin = "<plugin id>"` |
| SAGE0011 | A declared part or system that cannot be one (abstract, generic, private, not `IPrefabPart` / `ISystem`, a part without a public parameterless constructor) | Follow the message |
| SAGE0012 | Two declarations of one part or system id in an assembly | Rename one |
| SAGE0013 | A system ordered (`Before`/`After`) against a system of this assembly in another phase | Drop the constraint: the phase order decides it |
| SAGE0020 | A cvar, command, input action, entity input, record type, prefab part, saved resource, vocabulary entry, render pass (issue 4h-1) or module registered (or a save converter added) in `Start`, `OnWorldCreated`, `CreateRules`, or a system's constructor or `Run` (§10 item 7) | Register it in the module's `Init`; hand a system what it needs through its constructor |
| SAGE0021 | A `[Record]` type or `[SavedResource]` name that is empty or has whitespace, or a saved resource with `Version` below 1 | Name it (`"item"`); versions start at 1 |
| SAGE0022 | An `[Upgrade]` method nothing runs (not on a component, tag or saved resource), a saved resource's upgrader with the wrong shape or version, or two upgraders from one version | Move it to the type whose saved shape changed; fix the signature or the number |
| SAGE0023 | *Strict saves only:* a public component field that is neither `[Property]` nor `[Transient]` | Decide: `[Property]` saves it, `[Transient]` leaves it out |
| SAGE0024 | *Simulation-only projects:* a `Microsoft.Xna.Framework` (MonoGame) type | Move the code to the client half |
| SAGE0025 | *Base engine assemblies:* a reference to a `Sage.Kits.*` assembly, or a use of one of its types or members | The base never depends on a kit: move the code into the kit, or give the base a hook the kit plugs into |
| SAGE0100 | A vocabulary entry (`[AICondition]`, `[QuestObjective]`, `[ItemUse]`, …) whose plugin cannot be inferred | Add `Plugin = "<plugin id>"` |
| SAGE0101 | A vocabulary entry that cannot be one: abstract, generic, private, no public parameterless constructor, or not of the vocabulary's type | Follow the message |
| SAGE0102 | Two entries of one vocabulary with one id in an assembly (ids compare ignoring case, `_` and `-`) | Rename one |
| SAGE0103 | A vocabulary entry with an empty id | Name it |
| SAGE0104 | An entry attribute whose type is not marked `[Vocabulary("name")]`, so content could not name it | Mark the interface (or abstract class) |
| SAGE0050 | A `Friflo.*` type or namespace named anywhere but `Sage.Simulation`, which implements the ECS over it | Use Sage's `Entity`, `IComponent`, `ITag`, `Tags`, `Query<…>`, `EntityCommands` (§6) |
| SAGE0110 | *Sage.Sdk, after a build:* `game.json` names an `assembly` or `modules.add` path that the build did not write | Point it at `bin/{config}/Name.dll` (relative to the game folder; no target framework in the path) |
| SAGE0111 | *Sage.Sdk, after a build:* `game.json` does not load the project's own dll | Name it as `assembly` (the simulation half) or in `modules.add` (a client half); the message has the path |
| SAGE0112 | *Sage.Sdk:* no host, engine or `sage` CLI for this configuration (the `Sage.Player` package has Debug, Development and Shipping; in this repository, the host is not built) | Build in Debug, Development or Shipping (`-c Release` is Shipping since #294), or build `src/Sage.Host` / `src/Sage.Cli` in that configuration |
| SAGE0113 | *Sage.Sdk:* the project has `.fx` shaders but no local `mgfxc` (no `.config/dotnet-tools.json` above it) | `dotnet new tool-manifest && dotnet tool install dotnet-mgfxc --version 3.8.5.1`, or `-p:SageSkipShaders=true` |
| SAGE0114 | *Sage.Sdk, after a build:* a `<SageKit>` the project is built on that `game.json`'s `"kits"` does not name, so the host would not load it | Add its id to `"kits"` |

An attribute on the wrong kind of type — `[Record]` on a struct, `[Component]` on a class — is the
compiler's own CS0592, because each declaration attribute names what it may go on.

Three are switched on per project, in its `.csproj` (and a fourth, `SageEcsImplementation`, is the
engine's own: `Sage.Simulation` sets it to name Friflo, and a game cannot, since neither `games/` nor
`Sage.Sdk` makes it visible to the analyzers (test: TheEcsImplementationMayNameFriflo)):

- `<SageSimulationOnly>true</SageSimulationOnly>` — the project is simulation: it runs headless and
  names no MonoGame type (SAGE0024). Every engine project under `src/` is simulation-only unless it
  says otherwise (`src/Directory.Build.props`; `Sage.Client`, `Sage.Editor` and `Sage.Host` do), and
  `games/Sandbox` and `games/Hello` set it; a client half (`Sandbox.Client`) does not
  (test: AClientAssemblyMayUseMonoGame) (test: EveryBaseAssemblyIsSimulationOnly).
- `<SageBaseAssembly>true</SageBaseAssembly>` — the project is part of the base engine (`Sage.Core`
  to `Sage.Gameplay`, `Sage.UI`, `Sage.Client`, `Sage.Editor`), which never depends on a kit (SAGE0025,
  REDESIGN §0.5) (test: ABaseAssemblyThatUsesAKitIsABuildError) (test: AKitOrAGameMayUseAKit).
- `<SageStrictSaves>true</SageStrictSaves>` — off by default. Today every public field of a component
  is saved unless it is `[Transient]`, so a field added later is saved without anyone choosing it;
  strict mode asks each one for a decision (REDESIGN §4.5)
  (test: UnderStrictSavesAPublicComponentFieldMustDecide) (test: StrictSavesIsOffByDefault).
  `[Property]` is recognised by name once the engine has it (issue #18); until then only `[Transient]`
  counts, and the message says so (test: UnderStrictSavesPropertyIsADecisionOnceItExists).

Registering in `Init` is not an error, of course (test: RegisteringInInitIsNotAnError). Not checked
yet at build time: a `RecordRef<T>` whose `T` no plugin registers. `RecordRef<T>` exists since issue
#22 and content loading checks every reference by type; an analyzer for the declaration itself is a
follow-up.

## 10b. Experimental API

Some of the public API is marked `[Experimental("SAGE01xx")]`: it is new or about to change, and naming
it outside the assembly that declares it is an error until the project says it accepts that, with the id
in its `.csproj` (docs/RELEASING.md §2):

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);SAGE0120</NoWarn>
</PropertyGroup>
```

Content is not affected: a record, prefab or scene that uses these areas loads as before; only C# that
names their types needs the opt-in.

| Id | Area | Why it may change |
|---|---|---|
| SAGE0120 | The open vocabularies' contracts (issue #28): `IAbilityDelivery`, `IEffectExecution`, `IItemUse`, `IAICondition`, `IAIScheduleSelector`, `QuestObjective`, `ICondition`, `IAction`, their entry attributes and context structs. **Moved in 0.2 (issue #89):** `ICondition`, `IAction`, `ConditionAttribute`, `ActionAttribute`, `ConditionContext` and `ActionContext` are in `Sage.Simulation` now, not `Sage.Gameplay` — add `using Sage.Simulation;` (a game project already has it); content ids are unchanged | One issue old; how an entry reads its settings and what its context carries will move as games write entries |
| SAGE0121 | Scenes and placements in C# (issue #29): `SceneRecord`, `SceneEnvironment`, `Scenes`, `SceneWorldExtensions`, `Placement`, `PlacementFrame`, `PlacementsRecord`, `PlacementExtensions`; brushes (#61): `BrushPart`, `BrushShape`, `BrushFace`, `BlockoutBrush` | The level editor (#61) will reshape the document model |
| SAGE0122 | Brush maps from TrenchBroom (`.map`): `MapRecord`, `MapLevel`, `MapLevels`, `SolidEntity`, `MapBrush`, `MapFace`, `MapEntity`, `MapSpace`, `LevelBrush`, `BrushGeometry` | Kept until the level editor replaces the importer (REDESIGN §4.6) |
| SAGE0123 | Cameras as entities (issue #76): `Camera`, `CameraPose`, `CameraProjection`, `CameraViewport`, `CameraView`, `CameraViews`, `CameraDirector`, `CameraMath`, `CameraPart`; render targets and the screen (issue #77): `Renderer.DeclareTarget`, `FindTarget`, `ReleaseTarget`, `ScreenWorld`, `RenderStats.Views`/`TargetViews`, `MaterialParam.RenderTarget`; scripted cameras (issue #80): `ScriptedCamera`, `ScriptedCameraPart`; camera blends (issue #90): `CameraBlend`, `CameraBlends`; rigs (#78, #79): `FirstPersonRig`, `FirstPersonRigPart`, `FirstPersonRigSystem`, `ThirdPersonRig`, `ThirdPersonRigPart`, `ThirdPersonRigSystem`, `ToggleViewSystem`, `PlayerCamera`, `PlayerCameraSystem`, `CameraRigKind`, `CameraRigs`; the editor's cameras (#81): `DebugCamera`, `MainViewExtensions` (`world.TryGetMainView`) | Phase 4a is done (#75), and it stays experimental until its first consumers outside 4a exist: 4b's tweens will blend between views, 4c's UI toolkit will draw render targets in widgets, and phase 10's editor host will own the viewport |
| SAGE0124 | Phase 4b's logic (#87): the condition and action language's API (issue #89): `Conditions`, `Vars`, `Quests.HasReached`; topics (issue #93): `DialogueTopics`, `AvailableTopic`, `TopicRecord`, `TopicInfo`, `KnownTopics`; and the vocabulary shorthand (`VocabularyAttribute.Shorthand`, `EntryValueAttribute`, `RecordStore.PolymorphicShorthand`); easing, timers and tweens (issue #90): `Ease`, `Easing` (`Apply`, `Lerp`, `IsMonotonic`, `TryParse`), `LogicTimer`, `LogicTimerPart`, `Timers`, `Tween`, `TweenPart`, `TweenChannel`, `Tweens`; logic entities and bridges (issue #91): `EntityInputs.Register<T>` / `Takes` / `ComponentsTaking`, `EntityIO.Fire` and `FireOutput` with a value, `LogicRelay`, `LogicRelayScript`, `LogicCounter`, `LogicCompare`, `LogicBranch`, `MathRemap` and their parts, `LogicEntities`, `BridgeIO`, `QuestWatch`, `QuestWatchPart`; state machines (issue #92): `StateMachineRecord`, `MachineState`, `StateTransition`, `StateMachine`, `ActiveState` (issue #280), `StateMachinePart`, `StateMachines`, `RecordStore.Latest`; the rest of the logic set (issue #281): `LogicMultisource`, `LogicCase`, `LogicAuto`, `LogicTrigger` and their parts, `LogicGates`, `LogicSpawner`, `LogicSpawnerPart`, `Spawners` (`Spawn`), `TweenLoop`, `Tween.Loop`/`Loops`, `TweenPart.Sequence`, `TweenSequence`, `LogicTimerPart.RandomMin`/`RandomMax` | Phase 4b is still building on it: wires, relays, state machines and topics will read it |
| SAGE0125 | The retained game UI (issue #95), all of `Sage.UI`: `UiRoot`, `Widget`, `Container`, `Box`, `Stack`, `Grid`, `Label`, `Button`, `Image`, `Bar`, `ItemList`, `Scroll`, `Tooltip`, `UiInput`, `UiResult`, `UiNavigation`, `ITextMeasure`, `MonospaceTextMeasure`, `IWidgetVisitor`, `WidgetTypes`, `Thickness`, `Anchors`, `Align`, `Orientation`; its records and text (issue #96): `UiModule`, `UiStyleRecord`, `UiStyleStates`, `UiStyleState`, `UiLayoutRecord`, `UiNode`, `ScreenRecord`, `UiStyles`, `UiStyle`, `UiStyleColours`, `UiState`, `UiScreens`, `UiScreen`, `UiView`, `UiBindContext`, `IViewModel` (with `Activate`/`Back`, issue #98), `ViewModelAttribute`, `Localisation`, `PluralCategory`; showing screens (issue #97): `UiScreenStack` (with `OpenHud`, issue #99), `UiLayer`, `UiTween`; and the RPG kit's view-models (issues #98, #99): `ItemGrid`, `GridItem`, `GridCell`, `ItemGridView`, `InventoryView`, `LootView`, `EquipmentView`, `TopicsView`, `JournalView`, `MapView`, `ShopView`, `IPriceRule`, `StubPriceRule`; the perks screen's `PerksView` (#381) | Phase 4c builds on it: records and localisation (#96), drawing, input and transitions (#97), the RPG screens (#98), the HUD, journal, map, menu and shop (#99) |
| SAGE0126 | Skeletal animation (issue #116, phase 4d): `Skeleton`, `AnimationClip`, `AnimationInterpolation`, `ClipEvent`, `SkeletonPose`, `JointMask`, `PoseSampler` (`Sample`, `Blend`, `ToModelSpace`, `ClipTime`), `AnimationSet`, `GltfAnimationReader`; GPU skinning (issue #117): `SkinMath` (`Palette`, `Blend`, `SkinPosition`, `SkinNormal`, `MaxBones`, `Influences`), `SkinnedMeshRenderer`, `SkinnedMeshPart`, `RenderStats.Skinned`/`Bones`; sockets and IK (issue #120): `SkeletonPoses`, `TwoBoneIk`, `AimChainIk`, `AimJoint`, `PoseSampler.ToModelSpace(…, firstJoint)`, `SkeletonSocketsRecord`, `SkeletonSocket`, `BoneAttachment`, `BoneAttachmentPart`, `BoneAttachments`, `AimIk`, `AimIkJoint`, `AimIkPart`, and in Gameplay `FootIk`, `FootIkLeg`, `FootIkPart`; animation graphs (issue #118): `AnimGraphRecord`, `AnimState`, `AnimBlendSpace`, `AnimBlendPoint`, `AnimLayer`, `AnimParam`, `AnimParamKind`, `AnimParamSource`, `Animator`, `AnimatorLayer`, `AnimatorParam`, `AnimatorPart`, `Animators` (`SetParam`, `SetTrigger`, `GetParam`, `StateOf`, `HasTag`, `ClipWeight`, `Play`, `TryGetPose`, `Describe`), `Engine.Animations`; first-person arms (issue #121): `ViewmodelRecord`, `Viewmodel`, `ViewmodelPart`, `ViewmodelLayer`, `Viewmodels` (`Show`, `ArmsOf`, `WeaponOf`, `CameraOf`, `IsDrawn`), and in Gameplay `AttackRecord.Arms`; clip events (issue #119): `AnimEventsRecord`, `AnimEventEntry`, `IAnimationEventSink`, `Animators.TryGetClip`, `Animators.SpriteSwingGraph`, `Animators.AnimEventOutput`. Complete for phase 4d: its exit (issue #122) added no API — an NPC walking, running, aiming and attacking, and first-person arms reloading, are content over these (`tests/games/skeletal`). Phase 4p added root motion (`RootMotionMode`, `AnimState.RootMotion`/`RootMotionY`, `AnimGraphRecord.RootJoint`, `Animators.TryTakeRootMotion`, issue #357), `AnimLayerBlend`, `AnimLayer.Blend`, `AnimBlendSpace.Sync` and `StateTransition.Fade`/`Ease` (#358), `AttackStance`, `AttackDirection`, `AttackDirectionInput`, `AttackStances` and `Animators.InWindow` (#359), `SkeletonMapRecord` and `ClipRetargeter` (#360), `LookAtIk`, `LookAtIkPart`, `HandIk`, `HandIkArm`, `HandIkPart` and foot IK's `MaxFootTilt`/`PelvisSmoothing` (#361), `Animators.CreatePreviewWorld` (#362), morph targets on `Skeleton`, `AnimationClip`, `SkeletonPose`, `JointMask`, `AnimGraphRecord.Morphs` and `Animators.GetMorphWeight` (#363) and `IAnimDebugSource` (#364) | Stable after the Sandbox's creatures move to skeletons and 4e's weapons build on it; until then it may change |
| SAGE0127 | Weapons and combat generalised (issue #133, phase 4e): the hit pipeline — `HitRequest`, `HitResult`, `Combat.ApplyHit`, the `hit_delivery` vocabulary (`IHitDelivery`, `HitDeliveryAttribute`, `HitContext`, `HitDeliveries`), the shared queries `Hits` (`Sweep`, `Ray`, `CanBeHurt`, `Launch`), `DamageInfo.Location`/`Damaged.Location`, `AttackRecord.Delivery`/`Range`/`Pellets`/`ProjectileSpeed` and `Projectile.Attack`; since #134 `AttackRecord.Projectile`/`ProjectileGravity`/`ProjectilePierce`, `Projectile.Gravity`/`Pierce`/`Passed` and the attack overload of `ProjectileExtensions.Launch`; hit locations (issue #137): the `hit_location` and `hitboxes` records (`HitLocationRecord`, `HitboxesRecord`, `HitboxShape`), the `hitboxes` part, `Hitbox`, `Hitboxes`, `HitLocations`, and the query-only physics layers (`LayerMatrix.QueryOnly`, `Sees`); ammunition (issue #135): `AttackRecord.Ammo`/`Magazine`/`AmmoPerShot`/`ReloadTime`/`Automatic`/`RateOfFire`, `Magazine`, `MagazineSlot`, `Ammunition`, `WeaponFired`, `DryFire`; spread and recoil (issue #136): the `spread` and `recoil` records (`SpreadRecord`, `RecoilRecord`), `AttackRecord.Spread`/`Recoil`, `WeaponState`, `ShotRandom`, `Spread`, `HitContext.Cone`/`Shot`/`PelletAim`; and in the RPG kit (issue #138) `AmmoReadout` and `EquipmentView.Ammo`. Since #138 a projectile lands on hitboxes like a sweep or a ray, with no API change | Phase 4e's exit (#139) is a data-only weapons game over it; stable once that and a game with real weapons have used it |
| SAGE0129 | The open world (phase 4g). The calendar and passing time (issue 4g-2): `CalendarRecord` (the `calendar` record: `Months`, `Weekdays`, `StartYear`, `StartWeekday`, `Default`, `DateOf`, `DaysIn`, `DaysInYear`, `WeekdayOf`, `WeekdayIndex`, `InWindow`), `CalendarMonth`, `GameDate`, `Calendars` (`Of`, `Today`), `WorldClock.Calendar`/`Date`, the `weekday` and `date_between` conditions, `Time.Pass` and the `TimePassed` event; cells that go dormant with their state (4g-1): `InCell` (the `sage:cell` component: the scene, or the streamed sector, a runtime spawn belongs to and goes to sleep with) and save format 4 (`SaveSystem.FormatVersion`: `dormant` cells by source, each with the sector its positions are relative to); entities that stream by sector (4g-3): `SceneRecord.Streamed` (a scene placed sector by sector, each sector a cell `sector:<scene>:<x>,<z>`), `SceneRecord.Terrain` and the `terrain` record (`TerrainRecord`, `TerrainGeneratorKind`: the built-in `Flat` and `Hills` ground); NPC routines (issue 4g-4): `RoutineRecord` (the `routine` record), `RoutineEntry`, the `Routine` component (`sage:routine`) and `RoutinePart`, `Routines` (`Of`, `EntryAt`, `StartOf`, `Target`), `RoutineTarget`, `AIProfileRecord.Routine` and `AIScheduleChoice.Routine` (the `in_routine` condition, `AICondition.InRoutine`, and the `MoveToAnchor`, `FaceAnchor` and `StayAt` tasks are content and not marked); load doors, interiors and fast travel (4g-5): `Travel` (`To`, `ToPoint`, `HoursTo`, `Use`, the `Travel` input), `SceneRecord.Space` and `SceneSpace` (`Interior`: no rings, no terrain, lit by its lights and `SceneEnvironment.Ambient`), `LoadDoor` (`sage:load_door`) and `LoadDoorPart`, `TravelPoint` (`sage:travel_point`) and `TravelPointPart`, `TravelLog` (the saved `travel` resource) and `TravelDestination`; off-screen simulation (issue 4g-6): the cell handoff in Simulation (`ICellHandoff`, `CellContent`: `AddHandoff`, `RemoveHandoff`, `Release`, `Restore`, `SceneOf`, `IsLive`, `CellAt`, `Generation`), and in Gameplay the `Offscreen` component (`sage:offscreen`) and `OffscreenPart` (the `offscreen` part), `OffscreenAgent`, `OffscreenAgents` (the saved `offscreen` resource), `OffscreenDied`, the `offscreen_fight` vocabulary (`IOffscreenFight`, `OffscreenFightAttribute`, the built-in `strength`), `OffscreenFights`, `OffscreenMap` and `Factions.AreHostile`; the RPG kit's rest and fast travel (4g-7): `Scenes.Current`, `Rest` (`Can`, `EnemyNear`, `Begin`, the `rest` command), `RestKind`, `RestView` (the `rpg_rest` view-model of the `rpg:rest` screen), `RpgConventionsRecord.RestEnemyRange`/`RestMaxHours`/`RestEffect`, and `MapView`'s travel (`Destinations`, `HasDestinations`, `TravelStyle`, `Activate`, `Marker.Point`/`CanTravel`/`TravelHours`); the far ring (#277): `PrefabRecord.Far` and `FarLook` (a prefab's `"far"`: `mesh`, `size`, `material`); seasons, moons, leap years and calendar events (#289): `CalendarRecord`'s `LeapEvery`, `LeapSkipEvery`, `LeapRestoreEvery`, `LeapMonth`, `Seasons`, `MoonCycle`, `MoonStart`, `IsLeapYear`, `SeasonOf`, `MoonAge`, `MoonPhaseOf`, `MoonLight`, `MoonPhaseNames`, `CalendarSeason`, `CalendarEventRecord` (the `calendar_event` record), `CalendarEventListener` (`sage:calendar_event`) and `CalendarEventPart`, `CalendarEvents.OnCalendarEvent`; off-screen pathing and seeding (#284): `CellContent.SeedUnplaced`, `OffscreenMap.Blocked`, `OffscreenAgent.Route`/`RouteGoal`; several streaming sources and followers (#290): `StreamingRing` (`sage:streaming_ring`) and `StreamingRingPart`, `Follower` (`sage:follower`) and `FollowerPart`; live spaces (#291): `SceneRecord.Live`, `SceneEnvironment.GravityScale`, `Scenes.LiveBeside`, `SpaceGravity`, `GravityRegion` and the saved `spaces` resource; splat terrain (#307): `TerrainMaterialRecord` (the `terrain_material` record), `TerrainLayer`, `Terrain.Material`, the `terrain` record's `material`; sculpted terrain (#372): `TerrainSculpt`, `SculptSector`, the `terrain` record's `sculpt`, `Terrain.Resculpt`, `Refresh` and `Refreshed` | Phase 4g's last pieces are built on it; its exit (4g-8) may reshape it. Stable once routines and the off-screen simulation have read `TimePassed`; a calendar per scene may still reshape the calendar |
| SAGE0130 | The world clock, sky records and weather composition (issue 4h-2, phase 4h): `WorldClock`, `WorldClock.Between`/`Format`/`TryParseHour`/`Of`, the `time_between` condition, `SkyRecord`, `SkyKey`, `SkyState`, `SkyRules` (`Evaluate`, `SunAt`, `Current`, `Apply`), `RenderEnvironment.Zenith`/`ShadowStrength`, `SceneEnvironment.Sky`/`Hour` and the weather record's `fogTint`, `skyTint`, `fogStartScale`, `fogEndScale`. The render pass registry (issue 4h-1): `RenderStage`, `RenderStages`, `RenderPassAttribute`, `RenderPassInfo`, `RenderPassRegistry<TPass>`, and in the client `IRenderPass`, `RenderPasses`, `ClientModule.Passes`, `RenderContext`, `RenderViewInfo`, and `Renderer.DeclareTarget` with a format and a depth buffer. Sun shadows (issue 4h-4): `ShadowMath` (`Fit`, `PerspectiveSlice`, `OrthographicSlice`, `ToClip`, `Contains`, `TexelOf`, `Strength`, `Casts`), `ShadowFit`, `MaterialRecord.CastShadows`, and in the client `RenderStats.ShadowCasters`. The sky and fog (issue 4h-5): `FogMode`, `FogMath` (`Factor`, `Density`, `CullDistance`, `Hides`, `ShaderParam`, `Invisible`), `SkyRules` (`StarsAt`, `Gradient`, `Haze`, `SunDisc`, `ColorAt` and their constants), `SkyRecord.FogMode`, `SkyKey.FogDensity`, `SkyState.FogDensity`/`Stars`, `RenderEnvironment.DrawSky`/`Stars`/`FogMode`/`FogDensity`, and in the client `RenderStats.FogCulled`/`Skies`. Post-processing (issue 4h-6): `PostEffectRecord` (the `post_effect` record) and `RenderStats.PostSteps`. Complete for phase 4h: its exit (issue 4h-7) added no experimental API — the Sandbox's dusk, night and lamps are content over these, and the light switch it needed (`PointLight.Off`/`Lit`, `LightPart.Off`, the `TurnOn`/`TurnOff`/`Toggle` inputs) is ordinary API. Phase 4n's first pack: the material record's surface fields (#410: `NormalMap`, `SpecularMap`, `Specular`, `Gloss`, `EmissiveMap`, `Emissive`, `VertexColors`, `EnvironmentMap`, `Reflectivity`), the sky extras on `RenderEnvironment` (#320: `HazeBand`, `HazeAbove`, `StarAxis`, `StarTurn`, `MoonTexture`, `MoonDirection`, `MoonAge`, `MoonSize`, `MoonLevel`, `CloudTexture`, `CloudCover`, `CloudScale`, `CloudScroll`), `ShadowAtlas` (#315), `RenderStats.ShadowMaps` (#323), and `RenderPasses.Replace`/`Disable` (#322) on the render pass API | Phase 4h is done; 4g adds the calendar and schedules on the clock. Stable once 4g and a game with its own sky, shadows and post chain have used it; cascades, HDR and bloom may still reshape the shadow and post halves |
| SAGE0131 | Saves you can trust (phase 4i). The save placeholder and header API, a load that cannot half-happen (4i-2): `SavePlaceholder` (an entity whose prefab is gone, kept inert and written back unchanged), `UnknownSavedData` (components and tags this game has no type for, written back unchanged), the header's plugins and content (`SaveSlot.Plugins`, `SaveSlot.Content`, `SaveSlot.Mismatches`, `SavedPlugin`, `SavedContent`). The prefab override API (4i-1): `PrefabOverrides` (`Placement.Overrides`, `PrefabChild.Overrides`), `PrefabChild`, `PrefabRecord.Children`, `PrefabRecord.Persist` (runtime spawns persist unless a prefab says `"persist": false`, 4i-4; such a spawn is `Unsaved` and a load removes it, 4m-4), `PrefabOverridden`, `FromParentPrefab`, the overrides overload of `PrefabExtensions.Spawn`, and `RecordCheck.TryGet`. Reconciling loads (4i-3): `Placement.Id` (a placement's authored id in saves) and save format 3 (`SaveSystem.FormatVersion`: sources, tombstones and the world's scene). Saving what changed (4i-5): an entity spawned from a prefab is saved as a diff against its prefab as spawned (`"diff"`, `"removed"` in format 3), and a load lays it over the current prefab. Quick-save and autosave (4i-6): `SaveKind` and `SaveSlot.Kind`, `SaveSystem.Save(slot, kind)`, `RequestSave`, `RequestLoad`, `QuickSave`, `QuickLoad`, `Autosave`, `NextAutosaveSlot`, `Delete`, `HasPendingRequests`, and the `QuickSlot`, `AutosavePrefix`, `QuickSaveAction` and `QuickLoadAction` names. Thumbnails, compression, the background write and the version report (#285): `SaveSystem.Thumbnail`, `SaveThumbnail`, `IsWriting`, `WaitForWrites`, `Report`, `QuickSlotName`, `AutosavePrefixName`, the title overloads of `Save` and `RequestSave`, `SaveSlot.Title`/`ThumbnailPath`/`Compressed`, `SaveVersionReport`, `SaveVersionEntry`, `SaveVersionStatus` | Phase 4i is building on both: the exit game (4i-7) may reshape them |
| SAGE0132 | Data mods (phase 4j). Mod manifests and the load order (4j-1): `ModManifest` (`mod.json`), `ModLoadOrder.Resolve`, `ModLoadResult`, `RefusedMod`, `ModList` (`user://mods.json`) and `Engine.Mods`. Merge provenance and the content report (4j-2): `RecordStore.Writes`, `RecordWrite`, `RecordWriteOp`, `ContentReport` (`Build`, `Lines`, `IsModMount`, `NameOf`, `ModMountPrefix`), `ContentMountReport`, `PatchedRecord`, `ContentConflict`, `ContentConflictKind`, `ShadowedAsset` and `VirtualFileSystem.Shadows`. Saves name their mods (4j-4): `SaveSlot.Mods` and `SavedMod`. Mods at boot (4j-3): `ModManager` (`Engine.ModManager`: what was found, `Enable`/`Disable`/`Move` for the next start, `Next`, `WatchManifests`) and `SageAppOptions.Mods`, `UserModsDirectory`, `ModListFile` and `ModReportFile`. `sage` with mods (4j-5): `ValidateOptions.Mods` and `GameMods`, `ValidationReport.Mods`, `ModLines`, `ReportLines` and `Conflicts`, and `RecordSchemas.Mod` and `Game`. The mods screen (4j-6): `Sage.UI.ModsView` (`ui_mods`) and the kit's `RpgKitModule.ModsScreen` | Phase 4j is done: its exit game (`tests/games/mods`, 4j-7) used it unchanged, data only. Stable once a game with players' mods has used it; code mods, `.sagemod` zips and namespaced assets (phase 9) may still reshape the manifest and the report |
| SAGE0133 | The editor's model (phase 10a, #215), all of `Sage.Editing`: since #216 `IEditorCommand`; since #217 `CommandLog`, `EditDocument`, its commands (`AddPlacement`, `RemovePlacement`, `SetPlacement`, `SetOverride`, `ClearOverride`, `SetOutputs`) and `EditorCommands`; picking and gizmo maths (#220): `EditorRay`, `EditorPicking` (`RayFrom`, `RayFromOrthographic`, `Pick`, `FallbackRadius`), `PickResult`, `TranslateGizmo` (`HitTest`, `Drag`, `AxisOf`), `GizmoHandle`, `RotateGizmo` (`HitTest`, `Drag`), `GizmoMath.ScreenConstantSize`, `Snap` (`ToGrid`, `Angle`); the palette (#222): `PrefabPalette`, `PrefabGroup`, `Placing` (`Surface`, `Place`, `PlaceAt`, `UniqueName`), `PaletteCommands`; in `Sage.Core`, `JsonFileEdit` (#218: a record file edited in place, comments and formatting kept — `Open`, `SetRecord`, `PatchRecord`, `AddRecord`, `RemoveRecord`, `Set`, `Remove`, `Read`, `ReadRecord`, `ToNode`, `Save`); the inspector on the document (#223): `InspectorModel` (`Of`, `Find`, `Set`, `Revert`, `TrySet`, `TryRevert`, `ToNode`), `InspectorGroup`, `InspectorRow`, `InspectorValue` (`TryParse`, `Format`) and `InspectorCommands`; the record browser (#224): `RecordDocument`, `SetRecordValue`, `RecordEditor`, and in `Sage.Core` `RecordStore.RawJson` / `MountOf`; the editor mode (#219): `EditTarget`, `LogView`, `World.Editing`, `RunCondition.EvenWhenEditing` and `Engine.CreateEditWorld` / `SageApp.CreateEditWorld`; selecting and moving (#221): `EditorSelection`, `ViewportTools` (`ed_select`, `ed_move`, `ed_rotate`, `ed_delete`, `ed_duplicate`, `ed_snap`, `ed_grid`, `ed_angle`, `ed_gizmo`), `GizmoDrag`, `GizmoMode`, `ViewportCamera`, `EditorPicking.PickWhere`; wiring (#225): `Wiring` (`Add`, `Update`, `Remove`, `Check`, `InputsOf`, `TargetEntity`, `NameOf`, `NameIt`, `Describe`; `ed_wire`, `ed_unwire`, `ed_wires`), `WiringModel`, `WireInput`, `WireOutput`, `WireLine`; play-in-editor (#226): `PlaySession`, `PlayStart`, `PlayCommands` (`ed_play`, `ed_stop`) and `Engine.CreatePlayWorld`; the problems panel (#227): `ProblemList`, `Problem`, `ProblemGroup`, `ProblemSeverity`, `ProblemCommands` (`ed_problems`), and in `Sage.Core` `RecordStore.LoadErrors` / `LoadWarnings`; the command log, documents, forms, wiring and play sessions as 10a builds them; the exit (#228) added no API (`InspectorModel.Find` now prefers a row that can be edited, so `mover.seconds` is the part's); phase 10b (#365): the asset browser (#366: `AssetBrowser`, `AssetPicking`, `AssetRename`, `AssetCommands`, and in `Sage.Core` `RecordStore.Preview` and `Previewed`), several selected and scaled (#367: `EditorSelection.Placements`, `ScaleGizmo`, `GizmoSpace`, `SetPlacements`, `CommandGroup`), nested inspector rows (#368: `InspectorModel.AddItem`, `RemoveItem`, `MoveItem`, `AddComponent`, `RemoveComponent`, `SetOverrideBody`), `AIGraph` (#369), `VocabularyCatalog`, `VocabularyForm` and `VocabularyEditor` (#370), `PanelTour` and `EditorStatus` (#371), `TerrainDocument`, `PrefabCommands` and `RevertPlacement` (#372), `EntityLabelCache` and `LogView.Texts` (#374), `BlockoutTools` (#61) and `EditorWorkspace` (#375) | Phases 10a and 10b are done: the exit game (`tests/games/editor`) was built through these commands alone and loads in a plain boot. Stable once a game's own levels have been built with it |
| SAGE0134 | Physics joints and ragdolls (phase 4k, #130). Joints and collision groups in the physics facade (4k-1, #242): `JointKind`, `JointDesc` (`FromWorld`), `PhysicsJoint`, `JointBroken`, and on `IPhysicsWorld` (and `PhysicsSpace`) `AddJoint`, `RemoveJoint`, `JointExists`, `JointCount`, `JointBroken`, `AddBody(…, group)`, `SetGroup`, `GroupOf`, `AngularVelocityOf`, `SetAngularVelocity`, `ApplyImpulse`; the `joint` part (4k-4, #245): `Joint` (the `sage:joint` component), `JointPart` (the `joint` part), `BodyMotion` (the saved `sage:body_motion`: a dynamic body's linear and angular velocity) and `PhysicsJointIO` (the `Break` input, the `OnBreak` output). The `ragdoll` record (4k-2, #243): `RagdollRecord`, `RagdollBodyData`, `RagdollJointData`, `RagdollShape`, `RagdollJointKind`, `ResolvedRagdollBody`, `RagdollTable`, `Ragdolls.Resolve`. Handing an animator over (4k-3, #244): `Animators.Suspend`, `Resume`, `IsSuspended`, `PlayFrom`, `Animator.Suspended`. Going ragdoll (4k-5, #246): the `Ragdoll` component and `ragdoll` part (`RagdollPart`), the `Ragdolled` tag, `Ragdolls.Start`, `Stop`, `IsActive`, `ApplyImpulse`, `Ragdolls.Input`, `SystemId`, `FirstGroup`, and `BoneAttachment.Drop` (with the part's `drop`). Settling and saves (4k-7, #248): `Ragdolls.IsSettled`, `Ragdolls.OnSettled` (the output), `Ragdoll.Settled`, `RestTime` and `Bodies`, `RagdollBodyState`, and `IPhysicsWorld.Sleep` (and `PhysicsSpace.Sleep`). Getting up (4k-6, #247): `Ragdolls.GetUp`, `IsFaceUp`, `GetUpInput`, `GetUpSystemId`, the `RagdollGetUp` component (`sage:ragdoll_get_up`: `After`, `Back`, `Front`, `Fade`, `Down`, `StayDown`, `BackState`, `FrontState`, `Defaults`, `DefaultBack`, `DefaultFront`, `DefaultFade`) and the `ragdoll` part's `getUpAfter`, `getUpBack`, `getUpFront`, `getUpFade`. Complete for phase 4k: its exit (#249) added no API — a sentry knocked over on a wire that gets back up, and an NPC killed that stays down, are content over these (`tests/games/skeletal`) | Phase 4k is done (#130). Stable once a game's own characters ragdoll and get up on it (the Sandbox's creatures are still sprites); powered or partial ragdolls may still add members to the facade and the `ragdoll` part |

SAGE0120–0139 are for experimental areas (0128 was reserved for phase 4f, which shipped its API stable and took none; phase 4g shares SAGE0129, 4h SAGE0130, 4i SAGE0131, 4j SAGE0132, 10a SAGE0133 and 4k SAGE0134); an id is never reused once an area leaves.

---

## 11. What is not here yet

Worth knowing before you plan around it:

- **The editor's brushes are simple solids (phases 10a and 10b done).** §8a and
  [docs/EDITOR.md](EDITOR.md): place, select, move, turn, scale, override, wire, block out, sculpt, undo,
  play and save. Brushes have no CSG, vertex editing or brush entities, so detailed interiors are still
  better drawn in TrenchBroom as `.map` files (§5), and the AI graph view edits state machines, schedules and
  routines, since the engine has no behaviour trees.
- **Skeletal animation is new (phases 4d and 4p, done; SAGE0126).** Most of the Sandbox's creatures are still billboard
  sprites with direction groups (the Daggerfall model); the hut walker and the brute are skinned; `tests/games/skeletal` has skinned NPCs that walk,
  run, aim and attack, blended (issue #122), and fall as ragdolls when they die (`"ragdoll": {}`, issue #246, SAGE0134) and get back up (`Ragdolls.GetUp`, the `GetUp` input or the part's `getUpAfter`, issue #247); its tumbler is knocked over by a wire and gets up, and its casualty is killed and stays down (phase 4k's exit, issue #249). Phase 4p added root motion, additive layers, retargeting, look-at and hand IK, directional swings and guards and morph targets (above, "More from a graph"); what a guard does to a blow is 4r's. Skeletons and clips are read from a skinned `.glb`, and poses sampled,
  blended and put in model space headlessly (SAGE0126, issue #116); a skinned `.glb` draws with the
  `skinned_mesh` part and is skinned on the GPU, up to 64 joints a draw (issue #117). The `animator` part
  plays an `anim_graph` on it — blend spaces, layers, cross-fades (issue #118). `r_testskin 1` shows a
  generated one bending. Sockets, bone attachments and IK are in
  (issue #120): a sword with `"bone_attachment": "hand_r"`, parented to a character, follows the hand of
  whatever pose is registered for it in `SkeletonPoses`; `aim_ik` turns the spine toward a pitch and yaw;
  `foot_ik` plants the feet on slopes through the physics world. The animator registers its pose there
  (docs/design/12 "As built (attachments and IK)"). First-person arms holding a weapon are a `viewmodel`
  record an attack names, drawn over the world in first person only (issue #121). Clip events
  (`anim_events`, issue #119) land blows and drive transitions, for skinned clips and sprite sheets alike
  (docs/design/12 "As built (animation events)").
- **Lamp shadows are few.** A `light` entity lights a room, and four of them light any one surface (the
  strongest four, chosen per draw). A brush level can bake its unswitched lamps into a lightmap, with
  shadows (issue #313, no bounce light), the sun casts cascaded shadows and lamps that say `shadows` cast
  too (issue #315, the nearest two by default).
- **No multiplayer.** The engine follows rules that keep it possible (fixed tick, data-only components,
  no gameplay in rendering), but there is no networking. That is Phase 7.
- **Mods are data only (phase 4j).** A mod is a folder of records, strings and assets with a `mod.json`
  (docs/MODDING.md); there are no code mods, no `.sagemod` zips and no namespaced asset paths yet — two
  mods shipping one texture is reported as a conflict and the later one wins. Those are phase 9.
- **Saves cover the simulation, not your UI state.** A mover slides; it pushes a player out of its way,
  shoves a crate (#260), carries whoever stands on it (#261), swings on a hinge and follows a path of
  stops (#266), but a path is straight lines between stops, and a rider is carried round a turn, not turned with it.

---

## 12. Where to look next

| If you want | Read |
|---|---|
| Why the engine is shaped like this | [`ARCHITECTURE.md`](../ARCHITECTURE.md) |
| What is built and what is next | [`TODO.md`](../TODO.md) |
| The record pipeline and the VFS | [`docs/design/05-assets-and-vfs.md`](design/05-assets-and-vfs.md) |
| Levels, brushes and the FGD | [`docs/design/15-editor.md`](design/15-editor.md) |
| Wiring, events and entity I/O | [`docs/design/04-events-and-messaging.md`](design/04-events-and-messaging.md) |
| Gameplay: rules, AI, abilities | [`docs/design/16-gameplay-framework.md`](design/16-gameplay-framework.md) |
| The HUD and screens | [`docs/design/13-ui.md`](design/13-ui.md) |
