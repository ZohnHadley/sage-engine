# 12 · Input

> Status: partly built. Keyboard, mouse and one gamepad feed data-defined actions and contexts, a frame-rate latch turns them into one `PlayerCommand` per tick, and scripted input drives the same path for automated checks. Rebinding, rumble, multiple pads and replay are not built. Owning assemblies: `Sage.Simulation` (command, latch, gating, map record), `Sage.Client` (devices and action resolution). Design doc: [08 Input](../../design/08-input.md).

## 1. Purpose and scope

Input turns hardware into intent. Devices produce raw state; data-defined actions and contexts decide which action fires and who hears it; a per-tick `PlayerCommand` is what the simulation consumes. The simulation never sees keys, only commands, so a player, an AI controller and (later) a network or a replay all drive a pawn the same way through `PawnIntent`.

It deliberately does not do: game UI and focus navigation inside screens (the UI sheet, [13](13-ui.md); UI reads `Menu*` actions), mouse picking for strategy games (Stage D), or the physics of movement ([08](08-physics.md)).

## 2. Responsibilities

- Poll keyboard, mouse and gamepad, with edge detection and exact-match chords.
- Resolve `input_map` records into per-context bindings: keys, mouse buttons and delta and wheel, pad buttons, triggers and sticks, and composites such as WASD.
- Route input by context (Editor, Console, UI, Gameplay, highest priority first) with consumption, so a click does not reach both a screen and the game.
- Handle focus loss and return without stuck keys or false presses.
- Latch taps shorter than a tick and accumulate view angles at frame rate.
- Offer scripted input (console-driven actions) through the same chain as a device.
- Provide `PlayerInput`, the command resource, and `PlayerControlSystem`, which writes it into `PawnIntent`.

