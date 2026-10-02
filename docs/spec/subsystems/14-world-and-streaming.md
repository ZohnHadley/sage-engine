# 14 · World, streaming and time

> Status: built and tested for the 4g exit game; streaming is synchronous and has no LOD, and the calendar and time services are minimal. Owning assemblies: `Sage.Simulation` (`World`, `Levels`, `Content`), `Sage.Physics3D` (terrain collision), `Sage.Gameplay` (off-screen simulation), `Sage.Client` (terrain meshes). Design doc: [14-world-streaming](../../design/14-world-streaming.md).

## 1. Purpose and scope

The world subsystem makes an unbounded, large or interior-heavy game possible with small float numbers and a bounded cost per tick. It does this with a grid of 1024 m sectors, a streaming ring around the player, an origin that follows the player, cells that go dormant with their state, interiors behind load doors, fast travel, and a game clock with a calendar.

It deliberately does not do the following. Entity storage, scenes and prefabs are [04-ecs-and-scenes](04-ecs-and-scenes.md). Save writing and loading are [15-saves](15-saves.md), though dormancy reuses the save entry format. Terrain drawing is [07-rendering](07-rendering.md) and terrain collision bodies are [08-physics](08-physics.md). NPC routines and the off-screen fight rule are [09-navigation-and-ai](09-navigation-and-ai.md); this sheet covers only the seam they stand on. Rest rules and map screens are the RPG kit's, [17-rpg-kit](17-rpg-kit.md).

## 2. Responsibilities

- `SectorCoord` addressing, sector-local root transforms, the `Origin` resource and rebasing of every position-holder between ticks.
- The streaming ring: load within `stream_radius`, unload past `stream_radius + 1`, rebase past 1.5 sectors, all with hysteresis.
- Built-in terrain: heightfields of 129 by 129 samples, the `terrain` record and its `Flat` and `Hills` generators, and the `ITerrainGenerator` seam for a game's own.
- Streamed scenes: placements, placements documents and `.map` levels bucketed by sector, placed within a per-tick budget at the tick boundary.
- Cells: dormancy with state, runtime spawns owned by a cell, waking, absolute positions across rebases.
- Interiors (`"space": "interior"`), load doors, travel points, `Travel.To` and `travel`.
- The world clock, calendar record, `Time.Pass` skips and the `TimePassed` event.
- The `ICellHandoff` seam for what keeps simulating while a cell sleeps.

