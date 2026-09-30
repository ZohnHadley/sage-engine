# 16 — Gameplay Framework (short)

> **From the slice retrospective (2026-09-23), findings 2 and 5 — now R17.** Building magic taught the
> same lesson twice: **a rule a system applies is usually a rule something else needs to ask.**
> `AbilityPayload` (what a spell does, at a point) and `AbilityRules` (whether a cast is allowed) were
> both extracted within a day of their second caller appearing. The same shape is still locked inside
> melee ("may I swing now?"), interaction ("can I pick this up, and why not?") and items ("can I equip
> this?"), and each one is what a HUD or an AI will need. **First slice done 2026-09-23:**
> `Items.CanEquip(world, entity, item, out reason)` came out of `Equip`, and `AbilityRules.Explain`
> turns a `CastRefusal` into words, both feeding the panel model (13 "As built (the panel model)").
> Melee and interaction are still to do. Separately, `ChooseSchedule` is now a
> five-branch if-chain and is one branch from wanting utility scoring (§3.4's "left"). See
> [`../history/vertical-slice-2026-09-23.md`](../history/vertical-slice-2026-09-23.md).

## 1. Purpose and scope
The optional, genre-generic gameplay layer (`Sage.Framework`, plus `Sage.Framework.Client` for presentation parts such as camera rigs, 01 §3.1). Games use, extend or ignore each module. v1 covers what the Daggerfall-like vertical slice needs; each module is expanded when it's built (roadmap Phase 5).

## 2. Research basis
- UE's Gameplay Framework (GameMode/GameState/PlayerState/PlayerController/Pawn) is shaped by network authority. We take the **lean core**: rules object + Controller/Pawn (survey §2.2).
- GAS (attributes, gameplay effects, tags, cues) is a strong fit for RPG stats and a Daggerfall-style spellmaker (survey §2.2).
- HL1 schedules/tasks/conditions AI (survey §1.2).
- Event-driven, mostly idle behaviour (UnrealScript lesson, survey §1.4).

## 3. Key decisions

### 3.1 Core
- **`GameRules`** (world resource, supplied by the game module's `CreateRules`): spawning the player, death/respawn/game-over, time of day, loading hooks (`OnLoaded` after a save load, 09). Rules logic lives here, not scattered across systems (readiness rule 5). In future multiplayer it becomes server-only, like UE's GameMode. UE's `GameState`/`PlayerState` split is adopted then.
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
| **Interaction** | yes | The `Use` action → raycast → `Interactable` → fires the I/O output `OnUse` (04) and a `Used` event |
| **AI** | yes (one melee creature) | See 3.4 |
| **Navigation** | yes (local grid) | A grid built round the agent when the straight line is blocked, A* and string-pulling, plus target memory (F23, "As built (navigation)"). A navmesh for brush-built interiors and a coarse graph for crossing sectors are still later |
| **Narrative** (dialogue, quests, journal) | yes | `dialogue` records with conditions and outcomes, `quest` records with stages and objectives, a saved journal and two screens (F24) |
| **Factions** | yes | `faction` records with relations and a per-player standing; one rule answering "is this my enemy" for the AI, for blasts and for the death seam (F24, "As built (factions and reputation)") |
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
  **A cue names a moment as well as a reaction**, so the ids come in more than one list: an ability has `castCues` (where the spell leaves the caster) and `cues` (where it does its work), and an `attack` has `swingCue` (raised on the windup, hit or miss) while the *hit* takes the damage type's sound. One list raised at every moment puts a burst in the caster's hand — see 11 §11.
- **Spellmaker:** composing effect records into a *new* `ability` record at runtime. It's saved as data in the save game (09 saved resource), exactly as Daggerfall's custom spells were. **Built 2026-09-23** — see "As built (the spellmaker)".

### 3.4 AI (HL1-style first)
- **`schedule` records:** an ordered task list + an **interrupt condition mask** (`new_enemy`, `heavy_damage`, `lost_target`, `heard_noise`…).
- **Tasks are C# classes registered by name** (`MoveTo`, `FaceTarget`, `MeleeAttack`, `Wait`, `PlayAnim`, `FindCover`…) with `Start`/`Run` returning Running/Succeeded/Failed.
- **`AIState` component** (saved, 09): current schedule `RecordId`, task index, task-local data, conditions bitmask, target `EntityRef`.
- **Think rate:** `AIThinkSystem` (AI phase) thinks at 5–10 Hz, **staggered** across agents. Perception (sight cone raycast, hearing from events) updates conditions. `GetSchedule` picks the next schedule from conditions + state.
- **Later:** utility scoring for schedule choice, behaviour trees if schedules get unwieldy, squads/formations (Warband), daily routines (Daggerfall townsfolk).

### As built (F7, 2026-09-22)
- **One module per feature** (`src/Sage.Gameplay/GameplayModules.cs`, R15). Until 2026-09-23 this was a single `GameplayModule` holding nine record types, three cvars, seven console commands, six input actions and nine systems; adding anything meant editing four places, none of them near the code they were about. Each feature now owns its own:

  | Module | Owns | Depends on |
  |---|---|---|
  | `AttributesModule` | `attribute`/`tag`/`effect`/`gameplay_conventions` records, `GameplayRegistries`, `god`, the `attributes` and `effects` prefab parts, `EffectSystem`, `DeathRulesSystem` | — |
  | `CharacterModule` | `movement_profile`, Move/Jump/Run/Crouch/ToggleView, the `character` part, `PlayerControlSystem`, `CharacterMovementSystem`, the player camera and its rigs (`PlayerCameraSystem`, `FirstPersonRigSystem`, `ThirdPersonRigSystem`, `ToggleViewSystem`; #78, #79), the `PawnIntent` phase contract | `PhysicsModule` |
  | `AnimationModule` | the `sprite` part, `SpriteAnimationSystem` | — |
  | `LightsModule` | the `light` part (06 §3.9) | — |
  | `CombatModule` | `damage_type`/`attack`, `combat_debug`, `hurt`, Attack, the `melee` part, `MeleeCombatSystem` | Attributes, Character |
  | `ItemsModule` | `item`, `EquipSlots` (none of its own, #27), `g_interact_range`, `give`/`equip`/`unequip`/`drop`, Use, the `inventory` and `pickup` parts, `InteractionState`, `InteractionSystem` | Attributes, Combat |
  | `AbilitiesModule` | `ability`/`cue` records, `cast_debug`, `cast`/`learn`, the `abilities` prefab part, `AbilitySystem`, `ProjectileSystem` (the spellbook, the Cast button and `spells`/`ready` are the RPG kit's since #27) | Attributes, Character |
  | `AIModule` | `ai_profile`/`ai_schedule`, `ai_debug`, `AITasks`, `AIThinkSystem`, `AIDebugSystem` | Character, Combat |
  | `FactionsModule` | `faction` records, the `Reputation` saved resource, `rep`/`rep_set`, the `faction` part, `FactionDeathSystem` | Attributes |
  | `QuestsModule` | `quest` records, the `Journal` saved resource, `quests`/`quest_start`/`quest_stage`, `QuestDeathSystem` (issue #26) | — |
  | `DialogueModule` | `dialogue` records, the `dialogue` part, the world's `Conversation` (issue #26) | — |
  | `EntityIOModule` | entity inputs, `io_trace`/`io_maxdispatch`, `ent_fire`/`io_list`, `TriggerOutputSystem`, `EntityIOSystem` (04 §3.4, F17) | — |
  | `MoverModule` | the `mover` part, `MoverSystem` (F17) | Physics, EntityIO |

  These are **logical** modules inside `Sage.Gameplay`, not assemblies of their own (01 §3.1). `engine.Modules.AddGameplay()` adds them all, because "gameplay" is the unit a game wants; the host adds them one at a time so `game.json` can disable a single feature. Dependencies set `OnWorldCreated` order — combat needs attributes to exist before it can damage one — but **not** system order within a phase, which stays `before:`/`after:` and works across module boundaries. The `body` part moved to `PhysicsModule`, which owns where a shape sits.
- **Controller → intent → movement** works as designed: `PlayerControlSystem` (Commands) copies the tick's `PlayerCommand` into `PawnIntent` on `PlayerControlled` pawns, and `CharacterMovementSystem` reads only the intent (10 "The character controller"). An AI controller writing the same component gets the same movement for free.
- **First-person camera** (since #78 a camera entity per player pawn, spawned by `PlayerCameraSystem` from `sage:player_camera`, with a `FirstPersonRig`; 06 "As built (camera rigs)") sits in the pawn's head from the interpolated pose and the command's view angles, at display rate. The camera director mirrors it into `ActiveCamera`. The editor's free camera is a camera entity too since #81 (a `DebugCamera`, 06 "As built (the editor's cameras)"): below the player's camera, so it stands aside, until `cam_free 1` puts it above every camera; `ActiveCamera.RigEnabled` and `DrivenByRig`, the old protocol for this (`OwnedByRig` until 2026-09-22), are obsolete and nothing reads them. Since #79 the same camera has a third-person rig, switched with V.
### As built (GameRules and the first creature, 2026-09-22)
- **`GameRules`** (`src/Sage.Simulation/App/GameRules.cs`) is a world resource a game subclasses and installs in its module's `OnWorldCreated`. `Engine.CreateWorld` calls `OnWorldStarted` **after every module has seen the new world**, so the rules can populate a world that is fully set up; a world without a game's rules gets `DefaultGameRules`. `SpawnPlayer` and `OnLoaded` are there for combat (F20) and saves (09) to call; `OnEntityDied` is already called by `EffectSystem` when an entity's health runs out (§3.3). The default is installed by `Engine.CreateWorld` itself, after every module has had its turn — inside a gameplay module the "has the game installed its own?" check could never be false (review #47). *Since 2026-09-27 (issue #13)* a game returns its rules from **`IGameModule.CreateRules(World)`**, which `Engine.CreateWorld` calls after every module's `OnWorldCreated` and before `OnWorldStarted`, and logs which rules a world got; rules a module installs in `OnWorldCreated` still work, and `CreateRules` wins over them with a warning (test: AGamesRulesComeFromCreateRulesAndAreStarted).
- **AI** (`AI.cs`, `AIThinkSystem.cs`) is the HL1 shape of §3.4:
  - **conditions** (`SeeEnemy`, `LostEnemy`, `EnemyInMeleeRange`, `NoEnemy`, `TaskFailed`, `ScheduleDone`, and from F21 `CanMelee`, `CanCastAtEnemy`, `SpellComingBack`, `Casting`);
  - **schedules as records** (`ai_schedule`: an ordered task list plus the conditions that interrupt it), parsed once per record;
  - **tasks registered by name** (`Wait`, `FaceTarget`, `MoveToTarget`, `MeleeAttack`, `CastSpell`), each written as an object with its one argument named — `{ "task": "MoveToTarget", "distance": 1.6 }`, `{ "task": "Wait", "seconds": 1.5 }`, `{ "task": "MeleeAttack", "giveUpAfter": 1.5 }`, `{ "task": "CastSpell", "giveUpAfter": 2 }` — or as a bare name when it takes none (`"FaceTarget"`; names are case-insensitive). The old `"Wait:1.5"` strings are a load error that names `file:line:column` and gives the object to write (issue #22; test: AITasks_TheOldColonSyntaxIsAnErrorThatSaysWhatToWrite). A task says which argument it takes (`IAITask.Argument`), and a schedule that names another is an error when it runs, rather than a number read in the wrong unit (test: ATaskGivenAnArgumentItDoesNotTakeIsReportedAndStops). Both that and an unknown task name are also reported when the first world is created — once every task a game adds is in — at the step's `file:line:column`, and an unknown interrupt name is a load error at its line (issue #22; test: AISchedules_NameTheirLineForAnUnknownInterruptOrTask). A game adds its own through the registry `AIModule` provides — declare `AIModule` as a dependency and `ctx.Get<AITaskRegistry>()` in `Start` (tested by `AGameCanReachTheAITaskRegistry`);
  - **`ai_profile` records** for sight range, melee range, think rate, attack cooldown and turn speed;
  - **perception** is a distance check, a **sight cone** (`ai_profile.sightAngleDegrees`, 200° by default, so a creature has a blind spot behind it) and a line-of-sight raycast from eye height, so terrain, walls and props hide the player;
  - **think rate** is a few times a second, staggered by entity id; the current task runs every tick because it writes `PawnIntent`;
  - **`ai_debug`** (with `r_debugdraw 1`) draws what each agent knows: its sight cone on the ground, a line to its target — grey when it has lost you, yellow when it can see you, red when it thinks it can reach you — and its melee range as a ring — plus, for a caster, the reach of the spell it chose — which answers "why is it just standing there?" without a single log line.
- **Both controllers write intent in `Phase.Commands`**, `AIThinkSystem` after `PlayerControlSystem`, so movement (`PrePhysics`) acts on a creature's decision in the same tick it was made. `AIThinkSystem` originally sat in `Phase.AI`, four phases *after* the movement that reads `PawnIntent`, which cost every creature a tick of lag and made `FaceTarget` test a yaw the body had not adopted (review #48).
- **A creature keeps the direction the scene placed it facing**: `world.AddCharacter` seeds `PawnIntent.Yaw` from the entity's rotation (10 "The character controller", review #43).
- **The creature walks with the player's controller.** Its tasks write `PawnIntent`, exactly like `PlayerControlSystem`, so chasing uses the same capsule, slopes and step-ups. That is the payoff of the controller/pawn split.

### As built (combat, 2026-09-22)
- **Code:** `src/Sage.Gameplay/Combat/Combat.cs` — the `damage_type` and `attack` records, `DamageInfo` (the request), `Damaged` (the event), `Combat.ApplyDamage`, the `Melee` component and `MeleeCombatSystem`.
- **One pipeline, one place.** Every hit goes `Combat.ApplyDamage` → the damage type's resistance attribute → **an effect on health** → a `Damaged` event. Nothing anywhere subtracts health directly, so the `god` tag, stacking, damage over time and saves keep working through the single path F18 built. Damage is an effect *with a magnitude*: the record says `health -1`, the hit says how many (GAS's set-by-caller). A weapon or spell that also poisons passes its own effects to ride along, and they are blocked with the damage rather than separately.
- **Resistances are attributes**, read as a percentage of the damage stopped (`armor`, `fire_resist`), clamped so arithmetic never produces immunity — the attribute record's own `max` is the ceiling. A `damage_type` with no `resist` ignores armour entirely.
- **Both fighters press the same button.** `PlayerControlSystem` copies the Attack action into `PawnIntent`; the AI's `MeleeAttack` task sets the same bit. `MeleeCombatSystem` is the only thing that swings, so a creature and a player with the same `attack` record fight identically — and an AI can miss.
- **A swing is a physics query** (10 §4): a sphere swept along the attacker's aim from eye height, first solid thing it touches, then an arc check so a target at the edge of your vision doesn't count. Triggers are invisible to it (review #53). It hits whatever is solid, including the attacker's own kind: who a hit is *allowed* to hurt is a rules question (factions, F24), not a physics one.
- **An attack knows how it looks in your hands.** `attack.viewmodel` names the sprite sheet a first-person wielder sees (13 §3), which is why equipping a sword changes both what your swing does and what you watch it do, from one record.
- **Timing comes from the art when there is art.** A swing is windup → hit → recovery → cooldown, all from the `attack` record; if the attacker's clip has a `hit` frame event (12 §3) that lands the blow instead, so a creature's claws connect on the frame that shows them connecting. The clip names — the swing's clip when its attack names none, the `hit` event, the `idle` it returns to — are `gameplay_conventions` `animations` since #27 (test: AGameNamesTheSpriteClipsCombatPlays).
- **Death names its killer.** `EffectSystem` holds its own reader on `Damaged` (04 §3.2) and finds who last hurt the victim, so `GameRules.OnEntityDied` gets a killer without every damage path carrying one; poison and drowning simply have none. Because it is a cursor and not a shared list, a hit dealt in a later phase is credited on the next pass instead of being missed.
- **`hurt <amount> [type]`** (cheat) runs the whole pipeline against the local player, which is how resistances and the death seam get tested by hand, and **`combat_debug`** (with `r_debugdraw 1`) draws every swing: the reach as an arrow, the sweep's end as a sphere, green when it found something and red when it did not, left on screen for half a second because a miss is the hard thing to debug.
### As built (items and interaction, 2026-09-23)
- **Code:** `src/Sage.Gameplay/Items/Items.cs` — the `item` record, `Inventory`, `Equipment`, `Pickup`, the `Interactable` tag, the `Used` event, `InteractionState` and `InteractionSystem`, plus the `World` extensions (`AddInventory`, `Give`, `Take`, `Equip`, `Unequip`, `Drop`, `SpawnPickup`, `MakePickup`).
- **An item is a record, not an entity.** An inventory is a list of ids and counts, which is what makes stacking, saving and modding cheap (05 §3.5, 09). An item only becomes an entity while it is lying in the world — a `Pickup` with a billboard and a small static body — and stops being one the moment someone takes it.
- **Equipping is the seam combat left open.** A weapon's `attack` record goes into the wielder's `Melee`, so a player and a creature holding the same thing fight identically and `MeleeCombatSystem` never learns that swords exist (§3.2). `Melee.Natural` is what the wielder goes back to bare-handed. Anything else an item does — a shield's armour, a cursed ring — is an **effect applied while it is worn** and removed when it comes off, so items, spells and potions all change you through the one path F18 built (§3.3).
- **Use means look-at-and-press, or just be near it.** The ray from the eye wins if it finds something; otherwise the nearest usable thing within `g_interact_range` (2.5 m) and inside a 140° cone in front does. A ray alone would mean staring at your own boots to pick up a sword lying in the grass.
- **Weight, not slots.** `Inventory.Capacity` is kilograms (0 = unlimited) and `Give` refuses what won't fit rather than silently dropping it, so a caller moving items between containers can trust the answer before it destroys anything.
- **Slots by name (issue #27).** `ItemRecord.Slot` is a slot's name and `Equipment` a list of `{ Slot, Item }` (`sage:equipment` version 2; its upgrader turns the old `MainHand`/`OffHand` fields into slots of those names). Which slots exist is `EquipSlots`, which `ItemsModule` provides and seals at `Start`: the base registers none, the RPG kit its two hands, a game whatever else it wears, and an item naming a slot nobody registered is a load error (test: TheKitHasTwoHandsAndTheBaseNone).
- **Cheats:** `give <item> [count]`, `equip <item>`, `unequip [slot]`, `drop <item> [count]`, and the RPG kit's `inv`. Ids may be typed bare (`give practice_sword`), which `RecordStore.Resolve` looks up across namespaces — game code always names records in full.
- **Not yet:** containers and looting corpses, an inventory screen (13), item conditions and repair, enchantments, gold and trade, stacks that split on drop, and the entity-I/O side of interaction (a door that opens, 04).

### As built (abilities, 2026-09-23 — F21 v1)

- **Code:** `src/Sage.Gameplay/Abilities/Abilities.cs` (the `ability` and `cue` records, the `Abilities`
  component, the `AbilityCast`/`CastRefused`/`CueTriggered` events) and `AbilitySystem.cs`. Tests in
  `tests/Sage.Tests/Gameplay/AbilityTests.cs`; the Sandbox's fireball is in its `scene.json`.
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
  its own caster, and an overlapping sweep reported nothing (10 §4, review #55), so a three-metre
  burst reached exactly nothing. Since #30 a sweep leaves its caster out (`ignore`) and hits what
  it starts inside at distance 0 (10 "As built (the facade)").
- **Only things that can hold an effect are targets.** A blast lands on the world and most of the
  world is scenery; a fireball bursting against a tree is a normal Tuesday, not a mis-configured
  entity.
- **Cues** (`CueTriggered`) are raised and nothing listens yet: audio (11) and particles are later
  phases. The event is already on the bus, so the day something listens the spell needs no change.
- **Console:** `cast <ability>`, `learn <ability>`, `spells`, and `cast_debug 1` to draw where a cast
  reached and what it caught.
- **Not done here:** projectiles that arc, bounce or stick (they fly straight and stop at the first
  thing). AI casting arrived the same day — see "As built (AI casting)" in §3.4.

### As built (AI casting, F21/F22, 2026-09-23)
A creature's spell is the player's spell. The agent asks with `world.Cast`, the same call the console
and a pressed button make, and `AbilitySystem` applies the rules — so a creature pays mana, waits out
a cooldown, can be blocked by a tag and can miss, all without the AI knowing any of it.

- **Code:** `CastSpellTask` and the `cast_spell` / `hold_ground` schedules, `ChooseSpell` in
  `AIThinkSystem`, and `AbilityRules` (`src/Sage.Gameplay/Abilities/AbilityRules.cs`).
- **One set of rules, asked twice.** The gates came out of `AbilitySystem` into `AbilityRules` so that
  *deciding to cast* and *being allowed to cast* cannot answer differently. Written twice they drift,
  and the visible symptom is a creature that walks into range and winds up a spell it cannot pay for,
  every think, for ever. A HUD greying out a spell should ask the same question.
- **The decision is the whole question**, not "does it know a spell": known, off cooldown, affordable,
  allowed, and the enemy inside that spell's own `Range` (times 0.9, because at the very edge a
  projectile times out as it arrives). It picks the **dearest** castable spell — a creature leads with
  its best and falls back as its mana goes. Utility scoring goes here when there is more to weigh.
- **Why it cannot cast decides what it does instead.** On cooldown is worth waiting for, so a creature
  that cannot swing holds its ground (`hold_ground`) and faces its target; out of mana or out of range
  is not, so it closes in. Before that distinction a caster charged between casts and ended up in
  melee reach with nothing to do there.
- **`CanMelee`**, because a creature with no `Melee` used to walk into reach and run a melee schedule
  that could only fail, once a tick.
- **It does not re-decide mid-cast.** A think during a wind-up sees every spell refused as
  `AlreadyCasting`; without a condition for that the agent concluded it had no magic and wandered off
  in the middle of its own spell.
- **It aims with pitch as well as yaw** (`SageMath.PitchTo`): a spell leaves the eye and a body is
  lower, so without it a creature on a ledge fires over the player's head. It keeps tracking through
  the wind-up, because the aim is read when the spell goes off.
- **Left:** keeping distance (a caster stands where it is rather than backing away), casting at
  anything other than the player, self-buffs and healing — a creature that healed itself instead of
  fighting would be worse than one that does not try — and friendly fire rules (F24: a firebug's bolt
  will happily burn the watcher standing in front of it).

### As built (the spellmaker, F21, 2026-09-23)
Composing spells cost the engine almost nothing, which is the whole argument for F18–F21: a spell is
already "a cost, targeting and a list of effects", so making one is *filling in an `ability` record*,
and the cast system cannot tell a composed spell from one in a content file.

- **Code:** `src/Sage.Kits.Rpg/Spellmaker.cs` (the RPG kit's since #27) — `SpellDraft` (what the player chose),
  `Spellbook` (a `[SavedResource]` world resource: the drafts, nothing else), and `Spellmaker`
  (`Price`, `Compose`, `Restore`, `Forget`).
- **The draft is the data; the record is derived from it.** The save holds the choices (09
  "As built (saved resources)") and loading composes the records again, so a rebalanced effect changes
  a player's old spell rather than being frozen into it — and no `AbilityRecord` is ever serialized.
  Proved by a test that triples an effect's cost between two runs of the game.
- **Records made at run time** live in their own layer of the `RecordStore` (05 "As built"), under a
  namespace of their own — `custom`, or the game's `rpg_conventions` `spellNamespace` since #27 — and
  survive a content hot reload — nothing on disk could rebuild a player's spell.
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
- **What a spell costs is the game's word:** a composed spell draws on the `gameplay_conventions`
  `costAttribute` (mana in the engine's conventions, #26), and its price is worded with that
  attribute's name (#27).
- **Console:** `spell_effects` (what can be built with, and what each costs), `spell_make <name>
  <effect|key=value>...`, `spell_list`, `spell_forget <name>`.
- **And a screen** (`SpellmakerScreen`, 2026-09-23 with F38): name it, cycle its delivery and power,
  choose effects, and press Enter on a **"make it" row** that is greyed with the reason when the draft
  is not yet a spell — the reason coming from `Spellmaker.CanCompose`, which `Compose` itself applies
  (R17), so the button and the attempt cannot disagree. It lives in the RPG kit rather than in a game,
  because what a spellmaker *is* belongs to this feature; binding it to a key is the game's call.
- **Forgetting** removes the draft and the record, and an entity that still knows the id is refused
  with `NotKnown` — the same thing that happens to any ability whose record went away with a mod.

- **Not yet:** blocking and parries, directional melee and reversals (Lugaru/Warband, later), knockback and hit reactions, cleaving several targets with one swing, ranged and projectile attacks (F21), friendly-fire rules (F24), and damage over time routed through resistances (a periodic effect still changes health directly).
- **Steering** is raycast avoidance (probes ahead and to both sides just above step height, and turns toward the free side); it still runs, and is what keeps bodies apart. Pathfinding arrived with F23 — see "As built (navigation)".
- **Deviations and gaps:**
  - schedule *selection* is code (`ChooseSchedule`), as in HL1's `GetSchedule`; utility scoring or a behaviour tree can replace it without touching the tasks;
  - attacks go through the same melee pipeline as the player's: `MeleeAttackTask` presses Attack on the creature's `PawnIntent`, and the swing, the hit and the damage are `Combat`'s, which raises `Damaged` on the world's event bus (04, R13) — that is how cues and reactions reach the rest of the game;
  - perception is sight only (no hearing) and there are no squads; who counts as an enemy comes from factions (F24, "As built (factions and reputation)");
  - the AI phase runs after movement in the tick, so intent written this tick moves the creature on the next one.

### As built (quests and the journal, 2026-09-24 — F24)
Something to do, and something that notices you did it.

- **Code:** `src/Sage.Gameplay/Narrative/Quests.cs` (`quest` records, the saved `Journal`, the `Quests`
  rules and `QuestChanged`) and the RPG kit's `src/Sage.Kits.Rpg/JournalScreen.cs` (#27). Tests:
  `tests/Sage.Tests/Gameplay/QuestTests.cs`.
- **A quest watches; it never moves anybody.** A stage is a line of text and a list of things that must
  be true. The things it watches for are things the game already does — somebody died, something is in
  your bag — so a quest adds no machinery to the rest of the engine, only a place to write down what
  counts. It runs no code and owns no entities.
- **Two kinds of objective in v1:** `Kill` (a faction's members, or a particular prefab's) and `Have`
  (an item, in the bag, now). The first is *counted* from the death seam, because a kill is a moment;
  the second is *asked*, because carrying something is a state and handing it over un-does it.
- **A stage advances by its objectives being met, and by nothing else** — which is what keeps "where am
  I in this quest" answerable from the journal alone. Anything else that should move it says so:
  dialogue with `stage`, `startQuest` or `finishQuest`, or the console.
- **"Go and report" is not a finished quest.** A stage with no objectives waits to be *told* it is over,
  which is what a conversation's `finishQuest` does. A stage marked `done` ends the quest the moment it
  is reached, which is the other shape. The first version had only the second, and the errand was over
  before the hermit heard about it.
- **The journal is a list with the counting shown** — "kill 2 beasts of the wood  0/2" — because that is
  the question a player actually has. Finished quests stay in it, greyed: a journal you cannot look back
  through is a to-do list.
- **Console:** `quests`, `quest_start`, `quest_stage`.
- **In the Sandbox** the hermit asks you to thin the watchers, the journal counts them, and telling him
  it is done pays in standing — the whole loop through a conversation.
- **Not yet:** objectives for reaching a place or talking to somebody (a trigger volume and a `Spoke`
  reader would do both), timed quests, failure, quest items that cannot be dropped, and a quest log that
  remembers what you were *told* rather than what the stage says now.

### As built (dialogue, 2026-09-24 — F24)
Talking to somebody, as data.

- **Code:** `src/Sage.Gameplay/Narrative/Dialogue.cs` (`dialogue` records, the `Dialogue` component, the
  `Conversation` resource, `DialogueRules`) and the RPG kit's `src/Sage.Kits.Rpg/DialogueScreen.cs`;
  the client half is one system, `src/Sage.Client/UI/DialogueSystem.cs`, which asks the client's
  `ScreenRegistry` for `"dialogue"` (13 "As built (screens by id)", #27). Tests: `tests/Sage.Tests/Gameplay/DialogueTests.cs`.
- **A conversation is a record, not a script.** Nodes hold a line and the things you may say back; an
  option leads to another node and may *do* something on the way. Everything it can do — give an item,
  take one, apply an effect, move a reputation — is machinery that already existed, which is the whole
  design: **dialogue reaches the rest of the game rather than having rules of its own.**
- **A line you cannot say is shown greyed with the reason**, and `Pick` refuses exactly what `CanPick`
  greys (R17). The screen cannot offer something the rules would then turn down, because they are the
  same question asked twice.
- **What you may say depends on what they think of you**, which is why factions came first: an option
  can require a standing, an item, a tag, or the absence of one.
- **Node names are names, not record ids.** A node is a label inside one dialogue; typing it as a
  `RecordId` sent the validator hunting for a record called `sandbox:greet` and made every `goto` carry
  a namespace it does not have (R11's sharp edge, avoided rather than repeated).
- **The screen is the engine's, the window is the client's**, like the spellmaker: the node's line is the
  panel's title and the things you may say are its rows, so the client learnt nothing new to show a
  conversation. The simulation still opens nothing — `DialogueSystem` reads `Used` and decides that a
  thing with something to say means a window.
- **Two layout bugs the first conversation found** (13 §3): a row's name ran straight through the detail
  column, because until now every row was a short noun and a line of dialogue is a sentence — names are
  clipped with an ellipsis now; and the title itself ran off the panel, so a title wraps and the panel
  grows by exactly the lines it needs.
- **In the Sandbox** a hermit stands up the hill with four things to say, one of them a trade that takes
  the practice sword, pays in standing, and is greyed with "you are carrying no sword" until you have it.
- **Not yet:** quests and a journal (the rest of F24), voice and portraits, a conversation that a second
  person can interrupt, and barks (a line said without a window).

### As built (factions and reputation, 2026-09-24 — F24)
The question three systems were guessing at.

- **Code:** `src/Sage.Gameplay/Factions/Factions.cs` (`faction` records, the `Faction` component, the saved
  `Reputation` resource, the `Factions` rules and `ReputationChanged`), plus the three places that now
  ask it: `AIThinkSystem.FindNearestEnemy`, `AbilityPayload`, `MeleeCombatSystem`. Tests:
  `tests/Sage.Tests/Gameplay/FactionTests.cs`.
- **A faction has an opinion of other factions, and a separate one of *you*.** The first is a table in
  the record (`Hostile`/`Neutral`/`Ally`, with a default for anyone unlisted); the second is a number
  that moves as you act, with two thresholds where it becomes a stance. That is Daggerfall's model, and
  it is smaller than it looks — guilds, ranks and the rest are content stacked on it.
- **An AI hunts enemies, not players.** `FindNearestEnemy` asks `Factions`, so two creatures who have
  never heard of the player fight each other; in the Sandbox a watcher and a firebug now trade blows
  while you watch. Candidates come from the broad phase (cost follows what is *near*, not how many
  creatures exist), plus the cheap player query kept on its own — a buffer that holds sixty-four
  entities must never be the reason a creature fails to notice the player in a crowded world.
- **Being hit is how you find out somebody is behind you.** Sight is a cone, so a creature stabbed in
  the back never turned round — which hardly showed while the only attacker stood in front of it, and
  showed immediately once creatures fought each other. `AIThinkSystem` reads `Damaged` and takes the
  attacker as its target, unless it already has one it still remembers (F23).
- **Who a hit may land on is a rules question, not a physics one** (§3.2). A creature's swing passes
  through its own kind and a blast spares them, including a bolt that strikes one mid-flight. **The
  player may hit anybody**: aiming is their business and their reputation is what pays.
- **Killing somebody costs you with their faction, and a fifth of that with its allies** — while its
  enemies think a little better of you. Only what the *player* does moves a number: a wolf eating a
  sheep is between them.
- **Content that says nothing about factions behaves as it did before there were any:** a creature with
  no faction still comes for the player, which is what "a monster" means in the absence of a social
  model and is what every test written before F24 assumes.
- **Console:** `rep` (what everybody thinks of you, and what that makes them), `rep_set`.
- **Not yet:** dialogue and quests (the rest of F24), crime and guards noticing what you do, factions
  that own places, and ranks within a faction.

### As built (navigation, 2026-09-24 — F23)
Creatures walk round things instead of into them.

- **Code:** `src/Sage.Gameplay/Navigation/Navigation.cs` (`NavGrid`, `NavPath`, `Navigation`), the changed
  `MoveToTargetTask` and target memory in `AI.cs`/`AIThinkSystem.cs`. Tests:
  `tests/Sage.Tests/Gameplay/NavigationTests.cs`.
- **A local grid, built when it is needed and thrown away.** Not a navmesh and nothing precomputed: when
  the straight line is blocked, the creature opens a window round itself and its target (96 cells a side
  at most, 1 m by default), stamps the colliders that are *there now* plus ground too steep to climb,
  runs A* over eight neighbours, straightens the staircase into corners, and walks them. A crate pushed
  into a doorway is in the path within a tick, and nothing has to be rebuilt when the world changes.
- **The straight line is tried first**, with three rays a body's width apart, because most of the time it
  is clear and a plan would be waste — and because falling back to it is what happens when there is no
  route at all. A creature never stops moving because navigation failed; it does what it did before F23.
- **Budgets, not hopes.** A search stops after `nav_maxnodes` cells (4096) and planning stops after
  `nav_plans` plans a tick (4), so a crowd sealed in a room costs a known amount and the creatures that
  miss out keep last tick's path and ask again. A search that found *nothing* backs off to one attempt
  every 2.5 s rather than two a second. `AIThinkSystem` runs in a Fixed phase, and the whole of this
  allocates nothing: the grid, the open set and the corner buffer are all reused or on the stack.
- **Asked at the rate it matters.** "Is the way clear?" is three raycasts, and it is the one cost every
  chasing creature pays whether or not anything is in its way, so the answer is kept for 0.2 s rather
  than asked sixty times a second. Ground too steep to climb is sampled at the *terrain's* resolution
  (8 m) and not the grid's (1 m): one `NormalAt` is a sector lookup and four interpolations, and sixty-
  four of them per terrain step would say nothing extra.
- **A path is a list of world positions, so it moves with the world.** The corners, the place a creature
  last saw its target and the position it planned for are all origin space, and `AIThinkSystem`
  subscribes to `Origin.Rebased` to shift them (R6) — without it a creature a kilometre and a half from
  where it started walks at a corner that is now 1024 m away.
- **A creature remembers what it cannot see, and this is what makes the rest work.** Sight is a cone, so
  walking round a wall means looking away from what you are chasing: without memory a creature forgot its
  target on the first step of the detour and went back to idle. A target stays remembered for
  `memorySeconds` (6 by default, an `ai_profile` field) and the creature walks to **where it last saw**
  it, not to where it actually is — remembering is not clairvoyance. `RememberEnemy` keeps the chase
  schedule running; `LostEnemy` now means the memory ran out.
- **Console:** `nav_enabled` (the A/B — off, creatures go back to leaning on walls), `nav_debug` (the
  cells the last search thought were blocked, and every creature's corners), `nav_stats`, `nav_cellsize`,
  `nav_maxnodes`, `nav_plans`.
- **Measured in the Sandbox**, where a waist-high fence now stands between the player and a watcher: with
  navigation on it rounds the end and reaches the player in about 11 seconds; with `nav_enabled 0` the
  same creature slides 18 m along the fence the wrong way and never arrives. Headlessly, the same A/B is
  a three-walled pen — concave on purpose, because a fence can be escaped by sliding along it and so
  cannot tell steering and planning apart.
- **Not built:** a navmesh for brush-built interiors (F16 has built the geometry; nothing walks on it yet), a
  coarse graph for travelling across sectors, doors and other links a path has to *act* on, crowds
  avoiding each other (creatures are left out of the stamp on purpose), and paths that cost ground
  differently (mud, water, roads).

### As built (conventions, the `Died` event and the narrative plugins, issue #26, 2026-09-29)
- **`gameplay_conventions`** (`src/Sage.Gameplay/Conventions/GameplayConventions.cs`, owned by `sage.gameplay.attributes`): one record says which attribute is health, which tags are dead and invulnerable, the default damage type, attack, movement and AI profiles, the player's faction, the attribute spellmaker spells cost, the five schedules the AI's built-in choice picks (`idle`, `chase`, `meleeAttack`, `castSpell`, `holdGround`) and the action names combat, items, abilities, AI and the character controller read. The engine ships `sage:default_conventions` in `engine_content/data/conventions.json` (with a `sage:player` faction in `factions.json`) and code reads it with `world.Conventions()` (test: TheEngineShipsItsConventionsAsOneRecord); a game patches it, which is how it renames health to hp (test: AGameRenamesHealthToHpByChangingOneRecord). Fourteen `new RecordId("sage", …)` constants are gone from `Sage.Gameplay` and `Sage.Physics3D` and the Sandbox's two literals with them; a test greps those folders and `games/` for any that come back (test: NoGameplayCodeNamesAnEngineRecordId).
- **Empty means "the game has none".** Without the record (a test that mounts no engine content) nothing is health, so nothing dies, and a fighter with no attack cannot swing (test: WithoutConventionsNothingIsLifeAndNothingBreaks). Action names default to the names the engine's modules register, because actions are registered in `Init` before content loads; a name nobody registered is a load error (test: AConventionNamingAnActionNobodyRegisteredIsALoadError).
- **The character controller** sits in `Sage.Physics3D`, below gameplay, so it reads its share (the default movement profile and Jump/Run/Crouch) through `CharacterConventions` (in `Sage.Simulation` beside `CharacterController` since #30, so gameplay reads it without a physics backend), a world resource `CharacterModule` installs with the engine's values and `AttributesModule` replaces with one that reads the record.
- **`Died` replaces the direct calls** (§3.3 above), and **factions, quests and dialogue are three plugins** (`sage.gameplay.factions`, `sage.gameplay.quests`, `sage.gameplay.dialogue`). Saved-resource and component ids did not change (`journal`, `reputation`, `sage:dialogue`, `sage:faction`), so saves load as before. Nothing calls into any of the three: `Factions` answers "no faction" when its records are not registered (a creature is then hostile to the player, the pre-faction rule), every standing is 0 without `Reputation`, `Quests` answers "not on it" without a `Journal`, `DialogueRules.Start` says no without a `Conversation`, and quests count a kill by the victim's own `Faction` component. The issue asked for one `NarrativeModule`; it is two, because its acceptance switches quests and dialogue off separately.
- **Not done:** a switched-off plugin's prefab parts are still errors in content that uses them (records of its types are skipped with a warning); making them optional like the client's parts would need the generated registrations to name a plugin's parts without registering them.

### As built (open vocabularies, issue #28, 2026-09-29)
- **Every closed list is a registry a plugin adds to** (REDESIGN §4.3 stage 1, whose "As built" has the
  table; MAKING_A_GAME §3 and §6 say how to use and extend them). Entries are declared with an attribute
  and registered for their plugin by `VocabularyGenerator`; content names them, and a name nobody
  registered is a load error at its line (test: AnUnknownEntryIsALoadErrorThatSaysTheNearestName).
- **AI** (§3.4): conditions are `ai_condition` entries — the engine's eleven keep their `AICondition`
  bits and are set by its perception; a game's are sensed every think after it (`IAICondition.Sense`) —
  and `GetSchedule` is `ai_profile.selector`: `default` is the old code, `rules` picks by the profile's
  `rules` first (test: AGamesConditionPicksASchedulesThroughAProfilesRules)
  (test: AGamesSelectorIsNamedByTheProfile).
- **Quests** (§"As built (quests)"): objectives are `quest_objective` entries, `kill` when `kind` is left
  out; `reach` and `talk` are new, counted from `QuestWatchSystem` (`sage.quests.watch`: the player's
  position, and `Spoke`), and a game counts its own happenings with `Quests.Notice`
  (test: ReachTalkAndAGamesObjectiveMoveAQuestAlong).
- **Dialogue** (§"As built (dialogue)"): an option's `conditions` and `actions` are `condition` and
  `action` entries; `requires` and `then` are shorthand for the engine's own, asked and done first
  (test: ADialogueOptionUsesAGamesConditionAndActionsByName).
- **Abilities** (§3.3): `delivery` names an `ability_delivery` — where a cast lands (`Release`) and who
  its payload reaches (`Gather`) — and `targeting` names one of the five the enum had
  (test: AnAbilityNamesAGamesDelivery). `AbilityCasting.CastNow` casts one at once and for free, which
  is how an item's `cast` use goes off.
- **Effects** list `executions` (`knockback`, `teleport`, `summon`, `dispel`), run on each application
  and each period; structural work waits for `EffectExecutionSystem` (`sage.effects.executions`)
  (test: AnEffectRunsAGamesExecutionAndTheEnginesOwn) (test: KnockbackPushesACharacterAwayFromTheSource).
- **Items** list `uses` (`consume`, `read`, `cast`), run in order by `world.UseItem` and the `use_item`
  command (test: AnItemIsUsedThroughItsUsesInOrder) (test: ReadingTeachesAndAWandCastsForFree).
- **Caps:** `GameplayTags` holds a 256-bit `TagSet` (it was one ulong) and `ActionMask` two words, 128
  buttons; saves still write tags by name (test: TagsGoPast64) (test: ActionMask_HoldsButtonsPast64).

### As built (one condition and action language, issue #89, 2026-09-30)
- **The language is the base's** (REDESIGN §4.3 stage 2, decision D2). `ICondition`, `IAction`, their
  `[Condition]`/`[Action]` attributes and `ConditionContext`/`ActionContext` moved from `Sage.Gameplay`
  to `Sage.Simulation` (`src/Sage.Simulation/Logic/Conditions.cs`); content ids did not change, and the
  C# namespace move is allowed because they are SAGE0120 experimental API (MAKING_A_GAME §10b). A context
  is the world, the `Subject` it is about, the `Other` involved and the world's `Records` — no gameplay
  type. `Conditions.Test`/`TestAll`/`Evaluate` ask, `Conditions.Run` does (SAGE0124).
- **The base's words, owned by `sage.core`, so every game has them:** `all`, `any` (none of nothing:
  an empty `any` never holds), `not`, `var` (`eq`/`min`/`max`), and the actions `fire` (04 §3.4a),
  `set_var` and `add_var`. A game with no plugins but its own reads
  `"requires": { "all": [ …, { "not": … } ] }` and asks it (test: AGameWithoutDialogueReadsAndEvaluatesNestedRequires)
  (test: APluginThatIsNotLoadedRegistersNoEntries).
- **The shorthand:** an entry is `{ "condition": "has_item", "item": "key" }` or `{ "has_item": "key" }` —
  the first property that is a registered id names the entry, its value fills the entry's `[EntryValue]`
  field (or is its settings, as an object, when that field is not itself a condition), and the rest are
  settings (`VocabularyAttribute.Shorthand`, `Vocabulary.Expand`; also what the namespace qualifier
  reads, so a bare id in a patch means the patching file's namespace). An unknown id is a load error
  with the nearest one in either form (test: AnUnknownIdSuggestsTheNearestOne), and `sage schema` writes
  both forms (test: TheCommittedSchemasReadTheShorthand).
- **World variables:** `Vars`, the saved resource `vars` every world has — named numbers, 0 when unset,
  names case-sensitive — written `"vars": { "version": 1, "data": { "Values": { "alarm": 1 } } }`
  (test: AVarSurvivesSaveAndLoad).
- **Gameplay's words moved to the plugins they ask about**, out of dialogue: `has_tag`, `lacks_tag` and
  `apply_effect` are `sage.gameplay.attributes`'; `has_item`, `give_item` and `take_item`
  `sage.gameplay.items`'; `standing` and `change_standing` `sage.gameplay.factions`'; the quest ones stay
  `sage.gameplay.quests`', and `quest` gained `atLeast` (that stage, one after it, or finished;
  `Quests.HasReached`) (test: AQuestConditionAsksHowFarAlongItIs)
  (test: GameplayEntriesComeWithTheirPluginsNotWithDialogue). Dialogue keeps `requires`/`then` as sugar
  for them (`src/Sage.Gameplay/GameplayConditions.cs`).
- **Asking allocates nothing:** lists are read once at load and walked by index; every reason is a
  constant (test: EvaluatingConditionsAllocatesNothing).
- **Not built:** nothing but a game's own code and dialogue read a `requires` yet — conditional wires and
  relays with `then` are #91, state machines #92 (built: see "As built (state machines)"), topics #93. `fire` looks its target up when it runs,
  so an input sent at a name that does not exist yet is dropped (a wire's late binding would need
  `EntityIO` to take a name; #90 owns that file). Vars hold numbers only.

### As built (phase 4b's exit games, issue #94, 2026-09-30)
- `tests/games/topics` is a game with no C# (plugins `sage.gameplay.character`, `.io`, `.dialogue`): one
  `dialogue_topic` whose first info is conditional on a var and whose fallback answers otherwise, an NPC
  with a `dialogue` part, and a relay whose `then` sets the var. The test asks it before and after the
  relay fires (AConditionalTopicAnswersByAVar_InAGameWithNoCode). `tests/games/scripted-sequence` has the
  NPC speak through `StartDialogue` (04 §3.4e).

### As built (dialogue topics, issue #93, 2026-09-30)
- **A topic is a keyword whose answer depends on who asks, whom and when** (Morrowind's topics, beside
  the node tree, which is unchanged). `dialogue_topic` (`TopicRecord`, `src/Sage.Gameplay/Narrative/Topics.cs`,
  plugin `sage.gameplay.dialogue`) is a `keyword`, a `known` flag and an ordered list of `infos`; an info
  is `requires` — **one** condition of #89's language, `{ "all": [ … ] }` for several, the way any
  `requires` reads — a `then` (a list of actions) and its `text`. **The first info whose `requires` holds
  is the answer**, asked with the listener as the subject and the speaker as the other, so one topic
  answers by standing, by how far a quest has got and by a var (test: ATopicAnswersByStandingQuestStageAndAVar).
  `keyword` and `text` are stored as written; a raw `@key` is resolved by 4c where it is shown.
- **The dialogue plugin's two words:** `add_topic` teaches the action's subject a topic
  (`{ "add_topic": "the_toll" }`), and `speaker` holds when the other has that `sage:dialogue` record,
  entity `name` or `prefab` — Morrowind's speaker filter, so an info can be one NPC's.
- **What a listener knows is saved on it:** `sage:known_topics` (`KnownTopics`), the learnt ids in the
  order learnt, written `"sage:known_topics": { "version": 1, "data": { "Topics": ["ns:topic", …] } }`
  (test: KnownTopicsSurviveSaveAndLoad). A `known: true` topic is everyone's from the start and is never
  written down. A topic learnt from one NPC is asked of another, and a list only shows the topics this
  speaker has an answer for (test: ATopicLearntFromOneNpcIsAskedOfAnother).
- **The view model, frozen on #93 for #98's screen** (SAGE0124): `DialogueTopics.Available(world,
  speaker, listener, into)` clears and fills a caller's `List<AvailableTopic>` (id and keyword), sorted
  by keyword, and allocates nothing once the list has room (test: ListingTopicsAllocatesNothing);
  `Answer` finds the answering info without doing anything; `Ask` runs its `then` and returns it, null
  exactly when `Answer` is (R17); `Knows` and `Learn`.
- **Into the node tree:** an option with `"topics": true` does what it does, keeps the conversation on
  its node and sets `Conversation.Topics`, which a screen reads to show the list
  (test: ANodeOptionOpensTheTopics). The kit's screen for it is #98; until then the Sandbox's two topics
  (`games/Sandbox/content/data/topics.json`) are read by the validator, the schemas and the API only.
- **Without the dialogue plugin** `dialogue_topic` records are skipped with a warning, as any record of
  an unregistered type, and nothing is an error; `add_topic` and `speaker` are not registered, so a
  record of another plugin that names them is a load error at its line (the language's rule), never a
  crash; and the API answers "nothing to say": `Available` fills nothing, `Answer`/`Ask` are null,
  `Learn` false (test: WithoutTheDialoguePluginTopicsSayNothing). A save's `sage:known_topics` still loads.
- **Not built:** topics found in an answer's text (Morrowind's hyperlinks: write `add_topic` instead),
  greetings and per-speaker "known" lists, and a `TopicAsked` event for `talk` objectives (an info's
  `then` can `set_stage` already).

### As built (state machines, issue #92, 2026-09-30)
- **The language's second reader in the base:** a `state_machine` record's transitions ask a `when`
  condition every tick and its states run `enter`/`exit` actions (and a transition its `then`), with the
  activator as the subject and the machine as the other — the same contexts a wire's `fire` uses. A guard
  that grows alert on `{ "var": "alarm", "eq": 1 }`, attacks after a second and calms down on an input is
  data only (test: AGuardIdlesGrowsAlertAttacksAfterATimeoutAndCalmsDownOnAnInput). The mechanism —
  `sage:state_machine`, `SetState`, `OnStateChanged`, `on` any input name, saves and hot reload — is
  entity I/O's, in 04 §3.4c.
- **For AI and animation:** a state's `tags` (`StateMachines.HasTag`) are how gameplay code reads a
  machine without knowing its state names; `StateTransition` and `StateMachines.FirstTransition` are the
  rules 4d's animation graph steps with.

### As built (attributes, tags and effects, 2026-09-22)
- **Code:** `src/Sage.Gameplay/Attributes/Attributes.cs` (attribute and tag records, the id registries, the `Attributes` and `GameplayTags` components) and `Effects.cs` (`effect` records, `ActiveEffects`, `Effects.Apply/Remove/IsActive`, `EffectSystem`).
- **Ids are indices.** `attribute` and `tag` records become small indices (`GameplayRegistries`), so components hold numbers, not strings: attribute values are parallel arrays, tags a 64-bit set. More than 64 tags is reported rather than silently truncated.
- **Effects are the only thing that changes attributes.** Instant effects change the base value (damage, healing); timed and infinite ones are recomputed into the current value every tick, in order: adds, then multiplies, then overrides. `period` re-applies the modifiers on a timer (damage over time), `stacking` is Separate/Refresh/Stack with a cap, and `grantTags` holds tags while the effect runs.
- **Tags gate application**: `requireTags` and `blockTags` decide whether an effect lands. The `god` cheat is exactly that — it gives the player `state.invulnerable`, and damage effects block themselves on it, instead of every damage path checking a flag.
- **Death is a seam, not a feature.** When health reaches 0 the system tags the entity `state.dead` and calls `GameRules.OnEntityDied` once, outside the query loop. What death means is the game's business; the Sandbox respawns the player. *Since issue #26* it raises a **`Died(Victim, Killer)`** event instead of calling anyone: `FactionDeathSystem` and `QuestDeathSystem` read it, and `DeathRulesSystem` (`sage.effects.deaths`, after both) calls `GameRules.OnEntityDied`, all in the same Gameplay phase and tick (test: EachNarrativePluginCanBeSwitchedOffAlone) (test: TheEffectSystemCallsNobodyWhenSomethingDies). Which attribute is health and which tag is dead come from the `gameplay_conventions` record, not constants (§"As built (conventions)").
- **Effects never add components.** They are applied from inside system loops (the AI's melee task), where a structural change throws, so an entity is set up once with `world.AddAttributes(entity)` and an effect on anything else is reported and ignored.
- **Built (F21, 2026-09-23):** abilities and cues, projectiles, and the spellmaker — see "As built (abilities)" and "As built (the spellmaker)" below. **Not yet:** skill progression.

- **Built (F21, 2026-09-23):** creatures that cast — see "As built (AI casting)".
- **Not yet:** possession (a controller that changes pawn), and the rest of the module table — narrative, factions, quests, economy. Combat, inventory and interaction were built on 2026-09-22/23; see their "As built" sections above.

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
public struct AIState { public RecordId Profile, Schedule; public int TaskIndex; public ulong Conditions; public RecordId Spell; public Entity Target; public float NextThink, TaskTime; public bool TaskStarted; }

[Record("effect")]  public sealed class EffectRecord { public List<AttributeModifier> Modifiers; public EffectDuration Duration; public float Period; public EffectStacking Stacking; public List<RecordId> GrantTags, RequireTags, BlockTags, Cues; }
[Record("ability")] public sealed class AbilityRecord { public RecordId CostAttribute; public float Cost; public RecordId Cooldown; public AbilityTargeting Targeting; public float Range, Radius, Width, CastTime, Damage, Magnitude; public RecordId DamageType; public List<RecordId> Effects, RequireTags, BlockTags, Cues; public string Animation; public RecordId Projectile; public float ProjectileSpeed; }

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

**Still design-only:** combat, inventory and interaction aren't sketched here yet (§3.2) — they are built, just not written out above. Everything sketched above is shipped: `Pawn` really is that empty (no `Controller` back-reference — a controller only ever writes `PawnIntent`, nothing points the other way), and `EntityRef` is `Entity` in code everywhere (glossary, 03). `AIState` has **no** cooldown field, despite an earlier revision of this line claiming one: what paces a creature is its schedule and its abilities' own cooldown effects.

## 11. v1 scope vs later
- **v1:**
  - `GameRules`;
  - Controller/Pawn/`PawnIntent`;
  - Character + first-person rig;
  - attributes/effects/tags/abilities (health, mana, damage, burning, fireball);
  - the spellmaker (composing effects into an `ability` record, saved as the drafts behind it);
  - minimal combat, inventory and interaction;
  - AI schedules for one melee creature and one that casts;
  - log categories `Gameplay`, `AI`;
  - `ai_debug` overlay (schedule, task and conditions above each agent);
  - `give <item>`, `god`, `notarget` cheats.
- **Later:** the rest of the module table.

## 14. Build steps
1. ~~Controller/Pawn/`PawnIntent` + Character + `GameRules`~~ **Done 2026-09-22** (TODO F7, with 10).
2. ~~Attributes/effects/tags + `EffectSystem`~~ **Done 2026-09-22** (TODO F18).
3. ~~Abilities + cues + fireball~~ **Done 2026-09-23** (F21 v1, "As built (abilities)"), and with them ~~projectiles~~ and ~~the spellmaker~~ the same day. AI that casts is left.
4. ~~Combat, inventory and interaction~~ **Done 2026-09-22/23** (TODO F20, F19).
5. ~~AI schedules + perception + one creature~~ **Done 2026-09-22** (TODO F22), and ~~one that casts~~ 2026-09-23 with F21. Its swings go through the same `MeleeCombatSystem` a player's do (F20), so a creature can miss, and what its claws do is an `attack` record.
