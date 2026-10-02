# 14 — World Streaming and Large Worlds (short)

> **From the readiness review (2026-09-24), risk 1.** R6 (large-world coordinates) is **the single
> most expensive item left, and it grows**: every feature built since the decision assumes world-space
> floats — transforms, physics bodies, the renderer's camera-relative path, the save format, the AI's
> distance checks. The engine runs in one 1024 m sector, so the premise of the target game is the part
> that is not built. See [`../history/readiness-2026-09-24.md`](../history/readiness-2026-09-24.md).

## 1. Purpose and scope
Daggerfall-scale exteriors and separate interiors:
- **spaces**;
- **sectors** and **origin rebasing**;
- **streaming rings**;
- terrain chunks;
- **dormancy** of unloaded content;
- fast travel.

In `Sage.Simulation` (data, terrain generation), with client parts for terrain meshes. Expanded when roadmap Phase 4 starts.

## 2. Research basis
- Float precision: ≈1 mm at 10 km, ≈8 mm at 100 km (survey §3.3).
- Daggerfall Unity: 800 m terrain zones in rings around the player (the "Terrain Distance" option), with the world shifted back toward the origin; terrain generated with jobs.
- UE5: `double` positions + camera-relative rendering; World Partition cells around streaming sources, with HLOD.
- Bethesda and Daggerfall: interiors are separate spaces.

## 3. Key decisions
- **Spaces:**
  - **Exterior**: an unbounded grid of **1024 m sectors** (`SectorCoord`, 03 §3.6).
  - **Interiors**: dungeons and buildings, each a separate space with its own origin, loaded as a unit.

  A world is in one space at a time for the player. Going through a door is a space transition (a short load).
- **Coordinates** (the multiplayer-compatible form of the Daggerfall Unity model):
  - Root entities store `SectorCoord` + a local `Transform`. When a root crosses a sector edge, its sector is updated and its local position wrapped (`Late` phase).
  - The world resource `Origin { SectorCoord Sector }` defines origin space for `GlobalTransform`, physics and rendering.
  - **Rebasing:** when the player is more than 1.5 sectors from the origin sector (hysteresis), the origin moves to the player's sector. `GlobalTransform` (current and previous), physics bodies (10 `Rebase`) and any cached positions shift by the same offset, once, between ticks.
  - Rendering is additionally camera-relative (06 §3.3).
- **Streaming rings** around each **streaming source** (the player; later others, such as a followed NPC):
  - full detail within `stream_radius` sectors (default 1 → 3×3 sectors);
  - terrain-only LOD out to `stream_lod_radius` (default 3).

  Loading a sector:
  1. generate its heightfield (seeded procedural generation + authored overrides);
  2. build terrain chunk meshes at the needed LOD (jobs → `Renderer.CreateMesh` through the upload budget);
  3. build the collision heightfield;
  4. instantiate map/procedural entities (baseline), then apply save data (09);
  5. create the sector's `AssetScope`.
- **Unloading** serializes the sector's persistent entities into the in-memory save cache (**dormancy**), destroys them, and disposes the sector scope. Dormant entities don't simulate, except NPCs that opt in to the coarse off-screen simulation (issue 4g-6: design 16 "As built (off-screen simulation)"); economy and travel hooks come later (F25/F26).
- **Terrain:** per sector a 129×129 height grid (8 m spacing) plus LOD meshes; vertex-coloured / single-texture v1; splat materials later (07).
- **Fast travel / teleport:** load the target sectors behind a loading screen, rebase the origin, place the player.

