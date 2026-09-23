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
- **Code:** `src/Sage.Engine/Rendering/SpriteData.cs` (`SpriteSheetRecord`, `SpriteRenderer`, `SpriteMath`) and `src/Sage.Engine/Animation/SpriteAnimation.cs` (`SpriteAnimator`, `SpriteAnimationSystem`); frame selection in the client's `SpriteExtract` (06 §3.11).
- **Deviation — the sheet is a record**, `sprite_sheet` in `data/**/*.json`, not a `*.sheet.json` asset: records already give hot reload, mod patches and cross-type reference validation, and there is no `AssetServer` yet (05 §3.5 does list sprite animation sets as records). The JSON is otherwise the shape sketched above, plus `texture`, `directions`, `size`, `mode` and an optional `material`.
- **Clips are indexed, not named, in components:** `SpriteAnimator.Clip` is an index into the sheet's clip names sorted alphabetically (`SpriteSheetRecord.ClipIndex("walk")` looks one up), so components hold no strings.
- **The simulation owns time:** `SpriteAnimationSystem` runs in the `Animation` phase at the tick rate and only advances `Time`; the renderer picks the frame with `SpriteMath.FrameAt` (looping clips wrap, others hold their last frame).
- **Frame events are built (F20):** a clip carries `events: [{ "frame": 1, "name": "hit" }]`, where `frame` is a step of *that clip* (not an index into the sheet), so re-cutting the art doesn't move the events. `SpriteAnimationSystem` sends every step the tick crossed — once per pass for a looping clip, never again for one that has finished — as an `AnimationEvent` game event (04 §3.2), and combat lands a blow on `hit` (16 §3.2). Two consequences worth knowing: the Animation phase runs *after* Gameplay, so a reader sees the previous tick's events (one tick, 16 ms) — that lag is now the reader's cursor rather than a comment — and the system needs the sheet, so an animator without a `SpriteRenderer` no longer advances.
- **Clips are chosen by name, not by index.** `SpriteAnimator.Clip` is an index (components hold no strings), but *picking* one goes through `SpriteSheetRecord.ClipIndex("idle")`: index 0 is whichever name sorts first, which for a Daggerfall sheet is "attack", so asking for it by number left every creature standing frozen mid-swing.
- **Not yet:** `AnimationStateSystem` picking clips from gameplay state. Combat drives the sprite directly for now — `MeleeCombatSystem` plays the attack record's clip when a swing starts and goes back to "idle" when it ends — which is exactly the two-state machine that wants replacing once there is more to show than swinging and not swinging. Also missing: the `anim_debug` overlay.

### Skeletal animation (later)
- **glTF skins and clips via SharpGLTF.** Skeletons and clips are simulation-side assets (the pose is needed for hit detection); GPU skinning is the `Skinned` technique (07).
- **Animation graphs** (state machines + 1D/2D blend spaces + layered upper/lower body) as records.
- **Sim-side pose sampling** at a reduced rate for distant agents (animation LOD), for Warband crowds.
- Bone **attachments** (weapons, riders), **IK** (feet, look-at), **root motion** optional.
- **Ragdoll hand-off** with physics (10) and blend-back to get up (Lugaru).

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
2. Skeletal pipeline (TODO F9–F12), when Phase 3 starts.
