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

### 3.4 Passes (stages; registered passes since 4h-1)

| # | Pass | Contents | Sorting | State |
|---|---|---|---|---|
| 1 | Opaque | meshes, terrain | by material, then front-to-back | depth write, cull back |
| 2 | Alpha-tested | sprites, foliage, fences | by material/texture, then front-to-back | depth write, alpha test in the shader (`clip`) |
| 3 | Sky | clear-colour gradient (v1), skydome later | — | depth test, no write |
| 4 | Transparent | glass, particles, fading sprites | back-to-front | depth test, no write, blend |
| 5 | Debug | `DebugDraw` primitives | — | optional depth test |
| 6 | Overlay | ImGui dev UI, console, game UI (13) | — | 2D |

Shadows (later) would be pass 0 into a shadow map. Lightmaps (later, HL1-style interiors) are a material technique (07), not a pass.

Since issue 4h-1 these rows are **stages**, and what draws in them are registered passes (`Shadow`, then per view `Opaque`, `AlphaTested`, `Sky`, `Transparent`, `Debug`, then `PostProcess`, then `Overlay`); see "As built (render passes)" below.

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
- **The editor viewport** (`ed_viewport 1`, dev builds, #81) is the first target code declares: a camera
  entity draws the editor's free camera into `editor`, and an ImGui window shows the texture
  ("As built (the editor's cameras)").
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
- **Render scale:** the scene can render into a lower-resolution target and be upscaled with point filtering (`r_scale 0.5`) for a retro look and cheap performance. UI always renders at full resolution. Built in issue 4h-6: "As built (post-processing)".

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
  - **Camera:** cameras are entities since #76 ("As built (camera components)" below), and since #77 `CameraExtract` draws every view the world's `CameraViews` holds (§3.4a); `ActiveCamera` is the `CameraDirector`'s mirror of the screen view, and the fallback for a world without `CameraViews`. `ActiveCamera` holds position, rotation, fov, near 0.1, far 1000. Since #78 the player's view is a camera entity too ("As built (camera rigs)" below): its `FirstPersonRig` puts it in the local pawn's head, and the director mirrors it into `ActiveCamera`. Since #81 so is the editor's free camera: a `DebugCamera` that draws only where no other camera does, and over every camera while `cam_free` is on; the host no longer writes `ActiveCamera`, and `ActiveCamera.RigEnabled`/`DrivenByRig` are inert ("As built (the editor's cameras)" below, 16 §3.2).
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
  - One draw per run of sprites sharing a material **and** a texture, so a sheet's creatures batch together. The sheet's texture overrides the material's `Albedo`, so `sage:sprite_default` serves every sheet. A run is also split where the lamps lighting it change (§"As built (point lights)").
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
times. With a `sky` record the weather adjusts the sky's values instead (4h-2, "As built (the world clock, sky records and weather composition)"). See "As built (weather)".

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
  - `r_scale` (Archive, 0.25 to 1; built, 4h-6), `r_post`, `r_post_grade`, `r_post_vignette` (Archive);
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
- **Sprites lit by the lamps (later):** `sprite.fx`'s `Lit` technique takes the sun (with its shadows),
  the ambient and the point lights, with wrapped (half-Lambert) shading because a billboard's normal
  only faces the camera. It is `sage:sprite_lit`'s technique, which a sheet opts into; `sage:sprite_default`
  (also named `sage:sprite_unlit`) stays full-bright. A run of sprites is split where its
  four lights change (`LightRules.SameSet`, `Renderer.DrawSprites`), so sprites away from any lamp still
  batch (test: TwoChoicesAreTheSameSetWhateverTheirOrder) (test: SpritesAreUnlitByDefault_AndLitIsOptIn).
- **Not yet:** lightmaps (the right answer for a large level, HL1's), shadows, light entities that switch or
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
  director copies its view into `ActiveCamera`, so everything that reads it follows.
  (test: ActiveCameraMirrorsTheMainView)
  With **no** camera entity the director leaves it exactly as it was and makes the screen view from it,
  measured against a world whose director is switched off. (test: WithNoCameraEntity_ActiveCameraIsLeftExactlyAsItWas)
  Until #81 `cam_free` was a special case here, and `ActiveCamera.RigEnabled`/`DrivenByRig` its protocol
  with the host; both are gone ("As built (the editor's cameras)").
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
  scripted cuts from entity I/O (#80, done: "As built (scripted cameras from entity I/O)"); the editor camera as an entity (#81, done: "As built (the editor's cameras)"). One view per target means
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
- **`cam_free`** flies the editor's free camera over the player's and gives the screen back; since #81
  that camera is a `DebugCamera` and the director has no special case for it ("As built (the editor's
  cameras)"). (test: TheEditorsFreeCamera_FliesOverThePlayersCamera_AndGivesTheScreenBack)
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
  V and the right stick's button in the engine's `gameplay` input map) is read from the followed pawn's
  `PawnIntent` in the Commands phase by `sage.camera.toggle_view` — after the controller and after a
  scripted camera's input lock (#80), so a cut that holds the player's buttons holds this one
  (test: ALockingScriptedCameraHoldsTheToggle) — which turns the player camera's two rigs' `Enabled`
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
- **The director** cleared `ActiveCamera.DrivenByRig` when the last camera entity let go of the screen
  (it used to stay set, and the free camera never got the view back). Since #81 nothing reads the flag:
  a camera that lets go leaves `ActiveCamera` where it was, and the screen view is made from it again.
  (test: WhenTheLastCameraEntityLetsGo_ActiveCameraStaysWhereItWas)
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
  third-person crosshair that corrects for the shoulder's parallax.

### As built (scripted cameras from entity I/O, 2026-09-29 — #80)
Phase 4a's exit criterion "a scripted camera cut from I/O": an HL1 scene cuts to a placed camera when a
trigger fires, and back.

- **Code:** `src/Sage.Simulation/Camera/CameraIO.cs` — `ScriptedCamera` (`sage:scripted_camera`), the
  `scripted_camera` part, the inputs and outputs (registered in `Engine`'s constructor), and two systems
  added in `Engine.CreateWorld` beside the director: `sage.camera.scripted` (EntityIO, before
  `sage.io.dispatch`: holds running out) and `sage.camera.input_lock` (Commands, after
  `sage.character.player_control`). Engine content: `engine_content/data/cameras.json`. Tests:
  `tests/Sage.Tests/Presentation/CameraIOTests.cs`. Experimental (SAGE0123) with the rest.
- **Inputs `CameraOn [hold]` / `CameraOff`, outputs `OnCameraOn` / `OnCameraOff`**, on any entity with a
  `Camera`. They toggle `Camera.Enabled`; the camera then wins or loses the screen by priority like any
  other, so "back" means the next camera down — the player's camera (#78, priority 0), or `ActiveCamera`
  in a world without one. The outputs fire on a change only: `CameraOn` at a camera that is on restarts
  its hold and fires nothing. (test: CameraOnTakesTheScreen_CameraOffGivesItBack_AndTheOutputsFireOnlyOnAChange)
  The names are camera-specific because `EntityInputs` is one global, case-insensitive table.
- **The hold:** `CameraOn`'s parameter in seconds (> 0: off again after it, firing `OnCameraOff`; 0: until
  `CameraOff`); with no parameter, the entity's `ScriptedCamera.HoldTime`. A hold on a camera with no
  `ScriptedCamera` adds one; a parameter that is not a number is warned about and ignored; `CameraOff`
  cancels a running hold, so the next cut is not ended early by the last one's.
  (test: TheHoldComesFromTheWire_ElseTheEntity_AndCameraOffOrARestartCancelsIt)
  (test: AHoldOnACameraWithoutTheCompanionGivesItOne_AndABadParameterFallsBack)
  It counts down in fixed ticks (paused with the simulation) before each tick's dispatch, so a hold of N
  seconds ends on the tick a `CameraOff` queued with a delay of N at the same moment arrives.
  (test: AHoldTurnsTheCameraOffOnTheTickADelayedCameraOffWouldArrive)
- **Which frame the cut lands on (decision).** Entity I/O is delivered in the `EntityIO` phase of a fixed
  tick; the director runs in `FrameUpdate`. So: the physics step of tick N sees the overlap, PostPhysics
  fires `OnStartTouch`, a wire with no delay is delivered in tick N's `EntityIO` phase, and **the first
  frame after tick N** shows the camera — the same host frame, since a frame runs its ticks before its
  Frame phases. Turning off is the same: the frame after the tick that delivered `CameraOff` (or in which
  the hold ran out) shows the player's view. (test: TheCutLandsOnTheFrameAfterTheTickThatDeliveredIt)
  (test: ATriggerCutsToANamedCameraAndBack_InAGameWithNoCode) An output the camera fires in reply
  (`OnCameraOn`) is a tick later, as every chained wire is (04 §3.4a).
- **A cut, unless asked for a blend.** Since #90 `CameraOn`'s parameter may carry a blend time after the
  hold ("As built (camera blends)" below); without one it is the cut described here.
- **The input lock** is `ScriptedCamera.LockInput`: while a camera with it is on and draws to the screen,
  the local player's `PawnIntent` loses its movement and buttons after the controller writes it, and its
  view angles are held where the lock began — asked of the host each tick with `PlayerInput.RequestView`,
  so the mouse does not wind the view round behind the cut. AI and other pawns are untouched. Headless
  tests see exactly what a played game does, because the gate is in the Commands phase, not the client.
  (test: ALockingCameraHoldsThePlayersIntent_AndLetsGoWhenItIsOff)
- **The prefab `sage:scripted_camera`** is a `camera` part that starts off at priority 100 plus a
  `scripted_camera` part with `lockInput`: place it, name it, wire it.
  (test: TheEnginePrefabIsAnOffHighPriorityCameraThatLocksInput)
- **Ownership: `sage.core`**, like the `camera` part and the director: the inputs only touch components
  the engine owns, so every game has them whatever plugins it lists, a data-only game needs no extra
  plugin id to wire a cut, and entity I/O (`sage.gameplay.io`) does not have to know about cameras.
  Delivering them still takes that plugin, as every wire does. (test: TheInputsOutputsAndSystemsAreTheEngines_InAGameWithNoPlugins)
- **Wiring without a map** (a deviation the acceptance test needed): until now only a `.map` could wire
  I/O, so a game with no C# and no maps could not be proven. A placement (in a scene or a placements
  document) now has `outputs`, the five Hammer fields by name; each placed entity gets its own copies;
  an input nobody registered is a load error at the file and line, an undeclared output a warning; targets
  bind by name when they fire. And a `.map` classname may be a full id, so a mapper places
  `sage:scripted_camera` directly. (test: AScenePlacementsOutputsAreWiredAndChecked)
  (test: AMapPlacesTheEnginesScriptedCameraByItsQualifiedClassname)
- **`tests/games/camera-cut`**, a game with no C# (character and I/O plugins): a crate falls through the
  yard trigger as it starts (a `CameraOn` and a `CameraOff` two seconds behind it), and walking into the
  gate cuts to the intro camera for a three-second hold that says so through its outputs. CI validates it,
  generates schemas from it and smoke-runs it in the real host. (test: TheCameraCutGameValidates)
- **Zero allocation per tick** with holds running and the player locked. (test: CountingDownAndLockingAllocateNothing)
- **Not yet:** a camera that looks *at* something (a target to track) — a scripted camera
  looks where it is placed, or where a mover or a rig takes it (or a tween, since #90); letterboxing or a
  HUD hidden during a cut; the editor showing a camera's frustum and wiring (phase 10's editor host). A
  wire's delay fired from outside the dispatch (a trigger's `OnStartTouch`, in PostPhysics) was counted
  from the previous tick's I/O clock until #90, which moved the clock to the start of the tick (04 §3.4b):
  a `CameraOff` wired two seconds behind a `CameraOn` from the same trigger now arrives after 120 ticks.

