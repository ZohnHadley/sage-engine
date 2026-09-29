# 10 — Physics (short)

## 1. Purpose and scope
Collision, queries, triggers, rigid bodies and the character controller. Built on **BepuPhysics v2**, in the `Sage.Physics3D` plugin (Bepu uses `System.Numerics`, so it has no MonoGame dependency). Expanded when roadmap Phase 2 starts.

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
- **Queries** go through `PhysicsSpace`: raycast, shape sweep, overlap. Results go into caller-provided spans (no allocation).

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
  - `Collided` events, `phys_debug` (it needs `DebugDraw`, 06) and dynamic-vs-kinematic teleport smoothing are not built.
  - Stepping allocates ~40 bytes per tick inside Bepu's own profiler; everything else in the frame allocates nothing (TODO #41).
### The character controller (F7, 2026-09-22)
- **Code:** `src/Sage.Physics3D/Character/CharacterController.cs` — the `movement_profile` record, the `CharacterController` component and `CharacterMovementSystem` (PrePhysics, before the bodies sync), plus the first-person camera rig. It is simulation code, so it runs headless and a server would run the same paths.
- **Each tick:** intent → acceleration (ground or air, with friction when there is no input) → jump/gravity → **horizontal collide-and-slide** (up to 4 planes) with a **step-up** attempt → **vertical move** (falling or a ceiling) → **ground check** with snapping. The character is a kinematic body, so the world collides with it and it pushes dynamic props.
- **Details that matter:**
  - Horizontal sweeps start a few centimetres above the feet. Otherwise the ground the capsule rests on answers every horizontal sweep at zero distance and the character never moves.
  - A step-up moves forward at least a capsule radius before sweeping down, or the capsule lands on the *edge* of the step, whose blended normal looks like a cliff and gets rejected.
  - On a face steeper than the slope limit the character slides down it and cannot push itself up: projecting motion onto a steep plane otherwise turns "walk into the cliff" into "climb it".
  - Crouching is instant and checks headroom before standing up.
  - **Starting inside the ground is survivable.** A sweep that begins overlapping carries no normal, so the space reports nothing; if the ground check believed that, a character placed a few centimetres into a hill would fall through the world (review #55). When the sweep finds nothing it casts a ray down from the capsule's centre and, if that finds walkable ground, puts the feet back on top of it.
- **One call to place one:** `world.AddCharacter(entity, layer, profile)` adds the controller, a `Pawn`, a `PawnIntent` **seeded from the entity's rotation**, a `Collider.Standing` on the same layer and a kinematic `RigidBody`. Doing it by hand is what let the sweep layer, the collider layer and the capsule height drift apart, and what silently discarded the direction a scene placed a character facing (review #43).
- **Limitations:** no depenetration — a sweep that starts already overlapping carries no normal, so it is ignored and the skin width does the work (a shape-overlap query, §4, would fix it); no moving platforms; no smooth crouch interpolation; **crouching shrinks the sweep capsule but not the `Collider`**, so a crouching character still blocks the world at full height until `PhysicsSpace` can update a shape; the GoldSrc air-strafe profile is still "later".

- **Not yet:** the origin rebasing hook (`Rebase` exists but nothing calls it until R6).

## 4. API sketch
```csharp
public sealed class PhysicsSpace                          // world resource, one Bepu Simulation
{
    public LayerMatrix Layers { get; }                   // named layers: Player, Enemy, Trigger

    public RayHit   Raycast(Vector3 from, Vector3 direction, float maxDistance, LayerMask mask = default);
    public SweepHit Sweep(in Collider shape, in Pose from, Vector3 direction, float maxDistance, LayerMask mask = default);
    public int      OverlapBox(Vector3 center, Vector3 halfExtents, Span<Entity> results, LayerMask mask = default);   // broad phase only

    public Pose    PoseOf(in PhysicsBody body);          // the shape's pose, centre included
    public void    SetPose(in PhysicsBody body, in Collider collider, in Pose pose);   // pose is the entity's
    public Vector3 VelocityOf(in PhysicsBody body);
    public void    Rebase(Vector3 offset);               // origin sector changed (14; no caller yet)

    public ReadOnlySpan<TriggerOverlap> TriggerEnter { get; }   // events when 04 exists
    public ReadOnlySpan<TriggerOverlap> TriggerExit { get; }
}

public struct Collider : IComponent
{
    public ColliderShape Shape; public Vector3 Size; public Vector3 Center; public byte Layer; public bool IsTrigger;
    // Statics the engine builds itself, from geometry rather than from a component:
    //   PhysicsSpace.AddMesh(entity, vertices, indices, position, layer)   // terrain sectors (14)
    //   PhysicsSpace.AddHull(entity, points, position, layer)              // one brush of a level (15 §10a, F16)
    // A hull, not a mesh, for a brush: a brush is convex by construction, and a mesh collider is a
    // one-sided surface that a body ending up behind falls through. Bepu recentres a hull on its centre
    // of mass and returns the offset, which the static's pose has to carry. Both release their shape
    // when the static is removed (F16 — until then the list of engine-built shapes was write-only, and
    // every terrain sector leaked one). A mover pushes its static's pose with MoveStatic, because a
    // static does not follow a transform (tests: EveryBrushBecomesOneStaticHull, OpeningADoorMovesWhatYouWalkInto).
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
*Still design, not built:* `RaycastAll`, a narrow-phase `Overlap` (which depenetration needs), and `[Transient]` field metadata (09).

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
- **Later:** ragdolls (Lugaru/HL1 deaths, F11), constraints, mounts (F8), moving platforms, the GoldSrc movement profile, per-region simulations for very large active areas.

## 12. Multiplayer-later notes
Only the local player's KCC would be predicted (against static geometry). Bepu's lack of rollback doesn't matter for that (survey §5).

## 14. Build steps
1. ~~`PhysicsSpace` resource + `Collider`/`RigidBody`/`PhysicsBody` + body sync~~ **Done 2026-09-22** (TODO F6).
2. ~~Raycast/sweep/overlap queries + layers~~ **Done 2026-09-22** (overlap is broad phase only).
3. Triggers → game events. **Overlaps are collected as lists (`PhysicsSpace.TriggerEnter`/`TriggerExit`) and drive entity I/O's `OnStartTouch`/`OnEndTouch` (F17). The event bus exists (04, R13); moving the overlaps onto it is not done.**
4. ~~KCC + movement profile records~~ **Done 2026-09-22** (TODO F7). Left: depenetration (TODO bug 61).
5. ~~Origin rebasing hook (with 14)~~ **Done 2026-09-24** (R6; "As built (rebasing)").
