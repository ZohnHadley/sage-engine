# 04 — Events and Messaging

## 1. Purpose and scope
How parts of the engine and game tell each other that something happened, without calling each other directly. There are **four mechanisms**, each for a different job, and rules for choosing between them. Input from devices is covered in 08; this doc covers what happens after input becomes intent.

## 2. Research basis
- Event queues delivered at a defined point keep behaviour deterministic (Nystrom, *Event Queue*): survey §3.1.
- Per-reader cursors over typed event queues (Bevy `EventReader`), and the known pitfall of mixing fixed and frame schedules: survey §2.5.
- Level-design I/O: HL1 `targetname`/`target`, Source entity I/O (flexible but string-resolved at runtime and untyped): survey §1.2, §1.3.
- Global buses as the main coupling hurt debuggability (O3DE EBus): survey §2.6.
- Unreal's event-driven, mostly idle scripts: survey §1.4.

### As built (game events, 2026-09-23 — R13)

- **Code:** `src/Sage.Simulation/ECS/Events/GameEvents.cs` — `GameEvents` (the world's bus, `world.Events`),
  `EventQueue<T>`, `EventReader<T>` with its cursor, `EventIterator<T>`, and the `[GameEvent]` marker.
  Tests in `tests/Sage.Tests/Ecs/GameEventTests.cs`; `EventProbe<T>` is the test-side reader.
- **What it replaced.** Four features had each grown their own queue with its own `Clear()` and its
  own lifetime rule in a comment (engine review 2026-09-23, item 1). Migrating them turned up that
  only two were really queues:

  | Was | Is now | Why |
  |---|---|---|
  | `CombatEvents.Damage` | `Damaged` event | A fact. `DamageInfo` is what was asked for, `Damaged` is what happened, so `Applied` moved to the event |
  | `AnimationEvents` | `AnimationEvent` event | A fact. Raised in Animation, read in Gameplay, so a reader still sees it one tick later — now by cursor rather than by comment |
  | `InteractionEvents.Interactions` | `Used` event | A fact nothing in the engine read; games and entity I/O (§3.4) will |
  | `InteractionEvents.Hovered` | `InteractionState` resource | **Not an event.** "What is in reach right now" is state: a HUD drawn a frame later must get an answer, not a change |
  | `MessageLog` | `Said` event + `MessageLog` as its reader | The simulation sends; the log is the presentation buffer that reads with a cursor and ages in display time (§3.1) |

- **Ordering rules became cursors.** Combat's "anything else that damages must run *after* this
  system, or the clear moves elsewhere" is gone: `EffectSystem` and the game's combat log each hold
  their own reader, so a hit from an ability in a later phase is simply credited on the next pass.
- **Retention.** An event lives until every registered reader has passed it, then goes. A queue with
  no readers drops at once, so a headless server sending messages nobody draws does not grow.
  `ev_maxage` (default 8 ticks) is the backstop and names the reader that is behind.
- **Pruning is end-of-schedule, never mid-phase.** Readers hold sequence numbers; moving a queue
  under a system that hasn't run yet this tick would skip events for it.
- **A reader must keep up.** `MessageLog` drains at the end of every tick as well as every frame,
  because a dedicated server never draws and would otherwise be reported as the laggard.
- **Not done here:** `Added<T>`/`Removed<T>` structural events (§3.3), entity I/O (§3.4), `ISystem`
  access declarations. A reader is asked for in a system's constructor (`world.Events.Reader<T>(this)`)
  rather than declared, until `SystemAccess` exists (03 §3.5, R16).

## 3. Concepts

### 3.1 The four mechanisms

| # | Mechanism | Payload | Delivery | Use for | Don't use for |
|---|---|---|---|---|---|
| 1 | **Structural notifications** | entity/component added/removed | Immediate *inside* the world (query caches, id index, editor); **queued** for gameplay | Reacting to things appearing/disappearing (register a physics body when `Collider` is added) | Gameplay facts ("died", "opened") |
| 2 | **Game events** | typed `struct` | Queued per schedule; each reader has a cursor | Gameplay facts between systems: `Damaged`, `Died`, `ItemPickedUp`, `SpellCast`, `Used` | Engine plumbing; things a level designer wires |
| 3 | **Entity I/O** | output → target input, optional parameter and delay | Queued by fire time, dispatched in the `EntityIO` phase | Level/quest logic built in data: button `OnPressed` → door `Open`; quest stage triggers | System-to-system code paths |
| 4 | **Engine signals** | plain C# `event` | Immediate, main thread, *outside* ticks | Rare engine callbacks: `AssetReloaded`, `RecordsReloaded`, `CVarChanged`, `WindowResized`, `WorldCreated` | Anything per-tick or per-entity |

**Rules:**
- **No immediate gameplay callbacks during a tick.** A system never calls another system, and never raises a C# event that runs gameplay code synchronously. Facts go into a queue and are read in a later phase (or later in the same phase by a system ordered after). This avoids re-entrancy and hidden ordering bugs (the problems Unity's `SendMessage` and ad-hoc delegates cause).
- **Presentation → simulation goes only through `PlayerCommand` or a command queue** (08; readiness rule 2). UI and camera code never send game events into the simulation directly.
- **Simulation → presentation:** presentation systems (sounds, particles, UI) *read* game events from the Frame schedule with their own cursors.

### 3.2 Game events in detail
- An event type is a `struct` with an `[GameEvent]` attribute: `[GameEvent] public struct Damaged { public EntityRef Target, Source; public float Amount; public RecordId DamageType; }`.
- Each world has one queue per event type **per schedule** (Fixed, Frame). Sending from a Fixed system writes to the Fixed queue.
- **Readers** are fields on systems: `EventReader<Damaged>`. Each reader keeps a **cursor** (the index of the last event it read), so:
  - a reader never sees an event twice;
  - a reader running 0 times in a frame (no fixed tick this frame) doesn't miss events. It catches up next time;
  - a Frame-schedule reader of a Fixed queue sees every tick's events even when several ticks ran in one frame.
- **Retention:** events stay until *every registered reader* has passed them. A hard cap (`ev_maxage`, default 8 ticks) drops older events with a `Log.Every` warning naming the lagging reader. This catches a disabled system that's holding memory.
- **An event carries what its reader needs, because it is a statement about the past.** Every reader is
  later than the sender — a Fixed reader by a phase, a frame-side reader by up to a frame — and by then
  the entity the event names may be gone. `Used` carries the item that was taken and where it happened
  for exactly this reason: the pickup is destroyed in the tick it is taken, so `world.TryGet<Pickup>` on
  its target found nothing and the sound it should have made was never played (11 §11). An event whose
  meaning depends on asking the world is an event that means something different depending on when it
  is read.
- **Ordering:** the order events were sent, which is deterministic because systems run in a deterministic order (03 §3.5).
- **Allocation:** each queue is a growable ring buffer of structs. Steady state allocates nothing.

### 3.3 Structural notifications in detail
- *Internally* the world calls registered listeners immediately at flush points (03 §3.5): query caches, the `PersistentId` index, subsystems that mirror data (physics bodies, 10), and the editor.
- *For gameplay*, the same changes are available as game events: `Added<T>` and `Removed<T>` (carrying the `EntityRef` and, for `Removed`, a copy of the component), plus `Spawned` and `Destroyed`. Systems read them like any other game event.
- **Built in step 3** as `World` C# events (`EntitySpawned`, `ComponentAdded`, `ComponentRemoved`, `EntityDestroyed`); the queued `Added<T>`/`Removed<T>` game events come with the event bus.
- Ordering guarantees (kept from the old `EntityContext`, enforced by `World` on top of Friflo, 03 §3.1 E5): `Spawned` comes before any `Added<T>` for that entity; the `Removed<T>` events come before `Destroyed`.

### 3.4 Entity I/O in detail
Entities placed in maps or spawned from prefabs can have **outputs** wired to **inputs** on other entities, the way Source's Hammer I/O works, but typed and checked at load.

- **Outputs** are declared by components: `[Output] OnPressed` on `Button`, `[Output] OnOpened` on `Door`. A system fires one with `ctx.IO.Fire(entity, Door.OnOpened, activator)`.
- **Inputs** are declared as static methods: `[Input("Open")] static void Open(World w, EntityRef self, in IOContext io)`. A source generator (09) builds the dispatch tables.
- **Connections** are data on the source entity (an `IOConnections` component), stored in map and prefab files:

  | Field | Example |
  |---|---|
  | Output | `OnPressed` |
  | Target | `gate_01` (a name), `!self`, `!activator`, `!caller`, or `@group_name` |
  | Input | `Open` |
  | Parameter | `"2.5"` (parsed to the input's declared parameter type at load) |
  | Delay | `1.5` s |
  | Times | `-1` (unlimited) or N |

- **Load-time resolution:** names become `EntityRef`s when the map loads (or when a prefab spawns, for prefab-local names). The input must exist on some component of the target, and the parameter must parse. Failures are logged under `LogCat.Events` with the map file, the entity name and the connection, and the connection is dropped. In the editor they show as red links.
- **Dispatch:** `Fire` enqueues `(fireTick + delay, target, input, param, activator, caller)` into a priority queue. The `EntityIO` phase (03 §3.5) dispatches everything due this tick. Inputs run inside the tick, but only from this one phase, so the ordering is still defined.
- **Late binding:** an input on a target spawned *after* map load (a quest NPC) is resolved again by name when the connection fires. It's logged at `Debug` if there's still no target.

### 3.4a As built (entity I/O, 2026-09-24, F17)
- **Inputs are a registry, not attributes.** `[Input("Open")]` and generated dispatch tables want the
  source generator (09 §3.2); until then a module registers its inputs the way it registers prefab parts
  (`engine.Inputs.Register("Open", …)`), and `Engine.Inputs` is the one list `ent_fire` and the load-time
  check consult. The design above is unchanged — only who writes the registration.
- **Outputs are names a system fires**, with no declaration: `world.FireOutput(entity, "OnUse", user)`.
  The engine fires `OnUse` (something used it), `OnStartTouch`/`OnEndTouch` (a trigger volume, from the
  physics overlaps that until now were only logged) and `OnFullyOpen`/`OnFullyClosed` (a mover arriving).
  A thing wired to `OnUse` is *usable* for that reason alone, which is what makes a door in a map work
  without a component or a tag.
  (tests: AnOutputReachesTheInputItIsWiredTo, AMoverOpensSaysSoAndShutsItselfAgain,
  AThingWiredToOnUseCountsAsSomethingYouCanUse)
- **Connections come off map keys**, written the way Hammer writes them: a key named after an output
  holds `target,input[,parameter,delay,times]`. A scene or placements document writes the same five
  fields by name in a placement's `outputs` (issue #80), checked at content load the same way (an unknown
  input is an error at the file and line) and bound by name when they fire.
  (test: AScenePlacementsOutputsAreWiredAndChecked) Nothing else is read as a wire — in particular Quake's
  bare `target`/`targetname` pair is *not*, because it says which entity but never which input, and
  guessing the input per classname is the untyped string resolution this replaces.
- **Checked at load, resolved after.** An unknown input is an error naming the map file and line, and
  the wire is dropped. Names are resolved into handles once everything in the level is spawned, so a
  wire may point either way along the file; a name that resolves to nothing is looked up again each time
  it fires (late binding), which is how a wire reaches something spawned later.
  (tests: AnInputThatDoesNotExistIsRefusedWhenTheLevelLoadsNotWhenItFires, AConnectionIsReadTheWayHammerWritesOne,
  AWireFindsATargetThatDidNotExistYet)
- **A tick delivers a wire, not a chain.** Inputs fired *during* dispatch are due on the next tick, not
  this one. That is why a chain of wires takes a tick per link — and it is what makes a wire that fires
  itself a bug a mapper can see (two deliveries a tick, for ever) rather than a hang. The `io_maxdispatch`
  budget is for the other runaway: one output wired to more things than a tick should deliver.
  (tests: AWireThatFiresItselfCannotRunAwayInsideOneTick, OneOutputWiredToTooMuchIsCutOffWithAWarning)
- **Console:** `ent_fire <name|!player> <input> [parameter] [delay]`, `io_list` (every input),
  `io_trace` (log every dispatch), `io_maxdispatch`.
- **Triggers reach the player, and that was worth checking.** The player is a *kinematic* capsule moved
  by sweeps, and a trigger volume is a *static* hull; in a physics engine those two are often not
  simulated against each other at all, which would have made "walk into a trigger" true only of falling
  crates. It works, and there is now a test for each of the two body kinds so that it keeps working.
  (tests: WalkingIntoATriggerVolumeFiresOnStartTouch, ATriggerVolumeSeesTheKinematicThingsToo)
- **Cameras (issue #80):** `CameraOn [hold seconds]` / `CameraOff` on any camera entity, firing
  `OnCameraOn` / `OnCameraOff` on a change; registered by the engine (`sage.core`) rather than by this
  plugin, like the camera itself. A cut delivered in tick N shows in the first frame after tick N. See
  06 "As built (scripted cameras from entity I/O)". (test: TheCutLandsOnTheFrameAfterTheTickThatDeliveredIt)
- **Data can fire too (issue #89):** the `fire` action — `{ "fire": "hut_door", "input": "Open",
  "parameter": "", "delay": 0 }` — sends an input through `EntityIO.FireInput` from anything that runs
  actions (a dialogue option today; relays, state machines and topics in 4b). Its target is a name looked
  up when the input arrives (late binding, `EntityIO.FireInput(name, …)` since #90), or `!subject`/`!activator` (who the action is about) or `!other`/`!self`/`!caller`
  (who is doing it); the subject arrives as the activator. It is the base's (`sage.core`), and does
  nothing but warn once in a game without this plugin (test: FireSendsAnInputThroughEntityIO). 16 "As
  built (one condition and action language)" has the rest of the language.
- **Not built:** `@group` targets, an editor link view, and the per-entity I/O history.

### 3.4b As built (time in level logic, 2026-09-30, issue #90)
- **The queue is saved.** `EntityIO` is the `entity_io` saved resource (plugin `sage.gameplay.io`,
  version 1): every input still on its way — the seconds it has left, its target by persistent id *and*
  by name (an entity a map or a placements document put there has no id), the input, the parameter, the
  activator and the caller — and every wire's `Fired` count, against its entity (id, else name) and its
  index, with the output and input as a check that it is the same wire. A save taken half-way through a
  one-second delay delivers the input on the tick it would have, and a `times: 1` wire stays spent.
  (test: ADelayedInputSavedHalfWayArrivesOnTheTickItWould_AndItsWireStaysSpent)
  (test: TheSavedQueueNamesItsTargetsByIdentityAndByName)
- **Wires come back with a save.** `IOConnections` stays transient — the wiring is the scene's, as the
  walls are a map's — but a scene's placements (and its player) are rebuilt by a load, so
  `Scenes.AfterLoad` attaches their wires again, before the resources are read so the counts have wires
  to land on. A map's or a placements document's entities are not rebuilt by a load; their counts are
  reset and then set from the save like any other.
- **Old saves load.** A save from before #90 has no `entity_io`, and loads as a world with nothing on its
  way and every wire unfired — what it would have been then. No format change.
  (test: ASaveWithoutEntityIO_LoadsWithNothingPendingAndEveryWireUnfired)
- **The clock moves at the start of the tick.** It is the world's unpaused ticks, brought up to date the
  first time anything in a tick asks (an output fired in PostPhysics, the dispatch), so a trigger's wire
  and a wire fired by an input count the same delay from the same tick; before #90 the clock moved only
  in the dispatch and a trigger's delay was one tick short (#80's note). A paused world's clock stands
  still. `EntityIO.Now` reads it. A delay due within a microsecond of a tick is due on that tick.
  (test: ATriggerWiresDelayCountsFromTheSameTickAsADispatchWires) (test: APausedWorldsDelaysWait)
- **A load replaces the resource**, as it does every saved resource, so code holds `world.IO()` for a
  call, not across ticks; the dispatch system picks up the new one and carries the cvars' settings
  (`io_trace`, `io_maxdispatch`) over.
- **By name, late.** `EntityIO.FireInput(name, input, …)` finds its target when the input arrives, so a
  delayed input reaches something spawned or respawned meanwhile; the `fire` action uses it.
  (test: AnInputSentByNameFindsATargetSpawnedMeanwhile)
- **Timers** (`sage:timer`, part `timer`, prefab `sage:logic_timer`): `interval`, `spread` (each wait is
  interval ± spread, from the timer's own saved random stream, so runs and loads draw the same waits),
  `repeat`, `running` and `remaining`; inputs `TimerStart [interval]`, `TimerStop`, `TimerReset`; output
  `OnTimer`. Counted down in the EntityIO phase before the dispatch, so a timer started on tick D fires
  on the tick an input sent on tick D with the same delay arrives, and a repeating one carries its
  overshoot and does not drift. The inputs are named for timers because the input table is global
  (`Toggle` is movers'); #91's component-routed inputs may add shorter ones.
  (test: ATimerFiresOnTheTickADelayedWireArrives_AndRepeatsWithoutDrift)
  (test: AOneShotTimerFiresOnceAndStops_AndResetStartsTheWaitAgain)
  (test: ARandomTimerIsDeterministic_AcrossRunsAndASave)
- **Tweens** (`sage:tween`, part `tween`): a transform's local position, rotation or scale to a goal
  over some seconds along an `Ease` (`Easing`, pure maths), from `TweenTo [position|rotation|scale|offset|turn]
  [x y z] [seconds] [ease]` — every word optional, the tween's own `target`, `duration` and `ease`
  standing in; no commas, because a `.map` wire is comma-separated — and `TweenStop`; output
  `OnTweenDone`. A new TweenTo starts from where the entity is. Progress (`elapsed`, `from`, `to`) is
  saved, and each tick is a pure function of it, so two runs and a run through a save agree to the bit.
  A static body is told where it went, as movers do.
  (test: TweensAreDeterministicAndSurviveASave) (test: TweenToReadsItsParameter)
  (test: ATweenTurnsAndScales_AndASecondTweenToStartsFromWhereItIs)
- **Timers, tweens and blends are the engine's** (`sage.core`), like cameras: their inputs exist in every
  game, and reach a wire when this plugin is on. (test: TimersAndTweensAreTheEngines_InAGameWithNoPlugins)
- **No allocation per tick** with timers, tweens and wires running.
  (test: TimersTweensAndEntityIOAllocateNothingPerTick)
- **Camera blends:** `CameraOn [hold [blend [ease]]]`; see 06 "As built (camera blends)".

### 3.4c As built (logic entities and bridge I/O, 2026-09-30, issue #91)
- **Inputs routed by component.** The design above says "the input must exist on some component of the
  target"; now it can. `EntityInputs.Register<T>(name, handler)` registers a handler for one component
  (one per name and component), beside the global `Register(name, handler)`. An input arriving at an
  entity runs **every** routed handler whose component the entity has, in registration order — not the
  first, so an entity that is both a mover and a branch hears `Toggle` as both, and nothing depends on
  which plugin registered first but the order they run in — and the global handler only if none did; with
  neither it is refused with a warning naming the components that take it. Every global handler works as
  before, and a game may still register a global `Open` for things that are not movers. `Takes(entity,
  name)` and `ComponentsTaking(name)` answer for tools; the ledger records a routed one as
  `name@component` (`Toggle@sage:mover`), `io_list` prints the components beside each name and the
  registry dump lists them under `components`. The load-time check is still "some plugin takes this
  name": a wire's target is found by name when it fires, so its components are not known at load. Movers'
  `Open`/`Close`/`Toggle` are routed to `sage:mover`; the timer and tween inputs stay global under their
  long names. Dispatch allocates nothing (a lookup, a walk of a small array).
  (test: AnInputNameIsRoutedToEachComponentThatTakesIt)
  (test: ARoutedInputAtAnEntityWithoutItsComponentIsRefusedByName)
- **Logic entities** (`Logic/LogicEntities.cs`, the engine's like timers; parts and prefabs of the same
  names in `engine_content/data/logic.json`):
  `sage:logic_relay` (`Trigger` → `requires` of the activator → `then` actions with the activator as
  subject and the relay as other → `OnTrigger`, handing Trigger's parameter on; `Enable`, `Disable`,
  `Toggle`), `sage:logic_counter` (`Add`/`Subtract` [n], `SetValue`, `Reset`, `GetValue`, `Enable`,
  `Disable` → `OnChanged`, `OnHitMax`, `OnHitMin`, `OnGetValue`; clamped to `min`..`max` when max > min),
  `sage:logic_compare` (`SetValue`, `SetValueCompare`, `SetCompareValue`, `Compare` → `OnEqual`, or
  `OnNotEqual` and `OnLess`/`OnGreater`; equal within 1e-5, relative above 1), `sage:logic_branch`
  (`SetValue`, `SetValueTest`, `Toggle`, `ToggleTest`, `Test` → `OnTrue`/`OnFalse`) and `sage:math_remap`
  (`SetValue` → `OnValue`, along an `Ease`, clamped by default). Each driven with `ent_fire` headless:
  (test: ARelayTriggersRunsItsActionsOnlyWhenItsConditionHolds_AndCanBeDisabled)
  (test: ACounterCountsBetweenItsLimitsAndHandsItsValueOn) (test: ACompareSaysWhichIsBigger)
  (test: ABranchRemembersAndTells) (test: ARemapMapsOneRangeToAnother)
  (test: TheLogicEntitiesAreTheEngines_WithPrefabs)
- **Outputs carry values.** `EntityIO.Fire`/`world.FireOutput` take a value (a string or a number); a
  wire with no parameter of its own hands it on, as Source's do. A whole number from -1024 to 1023 is
  written once and kept, so a counter's outputs allocate nothing after the first time each count is seen.
- **Saved.** The logic entities' state is their components' (`sage:logic_counter` and the rest), saved
  as any component is; a relay's `requires`/`then` are content, in the [Transient]
  `sage:logic_relay_script` its part adds, which a load's respawn from the prefab puts back. A counter at
  2 of 3 is at 2 of 3 after a load. No format change. (test: ACountersStateSurvivesASave) An entity a
  `.map` places has no persistent id and is not saved, as before — logic that must survive a save goes in
  a scene or placements document.
- **Conditional wires.** `Connection.Requires` (an `ICondition`, `"requires"` on a scene or placements
  `outputs` entry; a `.map` key has no room for one) is asked when the output fires, with
  `ConditionContext(world, activator, self)`; while it fails the wire sends nothing and is not counted
  toward `times`. (test: AConditionalWireFiresOnlyWhenItsConditionHolds_AndAFailureIsNotCounted)
- **Same-tick relays.** A relay with `sameTick` queues its `OnTrigger` wires that have no delay as
  same-tick deliveries: after the tick's pass the dispatch runs another pass of only those, and so on down
  the chain. Everything else is unchanged — a plain wire still takes a tick a hop — and the
  `io_maxdispatch` budget counts every delivery of the tick across passes, so a same-tick relay wired to
  itself stops at the budget with the warning. Only relays have the option: it is where a mapper builds
  chains, and a counter or branch that must be instant feeds a same-tick relay. Not saved: a save is taken
  between ticks, when nothing same-tick is left unless the budget cut it, and then it arrives next tick.
  (test: SameTickRelaysRunAChainInOneTick) (test: ASameTickRelayWiredToItselfStopsAtTheBudget)
- **Bridge I/O**, each registered by the plugin that owns it (`Sage.Gameplay/Logic/BridgeIO.cs`):
  `SetStage "quest stage"` (or a bare stage at a `quest_watch`; starts the quest first if it is not on)
  and `OnStageChanged`/`OnQuestFinished` on every entity whose `quest_watch` names the quest, the stage as
  the value and the player as the activator (quests); `StartDialogue`, routed to `sage:dialogue`, with the
  activator or else the player listening (dialogue); `GiveItem "item [count]"`, routed to
  `sage:inventory` (items); `ApplyEffect "effect [magnitude]"`, the activator as the source (attributes);
  `SetFaction "faction"`, nothing clearing it (factions).
  (test: ABridgeInputChangesAQuestStage_AndAQuestWatchSaysSo)
  (test: BridgeInputsGiveItemsApplyEffectsSetFactionsAndStartConversations)
- **Outputs from events:** `OnDeath` on the victim from `Died` (the killer as activator; system
  `sage.io.deaths`, before `sage.effects.deaths` may destroy it), `OnDamaged` from `Damaged` (the attacker
  as activator, the damage done as the value; `sage.io.damage`) and `OnPickedUp` on a pickup as the
  interaction takes it (who took it as activator).
  (test: DeathAndDamageAreOutputs) (test: APickupSaysItWasPickedUp)
- **No allocation per tick** with a timer driving a counter, a comparison, a branch, a same-tick relay, a
  conditional wire and a remap. (test: LogicEntitiesAllocateNothingPerTick)
- **For state machines (#92):** register `SetState` with `Register<StateMachine>` and fire
  `OnStateChanged` with the state as its value; a machine's `Enable`/`Disable` can be its own.

### 3.5 Engine signals in detail
`EngineSignals` (on `Engine`, 01) holds plain C# events, raised on the main thread at the start of a frame (never inside a tick):
- `AssetReloaded(AssetPath)`, `RecordsReloaded(RecordType)`;
- `CVarChanged(string name)`;
- `WindowResized(int w, int h)`, `GamepadConnected(int index)`, `GamepadDisconnected(int index)` (raised by client modules);
- `WorldCreated(World)`, `WorldDestroying(World)`;
- `ModuleInitialized(IModule)`.

Handlers are expected to be cheap: invalidate a cache, mark something dirty. Heavy work is scheduled for later.

## 4. Public API sketch

```csharp
[AttributeUsage(AttributeTargets.Struct)] public sealed class GameEventAttribute : Attribute { }

public sealed class EventWriter<T> where T : struct { public void Send(in T ev); }
public sealed class EventReader<T> where T : struct
{
    public EventIterator<T> Read();               // ref struct enumerator; advances this reader's cursor; no allocation
    public bool HasPending { get; }
}

// Systems get readers/writers through their access declaration (ISystem.Declare arrives with this event bus;
// step 4 built ISystem without it, 03 §3.5):
//   public void Declare(SystemAccess a) => a.ReadsEvents<Damaged>(out _damaged).SendsEvents<Died>(out _died);

public readonly struct IOContext { public EntityRef Activator, Caller; public IOValue Param; }

public sealed class EntityIO                       // per world resource
{
    public void Fire(EntityRef source, OutputId output, EntityRef activator);
    public void FireInput(EntityRef target, InputId input, in IOValue param, float delay = 0f); // from code or the console
}

[AttributeUsage(AttributeTargets.Field)]  public sealed class OutputAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Method)] public sealed class InputAttribute  : Attribute { public InputAttribute(string name); }

