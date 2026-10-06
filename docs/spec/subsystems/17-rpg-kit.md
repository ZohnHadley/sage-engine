# 17 · RPG kit

> Status: partly built. The Daggerfall conventions, spellmaker, readied spell and the RPG screens exist and are tested; since phase 4q (2026-10-06) also the title, pause, save, load and options screens, loot and shop reachable from play, drag and drop in the grid, the map's picture and fog, the journal's history, and default keys. Progression, real shops and containers as records are designed (phase 4f). Owning assemblies: `Sage.Kits.Rpg`, `Sage.Kits.Rpg.Client`. Design doc: [16-gameplay-framework.md](../../design/16-gameplay-framework.md).

## 1. Purpose and scope

The RPG kit holds the rules that the action-RPG family shares and that are still genre decisions: a
spell readied once and fired by one button, a spellmaker that writes the player's own spells into a
book, two hands to hold things in, and the screens an RPG is played through (bag, equipment, loot, shop,
topics, journal, map, rest and travel, mods, and the menus round a playthrough: title, pause, save, load and options).

The owner's rule governs the split. **RPG rules live in the kit, generic hooks live in the base, content is
data.** The base has abilities, items, quests, dialogue, factions and time passing ([16](16-gameplay.md));
the kit says what Daggerfall does with them. A second game of another genre uses the same base with no kit.

