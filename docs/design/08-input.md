# 08 — Input

## 1. Purpose and scope
From hardware to intent:
- **devices** (keyboard, mouse, gamepad);
- **actions** defined by data;
- **input contexts** that decide who gets input (editor, console, UI, gameplay);
- the per-tick **`PlayerCommand`** the simulation consumes.

Also covered: mouse capture, text input routing, rebinding, and command recording for replays.

In `Sage.Client`, except `PlayerCommand` itself, which is simulation data in `Sage.Simulation`.

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

A `GamepadListener` is added (sticks, triggers, buttons, connect/disconnect), one per physical pad (up to four, `InputDevices.AllPads`); `PadAssignment` says which pad drives which local player and `InputDevices.Gamepad` is the first player's (§3.8). The devices are polled once per frame by the host (01 §5.2), before anything reads them, **with the window's focus** — while it is not ours they report neutral (§3.7). A pad's *connection* is still tracked either way, because being plugged in is hardware rather than input. The C# device events (`OnKeyPressed`, `OnDrag`…) stay, but **only UI, editor and camera code may use them**. Gameplay never reads devices.

### 3.2 Actions
An **action** is a named input with a kind:

| Kind | Value | Examples |
|---|---|---|
| `Button` | pressed / held / released | `Jump`, `Attack`, `Use`, `Menu`, `ToggleConsole` |
| `Axis1D` | float −1..1 | `Zoom` |
| `Axis2D` | Vector2 | `Move` (WASD, left stick), `Look` (mouse delta, right stick) |

- **Bindings** are data: an **input-map record** (05 §3.5) per context.
- Several bindings per action. Composite bindings (`W/A/S/D` → `Move`). Modifiers (`Ctrl+Z` uses the existing exact-chord logic). Per-binding scale, dead zone and invert.
- **Rebinding:** the player's changes are saved to `user://input.json` as **record patches** (`"patch": true`), applied after all mods. That reuses the one merge rule instead of inventing a second one. *Built (#328): §3.9.*

### 3.3 Input contexts
A stack, highest priority first:

| Context | Active when | Consumes |
|---|---|---|
| `Editor` | editor host, viewport focused | editor shortcuts, gizmo drags |
| `Console` | console open (available per 01 §3.2) | all keyboard input |
| `UI` | a menu or inventory screen is open, or ImGui wants the mouse/keyboard (`ImGui.GetIO().WantCaptureMouse/Keyboard`) | pointer + navigation + text |
| `Gameplay` | a pawn is possessed and no higher context consumed the input | gameplay actions |

Each context has its own input map. When a context handles a key or button, lower contexts don't see it that frame. That's what stops a click on an ImGui window from also firing `Attack`.