### As built (the editor's cameras, and phase 4a's exit, 2026-09-29 — #81)
The last piece of phase 4a: the host and the editor move onto camera views, the editor gets a viewport
that is a render target, and the Sandbox shows both exit criteria.

- **The free camera is a camera entity.** `DebugCamera` (`src/Sage.Simulation/Camera/DebugCamera.cs`,
  SAGE0123) is a `Camera` plus a `CameraPose` that a tool writes by hand, before the frame's FrameUpdate:
  `DebugCamera.Spawn(world, name, target)` and `DebugCamera.Drive(world, camera, position, rotation,
  overriding)`. Overriding, it is at `DebugCamera.OverridePriority` (`int.MaxValue`), above every camera a
  game places, a scripted cut included; otherwise at `IdlePriority`, below every camera and above only
  the director's `ActiveCamera` fallback. `DevTools` (`src/Sage.Editor/DevTools.cs`) spawns the
  "editor free camera", drives it from `DevCamera` every frame and overrides with `cam_free`.
  (test: ADebugCameraOverridesTheScreen_OverEveryCamera_AndGivesItBack)
  (test: TheEditorsFreeCamera_FliesOverThePlayersCamera_AndGivesTheScreenBack)
  Idle, it has the screen only where nothing else draws: a world with no player, where the free camera
  flew before #81 too. (test: AnIdleDebugCamera_DrawsOnlyWhereNothingElseDoes)
