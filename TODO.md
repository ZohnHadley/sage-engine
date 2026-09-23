# Sage Engine — TODO

The working tracker: **open bugs in today's code** and the **roadmap**. Updated 2026-09-22.

| Where else to look | For |
|---|---|
| [`ARCHITECTURE.md`](ARCHITECTURE.md) | Overview, layers, decisions (D1–D13), migration order (§7) |
| [`docs/design/`](docs/design/00-index.md) | How each subsystem works; every roadmap item below links to its doc |
| [`docs/history/code-review-log.md`](docs/history/code-review-log.md) | Full history of review items #1–#52 (problems, fixes, resolution notes). Closed items live only there |

Legend: `[ ]` open · `[~]` partly done · `[X]` done. When an item is done, tick it here. Once a phase is finished, move its detail to the history log.

---

## Open bugs in today's code (`engine/`)

These are against the **current** single-project code. Many disappear when the migration (ARCHITECTURE §7) rewrites the code they're in; the **Plan** column says whether it's worth fixing now.

- **Fix now:** cheap, and useful while the current code keeps running.
- **Migration:** don't patch the current code; the named roadmap item replaces it.

| # | Issue | Where | Plan |
|---|---|---|---|
| [~] 41 | Per-frame allocations: **found** — the ImGui entity inspector builds a label string per listed entity per frame (~130 B each, so ~4 KB once terrain chunks are entities). With `ui_entities 0` the frame allocates only the ~40 B/tick that Bepu's own profiler allocates inside `Timestep` | `Sage.Editor/Screens/EntityContextMenuUI.cs`, BepuPhysics | Cache the inspector's labels or list only what's visible; Bepu's 40 B is out of our hands short of a custom build (02 §4.6) |

Paths are relative to `src/`. The full detail for each item is in the history log.

**#25, #37, #39 and #40 resolved by migration step 6 (renderer, materials, input actions), 2026-09-22.** **#26 resolved by the Sandbox game module (step 5), 2026-09-22.** **#15, #33, #34, #36, #38 resolved by the `World` rewrite (step 3), 2026-09-22.** **#24 (typos) resolved by the solution split, 2026-09-22.** **Quick "fix now" batch: done 2026-09-22.** #9 (light icon field + `light.png` removed), #21/#35 (`TransfomSystem.cs` deleted), #22 (`MouseButton` internal), #32 (scale applied), and the trivial part of #36. Details in the history log. Everything left above waits for the migration.

---

## Roadmap — engine features for the target games (revised 2026-09-22)

The structure these items fit into is in `ARCHITECTURE.md` (overview), with the details in `docs/design/*.md`. The research behind it is in `docs/research/engine-survey.md`, and the approved plan in `docs/ENGINE_DESIGN_PLAN.md`. Each item names its design doc.

**Decisions in effect:**
- multiplayer **later** (Phase 7), with readiness rules followed now (ARCHITECTURE §4.9);
- **Daggerfall-like first**;
- **data + trusted C# mods**.

### Target games and what each one demands

| Target | Defining engine needs |
|---|---|
| **Daggerfall-like** (first) | Billboard sprites (NPCs, creatures, foliage) with 8-direction sprite sets. Huge streamed world (terrain + procedural towns/dungeons). First-person melee + magic. Data-driven spells (spellmaker), skills that level by use, factions, quests. Many dormant entities. Co-op *(later)*. |
| **Mount & Blade: Warband-like** | Skeletal animation with blending (directional melee, blocking, mounted combat). Hundreds of AI agents in one battle. Horses/mounts. Overworld map with parties. Economy and life paths (merchant, farmer, hunter): production chains, markets, jobs as data. Sieges. Multiplayer battles *(later)*. |
| **Half-Life 1 / GoldSrc-like** | Brush/BSP-style indoor levels with lightmaps. Quake-style player movement (air strafing, stairs, crouch). Hitscan + projectile weapons. Entity I/O (triggers, `target`/`targetname`, scripted sequences). NPC AI with schedules + squad behaviour. Client-server multiplayer with prediction and lag compensation *(later)*. |
| **Lugaru-like** | Skeletal animation + ragdoll physics. Context-sensitive melee (reversals, knockdowns). Third-person camera. Heightmap terrain. Tight, responsive character controller. |