### As built (F13, 2026-09-22): one sector, no streaming
- **Code:** `src/Sage.Simulation/World/Terrain.cs` (`SectorCoord`, `Heightfield`, `ITerrainGenerator`, `TerrainSector`, the `Terrain` world resource) and `src/Sage.Client/Rendering/TerrainMesh.cs` (`TerrainMeshSystem`).
- **`sage.streaming` installs a `Terrain` resource in every world** (it used to come with every world; issue #13 moved it to its plugin, test: TheStreamingPluginInstallsTheTerrain_AndLoadWithoutAGeneratorIsRefused). Collision, navigation, levels and the renderer look it up and do nothing without it. A game sets `Generator` and `Seed` and calls `Load(sector)`; the sector's 129×129 heightfield (8 m spacing over 1024 m) is generated on the main thread, once per sector.
- **Meshes:** `TerrainMeshSystem` (FrameUpdate) builds the chunk meshes of any sector it hasn't drawn yet: 4×4 chunks of 32×32 cells, each its own `Renderer.CreateMesh` and its own entity with a `MeshRenderer` holding the **`MeshHandle`** (R9: subsystems own the data, components hold handles). Chunks are ordinary mesh items, so they cull, sort and draw with everything else — there is no separate `TerrainExtract`.
- **Material:** `sage:terrain_default` (one tiling ground texture over `lit.fx`). Splat materials come with LOD.
- **Ground height:** `Terrain.HeightAt/NormalAt/OnGround` sample the heightfield bilinearly. The Sandbox places its scene with them.
- **Collision (F6, done):** `TerrainCollisionSystem` gives every loaded sector one static Bepu mesh (10 "As built"), so things actually rest on the ground. `HeightAt` stays as the cheap query for placement and AI.
- **`SectorCoord` exists but nothing rebases yet:** everything lives in sector (0, 0) and chunk vertices are absolute world positions. Sector-local transforms, `Origin` and rebasing are R6; until then the terrain must stay near the origin for float precision.
- **Edges:** normals at a sector's border are computed from its own clamped heights, so neighbouring sectors will show a faint seam until streaming samples across them (F14).
- **Superseded 2026-09-24** by "As built (rings and rebasing)" below — sector-local transforms, `Origin`
  and rebasing all exist now.

### As built (rings and rebasing, 2026-09-24 — R6 + F14 v1)
The world is now unbounded. A player can walk, or `warp`, a hundred kilometres from where they started
and the simulation carries on with small numbers, because the frame of reference follows them.

- **Code:** `src/Sage.Simulation/World/Origin.cs` (the `Origin` resource and `world.Rebase`),
  `src/Sage.Simulation/World/Streaming.cs` (`StreamingSource`, `SectorOwned`, `StreamingSystem`,
  `StreamingModule`, `warp`), plus `Terrain` gaining `Unload`, `CornerOf` and origin-aware queries.
  Tests in `tests/Sage.Tests/Streaming/StreamingTests.cs`.
- **Origin space is the only space the simulation knows.** Transforms, physics bodies, the camera and
  every distance check are relative to the **origin sector**; sectors themselves are keyed absolutely,
  because sector (117, −79) is the same ground whatever the origin is. `Terrain` is where the two meet
  (`Origin.ToAbsolute` / `ToOrigin`), which is why `HeightAt` did not change for a single caller when
  rebasing arrived — it takes origin space, as it always did, and converts inside.
- **Rebasing moves everything at once**, between ticks: every root transform, **both poses** of every
  `GlobalTransform`, every Bepu body and static, and the camera. The poses matter as much as the
  bodies: `Previous` is what rendering interpolates *from*, so shifting only `Current` would streak
  the whole world across the screen for one frame. There is a test for exactly that.
- **Physics needed more than a pose shift.** A static's bounds live in the broad phase and do not
  follow its pose, so an unrefreshed static still blocks rays where it used to be; and a *sleeping*
  body is not re-bounded until it wakes. `PhysicsSpace.Rebase` now updates static bounds and wakes
  every sleeping island — affordable at once per kilometre of travel.
- **Rings with hysteresis:** load within `stream_radius` (default 1 → 3×3 sectors), unload past
  `stream_radius + 1`, rebase past 1.5 sectors from the origin. Every threshold has a margin, because
  the failure mode of a bare one is a player standing on an edge and loading the world at 60 Hz.
- **`SectorOwned`** marks what a sector brought with it — its chunk meshes and its collision body — so
  unloading takes them. Terrain chunk vertices became **sector-local** with the corner in the entity's
  transform: baked-in absolute positions would have nailed the ground to the GPU buffer while the world
  moved around it.
- **`warp <x> <z>`** is fast travel in miniature, and its *order* is the design: **rebase, generate,
  then place**. The first version placed first, read a ground height of zero from a sector that did not
  exist yet, and dropped the player 1.6 km. There is a regression test.
- **`Origin.Rebased`** is raised **after** everything has moved, for anything holding a position of
  its own: the editor's free camera subscribes to it, and without that `cam_free` left the camera a
  sector behind whatever it was looking at. A game's cached waypoints would use the same hook.
- **A scene's coordinates are absolute**, converted on placement (`SandboxModule.Ground`). Treating
  them as origin-space meant dying a hundred kilometres out respawned the player at the scene's
  *numbers* in the current origin — a place the scene has never been. Now respawning travels home, and
  the origin rebases back on the next tick, which is visible in the log as a second rebase.
- **Console:** `stream_status` (origin, loaded sectors, rebases so far), `stream_radius`,
  `stream_enabled`, `warp`.
- **Not yet:** LOD past the ring, per-sector asset scopes, ~~dormancy per sector~~ (built with 4g-3, on 4g-1's cells:
  "As built (entities stream by sector)" below), generation on jobs, interiors as separate spaces, and seam-free normals
  across sector edges.
- **Since 4g-1, a scene goes dormant with its state**: what it placed and the runtime spawns made in it
  are kept, with absolute positions, when the player leaves it, and come back when it is placed again
  (09 "As built (cells go dormant with their state)").
- **Since 4g-3, entities stream by sector**: see "As built (entities stream by sector)" below.
- **Since 4g-5, interiors are scenes behind load doors, and fast travel exists**: see "As built (doors,
  interiors and travel)" below.
