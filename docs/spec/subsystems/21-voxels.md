# 21 · Voxel worlds

> Status: designed, not started. Owning assemblies (planned): `Sage.Simulation` (data, edits, saves),
> `Sage.Physics3D` (collision), `Sage.Client` (meshing upload and drawing). Phase 4s, epic #403.

## 1. Purpose and scope

A voxel volume is a grid of blocks that the player can dig, build and blow apart, as in Ace of Spades
(the Voxlap version) and Minecraft-likes. The owner named Ace of Spades as a game to emulate on
2026-10-02, so voxels become a base-engine feature rather than a game's code. A volume lives in the same
world as meshes, brushes, sprites and terrain; a game can use it for the whole world or for one
destructible wall.

Out of scope: smooth (marching-cubes) terrain, voxel global illumination and sparse voxel octree ray
casting. Voxlap itself was a software ray caster; Sage meshes chunks and draws them on the GPU, which
gives the same look on MonoGame.

## 2. Responsibilities

- Store block data in chunks, compactly, with a palette of block types (colour or material, solid or not, hardness).
- Generate volumes from a record (flat, heightmap, noise, an imported `.vox`/`.kv6`) and from authored data.
- Mesh chunks (greedy or culled faces) with per-face ambient occlusion, on a budget, and draw them.
- Apply runtime edits (set, remove sphere or box, paint) and remesh only the touched chunks.
- Detect blocks no longer connected to the ground and turn them into falling debris.
- Provide exact collision and ray queries against the grid.
- Stream chunks with the sector rings and save only the player's edits.

**Not responsible for:** weapon rules (the gameplay framework's hit pipeline calls voxel edits as an
effect), networking (later), smooth terrain (the built-in heightfield terrain stays).

## 3. Placement and dependencies

The data model, edit operations, connectivity and save diffs are simulation code in `Sage.Simulation`
with no MonoGame, so they are tested headlessly like everything else. Collision is a shape behind
`IPhysicsWorld` in `Sage.Physics3D`. Meshing produces vertex data in the simulation (testable), and
`Sage.Client` uploads and draws it through the existing mesh pass and materials. The editor's voxel
brushes live in `Sage.Editing` as commands. No kit is involved.

## 4. Interfaces (planned)

| Interface | Role |
|---|---|
| `voxel_volume` component and part | Places a volume: its record, origin, chunk size. |
| `voxel_palette` record | Block types: colour or material, solid, hardness, debris and impact cues. |
| `voxel_generator` record | Flat, heightmap, noise or imported source. |
| Voxel edit commands | Set block, remove sphere or box, paint; exposed as world commands, entity inputs (`VoxelBlast`, `VoxelSet`) and a condition/action word. |
| Voxel hit data | Raycasts and sweeps report block coordinates and the face hit. |
| Events | A `VoxelsChanged` event per edited region, so audio, particles and AI can react. |

## 5. Data model

Chunks of 32×32×32 blocks (configurable), stored as a palette index per block with run-length or
palette compression. A save stores per-chunk diffs against the generated or authored data, never the
whole volume, versioned like any saved resource (see [15-saves.md](15-saves.md)).

## 6. Lifecycle and data flow

Edits arrive as commands in the fixed tick and change the data at once, so collision and queries agree in
the same tick. Touched chunks are marked dirty; a meshing step in the frame takes dirty chunks up to a
time budget and hands meshes to the extract. Chunk generation runs off the main thread and joins at the
start of a tick. Streaming follows the sector rings of [14-world-and-streaming.md](14-world-and-streaming.md).

## 7. Threading, memory and performance

Targets: a 512×512×64 volume draws at 60 fps on the reference hardware; one edit remeshes within a frame
budget; a pooled chunk mesh allocates nothing per frame; a save after a thousand edits stays small.
Generation is the only work off the main thread, and it never touches the world directly.

## 8. Errors and diagnostics

Bad palette or generator records are load errors with file and line. Planned debug commands show chunk
bounds, dirty chunks and meshing time.

## 9. Requirements

| ID | Requirement | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-VOX-01 | The engine shall store voxel volumes in compressed chunks with a block palette. | Must | Not started | #404 |
| REQ-VOX-02 | The engine shall mesh chunks with hidden faces removed and per-face ambient occlusion, within a budget. | Must | Not started | #404 |
| REQ-VOX-03 | Volumes shall draw through the standard mesh pass with materials, culling and shadows. | Must | Not started | #404 |
| REQ-VOX-04 | Games shall edit volumes at runtime (set, remove sphere or box, paint) from commands, entity I/O and data. | Must | Not started | #405 |
| REQ-VOX-05 | Blocks disconnected from the ground by an edit shall fall as debris. | Should | Not started | #405 |
| REQ-VOX-06 | Characters shall stand, walk and step on voxels, and rays and sweeps shall report the block and face hit. | Must | Not started | #406 |
| REQ-VOX-07 | Collision shall reflect an edit in the same tick. | Must | Not started | #406 |
| REQ-VOX-08 | Saves shall store edits as per-chunk diffs and reload them exactly. | Must | Not started | #407 |
| REQ-VOX-09 | Chunks shall stream with the sector rings and generate off the main thread. | Must | Not started | #407 |
| REQ-VOX-10 | Designers shall paint and sculpt volumes in the editor, and `.vox`/`.kv6` shall import. | Should | Not started | #408 |
| REQ-VOX-11 | A data-only exit game shall dig, build, shoot and blast, and survive a save. | Must | Not started | #409 |

## 10. Open work

Phase 4s, Voxel worlds (epic #403):
- #404 4s-1 Voxel volumes: chunked data, meshing and rendering (P1)
- #405 4s-2 Runtime voxel edits: dig, build, blast, and falling pieces (P1)
- #406 4s-3 Voxel collision and queries (P1)
- #407 4s-4 Voxel saves and streaming (P1)
- #408 4s-5 Voxel tools and `.vox`/`.kv6` import (P2)
- #409 4s-6 Voxel exit game (P2)

Related: #410 richer materials and #315 shadows ([07-rendering.md](07-rendering.md)).

## 11. References

[SRS](../SRS.md) §3 (G5) and §6; [TDD](../TDD.md); [REDESIGN](../../REDESIGN.md) §5 (phase 4s);
[07-rendering.md](07-rendering.md), [08-physics.md](08-physics.md), [15-saves.md](15-saves.md).
