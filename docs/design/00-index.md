# Sage Engine — Design Docs Index

Detailed subsystem designs. The structured specification on top of them (requirements, status and open issues per subsystem) is [`../spec/`](../spec/README.md). `ARCHITECTURE.md` is the overview (layers, rules, decisions); these docs say *how* each part works. The research behind them is in `docs/research/engine-survey.md`, the plan that produced them in `docs/ENGINE_DESIGN_PLAN.md`, and the build tasks in `TODO.md`. What building the vertical slice on top of these docs actually taught us — what is paying for itself, and where the debt is — is in [`../history/engine-review-2026-09-23.md`](../history/engine-review-2026-09-23.md); several docs below carry a pointer to the part of it that is about them. The readiness review — what is strong, what is missing before a game ships, and what has never been measured — is [`../history/readiness-2026-09-24.md`](../history/readiness-2026-09-24.md). The retrospective written when the slice **closed** — what it cost, what the design got right, and the debt the last features exposed — is [`../history/vertical-slice-2026-09-23.md`](../history/vertical-slice-2026-09-23.md).

Status: designed 2026-09-22 and largely built since. Each doc's "As built" sections (usually in §3, sometimes their own numbered heading) say what exists; the rest is design. For what comes next, read [`../REDESIGN.md`](../REDESIGN.md).

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
| [15-editor](15-editor.md) | Editor documents, command log, undo, inspector; for designers, [EDITOR.md](../EDITOR.md) | short |
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

## Writing an "As built" section (R19)

Each doc's "As built" sections are the ones that claim something is *true now*, and three features in a
row ended with one of them being wrong while the code was right. Two rules keep them honest, and
`tools/check_docs.py` enforces both:

- **A claim about behaviour cites the test that proves it**: `(test: WalkingIntoATriggerVolumeFiresOnStartTouch)`,
  or a comma-separated list of them. The script checks each name resolves to a test in `tests/`. Writing the citation is
  the point — it is where you notice you have no test to cite.
- **A command, cvar or record named in such a section has to exist.** Elsewhere — an API sketch, a "Not
  yet" list, an open question — naming what does not exist yet is the job, and those are left alone.

Numbers about the engine (how many commands, records, tests) go on a line marked `<!-- counts -->`, and
the script computes them: `--fix` writes them, so they are never typed. A marker with arguments does the
same for facts about the repository — `<!-- counts: files games/Hello, code games/Hello -->` maintains
"four files" and "about fifty lines of code" on that line, in whichever form the sentence uses. An
approximate number ("about fifty") passes within a tenth; when the true value has no word for it, the
script reports rather than writing a numeral into the middle of a sentence. Run it beside the build:

```bash
python tools/check_docs.py --tests N      # N as `dotnet test` reported
python tools/check_docs.py --self-test    # checks the checker still catches things
```

The names and counts come from the registry dump the host writes with `-dump-registry` (README,
"Building and testing"; `user/registry.json` by default, `--registry` for another), not from the
source: the self test plants a name and counts only a dump has, and fails if they are reported.

## Glossary (shared names — use these exactly)

### Host and core
| Name | Meaning | Doc |
|---|---|---|
| `Engine` | Process-wide core services object: cvars (`CVars`), core cvars (`Core`), VFS, `RecordStore`, input actions (`Actions`), modules and worlds today; `AssetServer`, jobs and engine signals join in later steps. Created by the host, passed in (never a static singleton). Lives in `Sage.Simulation`, so it has no MonoGame types. Client services (renderer, input devices, audio) are provided by client modules through `ModuleContext.Provide` | 01 |
| `IModule` / `ModuleKind` | A logical engine or game unit with dependencies and `Init` (register) → `Start` (use records/GPU) → `OnWorldCreated` (per world) → `Shutdown`. Kinds: `Runtime`, `Editor`, `Tool` | 01 |
| `ModuleContext` | What a module gets in `Init`/`Start`: `Engine`, `Get<T>` (services from the host or declared dependencies only), `Provide<T>` | 01 |
| `IGameModule` | The single entry point a game assembly implements (exactly one public class per game assembly) | 01 |
| `game.json` | Game manifest: name, id (= record namespace), game assembly, mounts, modules to disable; later default map and mods folder | 01, 05 |
| `ClientModule` / `ContentService` / `Renderer` | The default client module (record types, engine input actions, per-world snapshot, audio mixer and Extract/Render systems), its interim VFS-backed loader for models, effects and textures (until the `AssetServer`), and the renderer service | 01, 05, 06 |
| Build configurations | `Debug`, `Development`, `Shipping` (compile-time). "Dev builds" = `Debug` + `Development` (`SAGE_DEV`). There is no runtime dev mode | 01, 02 |
| `developer` | `DevOnly` cvar that sets defaults (log verbosity, hot reload, `dev_override/`); each feature still has its own cvar | 01, 02 |
| `DevOnly` | Cvar/command flag: compiled only into dev builds | 02 |
| `Log`, `LogCat` | Logging API and categories | 02 |
| `CVar`, console command | Named, flagged runtime variables and commands | 02 |

