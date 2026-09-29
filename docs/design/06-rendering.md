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
| `Views` | camera matrices, target and viewport, frustum, camera position (in origin space), each view's runs of items, sprites, lights and debug lines (§3.4a) | `CameraExtract` |
| `Items` | `RenderItem`: mesh + submesh, material, world matrix (camera-relative), bounds, sort key, per-object params (tint) | `MeshExtract`, `TerrainExtract` |
| `Sprites` | `SpriteInstance`: centre (camera-relative), size, UV rect, tint, billboard mode, material | `SpriteExtract` |
| `Lights` | sun, ambient, point lights (position, radius, colour) | `LightExtract` |
| `Environment` | fog params, sky colours, time | `EnvironmentExtract` |
| `DebugLines` | pairs of `VertexPositionColor`, camera-relative and near-plane clipped | `DebugExtract` from the `DebugDraw` queue |

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

### 3.4a Views and render targets

**Built (issue #77, phase 4a).** A frame draws **several views**: the main view, a split-screen partner,
a minimap, a mirror, an editor viewport. Each is a `RenderView` in the snapshot's pooled `Views` list, a
value type with its own matrices, frustum and cull state, target, viewport and ranges.

- **Where views come from.** `CameraExtract` asks the client's `ViewSource` (`src/Sage.Client/Rendering/ViewSource.cs`),
  the one place that knows. It reads the world's `CameraViews` (#76, "As built (camera components)"
  below): one view per target, and a screen view made from `ActiveCamera` when no camera entity draws
  there. A world without that resource gets one screen view from `ActiveCamera`. Each view is built with the aspect ratio of **its own viewport**:
  half a screen or a square target is not stretched to the window's shape. Perspective and orthographic
  projections are both supported.
  A view can leave one entity out (`RenderView.Hidden`, filled by `ViewSource.HiddenFor`, nothing yet):
  the seam a first-person camera uses not to see its own body (#79).
- **Extract runs once per view.** `MeshExtract` and `SpriteExtract` interpolate an entity once and then
  cull, make camera-relative and key it once per view. Each item or sprite is tagged with its view.
  `LightExtract`, `ParticleExtract` and `DebugExtract` go a view at a time, so each view's lights and
  debug lines are one contiguous run of their lists. A sprite faces each camera separately, and the
  debug queue still ages once a frame.
- **Planning** (`RenderViewPlan`, `src/Sage.Simulation/Rendering/RenderViewPlan.cs`) is pure and has no
  graphics types, so a headless test can reach it:
  - **View order:** every view into an off-screen target first, grouped by target, then the screen's
    views. Within a target, a lower `Order` draws first, and ties keep their order
    (test: OffScreenTargetsDrawFirst_ThenTheScreen_EachByOrder).
  - **Item ranges:** a counting sort by view, then a sort by key within each view. Each view gets its
    own run of the order arrays, with each pass contiguous inside it
    (test: Bucket_GivesEachViewItsOwnRange_SortedByKey) (test: Bucket_KeepsEachPassContiguousWithinAView).
    The view can't be the top bits of the 64-bit key, because every bit is in use (§3.5).
  - Planning allocates nothing (test: Planning_DoesNotAllocate).
- **Drawing** (`Renderer.Draw`):
  - Each target is bound once and cleared whole to the sky colour. Depth is cleared too.
  - A view that covers only part of its target clears just its rectangle, through a scissor.
  - Each view then draws its range through the fixed passes: pass 1, 2, 4, then debug in pass 5.
  - The effects' frame-tier parameters are set **per view**: `ViewProj` differs between views.
  - The device goes back to the whole back buffer before Overlay. The **UI draws once, on the screen,
    after the views.**
- **Render targets** (decision D3):
  - The renderer owns a pool of **named** `RenderTarget2D`s. Each has one fixed size, colour plus
    depth, and `PreserveContents`.
  - Code declares a target with `Renderer.DeclareTarget(name, width, height)`. That returns the texture
    an editor viewport or a HUD draws. A target that only content names is made at 512×512 the first
    time it's used, and the log says so.
  - `FindTarget` and `ReleaseTarget` complete the API. All of it is `[Experimental("SAGE0123")]`.
  - A camera draws into a target by naming it. A **material samples a target as `rt:<name>`** in its
    `params`. That's a render target, not an asset, so no mount is asked for it
    (test: MaterialParam_RtName_IsARenderTarget_NotAnAsset).
  - Re-declaring a target at another size remakes it and rebuilds the materials that sample it.
  - A material is **not drawn into a target it samples**, because reading and writing one texture is
    undefined. It is skipped there, and the log says so once at Debug level.
- **One world draws to the screen** (`Renderer.ScreenWorld`). Before this, every world's `RenderSystem`
  cleared the back buffer, so with several worlds the last one won.
  - The screen world is the first world with a client, which is the host's main world, unless code
    sets another (an editor showing its edit world).
  - Other worlds drop their screen views at extract and draw only into render targets. Their UI queue
    is emptied without being drawn.
  - The host keeps the back buffer's contents across render-target switches (`PreserveContents`).
    MonoGame otherwise clears it to purple when a world that renders later binds a target and comes back.
  - A target drawn by a world that renders later is sampled a frame late by the screen world.
- **See it:** `r_testview 1` (a cheat, dev builds) splits the screen with a second view from behind.
  `r_testview 2` draws a top-down orthographic view into the target `testview` and shows it in the HUD's
  corner. `r_stats` prints the views, how many went into targets, and the pool's size. CI's Linux smoke
  run runs both modes. Under Xvfb the shaders aren't compiled, so that proves the code path and the
  clears, not the picture.
- **Allocations:** none added. Measured with `mem_warn_bytes 1` and `ui_entities 0` in the Sandbox, the
  host allocates 152 B/frame on `main` and 112–152 B/frame with `r_testview` 0, 1 or 2.

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
- Per object: up to **4 point lights**, the strongest by influence, chosen per draw by `LightRules.Nearest` (`r_lights`; 4 is what SM3's constant budget affords). Built — see "As built (point lights)" below.
- Sprites use the same inputs. The normal faces the camera for cylindrical sprites, or the material can be marked unlit (+ fog), which is the Daggerfall look.
- **Render scale:** the scene can render into a lower-resolution target and be upscaled with point filtering (`r_scale 0.5`) for a retro look and cheap performance. UI always renders at full resolution.

### 3.10 Debug drawing

**Built (2026-09-23, TODO F5).** `DebugDraw` is a **world resource** (`src/Sage.Simulation/Rendering/DebugDraw.cs`), not the static API sketched below: the engine has no static singletons (01 §4, 03 §3.4), and a per-world queue is what lets an editor's edit world and play world draw different things. Any simulation system can call it — `Line`, `Ray`, `Arrow`, `Cross`, `Box`, `Sphere`, `Capsule`, `Circle`, `Cone` — with a colour and an optional duration in seconds.

- **Everything becomes line segments in the engine**, so the client stays a line list and nothing in `Sage.Simulation` needs a graphics type. A capsule is two rings, four sides and four arcs; a sphere is three rings.
- **Runtime-gated, not compiled out.** Nothing is recorded while `Enabled` is false, which the client sets from `r_debugdraw` each frame, so a Shipping build pays one bool test per call and a Development build (optimised, the one you profile in) still has the tool. `[Conditional("SAGE_DEV")]` would have stripped the argument evaluation too, but it also strips it from Development.
- **Momentary shapes belong to the tick that drew them.** `World.RunFixed` clears them at the start of every tick, so a frame draws the newest state rather than every tick since it last looked — at 60 Hz against 50 fps that difference is a few hundred lines against sixteen thousand. A duration keeps a shape alive across frames, which is how you see something that happened in one tick (a swing that missed).
- **Depth is a cvar, not a per-shape flag**: `r_debugdraw_xray 1` draws through walls, which is what you want when the thing you are chasing is behind something. Shapes are drawn in pass 5, after the world and under the UI, and near-plane clipped at extract (an unclipped line with an endpoint behind the camera draws as a streak across the whole screen).
- **Consumers:** `phys_debug` (colliders, character capsules, ground normals, 10 §9), `ai_debug` (sight cones, targets, melee range, 16 §11), `combat_debug` (every swing and what it found, 16 §3.2). Each is off by default and costs one bool test.
- **Not built:** `Text3D` (there is no runtime text yet, 13), per-shape depth flags, and the visual logger (02 §12).

### 3.11 As built (migration step 6)
- **Code:** `src/Sage.Client/Rendering/` (`RenderSnapshot.cs`, `RenderSystems.cs`, `Renderer.cs`, `MaterialCache.cs`, `SpriteBatcher.cs` — expands `SpriteInstance`s into quads in a `DynamicVertexBuffer`, §3.7 — and `TerrainMesh.cs` — `TerrainMeshSystem`, builds a sector's chunk meshes the frame it appears, 14 §3); components and the environment in `src/Sage.Simulation/Rendering/RenderData.cs`.
- **Extract → snapshot → Render:** `ClientModule` installs a `RenderSnapshot` resource in every world, plus `CameraExtract` and `MeshExtract` (Extract phase, in that order) and `RenderSystem` (Render phase). Only Extract reads components; the `Renderer` draws only from the snapshot.
  - `MeshExtract`: `GlobalTransform` interpolated with alpha, camera-relative matrices, frustum culling per mesh part (bounding sphere), and sort keys (§3.5).
  - The Render phase clears to the environment's colour (the v1 sky), sorts, and draws opaque → alpha-tested → transparent in key order. Materials switch only when the material id changes.
- **Deviations and gaps:**
  - **Camera:** cameras are entities since #76 ("As built (camera components)" below), and since #77 `CameraExtract` draws every view the world's `CameraViews` holds (§3.4a); `ActiveCamera` is the `CameraDirector`'s mirror of the screen view, and the fallback for a world without `CameraViews`. `ActiveCamera` holds position, rotation, fov, near 0.1, far 1000. Since #78 the player's view is a camera entity too ("As built (camera rigs)" below): its `FirstPersonRig` puts it in the local pawn's head, and the director mirrors it into `ActiveCamera` and sets `ActiveCamera.DrivenByRig`. The host's editor camera only writes position/rotation when nothing did (`!activeCamera.DrivenByRig`); the `cam_free` cvar clears `ActiveCamera.RigEnabled` to fly the editor camera instead, and the director hands the screen back to the camera entity when it's set again (16 §3.2).
  - **Snapshot:** one view (`RenderSnapshot.View`) at first; a list of views since issue #77 (§3.4a).
  - **Sorting:** `Array.Sort` on the pooled key array, not a radix sort.
  - **Tint:** always 1 (no per-entity tint component yet).
  - **Allocations:** steady-state frames allocate nothing in these systems. Measured with `mem_warn_bytes 1`, the host allocates 176 B/frame, the same as before step 6 (TODO #41).
- **Meshes (as built, R12):** `MeshRenderer.Mesh` is an `AssetPath` to a `.glb`, opened off a VFS mount and read at runtime by `GltfLoader` (SharpGLTF) into vertex and index buffers the renderer owns and disposes. Each glTF primitive becomes one `MeshPart`; node transforms are **baked into the vertices** on load, so a part needs no bone matrix, and the winding is flipped **once, there** (glTF's front faces are counter-clockwise, this renderer culls the other way) rather than by a render state per draw. The `Scale=0.01` and 180° correction `Content.mgcb` applied to the old FBX went with the FBX: a file made for this engine needs no fixing up at build time.
- **Fallbacks:**
  - a missing mesh draws a 1 m magenta cube with `sage:error`;
  - a broken material draws as `sage:error` (07 §8).
- **Procedural meshes (F13):** `Renderer.CreateMesh`/`DestroyMesh` build GPU buffers the renderer owns and return a `MeshHandle`; a `MeshRenderer` with a handle draws it instead of an asset. Terrain chunks use this (14 "As built"), so they are ordinary mesh items: no separate `TerrainExtract` exists.
- **Sprites (F1, done):** `SpriteRenderer` + `SpriteExtract` + `SpriteBatcher`.
  - Extract picks the direction group (§3.8) and the animation frame (12), culls, and writes a `SpriteInstance`; the batcher expands the quads from the camera basis (world up for `Cylindrical`, the full basis for `Spherical`) into one `DynamicVertexBuffer`.
  - One draw per run of sprites sharing a material **and** a texture, so a sheet's creatures batch together. The sheet's texture overrides the material's `Albedo`, so `sage:sprite_default` serves every sheet.
  - Frame UVs are inset by half a texel, or the quad's edge samples the next frame in the atlas.
  - Meshes and sprites are interleaved by pass: each pass draws its meshes, then its sprites. Mixing *transparent* meshes and sprites by depth is not handled yet (nothing is transparent yet).
- **Not yet (v1 items left):** render scale, `stat render`, `r_snapshot_dump`. (Point lights landed later the same week — see "As built (point lights)".) Instancing stays "later".
- `GraphicsProfile.HiDef` is set by the host.

### 3.12 Particles (F39)
A particle is a billboard with a tint and a short life, so it is drawn by §3.8's path and simulated by
nobody else: `particle` records say how many, how fast, how long, and what colour they fade to; the pool
is flat arrays in `Sage.Simulation` (testable without a device); the client turns live particles into
`SpriteInstance`s in Extract. Two ceilings — per effect and across the world — and both are counted.
Emission comes from the events that already exist (a cue, a hit) or a `ParticleEmitter` component. See
"As built (particles and damage numbers)".

### 3.13 Weather (F40)
Weather is **one record and a blend between two of them**. A `weather` record says what falls and how
fast, how hard the wind drives it, what the fog and sky go, and how much of the sun is left; the world
holds what it was, what it is becoming, and how far between. Everything downstream reads that: the
particles, the environment, the sound of rain.

**It falls around the camera, not in the world.** Rain is not placed and belongs nowhere: a slab of sky
follows the player and is refilled every frame at the blended rate, which is the only affordable kind.
The `Box` shape and the volume come from the weather; how a drop behaves is the particle record's.

**The sun is scaled, not replaced.** A game sets the light it wants; weather says how much gets through,
so dusk and a storm compose instead of fighting, and three storms in a row do not darken the world three
times. See "As built (weather)".

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

// Components (simulation data, in Sage.Simulation; no MonoGame types)
public struct MeshRenderer   { public AssetPath Mesh; public MeshHandle Handle; public RecordId Material; public byte Layer; }
public struct SpriteRenderer { public RecordId Sheet; public RecordId Material; public Vector2 Size; public BillboardMode Mode; public byte Layer; }
public struct Camera         { public CameraProjection Projection; public float FovY; public float OrthoHeight; public float Near, Far;
                               public int Priority; public bool Enabled; public CameraViewport Viewport; public string Target; }   // built (#76, below)
public struct PointLight     { public Vector3 Colour; public float Range; public float Intensity; }            // built (§3.9)

// One light as the renderer wants it, camera-relative. `LightRules.Nearest` fills the four a draw carries.
public readonly record struct LightSample(Vector3 Position, Vector3 Colour, float Range) { public float InfluenceAt(Vector3 at); }
public static class LightRules { public const int PerObject = 4; public static int Nearest(ReadOnlySpan<LightSample> lights, Vector3 at, Span<LightSample> result); }

// In Sage.Simulation (simulation code calls it too), so System.Numerics and Sage types only. A world
// resource, reached with world.Debug() — see §3.10 for why it is not static.
public sealed class DebugDraw
{
    public bool Enabled;                               // the client sets this from r_debugdraw
    public void Line(Vector3 from, Vector3 to, uint rgba = DebugColour.White, float seconds = 0);
    public void Ray(Vector3 from, Vector3 direction, float length, uint rgba = DebugColour.White, float seconds = 0);
    public void Arrow(Vector3 from, Vector3 to, uint rgba = DebugColour.White, float seconds = 0);
    public void Cross(Vector3 at, float size = 0.15f, uint rgba = DebugColour.White, float seconds = 0);
    public void Box(Vector3 center, Vector3 halfExtents, Quaternion rotation, uint rgba = DebugColour.White, float seconds = 0);
    public void Sphere(Vector3 center, float radius, uint rgba = DebugColour.White, float seconds = 0);
    public void Capsule(Vector3 feet, float radius, float height, uint rgba = DebugColour.White, float seconds = 0);
    public void Cone(Vector3 at, float yaw, float degrees, float range, uint rgba = DebugColour.White, float seconds = 0);
}
```

Positions passed to `DebugDraw` are in origin space (the same space as `GlobalTransform`). `Material`/`Mesh`/`Texture` above are plain `int` ids — `MaterialCache`'s and the `Renderer`'s own compact indices — not wrapper structs. `RenderItem`, `SpriteInstance` and `Renderer` are client types and may use MonoGame types. The components (`MeshRenderer`, `SpriteRenderer`, `Camera`, `PointLight`) and `DebugDraw` live in `Sage.Simulation`, so they use `System.Numerics` and the Sage types, not MonoGame types.

**Still design-only** (everything above this line is shipped): `RenderView` as a list (`Views`, for split screen, mirrors, shadow views — v1 has one `RenderSnapshot.View`); `LightSet` as a per-view struct (the sun and ambient live in the snapshot's `EnvironmentParams`, and `RenderSnapshot.Lights` is a flat `PooledList<LightSample>`, §3.9); `Renderer.UpdateMesh` (only `CreateMesh`/`CreateBox`/`DestroyMesh` exist); `SpriteInstance.FlipU` as a field (the flip is folded into `Uv` at extract instead); per-object tint beyond the always-1 `RenderItem.Tint`/`SpriteInstance.Tint`.

## 5. Data flow (one frame)

```
FrameUpdate: camera rigs write CameraPose at display rate, then CameraDirector → CameraViews (+ ActiveCamera); sprite animators advance (12)
Extract:     snapshot.Clear()
             CameraExtract    → Views (camera-relative)
             MeshExtract      → Items   (interpolate GlobalTransform with alpha, frustum cull, sort key)
             TerrainExtract   → Items   (visible chunks from streaming, 14)
             SpriteExtract    → Sprites (interpolate, cull, pick direction/frame from sheet)
             LightExtract     → Lights  (every point light in range, camera-relative; the strongest four are picked per item at draw time)
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
  - `r_lights` (Archive; off is sun + ambient only);
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
| `src/Sage.Client/Rendering/ModelRendererSystem.cs` + `ModelRenderer` + the `RenderView` resource (BasicEffect, TODO #25) | **Done (step 6):** `MeshRenderer` (Sage.Simulation) + `MeshExtract` + the `Renderer`'s passes with material effects (07); the old files are deleted |
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
  - post-processing;
  - streaming-cell culling;
  - a move to MonoGame DesktopVK once it's proven, which the snapshot boundary makes a contained change.

### As built (particles and damage numbers, 2026-09-24 — F39)
Sparks, embers, smoke, blood, and the numbers over a fight.

- **Code:** `src/Sage.Simulation/Rendering/Particles.cs` (`particle` records, the `ParticleEmitter`
  component, the `Particles` pool) and `FloatingText.cs` (`FloatingTexts`, `DamageNumbers`); the client
  half is `src/Sage.Client/Rendering/ParticleSystems.cs` (`ParticleSystem`, `ParticleExtract`,
  `FloatingTextSystem`). Tests: `tests/Sage.Tests/Gameplay/ParticleTests.cs`.
- **The simulation is engine-side, the drawing is not**, the same split as the audio mixer: where a
  spark is after four tenths of a second is arithmetic anybody can check, so twelve headless tests cover
  emission, ageing, gravity, drag, the fade, both ceilings and the rebase — without a window.
- **A particle is not an entity.** There are thousands, they live for half a second, and nothing ever
  refers to one: they are flat arrays with a swap-remove, one buffer per effect, which is also what
  keeps a puff of smoke to a single draw call.
- **No renderer changes were needed.** A particle is a camera-facing quad with a tint, and the sprite
  path already draws exactly that — `ParticleExtract` writes `SpriteInstance`s with a transparent
  material, a whole-texture UV and a centred pivot. The `UnlitBlend` technique and `MaterialBlend.Additive`
  had both been written months earlier and never used; `sage:particle_additive` and `sage:particle_blend`
  are the records that finally use them.
- **One white dot is the whole texture budget.** `engine_content/textures/particle.png` is a 32××32
  radial fade, generated by `engine_content/tools/make_engine_textures.py`, and every effect tints it:
  embers orange, blood red, fire yellow-to-red. Additive for anything that glows, blended for anything
  that does not.
- **The simulation does not throw particles**, any more than it plays sounds. A **cue** names an effect,
  a **damage type** names an effect, and the client's `ParticleSystem` reads those events with its own
  cursors (04 §3.1) and decides what they look like. `ParticleEmitter` is the twin of `AudioSource` for
  things that smoke on their own, and a carried effect rides its owner and dies with it.
- **Damage numbers are the same idea for the part of a player that reads.** A `Damaged` event becomes a
  number that rises, drifts, fades and goes; its colour comes from the damage type (or a distinct one for
  what *you* took); the strings for 0–999 are built once, so a fight allocates nothing. `FloatingTextSystem`
  projects them with the engine's first and only world-to-screen, and draws them under the HUD.
- **Two ceilings, both counted:** `maxParticles` per effect so one spell cannot flood the screen, and a
  budget across everything (4000) so a hundred spells cannot either. `fx_stats` prints what is alive and
  what was turned away, per effect.
- **Console:** `fx_stats`, `fx_play <effect> [count]` (two metres in front of the eye, not inside your
  head), `r_particles`, `ui_damagenumbers`.
- **Particles turn.** `spinDegrees` was simulated from the first version and drawn by nothing, because
  the sprite batcher had no roll — a knob that costs cycles and does nothing is the "field nobody reads"
  mistake in a new coat. `SpriteInstance.Roll` now turns the quad about the view axis; sprites write zero
  and mean it.
- **Not yet:** particles that collide, animated sprite sheets over a life, soft particles, decals that
  stay (blood on the floor), and GPU simulation.

### As built (weather, 2026-09-24 — F40)
- **Code:** `src/Sage.Simulation/Rendering/Weather.cs` (`weather` records, the `Weather` state, `WeatherRules`)
  and `src/Sage.Client/Rendering/WeatherSystem.cs`. Tests: `tests/Sage.Tests/Gameplay/WeatherTests.cs`.
- **The numbers are engine-side and tested**: the blend, the fog and sun it implies, the rate and wind at
  any point through a change, and which of two kinds of precipitation is falling. What the client does
  with the answer is emit particles round the camera, write `RenderEnvironment`, and start a sound.
- **Changing your mind mid-storm starts from what the sky looks like now**, not from where the last
  change began — asking for sun a second into rain must not snap back to rain first.
- **One thing falls at a time.** Rain turning to snow changes over at the half-way point rather than
  overlapping, because two kinds of precipitation at once is a blend nobody asked for.
- **Wind is a world-level push on every particle** (`Particles.Wind`), not just the rain: the same gale
  leans a campfire's embers. A record may bake its own wind in on top.
- **Rain you cannot hear is a screen saver**, so a weather record names a 2D looping sound and the system
  starts and stops it as the weather takes and lets go (11 §3).
- **A missing weather id is a clear sky**, not a crash.
- **Console:** `weather` (what the sky is doing), `weather rain 8` (roll it in over eight seconds),
  `r_weather`.
- **Measured in the Sandbox:** a storm at 900 drops a second holds about 950 particles alive; the
  simulation costs 0.013 ms a frame and the extract 0.060 ms, inside a 0.26 ms render.
- **The state is the world's, the look is the client's.** `Weather` is installed with every `World`
  beside `RenderEnvironment`, so a headless server can be rained on and a quest can ask; only the system
  that makes it *look* like rain is client-side. It is saved, so the storm you walked into is part of the
  world you left.
- **Second pass (same day):** the weather system was assigning the particle record's `extents` every
  frame — one storm quietly editing an effect every other storm shares. A record is content and content
  is read-only at runtime, so the volume is passed to `Emit` instead. The baseline sky is now re-read
  whenever the weather is clear and settled, so a game that changes its own light (a day/night cycle
  will) is picked up rather than overwritten for ever. And with `snd_enabled 0` the rain loop was being
  made and killed sixty times a second, because the mixer stops everything each frame while muted.
- **Not yet:** rain that stops under a roof — F16 built the roofs, but the weather still falls in a slab that follows the camera and knows nothing about what is over it — puddles and wet surfaces,
  lightning, seasons or a clock that picks the weather, and weather that differs by region rather than
  by world.

### As built (point lights, 2026-09-24 — F2)
Interiors that look like interiors: F16 gave the engine rooms with roofs on them, and a roof is what a
sun cannot get past. The inside of the Sandbox's hut was a uniform dark grey box until this.

- **Code:** `src/Sage.Simulation/Rendering/Lights.cs` (the `PointLight` component, `LightSample`,
  `LightRules`), `LightExtract` in `src/Sage.Client/Rendering/RenderSystems.cs`,
  `EffectBinding.SetLights` in `MaterialCache.cs`, and `PointLights()` in
  `engine_content/shaders/common.fxh`. Tests: `tests/Sage.Tests/Gameplay/LightTests.cs`.
- **Four lights per draw, chosen per object.** The ceiling is the shader model DesktopGL gives us (07
  §2), and four is enough for a room, which is what this is for. `LightRules.Nearest` picks them from
  the frame's whole list for each item, because a wall and a lamp across the room want different
  answers.
- **Which four is a decision, so it is engine-side and headless.** The measure is influence, not
  distance: a bright lamp four metres away beats a candle at two, and picking by distance alone would
  light the room with the wrong thing. (test: TheBrightestNearbyLightWinsRatherThanTheClosestOne)
- **Out of range is absent, not dim.** The falloff is `(1 - d/range)²`, which reaches zero *at* the
  range rather than approaching it, so a light never contributes a hairline of colour across the map.
  (test: ALightOutOfRangeIsAbsentRatherThanDim)
- **The chosen four do not depend on what order entities arrive in.** Extract walks archetypes, and
  that order changes as things spawn and die; if the selection followed it, a wall would change its
  lighting when something unrelated was destroyed across the map.
  (test: TheOrderIsTheSameWhateverOrderTheLightsArrivedIn)
- **No lights means the shader adds nothing**, rather than multiplying by nothing: outdoors, the sun
  and the ambient are what light the world, and lamps are an addition to it.
  (test: NoLightsMeansNoneRatherThanBlack)
- **Camera-relative like everything else** (§3.3). Extract subtracts the camera position once, so the
  distance the shader needs is the length of the vertex's `Relative` with no further maths, and a lamp
  a sector away is not a float precision problem.
- **Lambert against the surface normal**, the same as the sun, so a wall facing away from a lamp stays
  dark and a room reads as a room rather than as a glowing fog.
- **`r_lights 0` is the old look** — extraction stops, the shader sees a count of zero, and the hut is
  a dark grey box again. Archive, because it is the kind of thing a player turns off once.
- **Content:** a `light` prefab part (`"light": { "colour": [...], "range": 8, "intensity": 1 }`,
  16 §3), registered as the `light` classname so a mapper places one in TrenchBroom like any other
  entity and `fgd_export` tells the editor about it. The Sandbox's hut has two lamps.
- **Second pass, day two: the lamps were not lit in the game at all, and every test passed.** Moving
  the `light` part into its own `LightsModule` (below) broke it: `Sage.Host/Program.cs` kept its own
  hand-written list of gameplay modules and never called `AddGameplay`, so the new module was added in
  every test and in no shipped build. The prefab part went unregistered, the lamp entities spawned with
  no `PointLight` on them, and the game said so on every boot — one `ERROR Records  sandbox:light: no
  prefab part 'light'` that nobody was reading. There is one list now (`GameplayModules.All()`, walked
  by both callers) and `ModuleSetTests` fails if a module is missing from it.
  (test: EveryGameplayModuleTheEngineDefinesIsInTheOneList)
- **`r_stats` counts lights**, because the three verification attempts before it were all somebody
  looking at a picture and deciding it seemed warmer — and a wooden floor in daylight reads exactly like
  lamplight. `lights 2 (max 2 on a draw)` does not. The count is what finally showed the lamps were
  missing, and then that `r_lights` was off.
- **`r_lights` is `Archive`, so `+r_lights 0` in a scripted run persists.** A comparison run wrote
  `r_lights "0"` into `user/<game>/config.cfg` and every run after it had lighting off, including the
  ones used to decide whether lighting worked. Worth knowing before trusting any screenshot taken after
  a scripted A/B: check the cvar, or read the number.
- **Second pass (same day), and it did not show in a screenshot.** `RenderSnapshot.Lights` was added
  as a field and left out of `Clear()`, so the list grew by every lamp every frame and every draw
  walked all of them — and the picture stayed *right*, because the extras were duplicates of the same
  two lamps. A list that is not cleared is not a bug you can see. `RenderSnapshot` now makes its pooled
  lists through a private `Pool<T>` that also records them, and `Clear()` empties what it recorded, so
  the next list cannot be forgotten. The `light` part also spent an afternoon registered in
  `AnimationModule` (where `sprite` lives); it has its own `LightsModule` now, which is what one module
  per feature means (16 §"As built (F7)").
- **Not yet:** lightmaps (the right answer for a large level, HL1's), shadows, sprites lit by point
  lights (`sprite.fx`'s `Lit` technique still takes sun + ambient only), light entities that switch or
  flicker through entity I/O, and any culling of the light list beyond what `LightRules` does per
  object — a hundred lamps is a hundred structs, and the work that matters is per *draw*. When that
  stops being true the answer is a grid, not a longer loop.

### As built (camera components, 2026-09-29 — #76)
Phase 4a's first piece (REDESIGN §5): cameras become entities, so rigs, several views, render targets and
the editor camera (#77–#81) have something to be. Nothing a game draws changed with #76 alone: the
renderer read `ActiveCamera` until #77 made it draw every view in `CameraViews` (§3.4a).

- **Code:** `src/Sage.Simulation/Camera/` — `Camera.cs` (`Camera`, `CameraPose`, `CameraProjection`,
  `CameraViewport`), `CameraViews.cs` (`CameraView`, `CameraViews`), `CameraDirector.cs`, `CameraMath.cs`,
  `CameraPart.cs` (the `camera` prefab part); installed in `Engine.CreateWorld`. Tests:
  `tests/Sage.Tests/Presentation/CameraTests.cs`. All of it is `[Experimental("SAGE0123")]`.
- **A camera is a `Camera` component** (`sage:camera`): `Projection` (`Perspective`/`Orthographic`),
  `FovY` in **degrees** (content writes it), `OrthoHeight` in metres of world top to bottom, `Near`/`Far`,
  `Priority`, `Enabled`, a normalised `Viewport` (0..1 from the target's top-left; empty = all of it)
  and a `Target` (a render target's name; empty = the screen). The `camera` part takes the same fields
  and starts from working defaults (enabled, 45°, the whole screen), which a zeroed struct does not have.
- **Where it looks from:** a rig's `CameraPose` (transient) when the entity has one, otherwise its
  `GlobalTransform` interpolated to the frame — so a fixed or scripted camera is just an entity you place
  and move. (test: ThePoseIsTheRigsWhenThereIsOne_ElseTheInterpolatedTransform)
- **Rigs (decision D2)** will live on camera entities that follow a pawn (#78, #79) and write `CameraPose`
  in FrameUpdate, declared `Before = new[] { CameraDirector.Id }`; the director reads them the same frame.
  (test: ARigOrderedBeforeTheDirectorIsSeenTheSameFrame)
- **The director** (`sage.camera.director`, FrameUpdate, owned by the engine and in every world an
  `Engine` makes, `"plugins": []` included) picks, per target, the enabled camera with the highest
  priority — a tie goes to the lower entity id — and writes the result into the `CameraViews` resource.
  (test: TheHigherPriorityCameraWins_AndDisablingItFallsBackToTheOther) Out-of-range values resolve to
  the defaults, so every view makes a matrix. (test: NonsenseResolvesToTheDefaults)
- **`CameraViews` is the renderer's contract (#77):** one `CameraView` per target (entity, target,
  viewport, position, rotation, projection, `FovY` in **radians**, `OrthoHeight`, near, far, priority);
  off-screen targets first by name and the screen **last**, so drawing in list order renders a target
  before the screen that shows it; `Main`/`MainIndex` is the screen view (-1 when there is none); pooled,
  and a frame allocates nothing. (test: ViewsAreOnePerTarget_OffScreenFirstByName_TheScreenLast)
  (test: ResolvingCamerasEveryFrameAllocatesNothing)
- **`ActiveCamera` stays, as a mirror (decision D1).** Once a camera entity draws to the screen, the
  director copies its view into `ActiveCamera` and sets `DrivenByRig`, so everything that reads it — audio,
  weather, particles, the HUD, `CameraExtract` — follows. (test: ActiveCameraMirrorsTheMainView_AndSaysSomethingDroveIt)
  With **no** camera entity the director leaves it exactly as it was and makes the screen view from it,
  measured against a world whose director is switched off. (test: WithNoCameraEntity_ActiveCameraIsLeftExactlyAsItWas)
  `cam_free` still wins the screen over any camera entity. (test: CamFree_GivesTheScreenToTheFreeCamera_AndLeavesActiveCameraToIt)
  `ActiveCamera.RigEnabled` and `DrivenByRig` are `[Obsolete]` for games; the engine still uses them,
  under scoped pragmas, until the first-person rig (#78) and the editor camera (#81) are camera entities.
- **Order, and a deviation from the issue.** The issue asked for the director *last* in FrameUpdate.
  It runs after the legacy first-person rig (`After = "?sage.character.camera"`) and otherwise first —
  the engine adds it before any plugin's systems — because the FrameUpdate systems that read
  `ActiveCamera` (audio, weather, particles, a game's HUD) would otherwise see last frame's mirror. It is
  last among the camera *writers*: anything that moves a camera in FrameUpdate orders itself before it.
- **`CameraMath`** builds the matrices in System.Numerics with the renderer's conventions (right-handed,
  looking down -Z, depth 0..1): `View`, `ViewRelative` (camera-relative, 06 §3.3), `Perspective`,
  `Orthographic` (the width follows the aspect: a wider window shows more world) and `Aspect` for a
  viewport of a target. (test: PerspectiveMatchesTheTextbookMatrix)
  (test: OrthographicMatchesTheTextbookMatrix_WidthFollowsTheAspect)
- **Origin rebasing** moves rig poses and the resolved views with everything else.
  (test: RebasingMovesRigPosesAndViews)
- **Since #77** every view is drawn, into its target and viewport, with its own projection (an
  orthographic screen camera draws orthographic), §3.4a.
- **Not yet:** rigs as camera entities, the first-person one moved over (#78), third-person (#79);
  scripted cuts from entity I/O (#80); the editor camera as an entity (#81). One view per target means
  split screen is two targets for now (the renderer itself draws several views into one target; the
  director resolves one). Pixel-perfect ortho (integer scaling, snapping) is REDESIGN §0.5's 2D work,
  not this.

### As built (camera rigs, 2026-09-29 — #78, #79)
The rigs of decision D2: the player's view is a camera entity that follows the pawn, and the pawn carries
no camera state. First person (#78), then third person over the shoulder with collision and the 1P/3P
toggle (#79).

- **Code:** `src/Sage.Simulation/Camera/Rigs/` — `CameraRigs.cs` (`FirstPersonRig`, the `PlayerCamera`
  tag, `CameraRigKind`, `world.MainViewRig()`), `FirstPersonRigSystem.cs`, `PlayerCameraSystem.cs`,
  `RigParts.cs` (the `first_person_rig` part); the prefab is `engine_content/data/camera.json`
  (`sage:player_camera`). The character plugin installs the systems (they replace
  `FirstPersonCameraSystem`, which is gone). Tests: `tests/Sage.Tests/Presentation/CameraRigTests.cs`.
  Experimental, SAGE0123.
- **The first-person rig** (`sage:first_person_rig`: `Follow`, `Enabled`; system `sage.camera.first_person`,
  FrameUpdate, before the director) writes its camera's `CameraPose` at the followed pawn's eye —
  `CharacterController.EyeOf`, the point the swing and the Use action start from, so all three agree —
  from its interpolated pose, looking along its `PawnIntent` view angles.
  (test: ThePlayerGetsACameraEntity_InItsHead_FromTheEnginePrefab)
  (test: ThePlayerHasWhatTheCameraRigLooksFor)
- **The player's camera** (`sage.camera.player`, FrameUpdate, before the rigs): every `PlayerControlled`
  pawn with no player camera following it gets one, spawned from `sage:player_camera` (a `camera` part and
  a `first_person_rig`; a game patches the record to change the player's view) and tagged
  `sage:player_camera`. A world without engine content (tests, tools) gets the same camera from the parts'
  defaults. (test: WithoutEngineContent_ThePlayerCameraIsBuiltFromThePartsDefaults) A game that wants no
  player camera disables the system by id or puts a camera of higher priority on the screen.
- **Saves.** The camera is saved when its pawn is, under a `PersistentId` derived from the pawn's
  (`camera:<pawn id>`), and a load rebuilds it from its prefab with `Follow` resolved to the rebuilt pawn:
  never two, never none. (test: SaveAndLoad_KeepExactlyOneCamera_FollowingTheRebuiltPlayer) A save from
  before #78 (no camera in it) gets one on the next frame, and a player camera left following nothing is
  relinked to the player rather than joined by a second.
  (test: ASaveWithoutACamera_GetsOne_AndAnOrphanIsRelinkedNotDuplicated)
- **"Am I looking out of the player's eyes?"** is `world.MainViewRig()` (`None`, `FirstPerson`), read from
  `CameraViews` after the director: the engine's crosshair and the Sandbox's viewmodel ask it instead of
  the obsolete `ActiveCamera.DrivenByRig`, so neither shows from a fixed camera, a cutscene or `cam_free`.
  (test: ACameraOfHigherPriorityTakesTheScreen_AndTheRigKindSaysSo)
- **`cam_free`** is unchanged: the director's special case gives the screen to the free camera and back.
  `RigEnabled`/`DrivenByRig` remain its protocol with the host until the editor camera is an entity (#81).
  (test: CamFree_FliesOverThePlayersCamera_AndGivesTheScreenBack)
- **Allocation:** a frame with the player's camera in its head allocates nothing.
  (test: ThePlayersCameraAllocatesNothingPerFrame)
- **The third-person rig** (`sage:third_person_rig`, the `third_person_rig` part; system
  `sage.camera.third_person`, FrameUpdate, after the first-person rig and before the director): from the
  followed pawn's eye, out by `ShoulderOffset` in the view's frame (x right, y up, z back) and back by
  `Distance` along the view, looking along the pawn's view angles — so the centre of the screen is where
  it aims, a shoulder-width aside. (test: ThirdPerson_SitsBehindTheShoulder_LookingWhereThePawnAims)
- **Collision.** A sphere of `ProbeRadius` is swept (`IPhysicsWorld.Sweep`) from the head toward that
  point against `CollisionLayers` (the part takes layer names, default `["default"]`: scenery, not
  creatures), ignoring the pawn. A hit pulls the camera in **at once**; when the way clears it eases back
  out over `Smoothing` seconds. (test: AWallBehindThePawn_PullsTheCameraIn_AtOnce_AndItEasesBackOut)
  **A physics query outside the tick is legal:** the physics world is single-threaded and steps only in
  `Phase.Physics`; FrameUpdate runs after the frame's ticks on the same thread and reads a settled world
  (the last tick's bodies against a pose interpolated up to one tick behind them — well inside the probe's
  radius at character speeds). A world without physics has no probe.
- **Origin rebasing.** The eased state is a length along the boom (`ThirdPersonRig.Boom`, transient), not a
  position, so a rebase has nothing of the rig's to move: `CameraPose` moves with the world and the easing
  carries on. (test: RebasingMovesTheThirdPersonCamera_WithoutAJump)
- **The 1P/3P toggle.** The `ToggleView` action (registered by the character plugin in `Init`; bound to
  V and the right stick's button in the engine's `gameplay` input map) is read from the tick's command
  in the Commands phase by `sage.camera.toggle_view`, which turns the player camera's two rigs' `Enabled`
  flags over (`ToggleViewSystem.Toggle(world)` from code). **Why two components, not a mode:** each rig is
  whole on its own (a chase camera is a `ThirdPersonRig` alone), the third-person settings survive the
  switch because nothing is removed, and flipping two booleans is no structural change, so the next frame
  draws the other view. The prefab starts in first person with the third-person rig off. Mid-swing in the
  Sandbox, the view switches the frame after the press and the fight (swings, health, positions, intent) is
  exactly what it is in a world that did not press it.
  (test: ToggleView_MidMeleeInTheSandbox_SwitchesWithinAFrame_AndCombatIsUnaffected)
  The choice is two saved flags on the camera, so a load comes back in the view the player left.
  (test: TheChosenViewIsSavedWithTheCamera)
- **The body.** `CameraRigs.HiddenBy(world, camera)` is the pawn a first-person rig sits in (unless its
  `ShowBody` is set) and nothing for any other camera; the renderer's per-view seam from #77
  (`ViewSource.HiddenFor`) asks it, so the first-person view does not draw its own body and the
  third-person view behind it does. The Sandbox's player has a placeholder body for that: the creature's
  sprite, with no animation (a clip with a `hit` event would time the player's swings).
- **The crosshair** shows for either rig (`MainViewRig()` is `FirstPerson` or `ThirdPerson`): the
  over-the-shoulder camera looks along the aim. The Sandbox's viewmodel (hands) is first person only.
- **The director** now clears `ActiveCamera.DrivenByRig` when the last camera entity lets go of the screen
  (it used to stay set, and the free camera never got the view back).
  (test: WhenTheLastCameraEntityLetsGo_DrivenByRigIsCleared)
- **Allocation:** the third-person rig and its probe, sweeping every frame against a wall, allocate
  nothing. (test: TheThirdPersonCameraAndItsProbeAllocateNothingPerFrame)
- **Deviations.** The director no longer orders itself after the legacy rig (`?sage.character.camera` is
  gone); the rigs order themselves before it. The eye is `CharacterController.EyeOf` rather than the old
  system's `Height + EyeOffset` (the same number for a standing character; it also covers a character
  whose height is not set yet). `FirstPersonCameraSystem` is removed from the public API (`*REMOVED*` in
  Sage.Simulation's `PublicAPI.Unshipped.txt`). The third-person rig's collision layers are stored as a
  mask's bits (`CollisionLayers`), not names, so a game that reorders `physics_layers` re-reads them from
  the prefab only for new cameras (a saved camera keeps its bits). The body hide is one entity: what the
  pawn holds is drawn unless hidden the same way.
- **Not yet:** a camera that turns the pawn toward where it looks in third person (strafe-only today, as
  in first person); orbiting the pawn freely while it stands (the view angles are the pawn's); a
  third-person crosshair that corrects for the shoulder's parallax; the editor camera as an entity (#81).

## 12. Multiplayer-later notes
Nothing changes: a client renders its own world's snapshot. A dedicated server doesn't load `Sage.Client` at all.

## 13. Open questions
- Should sprites be lit per-vertex with the nearest point lights, or unlit + fog only in v1? Default: **material decides**; Daggerfall-style content uses unlit + fog + ambient tint.
- ~~Is SM3's constant budget enough for 4 point lights + fog + sun in one pass on DesktopGL/MojoShader?~~ **Yes** (2026-09-24): `lit.fx`'s `Default` technique compiles and runs with all of it, and no fallback to 2 was needed.

## 14. Build steps
1. ~~`RenderSnapshot` + Extract phase + camera extract; port `ModelRendererSystem` to `MeshExtract` + the opaque pass~~ **Done 2026-09-22** (ARCHITECTURE §7 step 6; TODO R9, #25).
2. ~~Sort keys + material-based drawing (with 07)~~ **Done 2026-09-22** (radix sort later).
3. ~~Sprite batcher + `SpriteRenderer` + 8-direction selection~~ **Done 2026-09-22** (TODO F1; with 12).
4. ~~Lighting/fog/ambient~~ **Done 2026-09-22** (sun, hemispheric ambient, fog) and ~~point lights~~ **Done 2026-09-24** ("As built (point lights)"). Render scale left (TODO F2).
5. `DebugDraw` + `r_stats` + the overlay (TODO F5). *`r_stats` done in step 6.*
6. Instancing experiment behind `r_instancing` (later).
