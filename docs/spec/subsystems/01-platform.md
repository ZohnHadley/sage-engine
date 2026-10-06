# 01 · Platform layer

> Status: partly built. The window, graphics device, device polling, focus handling, audio device fallback and user folder work on Windows and Linux, with a frame-rate cap and `autoexec.cfg` (#299), and a game packages into a folder a player runs (#293); display modes, mouse capture, gamepad extras and per-app user folders do not exist yet. Owning assemblies: `Sage.Host`, `Sage.Client` (input and audio backend halves), `Sage.Core` (`UserPaths`, `BuildInfo`). Design docs: [01 host and modules](../../design/01-host-and-modules.md), [08 input](../../design/08-input.md), [02 core services](../../design/02-core-services-and-logging.md).

## 1. Purpose and scope

The platform layer is everything that touches the operating system or MonoGame directly: the window, the graphics device, operating-system events (close, resize, focus, typed characters), keyboard, mouse and gamepad polling, the audio output device, and the folders the engine reads and writes. Its job is to turn those into plain data and a few services, so the rest of the engine can run without them.

It deliberately does not talk to a graphics API (MonoGame is the abstraction, see [SRS](../SRS.md) §2.4), does not decide what input means (actions and contexts belong to [12 input](12-input.md)), does not mix sound ([11 audio](11-audio.md)) and does not draw ([07 rendering](07-rendering.md)). Everything below the platform line has a headless replacement, so tests, `sage validate` and a dedicated server run with no window, no GPU and no sound card.

## 2. Responsibilities

- Create the MonoGame `Game`, the `GraphicsDeviceManager` and the window, and walk the `SageApp` through its remaining boot stages once the device exists (`src/Sage.Host/Game1.cs`).
- Own the window cvars and keep the back buffer and the cvars in step: `vid_width` and `vid_height` follow the window when the player drags its edge, and a change to the cvar resizes the window.
- Run the main loop each frame: poll devices, resolve actions, run the fixed ticks through `HostLoop`, then the Frame schedule.
- Poll keyboard, mouse and gamepad once per frame (`InputDevices`), apply the focus rule so an unfocused window produces no input, and deliver typed characters as the operating system produced them.
- Provide the audio device backend (`MonoGameAudioBackend`) and fall back to a silent one when there is no device.
- Resolve and create the per-user folder (`UserPaths`): config, logs, crash reports, screenshots, saves, mods list.
- Provide host services to client modules (`ClientHost`, `InputDevices`, `InputActions`).
- Take screenshots and exit on request (`screenshot`, `quit`, `host_exitafter`) so automated runs can drive a real window.
- Provide the headless equivalents: `HeadlessApp` (no host), `NullAudioBackend`, `SageSimulationOnly` projects.

Not responsible for: what an action is or how it is rebound (input), mixing and positional sound (audio), render passes (rendering), the save format (saves), content mounts (assets), or window chrome beyond size and title.

## 3. Placement and dependencies

| Part | Assembly | May reference |
|---|---|---|
| `UserPaths`, `BuildInfo`, `Log` | `Sage.Core` | nothing of MonoGame |
| `InputGating`, `FocusPolicy`, `AudioMixer` (the decisions) | `Sage.Simulation` | Core; no MonoGame (SAGE0024) |
| `InputDevices`, listeners, `IAudioBackend` and its two backends, `ClientHost` | `Sage.Client` | Simulation, UI, MonoGame |
| `Game1`, `HostCVars`, `Program` | `Sage.Host` | Client, Simulation, Gameplay, Editor (dev builds only) |

The rule that makes headless work is the split above: every decision (is this a press, is the window ours, which voices play) lives in a simulation assembly and is tested without a device. The client only reads hardware and applies the decision. The `Sage.Editor` reference sits behind `SAGE_DEV` in the host, so a Shipping host has no ImGui in it.

`sage.client` (`ClientModule`) registers the client half of the platform: the input actions, `snd_enabled`, the audio backend choice and the renderer. A server leaves `HostModules` empty and none of it loads.

## 4. Interfaces

**Host services** given to client modules before `Start` (`ModuleManager.ProvideHostService`):

| Type | File | Role |
|---|---|---|
| `ClientHost` | `src/Sage.Client/ClientModule.cs` | The MonoGame `Game` and its `GraphicsDevice`. |
| `InputDevices` | `src/Sage.Client/Input/InputDevices.cs` | `Keyboard`, `Mouse`, `Gamepad` listeners, `Typed`, `Focused`, `Poll(bool focused)`, `EndFrame()`. |
| `InputActions` | `src/Sage.Client/Input/InputActions.cs` | Resolves bindings to actions from the devices (see the input sheet). |

**Backends:**

| Type | Role |
|---|---|
| `IAudioBackend` (`src/Sage.Client/Audio/AudioBackend.cs`) | `Apply(AudioMixer)`, `Playing`, `Release(AssetPath)`. Internal; swappable later for OpenAL Soft or FMOD. |
| `MonoGameAudioBackend` | One `SoundEffectInstance` per voice; becomes silent the first time MonoGame reports no device. |
| `NullAudioBackend` | Drains one-shots so the mixer never fills; used headless and with `snd_enabled 0`. |

**Cvars** (`src/Sage.Host/HostCVars.cs`, `src/Sage.Client/ClientModule.cs`):

| Cvar | Default | Flags | Meaning |
|---|---|---|---|
| `vid_width`, `vid_height` | 800, 410 | Archive | Window size in pixels, 320x200 to 7680x4320. |
| `r_vsync` | true | Archive | Vertical sync. |
| `sim_tickrate` | 60 | none | Fixed ticks per second. |
| `sim_maxframetime` | 0.25 | none | Longest frame the simulation catches up on. |
| `host_timescale` | 1 | DevOnly, Cheat | Simulation speed multiplier, on top of each world's own (`WorldTime.HostScale`); it also slows real time for real-time timers and the hit-stop countdown. |
| `host_exitafter` | 0 | DevOnly | Quit after N real seconds and log frame and tick counts. |
| `rec_hotreload` | developer >= 1 | DevOnly | Reload record files when they change on disk. |
| `snd_enabled` | true | Archive | Silent backend when false. |
| `m_sensitivity`, `m_invert_y` | see input sheet | Archive | Mouse look. |

**Commands:** `quit [seconds]`, `screenshot [delay]` (PNG into `user://screenshots`), `in_contexts`, and the dev-only automation set `in_type`, `in_cursor`, `in_tap`, `in_hold`, `in_axis`, `in_look`, `in_release`, `in_clear`, `in_scripted`.

**Launch options** (`src/Sage.Host/HostOptions.cs`, read by `Program.cs`; #298): `-game <folder>`, `-dump-registry <file>`, `-mods <dir>[,<dir>]`, `-nomods` (wins over `-mods`), `-edit [document]` (dev builds only); `+cmd args` runs a console statement once the first world exists. Unknown `-options` and options missing their value are warned and ignored (tests: TheHostReadsItsOptionsAndLeavesTheCommandsToTheConsole, ModsAreNamedInOrder_OrNoneWithNomods_WhichWinsOverMods, AMistypedOptionIsAWarningNeverAStop). A game folder that is wrong ends the host with a message naming the file and what to do (test: EachWayAGameFolderIsWrongSaysWhereAndWhat).

## 5. Data model

The platform layer owns no records and no components. Its persistent data is the user folder:

| Path under `user://` | Written by | Content |
|---|---|---|
| `config.cfg` | `SageApp.Configure` and shutdown | Archive cvars, as console statements. |
| `logs/` | `Log` file sink | One file per session, newest 10 kept (`log_keep`); `crash-YYYYMMDD-HHMMSS.txt`; `mod_report.txt`. |
| `screenshots/` | `screenshot` | `shot-<timestamp>.png`. |
| `saves/` | saves subsystem | See [15 saves](15-saves.md). |
| `mods/`, `mods.json` | mod manager | See [19 modding](19-modding.md). |

`UserPaths.Root` resolves to `<repo>/user/<gameId>` in a dev build run from a repository (found by `Sage.sln`), and to `%LOCALAPPDATA%/Sage/<gameId>` (or the platform equivalent) otherwise. `user://` is a path convention, not a VFS mount: content never reads it through the VFS. `UserPaths` is a static, so one process has one user folder.

## 6. Lifecycle and data flow

Boot (`Program.cs`, then `Game1.Initialize`): parse args, load `game.json`, initialise `UserPaths`, `Log` and `CrashReporter`, `SageApp.Create` and `Register`, create the window and graphics device, register host cvars and commands, `Configure` (reads `config.cfg`), `LoadContent`, provide host services, `Start`, create the main world, run `+` launch commands. Details of the stages are in [03 app and loop](03-app-and-loop.md).

Each frame, in `Game1.Update`: pump the console's `wait` queue in real time, `devices.Poll(IsActive)`, choose active input contexts, resolve actions into the pending `PlayerCommand`, `HostLoop.Update` (0 to N fixed ticks of every world), exit checks. `Game1.Draw` runs `HostLoop.Frame` (FrameUpdate, Extract, Render, Overlay), then the dev UI, `devices.EndFrame()` and `Profiler.EndFrame()`.

Focus: `Window.IsActive` is passed to `Poll`. On the frame focus is lost, held inputs release once; while unfocused every device reads neutral; on the frame focus returns, held buttons are read without a press edge (`FocusPolicy`, `src/Sage.Simulation/Input/InputGating.cs`) (test: FocusIsReadOnTheWayInAndNeutralWhileAway). A gamepad's connected state is read whatever the focus, because plugging in is hardware, not input.

Shutdown: worlds, modules in reverse dependency order, Archive cvars to `config.cfg`, then the log is flushed last. A crash anywhere writes a report first ([02 core services](02-core-services.md)).

## 7. Threading and memory

All MonoGame calls, device polling and `Game1` run on the thread named `main`. `Log` is safe from any thread; nothing else in the platform layer is. Polling and the listeners allocate nothing in steady state; typed characters use a small reused buffer. Audio voices are `SoundEffectInstance` objects created per play, which is the one platform path that allocates, and is bounded by `snd_maxvoices`. `GetBackBufferData` in `screenshot` allocates a full frame, which is acceptable for a developer command.

`host_maxfps` (0 to 1000, Archive; 0 is uncapped) caps the frame rate with vsync off: the host sleeps out the rest of each frame (`FrameLimiter`, #299; test: `FrameLimiter_WaitsOutTheRestOfTheFrame`). With vsync on the display paces frames.

## 8. Errors and diagnostics

A missing or malformed `game.json`, or a module that will not load, logs under `LogCat.Host` or `LogCat.Modules`, writes a crash report and exits with code 1. No audio device is logged once and the game runs silent. A log folder that cannot be written falls back to another location with a warning. The crash report carries a GPU section (adapter and profile). Useful commands: `in_contexts` (why is nothing responding, with the focus state), `version`, `mem`, `vfs_mounts`.

Gaps: a window that cannot be created shows no native message box; the failure is in the log and crash report only.

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-PLAT-01 | Open a window and graphics device through MonoGame DesktopGL on Windows and Linux. | Must | Done | `src/Sage.Host/Game1.cs`; smoke run in CI |
| REQ-PLAT-02 | Run the same game headlessly with no window, GPU or audio device. | Must | Done | `tests/Sage.Testing/HeadlessApp.cs`; `NullAudioBackend` |
| REQ-PLAT-03 | Keep window size and `vid_width`/`vid_height` in step in both directions and save it. | Must | Done | `Game1.OnClientSizeChanged`, `ApplyWindowSize` |
| REQ-PLAT-04 | Ignore input while the window is not focused and resynchronise on return. | Must | Done | test: FocusIsReadOnTheWayInAndNeutralWhileAway |
| REQ-PLAT-05 | Deliver typed characters from the operating system, not from key states. | Must | Done | `InputDevices.Typed`, `Window.TextInput` |
| REQ-PLAT-06 | Resolve a per-user folder for config, logs, saves and mods, in a repository run and in an installed run. | Must | Done | `src/Sage.Core/UserPaths.cs` |
| REQ-PLAT-07 | Run silent, not crash, when there is no audio device. | Must | Done | `MonoGameAudioBackend`, `NullAudioBackend` |
| REQ-PLAT-08 | Give each app in a process its own user folder, log and crash report. | Should | Not started | #49 |
| REQ-PLAT-09 | Capture and release the mouse for look, with a clean handoff between menu and gameplay. | Must | Done (#334): the cvar `m_capture`; the cursor is held for a possessed player and released for a screen, the console, the controls screen's capture, the dev UI, focus loss and the editor viewport | test: ThePossessedPlayerHoldsTheCursor, test: EachThingThatWantsAPointerReleasesIt, test: RecapturingAfterAScreenDoesNotJumpTheLook, test: RegainingFocusDoesNotJumpTheLook |
| REQ-PLAT-10 | Support several gamepads, hot-swap, rumble and device glyphs. | Should | Done (#331): up to four pads for four local players, rumble from `rumble` records; no real pad has been tried | test: PadsAreHandedOutToPlayersInOrder, test: AnUnpluggedPadReturnsToItsOwnPlayer, test: ACuesRumbleIsFeltByThePlayerWhoseEntityRaisedIt, test: APromptShowsTheBindingOfTheDeviceInUse |
| REQ-PLAT-11 | Persist rebinding to a per-user file. | Must | Done (#328): `user://input.json` | test: ARebindIsSavedAndTheNewKeyFiresAfterARestart |
| REQ-PLAT-12 | Offer fullscreen, borderless, display choice and a resolution list. | Must | Not started | No issue yet; see section 10 |
| REQ-PLAT-13 | Pause or throttle the simulation and mute audio on focus loss, as a setting. | Should | Not started | No issue yet; see section 10 |
| REQ-PLAT-14 | Offer a frame-rate cap (`host_maxfps`) and an `autoexec.cfg` at boot. | Should | Done (#299): `autoexec.cfg` in the user folder runs after `config.cfg` | test: `FrameLimiter_WaitsOutTheRestOfTheFrame`, `Autoexec_RunsAfterConfigAndWinsOverIt`, `Autoexec_MissingIsNotAnError` |
| REQ-PLAT-15 | Run a packaged game from a folder on a machine without the SDK, with Release mapped to Shipping. | Must | Done (#293, #294): `sage package` writes the Shipping host with the game in `game/` beside it, and CI runs the package under Xvfb on Linux; `-c Release` builds Shipping into `bin/Shipping` | test: `APackageIsTheShippingHostWithTheGameBesideItAndNothingElse`, `ThePackagedHelloAndSceneOnlyGamesLoadAndValidateFromTheOutputFolder`, `AReleaseBuildIsShippingAndWritesTheShippingFolder` |
| REQ-PLAT-16 | Keep MonoGame out of every simulation assembly and every decision about input and audio. | Must | Done | SAGE0024; test: EveryBaseAssemblyIsSimulationOnly |
| REQ-PLAT-17 | Write screenshots and exit on a timer so automated real-window runs are possible. | Should | Done | `screenshot`, `quit`, `host_exitafter` |

## 10. Open work

P2:
- #49 per-app log, user folder and crash reporter (P2)

Gaps with no issue filed yet, to be raised before the platform sheet can be called complete: fullscreen and borderless modes with display choice (REQ-PLAT-12), focus-loss policy for the simulation and audio (REQ-PLAT-13), high-DPI scaling, and a native message box when no window can be created.

## 11. References

- [Design 01](../../design/01-host-and-modules.md) §5.1 boot sequence, §5.2 main loop; [design 08](../../design/08-input.md) §3.1 devices and focus; [design 11](../../design/11-audio.md) backend split.
- [REDESIGN](../../REDESIGN.md) §3.2 one boot path, §4.8 SDK and packaging.
- [MAKING_A_GAME](../../MAKING_A_GAME.md) §2 and §9 (running and checking a game).
- Siblings: [03 app and loop](03-app-and-loop.md), [11 audio](11-audio.md), [12 input](12-input.md), [20 tooling and release](20-tooling-and-release.md).
