# Sage Engine — Design Docs Index

Detailed subsystem designs. `ARCHITECTURE.md` is the overview (layers, rules, decisions); these docs say *how* each part works. The research behind them is in `docs/research/engine-survey.md`, the plan that produced them in `docs/ENGINE_DESIGN_PLAN.md`, and the build tasks in `TODO.md`.

Status: design, 2026-09-22. Nothing here is built yet unless a doc's "Mapping from today's code" section says so.

## Reading order

| Doc | Summary | Depth |
|---|---|---|
| [01-host-and-modules](01-host-and-modules.md) | Boot sequence, main loop (fixed tick + interpolation), module system, build configurations, console availability and `developer` | detailed |
| [02-core-services-and-logging](02-core-services-and-logging.md) | Logging, asserts, crash reports, cvars/console, time, profiler, jobs, GC rules | detailed |
| [03-world-and-ecs](03-world-and-ecs.md) | `World`, entities, components, queries, command buffers, schedules, transform hierarchy | detailed |
| [04-events-and-messaging](04-events-and-messaging.md) | The four messaging mechanisms and when to use which | detailed |
| [05-assets-and-vfs](05-assets-and-vfs.md) | Virtual file system, asset handles and loading, hot reload, data records | detailed |
| [06-rendering](06-rendering.md) | Extract → `RenderSnapshot`, passes, culling, batching, billboards, lighting | detailed |
| [07-materials-and-shaders](07-materials-and-shaders.md) | Effect files, techniques as permutations, material records, parameter binding | detailed |
| [08-input](08-input.md) | Devices, action maps, input contexts, `PlayerCommand` | detailed |
| [09-serialization-and-saves](09-serialization-and-saves.md) | Attribute contract, source generator, saves, versioning | detailed |
| [10-physics](10-physics.md) | Bepu subsystem, queries, triggers, kinematic character controller | short |
| [11-audio](11-audio.md) | Sound sources, buses, music | short |
| [12-animation](12-animation.md) | Sprite animation now, skeletal later | short |
| [13-ui](13-ui.md) | ImGui for dev tools; runtime UI choice | short |
| [14-world-streaming](14-world-streaming.md) | Sectors, streaming rings, origin rebasing, interiors, dormancy | short |
| [15-editor](15-editor.md) | Editor documents, command log, undo, inspector | short |
| [16-gameplay-framework](16-gameplay-framework.md) | `GameRules`, Controller/Pawn, abilities, AI | short |
| [17-modding](17-modding.md) | Load order, record merge, trusted C# mods | short |

"Short" docs are expanded when their roadmap phase starts.

## Doc template

Detailed docs use all sections; short docs use at least 1, 3, 4, 11 and 14.

1. Purpose and scope
2. Research basis
3. Concepts and data model
4. Public API sketch
5. Data flow / lifecycle
6. Threading and memory
7. File formats
8. Errors and fallbacks
9. Debug and tooling hooks (cvars, console commands, overlays, log categories)
10. Mapping from today's code
11. v1 scope vs later
12. Multiplayer-later notes
13. Open questions
14. Build steps (linked to `TODO.md` ids)

**v1 scope** always means: the minimum needed for the Daggerfall-like vertical slice in `TODO.md` ("Suggested first milestone").

API sketches are C# signatures to pin down names and responsibilities, not final code. Following the existing codebase, engine types are `internal` unless they're part of the game-facing API, which is `public` (games live in other assemblies).

## Glossary (shared names — use these exactly)

