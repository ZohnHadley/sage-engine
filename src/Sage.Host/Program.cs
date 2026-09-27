#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using sage_engine;

// Boot sequence (docs/design/01-host-and-modules.md §5.1, REDESIGN §3.2):
//   args → game.json → logging + crash reporter → SageApp.Create (cvars, VFS mounts, modules; the game
//   assembly is loaded here) → Register (module Init) → [Game1.Initialize: host cvars/commands →
//   Configure (config.cfg) → LoadContent (records) → Start → main world → +launch commands] → loop →
//   shutdown. The stages are SageApp's; this file only decides what the app is made of.
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

Log.Info(LogCat.Host, $"Sage {BuildInfo.EngineVersion} ({BuildInfo.Config}), game '{manifest.Name}' ({manifest.Id}), user folder {UserPaths.Root}");
if (Log.File != null)
    Log.Info(LogCat.Host, $"Log file {Log.File.CurrentPath}");
foreach (var option in launch.Options.Keys.Where(o => !o.Equals("game", StringComparison.OrdinalIgnoreCase)))
    Log.Warn(LogCat.Host, $"Unknown launch option -{option} (ignored)");

// What this app is: the game, engine content beside the executable, the simulation modules and the
// client (a server would leave the client out). The game assembly must be loaded before the first World
// exists (03 §3.1), which SageApp.Create does.
SageApp app;
try
{
    app = SageApp.Create(new SageAppOptions
    {
        Game = manifest,
        EngineContentDirectory = Path.Combine(AppContext.BaseDirectory, "Content"),
        HostModules = new IModule[] { new ClientModule() },
        ConfigFile = UserPaths.ConfigFile,
        LaunchCommands = launch.Commands,
    });
    app.Register();
}
catch (Exception ex)
{
    Log.Error(LogCat.Modules, $"Can't start: {ex.Message}");
    CrashReporter.Write(ex, "Module loading failed");
    Log.Shutdown();
    return 1;
}
var engine = app.Engine;
CrashReporter.AddSection("CVars (non-default)", engine.CVars.DumpNonDefault);
CrashReporter.AddSection("Game / modules / mounts", () =>
    $"Game: {manifest.Name} ({manifest.Id}) from {manifest.Directory}\n" +
    $"Modules: {string.Join(", ", engine.Modules.Modules.Select(m => m.Name))}\n" +
    $"Mounts: {string.Join(", ", engine.Vfs.Mounts.Select(m => m.ToString()))}");

int exitCode = 0;
try
{
#if SAGE_DEV
    _ = typeof(DevTools).Assembly;   // Sage.Editor: loaded before the first World (03 §3.1)
#endif
    using var game = new Game1(app);
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
    app.Dispose();   // worlds, then modules in reverse dependency order, then config.cfg
    Log.Info(LogCat.Host, "Shutdown");
    Log.Shutdown();
}
return exitCode;

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
