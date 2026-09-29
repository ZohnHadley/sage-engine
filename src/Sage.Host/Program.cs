#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Sage.Host;

// Boot sequence (docs/design/01-host-and-modules.md §5.1, REDESIGN §3.2):
//   args → game.json → logging + crash reporter → SageApp.Create (cvars, VFS mounts, modules; the game
//   assembly is loaded here) → Register (module Init) → [Game1.Initialize: host cvars/commands →
//   Configure (config.cfg) → LoadContent (records) → Start → main world → +launch commands] → loop →
//   shutdown. The stages are SageApp's; this file only decides what the app is made of.
Thread.CurrentThread.Name = "main";

var launch = LaunchArgs.Parse(args);

GameManifest manifest;
try
{
    // -game <folder>, else ./game beside the executable; without either it is an error that says so
    // (GameManifest.Locate): no build falls back to games/Sandbox any more (issue #32).
    manifest = GameManifest.Load(GameManifest.Locate(launch.Options.GetValueOrDefault("game"), AppContext.BaseDirectory));
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
foreach (var option in launch.Options.Keys.Where(o => !o.Equals("game", StringComparison.OrdinalIgnoreCase)
                                                    && !o.Equals("dump-registry", StringComparison.OrdinalIgnoreCase)))
    Log.Warn(LogCat.Host, $"Unknown launch option -{option} (ignored)");

// -dump-registry <file>: boot, write everything registered as JSON (RegistryDump, issue #18) and quit.
// Resolved against the directory it was typed in, before anything changes it.
string? dumpRegistry = launch.Options.TryGetValue("dump-registry", out var dumpPath) && !string.IsNullOrEmpty(dumpPath)
    ? Path.GetFullPath(dumpPath) : null;
if (launch.Options.ContainsKey("dump-registry") && dumpRegistry == null)
    Log.Warn(LogCat.Host, "-dump-registry needs a file name (ignored)");

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
        AvailablePlugins = BasePlugins.All(),
        HostModules = new IModule[] { new ClientModule() },
        LoadKitClients = true,   // a kit's screens come with it (game.json "kits", issue #27)
        ConfigFile = UserPaths.ConfigFile,
        LaunchCommands = launch.Commands,
        OwnsProcessLog = true,   // the one app in this process, so its log cvars are the log's
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
    using var game = new Game1(app, dumpRegistry);
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