### Host and core
| Name | Meaning | Doc |
|---|---|---|
| `Engine` | Process-wide core services object: cvars, VFS, `RecordStore`, modules and worlds today; `AssetServer`, jobs and engine signals join in later steps. Created by the host, passed in (never a static singleton). Lives in `Sage.Engine`, so it has no MonoGame types. Client services (renderer, input devices, audio) are provided by client modules through `ModuleContext.Provide` | 01 |
| `IModule` / `ModuleKind` | A logical engine or game unit with dependencies and `Init` (register) → `Start` (use records/GPU) → `OnWorldCreated` (per world) → `Shutdown`. Kinds: `Runtime`, `Editor`, `Tool` | 01 |
| `ModuleContext` | What a module gets in `Init`/`Start`: `Engine`, `Get<T>` (services from the host or declared dependencies only), `Provide<T>` | 01 |
| `IGameModule` | The single entry point a game assembly implements (exactly one public class per game assembly) | 01 |
| `game.json` | Game manifest: name, id (= record namespace), game assembly, mounts, modules to disable; later default map and mods folder | 01, 05 |
| `ClientModule` / `ContentService` / `Renderer` | The default client module (record types, engine input actions, per-world snapshot + Extract/Render systems), its interim VFS-backed loader for models, effects and textures (until the `AssetServer`), and the renderer service | 01, 05, 06 |
| Build configurations | `Debug`, `Development`, `Shipping` (compile-time). "Dev builds" = `Debug` + `Development` (`SAGE_DEV`). There is no runtime dev mode | 01, 02 |
| `developer` | `DevOnly` cvar that sets defaults (log verbosity, hot reload, `dev_override/`); each feature still has its own cvar | 01, 02 |
| `DevOnly` | Cvar/command flag: compiled only into dev builds | 02 |
| `Log`, `LogCat` | Logging API and categories | 02 |
| `CVar`, console command | Named, flagged runtime variables and commands | 02 |