Not responsible for: LOD past the ring, HLOD, asset scopes per sector (open, #277), several streaming sources (#290), interiors as separate spaces (#291).

## 3. Placement and dependencies

Sector, origin, terrain, travel and clock code is in `src/Sage.Simulation/World`; cells, scenes and the streamed scene are in `src/Sage.Simulation/Content`; map levels are in `src/Sage.Simulation/Levels`. It sits in the simulation assembly with no MonoGame. `Sage.Physics3D` references it for `TerrainCollisionSystem`. `Sage.Gameplay` references it for `OffscreenSystem` (`src/Sage.Gameplay/World/Offscreen.cs`). `Sage.Client` consumes `Terrain` to build chunk meshes and subscribes to `Origin.Rebased`. Kits never appear in this code.

Plugins: `sage.streaming` (`StreamingModule`) installs the `Terrain` resource and the ring, so a world without it has no sectors; the records `scene`, `terrain` and `calendar` and the parts `load_door` and `travel_point` register under the core owner; `sage.gameplay.ai` owns the `offscreen` part and table. Most of this area is `[Experimental("SAGE0129")]`.

## 4. Interfaces

| Type | File | Role |
|---|---|---|
| `SectorCoord` | `World/Terrain.cs` | Absolute sector address (X, Z); sectors are 1024 m. |
| `Terrain`, `Heightfield`, `ITerrainGenerator` | `World/Terrain.cs` | Per-world ground: `Load`, `Unload`, `HeightAt`, `NormalAt`, `OnGround`, origin-aware. |
| `Origin` | `World/Origin.cs` | The origin sector, `ToAbsolute`/`ToOrigin`, event `Rebased(offset)`; `world.Rebase(sector)`. |
| `StreamingSource` | `World/Streaming.cs` | Tag on what the ring follows (the player). |
| `StreamingSystem`, `SectorRing`, `SectorOwnersSystem` | `World/Streaming.cs` | The ring, and crossing-an-edge ownership (both Phase.Late). |
| `Scenes`, `StreamedScene` | `Content/Scenes.cs`, `StreamedScene.cs` | Place and clear scenes; `Current(world)`; the tick-boundary placement. |
| `Cells`, `InCell`, `ICellHandoff` | `Content/Cells.cs`, `CellHandoff.cs` | Dormant store, cell ownership, the hand-off seam. |
| `Travel`, `TravelLog` | `World/Travel.cs` | `To`, `ToPoint`, `Use`, `HoursTo`; the saved list of discovered points. |
| `WorldClock`, `Time` | `World/WorldClock.cs`, `World/Time.cs` | Day and hour, scale; `Time.Pass(world, hours, reason)`. |
| `CalendarRecord` | `World/Calendar.cs` | Months, weekdays, start year; `WorldClock.Date`. |

Console and cvars: `warp <x> <z>`, `stream_status`, `stream_radius`, `stream_enabled`, `stream_place_budget`, `travel <point>`, `travel_speed`, `scene_load`, `time`, `time_set`, `time_pass <hours>`, `offscreen_status`. Inputs: the `Travel` input goes through a door, or takes `"<scene> <entry>"` sent to any entity; the Use action opens a `load_door`. Events: `Origin.Rebased`, `TimePassed(Hours, Reason, FromElapsed, ToElapsed)`, and `OffscreenDied`.

## 5. Data model

| Id | Kind | Notes |
|---|---|---|
| `scene` | record | `streamed`, `space` (`Exterior` or `Interior`), `terrain` reference, `environment`, placements, levels. |
| `terrain` | record | `Flat` or `Hills`, with `seed`, `height`, `amplitude`, `wavelength`. |
| `calendar` | record | `Months` (name, days), `Weekdays`, `StartYear`, `StartWeekday`. No leap years by default. |
| `sage:cell` | component (`InCell`, saved) | `Source`: `scene:<id>` or `sector:<scene>:<x>,<z>`. |
| `sage:sector_owned` | component | Marks what a sector brought (terrain chunks, collision body). |
| `sage:load_door` | component and part `load_door` | `scene`, `entry`, `hours`. Not saved. |
| `sage:travel_point` | component and part `travel_point` | `label`, `radius`, `discovered`. |
| `sage:offscreen` | component and part `offscreen` | `speed`, `strength`, `corpse`, `fight`. |
| `clock` | saved resource | `Day`, `Hour`, `Scale`, `Sky`, `Calendar`. |
| `travel` | saved resource | The `TravelLog`: discovered points with scene and absolute place. |
| `offscreen` | saved resource | The table of agents that are simulated while their cell sleeps. |

Dormant cells are written in the save under `dormant`, by source, each with its own origin frame (save format 4, see [15-saves](15-saves.md)).

## 6. Lifecycle and data flow

At the fixed tick: `sage.world.clock` (Phase.Commands) advances the clock; `sage.ai.offscreen` (Commands, before AI think) catches up the off-screen agents to the clock; `sage.streaming.sectors` and `sage.streaming.owners` (Phase.Late) keep the ring, rebase, and re-own roots that crossed an edge; `sage.world.travel_points` (Late) discovers points. After the fixed phases, at the tick boundary, `Scenes.TickEnded` unloads sectors that left the ring and places those that entered, nearest first, within `stream_place_budget` (default 64 entities a tick); `Travel` and `Time.Pass` requests also run here, so no system sees a scene or hour change under it.

A rebase moves every root, both poses of every `GlobalTransform`, every physics body and static, and the cameras at once, then raises `Origin.Rebased`. Order for travel and `warp` is rebase, generate ground, then place the player.

Going dormant writes live entities and the cell's runtime spawns as save entries, destroys them, and keeps the entries per source; waking lays them back on the placed content by stable id. Off-screen agents are taken out of the cell by `ICellHandoff` before it is written, and come back when their scene and sector are live. A time skip steps every game minute of the skip for off-screen agents. Save and load: the clock, travel log and off-screen table are saved resources; cells are the `dormant` section.

## 7. Threading, memory and performance

All of it runs on the main thread; terrain and sector generation is synchronous, which is the known source of a stall at a sector edge (REQ-PERF-05 in the [SRS](../SRS.md); generation on jobs is #277). A streamed scene at rest allocates nothing per tick (test: `AStreamedSceneAtRestAllocatesNothingPerTick`), and an off-screen step allocates nothing (test: `FiveHundredAgentsStepWithoutAllocating`). Placement is budgeted so a sector arrives over several ticks (test: `PlacingASectorIsBudgetedAtTheTickBoundary`).

## 8. Errors and diagnostics

A door to a missing scene or entry, with the nearest name, and a placed door with no scene are load errors, so `sage validate` reports them (test: `ADoorToAMissingSceneOrEntryIsALoadError`). `Time.Pass` warns and refuses a non-positive, non-finite or over-large amount, or a world with no clock. Log categories are `Streaming` and `World`. Debug with `stream_status` (origin, placed sectors, rebases), `offscreen_status`, `time`, and `warp`.

## 9. Requirements

| ID | Requirement | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-WORLD-01 | The world shall be an unbounded grid of sectors with sector-local transforms and an origin that follows the player, moving every position-holder between ticks. | Must | Done | `src/Sage.Simulation/World/Origin.cs`; test: `PhysicsBodiesRebaseWithTheWorld` |
| REQ-WORLD-02 | A streaming ring shall load and unload sectors with hysteresis and a per-tick placement budget. | Must | Done | test: `PlacingASectorIsBudgetedAtTheTickBoundary` |
| REQ-WORLD-03 | A game with no C# shall stream built-in seeded, seamless terrain. | Must | Done | test: `TheDataOnlyWorldStreams`, `BuiltInTerrainIsSeamlessAndSeeded` |
| REQ-WORLD-04 | Entities shall stream by sector and go dormant with their state, runtime spawns and tombstones. | Must | Done | test: `WalkingThreeSectorsEastPutsWhatIsBehindToSleepAndBringsItBack` |
| REQ-WORLD-05 | A root crossing a sector edge shall change owner without being placed twice. | Must | Done | test: `ARootThatCrossesASectorEdgeChangesOwner` |
| REQ-WORLD-06 | Dormant state shall survive a rebase, a save and a load. | Must | Done | test: `ADormantCellSurvivesARebaseAndASaveAndLoad` |
| REQ-WORLD-07 | Interiors, load doors and a door-travel path shall exist, with the exterior intact on return. | Must | Done | test: `UsingTheDoorPutsYouAtTheCryptEntryWithTerrainUnloaded` |
| REQ-WORLD-08 | Fast travel shall cost game time, require discovery, and land on generated ground. | Must | Done | test: `FastTravelAdvancesTheClockAndLandsOnTheGround` |
| REQ-WORLD-09 | A time skip shall run at a tick boundary and never tick the simulation. | Must | Done | test: `PassingTimeNeverTicksTheSimulation` |
| REQ-WORLD-10 | A calendar shall give months, weekdays and years from the clock. | Must | Done | test: `MonthsAndYearsWrapInACustomCalendar` |
| REQ-WORLD-11 | The reference open-world game shall run headless: dungeon trip, a day passes, NPC keeps its schedule, nothing doubled. | Must | Done | test: `OpenWorldExit_AwayADayInTheCrypt_TheSmithKeptHisScheduleAndNothingIsDoubled` |
| REQ-WORLD-12 | NPCs shall keep routines and fight off-screen, deterministically, with no per-step allocation. | Should | Partial: straight-line movement, only after a cell first sleeps | test: `TwoHostileSquadsOffscreenFightItOutTheSameWayEveryTime`; #284 |
| REQ-WORLD-13 | Streaming shall not stall a frame: generation on jobs, LOD and HLOD past the ring, per-sector asset scopes. | Must | Not started | #277 |
| REQ-WORLD-14 | Prefabs shall nest and keep per-placement overrides across sectors. | Should | Not started | #279 |
| REQ-WORLD-15 | The world shall offer a time scale, pause and hit-stop as services. | Should | Not started | #283 |
| REQ-WORLD-16 | The calendar shall add seasons, moon phases, leap years and scheduled events. | Could | Not started | #289 |
| REQ-WORLD-17 | There shall be several streaming sources, each with its own ring. | Could | Not started | #290 |
| REQ-WORLD-18 | Interiors shall be separate spaces, and companions shall follow through doors. | Could | Not started | #291 |

## 10. Open work

Milestone 2 (epic #274).

- #277 4m-3 Streaming: LOD/HLOD past the ring, per-sector asset scopes, generation on jobs (P1)
- #279 4m-5 Prefabs: nested prefabs, per-placement part overrides, overrides kept across sectors (P1)
- #278 4m-4 Saves: remove live unsaved entities on load, and the remaining "Limits" (P1, shared with [15-saves](15-saves.md))
- #283 4m-9 Time scale, pause and hit-stop as world services (P2)
- #284 4m-10 Off-screen simulation: pathing, unvisited cells, live NPCs with far anchors (P2)
- #289 4m-15 Calendar and clock: seasons, moon phases, leap years, scheduled calendar events (P3)
- #290 4m-16 Several streaming sources and per-source rings (P3)
- #291 4m-17 Interiors as separate spaces and companions through doors (P3)

Related: #308 4n-4 AssetServer scopes and memory budget (P1), #307 4n-3 terrain splat materials and seam-free normals (P1).

## 11. References

- [14-world-streaming](../../design/14-world-streaming.md): decisions and the dated build notes for rings, entity streaming, doors and the exit game.
- [09-serialization-and-saves](../../design/09-serialization-and-saves.md): cells go dormant with their state.
- [16-gameplay-framework](../../design/16-gameplay-framework.md): off-screen simulation.
- [REDESIGN](../../REDESIGN.md) §5 phase 4g; [MAKING_A_GAME](../../MAKING_A_GAME.md) §10b (experimental API).
- Siblings: [04-ecs-and-scenes](04-ecs-and-scenes.md), [08-physics](08-physics.md), [09-navigation-and-ai](09-navigation-and-ai.md), [15-saves](15-saves.md), [17-rpg-kit](17-rpg-kit.md); parent [SRS](../SRS.md).