- **No special case in the director.** It no longer reads `ActiveCamera.RigEnabled` or writes
  `DrivenByRig`, and the host no longer writes `ActiveCamera` (it had a Shipping branch that reset
  `RigEnabled` every frame; it is gone). Every `#pragma warning disable CS0618` naming #78 or #81 is
  removed. The director mirrors the free camera into `ActiveCamera` like any camera, so audio and gameplay
  follow it as before; it is not a rig, so `MainViewRig()` is `None` (no crosshair, no hands, and the
  player's body drawn). Turned on, `cam_free` starts from the view that had the screen rather than from
  wherever the free camera was left (unless `cam_set` just placed it, or the viewport has been showing it);
  hidden, the free camera no longer follows the player's WASD.
- **Decision: `ActiveCamera.RigEnabled` and `DrivenByRig` stay, `[Obsolete]` and inert.** They shipped
  in 0.1.0 (`PublicAPI.Shipped.txt`) without the attribute, which #76 added after that release: a 0.1
  game that sets them should get a warning from the next release, not an error. Nothing in the engine
  reads or writes them now, and their messages say so. They are removed (`*REMOVED*` lines) in the minor
  after the one that first ships them obsolete (RELEASING.md's 0.x rule: a minor may break).
- **What follows the main view.** `world.TryGetMainView(out CameraView)` (`MainViewExtensions`, SAGE0123)
  is the screen's view — `CameraViews.Main` once the director has run, else one made from `ActiveCamera`,
  else false. (test: TryGetMainView_IsTheScreensView_ElseActiveCamera_ElseNothing) The audio listener,
  the weather around the eye, `fx_play` and `ent_spawn` ask it instead of `ActiveCamera`, which stays the
  mirror (decision D1) for gameplay that reads it in the fixed tick. The HUD already asked
  `MainViewRig()` (#78); the Sandbox's HUD hides its Use prompt and its view hint while a cut or the free
  camera has the screen.
- **The editor viewport** (`ed_viewport 1`, or View → Viewport): a second debug camera, the "editor
  viewport camera", draws the free camera's view into the render target `editor`
  (`Renderer.DeclareTarget("editor", 480, 270)`) and an ImGui window shows it through the ImGui renderer's
  texture binding (`BindTexture`, rebound when the target is remade). Click it, then WASD and right-drag
  fly the free camera there while the game goes on in the player's view. It is the prerequisite REDESIGN
  §4.6's editor host builds on: an editor's view of a world is a camera entity and a named target, not a
  renderer path of its own. Dev builds only; the host reaches the renderer through `ClientModule.Renderer`.
  The View menu's items are cvars, like every menu item (`cam_free`, `ed_viewport`).
- **The window's size** is `vid_width`/`vid_height` (Archive; 800×410 as before), applied when they change,
  so `+vid_width 1600 +vid_height 900` on the command line or in `config.cfg` sizes it.
- **Phase 4a's exit, in the Sandbox.** *1P↔3P mid-fight:* V is on the HUD (top right, "V third person" /
  "V first person", while the player's rig has the screen) and in the README's controls; switching mid-swing
  changes nothing about the fight. (test: ToggleView_MidMeleeInTheSandbox_SwitchesWithinAFrame_AndCombatIsUnaffected)
  *A scripted cut from I/O:* the scene places a `hut_path` trigger in front of the hut's door and a
  `sage:scripted_camera` named `hut_cam` beside the path; the trigger's `OnStartTouch` wire turns the camera
  on for three seconds, once, and the camera's `OnCameraOn` says what it shows. The trigger is on a
  `player_zone` physics layer the Sandbox adds (a `physics_layers` patch with `layers+`) that ignores every
  layer but the player's, so the creatures wandering the scene never fire it — HL1's `trigger_once`.
  (test: TheSandboxCutsToTheHut_WhenThePlayerWalksUpThePath_AndBack) CI smoke-runs the viewport,
  `cam_free` and the cut in the real host.
- **SAGE0123 stays experimental (decision).** The camera API has no consumer outside 4a yet: 4b's tweens
  will blend between views, 4c's UI toolkit will draw render targets in widgets, and phase 10's editor host
  will own the viewport. Its shape should settle against those, not before; the id leaves when 4b and 4c
  have used it.
- **Not yet:** an editor viewport that picks, shows camera frustums and wiring, or looks at
  another world (the edit world is phase 10's); the free camera on Editor-context actions (08 §14 step 4);
  resizing the viewport with its window (it is a fixed 480×270 target, scaled).

### As built (camera blends, 2026-09-30 — #90)
What 4a deferred to 4b's tweens: a scripted camera eased in rather than cut to.

- **Code:** `src/Sage.Simulation/Camera/CameraBlend.cs` — `CameraBlend` (`sage:camera_blend`),
  `CameraBlends.Begin`/`End`, and `sage.camera.blend` (EntityIO, before `sage.io.dispatch`, added beside
  the director); `CameraOn`'s parameter in `CameraIO.cs`; the director applies a blend in
  `CameraDirector.Run`. Curves are `Easing` (`src/Sage.Simulation/Logic/Easing.cs`). Tests:
  `tests/Sage.Tests/Presentation/CameraBlendTests.cs`. Experimental (SAGE0123) with the rest.
- **`CameraOn [hold [blend [ease]]]`**, space-separated (a `.map` wire is comma-separated): the second
  number is a blend time in seconds and the word after it an easing curve (`SineInOut`, `ease_out_quad`;
  `Easing.TryParse`). Without them, the entity's `ScriptedCamera.BlendTime` and `BlendEase` (part fields
  `blendTime`, `blendEase`, which is `SmoothStep` unless it says otherwise); `BlendTime` is 0 unless set,
  so **a cut stays the default**, and a `0` on the wire cuts a camera whose own default blends. A word that
  is not what it should be is a warning and the default stands; the camera comes on either way.
  (test: ACutIsStillTheDefault_AndTheEntitysBlendTimeIsUsedWhenTheWireGivesNone)
  (test: ABadBlendOrEaseIsAWarning_AndTheCameraStillComesOn)
- **From the screen's view, to the camera's own.** The blend starts from `world.TryGetMainView` when the
  input arrives — whatever drew the screen: a rig, another camera, `ActiveCamera` — and ends on the
  camera's pose as the director resolves it every frame (its rig's `CameraPose`, else its interpolated
  transform), so a blend into a moving camera lands on it. Position lerps, rotation slerps, and the field
  of view blends when both are perspective. Only a camera that draws to the screen blends. The director
  applies it after choosing the views and before mirroring the screen into `ActiveCamera`, so every
  reader sees the blended view. (test: CameraOnWithABlendTime_EasesFromTheScreensViewToTheCamera_AndActiveCameraFollows)
- **Deterministic and saved.** `Elapsed` moves in fixed ticks (paused with the simulation) and the frame
  interpolates between the last two ticks' values with its alpha, so a blend is smooth at any display rate
  and the same in every run; `CameraBlend` is saved with the camera (its start point moved by rebasing), so
  a save taken half-way ends the blend on time. (test: ABlendSavedHalfWay_EndsOnTime)
- **No structural change mid-frame:** a finished blend stays on the entity, inert, until the next
  `CameraOn` replaces or ends it. Zero allocation per tick and frame while one runs.
  (test: ACameraBlendAllocatesNothingPerTickOrFrame)
- **Not yet:** blending *out* (`CameraOff` is a cut back to whatever is next), and blends between two rigs
  of the player's own camera (the V toggle cuts).

### As built (the viewmodel pass, 2026-09-30 — #121)
First-person arms drawn over the world (docs/design/12 "As built (first-person arms)" is what they are
and where they come from). **A separate view, not a layer bit:** #77's views already carry everything a
pass needs — a viewport, a projection, a draw order on a target, a run of items and lights — so the
viewmodel is one more view, and the renderer gained one flag.

- **Code:** `src/Sage.Simulation/Rendering/ViewmodelPass.cs` (headless: `ViewmodelPass.TryGet` decides,
  `ViewmodelPass.Emit` places the pieces) and `src/Sage.Client/Rendering/ViewmodelExtract.cs`
  (`sage.client.extract.viewmodel`, the last extract); `RenderView.DepthOnly` in `RenderSnapshot.cs` and
  its clear in `Renderer.DrawView`. Tests: `tests/Sage.Tests/Animation/ViewmodelTests.cs`.
- **When:** only while the screen's view comes from a camera looking out of its first-person rig with an
  enabled `Viewmodel` whose arms are spawned: not in third person (`world.MainViewRig()`), not from the
  editor's free camera (a `DebugCamera` has no rig, so `cam_free` hides it), not from a scripted camera,
  and never in a view into a render target (test: TheViewmodelIsHiddenInThirdPersonAndFromTheEditorsFreeCamera).
  The Sandbox's HUD asks the same question (`Viewmodels.IsDrawn`) before drawing its sprite hands.
- **The view:** a copy of the main view — its viewport (half the screen in `r_testview 1`), its rotation,
  its run of point lights (the same eye, so the same camera-relative lamps) — with the viewmodel's own
  field of view and depth range (54°, 0.01–10 m), `Order` one past the main view's so it draws after it on
  the same target, and **`DepthOnly`**: `DrawView` clears only depth (scissored to the viewport when it is
  not the whole target) and keeps the colour. So the arms are always in front of the world — a wall
  pressed against the player's face does not cut them — and never behind their own depth range.
- **The pieces:** the arms and the weapon carry the `sage:viewmodel_layer` tag, which `MeshExtract`,
  `SkinnedMeshExtract` and `SpriteExtract` leave out of every world view. Their poses are in the camera's
  own space, so `ViewmodelPass.ToCameraRelative` applies the view's rotation and nothing else (the eye is
  the origin of both) (test: ThePassPutsThePiecesInFrontOfTheEye_WhereverItLooks). The arms go through
  #117's `SkinnedExtract.Emit` for their one view, so their palette is written once and `r_stats` counts
  them with the other skinned draws; the weapon is a rigid mesh item.
- **Last in Extract,** after the meshes, sprites, lights, debug lines and particles, so none of those
  ever sees the extra view: nothing is culled into it, and no debug line or particle is drawn in it.
- **Allocation:** the pass allocates nothing per frame, nor do the viewmodel's systems per tick
  (test: TheViewmodelAndItsExtractAllocateNothingPerFrame).
- **Not yet:** shadows (there are none), a viewmodel in a split-screen partner's view (only the screen's
  main view gets one), and a separate lighting rig for the arms (they are lit by the world's sun and lamps).

### As built (render passes, 2026-09-30 — issue 4h-1)
REDESIGN §4.7's pass registry and public `RenderContext`, the first piece of phase 4h. The renderer's
private `Passes` array (opaque, alpha-tested, transparent) is gone; what draws is a list of registered
passes, ordered headless. All of it is experimental (SAGE0130, MAKING_A_GAME §10b).

- **Code:** `src/Sage.Simulation/Rendering/RenderPasses.cs` (headless: `RenderStage`, `RenderStages`,
  `[RenderPass]`, `RenderPassRegistry<TPass>`); `src/Sage.Client/Rendering/RenderPassApi.cs`
  (`IRenderPass`, `RenderPasses`, `RenderContext`, `RenderViewInfo`), `BuiltInPasses.cs`, and the stage
  walk in `Renderer.Draw`/`DrawView`/`DrawOverlay`. Tests: `tests/Sage.Tests/Presentation/RenderPassTests.cs`.
- **Declared, then added in Init:** a pass is a class with `[RenderPass("ns:id", RenderStage.X, After =
  ..., Before = ...)]`, added with `ctx.Get<RenderPasses>().Add(...)` in a module's `Init`, like a system
  (decision 4: no generator; the attribute is read when it is added). `ClientModule` adds the engine's
  and seals the registry in its `Start`, which orders it; the renderer only walks `In(stage)`.
- **The order:** by stage — `Shadow` once per world; `Opaque`, `AlphaTested`, `Sky`, `Transparent`,
  `Debug` once per view; `PostProcess` once on the screen; `Overlay` in the Overlay phase — then by
  `After`/`Before` within a stage, then in the order added (test: PassesDrawByStage_ThenByAfterAndBefore)
  (test: PassesNothingOrders_KeepTheOrderTheyWereAdded). Ties keep the order added rather than the id
  order, as systems do: the engine's passes are added first, so a game's pass with no constraint draws
  after them.
- **Load errors:** a cycle (test: ACycleIsALoadError_NamingThePasses), an id taken twice or a pass with
  no `[RenderPass]` (test: ADuplicateIdOrAPassWithoutADeclarationIsALoadError), and an `After`/`Before`
  naming a pass nobody added or one in another stage — `"?id"` is a soft dependency, as for systems
  (test: AnAfterNamingNothing_OrAPassInAnotherStage_IsALoadError_UnlessItIsSoft) — throw at the seal,
  which stops the boot. Adding after the seal throws (test: AddingAfterTheSealThrows), and adding in
  `Start`, `OnWorldCreated` or a system is SAGE0020 at build time (test: AddingARenderPassAfterInitIsABuildError).
- **Each scene stage draws one run** of the view's sorted items and sprites: the entries whose sort key
  starts with `RenderStages.SortKeyPass(stage)` (0, 1, 2 for the sky, 3), the same bits `RenderSortKey`
  writes for a material's pass (test: EachSceneStageOwnsOneRunOfTheSortKey). `Debug`, `Shadow`,
  `PostProcess` and `Overlay` draw no run.
- **The engine's passes:** `sage:opaque`, `sage:alpha_tested`, `sage:transparent` (the stage's run:
  meshes, then sprites, exactly as the array drew them), `sage:debug` (debug lines) and `sage:ui` (the
  game's UI: what `UiRenderSystem` drew before; the system now runs the Overlay stage and empties the
  queue). There is no sky pass yet: the sky is still each target's clear colour (4h-5 adds one). With
  only these, the frame is the same draws in the same order as before.
