# 14 · World, streaming and time

> Status: built and tested for the 4g exit game; streaming has a far ring of coarse ground and far looks, per-sector asset scopes, generation on jobs and budgeted per-chunk collision (#277); a world's time scale, pause and hit-stop are a saved service (`WorldTime`, #283); the calendar has leap years, seasons, moon phases and scheduled calendar events (#289); off-screen agents walk a coarse graph of the content's walls and doors, start in sectors never placed and take live NPCs through doors (#284); several streaming sources keep rings of their own and followers travel with the player (#290); a `live` scene the player leaves stays placed and simulated beside the next, with its own gravity, and companions come through doors (#291). Owning assemblies: `Sage.Simulation` (`World`, `Levels`, `Content`), `Sage.Physics3D` (terrain collision), `Sage.Gameplay` (off-screen simulation), `Sage.Client` (terrain meshes, the far ring). Design doc: [14-world-streaming](../../design/14-world-streaming.md).

## 1. Purpose and scope

The world subsystem makes an unbounded, large or interior-heavy game possible with small float numbers and a bounded cost per tick. It does this with a grid of 1024 m sectors, a streaming ring around the player, an origin that follows the player, cells that go dormant with their state, interiors behind load doors, fast travel, and a game clock with a calendar.

It deliberately does not do the following. Entity storage, scenes and prefabs are [04-ecs-and-scenes](04-ecs-and-scenes.md). Save writing and loading are [15-saves](15-saves.md), though dormancy reuses the save entry format. Terrain drawing is [07-rendering](07-rendering.md) and terrain collision bodies are [08-physics](08-physics.md). NPC routines and the off-screen fight rule are [09-navigation-and-ai](09-navigation-and-ai.md); this sheet covers only the seam they stand on. Rest rules and map screens are the RPG kit's, [17-rpg-kit](17-rpg-kit.md).

## 2. Responsibilities

- `SectorCoord` addressing, sector-local root transforms, the `Origin` resource and rebasing of every position-holder between ticks.
- The streaming rings: one per streaming source (the player always; `streaming_source` tags and `streaming_ring` components add more, #290); load within `stream_radius`, unload past `stream_radius + 1`, rebase past 1.5 sectors from the player, all with hysteresis.
- Built-in terrain: heightfields of 129 by 129 samples, the `terrain` record and its `Flat` and `Hills` generators, and the `ITerrainGenerator` seam for a game's own (with `ITerrainSampler` for seam-free edge normals).
- The far ring (`stream_far_radius`): coarse ground and far looks (`PrefabRecord.Far`) past the full ring, generation ahead on jobs (`stream_jobs`), a load budget while walking (`stream_load_budget`), and per-sector asset scopes (`SectorAssets`).
- Streamed scenes: placements, placements documents and `.map` levels bucketed by sector, placed within a per-tick budget at the tick boundary.
- Cells: dormancy with state, runtime spawns owned by a cell, waking, absolute positions across rebases.
- Interiors (`"space": "interior"`), load doors, travel points, `Travel.To` and `travel`; followers who come along on every journey (#290); scenes held live beside the player's (`"live": true`, `space_live_max`) with per-space gravity (#291).
- The world clock, calendar record, `Time.Pass` skips and the `TimePassed` event.
- The `ICellHandoff` seam for what keeps simulating while a cell sleeps.

Not responsible for: mesh LOD groups and instancing ([07-rendering](07-rendering.md), #305), the asset server's scopes for textures and sounds and a memory budget (#308); source priorities; separate physics simulations or `World`s per space (spaces share one world and one physics simulation, apart by their coordinates).

## 3. Placement and dependencies

Sector, origin, terrain, travel and clock code is in `src/Sage.Simulation/World`; cells, scenes and the streamed scene are in `src/Sage.Simulation/Content`; map levels are in `src/Sage.Simulation/Levels`. It sits in the simulation assembly with no MonoGame. `Sage.Physics3D` references it for `TerrainCollisionSystem`. `Sage.Gameplay` references it for `OffscreenSystem` (`src/Sage.Gameplay/World/Offscreen.cs`). `Sage.Client` consumes `Terrain` to build chunk meshes, `SectorLod` to draw the far ring and `SectorAssets` to free a sector's buffers, and subscribes to `Origin.Rebased`. Kits never appear in this code.

Plugins: `sage.streaming` (`StreamingModule`) installs the `Terrain` resource and the ring, so a world without it has no sectors; the records `scene`, `terrain` and `calendar` and the parts `load_door` and `travel_point` register under the core owner; `sage.gameplay.ai` owns the `offscreen` part and table. Most of this area is `[Experimental("SAGE0129")]`.

## 4. Interfaces

| Type | File | Role |
|---|---|---|
| `SectorCoord` | `World/Terrain.cs` | Absolute sector address (X, Z); sectors are 1024 m. |
| `Terrain`, `Heightfield`, `ITerrainGenerator` | `World/Terrain.cs` | Per-world ground: `Load`, `Unload`, `HeightAt`, `NormalAt`, `OnGround`, origin-aware. |
| `Origin` | `World/Origin.cs` | The origin sector, `ToAbsolute`/`ToOrigin`, event `Rebased(offset)`; `world.Rebase(sector)`. |
| `StreamingSource` | `World/Streaming.cs` | Tag (`sage:streaming_source`): another source with the cvars' rings; the player is always a source (#290). |
| `StreamingRing` | `World/Streaming.cs` | `sage:streaming_ring` and the part `streaming_ring` (`radius` 0 to 8, default 1; `farRadius` 0 to 16, default 0): a source with rings of its own; on the player it replaces the cvars' (#290). |
| `Follower`, `Followers` | `World/Followers.cs` | `sage:follower` and the part `follower` (`distance`, m, default 2); `Followers.Bring` puts every live follower behind the player after a journey (#290). |
| `Scenes.LiveBeside`, `SpaceGravity`, `GravityRegion` | `World/Spaces.cs` | The scenes held live beside the player's, and each space's gravity scale by where a body is, read by physics and the character controller (#291). |
| `StreamingSystem`, `SectorRing`, `SectorOwnersSystem` | `World/Streaming.cs` | The ring, and crossing-an-edge ownership (both Phase.Late). |
| `SectorLod`, `FarSector`, `FarLook` | `World/SectorLod.cs` | The far ring: coarse ground and far looks past the full ring, drawn by the client's `FarLodSystem` (#277). |
| `SectorAssets` | `World/SectorAssets.cs` | Per-sector asset scopes: mesh and texture paths ref-counted by live users, released when a leaving sector let go of the last one (#277, textures since #308); the client frees them at the frame's safe point (sheet [06](06-assets-and-content.md)). |
| `ITerrainSampler` | `World/Terrain.cs` | A generator's height at any point, for seam-free edge normals (#277). |
| `TerrainMaterialRecord`, `TerrainLayer`, `TerrainSplat` | `World/TerrainSplat.cs` | Splat terrain (#307): the `terrain_material` record, its load checks and the weight map; the client's `terrain.fx` blends the layers. |
| `Scenes`, `StreamedScene` | `Content/Scenes.cs`, `StreamedScene.cs` | Place and clear scenes; `Current(world)`; the tick-boundary placement. |
| `Cells`, `InCell`, `ICellHandoff` | `Content/Cells.cs`, `CellHandoff.cs` | Dormant store, cell ownership, the hand-off seam. |
| `Travel`, `TravelLog` | `World/Travel.cs` | `To`, `ToPoint`, `Use`, `HoursTo`; the saved list of discovered points. |
| `WorldClock`, `Time` | `World/WorldClock.cs`, `World/Time.cs` | Day and hour, scale; `Time.Pass(world, hours, reason)`. |
| `CalendarRecord` | `World/Calendar.cs` | Months, weekdays, start year, leap rule, seasons, moon cycle; `WorldClock.Date`, `IsLeapYear`, `SeasonOf`, `MoonPhaseOf` (#289). |
| `CalendarEventRecord`, `CalendarEventListener` | `World/CalendarEvents.cs` | A dated (or moon-phase) event and the component that fires `OnCalendarEvent` on its day (#289). |

Console and cvars: `warp <x> <z>`, `stream_status`, `stream_radius`, `stream_enabled`, `stream_place_budget`, `space_live_max`, `spaces`, `travel <point>`, `travel_speed`, `scene_load`, `time`, `time_set`, `time_pass <hours>`, `offscreen_status`. Inputs: the `Travel` input goes through a door, or takes `"<scene> <entry>"` sent to any entity; the Use action opens a `load_door`. Events: `Origin.Rebased`, `TimePassed(Hours, Reason, FromElapsed, ToElapsed)`, and `OffscreenDied`. Output: `OnCalendarEvent` (#289). Conditions: `weekday`, `date_between`, `season`, `moon_phase`, `on_date`.

## 5. Data model

| Id | Kind | Notes |
|---|---|---|
| `scene` | record | `streamed`, `space` (`Exterior` or `Interior`), `live` (held and simulated when the player leaves, #291), `terrain` reference, `environment` (with `gravityScale`, default 1, #291), placements, levels. |
| `sage:streaming_ring` | component and part `streaming_ring` | `radius`, `farRadius` (#290). |
| `sage:follower` | component and part `follower` | `distance` (#290). |
| `terrain` | record | `Flat` or `Hills`, with `seed`, `height`, `amplitude`, `wavelength`, `material` (a `terrain_material`, #307; `Terrain.Material` from code), and `sculpt` (a `.sterrain` file of the editor's height offsets, paint and water over the generator; empty: `terrain/<id>.sterrain` if there is one; #372). |
| `terrain_material` | record | `layers` (one to four: `texture`, `tile`, `minHeight`/`maxHeight`, `heightBlend`, `minSlope`/`maxSlope`, `slopeBlend`; the first is the ground everywhere), `detail`, `detailTile`, `detailStrength`, `fallback` (the material without the splat shader). Weights per vertex from height and slope, the same either side of a sector edge (#307). |
| `calendar` | record | `Months` (name, days), `Weekdays`, `StartYear`, `StartWeekday`; `LeapEvery`, `LeapSkipEvery`, `LeapRestoreEvery`, `LeapMonth` (no leap years by default); `Seasons` (name, month, day; each runs to the next start; the default calendar has four from 1 March); `MoonCycle` (days, default 29.53), `MoonStart` (#289). |
| `calendar_event` | record | `Name`, `Month` (0: any month, with a moon phase), `Day`, `Year` (0: every year), `Moon` (a phase name) (#289). |
| `sage:calendar_event` | component and part `calendar_event` | `Event` (a `calendar_event` record); saved with `LastDay`, so a day fires once and a skipped day fires once on waking. |
| `sage:cell` | component (`InCell`, saved) | `Source`: `scene:<id>` or `sector:<scene>:<x>,<z>`. |
| `sage:sector_owned` | component | Marks what a sector brought (terrain chunks, collision body). |
| `sage:load_door` | component and part `load_door` | `scene`, `entry`, `hours`. Not saved. |
| `sage:travel_point` | component and part `travel_point` | `label`, `radius`, `discovered`. |
| `sage:offscreen` | component and part `offscreen` | `speed`, `strength`, `corpse`, `fight`. |
| `clock` | saved resource | `Day`, `Hour`, `Scale`, `Sky`, `Calendar`. |
| `time` | saved resource | `WorldTime` (#283): `Scale` (world speed), `Paused`, `HitStopLeft`, `Unscaled`, `Scaled`, `Carry`. The clock advances on its steps, so at half speed the hours go by at half speed too. Console `world_speed`, `hit_stop`; actions `world_speed`, `hit_stop`. |
| `travel` | saved resource | The `TravelLog`: discovered points with scene and absolute place. |
| `offscreen` | saved resource | The table of agents that are simulated while their cell sleeps. |
| `spaces` | saved resource | `LiveSpaces`: the ids of the scenes held live beside the player's; a load places each again with the save's state (#291). |

Dormant cells are written in the save under `dormant`, by source, each with its own origin frame (save format 4, see [15-saves](15-saves.md)).

## 6. Lifecycle and data flow

At the fixed tick: `sage.world.clock` (Phase.Commands) advances the clock; `sage.ai.offscreen` (Commands, before AI think) catches up the off-screen agents to the clock; `sage.streaming.sectors` and `sage.streaming.owners` (Phase.Late) keep the ring, rebase, and re-own roots that crossed an edge; `sage.world.travel_points` (Late) discovers points; `sage.world.calendar_events` (Phase.EntityIO, before the dispatch) fires `OnCalendarEvent` for each listener whose event fell since the day it last looked. After the fixed phases, at the tick boundary, `Scenes.TickEnded` unloads sectors that left the ring and places those that entered, nearest first, within `stream_place_budget` (default 64 entities a tick); `Travel` and `Time.Pass` requests also run here, so no system sees a scene or hour change under it.

A rebase moves every root, both poses of every `GlobalTransform`, every physics body and static, and the cameras at once, then raises `Origin.Rebased`. Order for travel and `warp` is rebase, generate ground, then place the player.

Going dormant writes live entities and the cell's runtime spawns as save entries, destroys them, and keeps the entries per source; waking lays them back on the placed content by stable id. Off-screen agents are taken out of the cell by `ICellHandoff` before it is written, and come back when their scene and sector are live. Placements with the `offscreen` part in sectors of the current streamed scene that were never placed join the table once per placing of the scene, at their routine's place, and are tombstoned in their sector (`CellContent.SeedUnplaced`, #284). A time skip steps every game minute of the skip for off-screen agents. Save and load: the clock, travel log and off-screen table are saved resources; cells are the `dormant` section.

A scene with `"live": true` is held instead of going dormant when the player leaves it (#291): what it placed stays and is simulated, and going back takes it up as it is. An exterior is held only while the player is inside an interior (the ring stops following the player but keeps its sectors), and going to another exterior puts every held exterior to sleep first. At most `space_live_max` (default 4) are held; the one left longest ago goes dormant, and 0 is the old swap. A runtime spawn within the box a held scene's things stand in belongs to that scene's cell. A journey releases every follower from the scene being left and restores it into the cell the player arrives in, then `Followers.Bring` stands it behind the player (#290, #291). The `spaces` resource saves the held scenes; a held streamed exterior is dormant after a load until the player goes back.

## 7. Threading, memory and performance

Terrain generation runs on the thread pool: the ring plus one is generated ahead and the far ring's coarse ground on jobs, and the results are the same ground as a main-thread generation (test: `GenerationOnJobsIsTheSameGroundAsOnTheMainThread`). Everything else runs on the main thread. While walking, at most `stream_load_budget` sectors (default 1) become live a tick and physics builds at most four of a sector's sixteen collision chunks a tick, so crossing an edge does not stall (REQ-PERF-05 in the [SRS](../SRS.md); test: `AFarRingFourSectorsOutCostsABoundedTickAndCrossingEdgesDoesNotSpike`). A streamed scene at rest allocates nothing per tick (test: `AStreamedSceneAtRestAllocatesNothingPerTick`), and an off-screen step allocates nothing (test: `FiveHundredAgentsStepWithoutAllocating`). Placement is budgeted so a sector arrives over several ticks (test: `PlacingASectorIsBudgetedAtTheTickBoundary`).

## 8. Errors and diagnostics

A door to a missing scene or entry, with the nearest name, and a placed door with no scene are load errors, so `sage validate` reports them (test: `ADoorToAMissingSceneOrEntryIsALoadError`). `Time.Pass` warns and refuses a non-positive, non-finite or over-large amount, or a world with no clock. Log categories are `Streaming` and `World`. Debug with `stream_status` (origin, placed sectors, rebases, the far ring, generation here and on jobs, released assets), `offscreen_status`, `time`, and `warp`.

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
| REQ-WORLD-12 | NPCs shall keep routines and fight off-screen, deterministically, with no per-step allocation. | Should | Done (#284): round walls by the content's coarse graph, from sectors never placed; planning a way round allocates, walking does not | test: `TwoHostileSquadsOffscreenFightItOutTheSameWayEveryTime`, `FiveHundredAgentsStepWithoutAllocating`, `AnAgentCrossesAWalledTownByItsGate`, `ANeverVisitedSectorsNpcAppearsAtItsRoutinePosition` |
| REQ-WORLD-13 | Streaming shall not stall a frame: generation on jobs, LOD and HLOD past the ring, per-sector asset scopes. | Must | Done | test: `AFarRingFourSectorsOutCostsABoundedTickAndCrossingEdgesDoesNotSpike`, `TheFarRingHasCoarseGroundAndFarLooksPastTheFullRing`, `ASectorsAssetsAreReleasedWhenItUnloadsAndSharedOnesAreKept`, `SectorEdgeNormalsMatchTheNeighboursSoLightingHasNoSeam` |
| REQ-WORLD-14 | Prefabs shall nest and keep per-placement overrides across sectors. | Should | Done | test: `AHouseWithAnOverriddenMachineRoundTripsTheEditorASaveAndASectorCrossing` |
| REQ-WORLD-15 | The world shall offer a time scale, pause and hit-stop as services. | Should | Done | `src/Sage.Simulation/World/WorldTime.cs` (the saved `time` resource); test: TheWorldsTimeSurvivesASave, PauseStopsScaledTimeButNotRealTime |
| REQ-WORLD-16 | The calendar shall add seasons, moon phases, leap years and scheduled events. | Could | Done (#289) | test: `LeapYearsFollowTheRule`, `SeasonsRunToTheNextStart`, `TheMoonCyclesThroughEightPhases`, `OnDateMatchesADayOfTheYear_AndALeapDayOnlyInLeapYears`, `AFestivalFiresItsWireOnItsDay`, `ASkippedFestivalStillFires` |
| REQ-WORLD-17 | There shall be several streaming sources, each with its own ring. | Could | Done (#290): no source priorities | test: `EachSourceKeepsItsOwnRing`, `ASourcesFarRadiusIsItsOwn`, `ThePlayersOwnRingIsUsedAndTheOriginFollowsThePlayer`, `AFollowerArrivesWithThePlayerAfterFastTravel`, `FollowersStandApartBehindThePlayerAndComeAlongOnAWarp` |
| REQ-WORLD-18 | Interiors shall be separate spaces, and companions shall follow through doors. | Could | Done (#291): spaces in one world and one physics simulation, apart by their coordinates | test: `TwoInteriorsAreLiveAtOnceBesideTheirExterior`, `ASaveKeepsTheSpacesHeldLive`, `ARuntimeSpawnAmongAHeldSpaceIsThatSpaces`, `EachSpaceFallsAtItsOwnGravity`, `CompanionsComeThroughTheDoor`, `HowManySpacesStayLiveIsBounded` |
| REQ-WORLD-19 | The editor's sculpt, paint and water shall be laid over the generated ground in every generation, with no seam at a sector edge. | Should | Done (#372): `TerrainSculpt`, a `.sterrain` per terrain; the client does not draw water volumes | test: `AHillOnASectorEdgeHasNoSeam`, `TheSculptFileRoundTripsAndOneThatIsNotASculptIsRefused`, `ASculptedHillAPaintedPathAndALakeAreSavedAndTheStreamedWorldShowsThem` |

## 10. Open work

Milestone 2 (epic #274): none left on this sheet.

Related, done in 4n (2026-10-06): #308 asset scopes and the upload budget (sheet [06](06-assets-and-content.md)), #307 terrain splat materials (sheet [07](07-rendering.md) REQ-REND-18; a generator without `ITerrainSampler` still seams at sector edges, weights are not painted by a generator, and a `Terrain.Material` changed at run time is not applied to sectors already built), #311 weather picked by the clock and region (`weather_pattern`, sheet [07](07-rendering.md) REQ-REND-14).

## 11. References

- [14-world-streaming](../../design/14-world-streaming.md): decisions and the dated build notes for rings, entity streaming, doors and the exit game.
- [09-serialization-and-saves](../../design/09-serialization-and-saves.md): cells go dormant with their state.
- [16-gameplay-framework](../../design/16-gameplay-framework.md): off-screen simulation.
- [REDESIGN](../../REDESIGN.md) §5 phase 4g; [MAKING_A_GAME](../../MAKING_A_GAME.md) §10b (experimental API).
- Siblings: [04-ecs-and-scenes](04-ecs-and-scenes.md), [08-physics](08-physics.md), [09-navigation-and-ai](09-navigation-and-ai.md), [15-saves](15-saves.md), [17-rpg-kit](17-rpg-kit.md); parent [SRS](../SRS.md).
