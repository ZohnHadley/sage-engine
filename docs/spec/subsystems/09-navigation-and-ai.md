# 09 · Navigation and AI

> Status: partly built. Sight-only HL1-style AI with schedules, a navmesh baked from brushes, terrain and static colliders (with the local grid as its fallback), crowd avoidance, ground that costs or is closed to a faction, per-size navmeshes, casting, NPC routines and off-screen simulation work and are tested. Hearing, behaviour trees and squads are planned. Owning assemblies: `Sage.Gameplay` (with a seam in `Sage.Simulation`). Design docs: [16 Gameplay framework](../../design/16-gameplay-framework.md) (§3.4 and the AI status sections), [14 World streaming](../../design/14-world-streaming.md).

## 1. Purpose and scope

This subsystem makes non-player characters act. A creature perceives (sight today), picks a schedule of tasks from the conditions it perceives, walks to where it needs to be, fights, and keeps a daily routine. When the player is elsewhere, a small off-screen simulation keeps NPC positions and fights moving so the world does not freeze.

Everything is data first: `ai_profile`, `ai_schedule` and `routine` records say what a creature senses and does. C# is only for new tasks, conditions and selectors, which are registered by name.

It deliberately does not do: combat rules, damage, abilities (the gameplay sheet, [16](16-gameplay.md)), the character controller that actually moves a pawn ([08](08-physics.md)), or choosing a faction's opinion of the player beyond reading it (also [16](16-gameplay.md)). Animation of the walk is the animation sheet's ([10](10-animation.md)).

## 2. Responsibilities

