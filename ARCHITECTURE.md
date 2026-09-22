# Sage Engine — Architecture Overview

Status: design, revised 2026-09-22 (third pass: research-backed; see §9). Nothing here is built yet except where "Today" says so.

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
│ ENGINE  ┌─ Simulation (Sage.Engine, no MonoGame) ─────────┐  │
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
1. **Down only:** game → framework → engine. The engine never references framework or game types.
2. **The simulation never references MonoGame or client code.** `Sage.Engine` and `Sage.Framework` use `System.Numerics` (D2). Rendering *reads* simulation state through Extract (§4.6); the simulation never calls rendering.
3. **Presentation → simulation only through `PlayerCommand` or command queues.** UI and camera code never write gameplay components directly ([04 §3.1](docs/design/04-events-and-messaging.md)).
4. **Runtime never references tools or the editor.**

---

## 3. Solution layout (target)

```
sage-engine/
  src/
    Sage.Engine/            simulation: core services, World/ECS, events, VFS/assets/records,
                            serialization, physics, streaming          (no MonoGame)
    Sage.Client/            rendering, materials/shaders, input, audio, dev UI     (MonoGame)
    Sage.Framework/         optional gameplay modules, simulation side             (no MonoGame)
    Sage.Framework.Client/  camera rigs, cue playback, HUD helpers
    Sage.Editor/            editor host + panels
    Sage.Host/              game host exe (boot + main loop)
    Sage.Generators/        Roslyn source generator (serializers, inspector metadata, I/O tables)
  engine_content/           shaders (.fx → .mgfxo), placeholders, engine records
  tests/  Sage.Tests/ (headless worlds) · Sage.Benchmarks/
  games/  Sandbox/          the Daggerfall-like vertical slice (TODO milestone)
  docs/
```

A game: `games/MyGame/` with `MyGame.csproj` (an `IGameModule`), `game.json`, `content/` (assets) and `data/` (records). See [01 §3.3](docs/design/01-host-and-modules.md).

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
- **Transform hierarchy:** `Transform` + `SectorCoord` → `GlobalTransform` (previous/current poses, for interpolation).
- **Storage:** **Friflo.Engine.ECS** behind a thin `World`, adopted after it passed requirements E1–E9 in a spike (D4, 03 §3.1). Handles are Friflo's `Entity`; queries are Friflo's; hot paths iterate chunks.

