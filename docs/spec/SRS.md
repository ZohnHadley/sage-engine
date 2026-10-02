# Sage · Software Requirements Specification

> Version 1, 2026-10-02. Owner: the project owner (ZohnHadley). This document says **what** the engine must
> do and how well. [TDD.md](TDD.md) says how it is built, [API.md](API.md) is the contract game code programs
> against, and the [subsystem sheets](subsystems/README.md) carry the detailed, numbered requirements for each
> part. The plan that orders the work is [`../REDESIGN.md`](../REDESIGN.md) §5.

## 1. Introduction

### 1.1 Purpose

Sage is a C# game engine built on MonoGame. It is being turned into a **base engine** for game designers,
developers and modders: everything a game needs that is not a genre decision, with genre rules in kits and
the game itself in data. This specification fixes the goals, the platforms, the performance targets, the
quality attributes and the functional scope against which every subsystem is judged "complete".

### 1.2 Scope

In scope: the engine assemblies under `src/`, the RPG kit (`Sage.Kits.Rpg`, `Sage.Kits.Rpg.Client`), the
SDK and templates under `sdk/`, the `sage` command-line tool, the editor host, and the reference content
that proves them (`games/`, `tests/games/`).

Out of scope here: the design of any particular game, art and audio content, and the items listed in §9.

### 1.3 Definitions

| Term | Meaning |
|---|---|
| Base engine | `Sage.Core`, `Sage.Simulation`, `Sage.Physics3D`, `Sage.Gameplay`, `Sage.UI`, `Sage.Editing` (headless) plus `Sage.Client` and the hosts (MonoGame). Contains no genre decisions. |
| Kit | An optional assembly of genre rules built only on public base API. Today: the RPG kit. |
| Game | A folder with a `game.json`, data, and optionally one game assembly. |
| Record | A typed JSON document loaded through the VFS, such as an `item`, `prefab` or `anim_graph`. |
| Plugin | A unit of registration with a stable string id (`[Plugin]`, `IGameModule`). |
| Reference family | Daggerfall, Half-Life, Morrowind and S.T.A.L.K.E.R.: the first target games. |
| Headless | Running without a window or GPU: tests, `sage validate`, a dedicated server. |
| REQ-XXX-NN | A numbered requirement in a subsystem sheet. |

### 1.4 Stakeholders and users

| Who | Needs |
|---|---|
| **Designers** | Build levels, creatures, items, quests and logic in data and in the editor, with errors that name the file and line, and no C#. |
| **Developers** | A small, stable, documented API; one boot path; fast headless tests; a template that runs outside the repo. |
| **Modders** | Data mods that patch content without copying it, a load order, a conflict report, saves that survive a mod change; code mods later. |
| **Players** | A game that runs at a steady frame rate, saves anywhere, lets them rebind controls and change settings. |
| **The owner** | A Daggerfall-like action RPG on top of a base that can also make other genres. |

## 2. Overall description

### 2.1 Product perspective

Sage sits between MonoGame (window, graphics device, audio device, gamepad) and a game. It owns the loop,
the world, content loading, simulation and the subsystems listed in §6. It uses established libraries
rather than writing them: MonoGame for the platform and graphics API, BepuPhysics for 3D physics,
Friflo for ECS storage (wrapped behind Sage types), SharpGLTF for models and animation, Dear ImGui for
developer tools.

### 2.2 Product principles

1. **Base engine first.** A new game in any genre of the REDESIGN §0.5 gauntlet must be writable as game
   code and data with zero edits to engine assemblies. A sample that needs an engine edit has found a
   base-engine gap, which is fixed in the base.
2. **Declare, don't register.** Records, components, systems and parts are declared with attributes and
   registered by generated code.
3. **Data first.** Anything a designer tunes is a record field with metadata (range, unit, tooltip), so the
   inspector, the JSON Schemas, `sage validate` and the editor all see it.
4. **Headless by default.** Everything except drawing, sound output and device polling runs and is tested
   without MonoGame.
5. **Errors are content errors.** Bad data fails at load with the file, line and a fix, never as a
   crash during play.

### 2.3 Operating environment

