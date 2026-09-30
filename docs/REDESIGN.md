# Sage redesign plan: an engine for designers, developers and modders

*Planning document, 2026-09-27. Based on `main` at `141097d` (112 commits). Six parallel reviews read
the whole repo: docs and history, core, content and modding, gameplay, rendering and editor, and code health.
Nothing was changed. Paths are relative to the repo root. Suggested home: `docs/REDESIGN.md`.*

---

## 0. The short version

Sage's foundations are good. Keep them:
- the headless sim/client split, enforced by the compiler;
- the record pipeline with per-field patching and provenance;
- the VFS;
- entity I/O;
- the console culture;
- phase contracts.

These are exactly what a designer, developer and modder engine needs. The redesign keeps them.
**Sage mostly doesn't need a rewrite. It needs its promises turned into mechanisms.**

The review found five structural problems. Each hurts at least one audience badly:

| # | Problem | Hurts most | Evidence |
|---|---|---|---|
| 1 | **Registration fails silently.** An attribute alone does nothing, and a manual `Register` call is needed too. MAKING_A_GAME §10 lists 11 "silently does nothing" traps, and the code review found 10 more. | Developers | `[SavedResource]` bug shipped 3×; point lights dead in Shipping with 514 tests green (handoff-09-25 §2); `Modules.cs:66,187`; `SystemScheduler.cs:72` |
| 2 | **"Engine" and "RPG kit" are one assembly.** 37% of `Sage.Engine` is Daggerfall-flavoured gameplay. The `Sage.Framework` layer in ARCHITECTURE §2 was never built. | Developers, modders | `src/Sage.Engine/Gameplay/` (7,117 lines); `Effects.cs:354-355` calls `Factions`/`Quests` directly; `SpellmakerScreen.cs:10-13` |
| 3 | **Designer logic stops at C#.** AI conditions, schedule choice, quest objectives, dialogue conditions and outcomes, effect executions, ability delivery and item use are all closed enums or private code. | Designers, modders | `AI.cs:17-44`, `AIThinkSystem.cs:300-330`, `Quests.cs:19-23`, `Dialogue.cs:21-71`, `Abilities.cs:19-26` |
| 4 | **Modding is a design doc, not a feature.** There's no mod loader. `ModsDirectory` is parsed and never used. Assemblies go into the Default load context. Asset paths collide silently. Saves don't record the mod list. Shipping has no tools. | Modders | `GameManifest.cs:23`, `Modules.cs:185`, `SaveSystem.cs:102-109`, TODO F37 `[ ]` |
| 5 | **Authoring tools are a dev overlay on the live world.** There's no undo, no placing, and no record forms. Inspector edits are silently lost on save. JSON has no schema, and a missing record becomes a default-constructed one. | Designers | `EntityInspectorWindow.cs:72-76` vs `Placements.cs:78-92`; `RecordStore.cs:116-121`; `PrefabParts.cs:22-35` |

**The core move:** make every declaration (component, record type, system, prefab part, condition or
action, cvar) a **single typed declaration with a stable string id, discovered at build time by a source
generator.** Everything the three audiences need hangs off that one registry:
- registration that can't be forgotten;
- JSON Schemas and autocomplete;
- editor forms;
- the TrenchBroom FGD;
- save identity and upgraders;
- mod conflict reports;
- analyzers.

The docs already call the generator "the biggest single unlock" (09 §3.2) and still list it fourth. **In
this plan it comes first.**

---

## 0.5 Priority: a base engine first, your game second

**The owner's direction (2026-09-27):** build a **base engine** that the Daggerfall-like can be made in,
and that can also make almost any other game. Every phase in §5 is ordered by that goal. Editor polish
and the modding surface still matter, but they come *after* the base can carry several genres.

### What "base engine" means

A **base engine** is everything a game needs that is **not a genre decision**. Anything that assumes
a genre (first person, a readied spell, 8-direction billboards, mana, Daggerfall-style quests) goes in a
**kit** or a game.

**The test:** can a new game in any row of the table below be written as *game code and data only*,
with **zero edits to engine assemblies**? If a sample game needs an engine edit, that edit is a
base-engine gap. Fix it in the base engine, never inside the sample.

### The genre gauntlet: what "almost any game" asks of the base

| Capability | FPS (HL1) | 3rd-person action (Lugaru) | Top-down / iso RPG | 2D platformer | RTS / battles (Warband) | Puzzle / card / UI-heavy | **Your game** (Daggerfall) | Sage today |
|---|---|---|---|---|---|---|---|---|
| App kernel, plugins, records, VFS, console, saves | ● | ● | ● | ● | ● | ● | ● | ✅ mostly; boot and plugins need redoing (§3.2–3.3) |
| ECS, scheduler, events, prefabs, scenes | ● | ● | ● | ● | ● | ● | ● | ✅ core. Scenes are the engine's (#29). Prefab overrides and nesting missing (F31) |
| **Cameras as components**: perspective and ortho, rigs, several viewports | ● | ● | ● | ● | ● | ● | ● | ❌ one `ActiveCamera` resource, perspective only (`RenderSystems.cs:48`), one first-person rig (`CharacterController.cs:417`) |
| 3D physics, queries, triggers | ● | ● | ● | ○ | ● | ○ | ● | ✅ Bepu |
| 2D physics (bodies, tiles, one-way platforms, slopes) | | | ● | ● | | ○ | | ❌ |
| Character controllers (FP, 3P, top-down, 2D) | ● | ● | ● | ● | ○ | | ● | ◐ one kinematic first-person capsule |
| **Skeletal animation**, blending, anim state machine | ● | ● | ● | ○ | ● | | ○ | ❌ F9–F12. `Animation/` is only `SpriteAnimation.cs` (102 lines) |
| Sprites, sorting, tilemaps, 2D layer | ○ | | ● | ● | ○ | ● | ● | ◐ billboards and sprite sheets; no ortho, sorting layers or tilemaps |
| Meshes, materials, lights, particles, weather | ● | ● | ● | ○ | ● | ○ | ● | ✅ |
| **Render pass registry**, render targets, post-processing | ● | ● | ○ | ○ | ○ | ○ | ● | ❌ `Passes` is a private static array |
| **UI toolkit**: widgets, layout, focus and gamepad navigation, styling, localisation | ● | ● | ● | ● | ● | ●● | ● | ◐ list-style `Screen`/`Panel` only; HUD hand-drawn |
| Input: actions, gamepad, mouse picking, rebinding | ● | ● | ● | ● | ●● | ● | ● | ◐ actions and scripted input done; picking and rebinding UI missing |
| Navigation: grid now, navmesh later, groups | ● | ● | ● | | ●● | | ● | ◐ grid A* inside Gameplay |
| Timers, tweens, state machines, conditions/actions | ● | ● | ● | ● | ● | ●● | ● | ❌ (only I/O `delay`) |
| Large worlds and streaming | | | ○ | | ○ | | ● | ✅ (keep as an optional plugin) |
| Audio (mixer, 3D, music) | ● | ● | ● | ● | ● | ● | ● | ✅ one-shots and loops; music streaming missing |
| Stats, effects, damage, inventory, factions, AI (BT) | ● | ● | ● | ○ | ● | ○ | ● | ◐ built but RPG-shaped and closed (§4.3) |
| Dialogue and quest *models* | ○ | ○ | ● | | ○ | | ● | ◐ closed enums; screens in engine |
| Spellmaker, readied spell, journal, Daggerfall sprite rules | | | | | | | ● | ✅ in `Sage.Kits.Rpg` since #27 (the sprite clip names are `gameplay_conventions`) |

● needs it · ●● leans on it hard · ○ nice to have · ✅ have · ◐ partial · ❌ missing

**Reading the table:**
- Almost everything your game needs **already exists**.
- What stops Sage from being a general engine is a short list:
  1. cameras and viewports;
  2. skeletal animation;
  3. a real UI toolkit;
  4. render passes;
  5. timers, tweens and state machines;
  6. a 2D and orthographic layer;
  7. picking;
  8. the RPG code sitting inside the engine.
- Each of those unblocks four or more columns. That's the order of §5.

### The base-engine boundary (what goes where)

- **Base engine.** `Sage.Core`, `Sage.Simulation`, `Sage.Client`, plus optional base plugins:
  - `Sage.Physics3D` (Bepu) and `Sage.Physics2D` (a real 2D engine; see "2D is first-class" below);
  - `Sage.Streaming`, `Sage.Navigation`, `Sage.Animation`, `Sage.UI`, `Sage.Audio`;
  - `Sage.Gameplay`: generic stats, effects, damage, inventory, factions, AI, and the
    conditions/actions vocabulary.

  A game switches plugins on in `game.json`. A puzzle game loads neither physics nor gameplay.
- **Kits.** `Sage.Kits.Rpg` holds the spellmaker, readied spell, journal and dialogue/quest *screens*,
  plus the Daggerfall conventions. Later kits: `Sage.Kits.Shooter`, `Sage.Kits.Platformer`,
  `Sage.Kits.Strategy`. A kit uses only base-engine public API.
- **Games.** Your Daggerfall-like game is `Sandbox` grown up, and it is *Kit.Rpg plus its own content
  and code*.
