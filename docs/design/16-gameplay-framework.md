# 16 — Gameplay Framework (short)

## 1. Purpose and scope
The optional, genre-generic gameplay layer (`Sage.Framework`, plus `Sage.Framework.Client` for presentation parts such as camera rigs, 01 §3.1). Games use, extend or ignore each module. v1 covers what the Daggerfall-like vertical slice needs; each module is expanded when it's built (roadmap Phase 5).

## 2. Research basis
- UE's Gameplay Framework (GameMode/GameState/PlayerState/PlayerController/Pawn) is shaped by network authority. We take the **lean core**: rules object + Controller/Pawn (survey §2.2).
- GAS (attributes, gameplay effects, tags, cues) is a strong fit for RPG stats and a Daggerfall-style spellmaker (survey §2.2).
- HL1 schedules/tasks/conditions AI (survey §1.2).
- Event-driven, mostly idle behaviour (UnrealScript lesson, survey §1.4).

## 3. Key decisions

### 3.1 Core
- **`GameRules`** (world resource, supplied by the game module in `OnWorldCreated`): spawning the player, death/respawn/game-over, time of day, loading hooks (`OnLoaded` after a save load, 09). Rules logic lives here, not scattered across systems (readiness rule 5). In future multiplayer it becomes server-only, like UE's GameMode. UE's `GameState`/`PlayerState` split is adopted then.
- **Controller / Pawn:**
  - A `Pawn` component marks a possessable body.
  - `PlayerController` (reads `PlayerCommand`, 08) and `AIController` (reads its AI state) write the same **`PawnIntent`** component (move, look, actions).
  - Movement, combat and interaction systems read `PawnIntent`, so players and AI share every code path.

### 3.2 Modules
| Module | v1 | Key design |
|---|---|---|
| **Character** | yes | Uses the KCC (10). `movement_profile` records. First-person camera rig (client, `FrameUpdate`). Third-person later |
| **Attributes / Effects / Tags** (GAS-like) | yes (minimal) | See 3.3 |
| **Abilities** | yes (fireball) | See 3.3 |
| **Combat** | yes (minimal) | Damage pipeline: `Damaged` → resistances (attributes) → health. Hit detection with physics queries (10); sprite melee hits on animation "hit" events (12) |
| **Inventory** | yes (pick up, equip one weapon) | `item` records; an `Inventory` component (item `RecordId`s + counts); equipment slots |
| **Interaction** | yes | The `Use` action → raycast → `Interactable` → fires the I/O output `OnUsed` (04) and an `Interacted` event |
| **AI** | yes (one melee creature) | See 3.4 |
| **Navigation** | later | Navmesh for interiors/battlefields, a coarse graph for the overworld (F23). v1 creatures steer directly + avoid with raycasts |
| **Narrative** (dialogue, quests, journal) | later | Records + entity I/O + events (F24) |
| **Factions** | later | Reputation/relations records (F24) |
| **Economy / life paths** | later | Production chains, markets, professions as data; coarse offline simulation (F25) |
| **Overworld / parties** | later | A second world type (F26) |

### 3.3 Attributes, effects, tags, abilities (GAS-like, without prediction)
- **`Attributes`** component: attribute id → base + current value (ids from `attribute` records: `health`, `mana`, `strength`, `fire_resist`…).
- **`GameplayTags`** component: a bitset of tag ids from `tag` records, with hierarchical names (`state.stunned`, `element.fire`). Tag queries: has any / has all / has none.
- **`effect` records:**
  - modifiers (add / multiply / override on attributes);
  - duration (instant / timed / infinite) + period (damage over time);
  - stacking rules;
  - tags granted while active;
  - tags required/blocked to apply;
  - cue ids (cosmetic).
- **`ActiveEffects`** component: running effect instances (record, source `EntityRef`, remaining time, stacks). An `EffectSystem` (Gameplay phase) ticks them and recomputes current attribute values.
- **`ability` records:** cost (attribute), cooldown (an effect granting a cooldown tag), targeting (self / projectile / area / touch), effects to apply, cue ids, and the animation to play.
- **Cues:** presentation-only reactions (sound, particles, screen flash), played by Frame-schedule systems from `CueTriggered` events (04, 11).
- **Spellmaker:** composing effect records into a *new* `ability` record at runtime. It's saved as data in the save game (09 saved resource), exactly as Daggerfall's custom spells were.

### 3.4 AI (HL1-style first)
- **`schedule` records:** an ordered task list + an **interrupt condition mask** (`new_enemy`, `heavy_damage`, `lost_target`, `heard_noise`…).
- **Tasks are C# classes registered by name** (`MoveTo`, `FaceTarget`, `MeleeAttack`, `Wait`, `PlayAnim`, `FindCover`…) with `Start`/`Run` returning Running/Succeeded/Failed.
- **`AIState` component** (saved, 09): current schedule `RecordId`, task index, task-local data, conditions bitmask, target `EntityRef`.
- **Think rate:** `AIThinkSystem` (AI phase) thinks at 5–10 Hz, **staggered** across agents. Perception (sight cone raycast, hearing from events) updates conditions. `GetSchedule` picks the next schedule from conditions + state.
- **Later:** utility scoring for schedule choice, behaviour trees if schedules get unwieldy, squads/formations (Warband), daily routines (Daggerfall townsfolk).

## 4. API sketch
```csharp
public abstract class GameRules                               // world resource; games subclass it
{
    public virtual void OnWorldStarted(World w) { }
    public virtual EntityRef SpawnPlayer(World w) => EntityRef.None;
    public virtual void OnEntityDied(World w, EntityRef e, EntityRef killer) { }
    public virtual void OnLoaded(World w) { }                 // after a save load (09)
}

public struct Pawn       { public EntityRef Controller; }
public struct PawnIntent { public Vector2 Move; public float Yaw, Pitch; public ActionMask Held, Pressed; }   // written by controllers only
public struct PlayerControlled { }                            // tag: PlayerController reads PlayerCommand into PawnIntent
public struct AIState    { public RecordId Schedule; public int TaskIndex; public ulong Conditions; public EntityRef Target; public float NextThink; }

[Record("effect")]  public sealed class EffectDef  { public List<AttributeModifier> Modifiers; public EffectDuration Duration; public float Period; public List<RecordId> GrantTags, RequireTags, BlockTags, Cues; }
[Record("ability")] public sealed class AbilityDef { public RecordId CostAttribute; public float Cost; public RecordId Cooldown; public Targeting Targeting; public List<RecordId> Effects, Cues; public string Animation; }

public interface IAITask { TaskStatus Start(in AITaskContext c); TaskStatus Run(in AITaskContext c); }   // registered by name
```

## 11. v1 scope vs later
- **v1:**
  - `GameRules`;
  - Controller/Pawn/`PawnIntent`;
  - Character + first-person rig;
  - attributes/effects/tags/abilities (health, mana, damage, burning, fireball);
  - minimal combat, inventory and interaction;
  - AI schedules for one melee creature;
  - log categories `Gameplay`, `AI`;
  - `ai_debug` overlay (schedule, task and conditions above each agent);
  - `give <item>`, `god`, `notarget` cheats.
- **Later:** the rest of the module table.

## 14. Build steps
1. `GameRules` + Controller/Pawn/`PawnIntent` + Character (TODO F7, with 10).
2. Attributes/effects/tags + `EffectSystem` (TODO F18, F21).
3. Abilities + cues + fireball (TODO F21).
4. Combat + inventory + interaction (TODO F19, F20).
5. AI schedules + perception + one creature (TODO F22).
