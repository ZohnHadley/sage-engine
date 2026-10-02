# 07 · Rendering

> Status: built for a small, lit, shadowed world and tested on the simulation side; drawing itself is checked only by the smoke run. Thin for open worlds: no mesh LOD, instancing, cascades, decals, splat terrain or HDR. Owning assemblies: `Sage.Client` (`Rendering`, MonoGame), `Sage.Simulation` (`Rendering`, `Camera`, headless). Design docs: [06 Rendering](../../design/06-rendering.md), [07 Materials and shaders](../../design/07-materials-and-shaders.md).

## 1. Purpose and scope

Turn the simulation's world into a picture, without letting drawing touch simulation state. Cameras resolve into views, an extract step copies what is visible into a `RenderSnapshot`, and registered passes draw it into the screen or into named render targets. Lighting, shadows, sky, fog, weather, post-processing, particles, sprites and terrain are all part of this pipeline.

It does not own the graphics API (MonoGame does; no raw Vulkan or Direct3D work, see [SRS](../SRS.md) §2.4), the UI (sheet [13](13-ui.md)), skeletal animation (sheet [10](10-animation.md), which supplies the poses that are skinned here), or loading and caching of textures and meshes (sheet [06](06-assets-and-content.md)).

## 2. Responsibilities

- Resolve the world's camera entities into one view per render target each frame, with rigs, priorities and blends.
- Extract meshes, skinned meshes, sprites, lights, particles, debug lines and viewmodels into the snapshot, interpolated between ticks and made camera-relative, culled and sorted.
- Run the pass registry in a fixed stage order and let games add passes without engine edits.
- Build materials from records (effect, technique, render state, parameters) and draw with the engine's shaders.
- Light the world: a sun, hemispheric ambient, up to four point lights per object, exp2 or linear fog, sun shadows.
- Draw the sky from the world clock and sky record, weather from weather records, particles and floating damage numbers, billboard sprites with eight directions, terrain chunks and brush level meshes, and a post-processing chain.
- Provide debug drawing for every other subsystem.