Mouse capture: while the player holds a possessed pawn and nothing above `Gameplay` wants a pointer, the cursor is hidden and held at the centre (read as a relative delta), and anything that wants a pointer releases it. *Built (#334): §3.10.*

### 3.4 `PlayerCommand`
One tick's worth of player intent:

```csharp
public struct PlayerCommand                  // Sage.Simulation (simulation data)
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
- **`ActionMask`:** a 64-bit mask. Action ids are assigned at boot from the registered actions (engine + framework + game). There's a limit of 64 button actions, logged at boot if exceeded. *As built (issue #28):* two words, 128 button actions; registering the 129th throws (test: ActionMask_HoldsButtonsPast64).
- **Consumption:** in `Commands`, the `PlayerController` (16) turns the command into pawn intent (move, look, jump, use). AI controllers produce the same intent without a `PlayerCommand`.

### 3.5 Text input
Text typed into UI fields (the console, name entry, editor fields) comes from MonoGame's `Window.TextInput` (it handles keyboard layouts and repeat), routed to the focused widget in the top context. It is never derived from key states.

### 3.6 As built (migration step 6)
- **Code:**
  - `src/Sage.Simulation/Input/PlayerCommand.cs`: `ActionRegistry` (on `Engine.Actions`), `ActionId`, `ActionMask`, `PlayerCommand`, `CommandLatch`, the `PlayerInput` world resource, and the `input_map` record;
  - `src/Sage.Client/Input/`: `InputDevices` (keyboard, mouse, the new `GamepadListener`) and `InputActions`.
- **Actions** are registered by modules in `Init`, and where they are registered follows the layer: the simulation registers them with the feature that uses them — `CharacterModule` owns `Move`/`Jump`/`Run`/`Crouch`, `CombatModule` owns `Attack`, `ItemsModule` owns `Use`, the RPG kit owns `Cast` (`RpgKitModule`, the button that fires the readied spell, #27), and `ClientModule` owns the screen ones — the kit's client half the `Spellbook`, `Spellmaker` and `Journal` that open its screens, bound by the game (`games/Sandbox/content/data/input.json`). Screens read the pointer from the device layer directly, as the editor camera does — a cursor is not an action (13 "As built (the mouse)") (`MenuUp`/`MenuDown`/`MenuConfirm`/`MenuAlternate`/`MenuBack`, `Inventory`) because screens are the client's (13 "As built (screens)") — so a headless server has the same ids; `ClientModule` registers the client's own `Look`, `Menu` and `ToggleConsole`. The maps for all of them are in `engine_content/data/input.json` (`sage:console`, `sage:gameplay`).
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
- **Not yet** (as of this step, since done in phase 4o):
  - ~~an `Editor` input map~~ **done (#219)**: `editor`, with `EditorMove`, on only in the editor mode (15 §10f); the free camera's right-drag look still reads the mouse (§14 step 4);
  - ~~mouse capture~~ **done (#334, §3.10)**;
  - ~~`bind`/`unbind` + `user://input.json`~~ **done (#328, §3.9)**;
  - ~~`in_showactions`~~ **done (#332, §3.11)** and ~~`joy_deadzone`~~ **done (#331, §3.8)**;
  - text input stays with ImGui.

### 3.7 What counts as a press (review #60)
Three things that are not a press, each of which produced one anyway:

- **Input the window is not the target of.** The device layer is polled with the window's own
  `IsActive`, and while that is false every device reports neutral: no keys, no buttons, no cursor
  delta, no wheel. A click in somebody's browser is not an attack. Before this, the game read the
  devices whatever had focus — while the vendored ImGui backend had guarded `if (!_game.IsActive) return;`
  since the day it arrived, which is why the dev tools never showed the symptom and gameplay did.
- **Input something above has swallowed.** Edges are computed from the **raw** device state, not from
  the state left after consumption (3.3). Holding the attack button, opening a screen and closing it
  again used to look like a new press on the closing frame, because the filtered state had gone false
  in between and come back.
- **An analogue trigger resting on its threshold.** A trigger bound to a *button* action is read with
  hysteresis (down at 0.6, up at 0.4). `GamePadState.IsButtonDown(LeftTrigger)` is a single fixed
  threshold, and a trigger sitting on it chatters — a press every frame, which for a weapon is a swing
  every time its cooldown ends.

**The seam either side of focus is where the awkward cases are**, so it is a decision and lives in the
engine (`Sage.Simulation/Input/InputGating.cs`, `FocusPolicy` and `InputEdges`) where the headless tests can
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

### 3.8 Pads, players, response and rumble (issue #331, 2026-10-06)
- **Code:** `src/Sage.Simulation/Input/Gamepads.cs` (`PadAssignment`, `StickResponse`, `LastUsedDevice`, `InputGlyphs`), `Rumble.cs` (the `rumble` record and `RumbleMixer`), `ScriptedInput.cs`, `PlayerInput.SetCommand`, `Players.PlayerIndexOf`; in the client `GamepadListener` per pad, `InputDevices.PadFor(player)` and `ApplyRumble`; in gameplay `sage.combat.rumble` (`RumbleSystem`). Tests: `tests/Sage.Tests/Input/GamepadTests.cs`, `tests/Sage.Tests/Gameplay/RumbleTests.cs`. `Engine.Pads` and `Engine.LastDevice` hold the first two.
- **Pads are handed out to players in order** (up to four): a connected pad with no player goes to the lowest player without one, a pad no player wants is ignored until one is free, and **a pad that is unplugged and replugged returns to its own player** even when another pad was plugged in meanwhile (tests: PadsAreHandedOutToPlayersInOrder, AnUnpluggedPadReturnsToItsOwnPlayer, ANewPadTakesAFreeSlotAndPlayersCanBeReduced). `joy_players` (1 to 4, default 1) sets how many local players have a pad; `pad_list` shows who has which and `pad_assign <player> <pad>` (both from 1) hands one over by hand. A change is logged under `Input` (`Pad 2 now drives player 2`, `Pad 2 lost: player 2 has no pad`). This is what the `GamepadDisconnected` signal in §8 became: the assignment and the log, not an engine event.
- **Each player has a command.** Player 0's is `PlayerInput.Command`; `PlayerInput.SetCommand(player, command)` sets the others (`TryGetCommand` reads them), and a pawn names its player with the `sage:player_slot` component (`Index`; without it, the first). `ActionMapper.UpdateOther` evaluates a second player's pad through the `IPadInput` seam. Players 1 and up read only the `Gameplay` map's pad bindings: they have no keyboard or mouse look, and are held back while the console, a screen or the dev UI has the input (test: ASecondPlayersPawnIsDrivenByTheirOwnCommand).
- **Dead zone and curve.** `joy_deadzone` (0 to 0.9, default 0) is a floor under every stick and trigger, and a binding's own `deadzone` still applies if larger. The zone is **radial** and rescaled so the output still reaches 1; `joy_curve` shapes the length: `linear`, `quadratic`, `cubic` or an exponent from 0.1 to 8 (finer near the centre above 1) (tests: ADeadZoneIsRadialAndRescaledToReachOne, ResponseCurvesParse, BadResponseCurvesAreRefused, ACurveIsFinerNearTheCentreAndStillReachesOne). `ActionMapper` owns the evaluation (`JoyDeadzone`, `JoyCurve`).
- **The last-used device** (`LastUsedDevice`, `InputDeviceKind` `KeyboardMouse` or `Gamepad`) follows real use: a key or button is enough, a mouse needs 3 pixels of travel and a stick or trigger half its range, so a stick resting off-centre does not count; when both families act in one frame the one in use keeps it (test: TheLastUsedDeviceFollowsRealUseNotNoise). `InputGlyphs.Pick` gives the binding a prompt should show for the device in use, falling back to the other family's when an action has none (test: APromptShowsTheBindingOfTheDeviceInUse). Text shows it since #352 (§3.13).
- **Rumble is data.** A `rumble` record is `low` and `high` (the heavy and the light motor, 0 to 1), `duration`, `attack` and `release` in seconds. A `cue` and a `damage_type` name one in `rumble`: a cue's rumble is felt by the player whose entity is the cue's source (a spell they cast, a footstep of theirs), and a damage type's by the player a hit that cost health landed on (not a creature, not `god`, not a full resist) (tests: ACuesRumbleIsFeltByThePlayerWhoseEntityRaisedIt, AHitRumblesTheVictimsPadAndOnlyThatPlayers). The `RumbleMixer` (a world resource the client adds; a headless server has none and ignores rumble: AWorldWithoutAMixerIgnoresRumble) sums each player's effects, clamps each motor at 1, rises and falls on the envelope, and holds 32 effects at most (tests: TheMixerSumsActiveEffectsPerPlayerAndClamps, AnEffectRisesAndFallsAndGainScalesIt, TheMixerIsBounded). `joy_rumble` (0 to 1, default 1) scales it all; the host applies each player's motors once a frame, only when a value changed, and silences them while the window is not ours.
- **Scripted input for a player.** Every `in_*` command takes a trailing `@N` to aim at the Nth local player (`in_tap Attack @2`); with none, the first's. `ScriptedInput` holds the scripts, so a test drives a second player without a window: the last script for an action wins, and a tap is one frame (tests: AScriptForTheSecondPlayerLandsInTheirFrameOnly, ATapIsOneFrameAndTheLastScriptForAnActionWins, TheConsoleNamesAPlayerWithATrailingAt).
- **Limits:** there is no rumble content; rumble is not scaled by damage; `joy_players` is set by hand, not by how many pads are plugged in; touch is not covered; no real pad has been tried.

### 3.9 Rebinding (issue #328, 2026-10-06)
- **Code:** `src/Sage.Simulation/Input/InputRebinds.cs` (on `Engine.Rebinds`), `UserInputMount.cs`; the commands are registered by `SageApp`; the controls screen's view-model is `src/Sage.UI/ControlsView.cs` (`ui_controls`). Options: `SageAppOptions.InputFile` (the host passes `user://input.json`; a test or tool passes none and rebinds are kept nowhere), `HeadlessAppBuilder.WithUserInput(file)` in tests. Tests: `tests/Sage.Tests/Core/InputRebindTests.cs`, `tests/Sage.Tests/Kits/ControlsScreenTests.cs`.
- **A rebind is a record patch, as §3.2 said.** For each context the player has changed, the file holds an `input_map` of its own, `user:rebinds_<context>` (lower case), whose entry for an action is **the action's whole list**, and a patch on each other map that bound the action (the game's, a mod's) that empties it there. The file is mounted after the game and every mod, so the one merge rule decides and nothing here merges anything. Resetting an action deletes its lines and the game's own come back (tests: ARebindIsSavedAndTheNewKeyFiresAfterARestart, BindResetPutsTheGamesBindingsBack, EachContextHasItsOwnList).
- **Every change** validates the input, rewrites the file (a temp file, then a move), reloads the records and bumps `Version`, so the client rebuilds its bindings and the new key fires the next frame. A file that cannot be written changes nothing. A corrupt file is ignored with a warning and kept as `input.json.bad`.
- **Commands:** `bind <input> <action> [context]` adds an input (`E`, `key:E`, `mouse:Right`, `pad:A`); `unbind <input> [action] [context]` takes it off an action, or off every action in the context; `bind_reset [action|all] [context]` puts the game's own back. The context is `Gameplay`, `UI`, `Editor` or `Console`; left out, it is `Gameplay` if the action has bindings there (or has none anywhere), else the first context that has it (tests: BindAddsAnInputAndUnbindTakesItOff, TheConsoleCommandsDoTheSame).
- **A conflict** is an input another action in the context already uses. `Bind` changes nothing and returns `RebindStatus.Conflict` naming the actions (test: AnInputAnotherActionUsesIsAConflict); the caller may replace. An axis action's composite (WASD on `Move`) is never taken from it, only reported.
- **Capture** (what the controls screen does): `InputRebinds.BeginCapture(context, action)` waits, the client offers the next key, mouse button or pad button the player presses (`Offer`), and a conflict ends the capture with a `PendingRebind` for `Resolve` to replace or keep (test: CaptureNextInputSetsTheBindingFromThePress). The client never offers Escape, the left mouse button or the pad's B, Back and Start, because those cancel or click the screen's own buttons.
- **The controls screen** is the `ui_controls` view-model (every button action of a context with its inputs, Rebind, Reset, Reset all, a tab per context, Replace and Keep on a conflict, Back cancels a capture before it closes the screen) and the RPG kit's `rpg:controls` screen and `rpg.controls.*` strings; a game makes its own layout over `ui_controls`. This is issue #341, "in-game rebinding UI", which shipped with #328 (tests: PressingAKeyAfterRebindSetsTheBindingAndItSurvivesARestart, AConflictAsksToReplaceOrKeepAndBackCancels, ResetAndTheContextTabs).
- **Limits:** axis actions (`Move`, `Look`) are not rebindable, only button actions; the Sandbox has no menu button that opens the controls screen; conflicts are per context.

### 3.10 Mouse capture (issue #334, 2026-10-06)
- **Code:** `src/Sage.Simulation/Input/MouseCapture.cs` (`MouseCaptureInputs`, `MouseCapturePolicy`, `MouseCaptureFrame`, `MouseCaptureTracker`), `MouseListener.ApplyCapture` and `Captured`, and the host. Tests: `tests/Sage.Tests/Core/MouseCaptureTests.cs`. It did not exist before: the cursor was always visible.
- **The decision is the engine's, the client only applies it.** The cursor is held (hidden, warped to the centre, read as a delta from there) when the cvar `m_capture` (archived, default on) is on, the window is ours and the player's world has a pawn the player controls, and **released** for each thing that wants a pointer: an open screen, the console, the controls screen while it waits for an input (a right click is a candidate binding), the developer UI, losing focus and the editor viewport (tests: ThePossessedPlayerHoldsTheCursor, EachThingThatWantsAPointerReleasesIt, ScreenOpenThenClosedGoesThroughThePolicy). `MouseCapturePolicy.Why` says the first reason, and the host logs `Mouse captured` and `Mouse released (reason)` under `Input`.
- **Recapturing does not jump the look.** The frame that takes the cursor back warps it to the centre and drops that frame's delta, and so does regaining focus, and so does the window's centre moving (a resize), since the distance from the old centre is not a look either (tests: RecapturingAfterAScreenDoesNotJumpTheLook, RegainingFocusDoesNotJumpTheLook, AMovedMiddleDropsThatFramesDelta).
- **CI** checks it: the smoke run's `SAGE_SMOKE_EXPECT` (one regex per line, each matched against the log in order, next to `SAGE_SMOKE_COMMANDS`) has the Sandbox log `Mouse captured` while the player looks around.
- **Limits:** the dev ImGui windows are clickable only with the console open while `m_capture` is 1; the editor viewport is never captured and play-in-editor captures.

### 3.11 Testing the binding layer (issue #332, 2026-10-06)
- **Code:** `src/Sage.Simulation/Input/ActionMapper.cs` (internal), which was the body of the client's `InputActions`: it compiles bindings, evaluates the contexts top-down with consumption, composites, rate bindings, dead zones and the #60 edge rules. It knows no device: `IInputVocabulary` names keys and pad buttons, `IInputState` (over `IPadInput`) reads the devices, and the client implements both. `InputActions` is the device adapter plus the console verbs. Tests: `tests/Sage.Tests/Core/ActionMapperTests.cs`.
- **Covered headlessly:** binding text that does not parse says why (ConsoleBindTextThatDoesNotParseSaysWhy), malformed bindings are rejected with a reason (MalformedBindingsAreRejectedWithAReason, ABindingNeedsExactlyOneSource), a displaced context does not hear what a higher one binds and an open console swallows the keyboard but not the mouse (ADisplacedContextDoesNotHearWhatAHigherOneBinds, AnOpenConsoleSwallowsTheWholeKeyboardButNotTheMouse), rate bindings scale by frame time and others do not (RateBindingsScaleByFrameTimeAndOthersDoNot), WASD cancels opposites and a composite and a stick add up (WasdCompositeResolvesAndCancelsOpposites, ACompositeAndAStickAddUp), and the #60 regressions (RegressionClosingAScreenWithAButtonHeldIsNotANewPress, RegressionAPressStartedWhileSwallowedStillReleasesLoudly, RegressionARestingTriggerDoesNotChatter).
- **`in_showactions [on|off]`** prints the live action state (every action held or off-centre) now, or, `on`, logs it whenever it changes. It is a log, not an overlay.
- **Limits:** #40 (the listeners' edge-rolling) is checked only in the client; `ActionMapper.Rebuild(RecordStore)` has no direct test.

### 3.12 Command recording and replay (issue #333, 2026-10-06)
- **Code:** `src/Sage.Simulation/Input/Demos.cs` (`Demos` on `Engine.Demos`, `DemoSystem`, `WorldHash`) and `DemoFile.cs` (the format). Tests: `tests/Sage.Tests/Content/DemoTests.cs`.
- **Commands:** `record <name>` writes a save of now and then each tick's `PlayerCommand` to `user://demos/<name>.sagedemo` until `stop`; `playdemo <name>` loads the demo's start and feeds the commands back; `demos` lists what is in `user://demos`; `world_hash [world]` prints the hash of a world's saved state. One recording or playback at a time, and one world (test: ARecordingRefusesAnotherUntilStopped).
- **`DemoSystem`** (`sage.input.demo`, `Commands` phase, before `sage.character.player_control`) records or injects the command of the tick.
- **It proves itself.** `stop` writes the world hash after the last tick; playback compares it at its last tick and says whether the demo played true. `WorldHash` is FNV-1a over 64 bits of the world's save state, so two worlds with the same hash are, as far as a save can say, the same (tests: ARecordedSessionReplaysToTheSameWorldHash, TheWorldHashFollowsTheSavedState, ADemoRecordedAtLaunchReplaysInTheSandbox).
- **The file** is the magic `SAGEDEMO` and a version (1), a JSON header (build, game, plugins, mods, actions in registration order, world, scene, the step, the tick and time it began at), the embedded start save (so it is one file to pass around), one record per tick (a full command, the same as the tick before, or none), and an end record with the tick count and the hash. A demo from another build, game, plugin set, mod set or action list is refused with what differs (test: ADemoFromAnotherBuildOrWithOtherModsIsRefused). A file without its end was cut short: what is whole plays and the hash is not checked (test: ATruncatedDemoPlaysWhatIsWholeOrIsRefused).
- **Limits:** replay is exact only when the save holds everything the simulation reads. The real Sandbox client does not replay exactly: the camera rig's transform is written at frame rate and saved, `FaceCameraSystem` reads `ActiveCamera`, and `HopSystem` keys off the tick and a runtime entity id. There is no timedemo or demo loop. A follow-up issue is a candidate.

### 3.13 Glyphs in text, the kit's keys and two menu buttons (issues #352, #354, #346; 2026-10-06)
- **Glyphs in text (#352).** `{action:Name}` in any localised text is the glyph of the action's first binding for
  the device in use (`InputGlyphs.Pick`, the player's rebinds included), named by `@input.<glyph key>` when a
  string table has that key (`key.E`, `mouse.Left`, `xbox.A`, `playstation.A`; `InputGlyphs.GlyphKey`) and by its
  built-in name otherwise (`InputGlyphs.DefaultName`). The pad family (`PadFamily`, Xbox or PlayStation) is the
  client's guess from the pad's name (`InputGlyphs.FamilyOf`, kept on `LastUsedDevice.Pad`), and the archived
  `joy_glyphs` (`auto`, `xbox`, `playstation`) overrides it
  (test: EachPadFamilyNamesItsButtonsAndAPadsNameSaysWhichItIs)
  (test: AnActionPlaceholderShowsTheGlyphOfTheDeviceInUse). `InputPrompts` (`Sage.UI`) does it; 13 "As built
  (input glyphs in prompts)" has the rest.
- **A kit's default keys (#354).** The RPG kit registers `Spellbook`, `Spellmaker`, `Journal` and `Rest` in its
  simulation half and binds them in its own `input_map` records, `rpg:ui` and `rpg:gameplay` (B, M, J, T); maps
  add up per context, so a game patches them or adds its own
  (test: ABareKitGameHasTheKitsDefaultKeysInBothContexts) (test: ADefaultMapIsPatchedByAGameLikeAnyRecord).
- **Two menu buttons (#346).** The `ui` map gained `MenuRotate` (R, the left shoulder) and `MenuSplit` (F, the left
  trigger), which reach a screen as `UiCommand.Rotate` and `Split` beside `MenuAlternate`'s `Alternate`
  (test: ACommandGoesToTheFocusedWidgetOrTheOneBeingDragged).

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
    // Rebinding is on Engine.Rebinds (InputRebinds, §3.9): BeginCapture(context, action), Offer(input), Resolve(...), Bind, Unbind, Reset.
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
    // Recording and replay are Engine.Demos and DemoSystem (§3.12): the sampler needs no recorder.
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
- `user://input.json`: the player's rebinds as record patches (§3.9, built).
- Command recordings, `user://demos/<name>.sagedemo`: a header (build, game, plugins, mods, actions, world, scene, step, start tick and time), the embedded start save, one command record per tick and an end record with the tick count and the world hash (§3.12, built).

## 8. Errors and fallbacks
- An unknown key/button name in a binding: validation error for that binding (the record still loads).
- Two actions bound to the same key in one context: `Warn` at load, listing both. First registered wins.
- Gamepad disconnected: its axes read 0, its player is left without a pad (it returns to that player when it comes back), and the change is logged under `Input` (§3.8). There is no `GamepadDisconnected` engine signal; a game that wants to pause reads `Engine.Pads`.
- A corrupt `user://input.json`: ignored with a `Warn`; defaults are used, and the file is kept as `input.json.bad` (built, #328).

## 9. Debug and tooling hooks
- **Cvars:**
  - `m_sensitivity` (a multiplier on `Look` scale);
  - `m_invert_y`;
  - `m_capture` (hold the mouse for look; archived, on);
  - `joy_deadzone`, `joy_curve` and `joy_rumble` (archived), and `joy_players` (not archived), §3.8.
- **Commands:** `bindlist`, `in_contexts` and `in_showactions [on|off]` (a log of the live action state, not an overlay); `bind`, `unbind` and `bind_reset` (§3.9); `pad_list` and `pad_assign` (§3.8); `record`, `stop`, `playdemo`, `demos` and `world_hash` (§3.12); and the scripted set.
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
  - mouse capture (§3.10);
  - `PlayerCommand` with latching;
  - frame-rate look;
  - text input routing.
  - rebinding: `bind`, `unbind`, `bind_reset`, `user://input.json` and a controls screen (§3.9);
  - several pads, hot-swap, rumble, dead zone and curves, the last-used device (§3.8);
  - command recording and replay, `.sagedemo` (§3.12).
- **Later:** touch (no issue yet); rebinding axis actions.

### As built (scripted input, 2026-09-23)

- **Code:** `src/Sage.Client/Input/InputActions.cs` (the `Scripted` list and the `in_*` commands),
  `src/Sage.Core/Console/CVarRegistry.cs` (`wait` and the deferred statement queue).
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

- **Done since:** recording a session to a `.sagedemo` and playing it back (§3.12), which is the same chain
  plus a per-tick `PlayerCommand` log.

### As built (asking the player to face somewhere, 2026-09-25)
A teleport that does not turn you is half a teleport, and turning the player is not something the
simulation can simply do.

- **Why it needs asking at all.** The view angles are accumulated in the host's `CommandLatch` between
  ticks, and `PawnIntent.Yaw` is rewritten from the resulting `PlayerCommand` **every tick** (§3.4). A
  rotation written onto the pawn by gameplay therefore survives until the next tick and no longer. It
  looks like it works, which is worse than not working: `map_goto` shipped like that and put the player
  in the right room facing whatever way they had been.
- **`PlayerInput.RequestView(yaw, pitch = 0)`**, answered once by the host before it samples the next
  command — which is what finally called `CommandLatch.SetView`, a method that had existed with no
  callers at all. Once and then let go, because a request that kept being answered would pin the view
  and feel like a broken mouse. (test: AskingThePlayerToFaceSomewhereIsAnsweredOnceAndThenLetGo)
- **The simulation stays the one that decides**, and the host stays the only thing that owns the angles:
  the request is a field on a world resource, so a headless world can have one asked of it and nothing
  happens, which is exactly right.
- **Used by** `map_goto` (15 §10a). A respawn that faces you the right way wants it next, and so does
  anything cut-scene shaped.

## 12. Multiplayer-later notes
`PlayerCommand` is exactly what a client will send to a server each tick (with the tick number). The local controller's view angles become predicted state. Nothing about sampling changes.

## 13. Open questions
- 64 button actions enough? Probably for one pawn. Menus use UI navigation, not `PlayerCommand`. Revisit if a game needs more (e.g. Warband's many orders); those could be one `Command` action with a parameter instead.

## 14. Build steps
1. ~~Move the listeners into `InputDevices`; add gamepad~~ **Done 2026-09-22** (ARCHITECTURE §7 step 6).
2. ~~Actions + input-map records + contexts (with ImGui capture)~~ **Done 2026-09-22** (TODO R3).
3. ~~`PlayerCommand` + `CommandSampler` + latching; the `Menu` action replaces the Escape check~~ **Done 2026-09-22** (TODO R3, #37). The sampler is `CommandLatch` + the host's tick loop.
4. Editor camera rig on actions; drop display-size scaling (TODO #39).
5. ~~`bind`/`unbind` + `user://input.json`~~ **Done 2026-10-06** (#328, §3.9).
6. ~~Focus gating, raw-state edges and trigger hysteresis~~ **Done 2026-09-24** (review #60, §3.7).