- **Moves out of the base today:** *(done: #26 and #27, see §3.1 "As built")*
  - `SpellmakerScreen`, `Spellmaker`, readied `Selected`, `JournalScreen`, `DialogueScreen`;
  - `sage:mana` costing;
  - the five hard-coded schedule ids;
  - clip names `attack`/`hit`/`idle`;
  - `FirstPersonCameraSystem`, which becomes one camera rig among several in the base;
  - the two equipment slots (main and off hand), as a kit default.
- **Stays in the base:**
  - 8-direction billboards: generic sprite feature, useful for Doom-likes and isometric games;
  - kinematic controller, entity I/O, streaming, factions and attributes.

### Out of scope for the base (for now)

- Multiplayer: keep the readiness rules.
- Visual scripting.
- AOT and consoles.
- Mobile and web.
- 2D skeletal animation (Spine- or DragonBones-style). Sprite animation covers 2D first; revisit after
  the 2D samples.

### 2D is first-class (decided 2026-09-27)

**Decision:** the owner chose **3D and 2D**, so 2D is a peer of 3D, not a 3D camera pointed at a plane.
The rule is **one engine, two spatial profiles**. Everything that isn't spatial is shared:
- records, prefabs and scenes;
- ECS, events and saves;
- UI, input and audio;
- gameplay (stats, effects, AI, conditions/actions);
- the editor and modding.

Only the spatial layer comes in a 2D and a 3D flavour. A game picks its profile in `game.json`, and can
mix them: a 3D game with a 2D minigame world, or a 2D game with 3D backgrounds.

| Concern | Shared | 2D profile | 3D profile |
|---|---|---|---|
| Transform | One `Transform` (position, rotation, scale). 2D uses X/Y with rotation about Z. **Z is sort depth, never gravity** | `Transform2D` view helpers (angle in degrees, `Vector2` accessors), generated from the same component | as today |
| Physics | One `IPhysicsWorld` facade: bodies, layers, raycast/shape-cast/overlap, triggers, contact events, the same prefab parts (`body`, `collider`) and the same debug draw | **`Sage.Physics2D`**: a dedicated 2D engine. First candidate is **Aether.Physics2D** (pure C#, Box2D lineage, MIT, used with MonoGame); decide after a spike | `Sage.Physics3D`: Bepu, as today |
| Character controller | `PawnIntent` drives both; `character` part settings | kinematic 2D controller: slopes, one-way platforms, coyote time, jump buffering, ladders | today's kinematic capsule, moved to the base |
| Camera | camera component and rigs (4a) | ortho, **pixel-perfect** mode (integer scaling, snapping), 2D follow with dead zone, bounds and look-ahead | perspective rigs |
| Rendering | pass registry, materials as records, the same `RenderSnapshot` | sprite batching, **sorting layers and order-in-layer**, Y-sort for top-down, 9-slice, parallax layers, 2D lights later | meshes, lights, billboards |
| Levels | scenes, placements, entity I/O and logic entities | **Tiled** (`.tmx`/`.tmj`) import: tile layers become meshes plus collision; object layers become prefabs, using the TrenchBroom pattern (classname = prefab id, properties = map keys, I/O keys) | TrenchBroom `.map` |
| Navigation | one `INavigation` service | grid/tile A* (today's grid planner, promoted) | grid now, navmesh later |
| Animation | clips, events and state machines as data | sprite-sheet clips (today's `SpriteAnimation`, extended) | skeletal (4d) |
| Audio | mixer and buses | 2D panning by screen position | 3D positional |

**Why a real 2D engine and not Bepu locked to a plane.** One-way platforms, tile-edge snagging,
ultra-thin colliders, pixel-scale units and per-contact callbacks for platformer feel are all awkward
or slow on a 3D solver. The facade keeps the choice reversible. A spike in phase 7a decides between
Aether.Physics2D and a thin wrapper of your own (Box2D v3 through bindings is the fallback).

**What this changes for the headless tests:** nothing. Both physics backends are pure C# with no
MonoGame, so 2D games stay testable exactly like the Sandbox.

---

## 1. Who we are designing for

| Audience | Works in | Needs from Sage | Today |
|---|---|---|---|
| **Game designer** (non-programmer) | Editor, TrenchBroom, JSON with autocomplete | Place things, wire logic, tune numbers, make creatures, quests and dialogue *without C#*; errors they can see; undo | Hot-reload tuning is great. Everything else is hand JSON or C#. Errors go to a log file |
| **Game developer** (C#) | IDE, a game project | A small, typed, discoverable API; a project template that builds outside this repo; testable rules; extension points instead of forks | Good docs and a headless-test story. Games build only inside `games/`. Friflo leaks into every signature. One namespace, `sage_engine`, holds 350 public types |
| **Modder** (third party, shipped game) | Mod folder or package, the shipped editor, TrenchBroom | Add or patch content safely; declare dependencies; see conflicts; optionally ship code; saves that survive | Nothing is built beyond VFS shadowing |

**Design rule for every feature:** say which of the three audiences it serves, what they touch (data,
editor or C#), and how a mistake shows itself to *that* audience. A log line is fine for a
developer. It doesn't count as an error for a designer or modder.

---

## 2. What to keep (verified strengths)

- **Sim/client split.** `Sage.Engine` has no MonoGame, tests can't reference the client, and decisions
  live engine-side (`AudioMixer`, `Panel`, `InputGating`). This is also the future dedicated server.
- **Record pipeline.** Namespaced ids, `base`/`abstract`, per-field patch with `field+`/`field-`,
  redefinition treated as an error (the Bethesda lesson), `FieldOrigins` provenance with `rec_get`,
  record instances stable across hot reload, and runtime records that survive reload.
- **VFS** with shadowing and diagnostics (`vfs_which`, `vfs_mounts`), plus runtime asset loading (R12). A
  dropped-in file just works.
- **Entity I/O.** Resolved at load, errors name the file and line, deterministic, with a budget,
  `io_trace` and FGD export.
- **`PawnIntent` as the single control seam.** Players and AI are the same to movement and combat.
- **Event bus** with per-reader cursors, per-schedule queues, and an `ev_maxage` backstop that names the
  lagging reader.
- **Phase contracts, `Deferred<T>`, deterministic ordering, and loud errors on cycles.**
- **Console culture.** Every menu item is a command, there's scripted input with `wait`, and three build
  configurations.
- **TrenchBroom for brushes** instead of writing our own brush editor — kept for now, but **not for
  the long run** (decided 2026-09-28, §6 item 8): levels move into our own editor (§4.6), and the
  `.map` importer stays until that editor can build the same levels.
- **Screens as headless view-models,** and "can I?" answered in one place (R17).
- **Docs-as-tests** (`check_docs.py`) and the second-pass habit.

---

## 3. Target architecture

### 3.1 Assemblies (one namespace per assembly: `Sage.*`, not `sage_engine`)

```
                ┌─────────────────────────── games / mods ───────────────────────────┐
                │  MyGame (sim)   MyGame.Client   SomeMod (data)   SomeMod.Code (C#)   │
                └───────▲──────────────▲──────────────────────────────▲───────────────┘
   Kits (optional) ─────┤              │                              │
   Sage.Kits.Rpg        │  (spellmaker, readied spell, journal, dialogue/quest screens,
   Sage.Kits.Rpg.Client │   Daggerfall sprite conventions)
   Sage.Kits.Shooter…   │  (later: HL1 weapons, Lugaru melee, Warband battles)
                        │
   Sage.Gameplay ───────┤  attributes/tags/effects, damage, inventory, interaction, factions,
                        │  AI (behaviour trees + tasks), navigation, dialogue & quest *models*,
                        │  conditions/actions vocabulary, logic entities
   Sage.Simulation ─────┤  World/ECS facade, scheduler, events, physics, character controller,
                        │  transforms, streaming/origin, prefabs, entity I/O, saves, levels (.map)
   Sage.Core ───────────┘  app kernel, plugins, records, VFS, cvars/console, logging, diagnostics
                           — no MonoGame, no Friflo types in its public API

   Sage.Client            MonoGame: renderer (pass registry), UI drawing, audio backend, input devices
   Sage.Editor            editor model + ImGui/tool UI (plugins can extend it)
   Sage.Generators        source generator + analyzers (the declaration registry)
   Sage.Testing           headless SageApp builder, MountFixture, EventProbe — used by engine *and* games
   Sage.Sdk               MSBuild SDK + `dotnet new` templates (game, game-client, mod-data, mod-code)

   Hosts (thin):  Sage.Player (game exe) · Sage.Server (headless) · Sage.EditorHost · `sage` CLI
```

**Hard rules, enforced by the build:**
- No Simulation, Gameplay or Kit assembly references MonoGame. Keep it the way it is today, and add a
  banned-API analyzer.
- `Sage.Gameplay` never references a Kit.
- Nothing in Core, Simulation or Gameplay knows a `sage:` record id by constant.
  Conventions come from a `gameplay_conventions` record that the kit or game supplies. That replaces 15
  hard-coded `RecordId` constants (`Attributes.cs:26,35-36`, `AIThinkSystem.cs:326-330`, …).
- Cross-module calls become events. `Effects.Die` raises `Died`, and factions and quests subscribe to it.
- No `InternalsVisibleTo` except to test assemblies. If the editor needs something, it's public API.

*As built (issue #24, 2026-09-29).* `Sage.Engine` is now **`Sage.Core` ← `Sage.Simulation` ←
`Sage.Physics3D` ← `Sage.Gameplay`**, with `Sage.Client`, `Sage.Editor`, `Sage.Host`, `Sage.Cli`,
`Sage.Testing` and `Sage.Tests` on top, one namespace per assembly and no `sage_engine` left. Registry
dump, `sage validate`, the committed JSON Schemas and the ECS schema (45 components and 7 tags for the
Sandbox) are the same as before the split. Where it differs from the diagram above, deliberately:

- **`Engine`, `World`, `SageApp`, `IModule`/`ModuleManager`, `HostLoop` and `GameRules` are in
  `Sage.Simulation`, not Core.** `World` holds its `Engine`, and `IModule.OnWorldCreated(World)` is the
  module contract, so they cannot be pulled apart without a second abstraction nobody needs yet. Core is
  the world-free kernel: cvars/console, log, diagnostics, VFS, records, plugin identity and versions,
  the registration ledger and seals, the declaration attributes and the metadata table. It references
  no package; Friflo's `Entity` is recognised by name in the metadata, and the ECS's entity converter
  for record JSON is added by the `Engine`.
- **Physics is its own base plugin, `Sage.Physics3D` (Bepu)**, with character movement; the
  first-person rig it installs is `Sage.Simulation`'s since #27. The simulation and gameplay reach physics only through
  **`IPhysicsWorld`** (issue #30, below), which the space is installed as too. `PawnIntent`, `Pawn`, `PlayerControlled` and `Players.ForEachPlayer` are
  simulation (`Sage.Simulation/Input`), because streaming, maps, entity I/O and the controller all read them.
- **The RPG kit came with #27** (below). What stops its code coming back into the base is **SAGE0025**:
  a base assembly (`SageBaseAssembly`, set for Core to Gameplay, UI, Client and Editor) that references a
  `Sage.Kits.*` assembly or uses its types is a build error.
- **Plugin ids did not change** (`sage.physics3d`, `sage.streaming`, `sage.gameplay.*` — `game.json`
  names them), even where the plugin's assembly did: entity I/O (`sage.gameplay.io`) and maps are in
  `Sage.Simulation`, the character (`sage.gameplay.character`) in `Sage.Physics3D` (its data in
  `Sage.Simulation` since #30).
- **The one list of base plugins is `BasePlugins.All()` in `Sage.Gameplay`** (it is the only assembly
  that can name them all); `SageAppOptions.AvailablePlugins` replaces `IncludeSimulationModules`, and the
  host, `sage`, `HeadlessApp` and the tests pass it. `SaveSystem.AddConverter` (attributes and tags by
  name, added by `AttributesModule`) and `MapLevels.SolidSpawned` (a mover's shut position, from
  `MoverModule`) replace the two places the simulation reached into gameplay.
- **A declaration outside `Sage.Simulation` belongs to a plugin in its own assembly**: the engine
  includes its own assembly's generated registrations and each loaded module's, so one owned by a
  plugin elsewhere would never register (test: EveryDeclarationOutsideTheSimulationBelongsToAPluginInItsOwnAssembly).
  The `physics_layers` record (owned by `sage.physics3d`) stays in `Sage.Simulation` beside the other
  physics data, which is always included.
- **`Sage.UI` (issue #95, 2026-09-30) is a fifth base assembly, beside `Sage.Physics3D` on
  `Sage.Simulation`:** the game UI's retained widgets, layout and focus (docs/design/13 "As built
  (retained widgets)"), simulation-only and headless, referenced by `Sage.Client`, which draws them
  (#97). Simulation ← UI ← Client (decision D1): the simulation's `Screen`/`Panel` model does not know
  it, and it knows nothing of physics, gameplay or a kit (test: NoBaseAssemblyReferencesALayerAboveIt).
  It is in `EngineAssemblies.Base`, so the base-wide checks (declared API, simulation-only, no Friflo)
  read it too; `Sage.Sdk` references it for every game, without a global using.
  Since #96 it is also a plugin, `sage.ui` (`UiModule`), the last entry of `BasePlugins.All` — which is
  why `Sage.Gameplay` references it — so a server and `sage validate` read the UI's records as the game
  does; it owns the `ui_style`, `ui_layout` and `screen` records, the `view_model` vocabulary and the
  string tables, and no component or system (test: TheClientSitsOnTheRetainedUi).
- **Friflo builds its schema from every loaded assembly that references it**, the split ones included
  (23 in Simulation and 15 in Gameplay since #30 moved `CharacterController`; none in Physics3D) (test: TheEcsSchemaHasTheComponentsOfEveryBaseAssembly).
- **`InternalsVisibleTo` goes only to `Sage.Tests`**, so what the next layer used became public: the record
  JSON helpers (`JsonMembers`, `RecordParseContext`, the vector converters), `Upgraders`,
  `Screen.Index`'s setter, `RegistrationLedger.Owner`'s setter, `MovementProfileRecord.Fallback` and the
  client's keyboard and mouse listeners. #31's public-API pass decides which stay.
- Each project imports the layers below as **global usings in its csproj** (the `Transform` alias
  went with #25, §3.5), and games take the four base references and usings from `games/Directory.Build.props`
  (`Private="false"`, so a game's `bin/` still holds only its own dll). The generators name engine types
  from one list, `src/Sage.Generators/SageTypes.cs` (test: EveryTypeTheGeneratorsNameExists). Every
  engine project is simulation-only (SAGE0024) unless it opts out (Client, Editor, Host).
- *As built (issue #30, 2026-09-29): the physics facade.* **`IPhysicsWorld`** (`Sage.Simulation`) is the
  whole of what gameplay asks of physics: bodies (static, kinematic, dynamic; hulls and meshes), the
  named layers of `physics_layers`, raycast, shape cast and overlap (with an `ignore` entity), trigger
  and opt-in contact events, and debug draw (docs/design/10 "As built (the facade)"). Combat, projectiles,
  AI sight and steering, navigation probes, items, movers and entity I/O use it and nothing else;
  `Sage.Gameplay` uses no Bepu type, and from `Sage.Physics3D` only `PhysicsModule` and `CharacterModule`,
  in `BasePlugins` and `GameplayModules.All` (test: GameplayReachesPhysicsOnlyThroughTheFacade). **The
  project reference stays** for those two lists, the one place the base names its 3D backend: dropping
  it needs a composition root above both assemblies (`Sage.Sdk`, #32, or one plugin list per spatial
  mode when `Sage.Physics2D` arrives). What a 2D backend shares moved to `Sage.Simulation`: the
  character's data (`CharacterController`, `movement_profile`, `AddCharacter`), the `body` and
  `character` parts under the same ids, options and plugins, and `phys_debug`'s system. A sweep that
  starts inside something is a hit now rather than nothing, which was the "overlapped sweeps are
  discarded" gap (test: ASwingHitsATargetPressedAgainstTheAttacker).
- *As built (issue #27, 2026-09-29): the RPG kit.* **`Sage.Kits.Rpg`** (plugin `sage.kits.rpg`,
  simulation-only) and **`Sage.Kits.Rpg.Client`** (`sage.kits.rpg.client`) sit on the base and are not
  part of it: not base assemblies, not in `BasePlugins.All()`, loaded only for a game whose `game.json`
  says `"kits": ["sage.kits.rpg"]` (test: HelloHasNoKitAndTheSandboxHasTheRpgKit). The kit has the
  spellmaker and its saved `spellbook`, the readied spell and the `Cast` button that fires it through
  `world.Cast`'s queue (test: TheCastButtonFiresTheReadiedSpell), the bag and spellbook panels, the
  journal, conversation and spellmaker screens, the `spells`/`ready`/`inv`/`spell_*` commands, the two
  hands as equipment slots, and an optional `rpg_conventions` record (`spellNamespace`, formerly the
  fixed `custom`; `castAction`) (test: AGameChoosesTheNamespaceItsComposedSpellsLiveIn). Its client half
  registers the screens by id in the client's new **`ScreenRegistry`**, and the client's
  `DialogueSystem` asks for `"dialogue"` instead of making `DialogueScreen`. What the base keeps:
  ability use (`world.Cast`, the AI's casting), `Abilities.Selected` as data only (saves carry it under
  `sage:abilities`; the base never reads it), and equipment **by slot name**: `ItemRecord.Slot` is a
  string, `sage:equipment` a list of `{ Slot, Item }` (version 2, whose upgrader brings the golden
  saves' `MainHand`/`OffHand` across), and the slots are whatever the kit and the game register in
  `EquipSlots` — none in the base (test: TheKitHasTwoHandsAndTheBaseNone). Spell prices are worded in the
  conventions' `costAttribute`, and the sprite clips combat plays are `gameplay_conventions`
  `animations` (test: AGameNamesTheSpriteClipsCombatPlays) — since #119 only `hit` and `attackTrigger`:
  clip names are an `anim_graph`'s, and the old two are read only by the upgrade of a graphless sprite. **How the host finds a kit:** by its id —
  `sage.kits.rpg` is `Sage.Kits.Rpg.dll` — beside the game's assembly or its `modules.add` ones, or
  beside the host, where `build/Sage.Kits.targets` copies every kit without the host referencing one
  (a referenced kit would be loaded, and put in the ECS schema, for every game); loaded before the
  game's assemblies, with the `.Client` half in a host with a window (`SageAppOptions.LoadKitClients`)
  (test: AKitThatIsNotThereIsAnErrorThatSaysWhere). Layering: the kit references the base and no client
  (test: TheRpgKitIsBuiltOnTheBaseAndIsNotPartOfIt), neither the built client nor the editor references
  a kit (test: NeitherTheClientNorTheEditorReferencesAKit), and SAGE0025 fires against the real kit
  (test: ABaseAssemblyThatUsesTheRealRpgKitIsABuildError). `FirstPersonCameraSystem` moved to
  `Sage.Simulation` (it needed nothing of Bepu's). The kit had no content mount then, so its defaults were
  code and a game binds the kit's keys itself (`games/Sandbox/content/data/input.json`); since #98 a
  plugin carries content in its assembly (`[PluginContent]`, the kit's `rpg` screens and strings).
- Not done here: `GameplayModules.cs`, `PrefabParts.cs` and `MapLevel.cs` moved whole rather than one
  file per module or type; the render, audio, input and UI view-models stay in `Sage.Simulation` rather
  than separate `Sage.UI`/`Sage.Audio` plugins (§0.5); `Sage.Sdk` is #32.

### 3.2 One boot path: `SageApp`

Today boot is split between inline `Program.cs` and `Game1.Initialize`, and it's re-implemented
differently in 35 tests. The host ticks only one world (`Game1.cs:227-256`), although `Engine.Worlds` is
a list.

```csharp
SageApp.Create(args)
    .UseGame("games/MyGame")          // game.json → mounts, plugins, kit
    .AddPlugins(DefaultPlugins.Client) // Server host omits this
    .Run();                            // explicit stages: Discover → Register (sealed) → Load content
                                       // → Build ECS schema → Start → Create worlds → Loop (ticks every world)
```

- The **Player, Server, Editor and Test** hosts all call this. `Sage.Testing.HeadlessApp` is the same
  builder without a GPU, so tests run what ships. That removes the `ModuleSetTests` class of bug by
  construction.
- **Stages are sealed.** Registering a record type, cvar, action or plugin after its stage **throws**
  instead of being dropped. That fixes the cvar-in-`Start` trap (#58) and `Add` after `InitAll`.
- **The ECS schema is built once, after all plugins and mods load.** It stops depending on assembly
  load order (`EcsSchema.cs`).
- **Global statics become per-app services:** `Log`, `Profiler`, `CrashReporter`, `UserPaths`, the
  `AssetPath` interner and `BuildInfo`. `Log` can keep a static facade. Payoff: parallel tests, and an
  editor that can host a play session.

### 3.3 Plugins replace `IModule` with `Type[]` dependencies

```jsonc
// plugin identity, via [assembly: SagePlugin(...)] or plugin.json
{ "id": "sage.gameplay.ai", "version": "0.4.0", "sage": ">=0.4 <0.5",
  "kind": "simulation",               // simulation | client | editor | tool (enforced per host)
  "dependencies": { "sage.gameplay.effects": ">=0.4" } }
```

- Dependencies are string ids with SemVer ranges, not CLR `Type`s. A mod no longer has to compile
  against the exact assembly it depends on.
- Everything a plugin registers is **recorded against the plugin id.** That gives:
  - a conflict report, for example "two plugins set `GameRules`" or "mod X replaced system Y";
  - unloading;
  - an editor that can show where a thing came from.
- `Kind` is enforced: editor plugins load only in the editor host. Today `ModuleKind` is shown in
  `modules` and never enforced (`Modules.cs:168`).
- **Stable system ids** (`sage.character.move`) with `world.Systems.Replace/Disable(id)`, so mods can
  swap an engine system. A `before`/`after` that crosses phases becomes an error
  (`SystemScheduler.cs:72-73`). Event readers are released when their system is removed
  (`GameEvents.cs:154`).
- `GameRules` is created by an explicit `IGame.CreateRules(World)`. It's no longer a resource you must
  `Set` at the right moment (`Engine.cs:67-72`).
- `WorldResources.Set` splits into `Add` (throws if present) and `Replace` (logged, disposes the old
  value). `World` stops installing Terrain, Weather, PlayerInput and RenderEnvironment into every world
  (`World.cs:62-82`). Those plugins add them.

### 3.4 Declarations: the source generator (the load-bearing piece)

**One attribute per concept. The generator produces the registration, the metadata and the schema.
An analyzer rejects anything half-declared.**

```csharp
[Component("sage:health", Version = 2)]
public struct Health : IComponent {
    [Property(Min = 0, Unit = "hp", Tooltip = "Current hit points")] public float Value;
    [Transient] public float LastHitTime;          // not saved
    [Upgrade(1)] static void From1(ref JsonObject o) { o.Rename("hp", "value"); }
}

[Record("sage:item")]           public sealed class ItemRecord { public RecordRef<SoundRecord> PickupSound; … }
[PrefabPart("sage:light")]      public sealed class LightPart  { public Color Colour = Color.White; … }   // public, attributed
[System("sage.ai.think", Phase.Gameplay, After = "sage.ai.perceive")] public sealed class AIThinkSystem : ISystem { … }
[Condition("sage:has_item")]    public sealed class HasItem : ICondition { public RecordRef<ItemRecord> Item; public int Count = 1; }
[Action("sage:start_quest")]    public sealed class StartQuest : IAction { public RecordRef<QuestRecord> Quest; }
```

What the generator emits:

1. **Registration code** for each assembly, called by the plugin loader. You can no longer declare a
   type and forget to register it. That closes MAKING_A_GAME §10 items 1–4 and 7–9.
2. **A metadata table**: stable id, fields, types, units, ranges, tooltips, categories, enums and
   reference targets. The inspector, record forms, FGD export, console `ent_dump` and the docs all use
   it. Reflection stays only as a dev fallback, and the stack becomes trim/AOT-safe later.
3. **JSON Schemas** (`schemas/*.schema.json`) with a `type` discriminator. Record ids become
   `enum`/autocomplete lists at runtime through `sage schema`. See §4.2.
4. **Save identity**: a component is saved under its stable id, not its CLR name
   (`SaveSerializer.cs:85-91`), and carries a version with upgraders.
5. **Analyzers (compile errors):**
   - `[SavedResource]` without a plugin;
   - a cvar registered outside `Register`;
   - a `RecordRef<T>` to an unregistered type;
   - a public component field without a `[Property]`/`[Transient]` decision, when strict mode is on.

   As built (issue #19): SAGE0020 (a registration written in `Start`, `OnWorldCreated`, `CreateRules` or
   a system), SAGE0021/0022 (what the generators left unchecked: an empty record type or resource name,
   an `[Upgrade]` nothing runs), SAGE0023 (strict saves, `<SageStrictSaves>`) and SAGE0024 (a MonoGame
   type in a `<SageSimulationOnly>` project); `[SavedResource]` without a plugin was already SAGE0001.
   The `RecordRef<T>` check waits on #22. Every id is listed in MAKING_A_GAME §10a
   (test: RegisteringACvarInStartIsABuildError).

   As built (issue #21), item 3: the schemas are written from the metadata table at run time rather
   than by the generator, because the id enums need the loaded content anyway: `sage schema` writes
   `schemas/record.schema.json` (discriminating on `"type"`), one schema per record type, the prefab
   components and parts, and `ids.schema.json`; 05 "As built (JSON Schemas)" has the details
   (test: EveryShippedDataFile_ValidatesAgainstTheCommittedSchemas).

The ids are **namespaced strings**, so two mods each defining `Health` no longer silently overwrite each
other (`ComponentSchema.cs:39`).

### 3.5 Own the ECS vocabulary

Friflo was in every public signature until #25: `Entity`, `IComponent`, `ArchetypeQuery`, `CommandBuffer`,
`Tags`, plus a global `using Friflo.Engine.ECS` for games and a `Transform` alias hack
(`games/Directory.Build.props:14-18`). That made Friflo 3.6's API and schema behaviour part of every
game's and every mod's contract.

- `Sage.Simulation` exposes `Entity` (a Sage handle), `IComponent`, `Query<T1..Tn>` (a zero-cost
  struct over Friflo chunks), `Commands` and `Tags`. Friflo becomes a private implementation detail.
- The three different "commands" get separate names:
  - console: `ConsoleCommand`
  - ECS: `EntityCommands`
  - input: `PlayerCommand`
- In Shipping, `World.Get<T>` on a missing component must not return a shared static dummy
  (`World.cs:143-144`). Use `TryGet`/`ref` with a dev assert, and fail loudly in all configurations for
  writes.

*As built (issue #25, 2026-09-29).* `Sage.Simulation/ECS/Api` has `Entity`, `IComponent`, `ITag`,
`Tags`, `Query` and `Query<T1..T5>` (with `Chunks`, `Chunk<T>`, `ChunkEntities`, `Entities`) and
`EntityCommands`, each a struct (or, for the buffer, a class) over Friflo's that forwards to it; `World`
and `SystemContext` speak only these, and `ComponentSchema` answers in `System.Type`. The migration was
a script, `tools/migrate_ecs_api.py`, kept for code written against the old names. Where it differs:

- **Friflo is still a compile-time reference of every game.** Sage's `IComponent` and `ITag` *extend*
  Friflo's, because Friflo's schema scan looks for its own interface and its generic calls are
  constrained on it; anything else would put a boxing or reflective bridge in every component access.
  A derived interface needs its base to compile, so the package flows to games as before. "Private"
  is enforced instead as *nobody names it*: no `Friflo.*` type in a public signature of Core,
  Simulation, Physics3D, Gameplay or Client (test: NoPublicTypeInTheBaseEngineExposesFriflo)
  (test: NoPublicTypeInTheClientExposesFriflo) — the two marker interfaces' base aside — and
  **SAGE0050** for a Friflo type or namespace written outside `Sage.Simulation` (which sets
  `SageEcsImplementation`) (test: NamingFrifloOutsideTheEcsImplementationIsABuildError).
- **`Commands` stays the property name** (`world.Commands`, `ctx.Commands`); its type is
  `EntityCommands`. The console's registrars became `WorldConsoleCommands` and `ScaleConsoleCommands`;
  `ConsoleCommand` (Core) and `PlayerCommand` were already named.
- **`EntityName` is gone from the API**: `entity.Name` is a string (Friflo's component underneath).
- **Unchanged:** component ids, the save format and golden saves, prefabs, the registry dump, the JSON
  Schemas, and the ECS schema (45 components and 7 tags for the Sandbox). The scale run
  (`scale_spawn 2000 tree`) is within noise of before (numbers in 03 §3.1a).
- **Not done here:** the Shipping dummy in `World.Get<T>` (the last bullet above) is unchanged, a
  separate behaviour change for #31's API pass. *(Done in #31, §3.6: `Get<T>` throws in every build.)*

### 3.6 Public API discipline

- Add `PublicApiAnalyzers` (`PublicAPI.Shipped.txt`) on Core, Simulation, Gameplay and Client. Mark
  unstable areas `[Experimental]`.
- Use SemVer from git (MinVer or Nerdbank.GitVersioning) feeding `BuildInfo.EngineVersion`, which is
  always `1.0.0.0` today.
- Plugins and mods declare the `sage` range they need, and the loader checks it.

*As built (issue #31, 2026-09-29).* The procedure is [`docs/RELEASING.md`](RELEASING.md); the
experimental ids are MAKING_A_GAME §10b.

- **The version comes from git tags.** `build/Sage.Version.props` runs **MinVer** (tag prefix `v`,
  minimum 0.1) in every engine project and in the three packages, so one number reaches the engine
  assemblies, `Sage.Sdk`, `Sage.Player` and `Sage.Templates`; #32's hard-coded `SageVersion` 0.1.0 is gone,
  and `tools/pack_sdk.sh` asks MSBuild for it. Before the first tag that is `0.1.0-alpha.0.N`.
  `BuildInfo.EngineVersion` reads the informational version (`0.1.0-alpha.0.N+sha`), the host logs it,
  a new `version` command prints it (test: TheVersionCommandPrintsIt), and `BuildInfo.EngineSemVersion`
  is its `major.minor.patch` (test: TheEngineVersionIsASemVerFromGitNotTheDefaultAssemblyVersion). CI
  checks out with `fetch-depth: 0`; without git MinVer warns and builds `0.1.0-alpha.0`. The templates
  name `Sage.Sdk/SAGE_SDK_VERSION`, an `sdkVersion` template parameter whose default the pack stamps.
- **The public API is declared** by `Microsoft.CodeAnalysis.PublicApiAnalyzers` on Core, Simulation,
  Physics3D, Gameplay, UI (since #95), Client and both halves of the RPG kit (`SagePublicApi` in `src/Directory.Build.props`;
  the host, CLI, editor and generators are not compiled against). All of it starts in
  `PublicAPI.Unshipped.txt`: nothing ships before `v0.1.0`, and moving it to Shipped is a step of tagging.
  RS0016/RS0017 are errors in every configuration on both CI jobs; a Linux step adds an undeclared public
  type to Sage.Core and expects RS0016, and a test runs the analyzer from its package on a small library
  (test: ANewPublicMemberWithoutTheFilesIsABuildError) (test: EveryExportedTypeOfTheBaseIsDeclared).
  RS0026/RS0027 (optional-parameter design advice) are off.
- **Internalised what games and kits do not need**, 606 exported types to 522: the engine's systems
  (every base plugin's, the client's and the kit's), the built-in vocabulary entries, the renderer's
  snapshot and audio backends, the `.map` reader and FGD export, the console registrars. What another
  engine assembly uses stays public, because `InternalsVisibleTo` goes only to the tests; that decides
  #24's list — the record JSON helpers, `Upgraders`, `Screen.Index`'s setter, `RegistrationLedger.Owner`,
  `MovementProfileRecord.Fallback` and the keyboard and mouse listeners stay public (RELEASING §2).
  `Entity.DeleteEntity`/`AddChild`/`RemoveChild` are internal (World's checked methods are the way), and
  `World.Get<T>` on a missing component throws in Shipping too, instead of returning a shared dummy
  (test: Get_ReturnsRef_MissingComponentThrowsInEveryBuild).
- **Experimental:** SAGE0120 the open vocabularies' contracts (#28), SAGE0121 scenes and placements (#29),
  SAGE0122 brush maps. The declaring assemblies suppress their own ids, the client SAGE0122 (it draws
  maps), the tests all three.
- **The `sage` range.** The plugin id `sage` is the engine: `[RequiresPlugin("sage", "^0.1")]` is checked
  when modules are sorted, and game.json's `"sage"` when the manifest loads, each an error naming both
  versions (test: APluginForAnotherEngineIsALoadErrorThatSaysBothVersions)
  (test: AGameMadeForAnotherEngineIsALoadErrorThatSaysBothVersions); no plugin may take the id
  (test: NoPluginMayCallItselfSage). The kit, the Sandbox, Hello and the `sage-game` template say `>=0.1`
  (test: TheRpgKitSaysWhichEnginesItIsBuiltFor). Mods: `mod.json` gets `"sage"` in the template, read
  once data mods load (Stage B).
- **Not done here:** a build-time check of a malformed `[RequiresPlugin]` range (it is a load error);
  Core, Simulation and Gameplay still export ~490 types, most of them the vocabulary games use, and a
  finer pass (members, not types) can follow once games outside this repository exist to say what they use.

---

*As built (issue #26, 2026-09-29): conventions and `Died`.* The last two hard rules above hold for
gameplay. **`gameplay_conventions`** is a record type of `sage.gameplay.attributes`
(`src/Sage.Gameplay/Conventions/GameplayConventions.cs`); the engine ships one well-known instance,
`sage:default_conventions`, and a kit or game patches it. It names health, the dead and invulnerable
tags, the default damage type, attack, movement and AI profiles, the player's faction, the attribute
spellmaker spells cost, the AI's five built-in schedules and the action names, and code reads it with
`world.Conventions()` — so a game renames health to hp in one record (test:
AGameRenamesHealthToHpByChangingOneRecord). No `new RecordId("sage", …)` is left in `Sage.Gameplay`,
`Sage.Physics3D` or the games, and a test keeps it that way (test: NoGameplayCodeNamesAnEngineRecordId).
`EffectSystem` raises **`Died(Victim, Killer)`**; factions, quests and the game's rules
(`DeathRulesSystem` → `GameRules.OnEntityDied`) read it. Differences from the plan:

- **Two narrative plugins, not one `NarrativeModule`**: `sage.gameplay.quests` (quests, the `journal`)
  and `sage.gameplay.dialogue` (dialogue, the `dialogue` part, `Conversation`), beside
  `sage.gameplay.factions` (factions, `reputation`). The acceptance switches factions, quests and
  dialogue off one at a time, which one module could not do (test: EachNarrativePluginCanBeSwitchedOffAlone).
  Saved-resource, component and part ids are unchanged.
- **Action names stay registered in code.** Actions are registered in `Init`, before content loads, so the
  record picks among registered actions (defaulting to the engine's names) and an unregistered one is a
  load error, rather than the record creating actions.
- **The character controller reads its share through `CharacterConventions`** (`Sage.Simulation` since #30), because
  it sits below the assembly that owns the record; gameplay installs one that reads it.
- **Still constants below gameplay:** the simulation's own defaults — `sage:lit_default`, `sage:error`,
  the sprite and particle materials, `sage:clear` weather and `sage:default` physics material — are
  rendering and physics fallbacks rather than gameplay conventions, and wait for a scene or kit record.

## 4. Pillar plans

### 4.1 Gameplay layering and data-only games (serves all three)

- **Split** `Sage.Engine/Gameplay` into:
  - `Sage.Gameplay`: generic;
  - `Sage.Kits.Rpg`: the spellmaker, readied `Selected` spell, journal and dialogue screens,
    `sage:mana` cost, and sprite clip names `attack`/`hit`/`idle`. The Kit exposes these as conventions
    in data. *Done (issue #27), see §3.1: the cost and the clip names are `gameplay_conventions` fields,
    the rest is the kit's.*
- Move dialogue and quest record registration out of `FactionsModule` (`GameplayModules.cs:544-554`). *Done (issue #26): `QuestsModule` and `DialogueModule`, see §3.1.*
  Split `GameplayModules.cs` (634 lines) and `MapLevel.cs` (757 lines, 11 types) into one file per module.
- **Promote scene spawning into the engine.** `SceneRecord`, the spawn loop and respawn-on-reload are
  Sandbox C# today (`SandboxModule.cs:39,56-77,154`), and every game rewrites them.

  *As built (issue #29, 2026-09-29).* **`SceneRecord` is the engine's** (`Sage.Simulation/Content/Scenes.cs`,
  `Plugin = RegistrationOwners.Core` like `prefab` and `placements`, so a `"plugins": []` game has scenes):
  in Simulation because a scene is records, prefabs, placements, maps and the origin, and needs no
  physics or gameplay. A scene names `maps`, `placements` documents, an `origin` and a default
  `relativeTo`, a `player` placement, `place` and `environment.weather`. **One placement format** for
  scenes and the editor's `placements`: `prefab`, `at`, `yaw`, `name` and an optional `relativeTo`
  (`World`, the default; `Origin`; `Ground`, y above the terrain), camel-cased; the Sandbox's relative
  `at` became `"origin": [512, 0, 512], "relativeTo": "Ground"`. `game.json`'s `"scene"` picks the start
  scene (a name that is not a scene is a load error); `Engine.CreateWorld` places it after every
  module's `OnWorldCreated` and before the rules start, and `GameRules`' default `OnWorldStarted` and
  `SpawnPlayer` spawn the scene's player. Hot reload sweeps only what the scene placed (tagged
  `sage:from_scene`, its documents, its levels) and keeps the player (test:
  HotReloadRespawnsTheSceneWithoutDuplicatingOrLosingThePlayer); a save's reload re-tags placed entities
  by persistent id (test: AfterASaveLoadsAReloadStillDoesNotDuplicate); `scene_load <id>` swaps scenes
  and moves the player (test: SceneLoadReplacesTheSceneAndMovesThePlayer). `tests/games/scene-only` has
  no C# and boots into its scene (test: AGameWithNoCodeBootsIntoItsScene); CI validates and smoke-runs it,
  and `sage schema` includes it. The Sandbox and Hello use it; `SceneRecord` left the Sandbox. Not done:
  the scene's rules and conventions (a `conventions` slot waits for #26's `gameplay_conventions`
  record), time of day (no clock yet), and a saved "which scene" (a load assumes the world's current one).
- **Data-only games.** `LoadGame` must stop requiring exactly one `IGameModule`
  (`Modules.cs:206-207`). With a kit chosen in `game.json`, a terrain or level record and a
  `gameplay_conventions` record, a designer can make a playable game **with zero C#**. This is the
  single biggest statement the redesign can make to designers.
- Ship **templates**: `Empty`, `RPG` (Daggerfall-like) and `FPS` (HL1-like, once the Shooter kit exists).
  Each is data-only and runs on the stock Player host.

### 4.2 Content authoring and errors (designers first)

- **JSON Schema everywhere.** Every data file gets a `$schema`. The SDK and template write
  `.vscode/settings.json` mappings, so VS Code gives autocomplete, hover docs and red squiggles *before*
  the game runs. `sage schema` also emits the id enums from loaded content, including mods.
  As built (issue #21): the mapping, not a `$schema` per file (an array file has nowhere to put one);
  the repository's `.vscode/settings.json` maps the committed `schemas/` onto `**/data/**/*.json` as
  JSON with comments (test: AMisspeltComponentFieldInASandboxPrefab_FailsTheSchema). The SDK template
  writing its own waits on `Sage.Sdk` (Phase 3).
- **Strict loading by default.**
  - Unknown fields in components and part options are an error (`UnmappedMemberHandling.Disallow`).
    Today `"light": {"color": …}` is silently ignored (`PrefabParts.cs:22-35,77-82`).
  - Prefab bodies are validated at **load**, not spawn (`Prefab.cs:31-37`).
  - `AssetPath` fields are checked for existence.
  - `RecordRef<T>` is type-checked. Today one id set spans all types (`RecordStore.cs:436`).
  - Errors carry **file:line:column**.
- **Missing records fail visibly.** `RecordStore.Get<T>` returning `new T()` (`RecordStore.cs:116-121`)
  stays only as a Shipping safety net. In dev, a missing reference is a load error and never a
  zero-damage sword.
- **In-game problems panel.** Every content error or warning appears as a clickable list in the dev
  overlay and editor, grouped by file, with a count badge on the HUD. It opens the file at the line.
  "Grep the log for ERROR" stops being a designer's job.
- **Nicer data:**
  - Replace the string mini-languages (`"Wait:1.5"`) with objects (`{"task":"wait","seconds":1.5}`).
  - Colours as `"#FFB0A0"`, not `4294960111`.
  - Units in schema metadata, not in field names.
  - Case-sensitive component names, so typos are caught.
- **`sage validate <game> [--mods …]`** runs headless. It loads every mount, runs all checks and exits
  non-zero. It serves as CI for games and a pre-publish check for mods.
- Fix the doc drift found in the review: `rec_print` should be `rec_get`, "twenty-four" should be 25,
  and so on (see §7).

### 4.3 Designer logic: open vocabularies, not a second language

This is a direct conflict with decision D3 ("no scripting"), and the reviews disagreed. My
recommendation is a staged approach: build the vocabulary first, and decide on a language later against
measured need.

**Stage 1: open the closed sets into registries.** This follows the existing `AITaskRegistry` and
`EntityInputs` pattern, declared through §3.4.

| Closed today | Becomes |
|---|---|
| AI conditions enum, 64-bit mask (`AI.cs:17-44`) | `[AICondition]` registry (perception defined by the game) |
| `ChooseSchedule` private static (`AIThinkSystem.cs:300-330`) | selector named by `ai_profile` (see stage 3) |
| `ObjectiveKind { Kill, Have }` (`Quests.cs:19-23`) | `[QuestObjective]` registry (reach, talk, use, custom) |
| Dialogue `Requires` and `Then` fixed fields (`Dialogue.cs:21-71`) | `ICondition` / `IAction` |
| `AbilityTargeting` enum, `internal` payload (`Abilities.cs:19-26`, `AbilityPayload.cs:16`) | `[AbilityDelivery]` registry |
| Effects only modify numbers | `[EffectExecution]` registry (knockback, teleport, summon, dispel) |
| Items have no "use" (`Items.cs:21-35`) | `[ItemUse]` registry (consume, read, cast) |
| 64-tag and 64-action caps (`Attributes.cs:48`, `PlayerCommand.cs:26`) | bitset grows, or tags become interned ids |

As built (issue #28): one mechanism serves every row. A vocabulary is an interface (or abstract class)
marked `[Vocabulary("name", Key = …, Default = …)]`; an entry is a class carrying an attribute derived
from `VocabularyEntryAttribute<T>` (`[AICondition("is_night")]`), and `VocabularyGenerator` registers
it for its plugin just before that plugin's `Init`, like records and parts (SAGE0100–0104; SAGE0020
covers `Vocabulary<T>.Register` by hand). `Engine.Vocabularies` holds the registries, sealed when
content loads; a field of a vocabulary's type reads `"name"` or `{ "<key>": "name", …settings }` from
JSON through the engine's converter, strictly (an unknown name or setting is a load error with the
nearest one). `sage schema` writes `schemas/vocabularies.schema.json` (every registered id, and each
entry's settings), a `[VocabularyRef]` string names ids from it, and the registry dump lists every
entry with its owner. Every closed set became one, with today's behaviour as registered defaults and
no content change (test: AnUnknownEntryIsALoadErrorThatSaysTheNearestName)
(test: TheEnginesEntriesAreRegisteredByTheirPlugins):

| Was | Is | Owner | Defaults |
|---|---|---|---|
| `AICondition` mask | `ai_condition` (`IAICondition.Sense`, asked every think); a game's take free bits of the 64 | `sage.gameplay.ai` | the eleven perceived ones, at their old bits |
| `ChooseSchedule` | `ai_schedule_selector`, named by `ai_profile.selector`; `rules` reads the profile's `rules` | `sage.gameplay.ai` | `default` (the old code), `rules` |
| `ObjectiveKind` | `quest_objective` (`QuestObjective`: measured or counted via `QuestHappening`) | `sage.gameplay.quests` | `kill`, `have`, `reach`, `talk` |
| dialogue `requires`/`then` | `condition`/`action` lists on an option; the old fields are shorthand for them | `sage.gameplay.dialogue` (quest ones: `sage.gameplay.quests`) | `has_tag`, `lacks_tag`, `has_item`, `standing`, `quest`; `give_item`, `take_item`, `apply_effect`, `change_standing`, `start_quest`, `set_stage`, `finish_quest` |
| `AbilityTargeting` + payload | `ability_delivery` (release, then gather), named by `delivery`; `targeting` still names one | `sage.gameplay.abilities` | `self`, `touch`, `touch_area`, `area`, `projectile` |
| effects only modify numbers | `effect_execution` list on an effect, run on each application and period | `sage.gameplay.attributes` | `knockback`, `teleport`, `summon`, `dispel` |
| items have no use | `item_use` list on an item, `world.UseItem`, `use_item` | `sage.gameplay.items` | `consume`, `read`, `cast` |
| 64 tags, 64 buttons | a 256-bit `TagSet`; a two-word `ActionMask` (128 buttons) | — | saves still write tags by name |

(test: AGamesConditionPicksASchedulesThroughAProfilesRules) (test: ReachTalkAndAGamesObjectiveMoveAQuestAlong)
(test: ADialogueOptionUsesAGamesConditionAndActionsByName) (test: AnAbilityNamesAGamesDelivery)
(test: AnEffectRunsAGamesExecutionAndTheEnginesOwn) (test: AnItemIsUsedThroughItsUsesInOrder)
(test: TagsGoPast64) (test: ActionMask_HoldsButtonsPast64). AI conditions stay a 64-bit mask, which a
think reads without allocating; the conditions and actions are stage 2's seed, read by dialogue only so far.

**Stage 2: one shared condition and action language in data.** Anything that decides or does
something uses the same typed vocabulary, including dialogue, quests, triggers, items, AI and I/O:

```jsonc
"requires": { "all": [ { "has_item": "sandbox:key_iron" },
                       { "not": { "quest_stage": { "quest": "thin_the_wood", "atLeast": 2 } } } ] },
"then":     [ { "give_item": "sandbox:gold", "count": 20 },
              { "fire": "hut_door", "input": "Open" },          // dialogue can now open a door
              { "set_stage": { "quest": "thin_the_wood", "stage": 3 } } ]
```

- It's schema-validated and hot-reloadable. Mods can extend it with data, and programmers extend it by
  adding `[Condition]`/`[Action]` types.
- It's safe for untrusted mods, because it can only call registered verbs.
- It's saveable, because the save stores *which* verbs ran, not script state (the Papyrus lesson that
  17 §2 cites).

As built (issue #89, decision D2): the language is the base's. `ICondition`, `IAction`, their attributes
and contexts moved to `Sage.Simulation` (`Logic/Conditions.cs`; still SAGE0120, content ids unchanged),
and the base owns `all`, `any`, `not`, `var` and `fire`, `set_var`, `add_var` (plugin `sage.core`), so a
game with no plugins but its own reads the `requires` above and asks it with `Conditions.Evaluate`
(test: AGameWithoutDialogueReadsAndEvaluatesNestedRequires). An entry may be written as the sketch
writes it, its id as the property (`VocabularyAttribute.Shorthand`, `[EntryValue]`), or in the long form;
an unknown id is a load error with the nearest one (test: AnUnknownIdSuggestsTheNearestOne). World
variables are the saved `vars` resource (test: AVarSurvivesSaveAndLoad); asking allocates nothing
(test: EvaluatingConditionsAllocatesNothing). The gameplay entries moved from dialogue to the plugins
that own what they ask about, and `quest` takes `atLeast` — a stage *name*, since stages are named, not
numbered (test: AQuestConditionAsksHowFarAlongItIs). Dialogue's `requires`/`then` stay as sugar. What
reads a `requires` besides dialogue — wires, relays, state machines, topics — is the rest of phase 4b
(#91–#93). Details: 16 "As built (one condition and action language)".

As built (issue #93): **topics** are the language's first reader besides node options. A
`dialogue_topic` is a keyword and ordered infos, each one `requires` condition, a `then` and a line; the
first that holds answers (test: ATopicAnswersByStandingQuestStageAndAVar). The listener's learnt topics
are the saved `sage:known_topics`, taught by the dialogue plugin's `add_topic` action
(test: ATopicLearntFromOneNpcIsAskedOfAnother) (test: KnownTopicsSurviveSaveAndLoad), and
`DialogueTopics.Available`/`Ask` are the headless view model 4c's screen reads (#98). Details: 16 "As
built (dialogue topics)".

As built (issue #98): that screen exists. The RPG kit's `rpg:topics` screen record, over `TopicsView`,
lists `DialogueTopics.Available` for the player and the NPC (the screen's `UiBindContext.Other`) and asks
with `DialogueTopics.Ask` on A, so the answer's `then` — an `add_topic` among them — runs through the same
language as every other `then`, and the topic it teaches is in the list at the next refresh
(test: AGamepadMovesAnItem_IsRefusedOverTheWeightLimit_LootsACorpse_AndAsksATopic). A layout's
`visibleIf` now asks its conditions with that other entity as `other`. Details: docs/design/13 "As built
(the RPG screens)".

**Stage 3: entity I/O as the level-logic backbone.**
- Add logic entities: `logic_relay`, `logic_counter`, `logic_timer`, `logic_compare`, `logic_branch` and
  `math_*`.
- Add bridge inputs and outputs:
  - quests: `SetStage` and `OnStageChanged`;
  - dialogue: `Start`;
  - `GiveItem`, `ApplyEffect`, `SetFaction`;
  - `OnDeath`, `OnDamaged` and `OnPickedUp`, raised from existing events.
- Today there are only 6 inputs (`EntityIO.cs:324-336`, `Movers.cs:108-110`).
- **Per-entity map keys.** TrenchBroom properties override prefab-part fields using the generated
  metadata, so each variant no longer needs its own prefab (MAKING_A_GAME.md:541).
- An I/O output and a data `action` become the same thing: `fire` is an action, and an input can take
  `then` actions.

As built (issue #91): inputs are **routed by component** (`EntityInputs.Register<T>`): every component
of the target that takes the name runs it, a global handler only when none did, so `Toggle`, `Enable`,
`SetValue` and `Trigger` are each component's own and movers keep theirs
(test: AnInputNameIsRoutedToEachComponentThatTakesIt). The engine's `logic_relay` (with `requires` and
`then`), `logic_counter`, `logic_compare`, `logic_branch` and `math_remap` are components, parts and
prefabs, driven by `ent_fire` headless (test: ACounterCountsBetweenItsLimitsAndHandsItsValueOn) and saved
(test: ACountersStateSurvivesASave); outputs hand a value on to a wire with no parameter; a wire takes an
optional `requires` (test: AConditionalWireFiresOnlyWhenItsConditionHolds_AndAFailureIsNotCounted); a
relay may deliver in the same tick (test: SameTickRelaysRunAChainInOneTick). The bridges are `SetStage`
(test: ABridgeInputChangesAQuestStage_AndAQuestWatchSaysSo), `StartDialogue`, `GiveItem`, `ApplyEffect`,
`SetFaction` and `OnStageChanged`, `OnDeath` (test: DeathAndDamageAreOutputs), `OnDamaged`,
`OnPickedUp`, each registered by its plugin. `logic_timer` was #90's. Per-entity map keys were already
there (§5 of MAKING_A_GAME: `"logic_counter.max" "3"`). Details: 04 §3.4d.

**Stage 4: AI as data behaviour trees.**
- Leaves are the existing `IAITask`s, and today's schedules become a `sequence` node.
- The trees hot-reload, mods can patch them, and a graph editor can come later.

**Stage 5 (decision gate): embedded scripting.** Only if stages 1–4 plainly fall short for real
content, add a sandboxed language (Lua through MoonSharp, or a small expression language). It would be
scoped to *implementing* registry entries: a Lua function as an `ICondition`, `IAction`, `IEffectExecution`
or AI task. It would run deterministically in the sim phase and never hold saved state. A visual graph
editor, if one ever exists, is a front end over stages 2–4, never a new runtime.

### 4.4 Modding (build 17-modding.md, plus what it's missing)

**Package.** A mod is a folder or a `.sagemod` zip holding a manifest and `content/`, plus optional
`bin/`.

```jsonc
// mod.json
{ "id": "thieves_guild", "name": "Thieves' Guild", "version": "1.2.0",
  "game": "sandbox", "gameVersion": ">=0.3", "sage": ">=0.4 <0.5",
  "kind": "data",                         // data | code  (code = trusted assemblies, flagged in UI)
  "dependencies": { "better_ai": ">=2.0" }, "loadAfter": ["hd_textures"], "conflicts": ["old_guild"],
  "shadows": ["textures/creature.png"] }  // intentional asset overrides; others must be namespaced
```

- **Namespaced assets.** Asset paths become `ns:textures/x.png`, and a bare path means the mount's
  namespace. Overriding another namespace's file must appear in `shadows`. That turns silent last-wins
  collisions (`VirtualFileSystem.cs:163-168`) into declared ones.
- **Load order:**
  - a deterministic topological sort on `dependencies` and `loadAfter`/`loadBefore`, with the user's
    `mods.json` order breaking ties;
  - printable with `mod_order`;
  - SemVer checked, with refusal shown in the mod manager and not only in the log.
- **Conflict report.** Extend `FieldOrigins` (`RecordStore.cs:49`) into "field X of record Y was
  written by mods A and B, and B won". Include plugin-level conflicts (§3.3). Show it in a pre-play
  **mod manager screen**, available in Shipping, and as `mod_conflicts`.
- **Patch semantics fixes:**
  - R11: bare ids in a patch resolve against the *patching* file's namespace.
  - `"$remove": true` for keys. Today `null` means "apply with defaults" (`Prefab.cs:185-186`).
  - **Keyed list merge** for lists of objects with `id`/`key` (dialogue nodes, quest stages,
    placements), so a mod can change the third option of a conversation.
  - An explicit `"replace": true` for a whole-record override, which is impossible today
    (`RecordStore.cs:280-281`).
- **Code mods:**
  - one **collectible `AssemblyLoadContext`** per mod, sharing the `Sage.*` assemblies with the host and
    resolving the mod's own dependencies from its folder;
  - loaded before the ECS schema is built;
  - flagged "contains code" in the manager, never auto-downloaded, and clearly documented as
    **not sandboxed**.
  - Data mods get the stage-2 vocabulary, so most mods never need code.
- **Shaders in mods.** The host compiles `.fx` with mgfxc for any mount in dev builds, and on
  hot reload. The mod SDK ships a `sage build-mod` step that precompiles for packaging.
- **Tools for modders.** The editor host (§4.6) is **shippable next to a game**. A studio can choose to
  release it, with its build configuration separate from the dev overlay. Today Shipping contains no
  tools at all.
- **Hardening:** zip-slip checks, size limits, and no absolute paths in mounts.

### 4.5 Saves that survive content and mod changes

- **Header:** mod list with versions and content hashes (17 §3 and 09 §3.6 promise this;
  `SaveSystem.cs:102-109` doesn't write it). Load warns on mismatch, and the mod manager can offer to
  re-enable mods.
- **Versions:**
  - Per-component versions with `[Upgrade(n)]`.
  - Keep `FormatVersion` but allow upgrading older saves instead of refusing any mismatch
    (`SaveSystem.cs:203`).
  - Golden save files in tests: a save from each released version must still load.
- **Content vs state.** Go back to **opt-in** `[Saved]` for values that come from content. Today every
  public field is saved, so a designer's rebalance never reaches existing saves
  (`SaveAttributes.cs:8-15`, `SaveSystem.cs:270-288`).
- **Missing prefab:** spawn a placeholder that keeps the saved components, instead of dropping the
  entity (`SaveSystem.cs:270-271`).
- **Atomic load.** Parse every world before destroying any (`SaveSystem.cs:248`), and widen the catch
  (`FormatException` from `RecordId.Parse` escapes it today, `SaveSystem.cs:225,266`).
- **Runtime spawns persist by default.** `World.MakePersistent` is called only by tests, so nothing
  spawned at runtime survives a save.

### 4.6 The editor: a separate host around a document model

This is D7 as originally decided. The review found the in-game overlay can't reach it incrementally,
because it edits the live play world (`DevTools.cs:84-87`).

- **`Sage.EditorHost`** loads the same plugins through `SageApp` into an **edit world** that is built
  from **documents**: placements, prefabs, records and levels. The document is the source of truth, not
  the world. That fixes several problems in one go:
  - Inspector edits no longer get lost on save.
  - The inspector no longer touches entities no document owns.
  - Documents stop being bound to whichever world was created last (`DevTools.cs:82-94`).
- **Command model.** Every edit is an `IEditorCommand` with `Do`/`Undo` (sketched in 15 §4). Each
  command is also a console command, which keeps the "every menu item is a command" rule. There's an
  undo stack per document, and "dirty" is derived from the log position.
- **Play-in-editor.** Serialise the edit world into a fresh play world, run it, and throw it away on
  Stop.
- **Forms from metadata (§3.4):**
  - inspector and record editor with ranges, enums, tooltips, units, and pickers for record references
    and assets;
  - per-field provenance, meaning which file or mod set it (`Records.Describe` already computes this);
  - per-placement **overrides stored as diffs against the prefab**.
- **Write-back that preserves the file.** Edit only the touched record in place, keeping comments,
  order and formatting (a JSONC-preserving writer). Today the whole file is re-serialised: comments are
  lost, and `placements.json` has drifted to PascalCase keys and five-line vectors.
- **Scope, in order:**
  1. place a prefab from a palette, with picking and gizmos (F29);
  2. undo (F30);
  3. record forms;
  4. problems panel;
  5. conditions and actions editor;
  6. behaviour-tree view;
  7. asset browser;
  8. material preview.
- **Levels are built in this editor, not in TrenchBroom** (decided 2026-09-28, §6 item 8). In the edit
  world: block out rooms with simple brushes (boxes, wedges, cylinders; CSG only where a level needs
  it), place prefabs on them, set per-entity overrides from the metadata forms (§3.4), and wire entity
  I/O by picking source and target. A level is a document like any other, saved as records, so mods
  patch levels the way they patch everything else. The `.map` importer (15 §3) stays until the editor
  can build what it builds, then becomes an import path for existing maps.
- The ImGui overlay stays in dev builds as a **debugger**: console, stats, outliner, `ent_dump`. It's
  not the authoring tool.
- **Renderer prerequisites:**
  - multiple views per snapshot (`RenderSnapshot.cs:75`);
  - render targets, for viewports and thumbnails;
  - a public `RenderContext` in place of today's `internal` `ResolveMesh`/`ResolveTexture`/`Materials`
    (`Renderer.cs:122-126`).

### 4.7 Rendering and UI extensibility

- **Render passes.** Replace the private static `Passes` array (`Renderer.cs:340`) with registered,
  ordered `IRenderPass`es, each with an Extract hook and a Draw hook and able to allocate render
  targets. Games and code mods can add post-processing, outlines and similar passes.
- **Public extract API,** so game code can produce render items without going through `MeshRenderer`.
- **UI:**
  - Keep `Screen`/`Panel` as view-models.
  - Add `ui_style` and `ui_layout` records (fonts, colours, anchors, templates) used by `PanelView` and
    the HUD. That replaces the hard-coded colours (`ScreenSystem.cs:136-144`) and the hand-placed HUD
    (`Sandbox.Client/Hud.cs`).
  - A screen can be **declared in a record** bound to a named panel provider, so designers and modders
    can add a screen, or re-lay-out one, without C#.
  - Add localisation keys now, while strings are few.
  - *As built (issue #96, 2026-09-30).* `ui_style` (colours per state, padding, font), `ui_layout` (a
    flat widget tree by node name, with bindings into a view-model and `visibleIf` conditions) and
    `screen` (a layout bound to a `[ViewModel("id")]` class) records, built into #95's widgets headless
    and rebuilt on hot reload; `strings/<lang>/*.json` tables with placeholders and plural forms resolved
    from `@ns.key` by `Localisation`, and the `lang` cvar (docs/design/13 "As built (style and layout
    records)", 05 §3.7). Drawing them is #97; `PanelView` and the HUD still draw as before.
  - *As built (issue #97, 2026-09-30).* Widget screens are drawn and driven: a headless render plan
    (per style and state: backgrounds, nine-sliced style images, borders, bar fills, pictures, text;
    `Scroll` clips pushed and popped) cached against the tree's, the styles' and the string tables'
    versions and replayed into `UiDraw` (which gained a scissor stack and nine-slice); a `UiScreenStack`
    in every world that hosts screen records and code-built trees beside the panel screens in
    `ScreenSystem`, with Back, click-outside, focus on open, `Bind`/`ui_open`, and fades, slides and a
    focus highlight eased on frame time with 4b's `Easing`; the `Menu*` actions plus
    `MenuLeft`/`MenuRight`/`MenuTab` as its input. The Sandbox's status screen (`C`) is the demo
    (docs/design/13 "As built (drawing)"). The panel screens and the HUD move onto it in #98/#99.
  - *As built (issue #99, 2026-09-30).* The hand-placed HUD (`Sandbox.Client/Hud.cs`) is the
    `sandbox:hud` layout over `HudView`, on a layer that is drawn and never takes input
    (`UiScreenStack.OpenHud`; only the first-person hands are still drawn by hand); the journal and a
    markers-only map are the kit's `JournalView`/`MapView` (`sage:map_marker`); a main menu lists
    `SaveSystem.Slots` and loads one; the shop is a shell over a stub price rule until 4f
    (docs/design/13 "As built (the HUD, journal, map, main menu and shop)").
  - *As built (issue #98, 2026-09-30).* The first consumers: the RPG kit's inventory grid (with weight
    and item footprints), equipment, loot and topics screens are records in the kit's own content
    (`[PluginContent("rpg")]`) over headless view-models that act through `UiScreen.Handle`, and one test
    drives them with a gamepad — phase 4c's exit criterion (docs/design/13 "As built (the RPG screens)").
- **Asset hot reload** for meshes (`.glb`), sounds and `.fx`, in addition to textures and records.

### 4.8 SDK, packaging, CI and hygiene (developers first)

- **`Sage.Sdk`** is an MSBuild SDK: `<Project Sdk="Sage.Sdk/0.4.0">`. It carries:
  - target framework, usings, configurations and shader targets;
  - generation of `game.json` from csproj properties;
  - `dotnet run` launching the Player host with `-game`.

  Games build **out of tree**. This also fixes a bug: `Release` is an alias of `Shipping`, so `{config}`
  resolves to `bin/Shipping`, and the solution maps the games' Release to Debug
  (`GameManifest.cs:41`, `Sage.sln:238-267`). `dotnet publish` will probably not find the game DLL.

  *As built (issue #32, 2026-09-29).* **`Sage.Sdk`** is `sdk/Sage.Sdk/Sdk/Sdk.props` and `Sdk.targets` over
  `Microsoft.NET.Sdk`: net8.0 with RollForward=Major, the three configurations (build/'s file), nullable,
  implicit usings and the engine's namespaces, the engine compile-time only, the Sage generators and
  analyzers (SAGE0050 included), the game's own `.fx` compiled with `mgfxc` beside the source (so it is in
  a mount), `bin/<config>/Name.dll` with no target framework in the path, and `dotnet run` starting the
  host with `-game <project folder>` (`dotnet msbuild -t:SageValidate` runs `sage validate`). One set of
  files, two resolutions: packed (`tools/pack_sdk.sh`), the SDK restores **`Sage.Player`** of its own
  version, the host and the `sage` CLI per configuration, which is both what `dotnet run` starts and what
  the game compiles against; imported by path from the source tree (`games/Hello`, the in-repository
  proof), it references `src/` and runs `src/Sage.Host`'s build. `game.json` is **not** generated from
  csproj properties: it stays the one hand-written manifest the host, `sage validate` and `sage schema`
  read, and the SDK checks after each build that its `assembly` and `modules.add` paths are what was
  built, including its own dll (SAGE0110, SAGE0111; SAGE0112 is a missing host or engine, SAGE0113 shaders
  without `mgfxc`). A kit (#27) is `<SageKit Include="sage.kits.rpg" />`: its assembly (and its client half
  in a client project) compile-time only, its namespace as a global using, and SAGE0114 when `game.json`'s
  `"kits"` does not name it; `dotnet publish` of the host and the CLI now carries the kits too. The host no longer loads `games/Sandbox` without `-game`: it runs `./game` beside
  the executable or stops with an error that lists the repository's games
  (test: GameManifest_Locate_WithoutAGameIsAnErrorThatListsTheGames). Templates: `sage-game` (a client
  half in the game folder, so `dotnet run` there starts the game, and the simulation half under
  `Simulation/`), `sage-game-data` (no C#, the engine's scene) and `sage-mod-data` (a stub until data
  mods). CI packs all three packages into a local feed and, outside the checkout, builds a game from
  each template, validates it (the mod against the game), checks SAGE0050 fires, and `dotnet run`s it
  under Xvfb, walking; Windows builds one with a shader of its own. Not yet: a public feed (§6 decision
  7), `sage` as a dotnet tool, `sage-game-client`/`sage-mod-code`, the Release→Shipping `{config}` fix
  below (a packed Player has no Release host, so the SDK says to use Shipping), and the Sandbox on the SDK.
- **`dotnet new` templates:** `sage-game`, `sage-game-client`, `sage-mod-data` and `sage-mod-code`.
- **`sage` CLI:**
  - `new`, `run`, `validate`, `schema`, `package`;
  - `package` copies the Shipping build, packs mounts into zips and strips `tools/`.
- **CI (GitHub Actions), on Windows and Linux:**
  - `dotnet tool restore`, then build all three configurations;
  - tests with coverage;
  - `check_docs.py`, with the test count read from the test run;
  - public API diff;
  - template smoke test (`dotnet new sage-game && dotnet build && sage validate`);
  - a headless scripted run of `Hello`.
- **Build hygiene:**
  - `global.json`, a root `Directory.Build.props` (Nullable, warnings as errors, LangVersion,
    Deterministic) and `Directory.Packages.props`.
  - The props file doesn't set Nullable today, and 5 files lack `#nullable enable`.
- **Repo hygiene:**
  - Delete `pipelines/CustomModelPipeline`: dead XNA 4.0 code, with its committed `.vs/` folder.
  - Replace the vendored `packages/MonoGame.ImGuiNet-main` (696K, samples and FBX included) with the
    NuGet package, or `third_party/` plus a patch note.
  - Delete branch `dev_branch_test`. It has 23 throwaway commits from 2025 and no engine work.
  - Remove stale `.gitignore` lines.
- **Tests:**
  - Replace the 35 hand-rolled bootstraps with `Sage.Testing.HeadlessApp`, turn parallelisation back on
    once statics are gone (§3.2), and put test files in folders that match their subject (almost all
    are in `World/`).
  - Add golden saves and maps, a benchmark project (from `scale-2026-09-24.md`), and a first client
    smoke test through a scripted run.
- **Registry dump.** The engine emits `--dump-registry` JSON (commands, cvars, records, I/O, conditions
  and so on) from the §3.4 metadata. `check_docs.py` reads that instead of regexes over call shapes.

---

## 5. Roadmap (base engine first, action RPGs as the first target family)

**Ordering principle** (§0.5):
- **Stage A** makes the base engine *able to hold* any genre.
- **Stage B** fills the gaps for **one target family first: first- and third-person action RPGs**, which
  is where the engine will first be used.
- **Stage C** proves it with reference games from that family, including yours.
- **Stage D** widens the base to the other genres, 2D included.
- **Stage E** opens it up to designers and modders.

**Decided 2026-09-27: the first family is action RPGs.** The design stays general. The genre-agnostic
boundary in §0.5 and the 2D profile both still hold, and phase 3 still builds the physics facade the 2D
backend needs. Only the *order* changes: the gaps that action RPGs hit come first, and the 2D profile,
RTS picking and other genres move to Stage D.

**The reference family** is Daggerfall, Half-Life, Morrowind and S.T.A.L.K.E.R. What they share, and
what the base therefore needs first:

| Need | Daggerfall | HL1 | Morrowind | S.T.A.L.K.E.R. | Sage today |
|---|---|---|---|---|---|
| First-person play (third-person optional) with viewmodels | ● | ● | ● (both) | ● | ◐ first-person rig and sprite viewmodels only |
| Skeletal characters and first-person arms | billboards | ● | ● | ● | ❌ |
| Melee **and** ranged: projectile, hitscan, ammo, reload, hit locations | ● | ●● | ● | ●● | ◐ melee plus projectiles; no hitscan, ammo or reload |
| Stats, skills, levelling, effects | ● | ○ | ●● | ○ | ◐ attributes and effects; no skills or levelling |
| Inventory, equipment, loot, shops, containers | ● | ○ | ● | ●● (grid, weight) | ◐ bag, slots by name (the RPG kit's two hands); no containers, shops or use |
| Dialogue (topics or trees), quests, journal, factions | ● | | ●● | ● | ◐ closed enums (§4.3) |
| Scripted sequences, doors, lifts, triggers, logic | ● | ●● | ● | ● | ◐ entity I/O with 6 inputs |
| AI with perception, schedules and combat; off-screen simulation | ● | ● | ● | ●● (A-Life) | ◐ HL1-style schedules; no off-screen simulation |
| Big world: streamed exteriors, interior cells, travel | ●● | levels | ● | zones | ◐ terrain streams; entities don't; no cell transitions |
| Time of day, weather, lighting, day/night | ● | | ● | ●● | ◐ weather and point lights; no time of day or shadows |
| Save anywhere, robust across updates | ● | ● | ● | ● | ◐ runtime spawns lost; no upgraders |
| Mod culture (data mods first) | ○ | ● | ●● | ●● | ❌ |

### Stage A: make the base able to hold any game (unchanged, and ready as GitHub issues)

Tracked on GitHub: Phase 0 [#2](https://github.com/ZohnHadley/sage-engine/issues/2) · Phase 1 [#9](https://github.com/ZohnHadley/sage-engine/issues/9) · Phase 2 [#15](https://github.com/ZohnHadley/sage-engine/issues/15) · Phase 3 [#23](https://github.com/ZohnHadley/sage-engine/issues/23). Each has its work split into sub-issues.

**Status (2026-09-30; details in [`history/handoff-2026-09-30.md`](history/handoff-2026-09-30.md)):**

| Phase | State |
|---|---|
| 0 — Clean ground | **Done** except a publish smoke test (#6) and deleting `dev_branch_test` (owner) |
| 1 — Kernel | **Done.** `SageApp` and `HostLoop` (#10), parallel tests and only the host's app configuring the process log (#11), sealed registration and plugins (#12), world resources owned by their plugins and `CreateRules` (#13), `Sage.Testing` and every test on `HeadlessApp` (#14); a game with no plugins runs in the real host (CI). Deferred to Stage E (#49), when an editor hosts a play session: a separate log, user folder and crash reporter per app |
| 2 — Declarations | **Done.** Generated registration for records, saved resources and parts (#16, #17); stable component ids and saves keyed by them with upgraders (#16, #20); declared systems with ids, replace and disable (#17); a metadata table used by the inspector, `ent_dump` and the FGD, and a registry dump `check_docs` reads (#18); analyzers SAGE0001–0042 (#19); strict loading with `RecordRef<T>`, file:line errors and `sage validate` in CI (#22); JSON Schemas for every record, component and part with id enums from the loaded content, written by `sage schema` into a committed `schemas/` that `.vscode/settings.json` maps onto every data file, checked for staleness in CI (#21) |
| 3 — Carve the base | **Done** (#23). the assembly split (#24, [plan](history/plan-24-assembly-split.md)), engine-owned scenes (#29), decoupled gameplay (#26), the physics facade (#30), the owned ECS API (#25), the RPG kit (#27: `games/Hello` runs on the base alone, `Sandbox` on base plus `Kits.Rpg`), open vocabularies (#28: `[Vocabulary]` registries for AI conditions, schedule selectors, quest objectives, dialogue conditions and actions, ability delivery, effect executions and item uses) and the SDK and templates (#32, §4.8), and public API files, SemVer from git tags and `sage` ranges (#31, [RELEASING](RELEASING.md)), each with an "As built" note |
| 4a — Cameras as components | **Done** (#75: #76–#81). Cameras are entities with a director, rigs for first and third person with the V toggle, scripted cuts from entity I/O, several views and named render targets, and the editor's free camera and viewport on them; both exit criteria run in the Sandbox with tests |
| 4b — Timers, tweens, state machines, logic | **Done** (#87: #89–#94). One condition/action language in the base; timers, tweens and a saved I/O queue; dialogue topics; state machines as data; logic entities, routed inputs, conditional wires and bridges; the exit games `tests/games/scripted-sequence` and `tests/games/topics` run with no C# |
| 4c — UI toolkit and RPG screens | **Done** (#88: #95–#99). `Sage.UI`, style/layout/screen records, localisation, drawing, input and transitions; the inventory grid with weight, equipment, loot and topics screens from the kit's records, and the HUD, journal, map, shop shell and a main menu that loads save slots, each with its exit test. **Next: 4d** |

| Phase | Theme | Main work | Exit criterion |
|---|---|---|---|
| **0** | Clean ground (days) | Hygiene (§4.8), CI, `global.json` and props, licence (D9), fix the doc contradictions (§7), fix `Release`/`{config}` | CI green on Windows and Linux; no dead projects |
| **1** | Kernel | `SageApp` and one boot path; per-app services instead of statics; plugins with sealed stages, string ids and a `kind`; plugin sets chosen in `game.json`; `Sage.Testing` headless app; the host ticks every world | Tests and the Player host boot the same way; a game with **no physics and no gameplay plugin** boots and runs |
| **2** | Declarations | Source generator and analyzers (§3.4) for components, records, parts, systems, conditions and actions; stable ids in saves; JSON Schema output; strict loading; `sage validate` [09 §3.2, R11] | Deleting any `Register` call is a compile error; VS Code autocompletes records |
| **3** | Carve the base | Assembly split (§3.1, §0.5 boundary): genre code moves to `Sage.Kits.Rpg`; own the ECS API (§3.5); `Sage.*` namespaces; PublicApi analyzers; `Died` event instead of direct calls; a `gameplay_conventions` record instead of `sage:` constants; open the closed enums into registries (§4.3 stage 1); engine-owned scenes; physics behind an `IPhysicsWorld` facade in `Sage.Physics3D`; `Sage.Sdk` and `dotnet new sage-game` | `games/Hello` runs on the base alone; `Sandbox` runs unchanged on base plus `Kits.Rpg`; a game built **outside the repo** compiles and runs; an analyzer proves no base assembly references a kit |

### Stage B: the action-RPG base

Each piece lands **in the base or in the gameplay plugin, never in a game**, and must pass the §0.5
test: nothing in it may assume the Daggerfall-like. `Sage.Kits.Rpg` is the *action-RPG kit*:
- stats, skills and levelling rules;
- inventory, equipment, containers and shop screens;
- dialogue and quest screens;
- the spellmaker as an optional Daggerfall-flavoured feature.

| Phase | Work | Exit criterion |
|---|---|---|
| **4a** | **Done** (#75). **Cameras as components**: perspective (and ortho, cheap to include); rigs for first-person (moved out of Gameplay), third-person over-the-shoulder with collision and 1P/3P toggle, fixed/cinematic for scripted scenes; several views per snapshot; render targets | switch 1P↔3P mid-fight; a scripted camera cut from I/O |
| **4b** | **Done** (#87). **Timers, tweens, state machines**; the **conditions/actions vocabulary** (§4.3 stage 2); logic entities and bridge I/O (stage 3). This is HL1's scripted sequences and Morrowind's dialogue conditions in one mechanism | an HL1-style sequence (door, lift, NPC line, counter) and a conditional dialogue topic, built in data only |
| **4c** | **Done** (#88). **UI toolkit** (retained widgets, layout, focus and gamepad, style/layout records, localisation keys). First consumers are RPG screens: HUD, inventory **grid with weight** (S.T.A.L.K.E.R.), equipment, container/loot, shop, dialogue topics, journal, map, and a main menu with save/load | inventory, loot and dialogue screens built from records over headless view-models, with tests |
| **4d** | **Skeletal animation** [F9–F12]: glTF skins, clips, blending, anim state machine as data, animation events replacing the `attack`/`hit`/`idle` names, **first-person arms and viewmodels**, simple IK for aiming and feet | an NPC walks, runs, aims and attacks, blended; first-person arms reload a weapon |
| **4e** | **Weapons and combat generalised**: one damage pipeline for melee, projectile and **hitscan**; ammo, magazines, reload; spread and recoil as records; hit locations (head/limbs) from colliders; armour by location; the existing melee and spells re-expressed on it | the same pipeline drives a sword, a crossbow, a pistol and a fireball, each defined by records only |
| **4f** | **RPG progression and economy (generic)**: skills that rise with use or with XP (both as rules), levelling, perks as effects; containers, loot tables, shops and barter, item "use" (§4.3 `ItemUse`), durability and weight | a Morrowind-style "use a skill, it rises" and a S.T.A.L.K.E.R.-style trader, both in data |
| **4g** | **Open world, part 2**: entities stream by sector (not only terrain), **interior cells** with load doors, fast travel, **time of day** (sun, sky, lighting, NPC schedules), and **off-screen simulation** (A-Life-lite: coarse movement and fights for unloaded NPCs, reconciled when they stream in) | walk from an exterior into a dungeon and back; an NPC keeps its schedule across a day while you are away |
| **4h** | **Rendering for these worlds**: render pass registry and public `RenderContext` (§4.7), sun shadows, fog, day/night lighting, post-processing; mesh, sound and `.fx` hot reload | a dusk-to-night transition with shadows in a streamed exterior |
| **4i** | **Saves you can trust**: prefab overrides and nesting [F31]; runtime spawns persist; upgraders and placeholders (§4.5); quick-save and autosave | save anywhere, change a prefab and a record, load: nothing lost and nothing duplicated |
| **4j** | **Data mods, lite**: `mod.json`, mods folder, load order, `mod_conflicts` (§4.4, the data half only). This genre's players expect it, and the VFS and patching already do most of the work | a data mod that adds a weapon and patches a trader loads, and its conflicts are reported |

Order: 4a → 4b and 4c → 4d → the rest in parallel. 4a–4c unblock everything, and 4d is the longest
single item.

*As built, 4a (issue #76, 2026-09-29): the camera component.* Phase 4a is split into #76–#81 (parent
#75). #76 makes cameras entities: a `Camera` component (`sage:camera`; perspective or orthographic,
priority, enabled, a normalised viewport and a render target), `CameraPose` for rigs, and the engine's
`CameraDirector`, which resolves one view per target into the `CameraViews` resource that the multi-view
renderer (#77) will draw. `ActiveCamera` stays as the director's mirror of the screen view (D1), and a
world with no camera entity behaves as before. Rigs will be camera entities that follow a pawn and write
`CameraPose` before the director (D2: #78, #79). Ortho is in from the start (§0.5). The API is experimental
(SAGE0123). Details and deviations: docs/design/06 "As built (camera components)".

*As built, 4a (issue #77, 2026-09-29): several views and render targets.* The render snapshot holds a
pooled list of value-type views, each with its own frustum, target, viewport and ranges of items and
sprites; extract runs per view, and the renderer draws views into render targets first, then the screen's,
then the UI once. The views are the world's `CameraViews` (a world without it draws `ActiveCamera`). The
renderer owns a pool of **named render targets** of fixed size (`Renderer.DeclareTarget`, or 512×512 for a
target only content names), and materials sample one as `rt:<name>` (D3). **One world draws to the
screen** (`Renderer.ScreenWorld`, the host's main world unless set): the others draw only into targets,
where before every world cleared the back buffer and the last one won. The planning half (view order,
per-view ranges) is `RenderViewPlan`, tested headless; `r_testview` shows a split screen and a target on
Linux CI's smoke run. The public `RenderContext` and the pass registry stay in 4h (§4.7). Details:
docs/design/06 §3.4a.

*As built, 4a (issue #78, 2026-09-29): the first-person rig.* The player's view is a camera entity that
follows the pawn (D2): `PlayerCameraSystem` spawns one from the engine's `sage:player_camera` prefab for
each player pawn, and its `FirstPersonRig` writes `CameraPose` at the pawn's eye before the director.
`FirstPersonCameraSystem` is gone. The camera is saved with its pawn (an id derived from the pawn's), so a
load neither duplicates nor loses it; `world.MainViewRig()` replaces `ActiveCamera.DrivenByRig` for the
crosshair and viewmodels; `cam_free` is unchanged until #81. Details: docs/design/06 "As built (camera
rigs)".

*As built, 4a (issue #79, 2026-09-29): third person and the toggle.* The player's camera also carries a
`ThirdPersonRig`: over the shoulder, a sphere swept from the head keeping it out of walls (in at once, eased
back out), its eased state a length so origin shifts leave it alone. `ToggleView` (V) turns the two rigs'
`Enabled` flags over in the Commands phase — two whole components rather than a mode, no structural change —
and switching mid-swing in the Sandbox changes nothing about the fight. The first-person view hides its own
body through #77's per-view seam (`CameraRigs.HiddenBy`); the Sandbox's player has a placeholder sprite to
be seen in third person. Details: docs/design/06 "As built (camera rigs)".

*As built, 4a (issue #80, 2026-09-29): scripted cuts from I/O.* The exit criterion's "a scripted camera
cut from I/O": entity inputs `CameraOn [hold]` / `CameraOff` and outputs `OnCameraOn` / `OnCameraOff` on
any camera, owned by the engine (`sage.core`) so a data-only game has them; the `sage:scripted_camera`
prefab (off, priority 100, locks the player's input while on); a hold counted in fixed ticks; a cut, with
blends left to 4b's tweens. A cut delivered in tick N shows in the first frame after it
(test: TheCutLandsOnTheFrameAfterTheTickThatDeliveredIt). Deviation: scene and placements documents can
now wire entity I/O (a placement's `outputs`), because only `.map`s could, and a `.map` classname may name
another namespace's prefab. `tests/games/camera-cut` proves it with no C#
(test: ATriggerCutsToANamedCameraAndBack_InAGameWithNoCode). Details: docs/design/06 "As built (scripted
cameras from entity I/O)".

*As built, 4a (issue #81, 2026-09-29): the host and the editor on camera views; phase 4a done.* The
editor's free camera is a camera entity (`DebugCamera`): below every other camera, so it has the screen
only where nothing else draws, and above every other while `cam_free` is on. The director has no special
case any more, the host no longer writes `ActiveCamera`, and `ActiveCamera.RigEnabled`/`DrivenByRig` are
obsolete and inert (kept one release, having shipped in 0.1.0). Audio, weather, `fx_play` and `ent_spawn`
place things by `world.TryGetMainView`; `ActiveCamera` stays the mirror (D1). The editor viewport
(`ed_viewport`) draws the free camera into the render target `editor` and shows it in an ImGui window,
the prerequisite for §4.6's editor host. The Sandbox shows the exit criteria: V on the HUD for 1P↔3P
mid-fight (test: ToggleView_MidMeleeInTheSandbox_SwitchesWithinAFrame_AndCombatIsUnaffected), and a
scripted cut when the player walks up the path to the hut, wired in its scene
(test: TheSandboxCutsToTheHut_WhenThePlayerWalksUpThePath_AndBack). SAGE0123 stays experimental until 4b
and 4c use it. Details: docs/design/06 "As built (the editor's cameras, and phase 4a's exit)".

**Phase 4a summary.** Six issues (#76–#81) turned the camera from a resource the host and one rig wrote
into entities the engine resolves: a `Camera` component and `CameraDirector` writing `CameraViews`
(#76); a renderer drawing every view, into named render targets or the screen, with one world on the
screen (#77); the player's first-person rig (#78) and a third-person rig with collision and the V toggle
(#79) on a camera entity per player; scripted cuts from entity I/O, wired in data (#80); and the editor
on the same terms (#81). `ActiveCamera` survived as a mirror (D1); rigs are components on camera entities
(D2); render targets are named and fixed-size (D3). Left for later phases: blends (4b), render targets in
UI widgets (4c), the editor's own world and picking (phase 10).

*As built, 4b (issue #90, 2026-09-30): timers, tweens and a saved entity I/O queue.* `Easing` (an `Ease`
enum of Penner's curves and `Easing.Apply`, pure maths; merged first because 4c needs it) drives a
`sage:tween` (a transform's position, rotation or scale, from `TweenTo`, firing `OnTweenDone`) and camera
blends (`CameraOn [hold [blend [ease]]]`; a cut stays the default); a `sage:timer` fires `OnTimer` every
interval, give or take a saved random spread (`TimerStart`/`TimerStop`/`TimerReset`, prefab
`sage:logic_timer`). All three are the engine's (`sage.core`), like cameras. Entity I/O is the
`entity_io` saved resource: pending inputs (target by persistent id, else name) and each wire's fired
count, and a scene's wires come back with a load; old saves load as nothing pending (no format change).
The I/O clock moves at the start of the tick, so a trigger's delay counts from the tick it fired in
(test: ATriggerWiresDelayCountsFromTheSameTickAsADispatchWires)
(test: ADelayedInputSavedHalfWayArrivesOnTheTickItWould_AndItsWireStaysSpent)
(test: TweensAreDeterministicAndSurviveASave). Experimental as SAGE0124 (phase 4b's logic) and SAGE0123
(the blend). Details: docs/design/04 §3.4b and 06 "As built (camera blends)".

*As built, 4b (issue #92, 2026-09-30): state machines as data (decision D4: a record and a small
component).* A `state_machine` record is an `initial` state and named `states`, each with `enter`/`exit`
actions, free `tags` and transitions that go `to` a state `on` an input (any name, registered or not),
`when` a condition holds or `after` seconds in the state — first match wins, the state's own before the
machine's from-any-state ones. `sage:state_machine` (part `state_machine`) saves the state by name and the
time in it; `SetState` jumps and `OnStateChanged` hands its wires the new state's name. A save or a hot
reload that names a state the machine has lost goes back to `initial` with a warning. The engine's
(`sage.core`), like timers; stepped in the EntityIO phase with no allocation
(test: AGuardIdlesGrowsAlertAttacksAfterATimeoutAndCalmsDownOnAnInput)
(test: StateAndTimeInItSurviveSaveAndLoad)
(test: HotReloadKeepsAStateThatIsStillThere_AndSendsOneThatIsGoneToInitialWithAWarning)
(test: StateMachinesAllocateNothingPerTick). `StateTransition` and `StateMachines.FirstTransition` are
the rules 4d's animation graph can step with. SAGE0124. Details: docs/design/04 §3.4c.

*As built, 4b (issue #94, 2026-09-30): the exit games.* Phase 4b's exit criterion, "an HL1-style sequence
(door, lift, NPC line, counter) and a conditional dialogue topic, built in data only", runs in two
games with no C#, both loaded headlessly by tests. `tests/games/scripted-sequence`: a trigger's
`OnStartTouch` opens a door, its `OnFullyOpen` raises a lift, the lift's `OnFullyOpen` fires
`StartDialogue` at an NPC, and every step `Add`s to a `logic_counter` (`max` 3) whose `OnHitMax` fires a
`logic_relay` (test: ATriggerDoorLiftNpcCounterAndRelayRun_InAGameWithNoCode). `tests/games/topics`: one
`dialogue_topic` answered by a var a relay sets (test: AConditionalTopicAnswersByAVar_InAGameWithNoCode).
No engine change was needed. CI validates, schemas and smoke-runs both. Details: docs/design/04 §3.4e
and 16 "As built (phase 4b's exit games)".

*As built, 4d (issue #116, 2026-09-30): skeletons, clips and headless pose sampling.* Phase 4d (#115)
starts in the simulation: `SharpGLTF.Core` moved from `Sage.Client` to `Sage.Simulation` (plain .NET, so
SAGE0024 allows it), where `GltfAnimationReader` reads a `.glb`'s skin and clips through the VFS into a
`Skeleton` (parents first, rest pose, inverse binds) and `AnimationClip`s (step, linear and cubic-spline
channels, with room for #119's events), cached by `AssetPath` and never loaded by a tick. `PoseSampler`
samples a clip into a pooled `SkeletonPose` (looping wraps, a one-shot clamps), blends two poses with an
optional per-joint `JointMask` and puts one in model space, allocating nothing
(test: SamplingAllocatesNothing). `SkinnedModelBuilder` in `tests/Sage.Testing` writes the test rig in
code. SAGE0126. Details: docs/design/12 "As built (skeletons and sampling)".

*As built, 4d (issue #120, 2026-09-30): sockets, bone attachments and simple IK.* A `SkeletonPoses`
world resource is the one place a pose source (the Animator, #118) registers an entity's pose; it
passes through to #117's `SkinPoses`, so IK and skinning see the same pose. Sockets are a
`skeleton_sockets` record keyed by model (a joint and an offset), and `bone_attachment` makes a child
follow one in Phase.Late (test: AnAttachedSwordFollowsTheHandThroughAClip). The solvers are pure
Simulation maths over a `SkeletonPose` — analytic `TwoBoneIk` with a pole, clamping unreachable targets,
and `AimChainIk`, per-joint weights and limits (test: AimPitchTurnsTheSpineWithinItsLimits) — and foot
IK is Gameplay's, casting through `IPhysicsWorld` and lowering the pelvis (test: FeetRestOnARamp); none
allocates per tick. SAGE0126, TODO F12. Details: docs/design/12 "As built (attachments and IK)".

*As built, 4d (issue #118, 2026-09-30): the animation graph as data.* The `anim_graph` record (states
that play a clip, a 1D or 2D blend space, or nothing; float, bool and trigger params, some filled from
the body's velocity and `PawnIntent`; layers with joint masks) steps with 4b's `StateTransition` and
`StateMachines.FirstTransition` and cross-fades along 4b's `Easing`. `sage:animator` and the `animator`
part run it; `AnimatorSystem` (Phase.Animation) samples each into a pooled `SkeletonPose` registered in
#120's `SkeletonPoses` (and so #117's `SkinPoses`); #120's IK runs after it, in Phase.Late. Saved by name; a lost state goes to `initial` with a warning;
distant animators sample less often (`anim_lod_distance`); `anim_debug` shows them; a hundred allocate
nothing a tick (test: AHundredAnimatorsAllocateNothingPerTick) (test: WalkAndRunBlendWeightsFollowSpeed).
SAGE0126. Details: docs/design/12 "As built (the animation graph)".

*As built, 4d (issue #121, 2026-09-30): first-person arms.* A `viewmodel` record (skinned arms in view
space, their `anim_graph`, a weapon on a #120 socket) is shown by a `sage:viewmodel` component on the
player's camera, chosen by the attack in the pawn's hands (`AttackRecord.Arms`; an attack without one
keeps its sprite hands). The pass is **one more view**, not a renderer layer: a copy of the main view with
the viewmodel's own field of view and depth range, drawn after it with only depth cleared, holding just
the `sage:viewmodel_layer` pieces the world's extracts leave out; it exists only while the screen looks
out of a first-person rig (test: TheViewmodelIsHiddenInThirdPersonAndFromTheEditorsFreeCamera). A
`Reload` action plays the arms' reload (test: Reload_Plays_FiresMagOutThenMagIn_AndReturnsToIdle), whose
clip events reach `AnimationEvent` through #119's event firing (a small stand-in until it landed). Nothing
allocates per frame (test: TheViewmodelAndItsExtractAllocateNothingPerFrame). SAGE0126. Details:
docs/design/12 "As built (first-person arms)" and 06 "As built (the viewmodel pass)".

*As built, 4d (issue #119, 2026-09-30): animation events replace the `attack`/`hit`/`idle` names.* Clip
events come from `anim_events` records keyed by model (and a sprite sheet's frame events); the animator
raises each once per crossing — through loop wraps, on distant animators as on near ones, from the state
being left while it still shows more than half — as the existing `AnimationEvent`, as the `OnAnimEvent`
output, and as the same-named trigger param, which is how events drive transitions. Sprites became graph
leaves (the `sprite` part's `graph`), so combat sets an `attack` trigger and lands on `hit` for a sprite
goblin and a skinned NPC alike, on the tick it always did; the conventions' clip names are obsolete, and a
sprite fighter from before is upgraded to a graph that plays them (test:
ASpriteGoblinAndASkinnedNpcLandOnHitOnTheSameTickAsBefore) (test: AGameNamesTheSpriteClipsCombatPlays).
Nothing allocates per tick (test: RaisingEventsAllocatesNothingPerTick). SAGE0126, TODO F10. Details:
docs/design/12 "As built (animation events)".

### Stage C: prove it on the action-RPG family, then build your game

| Phase | Work | Exit criterion |
|---|---|---|
| **5** | **Action-RPG gauntlet** in `games/Samples/`, each small, complete, headless-tested and doubling as a `dotnet new` template:<br>• **"Black Mesa lite"** (HL1): a linear chapter with a scripted sequence, hitscan and melee, doors, lifts, a scientist that follows you<br>• **"Vvardenfell lite"** (Morrowind): a small town with topic dialogue, a quest, a shop, skills that rise with use, 1P/3P<br>• **"Zone lite"** (S.T.A.L.K.E.R.): a streamed outdoor area with guns, a grid inventory, a trader, two factions fighting off-screen, and a day/night cycle<br>**Rule: gaps are fixed in the base, never in a sample.** | all three run from the stock host; no genre `if` in base assemblies; each sample's code stays small (target under about 1,500 lines) |
| **6** | **Your game.** Grow `Sandbox` into the Daggerfall-like on `Kits.Rpg`: the vertical slice again, now on the new cameras, UI, weapons, progression, cells and saves | the 2026-09-23 vertical slice plays again, with no RPG code in the base |

### Stage D: widen the base to other genres (2D included)

| Phase | Work | Exit criterion |
|---|---|---|
| **7a** | **2D profile** (§0.5 "2D is first-class"): a spike to choose the 2D physics backend behind the phase-3 facade, then `Sage.Physics2D`, the 2D controller, pixel-perfect ortho, sorting layers and Y-sort, parallax and 9-slice, Tiled import, 2D panning | a Tiled level with a character that runs, jumps through one-way platforms and opens a door wired by I/O, identical at 1× and 4× pixel scale |
| **7b** | **Mouse picking, selection, group navigation**, many-unit performance (RTS and top-down) | drag-select 200 units and move them in formation at 60 Hz |
| **7c** | **Genre gauntlet, part 2**: a third-person melee arena (Lugaru-like), an RTS or battle skirmish (Warband-like), and on the 2D profile a platformer, a top-down 2D action RPG and a card or puzzle game | same rules as phase 5 |

### Stage E: open it up to designers and modders

| Phase | Work | Exit criterion |
|---|---|---|
| **8** | Designer logic polish: AI behaviour trees (§4.3 stage 4), per-entity map keys, problems panel, `sage validate` in templates | a quest with a door, a counter and a custom-BT creature, built with no C# |
| **9** | Full modding (code mods, `.sagemod` packages, namespaced assets, keyed list merge) and save headers (§4.4, §4.5) [F37, F27] | two conflicting example mods (data and code) show the report; saves survive mod changes |
| **10** | Editor host (§4.6) [F28–F30, D7], on the multi-view renderer from 4a | a designer builds a level (place, tune, wire, undo, play, save) without touching JSON |
| **gate** | Scripting decision (§4.3 stage 5), informed by what the phase-5 samples needed | a written decision with evidence, replacing or confirming D3 |

**Why this order.**
- Phases 1–3 make the base general.
- Stage B makes it good at one family, so real games can start.
- Stage D widens it before the editor and full modding, which both *read* the base engine's
  declarations. Building them before the base settles means building them twice.
- Phases 1–3 still bake in what the later work needs (stable ids, metadata, documents, per-plugin
  registrations), so it is additive and not a rewrite.
- The one exception is **4j**. Data mods are cheap on the existing VFS, and this genre lives on them.

**What deliberately waits:** multiplayer (keep the readiness rules), visual scripting, DesktopVK, AOT,
and 2D skeletal animation.

---

## 6. Decisions I need from you

These are the owner's calls, and the plan works with any answer. My recommendation comes first.

0. ~~How far does "almost any game" go?~~ **Decided 2026-09-27: 3D and 2D, both first-class**
   (§0.5), with **action RPGs as the first target family** (§5). Multiplayer stays later. Still open inside it: the 2D physics backend, which the phase-7a
   spike decides (recommended first candidate: Aether.Physics2D).
1. **Scripting (D3).**
   - Recommended: the typed vocabulary first, with a language decision after the phase-5 samples, on evidence.
   - Alternative: commit to Lua now for modders.
2. **Editor technology.**
   - Recommended: a MonoGame host with ImGui (proven in this repo, one stack).
   - Alternative: Avalonia, or a web front end over a headless `SageApp`. Nicer forms, but a second UI
     stack.
3. **Do modders get the editor?**
   - Recommended: yes, as an opt-in release by each game. This changes Shipping packaging.
4. **ECS ownership.**
   - Recommended: wrap Friflo behind Sage types (§3.5).
   - Alternative: accept Friflo as public API and skip the wrapper work.
5. **Licence (D9).** Still CC0, while the docs recommend MIT or Apache. This must be settled before
   anyone else builds on the engine.
6. **Target platforms for the first release.** Windows only, or Windows plus Linux. This affects CI
   and DesktopGL vs DesktopVK timing.
7. **Distribution.** A NuGet feed plus templates (recommended), or source-only ("clone the repo").
8. ~~How are levels built?~~ **Decided 2026-09-28: in our own in-engine editor**, not TrenchBroom
   (the owner doesn't want TrenchBroom). Brushes, placement, overrides and I/O wiring in the editor
   host (§4.6); the `.map` importer stays until the editor replaces it. Tracked as #61.

---

## 7. Doc contradictions to fix in phase 0

- README.md:46 says audio, streaming, pathfinding, factions and quests, and the editor are "not here
  yet", while the table above it says they exist.
- ARCHITECTURE.md's status line and 00-index say "design, nothing built", which is stale.
- ARCHITECTURE §2/§3 describe `Sage.Framework`, which doesn't exist (01 §3.1 admits this). This plan's
  §3.1 replaces it.
- D7 (separate editor host) contradicts what was built (15 §10b). This plan re-adopts D7.
- 13-ui §3 says "Game UI = to be decided", while D8 says decided.
- 01 §3.1 says glTF is "still to build", but SharpGLTF is in use.
- MAKING_A_GAME §3:
  - says "twenty-four" record types (25 exist) and leaves out `placements`;
  - refers to `rec_print`, but the command is `rec_get`.
- 16 §3.4 still mentions an `AIEvents` list, though the event bus exists. Docs 04 and 10 §14 list
  built things as unbuilt.
- F7 is marked done while depenetration is missing (#61).
- TODO.md has a stale header date and its bug section is titled `engine/`.
- 15-editor §10 maps to files that don't exist (`EntityContextMenuUI.cs`, `EditorManager.cs`).

---

## 8. Mapping to existing ids

| This plan | Existing ids |
|---|---|
| §3.4 generator | 09 §3.2, `RecordRef<T>`, R11 |
| §3.2/§3.3 app and plugins | R1 (statics), 01 §5.2, 01 §12 server |
| §4.1 layering | ARCH §2/§3 Framework, ARCH §5 ("belongs in the game's layer") |
| §4.3 logic | F17 (I/O), F21–F23, R16/R17 |
| §4.4 modding | F37, 17-modding, 05 §11 (paks), D3 |
| §4.5 saves | F27, F31, 09 §3.6 |
| §4.6 editor | F28–F30, D7, 15 §3–§4 |
| §4.7 render and UI | D8, 06, 13 |
| §4.8 SDK and CI | R19, readiness §2 ("nothing ever built for another machine") |
