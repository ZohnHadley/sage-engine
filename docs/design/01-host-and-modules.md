# 01 — Host, Modules and the Main Loop

> **From the slice retrospective (2026-09-23), finding 3.** One module per feature (R15) cost one
> thing worth writing down: a new system is registered **twice** — in its module, and the module in the
> host's list. F21 forgot the second one once, and the symptom was a spell that silently did nothing.
> A discovery pass over `[Module]`-attributed types would remove it, and is not worth building until a
> game other than the Sandbox exists. See [`../history/vertical-slice-2026-09-23.md`](../history/vertical-slice-2026-09-23.md).

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
  | `Sage.Core` (kernel: cvars, log, VFS, records, plugins, declarations) | .NET | Friflo, Bepu, MonoGame, anything below |
  | `Sage.Simulation` (engine, app, World/ECS, prefabs, saves, levels, streaming) | `Sage.Core`, Friflo | Bepu, MonoGame, anything below |
  | `Sage.Physics3D` (Bepu space behind `IPhysicsWorld`, character movement) | `Sage.Simulation`, Bepu | MonoGame, anything below |
  | `Sage.Gameplay` (gameplay modules, simulation side; `Sage.Framework` in the first plan) | `Sage.Simulation` and below; physics only through `IPhysicsWorld` (#30; the `Sage.Physics3D` reference is for the plugin lists) | Bepu, MonoGame, `Sage.Client`, `Sage.Editor`, any kit |
  | `Sage.Client` (render, audio, input, UI) | `Sage.Gameplay` and below, MonoGame | `Sage.Editor` |
  | `Sage.Framework.Client` (camera rigs, cue playback, HUD helpers) | `Sage.Framework`, `Sage.Client` | `Sage.Editor` |
  | `Sage.Editor` | everything | — |
  | `Sage.Host` (exe) | everything runtime; `Sage.Editor` only in the editor host (*today:* the one exe is still game + editor, so it references `Sage.Editor` until the editor host exists, 15/F28) | — |
  | Game assemblies | `Sage.Core` … `Sage.Gameplay`, `Sage.Client`, kits (a game can split its own sim/client parts the same way) | `Sage.Editor` (game editor extensions go in a separate `MyGame.Editor` assembly) |

  *As built (issue #24, 2026-09-29):* the solution has `Sage.Core`, `Sage.Simulation`, `Sage.Physics3D`, `Sage.Gameplay`, `Sage.Client`, `Sage.Editor`, `Sage.Host`, `Sage.Cli`, `games/Sandbox`, `games/Sandbox.Client`, `games/Hello`, `tests/Sage.Testing` and `tests/Sage.Tests` — one namespace per assembly (`Sage.*`; REDESIGN §3.1 records where this differs from its first sketch). No `Sage.Framework.Client`; the one kit, `Sage.Kits.Rpg` and `Sage.Kits.Rpg.Client`, came with #27 (below). `Sage.Simulation`'s package reference is **`Friflo.Engine.ECS`** and `Sage.Physics3D`'s **`BepuPhysics`**; SharpGLTF (**`SharpGLTF.Core`**) was `Sage.Client`'s, which loads glTF meshes (R12), and is `Sage.Simulation`'s since #116, which reads skeletons and clips headlessly (12 "As built (skeletons and sampling)"); the client reaches it through that reference, and no SharpGLTF type is in the simulation's public API. `Sage.Gameplay` references `Sage.Physics3D` until physics sits behind `IPhysicsWorld` (#30). `Sage.Client`, `Sage.Editor` and `Sage.Host` add MonoGame; `Sage.Host` references `Sage.Editor` for the reason given above; a game's simulation half references **only the four base assemblies** (from `games/Directory.Build.props`) and `games/Sandbox.Client` adds `Sage.Client` (R15, below); `tests/Sage.Tests` references the base assemblies and the Sandbox's and Hello's simulation halves. The build enforces the table: no MonoGame in a simulation-only project (SAGE0024, the default for every engine project but the client, editor and host), no kit in a base assembly (SAGE0025), and `AssemblyLayeringTests` checks what each built base assembly references (test: NoBaseAssemblyReferencesALayerAboveIt).

  *As built (issue #27, 2026-09-29): kits.* `Sage.Kits.Rpg` (plugin `sage.kits.rpg`, simulation-only) and `Sage.Kits.Rpg.Client` (`sage.kits.rpg.client`) reference the base and are not base assemblies or base plugins. A game names the kits it is built on in `game.json` (`"kits": ["sage.kits.rpg"]`, `GameManifest.Kits`) and references them compile-time only; `SageApp` loads each before the game's assemblies, as `Sage.Kits.<Name>.dll` (the id with each part capitalised) from beside the game's assembly, its `modules.add` assemblies or the host, plus its `.Client` half when `SageAppOptions.LoadKitClients` (the windowed host). `build/Sage.Kits.targets` builds every kit and copies it beside `Sage.Host` and `sage` without a reference, so a game that names no kit loads none (test: HelloHasNoKitAndTheSandboxHasTheRpgKit). The layering is checked on the built assemblies (test: TheRpgKitIsBuiltOnTheBaseAndIsNotPartOfIt).

> **A game splits the same way the engine does (2026-09-23, R15).** `games/Sandbox` is the
> simulation — scene, rules, spawning, combat log — and references the base engine and nothing else, so
> the compiler enforces what used to be a habit. `games/Sandbox.Client` holds the HUD and the one
> prefab part that builds a mesh at run time. The point is not tidiness: the game's own rules can now
> be ticked in a headless test, and `tests/Sage.Tests/Games/SandboxSimulationTests.cs` found a real
> bug the first time it ran (review #59). A dedicated server would run the first and not the second.
>
> Exactly one `IGameModule` per game, in the simulation half. The client half is a plain `IModule`,
> loaded through `game.json`'s **`modules.add`** — which the manifest had declared since step 5 and
> the host had been warning was unimplemented. A part that only the client declares is declared
> `Prefabs.Optional(name)` by the simulation, so headless it is skipped rather than reported missing.

> **Done for the engine (2026-09-23, R15).** `GameplayModule` — nine record types, three cvars, seven
> console commands, six input actions, nine systems — is now `AttributesModule`, `CharacterModule`,
> `AnimationModule`, `CombatModule`, `ItemsModule`, `AbilitiesModule` (added with F21 the same day) and
> `AIModule`, each owning its own registrations
> (16 §3.1). They are logical modules inside `Sage.Gameplay` (inside `Sage.Engine` until issue #24), which is what the two-level design below
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
  "modules": { "disable": [], "add": ["../Sandbox.Client/bin/{config}/net8.0/Sandbox.Client.dll"] }
}
```

`id` is the game's record namespace (05), so it follows record-id rules (lower case, digits, `_`, `.`, `-`). `assembly` is relative to the manifest and holds the game's **one** `IGameModule`, its simulation; `{config}` becomes the build configuration (`Debug`/`Development`/`Shipping`), so a dev build loads the matching game build. `modules.add` lists further assemblies whose every public `IModule` is added after it — that is how a game ships its client half separately (R15), and how a trusted C# mod will ship a module later (17). Paths resolve the same way, `{config}` and all. The host always mounts engine content, then framework content, first. The game's `mounts` are resolved relative to the manifest and mounted after them, in order (later wins). Mods come after the game, in load order (17). See 05 §3.1.

The host finds the game with `-game <folder>`; without it, every build uses `game/` next to the exe, and with neither it stops with an error that says what to pass and, inside the repository, lists its games (issue #32, `GameManifest.Locate`; test: GameManifest_Locate_WithoutAGameIsAnErrorThatListsTheGames). Dev builds used to fall back to `games/Sandbox`, which let a forgotten `-game` run the wrong game silently. A game built with `Sage.Sdk` passes `-game` itself from `dotnet run` (MAKING_A_GAME §2).

*As built (step 5, `modules.add` 2026-09-23):* `src/Sage.Core/GameManifest.cs`, `games/Sandbox/game.json`. `name`, `id`, `assembly`, `mounts`, `modules.disable` and `modules.add` work — the Sandbox uses `add` for its client half, and each added module still goes through the `disable` check, so one can be turned off by name. `modsDirectory` is where the game's mods are found (phase 4j; 17, "As built (mods at boot, issue 4j-3)"). **`defaultMap` is gone (R19):** it was parsed and never read, so a game could set it and nothing would happen — and now that levels exist (15 §10a), a game loads one from its own module or scene record, which is where the Sandbox does it. **`scene`** (issue #29) names the engine `scene` record every world starts in (`"main"` or `"ns:main"`); one that is not a scene stops the load with the scenes there are (test: AStartSceneThatIsNotAScene_IsALoadError). A missing assembly named in `add` stops the host with a message, the same as a missing game assembly. There is no framework content mount yet.

*As built (plugins, 2026-09-27, issue #12):* every engine module is a **plugin** with an id and a version — `[Plugin("sage.gameplay.items", "0.1.0")]` (`src/Sage.Core/Declarations/Plugins.cs`); a module without the attribute is known by its class name at 0.0.0. Dependencies can be given by id with a version range, `[RequiresPlugin("sage.gameplay.combat", ">=0.1")]` (`*`, exact, comparators, `^` and `~`), so a plugin need not reference the other's assembly; a missing plugin, a version outside the range and two modules claiming one id each stop the host with a message saying which (test: AMissingOrTooOldDependencySaysWhich). `game.json`'s optional **`plugins`** list chooses the engine's simulation plugins by id or family (`sage.gameplay.*`) and pulls in what they require; left out it means all of them, and `[]` means none (test: NoPluginsAtAllIsAGameWithNoPhysicsAndNoGameplay). `disable` takes a plugin id as well as a class name. A module's `Kind` is enforced: Editor modules load only in an editor host, Tool modules only in a tool (test: AnEditorModuleLoadsOnlyInTheEditor). Every cvar, command, record type, prefab part, entity input and input action is recorded against the plugin whose `Init` or `Start` registered it (`Engine.Registrations`), and the `plugins` console command lists plugins, versions and requirements, or with an id what that plugin registered (test: EveryRegistrationIsRecordedAgainstItsPlugin). A prefab part registered twice now says whose it replaced; since issue #17 a second part with a taken id is an error instead, and systems are recorded too — each world's systems against the plugin that added them, replacements and disables against the plugin that made them (test: ReplaceAndDisableAreRecordedAgainstThePluginThatDidThem).

### One list of gameplay modules (2026-09-25)

The host used to hand-write its own sequence of `AddDefaultModule(new …Module())` calls while
`GameplayModules.AddGameplay()` held a second copy for games and tests. They agreed until they did not:
`LightsModule` was added to one and not the other, so every test registered the `light` prefab part and
the only build anybody plays did not, and point lights silently did nothing for a day.

The general shape is worth naming, because it is not about lighting: **a check that runs against a
different configuration from the one that ships is not a check of the thing that ships.** The tests were
not weak here — they were thorough, and they were thorough about the wrong list.

`GameplayModules.All()` is now the only list. `AddGameplay` walks it; the host walks it too, still
through `AddDefaultModule` so `game.json` can disable a single feature. `ModuleSetTests` reflects over
the engine assembly and fails if a public `IModule` is neither in the list nor named there as installed
elsewhere (physics, streaming, maps, which the host adds on its own terms).

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

`Engine` lives in `Sage.Simulation`, so it holds only core services. Client services (`Renderer`, `InputDevices`, `AudioSystem`) are provided by their client modules with `ctx.Provide(...)`, and consumers declare a dependency on those modules. `Log` is static (02) because it must work before `Engine` exists and from any thread.

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

*As built (one boot path, 2026-09-27, issue #10):* the stages are **`SageApp`**'s (`src/Sage.Simulation/App/SageApp.cs`), not the host's, so the game executable, a headless server, the editor and a test boot the same way. `SageApp.Create` does steps 3–5 from a `SageAppOptions` (the `game.json`, the engine content folder, host modules such as `ClientModule`, `config.cfg`, the `+launch` commands): core cvars, the VFS (engine content in namespace `sage`, then the game's mounts), and the modules — the **`AvailablePlugins`** the host passes, which is **`BasePlugins.All()`** (`Sage.Gameplay`: physics, streaming, maps, then every gameplay module; `SageApp.SimulationModules()` until issue #24), less what `game.json`'s `plugins` leaves out, then the host's, then the game assembly and its `modules.add`, less anything `game.json` disables. A `disable` entry that names no module is a warning (a typo there means a module the game meant to turn off is running). Then `Register` (module `Init`, engine commands), `Configure` (`config.cfg`), `LoadContent` (records), `Start`, `CreateWorld`, `RunLaunchCommands` — each checks it follows the one before and throws if not (test: TheStagesRunInOrderAndSayWhenTheyDoNot). `Boot()` does them all for a headless app (test: HelloBootsHeadlesslyFromItsOwnManifest). `src/Sage.Host/Program.cs` is now only args, `game.json`, logging and the crash reporter, then `SageApp.Create` and `Register`; `Game1.Initialize` registers the window's cvars and commands and walks the app through the rest once the graphics device exists, providing the host services (`ClientHost`, `InputDevices`, `InputActions`) before `Start`. *Step 6:* each frame the host polls devices, resolves actions, feeds the `CommandLatch`, and hands each fixed tick of the player's world its `PlayerCommand` through that world's `PlayerInput` resource (08 §3.6).

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
  input devices poll; input contexts resolve; Look added to the view angles   (08 §3.4)
  accumulator += frameDt                              // real ticks: never scaled here
  while accumulator >= tickDt:                        // tickDt = 1 / sim_tickrate (60 Hz)
      for each world:                                 // WorldTime.HostScale = host_timescale (dev cvar)
          world.RunFixed(tickDt, sample PlayerCommand) // 0..N steps of tickDt by the world's time (below)
      accumulator -= tickDt
  alpha = accumulator / tickDt                        // interpolation factor; each world turns it into its own
  for each world: world.RunFrame(frameDt * host_timescale, alpha, realTime)   // FrameUpdate → Extract → Render → Overlay
```

- `sim_tickrate` default **60 Hz**; `sim_maxframetime` default 0.25 s; `host_timescale` (DevOnly, Cheat) default 1.
- **Built in step 4** (`FixedStepClock` in `Sage.Core/Time.cs`); the loop itself is **`HostLoop`** (`Sage.Simulation/App/HostLoop.cs`, issue #10), which ticks and frames **every** world — until then the host ran only the one world it had created (test: TheLoopTicksEveryWorldNotJustTheFirst). Measured with `host_exitafter 3` and vsync off: ~2,250 fps with exactly 60.0 ticks/s at `sim_tickrate 60` and 5.0 ticks/s at `sim_tickrate 5`.
- `Extract` interpolates transforms between the previous and current tick using `alpha` (06).
- Pausing (`pause`, `World.Paused`) skips `WhenNotPaused` systems; ticks keep counting and Frame systems (camera, UI, rendering) keep running.
- **Time scale, pause and hit-stop are each world's** (`WorldTime`, `src/Sage.Simulation/World/WorldTime.cs`, issue #283; saved as the `time` resource). The host runs *real* ticks; a world's real tick adds `tickDt × Scale × host_timescale` to its own accumulator and runs one step of exactly `tickDt` for each whole `tickDt` in it (at most 16). So the step never changes length — physics keeps the constant step it is tuned for, and two runs at the same scales take the same steps (test: AScaledWorldIsDeterministic) — and 0.5 is a step every other real tick, 2 two a tick (test: HalfScaleHalvesTimerTweenAndClockProgress). Scaling `dt` instead was rejected: every integrator would see a different step at each speed. A real tick with no step due (paused, hit-stopped, between the steps of a slowed world) is a *held pass*: the Fixed phases run once with `Dt` 0 and only `RunCondition.Always` systems, which read `WorldTime.RealDt` (test: RealTimeTimersAndTweensIgnoreScalePauseAndHitStop). `HitStop(seconds)` holds the world for real seconds (test: AHitStopFreezesTheWorldForRealSeconds). The player's command is sampled before each *step*, so a slowed world keeps a press for its next step; a paused one is handed one each tick and drops it, as before (test: TheHostLoopsTimeScaleRoutesThroughWorldTime). Each world's frame alpha is how far it is between its own steps (test: ASlowedWorldInterpolatesBetweenItsOwnSteps). Console: `world_speed`, `hit_stop`, `pause`.
- **Left after #283:** a real-time timer's outputs that fire during a pause are delivered when the world next runs a step (entity I/O's dispatch runs on steps); the Frame schedule's `FrameTime.Dt` is scaled by `host_timescale` but not by a world's speed, so frame-rate effects (particles, weather) do not slow with bullet time; there is no per-system speed factor (a system runs on the world's time, or on real time by `RunCondition.Always`). A pause raises `EngineSignals.PauseChanged` on the next tick (04 §3.5, PR #443).
- Vsync is a cvar (`r_vsync`, Archive). `host_maxfps` (a frame cap with vsync off) is **not built yet**.
- `host_exitafter <seconds>` (DevOnly) quits after that much real time and logs frame and tick counts, for automated smoke runs.

### 5.3 Worlds
Several worlds can exist (Warband's overworld and battle scene, the editor's edit/play worlds, tests). The host runs every *active* world each frame. Inactive worlds are kept but not ticked. *As built:* `HostLoop` runs every world in creation order; a world created during a tick starts on the next one (test: AWorldCreatedDuringATickStartsOnTheNextOne). There is no *inactive* flag yet, and two worlds drawn at once need viewports (REDESIGN phase 4a).

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
