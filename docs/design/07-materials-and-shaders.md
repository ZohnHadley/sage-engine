# 07 — Materials and Shaders

## 1. Purpose and scope
How surfaces are described and drawn:
- the shader source layout and the compile toolchain;
- techniques as shader variants;
- the engine's built-in effects;
- **material records**;
- parameter binding;
- shader hot reload.

Not in scope: pass order and sorting (06), loading `.mgfxo` files as assets (05).

## 2. Research basis
- MonoGame effect facts (verified 2026-09, survey §3.7):
  - `.fx` files compile via MGCB or the **`dotnet-mgfxc`** tool (native on Windows, Wine elsewhere).
  - DesktopGL supports `vs/ps_2_0` and `vs/ps_3_0`, translated to GLSL by MojoShader.
  - On GL, **effect parameter default values don't work**, **preshaders aren't supported**, and pixel shader inputs must exactly match vertex shader outputs.
- Shader permutation explosions are a known cost in big engines (Unity's keyword variants, UE's permutation compile times); a small named set avoids them.
- The one data-record pipeline for all definitions: 05 §3.5 (plan review #7).

## 3. Concepts

### 3.1 Source layout and compile
```
engine_content/shaders/
  common.fxh          frame params, lighting, fog, alpha test, skinning (#117), shadows, spot lights
  lit.fx              meshes: Default, AlphaTest, Unlit, Skinned, Lightmapped, the Instanced and ShadowCaster sets (§3.8)
  terrain.fx          terrain: Splat, ShadowCaster (§3.8)
  water.fx, sky.fx, post.fx, depth_haze.fx   water (#411), the sky (4h-5), post-processing (4h-6, #316)
  sprite.fx           billboard sprites: techniques Unlit, Lit (both alpha-tested), UnlitBlend
  debug.fx            DebugDraw lines/shapes (vertex colour)
  error.fx            flat magenta; the fallback for anything broken
```
- An MSBuild target in `Sage.Client` runs `dotnet-mgfxc <file>.fx <file>.mgfxo /Profile:OpenGL` for every changed `.fx` before build (incremental, by timestamp). `dotnet-mgfxc` is pinned in the repo's `.config/dotnet-tools.json`, where since R12 it is the **only** tool: the four `dotnet-mgcb*` entries went with MGCB.
- The `.mgfxo` output goes into the engine content mount, where it's loaded as an `Effect` asset (05). MGCB is no longer needed for shaders (the old plan kept it "for shaders only"; the dotnet tool is simpler and scriptable for hot reload).
- Game and mod shaders follow the same layout in their own mounts; they `#include "common.fxh"` through a shared include path.

### 3.2 Techniques are the variants
Instead of compiling permutations from a define matrix, each `.fx` declares a **small, named set of techniques**. Common code is shared through functions and macros in `common.fxh`:

| Effect | Techniques (v1) | Later |
|---|---|---|
| `lit.fx` | `Default` (lit, opaque), `AlphaTest`, `Unlit`; `Skinned` since #117 (§3.7) | built in 4n: `Lightmapped`, `Instanced` (§3.8) |
| `sprite.fx` | `Unlit` (alpha-tested, fog), `Lit` (alpha-tested, sun + shadows + ambient + nearest lights, wrapped, + fog), `UnlitBlend` (transparent) | built in 4n: the `Instanced` variants (§3.8) |

Fog isn't a variant: it's always evaluated, and `r_fog 0` or a material's `fog: false` sets its density to 0. That's one multiply-add per pixel, cheaper than doubling the technique count.

### 3.3 Material records
Materials are records (05 §3.5). They're hot-reloadable, mod-patchable, and can inherit through `base`:

```json
[
  { "type": "material", "id": "lit_default",
    "effect": "shaders/lit.mgfxo", "technique": "Default",
    "pass": "Opaque", "blend": "Opaque", "cull": "Back", "depthWrite": true,
    "sampler": { "filter": "Point", "address": "Wrap" },
    "params": { "Albedo": "textures/white.png", "Tint": [1, 1, 1, 1], "Specular": 0.0 } },

  { "type": "material", "id": "stone_wall", "base": "lit_default",
    "params": { "Albedo": "textures/stone_wall.png" } },

  { "type": "material", "id": "creature_sprite",
    "effect": "shaders/sprite.mgfxo", "technique": "Unlit", "pass": "AlphaTested",
    "cull": "None", "params": { "AlphaCutoff": 0.5 } }
]
```

- **`pass`:** `Opaque`, `AlphaTested`, or `Transparent`. It decides the pass and sort-key layout (06 §3.4–3.5).
- **Render state:**
  - `blend`: `Opaque`, `AlphaBlend` (premultiplied), `Additive`;
  - `cull`: `Back`, `None`;
  - `depthWrite`, `depthTest`;
  - `sampler`: filter `Point`/`Linear`/`Anisotropic`, address `Wrap`/`Clamp`.
- **Params:** name → value, where the value is a float, a 2–4 element array, a texture path, or `rt:<name>`, one of the renderer's named render targets (06 §3.4a; a mirror, a minimap, a security camera's screen). **Every parameter an effect uses must get a value** from the material or its `base` chain, because GL ignores `.fx` default values. Missing ones are a validation error, and the engine's `lit_default`/`sprite_default` records supply sane values to inherit from.
- **Per-object variation** (tint, flash-on-hit) uses `RenderItem.Tint`/`SpriteInstance.Tint` (06). Per-entity parameter blocks come later if needed.

