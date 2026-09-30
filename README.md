# Sage

[![CI](https://github.com/ZohnHadley/sage-engine/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ZohnHadley/sage-engine/actions/workflows/ci.yml)

A game engine in C# and MonoGame, built by writing a game with it. The target is a **Daggerfall-like
first-person RPG**: a big outdoor world, billboard creatures, a kinematic character controller,
data-driven records you can edit while the game runs, and mods that are data first and trusted C#
second. Later targets — an HL1-like, a Lugaru-like, a Warband-like — are what keep the design honest
about skeletal animation, melee and large battles. Multiplayer is deliberately later, and until then
the code follows a short list of readiness rules so adding it isn't a rewrite.

Nothing here is a product. It is an engine that grows one feature at a time, each one dogfooded by
the `games/Sandbox` test game in the same commit.

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — the overview: layers, rules, decisions and why.
- [`docs/design/`](docs/design) — one document per subsystem, each with an "As built" section saying
  what actually exists today. [`00-index.md`](docs/design/00-index.md) is the reading order and the
  glossary.
- [`TODO.md`](TODO.md) — the roadmap, with every item's id (F7, R9…) as the docs reference them.
- [`docs/REDESIGN.md`](docs/REDESIGN.md) — the plan the engine is following now: a base engine for
  designers, developers and modders, action RPGs first, 2D and 3D. Tracked as GitHub issues.
- [`docs/history/handoff-2026-09-30.md`](docs/history/handoff-2026-09-30.md) — **start here after a
  break**: what landed, how to build, test and verify it now, what is next, and the traps that have
  already cost a session.
- [`docs/history/code-review-log.md`](docs/history/code-review-log.md) — every bug worth remembering,
  with what it was and why it happened.
- [`docs/RELEASING.md`](docs/RELEASING.md) — the version (from git tags), the declared public API
  (`PublicAPI.*.txt`), experimental areas, the `sage` range plugins and games declare, and how to tag a
  release.

## What runs today

The first vertical slice is finished (2026-09-23): **you walk a heightmap sector, a creature hunts you
down and swings, you swing back with a sword you picked up, you throw a fireball that flies and bursts
— or one you invented yourself — and a save brings all of it back in a new process.**

Since then the engine has been following [`docs/REDESIGN.md`](docs/REDESIGN.md). Stage A (phases 0–3)
carved a base engine out of it, with an RPG kit on top. Stage B is building the action-RPG base:
cameras (4a), timers, state machines and logic entities (4b), the UI toolkit and RPG screens (4c) and
skeletal animation (4d) are done; weapons and combat on one hit pipeline (4e, #132) are in progress.

| Area | What exists |
|---|---|
| Core | Fixed 60 Hz tick with render interpolation, phases with ordering and dev-asserted contracts, logging with categories, cvars and a console, crash reports, three build configurations |
| World | Friflo ECS behind a thin `World`, in Sage's own vocabulary (`Entity`, `IComponent`, `Query<…>`, `EntityCommands`; no Friflo type in the API), several worlds per engine, hierarchy and transform propagation, one typed event bus with per-reader cursors, world resources |
| Content | One JSON record pipeline for every definition (items, spells, materials, AI, input maps, prefabs…) with namespaces, inheritance, per-field patch merge, validation and hot reload; a layered VFS; records that can also be made at run time |
| Rendering | Extract → pooled snapshot → fixed passes, our own shaders through `dotnet-mgfxc`, material records, 8-direction billboards with sprite animation, GPU-skinned models, heightmap terrain, debug draw |
| Cameras | Cameras are entities with a director: first- and third-person rigs with the `V` toggle, scripted cuts from entity I/O, several views a frame and named render targets |
| World | An unbounded grid of 1024 m sectors: terrain streams in and out in rings around the player, and the simulation rebases so nothing is ever far from its own origin — verified 120 km out |
| Physics | BepuPhysics per world behind handles, layers, raycast/sweep/overlap, triggers, and our own kinematic character controller |
| Gameplay | `GameRules`, controller → pawn intent → movement, attributes/tags/effects, one damage pipeline, items and equipment, abilities and projectiles, a spellmaker, HL1-style AI that chases, swings and casts — and now walks round what is in the way, remembering what it can no longer see |
| Weapons | Every attack goes through one hit pipeline (`hit_delivery`): a melee sweep, a hitscan ray with pellets, or a projectile on the same carrier abilities use. Attacks can spend ammunition from the bag, with magazines kept per attack in the save and a reload that lands on the animation's `mag_in`. Arced bolts, spread and recoil, and hit locations are being built (4e) |
| Logic | One condition/action language for designers; timers, tweens and a saved entity I/O queue; state machines as records; logic entities (relays, counters, compares, branches) and conditional wires — a scripted sequence or a dialogue topic needs no C# |
| Animation | Skeletons and clips from glTF, sampled headlessly; `anim_graph` records with blend spaces, layers and cross-fades; sockets, attachments, aim and foot IK; clip events that time hits and reloads; first-person arms |
| Audio | Positional one-shots from the events the simulation already raised, looping sources, volume buses and voice limiting — the mixer is engine-side and tested headlessly, the noise is the client's |
| Particles and weather | Sparks, embers, smoke and blood from the cues and hits the simulation already raised — pooled and budgeted engine-side, drawn as tinted billboards — plus damage numbers over the fight, and rain or snow that rolls in over the seconds you give it |
| Factions | Who counts as an enemy: stances between factions, a standing toward the player that killing moves, creatures that fight each other, and blasts that spare their own side |
| Dialogue and quests | Conversations as records — nodes, options gated on what you carry, what they think of you and what you are on — plus quests whose stages advance when their objectives are met, and a journal that counts them |
| Screens | `Sage.UI`, a retained, headless widget toolkit with style, layout and screen records, localisation and gamepad focus; the kit's inventory grid with weight, equipment, loot, topics, journal, map and shop screens, the HUD and a main menu that loads a save — what a screen shows comes from the simulation, so it is asserted by headless tests |
| Persistence | Prefabs, and saves that rebuild an entity from its prefab plus the state written over it — references, attribute values and tags stored by identity, not by this run's indices |
| Tools | Hot reload for records and textures, scripted input for repeatable checks, a Daggerfall importer that dresses the Sandbox in your own copy's art, 1118 headless tests | <!-- counts -->

What is deliberately **not** here yet: ragdolls, mod loading, a standalone editor (today's
is a dev-build overlay on the running game), and multiplayer. The roadmap in [`TODO.md`](TODO.md) says
where each one sits, and [`docs/REDESIGN.md`](docs/REDESIGN.md) is the plan for what comes next.

## Running it

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) or newer (pinned as a minimum in
`global.json`), and ideally the .NET 8 runtime too: the projects target net8.0 and roll forward to a
newer runtime only when .NET 8 isn't installed. From the repo root:

```bash
dotnet run --project src/Sage.Host -c Debug -- -game games/Sandbox
```

`-game` names the game folder; without it the host stops and lists the games it can see, rather than
guessing (it used to load the Sandbox). `-c Development` is the same thing optimised, and noticeably
smoother. A game built with `Sage.Sdk` passes `-game` itself: `dotnet run --project games/Hello`.

To make a game of your own, outside this repository, pack the SDK and start from a template
([MAKING_A_GAME §2](docs/MAKING_A_GAME.md#2-the-shape-of-a-game)):

```bash
tools/pack_sdk.sh ~/sage-feed --build
dotnet new install ~/sage-feed/Sage.Templates.*.nupkg   # the engine's version, from git tags
dotnet new sage-game -n MyGame -o ~/games/MyGame --feed ~/sage-feed && cd ~/games/MyGame && dotnet run
```

You should get a hilly field, some creatures, some crates falling through a trigger, a creature that
notices you and comes over to hit you, and a firebug that stands off and throws bolts of fire at you.
Walk up the path to the hut on the hill and a scripted camera shows it to you for three seconds. The HUD shows your health, what is in your hands, what
pressing `E` would pick up, and what just hit you.

### Controls

| Input | Does |
|---|---|
| `W` `A` `S` `D`, mouse | move, look |
| `Space` | jump |
| `Left Shift`, `Left Ctrl` | run, crouch |
| **Left mouse** | attack |
| `E` | use |
| `V` | first person / third person over the shoulder |
| `R` | reload |
| `I`, `B`, `M` | your bag, your spellbook, the spellmaker (↑↓ or the mouse to choose, Enter or click to use, Del or right-click, Esc or click away to close) |
| `C`, `J`, `N`, `F10` | status, journal, map, the main menu (load a save) |
| `Escape` | quit (closes the console, or an open screen, first) |
| `` ` `` | open the console |

Bindings live in [`engine_content/data/input.json`](engine_content/data/input.json) and are records
like everything else, so a game or a mod patches them rather than replacing them.

### The console

`` ` `` opens it. `help` lists what to type, `cmdlist` and `cvarlist` dump everything, `find <text>`
searches both. Some worth knowing:

| | |
|---|---|
| `sv_cheats 1`, then `god`, `hurt 30` | invulnerability; run damage through the pipeline by hand |
| `r_stats`, `phys_stats`, `mem`, `stat` | draw calls and batches, bodies and step time, allocations |
| `cam_free 1`, `cam_set <x> <y> <z> [yaw] [pitch]` | detach the camera from the player and fly it |
| `ed_viewport 1` | the free camera in a window (a render target), beside the game |
| `sv_cheats 1`, then `ent_fire hut_cam CameraOn 3` | the scripted cut the path to the hut triggers, by hand |
| `vid_width 1600`, `vid_height 900` | the window's size (saved) |
| `screenshot [delay]` | PNG into `user/sandbox/screenshots` |
| `rec_list`, `rec_get prefab watcher`, `rec_reload` | what records loaded, one record's values and where each field came from, reload them |
| `vfs_mounts`, `vfs_which textures/creature.png` | the mount stack, and which mount a path resolves to |
| `pause`, `host_timescale 0.3`, `sim_tickrate 30` | stop time, slow it, change the tick rate |
| `warp 120000 -80000`, `stream_status`, `stream_radius 2` | go a hundred kilometres away and watch the world stream in around you; the simulation stays near its own origin |
| `scale_spawn 2000 tree`, `scale_report` | fill the world and print what a frame costs, phase by phase ([the numbers](docs/history/scale-2026-09-24.md)) |
| `r_sprite_facecamera 1` | turn billboards toward the camera's position instead of the view plane |
| `in_axis Move 0 1 3`, `in_look 40 0 3`, `in_tap Attack` | drive the game without a keyboard (see below) |
| `wait 2` | pause a script here; the rest runs later |
| `quit [seconds]` | exit now, or after a while (Escape and the close button always work) |
| `give <item>`, `inv`, `equip <item>`, `drop <item>` | items: `give practice_sword` then `equip practice_sword` and watch the combat log change |
| `spells`, `cast fireball`, `learn <ability>` | magic: `cast fireball` throws a burning ball that bursts on what it hits, and costs mana |
| `spell_effects`, `spell_make "my fire" sage:burning target=projectile damage=20`, `spell_list`, `spell_forget` | the spellmaker: build a spell out of effects and cast it like any other. It goes in the save as *what you chose*, so loading composes it again |
| `ui_crosshair 0` | hide the crosshair (it is on while a camera rig has the view) |
| `r_debugdraw 1`, then `phys_debug 1`, `ai_debug 1`, `combat_debug 1` | see the simulation: colliders and capsules, sight cones and targets, every swing and what it found. `r_debugdraw_xray 1` draws it through walls |
| `modules`, `sys_list`, `ent_list` | what is loaded, what runs each phase, what exists in the world |
| `version`, `plugins` | the engine's version (SemVer from git tags, [RELEASING.md](docs/RELEASING.md)), and every plugin with its version and the ranges it needs |
| `asset_list`, `asset_reload [path]` | what art is loaded; reload one file or all of it |
| `save [slot]`, `load [slot]`, `saves` | write and read a save; JSON under `user/sandbox/saves/`, so you can read it |
| `snd_stats`, `snd_play boom_fire`, `snd_volume 0.5` | what is playing with its gain and pan, an audition of one sound, and the buses |
| `nav_debug 1`, `nav_stats`, `nav_enabled 0` | the cells a creature thinks are blocked and the corners it is walking, what planning cost, and the A/B that turns it all off |
| `rep`, `rep_set sandbox:beasts 50` | what every faction thinks of you, and a thumb on the scale |
| `quests`, `quest_start thin_the_wood` | what you are on and how far, and a way to skip the asking |
| `fx_play fire_burst 60`, `fx_stats` | throw a burst in front of you, and see what is alive and what was refused |
| `weather rain 8`, `weather clear 3` | roll a storm in over eight seconds, and let it pass over three |
| `ent_dump watcher`, `ent_types`, `ent_spawn goblin` | every component on a thing with its values; the names a prefab can use; place one in front of you |

Anything can also be passed on the command line: `+sv_cheats 1 "+hurt 30"` runs them once the world
is up, and `-dev`-style options are plain `+cvar value` pairs.

### Driving it without a keyboard

Scripted input drives the *actions*, not the simulation, so it goes through bindings → `PlayerCommand`
→ the character controller exactly as a keyboard does — which is what makes it worth checking. Add
`wait` and a sequence of launch arguments is an automated check:

```bash
dotnet run --project src/Sage.Host -c Development --   "+in_axis Move 0 1 3" "+in_look 40 0 3" "+wait 3" "+in_tap Attack" "+wait 1" "+screenshot"
```

| Command | What it does |
|---|---|
| `in_axis <action> <x> [y] [seconds]` | drive an axis (`Move 0 1` is forward); no seconds means until `in_clear` |
| `in_hold <action> <seconds>` | hold a button down |
| `in_tap <action>` | press it for one frame (one per frame: put a `wait` between two) |
| `in_type <text>` | type into whatever has the keyboard, for a screen with a field |
| `in_cursor <x> <y>` | put the pointer somewhere, in window pixels |
| `in_look <yaw°/s> [pitch°/s] [seconds]` | turn the view at a steady rate, in degrees per second |
| `in_release <action>`, `in_clear` | hand control back |
| `in_scripted` | what a script is holding right now |
| `wait [seconds]`, `wait_cancel` | pace a script; no argument means next frame |

`exec <file>` runs a file of these from the user folder, and `wait` works inside it.

### Editing content while it runs

`developer` defaults to 1 in a Debug build, which turns on hot reload for both records and assets.

Edit `games/Sandbox/content/data/scene.json` (what is in the world) or `engine_content/data/*.json`
(movement, AI, combat, materials, input) and save: the records reload and the scene respawns. Tuning
the creature's claws or your own reach mid-fight is the intended way to work.

Save a **texture** over one the game has loaded and it swaps on the spot — repaint a sprite sheet or
re-run the Daggerfall importer and the creatures change without a restart. Only the file that changed
is reloaded, and a half-written one leaves what was already on screen. `asset_reload` forces it,
`asset_list` shows what is loaded, and `asset_hotreload 0` turns the watching off.

Open the repository in **VS Code** and record files are checked as you type: autocomplete for fields,
component and part ids and the ids content defines, the tooltip and unit of a field on hover, and a red
underline under a misspelt one. The JSON Schemas behind it are generated into `schemas/` (committed;
CI fails when they are stale) and mapped by `.vscode/settings.json`; after adding a field, a component,
a part or a record, regenerate them:

```bash
src/Sage.Cli/bin/Development/net8.0/sage schema games/Sandbox games/Hello tests/games/scene-only tests/games/camera-cut tests/games/scripted-sequence tests/games/topics tests/games/skeletal --out schemas
```

[`docs/MAKING_A_GAME.md`](docs/MAKING_A_GAME.md) §3, "Editing records in VS Code", has the details.

## Building and testing

```bash
dotnet build Sage.sln -c Debug          # Debug | Development | Shipping
dotnet test tests/Sage.Tests/Sage.Tests.csproj -c Debug     # or -c Development; not Shipping,
                                                            # which compiles out what six tests assert
python tools/check_docs.py --tests N    # the docs against the code (R19), N as dotnet test reported
```

`check_docs.py` reads the engine's names from a **registry dump** — every command, cvar, record type,
component, prefab part, system, entity input and output and input action the Sandbox registers, with
the plugin that registered each and the declarations' fields — which the host writes and then quits:

```bash
dotnet build Sage.sln -c Development -p:SageSkipShaders=true
(cd src/Sage.Host/bin/Development/net8.0 && xvfb-run -a ./Sage.Host -game ../../../../../games/Sandbox \
    -dump-registry ../../../../../user/registry.json)   # what check_docs reads; Windows: no xvfb-run
```

It used to find them with regular expressions over the registration calls, and the day `[Record]` gained
a `Plugin` argument that counted 1 record type instead of 25 without a word; a name in the dump was
registered by running code (test: ANameRegisteredAnyWayAtAllIsInTheDump). A test or tool writes the same
file headlessly with `RegistryDump.Write(engine, path)`.

`check_docs.py` is there because three features in a row ended with the code being right and something
it *said about itself* being wrong. It checks that links resolve, that a command or cvar named in a
claim about what exists is really in the code, and that the numbers quoted on a `<!-- counts -->` line
are the numbers there are. Behaviour it cannot check — so an "As built" bullet cites the test that
proves it, `(test: …)`, and the script checks the name resolves.

**CI** ([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs all of this on every push to `main`
and every pull request:
- **Linux** builds the whole solution in all three configurations, the host without its shaders
  (`-p:SageSkipShaders=true`, because `mgfxc` needs Wine on Linux). It runs the tests in Debug and
  Development on the .NET 8 runtime the projects target, writes the registry dump with the host under a
  virtual display, and runs `check_docs.py` against it with the count the tests reported. It runs
  `sage validate` on each game, and regenerates the JSON Schemas with `sage schema` and fails if the
  committed `schemas/` differs. Then it runs the real executable for a few seconds on Hello and the Sandbox under a
  virtual display ([`tools/smoke_run.sh`](tools/smoke_run.sh)), and fails on a crash, a bad exit or any
  warning outside the categories it expects. Last, it packs `Sage.Sdk`, `Sage.Player` and the templates
  into a local feed ([`tools/pack_sdk.sh`](tools/pack_sdk.sh)) and makes a game from each template in a
  folder outside the checkout: built against the packages alone, validated, shown to get the Sage
  analyzers, and started with `dotnet run` under the virtual display, walking.
- **Windows** builds the whole solution, shaders included, and runs the tests; then builds a template
  game with a shader of its own against the packed SDK, which is where `mgfxc` runs for a game.

Both jobs check out the whole history (`fetch-depth: 0`), because the version comes from git tags, and
both jobs' builds check the **declared public API**: each assembly games compile against keeps
`PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`, and a public change the files do not have is a
build error (RS0016/RS0017; Linux also shows it on purpose). [`docs/RELEASING.md`](docs/RELEASING.md)
says how to update them.

Warnings are errors (`Directory.Build.props`), and package versions live in one place
(`Directory.Packages.props`). `main` is
protected by the ruleset in [`.github/rulesets/protect-main.json`](.github/rulesets/protect-main.json):
changes go through a pull request with both CI jobs green, and `main` can't be force-pushed or deleted.

Three configurations, as UE does it: **Debug** (asserts, verbose logs, `developer 1`), **Development**
(optimised, still has the console, cheats and dev tools) and **Shipping** (no dev cvars, no console;
it needs `-game <folder>` because it looks for a `game` folder beside the executable). `SAGE_DEV` is
defined in the first two.

The tests are all headless — no window, no graphics device — because the simulation has no MonoGame
dependency. That is the same property a dedicated server would need, so it is checked by the build.

## What's in the repo

| Path | What |
|---|---|
| `src/Sage.Core` | The kernel: cvars and console, logging, VFS, records, plugins, declarations. **No ECS, no MonoGame.** |
| `src/Sage.Simulation` | The simulation: engine and app, ECS, prefabs, saves, levels, streaming, entity I/O. **No MonoGame.** |
| `src/Sage.Physics3D` | Bepu physics and the character controller, a base plugin. **No MonoGame.** |
| `src/Sage.Gameplay` | Generic gameplay plugins: attributes, combat and weapons, items, abilities, AI, navigation, factions, animation. **No MonoGame.** |
| `src/Sage.UI` | Retained game-UI widgets, layout, focus and screen records on the simulation, headless (issue #95). **No MonoGame**; the client draws its render plan |
| `src/Sage.Kits.Rpg`, `src/Sage.Kits.Rpg.Client` | The action-RPG kit (issue #27), *not* part of the base: the readied spell, the spellmaker, two hands, the bag, spellbook, journal and conversation screens. A game opts in with `"kits": ["sage.kits.rpg"]` in `game.json` (MAKING_A_GAME §2) |
| `src/Sage.Client` | Rendering, input devices, assets, sprite batching — the MonoGame half |
| `src/Sage.Editor` | Dev camera, console window, entity outliner, stat overlay (ImGui) |
| `src/Sage.Host` | The executable: boot sequence and the main loop |
| `src/Sage.Cli` | `sage`, the headless command line: `sage validate <game>` checks a game's content and exits non-zero on errors; `sage schema <game>` writes the JSON Schemas record files are edited with |
| `schemas/` | Those JSON Schemas for engine content, the Sandbox, Hello, `tests/games/scene-only`, `tests/games/camera-cut`, `tests/games/scripted-sequence`, `tests/games/topics` and `tests/games/skeletal`, generated by `sage schema` (never edit by hand); `.vscode/settings.json` maps them onto `data/**/*.json` |
| `src/Sage.Generators` | The declarations generator (issue #16): `[Record]` and `[SavedResource]` register themselves for the plugin that owns them |
| `games/Sandbox` | The test game's **simulation**: its module, scene records, placeholder art and tools. References only the base engine (`Sage.Core` … `Sage.Gameplay`) and the RPG kit it is built on, so it is testable headlessly |
| `games/Sandbox.Client` | The same game's **client half**: the HUD, and the prefab part that needs a renderer |
| `engine_content` | Engine-owned data and shaders, mounted under the `sage:` namespace |
| `sdk/` | `Sage.Sdk`, the MSBuild SDK a game builds with (`<Project Sdk="Sage.Sdk/<version>">`), and the packing of it, of `Sage.Player` (the host and `sage`, per configuration) and of the `dotnet new` templates (`sage-game`, `sage-game-data`, `sage-mod-data`) |
| `tests/Sage.Tests` | xUnit, headless, in folders by subject (`Core`, `Ecs`, `Gameplay`, `Maps`, `UI`…) |
| `tests/Sage.Testing` | The harness tests boot through — `HeadlessApp`, `MountFixture`, `EventProbe`, `CaptureSink` — for the engine's tests and a game's |
| `tools/` | Content tools that are not part of the build (the Daggerfall importer) |
| `third_party/` | One vendored dependency: MonoGame.ImGuiNet, dev-only (see its `PATCHES.md`) |
| `user/` | Written at runtime: logs, config, screenshots, crash reports (not in git) |

## Placeholder art

`games/Sandbox/tools/make_placeholder_art.py` generates the Sandbox's creature, tree and fireball sprites. The
creature is deliberately crude but it encodes the engine's direction convention: a nose that swings
with the view angle, a pack when seen from behind, and bars at its feet counting the direction group,
so a screenshot says which of the eight groups was picked. Regenerate with:

```bash
python games/Sandbox/tools/make_placeholder_art.py
```

Two more generators make the rest of the art the repository owns, because **nothing here is built by a
content pipeline** — since R12 every asset is a file the engine reads at runtime, so anything the repo
ships it has to be able to make:

```bash
python games/Sandbox/tools/make_placeholder_model.py   # bunny.glb: five boxes, written as glTF by hand
python games/Sandbox/tools/make_hut.py                 # hut.map + its textures: the brush level (F16)
python engine_content/tools/make_font.py               # font.png: the 5×7 HUD font, 100 glyphs
```

`make_font.py --preview "hello"` prints glyphs as ASCII, which is how you check a letter before
building the atlas — `g` and `y` both had to be redrawn after a screenshot read them as `9` and `v`.

## Making a game with it

[`docs/MAKING_A_GAME.md`](docs/MAKING_A_GAME.md) is the practical guide: the shape of a game, the records
you write, how to get a character walking about, levels, your HUD, and the list of things that silently
do nothing if you forget them. It describes the engine as it is rather than as it is planned.

[`games/Hello`](games/Hello) is that guide as a runnable game — the smallest one this engine can run:
four files, 46 lines of code. It is in the solution and covered by tests, so it cannot rot. <!-- counts: files games/Hello, code games/Hello -->

```bash
dotnet run --project src/Sage.Host -c Development -- -game games/Hello
```

## Levels

> **Changing:** levels will be built in Sage's own editor rather than TrenchBroom (decided 2026-09-28,
> `docs/REDESIGN.md` §4.6 and §6 item 8). What follows is how it works today, and the `.map` importer
> stays until the editor replaces it.

Interiors are **brushes**, and the editor is [TrenchBroom](https://trenchbroom.github.io/): the engine
reads the `.map` files it writes (standard, Valve 220 and Quake 2/3 dialects) and turns each brush into
a convex hull to walk into and a mesh to look at. 32 map units to the metre, Z-up becomes Y-up, and a
face's texture name is a material id.

```bash
dotnet run --project src/Sage.Host -c Development -- +fgd_export     # entity definitions for TrenchBroom
```

A level is named by a `map` record (`games/Sandbox/content/data/level.json`), which a `scene` loads; in
the editor, `classname` is a prefab id, so `watcher` places the Sandbox's watcher. `map_load`, `map_list`
and `map_unload` do it from the console, and `map_goto` stands you where the map's `info_player_start`
says. See [`docs/design/15-editor.md`](docs/design/15-editor.md) §10a.

**What a level does** is wired in the same file. Brushes with a `classname` become an entity that owns
its geometry — a door, a lift, a trigger volume — and an entity fires named **outputs** that are wired to
named **inputs** on other entities:

```
"classname" "door"           a prefab, so what a door *does* lives in the prefab
"targetname" "hut_door"
"OnUse" "!self,Open"         using it opens it: target,input[,parameter,delay,times]
```

Connections are checked when the level loads, so a typo is an error naming the line rather than a door
that quietly never opens. `ent_fire`, `io_list` and `io_trace` drive and watch it from the console; see
[`docs/design/04-events-and-messaging.md`](docs/design/04-events-and-messaging.md) §3.4.

## Daggerfall art (your own copy)

The engine is aimed at a Daggerfall-like game, so the Sandbox can dress itself in Daggerfall's own
art — which is a good test of the sprite pipeline, because Daggerfall stores five views of a monster
and mirrors the other three, exactly what a `sprite_sheet` record calls `directions: 5`.

**The art is Bethesda's and none of it is in this repository.** The importer reads the copy of the
game you own and writes into two paths `.gitignore` keeps out of git, the same arrangement Daggerfall
Unity uses: the project supplies the engine, you supply the assets.

You need the game (Steam, GOG or Bethesda's own free release) and `DaggerfallConnect.dll`, which
comes with [Daggerfall Imaging 2](https://www.dfworkshop.net/daggerfall-imaging-2/). Then:

```bash
dotnet run --project tools/DaggerfallImport
```

It defaults to the Steam install and Daggerfall Imaging 2's DLL; pass `--arena2 <dir>` and
`--connect <dll>` if yours live elsewhere, `--help` for the rest. It writes:

- `games/Sandbox/content/textures/daggerfall/*.png` — seven creatures with walk and attack
  animations (rat, spriggan, orc, skeleton, zombie, gargoyle and a human fighter), four townspeople,
  ten woodland flats (trees, rocks, stumps, undergrowth), a sword, a mace, a shield, the
  first-person weapons those two swing (`WEAPON*.CIF`) and three tiling textures;
- `games/Sandbox/content/data/daggerfall.json` — the `sprite_sheet`, `material` and `prefab` records
  that use them: a line-up of creatures to walk around and hit, two that hunt you, a stone ruin to
  break their line of sight, scattered woodland, and a patch putting Daggerfall's grass on the
  terrain.

Everything in the scene is generated from tables at the top of `tools/DaggerfallImport/Program.cs`,
so adding a monster is one row. `--contact <archive>` dumps a whole TEXTURE archive (or a CIF, by file name) as one labelled grid,
which is how to find out what is in it — that is how the ones above were chosen.

The creatures fight with the same records the placeholder creature uses: the art is all that
changed, which is the point of keeping gameplay in data. Delete `daggerfall.json` to go back.

## Licence

Sage is [MIT-licensed](LICENSE). Its dependencies keep their own licences, listed in
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md). A game shipped on Sage should include that file:
three of the ECS library's dependencies are LGPL-3.0.
