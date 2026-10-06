# 12 · Input

> Status: built for v1 (phase 4o). Keyboard, mouse and up to four gamepads feed data-defined actions and contexts, a frame-rate latch turns them into one `PlayerCommand` per tick, and scripted input drives the same path for automated checks. A player rebinds with `bind` or the controls screen and it persists in `user://input.json`; the mouse is held for look and let go for anything that wants a pointer; pads rumble from data, hand out to players and hot-swap; prompts follow the last-used device; and a session records to a `.sagedemo` and replays to the same world hash. Left: axis rebinding, touch and the limits in section 10. Owning assemblies: `Sage.Simulation` (command, latch, gating, map record), `Sage.Client` (devices and action resolution). Design doc: [08 Input](../../design/08-input.md).

## 1. Purpose and scope

Input turns hardware into intent. Devices produce raw state; data-defined actions and contexts decide which action fires and who hears it; a per-tick `PlayerCommand` is what the simulation consumes. The simulation never sees keys, only commands, so a player, an AI controller and (later) a network or a replay all drive a pawn the same way through `PawnIntent`.

It deliberately does not do: game UI and focus navigation inside screens (the UI sheet, [13](13-ui.md); UI reads `Menu*` actions), mouse picking for strategy games (Stage D), or the physics of movement ([08](08-physics.md)).

## 2. Responsibilities

- Poll keyboard, mouse and up to four gamepads, with edge detection and exact-match chords.
- Resolve `input_map` records into per-context bindings: keys, mouse buttons and delta and wheel, pad buttons, triggers and sticks, and composites such as WASD.
- Route input by context (Editor, Console, UI, Gameplay, highest priority first) with consumption, so a click does not reach both a screen and the game.
- Handle focus loss and return without stuck keys or false presses.
- Latch taps shorter than a tick and accumulate view angles at frame rate.
- Offer scripted input (console-driven actions) through the same chain as a device.
- Provide `PlayerInput`, the command resource, and `PlayerControlSystem`, which writes it into `PawnIntent`.
- Save a player's rebinds as record patches in `user://input.json`, and offer a controls screen that captures the next input.
- Decide when the mouse is held (`m_capture`) and recapture without a jump of the view.
- Hand pads to local players, track the last-used device, and turn `rumble` records into motor strengths per player.
- Record the player's commands per tick to a `.sagedemo` and replay them, checking a hash of the world.

