# 12 — Animation (short)

## 1. Purpose and scope
**v1: sprite animation** for Daggerfall-style billboard creatures and NPCs. **Later: skeletal animation** (glTF skins, blending, attachments, ragdoll hand-off) for HL1-, Lugaru- and Warband-like games. Expanded when skeletal animation starts (roadmap Phase 3).

## 3. Key decisions

### Sprite animation (v1)
- **Sheet data** (`*.sheet.json`, loaded sim-side as `SpriteSheetData`, 05 §3.4):
  - frames (atlas rect, pivot);
  - animations, each with: fps, loop, and frames per **direction group** (5 directions + mirroring, or 8, as in 06 §3.8);
  - optional **frame events** (`"events": { "3": "hit", "0": "footstep" }`).
- **The simulation owns animation time.** In the `Animation` phase, an `AnimationStateSystem` picks the clip from gameplay state (idle, walk, attack, hurt, die) and advances `SpriteAnimator.Time` by the tick. When time crosses an event frame, it sends an `AnimationEvent` game event (04). **Melee hits land on the "hit" frame deterministically**, and sounds/footsteps key off the same events.
- **Rendering** only reads the animator (the interpolated time) to choose the frame at extract (06 §3.8). It never advances animation itself.

```json
{ "texture": "textures/creatures/goblin.png", "directions": 5,
  "frames": [ { "rect": [0, 0, 64, 96], "pivot": [32, 94] } ],
  "animations": {
    "idle":   { "fps": 4,  "loop": true,  "dirs": [[0,1],[2,3],[4,5],[6,7],[8,9]] },
    "attack": { "fps": 10, "loop": false, "dirs": [[10,11,12,13],[14,15,16,17],[18,19,20,21],[22,23,24,25],[26,27,28,29]],
                "events": { "2": "hit" } } } }
```

```csharp
public struct SpriteAnimator { public int Clip; public float Time; public float Speed; public bool Playing; }
[GameEvent] public struct AnimationEvent { public EntityRef Entity; public int EventName; }
```
`SpriteAnimator` has no sheet field of its own. It only carries the clip index and its time; the sheet itself is read from the entity's `SpriteRenderer.Sheet` (06 §4) by rendering, at extract, which is also where the direction group and the frame are picked (06 §3.8) — the animation system never touches the sheet.

### As built (F1, 2026-09-22)
- **Code:** `src/Sage.Simulation/Rendering/SpriteData.cs` (`SpriteSheetRecord`, `SpriteRenderer`, `SpriteMath`) and `src/Sage.Gameplay/Animation/SpriteAnimation.cs` (`SpriteAnimator`, `SpriteAnimationSystem`); frame selection in the client's `SpriteExtract` (06 §3.11).
- **Deviation — the sheet is a record**, `sprite_sheet` in `data/**/*.json`, not a `*.sheet.json` asset: records already give hot reload, mod patches and cross-type reference validation, and there is no `AssetServer` yet (05 §3.5 does list sprite animation sets as records). The JSON is otherwise the shape sketched above, plus `texture`, `directions`, `size`, `mode` and an optional `material`.
- **Clips are indexed, not named, in components:** `SpriteAnimator.Clip` is an index into the sheet's clip names sorted alphabetically (`SpriteSheetRecord.ClipIndex("walk")` looks one up), so components hold no strings.
- **The simulation owns time:** `SpriteAnimationSystem` runs in the `Animation` phase at the tick rate and only advances `Time`; the renderer picks the frame with `SpriteMath.FrameAt` (looping clips wrap, others hold their last frame).
- **Frame events are built (F20):** a clip carries `events: [{ "frame": 1, "name": "hit" }]`, where `frame` is a step of *that clip* (not an index into the sheet), so re-cutting the art doesn't move the events. `SpriteAnimationSystem` sends every step the tick crossed — once per pass for a looping clip, never again for one that has finished — as an `AnimationEvent` game event (04 §3.2), and combat lands a blow on `hit` (16 §3.2). Two consequences worth knowing: the Animation phase runs *after* Gameplay, so a reader sees the previous tick's events (one tick, 16 ms) — that lag is now the reader's cursor rather than a comment — and the system needs the sheet, so an animator without a `SpriteRenderer` no longer advances.
- **Clips are chosen by name, not by index.** `SpriteAnimator.Clip` is an index (components hold no strings), but *picking* one goes through `SpriteSheetRecord.ClipIndex("idle")`: index 0 is whichever name sorts first, which for a Daggerfall sheet is "attack", so asking for it by number left every creature standing frozen mid-swing.
- **Not yet:** `AnimationStateSystem` picking clips from gameplay state. Combat drives the sprite directly for now — `MeleeCombatSystem` plays the attack record's clip when a swing starts and goes back to the idle clip when it ends (their names, and the `hit` event's, are `gameplay_conventions` `animations` since #27) — which is exactly the two-state machine that wants replacing once there is more to show than swinging and not swinging. Also missing: the `anim_debug` overlay.