### 4.4 Events and messaging — [04](docs/design/04-events-and-messaging.md)
Four mechanisms, each with a distinct job:
1. **structural notifications**;
2. **game events** (typed, queued per schedule, per-reader cursors, no re-entrant callbacks);
3. **entity I/O** (Source-style outputs → inputs, resolved and type-checked at map load);
4. **engine signals** (rare C# events between ticks).

### 4.5 Assets, VFS and data records — [05](docs/design/05-assets-and-vfs.md)
- **VFS with mounts** and a separate writable `user://`. **Asset identity = virtual path**, so mods override by shadowing.
- **Assets:** `AssetPath` (unloaded, in components) vs `AssetRef<T>` (loaded). **Scopes own lifetimes**; LRU cache; async decode + budgeted GPU upload; placeholders instead of nulls; hot reload.
- **Formats:** runtime loaders for PNG / glTF / WAV, with **no MGCB** (this also fixes TODO #9).
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

### 4.11 Other subsystems (short docs, expanded when their phase starts)
| Subsystem | Doc | One-line summary |
|---|---|---|
| Physics | [10](docs/design/10-physics.md) | BepuPhysics v2 per world; components hold handles; triggers → events; **own kinematic character controller** |
| Audio | [11](docs/design/11-audio.md) | Event-driven presentation; `sound` records; buses; MonoGame backend behind an interface |
| Animation | [12](docs/design/12-animation.md) | v1 sprite animation with sim-owned time and frame events ("hit" frames); skeletal glTF later |
| UI | [13](docs/design/13-ui.md) | ImGui for dev/editor only; game UI library decision D8 |
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

**Today** (after step 4): `Sage.sln` with `src/Sage.Engine` (no MonoGame), `src/Sage.Client`, `src/Sage.Editor`, `src/Sage.Host` (exe) and `tests/Sage.Tests`; logging/console/build configurations (step 2); `Engine` + `World` over Friflo.Engine.ECS with no singletons (step 3); a fixed-tick loop with interpolated rendering, schedules/phases and the profiler (step 4). Still `BasicEffect` rendering and MGCB content. Each design doc has a "Mapping from today's code" table. The main moves:

| Today | Becomes |
|---|---|
| `Game1`, `Program` | `Sage.Host` boot + fixed-tick loop ([01](docs/design/01-host-and-modules.md)); test scene → `games/Sandbox` |
| ~~`EntityContext`, `Entity`, `EntityContextListener`, `ArchetypeView`~~ (step 3) | `World`, `EntityRef` (Friflo `Entity`), typed queries, structural notifications ([03](docs/design/03-world-and-ecs.md), [04](docs/design/04-events-and-messaging.md)) |
| `Transform` struct + `TransformMath` (step 3; was `ComponentTransform`) | + `GlobalTransform` + transform propagation (step 4), `SectorCoord` later (R6); math in `TransformMath`; billboarding moves to the renderer |
| `ModelRenderer` (struct, step 3), `ModelRendererSystem` | `MeshRenderer` (with `AssetPath`) + `MeshExtract` + material-based passes ([06](docs/design/06-rendering.md), [07](docs/design/07-materials-and-shaders.md)) |
| `InputSystem`, listeners | `InputDevices` + actions/contexts + `PlayerCommand` ([08](docs/design/08-input.md)) |
| `DevCamera` | Editor camera rig on actions ([15](docs/design/15-editor.md)) |
| `EditorUI`, `EntityContextMenuUI` | Editor menu, outliner, generated inspector ([15](docs/design/15-editor.md)) |
| `UtilAssets`, `Content.mgcb` | `AssetServer` + VFS + runtime loaders ([05](docs/design/05-assets-and-vfs.md)) |
| `Console.WriteLine` | `Log` with categories ([02](docs/design/02-core-services-and-logging.md)) |

**Suggested order** (each step keeps the engine running):
1. ~~**Solution split**~~ **Done 2026-09-22.** `Sage.Engine` / `Sage.Client` / `Sage.Editor` / `Sage.Host` + `tests/Sage.Tests` (15 headless tests); `ComponentTransform` on `System.Numerics`; `IEngineSystem.update(float)` instead of MonoGame's `GameTime`. Two differences from the plan: **`games/Sandbox` is deferred to step 4**, because it needs `IGameModule` to plug in (the test scene stays in `Game1` until then); and **`Sage.Host` references `Sage.Editor`** because today's exe is still game + editor in one.
2. ~~**Logging + build configurations + console/`developer`**~~ **Done 2026-09-22** (TODO R10). Debug/Development/Shipping configurations with `SAGE_DEV`; `Log` with categories, zero-cost disabled calls, file/stdout/ring sinks, duplicate collapse and rate limiting; `Assert.Dev/Ensure/Check`; crash reports; cvars, console commands, `config.cfg` and `+launch` args; the ImGui console (`~`) and `stat fps`/`stat mem`; 36 tests. Deferred on purpose: profiler scopes and `TickTime` (step 4, they need the tick phases) and the job system (step 5, with async asset loading).
3. ~~**`World` + `Engine` objects replace the singletons**~~ **Done 2026-09-22** (R1, R5; D4 decided). Friflo.Engine.ECS 3.6.0 passed all nine requirements in a spike (03 §3.1) and is adopted behind a thin `World` (struct components, stale-detecting handles, typed queries, command buffer, notifications with the old ordering guarantees, `PersistentId`, resources, hierarchy). `Engine` owns cvars and worlds; no engine singletons remain (`Log`/`CrashReporter` are static by design). Fixed #15, #33, #34, #36, #38. 44 tests.
4. ~~**Fixed tick + schedules + phases**~~ **Done 2026-09-22** (R2, R4). `FixedStepClock` (Fiedler accumulator, clamped frame time, time scale), `sim_tickrate` 60 Hz, render interpolation via `GlobalTransform` (previous/current poses) + transform propagation through the hierarchy, `ISystem` in Fixed/Frame phases with `before`/`after` ordering, run conditions, pause, per-phase command flushing, profiler scopes + `stat frame` + `sys_list`/`sys_toggle`. The loop allocates nothing in steady state (tested). Deferred: `SectorCoord`/origin rebasing (R6), access declarations (event bus), `host_maxfps`. 56 tests.
5. **Modules + `game.json` + VFS + records** (R8, R11). Sandbox becomes a real game module.
6. **Extract + snapshot + materials** (R9); **input actions → `PlayerCommand`** (R3).
7. Then the **vertical slice**, alternating features with the infrastructure they prove they need.

**Guarding against over-architecting.** Hobby engines usually die from years of infrastructure with nothing playable. After step 4, **alternate**: build a piece of the Sandbox slice, then the infrastructure it proved necessary. The design docs are a map, not a checklist to finish first. Every doc's "v1 scope" is the minimum for the slice. Engine or framework code is extracted **on second use** ("write games, not engines", survey §3.8).

**Game order:** Daggerfall-like (billboards defer skeletal animation, the single biggest system) → HL1-like (brush maps, entity I/O, movement feel) → skeletal animation → Lugaru-like → Warband-like.

---

## 8. Decisions

| # | Decision | Status / choice |
|---|---|---|
| D1 | Multiplayer | **Later (Phase 7), not now.** Follow the readiness rules (§4.9) |
| D2 | Math types in simulation | **`System.Numerics`** in all simulation code; MonoGame types only in `Sage.Client`. Bepu-native, SIMD, keeps MonoGame out of the simulation, enables headless tests and a future server |
| D3 | Game code and modding | **C# game modules; data mods + trusted C# mods** (no sandboxed scripting). Code mods are never auto-downloaded ([17](docs/design/17-modding.md)) |
| D4 | ECS storage | **Decided (step 3): Friflo.Engine.ECS 3.6.0** behind a thin `World`. It passed requirements E1–E9 in a spike ([03 §3.1](docs/design/03-world-and-ecs.md)); Arch was not needed. Queries are Friflo's own types; hot paths iterate chunks (0 allocations) |
| D5 | Physics | **BepuPhysics v2** + **own kinematic character controller** |
| D6 | Level editing | Own editor for terrain/props/entities + **TrenchBroom `.map` import** for brush interiors (later) |
| D7 | Editor form | **Separate editor host** loading the same game module, with play-in-editor |
| D8 | Game UI | ImGui for dev/editor only. **Evaluate Gum / Myra vs custom** before inventory/dialogue screens ([13](docs/design/13-ui.md)) |
| D9 | Engine licence | Currently **CC0** (`LICENSE`). Recommend **MIT** or **Apache-2.0** (adds a patent grant) if other developers will build commercial games on it. Keep vendored licences (`packages/MonoGame.ImGuiNet-main/LICENSE`). **Needs your decision** |
| D10 | Asset formats and pipeline | Runtime **PNG / glTF 2.0 (SharpGLTF) / WAV**; shaders via **`dotnet-mgfxc`**; **MGCB no longer used** |
| D11 | Model format for animation | **glTF 2.0** over FBX |
| D12 | Which game drives development | **Daggerfall-like** (confirmed) |
| D13 | MonoGame version/backend | **Upgrade 3.8.2 → 3.8.5.x, stay on DesktopGL** (TODO R12). Re-evaluate DesktopVK (new in 3.8.5, intended to replace DesktopGL over the next few years) once it has matured |

---

## 9. Revision history

### 9.1 Third pass — research-backed (2026-09-22)
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
| Gameplay framework | Module list | `GameRules`, Controller/Pawn/`PawnIntent`, **GAS-like** abilities, **HL1-style AI schedules** | UE Gameplay Framework + GAS; GoldSrc AI |
| Saves | Versioned fields | Visited-sector rule, tombstones, tagged binary, upgraders, **no behaviour state** | Skyrim Papyrus lesson; protobuf-style versioning |
| Library status | — | MonoGame 3.8.5.1 (DesktopVK/DX12), Friflo over Arch, Bepu local determinism only | Checked 2026-09 (survey §3.7) |
| Game order | Daggerfall → HL1 → skeletal → Lugaru/Warband | Same, confirmed | D12 |

**Refinements made while writing the design docs** have been folded back into the plan (`docs/ENGINE_DESIGN_PLAN.md`, "Revisions after approval", A–G), so plan and design agree:
- **A:** CPU-batched sprites in v1; instancing later (GL bug reports).
- **B:** sprite techniques `Unlit`/`Lit`/`UnlitBlend`, not a `Billboard` technique.
- **C:** no MGCB at all; shaders use `dotnet-mgfxc`.
- **D:** boot order modules `Init` (register) → records → `Start`.
- **E:** `GlobalTransform` stores poses, not matrices.
- **F:** sprite sheet data is a simulation-side asset (animation events drive melee hits).
- **G:** no separate dev mode; build configurations + console + `developer` cvar.

### 9.2 Second pass (2026-09-21)
System.Numerics in simulation; ECS spike before hand-rolling storage; generational/persistent ids and `EntityRef`; source-generated metadata with versioning; runtime asset loaders and a sim/client asset split; own kinematic character controller; frame phase for camera/cosmetics; job layer and GC rules; .NET Hot Reload before assembly reloading; tests/profiler/replays; fixed pass list instead of a render graph; localization and mod-trust rules; licence recommendation; alternating infrastructure with the vertical slice.