- **Phase 4g's exit** puts all of it in one data-only game: "As built (the 4g exit game)" just below.

### As built (the 4g exit game, issue 4g-8, 2026-10-01)
Phase 4g's exit (REDESIGN §5, issue #190): walk from an exterior into a dungeon and back; an NPC keeps its
schedule across a day while you are away. `tests/games/open-world` proves it headless, with no C# and no
kit (`tests/Sage.Tests/Games/OpenWorldExitTests.cs`).

- **The game.** A streamed scene, `valley`, on the built-in `Hills` (seed 4, 3 m), starting at 07:00. The
  village is in sector (0, 0) by its east edge: a well, the smith's home and, across the edge in (1, 0),
  the forge. The smith has a character, an AI, the `villagers` faction, a hammer, the `smith` routine (the
  forge 08–20, home 20–08, through `MoveToAnchor`, `FaceAnchor` and `StayAt`) and the `offscreen` part.
  Three guards stand at the north gate and three bandits in a camp 200 m further north, in (0, -1); the
  bandits' routine, `raid`, takes them to the gate by day. Neither has an AI, so they fight only off-screen
  (guards strength 8, bandits 5). Two sectors east of the village, in (2, 0), a `load_door` leads into
  `crypt`, an interior scene whose `.map` (`maps/crypt.map`, written by a short script) is a stone room
  with a torch, a skeleton (30 health), the entry `crypt_in` (a point with no prefab) and a door back out.
  The player fights with a staff (12 a blow) and carries a sword. The plugins are physics, streaming, maps,
  entity I/O, character, attributes, combat, items, AI, factions and lights.
- **The run** (test: OpenWorldExit_AwayADayInTheCrypt_TheSmithKeptHisScheduleAndNothingIsDoubled). At
  07:00 the smith, who has been burnt to 80, is at home. The player travels to the crypt door, uses it
  (the Use action), and is at `crypt_in` with the terrain gone. The village is asleep, and the smith, the
  guards and the bandits are in the off-screen table. Three blows kill the skeleton, the sword is dropped,
  and the player rests 26 hours (`Time.Pass`, reason `rest`): one skip, every game minute of it stepped.
  The game is saved, and a fresh app loads it in the crypt at 09:00 the next day. The player uses the way
  out and is at the crypt door at 09:00, and the forge's sector is in the ring:
  - the smith is at the forge, not at home: the same id, still at 80, the hammer on him, once;
  - the gate's sector, placed again, has what the fight left: one side is dead (the bandits, in this
    content), each guard and bandit has the health and the dead tag the table gave it, and none is in the
    world and in the table at once;
  - at 20:00 he leaves the forge and walks home (the hour has just begun, so the catch-up does not put him
    there), and an hour later he is home;
  - back in the crypt the skeleton is dead with 0 health, with its id, and the sword lies where it fell,
    with its id; no persistent id is in the world twice, or in the world and the table.
  The same run without the save and load ends with the same fight, to the health and the position of
  each guard and bandit.
- **Validated** (test: TheOpenWorldGameValidates); CI runs `sage validate` on it, includes it in `sage
  schema`, and smoke-runs it in the real host: into the crypt through the `Travel` input, `time_pass 26`,
  out again and `offscreen_status`.
