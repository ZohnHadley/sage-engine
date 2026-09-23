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
  | `Sage.Host` (exe) | everything runtime; `Sage.Editor` only in the editor host (*today:* the one exe is still game + editor, so it references `Sage.Editor` until the editor host exists, 15/F28) | — |
  | Game assemblies | `Sage.Framework`, `Sage.Framework.Client`, `Sage.Engine`, `Sage.Client` (a game can split its own sim/client parts the same way) | `Sage.Editor` (game editor extensions go in a separate `MyGame.Editor` assembly) |

  *Today* this is the intended layering; only some of it exists. The assemblies that are actually in the solution are `Sage.Engine`, `Sage.Client`, `Sage.Editor`, `Sage.Host`, `games/Sandbox`, `games/Sandbox.Client` and `tests/Sage.Tests` — no `Sage.Framework` or `Sage.Framework.Client` yet (the gameplay feature modules live in `Sage.Engine`, 16 §3.1, until there's enough in them to split out). `Sage.Engine`'s package references are **`Friflo.Engine.ECS` and `BepuPhysics`**, not SharpGLTF (glTF mesh loading is still to build, 05 §14 step 2). The rest of the reference graph matches the table: `Sage.Client`, `Sage.Editor` and `Sage.Host` add MonoGame; `Sage.Host` references `Sage.Editor` for the reason given above; `games/Sandbox` references **only `Sage.Engine`** and `games/Sandbox.Client` adds `Sage.Client` (R15, below); `tests/Sage.Tests` references `Sage.Engine` and the Sandbox's simulation half.

> **A game splits the same way the engine does (2026-09-23, R15).** `games/Sandbox` is the
> simulation — scene, rules, spawning, combat log — and references `Sage.Engine` and nothing else, so
> the compiler enforces what used to be a habit. `games/Sandbox.Client` holds the HUD and the one
> prefab part that builds a mesh at run time. The point is not tidiness: the game's own rules can now
> be ticked in a headless test, and `tests/Sage.Tests/World/SandboxSimulationTests.cs` found a real
> bug the first time it ran (review #59). A dedicated server would run the first and not the second.
>
> Exactly one `IGameModule` per game, in the simulation half. The client half is a plain `IModule`,
> loaded through `game.json`'s **`modules.add`** — which the manifest had declared since step 5 and
> the host had been warning was unimplemented. A part that only the client registers is declared
> `Prefabs.Optional(name)` by the simulation, so headless it is skipped rather than reported missing.

> **Done for the engine (2026-09-23, R15).** `GameplayModule` — nine record types, three cvars, seven
> console commands, six input actions, nine systems — is now `AttributesModule`, `CharacterModule`,
> `AnimationModule`, `CombatModule`, `ItemsModule` and `AIModule`, each owning its own registrations
> (16 §3.1). They are logical modules inside `Sage.Engine`, which is what the two-level design below
> is for. Still to do: the same split for games, `Game` + `Game.Client`, so a game's own simulation
> can be tested headlessly the way the engine's can.

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
  "assembly": "bin/{config}/net8.0/Sandbox.dll",
  "mounts": ["content"],
  "modsDirectory": "mods",
  "defaultMap": "maps/test_valley",
  "modules": { "disable": [], "add": ["../Sandbox.Client/bin/{config}/net8.0/Sandbox.Client.dll"] }
}
```

`id` is the game's record namespace (05), so it follows record-id rules (lower case, digits, `_`, `.`, `-`). `assembly` is relative to the manifest and holds the game's **one** `IGameModule`, its simulation; `{config}` becomes the build configuration (`Debug`/`Development`/`Shipping`), so a dev build loads the matching game build. `modules.add` lists further assemblies whose every public `IModule` is added after it — that is how a game ships its client half separately (R15), and how a trusted C# mod will ship a module later (17). Paths resolve the same way, `{config}` and all. The host always mounts engine content, then framework content, first. The game's `mounts` are resolved relative to the manifest and mounted after them, in order (later wins). Mods come after the game, in load order (17). See 05 §3.1.

The host finds the game with `-game <folder>`; without it, dev builds use `games/Sandbox` (found by walking up to `Sage.sln`) and other builds use `game/` next to the exe.

*As built (step 5, `modules.add` 2026-09-23):* `src/Sage.Engine/Core/GameManifest.cs`, `games/Sandbox/game.json`. `name`, `id`, `assembly`, `mounts`, `modules.disable` and `modules.add` work — the Sandbox uses `add` for its client half, and each added module still goes through the `disable` check, so one can be turned off by name. `modsDirectory` and `defaultMap` are parsed but unused until mods (17) and maps. A missing assembly named in `add` stops the host with a message, the same as a missing game assembly. There is no framework content mount yet.

## 4. Public API sketch

```csharp
public enum ModuleKind { Runtime, Editor, Tool }

public interface IModule                         // default interface members: implement only what you need
{
    string Name => GetType().Name;
    ModuleKind Kind => ModuleKind.Runtime;
    IReadOnlyList<Type> Dependencies => [];      // other IModule types
    void Init(ModuleContext ctx);                // register services, systems, record types, cvars, loaders (nothing else)
    void Start(ModuleContext ctx) { }            // after records are loaded and the GPU exists: read records, load engine-scope assets
    void OnWorldCreated(World world) { }         // install this module's per-world resources and systems
    void Shutdown() { }
}

public sealed class ModuleContext
{
    public Engine Engine { get; }                 // registration goes through Engine (CVars, Records...) today;
                                                  // a ModuleRegistry wrapper comes if module unloading ever needs it
    public T Get<T>() where T : class;            // services from the host or from modules this one depends on
    public void Provide<T>(T service) where T : class;
}

public interface IGameModule : IModule { }        // marks the game assembly's one entry point (GameRules, default map...)

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

`ModuleContext.Get<T>` only resolves services from declared dependencies (plus services the host provides with `ModuleManager.ProvideHostService`, such as `ClientHost`). Asking for anything else throws, which keeps the dependency list honest.

**Deviation (step 5):** `OnWorldCreated` is on every `IModule`, not only `IGameModule`. Client modules need a per-world hook too: `ClientModule` installs the `RenderSnapshot` resource and the Extract/Render systems in every world (06 §3.11). It runs in dependency order, so engine modules have set a world up before the game spawns into it.

`Engine` lives in `Sage.Engine`, so it holds only core services. Client services (`Renderer`, `InputDevices`, `AudioSystem`) are provided by their client modules with `ctx.Provide(...)`, and consumers declare a dependency on those modules. `Log` is static (02) because it must work before `Engine` exists and from any thread.

## 5. Lifecycle

### 5.1 Boot sequence
```
Program.Main(args)
 1. Parse args      -game <dir>; +<cvar> <value> / +<command> <args> (kept for step 6)
 2. Core services   Log (file + stdout; the ring buffer always exists), crash handler, BuildConfig
 3. Core cvars      the engine's own cvars and commands (02 §4.2)
 4. VFS             mount engine content → framework content → game.json mounts → mods in load order (17)
                    (+ dev_override/ when developer ≥ 1, dev builds); user:// resolved (05 §3.1)
 5. Modules         load the game assembly (+ trusted mod assemblies, later, 17)
                    → collect DefaultModules + game.json add/disable + game module
                    → topological sort by Dependencies (cycle = fatal, logged with the cycle)
                    → Init in order (Runtime; Editor only in the editor host).
                      Init only REGISTERS (schemas, loaders, systems, cvars, services); it must not
                      read records or assets yet
 6. Config + args   config.cfg (Archive cvars), then +args in order. Runs after every module has
                    registered its cvars and commands, so `+stat fps` or a game cvar works at launch
    Records         load + merge + validate all record files against the registered schemas (05)
 7. Graphics        MonoGame Game created; GraphicsDevice ready
    Start           modules Start in dependency order (records and GPU now available)
 8. World           Engine.CreateWorld("main") → IGameModule.OnWorldCreated(world)
 9. Main loop       (5.2)
10. Shutdown        worlds destroyed → modules Shutdown in reverse order → Archive cvars saved
                    → asset scopes released → logs flushed last
```

*As built (step 5):* `src/Sage.Host/Program.cs` does steps 1–5: args, `game.json` (a missing or invalid manifest logs, writes a crash report and exits 1), logging and the crash reporter (the user folder is named after the game id), core cvars, the VFS (engine content in namespace `sage`, then the game's mounts), then modules (the defaults `PhysicsModule`, the six gameplay feature modules and `ClientModule`, in that order, unless `game.json` disables any of them, plus the game assembly; `Init` in dependency order). `Game1.Initialize` does the rest once the graphics device exists: input devices and actions (08) and the host's cvars and commands, then `config.cfg`, then records, then the host services (`ClientHost`, `InputDevices`, `InputActions`), then module `Start`, then the main world, and finally the `+launch` commands. *Step 6:* each frame the host polls devices, resolves actions, feeds the `CommandLatch`, and hands every fixed tick its `PlayerCommand` through the world's `PlayerInput` resource (08 §3.6).

**`quit [seconds]`** takes a delay (2026-09-23), so a launch line that says `+quit 30` runs for
thirty seconds and then exits, which is what everyone writing one assumes. It used to ignore the
number and exit during startup — indistinguishable, from the outside, from a game that cannot stay
open. With `wait` (02 §4.2) either `+quit 30` or `+wait 30 +quit` bounds a run; Escape, the console's
`quit` and the window's close button all end it sooner.

**Deviation:** `+args` run **after** the main world exists, not with `config.cfg` in step 6, as Source runs `+map` last. That way `+rec_get prefab bunny`, `+ent_list` or `+pause` see the loaded game. Launch args still override `config.cfg`. What they can't do is change a cvar before a module's `Start` reads it — and, more sharply, **a cvar registered in `Start` misses `config.cfg` entirely**, because that runs between `Init` and `Start`: an `Archive` cvar registered late silently never reads its saved value (review #58). Registration belongs in `Init`, which is what §4 already says. Shutdown destroys the worlds, then shuts modules down in reverse order, saves Archive cvars and flushes the log last. Unknown `-options` are logged as warnings.

Each step is logged under `LogCat.Host` with its duration. A failure in steps 1–6 shows a native message box (if a window can't be created yet), writes the crash report (02) and exits non-zero.

### 5.2 Main loop (fixed tick + interpolation)
MonoGame's own fixed-step mode catches up by calling `Update` repeatedly without interpolation and couples badly with vsync, so the host sets `IsFixedTimeStep = false` and runs its own accumulator:

```
each MonoGame Update/Draw pair:
  realTime += realElapsed                             // unscaled, for host_exitafter, screenshot delay, shader Time
  frameDt   = min(realElapsed, maxFrameTime)          // sim_maxframetime, default 0.25 s → no spiral of death
  frameDt  *= host_timescale                          // dev cvar
  input devices poll; input contexts resolve; Look added to the view angles   (08 §3.4)
  accumulator += frameDt
  while accumulator >= tickDt:                        // tickDt = 1 / sim_tickrate (60 Hz)
      sample PlayerCommand for this tick              (08)
      for each world: world.RunFixed(tickDt)           // Fixed phases (03)
      accumulator -= tickDt
  alpha = accumulator / tickDt                        // interpolation factor for rendering
  for each world: world.RunFrame(frameDt, alpha, realTime)   // FrameUpdate → Extract → Render → Overlay
```

- `sim_tickrate` default **60 Hz**; `sim_maxframetime` default 0.25 s; `host_timescale` (DevOnly, Cheat) default 1.
- **Built in step 4** (`FixedStepClock` in `Sage.Engine/Core/Time.cs`, loop in `Game1`). Measured with `host_exitafter 3` and vsync off: ~2,250 fps with exactly 60.0 ticks/s at `sim_tickrate 60` and 5.0 ticks/s at `sim_tickrate 5`.
- `Extract` interpolates transforms between the previous and current tick using `alpha` (06).
- Pausing (`pause`, `World.Paused`) skips `WhenNotPaused` systems; ticks keep counting and Frame systems (camera, UI, rendering) keep running.
- Vsync is a cvar (`r_vsync`, Archive). `host_maxfps` (a frame cap with vsync off) is **not built yet**.
- `host_exitafter <seconds>` (DevOnly) quits after that much real time and logs frame and tick counts, for automated smoke runs.

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
- **Cvars:** `sim_tickrate`, `sim_maxframetime`, `host_timescale` (DevOnly), `host_maxfps` (later), `r_vsync`, `host_exitafter` (DevOnly), `developer`.
- **Commands:** `modules` (list with lifecycle state and dependencies; **done, step 5**; per-module init time is logged at `Debug`), `worlds`, `quit`, `restart_world`.
- **Overlay:** frame time, ticks this frame, alpha, and the ms per phase (from the profiler, 02).
- **Log category:** `Host`, `Modules`.

## 10. Mapping from today's code
| Today | Becomes |
|---|---|
| `src/Sage.Host/Program.cs` | `Sage.Host` `Program.Main`: boot sequence 5.1. **Done (step 5)** |
| `src/Sage.Host/Game1.cs` | The host's MonoGame `Game` subclass: loop 5.2 only. **Step 5:** the bunny spawn and billboard test moved to `games/Sandbox` (`SandboxModule`, `spawn` records); rendering setup moved to `ClientModule`. Still here: the dev camera and editor windows (15) |
| `Game1.Update` Escape check (TODO #37) | **Done:** the `Menu` action (08), bound to `Escape` in `engine_content/data/input.json`; `Game1.Update` closes the console if it's open, otherwise calls `Exit()`. Not a direct `Keyboard.GetState()` |
| `src/Sage.Editor/EditorManager.cs` | Window size → `r_width`/`r_height` cvars applied by the host. Camera ownership → editor (15) |
| Singletons (`EntityContext.getInstance()`, `InputSystem.getInstance()`, `ModelRendererSystem.getInstance()`, `EditorUI`/`EntityContextMenuUI`) | **Done (step 3):** `Engine` (cvars, worlds) and the main `World` are created in `Program` and passed in; `InputSystem`, `ModelRendererSystem` and the editor windows are instances owned by `Game1` (TODO R1). `Log`/`CrashReporter` stay static by design (02) |

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
1. ~~Solution split~~ **Done 2026-09-22** (ARCHITECTURE §7 step 1): `Sage.Engine`, `Sage.Client`, `Sage.Editor`, `Sage.Host`, `tests/Sage.Tests`. `games/Sandbox` waits for step 4 (`IGameModule`).
2. ~~`Engine` object + `IModule` + dependency sort; convert singletons~~ **Done** (TODO R1, R8): `Engine` + singleton removal in step 3; `IModule`, `ModuleManager`, `ClientModule` and the `games/Sandbox` game module in step 5.
3. ~~Custom fixed-tick loop + interpolation alpha~~ **Done 2026-09-22** (ARCHITECTURE §7 step 4; TODO R2).
4. ~~`game.json` + VFS mounts (with 05)~~ **Done 2026-09-22** (ARCHITECTURE §7 step 5).
5. ~~Build configurations + `developer` cvar + console availability~~ **Done 2026-09-22** (ARCHITECTURE §7 step 2; TODO R10).
