# 10 · Animation

> Status: built (phase 4p done, #356). Sprite animation, 8-direction billboards, glTF skeletons and clips, graphs, layers, clip events, sockets, IK, first-person arms and animator LOD work and are tested headlessly. Ragdolls (going down, settling, saves and getting up, #130) are done. Phase 4p added root motion, additive layers, sync markers and per-transition fades, directional attack and block sets, retargeting (`skeleton_map`), foot tilt, pelvis smoothing, look-at and hand IK, morph targets, `anim_debug` for sprites, a skeletal creature in the Sandbox and the editor's animation preview. Owning assemblies: `Sage.Simulation` (skeletal), `Sage.Gameplay` (sprites, foot IK). Design doc: [12 Animation](../../design/12-animation.md).

## 1. Purpose and scope

Animation turns clips and parameters into poses and sprite frames, on the fixed tick, so that the moment a blow lands or a foot touches the ground is deterministic and the same on every run. Two families exist: sprite animation for Daggerfall-style billboards, and skeletal animation (glTF skins) for Half-Life, S.T.A.L.K.E.R. and Warband-style characters and first-person arms.

It deliberately does not do: drawing and skinning on the GPU (rendering, [07](07-rendering.md)), the physics of ragdolls (physics, [08](08-physics.md), with the animation side built in #130), deciding which attack to play (gameplay, [16](16-gameplay.md)), or 2D skeletal animation (out of scope, [SRS](../SRS.md) §9).

## 2. Responsibilities

- Read skeletons and clips from `.glb` files and sample poses without allocating.
- Run an `anim_graph` per entity: states, transitions with fades, 1D and 2D blend spaces (with sync markers), masked override and additive layers, params from the body or from inputs, morph targets set from params.
- Move a body by its clips (root motion), and play one rig's clips on another (retargeting).
- Raise clip and frame events as `AnimationEvent`, as the animator's `OnAnimEvent` output, and as graph triggers.
- Advance sprite animators and choose the frame and direction group for billboards.
- Pose attachments: sockets and bone attachments that follow a joint.
- Solve IK: two-bone, aim chain, foot placement with tilt and a smoothed pelvis, look-at and hand reach.
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
| `LookAtIkSystem`, `HandIkSystem` | Turn the head toward a target; reach a hand to an entity or a joint (#361) | `LookAtIk.cs`, `HandIk.cs` |
| `RootMotionMode`, `Animators.TryTakeRootMotion` | Root motion from the base layer's clips into the body or the character controller (#357) | `RootMotion.cs` |
| `SyncMarkers`, `AdditivePose` | Sync markers on blend spaces; additive layers (#358) | `AnimBlending.cs` |
| `ClipRetargeter`, `SkeletonMapRecord` | Retarget a clip between skeletons (#360) | `Retargeting.cs` |
| `AttackStance`, `AttackStances`, `Animators.InWindow` | Directional attacks and guards, hit windows from clip events (#359) | `AttackStance.cs`, `AnimWindows.cs` |
| `MeshMorphing`, `Animators.GetMorphWeight` | Morph targets on the CPU before skinning (#363) | `src/Sage.Simulation/Rendering/MeshMorphing.cs` |
| `IAnimDebugSource` | Extra `anim_debug` lines; `Sage.Gameplay` installs one for sprites (#364) | `AnimEvents.cs` |
| `Animators.CreatePreviewWorld` | A bare world for the editor's animation preview (#362) | `Animator.cs` |
| `BoneAttachments` | Attach an entity to a socket or joint of another's skeleton | `Attachments.cs` |
| `SpriteAnimationSystem`, `SpriteGraphSystem` | Step sprite clips, and sprites played by a graph | `src/Sage.Gameplay/Animation/SpriteAnimation.cs` |
| `FootIkSystem` | Plants feet through `IPhysicsWorld` | `src/Sage.Gameplay/Animation/FootIk.cs` |

Console: `anim_debug [filter]` lists every animator's layers, params, root motion and LOD rate, and every sprite animator's clip and frame; cvar `anim_lod_distance`; the editor's `anim_preview`, `anim_preview_clip`, `anim_preview_param`, `anim_preview_state`, `anim_preview_step` and `anim_preview_close` (#362). Entity inputs: `SetAnimParam`, `AnimTrigger`; output: `OnAnimEvent`. The conditions `anim_param`, `anim_finished` and `anim_window` read the animator. Events: `AnimationEvent(Entity, Name)` is raised in `Phase.Animation`, so a Gameplay reader sees it on the next tick.

## 5. Data model

| Declaration | Kind | Holds |
|---|---|---|
| `anim_graph` | Record | States (with `rootMotion`, `rootMotionY`), transitions (`on` triggers, their own `fade`, `ease`), blend spaces (up to 16 points, `sync` markers), layers with masks (joints and morph targets) and `blend` (`Override`, `Additive`), params with a source, `rootJoint`, `morphs` |
| `skeleton_map` | Record | `source` and `target` models, `bones` (target joint to source joint), `scale`, `sampleRate` (#360) |
| `anim_events` | Record | Events by clip name for a model, as seconds and a name; several records for one model merge |
| `skeleton_sockets` | Record | Named sockets on joints for a model |
| `viewmodel` | Record | Arms model, graph, material, weapon model and socket |
| `ragdoll` | Record | Bodies and joints by model (data only so far) |
| `sprite_sheet` | Record | Texture, `Directions` (1, 5 mirrored, or 8), billboard mode, frames, named animations with fps, loop, direction groups and frame events |
| `sage:animator` | Component and `animator` part | Graph, layer states and times, params; `Suspended` is saved |
| `sage:sprite_animator` | Component | Clip index, time, speed, playing |
| `sage:bone_attachment` | Component and part | Target, socket or joint, offset |
| `sage:aim_ik`, `sage:foot_ik`, `sage:look_at_ik`, `sage:hand_ik` | Components and parts | IK joints and limits; foot IK's `maxFootTilt` and `pelvisSmoothing` |
| `sage:attack_stance` | Component | How the direction is chosen, the direction (saved), the guard (transient) (#359) |
| `sage:viewmodel_layer` | Tag | Marks entities drawn only by the viewmodel pass |

A graph state and its time are saved; the pose and any snapshot fade are transient, and a load in a fade shows the state directly.

## 6. Lifecycle and data flow

All animation runs on the fixed tick; rendering only reads the interpolated result at extract and never advances animation. Order: sprite systems and `sage.animation.animator` in `Phase.Animation` (the animator after sprites); then in `Phase.Late`, foot IK, aim IK, look-at IK, hand IK and `sage.animation.attachments`, in that order, so attachments see the final pose. Root motion is taken on the logic step: a kinematic body moves in Phase.Animation, a character controller takes it in the next tick's PrePhysics. `sage.combat.stance` chooses an attack direction in Phase.Gameplay, before the melee. Each tick a pose is rewritten from scratch before IK runs.

Hitboxes follow the pose Late leaves, so a strike in Gameplay sees the previous tick's bones (a documented one-tick lag, design 16 hit locations). Records are checked at load: a graph with an unknown state or a bad trigger, a socket with no model or joint, or a viewmodel without arms is a load error, and a hot reload that drops a state falls back to the initial state with a warning.

## 7. Threading, memory and performance

Everything runs on the simulation thread. Sampling, IK, attachments and the viewmodel allocate nothing per tick or per frame (test: SamplingAllocatesNothing, test: AHundredAnimatorsAllocateNothingPerTick, test: FiftyNpcsAllocateNothingPerTick, test: SuspendingAndFadingFromSnapshotsAllocateNothingPerTick). LOD: animators beyond `anim_lod_distance` sample less often but still step every tick, so events and times stay exact (test: DistantAnimatorsSampleLessOftenButStepEveryTick). Blend points are capped at 16 so weights are worked out on the stack.

## 8. Errors and diagnostics

Content errors carry file and line, for graphs, events, sockets, ragdolls and viewmodels (test: AGraphIsCheckedAtLoad, test: SocketRecordsWithoutAModelOrAJointAreLoadErrors, test: AViewmodelWithoutArmsOrAWeaponWithoutASocketIsALoadError). A missing or bad `.glb` logs a warning rather than crashing (test: ABadOrMissingFileLogsAWarningRatherThanCrashing). The log category is `Animation`. `anim_debug` is the first tool to reach for; it lists sprite animators too (test: AnimDebugListsSpriteClipsAndFrames). A bad `skeleton_map`, a socket on a joint the model lacks, a negative transition fade and a `morphs` entry naming an unknown or trigger param are load errors (test: BadSkeletonMapsAreLoadErrors, test: ASocketOnABoneTheModelLacksIsALoadError, test: TransitionFadesAndSyncMarkersAreCheckedAtLoad, test: MorphsAreCheckedAtLoad).

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
| REQ-ANIM-12 | Root motion shall move a character by its clip so feet do not slide. | Must | Done (#357) | test: AnNpcTravelsTheClipsAuthoredDistance_AndItsRootStaysInPlace, test: ACharacterControllerWalksTheClipsDistance, test: ALungeAndAClimbAreAuthoredInClips, test: RootMotionAllocatesNothingPerTick |
| REQ-ANIM-13 | Layers shall blend additively, with sync markers and per-transition fades. | Should | Done (#358) | test: AnAdditiveFlinchLayerComposesOverLocomotion, test: AWalkToRunBlendKeepsFeetInPhaseAtMarkers, test: ATransitionsOwnFadeWinsOverTheStates |
| REQ-ANIM-14 | A graph shall support directional attack and block sets and animation-driven combat states. | Should | Done (#359): what a direction does to damage is the combat rules' (#390) | test: ADataOnlyGraphSwingsFourWays_EachLandingOnItsOwnClipsHit, test: HoldingBlockRaisesTheGuardInTheChosenDirection, test: TheHitWindowIsTakenFromTheClipsEvents |
| REQ-ANIM-15 | A clip library shall be retargetable between skeletons. | Should | Done (#360) | test: RetargetedClipPutsEndEffectorsWhereTheSourceDoes, test: SkeletonMapLetsAModelPlayAnotherRigsClips |
| REQ-ANIM-16 | IK shall tilt feet, smooth the pelvis, look at targets, grab with hands and check sockets at load. | Should | Done (#361) | test: FeetRestOnARamp, test: AStairWalkRaisesThePelvisSmoothlyWithoutPopping, test: ALookAtTargetBehindTheShoulderIsClampedToTheChainsLimits, test: AHandReachesAnEntityOrAGripOnItsOwnSkeleton, test: ASocketOnABoneTheModelLacksIsALoadError |
| REQ-ANIM-17 | The Sandbox and the editor shall exercise and preview skeletal animation. | Should | Done (#362): the hut walker (#327) and the brute; the editor's Animation window | test: TheSandboxsBruteRunsOnASkeletonAndThePreviewOpensItsGraph, test: AGraphRunsWithTheParamsAndStatesThePreviewSets, test: TheAnimPreviewCommandsDriveThePreview |
| REQ-ANIM-18 | Morph targets and facial animation shall be supported. | Could | Done (#363) | test: ABlinkAnimatesThroughAnAnimGraphClipWhileTheBaseTalks, test: AParamDrivesAMorphTargetThroughTheGraphsMorphs, test: TheCpuMorphPassMovesTheVerticesByThePosesWeights |
| REQ-ANIM-19 | Sprites shall be lit, listed by `anim_debug`, and play state-driven clips. | Could | Done (#364): lighting is 4n's `sage:sprite_lit`; state-driven clips are a sprite's `anim_graph` (#119); atlas packing is not built | test: SpritesAreUnlitByDefault_AndLitIsOptIn, test: EntFireOnALampTogglesIt_AndASpriteNearItIsLitByIt, test: AnimDebugListsSpriteClipsAndFrames, test: AGraphRaisesASpritesFrameEventsOnTheTicksThePlainSpriteDoes |

## 10. Open work

Milestone 7, animation (epic #356), is done: #357 to #364 (2026-10-06; handoff [4p](../../history/handoff-2026-10-06-4p.md)). Left by its issues:

- Root motion (#357): a `from: Speed` param fed by root motion is circular; travel ignores a parent's transform; only the base layer gives root motion; the skeletal test game's NPCs still move by tweens.
- Blending (#358): no gradient-band 2D blend; `Animators.Play` uses the state's fade, not a transition's.
- Directional combat (#359): what a direction does to damage (a guard that must match the blow, parries) is the combat rules' (#390).
- Retargeting (#360): rigs need the same rest pose; translation only on the topmost mapped joints.
- Morph targets (#363): TANGENT deltas are not read; a rigid mesh does not animate its morphs; first-person arms are not morphed.
- Sprites (#364): atlas packing through the asset server; `anim_debug` is a console command, not an on-screen overlay.
- The editor preview (#362): the preview does not draw the skinned mesh (a stick figure), and graphs are not edited in it.

Milestone 4k, joints and ragdolls (epic #130), is done.

Related: #327 4o-3, footstep and surface sounds from animation events, is done (audio sheet [11](11-audio.md), design 10 "As built (surfaces)"): a skeleton's `foot_left` and `foot_right` clip events step on the surface under that foot.

## 11. References

- [Design 12](../../design/12-animation.md): sprite animation, the skeletal status sections, "Left for later" lists.
- [Design 16](../../design/16-gameplay-framework.md): hit locations and the pose-lag decision.
- [REDESIGN](../../REDESIGN.md) §5, phases 4d and 4k; [MAKING_A_GAME](../../MAKING_A_GAME.md) §10b.
- Siblings: [Rendering](07-rendering.md), [Physics](08-physics.md), [Audio](11-audio.md), [Gameplay](16-gameplay.md); parent [SRS](../SRS.md).