- **Two engine changes, the same as 4g-7's** (ported so the two merge as one). A door whose entry is a
  `.map` entity's `targetname` did not find it, because the scene's level was placed at the next tick, after
  the player had been put down: `Travel` now places the level's entities at once (`MapLoader.EnsureEntities`).
  And a journey set the clock to the destination scene's `environment.hour`, so coming out of the crypt
  into the valley made it 07:00 again: `hour` is for starting in a scene, and travel keeps the clock.

### As built (entities stream by sector, issue 4g-3, 2026-10-01)
Streaming loaded terrain only: a scene was placed whole, so an exterior bigger than a sector held every
goblin in it at once, and the ring did nothing at all without a C# generator, so no data-only game streamed.
Phase 4g's plan, decisions 2 and 5: a streamed sector is a cell (4g-1), and ground can come from data.
Code: `src/Sage.Simulation/Content/StreamedScene.cs`, `World/Streaming.cs` (`SectorRing`,
`SectorOwnersSystem`), `World/TerrainRecord.cs`; experimental SAGE0129.

- **A scene with `"streamed": true`** is put into buckets by absolute sector: its placements, its
  placements documents' and its `.map` levels that stand on the terrain (`onTerrain`; the others are placed
  with the scene). Each sector is a source and a cell, `sector:<scene>:<x>,<z>`. Nothing is placed when the
  scene is; a sector is placed when the ring reaches it and goes dormant, with its state and its runtime
  spawns, when the ring leaves it. Walk three sectors east and what is behind leaves the world (the count of
  placed entities drops from three to one), and walking back brings the wounded goblin back at 88 where it
  stood (test: WalkingThreeSectorsEastPutsWhatIsBehindToSleepAndBringsItBack).
- **Ids keep 4i's formula** (`ContentIds.ScenePlacement`, `DocumentPlacement`), only the source changes.
  A sector's tombstones are kept for good, and a dead placement is never placed only to be removed: a
  goblin killed in (2, 0) stays dead across the sector unloading and loading, a save and a load, and another
  trip (test: AGoblinKilledInSectorTwoStaysDeadAcrossUnloadReloadSaveAndLoad). An arrow dropped in (2, 0)
  sleeps with it, through a save and a load, and lies where it fell when the player comes back (test:
  AnArrowLeftInAnUnloadedSectorSurvives).
- **Old saves.** A load into a scene that streams re-keys what the save holds under `scene:<id>`
  (`SaveSystem.Rekey`): tombstones and placed entities' state go to the sector that places them now (prefab
  children included), a runtime spawn's cell becomes the sector it stands in, a dormant `scene:` cell is
  split by sector, and a format 2 save's absent placements are tombstoned in their sectors. The format 3
  golden save, put in a streamed scene with a guard it killed there, loads: the guard's sector is placed
  without it and keeps its tombstone, and the lantern's cell is its sector (test:
  AFormat3SaveOfASceneThatStreamsNowLoadsIntoItsSectors).
- **Rings without a generator.** `SectorRing` is the ring itself, kept by `StreamingSystem` whether or not
  there is ground (and `warp` works without it); terrain is generated for the ring when there is a
  generator. A `terrain` record names a built-in one, `Flat` or `Hills` (`seed`, `height`, `amplitude`,
  `wavelength`), and a scene's `"terrain"` gives the world that ground when it is placed; one that names
  none leaves the game's own (the Sandbox keeps its C# generator). A data-only game with the hills streams:
  the ground is generated around the player, a Ground-relative placement stands on it, and the hills follow
  the player east (test: TheDataOnlyWorldStreams). Both generators are functions of absolute position, so
  sectors meet without a step, and seeded (test: BuiltInTerrainIsSeamlessAndSeeded).
- **Crossing an edge** (`SectorOwnersSystem`, Late): a root whose sector is not its owner's moves. A runtime
  spawn's cell follows it; a placed entity leaves its sector (`ContentBaseline.Leave`: tombstoned there, so
  it is never placed twice) and becomes the new sector's runtime spawn. A goblin that walks from (0, 0)
  into (1, 0) stays when (0, 0) sleeps, sleeps and wakes with (1, 0), and is there once (test:
  ARootThatCrossesASectorEdgeChangesOwner). One that walks out of the ring sleeps in the sector it walked into.
