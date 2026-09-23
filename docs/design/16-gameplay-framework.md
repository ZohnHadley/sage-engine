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
| **Combat** | yes (minimal) | **Built (F20):** `Combat.ApplyDamage` → the damage type's resistance attribute → an effect on health → a `Damaged` game event (04). `attack` records, `Melee` + `MeleeCombatSystem`, hit detection by swept sphere (10), and sprite melee landing on the clip's "hit" event (12) |
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
- **Spellmaker:** composing effect records into a *new* `ability` record at runtime. It's saved as data in the save game (09 saved resource), exactly as Daggerfall's custom spells were. **Built 2026-09-23** — see "As built (the spellmaker)".

### 3.4 AI (HL1-style first)
- **`schedule` records:** an ordered task list + an **interrupt condition mask** (`new_enemy`, `heavy_damage`, `lost_target`, `heard_noise`…).
- **Tasks are C# classes registered by name** (`MoveTo`, `FaceTarget`, `MeleeAttack`, `Wait`, `PlayAnim`, `FindCover`…) with `Start`/`Run` returning Running/Succeeded/Failed.
- **`AIState` component** (saved, 09): current schedule `RecordId`, task index, task-local data, conditions bitmask, target `EntityRef`.
- **Think rate:** `AIThinkSystem` (AI phase) thinks at 5–10 Hz, **staggered** across agents. Perception (sight cone raycast, hearing from events) updates conditions. `GetSchedule` picks the next schedule from conditions + state.
- **Later:** utility scoring for schedule choice, behaviour trees if schedules get unwieldy, squads/formations (Warband), daily routines (Daggerfall townsfolk).

### As built (F7, 2026-09-22)
- **One module per feature** (`src/Sage.Engine/Gameplay/GameplayModules.cs`, R15). Until 2026-09-23 this was a single `GameplayModule` holding nine record types, three cvars, seven console commands, six input actions and nine systems; adding anything meant editing four places, none of them near the code they were about. Each feature now owns its own:

  | Module | Owns | Depends on |
  |---|---|---|
  | `AttributesModule` | `attribute`/`tag`/`effect` records, `GameplayRegistries`, `god`, the `attributes` and `effects` prefab parts, `EffectSystem` | — |
  | `CharacterModule` | `movement_profile`, Move/Jump/Run/Crouch, the `character` part, `PlayerControlSystem`, `CharacterMovementSystem`, `FirstPersonCameraSystem`, the `PawnIntent` phase contract | `PhysicsModule` |
  | `AnimationModule` | the `sprite` part, `SpriteAnimationSystem` | — |
  | `CombatModule` | `damage_type`/`attack`, `combat_debug`, `hurt`, Attack, the `melee` part, `MeleeCombatSystem` | Attributes, Character |
  | `ItemsModule` | `item`, `g_interact_range`, `give`/`inv`/`equip`/`unequip`/`drop`, Use, the `inventory` and `pickup` parts, `InteractionState`, `InteractionSystem` | Attributes, Combat |
  | `AIModule` | `ai_profile`/`ai_schedule`, `ai_debug`, `AITasks`, `AIThinkSystem`, `AIDebugSystem` | Character, Combat |

  These are **logical** modules inside `Sage.Engine`, not assemblies (01 §3.1). `engine.Modules.AddGameplay()` adds them all, because "gameplay" is the unit a game wants; the host adds them one at a time so `game.json` can disable a single feature. Dependencies set `OnWorldCreated` order — combat needs attributes to exist before it can damage one — but **not** system order within a phase, which stays `before:`/`after:` and works across module boundaries. The `body` part moved to `PhysicsModule`, which owns where a shape sits.
