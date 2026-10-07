# 16 · Gameplay framework

> Status: built and tested for combat, attributes, items, abilities, factions, dialogue and quests, and since phase 4f (2026-10-07) containers, loot tables, item instances, durability, slots in data, burden and merchants; and since phase 4r (2026-10-07) crime and bounty, blocking, parry, knockback and hit reactions, damage over time through resistances, a faction filter for friendly fire, quest timers and failure, barks and greetings, and healing, buffs and bouncing or sticking projectiles. Owning assemblies: `Sage.Gameplay`, with the pawn and controller types in `Sage.Simulation` and the character in `Sage.Physics3D`. Design doc: [16-gameplay-framework](../../design/16-gameplay-framework.md).

## 1. Purpose and scope

The gameplay framework is the base layer of rules every action RPG or shooter needs and no genre decides: who controls what, what changes a number, how a blow lands, what an item does, who is hostile to whom, what an NPC says and what a quest watches for. It is data first: attributes, effects, attacks, abilities, items, factions, dialogue and quests are records, and what they do is one pipeline each.

It deliberately does not do the following. Genre rules such as skills, levelling, perks, the spellmaker and the RPG screens are the kit's (the base gives them `attribute_gain`), see [17-rpg-kit](17-rpg-kit.md). The character controller, movers and joints are [08-physics](08-physics.md). AI think, perception, routines and navigation are [09-navigation-and-ai](09-navigation-and-ai.md). Animation graphs and IK are [10-animation](10-animation.md). Entity I/O, the condition and action language and state machines are [05-events-and-logic](05-events-and-logic.md). Screens are [13-ui](13-ui.md).

## 2. Responsibilities

