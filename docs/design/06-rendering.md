# 06 — Rendering

## 1. Purpose and scope
Turning the simulation's state into pixels:
- the Extract step and `RenderSnapshot`;
- views and cameras;
- culling, sorting, batching, the pass list;
- billboard sprites (8-direction), meshes and terrain chunks;
- lighting, fog, render scale;
- debug drawing and render stats.

All in `Sage.Client` (MonoGame).

Not in scope: shader code and material records (07), mesh/texture loading (05), sprite animation timing (12), terrain generation (14), UI (13).

## 2. Research basis
- An explicit hand-off from simulation to rendering: Bevy's Extract into a render world, UE scene proxies, Godot's RenderingServer, Destiny's buffered scene state (survey §2.2, §2.4, §2.5, §2.8). It enables pipelining later.
- A frame graph is valuable for many passes and async compute (Frostbite, UE RDG); **premature for a fixed forward pipeline on SM3** (survey §2.8). Sage uses a fixed pass list.
- Render interpolation between ticks: Fiedler (survey §3.1).
- Camera-relative rendering for large worlds: UE5 LWC, Daggerfall Unity floating origin (survey §3.3).
- MonoGame constraints (verified 2026-09, survey §3.7):
  - DesktopGL effects are `vs/ps_2_0` or `vs/ps_3_0` via MojoShader; no compute.
  - `DrawInstancedPrimitives` needs OpenGL 3.2+ and has bug reports on GL.
  - 3.8.5 adds DesktopVK and WindowsDX12 targets.

## 3. Concepts

### 3.1 Two worlds of data
The simulation never calls the renderer. Each frame, **Extract** systems (Frame schedule, `Extract` phase) read components and write a **`RenderSnapshot`**. The `Render` phase then draws **only from the snapshot**. This keeps rendering replaceable, lets rendering move to a different thread from the simulation later (01 §6), and makes "what did we draw this frame" inspectable.

### 3.2 `RenderSnapshot`
Pooled; its arrays grow but are never freed during play, so steady-state frames allocate nothing (02 §4.6).

| Buffer | Element | Filled by |
|---|---|---|
| `Views` | camera matrices, viewport, frustum, camera position (in origin space), render scale, clear/fog colour | `CameraExtract` |
| `Items` | `RenderItem`: mesh + submesh, material, world matrix (camera-relative), bounds, sort key, per-object params (tint) | `MeshExtract`, `TerrainExtract` |
| `Sprites` | `SpriteInstance`: centre (camera-relative), size, UV rect, tint, billboard mode, material | `SpriteExtract` |
| `Lights` | sun, ambient, point lights (position, radius, colour) | `LightExtract` |
| `Environment` | fog params, sky colours, time | `EnvironmentExtract` |
| `Debug` | lines, boxes, spheres, 3D text | `DebugDraw` queue |

### 3.3 Interpolation and camera-relative coordinates
- For each renderable, Extract interpolates `GlobalTransform.Previous → Current` with the frame's `alpha` (03 §3.6, 01 §5.2): lerp position and scale, slerp rotation. Then it builds the matrix.
- **Cameras are the exception.** The active camera is updated in `FrameUpdate` at the display rate (so mouse-look is smooth at any refresh rate) and extracted as-is, not interpolated.
- Everything is made **camera-relative** during extract (subtract the camera position). The view matrix then has no translation, and float precision is best exactly where the player looks. Combined with origin-sector rebasing (14), this keeps precision on Daggerfall-sized maps.

### 3.4 Passes (fixed order)

| # | Pass | Contents | Sorting | State |
|---|---|---|---|---|
| 1 | Opaque | meshes, terrain | by material, then front-to-back | depth write, cull back |
| 2 | Alpha-tested | sprites, foliage, fences | by material/texture, then front-to-back | depth write, alpha test in the shader (`clip`) |
| 3 | Sky | clear-colour gradient (v1), skydome later | — | depth test, no write |
| 4 | Transparent | glass, particles, fading sprites | back-to-front | depth test, no write, blend |
| 5 | Debug | `DebugDraw` primitives | — | optional depth test |
| 6 | Overlay | ImGui dev UI, console, game UI (13) | — | 2D |

