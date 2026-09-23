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

### As built (F7, 2026-09-22)
- **`GameplayModule`** (`src/Sage.Engine/Gameplay/PawnIntent.cs`) is the seed of this doc's framework: an engine module that registers the `movement_profile`, `ai_profile`, `ai_schedule`, `attribute`, `tag` and `effect` records, the `god` cheat (toggles `state.invulnerable` on the local player) and the gameplay input actions (Move, Jump, Run, Crouch, Attack, Use), and installs `PlayerControlSystem`, `AIThinkSystem`, `EffectSystem`, `CharacterMovementSystem` and `FirstPersonCameraSystem` in every world. It moves to `Sage.Framework` when there is more in it.
- **Controller → intent → movement** works as designed: `PlayerControlSystem` (Commands) copies the tick's `PlayerCommand` into `PawnIntent` on `PlayerControlled` pawns, and `CharacterMovementSystem` reads only the intent (10 "The character controller"). An AI controller writing the same component gets the same movement for free.
- **First-person camera** (`FirstPersonCameraSystem`, FrameUpdate) puts `ActiveCamera` in the pawn's head from the interpolated pose and the command's view angles, at display rate. It sets `ActiveCamera.DrivenByRig`, and the editor's free camera steps aside unless `cam_free 1` clears `ActiveCamera.RigEnabled` (the flag was called `OwnedByRig` until 2026-09-22).
### As built (GameRules and the first creature, 2026-09-22)
- **`GameRules`** (`src/Sage.Engine/Gameplay/GameRules.cs`) is a world resource a game subclasses and installs in its module's `OnWorldCreated`. `Engine.CreateWorld` calls `OnWorldStarted` **after every module has seen the new world**, so the rules can populate a world that is fully set up; a world without a game's rules gets `DefaultGameRules`. `SpawnPlayer` and `OnLoaded` are there for combat (F20) and saves (09) to call; `OnEntityDied` is already called by `EffectSystem` when an entity's health runs out (§3.3). The default is installed by `Engine.CreateWorld` itself, after every module has had its turn — inside `GameplayModule` the "has the game installed its own?" check could never be false (review #47).
- **AI** (`AI.cs`, `AIThinkSystem.cs`) is the HL1 shape of §3.4:
  - **conditions** (`SeeEnemy`, `LostEnemy`, `EnemyInMeleeRange`, `NoEnemy`, `TaskFailed`, `ScheduleDone`);
  - **schedules as records** (`ai_schedule`: an ordered task list plus the conditions that interrupt it), parsed once per record;
  - **tasks registered by name** (`Wait`, `FaceTarget`, `MoveToTarget`, `MeleeAttack`), with an optional number after a colon (`"MoveToTarget:1.6"`). A game adds its own through `GameplayModule.AITasks`;
  - **`ai_profile` records** for sight range, melee range, think rate, attack cooldown and turn speed;
  - **perception** is a distance check, a **sight cone** (`ai_profile.sightAngleDegrees`, 200° by default, so a creature has a blind spot behind it) and a line-of-sight raycast from eye height, so terrain, walls and props hide the player;
  - **think rate** is a few times a second, staggered by entity id; the current task runs every tick because it writes `PawnIntent`.
- **Both controllers write intent in `Phase.Commands`**, `AIThinkSystem` after `PlayerControlSystem`, so movement (`PrePhysics`) acts on a creature's decision in the same tick it was made. `AIThinkSystem` originally sat in `Phase.AI`, four phases *after* the movement that reads `PawnIntent`, which cost every creature a tick of lag and made `FaceTarget` test a yaw the body had not adopted (review #48).
- **A creature keeps the direction the scene placed it facing**: `world.AddCharacter` seeds `PawnIntent.Yaw` from the entity's rotation (10 "The character controller", review #43).
- **The creature walks with the player's controller.** Its tasks write `PawnIntent`, exactly like `PlayerControlSystem`, so chasing uses the same capsule, slopes and step-ups. That is the payoff of the controller/pawn split.
- **Steering** is raycast avoidance (probes ahead and to both sides just above step height, and turns toward the free side). Real pathfinding is F23.
- **Deviations and gaps:**
  - schedule *selection* is code (`ChooseSchedule`), as in HL1's `GetSchedule`; utility scoring or a behaviour tree can replace it without touching the tasks;
  - attacks already deal damage: `MeleeAttackTask` applies the profile's `AttackEffect` with `Effects.Apply` (F18), so `EffectSystem` runs it like any other effect. The hit is also published to an `AIEvents` list the game reads in a later phase, which is still how cues and reactions work until the event bus (04) exists;
  - perception is sight only (no hearing), one enemy type (the local player), and no squads;
  - the AI phase runs after movement in the tick, so intent written this tick moves the creature on the next one.

