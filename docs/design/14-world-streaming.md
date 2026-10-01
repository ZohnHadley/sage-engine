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
- **Limits.** A placed entity that left its sector is a runtime spawn from then on: a load spawns it from
  its prefab, so its placement's `overrides` and wires are not reapplied (its saved diff is). A streamed
  placements document's entities are the sectors', not the document's (`FromPlacements` is not given), so
  the editor does not read them back. A runtime spawn made directly into a sector outside the ring sleeps
  there at the next boundary. Levels that
  stand on terrain are placed and dropped whole with the sector their `at` is in. Interiors, doors and
  travel are 4g-5.

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