- **`RenderContext`** (one instance, reused): the world, the stage, whether this world draws to the
  screen, the views (`ViewInfo`: camera-relative matrices, position, viewport, target), and the current
  view in a per-view stage. In a pass's **Extract** (after `sage.client.extract.camera`, before the other
  extracts, `RenderPassExtract`): `AddView` (a view the world's meshes, sprites, lights and debug lines are
  extracted into like any camera's) and `AddItem` (a mesh handle with a material in a view, not culled).
  In its **Draw**: `Target(name)`, `SetTarget(name)` and `DrawFullScreen(texture, effect, blend)`.
  After a pass from outside the engine, the renderer re-applies material state and puts back the view's
  target and viewport (or the screen, in Overlay), so a game's pass cannot leave the next one drawing
  into the wrong place.
- **Render targets** take a format and a depth buffer: `Renderer.DeclareTarget(name, w, h, format,
  depth)`; another format or depth remakes the target like another size. The three-argument form is
  Color with Depth24, as before.
- **`r_passes`** lists every pass in draw order: stage, id, type and assembly, and its constraints.
- **Allocation:** walking the stages allocates nothing (test: WalkingTheStagesAllocatesNothing); the
  renderer's walk is over the registry's arrays with the one context, built once at the seal.
- **Not yet:** checked by a headless test only up to the order — the drawing is the client's, checked by
  the smoke run. Replacing or disabling an engine pass (a game swapping `sage:transparent` for an OIT
  pass) is not built; nor is the Shadow or PostProcess stage used by the engine (4h-4 and 4h-6).

### As built (the world clock, sky records and weather composition, 2026-09-30 — issue 4h-2)
Time of day, the light over a day, and weather on top of it, all headless: the client draws what
`RenderEnvironment` says. Experimental, SAGE0130 (MAKING_A_GAME §10b).

- **Code:** `src/Sage.Simulation/World/WorldClock.cs` (the saved `clock` resource, its fixed-tick system and
  the `time_between` condition), `src/Sage.Simulation/Rendering/Sky.cs` (`SkyRecord`, `SkyKey`,
  `SkyState`, `SkyRules`, `SkySystem`), the `time`, `time_set` and `time_scale` commands in
  `WorldConsoleCommands`, and `SceneEnvironment.Sky`/`Hour`. Tests: `tests/Sage.Tests/Gameplay/SkyTests.cs`.
- **The clock** is a day count, an hour in [0, 24) and a scale in game seconds per real second (default
  60: a game minute a second; 0 stops it). It advances in the fixed Commands phase, so it is
  deterministic and stops with the game paused (test: TheClockRunsInFixedTicksAndWrapsIntoTheNextDay).
  `time` prints it; `time_set 18:30` (or `18.5`) and `time_scale 120` are cheats (test: TheConsoleSetsTheClock).
  It is saved as the `clock` resource; a save from before it has none and loads, leaving the world's own
  clock (tests: TheClockSurvivesASave, AnOldSaveWithoutAClockStillLoads; the golden saves still load).
  The calendar and passing time are issue 4g-2's ("As built (the calendar and passing time)"); schedules and
  resting are the rest of phase 4g's and read this clock.
- **`time_between`** is a condition of the 4b vocabulary: `{ "time_between": { "from": 19, "to": 6 } }`. The
  window is from inclusive to exclusive and wraps past midnight; equal ends are an empty window
  (test: TimeBetweenWrapsPastMidnight).
- **A `sky` record** is keyframes by hour (`sun`, `ambientSky`, `ambientGround`, `horizon`, `zenith`,
  `fog`, `fogStart`, `fogEnd`, `shadow`) plus `sunrise`, `sunset` and `lean`. `SkyRules.Evaluate(sky, hour)`
  lerps between the two keys around the hour and **wraps**: before the first key it blends from the last
  of the day before (tests: TheCurveWrapsPastMidnight, DuskDarkensSteadily). The sun's direction is
  geometry from the hour (east at sunrise, overhead at midday, under the world at night), not keyed;
  its colour fades to nothing over the last 0.2 of elevation and **a sun at or below the horizon gives no
  light and no shadow** whatever the keys say (test: TheSunBelowTheHorizonGivesNoLightAndNoShadow).
  A world picks its sky with `WorldClock.Sky` (a scene's `environment.sky`, with `environment.hour`;
  test: ASceneChoosesTheSkyAndTheHour).
- **`SkySystem`** (fixed, Late, after the clock) writes the sky, as the weather leaves it, into the
  world's `RenderEnvironment`, including two new fields the sky and shadow passes will read: `Zenith` and
  `ShadowStrength`. `ClearColor` is the horizon colour.
- **The weather bug, verified.** `WeatherRules.Apply` replaced `FogColor`, `ClearColor`, `FogStart` and
  `FogEnd` from the weather record every frame (even `sage:clear` wrote its own green fog), and scaled a
  cached copy of the sun and ambient that `WeatherSystem` only refreshed while the sky was clear and
  settled, so a storm at dusk froze the light the day had reached when it arrived. **Under a sky, weather
  now adjusts:** the baseline is the sky at the clock's hour, re-evaluated every tick, and the weather
  record scales it: `sunScale`, `ambientScale` (as before), and new `fogTint`, `skyTint`, `fogStartScale`,
  `fogEndScale`, all defaulting to 1, so `sage:clear` changes nothing; `sunScale` also softens shadows
  (tests: AStormAtDuskKeepsDimmingWithTheDusk, ClearWeatherChangesNothingUnderASky,
  AStormAtNightIsDarkerThanClear). The client's `WeatherSystem` stops writing the environment when the
  world has a sky, and still throws the particles and plays the sound.
- **With no sky record nothing changes**: the old weather path (absolute fog and sky colours, the game's own
  light scaled) still runs, and the clock touches nothing (test: WithoutASkyTheLookIsUnchanged; the
  WeatherTests are as they were). A record's absolute `fogColor` and `skyColor` are ignored once a sky is
  on, which is the point.
- **Not yet:** the sky pass, exp² fog and the fog-distance cull (4h-5), sun shadows (4h-4) and the night tint
  in post (4h-6) read these values; weather's blend is advanced by the client's system, so a headless world
  with a sky holds its weather where it is unless something calls `Weather.Advance`.

### As built (sun shadows, 2026-09-30 — issue 4h-4)
The first Shadow-stage pass on the registry: one stable shadow map from the sun, fitted to the screen's
main view (decision 1: R32F, 2048², 60 m, manual PCF; cascades later). Where the map sits is decided
headless; the client only draws it. Experimental, SAGE0130 (MAKING_A_GAME §10b).

- **Code:** `src/Sage.Simulation/Rendering/ShadowMath.cs` (headless: `ShadowMath`, `ShadowFit`),
  `src/Sage.Client/Rendering/ShadowPass.cs` (`sage:shadow`), `Renderer.DrawShadowCasters`, the caster
  rule in the mesh and skinned extracts, `EffectBinding.SetFrame`'s shadow parameters, and in the shaders
  `common.fxh` (`ShadowViewProj`, `ShadowParams`, `ShadowMap`/`ShadowSampler`, `ShadowLit`) and `lit.fx`
  (`ShadowCaster`, `ShadowCasterSkinned`). Tests: `tests/Sage.Tests/Presentation/ShadowTests.cs`.
- **Switched on with `r_shadows 1`** (off by default; the Sandbox turns it on, 4h-7); `r_shadow_size`
  (texels a side, 256–4096, default 2048) and `r_shadow_distance` (metres of the view the map covers,
  5–500, default 60). `r_stats` adds `shadow casters N` (`RenderStats.ShadowCasters`).
- **The caster view.** `sage:shadow`'s Extract (before the other extracts) adds one view flagged
  `ShadowCaster`: the sun's orthographic view, camera-relative to the main view's camera, into the render
  target `sage:shadow` (`SurfaceFormat.Single` with a depth buffer). The mesh and skinned extracts put
  into it only what `ShadowMath.Casts` says — an **opaque** material with `castShadows` (a new
  `material` field, on by default) — culled against the map's box; terrain chunks are meshes, so they
  cast. Sprites, particles, lights and debug lines skip the view, the viewmodel never reaches it, and a
  first-person body still casts (test: OnlyOpaqueMaterialsThatSaySoCastShadows). The renderer's view
  loop skips it; its Draw renders those items into the map with `lit.fx`'s `ShadowCaster` (or
  `ShadowCasterSkinned`, with the item's palette) whatever each material's effect is, cull none, depth in
  [0, 1] cleared to 1.
- **The fit** (`ShadowMath.Fit`): a sphere around the view's slice from its near plane to
  `r_shadow_distance` (`PerspectiveSlice`, or `OrthographicSlice` for an orthographic camera), so turning
  the camera never changes the map's size, and the map contains the whole slice (test:
  TheFitContainsTheViewSlice). One texel is spare, so snapping never cuts the sphere.