- **Controller → intent → movement** works as designed: `PlayerControlSystem` (Commands) copies the tick's `PlayerCommand` into `PawnIntent` on `PlayerControlled` pawns, and `CharacterMovementSystem` reads only the intent (10 "The character controller"). An AI controller writing the same component gets the same movement for free.
- **First-person camera** (`FirstPersonCameraSystem`, FrameUpdate) puts `ActiveCamera` in the pawn's head from the interpolated pose and the command's view angles, at display rate. It sets `ActiveCamera.DrivenByRig`, and the editor's free camera steps aside unless `cam_free 1` clears `ActiveCamera.RigEnabled` (the flag was called `OwnedByRig` until 2026-09-22).
### As built (GameRules and the first creature, 2026-09-22)
- **`GameRules`** (`src/Sage.Engine/Gameplay/GameRules.cs`) is a world resource a game subclasses and installs in its module's `OnWorldCreated`. `Engine.CreateWorld` calls `OnWorldStarted` **after every module has seen the new world**, so the rules can populate a world that is fully set up; a world without a game's rules gets `DefaultGameRules`. `SpawnPlayer` and `OnLoaded` are there for combat (F20) and saves (09) to call; `OnEntityDied` is already called by `EffectSystem` when an entity's health runs out (§3.3). The default is installed by `Engine.CreateWorld` itself, after every module has had its turn — inside a gameplay module the "has the game installed its own?" check could never be false (review #47).
- **AI** (`AI.cs`, `AIThinkSystem.cs`) is the HL1 shape of §3.4:
  - **conditions** (`SeeEnemy`, `LostEnemy`, `EnemyInMeleeRange`, `NoEnemy`, `TaskFailed`, `ScheduleDone`);
  - **schedules as records** (`ai_schedule`: an ordered task list plus the conditions that interrupt it), parsed once per record;
  - **tasks registered by name** (`Wait`, `FaceTarget`, `MoveToTarget`, `MeleeAttack`), with an optional number after a colon (`"MoveToTarget:1.6"`). A game adds its own through the registry `AIModule` provides — declare `AIModule` as a dependency and `ctx.Get<AITaskRegistry>()` in `Start` (tested by `AGameCanReachTheAITaskRegistry`);
  - **`ai_profile` records** for sight range, melee range, think rate, attack cooldown and turn speed;
  - **perception** is a distance check, a **sight cone** (`ai_profile.sightAngleDegrees`, 200° by default, so a creature has a blind spot behind it) and a line-of-sight raycast from eye height, so terrain, walls and props hide the player;
  - **think rate** is a few times a second, staggered by entity id; the current task runs every tick because it writes `PawnIntent`;
  - **`ai_debug`** (with `r_debugdraw 1`) draws what each agent knows: its sight cone on the ground, a line to its target — grey when it has lost you, yellow when it can see you, red when it thinks it can reach you — and its melee range as a ring, which answers "why is it just standing there?" without a single log line.
- **Both controllers write intent in `Phase.Commands`**, `AIThinkSystem` after `PlayerControlSystem`, so movement (`PrePhysics`) acts on a creature's decision in the same tick it was made. `AIThinkSystem` originally sat in `Phase.AI`, four phases *after* the movement that reads `PawnIntent`, which cost every creature a tick of lag and made `FaceTarget` test a yaw the body had not adopted (review #48).
- **A creature keeps the direction the scene placed it facing**: `world.AddCharacter` seeds `PawnIntent.Yaw` from the entity's rotation (10 "The character controller", review #43).
- **The creature walks with the player's controller.** Its tasks write `PawnIntent`, exactly like `PlayerControlSystem`, so chasing uses the same capsule, slopes and step-ups. That is the payoff of the controller/pawn split.