- `GameRules`: the game's rules object for a world (player spawn, death, load hooks), supplied by `IGameModule.CreateRules`.
- Controller and pawn split: players and AI both write `PawnIntent`, and movement, combat and interaction read it.
- Attributes, gameplay tags and effects: the only things that change an attribute; stacking, periods, tag gates, executions.
- The hit pipeline: a strike is a request, a delivery and a landing, dealt once through `Combat.ApplyHit`, with factions, resistances, hit locations, ammunition, spread and recoil.
- Abilities and cues: cost, cooldown, targeting, payload and refusal reasons.
- Items, inventory by weight, equipment slots, pickups, item uses, and the Use interaction.
- Factions and a saved reputation, dialogue trees and topics, quests and the journal.
- Containers and bodies to loot, with locks, keys, owners and respawn; loot tables and leveled lists (#378, #379).
- Item instances (a name, enchantments, condition, charges), stack splitting, durability and repair (#383, #382).
- Equipment slots from data, and burden that slows a carrier (#384).
- Merchants: money as an item, a purse, prices by skill and standing, atomic trades and restock (#380).
- Attributes gained on events (`attribute_gain`), the seam the kit's skills rise by (#377).
- `gameplay_conventions`: which attribute is health, which tags mean dead, default damage type and action names.

Not responsible for: skills, levelling and perks, or faction ranks (the kit's, [17](17-rpg-kit.md)), or the AI that decides to guard or flank ([09](09-navigation-and-ai.md)).

## 3. Placement and dependencies

`Sage.Gameplay` is the top of the base: `Sage.Core` ← `Sage.Simulation` ← `Sage.Physics3D` ← `Sage.Gameplay`. It never references a kit (SAGE0025) and has no MonoGame (SAGE0024). `GameRules`, `Pawn`, `PawnIntent`, `PlayerControlled` and `PlayerControlSystem` live in `Sage.Simulation` so a world without a physics backend can still have a controller. The kit and games build on public gameplay API only.

Each module is a plugin with a stable id, picked in `game.json`:

| Plugin id | Owns |
|---|---|
| `sage.gameplay.character` | The character and controller (in `Sage.Physics3D`). |
| `sage.gameplay.attributes` | `attribute`, `tag`, `effect`, `gameplay_conventions`, `attribute_gain`; `attributes` and `effects` parts; the effect, death, gains and speed systems; the `attribute` condition. |
| `sage.gameplay.combat` | `damage_type`, `attack`, `block`, `spread`, `recoil`, `hit_location`, `hitboxes`; melee, reload, recoil systems; guards and hit reactions. |
| `sage.gameplay.items` | `item`, `loot_table`, `merchant`, `equip_slot`, `encumbrance`; inventory, equipment, pickup, use, containers, loot, burden and merchants; the interaction system. |
| `sage.gameplay.abilities` | `ability`, `cue`, projectiles, the cast system. |
| `sage.gameplay.factions` | `faction`, `crime`, the `reputation` and `bounty` resources; the `guard` and `owned_place` parts; the crime system and the guard AI conditions. |
| `sage.gameplay.dialogue` | `dialogue`, `dialogue_topic`, `barks`, known topics, the bark system. |
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
| `Items` (`Give`, `Take`, `Equip`, `Unequip`, `Drop`, `SpawnPickup`; since #383 `TakeAt`, `Split`, `MoveTo`, `DropAt`, `EquipAt`, `WornInstance`), `ItemInstance`, `EquipSlots`, `ItemUses`, `Used` | `Items/` | Inventory and equipment rules, item instances, item uses (`consume`, `read`, `cast`, `repair`), the Use event. |
| `Containers`, `ItemContainer`, `Stolen` | `Items/Containers.cs` | Open (lock, key, refill), `Took`, theft (#378). |
| `LootTables.Roll`, `LootTableRecord`, `LootPart`, `Loot` | `Items/LootTables.cs` | Rolling a table into an inventory, on spawn or death (#379). |
| `Durability`, `ItemDurability`, `ItemBreak`, `ItemBroke` | `Items/Durability.cs` | Condition, wear, breaking and repair (#382). |
| `Encumbrance` (`AddBurden`, `CarryLimitOf`, `BurdenLevelOf`), `EquipSlotRecord` | `Items/Encumbrance.cs`, `Slots.cs` | Burden levels and slots in data (#384). |
| `Merchants` (`Price`, `Buy`, `Sell`, `Ready`, `MoneyOf`), `TradeResult`, `TradeRefusal`, `Traded` | `Items/Merchants.cs` | Prices, atomic trades and restock (#380). |
| `AttributeGainRecord`, `AttributeGained` | `Attributes/AttributeGains.cs` | An attribute gained on `Damaged`, `Died`, `AbilityCast` or `Used` (#377). |
| `AbilityCasting`, `CastRefused`, `CueTriggered` | `Abilities/` | Cast, refusal reasons, cues. |
| `Factions`, `ReputationChanged` | `Factions/Factions.cs` | Stance of a faction to another and to the player. |
| `FactionFilter`, `FactionFilters.Allows` | `Factions/FactionFilter.cs` | The one filter (Default, Anyone, NotAllies, Hostile, Allies) for friendly fire, an ability's `affects` and heal or buff targets (#390, #393). |
| `Crime`, `CrimeRecord`, `Bounties`, `Guard`, `OwnedPlace`, `CrimeCommitted`, `BountyChanged` | `Factions/Crime.cs` | Crimes, witnesses, bounty and pursuit (#389). |
| `BlockRecord`, `GuardOutcome`, `Blocked`, `Staggered`, `DamageOverTime` | `Combat/Blocking.cs`, `DamageOverTime.cs` | Guards, parry, hit reactions (`[Experimental("SAGE0127")]`) and the `damage` execution (#390). |
| `DialogueBarks`, `BarkRecord`, `Barked`, `TopicAsked` | `Narrative/Barks.cs`, `Topics.cs` | Barks by occasion, and the event a quest's `talk` objective reads (#392, #391). |
| `Quests`, `QuestChanged`, `DialogueTopics`, `Spoke` | `Narrative/` | Journal rules (since #349 `Track` and `IsTracked`, a stage's and an objective's `target`, an entry's `History`); topic answering; speech event. |

Vocabularies a game extends by attribute: `hit_delivery`, `ability_delivery`, `effect_execution`, `item_use`, `quest_objective` (`kill`, `have`, `reach`, `talk`; since #391 a reach by `volume` and a talk by `topic`), plus the base condition and action words each plugin owns (`has_tag`, `lacks_tag`, `is_alive`, `attribute`, `has_item`, `standing`, `quest`, `apply_effect`, `set_tag`, `cue`, `give_item`, `change_standing`, `start_quest`, `set_stage`, `add_topic`, `speaker`).

Console: `give`, `equip`, `unequip`, `drop`, `use_item`, `loot`, `cast`, `learn`, `hurt`, `god`, `rep`, `rep_set`, `quests`, `quest_start`, `quest_stage`, `cast_debug`, `combat_debug`. Cvar `g_interact_range` (2.5 m). Events: `Damaged`, `Died`, `Used`, `WeaponFired`, `DryFire`, `CastRefused`, `CueTriggered`, `ReputationChanged`, `QuestChanged`, `Spoke`, `Stolen`, `Traded`, `ItemBroke`, `AttributeGained`.

## 5. Data model

| Kind | Ids |
|---|---|
| Records | `attribute`, `tag`, `effect`, `gameplay_conventions`, `attribute_gain`, `damage_type`, `attack`, `spread`, `recoil`, `hit_location`, `hitboxes`, `block`, `crime`, `barks`, `behaviour_tree`, `item`, `loot_table`, `merchant`, `equip_slot`, `encumbrance`, `ability`, `cue`, `faction`, `dialogue`, `dialogue_topic`, `quest` |
| Components | `sage:attributes`, `sage:gameplay_tags`, `sage:active_effects`, `sage:melee`, `sage:weapon_state` (transient), `sage:magazine`, `sage:hitbox`, `sage:inventory`, `sage:equipment` (version 2), `sage:pickup`, `sage:container`, `sage:loot`, `sage:burden`, `sage:merchant`, `sage:abilities`, `sage:projectile`, `sage:faction`, `sage:guard`, `sage:owned_place`, `sage:squad`, `sage:barks`, `sage:dialogue`, `sage:known_topics`, `sage:quest_watch` |
| Tag | `sage:interactable` |
| Saved resources | `journal`, `reputation`, `bounty`, `loot_random`, plus `vars` and `clock` from the base |
| Prefab parts | `attributes`, `effects`, `melee`, `hitboxes`, `inventory`, `pickup`, `container`, `loot`, `merchant`, `abilities`, `faction`, `guard`, `owned_place`, `squad`, `barks`, `dialogue`, `quest_watch` |

An attribute value is saved by id and base value only; the current value is recomputed. Tags are saved by id. An item is a record and an inventory a list of ids and counts, so stacking, saving and modding are cheap; since #383 a stack may carry an `ItemInstance` (name, enchantments, condition, charges), and only a unit that differs is one; an item becomes an entity only as a `Pickup` on the ground. An attack names its `delivery`, damage, `ammo`, `magazine`, `spread`, `recoil`, `projectile`, `projectileGravity` and `projectilePierce`. `known: true` topics are everyone's and never saved.

## 6. Lifecycle and data flow

All gameplay runs in the fixed tick. Controllers write `PawnIntent` in Phase.Commands (`PlayerControlSystem`, then AI think, then `sage.combat.recoil`, which keeps the intent final). In Phase.Gameplay: `sage.combat.reload`, then `sage.combat.melee` (the attack system, which queues requests, hands landed blows to their deliveries and deals them in order), projectile and cast systems, item use, then `sage.effects.tick`, executions, and `sage.effects.deaths`, which raises `Died` once. Quest, faction and I/O bridge systems read `Died` and `Damaged` between the tick and the deaths. `sage.combat.hitboxes` runs in Phase.PrePhysics. Since 4f, also in Phase.Gameplay: `sage.items.containers` and `sage.items.burden` after item use, `sage.items.loot` between the tick and the deaths (it rolls a body's table on `Died`), and `sage.attributes.gains` and `sage.attributes.speed` after the tick; a merchant restocks when it is traded with or its shop opens (`Merchants.Ready`), not in a system. Item instances, a container's stock, a burden level, a merchant's purse and the loot stream are saved (tests: `AnInstanceRoundTripsThroughASaveInThisBuild`, `AContainerRefillsRespawnHoursAfterItWasTakenFrom_AcrossASave`, `ABurdenSurvivesASave`, `LootIsDeterministicFromTheWorldSeed`).

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
| REQ-GAME-12 | Quests shall watch for kills, items, reaching a place and talking, and a game may add objectives by attribute. | Must | Done | test: `ReachTalkAndAGamesObjectiveMoveAQuestAlong`; since #391 a reach by trigger volume and a talk by topic, test: `WalkingIntoTheNamedVolumeReachesThePlace`, `AskingTheRightSpeakerAboutATopicCounts` |
| REQ-GAME-13 | The Use interaction shall find the aimed or nearest usable thing and fire its I/O. | Must | Done | `src/Sage.Gameplay/Items/Items.cs` (`InteractionSystem`) |
| REQ-GAME-14 | Quest objectives shall include timers and failure. | Should | Done (#391) | test: ATimedStageFailsWhenTheClockRunsOut, AFailWhenConditionAndTheFailQuestActionFailAQuest, FailureAndATimerRoundTripThroughASave, AGoldenSaveWithFourQuestsHalfDoneLoadsAndCarriesOn, TheJournalKeepsTheTextItWasToldAtTheTime, AQuestItemCannotBeDroppedOrSold |
| REQ-GAME-15 | Containers and corpses shall be lootable entities, with loot tables and leveled lists. | Must | Done (#378, #379) | test: AnOwnedChestOpensByUse_AndTakingFromItIsStolen, test: ALockedContainerOpensOnlyForTheKeysCarrier, test: ABodyIsAContainer_LootedByUseWithoutTheft, test: AContainerRefillsRespawnHoursAfterItWasTakenFrom_AcrossASave, test: LeveledEntriesAskTheEnginesConditions, test: ACreaturePrefabDropsItsLootTableWhenItDies, test: LootIsDeterministicFromTheWorldSeed |
| REQ-GAME-16 | Skills and levelling, perks and traits shall be rules as data. | Must | Done (#377, #381): the base's `attribute_gain`, and the kit's `skill`, `levelling` and `perk` records (17) | test: AnAttributeGainAddsOnItsEventToWhoeverDidItWhenItsFiltersHold, test: SkillsExit_ABladeSkillRisesWithUseAndLevelsTheCharacterAcrossASave, test: APerkIsPickedAtLevelUpWhenItsPrerequisitesHoldAndAppliesItsEffects |
| REQ-GAME-17 | Shops shall move money and have merchant gold, disposition and restock. | Must | Done (#380) | test: TraderExit_BuysSellsRefusesWithAReason_RestocksAfterADay_AndKeepsItAllAcrossASave, test: TraderExit_PricesMoveWithTheBarterAttributeAndStanding, test: TraderExit_ATradeThatCannotHappenMovesNothing |
| REQ-GAME-18 | Items shall have durability, condition, repair, instances, enchantments and stack splitting. | Should | Done (#382, #383) | test: TwoDistinctInstancesOfOneRecordAreHeldApartAndPlainOnesStayPlain, test: EquippingAnInstanceAppliesItsEnchantmentsWithTheItemsOwnEffects, test: AStackSplitsOnDropAndThePickupGivesBackTheSameInstance, test: AGoldenSaveWithTwoDistinctInstancesLoads, test: AWeaponLosesConditionOnHitDealsLessAndBreaks, test: ARepairKitRestoresTheEquippedWeaponAndIsUsedUp |
| REQ-GAME-19 | Equipment slots shall be registrable from data, and weight shall have consequences. | Could | Done (#384) | test: AHelmetSlotInDataIsWornWithNoCode, test: OverweightReducesAMovementAttribute, test: AnOverweightCharacterWalksLessFarInTheSameTicks |
| REQ-GAME-20 | Combat shall have blocking, parry, knockback, hit reactions and directional attacks. | Should | Done (#359, #390): directional swings and guards (#359), and since #390 what a guard does to a blow (block, parry, guard break, stamina), knockback, stagger and a hit reaction trigger, cleave, damage over time through resistances and a faction filter for friendly fire | test: ADataOnlyGraphSwingsFourWays_EachLandingOnItsOwnClipsHit, AnAiVariesItsSwingsAndGuardsAgainstTheIncomingBlow, AGuardFacingTheBlowBlocksItForStamina_AndOneFacingAwayDoesNot, AGuardRaisedJustInTimeParries_AndTheAttackerStaggers, AGuardThatCannotPayForTheBlockBreaks, ADirectionalGuardBlocksOnlyTheSwingItFaces, AHeavyBlowKnocksBack_SetsTheReactionTrigger_AndInterruptsTheVictimsSwing, ACleaveStrikesThatManyBodiesInItsArc_NearestFirst_ButNotThroughAWall, DamageOverTimeGoesThroughResistances_AndCreditsItsSource, BlockAndHitReactionMistakesAreLoadErrors |
| REQ-GAME-21 | Crime, witnesses, bounty and faction ranks shall be supported. | Should | Done (#389) | test: StealingFromAFlaggedChestInViewOfAGuard_RaisesABountyAndThePursuit, ATheftNobodyWhoCaresSeesCostsNothing, HittingABystanderIsAssault_OnceABeating_AndBringsTheGuard, AMurderSeenByAnAllyIsReportedToTheVictimsFaction, BeingInAShutPlaceIsTrespass_OnceAVisit, TheBountyIsSaved_AndPayingItCallsOffTheGuards, ACaptainsConversationAsksAndSettlesTheBounty (ranks: the kit's, 17) |
| REQ-GAME-22 | Dialogue shall have barks, greetings, linked topics and per-speaker known lists. | Could | Done (#392) | test: AGuardBarksOnAlertAndWhenItLosesYou, BarksTakeTurnsWaitTheirCooldownAndAnswerTheBarkAction, AGreetingVariesWithReputation, ASpeakersTopicsAndTopicsSaidInTheirWordsAreLearnt, TheSandboxsWatchersBarkAndItsHermitGreetsByStanding |
| REQ-GAME-23 | Friendly-fire, self-heal, buffs, and projectile bounce and stick shall be supported. | Could | Done (#390, #393) | test: FriendlyFireIsOneFactionFilter, AnAreasAffectsChoosesWhoItReaches, AHealerHealsAHurtAllyInTheMiddleOfAFight, ACreatureBuffsItselfOnceInAFightAndDoesNotHealWhenWell, SupportAbilityMistakesAreLoadErrors, ABoltFliesPastItsOwnFactionAndHitsTheEnemyBehind, ABouncingBoltComesBackOffAWallAndHitsWhatIsBehind, AStickingArrowStaysInTheWallAndInTheBodyItStruck, StuckArrowsSurviveASave |

## 10. Open work

Milestone 9, progression and economy (epic #376), is done (2026-10-07): #377 to #384. Left from it: armour's effects are not scaled by its condition, the kit's grid does not show condition, and there is no repair command (#382); `tests/games/weapons`' helmet could now be an item (#384); `Stolen` is read by crime (#389).

Milestone 10, AI, combat and narrative depth (epic #385), is done (2026-10-07): #386 to #394. Left from it: containers inside an owned place do not inherit its ownership; there is no jail or arrest flow (a guard's arrest schedule walks up and confronts, and content hangs a dialogue on the captain with `pay_bounty`); a bounty does not decay; the Sandbox has no guard or crime demo; a quest item can still be moved into a container; hyperlinked topics are not highlighted in the kit's text; there are no barks between two NPCs and no voiced conversations; stuck arrows cannot be picked up again; healing out of combat is not chosen by the AI; a buff is judged only by whether the target lacks one of its effects.

Related: #359 4p-3 directional attack and block sets is done (sheet [10](10-animation.md), design 12 "As built (directional attacks and blocks)"): the stance, the guard and the hit window are the animation side; their effect on damage is #390's, built. #344 (4q-7) is built: the dead say nothing (`DialogueRules.Start` refuses a speaker with the conventions' dead tag) and using a body loots it (17-rpg-kit).

## 11. References

- [16-gameplay-framework](../../design/16-gameplay-framework.md): attributes and effects, the hit pipeline (#133 to #139), items, abilities, factions, dialogue, topics, quests, conventions, open vocabularies.
- [REDESIGN](../../REDESIGN.md) §4.3 (vocabularies, one condition language) and §5 phases 4e and 4f; [MAKING_A_GAME](../../MAKING_A_GAME.md) §3, §6 and §10b.
- Siblings: [05-events-and-logic](05-events-and-logic.md), [08-physics](08-physics.md), [09-navigation-and-ai](09-navigation-and-ai.md), [17-rpg-kit](17-rpg-kit.md), [13-ui](13-ui.md); parent [SRS](../SRS.md).