Shadows (later) would be pass 0 into a shadow map. Lightmaps (later, HL1-style interiors) are a material technique (07), not a pass.

### 3.5 Sort key
A 64-bit key per item, compared as an integer:

```
opaque / alpha-tested:  [pass:4][layer:4][material:20][mesh:20][depth:16 front-to-back]
transparent:            [pass:4][layer:4][depth:24 back-to-front][material:20][mesh:12]
```

Sorting by material first minimises effect and texture switches; depth last gives early-z where it's cheap. Sorting uses an in-place radix sort on the pooled key array.

### 3.6 Culling
- v1: frustum vs bounding sphere per `RenderItem`/`SpriteInstance`, done during extract (culled items never enter the snapshot).
- Later: coarse culling per streaming cell (14), and distance culling per layer (small props fade out before large ones). Occlusion culling is not planned for SM3-era content.

### 3.7 Batching
- **Meshes:** consecutive items with the same mesh + material (after sorting) are drawn in a loop with only per-object parameters changing. With `r_instancing 1` and a capable device (OpenGL 3.2+ on DesktopGL, HiDef), runs of ≥ `r_instancing_min` (default 8) identical items become one `DrawInstancedPrimitives` call.
- **Sprites (v1):** CPU-expanded quads written into a large `DynamicVertexBuffer` (`SetData` with `NoOverwrite`/`Discard`), one draw per material/texture run, like `SpriteBatch` but in 3D. This works on every GL driver and avoids the reported instancing bugs. Sprite sheets are atlases, so hundreds of creatures and trees usually batch into a few draws.
- **Sprites (later):** instanced quads behind `r_instancing` once tested on target GPUs.
- Graphics profile: set `GraphicsProfile.HiDef` explicitly (32-bit indices, larger textures, required for instancing).