The kit does not decide how a screen is drawn (that is [13](13-ui.md)'s widgets and layouts), does not own
any save format (it declares saved resources and components like any plugin), and carries no game content
beyond its own screens, layouts, styles and string table.

## 2. Responsibilities

- Register the kit's equipment slots, `MainHand` and `OffHand`, which are Daggerfall's two hands. The base registers none.
- Fire the readied spell from the `Cast` button through the base's ability queue, so gates, wind-up and refusals are the base's.
- Compose, price, name and store player-made spells (`Spellmaker`, the saved `Spellbook`), and rebuild them as records after a load.
- Provide the RPG screens as view-models over base data: inventory grid, equipment, loot, shop shell, topics, journal, map, rest, and the mods screen; and the title, pause, save and load menus (#342) and the options screen's layout and settings (#339).
- Open loot from play: a `use_screen` part on a container, and a body with an inventory (#344).
- Keep what the player has explored of each scene's map (`area_map`, the saved `map_discovery`, #349).
- Bind its screens' buttons to default keys in its own `input_map` records (#354).
- Provide the rest rule (`Rest`) over the base's `Time.Pass`, and fast travel from the map over the base's `Travel`.
- Read the game's words from one optional `rpg_conventions` record.
- Ship its screens and strings as content under the record namespace `rpg`, patchable by any game or mod.

Not responsible for: the ability, item, quest and dialogue models, effects, combat or time (the base,
[16](16-gameplay.md)); widget drawing, fonts and focus ([13](13-ui.md)); which key opens which screen
(the kit's defaults are records a game patches); mod loading itself ([19](19-modding.md)).

## 3. Placement and dependencies

| Item | Value |
|---|---|
| Simulation half | `src/Sage.Kits.Rpg`, namespace `Sage.Kits.Rpg`, plugin id `sage.kits.rpg` (`RpgKitModule`). |
| Client half | `src/Sage.Kits.Rpg.Client`, plugin id `sage.kits.rpg.client` (`RpgKitClientModule`). The only half that may use MonoGame types. |
| References | The simulation half depends on `AbilitiesModule`, `ItemsModule` and `UiModule`; the client half on `ClientModule` and the simulation half. |
| Referenced by | Games only, through `<SageKit Include="sage.kits.rpg" />` and `"kits"` in `game.json` (SAGE0114 if they disagree). Never by the base: SAGE0025 forbids it. |
| Loaded | Only when a game names it; it is not in `BasePlugins.All()`. The client half loads with it in a host that has a window. |
| Content | `[PluginContent("rpg")]` mounts the embedded `content/` (screens, layouts, styles, `strings/en/rpg.json`) after engine content and before the game's mounts. |

The kit is declared against `sage >=0.1` with `[RequiresPlugin]`. Its public API is in `PublicAPI.*.txt`.
The RPG screens are `[Experimental("SAGE0125")]` and the open-world additions (rest, travel in the map)
are SAGE0129 (MAKING_A_GAME §10b).

## 4. Interfaces

| Type | Role | File |
|---|---|---|
| `RpgKitModule` | The plugin; constants for the screen ids (`InventoryScreen`, `EquipmentScreen`, `LootScreen`, `TopicsScreen`, `ShopScreen`, `JournalScreen`, `MapScreen`, `RestScreen`, `ModsScreen`, `ControlsScreen`, `TitleScreen`, `PauseScreen`, `SaveScreen`, `LoadScreen`, `OptionsScreen`) and the slots | `src/Sage.Kits.Rpg/RpgKitModule.cs` |
| `ReadiedSpell`, `ReadiedSpellSystem` | `Readied`, `Ready`; the system turns a `Cast` press into the queued ability | `src/Sage.Kits.Rpg/ReadiedSpell.cs` |
| `Spellmaker`, `SpellDraft`, `Spellbook`, `SpellResult` | Price, check, compose and restore spells | `src/Sage.Kits.Rpg/Spellmaker.cs` |
| `GameplayPanels` | The row builders behind `inv` and `spells`: what a row says and why it is greyed | `src/Sage.Kits.Rpg/GameplayPanels.cs` |
| `ItemGrid`, `ItemGridView`, `InventoryView`, `LootView`, `ShopView` | Grid model and the bag, loot and shop view-models | `ItemGrid.cs`, `InventoryView.cs`, `LootView.cs`, `ShopView.cs` |
| `EquipmentView`, `TopicsView`, `JournalView`, `MapView`, `RestView` | The remaining screens' view-models (`ModsView` is the base's, in `Sage.UI`) | `src/Sage.Kits.Rpg/*View.cs` |
| `TitleView`, `PauseView`, `SaveGameView`, `LoadGameView`, `RpgMenus` | The menus round a playthrough (`rpg_title`, `rpg_pause`, `rpg_save`, `rpg_load`), asking with `UiScreenStack.Confirm` | `src/Sage.Kits.Rpg/MenuViews.cs` |
| `UseScreen`, `UseScreenPart`, `UseScreenSystem` | Using a container or a body opens its screen about it | `src/Sage.Kits.Rpg/UseScreens.cs` |
| `AreaMapRecord`, `MapDiscovery`, `MapDiscoverySystem` | A scene's map picture and fog, and where the player has been | `src/Sage.Kits.Rpg/AreaMap.cs` |
| `IPriceRule`, `StubPriceRule` | What a trade costs; the stub is the placeholder until 4f | `src/Sage.Kits.Rpg/ShopView.cs` |
| `Rest`, `RestKind` | `Can`, `EnemyNear`, `Begin`; the screen and the console share it | `src/Sage.Kits.Rpg/Rest.cs` |
| `RpgConventions` | `Of(RecordStore)` and `Of(World)`: the game's conventions or the defaults, cached per content load | `src/Sage.Kits.Rpg/RpgConventions.cs` |
| `DialogueScreen`, `JournalScreen`, `SpellmakerScreen` | Client screens registered by id (`dialogue`, `journal`, `spellmaker`) | `src/Sage.Kits.Rpg/*Screen.cs` |

**Console.** `spells`, `ready <ability>`, `inv`, `rest <hours> [wait]`, plus the spellmaker's composing
commands registered by `Spellmaker.RegisterCommands`. The registry dump lists them.

**Input actions.** The simulation half registers `Cast`, `Spellbook`, `Spellmaker`, `Journal` and `Rest`
(since #354; the client half registered the last four before). The kit's content binds them by default in
`rpg:ui` and `rpg:gameplay` (B, M, J, T); the simulation half binds `Rest` to `rpg:rest` and the client half
`Spellmaker` and `Journal` to its panel screens. A game patches the maps for other keys.

**Events and I/O.** The kit raises none of its own. It uses the base's `TimePassed`, `Travel` and ability
queue, and `StubPriceRule` reads `ItemRecord.Value`.

## 5. Data model

| Declaration | Id | Notes |
|---|---|---|
| Record `RpgConventionsRecord` | `rpg_conventions` | `spellNamespace` (default `custom`), `castAction` (`Cast`), `inventoryGrid` (`[8, 6]`), `lootScreen` (#344), and the experimental `restEnemyRange`, `restMaxHours`, `restEffect`. At most one per game; two is a load error. Checked at load: the cast action must be a registered button. |
| Record `RpgItemRecord` | `rpg_item` | The kit's fields for an item, by the item's own id: `grid`, the footprint in squares, and `icon`, its picture across that footprint (#346). Checked against the item. |
| Record `AreaMapRecord` | `area_map` | A scene's map, by the scene's id: `picture`, the ground it covers (`from`, `to`, absolute metres X and Z), `fog` (on by default) in squares of `cell` metres, and how far round the player they lift (`reveal`) (#349). |
| Saved resource `MapDiscovery` | `map_discovery` | The squares of each scene's map the player has been near (#349). |
| Component `UseScreen` | `sage:use_screen` | The screen using the entity opens; the `use_screen` prefab part adds it and makes the thing usable (#344). |
| Input maps | `rpg:ui`, `rpg:gameplay` | The kit's default keys (#354). |
| Component `ItemGridPlacements` | `rpg:item_grid` | Where each stack lies on its carrier's grid, saved. |
| Component `MapMarker` | `sage:map_marker` | `label` and `style`; content writes it as `map_marker`. |
| Saved resource `Spellbook` | `spellbook` | The drafts only, never the derived records, so a rebalanced effect still works. |
| View-model ids | `rpg_shop`, `rpg_map`, `rpg_rest`, `rpg_journal`, `rpg_title`, `rpg_pause`, `rpg_save`, `rpg_load`, ... | Screen records `rpg:inventory`, `rpg:equipment`, `rpg:loot`, `rpg:topics`, `rpg:shop`, `rpg:journal`, `rpg:map`, `rpg:rest`, `rpg:mods`, `rpg:controls`, `rpg:options`, `rpg:title`, `rpg:pause`, `rpg:save`, `rpg:load` live in the kit's `ui_screens.json`; the options screen's settings in `ui_options.json`. |

Since #349 the kit lays out the journal and the map itself. The Sandbox's own main menu of #99
(`sandbox:main_menu`, `MainMenuView`) is still registered (`ui_open main_menu`); F10 opens the kit's pause menu.

## 6. Lifecycle and data flow

The kit's `Init` registers the cast button, the two slots, the record checks and the commands, all before
registries seal. `Start` enforces the single `rpg_conventions` rule. `OnWorldCreated` adds the
`rpg.readied_spell` system in `Phase.Gameplay`, ordered before `sage.abilities.cast`, and puts `EquipSlots`
in the world's resources.

A cast press becomes the readied ability in `Abilities.Queued`; from there the base's ability system runs
it. What is readied is the base's `Abilities.Selected`, which saves already carry, so the kit adds no save
format. Loading a save restores the `Spellbook`, and its `AfterLoad` makes the spell records again.

Screens are widget screens: a view-model refreshes each frame without allocating once warm and writes back
through the base's rules (`ItemGrid.Transfer`, the equip rules, `Time.Pass`, `Travel.ToPoint`).

## 7. Threading, memory and performance

Everything runs on the simulation thread. Rows are rebuilt only when the set of things shown changes;
per-frame work moves numbers, not objects. Rest scans for enemies without allocating once the world's
scan exists. The test `RefreshingTheRpgScreensAllocatesNothing` holds the screens to this.

## 8. Errors and diagnostics

A bad `rpg_conventions` (unknown or non-button cast action, grid below one square, non-positive rest
hours, a bad spell namespace) and an `rpg_item` with no matching item are load errors with file and line,
caught by `sage validate`. Refusals the player meets (cannot afford, cannot sleep with an enemy near,
cannot compose a duplicate name) are words from the same rules the screen greys out, so the button and the
attempt agree. Log category `Console` carries the panels printed by `spells` and `inv`.

## 9. Requirements

| ID | Requirement | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-RPG-01 | The kit shall load only for a game that names it and shall never be referenced by the base. | Must | Done | test: ABaseAssemblyThatUsesAKitIsABuildError |
| REQ-RPG-02 | The kit shall provide a readied spell that a Cast button fires, through the base's ability rules. | Must | Done | test: TheCastButtonFiresTheReadiedSpell |
| REQ-RPG-03 | The kit shall provide two-hand equipment slots as a default, with the base registering none. | Must | Done | test: TheKitHasTwoHandsAndTheBaseNone |
| REQ-RPG-04 | A player shall be able to compose, price, name and keep custom spells that survive saves and rebalances. | Must | Done | `src/Sage.Kits.Rpg/Spellmaker.cs` |
| REQ-RPG-05 | The kit shall provide inventory, equipment, loot and topics screens as data over view-models. | Must | Done | test: TheEquipmentScreenPutsOnAndTakesOffThroughTheItemRules |
| REQ-RPG-06 | A game shall be able to patch the kit's screens and strings from its own mounts. | Must | Done | test: TheKitsContentIsMountedAndAGamePatchesIt |
| REQ-RPG-07 | The kit shall provide a rest and wait rule and screen over the base's time passing. | Should | Done | test: SleepIsRefusedWithAnEnemyNearAndWaitingIsNot |
| REQ-RPG-08 | The kit shall provide a journal and a map with fast travel to discovered points. | Should | Done: since #349 the map has its scene's picture under fog a save keeps, zoom and pan, and a tracked quest's targets; the journal keeps finished stages and tracks quests | test: ExploringLiftsTheMapsFogAndASaveKeepsIt, test: TheMapZoomsPansAndLaysItsPictureUnderTheMarkers, test: ATrackedQuestShowsItsTargetOnTheMap, test: TheJournalKeepsFinishedStagesAndTracksAQuest |
| REQ-RPG-09 | The kit shall provide a mods screen. | Should | Done | test: TheModsScreenTogglesAndReordersAndWritesTheListForTheNextStart |
| REQ-RPG-10 | Skills shall rise with use or XP and characters shall level, with the rules in data. | Must | Not started | #377 |
| REQ-RPG-11 | Shops shall have money, merchant gold, disposition, barter and restock. | Must | Not started | #380 (shell and `StubPriceRule` only) |
| REQ-RPG-12 | Containers, corpses and loot tables shall be entities and records, reachable from play. | Must | Partial: a container (`use_screen`) and a body with an inventory open the loot screen from Use (#344); loot tables and containers as their own records are not built | test: UsingAChestWithAUseScreenOpensItsLootAboutIt, test: ADeadBodyWithAnInventoryLoots_AndTheDeadSayNothing; #378, #379 |
| REQ-RPG-13 | Perks and traits shall be effects with prerequisites. | Should | Not started | #381 |
| REQ-RPG-14 | A main menu, pause menu and save/load slots shall exist in the kit's client half. | Must | Done: in the kit's simulation half and content (`rpg:title`, `rpg:pause`, `rpg:save`, `rpg:load`), so a headless test drives them | test: TheSandboxBootsToItsTitleAndNewGameStartsTheWorld, test: ThePauseMenuStandsTheWorldStillUntilItCloses, test: TheSaveScreenAsksBeforeOverwritingOrDeleting, test: TheLoadScreenAsksInAGameAndRefusesWhatItCannotRead |
| REQ-RPG-15 | The inventory grid shall support drag and drop, item pictures, stack split and rotate. | Should | Done | test: AMouseDragsAnItemByItsPictureTurnsItAndDropsAStackOnTheGround, test: AGamepadTurnsSplitsAndDropsAStack |
| REQ-RPG-16 | Crime, witnesses, bounty and faction ranks shall be provided. | Should | Not started | #389 |
| REQ-RPG-17 | The kit's screens and combat edge cases shall have test coverage. | Should | Partial | `tests/Sage.Tests/Kits/RpgScreenTests.cs`; gaps in #394 |

## 10. Open work

**Milestone 9, RPG progression and economy (epic #376)**

- #377 4f-1 Skills that rise with use or XP, and levelling (P1)
- #378 4f-2 Containers and corpse looting as entities (P1)
- #379 4f-3 Loot tables and leveled lists (P1)
- #380 4f-4 Real shops and barter (P1)
- #381 4f-5 Perks and traits as effects with prerequisites (P2)
- #382 4f-6 Item durability, condition and repair (P2)
- #383 4f-7 Item instances, enchantments and stack splitting (P2)
- #384 4f-8 Slots registrable from data, and weight consequences (P3)

**Milestone 6, game UI** — #342, #344, #346, #349 and #354 are built (2026-10-06). Left in the kit from them:
the Sandbox has no chest; the shop's prices are the stub until 4f; the HUD's Use prompt does not say "Loot" or
"Trade"; a conversation on Use is still the old panel screen (#350); the kit has no spellbook screen and no
pause key of its own; a split halves a stack; the map has no wheel zoom, drag pan or pad shortcuts, its fog is
drawn as squares, and quest targets are found by the first entity of a name.

**Milestone 10, AI, combat and narrative**

- #389 4r-4 Crime, witnesses, bounty and faction ranks (P2)
- #392 4r-7 Dialogue extras: barks, greetings, hyperlinked topics (P3)
- #394 4r-9 Tests for the kit screens and combat edge cases (P3)

## 11. References

- [SRS](../SRS.md) §6; sheets [16 Gameplay](16-gameplay.md), [13 UI](13-ui.md), [14 World](14-world-and-streaming.md), [19 Modding](19-modding.md).
- [design/16-gameplay-framework.md](../../design/16-gameplay-framework.md) §3.3 (abilities, spellmaker); [design/13-ui.md](../../design/13-ui.md) (the HUD, journal, map and menus).
- [REDESIGN.md](../../REDESIGN.md) §0.5 (the owner's rule), §5 (phase 4f).
- [MAKING_A_GAME.md](../../MAKING_A_GAME.md) "Kits" and §10b (experimental ids).
