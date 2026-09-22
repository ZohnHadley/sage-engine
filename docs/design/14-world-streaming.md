# 14 — World Streaming and Large Worlds (short)

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
- **Not yet (F14):** streaming rings, LOD, per-sector asset scopes, dormancy, `stream_debug`, generation on jobs.

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
3. Heightfield terrain: generator interface, meshes, collision (TODO F13). **Generator + meshes done 2026-09-22** ("As built"); collision waits for physics (F6).
4. Interiors as spaces + door transitions.