Not responsible for: deciding which camera the player uses (rigs belong to cameras, but a game's rules decide when to switch), or audio and physics debug views other than calling `DebugDraw`.

## 3. Placement and dependencies

The split follows the headless boundary. Everything that can be decided without a GPU is in `Sage.Simulation`, so tests cover it:

| Where | What |
|---|---|
| `Sage.Simulation/Camera` | `Camera`, `CameraPose`, `CameraDirector`, `CameraViews`, `CameraBlend`, `CameraIO`, rigs, `CameraMath`, `DebugCamera`, viewmodels. Owned by the engine (`sage.core`). |
| `Sage.Simulation/Rendering` | Render data (`MeshRenderer`, `SpriteRenderer`, `PointLight`, `ParticleEmitter`), records (`material`, `sky`, `weather`, `particle`, `post_effect`, `sprite_sheet`), `LightRules`, `SkyRules`, `WeatherRules`, `ShadowMath`, `FogMath`, `RenderStage`, `DebugDraw`, `FloatingTexts`. |
| `Sage.Client/Rendering` | `Renderer`, `RenderSnapshot` (internal), the passes, `MaterialCache`, `SpriteBatcher`, `TerrainMeshSystem`, `MapMesh`, `PostProcessPass`, `RenderTargetPool`. Plugin id `sage.client`. |

`Sage.Client` references MonoGame and the simulation; nothing in the base references it. Tests cannot reference it, so extract maths, light selection, shadow fitting, sky curves and pass ordering are written as pure functions in the simulation.

## 4. Interfaces

| Type (file) | Role |
|---|---|
| `Camera` (`Camera/Camera.cs`) | Component `sage:camera`: projection, `FovY`, `OrthoHeight`, `Near`, `Far`, `Priority`, `Enabled`, `Viewport`, `Target` (empty = the screen). |
| `CameraDirector` (`Camera/CameraDirector.cs`) | System `sage.camera.director` (FrameUpdate): the enabled camera with the highest priority wins each target, ties to the lower entity id. |
| `CameraViews`, `CameraView` | The resolved views, in origin space. `ActiveCamera` mirrors the screen view. |
| Rigs (`Camera/Rigs`) | First person, third person (with a probe that pulls the camera in front of walls), and the toggle. Scripted cameras and blends from entity I/O. |
| `IRenderPass`, `RenderPasses`, `[RenderPass("ns:id", RenderStage.X, After, Before)]` (`Client/Rendering/RenderPassApi.cs`) | Experimental (SAGE0130). `Extract(RenderContext)` and `Draw(RenderContext)`. A pass is added with `ctx.Get<RenderPasses>().Add(...)` in module `Init`. |
| `RenderContext`, `RenderViewInfo` | What a pass may touch: the renderer, device, world, and the view's camera-relative matrices. |
| `RenderStage` | In order: `Shadow` (once per world), `Opaque`, `AlphaTested`, `Sky`, `Transparent`, `Debug` (once per view), `PostProcess` and `Overlay` (once, on the screen). |
| `DebugDraw` (`Rendering/DebugDraw.cs`) | Lines, boxes, spheres, capsules; momentary (one tick), timed, or persistent. |
| `MaterialRecord` (`Rendering/RenderData.cs`) | `Effect`, `Technique`, `Pass`, `Blend`, `Cull`, `DepthWrite`, `DepthTest`, `Fog`, `CastShadows`, `Sampler`, `Params`. |

Engine passes: `sage:shadow`, `sage:opaque`, `sage:alpha_tested`, `sage:sky`, `sage:transparent`, `sage:debug`, `sage:post`, `sage:ui`, plus the viewmodel pass.

Shaders (`engine_content/shaders`): `lit.fx` (techniques `Default`, `AlphaTest`, `Unlit`, `Skinned`, `ShadowCaster`, `ShadowCasterSkinned`), `sprite.fx` (`Unlit`, `Lit`, `UnlitBlend`), `sky.fx`, `post.fx` (`Copy`, `Grade`, `Vignette`), `debug.fx`, `error.fx`, shared `common.fxh`. `lit.fx` samples one `Albedo` texture today.

Console: `cam_free`, `cam_set`, `r_passes`, `r_stats`, `weather`; cvars `r_shadows`, `r_shadow_size`, `r_shadow_distance`, `r_fog`, `r_lights`, `r_particles`, `r_weather`, `r_post`, `r_post_grade`, `r_post_vignette`, `r_scale`, `r_vsync`, and the developer cvars `r_wireframe`, `r_freezecull`, `r_debugdraw`, `r_debugdraw_xray`, `r_testview`, `r_testskin`, `r_sprite_facecamera`.

Camera inputs and outputs for wires: `CameraOn [hold [blend [ease]]]`, `CameraOff`, `OnCameraOn`, `OnCameraOff` (sheet [05](05-events-and-logic.md)).

## 5. Data model

| Kind | Ids as declared |
|---|---|
| Records | `material` (plugin `sage.client`), `particle`, `post_effect`, `sprite_sheet`, `weather` (all `sage.client`); `sky`, `viewmodel` (`sage.core`) |
| Components | `sage:camera`, `sage:camera_pose`, `sage:camera_blend`, `sage:scripted_camera`, `sage:first_person_rig`, `sage:third_person_rig`, `sage:viewmodel`, `sage:mesh_renderer`, `sage:skinned_mesh_renderer`, `sage:sprite_renderer`, `sage:point_light`, `sage:particle_emitter` |
| Saved resources | `weather` (current and target weather, so a storm in progress survives a save) |
| Prefab parts | `camera`, `scripted_camera`, `first_person_rig`, `third_person_rig`, `viewmodel`, `skinned_mesh` (`sage.core`); `particles`, `audio` (`sage.client`) |
| Files | glTF binary `.glb` (meshes, skins), PNG/JPG and other stills, `.fx` to `.mgfxo`, sprite sheet records naming a texture and clips |

A material parameter is a number, an array of 1 to 16 numbers, a texture path, or `"rt:name"` for a named render target. OpenGL ignores `.fx` defaults, so every parameter the effect uses needs a value in the material or its base chain; the client checks this when it builds the material.

## 6. Lifecycle and data flow

Each frame, after the fixed ticks have run for it:

1. `FrameUpdate`: rigs write `CameraPose`; `sage.camera.director` resolves views; weather, particles, terrain and map meshes update.
2. `Extract`: per world, `sage.client.extract.camera` first, then passes' `Extract` (which may add views), then meshes, skinned meshes, sprites, lights, particles, debug lines and the viewmodel. Positions are interpolated between ticks and made relative to the camera in origin space, so the view matrix has no translation and large worlds keep precision. Items are frustum-culled, fog-culled where fog hides them wholly, and given a sort key.
3. `Render`: `sage.client.render` plans views (off-screen targets first, then the screen, each by order), buckets items into per-view runs, and walks the stages. The sun's shadow map is drawn once; the screen's views draw into a scene target when post-processing or `r_scale` is on; the post chain ping-pongs between two targets and the last step writes the back buffer.
4. `Overlay`: the UI and floating damage numbers.

The registry is sealed in the client's `Start`; replacing or disabling an engine pass by id is not possible yet (#322). The renderer never reads the ECS during draw. Render targets are named, sized by declaration, and pooled (`RenderTargetPool`), so a security monitor is a camera with a `Target` and a material parameter `rt:name`.

Time of day: the world clock and sky record set the sun direction, colours, fog and shadow strength through `SkyRules.Apply`; weather composes with it. A sun below the horizon gives no light and no shadow.

## 7. Threading, memory and performance

All drawing runs on the main thread. Extract and every pass's `Draw` allocate nothing in steady state: item lists are pooled, sorting and bucketing use pre-sized buffers. Tests assert this for view planning, stage walking, skinned extraction and camera resolution (see requirement REQ-REND-19). Sun shadows are one 2048 R32F map covering 60 m with manual PCF. Up to four point lights affect an object (the brightest nearby, not the closest). Sprites are CPU-batched; there is no instancing yet (#309). Item buckets use a comparison sort with a note to move to radix at 20,000 items (#319). Texture and mesh memory is never released (#308).

## 8. Errors and diagnostics

- A material that fails to build, or a missing texture, draws as the error material or a magenta and black checker; the load logs once under `Assets` or `Shaders`. A mistake in a material record is a load error with file and line (sheet [06](06-assets-and-content.md)).
- A pass with no declaration, a duplicate id, an `After` naming nothing or a pass in another stage, and a cycle are load errors that name the passes. Adding after the seal throws.
- More joints than a skinned draw takes are cut with a warning.
- Tools: `r_passes` (draw order), `r_stats` (last frame's draw counts), `r_wireframe`, `r_freezecull`, `r_debugdraw`, `cam_free` (fly camera, itself an ordinary camera entity at the highest priority). `stat render` and `r_snapshot_dump` are designed, not built (#322).
- Drawing is verified only by the smoke run (`tools/smoke_run.sh` with `Shaders`) and a Windows shader compile in CI (#318).

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-REND-01 | Cameras shall resolve per render target by priority, ties to the lower entity id, with the screen's view mirrored into `ActiveCamera`. | Must | Done | test: TheHigherPriorityCameraWins_AndDisablingItFallsBackToTheOther |
| REQ-REND-02 | Camera rigs (first person, third person with wall probe), scripted cameras from entity I/O and blends shall work and survive a save. | Must | Done | test: ThirdPerson_SitsBehindTheShoulder_LookingWhereThePawnAims; test: ABlendSavedHalfWay_EndsOnTime |
| REQ-REND-03 | A camera shall be able to draw to a named render target, and the extract shall give each view its own run of items. | Must | Done | test: OffScreenTargetsDrawFirst_ThenTheScreen_EachByOrder |
| REQ-REND-04 | Secondary views shall have the same pass set as the main view (shadows, viewmodel) with per-view flags, and blends shall ease out as well as in. | Should | Not started | #323 |
| REQ-REND-05 | Render passes shall be declared with a stage and ordering, and games shall add them without engine edits. | Must | Done | test: PassesDrawByStage_ThenByAfterAndBefore |
| REQ-REND-06 | A game shall be able to replace or disable an engine pass by id, with a clear error on conflict, and dump the frame's items. | Could | Not started | #322 |
| REQ-REND-07 | Materials shall be records (effect, technique, state, parameters, base inheritance) with strict parameter checking. | Must | Done | test: MaterialRecord_ParsesParamsAndState_AndInheritsThroughBase |
| REQ-REND-08 | Materials shall support normal, specular and emissive maps and vertex colours, for Morrowind, Lugaru and S.T.A.L.K.E.R. content. | Must | Not started | #410 |
| REQ-REND-09 | Lighting shall provide a sun, hemispheric ambient and a bounded set of point lights chosen deterministically. | Must | Done | test: OnlyFourSurviveAndTheyAreTheStrongestFour |
| REQ-REND-10 | Lights shall scale to levels with many lamps, flicker and switch from entity I/O, light sprites, and cast shadows; brush levels shall have a lightmapped technique. | Should | Partial: lights switch by I/O; the rest not started | test: ALightIsSwitchedByEntityIO; #314, #313 |
| REQ-REND-11 | The sun shall cast shadows from opaque casters with a stable texel grid under camera moves and origin rebases. | Must | Done (one map) | test: ASubTexelCameraMoveKeepsTheTexelGrid |
| REQ-REND-12 | Shadows shall use cascades, alpha-tested casters and point and spot light shadows for open-world sight lines. | Should | Not started | #315 |
| REQ-REND-13 | The sky, sun, ambient and fog shall follow the world clock through sky records, with fog culling of fully hidden objects. | Must | Done | test: FogCullsWhatItFullyHides; test: TheSkyAtNineAtNightAgainstNoon |
| REQ-REND-14 | Weather shall blend over time with the sky and drop rain or snow near the camera; it shall respect roofs and add lightning and regional weather. | Should | Partial: rain and blend done; roofs not | test: WeatherArrivesOverTimeRatherThanBetweenFrames; #311 |
| REQ-REND-15 | Post-processing shall be a record-driven chain with render scale; it shall add HDR, bloom, anti-aliasing and a depth-reading hook. | Should | Partial: Grade and Vignette chain done | test: TheChainPingPongsItsTargets_AndTheLastWritesTheScreen; #316 |
| REQ-REND-16 | Particles and floating damage numbers shall be record-driven, pooled and capped; particles shall collide, animate sheets and fog-cull. | Should | Partial: emitters and caps done | test: BothCeilingsHold; #310 |
| REQ-REND-17 | Billboard sprites shall pick one of up to eight direction groups from the camera, and be lit by nearby lamps. | Must | Partial: directions done, lit by lamps not | test: EightDirections_PickTheGroupFacingTheCamera; #314 |
| REQ-REND-18 | Terrain shall render with splat materials (weight map and layer textures) and seam-free normals, and with chunk LOD past the near ring. | Must | Partial: one tiling texture, chunk-local normals | #307, #305, #277 |
| REQ-REND-19 | Extract and draw shall allocate nothing per frame in steady state. | Must | Done for view planning, stages, skinned extraction and cameras | test: WalkingTheStagesAllocatesNothing; test: ExtractingSkinnedItems_AllocatesNothing; test: ResolvingCamerasEveryFrameAllocatesNothing |
| REQ-REND-20 | Meshes shall have LOD groups and a per-layer draw distance, and identical meshes shall draw instanced. | Should | Not started | #305, #309 |
| REQ-REND-21 | Impacts shall leave decals (blood, bullet holes, scorch) from a pooled, capped store. | Must | Not started | #306 |
| REQ-REND-22 | Water shall draw as animated, reflective and refractive surfaces. | Should | Not started | #411 |
| REQ-REND-23 | Debug drawing shall offer shapes that are momentary, timed or persistent, with a cap against runaway loops. | Must | Done | test: ARunawayLoopIsCappedRatherThanEatingTheHeap |
| REQ-REND-24 | CI shall check the drawing path automatically (screenshot or readback comparison of sky, shadow, fog and grade). | Should | Not started | #318 |

## 10. Open work

Milestone 4n, Rendering and assets (epic #304):

- #305 4n-1 Mesh LOD and distance culling per layer (P1)
- #306 4n-2 Decals that stay (blood, bullet holes, scorch) (P1)
- #307 4n-3 Terrain splat materials and seam-free normals (P1)
- #410 4n-20 Richer materials: normal, specular and emissive maps, vertex colours (P1)
- #308 4n-4 AssetServer: scopes, ref-counting, eviction and mesh/texture memory budget (P1), see sheet [06](06-assets-and-content.md)
- #309 4n-5 Mesh instancing behind r_instancing (P2)
- #310 4n-6 Particle gaps: collision, animated sheets, soft particles, fog culling (P2)
- #311 4n-7 Rain and weather that respect roofs, plus lightning, puddles, regional weather (P2)
- #313 4n-9 Lightmapped technique for brush levels (P2)
- #314 4n-10 Dynamic light scaling: grid culling, flicker/switch from I/O, lit sprites, light shadows (P2)
- #315 4n-11 Shadow completeness: cascades, alpha-tested casters, sun direction quantising, point/spot shadows (P2)
- #316 4n-12 HDR targets, bloom and a depth-reading effect hook for post-processing (P2)
- #317 4n-13 Texture pipeline: mipmaps, compression, TGA/DDS, cooked formats (P2)
- #318 4n-14 Automated checks of the drawing path (screenshot comparisons in CI) (P2)
- #411 4n-21 Water surfaces: animated, reflective and refractive (P2)
- #319 4n-15 Radix sort for render item buckets (P3)
- #320 4n-16 Sky extras: moon, clouds, turning star field (P3)
- #321 4n-17 Model format coverage: a documented glTF subset, vertex colours and a second UV set (P3)
- #322 4n-18 Render pass registry: replace or disable an engine pass; stat render; r_snapshot_dump (P3)
- #323 4n-19 Split-screen and secondary views: viewmodel, shadows, per-view settings (P3)

Other milestones: #362 4p-6 Skeletal animation coverage for the Sandbox and an editor preview (P2, sheet [10](10-animation.md)); #366 10b-1 Asset browser and material preview (P1, sheet [18](18-editor.md)).

## 11. References

- [Design 06, Rendering](../../design/06-rendering.md): §3.2 `RenderSnapshot`, §3.4 passes and §3.4a views, §3.5 sort key, the build notes for camera components, rigs, scripted cameras, blends, the viewmodel pass, render passes, the world clock and sky, sun shadows, sky and fog, post-processing, particles, weather and point lights.
- [Design 07, Materials and shaders](../../design/07-materials-and-shaders.md): §3.2 techniques, §3.3 material records, §3.5 the `common.fxh` contract.
- [REDESIGN](../../REDESIGN.md) §4.7 Rendering and UI extensibility.
- [MAKING_A_GAME](../../MAKING_A_GAME.md) §10b for the experimental render API (SAGE0130).
- Related sheets: [06 Assets and content](06-assets-and-content.md), [10 Animation](10-animation.md), [13 UI](13-ui.md), [14 World and streaming](14-world-and-streaming.md) for terrain and sectors.