### Simulation
| Name | Meaning | Doc |
|---|---|---|
| `World` | One simulation: entities, schedules, event queues, physics space | 03 |
| `EntityRef` | Handle to an entity in a `World`; detects stale (deleted) entities. **Realised as Sage's `Entity` struct**, over Friflo.Engine.ECS's (in code: `Entity`; issue #25) | 03 |
| `PersistentId` | Stable id for placed or saved entities, used by maps, saves, quests | 03, 09 |
| Component | Plain data attached to an entity: a struct implementing Sage's `IComponent` (tags: `ITag`), with a stable id. No behaviour, no service references | 03 |
| System | Code that runs over queries in a schedule phase | 03 |
| `EntityCommands` | Deferred structural changes (despawn, add/remove component or tag, parent): `World.Commands` / `SystemContext.Commands`, over Friflo's command buffer, applied at the end of every phase (and by `World.FlushCommands`). Not a `ConsoleCommand` (typed at the console) nor a `PlayerCommand` (a tick of input) | 03 |
| `Query<…>` | The entities with some components (and tags), walked chunk by chunk: Sage structs over Friflo's archetype queries | 03 |
| `Schedule.Fixed` | Runs at a fixed rate, `sim_tickrate` (default **60 Hz**). Gameplay, physics, AI | 01, 03 |
| `Schedule.Frame` | Runs once per rendered frame. Camera, cosmetics, extract, render, UI | 01, 03 |
| Fixed phases | `Commands → PrePhysics → Physics → PostPhysics → Gameplay → AI → Animation → EntityIO → Late` | 03 |
| Frame phases | `FrameUpdate → Extract → Render → Overlay` | 03, 06 |
| `Transform` | Local position/rotation/scale, relative to the parent (or to the root's sector) | 03 |
| `Attributes` / `GameplayTags` | Components: an entity's attribute values (health, mana, armour…) and its tag bitset (256 tags since issue #28) | 16 |
| `damage_type` / `attack` | Records: a kind of damage and the attribute that resists it; one swing's reach, arc, damage and timing | 16 |
| `Combat.ApplyDamage` / `Damaged` | The one damage pipeline (resistance → an effect on health) and the game event it sends, which the death seam reads to name a killer | 16, 04 |
| `GameEvents` / `EventReader<T>` | The world's event bus (`world.Events`) and a system's cursor into one queue of it: every event once, none missed | 04 |
| `Melee` | Component: what a character swings and how far through a swing it is; `MeleeCombatSystem` is the only thing that swings, for players and AI alike | 16 |
| `AnimationEvent` | A named moment in a clip ("hit"), sent as a game event; combat lands a blow on one | 12, 16, 04 |
| `prefab` / `world.Spawn` | The record describing a placeable thing (components + parts) and the one call that places it | 05, 09 |
| `IPrefabPart` / `ComponentSchema` | A named setup a plugin declares for a prefab (`[PrefabPart]`, its options are its fields), and components/tags addressable by stable id (`[Component("sage:…")]`, issue #16) | 03, 05, 09 |
| `effect` / `ActiveEffects` | The record describing a change (modifiers, duration, period, stacking, tags) and the instances running on an entity | 16 |
| `GameRules` | Per-world resource a game subclasses: `OnWorldStarted` (populate the world), `SpawnPlayer`, `OnEntityDied` (called for every `Died` event `EffectSystem` raises when health runs out, issue #26), `OnLoaded` (after a save load). Installed by the game's module in `OnWorldCreated`; `Engine.CreateWorld` installs `DefaultGameRules` if none was set, and calls `OnWorldStarted` once every module has seen the world | 16 |
| `AIState` / `ai_schedule` / `ai_profile` | An agent's conditions, schedule and current task; the record listing a schedule's tasks and interrupts; the record with its sight, melee and think tuning | 16 |
| `CharacterController` | Component: the kinematic capsule the engine moves (collide-and-slide, step-up, slopes, crouch, jump) | 10 |
| Controller / `Pawn` / `PawnIntent` / `PlayerControlled` | A `Controller` (player or AI) possesses a `Pawn` (a component marking a possessable body); both write the same `PawnIntent` component (move, look, actions) that movement, combat and interaction only ever read; `PlayerControlled` tags the local player's pawn | 16 |
| `movement_profile` | Record with speeds, acceleration, jump, gravity, slope and step limits | 10, 16 |
| `SageMath` | The engine's one angles/directions convention, stated once so movement, AI, sprites and the camera can't disagree: +Y up, an entity's front is its local -Z, yaw 0 faces -Z, positive yaw turns counter-clockwise seen from above | 03, 10 |
| `IPhysicsWorld` | World resource: the physics facade gameplay uses — bodies, named layers, raycast/sweep/overlap, trigger and contact events, debug draw — with no backend type in it (#30) | 10 |
| `PhysicsSpace` | The 3D backend behind `IPhysicsWorld`: one Bepu simulation per world, in `Sage.Physics3D` | 10 |
| `Collider` / `RigidBody` / `PhysicsBody` | The shape and layer, how it moves (static, kinematic, dynamic), and the backend's handle the engine manages. `Collider.Standing` builds a capsule anchored at the entity's feet via `Collider.Center` | 10 |
| `LayerMask` | Which collision layers a query or a contact considers | 10 |
| `World.AddCharacter` | Adds a `CharacterController` + `Pawn` + a `PawnIntent` seeded from the entity's facing + a standing capsule `Collider` + a kinematic `RigidBody`, so the sweep layer, collider layer and capsule height can't drift apart | 10, 16 |
| `GlobalTransform` | Computed `Pose Current` + `Pose Previous` relative to the world's origin sector; interpolated by Extract | 03, 06 |
| `ActiveCamera` | World resource: the camera position/rotation/fov/near/far the view is rendered from. The `CameraDirector` mirrors the screen's camera entity into it (the player's camera since #78, the editor's free camera since #81); with no camera entity it is left to whoever writes it. `world.TryGetMainView` gives the same view with its projection. `RigEnabled`/`DrivenByRig` are obsolete and inert (#81). Read by `CameraExtract` and by simulation code such as billboard facing | 03, 06, 16 |
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
| `SectorCoord` | `readonly record struct (X, Z)`: which **1024 m** sector of the exterior something is in, used as a key by `Terrain`/`TerrainSector`, not a component; today everything is in (0, 0) | 14, 03 |
| `Heightfield` / `Terrain` | A square grid of heights, and the world resource holding the loaded sectors (ground height for gameplay, chunk meshes for the client) | 14 |
| `MeshHandle` | A mesh the renderer built itself (terrain chunks, other procedural geometry): `Renderer.CreateMesh` returns one, GPU buffers live in the renderer, `MeshRenderer.Handle` holds it (0 = none). Renderer texture ids are plain `int`s; there is no texture handle type | 06 |
| `SpriteRenderer` / `sprite_sheet` | Billboard component, and the record holding a sheet's texture, direction groups, frames and animations | 06, 12 |
| `BillboardMode` | `Cylindrical` (turns about Y: characters, trees) or `Spherical` (faces the camera fully) | 06 |
| `SpriteAnimator` | Component with the playing clip index and its time, advanced by the simulation | 12 |
| `RenderItem` | One drawable in the snapshot: mesh, material, world matrix, sort key | 06 |
| `SpriteInstance` | One billboard sprite in the snapshot | 06 |
| Action | A named input (`MoveForward`, `Attack`) bound to keys/buttons by an input-map record | 08 |
| Input context | A layer (`Editor`, `UI`, `Gameplay`) that can consume input | 08 |
| `PlayerCommand` | One tick's worth of player intent (move, look, actions), sampled from actions | 08 |

### Log categories
`Core`, `Host`, `Modules`, `VFS`, `Assets`, `Records`, `Shaders`, `Render`, `Input`, `World`, `Events`, `Physics`, `Audio`, `Animation`, `UI`, `Streaming`, `Save`, `AI`, `Gameplay`, `Editor`, `Mods`, `Console` (console echo and command output). Games add their own (see 02).