- Perceive the world at a staggered think rate and set a condition bitmask on each agent.
- Choose a schedule from the conditions through a selector (`default`, `rules`, or a game's own).
- Run schedule tasks and write the result into `PawnIntent`, never into physics directly.
- Find a walkable route on a navmesh baked from brush floors, terrain and static colliders, across levels and sector borders, falling back to a local grid A* where there is no mesh; within a per-tick budget.
- Warn, under `sage validate`, about a level's `info_` markers that no body can walk to.
- Plan through doors (a creature opens one and waits) and over off-mesh links (walk, jump, drop, ladder, teleport), and plan again when a blocker comes to rest in a path.
- Steer walking creatures round each other, weigh ground by its `nav_area` (by surface, water or volume), keep a faction out of ground closed to it, and plan each body on the navmesh of its size class.
- Remember a target that went out of sight and walk to where it was last seen.
- Ask the faction rules who is an enemy, and take whoever hurt it as a target.
- Keep NPC routines by the hour: pick a schedule and an anchor place from the clock.
- Simulate agents whose cell is dormant: walk toward routine anchors, settle fights, and spawn them back when their cell is live.

Not responsible for: riding a lift or a moving platform, hearing, squad tactics, crime and witnesses (planned in the gameplay sheet), or editing AI (the editor sheet, [18](18-editor.md)).

## 3. Placement and dependencies

All code lives in `Sage.Gameplay` under `Navigation/`, `AI/` and `World/Offscreen.cs`. It references `Sage.Simulation` (records, ECS, content, vocabularies) and `Sage.Physics3D` (raycasts, the `hitbox`-free solid layers, the character's `PawnIntent`). It never references MonoGame or a kit. The off-screen system stands on `ICellHandoff` in `src/Sage.Simulation/Content/CellHandoff.cs`, which the cell dormancy code calls.

Plugin ids: `sage.gameplay.ai` (declared by `AIModule`, which requires `sage.gameplay.character`) owns the AI and off-screen declarations; `sage.gameplay.factions` owns factions. Tasks are added by a game through the `AITaskRegistry` that `AIModule` provides in `Start`. Conditions, schedule selectors and off-screen fight rules are open vocabularies declared with attributes.

## 4. Interfaces

| Type | Role | File |
|---|---|---|
| `Navigation` | World resource: plans routes, holds budgets and statistics | `src/Sage.Gameplay/Navigation/Navigation.cs` |
| `NavGrid` | Window of up to 96 by 96 cells, stamped from colliders and steep ground; A* over eight neighbours; the fallback where there is no navmesh | same |
| `NavMesh` (internal) | Tiles of 8 m (32 cells of 25 cm) baked on demand from brush planes, terrain heights and static collider boxes, eroded by a 0.35 m body; a coarse A* over each tile's regions (a tile not baked yet is one optimistic node), then a span A* inside a corridor of up to eight regions, straightened into corners | `src/Sage.Gameplay/Navigation/NavMesh.cs` |
| `NavWorldGeometry` (internal) | What the mesh is baked from, kept in step with the world: drops the tiles under a level placed or unloaded, a terrain sector loaded or unloaded, or a static collider added, moved or removed | `src/Sage.Gameplay/Navigation/NavMeshSource.cs` |
| `Crowd` (internal) | Steering round other characters: a once-a-tick snapshot of every character, and a sampled velocity-obstacle choice per walking creature with a neighbour within 4 m | `src/Sage.Gameplay/Navigation/Crowd.cs` |
| `NavAreas` (internal), `NavAreaRecord`, `NavArea`, `NavAreaPart` | Areas as numbers (cost, factions shut out, the surfaces and water they cover) and the `nav_area` record, component and part | `src/Sage.Gameplay/Navigation/NavAreas.cs` |
| `NavMeshChecks` (internal) | The unreachable-marker check on `map` records | `src/Sage.Gameplay/Navigation/NavMeshChecks.cs` |
| `NavPath` | Up to six corners an agent is walking, with replan timers | same |
| `IAITask`, `AITaskContext` | A task is `Start`/`Run` returning `AITaskStatus`; the context exposes the agent's components | `src/Sage.Gameplay/AI/AI.cs` |
| `AITaskRegistry` | Tasks by name: `Wait`, `FaceTarget`, `MoveToTarget`, `MeleeAttack`, `CastSpell`, `MoveToAnchor`, `FaceAnchor`, `StayAt` | same |
| `IAICondition`, `AIConditions` | Sensors that set a condition bit; `[AICondition("name")]` declares one | `src/Sage.Gameplay/AI/AIVocabulary.cs` |
| `IAIScheduleSelector` | Picks a schedule from `AIScheduleChoice`; engine entries `default` and `rules` | same |
| `Routines` | Static helpers: which routine entry holds now, anchor lookup, `RoutineTarget` | `src/Sage.Gameplay/AI/Routines.cs` |
| `IOffscreenFight` | How an off-screen fight round is settled; engine entry `strength` | `src/Sage.Gameplay/World/Offscreen.cs` |
| `Factions` | `Between`, `AreHostile` and the player's stance, read by AI, combat and abilities | `src/Sage.Gameplay/Factions/Factions.cs` |

Console and cvars: `ai_debug` (draws sight and chase targets, needs `r_debugdraw 1`), `nav_enabled`, `nav_mesh`, `nav_avoid`, `nav_debug`, `nav_cellsize`, `nav_maxnodes`, `nav_plans`, commands `nav_stats` and `nav_rebuild`, cvar `offscreen_budget`, command `offscreen_status`, and the faction commands `rep` and `rep_set`.

Events: the AI reads `Damaged` (to turn on an attacker), `TimePassed` (to catch routines up after a time skip) and `Origin.Rebased` (to shift stored paths). It raises `OffscreenDied` when an off-screen agent dies. Entity inputs and outputs: none of its own; AI conditions are also usable from the shared condition language through the vocabulary.

## 5. Data model

| Declaration | Kind | Holds |
|---|---|---|
| `ai_profile` | Record | `SightRange`, `SightAngleDegrees`, `MemorySeconds`, `MeleeRange`, `ThinkRate`, `TurnSpeedDegrees`, `Selector`, `Rules`, `Routine` |
| `ai_schedule` | Record | `Tasks` (each an object such as `{ "task": "Wait", "seconds": 1.5 }`) and `Interrupts` (condition names) |
| `routine` | Record | `entries`: hour window, optional `days`, `schedule`, `at` anchor (a placement's name or a `.map` targetname), optional `scene` |
| `faction` | Record | Relations to other factions, a default stance, the player's `Standing` and thresholds, `KillCost` |
| `nav_area` | Record | `Cost` per metre (1 is ordinary ground), `Forbidden` factions, the `Surfaces` (physics_materials) and `Water` it covers |
| `sage:nav_area` | Component and `nav_area` prefab part | `Area` and `Size`: a box of ground that is that area, over water and surfaces |
| `sage:ai_state` | Component | Profile, schedule, task index, target (saved); conditions, path, last seen (transient) |
| `sage:routine` | Component and `routine` prefab part | The entity's own routine, which wins over its profile's |
| `sage:offscreen` | Component and `offscreen` prefab part | `speed`, `strength`, `corpse`, `fight`; opts an NPC into simulation |
| `sage:faction` | Component | Which faction an entity belongs to |
| `offscreen` | Saved resource | The table of off-screen agents and the game minute reached |
| `reputation` | Saved resource | The player's standing with each faction |

Engine conditions (the `AICondition` bits): `SeeEnemy`, `LostEnemy`, `EnemyInMeleeRange`, `NoEnemy`, `TaskFailed`, `ScheduleDone`, `CanCastAtEnemy`, `CanMelee`, `SpellComingBack`, `Casting`, `RememberEnemy` and `in_routine`.

## 6. Lifecycle and data flow

`sage.ai.offscreen` runs in `Phase.Commands` before `sage.ai.think`; `sage.ai.think` runs in `Phase.Commands` after `sage.character.player_control`, so `PawnIntent` is final after Commands. `sage.ai.debug` runs in `Phase.Late`. All are on the fixed tick.

A think is staggered per entity at the profile's `ThinkRate` (default 6 Hz). It perceives, rebuilds conditions, picks a schedule when the current one ends or an interrupt condition appears, then runs the current task every tick. Target and path state are rebuilt after a load; the schedule and task index are saved.

When a cell goes dormant, `ICellHandoff` offers its roots to the off-screen system, which takes living NPCs that carry the `offscreen` part into the saved table. A step is one game minute. An agent walks straight while the minute's walk is clear and plans round walls (A* over the content's coarse graph: static boxes taller than a step are walls, movers are doors unless their `nav_door` is locked, `nav_link`s are edges, wall corners are nodes) when it is not; its `Route` and `RouteGoal` are saved. Placements with the part in sectors never placed join the table at their routine's place (`CellContent.SeedUnplaced`). A live NPC whose anchor is not in the world walks to where the content has it, or to the door to its scene, and one with the part goes through (#284). When the agent's cell is live again it is spawned back from its save entries. A time skip runs every minute it covers at once.

Registration: tasks are open until the first world is created and are checked then; conditions, selectors and fight rules are sealed when content loads. Routines and off-screen simulation are `[Experimental("SAGE0129")]`.

## 7. Threading, memory and performance

Everything runs on the simulation thread. The grid, open set and corner buffer are reused; a search allocates nothing. Budgets are explicit: a search stops after `nav_maxnodes` nodes (4096; on the navmesh the coarse regions and the fine spans together), at most `nav_plans` plans run a tick (4), at most 16 navmesh tiles are baked a tick besides the one a planning creature stands in (beyond that a tile is planned through as one optimistic node and baked later), a failed search backs off to one try every 2.5 s, and "is the line clear" is cached for 0.2 s. Ground steepness is sampled at terrain resolution by the grid, and per 25 cm column by the navmesh. Baked tiles are kept (about 40 KB each, at most 2048, least recently used first out) and keyed by absolute position, so a rebase rebuilds nothing; a bake allocates its tile, a search reuses its working set.

Crowd steering reads one snapshot of every character a tick into kept arrays and a hashed grid, and scores 50 candidate velocities against at most ten neighbours, with at most six rays for room; a creature with nobody within 4 m pays one grid lookup. Areas cost nothing in a game without `nav_area` records; with them, the straight-line check walks the navmesh cells of the line five times a second, and a tile keeps one byte of area and one of nearby area per span. Each size class has its own tiles, baked only once a body of that size plans.

Off-screen catch-up is limited to `offscreen_budget` agent-steps a tick. An off-screen step allocates nothing (test: FiveHundredAgentsStepWithoutAllocating). The AI think and navigation are written to the same rule; no dedicated measurement test exists for them yet (see #273 range for the physics-side numbers).

## 8. Errors and diagnostics

Content mistakes are load errors with file, line and column: an unknown interrupt, condition, selector or routine weekday, a task given an argument it does not take, and the old `"Wait:1.5"` string form, which says what to write. Unknown task names are reported when the first world is created. A level's `info_` marker (`info_player_start`, `info_target`, ...) that stands on nothing walkable, or that no walkable way joins to the level's `info_player_start`, is a warning at the marker's line in the map, from baking the level's brushes at load (in a development build and under `sage validate`; test: ValidateNamesAMarkerNothingCanWalkTo). A missing selector or off-screen fight rule logs once on the AI category and falls back to the default. `sage validate` runs the same checks headlessly.

Debug tools: `ai_debug`, `nav_debug`, `nav_stats` (plans on the mesh and on the grid, tiles baked), `nav_rebuild` (drop every baked tile), `offscreen_status`. `nav_enabled 0` is the A/B switch that returns creatures to walking straight at their target; `nav_mesh 0` returns them to the local grid alone.

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-AI-01 | An agent shall perceive an enemy by a sight cone with line of sight blocked by scenery. | Must | Done | test: ItSeesThePlayerAndChasesUntilItIsInRange, test: AWallBreaksTheLineOfSight |
| REQ-AI-02 | An agent shall act through data schedules of named tasks with interrupt conditions, and write only `PawnIntent`. | Must | Done | test: NothingWritesPawnIntentAfterTheCommandsPhase |
| REQ-AI-03 | An agent shall remember an unseen target and walk to where it was last seen. | Must | Done | test: ACreatureRemembersWhatItCanNoLongerSeeAndThenForgets |
| REQ-AI-04 | An agent shall find a route round obstacles within a bounded per-tick cost. | Must | Done | test: PlanningIsBudgetedPerTick, test: ACreatureWalksRoundAWallToReachThePlayer |
| REQ-AI-05 | Paths shall stay correct when the world origin is rebased. | Must | Done | test: APathMovesWithTheWorld |
| REQ-AI-06 | An agent shall choose enemies through faction rules and turn on whoever hurt it. | Must | Done | `src/Sage.Gameplay/Factions/Factions.cs` |
| REQ-AI-07 | NPCs shall follow a data routine by the hour, resuming it after a fight and after a save. | Must | Done | test: AFightInterruptsTheRoutineAndHeGoesBackToIt, test: SavedMidWalkHeArrivesAfterALoad |
| REQ-AI-08 | Agents in dormant cells shall keep their routines and settle fights deterministically, then reappear. | Should | Done | test: TwoHostileSquadsOffscreenFightItOutTheSameWayEveryTime |
| REQ-AI-09 | Games shall add tasks, conditions, selectors and off-screen fight rules without engine edits. | Must | Done | `AITaskRegistry`, `[AICondition]` in `src/Sage.Gameplay/AI/AIVocabulary.cs` |
| REQ-AI-10 | Agents shall hear noise and be alerted by it. | Must | Not started | #386 |
| REQ-AI-11 | Routes shall be planned on a navmesh built from brush floors and terrain, for interiors and long distances. | Must | Done | test: ACreatureCrossesALevelOfRoomsAndASectorBorderOnTheNavmesh, test: WithoutTheNavmeshTheSameCreatureIsStuckInTheFirstRoom, test: ALongWayAcrossASectorBorderIsPlannedAStretchAtATime, test: TerrainIsBakedAndARidgeAcrossASectorBorderIsWalkedRound, test: ValidateNamesAMarkerNothingCanWalkTo |
| REQ-AI-12 | Paths shall handle doors, dynamic obstacles and off-mesh links. | Should | Done | test: ACreatureOpensAClosedDoorAndWalksThroughIt, test: ALockedDoorIsAWallToThePlanner, test: ACreatureDropsFromALedgeByALink, test: WithoutTheLinkTheCreatureStaysOnTheLedge, test: ACreatureReplansWhenABlockerComesToRestInItsWay, test: EachKindOfLinkIsCrossed |
| REQ-AI-13 | A game shall be able to select behaviour with behaviour trees or utility scoring beside schedules. | Should | Not started | #387 |
| REQ-AI-14 | Hostile groups shall move in combat (strafe, retreat, cover) and as squads. | Should | Not started | #388 |
| REQ-AI-15 | Agents shall avoid each other and weigh terrain costs and area flags. | Could | Done | test: TwentyCreaturesConvergingInACorridorAllGetThroughRoundEachOther, test: WithoutAvoidanceTheSameCrowdWalksThroughItself, test: ACreatureTakesTheRoadRoundAFieldRatherThanTheShortWayAcrossIt, test: ThePlannerGoesRoundDearWaterAndStraightensOnlyOverCheapGround, test: BrushTexturesAndTerrainLayersAreTheAreaOfWhatTheyAreMadeOf, test: GroundClosedToAFactionIsPlannedRoundByItAndCrossedByOthers, test: AWolfChasingIntoGroundClosedToItStopsAtTheEdge, test: EachBodyPlansOnTheMeshOfItsOwnSize |
| REQ-AI-16 | Off-screen simulation shall path round walls, cover cells never visited and handle live NPCs with far anchors. | Should | Done (#284) | test: AnAgentCrossesAWalledTownByItsGate, AMapsTargetnamesAreAnchorsAndItsBrushesWalls, ANeverVisitedSectorsNpcAppearsAtItsRoutinePosition, ALiveNpcWalksToAnAnchorThatIsOnlyInTheContent, ALiveNpcWhoseAnchorIsInAnotherSceneGoesThroughTheDoor |
| REQ-AI-17 | Crime, witnesses, bounty and faction ranks shall be expressible as data. | Should | Not started | #389 |

## 10. Open work

Milestone 10, AI, combat and narrative depth (epic #385):

- #386 4r-1 Hearing, noise and alerting in AI perception (P1)
- #387 4r-2 Behaviour trees / utility selection beside schedules (P2)
- #388 4r-3 Combat movement and squad behaviour (P2)
- #389 4r-4 Crime, witnesses, bounty and faction ranks (P2)

Related, in the editor sheet: #369 10b-4 Behaviour-tree / AI graph view, done: the AI graphs are state machines, schedules and routines, shown with the states and task an agent is in (test: WhilePlayingTheViewShowsTheStatesTheGuardIsIn).

## 11. References

- [Design 16](../../design/16-gameplay-framework.md): §3.4, status sections for AI casting, factions, navigation, NPC routines and off-screen simulation.
- [Design 14](../../design/14-world-streaming.md): cells, dormancy and the handoff seam.
- [REDESIGN](../../REDESIGN.md) §5, the roadmap; [MAKING_A_GAME](../../MAKING_A_GAME.md) §10b for the experimental ids.
- Siblings: [Physics](08-physics.md), [Animation](10-animation.md), [Gameplay](16-gameplay.md), [World and streaming](14-world-and-streaming.md), [Editor](18-editor.md); parent [SRS](../SRS.md).
