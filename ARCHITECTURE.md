# Sage Engine — Architecture Overview

Status: design, revised 2026-09-22 (third pass: research-backed; see §9), and mostly built since: where a section's "Today" differs from its design, "Today" is what exists. [`docs/REDESIGN.md`](docs/REDESIGN.md) (2026-09-27) supersedes parts of it — the `Sage.Framework` layer (§2, §3) is replaced by the base-engine/kit split in REDESIGN §0.5 and §3.1, D7 (a separate editor host) is re-adopted, and D3 (no scripting) is under review in REDESIGN §4.3.

| Document | Role |
|---|---|
| **This file** | The overview: what Sage is, layers, rules, decisions, strategy |
| [`docs/design/`](docs/design/00-index.md) | How each subsystem works (18 design docs + glossary) |
| [`docs/research/engine-survey.md`](docs/research/engine-survey.md) | How other engines are built and why, with sources |
| [`docs/ENGINE_DESIGN_PLAN.md`](docs/ENGINE_DESIGN_PLAN.md) | The approved plan that produced this revision |
| [`TODO.md`](TODO.md) | Open bugs in today's code + roadmap (R = architecture items, F = features) |
| [`docs/history/code-review-log.md`](docs/history/code-review-log.md) | Archived history of code-review items #1–#39 |

---

## 1. What Sage is

Sage is an **engine layer**. Games are built as **game layers** on top of it, and the engine never contains game-specific code. Target games: Daggerfall-like (built first), then HL1-like, Lugaru-like and Mount & Blade: Warband-like.

It sits deliberately between Source and Unity:

| | Source / GoldSrc | Unity | **Sage** |
|---|---|---|---|
| Who owns the main loop | Engine | Engine | Engine |
| Game code | C++ game DLLs from a forked SDK | C# scripts on GameObjects | C# **game module** assemblies loaded by the host |
| Gameplay framework | Rich, shaped around one game | Almost none | **Optional, genre-generic framework modules** (character, abilities, combat, AI, inventory…) |
| Entity model | Class inheritance + keyvalues + I/O | GameObject + components | **ECS composition** + **prefab records** + **typed, load-checked entity I/O** |
| Networking | Built in from day one | Add-on | **Single-player first, kept multiplayer-ready** by a few cheap rules (§4.9); netcode is Phase 7 |
| Tools | Hammer + FGD, console, cvars | One integrated editor | Integrated ImGui editor generated from code metadata, **plus** console/cvars (no separate dev mode), **plus** external tools where they're better (TrenchBroom, Blender) |
| Content | Search paths (`gameinfo.txt`) | Asset database | **Layered VFS** (engine → framework → game → mods) + **data records merged per field** |

In short: **Unity's composition and editor, Source's framework, console culture and entity I/O, plus Bethesda-style data records without the "rule of one" mod conflicts.**

---

## 2. Layers

```
┌──────────────────────────────────────────────────────────────┐
│ GAME LAYER      game module · game systems/components · data │
├──────────────────────────────────────────────────────────────┤
│ FRAMEWORK       GameRules · Controller/Pawn · Character      │
│ (optional)      Attributes/Effects/Abilities · Combat · AI   │
│                 Inventory · Interaction · (later) Narrative, │
│                 Factions, Economy, Overworld, Navigation     │
├──────────────────────────────────────────────────────────────┤
│ ENGINE  ┌─ Simulation (Core+Simulation, no MonoGame) ─────┐  │
│         │ Core services · World/ECS · Events · Assets/VFS │  │
│         │ Records · Serialization · Physics · Streaming   │  │
│         └─────────────────────────────────────────────────┘  │
│         ┌─ Client (Sage.Client, MonoGame) ────────────────┐  │
│         │ Rendering · Materials/Shaders · Input · Audio   │  │
│         │ Dev UI (ImGui)                                  │  │
│         └─────────────────────────────────────────────────┘  │
├──────────────────────────────────────────────────────────────┤
│ PLATFORM        MonoGame (DesktopGL) · SDL · .NET 8 runtime  │
├──────────────────────────────────────────────────────────────┤
│ HOSTS / TOOLS   Game host · Editor host · Source generator   │
└──────────────────────────────────────────────────────────────┘
      Libraries: Friflo.Engine.ECS · BepuPhysics v2 · SharpGLTF · ImGui.NET
```

Gregory's reference stack (survey §2.1) maps onto this directly. "Gameplay foundations" is our framework layer.

