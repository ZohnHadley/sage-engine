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

### 3.4c As built (state machines, 2026-09-30, issue #92)
- **A record and a small component (decision D4).** A `state_machine` record (`StateMachineRecord`,
  `src/Sage.Simulation/Logic/StateMachines.cs`, owned by `sage.core` like timers) is an `initial` state,
  `states` by name — each `enter` and `exit` actions, `transitions` and free `tags` — and top-level
  `transitions` from any state. `sage:state_machine` (`StateMachine`, part `state_machine: { "machine" }`)
  names the record and holds the state **by name** and the seconds spent in it; that is what a save writes
  (`"sage:state_machine": { "version": 1, "data": { "Machine": "ns:guard", "State": "alert", "TimeInState": 0.5 } }`),
  so a save survives renumbering, and a load goes on counting from where it was
  (test: StateAndTimeInItSurviveSaveAndLoad). A machine that has never run enters `initial` on its first
  tick, running its `enter` without an `OnStateChanged`.
- **Transitions:** `to` a state when every trigger it names holds — `on` (that input arrived at the
  entity), `when` (a condition of #89's language) and `after` (seconds in the state); none of them means
  at once. The state's own are tried in order, then the machine's from-any-state ones (never to the state
  it is in); **the first that matches wins**, and a machine changes at most once a tick or per input. A
  change runs the old state's `exit`, the transition's `then`, the new state's `enter`, then fires
  `OnStateChanged`. Actions and conditions are asked with the activator (who sent the input that moved
  it) as the subject and the machine as the other, so `fire` at `!self` reaches the machine.
  (test: AGuardIdlesGrowsAlertAttacksAfterATimeoutAndCalmsDownOnAnInput)
- **Timing is timers':** the machines step in the EntityIO phase after `sage.logic.timers` and before the
  dispatch, so `after: 1` fires on the 60th tick in the state (at 60 Hz) — the tick a one-second wire sent
  on the entering tick arrives — and `OnStateChanged`'s wires with no delay arrive the same tick. A change
  made by an input fires outputs that arrive next tick, as any output an input fires does.
- **Any input name.** `on` may name any input — a registered one (`Open`, `TimerStart`) or one no plugin
  registers (`Calm`). Two small hooks on `EntityInputs` make that work without a registration per name:
  `Has` also answers for names some machine's `on` uses (asked against the content being loaded,
  `RecordStore.Latest`, so a scene checked before the machines is right), which is what lets a wire or
  `ent_fire` send one; and the dispatch offers every delivered input to the machine on its target after
  the registered handler, so "no input called" is said only when neither took it. A name a machine
  listens for arriving in a state that does not is ignored quietly.
  (test: AMachineIsCheckedAtLoad_AndAWireMaySendANameAMachineListensFor)
- **`SetState <state>`** goes there at once (exit, enter, `OnStateChanged`); already there, nothing; an
  unknown state is a warning naming the nearest. **`OnStateChanged`** hands a wire with no parameter of
  its own the new state's name — Source's rule for an output's value, now `EntityIO.Fire(…, value)` /
  `world.FireOutput(…, value)` — and a wire with a parameter keeps it.
  (test: SetStateGoesThereAtOnce_AndAWireWithItsOwnParameterKeepsIt)
- **Hot reload:** the record keeps its instance and recompiles when its states change; a machine whose
  state is still there stays in it with its time and follows the new transitions; one whose state is gone
  goes to `initial` (its `enter`, an `OnStateChanged`) with a warning naming the state. A removed record
  stands its machines still, said once.
  (test: HotReloadKeepsAStateThatIsStillThere_AndSendsOneThatIsGoneToInitialWithAWarning)
- **Checked at load:** `initial` and every `to` are states (with the nearest name), names are unique
  ignoring case, `after` is a time; a transition to its own state, or from any state, with no trigger is
  a warning, because it is taken every tick.
- **The engine's**, with no plugins (test: StateMachinesAreTheEngines_InAGameWithNoPlugins), and no
  allocation per tick with machines changing state, hearing inputs and firing outputs
  (test: StateMachinesAllocateNothingPerTick).
- **For 4d:** nothing here knows guards or doors. `StateTransition` and `StateMachines.FirstTransition`
  (first match, `on`/`when`/`after`) are public so an animation graph's record — states carrying a clip
  and a blend instead of actions — steps with the same rules, animation events arriving as `on` names;
  `tags` and `StateMachines.HasTag` let a HUD or a layer read a state without knowing its name
  (test: FirstTransitionTakesTheFirstThatMatches).
- **Not built:** `during`/`update` actions each tick, per-state outputs (`enter` can `fire` instead),
  nested or parallel states, a saved activator (it is a handle, as a timer's is), and an engine prefab
  (placements cannot override a part's `machine` yet, so a game writes its own prefab with the part).

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
