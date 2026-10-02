# 08 · Physics and movement

> Status: built for walking-simulator scale. The facade, the Bepu backend, layers, queries, triggers, contacts, the character controller, simple movers and (from phase 4k) joints and collision groups work and are tested. Movers push what they move into or are blocked by it (#260) and carry riders (#261), water and swimming (#262) and ladders (#263) are built, and joints beyond the facade and the `joint` part are in progress in another thread. Owning assemblies: `Sage.Simulation` (`Physics`, the facade and data), `Sage.Physics3D` (Bepu backend and character controller), `Sage.Gameplay` (`Movers`). Design doc: [10 Physics](../../design/10-physics.md).

## 1. Purpose and scope

Give the simulation solid, queryable space: bodies that fall and rest, rays and sweeps for combat and line of sight, trigger volumes that wire into levels, a character that walks, steps, slopes and crouches, geometry that moves (doors, lifts), and joints for hinged signs, ropes and later ragdolls.

It does not do pathfinding and navigation meshes (sheet [09](09-navigation-and-ai.md)), skeletal animation or ragdoll posing (sheet [10](10-animation.md), which will use the joint API), or drawing physics shapes beyond the debug view (sheet [07](07-rendering.md)). A 2D backend is Stage D work in the [REDESIGN](../../REDESIGN.md) and out of scope here.

## 2. Responsibilities

- Hide the physics engine behind `IPhysicsWorld`, in origin space with Sage types only, so combat, AI, movers and levels run on any backend.
- Own bodies (static, kinematic, dynamic), colliders (box, sphere, capsule, mesh, convex hulls for brushes, terrain meshes), the 32-layer collision matrix and collision groups.
- Answer raycasts, shape sweeps and box overlaps with layer masks, trigger filtering and ignore-self.
- Report trigger enter and exit and, for colliders that ask, solid contact begin and end; feed entity I/O with `OnStartTouch` and `OnEndTouch`.
- Move characters by collide-and-slide sweeps, deterministically and headlessly.
- Move doors and lifts as data (`Mover`), keep joints, and save what a save needs (body velocities, joint state).
- Keep bodies in step with a floating-origin rebase.

Not responsible for: what hits do (the combat pipeline, sheet [16](16-gameplay.md)), footsteps and impact sounds (sheet [11](11-audio.md), which will read physics events).

## 3. Placement and dependencies

`Sage.Core` ← `Sage.Simulation` ← `Sage.Physics3D` ← `Sage.Gameplay`. The facade, collider and body data, layer matrix, parts and joint descriptions are in `Sage.Simulation`, so nothing above the backend names Bepu. `Sage.Physics3D` implements the facade with BepuPhysics 2 (`PhysicsSpace`) and holds the character controller. `Sage.Gameplay` holds movers and everything that uses physics for rules. Nothing here references MonoGame or a kit.

| Plugin id | Registers |
|---|---|
| `sage.physics3d` | The `PhysicsSpace` resource, the `physics_layers` record, parts `body` and `joint`, the step systems, `phys_debug`, `phys_stats`. |
| `sage.gameplay.character` | The character controller, `movement_profile` record, part `character`. |
| `sage.gameplay.movers` | The `mover` component and part, system `sage.movers.move`. |

A world with no physics plugin has no `IPhysicsWorld` resource, and code that can live without it uses `TryGet`.

## 4. Interfaces

| Type (file) | Role |
|---|---|
| `IPhysicsWorld` (`Simulation/Physics/IPhysicsWorld.cs`) | The facade: `AddBody`, `AddHull`, `AddMesh`, `RemoveBody`, `MoveStatic`, `SetPose`, `PoseOf`, velocity and impulse calls, `Raycast`, `Sweep`, `OverlapBox`, `Overlap` (narrow phase, #259), event spans, `Rebase`, `DebugDraw`. |
| `PhysicsSpace` (`Sage.Physics3D/PhysicsSpace.cs`, `PhysicsSpace.Joints.cs`) | The Bepu implementation. |
| `Collider`, `RigidBody`, `BodyKind`, `ColliderShape` (`PhysicsData.cs`) | Components `sage:collider` and `sage:rigid_body`. `Collider.Standing(...)` anchors a capsule at the feet. |
| `LayerMatrix`, `LayerMask`, `PhysicsLayersRecord` | Which layers collide, by name. Query-only layers (hitboxes) are seen only by a mask that names nothing else. |
| `RayHit`, `SweepHit`, `OverlapHit`, `TriggerOverlap`, `ContactEvent`, `JointBroken` | Query results and event records. A ray or sweep hit carries the `Surface` it hit (#270). |
| `PhysicsMaterialRecord`, `SurfaceFace`, `IPhysicsWorld.SetSurface`/`SetSurfaces`/`SurfaceOf` (`PhysicsMaterial.cs`) | Surfaces: what colliders, brush faces and terrain layers are made of (#270). |
| `JointDesc`, `JointKind` (Ball, Hinge, Fixed, Distance), `PhysicsJoint` (`PhysicsJoints.cs`) | Joints. Experimental (SAGE0134). |
| `CharacterController`, `MovementProfileRecord`, `World.AddCharacter` (`Physics/Character.cs`) | The kinematic character and its tuning record. |
| `PawnIntent` | The controller's input: move, look, jump, crouch, run. Set by the player controller or AI. |
| `Mover` (`Gameplay/Movers/Movers.cs`) | A linear door or lift: `OpenOffset`, `Seconds`, `CloseAfter`, and `OnBlocked` (`Reverse`, `Stop` or `Crush`, #260). |

Entity I/O: movers take `Open`, `Close`, `Toggle` and fire `OnFullyOpen`, `OnFullyClosed` and `OnBlocked` (the blocker as activator); triggers fire `OnStartTouch` and `OnEndTouch`; joints take `Break` and fire `OnBreak`.

Console and cvars: `phys_debug` (draw bodies, triggers in magenta, joints as yellow lines), `phys_stats` (active bodies, statics, last step time, gravity), `sim_tickrate`.

Events: trigger and contact spans are valid until the next step and are read in `PostPhysics`; the same facts are sent on the event bus as `TriggerEntered`, `TriggerExited`, `Collided` (with impulse and speed) and `CollisionEnded` by `sage.physics.events` (#269).

## 5. Data model

| Kind | Ids as declared |
|---|---|
| Records | `physics_layers` (list of layer names, index = layer), `physics_material` (friction, restitution, footstep and impact cues, decal, penetration hint, brush textures; #270), `movement_profile` (walk and run speed, acceleration, friction, jump speed, gravity, slope limit, step height, radius, stand and crouch height, ground snap, eye offset) |
| Components | `sage:collider` (with its `Surface`), `sage:rigid_body`, `sage:physics_body` (runtime handle, never saved), `sage:body_motion` (linear and angular velocity, saved), `sage:character_controller`, `sage:joint`, `sage:mover` |
| Prefab parts | `body` (size and mass, or shape, radius and height; options `trigger`, `contacts`, `layer` and `surface`), `footsteps` (stride, reach; #270), `character` (layer, profile), `joint`, `mover` |
| Saved state | Body poses through transforms, `BodyMotion` for dynamic bodies, `Joint` (data, broken flag, how it was made), `Mover` (position, direction, hold) |

A `joint` part looks like `"joint": { "kind": "Hinge", "target": "beam", "anchor": [0, 0.25, 0], "axis": [1, 0, 0], "min": -70, "max": 70 }`; angles are degrees in data and radians in the facade.

## 6. Lifecycle and data flow

The simulation runs at a fixed tick (60 Hz by default). Physics phases, in order:

| Phase | System | Does |
|---|---|---|
| `PrePhysics` | `sage.physics.terrain` | Gives loaded terrain sectors a collision mesh. |
| `PrePhysics` | `sage.character.move` | Turns intent into movement by sweeps, writes the transform. |
| `PrePhysics` | `sage.physics.sync` | Creates, updates and removes bodies to match components. |
| `PrePhysics` | `sage.physics.joints` | Makes facade joints once both bodies exist. |
| `Physics` | `sage.physics.step` | Steps Bepu. |
| `PostPhysics` | `sage.physics.write_back` | Writes dynamic poses and `BodyMotion` back to entities. |
| `PostPhysics` | `sage.physics.joint_breaks`, `sage.io.triggers` | Fire `OnBreak`, `OnStartTouch`, `OnEndTouch`. |
| `Gameplay` | `sage.movers.move` | Advances movers, moves their static colliders, then pushes what they now overlap (characters as far as they have room, dynamic bodies to at least the mover's speed) or is blocked. |
| `Late` | `sage.physics.debug` | Draws the debug view. |

`sage.physics.terrain` and `sage.physics.sync` also run in an edit world so the editor can pick bodies. A floating-origin rebase calls `Rebase(offset)`, which moves every body and static, refreshes static bounds and wakes sleeping bodies. Saving keeps velocities, so a swing saved mid-way resumes in a fresh app.

## 7. Threading, memory and performance

The step is called from the simulation thread, and Bepu spreads its work over its own worker threads (a thread dispatcher sized to the processor count minus one). Cross-run determinism is not yet checked by a test (#273). A steady-state step allocates about 40 bytes a tick (a Bepu profiler allocation, tracked in #273); a chain of twenty limited joints adds nothing to that (test: AChainOfTwentyLinksAllocatesNothingPerTick). Queries take a caller-supplied span and allocate nothing. Sleeping bodies are not re-bounded until they wake, which a rebase accounts for. Many hitboxes on many NPCs cost step time; a budget is part of #273. The whole Sandbox tick uses 0.76 ms of a 16.67 ms budget at 2,000 entities (scale review in the [SRS](../SRS.md) REQ-PERF-04).

## 8. Errors and diagnostics

- A character on the default layer is told it will ignore the ground; a layer list over 32 logs an error; a joint part whose limits are the wrong way round is a load error; a joint on an entity with no dynamic body logs an error and makes nothing.
- A hull that fails to build returns a default `PhysicsBody`, and callers must check it.
- Log category `Physics`. `phys_debug 1` draws what the backend actually simulates, `phys_stats` prints counts and step time.

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-PHYS-01 | Physics shall sit behind a backend-neutral facade in origin space, so combat, AI and levels never name the engine. | Must | Done | test: ThePhysicsPluginInstallsItsSpaceAsTheWorldsIPhysicsWorld |
| REQ-PHYS-02 | The facade shall support static, kinematic and dynamic bodies that fall and rest, with 32 named layers and an ignore matrix. | Must | Done | test: ADynamicBoxFallsAndRestsOnAStaticOne; test: LayerMasks |
| REQ-PHYS-03 | Raycast, shape sweep and box overlap shall respect layer masks, skip triggers unless asked, and ignore the asking entity. | Must | Done | test: RaycastHitsTheNearestCollider_AndRespectsLayers |
| REQ-PHYS-04 | Queries shall include a narrow-phase shape overlap, `RaycastAll` and a sphere overlap; today's `OverlapBox` reports bounds only. | Should | Done | test: OverlapTestsShapesNotBoundsAndSaysWhichWayIsOut; test: RaycastAllReportsEveryHitNearestFirst; test: OverlapSphereReportsWhatTheSphereTouchesNotItsBounds |
| REQ-PHYS-05 | Trigger volumes shall fire `OnStartTouch` and `OnEndTouch` on entity I/O, including for bodies asleep inside them. | Must | Done | test: WalkingIntoATriggerVolumeFiresOnStartTouch; test: SomethingAsleepInATriggerIsStillInsideIt |
| REQ-PHYS-06 | Colliders that ask shall report contact begin and end, with an impulse for impact sounds and damage. | Must | Done | test: AColliderThatAsksForContactsReportsTheirBeginningAndEnd; test: TriggersAndContactsArriveOnTheEventBusWithTheImpactsImpulse; test: TheImpulseOfACollisionBetweenTwoBodiesUsesTheirEffectiveMass |
| REQ-PHYS-07 | Trigger and contact events shall be published as typed game events. | Should | Done | test: TriggersAndContactsArriveOnTheEventBusWithTheImpactsImpulse; test: PhysicsEventsAndTheNewQueriesAllocateNothingPerTick |
| REQ-PHYS-08 | The character controller shall walk, slide along walls, step up low ledges, climb gentle slopes and not cliffs, jump only when grounded, and crouch without standing under a ceiling. | Must | Done | test: StepsOntoALowLedgeButNotAHighOne; test: CrouchesAndCannotStandUnderACeiling |
| REQ-PHYS-09 | A character that ends up inside geometry (a closing door, a teleport) shall be pushed out over a few ticks. | Must | Done | test: ACharacterPlacedInsideABoxIsPushedOutAndWalksOn; test: ACharacterDeepInsideComesOutAFewTicksLaterNeverMoreThanARadiusATick |
| REQ-PHYS-10 | Movers shall sweep their collider and push or block what is in the way (stop, reverse or crush), firing `OnBlocked`. | Must | Done | test: ADoorThatCannotPushACharacterReopensAndSaysWhoBlockedIt; test: TheSandboxHutDoorNoLongerTrapsAPlayerStandingInTheDoorway |
| REQ-PHYS-11 | Movers shall be kinematic bodies with velocity that carry riders and props, and the controller shall inherit the ground's velocity. | Should | Done | test: ACrateAndAPlayerRideALiftUpAndStayOnIt; test: APlayerStandingOnAMovingPlatformIsCarriedWithIt |
| REQ-PHYS-12 | Movers shall include hinged and multi-stage path movers with lock state, for Daggerfall, Morrowind and Half-Life doors and trains. | Should | Done | test: AHingedDoorIsADataOnlyPrefabThatSwingsOnItsHinge; test: ATwoStopLiftIsADataOnlyPrefabThatStopsAtEachFloor; test: ALockedDoorRefusesToOpenUntilUnlocked |
| REQ-PHYS-13 | Joints (ball, hinge, fixed, distance) and collision groups shall be in the facade, with removal with the body and break reporting. | Must | Done (#242) | test: AHingeStopsAtItsLimits; test: SameGroupBodiesPassThroughEachOtherAndOtherGroupsDont |
| REQ-PHYS-14 | A `joint` prefab part shall make joints from data, fire `OnBreak`, and survive a save mid-swing. | Must | Done (#245) | test: ASaveMidSwingResumesTheSwingInAFreshApp |
| REQ-PHYS-15 | The rest of phase 4k (ragdolls, joint tuning and tools) shall follow the joint facade. | Should | Done | test: TheTumblerFallsOnItsWire_SettlesWithinItsLimits_AndGetsUpIntoIdle, test: ASettledRagdollSleeps_AndLoadsDownSettledAndAsleep |
| REQ-PHYS-16 | Swimming shall work: water volumes with surface, drag and buoyancy, a swim mode, surface exit, and floating bodies. | Must | Done (#262) | test: ACharacterFallingIntoWaterSwimsWithItsHeadOut; test: ASwimmerClimbsOutOntoTheBank; test: ACrateFloatsHalfUnder |
| REQ-PHYS-17 | Ladders and climbing shall work from a ladder volume prefab part. | Must | Done (#263) | test: ClimbsALadderToTheLedgeAndStepsOff; test: ALadderBrushEntityInAMapIsClimbedToItsLedge |
| REQ-PHYS-18 | The controller shall offer movement modes: walk, noclip and fly, GoldSrc air-strafe, smooth crouch with a shrinking collider. | Should | Done (#267) | test: ACrouchedCharacterFitsUnderALintelAndHeadHeightSweepsPassOverIt; test: AnAirStrafeProfileBunnyHopsPastTheGroundMaximum; test: FlyIgnoresGravityButCollidesAndNoclipGoesThroughWalls |
| REQ-PHYS-19 | Colliders shall be re-shapeable in place and valid on child entities. | Should | Done (#268) | test: SetShapeShrinksACharactersCapsuleInPlace; test: AChildColliderFollowsItsParentAndIsHitByARayWhereItIs; test: ADynamicBodyAndItsChildrensCollidersAreOneCompound |
| REQ-PHYS-20 | Physics materials and surface types shall be records on colliders, terrain layers and brush textures, returned in hit results. | Should | Done (#270) | test: ABrushFaceIsTheSurfaceItsTextureNames; test: ATerrainLayerIsTheSurfaceOfTheGroundItCovers; test: AFootstepPicksItsCueFromTheSurfaceUnderfoot |
| REQ-PHYS-21 | Bodies shall follow a floating-origin rebase, including sleeping ones and statics. | Must | Done | test: PhysicsBodiesRebaseWithTheWorld |
| REQ-PHYS-22 | A steady-state step shall allocate nothing, hitbox cost shall be budgeted, and runs shall be cross-run deterministic. | Should | Partial: about 40 B a tick | #273 |
| REQ-PHYS-23 | Every `Mover` field shall have a headless assertion, with travel time, `CloseAfter`, a save mid-travel, a world with no physics, a replayed run and a busy level covered. | Should | Done (#272) | test: ATripTakesItsSecondsAndEndsAtItsOpenOffset; test: CloseAfterShutsADoorByItselfAndZeroLeavesItOpen; test: AMoverSavedMidTravelAndMidHoldCarriesOnFromThere; test: AMoverInAWorldWithNoPhysicsStillTravelsAndArrives; test: BlockedIsSetWhileSomethingItCannotPushIsInTheWay; test: ReplayingASteppingRunGivesBitIdenticalPositions; test: ManyMoversBodiesAndCharactersStepWithinABudget |

## 10. Open work

Milestone 4k, Joints and ragdolls (epic #130), is done: #242 to #249 are merged. A ragdoll head that rocks at about its `settleSpeed` can take ten seconds or more to settle; that is left for later (design 12, "As built (phase 4k's exit)").

Milestone 4l, Physics, movement and navigation (epic #258):

- #259 4l-1 Narrow-phase overlap query and depenetration for the character controller (P0)
- #260 4l-2 Movers sweep and push or block what is in their way (closing doors, TODO bug 61) (P0)
- #261 4l-3 Movers become kinematic bodies with velocity (and carry riders) (P1)
- #262 4l-4 Swimming and water volumes (P1)
- ~~#263 4l-5 Ladders and climbing (P1)~~ done: the `ladder` part and the controller's climb
- #266 4l-8 Rotating and multi-stage movers (hinged doors, path movers) (P2)
- #267 4l-9 Movement modes in the controller: noclip/fly, GoldSrc air-strafe, smooth crouch, crouching collider (P2)
- ~~#268 4l-10 Moving a physics shape in place and parented colliders (P2)~~ done: `SetShape`, colliders on child entities, compounds
- #269 4l-11 Game events from physics (Collided, TriggerEntered/Exited) and richer queries (P2)
- #270 4l-12 Physics materials and surface types (P2)
- ~~#272 4l-14 Mover and physics test coverage (P2)~~ done: `MoverTests` and `MoverBudgetTests`
- #273 4l-15 Perf and determinism: 40 B/tick allocation, hitbox cost, sleeping and rebase wake (P3)

Navigation issues of the same milestone (#264, #265, #271) are in sheet [09](09-navigation-and-ai.md).

## 11. References

- [Design 10, Physics](../../design/10-physics.md): §3 key decisions and the build notes for the facade, query-only layers, joints (#242), the joint part (#245), rebasing and the character controller; §11 scope.
- [REDESIGN](../../REDESIGN.md) §0.5 for the 2D backend plan, and §5 phase 4k and 4l.
- [MAKING_A_GAME](../../MAKING_A_GAME.md) §5 Levels: rooms, doors and triggers.
- Related sheets: [05 Events and logic](05-events-and-logic.md) for trigger and mover wiring, [09 Navigation and AI](09-navigation-and-ai.md), [10 Animation](10-animation.md), [14 World and streaming](14-world-and-streaming.md) for rebasing.
