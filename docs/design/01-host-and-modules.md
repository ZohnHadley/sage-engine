# 01 — Host, Modules and the Main Loop

## 1. Purpose and scope
The host owns the process: it parses arguments, starts core services, mounts content, loads modules (engine, framework, game), creates worlds, runs the main loop and shuts everything down. It also defines the **build configurations** and how the **console** and the `developer` cvar replace a separate dev mode.

Not in scope: what the services do (02), how worlds run systems (03).

## 2. Research basis
- The engine owns the loop and games plug in: Quake 2 game DLL function tables, Source `server.dll`/`client.dll`, Unreal modules (survey §1.1, §1.3, §2.2).
- Module registration with declared dependencies and Init/Shutdown, not a DI container: Unreal `IModuleInterface`, Bevy plugins, O3DE Gems (survey §2.2, §2.5, §2.6, §3.6).
- Build-enforced boundaries: Unreal module types (Runtime/Editor/DeveloperTool), Unity asmdef (survey §2.2, §2.3).
- Fixed timestep with interpolation: Fiedler "Fix Your Timestep" (survey §3.1).
- Debug/Development/Shipping configurations: Unreal's build configurations; Source's `-dev` / `developer 1` (survey §1.3, §2.2).

## 3. Concepts

### 3.1 Two-level module system
- **Assemblies (csproj) mark layer boundaries**, where the compiler must enforce the rules:

  | Assembly | May reference | Must not reference |
  |---|---|---|
  | `Sage.Engine` (simulation, core, world, assets) | .NET, Bepu, SharpGLTF | MonoGame, anything below |
  | `Sage.Client` (render, audio, input, UI) | `Sage.Engine`, MonoGame | `Sage.Editor` |
  | `Sage.Framework` (gameplay modules, simulation side) | `Sage.Engine` | MonoGame, `Sage.Client`, `Sage.Editor` |
  | `Sage.Framework.Client` (camera rigs, cue playback, HUD helpers) | `Sage.Framework`, `Sage.Client` | `Sage.Editor` |
  | `Sage.Editor` | everything | — |
  | `Sage.Host` (exe) | everything runtime; `Sage.Editor` only in the editor host | — |
  | Game assemblies | `Sage.Framework`, `Sage.Framework.Client`, `Sage.Engine`, `Sage.Client` (a game can split its own sim/client parts the same way) | `Sage.Editor` (game editor extensions go in a separate `MyGame.Editor` assembly) |