### 3.4 Parameter binding
Three tiers, all resolved **once** into cached `EffectParameter` references (never looked up by string per draw):

| Tier | Parameters | When set |
|---|---|---|
| Frame / view | `ViewProj`, `Time`, `FogColor`, `FogParams`, `SunDir`, `SunColor`, `AmbientSky`, `AmbientGround`, `CameraRight`, `CameraUp` | once per effect per view (when the effect first appears in the sorted list) |
| Material | the material's `params` (textures, scalars) | when the material changes between draws (sorting keeps this rare) |
| Object | `World`, `Tint`, `LightPositions[4]`, `LightColors[4]`, `LightCount`; `Bones[64]` for skinned draws (§3.7) | per draw (or per instance, later) |

`MaterialRuntime` (client) is built from a material record plus its loaded effect:
- the resolved `EffectTechnique`;
- a list of `(EffectParameter, value)` pairs;
- the cached state objects;
- a compact `MaterialId` for sort keys.

It's rebuilt when `RecordsReloaded(material)` or `AssetReloaded(effect/texture)` arrives (04 §3.5).

### 3.5 `common.fxh` contract
The shared include defines:
- the frame/object parameter names above, so all engine and game effects bind the same way;
- `ApplyFog(color, viewDepth)`, `HemiAmbient(normal)`, `SunLight(normal)`, `PointLights(normal, relative)` — `relative` being the camera-relative position the lights are given in, so the distance costs one subtraction;
- `AlphaTest(alpha)` (calls `clip`);
- `SkinMatrix(indices, weights)`, `SkinPosition(position, skin)` and `SkinNormal(normal, skin)` over the object-tier `Bones` palette (§3.7);
- billboard corner expansion lives in `sprite.fx`'s instanced techniques (#309), not here.

Game shaders that include it get lighting and fog consistent with engine materials.