Not responsible for: what an action means (gameplay reads action names from the conventions record), AI control (it writes `PawnIntent` itself), or the look of the controls screen beyond its view-model (the RPG kit's layout is content, [13](13-ui.md)).

## 3. Placement and dependencies

The data and logic that need no device are in `Sage.Simulation/Input/`: `PlayerCommand`, `ActionRegistry`, `ActionMask`, `CommandLatch`, `PlayerInput`, `FocusPolicy` and `InputEdges`, `InputBinding` and `InputMapRecord`, `Pawn`, `PawnIntent`, `PlayerControlSystem`, and since phase 4o `ActionMapper` (binding compile, contexts, masking, composites, dead zones and curves; internal, behind the `IInputVocabulary`, `IInputState` and `IPadInput` seams), `InputRebinds` and `UserInputMount`, `MouseCapturePolicy` and `MouseCaptureTracker`, `Demos`, `DemoSystem` and the `.sagedemo` format, `PadAssignment`, `LastUsedDevice`, `InputGlyphs`, `StickResponse`, `ScriptedInput` and `RumbleMixer`. The MonoGame-facing parts are in `Sage.Client/Input/`: `InputDevices`, `KeyboardListener`, `MouseListener`, `GamepadListener`, `InputActions`, which are now the device adapter over `ActionMapper`. Tests cannot reference `Sage.Client`, so the testable seams live in Simulation by design.

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
| `ActionMapper` | Compiles bindings and resolves them per context and per player: masking, composites, rate bindings, dead zones, response curves | `src/Sage.Simulation/Input/ActionMapper.cs` |
| `InputRebinds` | The player's rebinds: `Bind`, `Unbind`, `Reset`, `BeginCapture`/`Offer`/`Resolve` for the controls screen; on `Engine.Rebinds` | `src/Sage.Simulation/Input/InputRebinds.cs` |
| `MouseCapturePolicy`, `MouseCaptureTracker` | Whether the cursor is held, why not, and the first frame's delta | `src/Sage.Simulation/Input/MouseCapture.cs` |
| `Demos`, `DemoSystem` | Record, play and stop a `.sagedemo`; `WorldHash` | `src/Sage.Simulation/Input/Demos.cs`, `DemoFile.cs` |
| `PadAssignment`, `LastUsedDevice`, `InputGlyphs`, `StickResponse` | Which pad drives which player, the device in use, the binding a prompt shows, the stick's dead zone and curve; on `Engine.Pads` and `Engine.LastDevice` | `src/Sage.Simulation/Input/Gamepads.cs` |
| `PadFamily`, `InputPrompts` | Whose names a pad's buttons go by (Xbox or PlayStation, from the pad's name, or `joy_glyphs`); `{action:Name}` in localised text resolved to the glyph of the device in use, named by `@input.<glyph key>` when a string table has it (#352) | `src/Sage.Simulation/Input/Gamepads.cs`, `src/Sage.UI/InputPrompts.cs` |
| `ScriptedInput` | Scripted actions for a chosen local player | `src/Sage.Simulation/Input/ScriptedInput.cs` |
| `RumbleMixer`, `rumble` record | Each player's two motors, the sum of the effects playing | `src/Sage.Simulation/Input/Rumble.cs` |
| `InputActions` | The device adapter over `ActionMapper`, the console verbs, scripted input | `src/Sage.Client/Input/InputActions.cs` |
| `InputDevices`, `GamepadListener` | Device polling (four pads, `PadFor(player)`), rumble applied to the pads | `src/Sage.Client/Input/InputDevices.cs` |

Cvars (archived): `m_sensitivity`, `m_invert_y`, `m_capture` (hold the mouse for look, on), `joy_deadzone` (a floor under every stick and trigger, 0 to 0.9), `joy_curve` (`linear`, `quadratic`, `cubic` or an exponent 0.1 to 8) and `joy_rumble` (0 to 1); `joy_players` (1 to 4 local players with a pad, not archived). Commands: `bindlist`, `in_contexts` and `in_showactions [on|off]` for inspection; `bind <input> <action> [context]`, `unbind <input> [action] [context]` and `bind_reset [action|all] [context]`; `pad_list` and `pad_assign <player> <pad>`; `record <name>`, `stop`, `playdemo <name>`, `demos` and `world_hash [world]`; the developer-only scripted set `in_tap`, `in_hold`, `in_axis`, `in_look`, `in_release`, `in_clear`, `in_scripted`, `in_type` and `in_cursor`, each taking a trailing `@N` to aim at the Nth local player.

## 5. Data model

| Declaration | Kind | Holds |
|---|---|---|
| `input_map` | Record | `Context` and `Actions`: a list of `InputBinding` per action name |
| `rumble` | Record | `Low`, `High` (the two motors), `Duration`, `Attack`, `Release`; a `cue` and a `damage_type` name one with `rumble` |
| `sage:player_slot` | Component | Which local player (0 to 3) a player-controlled pawn is; without it, the first |
| `InputBinding` | Nested type | One of `Key`, `Mouse`, `Gamepad` or `Composite`, plus `Scale`, `Deadzone` (default 0.2), `Invert`, `Rate` |
| `sage:pawn`, `sage:pawn_intent` | Components | Marks a controllable body; its per-tick intent |
| `sage:player_controlled` | Tag | The local player's pawn |

Several maps may target one context (engine, game, mods) and their bindings add up. The engine ships an `editor` map and gameplay and UI maps. A player's rebinds are an `input_map` of their own per context (`user:rebinds_<context>`, the action's whole list) plus a patch on each other map that bound the action, which empties it there, all in `user://input.json` and mounted after the mods (test: ARebindIsSavedAndTheNewKeyFiresAfterARestart). A file that does not parse is ignored with a warning and kept as `input.json.bad`. Nothing else in input is saved; `PlayerInput` is per tick state. A `.sagedemo` is a magic and version, a JSON header (build, game, plugins, mods, actions, world, scene, step, the tick and time it began at), the save it starts from, one record per tick (a full command, the same as before, or none) and an end record with the tick count and the world hash.

## 6. Lifecycle and data flow

Per frame, the host polls devices, `InputActions` evaluates bindings for the active contexts (raw pass, then consumption by priority) and feeds `CommandLatch.AddFrame` and `AddLook`. Before each fixed tick the host samples the latch into `PlayerInput.Command`. In `Phase.Commands`, `sage.character.player_control` copies it into the `PawnIntent` of every player-controlled pawn; AI think runs after it. Contexts are switched by the host and screen system (`SetActive`): the editor and play-in-editor, the console, the UI stack and gameplay.

Rebinding goes through `InputRebinds`: it validates the input, rewrites the file (temp file, then move), reloads the records, and the client's `Rebuild` has the new key firing the next frame. A file that cannot be written changes nothing. Recording writes a save of now, then each tick's command in `Commands` phase (`sage.input.demo`, before `sage.character.player_control`); playback loads that save, feeds the commands and compares the world hash at the end. Rumble is mixed in the simulation (`sage.combat.rumble`, from `CueTriggered` and `Damaged`, for the player whose entity it happened to) and the host applies each player's motors to their pad once a frame, silenced while the window is not ours.