**Shared core** (needed by all four): fixed-timestep simulation, physics with a character controller, skeletal animation, audio, AI + pathfinding, save/load, data-driven content, and an editor that can build levels.

### Architecture items (build before or alongside the first features)

- [X] **R1. Replace singletons with `World` + `Engine`.** *Done 2026-09-22 (migration step 3). `Engine` (cvars, worlds) and the main `World` come from `Program`; `InputSystem`, `ModelRendererSystem` and the editor windows are instances. `Log`/`CrashReporter` stay static by design.* `EntityContext`, `EntityContextListener`, `ModelRendererSystem` and `InputSystem` are process-wide singletons. Several worlds are needed for Warband's overworld/battle split, the editor's edit/play worlds, level transitions and headless tests, and later for a server/client pair. `Entity.context` (#36) goes away. → `docs/design/01`, `03`
- [X] **R2. Fixed tick + render interpolation.** *Done 2026-09-22 (migration step 4).* Gameplay runs in a 60 Hz fixed schedule (`sim_tickrate`) with a clamped accumulator. Rendering interpolates between the previous and current poses. This is required for stable physics and deterministic animation events, and it's the tick any future netcode would index by. Today `Game1.Update` mixes input, camera and gameplay per frame. → `01 §5.2`, `03 §3.5`
- [X] **R3. Input as actions → `PlayerCommand`.** *Done 2026-09-22 (migration step 6): `InputDevices` (+ gamepad), `InputActions` with input_map records and consumption, `CommandLatch` → `PlayerInput` per tick, `Menu`/`ToggleConsole` actions. Left for later (08 §14 steps 4–5): the editor camera on Editor-context actions, `bind` + `user://input.json`, mouse capture.* Actions from input-map records, a context stack (Editor > Console > UI > Gameplay), a per-tick command with latched taps, and frame-rate look. Replaces `InputSystem.key_binds` (#36) and the direct reads (#37). Gamepad included. → `08`
- [X] **R4. Data-only components, logic in systems.** *Done: struct components + `TransformMath` (step 3); `ISystem`, phases, ordering, per-phase command buffers (step 4). Access declarations come with the event bus.* Components are plain data. `LookAt`/`Billboard` move to `TransformMath`, and billboarding moves to the renderer (#26, #35). A system registry with phases, ordering and access declarations; command buffers. → `03`
- [X] **R5. Typed queries + the ECS spike.** *Done 2026-09-22 (step 3): Friflo.Engine.ECS 3.6.0 adopted after passing E1–E9 (`docs/design/03` §3.1).* Replace string-keyed groups/`ArchetypeView` with typed queries (#38). Spike **Friflo.Engine.ECS** against requirements E1–E9 (ARCHITECTURE D4). → `03 §3.1`
- [ ] **R6. Large-world coordinates.** `SectorCoord` (1024 m sectors) + a local `Transform` in the simulation. A world `Origin` rebased around the player (with hysteresis). Camera-relative rendering. Interiors as separate spaces. This is the Daggerfall Unity model, kept compatible with a future multi-player server. → `03 §3.6`, `14`
- [X] **R7. Content as data.** Superseded by **R11** (one record pipeline for all definitions); the first content as data is the Sandbox scene (`spawn` records, step 5).
- [X] **R8. Two-level module system.** *Done 2026-09-22 (migration step 5): `IModule`/`IGameModule`, `ModuleManager` (dependency sort, dependency-checked `Get<T>`, lifecycle), `ClientModule`, `game.json`, `games/Sandbox`. `Sage.Framework*` and `Sage.Generators` get created when there is code for them.* csproj boundaries only at layers (`Sage.Engine`, `Sage.Client`, `Sage.Framework`, `Sage.Framework.Client`, `Sage.Editor`, `Sage.Host`, `Sage.Generators`). Logical `IModule`s with dependencies and `Init` (register) / `Start` (use) / `Shutdown`; `game.json`. → `01`
- [~] **R9. Subsystem handles + Extract phase.** *Extract + pooled `RenderSnapshot` + the renderer owning meshes by id: done 2026-09-22 (migration step 6). Physics and audio handles come with those subsystems.* Heavy subsystems (renderer, physics, audio) own their data, and components hold handles. An Extract phase builds a pooled `RenderSnapshot` from interpolated, camera-relative poses. → `06`, `10`
- [X] **R10. Build configurations + console + logging.** *Done 2026-09-22 (steps 2 and 4; the profiler landed with the tick phases in step 4).* `Debug`/`Development`/`Shipping` (no separate runtime dev mode): `developer` cvar as a defaults setter, `DevOnly` = compiled into dev builds only, restricted Shipping console via `con_enable`. `Log` with categories/levels/sinks (zero-cost when off); `Assert.Dev/Ensure/Check`; crash reports; cvars/console; the profiler + allocation counter. Replace the existing `Console.WriteLine` calls (#34, #36). → `02`
- [~] **R11. Unified data-record pipeline.** *v1 done 2026-09-22 (migration step 5): VFS, `RecordStore` with namespaces, `base`/`abstract`, patch merge, validation, hot reload, `rec_*` commands (05 §3.6). Left: generated readers/validators (09), range checks, typed references, `RecordRef<T>`.* JSON records for *every* definition (items, spells, materials, input maps, sounds, prefabs…): namespaced `RecordId`s, `base` inheritance, per-field patch merge in load order, validation generated from `[Record]` schemas, hot reload. → `05 §3.5`, `09`
- [ ] **R12. MonoGame 3.8.2 → 3.8.5.x upgrade, stay on DesktopGL.** Set `GraphicsProfile.HiDef`. Re-evaluate DesktopVK later (ARCHITECTURE D13). Remove the MGCB dependency: runtime asset loaders + `dotnet-mgfxc` for shaders (fixes #9). → `05`, `07`

### Feature phases

Each phase builds on the previous one. Items marked **(v1)** are part of the first milestone below.

#### Phase 1 — Core runtime
- [~] **F1. Sprite/billboard renderer (v1).** *Done 2026-09-22 (the first slice feature): `sprite_sheet` records (texture, direction groups, frames with pivots, animations), `SpriteRenderer`/`SpriteAnimator`, direction selection + frame choice at extract, CPU-batched camera-facing quads (one draw per material+texture run), cylindrical and spherical modes, `sprite.fx` with Unlit/Lit/UnlitBlend. Left: 5-direction mirrored art in practice (the code handles it), frame events (needs 04), `anim_debug`, atlas packing with the AssetServer (R12).* CPU-batched camera-facing quads from atlases; cylindrical/spherical modes; 8-direction selection (5 + mirroring supported); sprite animation with frame events. → `06 §3.7–3.8`, `12`
- [~] **F2. Custom shaders and materials (v1).** *Done in step 6: `common.fxh`, `lit.fx` (Default/AlphaTest/Unlit), `error.fx`, the mgfxc build, material records + `MaterialCache`, sun + hemispheric ambient + fog. Left: `sprite.fx` (with F1), point lights, shader hot reload.* `lit.fx`, `sprite.fx`, `common.fxh`; techniques as variants; material records; fog, hemispheric ambient, up to 4 point lights; later lightmaps (HL1) and skinning (F9). → `07`
- [~] **F3. Camera modes (v1: first-person + editor free-fly).** *First-person rig done with F7 (`FirstPersonCameraSystem`, `cam_free` to fly the editor camera instead). Left: the camera as a proper rig with modes, and third-person orbit with collision.* First-person rig; third-person orbit with collision later (Lugaru, M&B). `DevCamera` becomes the editor rig. → `08`, `15`, `16`
- [ ] **F4. Audio.** `sound` records, event-driven playback, buses, `AudioSource`, voice limiting. → `11`
- [~] **F5. Debug overlays (v1 minimal).** *`stat fps` / `stat mem` (step 2), `stat frame` (step 4) and `r_stats` (step 6) done; `stat render`, `phys_debug`, `ai_debug` come with their systems.* `stat` overlays (frame, phases, memory), `r_stats`, `phys_debug`, `ai_debug`; later the visual logger. Properly replaces the overlays removed in review items #7/#8 (history log). → `02`, `06`

#### Phase 2 — Physics and movement
- [~] **F6. Physics layer (v1).** *Done 2026-09-22: BepuPhysics 2.4 per world through `PhysicsModule`, `Collider`/`RigidBody`/`PhysicsBody`, box/sphere/capsule/mesh shapes, terrain collision meshes, raycast/sweep/overlap with layers, trigger overlaps, `phys_stats`, deterministic stepping. Left: trigger and contact *events* (needs 04), `Collided`, `phys_debug` (needs `DebugDraw`), parented colliders, ragdolls (F11).* BepuPhysics v2 per world; colliders/rigid bodies via handles; raycasts/sweeps/overlaps; triggers → events; layers. → `10`
- [X] **F7. Kinematic character controller (v1).** *Done 2026-09-22: capsule with collide-and-slide, step-up, slope limit with downhill sliding, ground snapping, crouch with a headroom check, jump and air control, all tuned by `movement_profile` records; `PawnIntent` and the player controller feed it, and a first-person camera rig sits in its head. Left: depenetration when something overlaps it, moving platforms, the GoldSrc air-strafe profile.* Capsule, collide-and-slide, step-up, slopes, crouch, jump; `movement_profile` records; GoldSrc air-acceleration profile later. → `10`, `16`
- [ ] **F8. Mounts.** Rideable-entity controller, rider attachment, speed-based bonus damage. → `10` (later)

#### Phase 3 — Animation
- [ ] **F9. Skeletal animation pipeline.** glTF skins/clips via SharpGLTF; sim-side pose sampling; GPU skinning technique. → `12`
- [ ] **F10. Animation blending + state machine.** Crossfades, layers, blend spaces, directional attack/block sets. → `12`
- [ ] **F11. Ragdolls + physical animation.** Ragdoll on death/knockdown, blend back to animation. Depends on F6. → `10`, `12`
- [ ] **F12. Attachment points.** Weapons, shields and riders on bones. → `12`

#### Phase 4 — World
- [X] **F13. Heightmap terrain (v1: one sector, no LOD).** *Done 2026-09-22 (collision landed with F6): `Terrain` world resource, `Heightfield` (129×129 per 1024 m sector), game-provided `ITerrainGenerator`, chunk meshes through `Renderer.CreateMesh` + `MeshHandle`, `sage:terrain_default`, and `HeightAt`/`OnGround` for standing on the ground. Collision is one static Bepu mesh per sector (F6). LOD, splat materials and streaming are F14.* 129×129 grid per 1024 m sector, game-provided `ITerrainGenerator`, meshes + collision; LOD and splat materials later. → `14`
- [ ] **F14. World streaming.** Streaming rings around the player (Daggerfall Unity model), per-sector asset scopes, dormancy into the save cache, origin rebasing. Depends on R6. → `14`
- [ ] **F15. Procedural generation.** Seeded towns, dungeons and wilderness; deterministic from the seed so saves store only visited sectors. → `14`, `09`
- [ ] **F16. Brush/level geometry.** TrenchBroom `.map` import (+ FGD export from component metadata); lightmaps later. → `15`
- [ ] **F17. Entity I/O (v1 minimal).** `[Output]`/`[Input]`, connections resolved and type-checked at map load, delays, `!self`/`!activator`, `ent_fire`, `io_trace`. → `04 §3.4`

#### Phase 5 — Gameplay framework
- [X] **F18. Attributes, effects, tags (v1 minimal).** *Done 2026-09-22: `attribute`/`tag`/`effect` records, id registries, `Attributes`/`GameplayTags`/`ActiveEffects` components, `EffectSystem` (durations, periods, stacking, granted tags, recomputed values), tag gating with the `god` cheat, and the death seam calling `GameRules.OnEntityDied`. The creature's claws now take the player's health. Skill progression and the damage pipeline's resistances come with F20/F21.* GAS-like: `Attributes`, `GameplayTags`, `effect` records, `ActiveEffects` + `EffectSystem`; skill progression later. → `16 §3.3`
- [ ] **F19. Inventory and items (v1 minimal).** `item` records, `Inventory` component, equipment slots, pickup via interaction. → `16`
- [ ] **F20. Combat (v1 minimal).** Damage pipeline, resistances, physics-query hit detection, sprite melee on "hit" animation events; directional melee/reversals later. → `16`, `12`
- [ ] **F21. Abilities and magic (v1: fireball).** `ability` records, costs, cooldowns, cues; a spellmaker composing effects into saved custom abilities. → `16 §3.3`
- [~] **F22. AI (v1: one melee creature).** *Done 2026-09-22: conditions, `ai_schedule` records, named tasks (Wait, FaceTarget, MoveToTarget, MeleeAttack), `ai_profile` tuning, sight + line-of-sight perception, staggered thinking, raycast steering; the creature drives `PawnIntent`, so it walks with the player's controller. Left: hearing, damage on its hits (F20), utility/BT selection, squads and formations.* HL1-style schedules/tasks/conditions, staggered think, perception; utility/BT and formations later. → `16 §3.4`
- [ ] **F23. Pathfinding.** Navmesh for interiors/battlefields; a coarse overworld graph. → `16`
- [ ] **F24. Factions, reputation, dialogue and quests.** Records + entity I/O + events. → `16`
- [ ] **F25. Economy and life paths.** Production chains, markets, professions as data; coarse offline simulation (ties into F14 dormancy). → `16`
- [ ] **F26. Overworld / party layer.** A second world type (depends on R1). → `16`
- [ ] **F27. Save/load (v1).** Generated serializers; visited-sector rule; tombstones; saved resources; tagged binary; temp + rename; no behaviour state. → `09`

#### Phase 6 — Editor
- [ ] **F28. Editor host + inspector (v1 minimal).** Separate host; documents; outliner; editable inspector generated from metadata; wire up the File → New/Open/Save stubs. → `15`
- [ ] **F29. Gizmos + picking (v1: translate).** → `15`
- [ ] **F30. Undo/redo via the command log (v1).** → `15`
- [ ] **F31. Prefabs.** Prefab records with overrides; "revert to prefab" UI. → `05`, `09`, `15`
- [ ] **F32. Asset/record/shader hot reload (v1 for folders).** → `05`, `07`

#### Phase 7 — Multiplayer (later)
Not built now. The readiness rules (ARCHITECTURE §4.9) keep it from being a rewrite. Research and design notes: `docs/research/engine-survey.md` §5 and each design doc's "Multiplayer-later notes".
- [ ] **F33. Transport.** LiteNetLib (study LiteEntitySystem as a reference).
- [ ] **F34. Authoritative server + snapshots.** Delta-compressed against each client's last acknowledged snapshot (Quake 3); `[Replicated]` metadata + quantization.
- [ ] **F35. Client prediction + reconciliation** for the local player's character controller only, plus **server-side lag compensation** for hitscan.
- [ ] **F36. Interest management.** Relevancy/priority per client (Unreal Replication Graph ideas).

#### Modding
- [ ] **F37. Mod loading (v1 basics).** `mods/<id>/mod.json`, `user://mods.json` load order with a topological sort, VFS mounting, record patches, conflict report (`mod_conflicts`), mod list in saves; trusted C# mod assemblies later. → `17`

### Suggested first milestone — one vertical slice (Daggerfall-like)

> A first-person player walks on one heightmap terrain sector with a few billboard-sprite trees. One billboard-sprite creature chases the player and attacks in melee. The player can swing a weapon, cast one data-defined fireball, pick up an item, then save and reload.

This needs R1–R6 and R8–R12, plus the **(v1)** parts of F1–F3, F5–F7, F13, F17–F22, F27–F30 and F32. It leaves out skeletal animation and networking, the two biggest items, while keeping the architecture ready for both. Build it by **alternating** infrastructure and features (ARCHITECTURE §7).

### Already present
- **Input devices:** `KeyboardListener` + `MouseListener` (edge events, polling, exact-match chords, threshold-gated drag). They become the device layer of R3 (`08 §3.1`).
- **World / ECS (step 3):** `Engine` + `World` over Friflo.Engine.ECS, typed queries, command buffer, structural notifications with the old ordering guarantees, `PersistentId`, resources, hierarchy.
- **Look-at / billboard math:** `TransformMath.LookAt`/`Billboard` (#30/#31). Sprite facing moves to the renderer (F1).
- **Core services (step 2):** logging, asserts, crash reports, cvars/console, build configurations.
- **Main loop (step 4):** fixed tick + interpolation, schedules/phases, profiler, `stat frame`, `sys_list`, `pause`, `host_exitafter`.
- **Rendering, materials, input (step 6):** Extract → `RenderSnapshot` → `Renderer`; material records and our own shaders; input actions/contexts → `PlayerCommand`; `r_stats`, `mat_list`/`mat_info`, `bindlist`, `in_tap`, `screenshot`.
- **Sprites (F1):** `sprite_sheet` records, billboards with 8 direction groups, sprite animation at the tick rate, the CPU sprite batcher; `cam_set` and `screenshot [delay]` for repeatable viewpoint checks.
- **Terrain (F13):** `Terrain`/`Heightfield`/`ITerrainGenerator` per world, chunk meshes from `Renderer.CreateMesh`, ground-height sampling; the Sandbox generates hills and stands its scene on them.
- **Physics (F6):** BepuPhysics per world via `PhysicsModule`, colliders and rigid bodies behind handles, terrain collision meshes, raycast/sweep/overlap with layers, trigger overlaps, `phys_stats`; the Sandbox drops crates through a trigger onto the hills.
- **Character and camera (F7, part of F3):** `GameplayModule` with `PawnIntent`, the player controller, the kinematic character controller and the first-person rig; `cam_free` and the Sandbox's `sandbox_autowalk` for automated checks.
- **Rules and AI (16, F22):** `GameRules` started by `Engine.CreateWorld`, HL1-style schedules/tasks/conditions with sight perception and raycast steering; the Sandbox's creature chases the player and swings at it.
- **Attributes and effects (F18):** attribute/tag/effect records, `EffectSystem` with durations, periods, stacking and granted tags, tag-gated application (`god`), and death reported to the rules; the Sandbox's player loses health to the creature and respawns.
- **Consistency pass over steps 1-19 (2026-09-22):** one facing convention for the whole engine (`SageMath`: front is local -Z, yaw 0 faces -Z) replacing the sprite system's +Z and the `+PI` the character controller used to bridge them; `Collider.Center`/`Collider.Standing`, so a character's or a tree's capsule stands where the art does instead of being half buried; `World.AddCharacter`; named physics layers; AI thinking in `Phase.Commands` (same-tick intent) and checking its sight cone. Review items #43-#52.
- **Modules, VFS, records (step 5):** `IModule` + `game.json` + the `games/Sandbox` game module; VFS mounts with shadowing; the `RecordStore` (patch merge, inheritance, validation, hot reload); `modules`, `vfs_which`/`vfs_ls`/`vfs_mounts`, `rec_list`/`rec_get`/`rec_reload`.