- **Placement is budgeted, at the tick boundary.** `Scenes.TickEnded` runs after `World.RunFixed`'s phases
  (after a save asked for in the tick), never inside the fixed schedule: it puts to sleep what left the
  ring, then places what came in, nearest sector first, at most `stream_place_budget` entities a tick (default
  64); a sector half placed is finished on the next tick and becomes live only then. Ten stones with a budget
  of three arrive 3, 6, 9, 10 (test: PlacingASectorIsBudgetedAtTheTickBoundary); a save in between writes
  the half-placed sector's entities with their source, so a load does not take them for the game's own. A
  streamed scene at rest ticks without allocating, the owner check and the boundary included (test:
  AStreamedSceneAtRestAllocatesNothingPerTick). `stream_status` says how many sectors are placed.
- **A placed entity that left its sector keeps its placement** (issue #279, `KeptPlacement`). It is a
  runtime spawn from then on, so content does not place it again; its saved entry (in a save, or in the
  dormant cell it sleeps in) carries its placement's `overrides` and `outputs` beside its diff, a wire's
  `fired` count with it, and a load or a waking sector spawns it from its prefab *with* those overrides and
  wires it again. A house with child furniture whose `state_machine` part's `machine` an editor overrode
  crosses an edge, is saved, loaded, sleeps and wakes, and is the same house each time (test:
  AHouseWithAnOverriddenMachineRoundTripsTheEditorASaveAndASectorCrossing); a root content still places
  writes only its source, as before (test: OnlyARootNoContentPlacesWritesItsPlacement).
- **Limits.** What such an entity keeps is what its placement said when it left: a later edit of that
  placement's overrides or wires does not reach it (its sector has tombstoned it). A streamed
  placements document's entities are the sectors', not the document's (`FromPlacements` is not given), so
  the editor does not read them back. A runtime spawn made directly into a sector outside the ring sleeps
  there at the next boundary. Levels that
  stand on terrain are placed and dropped whole with the sector their `at` is in. Interiors, doors and
  travel came with 4g-5 (below).

### As built (doors, interiors and travel, issue 4g-5, 2026-10-01)
`scene_load` was a cheat that put the player at the scene's start, nothing said a scene was indoors, so a
crypt kept streaming terrain and lit by the sun, and fast travel was `warp <x> <z>`. Phase 4g's plan,
decision 1: an interior is a scene in the same world, swapped through dormancy (4g-1's cells). Code:
`src/Sage.Simulation/World/Travel.cs`, `Content/Scenes.cs` (`ApplySpace`, entries), `World/Streaming.cs`
(`SectorRing.Suspended`); experimental SAGE0129.

- **`Travel.To(world, scene, entry, hours)` runs at a tick boundary**, before a time skip and a save asked
  for in the same tick: no system later in the tick sees the scene change, the hours pass with it through
  `Time.Pass`, and a second journey asked for in the tick is refused (test: TravelRunsAtTheTickBoundary).
  The scene the player is in goes dormant with its state, the other is placed, the player is put at the
  *entry* — a placement's `name` (the scene's, its player start's or a placements document's) or a map
  entity's `targetname`, a point with no prefab included — facing its yaw, and the origin follows. Within
  one scene nothing is unloaded: the player moves and the ring follows on the next tick.
- **Warp's order.** When content says where the entry is (a placement), the origin moves to its sector and
  the ground there is generated before the scene is placed and the player stands on it; an entry in a map
  is found once the map is placed, and the origin then follows the player.
- **`"space": "interior"`** on a scene: the rings stop (`SectorRing.Suspended`), the terrain unloads, and
  the world is lit by its lights and `environment.ambient` only — no sun, no sky, no stars, no sun shadow;
  the sky system steps aside and the client's weather neither falls nor touches the light. Use the door
  (the Use action, through the interaction system) and the player is at the crypt's entry five kilometres
  east, the origin with them, the village's sectors asleep, the terrain gone and staying gone
  (test: UsingTheDoorPutsYouAtTheCryptEntryWithTerrainUnloaded). Coming back, the ground around the door
  is there before the player is, the light the village had is put back, the ring starts again, the wounded
  goblin is wounded still, and the skeleton killed in the crypt and the sword dropped there are dead and on
  the floor when the player goes back in (test: ComingBackYouAreAtTheDoorWithTheExteriorIntact). A save in
  the crypt loads into a game that started in the village as the crypt, terrain unloaded and lit inside, and
  the way out leads to the village as it was (test: SaveAndLoadInTheCrypt). An interior is never streamed.
- **Doors**: the `load_door` part (`sage:load_door`, not saved: content says it again) with `scene`,
  `entry` and `hours`. Using one travels (`Travel.Use`; only the player goes through), and so does the
  `Travel` input, which also takes `"<scene> <entry>"` sent to anything
  (test: TheTravelInputGoesThroughADoorOrToAnEntry). A door to a scene that does not exist, to an entry
  its scene does not have (with the nearest name), or a placed door with no scene is a load error, so
  `sage validate` reports it; a prefab's own door is checked when it names both, a placement's with its
  overrides over the prefab's (test: ADoorToAMissingSceneOrEntryIsALoadError).
- **Fast travel**: the `travel_point` part (`label`, `radius`, `discovered`). Coming within `radius`
  discovers it (`TravelPointSystem`, Late), which the component keeps and the saved `travel` resource
  (`TravelLog`) lists with its scene and absolute place, so a point is known while its sector sleeps.
  `travel <point>` (`Travel.ToPoint`) costs the straight-line distance from the player over `travel_speed`
  (metres per game hour, default 5000) and lands on the ground there, the ground generated first; a point
  not yet discovered cannot be travelled to (test: FastTravelAdvancesTheClockAndLandsOnTheGround).
- **Limits.** Only the player travels: companions and followers stay (4g-6 moves NPCs between spaces).
  The distance of a journey is measured from where the player stands, so from an interior it is from the
  interior's coordinates. An entry in a streamed scene must be a placement (a map on the terrain is placed
  with its sector, after the player arrives). `warp` refuses inside an interior. The kit's rest screen and
  map markers came with 4g-7 (below).

