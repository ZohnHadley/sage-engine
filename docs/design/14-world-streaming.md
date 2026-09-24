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

In `Sage.Engine` (data, terrain generation), with client parts for terrain meshes. Expanded when roadmap Phase 4 starts.

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
- **Unloading** serializes the sector's persistent entities into the in-memory save cache (**dormancy**), destroys them, and disposes the sector scope. Dormant entities don't simulate. Coarse "offline" simulation hooks (economy, travel) come later (F25/F26).
- **Terrain:** per sector a 129×129 height grid (8 m spacing) plus LOD meshes; vertex-coloured / single-texture v1; splat materials later (07).
- **Fast travel / teleport:** load the target sectors behind a loading screen, rebase the origin, place the player.

### As built (F13, 2026-09-22): one sector, no streaming
- **Code:** `src/Sage.Engine/World/Terrain.cs` (`SectorCoord`, `Heightfield`, `ITerrainGenerator`, `TerrainSector`, the `Terrain` world resource) and `src/Sage.Client/Rendering/TerrainMesh.cs` (`TerrainMeshSystem`).
- **Every world has a `Terrain` resource.** A game sets `Generator` and `Seed` and calls `Load(sector)`; the sector's 129×129 heightfield (8 m spacing over 1024 m) is generated on the main thread, once per sector.
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

- **Code:** `src/Sage.Engine/World/Origin.cs` (the `Origin` resource and `world.Rebase`),
  `src/Sage.Engine/World/Streaming.cs` (`StreamingSource`, `SectorOwned`, `StreamingSystem`,
  `StreamingModule`, `warp`), plus `Terrain` gaining `Unload`, `CornerOf` and origin-aware queries.
  Tests in `tests/Sage.Tests/World/StreamingTests.cs`.
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
- **Console:** `stream_status` (origin, loaded sectors, rebases so far), `stream_radius`,
  `stream_enabled`, `warp`.
- **Not yet:** LOD past the ring, per-sector asset scopes, **dormancy** (sectors currently keep only
  their terrain; entities placed in them are not yet saved and restored per sector — that needs maps,
  09 §3.4), generation on jobs, interiors as separate spaces, and seam-free normals across sector
  edges.

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
3. ~~Heightfield terrain: generator interface, meshes, collision~~ **Done 2026-09-22** (TODO F13, "As built"): generator + meshes in step 6; collision (`TerrainCollisionSystem`, `src/Sage.Engine/Physics/PhysicsSystems.cs`) with physics (F6).
4. Interiors as spaces + door transitions.