### 3.6 As built (migration step 6)
- **Shaders:** `engine_content/shaders/`: `common.fxh` (the §3.5 contract), `lit.fx` (`Default`, `AlphaTest`, `Unlit`), `sprite.fx` (`Unlit`, `Lit`, `UnlitBlend`; F1) and `error.fx`. `debug.fx` comes with `DebugDraw`.
- **Build:** `build/Sage.EngineContent.targets` runs `dotnet mgfxc … /Profile:OpenGL` (pinned 3.8.5.1 in `.config/dotnet-tools.json`, where since R12 it is the only tool; the target restores it itself, which until R12 happened only as a side effect of the vendored ImGuiNet project restoring tools for MGCB) and copies `engine_content/` into the exe's `Content/` (minus `tools/`, which *makes* content — the font and texture generators — rather than being it). Any `.fx`/`.fxh` change recompiles all; unchanged builds skip it. **Deviation:** the target is imported by `Sage.Host` (the exe owns the Content folder), not `Sage.Client`.
- **Material records:** `MaterialRecord` in `src/Sage.Simulation/Rendering/RenderData.cs`; engine materials `sage:lit_default`, `sage:unlit_default`, `sage:terrain_default` (one tiling ground texture over `lit_default`, 14 §3), `sage:sprite_default` (full-bright), `sage:sprite_unlit` (the same, by name), `sage:sprite_lit` (the world's light) and `sage:error` in `engine_content/data/materials.json`; the Sandbox inherits `sage:lit_default` for its bunny and `sage:sprite_lit` for its trees.
- **Sprite materials (F1):** the sprite sheet's texture replaces the material's `Albedo` per draw, so one material draws every sheet. Sprite quads carry their own per-vertex colour, multiplied with `AlbedoColor` and the object `Tint`.
- **`MaterialCache`** (client) builds a runtime per material on first use: effect, technique, material params and cached state objects. It binds the three tiers of §3.4, and rebuilds everything lazily when records reload (material hot reload works; **shader** hot reload is §14 step 4, not done).
- **Deviations:**
  - **Validation time:** missing effect parameters, unknown techniques and wrong param shapes are checked when the material is built (effect loaded), not at record load. The error names the material and the missing params, and the material draws as `sage:error`.
  - **Fog switch:** a per-material `FogEnabled` parameter (from the record's `fog`) instead of zeroing the density. Frame-level `r_fog` and the environment's fog go into `FogParams.z`.
  - **`AlbedoColor`:** `lit.fx` has a material-tier `AlbedoColor` that multiplies the texture (so materials can tint without a texture).
  - **Point lights (2026-09-24):** `LightPositions`/`LightColors`/`LightCount` are object-tier, set per draw by `EffectBinding.SetLights` from the four `LightRules.Nearest` picked (06 §3.9). `LightColors.a` carries the range, so one `float4` array does the work of two. An effect without the parameters (`debug.fx`) simply has nothing set — `SetLights` returns when `LightCount` is absent, which is how the same draw loop serves lit and unlit effects. `lit.fx`'s `Default` and `AlphaTest` techniques use them; `sprite.fx`'s `Lit` did not at first; it does since (06 "Sprites lit by the lamps").
- **Textures:** `.png`/`.jpg` (and `.tga` since #317) load through the VFS with `Texture2D.FromStream` and are premultiplied on load (§13). A missing one becomes a magenta/black checker, with a warning.

### 3.7 As built (GPU skinning, issue #117)
- **Technique:** `lit.fx` `Skinned` is `Default` with a skinning vertex shader (`VSSkinned`): the vertex's four `BLENDINDICES0` joints' palette entries blended by `BLENDWEIGHT0`, then `World` and `ViewProj` as for any mesh; the pixel shader is `PSDefault`, so a skinned mesh is lit and fogged exactly like a rigid one. It compiles with mgfxc under Wine on Linux and natively on Windows CI.
- **Palette:** `float4x3 Bones[64]` in `common.fxh` — 192 of vs_3_0's 256 constant registers, the reason a draw takes at most `SkinMath.MaxBones` = 64 joints; a skin with more is drawn with its first 64 and a warning (test: MoreJointsThanADrawTakes_AreCutToMaxBones_WithAWarning). `Bones` is an engine parameter (`EffectBinding.EngineParams`), set per skinned draw from `RenderSnapshot.Bones`; materials never supply it.
- **Which technique:** a skinned item draws with its material's effect's `Skinned` technique (`MaterialRuntime.Skinned`), whatever technique the material names, so `sage:lit_default` and every material based on it skin with no change. An effect without `Skinned` draws the mesh in its bind pose with the material's own technique (the skinned vertex starts with the rigid vertex's three elements), logged once per material.
- **Maths:** `SkinMath` (Sage.Simulation) is the shader's maths on the CPU: `palette[j] = inverseBind[j] * modelJoint[j]` in row-vector order (test: Palette_IsInverseBindThenJoint_InRowVectorOrder), an identity palette in the bind pose (test: TheBindPose_GivesAnIdentityPalette), and `SkinPosition`/`SkinNormal` as `SkinMatrix`/`SkinPosition`/`SkinNormal` compute them (test: AJointTurned90Degrees_CarriesItsVertices_AroundIt). The rest is in 12 §3 "As built (GPU skinning)".

### 3.8 As built (phase 4n, pack 1, 2026-10-06)
What the materials and shaders gained with 4n's first pack. The rendering side of each is in 06's "As
built" sections of the same date.

- **Shaders now:** `common.fxh`, `lit.fx`, `sprite.fx`, `terrain.fx` (the splat, #307:
  `Splat` and `ShadowCaster`), `water.fx` (`Water`, #411), `sky.fx`, `post.fx` (`Copy`, `Grade`, `Vignette`,
  and since #316 `BloomPrefilter`, `BloomDown`, `BloomUp`, `Tonemap`, `Fxaa`), `depth_haze.fx` (the Sandbox's
  depth-reading example, #316), `debug.fx`, `error.fx`.
- **`lit.fx` techniques:** `Default`, `AlphaTest`, `Unlit`, `Skinned`, `Lightmapped` (#313, `Default` for a
  brush level's faces, with the baked light from `Lightmap` at `register(s6)` and the second texture
  coordinate), the instanced `Instanced`, `AlphaTestInstanced`, `UnlitInstanced` (#309), and the casters
  `ShadowCaster`, `ShadowCasterSkinned`, `ShadowCasterInstanced`, `ShadowCasterAlphaTest`,
  `ShadowCasterAlphaTestSkinned` (#315: a cut-out caster clips at `AlphaCutoff` as `AlphaTest` does).
  `sprite.fx` adds `UnlitInstanced`, `LitInstanced`, `UnlitBlendInstanced` and `ShadowCasterAlphaTest`.
- **Surface maps (#410):** a material record names `normalMap`, `specularMap`, `specular`, `gloss`,
  `emissiveMap`, `emissive`, `vertexColors`, `environmentMap` and `reflectivity`; the engine binds them to
  `lit.fx`'s `NormalMap` (s2), `SpecularMap` (s3), `EmissiveMap` (s4) and `EnvironmentMap` (s5) beside
  `Albedo` (s0); s1 stays the shadow map. A map not named is a 1×1 stand-in that changes nothing, so every
  lit material draws with one technique. `params` cannot set those names, an `emissiveMap` needs an
  `emissive` colour and a `reflectivity` needs an `environmentMap`: load errors (06 "As built (surface
  materials)").
- **Spot lights (#314):** `common.fxh`'s object tier has `LightSpots[4]` beside `LightPositions` and
  `LightColors`: a spot light's direction and cone, 0 for a lamp that shines all round.
- **Cascades (#315):** `ShadowViewProj` is an array of three, with `ShadowCascadeRects` (where each sits in
  the atlas) and `ShadowCascadeBias`; `ShadowParams.y` is how many are in use.
- **The terrain splat (#307, 14, `terrain_material`):** `terrain.fx`'s `Splat` blends up to four `Layer0`–`Layer3`
  textures (s0, s2, s3, s4) by height and slope weights in the vertex colour, tiles each by `LayerTiling`,
  and multiplies a greyscale `Detail` (s5) by `DetailParams` (repeats, strength).
- **Sampling (#317):** every texture has its mip chain. A material's `Linear` or `Anisotropic` filter is
  trilinear, anisotropic up to `r_anisotropy` taps (default 4); `Point` keeps level 0 (point filtering with
  the level-of-detail bias at its floor), so pixel art, sprites and the UI look as before.
- **Two rules for shader authors (#318, the OpenGL build):** MojoShader packs the constants a shader uses
  and MonoGame fills them by register, so a matrix parameter must use **every column** it declares (read
  `w` even when it is 1), or every constant after it is read from the wrong place; and the GLSL prints
  literals to six decimals, so a tiny one loses its digits (`1e-8` became 0, which turned `lit.fx`'s
  `rsqrt(max(x, 1e-8))` into a divide by zero): keep literals at `1e-4` or larger, or pass a parameter. CI's drawing job runs
  `tools/check_glsl_constants.py` on the engine's compiled effects for the first.

## 4. Public API sketch

```csharp
[Record("material")]
public sealed class MaterialRecord                         // Sage.Simulation (data only; no MonoGame types)
{
    public AssetPath Effect;  public string Technique;
    public RenderPass Pass;   public BlendMode Blend;  public CullMode Cull;
    public bool DepthWrite = true, DepthTest = true, Fog = true;
    public SamplerDesc Sampler;
    public Dictionary<string, MaterialParam> Params;       // float | Vector2/3/4 | AssetPath (texture)
}

public readonly struct MaterialId { }                       // compact renderer index

public sealed class MaterialCache                           // Sage.Client
{
    public MaterialId Resolve(RecordId material);           // builds the MaterialRuntime on first use; sage:error on failure
    public void Apply(MaterialId id, GraphicsDevice device); // sets technique, state, material params (skips if unchanged)
    public void BeginView(in RenderView view, in EnvironmentParams env, in LightSet lights);
}
```

> **Built in part (2026-09-23):** a compiled effect (`.mgfxo`) that changes on disk is reloaded and
> every material rebuilt (05 "As built (asset hot reload)"), because the engine mount is the mgfxc
> output folder. What is still missing is running `mgfxc` when the `.fx` *source* changes, and the
> magenta error shader on a compile failure — the rest of F32.

## 5. Data flow

```
build:   .fx ──dotnet-mgfxc──► .mgfxo (engine/game content mounts)
boot:    RecordStore has material records (merged, validated: every effect param has a value)
render:  first use of RecordId ─► MaterialCache.Resolve ─► load Effect + textures (05)
                                   ─► cache technique, EffectParameters, state objects ─► MaterialId
         per view: BeginView sets frame params once per effect
         per draw: Apply(material) if changed; set object params; draw
dev:     .fx saved ─► watcher runs dotnet-mgfxc ─► success: Effect replaced in place → AssetReloaded
                                                 └► failure: compiler output logged (Shaders, Error);
                                                    old effect kept; error.fx if there was none
```

## 6. Threading and memory
- `MaterialCache` is main-thread only (it touches the `GraphicsDevice`). Material records are immutable during a frame.
- The shader compile for hot reload runs as a background process. Its completion is posted to the main thread (02 §4.5).
- No per-draw allocations: parameter lists are arrays built at resolve time; `Apply` compares ids before setting anything.

## 7. File formats
- `.fx`: MonoGame HLSL effect format (techniques/passes), SM3-compatible.
- `.mgfxo`: compiled MonoGame effect (binary, per profile).
- Material records: JSON (§3.3).

## 8. Errors and fallbacks
| Failure | Behaviour |
|---|---|
| `.fx` compile error (build) | Build fails with the mgfxc output |
| `.fx` compile error (hot reload) | `Error` under `Shaders` with file:line from mgfxc; keep the previous effect |
| Material references an unknown technique | Validation error; the material resolves to `sage:error` |
| Effect parameter without a value | Validation error listing the missing names (GL has no defaults) |
| Material param that the effect doesn't have | `Warn` once (probably a typo); ignored |
| Texture param missing | Texture placeholder (05) |

## 9. Debug and tooling hooks
- **Cvars:** `r_fog`, `mat_hotreload` (`DevOnly`; default = `developer` ≥ 1), `mat_showmissing` (tints materials that use placeholder textures).
- **Commands:**
  - `mat_list` (id, effect, technique, users this frame);
  - `mat_info <id>` (merged record + resolved params + which file set each);
  - `shader_rebuild [file|*]`.
- **Log category:** `Shaders` (compile output, validation, reloads).

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `BasicEffect` in `ModelRendererSystem.render` with `EnableDefaultLighting()` every draw and an unused `lightDirection` (TODO #25) | **Done (step 6):** `lit.fx` `Default` technique via the `lit_default` material; lighting params bound per view |
| `src/Sage.Host/Content/Content.mgcb` (models until R12) | **Deleted (R12).** `dotnet-mgfxc` via `build/Sage.EngineContent.targets` is now the only build-time content step of any kind |
| `.config/dotnet-tools.json` | Also pins `dotnet-mgfxc` (**done, step 6**) |

## 11. v1 scope vs later
- **v1:**
  - `common.fxh`;
  - `lit.fx` (`Default`, `AlphaTest`, `Unlit`), `sprite.fx` (`Unlit`, `Lit`, `UnlitBlend`), `debug.fx`, `error.fx`;
  - the mgfxc MSBuild target;
  - material records with `base`;
  - `MaterialCache` with three-tier binding;
  - hot reload;
  - validation of missing params.
- **Since built:** `Skinned` (§3.7), `Lightmapped`, `Instanced`, point-lit sprites, the terrain splat shader (§3.8).
- **Later:** per-entity parameter blocks; DirectX/Vulkan profiles when switching MonoGame backends.

## 12. Multiplayer-later notes
None. Materials are client-only.

## 13. Open questions
- Does MonoGame 3.8.5's DesktopVK use the same `.fx` → mgfxc path, or its new shader compiler (planned "unified shader compiler" for 3.9)? Re-check before any backend switch. **[unverified]**
- Premultiplied alpha everywhere (textures loaded with the `PremultiplyAlpha` processor, 05)? Yes. Blend states assume premultiplied.

## 14. Build steps
1. ~~`common.fxh` + `lit.fx` + `error.fx`; mgfxc MSBuild target; port the bunny to the `lit_default` material~~ **Done 2026-09-22** (ARCHITECTURE §7 step 6; TODO F2).
2. ~~Material record schema + validation + `MaterialCache` (with 05 records)~~ **Done 2026-09-22** (validation when the material is built, §3.6).
3. ~~`sprite.fx` (with 06 sprites, TODO F1)~~ **Done 2026-09-22.**
4. Shader hot reload.
