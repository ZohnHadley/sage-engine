# 08 — Input

## 1. Purpose and scope
From hardware to intent:
- **devices** (keyboard, mouse, gamepad);
- **actions** defined by data;
- **input contexts** that decide who gets input (editor, console, UI, gameplay);
- the per-tick **`PlayerCommand`** the simulation consumes.

Also covered: mouse capture, text input routing, rebinding, and command recording for replays.

In `Sage.Client`, except `PlayerCommand` itself, which is simulation data in `Sage.Engine`.

## 2. Research basis
- Quake 3 turns every input into an `event_t` in one queue, which makes sessions replayable (survey §1.1).
- Players and AI drive the same body through commands (UE PlayerController/AIController possessing a Pawn; survey §2.2). Netcode sends commands, never keys (survey §5), so this is readiness rule 2.
- Input context stacks with consumption are the standard answer to "the UI and the game both reacted to the click" (Unity's Input System action maps, UE's Enhanced Input mapping contexts **[unverified detail]**).

## 3. Concepts

### 3.1 Device layer
Today's `KeyboardListener` and `MouseListener` are good device code and are kept:
- edge detection;
- exact-match chords;
- the threshold-gated drag gesture;
- the cached enum arrays;
- seeded mouse state.

A `GamepadListener` is added (sticks with dead zones, triggers, buttons, connect/disconnect). The devices are polled once per frame by the host (01 §5.2), before anything reads them. The C# device events (`OnKeyPressed`, `OnDrag`…) stay, but **only UI, editor and camera code may use them**. Gameplay never reads devices.

### 3.2 Actions
An **action** is a named input with a kind:

| Kind | Value | Examples |
|---|---|---|
| `Button` | pressed / held / released | `Jump`, `Attack`, `Use`, `Menu`, `ToggleConsole` |
| `Axis1D` | float −1..1 | `Zoom` |
| `Axis2D` | Vector2 | `Move` (WASD, left stick), `Look` (mouse delta, right stick) |

- **Bindings** are data: an **input-map record** (05 §3.5) per context.
- Several bindings per action. Composite bindings (`W/A/S/D` → `Move`). Modifiers (`Ctrl+Z` uses the existing exact-chord logic). Per-binding scale, dead zone and invert.
- **Rebinding:** the player's changes are saved to `user://input.json` as **record patches** (`"patch": true`), applied after all mods. That reuses the one merge rule instead of inventing a second one.

### 3.3 Input contexts
A stack, highest priority first:

| Context | Active when | Consumes |
|---|---|---|
| `Editor` | editor host, viewport focused | editor shortcuts, gizmo drags |
| `Console` | console open (available per 01 §3.2) | all keyboard input |
| `UI` | a menu or inventory screen is open, or ImGui wants the mouse/keyboard (`ImGui.GetIO().WantCaptureMouse/Keyboard`) | pointer + navigation + text |
| `Gameplay` | a pawn is possessed and no higher context consumed the input | gameplay actions |

Each context has its own input map. When a context handles a key or button, lower contexts don't see it that frame. That's what stops a click on an ImGui window from also firing `Attack`.

Mouse capture: when `Gameplay` is the top active context, the cursor is hidden and locked (relative mouse mode). Opening a UI context releases it.

### 3.4 `PlayerCommand`
One tick's worth of player intent:

```csharp
public struct PlayerCommand                  // Sage.Engine (simulation data)
{
    public long Tick;
    public Vector2 Move;                     // normalized, local to view yaw
    public float ViewYaw, ViewPitch;         // absolute view angles at sample time (radians)
    public ActionMask Held;                  // buttons held at sample time
    public ActionMask Pressed;               // buttons that went down at any point since the previous tick
    public ActionMask Released;              // buttons that went up at any point since the previous tick
}
```

- **Sampling:** at the start of each fixed tick (01 §5.2), before the `Commands` phase.
- **Look runs at frame rate:** every *frame*, right after the devices are polled and **before** any ticks run (01 §5.2), the `Look` axis is added to the local controller's view angles. Each command sampled that frame therefore carries up-to-date angles, and the camera rig reads the same angles in `FrameUpdate`, so aiming is smooth at any refresh rate with no frame of lag.
- **Latching:** a tap shorter than one tick (at a high frame rate) is not lost. `Pressed`/`Released` accumulate across all frames since the last tick. If no tick runs in a frame, they keep accumulating.
- **`ActionMask`:** a 64-bit mask. Action ids are assigned at boot from the registered actions (engine + framework + game). There's a limit of 64 button actions, logged at boot if exceeded.
- **Consumption:** in `Commands`, the `PlayerController` (16) turns the command into pawn intent (move, look, jump, use). AI controllers produce the same intent without a `PlayerCommand`.