- **`IModule`s are logical units inside those assemblies** (Renderer, Physics, Audio, Streaming, Abilities…). Each declares its dependencies and gets `Init`/`Shutdown` in dependency order. Games can replace or disable engine modules (Bevy's `DefaultPlugins` idea) without touching engine code.

### 3.2 Build configurations, the console and `developer`
There is **no separate "dev mode"**. Two mechanisms cover everything:

1. **Build configurations (compile time)** decide what code exists in the binary. That's the only way to make debug code cost nothing in the released game.

   | | `Debug` | `Development` | `Shipping` |
   |---|---|---|---|
   | Optimizations | off | on | on |
   | `SAGE_DEV` compile symbol ("dev builds") | yes | yes | **no** |
   | `Log.Trace`/`Debug`, `Assert.Dev`, `DebugDraw` | compiled in | compiled in | **stripped** |
   | `DevOnly` cvars and commands (hot reload, `ent_fire`, `r_wireframe`, `crash`…) | compiled in | compiled in | **not compiled** |
   | ImGui dev tools (windows, overlays) | yes | yes | no |
   | Default for `developer` | `1` | `0` | n/a (the cvar is `DevOnly`) |

2. **The console and cvars (runtime)** switch individual features on and off. The **`developer`** cvar (`DevOnly`, as in Source's `developer 1`) is only a *defaults setter*. When it changes, it sets the defaults of:
   - log verbosity (`Debug` instead of `Info`) and the stdout sink;
   - hot reload (`asset_hotreload`, `mat_hotreload`, `rec_hotreload`);
   - the `dev_override/` mount (read at boot).

   Every feature keeps its **own cvar**, so one can be turned on without the rest. Settings needed before the console exists are passed at launch with the same syntax: `+developer 1`, `+log_level assets trace` (§5.1 step 1).

**Console availability:**

| | Dev builds | `Shipping` |
|---|---|---|
| Console | always available (`~`) | off by default. `con_enable 1` (`Archive`, set from a settings option or `+con_enable 1`) makes it available |
| Normal commands (`bind`, `save`, `log_level`, `mod_conflicts`, `stat fps`) | yes | yes |
| Cheats (`god`, `noclip`, `give`) | when `sv_cheats 1` | when `sv_cheats 1` (Bethesda/Daggerfall-style) |
| `DevOnly` cvars/commands | yes | don't exist |

Keeping a restricted console in Shipping suits a modding-friendly RPG: modders debug with it, and `mod_conflicts`/`log_level` work for players' bug reports.

### 3.3 Game manifest (`game.json`)
Like Source's `gameinfo.txt`: tells the host what to mount and load.

```json
{
  "name": "Sandbox",
  "id": "sandbox",
  "assembly": "Sandbox.dll",
  "mounts": ["content"],
  "modsDirectory": "mods",
  "defaultMap": "maps/test_valley",
  "modules": { "disable": [], "add": [] }
}
```

`id` is the game's record namespace (05). The host always mounts engine content, then framework content, first. The game's `mounts` are resolved relative to the manifest and mounted after them, in order (later wins). Mods come after the game, in load order (17). See 05 §3.1.

## 4. Public API sketch

```csharp
public enum ModuleKind { Runtime, Editor, Tool }

public interface IModule
{
    string Name { get; }
    ModuleKind Kind { get; }
    IReadOnlyList<Type> Dependencies { get; }   // other IModule types
    void Init(ModuleContext ctx);                // register services, systems, record types, cvars, loaders (nothing else)
    void Start(ModuleContext ctx) { }            // after records are loaded and the GPU exists: read records, load engine-scope assets
    void Shutdown();
}

public sealed class ModuleContext
{
    public Engine Engine { get; }
    public ModuleRegistry Registry { get; }       // add systems, record schemas, asset loaders, console commands
    public T Get<T>() where T : class;            // services registered by modules this one depends on
    public void Provide<T>(T service) where T : class;
}

public interface IGameModule : IModule
{
    void OnWorldCreated(World world);             // install GameRules, load the default map, etc.
}

public sealed class Engine                        // process-wide services; created by the host, passed in
{
    public BuildConfig Config { get; }
    public CVarRegistry CVars { get; }
    public VirtualFileSystem Vfs { get; }
    public AssetServer Assets { get; }
    public RecordStore Records { get; }
    public JobSystem Jobs { get; }
    public EngineSignals Signals { get; }
    public IReadOnlyList<World> Worlds { get; }
    public World CreateWorld(string name);
    public void DestroyWorld(World world);
}
```

`ModuleContext.Get<T>` only resolves services from declared dependencies. Asking for anything else throws at init, which keeps the dependency list honest.

`Engine` lives in `Sage.Engine`, so it holds only core services. Client services (`Renderer`, `InputDevices`, `AudioSystem`) are provided by their client modules with `ctx.Provide(...)`, and consumers declare a dependency on those modules. `Log` is static (02) because it must work before `Engine` exists and from any thread.

## 5. Lifecycle

### 5.1 Boot sequence
```
Program.Main(args)
 1. Parse args      -game <dir>; +<cvar> <value> / +<command> <args> (applied after config.cfg)
 2. Core services   Log (sinks: stdout + file first, console later), crash handler, BuildConfig
 3. CVars           defaults → config.cfg (Archive cvars) → +cvar args
 4. VFS             mount engine content → framework content → game.json mounts → mods in load order (17)
                    (+ dev_override/ when developer ≥ 1, dev builds); user:// resolved (05 §3.1)
 5. Modules         load the game assembly (+ trusted mod assemblies, later, 17)
                    → collect DefaultModules + game.json add/disable + game module
                    → topological sort by Dependencies (cycle = fatal, logged with the cycle)
                    → Init in order (Runtime; Editor only in the editor host).
                      Init only REGISTERS (schemas, loaders, systems, cvars, services); it must not
                      read records or assets yet
 6. Records         load + merge + validate all record files against the registered schemas (05)
 7. Graphics        MonoGame Game created; GraphicsDevice ready
    Start           modules Start in dependency order (records and GPU now available)
 8. World           Engine.CreateWorld("main") → IGameModule.OnWorldCreated(world)
 9. Main loop       (5.2)
10. Shutdown        worlds destroyed → modules Shutdown in reverse order → Archive cvars saved
                    → asset scopes released → logs flushed last
```

Each step is logged under `LogCat.Host` with its duration. A failure in steps 1–6 shows a native message box (if a window can't be created yet), writes the crash report (02) and exits non-zero.

### 5.2 Main loop (fixed tick + interpolation)
MonoGame's own fixed-step mode catches up by calling `Update` repeatedly without interpolation and couples badly with vsync, so the host sets `IsFixedTimeStep = false` and runs its own accumulator:

```
each MonoGame Update/Draw pair:
  frameDt   = min(realElapsed, maxFrameTime)          // sim_maxframetime, default 0.25 s → no spiral of death
  frameDt  *= host_timescale                          // dev cvar
  input devices poll; input contexts resolve; Look added to the view angles   (08 §3.4)
  accumulator += frameDt
  while accumulator >= tickDt:                        // tickDt = 1 / sim_tickrate (60 Hz)
      sample PlayerCommand for this tick              (08)
      for each world: world.RunFixed(tickDt)           // Fixed phases (03)
      accumulator -= tickDt
  alpha = accumulator / tickDt                        // interpolation factor for rendering
  for each world: world.RunFrame(frameDt, alpha)       // FrameUpdate → Extract → Render → Overlay
```

- `sim_tickrate` default **60 Hz**; `sim_maxframetime` default 0.25 s; `host_timescale` (DevOnly) default 1.
- `Extract` interpolates transforms between the previous and current tick using `alpha` (06).
- Pausing sets the world's time scale to 0; Frame systems (camera, UI) keep running.
- Vsync is a cvar (`r_vsync`). With vsync off, `host_maxfps` caps the frame rate.

### 5.3 Worlds
Several worlds can exist (Warband's overworld and battle scene, the editor's edit/play worlds, tests). The host runs every *active* world each frame. Inactive worlds are kept but not ticked.

## 6. Threading and memory
- Boot, module `Init`, and the loop run on the main thread. `GraphicsDevice` is main-thread only (MonoGame).
- Worlds tick on the main thread in v1. Systems may use the job layer (02) internally.
- Future pipelining (survey §2.8, Destiny/Bevy): simulation on a worker thread, rendering on the main thread, with `RenderSnapshot` as the hand-off. The loop above is shaped so that change touches only the host.

## 7. File formats
- `game.json` (above).
- `config.cfg`: text, one `cvar value` per line, written from `Archive` cvars on shutdown (02).
- `autoexec.cfg` (optional): console commands run after boot, as in Quake/Source.

## 8. Errors and fallbacks
- Missing or invalid `game.json` → fatal, with a message naming the path.
- Module dependency cycle or missing dependency → fatal, with the module chain in the message.
- A module's `Init` throws → fatal in `Shipping`. In dev builds, that module and its dependents are disabled and logged at `Error`, and boot continues.
- The game assembly fails to load → fatal, with the `AssemblyLoadContext` error and the probed path.

## 9. Debug and tooling hooks
- **Cvars:** `sim_tickrate`, `sim_maxframetime`, `host_timescale` (DevOnly), `host_maxfps`, `r_vsync`, `developer`.
- **Commands:** `modules` (list with state and init time), `worlds`, `quit`, `restart_world`.
- **Overlay:** frame time, ticks this frame, alpha, and the ms per phase (from the profiler, 02).
- **Log category:** `Host`, `Modules`.

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `engine/Program.cs` | `Sage.Host` `Program.Main`: boot sequence 5.1 |
| `engine/Game1.cs` | The host's MonoGame `Game` subclass: loop 5.2 only. The bunny spawn and billboard test move to `games/Sandbox` |
| `Game1.Update` Escape check (TODO #37) | An input action (08), not a direct `Keyboard.GetState()` |
| `engine/Classes/EditorManager.cs` | Window size → `r_width`/`r_height` cvars applied by the host. Camera ownership → editor (15) |
| Singletons (`EntityContext.getInstance()`, `InputSystem.getInstance()`, `ModelRendererSystem.getInstance()`) | Services on `Engine` or per-`World`, passed in (TODO R1) |

## 11. v1 scope vs later
- **v1:**
  - boot sequence;
  - `IModule` with dependency sort;
  - `game.json`;
  - the fixed-tick loop with interpolation;
  - one world at a time;
  - build configurations, `developer`, console availability rules;
  - `config.cfg`.
- **Later:**
  - several active worlds (Warband overworld/battle);
  - `autoexec.cfg`;
  - disabling modules on `Init` failure;
  - a pipelined sim thread;
  - game-assembly hot reload (serialize → reload → restore, as Flax does). .NET Hot Reload covers most edits until then.

## 12. Multiplayer-later notes
A dedicated server is a different host (`Sage.Host.Server`) running the same boot without client modules, with a server `World` and no graphics. The loop's fixed tick is already the tick the network would index by.

## 13. Open questions
- ~~Does the framework's client-side code live in a folder of `Sage.Framework` or in its own assembly?~~ **Decided: a separate `Sage.Framework.Client` assembly** (§3.1 table), so the framework's simulation part stays headless and compiler-checked.
- Should `tickDt` be exactly 1/64 s (exactly representable in binary) instead of 1/60? Not needed without lockstep networking; 60 Hz kept.

## 14. Build steps
1. Solution split into `Sage.Engine`, `Sage.Client`, `Sage.Editor`, `Sage.Host`, `games/Sandbox`, `tests/Sage.Tests` (ARCHITECTURE §9 step 1).
2. `Engine` object + `IModule` + dependency sort; convert singletons (TODO R1, R8).
3. Custom fixed-tick loop + interpolation alpha (TODO R2).
4. `game.json` + VFS mounts (with 05).
5. Build configurations + `developer` cvar + console availability (TODO R10).
