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

All of it lives in `Sage.Engine` (no MonoGame). The console *window* and overlays are drawn by client code (13) but read from these services.

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

## 4. Public API sketch

```csharp
public enum LogLevel { Trace, Debug, Info, Warn, Error, Fatal }

public sealed class LogCat
{
    public LogCat(string name, LogLevel defaultLevel = LogLevel.Info);
    public string Name { get; }
    public LogLevel MinLevel { get; set; }                     // set by the log_level cvar/command
    public bool IsEnabled(LogLevel level);
    // Engine categories: LogCat.Core, .Host, .Modules, .VFS, .Assets, .Records, .Shaders, .Render,
    // .Input, .World, .Events, .Physics, .Audio, .Animation, .UI, .Streaming, .Save, .AI,
    // .Gameplay, .Editor, .Mods
}

public static class Log
{
    // One overload set per level (Trace shown; Debug/Info/Warn/Error/Fatal are identical, and
    // Trace/Debug carry [Conditional("SAGE_DEV")]). Fields are fixed-arity overloads, not
    // `params LogField[]`, because a params array allocates on every call.
    [Conditional("SAGE_DEV")] public static void Trace(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler msg);
    [Conditional("SAGE_DEV")] public static void Trace(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler msg, in LogField f1);
    [Conditional("SAGE_DEV")] public static void Trace(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler msg, in LogField f1, in LogField f2);
    [Conditional("SAGE_DEV")] public static void Trace(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler msg, in LogField f1, in LogField f2, in LogField f3);
    // ... Debug, Info, Warn, Error, Fatal: same shapes

    // Spam control. The handler receives the key/interval too, and checks the rate-limit table
    // before formatting, so a suppressed message is never formatted.
    public static void Once (LogCat cat, LogLevel level, string key,
                             [InterpolatedStringHandlerArgument("cat", "level", "key")] ref RateLimitedLogHandler msg);
    public static void Every(LogCat cat, LogLevel level, string key, TimeSpan interval,
                             [InterpolatedStringHandlerArgument("cat", "level", "key", "interval")] ref RateLimitedLogHandler msg);

    public static void AddSink(ILogSink sink);
    public static void Flush();
}

public readonly struct LogField { public readonly string Key; public readonly object? Value; }  // implicit from (string, T) tuples.
// Note: boxing Value allocates, so fields are for Warn and above, or for dev-only paths — not per-frame Info logs.

// Interpolated string handler: if the category/level is disabled, the handler's constructor
// sets shouldAppend = false, and C# skips formatting entirely, so nothing is allocated.
[InterpolatedStringHandler]
public ref struct LogHandler { /* formats into a pooled buffer */ }

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

Duplicate collapse: the console and file sinks fold consecutive identical messages into one line with a count ("… ×120"), as consoles in browsers and many engines do.

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

public sealed class CVar<T>                                     // T: bool, int, float, string, enum
{
    public string Name { get; }
    public T Value { get; set; }                                 // validated (min/max/allowed values)
    public T Default { get; }
    public CVarFlags Flags { get; }
    public event Action<T>? Changed;                             // engine signal (04), main thread
}

public sealed class CVarRegistry
{
    public CVar<T> Register<T>(string name, T defaultValue, CVarFlags flags, string help, T? min = default, T? max = default);
    public void RegisterCommand(string name, CVarFlags flags, string help, Action<ConsoleArgs> run);
    public void Execute(string line);                           // "name value", "command args", "a; b"
    public void LoadConfig(VirtualPath path);                   // config.cfg
    public void SaveArchived(VirtualPath path);
}
```
Rules:
- `Cheat` cvars can only change when `sv_cheats 1`.
- `DevOnly` cvars and commands exist only in dev builds (`Debug`/`Development`); they're not compiled into `Shipping`.
- Console availability per build is defined in 01 §3.2 (`con_enable` in `Shipping`).
- Game and mod cvars are prefixed with their game id (`sandbox_spawn_rate`).

Built-in commands:
- `help`, `find <text>`, `cvarlist`, `exec <file>`;
- `log_level <cat|*> <level>`, `log_list`;
- `crash` (`DevOnly`, tests the reporter);
- `profile_start`/`profile_stop`;
- `mem` (GC stats).

### 4.3 Time
```csharp
public readonly struct TickTime  { public long Tick; public float Dt; public double SimTime; }   // Fixed schedule
public readonly struct FrameTime { public long Frame; public float Dt; public float Alpha; public double RealTime; }
```
Systems receive these through their run context (03), never through `DateTime.Now` or MonoGame's `GameTime`. That's what makes headless tests and time scaling work.

### 4.4 Profiler
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
- **Measurement:** `GC.GetAllocatedBytesForCurrentThread()` is sampled per frame and shown in the overlay. A dev warning fires when steady-state frames allocate more than `mem_warn_bytes` (default 1 KB) for more than 60 frames in a row. `mem` prints GC counts per generation and the heap size.
- `GCSettings.LatencyMode = SustainedLowLatency` during gameplay (cvar `mem_lowlatency`).

## 5. Data flow

