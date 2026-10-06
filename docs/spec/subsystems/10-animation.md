# 10 · Animation

> Status: mostly built. Sprite animation, 8-direction billboards, glTF skeletons and clips, graphs, layers, clip events, sockets, IK, first-person arms and animator LOD work and are tested headlessly. Ragdolls (going down, settling, saves and getting up, #130) are done. Root motion, additive layers, retargeting and morph targets are planned. Owning assemblies: `Sage.Simulation` (skeletal), `Sage.Gameplay` (sprites, foot IK). Design doc: [12 Animation](../../design/12-animation.md).

## 1. Purpose and scope

Animation turns clips and parameters into poses and sprite frames, on the fixed tick, so that the moment a blow lands or a foot touches the ground is deterministic and the same on every run. Two families exist: sprite animation for Daggerfall-style billboards, and skeletal animation (glTF skins) for Half-Life, S.T.A.L.K.E.R. and Warband-style characters and first-person arms.

It deliberately does not do: drawing and skinning on the GPU (rendering, [07](07-rendering.md)), the physics of ragdolls (physics, [08](08-physics.md), with the animation side built in #130), deciding which attack to play (gameplay, [16](16-gameplay.md)), or 2D skeletal animation (out of scope, [SRS](../SRS.md) §9).

## 2. Responsibilities

- Read skeletons and clips from `.glb` files and sample poses without allocating.
- Run an `anim_graph` per entity: states, transitions with fades, 1D and 2D blend spaces, masked layers, params from the body or from inputs.
- Raise clip and frame events as `AnimationEvent`, as the animator's `OnAnimEvent` output, and as graph triggers.
- Advance sprite animators and choose the frame and direction group for billboards.
- Pose attachments: sockets and bone attachments that follow a joint.
- Solve IK: two-bone, aim chain and foot placement.
- Run first-person arms and a held weapon (`viewmodel`).
- Update far animators less often (LOD) without changing results near the player.

Not responsible for: hit locations and hitboxes (gameplay), skinning shaders, footstep sounds (audio and gameplay: the `footsteps` part, #327), or the AI that decides to walk.

## 3. Placement and dependencies

Skeletal animation is in `Sage.Simulation` (`Animation/` and `Camera/Viewmodel.cs`), so it is headless and has no MonoGame. Sprite animation and foot IK are in `Sage.Gameplay` (`Animation/`), because they read physics. The sprite sheet record lives in `Sage.Simulation/Rendering/SpriteData.cs`; the client reads it to draw.

Skeletal records and components are registered for the engine plugin (`RegistrationOwners.Core`); sprite animation and foot IK belong to `sage.gameplay.animation` (`AnimationModule`). Core code registers `AnimatorSystem` and `ViewmodelSystem` from the engine's world setup. The skeletal API is `[Experimental]`: SAGE0126, and SAGE0134 for the suspend and ragdoll parts (MAKING_A_GAME §10b).

## 4. Interfaces

| Type | Role | File |
|---|---|---|
| `GltfAnimationReader`, `AnimationSet` | Reads a skeleton and its clips from `.glb`, cached by path | `src/Sage.Simulation/Animation/GltfAnimationReader.cs` |
| `Skeleton`, `AnimationClip`, `ClipEvent` | Joint hierarchy with rest pose and inverse binds; keyed clips with sorted events | `Skeleton.cs`, `AnimationClip.cs` |
| `PoseSampler` | Samples and blends poses, with or without a `JointMask` | `PoseSampler.cs` |
| `SkeletonPose`, `SkeletonPoses` | A pose buffer, and the world's registry of each entity's pose | `SkeletonPose.cs`, `SkeletonPoses.cs` |
| `Animators` | Static API: params, triggers, `Suspend`, `Resume`, `PlayFrom`, `StateOf` | `Animator.cs`, `AnimatorSuspend.cs` |
| `IAnimationEventSink` | Where clip events go; `Sage.Gameplay` installs one that raises `AnimationEvent` | `AnimEvents.cs` |
| `TwoBoneIk`, `AimChainIk` | Pure solvers | `InverseKinematics.cs` |
| `BoneAttachments` | Attach an entity to a socket or joint of another's skeleton | `Attachments.cs` |
| `SpriteAnimationSystem`, `SpriteGraphSystem` | Step sprite clips, and sprites played by a graph | `src/Sage.Gameplay/Animation/SpriteAnimation.cs` |
| `FootIkSystem` | Plants feet through `IPhysicsWorld` | `src/Sage.Gameplay/Animation/FootIk.cs` |

Console: `anim_debug [filter]` lists every animator's layers, params and LOD rate; cvar `anim_lod_distance`. Entity inputs: `SetAnimParam`, `AnimTrigger`; output: `OnAnimEvent`. The condition `anim_param` reads a param. Events: `AnimationEvent(Entity, Name)` is raised in `Phase.Animation`, so a Gameplay reader sees it on the next tick.

## 5. Data model

| Declaration | Kind | Holds |
|---|---|---|
| `anim_graph` | Record | States, transitions (`on` triggers, `Fade`, `Ease`), blend spaces (up to 16 points), layers with masks, params with a source |
| `anim_events` | Record | Events by clip name for a model, as seconds and a name; several records for one model merge |
| `skeleton_sockets` | Record | Named sockets on joints for a model |
| `viewmodel` | Record | Arms model, graph, material, weapon model and socket |
| `ragdoll` | Record | Bodies and joints by model (data only so far) |
| `sprite_sheet` | Record | Texture, `Directions` (1, 5 mirrored, or 8), billboard mode, frames, named animations with fps, loop, direction groups and frame events |
| `sage:animator` | Component and `animator` part | Graph, layer states and times, params; `Suspended` is saved |
| `sage:sprite_animator` | Component | Clip index, time, speed, playing |
| `sage:bone_attachment` | Component and part | Target, socket or joint, offset |
| `sage:aim_ik`, `sage:foot_ik` | Components and parts | IK joints and limits |
| `sage:viewmodel_layer` | Tag | Marks entities drawn only by the viewmodel pass |

A graph state and its time are saved; the pose and any snapshot fade are transient, and a load in a fade shows the state directly.

## 6. Lifecycle and data flow

All animation runs on the fixed tick; rendering only reads the interpolated result at extract and never advances animation. Order: sprite systems and `sage.animation.animator` in `Phase.Animation` (the animator after sprites); then in `Phase.Late`, aim IK, foot IK and `sage.animation.attachments`, in that order, so attachments see the final pose. Each tick a pose is rewritten from scratch before IK runs.

Hitboxes follow the pose Late leaves, so a strike in Gameplay sees the previous tick's bones (a documented one-tick lag, design 16 hit locations). Records are checked at load: a graph with an unknown state or a bad trigger, a socket with no model or joint, or a viewmodel without arms is a load error, and a hot reload that drops a state falls back to the initial state with a warning.

## 7. Threading, memory and performance

Everything runs on the simulation thread. Sampling, IK, attachments and the viewmodel allocate nothing per tick or per frame (test: SamplingAllocatesNothing, test: AHundredAnimatorsAllocateNothingPerTick, test: FiftyNpcsAllocateNothingPerTick, test: SuspendingAndFadingFromSnapshotsAllocateNothingPerTick). LOD: animators beyond `anim_lod_distance` sample less often but still step every tick, so events and times stay exact (test: DistantAnimatorsSampleLessOftenButStepEveryTick). Blend points are capped at 16 so weights are worked out on the stack.

## 8. Errors and diagnostics

Content errors carry file and line, for graphs, events, sockets, ragdolls and viewmodels (test: AGraphIsCheckedAtLoad, test: SocketRecordsWithoutAModelOrAJointAreLoadErrors, test: AViewmodelWithoutArmsOrAWeaponWithoutASocketIsALoadError). A missing or bad `.glb` logs a warning rather than crashing (test: ABadOrMissingFileLogsAWarningRatherThanCrashing). The log category is `Animation`. `anim_debug` is the first tool to reach for; it does not yet list sprite animators (#364).

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-ANIM-01 | Sprite animators shall step on the tick and raise frame events that land blows deterministically. | Must | Done | `src/Sage.Gameplay/Animation/SpriteAnimation.cs` |
| REQ-ANIM-02 | Billboards shall support 1, 5 (mirrored) and 8 direction groups. | Must | Done | `SpriteSheetRecord.Directions` in `src/Sage.Simulation/Rendering/SpriteData.cs` |
| REQ-ANIM-03 | The engine shall read glTF skeletons and clips and sample poses without allocation. | Must | Done | test: SamplingAllocatesNothing |
| REQ-ANIM-04 | A data graph shall drive states, blends, masked layers and cross-fades. | Must | Done | test: ALayerBlendsOnlyTheBranchItsMaskNames, test: ATwoDimensionalBlendSpaceWeighsItsPointsByDistance |
| REQ-ANIM-05 | Clip events shall reach gameplay, entity I/O and the graph. | Must | Done | `src/Sage.Simulation/Animation/AnimEvents.cs` |
| REQ-ANIM-06 | Weapons and props shall follow sockets and joints. | Must | Done | test: AnAttachedSwordFollowsTheHandThroughAClip |
| REQ-ANIM-07 | Aim, two-bone and foot IK shall pose characters on the ground and at a target. | Must | Done | test: FeetRestOnARamp, test: AimPitchTurnsTheSpineWithinItsLimits |
| REQ-ANIM-08 | First-person arms shall play data clips and a held weapon, hidden in third person. | Must | Done | test: TheViewmodelIsHiddenInThirdPersonAndFromTheEditorsFreeCamera |
| REQ-ANIM-09 | Distant animators shall update less often without changing nearby results. | Should | Done | test: DistantAnimatorsSampleLessOftenButStepEveryTick |
| REQ-ANIM-10 | Animators shall save and load with state and time intact. | Must | Done | test: StateAndTimeSurviveSaveAndLoad |
| REQ-ANIM-11 | A ragdoll shall take over a character on death and blend back to the graph. | Must | Done | test: KnockedDownFaceUp_ItGetsUpWithGetupBackIntoIdle, test: TheCasualtyIsKilled_FallsAndStaysDown |
| REQ-ANIM-12 | Root motion shall move a character by its clip so feet do not slide. | Must | Not started | #357 |
| REQ-ANIM-13 | Layers shall blend additively, with sync markers and per-transition fades. | Should | Not started | #358 |
| REQ-ANIM-14 | A graph shall support directional attack and block sets and animation-driven combat states. | Should | Not started | #359 |
| REQ-ANIM-15 | A clip library shall be retargetable between skeletons. | Should | Not started | #360 |
| REQ-ANIM-16 | IK shall tilt feet, smooth the pelvis, look at targets, grab with hands and check sockets at load. | Should | Not started | #361 |
| REQ-ANIM-17 | The Sandbox and the editor shall exercise and preview skeletal animation. | Should | Partial: the Sandbox has one skeletal creature, the hut walker (#327); no editor preview | test: TheSandboxsWalkerAndPlayerStepOnTheirOwnSurfaces; #362 |
| REQ-ANIM-18 | Morph targets and facial animation shall be supported. | Could | Not started | #363 |
| REQ-ANIM-19 | Sprites shall be lit, listed by `anim_debug`, and play state-driven clips. | Could | Not started | #364 |

## 10. Open work

Milestone 7, animation (epic #356):

- #357 4p-1 Add root motion (P1)
- #358 4p-2 Add additive layers, sync markers and per-transition fades (P2)
- #359 4p-3 Add directional attack/block sets and animation-driven combat states (P2)
- #360 4p-4 Animation retargeting between skeletons (P2)
- #361 4p-5 Finish IK extras: foot tilt, pelvis smoothing, look-at, hand IK, sockets checked at load (P2)
- #362 4p-6 Skeletal animation coverage for the Sandbox and an editor preview (P2)
- #363 4p-7 Morph targets and facial animation (P3)
- #364 4p-8 Sprite animation: lighting, `anim_debug` for sprites, and state-driven clips (P3)

Milestone 4k, joints and ragdolls (epic #130), is done.

Related: #327 4o-3, footstep and surface sounds from animation events, is done (audio sheet [11](11-audio.md), design 10 "As built (surfaces)"): a skeleton's `foot_left` and `foot_right` clip events step on the surface under that foot.

## 11. References

- [Design 12](../../design/12-animation.md): sprite animation, the skeletal status sections, "Left for later" lists.
- [Design 16](../../design/16-gameplay-framework.md): hit locations and the pose-lag decision.
- [REDESIGN](../../REDESIGN.md) §5, phases 4d and 4k; [MAKING_A_GAME](../../MAKING_A_GAME.md) §10b.
- Siblings: [Rendering](07-rendering.md), [Physics](08-physics.md), [Audio](11-audio.md), [Gameplay](16-gameplay.md); parent [SRS](../SRS.md).