### 3.5 Text input
Text typed into UI fields (the console, name entry, editor fields) comes from MonoGame's `Window.TextInput` (it handles keyboard layouts and repeat), routed to the focused widget in the top context. It is never derived from key states.

### 3.6 As built (migration step 6)
- **Code:**
  - `src/Sage.Engine/Input/PlayerCommand.cs`: `ActionRegistry` (on `Engine.Actions`), `ActionId`, `ActionMask`, `PlayerCommand`, `CommandLatch`, the `PlayerInput` world resource, and the `input_map` record;
  - `src/Sage.Client/Input/`: `InputDevices` (keyboard, mouse, the new `GamepadListener`) and `InputActions`.
- **Actions** are registered by modules in `Init`, and where they are registered follows the layer: the simulation registers them with the feature that uses them — `CharacterModule` owns `Move`/`Jump`/`Run`/`Crouch`, `CombatModule` owns `Attack`, `ItemsModule` owns `Use`, `AbilitiesModule` owns `Cast`, and `ClientModule` owns the screen ones (including `Spellmaker`). Screens read the pointer from the device layer directly, as the editor camera does — a cursor is not an action (13 "As built (the mouse)") (`MenuUp`/`MenuDown`/`MenuConfirm`/`MenuAlternate`/`MenuBack`, `Inventory`, `Spellbook`) because screens are the client's (13 "As built (screens)") — so a headless server has the same ids; `ClientModule` registers the client's own `Look`, `Menu` and `ToggleConsole`. The maps for all of them are in `engine_content/data/input.json` (`sage:console`, `sage:gameplay`).
- **Contexts:** evaluated top-down with consumption. Console (while open) consumes the whole keyboard; UI takes whatever ImGui captures (its flags from the previous frame).
- **Per frame:** the host resolves actions and feeds a `CommandLatch`. Before each tick it stores `latch.Sample(tick)` in the world's `PlayerInput`. `Menu` (Escape, gamepad Start/Back) closes the console or quits (#37); `ToggleConsole` replaces the old key event.
- **Additions:**
  - a binding option `"rate": true` (the value is per second and multiplied by the frame time, for sticks driving `Look`);
  - **scripted input** (DevOnly, 2026-09-23): `in_tap <action>` presses a button for one frame,
    `in_hold <action> <seconds>` holds one, `in_axis <action> <x> [y] [seconds]` drives an axis,
    `in_look <yaw°/s> [pitch°/s] [seconds]` turns the view at a steady rate, `in_release <action>`
    and `in_clear` hand control back, and `in_scripted` shows what is currently held. See
    "As built (scripted input)" below;
  - `bindlist` and `in_contexts`;
  - `m_sensitivity` and `m_invert_y` (Archive), which also drive the editor camera, whose look is now a plain radians-per-pixel rate (#39).
- **Fixed on the way (#40):** the keyboard and mouse listeners rolled their previous state at the *end* of `Update`, so every polled edge (`IsKeyPressed`…) read false afterwards, and Escape-to-quit never worked. They now roll at the start.
- **Not yet:**
  - an `Editor` input map (the editor camera still reads devices, §14 step 4);
  - mouse capture (it comes with a possessed pawn);
  - `bind`/`unbind` + `user://input.json` (§14 step 5);
  - `in_showactions`, `joy_deadzone` (dead zones are per binding);
  - text input stays with ImGui.

### 3.7 What counts as a press (review #60)
Three things that are not a press, each of which produced one anyway:

- **Input the window is not the target of.** The device layer is polled with the window's own
  `IsActive`, and while that is false every device reports neutral: no keys, no buttons, no cursor
  delta, no wheel. A click in somebody's browser is not an attack. Before this, the game read the
  devices whatever had focus.
- **Input something above has swallowed.** Edges are computed from the **raw** device state, not from
  the state left after consumption (3.3). Holding the attack button, opening a screen and closing it
  again used to look like a new press on the closing frame, because the filtered state had gone false
  in between and come back.
- **An analogue trigger resting on its threshold.** A trigger bound to a *button* action is read with
  hysteresis (down at 0.6, up at 0.4). `GamePadState.IsButtonDown(LeftTrigger)` is a single fixed
  threshold, and a trigger sitting on it chatters — a press every frame, which for a weapon is a swing
  every time its cooldown ends.

**The seam either side of focus is where the awkward cases are**, so it is a decision and lives in the
engine (`Sage.Engine/Input/InputGating.cs`, `FocusPolicy` and `InputEdges`) where the headless tests can
reach it — the devices themselves are in `Sage.Client` and the test project cannot see them by design:

| Frame | What happens |
|---|---|
| Focus lost | The previous state stays real, the new one is neutral, so a held key **releases once**: a player who alt-tabs mid-stride stops walking. |
| While away | Both neutral: nothing held, no edges, no deltas. |
| Focus returned | Both read from the device, so a button already down is **not** a new press and a cursor that moved across the desk is not a flick of the view. |
| First frame ever | Treated as a return, so a key already down as the game starts is not pressed on frame one. |

`in_contexts` prints whether the window is focused, and a focus change is logged under `Input` at debug
level, because "the game is ignoring my keyboard" is otherwise a mystery. Tests:
`tests/Sage.Tests/Core/InputGatingTests.cs`.

## 4. Public API sketch

```csharp
public enum ActionKind { Button, Axis1D, Axis2D }
public readonly struct ActionId { }

public sealed class InputActions                                    // client module service
{
    public ActionId Register(string name, ActionKind kind);         // engine/framework/game modules at Init
    public bool Pressed(ActionId a);   public bool Held(ActionId a);   public bool Released(ActionId a);   // this frame, top context
    public float Axis(ActionId a);     public Vector2 Axis2(ActionId a);
    public void PushContext(InputContextId c);   public void PopContext(InputContextId c);
    public void StartRebind(ActionId a, Action<Binding> done);      // for options menus
}

public sealed class InputDevices                                    // the device layer (today's listeners + gamepad)
{
    public KeyboardListener Keyboard { get; }
    public MouseListener Mouse { get; }
    public GamepadListener Gamepad { get; }
    public void Poll();                                            // once per frame, by the host
}

public sealed class CommandSampler                                  // host calls this before each fixed tick
{
    public PlayerCommand Sample(long tick);
    public ICommandRecorder? Recorder { get; set; }                 // later: replays
}
```

Input-map record:
```json
{ "type": "input_map", "id": "gameplay", "context": "Gameplay",
  "actions": {
    "Move":   [ { "composite": "WASD" }, { "gamepad": "LeftStick", "deadzone": 0.2 } ],
    "Look":   [ { "mouse": "Delta", "scale": 0.0025 }, { "gamepad": "RightStick", "scale": 3.0 } ],
    "Jump":   [ { "key": "Space" }, { "gamepad": "A" } ],
    "Attack": [ { "mouse": "Left" }, { "gamepad": "RightTrigger" } ],
    "Menu":   [ { "key": "Escape" }, { "gamepad": "Start" } ]
  } }
```

## 5. Data flow

```
frame:  InputDevices.Poll()  (keyboard, mouse, gamepad; edges computed)
        contexts resolve top-down: Editor → Console → UI(ImGui) → Gameplay; handled input is consumed
        Look added to the local controller's view angles (display rate, before ticks)
        Pressed/Released latched into the pending command
tick:   (0..N per frame) CommandSampler.Sample(tick) → PlayerCommand → Commands phase → PlayerController → pawn intent
frame:  FrameUpdate: camera rig reads the view angles + the interpolated pawn pose
```

## 6. Threading and memory
All main thread. `PlayerCommand` is a small struct; there are no allocations per frame. Context and map lookups are arrays indexed by `ActionId`.

## 7. File formats
- Input-map records (JSON, §4).
- `user://input.json`: the player's rebinds as record patches.
- Later: command recordings (`.sagedemo`): header (build, game, mods, map, seed or save) + one `PlayerCommand` per tick.

## 8. Errors and fallbacks
- An unknown key/button name in a binding: validation error for that binding (the record still loads).
- Two actions bound to the same key in one context: `Warn` at load, listing both. First registered wins.
- Gamepad disconnected: its axes read 0; a `GamepadDisconnected` engine signal lets the game pause.
- A corrupt `user://input.json`: ignored with a `Warn`; defaults are used, and the file is kept as `input.json.bad`.

## 9. Debug and tooling hooks
- **Cvars:**
  - `m_sensitivity` (a multiplier on `Look` scale);
  - `m_invert_y`;
  - `joy_deadzone`;
  - `in_showactions` (overlay of active contexts, actions, the current `PlayerCommand`).
- **Commands:** `bind <key> <action>` (writes a patch to `user://input.json`), `unbind`, `bindlist`, `in_contexts`.
- **Log category:** `Input` (context push/pop at `Debug`, binding problems at `Warn`).

## 10. Mapping from today's code
| Today (`src/...`) | Becomes |
|---|---|
| `Sage.Client/Input/InputSystem.cs` (facade, events + polling; one instance owned by `Game1` since step 3) | **Done (step 6):** `InputDevices` + `InputActions`, owned by the host and provided to modules as host services; `ToggleConsole` is an action in the `Console`/`Gameplay` maps |
| `Sage.Client/Input/KeyboardListener.cs`, `MouseListener.cs` | Kept as the device layer. (`MouseButton` is already `internal`) |
| `Game1.Update` Escape/GamePad Back checks (TODO #37) | **Done (step 6):** the `Menu` action |
| `DevCamera` WASD polling + `OnMouseDrag` look, with display-size sensitivity scaling (TODO #39) | **#39 done (step 6):** raw delta × 0.004 rad/px × `m_sensitivity`. Still on devices; the editor camera rig reads `Move`/`Look` in the `Editor` context later (§14 step 4) |

## 11. v1 scope vs later
- **v1:**
  - devices (keyboard, mouse, gamepad);
  - actions + input-map records;
  - the context stack with ImGui capture;
  - mouse capture;
  - `PlayerCommand` with latching;
  - frame-rate look;
  - text input routing;
  - `bind` + `user://input.json`.
- **Later:** an in-game rebinding UI, command recording/replay, touch.

### As built (scripted input, 2026-09-23)

- **Code:** `src/Sage.Client/Input/InputActions.cs` (the `Scripted` list and the `in_*` commands),
  `src/Sage.Engine/Core/Console/CVarRegistry.cs` (`wait` and the deferred statement queue).
- **Injected after the bindings, not instead of them.** A scripted action is written into the same
  per-frame action state a device writes, so it goes through binding → `PlayerCommand` → `PawnIntent`
  → the character controller like a keyboard does. That is the point: an automated check that takes
  a shortcut into the simulation proves the shortcut works.
- **What it replaced.** The Sandbox had grown `sandbox_autowalk` and `sandbox_autoattack`, two systems
  that wrote `PawnIntent` **directly** in the Commands phase — a readiness-rule violation (§2:
  presentation reaches the simulation only through `PlayerCommand`) that also skipped everything the
  smoke test was meant to exercise. Both are gone. It is the same shape as the `spawn` record (F31):
  a game invented a thing because the engine had no opinion, and the fix belongs in the engine.
- **`in_look` is degrees per second, not raw axis.** `Look` is a per-frame delta in radians because a
  mouse is, so a script writing it directly would turn at a rate that depended on the frame rate —
  and a check that is not reproducible is not a check. The rate is converted once and scaled by `dt`.
- **Contexts do not gate it.** Scripted input is applied after every context has had its say, so it
  works with the console open. A person driving a script wants it to run, not to be consumed by the
  window they typed it in.
- **`wait` makes it a script.** `wait [seconds]` pauses a statement list and the rest runs later,
  pumped once a frame in **real** time (a paused game still runs its script). No argument means
  "next frame". An `exec` inside a script runs where it is written — nested statements go to the
  front of the queue, not the back — and `wait_cancel` drops whatever is queued.

  ```
  sage.exe "+in_axis Move 0 1 3" "+in_look 40 0 3" "+wait 3" "+in_tap Attack" "+screenshot"
  ```

- **Not done here:** recording a session to a `.sagedemo` and playing it back (§3, "Later"), which is
  the same queue plus a per-tick `PlayerCommand` log.

## 12. Multiplayer-later notes
`PlayerCommand` is exactly what a client will send to a server each tick (with the tick number). The local controller's view angles become predicted state. Nothing about sampling changes.

## 13. Open questions
- 64 button actions enough? Probably for one pawn. Menus use UI navigation, not `PlayerCommand`. Revisit if a game needs more (e.g. Warband's many orders); those could be one `Command` action with a parameter instead.

## 14. Build steps
1. ~~Move the listeners into `InputDevices`; add gamepad~~ **Done 2026-09-22** (ARCHITECTURE §7 step 6).
2. ~~Actions + input-map records + contexts (with ImGui capture)~~ **Done 2026-09-22** (TODO R3).
3. ~~`PlayerCommand` + `CommandSampler` + latching; the `Menu` action replaces the Escape check~~ **Done 2026-09-22** (TODO R3, #37). The sampler is `CommandLatch` + the host's tick loop.
4. Editor camera rig on actions; drop display-size scaling (TODO #39).
5. `bind`/`unbind` + `user://input.json`.
6. ~~Focus gating, raw-state edges and trigger hysteresis~~ **Done 2026-09-24** (review #60, §3.7).
