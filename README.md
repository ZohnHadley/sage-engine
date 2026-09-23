# Sage

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
- [`docs/history/code-review-log.md`](docs/history/code-review-log.md) — every bug worth remembering,
  with what it was and why it happened.

## Running it

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) or newer (the projects target
net8.0 and roll forward). From the repo root:

```bash
dotnet run --project src/Sage.Host -c Debug
```

No arguments needed: a development build walks up from the executable looking for `Sage.sln` and
loads `games/Sandbox`. `-c Development` is the same thing optimised, and noticeably smoother.

You should get a hilly field, three creatures, some crates falling through a trigger, and a creature
that notices you and comes over to hit you. The HUD shows your health, what is in your hands, what
pressing `E` would pick up, and what just hit you.

### Controls

| Input | Does |
|---|---|
| `W` `A` `S` `D`, mouse | move, look |
| `Space` | jump |
| `Left Shift`, `Left Ctrl` | run, crouch |
| **Left mouse** | attack |
| `E` | use |
| `Escape` | quit (closes the console first) |
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
| `screenshot [delay]` | PNG into `user/sandbox/screenshots` |
| `rec_list`, `rec_get prefab watcher`, `rec_reload` | what records loaded, one record's values and where each field came from, reload them |
| `vfs_mounts`, `vfs_which textures/creature.png` | the mount stack, and which mount a path resolves to |
| `pause`, `host_timescale 0.3`, `sim_tickrate 30` | stop time, slow it, change the tick rate |
| `r_sprite_facecamera 1` | turn billboards toward the camera's position instead of the view plane |
| `in_axis Move 0 1 3`, `in_look 40 0 3`, `in_tap Attack` | drive the game without a keyboard (see below) |
| `wait 2` | pause a script here; the rest runs later |
| `quit [seconds]` | exit now, or after a while (Escape and the close button always work) |
| `give <item>`, `inv`, `equip <item>`, `drop <item>` | items: `give practice_sword` then `equip practice_sword` and watch the combat log change |
| `ui_crosshair 0` | hide the crosshair (it is on while a camera rig has the view) |
| `r_debugdraw 1`, then `phys_debug 1`, `ai_debug 1`, `combat_debug 1` | see the simulation: colliders and capsules, sight cones and targets, every swing and what it found. `r_debugdraw_xray 1` draws it through walls |
| `modules`, `sys_list`, `ent_list` | what is loaded, what runs each phase, what exists in the world |
| `asset_list`, `asset_reload [path]` | what art is loaded; reload one file or all of it |
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
| `in_tap <action>` | press it for one frame |
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

## Building and testing

```bash
dotnet build Sage.sln -c Debug          # Debug | Development | Shipping
dotnet test tests/Sage.Tests/Sage.Tests.csproj -c Debug
```

Three configurations, as UE does it: **Debug** (asserts, verbose logs, `developer 1`), **Development**
(optimised, still has the console, cheats and dev tools) and **Shipping** (no dev cvars, no console;
it needs `-game <folder>` because it looks for a `game` folder beside the executable). `SAGE_DEV` is
defined in the first two.

The tests are all headless — no window, no graphics device — because the simulation has no MonoGame
dependency. That is the same property a dedicated server would need, so it is checked by the build.

## What's in the repo

| Path | What |
|---|---|
| `src/Sage.Engine` | The simulation: ECS, records/VFS, physics, gameplay, animation. **No MonoGame.** |
| `src/Sage.Client` | Rendering, input devices, assets, sprite batching — the MonoGame half |
| `src/Sage.Editor` | Dev camera, console window, entity outliner, stat overlay (ImGui) |
| `src/Sage.Host` | The executable: boot sequence and the main loop |
| `games/Sandbox` | The test game's **simulation**: its module, scene records, placeholder art and tools. References only `Sage.Engine`, so it is testable headlessly |
| `games/Sandbox.Client` | The same game's **client half**: the HUD, and the prefab part that needs a renderer |
| `engine_content` | Engine-owned data and shaders, mounted under the `sage:` namespace |
| `tests/Sage.Tests` | xUnit, headless |
| `tools/` | Content tools that are not part of the build (the Daggerfall importer) |
| `user/` | Written at runtime: logs, config, screenshots, crash reports (not in git) |

## Placeholder art

`games/Sandbox/tools/make_placeholder_art.py` generates the Sandbox's creature and tree sprites. The
creature is deliberately crude but it encodes the engine's direction convention: a nose that swings
with the view angle, a pack when seen from behind, and bars at its feet counting the direction group,
so a screenshot says which of the eight groups was picked. Regenerate with:

```bash
python games/Sandbox/tools/make_placeholder_art.py
```

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