### 3.8 Billboard sprites and 8 directions
- **One facing axis, engine-wide (`SageMath`).** An entity's front is its local **-Z**, yaw 0 faces -Z, and positive yaw turns counter-clockwise seen from above. The sprite system used to read +Z as the front and the character controller bridged the two with a half turn, which quietly broke `LookAt`, authored `yaw` and every character's initial facing (review #43).
- **Direction groups are numbered Doom's way**, so a sheet cut for Doom or Daggerfall drops straight in ([Doom wiki: Sprite](https://doomwiki.org/wiki/Sprite)): rotation 1 is the thing seen head-on, and 2..8 follow it turning **45° clockwise at a time, seen from above** — rotation 2 is it "facing diagonally to the left of the player", 3 side-on facing the player's left, 5 back-on, 7 side-on facing the player's right. Our groups are 0-based, so Doom's rotation *N* is group *N-1*: **0 front, 2 its left side, 4 behind, 6 its right side**, the index growing as the camera moves toward the entity's left.
- **What that means for the art.** Project an entity's facing onto the screen's right axis and it comes out as `-sin(θ)`, where θ is how far the camera has swung toward the entity's left. So in group 1 the face points **left** on the sheet, in group 6 it points right, and anything it wears on its back moves the opposite way. The Sandbox's placeholder creature had that sign inverted — its nose swung the wrong way while the pack on its back swung the right way — which is the "the facing looks off" that kept being half-seen (review #54).
- **Quads are parallel to the view plane** (`Cylindrical` keeps world up and uses the camera's flattened right; `Spherical` uses the camera basis outright). This is what Doom and Daggerfall did, and Daggerfall Unity reached the same conclusion: turning each quad toward the camera's *position* looks slightly better at a distance but visibly wrong up close, where a sprite swings away as you walk past its origin — and you can walk right up to a tree ([Daggerfall Workshop forums](https://forums.dfworkshop.net/viewtopic.php?t=3605)). `r_sprite_facecamera 1` switches to position-facing to compare them.
- `SpriteRenderer` (component): sheet (`RecordId`), material (`RecordId`), size in metres (0 = the sheet's), sort layer, billboard mode (`Cylindrical` = rotate about Y only, for characters and trees; `Spherical` = face the camera fully, for particles/effects). How many direction groups there are is the **sheet record's** business (`sprite_sheet.directions`), not the component's.
- **Direction selection at extract:**
  1. `yawToCamera = SageMath.YawTo(sprite, camera)` (the XZ plane, in the engine's one yaw convention).
  2. `relative = yawToCamera − entityYaw`, where `entityYaw = SageMath.YawOf(rotation)`.
  3. `index = round(relative / 45°) mod 8`.
  4. The sheet maps index → frame group. Sets may store only 5 directions (front, front-side, side, back-side, back) and mirror the other 3 by flipping U, as Doom and Daggerfall did.
- **Animation frame** comes from the `SpriteAnimator` component's time and the sheet's animation table (12).
- **Quad orientation:**
  - v1: in the CPU batcher, from the view's right/up vectors (or world up for cylindrical).
  - later: in the vertex shader of `sprite.fx`'s `Instanced` technique (07), when instancing is enabled.

### 3.9 Lighting and atmosphere (forward, SM3-friendly)
- Per frame: one **sun** (direction, colour), **hemispheric ambient** (sky colour, ground colour), **fog** (linear or exp², colour, start/end/density).
- Per object: up to **4 point lights**, the nearest by influence, chosen during extract (`r_maxlights`, max 4 on SM3 constant budgets).
- Sprites use the same inputs. The normal faces the camera for cylindrical sprites, or the material can be marked unlit (+ fog), which is the Daggerfall look.
- **Render scale:** the scene can render into a lower-resolution target and be upscaled with point filtering (`r_scale 0.5`) for a retro look and cheap performance. UI always renders at full resolution.

### 3.10 Debug drawing
`DebugDraw` is a static API usable from any system on the main thread: `Line`, `Box`, `Sphere`, `Arrow`, `Text3D`, each with a colour, a duration and a depth-test flag. It's compiled only with `SAGE_DEV` (like `Log.Debug`). Primitives are queued, copied into the snapshot at extract, and drawn in pass 5.

### 3.11 As built (migration step 6)
- **Code:** `src/Sage.Client/Rendering/` (`RenderSnapshot.cs`, `RenderSystems.cs`, `Renderer.cs`, `MaterialCache.cs`, `SpriteBatcher.cs` — expands `SpriteInstance`s into quads in a `DynamicVertexBuffer`, §3.7 — and `TerrainMesh.cs` — `TerrainMeshSystem`, builds a sector's chunk meshes the frame it appears, 14 §3); components and the environment in `src/Sage.Engine/Rendering/RenderData.cs`.
- **Extract → snapshot → Render:** `ClientModule` installs a `RenderSnapshot` resource in every world, plus `CameraExtract` and `MeshExtract` (Extract phase, in that order) and `RenderSystem` (Render phase). Only Extract reads components; the `Renderer` draws only from the snapshot.
  - `MeshExtract`: `GlobalTransform` interpolated with alpha, camera-relative matrices, frustum culling per mesh part (bounding sphere), and sort keys (§3.5).
  - The Render phase clears to the environment's colour (the v1 sky), sorts, and draws opaque → alpha-tested → transparent in key order. Materials switch only when the material id changes.
- **Deviations and gaps:**
  - **Camera:** there's no `Camera` component yet. `CameraExtract` reads the world's `ActiveCamera` resource (position, rotation, fov, near 0.1, far 1000). A camera rig fills it when one is driving: `FirstPersonCameraSystem` (`src/Sage.Engine/Gameplay/CharacterController.cs`, FrameUpdate) puts it in the local pawn's head and sets `ActiveCamera.DrivenByRig`. The host's editor camera only writes position/rotation when no rig did (`!activeCamera.DrivenByRig`); the `cam_free` cvar clears `ActiveCamera.RigEnabled` to fly the editor camera instead, and the rig hands the camera back when it's set again (16 §3.2).
  - **Snapshot:** one view (`RenderSnapshot.View`), not a list.
  - **Sorting:** `Array.Sort` on the pooled key array, not a radix sort.
  - **Tint:** always 1 (no per-entity tint component yet).
  - **Allocations:** steady-state frames allocate nothing in these systems. Measured with `mem_warn_bytes 1`, the host allocates 176 B/frame, the same as before step 6 (TODO #41).
- **Meshes:** `MeshRenderer.Mesh` is an `AssetPath` to an MGCB model, loaded through the VFS (`ContentService`) until R12. Model bone transforms are now applied; the old `ModelRendererSystem` ignored them. The bunny's FBX root bone scales ×100 and turns 180°, so `Content.mgcb` builds it with `Scale=0.01`, and it now faces the way the file says.
- **Fallbacks:**
  - a missing mesh draws a 1 m magenta cube with `sage:error`;
  - a broken material draws as `sage:error` (07 §8).
- **Procedural meshes (F13):** `Renderer.CreateMesh`/`DestroyMesh` build GPU buffers the renderer owns and return a `MeshHandle`; a `MeshRenderer` with a handle draws it instead of an asset. Terrain chunks use this (14 "As built"), so they are ordinary mesh items: no separate `TerrainExtract` exists.
- **Sprites (F1, done):** `SpriteRenderer` + `SpriteExtract` + `SpriteBatcher`.
  - Extract picks the direction group (§3.8) and the animation frame (12), culls, and writes a `SpriteInstance`; the batcher expands the quads from the camera basis (world up for `Cylindrical`, the full basis for `Spherical`) into one `DynamicVertexBuffer`.
  - One draw per run of sprites sharing a material **and** a texture, so a sheet's creatures batch together. The sheet's texture overrides the material's `Albedo`, so `sage:sprite_default` serves every sheet.
  - Frame UVs are inset by half a texel, or the quad's edge samples the next frame in the atlas.
  - Meshes and sprites are interleaved by pass: each pass draws its meshes, then its sprites. Mixing *transparent* meshes and sprites by depth is not handled yet (nothing is transparent yet).
- **Not yet (v1 items left):** point lights, render scale, `DebugDraw`, `stat render`, `r_snapshot_dump`. Instancing stays "later".
- `GraphicsProfile.HiDef` is set by the host.

## 4. Public API sketch

```csharp
public sealed class Renderer                          // client module service (ctx.Provide)
{
    public MeshHandle CreateMesh(ReadOnlySpan<VertexPositionNormalTexture> vertices, ReadOnlySpan<int> indices, BoundingSphere bounds, string name = "(procedural)"); // procedural (terrain chunks)
    public MeshHandle CreateBox(Vector3 size, string name = "(box)");
    public void DestroyMesh(MeshHandle handle);
    public RenderStats LastFrame { get; }
}

public readonly record struct MeshHandle(int Id) { public bool IsEmpty => Id == 0; }   // GPU buffers owned by the Renderer (asset meshes resolve to one too)

public sealed class RenderSnapshot
{
    public RenderView View;                             // v1: one view, not a list
    public EnvironmentParams Environment;
    public PooledList<RenderItem> Items;
    public PooledList<SpriteInstance> Sprites;
    public int Culled;
    public bool HasView;
    public void Clear();                                // keeps capacity
}

public struct RenderItem     { public int Mesh; public int Part; public int Material; public Matrix World; public Vector4 Tint; public ulong SortKey; }
public struct SpriteInstance { public Vector3 Center; public Vector2 Size; public Vector2 Pivot; public Vector4 Uv; public Vector4 Tint; public int Material; public int Texture; public BillboardMode Mode; public ulong SortKey; }

// Components (simulation data, in Sage.Engine; no MonoGame types)
public struct MeshRenderer   { public AssetPath Mesh; public MeshHandle Handle; public RecordId Material; public byte Layer; }
public struct SpriteRenderer { public RecordId Sheet; public RecordId Material; public Vector2 Size; public BillboardMode Mode; public byte Layer; }
public struct Camera         { public float FovY; public float Near; public float Far; public bool Active; }   // not built: ActiveCamera is a resource, not a component (§3.11)
public struct PointLight     { public Vector3 Color; public float Radius; public float Intensity; }             // not built (§3.9)

// In Sage.Engine (simulation code calls it too), so System.Numerics and Sage types only. Not built (§3.10).
public static class DebugDraw                          // [Conditional("SAGE_DEV")] members
{
    public static void Line(Vector3 a, Vector3 b, Rgba c, float duration = 0, bool depthTest = true);
    public static void Box(Vector3 min, Vector3 max, Rgba c, float duration = 0);
    public static void Sphere(Vector3 center, float r, Rgba c, float duration = 0);
    public static void Text3D(Vector3 at, string text, Rgba c, float duration = 0);
}
```

Positions passed to `DebugDraw` are in origin space (the same space as `GlobalTransform`). `Material`/`Mesh`/`Texture` above are plain `int` ids — `MaterialCache`'s and the `Renderer`'s own compact indices — not wrapper structs. `RenderItem`, `SpriteInstance` and `Renderer` are client types and may use MonoGame types. The components (`MeshRenderer`, `SpriteRenderer`, `Camera`, `PointLight`) and `DebugDraw` live in `Sage.Engine`, so they use `System.Numerics` and the Sage types, not MonoGame types.

**Still design-only** (everything above this line is shipped): `RenderView` as a list (`Views`, for split screen, mirrors, shadow views — v1 has one `RenderSnapshot.View`); `LightSet`/`Lights` and `PointLight` (§3.9); the `Camera` component (the camera is the `ActiveCamera` resource today, §3.11); `PooledList<DebugPrimitive> Debug` and all of `DebugDraw` (§3.10); `Renderer.UpdateMesh` (only `CreateMesh`/`CreateBox`/`DestroyMesh` exist); `SpriteInstance.FlipU` as a field (the flip is folded into `Uv` at extract instead); per-object tint beyond the always-1 `RenderItem.Tint`/`SpriteInstance.Tint`.

## 5. Data flow (one frame)

```
FrameUpdate: camera rigs follow the pawn at display rate; sprite animators advance (12)
Extract:     snapshot.Clear()
             CameraExtract    → Views (camera-relative)
             MeshExtract      → Items   (interpolate GlobalTransform with alpha, frustum cull, sort key)
             TerrainExtract   → Items   (visible chunks from streaming, 14)
             SpriteExtract    → Sprites (interpolate, cull, pick direction/frame from sheet)
             LightExtract     → Lights  (sun, ambient, nearest point lights per item)
             DebugDraw drain  → Debug
Render:      radix sort Items → for each view: set frame params (07) → passes 1–5
Overlay:     ImGui dev tools, console, game UI
```

## 6. Threading and memory
- Extract and Render run on the main thread in v1. Extract only reads components; Render only reads the snapshot. That's the property that allows a later split (simulation thread → snapshot → main-thread render).
- All snapshot buffers, sort buffers and dynamic vertex buffers are pooled and reused. Material/state objects (`BlendState`, `RasterizerState`, `DepthStencilState`) are created once and cached, because MonoGame state objects become immutable after first use.
- GPU uploads of new assets go through the asset upload budget (05 §3.4). Terrain chunk meshes use the same budget.

## 7. File formats
None; rendering consumes assets (05) and material records (07). The sprite sheet format (`*.sheet.json`) is defined with animation (12).

## 8. Errors and fallbacks
- Missing or loading mesh/texture → the placeholder from 05 (the error mesh is drawn, never skipped silently).
- Missing material record → `sage:error` material (magenta, 07).
- Instancing requested but unsupported or failing → `Log.Once` warning, `r_instancing` forced to 0, loop draws used.
- Device lost/reset (MonoGame `DeviceReset`) → the renderer re-creates dynamic buffers and render targets; asset textures are managed by MonoGame.

## 9. Debug and tooling hooks
- **Built in step 6 / F1:** `r_fog`, `r_wireframe` and `r_freezecull` (both DevOnly + Cheat), `r_stats` (items, sprites, culled, draws, triangles, material switches), `mat_list`, `mat_info`, and the host's `screenshot [delay]` and `cam_set <x> <y> <z> [yaw] [pitch]` (DevOnly) commands, which is how the renderer is checked from fixed viewpoints.
- **Cvars:**
  - `r_instancing`, `r_instancing_min`;
  - `r_maxlights`;
  - `r_scale`;
  - `r_vsync` (01);
  - `r_fog`;
  - `r_wireframe` (DevOnly);
  - `r_drawbounds` (DevOnly);
  - `r_freezecull` (DevOnly: keep the current culling frustum while flying the camera);
  - `r_texfilter` (point/linear/anisotropic; point for the retro look).
- **Commands:**
  - `r_stats` (draw calls, triangles, items, sprites, culled, material switches, instanced runs);
  - `r_snapshot_dump` (writes the current snapshot as text to `user://logs`).
- **Overlay:** `stat render`.
- **Log category:** `Render` (setup, device resets, fallbacks). Per-frame logging only at `Trace`.

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `src/Sage.Client/Rendering/ModelRendererSystem.cs` + `ModelRenderer` + the `RenderView` resource (BasicEffect, TODO #25) | **Done (step 6):** `MeshRenderer` (Sage.Engine) + `MeshExtract` + the `Renderer`'s passes with material effects (07); the old files are deleted |
| Any procedural geometry (terrain chunks) | Built with `Renderer.CreateMesh`, wound **clockwise seen from the front**: MonoGame's default rasterizer culls counter-clockwise faces, so the other winding renders nothing |
| `Game1.Draw`: sets `DepthStencilState.Default`, `RasterizerState.CullCounterClockwise`, clears to `DarkOliveGreen` | **Done (step 6):** the Renderer clears to `RenderEnvironment.ClearColor` (the same green by default); render state comes from materials |
| `DevCamera`: `projectionMatrix`/`viewMatrix`, 45° FOV, near 0.01 / far 1000 | **Step 6:** `DevCamera` only moves a position and yaw/pitch; `CameraExtract` builds the matrices from `ActiveCamera` (near 0.1). A `Camera` component and rigs come with the pawn (16) |
| `TransformMath.Billboard` + the per-frame call in `Game1.Update` (TODO #26, #31) | **Done (F1):** `SpriteRenderer` with `BillboardMode` + direction selection at extract. `TransformMath.Billboard` stays for gameplay that wants an entity to *turn* (the Sandbox bunny) |
| `Game1.GuiRenderer` (ImGui) | The Overlay pass (13) |

## 11. v1 scope vs later
- **v1:**
  - Extract + pooled `RenderSnapshot`;
  - camera-relative interpolation;
  - passes 1–6;
  - the sort key;
  - per-item frustum culling;
  - loop batching for meshes, CPU-batched billboard sprites with 8-direction selection;
  - sun + hemispheric ambient + fog + up to 4 point lights;
  - render scale;
  - `DebugDraw`;
  - `r_stats`.
- **Later:**
  - instancing (after GPU testing);
  - shadows;
  - skydome;
  - lightmaps (HL1-style interiors);
  - particles;
  - post-processing;
  - streaming-cell culling;
  - a move to MonoGame DesktopVK once it's proven, which the snapshot boundary makes a contained change.

## 12. Multiplayer-later notes
Nothing changes: a client renders its own world's snapshot. A dedicated server doesn't load `Sage.Client` at all.

## 13. Open questions
- Should sprites be lit per-vertex with the nearest point lights, or unlit + fog only in v1? Default: **material decides**; Daggerfall-style content uses unlit + fog + ambient tint.
- Is SM3's constant budget enough for 4 point lights + fog + sun in one pass on DesktopGL/MojoShader? Verify with the first material. Fall back to 2 lights if not.

## 14. Build steps
1. ~~`RenderSnapshot` + Extract phase + camera extract; port `ModelRendererSystem` to `MeshExtract` + the opaque pass~~ **Done 2026-09-22** (ARCHITECTURE §7 step 6; TODO R9, #25).
2. ~~Sort keys + material-based drawing (with 07)~~ **Done 2026-09-22** (radix sort later).
3. ~~Sprite batcher + `SpriteRenderer` + 8-direction selection~~ **Done 2026-09-22** (TODO F1; with 12).
4. Lighting/fog/ambient + render scale (TODO F2).
5. `DebugDraw` + `r_stats` + the overlay (TODO F5). *`r_stats` done in step 6.*
6. Instancing experiment behind `r_instancing` (later).