```
any thread ── Log.X(cat, $"...") ──► enabled? ─no─► (nothing formatted, nothing allocated)
                                        │yes
                                        ▼
                               LogEntry into lock-free MPSC queue
                                        │
                     ┌──────────────────┼──────────────────────────┐
                     ▼                  ▼                          ▼
          background writer      main thread drain            ring buffer
          → file sink (batched)  → console / editor panel     (always, for crash reports)
          → stdout/debugger
```

## 6. Threading and memory
- `Log.*` is thread-safe and lock-free for producers. The file writer is one background thread; the UI sinks drain on the main thread once per frame.
- `LogHandler` formats into a pooled `char` buffer. Entries are pooled structs with string storage in a pooled arena; the queue has a fixed capacity (`log_queue_size`, default 16,384). If it's full, producers drop `Trace`/`Debug`, block for `Warn`+, and count the drops ("… 312 log entries dropped").
- `Fatal` and crash handling write synchronously.

## 7. File formats
- **Log file:** one line per entry: `2026-09-22T14:03:11.412Z [+00:12.345] f=742 t=701 [main] WARN  Assets  Missing texture textures/goblin.png → placeholder (mount=game) (AssetServer.cs:212)`.
- **Crash report:** plain text with sections (Exception / Log tail / Build / Modules / Mods / CVars / System).
- **`config.cfg`:** `name "value"` lines (01).
- **Profiler dump:** Chrome trace event JSON.

## 8. Errors and fallbacks
- If the log file can't be opened (read-only folder), fall back to `%LOCALAPPDATA%/Sage/logs`, then to stdout only, with a one-time `Warn`.
- A sink that throws is removed and reported through the remaining sinks.
- The crash reporter must never throw. Each section is wrapped separately, so a failure in one still writes the rest.

## 9. Debug and tooling hooks
- **Cvars:** `developer` (`DevOnly`, defaults setter, 01 §3.2), `con_enable` (`Archive`; Shipping console), `log_keep`, `log_queue_size`, `log_console_level`, `log_file_level`, `sv_cheats`, `mem_warn_bytes`, `mem_lowlatency`.
- **Commands:** `log_level`, `log_list`, `cvarlist`, `find`, `help`, `exec`, `crash`, `mem`, `profile_start`, `profile_stop`.
- **Overlays:** `stat fps` / `stat frame` / `stat mem`: frame time, per-phase ms, allocations, GC.
- **Later: visual logger** (Unreal's idea). `VLog.Shape(cat, entity, shape, color, text)` records world-space shapes with the tick. An editor timeline scrubs through them, which is especially useful for AI ("why did it path there?").

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `Console.WriteLine("Component cannot be null.")` in `Entity.addComponent` (`src/Sage.Engine/ECS/Entity.cs`) | `Log.Error(LogCat.World, …)` (with TODO #34: throw instead) |
| `Console.WriteLine("group: " + group)` in `EntityContext.getAllEntitiesFromListOfGroups` (`EntityContext.cs:101`) | Removed with that method (TODO #36) or `Log.Trace(LogCat.World, …)` |
| (removed 2026-09-22: the unused `InputSystem.isDebug` / `key_binds["toggle_debug"]`) | The `ToggleConsole` action (08), active when the console is available (01 §3.2) |
| `EditorManager`'s commented-out `debugModes` | Overlay cvars (`stat …`, `r_drawbounds`, `phys_debug`) |

## 11. v1 scope vs later
- **v1:**
  - `Log` + categories + levels + the interpolated handler;
  - file, stdout and ring-buffer sinks;
  - the in-game console sink with an ImGui window;
  - `Assert.Dev`/`Ensure`/`Check`;
  - crash reports;
  - cvars/commands + `config.cfg`;
  - `TickTime`/`FrameTime`;
  - profiler scopes + overlay;
  - `JobSystem` over the thread pool;
  - the allocation counter.
- **Later:** editor log panel, Chrome-trace dump, Tracy, visual logger, structured-field search.

## 12. Multiplayer-later notes
- Add a `Replicated` cvar flag (server values pushed to clients, like Quake 3 `SERVERINFO`).
- Log entries gain a `world`/`role` field so server and client logs in one process can be told apart.

## 13. Open questions
- Use `Microsoft.Extensions.Logging` + `LoggerMessage` source generator instead of a custom `Log`? **No:** it's heavier, allocation-prone with structured state, and doesn't know about frames and ticks. It could be offered as an adapter sink later if tools want it.
- Should `Trace` be separate from `Debug` at all? Kept, because `log_level events trace` (every event) is too noisy for `Debug`.

## 14. Build steps
1. `Log`, `LogCat`, `LogHandler`, file/stdout/ring sinks; replace the existing `Console.WriteLine` calls (TODO R10, #34, #36).
2. Build configurations + `SAGE_DEV` symbol + `developer` cvar + console availability (with 01; TODO R10).
3. `CrashReporter` (TODO R10).
4. `CVarRegistry` + console commands + `config.cfg` + the ImGui console window (TODO R10).
5. `Profiler` scopes + overlay; the allocation counter (TODO F5).
6. `JobSystem` (with 05 async loading).