### As built (combat, 2026-09-22)
- **Code:** `src/Sage.Engine/Gameplay/Combat.cs` — the `damage_type` and `attack` records, `DamageInfo` (the request), `Damaged` (the event), `Combat.ApplyDamage`, the `Melee` component and `MeleeCombatSystem`.
- **One pipeline, one place.** Every hit goes `Combat.ApplyDamage` → the damage type's resistance attribute → **an effect on health** → a `Damaged` event. Nothing anywhere subtracts health directly, so the `god` tag, stacking, damage over time and saves keep working through the single path F18 built. Damage is an effect *with a magnitude*: the record says `health -1`, the hit says how many (GAS's set-by-caller). A weapon or spell that also poisons passes its own effects to ride along, and they are blocked with the damage rather than separately.
- **Resistances are attributes**, read as a percentage of the damage stopped (`armor`, `fire_resist`), clamped so arithmetic never produces immunity — the attribute record's own `max` is the ceiling. A `damage_type` with no `resist` ignores armour entirely.
- **Both fighters press the same button.** `PlayerControlSystem` copies the Attack action into `PawnIntent`; the AI's `MeleeAttack` task sets the same bit. `MeleeCombatSystem` is the only thing that swings, so a creature and a player with the same `attack` record fight identically — and an AI can miss.
- **A swing is a physics query** (10 §4): a sphere swept along the attacker's aim from eye height, first solid thing it touches, then an arc check so a target at the edge of your vision doesn't count. Triggers are invisible to it (review #53). It hits whatever is solid, including the attacker's own kind: who a hit is *allowed* to hurt is a rules question (factions, F24), not a physics one.
- **An attack knows how it looks in your hands.** `attack.viewmodel` names the sprite sheet a first-person wielder sees (13 §3), which is why equipping a sword changes both what your swing does and what you watch it do, from one record.
- **Timing comes from the art when there is art.** A swing is windup → hit → recovery → cooldown, all from the `attack` record; if the attacker's clip has a `hit` frame event (12 §3) that lands the blow instead, so a creature's claws connect on the frame that shows them connecting.
- **Death names its killer.** `EffectSystem` holds its own reader on `Damaged` (04 §3.2) and finds who last hurt the victim, so `GameRules.OnEntityDied` gets a killer without every damage path carrying one; poison and drowning simply have none. Because it is a cursor and not a shared list, a hit dealt in a later phase is credited on the next pass instead of being missed.
- **`hurt <amount> [type]`** (cheat) runs the whole pipeline against the local player, which is how resistances and the death seam get tested by hand, and **`combat_debug`** (with `r_debugdraw 1`) draws every swing: the reach as an arrow, the sweep's end as a sphere, green when it found something and red when it did not, left on screen for half a second because a miss is the hard thing to debug.
### As built (items and interaction, 2026-09-23)
- **Code:** `src/Sage.Engine/Gameplay/Items.cs` — the `item` record, `Inventory`, `Equipment`, `Pickup`, the `Interactable` tag, the `Used` event, `InteractionState` and `InteractionSystem`, plus the `World` extensions (`AddInventory`, `Give`, `Take`, `Equip`, `Unequip`, `Drop`, `SpawnPickup`, `MakePickup`).
- **An item is a record, not an entity.** An inventory is a list of ids and counts, which is what makes stacking, saving and modding cheap (05 §3.5, 09). An item only becomes an entity while it is lying in the world — a `Pickup` with a billboard and a small static body — and stops being one the moment someone takes it.
- **Equipping is the seam combat left open.** A weapon's `attack` record goes into the wielder's `Melee`, so a player and a creature holding the same thing fight identically and `MeleeCombatSystem` never learns that swords exist (§3.2). `Melee.Natural` is what the wielder goes back to bare-handed. Anything else an item does — a shield's armour, a cursed ring — is an **effect applied while it is worn** and removed when it comes off, so items, spells and potions all change you through the one path F18 built (§3.3).
- **Use means look-at-and-press, or just be near it.** The ray from the eye wins if it finds something; otherwise the nearest usable thing within `g_interact_range` (2.5 m) and inside a 140° cone in front does. A ray alone would mean staring at your own boots to pick up a sword lying in the grass.
- **Weight, not slots.** `Inventory.Capacity` is kilograms (0 = unlimited) and `Give` refuses what won't fit rather than silently dropping it, so a caller moving items between containers can trust the answer before it destroys anything.
- **Cheats:** `give <item> [count]`, `inv`, `equip <item>`, `unequip [main|off]`, `drop <item> [count]`. Ids may be typed bare (`give practice_sword`), which `RecordStore.Resolve` looks up across namespaces — game code always names records in full.
- **Not yet:** containers and looting corpses, an inventory screen (13), item conditions and repair, enchantments, gold and trade, stacks that split on drop, and the entity-I/O side of interaction (a door that opens, 04).

### As built (abilities, 2026-09-23 — F21 v1)

- **Code:** `src/Sage.Engine/Gameplay/Abilities.cs` (the `ability` and `cue` records, the `Abilities`
  component, the `AbilityCast`/`CastRefused`/`CueTriggered` events) and `AbilitySystem.cs`. Tests in
  `tests/Sage.Tests/World/AbilityTests.cs`; the Sandbox's fireball is in its `scene.json`.
- **Almost none of it is new machinery.** An ability is a cost, a cooldown, a way of choosing targets
  and a list of effects. Everything it does to anybody is an effect (§3.3) or damage through the
  combat pipeline (§3.2) — so the `god` tag stops a fireball for the same reason it stops a sword,
  `fire_resist` works without the spell knowing it exists, and a kill by spell names its killer. The
  two genuinely new things are the gates.
- **The gates, in the order a player would think of them:** do I know it, is it ready, can I afford
  it, am I allowed. Each refusal is a `CastRefused` event carrying *which*, so a HUD can say "not
  enough mana" without the cast system knowing what a HUD is.
- **Cost is spent as an effect.** An `attribute` record names its own `spendEffect`, and a cast
  applies it at the cost's magnitude. So mana leaves you the way health does, and "free casting" is
  an effect on top rather than a special case in the caster.
- **A cooldown is an effect that grants a tag**, exactly as this doc asked. There is no second clock:
  it shows up in a save, a dispel makes the spell ready again, and the ability only has to name the
  effect — the system reads *its* granted tags to decide whether you are still on it.
- **Targeting:** `Self`, `Touch` (the first thing along your aim), `Area` (around you), `TouchArea`
  (a burst where a Touch would have landed — a fireball). `Range` is how far it reaches, `Radius` is
  how wide it bursts, and `Width` is how fat the thing that travels is. Those last two started as one
  field and it was a bug: sweeping with the *burst* radius makes a fireball start already overlapping
  its own caster, and an overlapping sweep reports nothing (10 §4, review #55), so a three-metre
  burst reached exactly nothing.
- **Only things that can hold an effect are targets.** A blast lands on the world and most of the
  world is scenery; a fireball bursting against a tree is a normal Tuesday, not a mis-configured
  entity.
- **Cues** (`CueTriggered`) are raised and nothing listens yet: audio (11) and particles are later
  phases. The event is already on the bus, so the day something listens the spell needs no change.
- **Console:** `cast <ability>`, `learn <ability>`, `spells`, and `cast_debug 1` to draw where a cast
  reached and what it caught.
- **Not done here:** AI that casts, and projectiles that arc, bounce or stick (they fly straight and
  stop at the first thing).

### As built (the spellmaker, F21, 2026-09-23)
Composing spells cost the engine almost nothing, which is the whole argument for F18–F21: a spell is
already "a cost, targeting and a list of effects", so making one is *filling in an `ability` record*,
and the cast system cannot tell a composed spell from one in a content file.

- **Code:** `src/Sage.Engine/Gameplay/Spellmaker.cs` — `SpellDraft` (what the player chose),
  `Spellbook` (a `[SavedResource]` world resource: the drafts, nothing else), and `Spellmaker`
  (`Price`, `Compose`, `Restore`, `Forget`).
- **The draft is the data; the record is derived from it.** The save holds the choices (09
  "As built (saved resources)") and loading composes the records again, so a rebalanced effect changes
  a player's old spell rather than being frozen into it — and no `AbilityRecord` is ever serialized.
  Proved by a test that triples an effect's cost between two runs of the game.
- **Records made at run time** live in their own layer of the `RecordStore` (05 "As built"), under the
  `custom` namespace of their own, and survive a content hot reload — nothing on disk could rebuild a
  player's spell.
- **Effects are priced by the effect record** (`EffectRecord.Cost`), so a mod prices what it adds in
  the file that adds it. **Cost 0 means "not for sale"**: `spend_mana` and cooldowns are effects the
  game applies itself, and without that rule "a spell that drains your own mana" is composable.
- **Price** is one function, separate from composing, because a spellmaker screen has to show a number
  before the player commits: effects plus damage, scaled by magnitude, multiplied by what the delivery
  costs, and only counting the range and radius the delivery actually uses. Minimum one.
- **Refusals carry words a screen can show** (no name, no effects, an effect not for sale, a name
  already used, an id already taken), because composing is the one place a player's idea meets the rules.
- **No cooldown yet:** a composed spell is gated by cost, which is the bargain Daggerfall's spellmaker
  struck. When a cooldown becomes a choice it is a field on the draft and a term in the price.
- **v1 assumes mana:** a composed spell draws on `sage:mana`, the engine's own pool. A game whose magic
  runs on something else needs that to become a choice (a field on the draft, or a rule on `GameRules`).
- **Console:** `spell_effects` (what can be built with, and what each costs), `spell_make <name>
  <effect|key=value>...`, `spell_list`, `spell_forget <name>`. The spellmaker *screen* waits for UI
  (13); the rules are what needed building, and a screen will call exactly these.
- **Forgetting** removes the draft and the record, and an entity that still knows the id is refused
  with `NotKnown` — the same thing that happens to any ability whose record went away with a mod.

- **Not yet:** blocking and parries, directional melee and reversals (Lugaru/Warband, later), knockback and hit reactions, cleaving several targets with one swing, ranged and projectile attacks (F21), friendly-fire rules (F24), and damage over time routed through resistances (a periodic effect still changes health directly).
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
- **Built (F21, 2026-09-23):** abilities and cues, projectiles, and the spellmaker — see "As built (abilities)" and "As built (the spellmaker)" below. **Not yet:** skill progression.

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
[Record("ability")] public sealed class AbilityRecord { public RecordId CostAttribute; public float Cost; public RecordId Cooldown; public AbilityTargeting Targeting; public float Range, Radius, Width, CastTime, Damage, Magnitude; public RecordId DamageType; public List<RecordId> Effects, RequireTags, BlockTags, Cues; public string Animation; }

public interface IAITask { AITaskStatus Start(ref AITaskContext c) => AITaskStatus.Running; AITaskStatus Run(ref AITaskContext c); }   // registered by name

// The spellmaker (§3.3). A draft is what the player chose and the only thing saved; the record is
// composed from it, here and again on load.
public sealed class SpellDraft { public string Name; public AbilityTargeting Targeting; public List<RecordId> Effects; public float Magnitude, Range, Radius, Damage, ProjectileSpeed; public RecordId DamageType, Projectile; }
[SavedResource("spellbook")] public sealed class Spellbook : ISavedResource { public List<SpellDraft> Drafts { get; set; } }

public static class Spellmaker
{
    public static float Price(RecordStore records, SpellDraft draft);    // for a screen, before committing
    public static SpellResult Compose(World world, SpellDraft draft);    // rules, price, record, book
    public static int Restore(World world);                             // after a load: drafts → records
    public static bool Forget(World world, string name);
}
```

**Still design-only:** combat, inventory and interaction aren't sketched here yet (§3.2). Everything else above is shipped: `Pawn` really is that empty (no `Controller` back-reference — a controller only ever writes `PawnIntent`, nothing points the other way); `EntityRef` is `Entity` in code everywhere (glossary, 03); `AIState` carries `Profile`, `TaskTime` and `Cooldown` too, not just the fields shown before.

## 11. v1 scope vs later
- **v1:**
  - `GameRules`;
  - Controller/Pawn/`PawnIntent`;
  - Character + first-person rig;
  - attributes/effects/tags/abilities (health, mana, damage, burning, fireball);
  - the spellmaker (composing effects into an `ability` record, saved as the drafts behind it);
  - minimal combat, inventory and interaction;
  - AI schedules for one melee creature;
  - log categories `Gameplay`, `AI`;
  - `ai_debug` overlay (schedule, task and conditions above each agent);
  - `give <item>`, `god`, `notarget` cheats.
- **Later:** the rest of the module table.

## 14. Build steps
1. ~~Controller/Pawn/`PawnIntent` + Character + `GameRules`~~ **Done 2026-09-22** (TODO F7, with 10).
2. ~~Attributes/effects/tags + `EffectSystem`~~ **Done 2026-09-22** (TODO F18).
3. ~~Abilities + cues + fireball~~ **Done 2026-09-23** (F21 v1, "As built (abilities)"), and with them ~~projectiles~~ and ~~the spellmaker~~ the same day. AI that casts is left.
4. ~~Combat, inventory and interaction~~ **Done 2026-09-22/23** (TODO F20, F19).
5. ~~AI schedules + perception + one creature~~ **Done 2026-09-22** (TODO F22). Its swings go through the same `MeleeCombatSystem` a player's do (F20), so a creature can miss, and what its claws do is an `attack` record.
