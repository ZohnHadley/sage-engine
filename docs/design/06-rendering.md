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
- `SpriteRenderer` (component): sheet (`AssetPath`), material (`RecordId`), size in metres, pivot, billboard mode (`Cylindrical` = rotate about Y only, for characters and trees; `Spherical` = face the camera fully, for particles/effects), and optional `DirectionalSet` (8 directions).
- **Direction selection at extract:**
  1. `viewAngle = atan2(camera − sprite)` in the XZ plane.
  2. `relative = viewAngle − entityYaw`.
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

## 4. Public API sketch

```csharp
public sealed class Renderer                          // client module service (ctx.Provide)
{
    public MeshHandle CreateMesh(ReadOnlySpan<VertexPositionNormalTexture> v, ReadOnlySpan<uint> i, BoundingSphere bounds); // procedural (terrain chunks)
    public void UpdateMesh(MeshHandle h, ...);
    public void DestroyMesh(MeshHandle h);
    public RenderStats LastFrameStats { get; }
}

public readonly struct MeshHandle { }                  // GPU buffers owned by the Renderer (asset meshes resolve to one too)

public sealed class RenderSnapshot
{
    public PooledList<RenderView> Views;
    public PooledList<RenderItem> Items;
    public PooledList<SpriteInstance> Sprites;
    public LightSet Lights;
    public EnvironmentParams Environment;
    public PooledList<DebugPrimitive> Debug;
    public void Clear();                                // keeps capacity
}

public struct RenderItem { public MeshHandle Mesh; public int Submesh; public MaterialId Material; public Matrix World; public BoundingSphere Bounds; public ulong SortKey; public Vector4 Tint; }
public struct SpriteInstance { public Vector3 Center; public Vector2 Size; public Vector4 UvRect; public Vector4 Tint; public BillboardMode Mode; public MaterialId Material; public bool FlipU; }

// Components (simulation data, in Sage.Engine; no MonoGame types)
public struct MeshRenderer   { public AssetPath Mesh; public RecordId Material; public byte Layer; }
public struct SpriteRenderer { public AssetPath Sheet; public RecordId Material; public Vector2 Size; public Vector2 Pivot; public BillboardMode Mode; public bool Directional; }
public struct Camera         { public float FovY; public float Near; public float Far; public bool Active; }
public struct PointLight     { public Vector3 Color; public float Radius; public float Intensity; }

// In Sage.Engine (simulation code calls it too), so System.Numerics and Sage types only.
public static class DebugDraw                          // [Conditional("SAGE_DEV")] members
{
    public static void Line(Vector3 a, Vector3 b, Rgba c, float duration = 0, bool depthTest = true);
    public static void Box(Vector3 min, Vector3 max, Rgba c, float duration = 0);
    public static void Sphere(Vector3 center, float r, Rgba c, float duration = 0);
    public static void Text3D(Vector3 at, string text, Rgba c, float duration = 0);
}
```

Positions passed to `DebugDraw` are in origin space (the same space as `GlobalTransform`). `MaterialId` is the renderer's compact index for a material record (07). `RenderItem`, `SpriteInstance` and `Renderer` are client types and may use MonoGame types. The components (`MeshRenderer`, `SpriteRenderer`, `Camera`, `PointLight`) and `DebugDraw` live in `Sage.Engine`, so they use `System.Numerics` and the Sage `Rgba` colour struct. The debug API only queues data; the client drains the queue at extract.

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
| `src/Sage.Client/Rendering/ModelRendererSystem.cs`: iterates entities in `render()`, uses `BasicEffect`, calls `EnableDefaultLighting()` per mesh per frame, recomputes the world matrix per effect (TODO #25) | `MeshExtract` (reads `GlobalTransform` + `MeshRenderer`) + the opaque pass with material effects (07). Scale is part of the pose |
| `Game1.Draw`: sets `DepthStencilState.Default`, `RasterizerState.CullCounterClockwise`, clears to `DarkOliveGreen` | Pass setup (§3.4); the clear colour comes from `Environment` |
| `DevCamera`: `projectionMatrix`/`viewMatrix`, 45° FOV, near 0.01 / far 1000 | `Camera` component + `CameraExtract`. The editor's free-fly camera (15) is one camera rig. Near plane raised (0.01 is too small for depth precision at 1000 far; 0.1 suggested) |
| `TransformMath.Billboard` + the per-frame call in `Game1.Update` (TODO #26, #31) | `SpriteRenderer` with `BillboardMode` + direction selection at extract |
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
1. `RenderSnapshot` + Extract phase + camera extract; port `ModelRendererSystem` to `MeshExtract` + the opaque pass (TODO R9, #25).
2. Sort keys + material-based drawing (with 07).
3. Sprite batcher + `SpriteRenderer` + 8-direction selection (TODO F1; with 12).
4. Lighting/fog/ambient + render scale (TODO F2).
5. `DebugDraw` + `r_stats` + the overlay (TODO F5).
6. Instancing experiment behind `r_instancing` (later).
