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
`SpriteAnimator` has no sheet field of its own. The animation system reads the sheet from the entity's `SpriteRenderer.Sheet` (06 §4) and loads its `SpriteSheetData` sim-side.

### As built (F1, 2026-09-22)
- **Code:** `src/Sage.Engine/Rendering/SpriteData.cs` (`SpriteSheetRecord`, `SpriteRenderer`, `SpriteMath`) and `src/Sage.Engine/Animation/SpriteAnimation.cs` (`SpriteAnimator`, `SpriteAnimationSystem`); frame selection in the client's `SpriteExtract` (06 §3.11).
- **Deviation — the sheet is a record**, `sprite_sheet` in `data/**/*.json`, not a `*.sheet.json` asset: records already give hot reload, mod patches and cross-type reference validation, and there is no `AssetServer` yet (05 §3.5 does list sprite animation sets as records). The JSON is otherwise the shape sketched above, plus `texture`, `directions`, `size`, `mode` and an optional `material`.
- **Clips are indexed, not named, in components:** `SpriteAnimator.Clip` is an index into the sheet's clip names sorted alphabetically (`SpriteSheetRecord.ClipIndex("walk")` looks one up), so components hold no strings.
- **The simulation owns time:** `SpriteAnimationSystem` runs in the `Animation` phase at the tick rate and only advances `Time`; the renderer picks the frame with `SpriteMath.FrameAt` (looping clips wrap, others hold their last frame).
- **Not yet:** frame events (`AnimationEvent` needs the event bus, 04), `AnimationStateSystem` picking clips from gameplay state (there is no gameplay state yet), the `anim_debug` overlay.

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
1. ~~`SpriteSheetData` + `SpriteAnimator`~~ **Done 2026-09-22** as the `sprite_sheet` record + `SpriteAnimationSystem` (TODO F1, with 06). `AnimationStateSystem` and `AnimationEvent` wait for gameplay state and the event bus (04).
2. Skeletal pipeline (TODO F9–F12), when Phase 3 starts.