### As built (attributes, tags and effects, 2026-09-22)
- **Code:** `src/Sage.Engine/Gameplay/Attributes.cs` (attribute and tag records, the id registries, the `Attributes` and `GameplayTags` components) and `Effects.cs` (`effect` records, `ActiveEffects`, `Effects.Apply/Remove/IsActive`, `EffectSystem`).
- **Ids are indices.** `attribute` and `tag` records become small indices (`GameplayRegistries`), so components hold numbers, not strings: attribute values are parallel arrays, tags a 64-bit set. More than 64 tags is reported rather than silently truncated.
- **Effects are the only thing that changes attributes.** Instant effects change the base value (damage, healing); timed and infinite ones are recomputed into the current value every tick, in order: adds, then multiplies, then overrides. `period` re-applies the modifiers on a timer (damage over time), `stacking` is Separate/Refresh/Stack with a cap, and `grantTags` holds tags while the effect runs.
- **Tags gate application**: `requireTags` and `blockTags` decide whether an effect lands. The `god` cheat is exactly that — it gives the player `state.invulnerable`, and damage effects block themselves on it, instead of every damage path checking a flag.
- **Death is a seam, not a feature.** When health reaches 0 the system tags the entity `state.dead` and calls `GameRules.OnEntityDied` once, outside the query loop. What death means is the game's business; the Sandbox respawns the player.
- **Effects never add components.** They are applied from inside system loops (the AI's melee task), where a structural change throws, so an entity is set up once with `world.AddAttributes(entity)` and an effect on anything else is reported and ignored.
- **Not yet:** abilities and cues (F21), the spellmaker, resistances in a damage pipeline (F20), skill progression.

- **Not yet:** possession, combat, inventory, interaction — the rest of this doc.

## 4. API sketch
```csharp
public abstract class GameRules                               // world resource; games subclass it
{
    public virtual void OnWorldStarted(World world) { }
    public virtual Entity SpawnPlayer(World world) => default;
    public virtual void OnEntityDied(World world, Entity victim, Entity killer) { }
    public virtual void OnLoaded(World world) { }              // after a save load (09)
}

public struct Pawn { }                                         // marker: a body a controller can drive, nothing else
public struct PawnIntent { public Vector2 Move; public float Yaw, Pitch; public ActionMask Held, Pressed; }   // written by controllers only
public struct PlayerControlled { }                             // tag: PlayerController reads PlayerCommand into PawnIntent
public struct AIState { public RecordId Profile, Schedule; public int TaskIndex; public ulong Conditions; public Entity Target; public float NextThink, TaskTime, Cooldown; public bool TaskStarted; }

[Record("effect")]  public sealed class EffectRecord { public List<AttributeModifier> Modifiers; public EffectDuration Duration; public float Period; public EffectStacking Stacking; public List<RecordId> GrantTags, RequireTags, BlockTags, Cues; }
[Record("ability")] public sealed class AbilityDef  { public RecordId CostAttribute; public float Cost; public RecordId Cooldown; public Targeting Targeting; public List<RecordId> Effects, Cues; public string Animation; }

public interface IAITask { AITaskStatus Start(ref AITaskContext c) => AITaskStatus.Running; AITaskStatus Run(ref AITaskContext c); }   // registered by name
```

**Still design-only:** `AbilityDef`, `Targeting` and the whole `ability` record (F21, §3.3); combat, inventory and interaction aren't sketched here yet (§3.2). Everything else above is shipped: `Pawn` really is that empty (no `Controller` back-reference — a controller only ever writes `PawnIntent`, nothing points the other way); `EntityRef` is `Entity` in code everywhere (glossary, 03); `AIState` carries `Profile`, `TaskTime` and `Cooldown` too, not just the fields shown before.

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
1. ~~Controller/Pawn/`PawnIntent` + Character + `GameRules`~~ **Done 2026-09-22** (TODO F7, with 10).
2. ~~Attributes/effects/tags + `EffectSystem`~~ **Done 2026-09-22** (TODO F18).
3. Abilities + cues + fireball (TODO F21).
4. Combat + inventory + interaction (TODO F19, F20).
5. ~~AI schedules + perception + one creature~~ **Done 2026-09-22** (TODO F22); its hits already deal damage through the F18 `EffectSystem` (`AttackEffect`). Combat proper (hit detection via physics queries, resistances, F20) is still to build.
