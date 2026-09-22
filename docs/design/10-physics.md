# 10 — Physics (short)

## 1. Purpose and scope
Collision, queries, triggers, rigid bodies and the character controller. Built on **BepuPhysics v2**, in `Sage.Engine` (Bepu uses `System.Numerics`, so it has no MonoGame dependency). Expanded when roadmap Phase 2 starts.

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
  3. `PostPhysics`: dynamic bodies → `Transform`; contact/trigger buffers → game events (`TriggerEntered`, `TriggerExited`, `Collided`).
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

### As built (F6, 2026-09-22)
- **Code:** `src/Sage.Engine/Physics/` — `PhysicsData.cs` (components, layers, query results), `PhysicsSpace.cs` (the Bepu simulation, callbacks and queries), `PhysicsCallbackData.cs` (what the worker threads may touch), `PhysicsSystems.cs` (the tick systems, terrain collision and `PhysicsModule`). BepuPhysics 2.4.0 is a package reference of `Sage.Engine`, so physics runs headless.
- **`PhysicsModule`** is an engine module the host loads like `ClientModule`; it creates a `PhysicsSpace` per world, applies the `physics_layers` record and installs the systems. Tests drive it the same way a dedicated server would.
- **Tick order** as designed: `PrePhysics` (terrain collision meshes, new colliders get bodies, kinematic transforms are pushed) → `Physics` (`Simulation.Timestep` with Bepu's thread dispatcher) → `PostPhysics` (dynamic bodies write back to `Transform`, trigger overlaps are published).
- **Shapes:** box, sphere and capsule, plus meshes the engine builds (`AddMesh`). Terrain sectors get one static collision mesh each (32k triangles for a 1024 m sector), which closes F13's collision gap.
- **Queries:** `Raycast` (nearest hit), `Sweep` (box/sphere/capsule, what the character controller will use) and `OverlapBox`. All take a `LayerMask`.
- **Determinism:** `Simulation.Deterministic = true`, so a run repeats on the same machine. Bepu still has no state rollback (§2).
- **Deviations:**
  - **Triggers are lists, not events.** `PhysicsSpace.TriggerEnter/TriggerExit` are spans a system reads in `PostPhysics`; they become `TriggerEntered`/`TriggerExited` game events when the event bus exists (04).
  - `OverlapBox` is **broad phase only**: it can report entities whose shapes don't quite touch.
  - Physics entities are assumed to be **roots**: the sync uses the local transform, so a parented collider would be placed wrong.
  - `Collided` events, `phys_debug` (it needs `DebugDraw`, 06) and dynamic-vs-kinematic teleport smoothing are not built.
  - Stepping allocates ~40 bytes per tick inside Bepu's own profiler; everything else in the frame allocates nothing (TODO #41).
- **Not yet:** the character controller and `movement_profile` records (F7, §14 step 4) and the origin rebasing hook (`Rebase` exists but nothing calls it until R6).

## 4. API sketch
```csharp
public sealed class PhysicsSpace                         // world resource
{
    public bool Raycast(Vector3 from, Vector3 dir, float maxDist, LayerMask mask, out RayHit hit);
    public int  RaycastAll(Vector3 from, Vector3 dir, float maxDist, LayerMask mask, Span<RayHit> hits);
    public bool Sweep(in ShapeDesc shape, in Pose from, Vector3 dir, float maxDist, LayerMask mask, out SweepHit hit);
    public int  Overlap(in ShapeDesc shape, in Pose at, LayerMask mask, Span<EntityRef> results);
    public void Rebase(Vector3 offset);                  // origin sector changed (14)
}

public struct CharacterController { public float Radius, Height, StepHeight, MaxSlopeDeg; public RecordId Profile; [Transient] public bool Grounded; public Vector3 Velocity; }
```

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
3. Triggers → game events. **Overlaps are collected and published as lists (done); the events wait for 04.**
4. KCC + movement profile records (TODO F7).
5. Origin rebasing hook (with 14).