Look is applied at frame rate; a request to face a direction (teleport, respawn, `info_player_start`) goes through `PlayerInput.RequestView` because the latch owns the angles. Scripted input is injected after bindings are evaluated, so it takes the real chain. Registrations (actions, maps) are sealed after `Init` like other registries.

## 7. Threading, memory and performance

All input runs on the main thread. A tap shorter than a tick is never lost, because pressed and released bits accumulate until a command carries them (test: Latch_KeepsATapShorterThanATick). Move is clamped to length one and pitch to 89 degrees (test: Latch_HeldAndMoveAreTheLatestFrame_MoveIsClampedToLengthOne, test: Latch_LookTurnsRightAndUp_ClampsPitch_WrapsYaw). Edges come from raw device state, never the swallowed state, which fixed a bug where closing a screen made the character swing (test: AButtonSwallowedByAScreenIsNotPressedAgainWhenItCloses). No allocation measurement exists for the client input path.

## 8. Errors and diagnostics

A binding naming an unknown key, mouse or pad button, or an action no module registered, is reported when the map loads with its id and position (`sage:gameplay Jump[0]` style). `bindlist` lists bindings per context and `in_contexts` shows which are active. Focus handling is tested headlessly (test: FocusIsReadOnTheWayInAndNeutralWhileAway, test: StartingUnfocusedIsNeutralUntilTheWindowIsOurs, test: ATriggerRestingOnItsThresholdDoesNotChatter). Binding text that does not parse, malformed bindings, context masking, composites, rate bindings and the #60 regressions are tested headlessly through `ActionMapper` (tests: ConsoleBindTextThatDoesNotParseSaysWhy, MalformedBindingsAreRejectedWithAReason, ABindingNeedsExactlyOneSource). The device adapter in the client is checked only by the smoke run, which also checks the mouse is held while the player looks around. A rebind that conflicts says which action has the input and does nothing until the player replaces or keeps; the capture never offers Escape, the left mouse button or the pad's B, Back and Start. A demo from another build or other mods is refused with what differs (test: ADemoFromAnotherBuildOrWithOtherModsIsRefused), and a truncated one plays what is whole.

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
| REQ-INP-09 | A player shall rebind any action with `bind`, `unbind` and a persisted `user://input.json`. | Must | Done | test: ARebindIsSavedAndTheNewKeyFiresAfterARestart, test: BindAddsAnInputAndUnbindTakesItOff, test: BindResetPutsTheGamesBindingsBack, test: TheConsoleCommandsDoTheSame, test: EachContextHasItsOwnList. Axis actions (Move, Look) are not rebindable |
| REQ-INP-10 | The game shall offer a controls screen with capture and conflict detection. | Must | Done | test: CaptureNextInputSetsTheBindingFromThePress, test: AnInputAnotherActionUsesIsAConflict, test: PressingAKeyAfterRebindSetsTheBindingAndItSurvivesARestart, test: AConflictAsksToReplaceOrKeepAndBackCancels, test: ResetAndTheContextTabs (the RPG kit's `rpg:controls`, delivered with #328 as #341) |
| REQ-INP-11 | Gamepads shall support rumble, several pads, glyphs and hot-swap. | Should | Done | test: ACuesRumbleIsFeltByThePlayerWhoseEntityRaisedIt, test: AHitRumblesTheVictimsPadAndOnlyThatPlayers, test: AWorldWithoutAMixerIgnoresRumble, test: TheMixerSumsActiveEffectsPerPlayerAndClamps, test: AnEffectRisesAndFallsAndGainScalesIt, test: TheMixerIsBounded, test: PadsAreHandedOutToPlayersInOrder, test: AnUnpluggedPadReturnsToItsOwnPlayer, test: ANewPadTakesAFreeSlotAndPlayersCanBeReduced, test: ASecondPlayersPawnIsDrivenByTheirOwnCommand, test: ADeadZoneIsRadialAndRescaledToReachOne, test: ResponseCurvesParse, test: BadResponseCurvesAreRefused, test: ACurveIsFinerNearTheCentreAndStillReachesOne, test: AScriptForTheSecondPlayerLandsInTheirFrameOnly, test: ATapIsOneFrameAndTheLastScriptForAnActionWins, test: TheConsoleNamesAPlayerWithATrailingAt |
| REQ-INP-12 | Prompts and glyphs shall follow the last-used device. | Should | Done | test: TheLastUsedDeviceFollowsRealUseNotNoise, test: APromptShowsTheBindingOfTheDeviceInUse, test: EachPadFamilyNamesItsButtonsAndAPadsNameSaysWhichItIs, test: AnActionPlaceholderShowsTheGlyphOfTheDeviceInUse, test: TheInteractionPromptChangesWhenThePlayerPicksUpThePad |
| REQ-INP-13 | A recorded command stream shall replay deterministically (`.sagedemo`). | Should | Done | test: ARecordedSessionReplaysToTheSameWorldHash, test: ADemoRecordedAtLaunchReplaysInTheSandbox, test: ADemoFromAnotherBuildOrWithOtherModsIsRefused, test: ATruncatedDemoPlaysWhatIsWholeOrIsRefused, test: ARecordingRefusesAnotherUntilStopped, test: TheWorldHashFollowsTheSavedState. Exact where the save holds what the simulation reads; the real Sandbox client does not match yet (section 10) |
| REQ-INP-14 | Opening a screen shall release the mouse and closing it recapture it without a look jump. | Must | Done | test: ThePossessedPlayerHoldsTheCursor, test: EachThingThatWantsAPointerReleasesIt, test: RecapturingAfterAScreenDoesNotJumpTheLook, test: RegainingFocusDoesNotJumpTheLook, test: AMovedMiddleDropsThatFramesDelta, test: ScreenOpenThenClosedGoesThroughThePolicy |
| REQ-INP-15 | Binding parsing, context masking, composites and dead zones shall be covered by tests that need no window. | Should | Done | test: ConsoleBindTextThatDoesNotParseSaysWhy, test: MalformedBindingsAreRejectedWithAReason, test: ABindingNeedsExactlyOneSource, test: ADisplacedContextDoesNotHearWhatAHigherOneBinds, test: AnOpenConsoleSwallowsTheWholeKeyboardButNotTheMouse, test: RateBindingsScaleByFrameTimeAndOthersDoNot, test: WasdCompositeResolvesAndCancelsOpposites, test: ACompositeAndAStickAddUp, test: RegressionClosingAScreenWithAButtonHeldIsNotANewPress, test: RegressionAPressStartedWhileSwallowedStillReleasesLoudly, test: RegressionARestingTriggerDoesNotChatter, test: ADeadZoneIsRadialAndRescaledToReachOne |
| REQ-INP-16 | The kit's default input maps shall appear in the registry dump. | Could | Done: the kit registers `Spellbook`, `Spellmaker`, `Journal` and `Rest` in its simulation half, so the dump's `inputActions` has them with the kit as owner; their default keys are the kit's `rpg:ui` and `rpg:gameplay` maps; the client's screen ids were the dump's `screens` section until #350 deleted the screen registry | test: ABareKitGameHasTheKitsDefaultKeysInBothContexts, test: ADefaultMapIsPatchedByAGameLikeAnyRecord, test: ALayerAboveTheSimulationAddsASectionToTheRegistryDump |

## 10. Open work

Milestone 5, audio and input (epic #324) is built on the input side: #328, #331, #332, #333 and #334 are done, and #341, the in-game rebinding UI, shipped with #328. What is left:

- **Rebinding:** axis actions (Move, Look) cannot be rebound, only button actions; a conflict is per context. The controls screen opens from the options screen's controls page (#339).
- **Mouse capture:** the dev ImGui windows are clickable only with the console open while `m_capture` is 1; the editor viewport never captures the mouse and play-in-editor does.
- **Pads:** there is no rumble content; rumble is not scaled by damage; players 1 and up read only the Gameplay map's pad bindings (no keyboard or mouse look); `joy_players` is set by hand; touch is not covered; no real pad has been tried.
- **Replay:** replay is exact only when the save holds everything the simulation reads. The real Sandbox client does not replay exactly: the camera rig's transform is written at frame rate and saved, `FaceCameraSystem` reads `ActiveCamera`, and `HopSystem` keys off the tick and a runtime entity id. There is no timedemo or demo loop, and one world only.
- **Tests:** the listener edge-rolling bug (#40) is checked only in the client; `ActionMapper.Rebuild(RecordStore)` has no direct test.

Milestone 6 (epic #337) built the player-facing half: input glyphs in prompts (#352) and the kit's default keys (#354). Left from it: prompts show names, not button pictures (a game whose font has them maps `@input.*` to its characters); the old panel screens' hint lines still name keys by hand; `DisplayName`'s guess of the pad family has no test with a real pad.

Design doc 08 now says what the code does (reconciled in #303 and again with the 4o pack).

## 11. References

- [Design 08](../../design/08-input.md): devices, actions, contexts, the command, rebinding, mouse capture, pads and rumble, demos, scripted input.
- [REDESIGN](../../REDESIGN.md) §5, phase 4o; ARCHITECTURE §4.9 for the multiplayer-readiness rules (commands as input).
- Siblings: [Physics](08-physics.md), [UI](13-ui.md), [Audio](11-audio.md); parent [SRS](../SRS.md).
