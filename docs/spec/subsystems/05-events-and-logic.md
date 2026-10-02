# 05 · Events and logic

> Status: built and tested. The event bus, entity I/O, logic entities, timers, tweens, state machines and the condition/action vocabulary all work and survive saves; the vocabulary is small and several designed extras are not built. Owning assemblies: `Sage.Simulation` (namespace `Sage.Simulation`, folders `ECS/Events` and `Logic`), `Sage.Gameplay` (`Logic/BridgeIO.cs`). Design doc: [04 Events and messaging](../../design/04-events-and-messaging.md).

## 1. Purpose and scope

This subsystem is how one part of a game tells another that something happened, and how a level designer makes things happen without writing C#. It offers four mechanisms, each for a different job:

| Mechanism | For | Who uses it |
|---|---|---|
| Typed game events | A system telling later systems about a fact: `Damaged`, `Died`, `Used`. | Programmers |
| Entity I/O | A designer wiring an output of one entity to an input of another, with a delay. | Designers, mappers |
| Logic entities, timers, tweens, state machines | Level logic written as data: relays, counters, waits, eases, guard moods. | Designers |
| The condition and action vocabulary | One language for "may this happen?" and "what happens?", shared by wires, dialogue, quests, topics and state machines. | Designers, kits |

It does not do scripting in a general-purpose language (the scripting decision waits for the phase-5 samples, see [SRS](../SRS.md) §10), pathfinding or AI (sheet [09](09-navigation-and-ai.md)), or dialogue and quest content (sheet [16](16-gameplay.md)). Engine plumbing signals such as "scene loaded" are a separate, not yet built mechanism (`EngineSignals`, #282).

## 2. Responsibilities

- Keep one queue per event type per schedule, give every reader its own cursor, and drop events once every reader has passed them.
- Warn, by name, about a reader that falls so far behind that the `ev_maxage` backstop drops events.
- Deliver entity inputs deterministically: delayed wires wait for their time, ties are broken by queue order, and a runaway chain is cut at a budget.
- Save everything that is state: the pending I/O queue, wire fire counts, counters, timers, tweens and state machines.
- Check wiring when content loads, so a wire to an input that does not exist is a load error, not a silent no-op.
- Provide the open condition and action vocabularies, with ids registered by the plugin that owns what they ask about.

Not responsible for: what a particular input does (the plugin that owns the thing registers it: movers register `Open`, quests register `SetStage`), game-specific rules, or editor forms for wires (sheet [18](18-editor.md)).

## 3. Placement and dependencies

The core of this subsystem lives in `Sage.Simulation`, so a game with no plugins at all still has events, timers, tweens, state machines, logic entities and the base vocabulary. They are registered under the engine plugin (`sage.core`); wires need the entity I/O plugin `sage.gameplay.io` to run. Gameplay's bridge inputs and outputs (`BridgeIO`) are registered by the plugin that owns what each touches (`sage.gameplay.quests`, `sage.gameplay.items`, and so on), so a game without quests has no `SetStage` and a wire sending one is a load error.

The subsystem may reference `Sage.Core` only. `Sage.Gameplay` references it and adds words. Kits and games add vocabulary entries with `[Condition("id")]` or `[Action("id")]` and a `Plugin = "..."`; the generator registers them (SAGE0104 flags a mistake). Nothing here references MonoGame.

## 4. Interfaces

### 4.1 Game events

| Type (file) | Role |
|---|---|
| `GameEvents` (`ECS/Events/GameEvents.cs`) | The world's bus: `Send<T>`, `Reader<T>(owner, schedule)`, `Release(owner)`, `Stats()`. `world.Events`. |
| `EventQueue<T>`, `EventReader<T>`, `EventIterator<T>` | The queue, a system's cursor into it, and the allocation-free `foreach` over what is new. |
| `[GameEvent]` | Marks an event struct. Examples in code: `Damaged`, `Died`, `Used`, `Spoke`, `QuestChanged`, `AbilityCast`, `TimePassed`, `AnimationEvent`. |

A reader is created once, in a system's constructor, with the system as owner, so removing the system releases it. A reader names the schedule it reads (`Fixed` by default, or `Frame`), so a HUD in Overlay reads what combat sent in Gameplay. A reader that joins late starts from "now".

Commands and cvars: `ev_stats` (queues, readers, age of the oldest event), `ev_maxage` (ticks, default 8), `ev_trace` (log every send of a named type, or `*`).

### 4.2 Entity I/O

| Type (file) | Role |
|---|---|
| `EntityInputs` (`Logic/EntityIO.cs`) | The input table. `Register(name, handler)` for any entity, `Register<T>(name, handler)` to route a short name to the component that takes it (`Toggle` means a mover's on a door and a branch's on a branch). Sealed after init. |
| `EntityOutputs` | Declared output names with a description, for `io_list`, the FGD and checks. |
| `Connection` | One wire: `Output`, `Target`, `Input`, `Parameter`, `Delay`, `Times`, and an optional `Requires` condition. Targets are an entity name or `!self`, `!activator`, `!caller`. |
| `IOConnections` (`sage:io_connections`) | The component holding an entity's wires. Written in a scene or placements document as `outputs`, or in a `.map` as `"OnStartTouch" "target,input,parameter,delay,times"`. |
| `EntityIO` (saved resource `entity_io`) | The queue: `Fire`, `FireInput`, `PendingCount`, `Budget`, `Trace`. |
| `IOContext` | What a handler receives: `Self`, `Activator`, `Caller`, `Parameter`, `Number()`. |
| `World.FireOutput(...)`, `World.IO()`, `World.FindByName(...)` | The calls game code uses. |

Inputs every game has: `Kill`, `Say`, `Fire` (fire one of this entity's own outputs). Outputs: `OnStartTouch`, `OnEndTouch` from trigger volumes. Others come from the parts below and from cameras (`CameraOn`, `CameraOff`, `OnCameraOn`, `OnCameraOff`), movers, joints (`Break`, `OnBreak`), and the bridges in 4.5.

Commands and cvars: `ent_fire <name|!player> <input> [parameter] [delay]`, `io_list`, `io_trace`, `io_maxdispatch` (default 256 inputs a tick).

### 4.3 Logic entities, timers, tweens, state machines

| Part (prefab part id) | Component | Inputs | Outputs |
|---|---|---|---|
| `logic_relay` | `sage:logic_relay` | `Trigger`, `Enable`, `Disable`, `Toggle` | `OnTrigger` |
| `logic_counter` | `sage:logic_counter` | `Add`, `Subtract`, `SetValue`, `Reset`, `GetValue`, `Enable`, `Disable` | `OnChanged`, `OnHitMax`, `OnHitMin`, `OnGetValue` |
| `logic_compare` | `sage:logic_compare` | `SetValue`, `SetValueCompare`, `SetCompareValue`, `Compare` | `OnEqual`, `OnNotEqual`, `OnLess`, `OnGreater` |
| `logic_branch` | `sage:logic_branch` | `SetValue`, `SetValueTest`, `Toggle`, `ToggleTest`, `Test` | `OnTrue`, `OnFalse` |
| `math_remap` | `sage:math_remap` | `SetValue` | `OnValue` |
| `timer` | `sage:timer` | `TimerStart`, `TimerStop`, `TimerReset` | `OnTimer` |
| `tween` | `sage:tween` | `TweenTo [channel] [x y z] [seconds] [ease]`, `TweenStop` | `OnTweenDone` |
| `state_machine` | `sage:state_machine` | `SetState`, plus any name a transition lists under `on` | `OnStateChanged` |

Parameters are words separated by spaces, never commas, because a `.map` wire is comma-separated. An output that carries a value (a counter's `OnChanged`) hands it to a wire with no parameter of its own. The engine ships prefabs for the relay, counter, compare, branch, remap and timer (`sage:logic_relay`, `sage:math_remap`, `sage:logic_timer` and so on). Easing curves are in `Logic/Easing.cs`.

### 4.4 Condition and action vocabulary

`ICondition` and `IAction` (`Logic/Conditions.cs`) are open vocabularies declared with `[Vocabulary]`. An entry is written `{ "condition": "has_item", "item": "key_iron" }` or in shorthand `{ "has_item": "key_iron" }`. A mistyped id is a load error naming the nearest registered id.

| Owner | Conditions | Actions |
|---|---|---|
| Base (`sage.core`) | `all`, `any`, `not`, `var`, `weekday`, `date_between`, `time_between`, `anim_param`, `anim_finished` | `fire`, `set_var`, `add_var` |
| `sage.gameplay.items` | `has_item` | `give_item`, `take_item` |
| `sage.gameplay.attributes` | `has_tag`, `lacks_tag` | `apply_effect` |
| `sage.gameplay.quests` | `quest` | `start_quest`, `set_stage`, `finish_quest` |
| `sage.gameplay.factions` | `standing` | `change_standing` |
| `sage.gameplay.dialogue` | `speaker` | `add_topic` |

`Vars` (saved resource `vars`) holds the named numbers that `var`, `set_var` and `add_var` use. Other vocabularies in the engine follow the same declaration pattern (`ai_condition`, `quest_objective`, `effect_execution`, `item_use`, `ability_delivery`, `hit_delivery`); the registry dump lists them all.

### 4.5 Bridge I/O (gameplay)

Inputs `SetStage`, `StartDialogue`, `GiveItem`, `ApplyEffect`, `SetFaction`; outputs `OnStageChanged`, `OnQuestFinished`, `OnDeath`, `OnDamaged`, `OnPickedUp`. Systems `sage.io.quests`, `sage.io.deaths` and `sage.io.damage` read the matching game events and fire the outputs.

## 5. Data model

| Kind | Ids as declared |
|---|---|
| Records | `state_machine` (states with `enter`, `exit`, `tags`, `transitions` using `on`, `when`, `after`, `then`; top-level `initial` and `transitions`) |
| Components | `sage:io_connections`, `sage:logic_relay`, `sage:logic_relay_script` (transient), `sage:logic_counter`, `sage:logic_compare`, `sage:logic_branch`, `sage:math_remap`, `sage:timer`, `sage:tween`, `sage:state_machine` |
| Saved resources | `entity_io` (pending inputs, delays in flight, wire fire counts), `vars` |
| Prefab parts | `logic_relay`, `logic_counter`, `logic_compare`, `logic_branch`, `math_remap`, `timer`, `tween`, `state_machine` |
| Vocabularies | `condition`, `action` |

A state machine is saved by state name and seconds in the state, so a save written before a state was renamed still loads: an unknown state sends the machine to `initial` with a warning. A timer saves its random stream, so a loaded game draws the waits it would have drawn.

## 6. Lifecycle and data flow

| Piece | When it runs |
|---|---|
| Game events | Sent in any phase; queues are pruned at the end of the schedule that owns them, never mid-phase. In a world that draws, `ev_maxage` is checked at the end of each frame so a frame reader is not blamed for a catch-up. |
| `sage.logic.timers`, `sage.logic.tweens`, `sage.logic.state_machines` | Phase `EntityIO`, in that order, before `sage.io.dispatch`. |
| `sage.io.dispatch` | Phase `EntityIO`: delivers every input that is due. |
| `sage.io.triggers` | Phase `PostPhysics`: turns physics trigger enter and exit into `OnStartTouch` and `OnEndTouch`. |
| Bridge systems | Phase `Gameplay`. |

Timing is uniform on purpose: a timer, tween or `after` of N seconds started on tick D fires on the tick that an input sent on tick D with a delay of N would arrive, and undelayed wires from its output arrive that same tick. A relay can be marked same-tick, so a chain of relays runs in one tick up to the budget.

Registration happens in module `Init` (inputs, outputs, vocabulary entries by generated code); the tables are sealed afterwards, and a registration written later is build error SAGE0020. After a load, `EntityIO.AfterLoad` rebinds queued inputs: targets are found by identity, and by name when the handle is dead, so a target spawned meanwhile is still found. Wires are resolved when content loads and checked against the input table then.

## 7. Threading, memory and performance

Single-threaded on the simulation thread. Event queues grow to their high-water mark and then stop; a queue nobody reads drops immediately. Readers and iterators are allocation-free, as are condition evaluation (lists are read once at load, reasons are constants), timers, tweens, state machines, logic entities and the I/O dispatch in steady state. Handlers must not keep an `IOContext` past the call.

Measured: tests assert zero managed allocation per tick for evaluation (test: EvaluatingConditionsAllocatesNothing), for logic entities (test: LogicEntitiesAllocateNothingPerTick), for timers, tweens and I/O together (test: TimersTweensAndEntityIOAllocateNothingPerTick) and for state machines (test: StateMachinesAllocateNothingPerTick).

## 8. Errors and diagnostics

- A wire naming an input that no component takes, a mistyped condition or action id, and a transition naming a missing state are load errors with file and line, and `sage validate` reports them (sheet [06](06-assets-and-content.md)). `TweenTo` with a parameter it cannot read is a warning and the tween does not move.
- A target that does not exist yet is not an error: it is late-bound by name when the wire fires.
- A wire that fires itself, or an output wired to too much, is cut with a warning (`io_maxdispatch`; the cap on one output's fan-out).
- The `ev_maxage` warning names the reader that is behind (log category `Events`).
- Tools: `io_list` (every input with the components that take it, and every output), `io_trace`, `ent_fire`, `ev_stats`, `ev_trace`.

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-LOGIC-01 | Each event reader shall see every event of its queue exactly once, in order, with its own cursor, even when several ticks run in one frame. | Must | Done | test: AReaderSeesWhatWasSentAndThenNothing |
| REQ-LOGIC-02 | An event shall be dropped once every reader has passed it, and a queue with no readers shall not grow. | Must | Done | test: AQueueNobodyReadsDoesNotGrow |
| REQ-LOGIC-03 | A lagging reader shall be named in a warning and its events dropped at `ev_maxage`, so a disabled system never leaks memory. | Must | Done | test: ALaggingReaderIsDroppedAtMaxAgeRatherThanHoldingTheQueue |
| REQ-LOGIC-04 | Systems shall be able to react to a component being added or removed through queued `Added<T>` and `Removed<T>` events, and to engine signals (scene loaded, world created, paused). | Should | Not started | #282 |
| REQ-LOGIC-05 | An entity output shall reach its wired input after the wire's delay, deterministically, with `times` limiting how often it fires. | Must | Done | test: ADelayedWireArrivesLateAndNotBefore |
| REQ-LOGIC-06 | The pending I/O queue shall be saved, so a delayed input saved half-way arrives on the tick it would have, and a spent wire stays spent. | Must | Done | test: ADelayedInputSavedHalfWayArrivesOnTheTickItWould_AndItsWireStaysSpent |
| REQ-LOGIC-07 | A wire to an input that does not exist shall be a load error, not a runtime no-op. | Must | Done | test: AnInputThatDoesNotExistIsRefusedWhenTheLevelLoadsNotWhenItFires |
| REQ-LOGIC-08 | A wire shall be able to carry a `requires` condition, and a wire that does not fire shall not count toward `times`. | Must | Done | test: AConditionalWireFiresOnlyWhenItsConditionHolds_AndAFailureIsNotCounted |
| REQ-LOGIC-09 | A runaway wire loop shall be cut at a per-tick budget with a warning. | Must | Done | test: AWireThatFiresItselfCannotRunAwayInsideOneTick |
| REQ-LOGIC-10 | A wire shall be able to address a group of entities (a tag or class selector), and the I/O history shall be inspectable. | Should | Not started | #276 |
| REQ-LOGIC-11 | A caught failure in one input handler shall not stop the rest of the tick's dispatch. | Must | Not started | #402 |
| REQ-LOGIC-12 | The base shall provide relay, counter, compare, branch and remap entities, and, for Half-Life style puzzles, multi-source, math, random, case, template and spawner entities. | Must | Partial: first five built | test: ACounterCountsBetweenItsLimitsAndHandsItsValueOn; #281 |
| REQ-LOGIC-13 | Timers shall support repeat, a random spread that is deterministic across runs and saves, and start/stop/reset inputs. | Must | Done | test: ARandomTimerIsDeterministic_AcrossRunsAndASave |
| REQ-LOGIC-14 | Tweens shall move, turn or scale a transform along a named easing curve, from a wire, deterministically and across a save. | Must | Done | test: TweensAreDeterministicAndSurviveASave |
| REQ-LOGIC-15 | State machines shall be data records, saved by state name, and survive a record hot reload. | Must | Done | test: StateAndTimeInItSurviveSaveAndLoad |
| REQ-LOGIC-16 | State machines shall support actions that run each tick in a state, nested and parallel states, per-state outputs, and placement overrides of the `machine`. | Should | Not started | #280 |
| REQ-LOGIC-17 | Conditions and actions shall be open vocabularies registered by the owning plugin, and an unknown id shall suggest the nearest one. | Must | Done | test: AnUnknownIdSuggestsTheNearestOne |
| REQ-LOGIC-18 | The base vocabulary shall cover what a data-only game needs: chance, entity existence and distance, spawn, destroy, teleport, sound, tags, time passing, scene load, save, log and wait. | Must | Partial: a handful of base words | #275 |
| REQ-LOGIC-19 | Delays, timers and tweens shall pause with a paused world, and honour a world time scale. | Should | Partial: pause done, scale not | test: APausedWorldsDelaysWait; #283 |
| REQ-LOGIC-20 | A steady-state tick of events, I/O, timers, tweens, state machines and condition evaluation shall allocate nothing. | Must | Done | test: TimersTweensAndEntityIOAllocateNothingPerTick |

## 10. Open work

Milestone 4m, World, logic and saves (epic #274):

- #275 4m-1 Widen the base condition and action vocabulary for data-only games (P1)
- #276 4m-2 Entity I/O: group targets, editor link view and output history (P1)
- #280 4m-6 State machines: per-tick actions, nested/parallel states, per-state outputs (P2)
- #281 4m-7 Logic entities: round out the set (multi-source, math, random, case, template, spawner) (P2)
- #282 4m-8 Events: structural `Added<T>`/`Removed<T>` and `EngineSignals` (P2)
- #283 4m-9 Time scale, pause and hit-stop as world services (P2)
- #402 4m-18 Entity I/O input handler exceptions are not caught per dispatch (P2)
- #286 4m-12 Tests: hierarchy, multi-world, timers and tweens across a save (P2)

Milestone 10b, Editor part 2: #370 10b-5 Conditions and actions editor, and a `requires` form (P2), which is the editor side of REQ-LOGIC-18.

## 11. References

- [Design 04, Events and messaging](../../design/04-events-and-messaging.md): §3.2 events, §3.4 entity I/O and its build notes (§3.4a to §3.4e), §3.5 engine signals.
- [REDESIGN](../../REDESIGN.md) §4.3 Designer logic: open vocabularies, not a second language.
- [MAKING_A_GAME](../../MAKING_A_GAME.md) §5 Levels: rooms, doors and triggers; §10a for SAGE ids.
- [EDITOR](../../EDITOR.md) §11 for the wire and condition forms not yet built.
- Related sheets: [03 App and loop](03-app-and-loop.md) for phases and system ordering, [04 ECS and scenes](04-ecs-and-scenes.md) for prefab parts, [15 Saves](15-saves.md), [16 Gameplay](16-gameplay.md) for the vocabularies kits extend, [08 Physics](08-physics.md) for triggers and movers.