### Dependency rules (enforced by csproj references; details in [01 §3.1](docs/design/01-host-and-modules.md))
1. **Down only:** game → kit → framework → engine. The engine never references a kit, the framework or game types (a kit: SAGE0025).
2. **The simulation never references MonoGame or client code.** `Sage.Core`, `Sage.Simulation`, `Sage.Physics3D`, `Sage.Gameplay` (the framework layer) and `Sage.UI` (the game UI's widgets, #95) use `System.Numerics` (D2), and the build says so (SAGE0024). Rendering *reads* simulation state through Extract (§4.6); the simulation never calls rendering.
3. **Presentation → simulation only through `PlayerCommand` or command queues.** UI and camera code never write gameplay components directly ([04 §3.1](docs/design/04-events-and-messaging.md)).
4. **Runtime never references tools or the editor.**

---

## 3. Solution layout (target)

```
sage-engine/
  src/
    Sage.Core/              kernel: cvars/console, log, diagnostics, VFS, records, plugins,
                            declarations                               (no ECS, no MonoGame)
    Sage.Simulation/        engine + app, World/ECS, events, prefabs, saves, levels,
                            streaming, entity I/O, presentation data   (no MonoGame)
    Sage.Physics3D/         Bepu physics, character controller          (no MonoGame)
    Sage.Gameplay/          optional gameplay modules, simulation side  (no MonoGame)
    Sage.UI/                retained game-UI widgets, layout, focus; on Simulation, headless (no MonoGame; #95)
    Sage.Kits.Rpg/          the action-RPG kit: spellmaker, readied spell, RPG screens (no MonoGame;
    Sage.Kits.Rpg.Client/   and its client half)   not part of the base: a game names it in game.json (#27)
    Sage.Client/            rendering, materials/shaders, input, audio, dev UI     (MonoGame)
    Sage.Framework.Client/  camera rigs, cue playback, HUD helpers
    Sage.Editor/            editor host + panels
    Sage.Host/              game host exe (boot + main loop)
    Sage.Generators/        Roslyn source generator: declarations register themselves (built, #16); metadata, schemas next
  engine_content/           shaders (.fx → .mgfxo), placeholders, engine records
  tests/  Sage.Tests/ (headless worlds) · Sage.Testing/ (HeadlessApp, shared with games) · Sage.Benchmarks/
  games/  Sandbox/          the Daggerfall-like vertical slice (TODO milestone)
  docs/
```

A game: `games/MyGame/` with `MyGame.csproj` (an `IGameModule`), `game.json` and `content/` (assets, with records in `content/data/`). See [01 §3.3](docs/design/01-host-and-modules.md) and `games/Sandbox`.

Inside each assembly, features are **logical `IModule`s** (Renderer, Physics, Audio, Abilities…) with declared dependencies and `Init`/`Start`/`Shutdown`, like Bevy plugins or Unreal modules. There are csproj boundaries only between layers, not per feature ([01 §3.1](docs/design/01-host-and-modules.md)).

---

## 4. Core concepts (summaries; each links to its design doc)

### 4.1 Host, modules, loop, build configurations — [01](docs/design/01-host-and-modules.md)
- The engine owns `Main`. The boot order is logging → cvars → VFS → modules `Init` (register only) → records → graphics → modules `Start` → world → loop.
- **The fixed tick is 60 Hz** (`sim_tickrate`) with a clamped accumulator and **render interpolation** (Fiedler).
- **No separate dev mode.** Build configurations (**Debug / Development / Shipping**) decide what debug code *exists*; `Shipping` strips it. At runtime the **console + cvars** switch features individually, and the `developer` cvar only sets their defaults (`+developer 1` at launch). Shipping keeps a restricted console (`con_enable`): normal commands, cheats behind `sv_cheats`, no `DevOnly` commands.

### 4.2 Core services and logging — [02](docs/design/02-core-services-and-logging.md)
- **Logging:**
  - `Log.Trace/Debug/Info/Warn/Error/Fatal` with **categories** and per-category levels (`log_level assets debug`);
  - zero-cost when disabled (conditional compilation + an interpolated string handler);
  - sinks: file, stdout, in-game console, editor panel, and a ring buffer.
- **Asserts** (`Dev`/`Ensure`/`Check`) and **crash reports** (exception + log tail + cvars + mods + build).
- **Other services:**
  - cvars/console commands with flags;
  - `TickTime`/`FrameTime`;
  - profiler scopes;
  - a job system over the thread pool;
  - **zero allocations per frame** in steady state.

### 4.3 World and ECS — [03](docs/design/03-world-and-ecs.md)
- A `World` per simulation: no static singletons. Components are **plain data**, and systems run in **schedules**:
  - **Fixed:** `Commands → PrePhysics → Physics → PostPhysics → Gameplay → AI → Animation → EntityIO → Late`;
  - **Frame:** `FrameUpdate → Extract → Render → Overlay`.
- **`EntityRef`** (stale-detecting) and **`PersistentId`** (maps/saves).
- **Command buffers** flushed at phase ends.
- **Transform hierarchy:** `Transform` → `GlobalTransform` (previous/current poses, for interpolation), all relative to the world's **origin sector** (`Origin`, R6 as built 2026-09-24). A rebase shifts every transform, both poses, the physics world and the camera at once, rather than each entity carrying a `SectorCoord`.
- **Storage:** **Friflo.Engine.ECS** behind a thin `World`, adopted after it passed requirements E1–E9 in a spike (D4, 03 §3.1). The vocabulary is Sage's (issue #25, 03 §3.1a): `Entity`, `IComponent`/`ITag`, `Tags`, `Query<…>` and `EntityCommands` are zero-cost structs over Friflo's, no public signature names Friflo, and a game that does is a build error (SAGE0050); hot paths iterate chunks.

### 4.4 Events and messaging — [04](docs/design/04-events-and-messaging.md)
Four mechanisms, each with a distinct job:
1. **structural notifications**;
2. **game events** (typed, queued per schedule, per-reader cursors, no re-entrant callbacks);
3. **entity I/O** (Source-style outputs → inputs, resolved and type-checked at map load);
4. **engine signals** (rare C# events between ticks).

### 4.5 Assets, VFS and data records — [05](docs/design/05-assets-and-vfs.md)
- **VFS with mounts** and a separate writable `user://`. **Asset identity = virtual path**, so mods override by shadowing.
- **Assets:** `AssetPath` (unloaded, in components) vs `AssetRef<T>` (loaded). **Scopes own lifetimes**; LRU cache; async decode + budgeted GPU upload; placeholders instead of nulls; hot reload.
- **Formats:** runtime loaders for PNG / glTF / WAV, with **no MGCB** (this also fixes TODO #9). **As built 2026-09-24 (R12):** all three, plus a generated bitmap font; `dotnet-mgfxc` for `.fx` is the only build-time content step left.
- **One data-record pipeline** for every definition (items, spells, materials, input maps, prefabs…): JSON, namespaced ids, `base` inheritance, **per-field patch merge in load order**, generated validation.

### 4.6 Rendering — [06](docs/design/06-rendering.md)
- **Extract → pooled `RenderSnapshot` → fixed pass list** (opaque, alpha-tested, sky, transparent, debug, overlay).
- **Interpolated, camera-relative** transforms; 64-bit sort keys; frustum culling.
- **Batching:** CPU-batched **billboard sprites with 8-direction selection** (instancing later, behind a cvar); forward lighting (sun + hemispheric ambient + 4 point lights) + fog.
- **Other:** a retro render-scale option, and `DebugDraw`.
- **Target:** MonoGame DesktopGL (SM3 via MojoShader). The snapshot boundary keeps a later backend switch (DesktopVK) contained.

### 4.7 Materials and shaders — [07](docs/design/07-materials-and-shaders.md)
- `.fx` compiled with **`dotnet-mgfxc`** by an MSBuild target.
- **Techniques are the variants** (no permutation matrix). A `common.fxh` contract.
- **Materials are records.** Every parameter needs a value, because GL ignores `.fx` defaults.
- Three-tier cached parameter binding; shader hot reload with a magenta fallback.

### 4.8 Input — [08](docs/design/08-input.md)
- Today's listeners become the device layer. **Actions** come from input-map records; the player's rebinds are saved as record patches.
- **Context stack** (Editor > Console > UI > Gameplay) with consumption, so ImGui and gameplay don't both react.
- **Per-tick `PlayerCommand`**, with frame-rate look and latched taps.

### 4.9 Multiplayer (later): readiness rules
Multiplayer is Phase 7, not built now. These rules cost little today and keep it from becoming a rewrite (survey §5):
1. Fixed-tick simulation, separate from rendering (4.1).
2. Input → `PlayerCommand`; gameplay never reads devices (4.8).
3. Components are plain data (4.3).
4. The simulation has no MonoGame dependency, so it can run headless (§2).
5. Rules/authority logic lives in `GameRules`, separate from presentation ([16](docs/design/16-gameplay-framework.md)).
6. `EntityRef`, never object pointers (4.3).
7. The component metadata can later gain a `[Replicated]` flag (4.10).
8. No gameplay logic in rendering or UI (§2 rule 3).

When the phase starts: a server-authoritative **snapshot** model (not lockstep or rollback, because Bepu is only locally deterministic and can't snapshot), prediction of the local player's character controller only, lag compensation for hitscan, relevancy/interest management, and LiteNetLib as the transport. The research is in survey §5.

### 4.10 Serialization and saves — [09](docs/design/09-serialization-and-saves.md)
- **One attribute declaration** (`[Component]`, `[Saved(tag)]`, `[Property]`, `[Transient]`) → a **source generator** emits JSON/binary serializers, inspector metadata, I/O tables and record validators. This avoids Source's four separate declarations.
- **Maps** are per-sector JSON files.
- **Saves:**
  - every *visited* sector is saved in full, plus tombstones and saved resources;
  - tagged binary with upgraders;
  - temp-file + rename writes;
  - **never behaviour/script state** (the Skyrim Papyrus lesson).
- **As built (F27, 2026-09-23):** JSON rather than tagged binary and reflection rather than the
  generator, both deliberate and reversible; every persistent entity written in full (no maps yet, so
  no visited-sector rule and no tombstones); opt-**out** with `[Transient]` until the generator can
  warn about a field that decided neither way; `[SavedResource]` world resources with an `AfterLoad`
  hook, which is how the spellmaker's drafts become `ability` records again. Entity references,
  attribute values and gameplay tags are written by **identity or name**, never by the index they
  happen to occupy this run. Details and the four deviations: [09](docs/design/09-serialization-and-saves.md).

### 4.11 Other subsystems (short docs, expanded when their phase starts)
| Subsystem | Doc | One-line summary |
|---|---|---|
| Physics | [10](docs/design/10-physics.md) | BepuPhysics v2 per world; components hold handles; triggers → events; **own kinematic character controller** |
| Audio | [11](docs/design/11-audio.md) | Event-driven presentation; `sound` records; buses; MonoGame backend behind an interface |
| Animation | [12](docs/design/12-animation.md) | v1 sprite animation with sim-owned time and frame events ("hit" frames); skeletal glTF later |
| UI | [13](docs/design/13-ui.md) | ImGui for dev/editor only; game UI library decision D8. **A screen’s *contents* are simulation data** (`Panel`/`PanelRow`, F38’s engine half): what to show and whether each row can be used, with the reason taken from the rule that would refuse it — so a screen is testable headlessly and the library choice stays open. **Retained widgets, layout and focus** are `Sage.UI` (#95), headless too: a test lays out a grid, moves focus with the D-pad and hit-tests the pointer |
| World streaming | [14](docs/design/14-world-streaming.md) | 1024 m sectors, origin rebasing, streaming rings (Daggerfall Unity model), separate interior spaces, dormancy |
| Editor | [15](docs/design/15-editor.md) | Separate host; documents + command log + undo; generated inspector; play-in-editor |
| Gameplay framework | [16](docs/design/16-gameplay-framework.md) | `GameRules`, Controller/Pawn/`PawnIntent`, GAS-like attributes/effects/abilities, HL1-style AI schedules |
| Modding | [17](docs/design/17-modding.md) | `mod.json`, load order, additive per-field patches, conflict report, trusted C# mods only |

---

## 5. Framework layer

Optional, genre-generic modules; games use, extend or skip them. Full table and v1 scope in [16](docs/design/16-gameplay-framework.md). The rule: **if a feature only makes sense for one game, it belongs in that game's layer.**

---

## 6. How the target games map onto the layers

| | Engine | Framework | Game layer |
|---|---|---|---|
| **Daggerfall-like** | billboards, terrain, streaming + large-world coordinates, records, saves | Character, Attributes/Effects/Abilities (spellmaker), Combat, Inventory, AI, (later) Narrative, Factions | world generator, class/race rules, spellmaker UI, creatures and content. Co-op (later) |
| **HL1-like** | brush map import, lightmaps, physics, entity I/O | Character (GoldSrc movement profile), Combat (hitscan), AI (schedules/squads), Interaction | weapons, NPC types, scripted sequences, maps. Multiplayer (later) |
| **Lugaru-like** | skeletal animation, ragdolls, terrain | Character (3rd person), Combat (reversals, knockdowns) | move set, characters, combat tuning |
| **Warband-like** | skeletal animation, crowds (animation/AI LOD), physics | Character (+mounts), Combat (directional), AI (formations), Economy, Overworld, Factions | campaign rules, troop trees, sieges, life paths (merchant, farmer, hunter as data). Multiplayer battles (later) |

If a feature shows up in two or more rows, it belongs in the framework or engine.

---

## 7. From today's code to this structure

**Today** (after step 6):
- **Solution:** `Sage.sln` with `src/Sage.Core`, `src/Sage.Simulation`, `src/Sage.Physics3D`, `src/Sage.Gameplay` (no MonoGame; one assembly, `Sage.Engine`, until issue #24), `src/Sage.Client`, `src/Sage.Editor`, `src/Sage.Host` (exe), `src/Sage.Cli` (`sage validate`, headless), `games/Sandbox` and `tests/Sage.Tests`.
- **By step:**
  - step 2: logging, the console and build configurations;
  - step 3: `Engine` + `World` over Friflo.Engine.ECS, with no singletons;
  - step 4: a fixed-tick loop with interpolated rendering, schedules/phases and the profiler;
  - step 5: modules, `game.json`, the VFS and records;
  - step 6: Extract → `RenderSnapshot` → material-driven rendering with our own shaders, and input actions → `PlayerCommand`;
  - **the vertical slice starts:** F1 billboard sprites (8 direction groups, sheet records, sprite animation at the tick rate), F13 heightmap terrain (one 1024 m sector, game-provided generator, chunk meshes) F6 physics (BepuPhysics per world, colliders behind handles, queries, triggers, terrain collision) F7 the kinematic character controller with a first-person camera, the first `GameRules` and AI (schedules, perception, a creature that chases and attacks) and F18 attributes/tags/effects — dogfooded by a Sandbox where a player walks the hills, a creature hunts them down, and losing all your health respawns you.
- **Still to replace:** each design doc has a "Mapping from today's code" table. (Models were MGCB `.xnb` files until R12; they are `.glb` read at runtime now.) The main moves:

| Today | Becomes |
|---|---|
| `Game1`, `Program` | `Sage.Host` boot + fixed-tick loop ([01](docs/design/01-host-and-modules.md)); test scene → `games/Sandbox` (**done, step 5**) |
| ~~`EntityContext`, `Entity`, `EntityContextListener`, `ArchetypeView`~~ (step 3) | `World`, `EntityRef` (Friflo `Entity`), typed queries, structural notifications ([03](docs/design/03-world-and-ecs.md), [04](docs/design/04-events-and-messaging.md)) |
| `Transform` struct + `TransformMath` (step 3; was `ComponentTransform`) | + `GlobalTransform` + transform propagation (step 4), **origin-relative with `Origin` + `world.Rebase` (R6, done 2026-09-24)**; math in `TransformMath`; billboarding moves to the renderer |
| `ModelRenderer` (struct, step 3), `ModelRendererSystem` | `MeshRenderer` (with `AssetPath`) + `MeshExtract` + material-based passes ([06](docs/design/06-rendering.md), [07](docs/design/07-materials-and-shaders.md)). **Done, step 6** |
| `InputSystem`, listeners | `InputDevices` + actions/contexts + `PlayerCommand` ([08](docs/design/08-input.md)). **Done, step 6** |
| `DevCamera` | Editor camera rig on actions ([15](docs/design/15-editor.md)) |
| `EditorUI`, `EntityContextMenuUI` | Editor menu, outliner, generated inspector ([15](docs/design/15-editor.md)) |
| `UtilAssets`, `Content.mgcb` | `AssetServer` + VFS + runtime loaders ([05](docs/design/05-assets-and-vfs.md)). **Step 5:** `UtilAssets` removed; VFS done. **R12 (2026-09-24):** `Content.mgcb` deleted — models (`.glb`), textures, sounds and the font all load at runtime through `ContentService`. Scopes, ref-counting and async loading are what the `AssetServer` still adds |
| `Console.WriteLine` | `Log` with categories ([02](docs/design/02-core-services-and-logging.md)) |

**Suggested order** (each step keeps the engine running):
1. ~~**Solution split**~~ **Done 2026-09-22.** `Sage.Engine` / `Sage.Client` / `Sage.Editor` / `Sage.Host` + `tests/Sage.Tests` (15 headless tests); `ComponentTransform` on `System.Numerics`; `IEngineSystem.update(float)` instead of MonoGame's `GameTime`. Two differences from the plan: **`games/Sandbox` is deferred to step 4**, because it needs `IGameModule` to plug in (the test scene stays in `Game1` until then); and **`Sage.Host` references `Sage.Editor`** because today's exe is still game + editor in one.
2. ~~**Logging + build configurations + console/`developer`**~~ **Done 2026-09-22** (TODO R10). Debug/Development/Shipping configurations with `SAGE_DEV`; `Log` with categories, zero-cost disabled calls, file/stdout/ring sinks, duplicate collapse and rate limiting; `Assert.Dev/Ensure/Check`; crash reports; cvars, console commands, `config.cfg` and `+launch` args; the ImGui console (`~`) and `stat fps`/`stat mem`; 36 tests. Deferred on purpose: profiler scopes and `TickTime` (step 4, they need the tick phases) and the job system (step 5, with async asset loading).
3. ~~**`World` + `Engine` objects replace the singletons**~~ **Done 2026-09-22** (R1, R5; D4 decided). Friflo.Engine.ECS 3.6.0 passed all nine requirements in a spike (03 §3.1) and is adopted behind a thin `World` (struct components, stale-detecting handles, typed queries, command buffer, notifications with the old ordering guarantees, `PersistentId`, resources, hierarchy). `Engine` owns cvars and worlds; no engine singletons remain (`Log`/`CrashReporter` are static by design). Fixed #15, #33, #34, #36, #38. 44 tests.
4. ~~**Fixed tick + schedules + phases**~~ **Done 2026-09-22** (R2, R4). `FixedStepClock` (Fiedler accumulator, clamped frame time, time scale), `sim_tickrate` 60 Hz, render interpolation via `GlobalTransform` (previous/current poses) + transform propagation through the hierarchy, `ISystem` in Fixed/Frame phases with `before`/`after` ordering, run conditions, pause, per-phase command flushing, profiler scopes + `stat frame` + `sys_list`/`sys_toggle`. The loop allocates nothing in steady state (tested). Deferred: `SectorCoord`/origin rebasing (R6), access declarations (event bus), `host_maxfps`. 56 tests.
5. ~~**Modules + `game.json` + VFS + records**~~ **Done 2026-09-22** (R8, R11 v1). `IModule` (`Init` → `Start` → `OnWorldCreated` → `Shutdown`) with dependency sort and dependency-checked services; `game.json` (game id = record namespace, `{config}` in the assembly path, module disabling); the game assembly loads before the first `World`. VFS with priority folder mounts, shadowing and case-insensitive lookup; `RecordStore` with namespaced ids, per-field patch merge (`field+`/`field-`), `disabled`, `base` + `abstract`, validation (unknown fields, types, references), in-place hot reload; `vfs_*`, `rec_*`, `modules` commands. **`games/Sandbox` is now a real game module** (it was planned for step 4 in step 1's note): its scene is `spawn` records, and billboard facing is its own system. Deviations, all documented in 01 §4/§5.1 and 05 §3.6: `OnWorldCreated` is on every module, not only the game; `+launch` commands run after the main world exists; records use `System.Text.Json` reflection until the generator (09); models still come from MGCB `.xnb` files, now read through the VFS (`ContentService`), until R12. 89 tests.
6. ~~**Extract + snapshot + materials** (R9); **input actions → `PlayerCommand`** (R3)~~ **Done 2026-09-22.**
   - **Rendering:** `MeshRenderer` (Sage.Simulation) → `CameraExtract`/`MeshExtract` → a pooled `RenderSnapshot` (camera-relative, interpolated, frustum-culled, sort keys) → the `Renderer`'s fixed passes.
   - **Materials and shaders:** material records (`sage:lit_default`, `sage:error`, `base` inheritance, hot reload) drawn with our own `lit.fx`/`error.fx`. Shaders are compiled by `dotnet-mgfxc` from `engine_content/` at build time, and `BasicEffect` is gone.
   - **Input:** `InputDevices` (+ gamepad) → `InputActions` (input_map records, Editor/Console/UI/Gameplay contexts with consumption) → `CommandLatch` → a `PlayerCommand` per tick in the world's `PlayerInput`. The player's character jumps on Jump, and `Menu` replaces the Escape check.
   - **Fixed:** #25, #37, #39, and a new #40 (listener edges were always false).
   - **Deviations, all written up in 06 §3.11, 07 §3.6 and 08 §3.6:**
     - no `Camera` component yet (`ActiveCamera` is the view);
     - material validation happens when a material is built, not at load;
     - the shader build target lives in the host;
     - the editor camera still reads devices.
   - **Deferred:** sprites (F1), point lights, `DebugDraw`, shader hot reload, `bind`/`user://input.json`.
   - 100 tests.
7. **The vertical slice**, alternating features with the infrastructure they prove they need (the "Suggested first milestone" in TODO.md). **Started 2026-09-22 with F1 (billboard sprites), F13 (terrain), F6 (physics), F7 (the character controller with a first-person camera) and the first gameplay: `GameRules` plus HL1-style AI (F22).** The engine is walkable and something chases you.

A consistency pass over those nineteen steps (2026-09-22, review #43-#52) then settled the conventions they had each grown their own version of: one facing axis and one set of angle helpers (`SageMath`), collider anchors stated explicitly, physics layers by name, and both controllers writing pawn intent in the same phase. F20 then made the fight real: one damage pipeline (resistance attributes → an effect on health → combat events), `attack` records, and a single melee system both the player and the AI drive by pressing the same button, with swings landing on the animation frame that shows them. F19 then added items: an inventory of record ids and counts, pickups you walk up to and take, and equipment that changes what you swing by handing the weapon's own `attack` record to the wielder. F21 then made magic real: an `ability` is a cost, a cooldown, a way of choosing targets and a list of effects, so a fireball reaches the world through the same damage pipeline a sword does and `fire_resist` means something without the spell knowing it exists — and it travels, as an ordinary entity whose looks are a prefab. F27 then closed it: a save writes every persistent entity as JSON and a load rebuilds each one by spawning its prefab and laying the saved state over the top, with entity references, attribute values and tags written by *identity* rather than by the index they happen to occupy this run. The slice is walkable, fightable, lootable, castable and resumable.

Two features then pushed on what the slice had built rather than adding to it, and both paid off in
the same way — by making something the engine could already do reachable from somewhere new. The
**spellmaker** composes effect records into an `ability` record *at run time*, which needed records
that were not read from a file and a save that holds the player's choices rather than the record they
produced; the cast system cannot tell the difference. **AI casting** then asked the cast system a
question it had only ever answered: the gates moved out into `AbilityRules` so that deciding to cast
and being allowed to cast cannot drift apart, and a creature throws a spell through the same
`world.Cast` a button does. **The first milestone is met** — what it cost and what it taught is in
[`docs/history/vertical-slice-2026-09-23.md`](docs/history/vertical-slice-2026-09-23.md).

**Is it production ready?** No — [`docs/history/readiness-2026-09-24.md`](docs/history/readiness-2026-09-24.md) says what is strong, what is
missing before a game ships, what has never been measured (scale, frame time, any second machine), and
the three risks that get more expensive with waiting: R6 large-world coordinates, the source generator
two subsystems already assume, and multiplayer readiness rules no network layer has ever tested.

**What the slice taught us about the engine** is written up in
[`docs/history/engine-review-2026-09-23.md`](docs/history/engine-review-2026-09-23.md): records, the
controller → pawn-intent split, effects as the only way attributes change and a simulation with no
MonoGame dependency are all paying for themselves, while the **glue between systems** is where the
debt is — four hand-rolled event queues instead of the bus 04 designed, three hand-rolled deferral
lists, a `GameplayModule` that has outgrown being one module, no prefabs, and phase guarantees that
live in prose rather than in an assertion. That became R13–R16 in TODO.md, with F31 (prefabs) and
F27 (saves) promoted out of Phase 6. **R13, F31, R14, R15 and R16 are done** (2026-09-23): one event bus with
per-reader cursors, and the four queues retired into it — though building it showed that two of them
were state rather than events, which the review now records; and prefabs, so placing a thing is one
call against a record instead of eight calls in a particular order.

**Step 8: the world grows (2026-09-24 onwards).** The [readiness review](docs/history/readiness-2026-09-24.md)
put large-world coordinates and streaming (R6, F14) first, ahead of audio and everything else, on one
argument: they are the only remaining items that get *more expensive with every feature added*, because
each new feature assumes world-space floats. The second milestone in TODO.md is the target — walk out
of the starting sector and keep going — and the order after it is R18 (a scale test with numbers), F4
audio, F23 pathfinding, F24 factions, R12.

**R6, F14, R18 and F4 are done** (2026-09-24). The world is unbounded: positions are stored relative to
a world `Origin` that follows the player in whole sectors, sectors stream in rings with hysteresis, and
a rebase is an event every subsystem that holds a position subscribes to — physics, the renderer, debug
shapes, the audio mixer, the editor camera. Three of those five were found by the second pass rather
than written first, which is the argument for having one. Then [a scale test with
numbers](docs/history/scale-2026-09-24.md) measured a full world instead of asserting about one: 2,000
entities and 100 thinking creatures cost **40 bytes a tick**, and the two allocations it found had been
invisible to every correctness test — boxing in the phase-contract checks (3.2 KB per character per
tick) and `Enum.HasFlag` in the AI's schedule chooser. Then F4 gave the slice a voice: every audio
*decision* is an engine-side `AudioMixer` with no MonoGame in it, the noise is a client backend that
decides nothing, and a mixer belongs to a world because a voice's position is in that world's origin
space. Its own second pass was the most productive yet — an event that described a destroyed entity, a
sound record with no code path, and one cue list raised at two different moments, none of which any
passing test could see. The engine is now walkable, fightable, lootable, castable, resumable, unbounded
and audible: **187 console commands, 60 record types, 2244 headless tests.** <!-- counts -->

F23 then taught the same lesson one layer up: a creature that can *plan* a way round a wall still needs
to **remember what it is chasing**, because walking round something means looking away from it, and sight
is a cone. Pathfinding without memory is decoration.

Step 8 finished with F24 (factions, reputation, dialogue, quests), F39/F40 (particles and weather) and
R12 — MonoGame 3.8.5.1, runtime loaders, and the end of MGCB.

**Step 9 gave the engine the three things a Daggerfall-like still lacked**, and each one arrived from a
direction the plan had not expected. **Levels** (F16) came *before* the editor rather than after it,
because TrenchBroom already edits brushes and reading what it writes took a day; a brush is planes, so
every solid is convex by construction and every one becomes a hull rather than a triangle soup.
**Entity I/O** (F17) made those levels do something — an output wired in map data to an input on another
entity, checked when the level loads — and with it doors, lifts and trigger volumes. **The editor**
(F28) then had something to edit that nothing else could: not brushes, not what a thing *is*, but where
things stand, so its document is a `placements` record the engine owns.

Two smaller things changed how the engine is worked on. **R19** put a checker between the documentation
and the code, because three features in a row ended with the code right and its own words wrong. And
[`games/Hello`](games/Hello) is the smallest game this engine runs — four files, in the solution so it
cannot rot — which found an engine trap on its first run that the Sandbox had hidden for months.

The handoff at [`docs/history/handoff-2026-09-29.md`](docs/history/handoff-2026-09-29.md) says where to
start, and [`docs/REDESIGN.md`](docs/REDESIGN.md) where the engine is going; [`docs/MAKING_A_GAME.md`](docs/MAKING_A_GAME.md) is for using the engine rather than building it.

**Guarding against over-architecting.** Hobby engines usually die from years of infrastructure with nothing playable. After step 4, **alternate**: build a piece of the Sandbox slice, then the infrastructure it proved necessary. The design docs are a map, not a checklist to finish first. Every doc's "v1 scope" is the minimum for the slice. Engine or framework code is extracted **on second use** ("write games, not engines", survey §3.8).

**Game order:** Daggerfall-like (billboards defer skeletal animation, the single biggest system) → HL1-like (brush maps, entity I/O, movement feel) → skeletal animation → Lugaru-like → Warband-like.

---

## 8. Decisions

| # | Decision | Status / choice |
|---|---|---|
| D1 | Multiplayer | **Later (Phase 7), not now.** Follow the readiness rules (§4.9) |
| D2 | Math types in simulation | **`System.Numerics`** in all simulation code; MonoGame types only in `Sage.Client`. Bepu-native, SIMD, keeps MonoGame out of the simulation, enables headless tests and a future server |
| D3 | Game code and modding | **C# game modules; data mods + trusted C# mods** (no sandboxed scripting). Code mods are never auto-downloaded ([17](docs/design/17-modding.md)) |
| D4 | ECS storage | **Decided (step 3): Friflo.Engine.ECS 3.6.0** behind a thin `World`. It passed requirements E1–E9 in a spike ([03 §3.1](docs/design/03-world-and-ecs.md)); Arch was not needed. Queries were Friflo's own types; since issue #25 they, `Entity`, `IComponent`/`ITag`, `Tags` and `EntityCommands` are Sage's zero-cost structs over Friflo's (03 §3.1a). Hot paths iterate chunks (0 allocations) |
| D5 | Physics | **BepuPhysics v2** + **own kinematic character controller** |
| D6 | Level editing | Own editor for terrain/props/entities + **TrenchBroom `.map` import** for brush interiors. **As built (2026-09-24, F16): the import came first** — brushes, hulls, meshes, prefab entities and FGD export, all without the editor, which is the point: a good editor already exists for brushes |
| D7 | Editor form | **Separate editor host** loading the same game module, with play-in-editor |
| D8 | Game UI | **Decided 2026-09-23: our own, on `UiDraw`.** ImGui stays dev/editor only. The engine already produced a screen’s *contents* as data (`Panel`), so what a library would have added was a list, a selection and a box — about 300 lines, against a dependency with its own fonts, stylesheets or external layout editor. Gum and Myra stay reasonable answers if screens outgrow lists ([13](docs/design/13-ui.md)). When they did (grids, gamepad focus; Phase 4c, #95) the answer stayed our own: retained widgets in `Sage.UI`, drawn by the client (#97) |
| D9 | Engine licence | **MIT** (`LICENSE`), decided 2026-09-27, replacing CC0: short and permissive, the same as MonoGame's other dependencies and Friflo, and a normal licence for code others build commercial games on (CC0 is unusual for code and grants no patent licence). Apache-2.0 was the alternative, for its explicit patent grant. Dependencies keep their own licences, listed in `THIRD_PARTY_NOTICES.md` — including three LGPL-3.0 assemblies that Friflo.Engine.ECS pulls in |
| D10 | Asset formats and pipeline | Runtime **PNG / glTF 2.0 (SharpGLTF) / WAV**; shaders via **`dotnet-mgfxc`**; MGCB dropped. **As built (2026-09-24, R12): true.** Models read from `.glb` by `GltfLoader`, textures and sounds from streams, the HUD font from a generated glyph atlas (13 §3); `Content.mgcb`, the builder task, the `dotnet-mgcb*` tools and every `.xnb` path are gone. `dotnet-mgfxc` remains, for `.fx` only. **Since #116** SharpGLTF is referenced by `Sage.Simulation` (it is plain .NET, so SAGE0024 allows it): skins and clips are read there by `GltfAnimationReader`, headlessly (12 "As built (skeletons and sampling)"), and the client's `GltfLoader` reads meshes through the same reference |
| D11 | Model format for animation | **glTF 2.0** over FBX |
| D12 | Which game drives development | **Daggerfall-like** (confirmed) |
| D13 | MonoGame version/backend | **Done 2026-09-24 (R12): 3.8.5.1 on DesktopGL**, `GraphicsProfile.HiDef`. Re-evaluate DesktopVK (new in 3.8.5, intended to replace DesktopGL over the next few years) once it has matured |

---

## 9. Revision history

### 9.1 Fourth pass — the slice, built (2026-09-23)
Not a design pass: the first vertical slice was **built**, and this records where the plan met the
code. Full write-up in [`docs/history/vertical-slice-2026-09-23.md`](docs/history/vertical-slice-2026-09-23.md);
the mid-slice architecture review that produced R13–R16 is in
[`docs/history/engine-review-2026-09-23.md`](docs/history/engine-review-2026-09-23.md).

| Area | What the plan said | What building it showed |
|---|---|---|
| Events | One bus, four mechanisms | Right, but **two of the four "queues" it was meant to replace were state, not events** — "what is in reach right now" is asked, not read once. A bus is not the fix for every shared container |
| Serialization | `[Saved]` opt-in, generated, tagged binary | Inverted to **`[Transient]` opt-out** until the generator exists, because opt-in without a compile-time warning silently loses new fields. JSON and reflection first; the format change is what upgraders are for |
| Saves | Entities, sectors, tombstones | Entities were not enough: a world's own state (a composed spellbook) needed **`[SavedResource]`**, and what it saves are the player's *choices*, with the records derived from them on load |
| Records | Loaded from files | A record can also be **made at run time** (`AddRuntime`), which a content hot reload must not delete. The data behind it lives in the save, so the store is a cache for those |
| Gameplay rules | Systems own their rules | Twice now the rule had to be **asked as well as applied** — `AbilityPayload` (what a spell does, at a point) and `AbilityRules` (whether a cast is allowed). Extract on second use, and the second use arrived both times within a day |
| Modules | One gameplay module | Outgrew itself at nine record types and nine systems; split per feature (R15). The cost is two registration points for a new system, which is the trade |
| Phase guarantees | Prose in the docs | Prose was already wrong (creatures acted a tick late for a week). **`world.Contracts.FinalAfter<T>`** asserts it in dev |

### 9.2 Third pass — research-backed (2026-09-22)
Based on three research passes (classic engines, modern engines, cross-cutting patterns; [survey](docs/research/engine-survey.md)) and your decisions (multiplayer later, Daggerfall-like first, data + trusted C# modding).

| Area | Before | Now | Why / source |
|---|---|---|---|
| Networking | Built in from day one; single-player as a listen server | **Later (Phase 7)**, plus 8 readiness rules | Your decision; the rules keep it cheap to add (Quake/Source/Unreal lessons, survey §5) |
| Large worlds | Sector + local; "floating origin can't work" | Sector + local in simulation + **origin rebasing** around the player + camera-relative rendering; streaming rings; interiors as separate spaces | Daggerfall Unity model, kept multiplayer-compatible (survey §3.3) |
| Module system | Assembly per subsystem | **Two levels:** csproj per layer, logical `IModule`s inside, with `Init` (register) / `Start` (use) | UE modules/Bevy plugins without 20 projects; the boot order needs schemas before records |
| Heavy subsystems | Unspecified | **Subsystems own their data, components hold handles** (hybrid model) | Godot servers, UE Actors+Mass, Unity GO+Entities (survey §2) |
| Sim → render | "Render reads an interpolated snapshot" | Explicit **Extract phase → pooled `RenderSnapshot`**; interpolated poses (not matrices); camera-relative | Bevy/UE/Destiny; matrix lerp distorts rotations |
| Dev tooling | Console + overlays | **Build configurations + console/cvars (no separate dev mode) + a full logging design** (categories, zero-cost, sinks, crash reports, asserts) | Your request; UE log categories, Source spew/`developer` |
| Events | "Event bus" | **Four mechanisms** with rules; per-reader cursors; typed, load-resolved I/O | Bevy pitfalls; Source I/O's untyped strings; O3DE EBus debugging |
| Data | JSON definitions | **One record pipeline for everything**, per-field patch merge, `base` inheritance | Bethesda "rule of one" and Warband whole-module lessons; Dungeon Siege templates |
| Shaders | MGCB for shaders; instancing in v1 | **`dotnet-mgfxc`**, techniques as variants; **CPU-batched sprites in v1**, instancing later | MonoGame docs: GL instancing needs GL 3.2+ and has bug reports; no default params on GL |
| MonoGame version | 3.8.2, MGCB in the build | **3.8.5.1, `GraphicsProfile.HiDef`, no MGCB** (done 2026-09-24, R12); DesktopVK re-evaluated later (D13) | Survey §3.7; MGCB could not load a file a player or a mod added |
| Gameplay framework | Module list | `GameRules`, Controller/Pawn/`PawnIntent`, **GAS-like** abilities, **HL1-style AI schedules** | UE Gameplay Framework + GAS; GoldSrc AI |
| Saves | Versioned fields | Visited-sector rule, tombstones, tagged binary, upgraders, **no behaviour state** | Skyrim Papyrus lesson; protobuf-style versioning |
| Library status | — | MonoGame 3.8.5.1 (DesktopVK/DX12), Friflo over Arch, Bepu local determinism only | Checked 2026-09 (survey §3.7) |
| Game order | Daggerfall → HL1 → skeletal → Lugaru/Warband | Same, confirmed | D12 |

**Refinements made while writing the design docs** have been folded back into the plan (`docs/ENGINE_DESIGN_PLAN.md`, "Revisions after approval", A–G), so plan and design agree:
- **A:** CPU-batched sprites in v1; instancing later (GL bug reports).
- **B:** sprite techniques `Unlit`/`Lit`/`UnlitBlend`, not a `Billboard` technique.
- **C:** no MGCB at all; shaders use `dotnet-mgfxc`. **Done 2026-09-24 (R12).**
- **D:** boot order modules `Init` (register) → records → `Start`.
- **E:** `GlobalTransform` stores poses, not matrices.
- **F:** sprite sheet data is a simulation-side asset (animation events drive melee hits).
- **G:** no separate dev mode; build configurations + console + `developer` cvar.

### 9.3 Second pass (2026-09-21)
System.Numerics in simulation; ECS spike before hand-rolling storage; generational/persistent ids and `EntityRef`; source-generated metadata with versioning; runtime asset loaders and a sim/client asset split; own kinematic character controller; frame phase for camera/cosmetics; job layer and GC rules; .NET Hot Reload before assembly reloading; tests/profiler/replays; fixed pass list instead of a render graph; localization and mod-trust rules; licence recommendation; alternating infrastructure with the vertical slice.
