# 02 — Core Services and Logging

## 1. Purpose and scope
The services every other subsystem uses:
- **logging** (verbosity controlled per category from the console);
- **asserts**;
- **crash reports**;
- **cvars and console commands**;
- **time**;
- the **profiler**;
- the **job layer**;
- the **allocation (GC) rules**.

All of it lives in `Sage.Core`, the kernel (no ECS, no MonoGame). The console *window* and overlays are drawn by client code (13) but read from these services.

## 2. Research basis
- Log categories with per-category verbosity: Unreal `UE_LOG(Category, Verbosity)`, Source spew groups in `tier0` (survey §1.3, §2.2).
- `Ensure` vs `Check` asserts: Unreal. Cvars with flags and `config.cfg`: Quake 3 `CVAR_ARCHIVE`, Source ConVars (survey §1.1, §1.3).
- Visual logger: Unreal Visual Logger (later).
- Jobs, not dedicated subsystem threads: Naughty Dog, idTech 7 (survey §2.8).
- Zero allocations per frame; GC spikes: survey §3.7.

## 3. Concepts

### 3.1 Log levels
| Level | Use | Shipping |
|---|---|---|
| `Trace` | Very chatty flow detail (every event, every asset lookup) | stripped |
| `Debug` | Developer detail (module init times, asset loads) | stripped |
| `Info` | Normal milestones (map loaded, save written) | kept |
| `Warn` | Something is wrong but handled (missing texture → placeholder) | kept |
| `Error` | A feature failed (a record failed validation, a shader failed to compile) | kept |
| `Fatal` | Unrecoverable; writes a crash report and exits | kept |

"Stripped" means the call doesn't exist in the `Shipping` binary: `[Conditional("SAGE_DEV")]` (01 §3.2).

### 3.2 Categories
A `LogCat` is a static object with a name and a current minimum level. The engine defines the categories listed in the index glossary; games and mods define their own the same way. The default minimum is `Info`, or `Debug` when `developer` ≥ 1 (01 §3.2). It's changeable per category at runtime.

### 3.3 Log entry
Each entry records:
- the timestamp (UTC + session-relative ms);
- the frame number and the fixed tick number (0 before the loop starts);
- the thread id/name;
- the category and level;
- the message;
- optional key/value fields, up to 3 (`Log.Warn(LogCat.Assets, $"missing", ("path", p), ("mount", m))`);
- the caller file/line (dev builds, via `CallerFilePath`/`CallerLineNumber`).

### 3.4 Sinks
Pluggable; each has its own minimum level.

| Sink | Default | Notes |
|---|---|---|
| File | on; `Debug` when `developer` ≥ 1, `Info` otherwise | `logs/sage-YYYYMMDD-HHMMSS.log`; keeps the last 10 sessions (`log_keep`) |
| Stdout / debugger | on when `developer` ≥ 1 | `Debug.WriteLine` when a debugger is attached |
| In-game console | whenever the console is available (01 §3.2) | Coloured by level; filter by category/text |
| Editor log panel | editor only | Filter, search, click-to-select the entity if the entry has an `entity` field |
| Ring buffer | always | The last 2,000 entries in memory, for crash reports and the console's scroll-back |

