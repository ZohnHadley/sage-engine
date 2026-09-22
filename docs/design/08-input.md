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
| Today (`engine/Classes/EnginClasses/...`) | Becomes |
|---|---|
| `Input/InputSystem.cs` (singleton facade, events + polling) | `InputDevices` (devices) + `InputActions` (actions/contexts) as client module services. `toggle_debug` becomes the `ToggleConsole` action in the `Console`/`Gameplay` maps (dev only) |
| `Listeners/KeyboardListener.cs`, `Listeners/MouseListener.cs` | Kept as the device layer (moved to `Sage.Client/Input`). (`MouseButton` is already `internal`) |
| `Game1.Update` Escape/GamePad Back checks (TODO #37) | The `Menu` action |
| `DevCamera` WASD polling + `OnMouseDrag` look, with display-size sensitivity scaling (TODO #39) | The editor camera rig reads `Move`/`Look` in the `Editor` context. Sensitivity is raw delta × `Look.scale` × `m_sensitivity` (radians per pixel), with no display-size factor |

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

## 12. Multiplayer-later notes
`PlayerCommand` is exactly what a client will send to a server each tick (with the tick number). The local controller's view angles become predicted state. Nothing about sampling changes.

## 13. Open questions
- 64 button actions enough? Probably for one pawn. Menus use UI navigation, not `PlayerCommand`. Revisit if a game needs more (e.g. Warband's many orders); those could be one `Command` action with a parameter instead.

## 14. Build steps
1. Move the listeners into `InputDevices`; add gamepad.
2. Actions + input-map records + contexts (with ImGui capture) (TODO R3).
3. `PlayerCommand` + `CommandSampler` + latching; the `Menu` action replaces the Escape check (TODO R3, #37).
4. Editor camera rig on actions; drop display-size scaling (TODO #39).
5. `bind`/`unbind` + `user://input.json`.
