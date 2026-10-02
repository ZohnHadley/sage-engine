# 10 — Physics (short)

## 1. Purpose and scope
Collision, queries, triggers, rigid bodies and the character controller. Everything outside the physics plugin reaches it through one interface, **`IPhysicsWorld`** (`Sage.Simulation`, issue #30), so a 2D backend can stand in for the 3D one (REDESIGN §0.5). The 3D backend is **BepuPhysics v2**, in the `Sage.Physics3D` plugin (Bepu uses `System.Numerics`, so it has no MonoGame dependency). Expanded when roadmap Phase 2 starts.

## 2. Research basis
Bepu v2 (survey §3.7): .NET 8, SIMD, multithreaded, CCD; ragdoll and character *demos* (dynamic character without crouch or step-up); **local determinism only; no full-state snapshot/restore**. That's fine for single-player, and it's why the character controller is our own kinematic one.

## 3. Key decisions
- **One Bepu `Simulation` per `World`**, as the world resource `PhysicsSpace`. Bepu owns its body/static storage. Components hold **handles** (plan review #2):

  | Component | Data |
  |---|---|
  | `Collider` | shape (`Box`, `Sphere`, `Capsule`, `Mesh` via `AssetPath` → `CollisionMesh`, `Heightfield`), layer, `IsTrigger` |
  | `RigidBody` | kind (`Static`, `Kinematic`, `Dynamic`), mass, friction, restitution |
  | `PhysicsBody` | `[Transient]` Bepu handle, created and destroyed by structural notifications (04 §3.3). Never saved: rebuilt on load (09 §3.5) |

- **Tick order** (03 phases):
  1. `PrePhysics`: kinematic bodies, teleports and the character controller push `Transform` → Bepu.
  2. `Physics`: `Simulation.Timestep(tickDt)` with Bepu's thread dispatcher.
  3. `PostPhysics`: dynamic bodies → `Transform`; contact/trigger buffers → the entity-I/O outputs `OnStartTouch`/`OnEndTouch` (04 §3.4). *Design-only:* `TriggerEntered`/`TriggerExited`/`Collided` as **game events** — no such type exists in the code (checked 2026-09-25); a system reads the spans instead.
- **Coordinates:** Bepu runs in origin space (the same as `GlobalTransform`). When the origin sector changes (14), all bodies are shifted once.
- **Layers:** a 32-bit layer mask per collider plus a collision matrix record (`physics_layers`). Filtered in Bepu's narrow-phase callbacks.
- **Contact callbacks run on Bepu worker threads.** They write only to per-thread pooled buffers, which are merged on the main thread in `PostPhysics` (no world access from callbacks).
- **Kinematic character controller (KCC)**, not a dynamic body:
  - a capsule;
  - collide-and-slide with sweep tests (up to 4 iterations);
  - step-up (sweep up → forward → down), slope limit, ground snapping;
  - crouch with a headroom check, jump and gravity.
  
  Movement tuning is in `movement_profile` records (walk/run/air control). A GoldSrc-style air-acceleration profile comes later for HL1-like games.
- **Queries** go through `IPhysicsWorld` (the Bepu `PhysicsSpace` behind it): raycast, shape sweep, overlap. Results go into caller-provided spans (no allocation).

### As built (the facade, issue #30, 2026-09-29)
- **`IPhysicsWorld`** (`src/Sage.Simulation/Physics/IPhysicsWorld.cs`) is what gameplay, levels, entity I/O, origin rebasing and the scale commands use; `PhysicsModule` installs its `PhysicsSpace` under it in every world. It has the world (counts, step time, gravity, the named `Layers`, `Rebase`), **bodies** (`AddBody` for a `Collider` + `RigidBody` of any kind, `RemoveBody`, `AddHull`, `AddMesh`, `MoveStatic`, pose and velocity), **queries** (`Raycast`, `Sweep`, `OverlapBox`, each with a `LayerMask`, `includeTriggers` and an `ignore` entity), **events** (trigger enter/exit, contact begin/end) and **`DrawDebug`**. It names no Bepu type; `PhysicsSpace.Simulation` and its callbacks are internal to `Sage.Physics3D`.
- **`Sage.Gameplay` uses no Bepu type and no `Sage.Physics3D` type but `PhysicsModule` and `CharacterModule`**, which it names in the two composition lists (`BasePlugins`, `GameplayModules.All`); combat, AI, abilities and navigation require the character plugin by id (`[RequiresPlugin("sage.gameplay.character")]`). Checked from the compiled metadata (test: GameplayReachesPhysicsOnlyThroughTheFacade). The project reference stays for those lists: dropping it needs a composition root above both (`Sage.Sdk`, #32, or a profile list per spatial mode).
- **Backend-neutral data lives in `Sage.Simulation`:** `Collider`/`RigidBody`/`PhysicsBody`, `CharacterController`, `movement_profile` and `AddCharacter` (`Physics/Character.cs`), the `body` and `character` prefab parts (`Physics/PhysicsParts.cs`, same ids, options and owning plugins, so shipped prefabs and golden saves are unchanged) and `PhysicsDebugSystem`. `Sage.Physics3D` keeps the Bepu space, its tick systems, terrain collision, `CharacterMovementSystem` and the first-person rig, and declares no component (test: TheEcsSchemaHasTheComponentsOfEveryBaseAssembly).
- **The sweep gap is closed.** A shape cast that starts inside something used to be dropped (`OnHitAtZeroT` was empty), so a swing pressed against its target, or a bolt fired point-blank, found nothing. Now it is a hit at distance 0 with `SweepHit.StartsInside` (no surface, so `Normal` is `-direction`), and it wins over anything further along; combat, projectiles and touch abilities leave their own caster out with `ignore` (test: ASwingHitsATargetPressedAgainstTheAttacker). The character controller passes `ignoreInitialOverlaps`, because it rests a skin width above the floor and wants only what it moves into (test: ASweepThatStartsInsideSomethingHitsItAtDistanceZero). Depenetration is still not built (TODO 61).
- **Contact events are opt-in:** `Collider.ReportContacts` (the `body` part's `"contacts": true`) makes `ContactBegin`/`ContactEnd` report a pair, with the deepest contact's point and a normal from B to A; tracking every pair of a two-thousand-tree world would cost every tick (test: AColliderThatAsksForContactsReportsTheirBeginningAndEnd). **A pair with nothing awake in it keeps its state**: Bepu stops testing it, which used to read as leaving a trigger when a crate fell asleep inside one (test: SomethingAsleepInATriggerIsStillInsideIt).
- **`phys_debug` draws through the facade** (`IPhysicsWorld.DrawDebug`): the backend draws what it simulates, so brush hulls (which have no `Collider`) show up as their bounds, and a body out of step with its transform shows where physics has it (test: PhysicsDebugDrawShowsWhatTheBackendSimulates).

### As built (query-only layers, issue #137, 2026-09-30)
- **A layer that ignores `"*"` collides with nothing** (`LayerMatrix.Apply`; every layer, including ones a later patch appends) and is **query-only** (`LayerMatrix.QueryOnly`, SAGE0127): a query sees it only when its mask names nothing else (`LayerMatrix.Sees`), and then never a collider parented to the query's `ignore` entity. The engine's `physics_layers` has one, `hitbox`, for a creature's hitboxes (16 "As built (hit locations)"): the character controller, foot IK, AI sight and a swing, which all ask for solid things, go on seeing exactly what they saw, and a shot from the eye never meets the shooter's own head (test: AShotAtTheHeadLandsOnHeadForDoubleDamage_AndAHelmetHalvesIt) (test: FiftyNpcsAllocateNothingPerTick).
- **A parented kinematic collider is posed from its parents** (`PhysicsSyncSystem`): composed up the parents' transforms as they are that tick, not `GlobalTransform` (last tick's), so a hitbox on a bone moves with a body the character controller has just moved (test: TheBoxesFollowTheBonesAndTheBody). Dynamic bodies are still assumed to be roots.

### As built (joints, issue #242, 2026-10-01)
Phase 4k's first step (#130, SAGE0134): joints and collision groups in the facade, for a hinged sign or a rope bridge as much as for a ragdoll. The ragdolls built on them are in [design/12](12-animation.md), "As built (going ragdoll, issue #246)" to "As built (phase 4k's exit, issue #249)".
- **`IPhysicsWorld.AddJoint(a, b, JointDesc)`** joins two bodies (`b` a body or a static) and **`AddJoint(a, desc)`** joins `a` to the world; both return a `PhysicsJoint` (a slot and a version, so a stale handle never reaches a reused slot), and `RemoveJoint`, `JointExists` and `JointCount` go with them. `JointDesc` names the `JointKind` — **Ball** (Bepu `BallSocket`, plus a `SwingLimit` cone and a `TwistLimit` when asked), **Hinge** (`Hinge`, plus a `TwistLimit` about its axis), **Fixed** (`Weld`) and **Distance** (`DistanceLimit`: a rope) — anchors in **each body's shape space** (the centre and rotation `PoseOf` reports; world space for the world), an axis in A's space, a rest pose (A's rotation in B's; default: as they are), limits in radians measured as A relative to B, a break force and spring settings (30 Hz, critically damped by default). `JointDesc.FromWorld` fills the local fields from a world anchor and axis. A Bepu constraint joins bodies only, so a joint to the world or a static has a shapeless kinematic anchor of its own, which `BodyCount` and the debug draw leave out and a rebase moves with everything else. A pendulum swings through and holds its length to millimetres (test: APendulumSwingsAndKeepsItsLength); a ball joint shoved and spun stays inside its cone and twist range (test: ABallJointHoldsItsSwingConeAndTwistLimitsUnderAShove); a hinged door stops at either limit and turns only about its hinge (test: AHingeStopsAtItsLimits); a jointed body sleeps like any other and an impulse wakes it (test: AJointedBodySleepsAndAnImpulseWakesIt).
- **Removing a body removes its joints** (and a static's, for joints anchored to it): `RemoveBody` walks the joint slots first, so a destroyed entity never leaves a constraint holding nothing (test: RemovingABodyRemovesItsJoints).
- **A joint breaks** when its constraint's accumulated impulse over the step, divided by the step, is above `BreakForce` (0 = never): read after each step for awake joints, removed, and reported once in **`JointBroken`** (a span like `ContactBegin`: the joint, both entities, the force) (test: ABrokenJointIsReportedOnce).
- **Collision groups:** `AddBody(…, group)` and `SetGroup`/`GroupOf`; bodies sharing a nonzero group never collide (`PhysicsCallbackData.ShouldCollide`), whatever their layers, and bodies in different groups collide as their layers say (test: SameGroupBodiesPassThroughEachOtherAndOtherGroupsDont). **Spin and shoves:** `AngularVelocityOf`/`SetAngularVelocity` and `ApplyImpulse(body, impulse, worldPoint)`, which wakes the body.
- **`phys_debug` draws joints**: a yellow line between the anchors, a cross on A's and the axis in orange for a ball or a hinge (test: TheDebugDrawShowsAJointBetweenItsAnchors).
- **Nothing per tick:** a 20-link chain of limited ball joints with break forces adds no allocation to the step over a world with one loose body; the break check, the span and `BodyCount` are allocation-free (test: AChainOfTwentyLinksAllocatesNothingPerTick).

### As built (the joint part, issue #245, 2026-10-01)
Phase 4k's `joint` part (#130, SAGE0134): joints in data, so a level designer hangs a sign or a lamp without code.
- **`"joint": { "kind": "Hinge", "target": "beam", "anchor": [0, 0.25, 0], "axis": [1, 0, 0], "min": -70, "max": 70 }`** is a prefab part (`JointPart`, owned by `sage.physics3d`) that adds a **`Joint`** component (`sage:joint`) with `kind` (Ball, Hinge, Fixed, Distance), `target`, `anchor`, `targetAnchor`, `axis`, `swing`, `twistMin`/`twistMax`, `min`/`max`, `minDistance`/`maxDistance`, `breakForce` and `drag`; angles are degrees in data and radians in the facade. A part whose limits are the wrong way round (`min` above `max`) is a load error (test: AJointPartWithItsLimitsTheWrongWayRoundIsAnError).
- **The target** is an entity by name (`World.FindByName`, as an I/O target is); empty means the parent if it has a body, else the world. `anchor` is in this entity's space (the entity's origin, not the shape's centre: `Collider.Center` is taken off); `targetAnchor` is in the target's, or a world point for the world, and left out it is the same point as `anchor` where the bodies stand when the joint is made, which is how a sign is hung: put it where it hangs (test: ASignHingedToABeamSwingsAndSettles). The joint's rest pose, where its angles count from, is how they stand then.
- **`sage.physics.joints`** (PrePhysics, after `sage.physics.sync`) makes the facade joint once the entity and its target both have bodies (it waits for a target that is not there yet), and again after a load; the entity must be a dynamic body, or it is an error in the log and no joint is made (test: AJointOnAnEntityWithNoDynamicBodyIsNeverMade). The handle is transient. A joint whose target went (a body takes its joints with it) is marked broken, silently. A destroyed entity takes its joint with it.
- **`drag`** (the part defaults it to 0.5 a second; 0 = none) is the share of a jointed body's speed and spin it loses each second, applied while it is awake and moving: the facade has no damping of its own, and a swing with nothing to lose energy to never stops or sleeps (test: ASignHingedToABeamSwingsAndSettles settles hanging straight down, and asleep). A rope of two distance joints holds a crate hanging (test: ARopeOfDistanceJointsHoldsACrate).
- **`OnBreak` and `Break`:** `sage.physics.joint_breaks` (PostPhysics) reads `IPhysicsWorld.JointBroken`, marks the `Joint` broken and fires `OnBreak` on the entity, the activator what it was joined to; the `Break` input removes the joint and fires the same output, once however often it is sent (tests: ABreakForceBreaksTheJointAndFiresOnBreakOnce, TheBreakInputRemovesTheJointAndFiresOnBreakOnce).
- **Saves:** `Joint` saves its data, `Broken`, and how it was made (`Built`, `BuiltAnchor`, `Rest`), so a load makes the same joint, with the angles still counted from the original rest pose and a world pivot where it was, not where the swing had got to; a broken joint stays broken (test: ABrokenJointStaysBrokenAcrossASaveAndLoad). The pivot of a joint to the world is saved as an origin-space point.
- **A dynamic body saves its velocity:** the physics sync gives every dynamic body a **`BodyMotion`** component (`sage:body_motion`: `Linear`, `Angular`), `sage.physics.write_back` keeps it current each step, and a body made from a load starts with it (not in an edit world). A save in the middle of a swing, loaded into a fresh app, swings on as the uninterrupted one did (test: ASaveMidSwingResumesTheSwingInAFreshApp). A save from before this has no `BodyMotion`: its bodies start at rest, and the golden saves still load.
- A joint's entity should be a root (a dynamic body's pose is written to its local transform); the Sandbox's gantry by the start hangs a sign on a hinge and a lamp on a ball-joint link and a rope, in data.

### As built (rebasing, 2026-09-24 — R6)
`PhysicsSpace.Rebase(offset)` shifts every body and static when the origin sector moves (14 §3). Two
parts of it are not obvious and are silent corruption if missed: a **static's bounds** live in the
broad phase and do not follow its pose, so an unrefreshed static keeps blocking rays where it used to
be; and a **sleeping body** is not re-bounded until it wakes, so it would wake a sector away from its
own collision box. The method updates static bounds and wakes every sleeping island — affordable,
because a rebase happens once per kilometre of travel — and walks the sleeping sets backwards, since
waking one deallocates it.

### As built (F6, 2026-09-22)
- **Code:** `src/Sage.Simulation/Physics/` — `PhysicsData.cs` (components, layers, query results) and `IPhysicsWorld.cs` (what the simulation asks of a world's physics, issue #24); `src/Sage.Physics3D/` — `PhysicsSpace.cs` (the Bepu simulation, callbacks and queries), `PhysicsCallbackData.cs` (what the worker threads may touch), `PhysicsSystems.cs` (the tick systems, terrain collision and `PhysicsModule`). BepuPhysics 2.4.0 is a package reference of `Sage.Physics3D`, so physics runs headless.
- **`PhysicsModule`** is an engine module the host loads like `ClientModule`; it creates a `PhysicsSpace` per world, applies the `physics_layers` record and installs the systems. Tests drive it the same way a dedicated server would.
- **Tick order** as designed: `PrePhysics` (terrain collision meshes, new colliders get bodies, kinematic transforms are pushed) → `Physics` (`Simulation.Timestep` with Bepu's thread dispatcher) → `PostPhysics` (dynamic bodies write back to `Transform`, trigger overlaps are published).
- **Shapes:** box, sphere and capsule, plus meshes the engine builds (`AddMesh`). Terrain sectors get one static collision mesh each (32k triangles for a 1024 m sector), which closes F13's collision gap.
- **Queries:** `Raycast` (nearest hit), `Sweep` (box/sphere/capsule, what the character controller will use) and `OverlapBox`. All take a `LayerMask`.
- **Determinism:** `Simulation.Deterministic = true`, so a run repeats on the same machine. Bepu still has no state rollback (§2).
- **Deviations:**
  - **Triggers are lists, not events**, and still are now that the event bus exists (R13).
    `PhysicsSpace.TriggerEnter/TriggerExit` are spans a system reads in `PostPhysics`, and
    `TriggerOutputSystem` turns them into the `OnStartTouch`/`OnEndTouch` outputs a mapper wires (F17).
    `TriggerEntered`/`TriggerExited` as event structs were the plan and were never built — nothing in
    the codebase declares them (checked 2026-09-25). Wiring in data turned out to be what the levels
    wanted; a system that needs the signal in code reads the span.
  - `OverlapBox` is **broad phase only**: it can report entities whose shapes don't quite touch.
  - **`phys_debug` draws it all** (06 §3.2, needs `r_debugdraw 1`): every collider in its real place — cyan for solid, magenta for triggers — plus each character's swept capsule, green when it is grounded and orange when it is not, and an arrow along the ground normal that turns red on a slope too steep to stand on. A capsule sunk into the ground (review #44) is obvious at a glance in it.
  - **Queries see solid things.** A trigger has no surface, so it stops neither a ray, a sweep nor a sword; `includeTriggers: true` asks for them anyway. Before F20 a trigger volume blocked line of sight and swallowed a swing (review #53).
  - Physics entities are assumed to be **roots**: the sync uses the local transform, so a parented collider would be placed wrong.
  - Dynamic-vs-kinematic teleport smoothing is not built. Contacts arrived with the facade (#30) as opt-in lists (`ContactBegin`/`ContactEnd`), not as `Collided` game events.
  - Stepping allocates ~40 bytes per tick inside Bepu's own profiler; everything else in the frame allocates nothing (TODO #41).
### The character controller (F7, 2026-09-22)
- **Code:** `src/Sage.Simulation/Physics/Character.cs` — the `movement_profile` record, the `CharacterController` component and `AddCharacter`, which gameplay reads without a physics backend (#30); `src/Sage.Physics3D/Character/CharacterController.cs` — `CharacterMovementSystem` (PrePhysics, before the bodies sync), plus the first-person camera rig. It is simulation code, so it runs headless and a server would run the same paths.
- **Each tick:** intent → acceleration (ground or air, with friction when there is no input) → jump/gravity → **horizontal collide-and-slide** (up to 4 planes) with a **step-up** attempt → **vertical move** (falling or a ceiling) → **ground check** with snapping. The character is a kinematic body, so the world collides with it and it pushes dynamic props.
- **Details that matter:**
  - Horizontal sweeps start a few centimetres above the feet. Otherwise the ground the capsule rests on answers every horizontal sweep at zero distance and the character never moves.
  - A step-up moves forward at least a capsule radius before sweeping down, or the capsule lands on the *edge* of the step, whose blended normal looks like a cliff and gets rejected.
  - On a face steeper than the slope limit the character slides down it and cannot push itself up: projecting motion onto a steep plane otherwise turns "walk into the cliff" into "climb it".
  - Crouching is instant and checks headroom before standing up.
  - **Inside something, it gets out first (issue #259).** Before moving, the controller asks `IPhysicsWorld.Overlap` what its capsule actually intersects and pushes out along the deepest overlap's way out, up to four times (getting out of one surface can put it into another), at most a capsule radius per tick so a capsule deep in a wall comes out over a few ticks instead of jumping through it; velocity into the surface is dropped. Resting a skin width above the floor is touching, not overlapping, so a standing character is left alone (test: ACharacterPlacedInsideABoxIsPushedOutAndWalksOn; test: ACharacterStandingOnTheFloorOverlapsNothing).
  - **Starting inside the ground is survivable.** A sweep that begins overlapping carries no normal, and the controller's sweeps leave such overlaps out (`ignoreInitialOverlaps`); if the ground check believed that, a character placed a few centimetres into a hill would fall through the world (review #55). When the sweep finds nothing it casts a ray down from the capsule's centre and, if that finds walkable ground, puts the feet back on top of it.
- **One call to place one:** `world.AddCharacter(entity, layer, profile)` adds the controller, a `Pawn`, a `PawnIntent` **seeded from the entity's rotation**, a `Collider.Standing` on the same layer and a kinematic `RigidBody`. Doing it by hand is what let the sweep layer, the collider layer and the capsule height drift apart, and what silently discarded the direction a scene placed a character facing (review #43).
- **Limitations:** no smooth crouch interpolation; **crouching shrinks the sweep capsule but not the `Collider`**, so a crouching character still blocks the world at full height until `PhysicsSpace` can update a shape; the GoldSrc air-strafe profile is still "later".

- **Not yet:** the origin rebasing hook (`Rebase` exists but nothing calls it until R6).

## 4. API sketch
```csharp
public interface IPhysicsWorld                            // world resource; PhysicsSpace (Bepu) in 3D
{
    LayerMatrix Layers { get; }                          // named layers: Player, Enemy, Trigger
    void Rebase(Vector3 offset);                         // origin sector changed (14)

    PhysicsBody AddBody(Entity entity, in Collider collider, in RigidBody body, in Pose pose);
    void        RemoveBody(in PhysicsBody body);
    void        MoveStatic(in PhysicsBody body, Vector3 position);
    Pose        PoseOf(in PhysicsBody body);             // the shape's pose, centre included
    void        SetPose(in PhysicsBody body, in Collider collider, in Pose pose);   // pose is the entity's
    Vector3     VelocityOf(in PhysicsBody body);

    RayHit   Raycast(Vector3 from, Vector3 direction, float maxDistance, LayerMask mask = default,
                     bool includeTriggers = false, Entity ignore = default);
    SweepHit Sweep(in Collider shape, in Pose from, Vector3 direction, float maxDistance, LayerMask mask = default,
                   bool includeTriggers = false, Entity ignore = default, bool ignoreInitialOverlaps = false);
    int      OverlapBox(Vector3 center, Vector3 halfExtents, Span<Entity> results, LayerMask mask = default,
                        bool includeTriggers = false, Entity ignore = default);   // broad phase only

    ReadOnlySpan<TriggerOverlap> TriggerEnter { get; }   // read in PostPhysics
    ReadOnlySpan<TriggerOverlap> TriggerExit { get; }
    ReadOnlySpan<ContactEvent>   ContactBegin { get; }   // Collider.ReportContacts only
    ReadOnlySpan<ContactEvent>   ContactEnd { get; }

    void DrawDebug(DebugDraw debug, Vector3 around, float range);   // phys_debug
    // … counts, gravity, AddHull/AddMesh, SetVelocity, IsDynamic, IsAwake
}

public struct Collider : IComponent
{
    public ColliderShape Shape; public Vector3 Size; public Vector3 Center; public byte Layer; public bool IsTrigger, ReportContacts;
    // Statics the engine builds itself, from geometry rather than from a component:
    //   IPhysicsWorld.AddMesh(entity, vertices, indices, position, layer)   // terrain sectors (14)
    //   IPhysicsWorld.AddHull(entity, points, position, layer)              // one brush of a level (15 §10a, F16)
    // A hull, not a mesh, for a brush: a brush is convex by construction, and a mesh collider is a
    // one-sided surface that a body ending up behind falls through. Bepu recentres a hull on its centre
    // of mass and returns the offset, which the static's pose has to carry. Both release their shape
    // when the static is removed (F16 — until then the list of engine-built shapes was write-only, and
    // every terrain sector leaked one). A mover pushes its static's pose with MoveStatic, because a
    // static does not follow a transform (tests: EveryBrushBecomesOneStaticHull, OpeningADoorMovesWhatYouWalkInto).
    // Then it asks Overlap(body) what it moved into (issue #260): a character is pushed along the way out
    // if, there, it would overlap nothing but the mover, and a dynamic body gets at least the mover's
    // speed; a character with no room blocks it, and the mover goes back, stops or crushes
    // (Mover.OnBlocked), firing OnBlocked (test: ADoorThatCannotPushACharacterReopensAndSaysWhoBlockedIt).
    // Once it first moves, a mover's static becomes a kinematic body (MakeKinematic) moved with
    // MoveKinematic at its own velocity (issue #261): a crate on it rides, and a grounded character
    // stands on its CharacterController.GroundVelocity, carried along before it moves itself
    // (tests: ACrateAndAPlayerRideALiftUpAndStayOnIt, APlayerStandingOnAMovingPlatformIsCarriedWithIt).
    public static Collider Box(Vector3 size, byte layer = 0);
    public static Collider Standing(float radius, float totalHeight, byte layer = 0);   // stands on the origin
}

public struct CharacterController : IComponent
{
    public RecordId Profile; public byte Layer; public Vector3 Velocity; public float Height;
    public Vector3 GroundNormal; public bool Grounded, OnSteep, Crouching;
    public static CharacterController Create(byte layer, RecordId profile = default);
}

public static void AddCharacter(this World world, Entity entity, byte layer, RecordId profile = default);
```
*Still design, not built:* `RaycastAll` and `[Transient]` field metadata (09). The narrow-phase `Overlap` is built (issue #259): the broad phase finds candidates by bounds, then Bepu's collision batcher runs the step's own contact tests, one pair per candidate, and the deepest contact of each is reported with its depth and the way out, deepest first (test: OverlapTestsShapesNotBoundsAndSaysWhichWayIsOut).

## 10. Mapping from today's code
The terrain's `HeightAt` (14) stopped being the only "collision" once F6 landed: it stays as a cheap ground query, while real collision goes through the sector's static mesh.

## 11. v1 scope vs later
- **v1:**
  - static colliders (box, capsule, mesh, heightfield);
  - trigger volumes → events;
  - raycasts and sweeps;
  - the KCC;
  - simple dynamic props;
  - layers;
  - `phys_debug` (colliders via `DebugDraw`) and `phys_stats`;
  - log category `Physics`.
- **Done since:** joints (constraints) and ragdolls, phase 4k (#130): "As built (joints)" and "As built (the joint part)" above, and design/12 "As built (going ragdoll)" to "As built (phase 4k's exit, issue #249)" for ragdolls (Lugaru/HL1 deaths, F11).
- **Later:** powered and partial ragdolls, mounts (F8), hinged and path movers (#266), the GoldSrc movement profile, per-region simulations for very large active areas.

## 12. Multiplayer-later notes
Only the local player's KCC would be predicted (against static geometry). Bepu's lack of rollback doesn't matter for that (survey §5).

## 14. Build steps
1. ~~`PhysicsSpace` resource + `Collider`/`RigidBody`/`PhysicsBody` + body sync~~ **Done 2026-09-22** (TODO F6).
2. ~~Raycast/sweep/overlap queries + layers~~ **Done 2026-09-22** (overlap is broad phase only).
3. Triggers → game events. **Overlaps are collected as lists (`IPhysicsWorld.TriggerEnter`/`TriggerExit`) and drive entity I/O's `OnStartTouch`/`OnEndTouch` (F17). The event bus exists (04, R13); moving the overlaps onto it is not done.**
4. ~~KCC + movement profile records~~ **Done 2026-09-22** (TODO F7). Depenetration done 2026-10-02 (#259).
5. ~~Origin rebasing hook (with 14)~~ **Done 2026-09-24** (R6; "As built (rebasing)").