### Skeletal animation (later)
- **glTF skins and clips via SharpGLTF.** Skeletons and clips are simulation-side assets (the pose is needed for hit detection); GPU skinning is the `Skinned` technique (07).
- **Animation graphs** (state machines + 1D/2D blend spaces + layered upper/lower body) as records.
- **Sim-side pose sampling** at a reduced rate for distant agents (animation LOD), for Warband crowds.
- Bone **attachments** (weapons, riders), **IK** (feet, look-at), **root motion** optional.
- **Ragdoll hand-off** with physics (10) and blend-back to get up (Lugaru).

### As built (skeletons and sampling)
Issue #116 (phase 4d, part of #115), 2026-09-30. Experimental: **SAGE0126** (MAKING_A_GAME §10b).
- **Code:** `src/Sage.Simulation/Animation/` — `Skeleton`, `AnimationClip`, `SkeletonPose` and `JointMask`, `PoseSampler`, `GltfAnimationReader` and `AnimationSet`. Tests: `tests/Sage.Tests/Animation/SkeletalAnimationTests.cs`, against a skinned `.glb` that `SkinnedModelBuilder` (`tests/Sage.Testing`) writes in code with SharpGLTF's Schema2 API: a root → mid → tip chain with an `idle` clip (linear root bob, stepped tip scale) and a `walk` clip (linear mid swing, cubic-spline root slide), all with known keys.
- **SharpGLTF moved to the simulation.** `SharpGLTF.Core` is `Sage.Simulation`'s reference now, not `Sage.Client`'s: it is plain .NET (SAGE0024 allows it), and the pose is needed headlessly (hit detection, sockets, IK). No SharpGLTF type is in the public API; the client's `GltfLoader` reads meshes through the same reference (01 §3.1, ARCHITECTURE D10, THIRD_PARTY_NOTICES).
- **Math is System.Numerics**, row vectors, as the rest of the engine. A joint's local transform is the engine's `Pose` (translation, rotation, scale); a skinning matrix is `Skeleton.InverseBind[j] * SkeletonPose.ModelSpace[j]`, both plain `Matrix4x4` spans.
- **`Skeleton`:** joint names, parents, rest pose (`Pose`) and inverse bind matrices, with every parent before its children (the constructor refuses anything else) (test: ASkeletonWhoseParentComesAfterItsChildIsRefused). Read from glTF it keeps the skin's joint order when that is already parents-first and reorders otherwise; `JointOfSkinIndex` maps the file's skin index (what JOINTS_0 holds) to the joint (test: TheSkeletonIsReadInParentFirstOrderWithItsRestPoseAndInverseBinds).
- **`AnimationClip`:** per-joint translation, rotation and scale channels, each `Step`, `Linear` (slerp for rotation) or `CubicSpline` (glTF's Hermite: in-tangent, value, out-tangent, tangents scaled by the key span), and a duration. A joint a clip does not move keeps its rest transform (test: JointTransformsMatchTheKeysAtChosenTimes). **Looping is the sampler's, not the clip's** (glTF has no flag, and one walk is looped by locomotion and played once by a cut-scene): `PoseSampler.ClipTime` wraps into `[0, Duration)` or clamps to `[0, Duration]` (test: ALoopingClipWrapsAndAOneShotClampsAtItsEnd). **Room for events:** `Events` is a list of `ClipEvent(time, name)` kept sorted by `AddEvent` (test: AClipKeepsItsEventsSortedByTime); reading them from files and sending them is #119.
- **`SkeletonPose`:** local transforms and model-space matrices, rented from the shared `ArrayPool` when the pose is made and returned by `Dispose`. Named so because `Pose` is taken (it is the joint transform). **Never saved:** a pose is recomputed from clip times, which are what #118's `Animator` will save.
- **`PoseSampler`:** `Sample(clip, t, loop, pose)`; `Blend(a, b, w, mask)` into `a` (and a five-argument form into another pose), where `mask` is a `JointMask` of per-joint weights (`SetBranch("spine", 1)` for an upper-body layer) and null blends every joint (test: BlendingWorksWithAndWithoutAMask); `ToModelSpace(skeleton, pose)`, one parents-first pass (test: ToModelSpaceGivesTheExpectedChain). None of them allocates (test: SamplingAllocatesNothing).
- **`GltfAnimationReader`:** reads the first skin of a `.glb` and every animation's transform channels that target its joints, through the VFS, and caches the result by `AssetPath`, a failure included. **Loading is content-time:** `Load(path)` reads a file the first time it is asked for; `TryGet(path, out set)` only looks in the cache and is what a tick may call; `Forget(path)` is for hot reload (test: TheReaderCachesByPathAndTryGetNeverLoads). A missing file, a file that is not a `.glb` and one with no skin are each a warning in the `Animation` category and a null, never a crash (test: ABadOrMissingFileLogsAWarningRatherThanCrashing).
- **Deviations and gaps:** the reader does not apply the transforms of non-joint nodes above the root joint (glTF would); it says so in a warning. Morph weights, several skins per file, a text `.gltf` and animation LOD are not read. Nothing ties a skeleton to an entity yet: the `anim_graph` record, blend spaces, layers and the `Animator` component are #118, GPU skinning is #117, sockets and IK are #120.

## 4. API sketch
The sprite API is inline above: the sheet JSON, `SpriteAnimator` and `AnimationEvent`. The skeletal API is sketched when Phase 3 starts.

## 10. Mapping from today's code
The per-frame `TransformMath.Billboard` call in `Game1` was presentation and moved to the renderer (06 §3.8, 03 §4): **done in F1**, where billboarding happens in the sprite batcher and the sheet's direction group is chosen at extract.

## 11. v1 scope vs later
- **v1:**
  - `SpriteSheetData` + `SpriteAnimator` + `AnimationStateSystem`;
  - frame events → `AnimationEvent`;
  - extract-time frame selection;
  - `anim_debug` overlay (clip, time, direction index above each creature);
  - log category `Animation`.
- **Later:** everything under "Skeletal animation".

## 14. Build steps
1. ~~`SpriteSheetData` + `SpriteAnimator` + frame events~~ **Done 2026-09-22** as the `sprite_sheet` record, `SpriteAnimationSystem` (installed by `AnimationModule`: animation is simulation) and frame events (TODO F1, F20, with 06); **on the event bus since 2026-09-23** (04, R13). `AnimationStateSystem` waits for gameplay state.
2. Skeletal pipeline (TODO F9–F12), phase 4d (#115): skeletons, clips and headless sampling **done** (#116, above); GPU skinning (#117), graphs and the Animator (#118), clip events (#119), sockets and IK (#120) to come.