- **No shimmer.** The map's corner snaps to whole texels **in absolute light space**: the origin sector's
  corner (`Origin.ToAbsolute(0)`) is added back in double before snapping, so a camera move of less than a
  texel slides nothing (test: ASubTexelCameraMoveKeepsTheTexelGrid) and neither does an origin rebase,
  which changes every number in memory by a sector (test: AnOriginRebaseKeepsTheTexelGrid). A moving sun
  still rotates the grid (not quantised yet).
- **Casters between the sun and the view are kept:** the map's depth reaches 200 m
  (`ShadowMath.DefaultCasterReach`) past the sphere toward the sun, so a cliff behind the camera still
  shades the path at dusk; things beyond that, behind the slice or beside it are not drawn (test:
  CastersBetweenTheSunAndTheViewAreKept).
- **Strength** is `RenderEnvironment.ShadowStrength` — the sky's `shadow` key, softened by weather
  (4h-2) — and **0 once the sun is at or below the horizon**, whatever set the environment
  (`ShadowMath.Strength`; test: ShadowStrengthIsZeroBelowTheHorizon). A world with **no sky** keeps the
  environment's default of 1: full shadows under the default sun. At strength 0 no caster view is made
  and nothing is drawn.
- **In the shaders:** the lit techniques `Default`, `AlphaTest` and `Skinned` multiply the sun's light
  (only the sun's: ambient and lamps are untouched) by `ShadowLit`, a manual 2×2 PCF over point samples
  (compare four texels, blend the answers bilinearly), with a depth bias of a texel and a half plus 5 cm,
  more on surfaces turned from the sun. It is computed in the pixel shader from the camera-relative
  position, so the vertex shaders gain no constants: `Skinned` keeps its 64 bones in 192 of vs_3_0's
  256. The sampler is `register(s1)`, so an effect's own sampler keeps s0 and the material's sampler
  state. Off the map, or at strength 0, the sun is unshadowed.
- **Not yet:** cascades; alpha-tested casters (leaves; no clip in the caster); a custom effect's own
  vertex deformation in its shadow (casters draw with `lit.fx`); quantising the sun's direction so the
  grid holds still while the sun moves; shadows in views of worlds that do not draw to the screen.
  Checked on a GPU only by the Windows shader compile and the smoke run (`+r_shadows 1`).

### As built (sky and fog, 2026-09-30 — issue 4h-5)
The Sky stage's pass and the second fog curve §3.9 promised, both read from the world's `sky` record
(4h-2). What the sky looks like and how far fog reaches are decided headless; the client only draws.
Experimental, SAGE0130 (MAKING_A_GAME §10b).

- **Code:** `src/Sage.Simulation/Rendering/Fog.cs` (headless: `FogMode`, `FogMath`), the sky maths in
  `src/Sage.Simulation/Rendering/Sky.cs` (`SkyRules.StarsAt`, `Gradient`, `Haze`, `SunDisc`, `ColorAt`),
  `src/Sage.Client/Rendering/SkyPass.cs` (`sage:sky` and its `SkyDome`), `Renderer.DrawSky`, the fog cull
  in the mesh, skinned and sprite extracts (`RenderSnapshot.FogHides`), and in the shaders `sky.fx` and
  `common.fxh`'s `FogFactor`. Tests: `tests/Sage.Tests/Presentation/SkyFogTests.cs`.
- **The sky pass** draws per view in the Sky stage — after the opaque and alpha-tested runs, before the
  transparent one — one triangle at the far plane with the depth test on and depth writes off, so it
  fills only what nothing covered; it draws into whatever target the view is bound to (the screen, a
  render target, or a post-processing target when there is one). The colour along each view ray is
  `SkyRules.ColorAt`: the horizon colour (`ClearColor`) at and below the horizon to `Zenith` overhead,
  most of the change low down; the **fog colour hazing the lowest 0.12** (sin of elevation) when fog is on,
  so a hill fogged away meets a sky of its own colour; and the **sun disc** (about 1.5°, four times the
  sun's colour, which is zero once it has set) where the light comes from. **Stars** are hashed from the
  view direction on the GPU, above the haze; how much they show is `StarsAt(sun elevation)`: none while the
  sun is up, all of them once it is 0.15 below the horizon, and fewer under a heavy sky (weather's
  `sunScale`). At noon the zenith is the key's, the horizon the fog's, the sun a bright disc and there are
  no stars; at 21:00 all of it is dark, there is no sun and the stars are out
  (test: TheSkyAtNineAtNightAgainstNoon).
- **Drawn only while `RenderEnvironment.DrawSky`**, which the sky system turns on for a world with a sky
  record and off when the sky goes; a game may turn it on itself. Not in a depth-only view (the
  viewmodel's, drawn over a finished picture). **A world with no sky keeps its clear colour, linear fog
  and no stars**: the look before 4h-5 (test: TheSkySetsTheFogAndDrawsOnlyWhileTheWorldHasOne).
- **Fog** (`FogMath.Factor`, and `FogFactor` in `common.fxh`, pixel shader only — no vertex shader gains
  a constant, so `Skinned` keeps its budget): **linear**, from `fogStart` to `fogEnd` as before
  (test: LinearFogRisesInAStraightLineFromStartToEnd), or **exp²**, `1 - e^-(density·(d - start))²` —
  softer than linear near the start, closing in faster — with a sky record's `"fogMode": "Exp2"`. A key's
  `fogDensity` (per metre) sets it; 0, the default, is the density that leaves 1/512 of the surface at
  `fogEnd`, so `fogStart`/`fogEnd` mean the same in both modes (test: Exp2FogStartsSoftAndIsCompleteAtItsEnd).
  The mode rides in `FogParams.w` (0: linear; else the density scaled for `exp2()`), so it costs no
  constant either. Weather's `fogEndScale` divides an explicit density, pulling exp² fog in as it pulls
  in linear fog's end. The fog colour is the sky key's `fog`, tinted by weather.
- **Culling what fog hides:** past `FogMath.CullDistance` — `fogEnd` for linear fog, where 1/512 or less
  shows through for exp² — a fogged surface is its fog colour to within half an 8-bit step, so an item or
  sprite whose bounding sphere is wholly beyond it (`FogMath.Hides`: nearest point past the distance) is
  not extracted (test: FogCullsWhatItFullyHides). Only opaque and alpha-tested materials with `fog` on,
  never into the sun's caster view (a caster past the fog may still shade what is near), and only against
  a background of the fog's colour: while the sky pass draws, or with no sky when the clear colour is the
  fog colour (the default) — so a game that set them apart does not see its fogged silhouettes vanish.
  `r_stats` shows `fog culled N` (`RenderStats.FogCulled`, counted in `Culled` too) and `skies N`.
- **Not yet:** a moon, clouds and a star field that turns with the hours (the stars are fixed to the
  world's axes); fog on the sky above the haze band, so a tall thing culled at the fog's end can pop
  against the gradient above the horizon; particles are never fog-culled. Checked on a GPU only by the
  Windows shader compile and the smoke run; the Sandbox has had one since 4h-7 (below).

### As built (post-processing, 2026-09-30 — issue 4h-6)
A chain of full-screen effects between the screen's views and the UI, and the render scale of §3.9.
Experimental, SAGE0130 (MAKING_A_GAME §10b).

- **Code:** `src/Sage.Simulation/Rendering/PostProcess.cs` (headless: the `post_effect` record,
  `PostChainPlan`), `src/Sage.Client/Rendering/PostProcess.cs` (the `sage:post` pass and `PostChain`, which
  reads the records and cvars), the screen redirect and `DrawPostChain` in `Renderer`,
  `engine_content/shaders/post.fx` and `engine_content/data/post.json`. Tests:
  `tests/Sage.Tests/Presentation/PostProcessTests.cs`.
- **A `post_effect` record** names a `material` (effect, technique, params and sampler, 07 §3.3), an
  `order` and a `cvar` that switches it (a bool, or a number where 0 is off; none: always on). Effects run
  by order, then id (test: EffectsRunByOrderThenId_EachSwitchedByItsCvar); a mod removes one of the
  engine's with a disabling patch, like any record. `r_post` (default 0) switches the whole chain.
- **The plan is headless** (`PostChainPlan.Plan`): with nothing on the frame is exactly what it was, the
  screen's views drawn straight into the back buffer (test: NoEffectsMeansNoChain_TheViewsDrawStraightToTheScreen);
  effects switched off are skipped (test: DisabledEffectsAreSkipped); the first reads `sage:scene`, each
  writes the one of `sage:post0`/`sage:post1` the step before did not, and the last writes the screen
  (test: TheChainPingPongsItsTargets_AndTheLastWritesTheScreen).
- **Drawing:** while the chain is on, "the screen" a screen view or a pass in a per-view stage binds is
  `sage:scene` (Color with Depth24), and each screen view's viewport is scaled with it, so a split screen's
  halves stay halves. Views into other targets (`r_testview 2`'s map, the editor viewport) are not
  redirected, so nothing is post-processed twice. Then `sage:post` (PostProcess) draws a clip-space quad a
  step through the effect's material; the engine sets `Source`, `SourceSize` and `Night`, which a post
  material therefore does not (and `MaterialCache` does not ask it for). A game's PostProcess passes run
  after it, on the back buffer, and the UI (Overlay) draws over the result at full size.
- **The render scale** (`r_scale`, 0.25 to 1, with or without `r_post`): `sage:scene` and the ping-pong pair
  are the screen's size times the scale, and the last step writes the full-size screen through the
  material's sampler, point by default, so the picture upscales as big pixels. With no effect on it is one
  plain copy (test: ARenderScaleDrawsTheSceneSmaller_AndAloneIsOneCopy).
- **The engine's effects** (`engine_content/data/post.json`): `sage:grade` (order 100, `r_post_grade`):
  exposure in stops, a night tint, saturation, contrast and a colour filter; `sage:vignette` (order 200,
  `r_post_vignette`). Their materials inherit every param `post.fx` has from `sage:post_copy`, since GL
  ignores `.fx` defaults (test: TheEnginesEffectsAndTheirMaterialsLoad). LDR in and out, exposure in the
  grade (decision 6).
- **The night tint** reads the sky, not the light: `PostChainPlan.Night` is 0 while the sun is up, rises as
  it sets and is 1 once it is well under the horizon; with no sky it is 0 and the grade is only a grade
  (test: TheNightTintFollowsTheSky).
- **See it:** `r_post 1`, `r_scale 0.5`; `r_stats` adds `post N step(s), scene WxH`. Under Xvfb the shaders
  aren't compiled, so each effect falls back to a plain copy (its material can't be built): the smoke run
  proves the targets, the redirect and the chain's steps, not the picture. `post.fx` is checked by the
  Windows CI's shader compile only.
- **Not yet:** HDR targets and bloom (decision 6: they wait for GPU testing on both platforms); anti-aliasing;
  depth-reading effects (the scene target has depth, but no effect is handed it yet).

### As built (the 4h exit, 2026-10-01 — issue 4h-7)
Phase 4h's exit criterion, "a dusk-to-night transition with shadows in a streamed exterior", on the
Sandbox's streamed hills (decision 7: terrain needs C#, and entity streaming is 4g's). The Sandbox gained
a sky, lamps that light themselves and shadows; the engine gained a light switch and two fixes the exit
found. The headless test checks the maths; the CI smoke run checks the client.

- **Code:** the Sandbox's `content/data/scene.json` (the `sandbox:day` sky, the scene's `environment`, the
  weather's tints), `content/data/level.json` (the lamp prefab and the `sandbox:lamp` state machine) and
  `Sandbox.Client/SandboxClientModule.cs` (`r_shadows`); in the engine `PointLight.Off`/`Lit`
  (`Rendering/Lights.cs`), `LightPart.Off` and `LightsModule`'s inputs (`Sage.Gameplay`), the light
  extract's check, `WeatherRules.Find` and `GameEvents.EndOfSchedule`. Tests:
  `tests/Sage.Tests/Presentation/DuskToNightExitTests.cs`, and one each in `LightTests`, `SkyTests` and
  `GameEventTests`.
- **The Sandbox at dusk.** Its scene starts at 18:30 under `sandbox:day`, a sky keyed at 0, 6, 12, 17,
  18:30, 20 and 22 with the sun up from 5:30 to 19:30, exp² fog, and shadows that weaken from 0.9 in the
  afternoon to none at night. The clock runs at its default, a game minute a second (`time_scale`,
  `time_set`). Its client turns `r_shadows` on in `Init`: the game's default, before config.cfg and the
  command line, so `+r_shadows 0` still wins. Rain and snow gained `fogTint`, `skyTint` and fog scales, so a
  storm darkens the hour's light rather than replacing it.
- **The exit test** walks the player east from the hut, four metres a tick on the hills, while the clock
  runs from 18:30. It crosses the sector edge that moves the origin by two sectors at about 18:56, with
  the sun still up, and then runs a game hour a second to 22:00
  (test: TheSandboxGoesFromDuskToNight_AcrossASectorEdge_WithShadowsAndLamps). It checks every tick
  that the sun's light, the ambient and the shadow strength (`ShadowMath.Strength`, as `sage:shadow`
  uses it) never rise, and that at 22:00 they are zero and the stars are out. It checks that the lamps are
  dark before sunset and lit from 20:00. At the rebase, it fits the shadow map for the main camera's view
  before and after, as the client does, with the sun held still. A ground point ten metres ahead stays at
  the same fraction of a texel, so the grid does not slide. At 21:00 it saves, scrambles the clock and the
  lamps, and loads: the hour, day, scale and sky come back, the light is the sky at that hour again, and the
  lamps are lit.
- **A light switch** (`PointLight.Off`, the part's `"off": true`): `TurnOn`, `TurnOff` and `Toggle` are
  inputs routed to lights, owned by `sage.gameplay.lights`. So a branch's or a door's `Toggle` is
  untouched. A light that is off gives nothing (`PointLight.Lit`, which the client's light extract asks).
  A save from before the field loads its lights as on (test: ALightIsSwitchedByEntityIO).
- **Lamps by the clock, from data.** The hut's lamps start dark. `sandbox:lamp` is two states whose
  enter actions fire `TurnOn`/`TurnOff` at `!self`. Its two machine-wide transitions ask
  `{ "time_between": { "from": 19.5, "to": 5.5 } }` and its `not`, and since a machine-wide transition
  never goes to the state the machine is in, each fires once a dusk or dawn. No C#.
- **Weather at dusk composes with the sky.** The Sandbox's rain, rolled in at 19:00, makes the sun, the
  ambient, the horizon, the zenith and the fog darker, pulls the fog in and softens the shadows. The light
  is still the dusk's colours, scaled, not the rain record's grey (test: AStormAtDuskInTheSandboxIsDarkerThanClear).
- **Fix: a world without the client has no `weather` record type** (it is `sage.client`'s), and under a
  sky, a weather id set by a save or a scene made the sky system throw every tick. It reads as clear now,
  as a missing record always did (test: WithoutTheClientsWeatherTypeTheWeatherReadsAsClear).
- **Fix: the event age backstop blamed frame readers during a catch-up.** This was the occasional "WARN
  Events Dropping 1 CueTriggered … slowest reader: ParticleSystem" in the Sandbox's first frames under
  llvmpipe with post on. After a slow frame the host runs up to `sim_maxframetime` × tick rate ticks
  (15 at 60 Hz) before the next frame. `ev_maxage` is 8, and it was checked at the end of every tick. So
  a cue sent early in the catch-up was dropped before ParticleSystem (FrameUpdate) could read it, and
  the warning blamed it. Now, once a world has drawn a frame, a tick drops only what every reader has
  passed, and the age is checked at the end of the frame, when every reader has had its turn. A world
  that never draws (a server, a test that only ticks) checks every tick, as before
  (test: AFrameReaderKeepsUpThroughACatchUpLongerThanMaxAge).
- **The smoke run** (`.github/workflows/ci.yml`): the Sandbox with `r_post 1`, `r_scale 0.5`, the clock set
  to 19:20 and run through sunset, rain rolled in, then 22:00, with `r_stats` and `r_passes`. `r_stats`
  shows the lamps come on (`lights 2` after sunset, 0 before) and `shadow casters` with no `(r_shadows 0)`.
  Without shaders no material builds, so nothing casts and the sky and post steps fall back to clears and
  copies: the run proves the passes, targets, cvars, clock and weather in the real host, not the picture.
- **Not yet:** the sun's direction is not quantised, so the grid that holds through a rebase still turns
  as the sun moves (4h-4); a moon, and lamps that fade rather than switch; NPC schedules on the same clock are
  4g's (the calendar and passing time are built: "As built (the calendar and passing time)").

### As built (the calendar and passing time, 2026-10-01 — issue 4g-2)
What the clock's day count means as a date, and a way to move the clock a long way at once. Headless, in
`Sage.Simulation/World`. Experimental, SAGE0129 (MAKING_A_GAME §10b).

- **Code:** `src/Sage.Simulation/World/Calendar.cs` (`CalendarRecord`, `CalendarMonth`, `GameDate`,
  `Calendars`, the `weekday` and `date_between` conditions), `src/Sage.Simulation/World/Time.cs`
  (`Time.Pass`, `TimePassed`), `WorldClock.Calendar`/`Date`, the `time_pass` command in
  `WorldConsoleCommands`, and the tick-boundary hook in `World.RunFixed`. Tests:
  `tests/Sage.Tests/Gameplay/CalendarTests.cs`.
- **A `calendar` record** names `months` (`name`, `days`), `weekdays`, `startYear` and `startWeekday` (which
  weekday the clock's day 0 is). Day 0 is the first day of the first month of `startYear`.
  `CalendarRecord.DateOf(day)` and `WorldClock.Date(calendar)` walk the months, so months and years wrap by
  arithmetic, and a day before day 0 still has a date (test: MonthsAndYearsWrapInACustomCalendar). A world
  chooses one with `WorldClock.Calendar`; with none (or a record that is gone) it has
  `CalendarRecord.Default`: twelve months of the usual lengths, no leap years, Monday to Sunday, year 1, day 0
  a Monday (test: TheDefaultCalendarHasMonday). There are no leap years or moons yet: the year is the sum of
  the months.
- **`weekday`** is `{ "weekday": { "is": "Monday" } }` or `{ "weekday": { "anyOf": ["Saturday", "Sunday"] } }`,
  by the world's calendar's names, case ignored (test: WeekdayReadsMonday). **`date_between`** is
  `{ "date_between": { "fromMonth": 12, "fromDay": 1, "toMonth": 2, "toDay": 28 } }`: a window of the year,
  both ends included, wrapping past the new year (test: DateBetweenWrapsPastTheNewYear).
- **`Time.Pass(world, hours, reason)`** asks for a skip at the world's next tick boundary (the same place a
  save runs): when the fixed phases of the tick have all finished, and before the tick's save, so no system
  sees the hour change under it (test: TimePassesAtTheTickBoundary). Asked with no tick running, it runs at
  once. Several requests in one tick are one skip: the hours add up, and the one event carries the first
  reason (test: SeveralRequestsInATickAreOneSkip). It refuses hours that are not positive, finite and at most
  a thousand years.
- **A skip is an event, not ticks.** It moves the clock (day and hour) at once and sends one `TimePassed`
  (`Hours`, `Reason`, `FromElapsed`, `ToElapsed`), which a reader sees at the start of the next tick, with the
  clock already at the new time. Nothing ticks hour by hour, and `Scale` stays the rate the clock runs at
  while played (test: PassingTimeNeverTicksTheSimulation). Schedules and the off-screen simulation (4g-4,
  4g-6) catch up from the event.
- **`time_pass <hours>`** (a cheat; `wait` is a script pause) does it in every world, reason `console`
  (test: TimePass30MovesToTheNextDayAndFiresOnce). `time` prints the date as well.
- **Saved** with the clock: `Calendar` is one more field of the `clock` resource, and a clock saved before it
  loads with none, keeping its day count (tests: TheCalendarSurvivesASave, AClockSavedBeforeTheCalendarStillLoads;
  the format 2 and 3 golden saves still load).
- **Not yet:** routines read `TimePassed` (issue 4g-4, design 16 "As built (NPC routines)"); the off-screen
  simulation (4g-6) and rest and wait screens (4g-7) do not yet; a scene cannot choose a calendar, as it can a sky.

## 12. Multiplayer-later notes
Nothing changes: a client renders its own world's snapshot. A dedicated server doesn't load `Sage.Client` at all.

## 13. Open questions
- Should sprites be lit per-vertex with the nearest point lights, or unlit + fog only in v1? Default: **material decides**; Daggerfall-style content uses unlit + fog + ambient tint.
- ~~Is SM3's constant budget enough for 4 point lights + fog + sun in one pass on DesktopGL/MojoShader?~~ **Yes** (2026-09-24): `lit.fx`'s `Default` technique compiles and runs with all of it, and no fallback to 2 was needed.

## 14. Build steps
1. ~~`RenderSnapshot` + Extract phase + camera extract; port `ModelRendererSystem` to `MeshExtract` + the opaque pass~~ **Done 2026-09-22** (ARCHITECTURE §7 step 6; TODO R9, #25).
2. ~~Sort keys + material-based drawing (with 07)~~ **Done 2026-09-22** (radix sort later).
3. ~~Sprite batcher + `SpriteRenderer` + 8-direction selection~~ **Done 2026-09-22** (TODO F1; with 12).
4. ~~Lighting/fog/ambient~~ **Done 2026-09-22** (sun, hemispheric ambient, fog) and ~~point lights~~ **Done 2026-09-24** ("As built (point lights)"). Render scale done in issue 4h-6 ("As built (post-processing)").
5. `DebugDraw` + `r_stats` + the overlay (TODO F5). *`r_stats` done in step 6.*
6. Instancing experiment behind `r_instancing` (later).
