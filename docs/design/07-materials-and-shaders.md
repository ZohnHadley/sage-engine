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
  common.fxh          frame params, lighting, fog, alpha test, (later) skinning, billboard expansion
  lit.fx              meshes and terrain: techniques Default, AlphaTest, Unlit
  sprite.fx           billboard sprites: techniques Unlit, Lit (both alpha-tested), UnlitBlend
  debug.fx            DebugDraw lines/shapes (vertex colour)
  error.fx            flat magenta; the fallback for anything broken
```
- An MSBuild target in `Sage.Client` runs `dotnet-mgfxc <file>.fx <file>.mgfxo /Profile:OpenGL` for every changed `.fx` before build (incremental, by timestamp). `dotnet-mgfxc` is pinned in the repo's `dotnet-tools.json`, like the MGCB tools in today's `.config/dotnet-tools.json`.
- The `.mgfxo` output goes into the engine content mount, where it's loaded as an `Effect` asset (05). MGCB is no longer needed for shaders (the old plan kept it "for shaders only"; the dotnet tool is simpler and scriptable for hot reload).
- Game and mod shaders follow the same layout in their own mounts; they `#include "common.fxh"` through a shared include path.

### 3.2 Techniques are the variants
Instead of compiling permutations from a define matrix, each `.fx` declares a **small, named set of techniques**. Common code is shared through functions and macros in `common.fxh`:

| Effect | Techniques (v1) | Later |
|---|---|---|
| `lit.fx` | `Default` (lit, opaque), `AlphaTest`, `Unlit` | `Lightmapped`, `Skinned`, `Instanced` |
| `sprite.fx` | `Unlit` (alpha-tested, fog), `Lit` (alpha-tested, nearest lights + fog), `UnlitBlend` (transparent) | `Instanced` |

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
- **Params:** name → value, where the value is a float, a 2–4 element array, or a texture path. **Every parameter an effect uses must get a value** from the material or its `base` chain, because GL ignores `.fx` default values. Missing ones are a validation error, and the engine's `lit_default`/`sprite_default` records supply sane values to inherit from.
- **Per-object variation** (tint, flash-on-hit) uses `RenderItem.Tint`/`SpriteInstance.Tint` (06). Per-entity parameter blocks come later if needed.

### 3.4 Parameter binding
Three tiers, all resolved **once** into cached `EffectParameter` references (never looked up by string per draw):

| Tier | Parameters | When set |
|---|---|---|
| Frame / view | `ViewProj`, `Time`, `FogColor`, `FogParams`, `SunDir`, `SunColor`, `AmbientSky`, `AmbientGround`, `CameraRight`, `CameraUp` | once per effect per view (when the effect first appears in the sorted list) |
| Material | the material's `params` (textures, scalars) | when the material changes between draws (sorting keeps this rare) |
| Object | `World`, `Tint`, `LightPos[4]`, `LightColor[4]` | per draw (or per instance, later) |

`MaterialRuntime` (client) is built from a material record plus its loaded effect:
- the resolved `EffectTechnique`;
- a list of `(EffectParameter, value)` pairs;
- the cached state objects;
- a compact `MaterialId` for sort keys.

It's rebuilt when `RecordsReloaded(material)` or `AssetReloaded(effect/texture)` arrives (04 §3.5).

### 3.5 `common.fxh` contract
The shared include defines:
- the frame/object parameter names above, so all engine and game effects bind the same way;
- `ApplyFog(color, viewDepth)`, `HemiAmbient(normal)`, `SunLight(normal)`, `PointLights(pos, normal)`;
- `AlphaTest(alpha)` (calls `clip`);
- later: `SkinPosition` and billboard corner expansion (for instancing).

Game shaders that include it get lighting and fog consistent with engine materials.

### 3.6 As built (migration step 6)
- **Shaders:** `engine_content/shaders/`: `common.fxh` (the §3.5 contract), `lit.fx` (`Default`, `AlphaTest`, `Unlit`), `sprite.fx` (`Unlit`, `Lit`, `UnlitBlend`; F1) and `error.fx`. `debug.fx` comes with `DebugDraw`.
- **Build:** `build/Sage.EngineContent.targets` runs `dotnet mgfxc … /Profile:OpenGL` (pinned 3.8.2.1105 in `.config/dotnet-tools.json`) and copies `engine_content/` into the exe's `Content/`. Any `.fx`/`.fxh` change recompiles all; unchanged builds skip it. **Deviation:** the target is imported by `Sage.Host` (the exe owns the Content folder), not `Sage.Client`.
- **Material records:** `MaterialRecord` in `src/Sage.Engine/Rendering/RenderData.cs`; engine materials `sage:lit_default`, `sage:unlit_default`, `sage:terrain_default` (one tiling ground texture over `lit_default`, 14 §3), `sage:sprite_default`, `sage:sprite_lit` and `sage:error` in `engine_content/data/materials.json`; the Sandbox inherits `sage:lit_default` for its bunny and `sage:sprite_lit` for its trees.
- **Sprite materials (F1):** the sprite sheet's texture replaces the material's `Albedo` per draw, so one material draws every sheet. Sprite quads carry their own per-vertex colour, multiplied with `AlbedoColor` and the object `Tint`.
- **`MaterialCache`** (client) builds a runtime per material on first use: effect, technique, material params and cached state objects. It binds the three tiers of §3.4, and rebuilds everything lazily when records reload (material hot reload works; **shader** hot reload is §14 step 4, not done).
- **Deviations:**
  - **Validation time:** missing effect parameters, unknown techniques and wrong param shapes are checked when the material is built (effect loaded), not at record load. The error names the material and the missing params, and the material draws as `sage:error`.
  - **Fog switch:** a per-material `FogEnabled` parameter (from the record's `fog`) instead of zeroing the density. Frame-level `r_fog` and the environment's fog go into `FogParams.z`.
  - **`AlbedoColor`:** `lit.fx` has a material-tier `AlbedoColor` that multiplies the texture (so materials can tint without a texture).
  - **Point lights:** not yet (06 §3.9).
- **Textures:** `.png`/`.jpg` load through the VFS with `Texture2D.FromStream` and are premultiplied on load (§13). A missing one becomes a magenta/black checker, with a warning.

## 4. Public API sketch

```csharp
[Record("material")]
public sealed class MaterialRecord                         // Sage.Engine (data only; no MonoGame types)
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
> every material rebuilt (05 "As built (asset hot reload)"), because the engine mount is the MGCB
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
| `src/Sage.Host/Content/Content.mgcb` (models today) | Not used for shaders: `dotnet-mgfxc` via `build/Sage.EngineContent.targets` (**done, step 6**) |
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
- **Later:** `Lightmapped`, `Skinned`, `Instanced` techniques; terrain splat shader (with 14); per-entity parameter blocks; DirectX/Vulkan profiles when switching MonoGame backends.

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