public sealed class EngineSignals
{
    public event Action<AssetPath>? AssetReloaded;
    public event Action<RecordType>? RecordsReloaded;
    public event Action<string>? CVarChanged;
    public event Action<int, int>? WindowResized;
    public event Action<int>? GamepadConnected, GamepadDisconnected;
    public event Action<World>? WorldCreated, WorldDestroying;
    public event Action<IModule>? ModuleInitialized;
}
```

## 5. Data flow (one tick)

```
Commands ─ PlayerCommand → pawn intent
PrePhysics / Physics / PostPhysics ─ physics fills trigger enter/exit *lists* (10 §"As built")
Gameplay ─ systems read events, send Damaged/Died/…, fire I/O outputs, queue structural changes
AI       ─ reads perception events, sends intents
Animation
EntityIO ─ dispatch due I/O inputs (may send events / queue structural changes)
Late
   └ at every phase end: EntityCommands flush → structural notifications (internal now; Added/Removed queued)
Frame schedule (presentation): readers with their own cursors turn events into sounds, particles, UI
```

## 6. Threading and memory
- Game event queues and I/O belong to one world and are touched only on the world's thread (main in v1).
- Jobs never send events directly. They return results that a system turns into events.
- Engine signals are main-thread only. Background loaders post completions through `JobSystem.RunOnMainThread` (02 §4.5), which raises the signal at the start of the next frame.
- All queues are pooled ring buffers. I/O's priority queue is a pooled binary heap.

## 7. File formats
I/O connections are part of map/prefab entity data (09):
```json
"io": [
  { "output": "OnPressed", "target": "gate_01", "input": "Open", "delay": 0.5 },
  { "output": "OnPressed", "target": "!self",   "input": "Lock", "times": 1 }
]
```

## 8. Errors and fallbacks
- An unknown event type read or sent without a declaration: a dev assert naming the system. Declarations are the single source for event wiring.
- A lagging reader past `ev_maxage`: the old events are dropped with a warning naming the reader.
- Bad I/O connections are dropped at load with an error (see 3.4). The rest of the map still works.
- An exception in an I/O input handler is caught per dispatch and logged with the connection; the remaining dispatches continue. In dev with a debugger attached, it breaks.

## 9. Debug and tooling hooks
- **Cvars:** `ev_maxage` and `ev_trace <EventType|*>` (DevOnly; logs sends at `Trace`) are **built**;
  `io_trace` (log every I/O dispatch at `Info`) comes with entity I/O.
- **Commands:**
  - `ev_stats` (queue sizes, readers, oldest event age) is **built**, in `WorldConsoleCommands`;
  - `ent_fire <name> <input> [param]` (fire an input from the console, like Source's `ent_fire`)
    comes with entity I/O.
- **Editor:** I/O links drawn between entities (red = unresolved). Per-entity "recent I/O" in the inspector.
- **Log category:** `Events`.

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| (old) `EntityContext.OnEntityAdded/OnEntityRemoved/OnComponentAdded/OnComponentRemoved`, now `World.EntitySpawned/ComponentAdded/ComponentRemoved/EntityDestroyed` (step 3) | Structural notifications (§3.3): immediate internal listeners + queued `Spawned`/`Destroyed`/`Added<T>`/`Removed<T>` |
| (old) `EntityContextListener` re-broadcast + `ArchetypeView.OnEnter/OnExit` | **Done (step 3):** typed Friflo queries (03); `Added<T>`/`Removed<T>` queued events later |
| `InputSystem` C# events (`OnKeyPressed`, `OnMouseDrag`, …) used by `DevCamera` | **Done (step 6):** the listener events live on `InputDevices` for **UI/editor/camera** use (08); gameplay reads `PlayerCommand` (the Sandbox's `HopSystem`) |
| `CVar`-like fields and ad-hoc delegates (none yet) | Engine signals |

## 11. v1 scope vs later
- **v1:**
  - game events with cursors + retention;
  - structural notifications (both forms);
  - engine signals;
  - entity I/O with load-time resolution, delays, `!self`/`!activator`;
  - `ent_fire`, `io_trace`.
- **Later:** `@group` targets, `times`, editor link visualisation, the per-entity I/O history, event recording for replays.

## 12. Multiplayer-later notes
Game events are simulation-internal and stay on the server. Clients get the *effects* (replicated state, cosmetic "cue" messages like UE's GameplayCues). I/O runs on the server only. The rule "presentation reads events with its own cursors" maps directly onto "client presentation reads replicated cues".

## 13. Open questions
- Should game events also be recordable for replays (Quake 3's single event queue)? Replays can be built from `PlayerCommand` recording alone (08) if the simulation is deterministic *on one machine*, which Bepu allows (survey §3.7). Event recording is only a debugging aid. Deferred.
- Should I/O inputs be allowed to run in the same tick they're fired with 0 delay (Source does) or always the next EntityIO phase? Current choice: **same tick if the source fires before EntityIO**, next tick otherwise. Documented so level designers can reason about it.

## 14. Build steps
1. ~~Game event queues + `EventReader`/`EventWriter` + declarations~~ **Done 2026-09-23** (R13; "As built (game events)").
2. Structural notifications from `World` (with 03 step 2). **Partly:** `World` raises plain C# events (`EntityDestroyed`, …); the queued form in §3.1 is not built.
3. `EngineSignals` (with 01/05). Not built.
4. ~~Entity I/O: connections, load-time resolution, `ent_fire`~~ **Done 2026-09-24** (F17; §3.4a). Left: attributes and generated dispatch, which wait for the source generator (09 §3.2, REDESIGN §3.4).