Not responsible for: what an action means (gameplay reads action names from the conventions record), AI control (it writes `PawnIntent` itself), saving player bindings (planned, #328).

## 3. Placement and dependencies

The data and logic that need no device are in `Sage.Simulation/Input/`: `PlayerCommand`, `ActionRegistry`, `ActionMask`, `CommandLatch`, `PlayerInput`, `FocusPolicy` and `InputEdges`, `InputBinding` and `InputMapRecord`, `Pawn`, `PawnIntent`, `PlayerControlSystem`. The MonoGame-facing parts are in `Sage.Client/Input/`: `InputDevices`, `KeyboardListener`, `MouseListener`, `InputActions`. Tests cannot reference `Sage.Client`, so the testable seams live in Simulation by design.

Plugin ids: `sage.client` registers the `input_map` record and the client's own actions (Look, Menu, ToggleConsole, EditorMove, the `Menu*` set). The simulation's gameplay actions (Move, Jump, Attack and so on) are registered by gameplay modules; games add maps for their own. The engine's maps are in `engine_content/data/input.json`.

## 4. Interfaces

| Type | Role | File |
|---|---|---|
| `ActionRegistry` | Registers named actions of kind Button, Axis1D or Axis2D; up to 128 buttons | `src/Sage.Simulation/Input/PlayerCommand.cs` |
| `ActionMask` | Bit set of button actions for held, pressed and released | same |
| `PlayerCommand` | One tick: tick number, `Move`, view yaw and pitch, held, pressed, released | same |
| `PlayerInput` | World resource with the tick's command, `HasCommand` (false headless), and `RequestView` for teleports and respawns | same |
| `CommandLatch` | Accumulates frame input between ticks; `AddFrame`, `AddLook`, `SetView`, `Sample(tick)` | same |
| `InputContext` | Editor, Console, UI, Gameplay | same |
| `FocusPolicy`, `InputEdges` | Focus-loss handling; edges from raw state, trigger hysteresis | `src/Sage.Simulation/Input/InputGating.cs` |
| `PawnIntent`, `PlayerControlSystem` | The pawn's intent; the player's command becomes intent in `Phase.Commands` | `src/Sage.Simulation/Input/PawnIntent.cs` |
| `InputActions` | Resolves bindings per context, scripted input, the console verbs | `src/Sage.Client/Input/InputActions.cs` |
| `InputDevices`, `GamepadListener` | Device polling (one pad) | `src/Sage.Client/Input/InputDevices.cs` |

Cvars: `m_sensitivity` and `m_invert_y` (archived). Commands: `bindlist` and `in_contexts` for inspection; the developer-only scripted set `in_tap`, `in_hold`, `in_axis`, `in_look`, `in_release`, `in_clear`, `in_scripted`, `in_type` and `in_cursor`. There is no `bind` or `unbind` command yet, and no `in_showactions` (#332).

## 5. Data model

| Declaration | Kind | Holds |
|---|---|---|
| `input_map` | Record | `Context` and `Actions`: a list of `InputBinding` per action name |
| `InputBinding` | Nested type | One of `Key`, `Mouse`, `Gamepad` or `Composite`, plus `Scale`, `Deadzone` (default 0.2), `Invert`, `Rate` |
| `sage:pawn`, `sage:pawn_intent` | Components | Marks a controllable body; its per-tick intent |
| `sage:player_controlled` | Tag | The local player's pawn |

Several maps may target one context (engine, game, mods) and their bindings add up. The engine ships an `editor` map and gameplay and UI maps. Nothing in input is saved; `PlayerInput` is per tick state.

## 6. Lifecycle and data flow

Per frame, the host polls devices, `InputActions` evaluates bindings for the active contexts (raw pass, then consumption by priority) and feeds `CommandLatch.AddFrame` and `AddLook`. Before each fixed tick the host samples the latch into `PlayerInput.Command`. In `Phase.Commands`, `sage.character.player_control` copies it into the `PawnIntent` of every player-controlled pawn; AI think runs after it. Contexts are switched by the host and screen system (`SetActive`): the editor and play-in-editor, the console, the UI stack and gameplay.

Look is applied at frame rate; a request to face a direction (teleport, respawn, `info_player_start`) goes through `PlayerInput.RequestView` because the latch owns the angles. Scripted input is injected after bindings are evaluated, so it takes the real chain. Registrations (actions, maps) are sealed after `Init` like other registries.

## 7. Threading, memory and performance

All input runs on the main thread. A tap shorter than a tick is never lost, because pressed and released bits accumulate until a command carries them (test: Latch_KeepsATapShorterThanATick). Move is clamped to length one and pitch to 89 degrees (test: Latch_HeldAndMoveAreTheLatestFrame_MoveIsClampedToLengthOne, test: Latch_LookTurnsRightAndUp_ClampsPitch_WrapsYaw). Edges come from raw device state, never the swallowed state, which fixed a bug where closing a screen made the character swing (test: AButtonSwallowedByAScreenIsNotPressedAgainWhenItCloses). No allocation measurement exists for the client input path.

## 8. Errors and diagnostics

A binding naming an unknown key, mouse or pad button, or an action no module registered, is reported when the map loads with its id and position (`sage:gameplay Jump[0]` style). `bindlist` lists bindings per context and `in_contexts` shows which are active. Focus handling is tested headlessly (test: FocusIsReadOnTheWayInAndNeutralWhileAway, test: StartingUnfocusedIsNeutralUntilTheWindowIsOurs, test: ATriggerRestingOnItsThresholdDoesNotChatter). The client half is checked only by the smoke run; binding parsing, context masking and composites have no headless test yet (#332).

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-INP-01 | Actions shall be defined by data (`input_map`), with several maps per context adding up. | Must | Done | `InputMapRecord` in `src/Sage.Simulation/Input/PlayerCommand.cs` |
| REQ-INP-02 | Input shall be routed by context with consumption, so a UI click does not reach gameplay. | Must | Done | test: AButtonSwallowedByAScreenIsNotPressedAgainWhenItCloses |
| REQ-INP-03 | The simulation shall consume one `PlayerCommand` per tick and never raw devices. | Must | Done | `CommandLatch`, `PlayerControlSystem` |
| REQ-INP-04 | A tap shorter than a tick shall not be lost. | Must | Done | test: Latch_KeepsATapShorterThanATick |
| REQ-INP-05 | Losing and regaining window focus shall leave no stuck or phantom input. | Must | Done | test: FocusIsReadOnTheWayInAndNeutralWhileAway |
| REQ-INP-06 | Keyboard, mouse and a gamepad shall drive all actions, with dead zones per binding. | Must | Done | `src/Sage.Client/Input/InputDevices.cs` |
| REQ-INP-07 | Scripted input shall drive actions through the same path as devices. | Must | Done | `in_tap`, `in_hold`, `in_axis` in `InputActions.cs` |
| REQ-INP-08 | An editor input map shall be active in editing, above gameplay. | Should | Done | `engine_content/data/input.json` |
| REQ-INP-09 | A player shall rebind any action with `bind`, `unbind` and a persisted `user://input.json`. | Must | Not started | #328 |
| REQ-INP-10 | The game shall offer a controls screen with capture and conflict detection. | Must | Not started | #341 |
| REQ-INP-11 | Gamepads shall support rumble, several pads, glyphs and hot-swap. | Should | Not started | #331 |
| REQ-INP-12 | Prompts and glyphs shall follow the last-used device. | Should | Not started | #352 |
| REQ-INP-13 | A recorded command stream shall replay deterministically (`.sagedemo`). | Should | Not started | #333 |
| REQ-INP-14 | Opening a screen shall release the mouse and closing it recapture it without a look jump. | Must | Partial: verify and finish | #334 |
| REQ-INP-15 | Binding parsing, context masking, composites and dead zones shall be covered by tests that need no window. | Should | Not started | #332 |
| REQ-INP-16 | The kit's default input maps shall appear in the registry dump. | Could | Not started | #354 |

## 10. Open work

Milestone 5, audio and input (epic #324):

- #328 4o-4 Rebinding path: `bind`/`unbind` and `user://input.json` (P1)
- #331 4o-7 Gamepad: rumble, multiple pads, glyphs and device hot-swap (P2)
- #332 4o-8 Close the input layer's test gap, and the missing `in_showactions` (P2)
- #333 4o-9 Command recording and replay (.sagedemo) (P2)
- #334 4o-10 Mouse capture and the Menu/gameplay cursor handoff (P2, bug)

Milestone 6, game UI and menus (epic #337), where the player-facing half lives:

- #341 4q-4 Controls screen: rebind keys and buttons (P1)
- #352 4q-15 Input glyphs and prompts per device (P2)
- #354 4q-17 Kit default input maps and registry dump coverage (P3)

Design doc 08 contradicts itself on whether `bind` and `user://input.json` are in v1 (§3 says not yet, §11 lists them); the code agrees with §3. Refreshing it is part of #328.

## 11. References

- [Design 08](../../design/08-input.md): devices, actions, contexts, the command, scripted input, "Not yet" lists.
- [REDESIGN](../../REDESIGN.md) §5, phase 4o; ARCHITECTURE §4.9 for the multiplayer-readiness rules (commands as input).
- Siblings: [Physics](08-physics.md), [UI](13-ui.md), [Audio](11-audio.md); parent [SRS](../SRS.md).