| Item | Requirement |
|---|---|
| Runtime | .NET 8 (projects target `net8.0`); built with the .NET 10 SDK pinned in `global.json`. |
| Platform layer | MonoGame DesktopGL 3.8.5 (OpenGL). |
| Operating systems | Windows x64 and Linux x64 for development and CI. The first release's shipping platforms are an open decision (§10). |
| Graphics | OpenGL 3.x class GPUs through DesktopGL. Shaders are HLSL `.fx` compiled by `mgfxc` (Wine on Linux). |
| Input | Keyboard, mouse and XInput-style gamepads through MonoGame. |
| Audio | MonoGame `SoundEffect` (WAV today; OGG streaming is planned, #279 range, see the audio sheet). |

### 2.4 Constraints

- **No raw graphics API work.** Sage does not talk to Vulkan, Direct3D 12 or OpenGL directly; MonoGame is
  the graphics abstraction layer. DesktopVK and other backends wait for MonoGame (REDESIGN §5 "What
  deliberately waits").
- **Warnings are errors**, and the public API of the base and the kit is declared in `PublicAPI.*.txt`.
- **No MonoGame in simulation assemblies** (analyzer SAGE0024) and **no kit referenced by the base** (SAGE0025).
- **One branch and one PR per issue**; CI must be green on Linux and Windows before merging.

### 2.5 Assumptions

- Games are single-player first. Multiplayer is later, but the readiness rules in ARCHITECTURE §4.9 hold
  (fixed tick, commands as input, no client-only state in the simulation).
- Content is authored as JSON (JSONC accepted), glTF binary models, PNG/JPG textures, WAV sounds and
  `.map` brush levels until the editor's brushes replace them.

## 3. Goals and success criteria

| Goal | Measured by |
|---|---|
| G1. Every current subsystem is complete for the reference family. | Every Must row in every subsystem sheet is Done; each phase's exit criterion (REDESIGN §5) passes headlessly. |
| G2. Games need no engine edits. | The phase-5 samples (HL1-lite, Morrowind-lite, STALKER-lite) run from the stock host with small game code. |
| G3. Designers work without C# or JSON. | The editor exit games (10a, 10b) build and play a level through the editor only. |
| G4. Modders can extend and override safely. | The 4j exit game and the phase-9 exit (data and code mods, packed) report conflicts and keep saves loading. |
| G5. Developers ship outside the repo. | R1's exit: install templates from a feed, create, run and package a game on a clean machine. |

## 4. Platform and technology requirements

| ID | Requirement | Priority | Status |
|---|---|---|---|
| REQ-SYS-01 | The engine shall build and pass its tests on Windows x64 and Linux x64 in CI. | Must | Done |
| REQ-SYS-02 | The engine shall run a game in a window through MonoGame DesktopGL, and headlessly with no window or GPU. | Must | Done |
| REQ-SYS-03 | Simulation assemblies shall not reference MonoGame; the client, hosts and editor may. | Must | Done (SAGE0024) |
| REQ-SYS-04 | A game shall be buildable outside the repository from the Sage SDK and templates. | Must | Done (local feed); public feed open (R1) |
| REQ-SYS-05 | A game shall be packageable into a folder that runs on a machine without the SDK. | Must | Not started (R1, #6) |
| REQ-SYS-06 | The engine shall keep its public API declared and versioned with SemVer from git tags. | Must | Done; first tag pending (R1) |

## 5. Performance requirements

Reference hardware is not yet fixed (§10). Until it is, targets are measured on CI runners and the
developer's machine, and the zero-allocation rules are the hard requirements.

| ID | Requirement | Priority | Status |
|---|---|---|---|
| REQ-PERF-01 | The simulation shall run at a fixed tick, 60 Hz by default (`sim_tickrate`), with interpolated rendering and a catch-up cap (`sim_maxframetime`). | Must | Done |
| REQ-PERF-02 | A steady-state tick of the core systems (ECS, logic, animation, combat, AI think) shall allocate nothing on the managed heap. | Must | Done for the systems with Measurements tests; physics backend allocates about 40 B a tick (#272 range, 4l) |
| REQ-PERF-03 | A frame shall allocate nothing in steady state with the developer overlay closed. | Must | Partial (dev tools allocate; 10b) |
| REQ-PERF-04 | The Sandbox shall hold 60 frames per second with 2,000 entities, and a tick shall use under a quarter of its budget at that size. | Must | Done for the tick (scale review 2026-09-24: 0.76 ms of 16.67 ms) |
| REQ-PERF-05 | Crossing a streaming sector edge shall not stall a frame beyond the frame budget. | Must | Partial (synchronous generation; 4m) |
| REQ-PERF-06 | GPU texture and mesh memory shall return to baseline after the content that used them unloads. | Must | Not started (asset scopes, 4n) |
| REQ-PERF-07 | Saving a large world shall not stall the tick that requested it. | Should | Not started (4m) |
| REQ-PERF-08 | Distant animators and AI shall update less often (LOD) without changing results near the player. | Should | Done for animators (`anim_lod_distance`) |

## 6. Functional requirements

Each subsystem's numbered requirements, status and open issues are in its sheet. This table is the
summary and the traceability index.

| # | Subsystem | Code | What it must provide | Sheet |
|---|---|---|---|---|
| 01 | Platform layer | PLAT | Window, OS events, timing, file system roots, device polling, behind the host. | [01](subsystems/01-platform.md) |
| 02 | Core services | CORE | Logging, asserts, crash reports, cvars and console, profiler, allocation rules. | [02](subsystems/02-core-services.md) |
| 03 | App and game loop | LOOP | One boot path (`SageApp`), plugins, sealed registration, fixed-tick loop, phases and system scheduling. | [03](subsystems/03-app-and-loop.md) |
| 04 | ECS, scenes and prefabs | ECS | Worlds, entities, components with stable ids, queries, hierarchy, prefabs, scenes, placements. | [04](subsystems/04-ecs-and-scenes.md) |
| 05 | Events and logic | LOGIC | Event bus, entity I/O, logic entities, timers, tweens, state machines, the condition/action vocabulary. | [05](subsystems/05-events-and-logic.md) |
| 06 | Assets and content | ASSET | VFS and mounts, records, strict loading, schemas, asset loading and caching, hot reload. | [06](subsystems/06-assets-and-content.md) |
| 07 | Rendering | REND | Cameras, the extract-and-draw pipeline, passes, materials, lighting, shadows, sky, post, particles, LOD. | [07](subsystems/07-rendering.md) |
| 08 | Physics and movement | PHYS | Bodies, queries, triggers, the character controller, movers, joints, water and ladders. | [08](subsystems/08-physics.md) |
| 09 | Navigation and AI | AI | Pathfinding, perception, schedules, routines, off-screen simulation, behaviour selection. | [09](subsystems/09-navigation-and-ai.md) |
| 10 | Animation | ANIM | Sprites and skeletons, clips, the animation graph, events, IK, root motion, retargeting. | [10](subsystems/10-animation.md) |
| 11 | Audio | AUD | Mixer and buses, positional sound, cues, music, streaming, occlusion and reverb. | [11](subsystems/11-audio.md) |
| 12 | Input | INP | Devices, actions and contexts, gamepad, rebinding, scripted input and replay. | [12](subsystems/12-input.md) |
| 13 | Game UI | UI | Widgets, layout, styles, focus and gamepad, localisation, fonts, menus and HUD. | [13](subsystems/13-ui.md) |
| 14 | World, streaming and time | WORLD | Sectors and rebasing, cells and dormancy, interiors, travel, the calendar and clock. | [14](subsystems/14-world-and-streaming.md) |
| 15 | Saves | SAVE | Diff saves keyed by stable ids, upgraders, reconcile on load, quick-save and autosave. | [15](subsystems/15-saves.md) |
| 16 | Gameplay framework | GAME | Rules, pawns and controllers, attributes and effects, damage, abilities, items, factions, dialogue and quests. | [16](subsystems/16-gameplay.md) |
| 17 | RPG kit | RPG | Skills and levelling, the RPG screens, shops, the spellmaker, Daggerfall conventions. | [17](subsystems/17-rpg-kit.md) |
| 18 | Editor | EDIT | The editing model, commands and undo, placement, inspector, wiring, play-in-editor, brushes, browsers. | [18](subsystems/18-editor.md) |
| 19 | Modding | MOD | Mod manifests, load order, record patching, conflict reports, packed mods, code mods. | [19](subsystems/19-modding.md) |
| 20 | Tooling and release | TOOL | The `sage` CLI, SDK and templates, analyzers, CI, packaging, versioning and release. | [20](subsystems/20-tooling-and-release.md) |

## 7. Quality attributes

| ID | Requirement | Priority | Status |
|---|---|---|---|
| REQ-QUAL-01 | **Data-only games.** A game with no C# shall be able to place, wire, script, save and play a level using records alone. | Must | Done for the exit games in `tests/games/`; vocabulary gaps in 4m |
| REQ-QUAL-02 | **Content errors at load.** Malformed or dangling content shall be reported with file and line before play, by the game and by `sage validate`. | Must | Done |
| REQ-QUAL-03 | **Determinism.** Given the same content, save and commands, a headless run shall produce the same world, tick for tick. | Should | Partial: by construction, no cross-run check yet (4l, 4o replay) |
| REQ-QUAL-04 | **Testability.** Every simulation feature shall be testable headlessly through `HeadlessApp`, in parallel. | Must | Done |
| REQ-QUAL-05 | **Save robustness.** Saves shall survive content rebalances, prefab edits and mod changes without losing or doubling entities. | Must | Done (4i, 4j exits); remaining limits in 4m |
| REQ-QUAL-06 | **API stability.** Public base and kit API shall change only with a declared entry and SemVer; unstable areas carry `[Experimental]` ids. | Must | Done |
| REQ-QUAL-07 | **Honest documentation.** Claims that something exists shall cite the test that proves it, checked in CI. | Must | Done (`tools/check_docs.py`) |
| REQ-QUAL-08 | **Moddability.** Every record type shall be patchable by a mod without copying the whole record. | Must | Done for data; keyed list merge in phase 9 |
| REQ-QUAL-09 | **Accessibility.** Players shall be able to rebind every action, scale the UI, and read subtitles. | Should | Not started (4o, 4q) |
| REQ-QUAL-10 | **Extensibility.** A game or kit shall add components, records, systems, render passes, vocabulary words and UI widgets without engine edits. | Must | Done except UI widgets from data and pass replacement (4q, 4n) |

## 8. External interfaces

| Interface | Format or contract | Sheet |
|---|---|---|
| Game manifest | `game.json` (id, version, `sage` range, mounts, plugins, start scene) | [03](subsystems/03-app-and-loop.md) |
| Mod manifest | `mod.json` (id, version, dependencies, load-after) and `user://mods.json` | [19](subsystems/19-modding.md) |
| Content | JSON/JSONC records; JSON Schemas generated into `schemas/` | [06](subsystems/06-assets-and-content.md) |
| Models and animation | glTF binary (`.glb`) with skins and clips | [07](subsystems/07-rendering.md), [10](subsystems/10-animation.md) |
| Textures, sound | PNG/JPG; WAV (OGG planned) | [06](subsystems/06-assets-and-content.md), [11](subsystems/11-audio.md) |
| Shaders | HLSL `.fx` compiled to `.mgfxo` | [07](subsystems/07-rendering.md) |
| Brush levels | Quake `.map` (TrenchBroom) import, until editor brushes (#61) | [18](subsystems/18-editor.md) |
| Saves | JSON save format 4 with stable ids, mod list and upgraders | [15](subsystems/15-saves.md) |
| Command line | `Sage.Host -game <folder> [-edit level] [+cmd ...]`, `sage validate|schema|mods` | [20](subsystems/20-tooling-and-release.md) |
| User folder | `user://` (config, saves, mods.json, logs, crash reports) | [02](subsystems/02-core-services.md) |
| Code | The public API of the base and the kit | [API.md](API.md) |

## 9. Out of scope (for now)

Multiplayer and netcode (the readiness rules hold), visual scripting, consoles, mobile and web, AOT,
DesktopVK and direct Vulkan or Direct3D 12 back ends, 2D skeletal animation. The 2D profile, mouse picking
for strategy games and the other genres are Stage D (REDESIGN §5), after the current subsystems are complete.

## 10. Open decisions

These are the owner's (REDESIGN §6). The plan works with any answer; each has a recommendation there.

| Decision | Affects |
|---|---|
| Reference hardware for the performance targets | §5 |
| Target platforms of the first release (Windows only, or Windows and Linux) | REQ-SYS-05, R1 |
| Distribution: a NuGet feed and templates, or source only | R1 |
| Licence (still CC0; MIT or Apache recommended) | Anyone building on the engine |
| Scripting language, decided on evidence after the phase-5 samples | Phase 9, the scripting gate |
| Whether modders get the editor in shipped games | 10b, phase 9 |

## 11. Traceability to the plan

| Milestone | Theme | Main sheets |
|---|---|---|
| 4k | Joints and ragdolls (in progress) | 08, 10 |
| 4l | Physics, movement and navigation | 08, 09 |
| 4m | World, logic and saves | 04, 05, 14, 15 |
| R1 | Tooling and the first release | 02, 06, 20 |
| 4n | Rendering and assets | 06, 07 |
| 4o | Audio and input | 11, 12 |
| 4q | Game UI and menus | 13 |
| 4p | Animation | 10 |
| 10b | Editor, part 2 | 18 |
| 4f | RPG progression and economy | 16, 17 |
| 4r | AI, combat and narrative depth | 09, 16 |
| 9 | Code mods and packaging | 19 |
