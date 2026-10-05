# 02 · Core services

> Status: mostly built. Logging, asserts, crash reports, cvars, the console script language, the profiler and the allocation counter work and are tested, and the console has history and Tab completion (#299); the profiler can capture a Chrome trace and the dev overlay shows render and asset statistics (#300); a job system and per-app (rather than per-process) logging are not built. Owning assembly: `Sage.Core`. Design doc: [02 core services and logging](../../design/02-core-services-and-logging.md).

## 1. Purpose and scope

Core services are what every other part of the engine uses and none of them should have to build: a log that is safe from any thread and costs nothing when disabled, asserts with three strengths, a crash report that names the cause, typed runtime variables and commands with a small script language, a profiler, a time model, and the rules that keep steady-state frames from allocating.

They deliberately do not include content loading and errors with file and line (that is [06 assets and content](06-assets-and-content.md), which uses the log), the on-screen console and overlays (the editor assembly draws them, [18 editor](18-editor.md)), or any rule about gameplay. `Sage.Core` has no MonoGame and references nothing above it.

## 2. Responsibilities

- Logging: `Log` with six levels (Trace to Fatal), named categories (`LogCat`), structured fields, caller file and line, duplicate collapse, rate limiting (`Log.Once`, `Log.Every`), and sinks for file, stdout and an in-memory ring.
- Asserts: `Assert.Dev` (dev builds, breaks in a debugger), `Assert.Ensure` (all builds, logs once per call site, returns the condition), `Assert.Check` (all builds, fatal).
- Crash reports: write `crash-<stamp>.txt` with summary, exception, build, system, extra sections and the log tail, on `Log.Fatal`, a failed `Assert.Check` and any unhandled exception.
- CVars and commands: `CVarRegistry`, `CVar<T>` (bool, int, float, string, enum) with range, flags and change events; `RegisterCommand`; the statement language (`;`, quotes, comments, `wait`, `exec`); `config.cfg` round trip; `LaunchArgs`.
- Profiler: nested named scopes per thread, a per-frame table with moving averages, optional allocation tracking.
- Time: `TickTime`, `FrameTime`, `FixedStepClock`; the rule that systems read time from their context.
- Build identity: `BuildInfo` (configuration, SemVer from git tags), `GameManifest`, `SemVersion`.
- The registration seal (`RegistrationSeal`), which turns a late registration into an exception.

Not responsible for: drawing the console or the stat overlays, content errors and reports, the world clock and calendar ([14 world and streaming](14-world-and-streaming.md)), pausing and time scale per world (`WorldTime`, [14 world and streaming](14-world-and-streaming.md)), or a job system (design 02 §4.5, not built).

## 3. Placement and dependencies

`Sage.Core`, namespace `Sage.Core`, is the bottom of the stack: Core, then Simulation, then Physics3D, then Gameplay, with UI and Editing on Simulation. Core references no other Sage assembly and no MonoGame; the analyzer SAGE0024 enforces the second, and `AssemblyLayeringTests` the first. Everything above uses `Log`, `LogCat`, `CVarRegistry`, `Profiler` and `Assert` directly; they are static or passed in, never resolved through a service locator.

The core cvars and commands are registered by `CoreCVars.Register`, called by `SageApp.Create`, so they exist in every app. The engine's other plugins register their own cvars in `Init` ([03 app and loop](03-app-and-loop.md)). The stat overlays are in `Sage.Editor`, so a Shipping build has none of them.

## 4. Interfaces

| Type | File | Role |
|---|---|---|
| `Log`, `LogLevel`, `LogField`, `LogEntry` | `src/Sage.Core/Logging/Log.cs` | `Log.Trace/Debug/Info/Warn/Error/Fatal(cat, $"...")`, `Log.Once`, `Log.Every`, `Log.Flush`, `Log.Initialize(LogOptions)`. Trace and Debug are compiled out of Shipping. |
| `LogCat` | `src/Sage.Core/Logging/LogCat.cs` | A category with a runtime level. Built in: Core, Host, Modules, VFS, Assets, Records, Shaders, Render, Input, World, Events, Physics, Audio, Animation, UI, Streaming, Level, Save, AI, Gameplay, Editor, Mods, Console. Games add their own with `new LogCat("Name")`. |
| `ILogSink`, `FileLogSink`, `StdoutLogSink`, `RingBufferLogSink` | `src/Sage.Core/Logging/LogSinks.cs` | Where entries go; `Log.AddSink` adds more. |
| `Assert` | `src/Sage.Core/Diagnostics/Assert.cs` | The three asserts. `SageFatalException` is thrown by `Check` and `Log.Fatal`. |
| `CrashReporter` | `src/Sage.Core/Diagnostics/CrashReporter.cs` | `Install`, `Write(exception, reason)`, `AddSection(name, provider)`, `LastReportPath`. |
| `CVarRegistry`, `CVar<T>`, `CVarFlags` | `src/Sage.Core/Console/` | `Register`, `RegisterCommand`, `Execute`, `Pump`, `ExecFile`, `SaveArchived`, `Complete`, `Find`. Flags: `Archive`, `Cheat`, `DevOnly`, `ReadOnly`. |
| `ConsoleInput` | `src/Sage.Core/Console/ConsoleInput.cs` | The console's line editor without the keys (#299): `Submit`, Up/Down history (`MaxHistory` 100 lines, a repeat kept once, the draft restored), and Tab completion of the first word against commands and cvars and of later words against a cvar's enum or bool values and record ids. |
| `FrameLimiter` | `src/Sage.Core/Console/FrameLimiter.cs` | `host_maxfps` arithmetic (#299): how long the rest of a frame still has to wait. |
| `ContentProblems`, `ContentProblem` | `src/Sage.Core/Content/ContentProblems.cs` | The content's errors and warnings from the last load, with file and line, and mod conflicts as warnings (#301): what `problems`, the dev overlay's badge and the editor's `ProblemList` read. |
| `ConsoleArgs`, `LaunchArgs`, `ExecSource` | `src/Sage.Core/Console/CommandLine.cs` | Command arguments and launch parsing (`-option`, `+statement`). |
| `Profiler` | `src/Sage.Core/Diagnostics/Profiler.cs` | `using (Profiler.Begin("name"))`, `Record(name, ticks)` (time measured elsewhere, such as a parallel system's on a worker, #288), `EndFrame`, `All`, `Find`, and the trace capture (`StartCapture`, `TraceSpan`, `DumpTrace`, `ProfilerTrace.cs`, #300). Off in Shipping unless enabled. |
| `WorkStats` | `src/Sage.Core/Diagnostics/WorkStats.cs` | Process-wide counts of jobs handed to other threads, loads and their time, and GPU uploads, made per frame by `EndFrame()` (#300): what `stat render` and `stat assets` show. |
| `VisualLog`, `VisualLogEntry`, `VisualShape` | `src/Sage.Simulation/Rendering/VisualLog.cs` | Debug shapes kept per tick and category, one per world (`world.VisualLog()`), scrubbed with `vlog_at` and the editor's Visual log window (#300). |
| `TickTime`, `FrameTime`, `FixedStepClock` | `src/Sage.Core/Time.cs` | Time as systems see it; the accumulator that decides how many ticks a frame pays for. |
| `BuildInfo`, `BuildConfig`, `SemVersion` | `src/Sage.Core/BuildInfo.cs`, `Declarations/Plugins.cs` | Configuration (Debug, Development, Shipping), `IsDevBuild`, `EngineVersion`. |
| `RegistrationSeal` | `src/Sage.Core/RegistrationSeal.cs` | `Seal(stage)` then `Check(name)` throws with what was missed and where to register. |

**Core cvars** (`src/Sage.Core/Console/CoreCVars.cs`): `developer`, `sv_cheats`, `con_enable`, `log_keep`, `log_file_level`, `log_console_level`, `log_queue_size`, `mem_warn_bytes`, `ev_maxage`, `ev_trace`.

**Core commands:** `help`, `cvarlist`, `cmdlist`, `find`, `echo`, `version`, `wait`, `wait_cancel`, `exec`, `log_level`, `log_list`, `mem`, `crash` (dev only); `problems` comes with the record store (#301). `trace_start` and `trace_dump` (#300) are core commands too. The stat overlays (`stat fps|mem|frame|render|assets|all|none`) are registered by the editor assembly in dev builds; `sys_list`, `sys_toggle`, `ev_stats`, `plugins` and `modules` come from the simulation.

The console is available when `BuildInfo.IsDevBuild` or `con_enable` is set (`CoreCVars.ConsoleAvailable`), so a Shipping game has no console unless the player opts in.

## 5. Data model

Core services own no records or components. Formats:

| Format | Where | Notes |
|---|---|---|
| `config.cfg` | `user://config.cfg` | Archive cvars as `name value` statements, executed at `Configure`, written at shutdown. |
| `autoexec.cfg` | `user://autoexec.cfg` | Optional console statements, executed right after `config.cfg`, so they win over saved values (#299). |
| Log file | `user://logs/` | Timestamp, level, category, frame and tick, message, fields, caller; newest `log_keep` sessions kept. |
| Trace dump | `user://logs/trace-<time>.json` | Chrome trace event JSON from `trace_dump` (#300); opens in Perfetto or `chrome://tracing`. |
| Crash report | `user://logs/crash-<stamp>.txt` | Plain text sections; extra sections are added by the host (non-default cvars, game and mounts, GPU, mods). |
| `game.json` | game folder | Parsed by `GameManifest`; see [03 app and loop](03-app-and-loop.md). |

## 6. Lifecycle and data flow

`Program.Main` initialises `UserPaths`, `Log` (file and stdout) and `CrashReporter` before anything can fail. `SageApp.Create` builds the `CVarRegistry` and registers the core cvars; every module registers its own in `Init`; `Configure` executes `config.cfg` and then `autoexec.cfg` when there is one (#299), after which the cvar registry is sealed, so a cvar registered in `Start` is a build error (SAGE0020) and a runtime exception. Launch `+statements` run once the first world exists. The host calls `Log.SetFrame` each frame and the world calls `Log.SetTick` each tick, so every entry carries both. `Profiler.EndFrame` runs at the end of each frame.

`CVarRegistry.Pump(realDt)` runs once a frame in real time, so a console script that uses `wait` keeps running while the game is paused. That is what makes `+cmd` launch lines and `exec` files usable as automated checks.

`log_level`, `developer`, `log_file_level` and `log_queue_size` configure the process log only for the app that owns it (`SageAppOptions.OwnsProcessLog`); a second app in the same process leaves the log alone (test: AnAppThatDoesNotOwnTheLogLeavesItAlone).

## 7. Threading and memory

`Log` is safe from any thread: producers enqueue, a background writer feeds the sinks. When the queue is over `log_queue_size`, Trace and Debug entries are dropped and counted; Info and above are never dropped and producers never block. A call whose category and level are disabled formats and allocates nothing (test: DisabledLevel_FormatsNothing_EnabledLevel_Writes). The profiler keeps its tables per thread. `CVarRegistry` and the console are main-thread only.

Allocation rule: Fixed and Frame systems, rendering and enabled logging do not allocate in steady state: no LINQ, no per-call lambdas that capture, no boxing, no string building. Measured per thread with `GC.GetAllocatedBytesForCurrentThread`. Tests that assert this run in the `Measurements` collection so nothing else shares the machine (test: SteadyStateTicksAndFrames_DoNotAllocate). A probe names the system that allocated (test: AnAllocationProbe_NamesTheSystemThatAllocated). `mem_warn_bytes` warns after 60 steady frames above a threshold; it defaults to 0 (off) because the dev tools allocate (#374). There is no `mem_lowlatency` GC setting.

Process-wide state (`Log`, `UserPaths`, `CrashReporter`) is static. Tests that change it run in the `ProcessWideState` collection, alone.

## 8. Errors and diagnostics

Errors go to the log with the category that owns them. Content mistakes are reported with file and line by the content layer, not here. An unknown command warns on `Console`; in Shipping, an unknown name read from a config file (`config.cfg`, `autoexec.cfg`) is an Info note, "X is not in this build; ignored", because `config.cfg` is shared by every build of a game and a dev build archives cvars a Shipping host lacks (#293). A cvar set from code out of range is clamped; a `Cheat` cvar needs `sv_cheats`; a `ReadOnly` cvar refuses the console. A crash report is written even when one of its sections throws (test: CrashReport_HasAllSections_EvenWhenOneFails). `Assert.Check` writes a report and throws `SageFatalException` (test: Check_ThrowsFatal_AndWritesCrashReport).

Debug commands: `log_list`, `log_level <cat> <level>`, `cvarlist [prefix]`, `find <text>`, `mem`, `crash`, `stat frame` (per-phase and per-system milliseconds), and, from #300:

- `trace_start [max events]` and `trace_dump [file]`: capture every profiler scope on every thread, then write a Chrome trace (a bare name goes in `logs/`, default `trace-<time>.json`) that Perfetto opens. The buffer is fixed, nothing allocates per scope, and events past it are dropped and counted in `otherData.dropped`. Side-by-side systems on workers report through `Profiler.TraceSpan`. Tracy is not built.
- `stat render` (draw calls, triangles, material switches, items, uploads, jobs) and `stat assets` (meshes, textures, materials, loads and their milliseconds, loads pending, terrain sectors), ImGui overlays in dev builds. They read `RenderStats` (`Meshes`, `Textures`, `Materials`) and `WorkStats`, which counts jobs, loads and GPU uploads per frame; the host calls `WorkStats.EndFrame()`. Mesh load time is measured in `ContentService.LoadModel` (a cooked `.sgmesh` or a `.glb`), not in the renderer.
- The visual logger: `world.VisualLog()` records shapes per tick and category (`VisualShape`, `VisualLogEntry`); cvars `vlog_record`, `vlog_ticks`, `vlog_show`, `vlog_window`, commands `vlog_at`, `vlog_step`, `vlog_list`, `vlog_clear`, and the editor's Visual log window. The AI debug view (`ai`) and `phys_debug` (`physics`, characters only) record into it.

Not built: a general job system (`JobSystem`), Tracy, and visual-log recording from combat, ability and navigation debug and from `phys_debug`'s colliders.

## 9. Requirements

| ID | Requirement (shall ...) | Priority | Status | Evidence or issue |
|---|---|---|---|---|
| REQ-CORE-01 | Log from any thread without blocking, with levels, categories, fields and caller location. | Must | Done | `src/Sage.Core/Logging/Log.cs` |
| REQ-CORE-02 | Format and allocate nothing for a disabled level or category, and remove Trace and Debug from Shipping. | Must | Done | test: DisabledLevel_FormatsNothing_EnabledLevel_Writes |
| REQ-CORE-03 | Provide Dev, Ensure and Check asserts with the semantics in section 2. | Must | Done | test: Ensure_ReturnsCondition_AndLogsOncePerCallSite |
| REQ-CORE-04 | Write a crash report on fatal errors and unhandled exceptions, with extra sections from the host. | Must | Done | test: Check_ThrowsFatal_AndWritesCrashReport |
| REQ-CORE-05 | Offer typed cvars with range, flags, change events and `config.cfg` persistence. | Must | Done | test: Config_SaveAndExec_RoundTrip |
| REQ-CORE-06 | Offer a console script language with `wait`, `exec` and launch `+statements`, usable for automated runs. | Must | Done | test: AWaitStopsTheRestUntilItsTimeIsUp |
| REQ-CORE-07 | Reject a registration made after its registry sealed, naming where it belongs. | Must | Done | test: ACvarRegisteredAfterConfigCfgIsAnError |
| REQ-CORE-08 | Measure time per phase and per system and allocation per scope. | Must | Done | test: Profiler_RecordsPhasesAndSystems |
| REQ-CORE-09 | Keep a steady-state tick and frame allocation-free for the core systems. | Must | Done | test: SteadyStateTicksAndFrames_DoNotAllocate; the physics step too since #273 (test: ASteadyStateStepAllocatesNothingOnAnyThread) |
| REQ-CORE-10 | Give systems time only through their context (`TickTime`, `FrameTime`), never the wall clock. | Must | Done | `src/Sage.Core/Time.cs`; test: Clock_RunsWholeTicks_AndCarriesTheRemainder |
| REQ-CORE-11 | Provide console history and Tab completion for commands, cvars and record ids. | Should | Done (#299): `ConsoleInput`, headless; the ImGui console forwards the keys | test: `History_WalksBackAndForthAndKeepsTheDraft`, `History_IsBounded`, `Tab_CompletesCommandsAndCVars`, `Tab_CompletesCVarValuesAndRecordIds` |
| REQ-CORE-12 | Provide a visual logger, `stat render`, `stat assets` and a trace export. | Could | Done (#300) | test: TraceDumpWritesAChromeTraceOfEveryThreadThatPerfettoOpens, AFullBufferDropsTheRestAndSaysHowMany, TracingAScopeAllocatesNothing, ABareFileNameGoesInTheLogsFolder, ShapesAreKeptPerTickAndScrubbedBackTo, NothingIsRecordedUntilVlogRecordIsOn, OnlyTheLastVlogTicksAreKept, VlogShowPicksTheCategoriesDrawnAndListed, ARebaseClearsTheHistoryItsPositionsNoLongerMatch, AShapeWithoutTextAllocatesNothing, TheVisualLogKeepsWhatAnAgentSawAndWhereItStood |
| REQ-CORE-13 | Give each app in a process its own log sinks, user folder and crash report. | Should | Not started | #49 |
| REQ-CORE-14 | Offer a job system on the thread pool for decoding and generation, with main-thread completion. | Should | Partial: terrain generation on the thread pool with main-thread completion (test: `GenerationOnJobsIsTheSameGroundAsOnTheMainThread`); jobs, loads and uploads are counted (`WorkStats`; test: `ATerrainJobIsCountedAsAJobAndALoadUntilItIsDone`, `UploadsAreCountedInTheFrameTheyHappen`); no general job API | design 02 §4.5 |
| REQ-CORE-15 | Expose content errors in a shipped game without the editor (`problems` command and badge). | Should | Done (#301): `ContentProblems`; `problems` is in every build, the badge (`ui_problems`) in dev builds' overlay | test: `ProblemsListsTheSameEntriesAsValidate`, `ProblemsListsTheLoadErrorsWithFileAndLine`, `ACleanLoadSaysSo_AndModConflictsAreWarnings` |
| REQ-CORE-16 | Offer world and per-system time scale, pause and hit-stop. | Should | Done | `src/Sage.Simulation/World/WorldTime.cs`; per system: the world's time, or real time by `RunCondition.Always` (no per-system factor); test: HalfScaleHalvesTimerTweenAndClockProgress, AHitStopFreezesTheWorldForRealSeconds |
| REQ-CORE-17 | Version the engine from git tags and check plugin, kit and game ranges against it. | Must | Done | `BuildInfo.EngineSemVersion`; first tag pending (#295) |

## 10. Open work

R1, Tooling and the first release (milestone 3):
- #295 R1-3 Push the v0.1.0 tag and cut a first real release (P1): the API is frozen and the release workflow is in; the tag is the owner's push
- ~~#300 R1-8 Diagnostics gaps~~ done

Existing: #49 per-app log, user folder and crash reporter (P2).

## 11. References

- [Design 02](../../design/02-core-services-and-logging.md): §3 concepts, §4.1 crash reports, §4.2 cvars and console, §4.4 profiler, §4.6 allocation rules, §11 scope.
- [REDESIGN](../../REDESIGN.md) §3.3 registration seals; [MAKING_A_GAME](../../MAKING_A_GAME.md) §10 (things that silently do nothing) and §10a (the SAGE diagnostics).
- Siblings: [01 platform](01-platform.md), [03 app and loop](03-app-and-loop.md), [06 assets and content](06-assets-and-content.md).