### Simulation
| Name | Meaning | Doc |
|---|---|---|
| `World` | One simulation: entities, schedules, event queues, physics space | 03 |
| `EntityRef` | Handle to an entity in a `World`; detects stale (deleted) entities. **Realised as Friflo.Engine.ECS's `Entity` struct** (in code: `Entity`) | 03 |
| `PersistentId` | Stable id for placed or saved entities, used by maps, saves, quests | 03, 09 |
| Component | Plain data attached to an entity: a struct implementing `Friflo.Engine.ECS.IComponent`. No behaviour, no service references | 03 |
| System | Code that runs over queries in a schedule phase | 03 |
| `CommandBuffer` | Deferred structural changes (spawn, despawn, add/remove component): Friflo's, reached through `World.Commands`, applied at the end of every phase (and by `World.FlushCommands`) | 03 |
| `Schedule.Fixed` | Runs at a fixed rate, `sim_tickrate` (default **60 Hz**). Gameplay, physics, AI | 01, 03 |
| `Schedule.Frame` | Runs once per rendered frame. Camera, cosmetics, extract, render, UI | 01, 03 |
| Fixed phases | `Commands → PrePhysics → Physics → PostPhysics → Gameplay → AI → Animation → EntityIO → Late` | 03 |
| Frame phases | `FrameUpdate → Extract → Render → Overlay` | 03, 06 |
| `Transform` | Local position/rotation/scale, relative to the parent (or to the root's sector) | 03 |
| `Attributes` / `GameplayTags` | Components: an entity's attribute values (health, mana, armour…) and its 64-tag bitset | 16 |
| `effect` / `ActiveEffects` | The record describing a change (modifiers, duration, period, stacking, tags) and the instances running on an entity | 16 |
| `GameRules` | World resource a game subclasses: spawning the player, deaths, loading hooks. Started by `Engine.CreateWorld` once every module has seen the world | 16 |
| `AIState` / `ai_schedule` / `ai_profile` | An agent's conditions, schedule and current task; the record listing a schedule's tasks and interrupts; the record with its sight, melee and think tuning | 16 |
| `CharacterController` | Component: the kinematic capsule the engine moves (collide-and-slide, step-up, slopes, crouch, jump) | 10 |
| `Pawn` / `PawnIntent` / `PlayerControlled` | A possessable body, what its controller wants this tick, and the tag marking the local player's pawn | 16 |
| `movement_profile` | Record with speeds, acceleration, jump, gravity, slope and step limits | 10, 16 |
| `PhysicsSpace` | World resource: the Bepu simulation, its queries (raycast, sweep, overlap) and trigger overlaps | 10 |
| `Collider` / `RigidBody` / `PhysicsBody` | The shape and layer, how it moves (static, kinematic, dynamic), and the Bepu handle the engine manages | 10 |
| `LayerMask` | Which collision layers a query or a contact considers | 10 |
| `SectorCoord` | Component on root entities: which **1024 m** sector (int X, Z) their `Transform` is relative to | 03, 14 |
| `GlobalTransform` | Computed `Pose Current` + `Pose Previous` relative to the world's origin sector; interpolated by Extract | 03, 06 |
| `GameRules` | Per-world object owning game flow (spawning, win/lose, time of day) | 16 |
| Controller / Pawn | A `Controller` (player or AI) possesses a `Pawn` (the body) | 16 |
| `PawnIntent` | Component both controllers write (move, look, actions); movement, combat and interaction read only this | 16 |
| `ActiveCamera` | World resource: the camera position/rotation/fov/near/far the view is rendered from (set by the host's camera today; read by `CameraExtract` and by simulation code such as billboard facing) | 03, 06 |
| `RenderEnvironment` | World resource: clear (sky) colour, sun, hemispheric ambient, fog | 06 |
| `PlayerInput` | World resource: the local player's `PlayerCommand` for the tick being simulated | 08 |

### Messaging
| Name | Meaning | Doc |
|---|---|---|
| Structural notification | Entity/component added/removed, from `World` | 04 |
| Game event | Typed struct event in a per-schedule queue, read through a per-reader cursor | 04 |
| Entity I/O | Output (`OnOpen`) → target input (`Close`), with delay, resolved at map load | 04 |
| Engine signal | Plain C# event for rare engine callbacks (`AssetReloaded`, window resize) | 04 |

### Content
| Name | Meaning | Doc |
|---|---|---|
| `VirtualPath` | Path inside the VFS, e.g. `textures/goblin.png`; the identity of an asset | 05 |
| Mount | A folder or `.pak` (zip) added to the VFS at a priority | 05 |
| `AssetPath` | An interned `VirtualPath` (a small id) stored in components. It says *which* asset without loading it; the simulation uses it for render-only assets | 05 |
| `AssetRef<T>` | Cheap handle (slot + generation) to a loading or loaded asset; returns a placeholder until ready. Not ref-counted itself | 05 |
| `AssetServer` | Loads, caches, reloads assets through `IAssetLoader<T>` | 05 |
| `AssetScope` | Owns asset references: loading into a scope takes a reference; disposing it releases all of them (per engine, world, level, UI screen). Zero references → LRU cache → evicted | 05 |
| Record | A data definition (item, spell, material, input map…) in JSON, in `data/**/*.json` of any mount. `base` inherits, `"patch": true` merges field by field, `"abstract": true` marks a template | 05 |
| `RecordId` | `namespace:name`, e.g. `sage:lit_default`, `mygame:iron_sword` | 05 |
| `RecordStore` | Holds all records after load-order merge; typed lookup | 05 |
| Effect | A compiled MonoGame shader file (`.mgfxo`) | 07 |
| Technique | A named shader variant inside an effect (`Default`, `AlphaTest`, `Unlit`; sprites: `Unlit`, `Lit`, `UnlitBlend`) | 07 |
| Material | A record: effect + technique + parameters + textures + render state | 07 |

### Rendering and input
| Name | Meaning | Doc |
|---|---|---|
| `RenderSnapshot` | Pooled per-frame copy of everything rendering needs, built in Extract | 06 |
| `SectorCoord` | Which 1024 m sector of the exterior something is in; today everything is in (0, 0) | 14, 03 |
| `Heightfield` / `Terrain` | A square grid of heights, and the world resource holding the loaded sectors (ground height for gameplay, chunk meshes for the client) | 14 |
| `MeshHandle` | A mesh the renderer built (terrain chunks): the buffers live in the renderer, components hold the handle | 06 |
| `SpriteRenderer` / `sprite_sheet` | Billboard component, and the record holding a sheet's texture, direction groups, frames and animations | 06, 12 |
| `BillboardMode` | `Cylindrical` (turns about Y: characters, trees) or `Spherical` (faces the camera fully) | 06 |
| `SpriteAnimator` | Component with the playing clip index and its time, advanced by the simulation | 12 |
| `RenderItem` | One drawable in the snapshot: mesh, material, world matrix, sort key | 06 |
| `SpriteInstance` | One billboard sprite in the snapshot | 06 |
| `MeshHandle`, `TextureHandle` | Handles to GPU resources owned by the renderer | 06 |
| Action | A named input (`MoveForward`, `Attack`) bound to keys/buttons by an input-map record | 08 |
| Input context | A layer (`Editor`, `UI`, `Gameplay`) that can consume input | 08 |
| `PlayerCommand` | One tick's worth of player intent (move, look, actions), sampled from actions | 08 |

### Log categories
`Core`, `Host`, `Modules`, `VFS`, `Assets`, `Records`, `Shaders`, `Render`, `Input`, `World`, `Events`, `Physics`, `Audio`, `Animation`, `UI`, `Streaming`, `Save`, `AI`, `Gameplay`, `Editor`, `Mods`, `Console` (console echo and command output). Games add their own (see 02).
