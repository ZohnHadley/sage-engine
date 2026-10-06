# 16 · Gameplay framework

> Status: built and tested for combat, attributes, items, abilities, factions, dialogue and quests; progression, economy, crime and combat reactions are not started. Owning assemblies: `Sage.Gameplay`, with the pawn and controller types in `Sage.Simulation` and the character in `Sage.Physics3D`. Design doc: [16-gameplay-framework](../../design/16-gameplay-framework.md).

## 1. Purpose and scope

The gameplay framework is the base layer of rules every action RPG or shooter needs and no genre decides: who controls what, what changes a number, how a blow lands, what an item does, who is hostile to whom, what an NPC says and what a quest watches for. It is data first: attributes, effects, attacks, abilities, items, factions, dialogue and quests are records, and what they do is one pipeline each.

It deliberately does not do the following. Genre rules such as skills, levelling, the spellmaker, barter and the RPG screens are the kit's, see [17-rpg-kit](17-rpg-kit.md). The character controller, movers and joints are [08-physics](08-physics.md). AI think, perception, routines and navigation are [09-navigation-and-ai](09-navigation-and-ai.md). Animation graphs and IK are [10-animation](10-animation.md). Entity I/O, the condition and action language and state machines are [05-events-and-logic](05-events-and-logic.md). Screens are [13-ui](13-ui.md).

## 2. Responsibilities

- `GameRules`: the game's rules object for a world (player spawn, death, load hooks), supplied by `IGameModule.CreateRules`.
- Controller and pawn split: players and AI both write `PawnIntent`, and movement, combat and interaction read it.
- Attributes, gameplay tags and effects: the only things that change an attribute; stacking, periods, tag gates, executions.
- The hit pipeline: a strike is a request, a delivery and a landing, dealt once through `Combat.ApplyHit`, with factions, resistances, hit locations, ammunition, spread and recoil.
- Abilities and cues: cost, cooldown, targeting, payload and refusal reasons.
- Items, inventory by weight, equipment slots, pickups, item uses, and the Use interaction.
- Factions and a saved reputation, dialogue trees and topics, quests and the journal.
- `gameplay_conventions`: which attribute is health, which tags mean dead, default damage type and action names.