### As built (the kit's rest and fast travel, and the Sandbox's open world, issue 4g-7, 2026-10-01)
Phase 4g's plan, decision 8: rest rules and the screens are the RPG kit's (`Sage.Kits.Rpg`), content is
the game's, and the base only passes time and travels. Code: `src/Sage.Kits.Rpg/Rest.cs`, `RestView.cs`,
`MapView.cs`, the kit's `rpg:rest` screen (`content/data/ui_screens.json`); the Sandbox's
`content/data/open_world.json` and `tools/make_crypt.py`; experimental SAGE0129.

- **Resting is a kit rule over `Time.Pass`.** `Rest.Begin(world, who, Sleep | Wait, hours)` asks for the
  hours with the reason `rest` or `wait`; a sleep is refused while a hostile creature (an AI, alive, that
  `Factions` says is hostile) is within `rpg_conventions.restEnemyRange` metres, with the reason
  `@rpg.rest.enemies`, and waiting is not; neither goes past `restMaxHours`. Waking from a sleep applies
  `restEffect` with the hours slept as its magnitude, so a heal is content: `health +5` heals 40 over eight
  hours (test: SleepIsRefusedWithAnEnemyNearAndWaitingIsNot). Asked for inside a tick, it passes at the
  tick's end like any skip (test: ARestAskedForDuringATickPassesAtItsEnd). `rest <hours> [sleep|wait]` is
  the same call at the console.
- **The rest screen** (`rpg:rest` over `RestView`, opened by the kit's `Rest` action a game binds): the
  hours are a slider made of a bar between `less` and `more` buttons, within 1 to `restMaxHours`; `sleep`
  is disabled, with the reason beside it, while the rule refuses it, and enabled again once the enemy is
  gone; choosing it passes the hours, heals, and says so, with the clock's words (test:
  TheRestScreenSlidesTheHoursAndSleepsWhenNoEnemyIsNear). Open and read every frame, it allocates nothing
  (test: RefreshingTheRpgScreensAllocatesNothing).
