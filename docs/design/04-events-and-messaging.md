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

- **Code:** `src/Sage.Engine/ECS/Events/GameEvents.cs` — `GameEvents` (the world's bus, `world.Events`),
  `EventQueue<T>`, `EventReader<T>` with its cursor, `EventIterator<T>`, and the `[GameEvent]` marker.
  Tests in `tests/Sage.Tests/World/GameEventTests.cs`; `EventProbe<T>` is the test-side reader.
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
| 2 | **Game events** | typed `struct` | Queued per schedule; each reader has a cursor | Gameplay facts between systems: `Damaged`, `Died`, `ItemPickedUp`, `SpellCast`, `TriggerEntered` | Engine plumbing; things a level designer wires |
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
PrePhysics / Physics / PostPhysics ─ physics writes TriggerEntered/Exited events
Gameplay ─ systems read events, send Damaged/Died/…, fire I/O outputs, queue structural changes
AI       ─ reads perception events, sends intents
Animation
EntityIO ─ dispatch due I/O inputs (may send events / queue structural changes)
Late
   └ at every phase end: CommandBuffer flush → structural notifications (internal now; Added/Removed queued)
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
  - `ev_stats` (queue sizes, readers, oldest event age) is **built**, in `WorldCommands`;
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
1. Game event queues + `EventReader`/`EventWriter` + declarations (TODO R4).
2. Structural notifications from `World` (with 03 step 2).
3. `EngineSignals` (with 01/05).
4. Entity I/O: attributes, generated dispatch, connections component, load-time resolution, `ent_fire` (TODO F17).