Not responsible for: skills and levelling (open, #377), containers and loot tables (#378, #379), real barter (#380), durability (#382), item instances and enchantments (#383), crime (#389), blocking and parry (#390).

## 3. Placement and dependencies

`Sage.Gameplay` is the top of the base: `Sage.Core` ← `Sage.Simulation` ← `Sage.Physics3D` ← `Sage.Gameplay`. It never references a kit (SAGE0025) and has no MonoGame (SAGE0024). `GameRules`, `Pawn`, `PawnIntent`, `PlayerControlled` and `PlayerControlSystem` live in `Sage.Simulation` so a world without a physics backend can still have a controller. The kit and games build on public gameplay API only.

Each module is a plugin with a stable id, picked in `game.json`:

| Plugin id | Owns |
|---|---|
| `sage.gameplay.character` | The character and controller (in `Sage.Physics3D`). |
| `sage.gameplay.attributes` | `attribute`, `tag`, `effect`, `gameplay_conventions`; `attributes` and `effects` parts; the effect and death systems. |
| `sage.gameplay.combat` | `damage_type`, `attack`, `spread`, `recoil`, `hit_location`, `hitboxes`; melee, reload, recoil systems. |
| `sage.gameplay.items` | `item`, inventory, equipment, pickup, use, the interaction system. |
| `sage.gameplay.abilities` | `ability`, `cue`, projectiles, the cast system. |
| `sage.gameplay.factions` | `faction`, the `reputation` resource. |
| `sage.gameplay.dialogue` | `dialogue`, `dialogue_topic`, known topics. |
| `sage.gameplay.quests` | `quest`, the `journal` resource, objectives. |

`BasePlugins.All` lists every one so game, server, `sage validate` and `sage schema` agree. A switched-off plugin's records are skipped with a warning and its prefab parts are errors.

## 4. Interfaces

| Type | File | Role |
|---|---|---|
| `GameRules`, `IGameModule.CreateRules` | `src/Sage.Simulation/App/GameRules.cs`, `Modules.cs` | `OnWorldStarted`, `SpawnPlayer`, `OnEntityDied`, `OnLoaded`; the default is `DefaultGameRules`. |
| `Pawn`, `PawnIntent`, `PlayerControlled`, `ActionMask` | `src/Sage.Simulation/Input/` | Body marker; the intent controllers write; the player tag; 128-button mask. |
| `Effects`, `EffectSystem`, `Died` | `Attributes/Effects.cs` | Apply, remove, query effects; raises `Died(Victim, Killer)`. |
| `Combat.ApplyHit`, `HitRequest`, `HitResult`, `HitContext`, `Hits` | `Combat/Combat.cs`, `Hits.cs` | The one damage path; deliveries `sweep`, `ray`, `projectile` are `hit_delivery` entries. |
| `Ammunition`, `WeaponFired`, `DryFire` | `Combat/Ammunition.cs` | Magazines and reload. |
| `Items` (`Give`, `Take`, `Equip`, `Unequip`, `Drop`, `SpawnPickup`), `EquipSlots`, `ItemUses`, `Used` | `Items/` | Inventory and equipment rules, item uses (`consume`, `read`, `cast`), the Use event. |
| `AbilityCasting`, `CastRefused`, `CueTriggered` | `Abilities/` | Cast, refusal reasons, cues. |
| `Factions`, `ReputationChanged` | `Factions/Factions.cs` | Stance of a faction to another and to the player. |
| `Quests`, `QuestChanged`, `DialogueTopics`, `Spoke` | `Narrative/` | Journal rules (since #349 `Track` and `IsTracked`, a stage's and an objective's `target`, an entry's `History`); topic answering; speech event. |

Vocabularies a game extends by attribute: `hit_delivery`, `ability_delivery`, `effect_execution`, `item_use`, `quest_objective` (`kill`, `have`, `reach`, `talk`), plus the base condition and action words each plugin owns (`has_tag`, `lacks_tag`, `is_alive`, `has_item`, `standing`, `quest`, `apply_effect`, `set_tag`, `cue`, `give_item`, `change_standing`, `start_quest`, `set_stage`, `add_topic`, `speaker`).

Console: `give`, `equip`, `unequip`, `drop`, `use_item`, `cast`, `learn`, `hurt`, `god`, `rep`, `rep_set`, `quests`, `quest_start`, `quest_stage`, `cast_debug`, `combat_debug`. Cvar `g_interact_range` (2.5 m). Events: `Damaged`, `Died`, `Used`, `WeaponFired`, `DryFire`, `CastRefused`, `CueTriggered`, `ReputationChanged`, `QuestChanged`, `Spoke`.

## 5. Data model

| Kind | Ids |
|---|---|
| Records | `attribute`, `tag`, `effect`, `gameplay_conventions`, `damage_type`, `attack`, `spread`, `recoil`, `hit_location`, `hitboxes`, `item`, `ability`, `cue`, `faction`, `dialogue`, `dialogue_topic`, `quest` |
| Components | `sage:attributes`, `sage:gameplay_tags`, `sage:active_effects`, `sage:melee`, `sage:weapon_state` (transient), `sage:magazine`, `sage:hitbox`, `sage:inventory`, `sage:equipment` (version 2), `sage:pickup`, `sage:abilities`, `sage:projectile`, `sage:faction`, `sage:dialogue`, `sage:known_topics`, `sage:quest_watch` |
| Tag | `sage:interactable` |
| Saved resources | `journal`, `reputation`, plus `vars` and `clock` from the base |
| Prefab parts | `attributes`, `effects`, `melee`, `hitboxes`, `inventory`, `pickup`, `abilities`, `faction`, `dialogue`, `quest_watch` |

An attribute value is saved by id and base value only; the current value is recomputed. Tags are saved by id. An item is a record and an inventory a list of ids and counts, so stacking, saving and modding are cheap; an item becomes an entity only as a `Pickup` on the ground. An attack names its `delivery`, damage, `ammo`, `magazine`, `spread`, `recoil`, `projectile`, `projectileGravity` and `projectilePierce`. `known: true` topics are everyone's and never saved.

## 6. Lifecycle and data flow

All gameplay runs in the fixed tick. Controllers write `PawnIntent` in Phase.Commands (`PlayerControlSystem`, then AI think, then `sage.combat.recoil`, which keeps the intent final). In Phase.Gameplay: `sage.combat.reload`, then `sage.combat.melee` (the attack system, which queues requests, hands landed blows to their deliveries and deals them in order), projectile and cast systems, item use, then `sage.effects.tick`, executions, and `sage.effects.deaths`, which raises `Died` once. Quest, faction and I/O bridge systems read `Died` and `Damaged` between the tick and the deaths. `sage.combat.hitboxes` runs in Phase.PrePhysics.

Boot: `IGameModule.CreateRules` supplies the rules; `Engine.CreateWorld` calls `OnWorldStarted` after every module has seen the world. Registration is declarative; nothing registers in `Start` or a system (SAGE0020). Save and load: the components and resources above are saved by stable id; `GameRules.OnLoaded` runs after a load; a bolt in flight, rounds loaded and known topics survive a save (tests: `ABoltSavedMidFlightStillLandsAfterTheLoad`, `ASaveWithThreeRoundsLoadedStillHasThreeAfterLoading`, `KnownTopicsSurviveSaveAndLoad`).

## 7. Threading, memory and performance

Single-threaded on the main thread. Steady-state ticks allocate nothing in the measured scenarios: twenty attackers of every delivery (test: `TwentyAttackersAllocateNothingOver600Ticks`), twenty wielders with spread and recoil (test: `TwentyWieldersAllocateNothingOver600Ticks`), fifty NPCs with eleven hitboxes each (test: `FiftyNpcsWithHitboxesAllocateNothingPerTick`) and condition evaluation (test: `EvaluatingConditionsAllocatesNothing`), each beside the physics backend's own few tens of bytes a tick. Randomness for spread is a hash of tick, entity, shot and salt, not `System.Random`, so replays and a future server agree. Tags are a 256-bit set (test: `TagsGoPast64`) and the action mask holds 128 buttons.

## 8. Errors and diagnostics

Content mistakes are load errors at their lines: an unknown vocabulary entry with the nearest name (test: `AnUnknownEntryIsALoadErrorThatSaysTheNearestName`), a bad record reference, a missing attribute or tag, a dialogue node that does not exist. Without `gameplay_conventions` nothing is health and so nothing dies, and a fighter without an attack cannot swing (test: `WithoutConventionsNothingIsLifeAndNothingBreaks`). A dialogue option a player cannot pick is shown with the reason, and `Pick` refuses exactly what `CanPick` shows. Refused casts carry the reason in `CastRefused`; `Give` refuses what will not fit and says why. Effects applied to an entity without `sage:attributes` are reported and ignored. Log categories `Gameplay` and `AI`. Debug with `cast_debug`, `combat_debug`, `ai_debug`, `rep` and `quests`.

## 9. Requirements

| ID | Requirement | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-GAME-01 | A game shall supply its rules through `CreateRules`, with player spawn, death and load hooks. | Must | Done | `src/Sage.Simulation/App/GameRules.cs` |
| REQ-GAME-02 | Players and AI shall drive pawns through the same `PawnIntent`. | Must | Done | `src/Sage.Simulation/Input/PawnIntent.cs` |
| REQ-GAME-03 | Attributes shall change only through effects, with stacking, periods, tag gates and executions. | Must | Done | test: `AnEffectRunsAGamesExecutionAndTheEnginesOwn` |
| REQ-GAME-04 | Every strike shall pass one pipeline: request, delivery (sweep, ray, projectile), faction check, resistance, effect, `Damaged`. | Must | Done | `src/Sage.Gameplay/Combat/Combat.cs`; test: `WeaponsExit_SameDamagedPipelineForAllFour` |
| REQ-GAME-05 | Damage shall depend on hit location and armour, as data. | Must | Done | test: `AShotAtTheHeadLandsOnHeadForDoubleDamage_AndAHelmetHalvesIt` |
| REQ-GAME-06 | Weapons shall have ammunition, magazines, reload, spread and recoil that survive a save. | Must | Done | test: `AnAttackWithNoAmmoNeverRunsDry`, `WeaponsExit_TheAmmoSurvivesAReloadAndASave` |
| REQ-GAME-07 | Projectiles shall fly arced and swept, pierce, and survive a save. | Must | Done | test: `ABoltSavedMidFlightStillLandsAfterTheLoad` |
| REQ-GAME-08 | Abilities shall gate on known, ready, affordable and allowed, with cooldowns as effects. | Must | Done | `src/Sage.Gameplay/Abilities/AbilitySystem.cs` |
| REQ-GAME-09 | Items shall be records with weight-limited inventories, named equipment slots, pickups and ordered item uses. | Must | Done | test: `AnItemIsUsedThroughItsUsesInOrder` |
| REQ-GAME-10 | Factions shall give a faction-to-faction stance and a saved reputation that moves with the player's actions. | Must | Done | `src/Sage.Gameplay/Factions/Factions.cs` |
| REQ-GAME-11 | Dialogue shall be data: node trees and Morrowind-style topics answered by standing, quest stage and variables. | Must | Done | test: `ATopicAnswersByStandingQuestStageAndAVar` |
| REQ-GAME-12 | Quests shall watch for kills, items, reaching a place and talking, and a game may add objectives by attribute. | Must | Done | test: `ReachTalkAndAGamesObjectiveMoveAQuestAlong` |
| REQ-GAME-13 | The Use interaction shall find the aimed or nearest usable thing and fire its I/O. | Must | Done | `src/Sage.Gameplay/Items/Items.cs` (`InteractionSystem`) |
| REQ-GAME-14 | Quest objectives shall include timers and failure. | Should | Not started | #391 |
| REQ-GAME-15 | Containers and corpses shall be lootable entities, with loot tables and leveled lists. | Must | Not started | #378, #379 |
| REQ-GAME-16 | Skills and levelling, perks and traits shall be rules as data. | Must | Not started | #377, #381 |
| REQ-GAME-17 | Shops shall move money and have merchant gold, disposition and restock. | Must | Partial: stub price rule, no money | #380 |
| REQ-GAME-18 | Items shall have durability, condition, repair, instances, enchantments and stack splitting. | Should | Not started | #382, #383 |
| REQ-GAME-19 | Equipment slots shall be registrable from data, and weight shall have consequences. | Could | Not started | #384 |
| REQ-GAME-20 | Combat shall have blocking, parry, knockback, hit reactions and directional attacks. | Should | Not started | #390, #359 |
| REQ-GAME-21 | Crime, witnesses, bounty and faction ranks shall be supported. | Should | Not started | #389 |
| REQ-GAME-22 | Dialogue shall have barks, greetings, linked topics and per-speaker known lists. | Could | Not started | #392 |
| REQ-GAME-23 | Friendly-fire, self-heal, buffs, and projectile bounce and stick shall be supported. | Could | Not started | #393 |

## 10. Open work

Milestone 9, progression and economy (epic #376).

- #377 4f-1 Skills that rise with use or XP, and levelling (rules as data) (P1)
- #378 4f-2 Containers and corpse looting as entities (P1)
- #379 4f-3 Loot tables and leveled lists (P1)
- #380 4f-4 Real shops and barter (P1)
- #381 4f-5 Perks and traits as effects with prerequisites (P2)
- #382 4f-6 Item durability, condition and repair (P2)
- #383 4f-7 Item instances, enchantments and stack splitting (P2)
- #384 4f-8 Slots registrable from data, and weight/encumbrance consequences (P3)

Milestone 10, AI, combat and narrative depth (epic #385).

- #389 4r-4 Crime, witnesses, bounty and faction ranks (P2)
- #390 4r-5 Blocking, parry, knockback and hit reactions (P2)
- #391 4r-6 Quest objectives for places, talking, timers, failure (P2)
- #392 4r-7 Dialogue extras: barks, greetings, hyperlinked topics, speaker-known lists (P3)
- #393 4r-8 Friendly-fire and ability gaps: self-heal, buffs, projectile bounce/stick (P3)
- #394 4r-9 Tests for the kit screens and combat edge cases (P3)

Related: #359 4p-3 directional attack and block sets (P2). #344 (4q-7) is built: the dead say nothing (`DialogueRules.Start` refuses a speaker with the conventions' dead tag) and using a body loots it (17-rpg-kit).

## 11. References

- [16-gameplay-framework](../../design/16-gameplay-framework.md): attributes and effects, the hit pipeline (#133 to #139), items, abilities, factions, dialogue, topics, quests, conventions, open vocabularies.
- [REDESIGN](../../REDESIGN.md) §4.3 (vocabularies, one condition language) and §5 phases 4e and 4f; [MAKING_A_GAME](../../MAKING_A_GAME.md) §3, §6 and §10b.
- Siblings: [05-events-and-logic](05-events-and-logic.md), [08-physics](08-physics.md), [09-navigation-and-ai](09-navigation-and-ai.md), [17-rpg-kit](17-rpg-kit.md), [13-ui](13-ui.md); parent [SRS](../SRS.md).