- **Fast travel from the map.** `MapView` (the kit's `rpg_map`) makes each discovered travel point in the
  world's scene a marker you can choose (`canTravel`, and `travelHours`, `Travel.HoursTo` to a tenth): the
  point's own entity's marker when it is placed with a map marker (`sage:map_marker`), else a marker of
  its own where the saved `TravelLog` says it is (style `rpg:map_travel`), so a point whose sector sleeps
  stays on the map. `destinations` lists every discovered point in any scene; choosing a marker or a row
  travels there (`Travel.ToPoint`) and the journey costs its hours (test:
  TheRoadLeadsToTheWildsWhichStreamAndTheMapTravelsBack). The base gained `Scenes.Current(world)`, the
  scene the world is in, for this.
- **The Sandbox's open world**, in data: a crypt (`sandbox:crypt`, `"space": "Interior"`, a `.map` made
  by `tools/make_crypt.py`) behind a door on the hut's hill, lit by three torches and its ambient; sleeping
  is refused by the start, where a watcher stands, and allowed in the crypt; the way out puts the player on
  the steps (test: TheCryptIsAnInteriorLitByItsTorchesAndTheWayOutKeepsTheClock). A smith with a routine,
  at his anvil from 08:00 to 20:00 and by the west trees at night (test: TheSmithKeepsTheHoursOfHisForge).
  A road sign (a `load_door` with `"hours": 2`) to `sandbox:wilds`, a streamed scene four kilometres east
  whose camp is placed with its sector and whose far watcher only when the player goes past it, and
  waystones (`travel_point`s) the map travels between (test:
  TheRoadLeadsToTheWildsWhichStreamAndTheMapTravelsBack). `T` opens the rest screen; the smoke run goes
  into the crypt, reads the lights, rests and comes back out through fast travel.
- **Two fixes to 4g-5 the Sandbox found.** A journey no longer sets the clock to its scene's
  `environment.hour` (coming back out of the crypt at 02:30 into the Sandbox's main scene, which starts at
  18:30, made it dusk again; a world starting there and `scene_load` still set it) (test: GoingThroughADoorKeepsTheClock); and an entry that is a map entity's
  `targetname` is found as the player arrives: the destination's levels are placed and their entities
  spawned in the journey, where before the player was left where they stood (test:
  AnEntryInAMapIsFoundAsThePlayerArrives).
- **Limits.** The rest rule does not interrupt a rest (nothing ticks through a skip to come for you); a
  creature with no AI is never an enemy for it. Travel points with the same label in two scenes are told
  apart by `Travel.ToPoint`'s first match. The map leaves a fast-travel journey open on the map, now around
  the destination (a view-model cannot close its own screen).

## 4. API sketch
```csharp
public struct Origin { public SectorCoord Sector; }                     // world resource
public struct StreamingSource { public int Radius; public int LodRadius; }   // component (on the player pawn)

public sealed class WorldStreamer                                       // world resource/system set
{
    public SpaceId CurrentSpace { get; }
    public bool IsLoaded(SectorCoord s);
    public void TravelTo(SpaceId space, SectorCoord sector, Vector3 local);   // fast travel / doors
    public event Action<SectorCoord>? SectorLoaded, SectorUnloaded;           // engine-signal style, raised between ticks
}

public interface ITerrainGenerator { void Generate(SectorCoord s, Span<float> heights129x129, int seed); }   // game-provided
```

## 11. v1 scope vs later
- **v1:**
  - `SectorCoord` + `Origin` + rebasing;
  - streaming rings (full detail only);
  - procedural heightfield via a game-provided `ITerrainGenerator`;
  - terrain meshes + collision;
  - dormancy into the in-memory save cache;
  - one interior space type;
  - `stream_debug` overlay (sector grid, loaded/loading, origin);
  - log category `Streaming`.
- **Later:** LOD rings, HLOD for distant objects, splat texturing, several streaming sources, offline simulation, one-file-per-entity maps (09 §3.4).

## 12. Multiplayer-later notes
The server tracks one streaming source per player. Origin space becomes per client (camera-relative only), and physics would need per-region simulations for players far apart (10 Later). That's why simulation positions are sector + local rather than a single floating origin.

## 14. Build steps
1. `SectorCoord`/`Origin` + propagation relative to the origin + rebasing (with 03; TODO R6).
2. Sector load/unload with asset scopes and dormancy (TODO F14).
3. ~~Heightfield terrain: generator interface, meshes, collision~~ **Done 2026-09-22** (TODO F13, "As built"): generator + meshes in step 6; collision (`TerrainCollisionSystem`, `src/Sage.Physics3D/PhysicsSystems.cs`) with physics (F6).
4. Interiors as spaces + door transitions.
