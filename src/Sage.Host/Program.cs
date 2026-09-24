#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using sage_engine;

// Boot sequence (docs/design/01-host-and-modules.md §5.1):
//   args → game.json → logging + crash reporter → core cvars → VFS mounts → modules (game assembly
//   loaded, Init = register) → [Game1.Initialize: host cvars/commands → config.cfg → records →
//   modules Start → main world → +launch commands] → loop → shutdown.
// The engine is disposed (worlds destroyed) before the log shuts down, so teardown is logged.
Thread.CurrentThread.Name = "main";

var launch = LaunchArgs.Parse(args);
string gameDir = ResolveGameDirectory(launch);

GameManifest manifest;
try
{
    manifest = GameManifest.Load(gameDir);
}
catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or FormatException)
{
    UserPaths.Initialize("sage");
    Log.Initialize(new LogOptions { LogDirectory = UserPaths.Logs, Stdout = true });
    Log.Error(LogCat.Host, $"Can't start: {ex.Message}");
    CrashReporter.Write(ex, "Invalid or missing game.json");
    Log.Shutdown();
    return 1;
}

UserPaths.Initialize(manifest.Id);
Log.Initialize(new LogOptions { LogDirectory = UserPaths.Logs, Stdout = true });
CrashReporter.Install();

var cvars = new CVarRegistry();
var core = CoreCVars.Register(cvars);
var engine = new Engine(cvars, core);
CrashReporter.AddSection("CVars (non-default)", cvars.DumpNonDefault);
CrashReporter.AddSection("Game / modules / mounts", () =>
    $"Game: {manifest.Name} ({manifest.Id}) from {manifest.Directory}\n" +
    $"Modules: {string.Join(", ", engine.Modules.Modules.Select(m => m.Name))}\n" +
    $"Mounts: {string.Join(", ", engine.Vfs.Mounts.Select(m => m.ToString()))}");

Log.Info(LogCat.Host, $"Sage {BuildInfo.EngineVersion} ({BuildInfo.Config}), game '{manifest.Name}' ({manifest.Id}), user folder {UserPaths.Root}");
if (Log.File != null)
    Log.Info(LogCat.Host, $"Log file {Log.File.CurrentPath}");
foreach (var option in launch.Options.Keys.Where(o => !o.Equals("game", StringComparison.OrdinalIgnoreCase)))
    Log.Warn(LogCat.Host, $"Unknown launch option -{option} (ignored)");

// VFS (05 §3.1): engine content first, then the game's mounts in order (later wins). Mods: F37.
engine.Vfs.Mount(new FolderMount("engine", Path.Combine(AppContext.BaseDirectory, "Content"), "sage"));
foreach (string mount in manifest.Mounts)
    engine.Vfs.Mount(new FolderMount($"{manifest.Id}/{mount}", Path.Combine(manifest.Directory, mount), manifest.Id));
// Modules (01 §3.1, §5.1). The game assembly must be loaded before the first World exists (03 §3.1).
try
{
    AddDefaultModule(new PhysicsModule());
    AddDefaultModule(new StreamingModule());   // terrain rings and origin rebasing (R6, F14)
    // One per feature since R15, and each still goes through AddDefaultModule, so `game.json` can
    // disable them individually: a game with no items or no AI drops that module and nothing else.
    AddDefaultModule(new FactionsModule());    // who counts as an enemy (16 §3.5, F24)
    AddDefaultModule(new AttributesModule());
    AddDefaultModule(new CharacterModule());
    AddDefaultModule(new AnimationModule());
    AddDefaultModule(new CombatModule());
    AddDefaultModule(new ItemsModule());
    AddDefaultModule(new AbilitiesModule());
    AddDefaultModule(new AIModule());
    AddDefaultModule(new ClientModule());
    engine.Modules.Add(ModuleManager.LoadGame(manifest.AssemblyPath));
    // `modules.add`: the game's other assemblies, such as its client half (01 §3.3, R15).
    foreach (string extra in manifest.ModuleAssemblies)
        foreach (var module in ModuleManager.LoadModules(extra))
            AddDefaultModule(module);
    engine.Modules.InitAll();
}
catch (Exception ex)
{
    Log.Error(LogCat.Modules, $"Can't start: {ex.Message}");
    CrashReporter.Write(ex, "Module loading failed");
    Log.Shutdown();
    return 1;
}
engine.Modules.RegisterCommands(cvars);
VirtualFileSystem.RegisterCommands(cvars, engine.Vfs);
engine.Records.RegisterCommands(cvars);
engine.Saves.RegisterCommands(cvars);

// config.cfg runs once every cvar and command is registered (the host's `quit`, `stat`... are
// registered in Game1.Initialize), before records load and modules start. +args run last, once the main
// world exists (like Source's +map), so `+stat fps`, `+rec_get spawn bunny` and `+ent_list` all work;
// they still override config.cfg.
void ApplyConfig()
{
    if (File.Exists(UserPaths.ConfigFile))
        cvars.ExecFile(UserPaths.ConfigFile, ExecSource.Config);
}

void RunLaunchCommands()
{
    foreach (string command in launch.Commands)
        cvars.Execute(command, ExecSource.LaunchArgs);
}

int exitCode = 0;
try
{
    _ = typeof(DevConsoleWindow).Assembly;   // Sage.Editor: loaded before the first World (03 §3.1)
    using var game = new Game1(engine, ApplyConfig, RunLaunchCommands);
    game.Run();
}
catch (SageFatalException fatal) when (fatal.Reported)
{
    exitCode = 1;   // Log.Fatal already wrote the crash report
}
catch (Exception ex)
{
    CrashReporter.Write(ex, "Unhandled exception in the main loop");
    exitCode = 1;
}
finally
{
    engine.Dispose();               // worlds first (their systems belong to modules)...
    engine.Modules.ShutdownAll();   // ...then modules, in reverse dependency order
    try { cvars.SaveArchived(UserPaths.ConfigFile); }
    catch (Exception ex) { Log.Warn(LogCat.Host, $"Couldn't save {UserPaths.ConfigFile}: {ex.Message}"); }
    Log.Info(LogCat.Host, "Shutdown");
    Log.Shutdown();
}
return exitCode;

void AddDefaultModule(IModule module)
{
    if (manifest.Modules.Disable.Contains(module.Name, StringComparer.OrdinalIgnoreCase))
        Log.Info(LogCat.Modules, $"{module.Name} disabled by game.json");
    else
        engine.Modules.Add(module);
}

// -game <folder>, else the Sandbox game in dev builds (found from the repo root), else ./game next to the exe.
static string ResolveGameDirectory(LaunchArgs launch)
{
    if (launch.Options.TryGetValue("game", out var dir) && !string.IsNullOrEmpty(dir))
        return Path.GetFullPath(dir);
    if (BuildInfo.IsDevBuild)
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Sage.sln")))
                return Path.Combine(d.FullName, "games", "Sandbox");
    return Path.Combine(AppContext.BaseDirectory, "game");
}