The log is one per process, so **only the app that owns it applies these defaults** (issue #11): the
executable's app sets `SageAppOptions.OwnsProcessLog`, and its `developer`, `log_file_level` and
`log_queue_size` configure the log. Any other app in the process — a test, a tool, a second app —
still has those cvars, and setting them leaves the log as the host set it (test: AnAppThatDoesNotOwnTheLogLeavesItAlone).

## 4. Public API sketch

```csharp
public enum LogLevel { Trace, Debug, Info, Warn, Error, Fatal }

public sealed class LogCat
{
    public LogCat(string name);                                // follows LogCat.DefaultLevel until set explicitly
    public string Name { get; }
    public LogLevel MinLevel { get; set; }                     // setting it marks the category as overridden (log_level)
    public bool IsEnabled(LogLevel level);
    public void ResetToDefault();
    public static LogLevel DefaultLevel { get; set; }          // Info, or Debug while developer >= 1
    // Engine categories: LogCat.Core, .Host, .Modules, .VFS, .Assets, .Records, .Shaders, .Render,
    // .Input, .World, .Events, .Physics, .Audio, .Animation, .UI, .Streaming, .Save, .AI,
    // .Gameplay, .Editor, .Mods, .Console
}

public static class Log
{
    // Per level (Trace shown; Debug/Info/Warn/Error/Fatal have the same shapes, and Trace/Debug carry
    // [Conditional("SAGE_DEV")]): an interpolated form and a plain-string form. Up to 3 fields as
    // optional `in` parameters (a `params` array would allocate on every call); caller file/line are
    // captured automatically.
    [Conditional("SAGE_DEV")]
    public static void Trace(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler<LogLevels.Trace> message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0);
    [Conditional("SAGE_DEV")]
    public static void Trace(LogCat cat, string message, in LogField f1 = default, /* ... same */);
    // Fatal logs, flushes, writes a crash report, then throws SageFatalException ([DoesNotReturn]).

    // Level chosen at runtime (console, tools).
    public static void Write(LogCat cat, LogLevel level, [InterpolatedStringHandlerArgument("cat", "level")] ref LogDynamicHandler message, ...);

    // Spam control. The handler also receives the key/interval and checks the rate limit in its
    // constructor, so a suppressed message is never formatted. Every appends "(+N suppressed)".
    public static void Once (LogCat cat, LogLevel level, string key,
                             [InterpolatedStringHandlerArgument("cat", "level", "key")] ref LogDynamicHandler message, ...);
    public static void Every(LogCat cat, LogLevel level, string key, TimeSpan interval,
                             [InterpolatedStringHandlerArgument("cat", "level", "key", "interval")] ref LogDynamicHandler message, ...);

    public static void Initialize(LogOptions options);          // file + stdout sinks (the ring buffer always exists)
    public static void AddSink(ILogSink sink);  public static void RemoveSink(ILogSink sink);
    public static void SetFrame(long frame);   public static void SetTick(long tick);
    public static void Flush();                 public static bool Flush(TimeSpan timeout);
    public static void Shutdown();
    public static RingBufferLogSink Ring { get; }               // console scroll-back + crash-report tail
}

// Fields: Log.Warn(LogCat.Assets, $"Missing texture", new LogField("path", p), new LogField("mount", m));
// Note: boxing Value allocates, so fields are for Warn and above, or for dev-only paths — not per-frame Info logs.
public readonly struct LogField { public LogField(string key, object? value); }

// Interpolated string handler, generic over a compile-time level marker (LogLevels.Trace … Fatal),
// so one type serves every level. If the category/level is disabled, the constructor sets
// shouldAppend = false and C# skips every Append call: nothing is formatted or allocated.
[InterpolatedStringHandler]
public ref struct LogHandler<TLevel> where TLevel : struct, ILogLevelMarker { /* wraps DefaultInterpolatedStringHandler (pooled) */ }

public interface ILogSink { LogLevel MinLevel { get; } void Write(in LogEntry entry); void Flush(); }

public static class Assert
{
    [Conditional("SAGE_DEV")] public static void Dev(bool condition, string message);   // dev-only check
    public static bool Ensure(bool condition, string message);   // logs Error + stack once per call site; returns condition; continues
    public static void Check(bool condition, string message);    // always on; Fatal + crash report if false
}
```

Usage:
```csharp
Log.Debug(LogCat.Assets, $"Loaded {path} in {ms:F1} ms");                 // free when Assets < Debug
if (!Assert.Ensure(mesh != null, "Renderable without mesh")) return;
Log.Every(LogCat.Physics, LogLevel.Warn, "tunnel", TimeSpan.FromSeconds(5), $"Body {id} tunnelled through wall");
```

Duplicate collapse: the log writer folds consecutive identical messages (same category, level and text) into one, followed by "(previous message repeated N more times)" when the run ends or after a second of quiet. All sinks see the collapsed stream.

Test code note: inside `namespace sage_engine…`, `Assert` means this class, not xUnit's. Test files add `using Assert = Xunit.Assert;` after their namespace declaration.

### 4.1 Crash reports
```csharp
public static class CrashReporter
{
    public static void Install();                   // AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException
    public static string Write(Exception? ex, string reason);   // returns the report path
}
```
A report is written to `logs/crash-YYYYMMDD-HHMMSS.txt` on `Fatal`, `Check` failure, or an unhandled exception. It contains:
- the exception and its inner exceptions, with stack traces;
- the last 2,000 log entries (ring buffer);
- the build config, `developer` level, engine version and game id;
- the loaded modules and mods in load order;
- all non-default cvars;
- the OS, .NET runtime, GPU adapter name (if the client started) and memory stats.

Then all sinks are flushed. In dev with a debugger attached, `Debugger.Break()` comes first.

### 4.2 CVars and console commands
```csharp
[Flags] public enum CVarFlags { None = 0, Archive = 1, Cheat = 2, DevOnly = 4, ReadOnly = 8 /* later: Replicated */ }

public sealed class CVar<T> : CVar                              // T: bool, int, float, string, enum
{
    public T Value { get; set; }                                 // from code: clamped to the range (with a warning)
    public T Default { get; }
    // (base CVar) Name, Help, Flags, ValueString, IsDefault, TrySet(string, out error), Reset(),
    // event Action<CVar> Changed  — raised only when the value actually changes
}

public sealed class CVarRegistry
{
    public CVar<T> Register<T>(string name, T defaultValue, CVarFlags flags, string help);
    public CVar<int> Register(string name, int defaultValue, CVarFlags flags, string help, int min, int max);
    public CVar<float> Register(string name, float defaultValue, CVarFlags flags, string help, float min, float max);
    public void RegisterCommand(string name, CVarFlags flags, string help, Action<ConsoleArgs> run);
    public bool Execute(string text, ExecSource source = ExecSource.Console);   // "name value", "cmd args", "a; b", // comments
    public bool ExecFile(string path, ExecSource source = ExecSource.Config);   // config.cfg, exec <file>
    public void SaveArchived(string path);                                      // temp file + move
    public IEnumerable<string> Complete(string prefix);
}
```
`ExecSource` is `Code`, `Console`, `Config` or `LaunchArgs`. Cheat and read-only checks apply to every source except `Code`. Paths are plain file paths under `UserPaths.Root` until the VFS exists (migration step 5).
Rules:
- `Cheat` cvars can only change when `sv_cheats 1`.
- `DevOnly` cvars and commands exist only in dev builds (`Debug`/`Development`); they're not compiled into `Shipping`.
- Console availability per build is defined in 01 §3.2 (`con_enable` in `Shipping`).
- Game and mod cvars are prefixed with their game id (`sandbox_spawn_rate`).

Built-in commands (engine): `help [name]`, `find <text>`, `cvarlist [prefix]`, `cmdlist`, `echo`, `exec <file>`, `log_level <cat|*> [level|default]`, `log_list`, `mem`, `crash` (`DevOnly`). Host/editor: `quit`, `stat <fps|mem|all|none>`, `clear`, `toggleconsole`. `profile_start`/`profile_stop` come with the profiler (§4.4).

Launch arguments (`+name value`, `+command args`) and `config.cfg` run **after every cvar and command is registered**, so `+stat fps` works from the command line (01 §5.1).

### 4.3 Time
*Built in migration step 4 (`Sage.Core/Time.cs`), as record structs `TickTime(Tick, Dt, SimTime)` and `FrameTime(Frame, Dt, Alpha, RealTime)`.*
```csharp
public readonly struct TickTime  { public long Tick; public float Dt; public double SimTime; }   // Fixed schedule
public readonly struct FrameTime { public long Frame; public float Dt; public float Alpha; public double RealTime; }
```
Systems receive these through their run context (03), never through `DateTime.Now` or MonoGame's `GameTime`. That's what makes headless tests and time scaling work.

### 4.4 Profiler
*Built in migration step 4 (`Sage.Core/Diagnostics/Profiler.cs`). `World` wraps every phase (`Fixed.Gameplay`) and system (`Fixed.Gameplay/FaceCameraSystem`); the host adds `Frame.ImGui`. Per-frame totals and a ~20-frame moving average; enabled in dev builds, off in Shipping. Chrome-trace dump and Tracy are still later.*

*As built (per-thread tables, 2026-09-27, issue #11):* the tables are per thread. A world is ticked on one thread and the game's is the main thread, where `stat` and `sys_list` read, so the game sees what it always did; a world ticked on another thread — a test running in parallel, later a server or an editor play world — gets tables of its own instead of writing into a shared `Dictionary`, which corrupted it and allocated on the other world's behalf (test: AFullWorldStillAllocatesNothingPerTick, which caught it once tests ran in parallel). `Enabled` is one setting for the process.
```csharp
public static class Profiler
{
    public static Scope Begin(string name);       // using var _ = Profiler.Begin("AI.Think"); Scope is a struct; near-zero cost when profiling is off
    public static void EndFrame();
}
```
- Scopes are recorded per frame into a fixed-size buffer (no allocations).
- The host wraps every phase and system automatically. The overlay shows ms per phase/system, allocations per frame and GC counts.
- `profile_start`/`profile_stop` dump a Chrome-trace JSON (`about://tracing` / Perfetto) to `logs/`.
- Later: Tracy integration.

### 4.5 Jobs
*Arrives with async asset loading (migration step 5, 05 §3.4); not built in step 2.*
```csharp
public sealed class JobSystem
{
    public JobHandle Schedule(Action job, JobHandle dependsOn = default);
    public JobHandle ScheduleParallelFor(int count, int batchSize, Action<int> body, JobHandle dependsOn = default);
    public void Complete(JobHandle handle);
    public void RunOnMainThread(Action action);   // queued; drained at the start of the next frame
}
```
Built on the .NET thread pool (`Task`/`Parallel.For`), with no custom fibers (C# doesn't have them; survey §2.8).
- **Uses:** asset decoding, terrain generation, pathfinding requests, and parallel-safe systems.
- **Rules:**
  - no MonoGame calls off the main thread;
  - jobs don't touch `World` structure (they write into their own buffers; results are applied on the main thread);
  - `Log` is safe from any thread.

### 4.6 GC and allocation rules
- Hot paths (Fixed and Frame systems, rendering, logging when enabled) must not allocate in steady state:
  - no LINQ;
  - no `ToList()`/`ToArray()` snapshots;
  - no lambdas capturing variables per call;
  - no boxing (struct → interface, `params object[]`);
  - no string concatenation or formatting.
- Use `ArrayPool<T>`, pooled lists, `Span<T>`/`stackalloc`, and structs.
- **Measurement:** `GC.GetAllocatedBytesForCurrentThread()` is sampled per frame and shown in the overlay. A dev warning fires when steady-state frames allocate more than `mem_warn_bytes` for 60 frames in a row. The default is **0 (off)** for now: today's editor inspector and ImGui layer allocate every frame, so a 1 KB default would only produce noise. Turn it on to measure, and raise the default once the renderer and editor are rewritten. `mem` prints GC counts per generation and the heap size.
- `GCSettings.LatencyMode = SustainedLowLatency` during gameplay (cvar `mem_lowlatency`).

> **Built (2026-09-23): `wait` and the deferred statement queue (§4.2).** A console script paces
> itself: `wait [seconds]` stops the rest of a statement list, and the host pumps the queue once a
> frame in **real** time, so a paused game still runs its script. No argument means "next frame". A
> nested `exec` runs where it is written (its statements go to the front of the queue, not the back),
> and `wait_cancel` drops what is left. This is what turns `+cmd` launch arguments and `exec` files
> into automated checks; before it, every command that needed to happen later grew its own delay
> argument, which is what `screenshot <delay>` was.

## 5. Data flow

```
any thread ── Log.X(cat, $"...") ──► enabled? ─no─► (nothing formatted, nothing allocated)
                                        │yes
                                        ▼
                               LogEntry into a ConcurrentQueue
                                        │
                                        ▼
                     background writer thread ("log-writer")
                     duplicate collapse → sinks:
                       • file (batched, flushed per drain)
                       • stdout/debugger (while developer >= 1)
                       • ring buffer (always) ◄── console window reads it each frame
                                                  crash reports copy its tail
```

## 6. Threading and memory
- `Log.*` is thread-safe and lock-free for producers. One background writer thread feeds all sinks; sinks never run concurrently. The console window reads the ring buffer on the main thread (it copies only when the buffer's version changes).
- A **disabled** call formats and allocates nothing. An **enabled** call allocates its message string (formatted through the pooled `DefaultInterpolatedStringHandler`) and a queue slot. That's acceptable because enabled logs aren't per-frame hot paths; a pooled string arena was considered and dropped as not worth the complexity.
- The queue is soft-capped (`log_queue_size`, default 16,384). Over the cap, `Trace`/`Debug` entries are dropped and counted ("… N log entries dropped"); `Info` and above are always kept, so **producers never block**.
- `Fatal` and crash handling flush synchronously (with a timeout, so a stuck sink can't hang the process).

## 7. File formats
- **Log file:** one line per entry: `2026-09-22T14:03:11.412Z [+00:12.345] f=742 t=701 [main] WARN  Assets  Missing texture textures/goblin.png → placeholder (mount=game) (AssetServer.cs:212)`.
- **Crash report:** plain text with sections: Summary, Exception, Build, System, then registered sections (today: CVars (non-default), Modules / mods, GPU), then Log tail.
- **`config.cfg`:** `name "value"` lines (01).
- **Profiler dump:** Chrome trace event JSON.

## 8. Errors and fallbacks
- If the log file can't be opened (read-only folder), fall back to `%LOCALAPPDATA%/Sage/logs`, then to stdout only, with a one-time `Warn`.
- A sink that throws is removed and reported through the remaining sinks.
- The crash reporter must never throw. Each section is wrapped separately, so a failure in one still writes the rest.

## 9. Debug and tooling hooks
- **Cvars:** `developer` (`DevOnly`, defaults setter, 01 §3.2), `con_enable` (`Archive`; Shipping console), `log_keep`, `log_queue_size`, `log_console_level`, `log_file_level`, `sv_cheats`, `mem_warn_bytes`, `mem_lowlatency`.
- **Commands:** `help`, `find`, `cvarlist`, `cmdlist`, `echo`, `exec`, `log_level`, `log_list`, `mem`, `crash`; host/editor `quit`, `stat`, `clear`, `toggleconsole`. Later: `profile_start`, `profile_stop`.
- **Overlays:** `stat fps` (fps, average and worst ms over 0.5 s) and `stat mem` (bytes allocated per frame, heap, GC counts, dropped log entries), and `stat frame` (average ms per phase and system, from the profiler; built in step 4).
- **Later: visual logger** (Unreal's idea). `VLog.Shape(cat, entity, shape, color, text)` records world-space shapes with the tick. An editor timeline scrubs through them, which is especially useful for AI ("why did it path there?").

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `Console.WriteLine("Component cannot be null.")` in `Entity.addComponent` | **Done (step 2):** `Log.Error(LogCat.World, …)`. Still ignores the null (TODO #34: throw instead, with R1) |
| `Console.WriteLine("group: " + group)` in `EntityContext.getAllEntitiesFromListOfGroups` | **Done (step 2):** `Log.Trace(LogCat.World, …)`; the method was removed with `EntityContext` in step 3 |
| `Program.cs` (two lines) | **Done (step 2):** boot sequence: logging, crash reporter, cvars, config and launch args, then the game |
| (removed 2026-09-22: the unused `InputSystem.isDebug` / `key_binds["toggle_debug"]`) | The `ToggleConsole` action (08), active when the console is available (01 §3.2) |
| `EditorManager`'s commented-out `debugModes` | Overlay cvars (`stat …`, `r_drawbounds`, `phys_debug`) |

## 11. v1 scope vs later
- **v1** (✓ = built in migration step 2):
  - ✓ `Log` + categories + levels + the interpolated handler; `Once`/`Every`; duplicate collapse;
  - ✓ file (rotating), stdout and ring-buffer sinks;
  - ✓ the ImGui console window (level/category/text filters, auto-scroll, command input);
  - ✓ `Assert.Dev`/`Ensure`/`Check`;
  - ✓ crash reports;
  - ✓ cvars/commands + `config.cfg` + launch arguments;
  - ✓ the allocation counter + `stat fps`/`stat mem`;
  - ✓ `TickTime`/`FrameTime` and profiler scopes + `stat frame` (step 4);
  - `JobSystem` over the thread pool (step 5, with async asset loading).
- **Later:** ~~console command history and Tab completion~~ (built with #299: `ConsoleInput` in `Sage.Core` keeps up to 100 lines of Up/Down history with the draft restored, and Tab completes the first word against commands and cvars and later words against a cvar's enum or bool values and record ids; the ImGui console only forwards the keys; tests: History_WalksBackAndForthAndKeepsTheDraft, History_IsBounded, Tab_CompletesCommandsAndCVars, Tab_CompletesCVarValuesAndRecordIds; arguments are not completed by kind, so `exec` does not complete file names), editor log panel, Chrome-trace dump, Tracy, visual logger, structured-field search.

## 12. Multiplayer-later notes
- Add a `Replicated` cvar flag (server values pushed to clients, like Quake 3 `SERVERINFO`).
- Log entries gain a `world`/`role` field so server and client logs in one process can be told apart.

## 13. Open questions
- Use `Microsoft.Extensions.Logging` + `LoggerMessage` source generator instead of a custom `Log`? **No:** it's heavier, allocation-prone with structured state, and doesn't know about frames and ticks. It could be offered as an adapter sink later if tools want it.
- Should `Trace` be separate from `Debug` at all? Kept, because `log_level events trace` (every event) is too noisy for `Debug`.

## 14. Build steps
1. ✓ `Log`, `LogCat`, handlers, file/stdout/ring sinks; `Console.WriteLine` calls replaced (TODO R10).
2. ✓ Build configurations + `SAGE_DEV` symbol + `developer` cvar + console availability (with 01; TODO R10).
3. ✓ `CrashReporter` (TODO R10).
4. ✓ `CVarRegistry` + console commands + `config.cfg` + launch arguments + the ImGui console window (TODO R10).
5. ✓ The allocation counter and `stat fps`/`stat mem` (step 2); `Profiler` scopes + `stat frame` (step 4) (TODO F5, R2).
6. `JobSystem` (with 05 async loading, step 5).
