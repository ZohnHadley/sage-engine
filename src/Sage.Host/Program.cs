#nullable enable
using System;
using System.Threading;
using sage_engine;

// Boot sequence (docs/design/01-host-and-modules.md §5.1). Modules, VFS, records and worlds join in
// later migration steps; for now: args → logging + crash reporter → cvars → config.cfg → +args → game.
Thread.CurrentThread.Name = "main";

var launch = LaunchArgs.Parse(args);
UserPaths.Initialize("sage");   // becomes the game id from game.json in migration step 5
Log.Initialize(new LogOptions { LogDirectory = UserPaths.Logs, Stdout = true });
CrashReporter.Install();

var cvars = new CVarRegistry();
var core = CoreCVars.Register(cvars);
CrashReporter.AddSection("CVars (non-default)", cvars.DumpNonDefault);
CrashReporter.AddSection("Modules / mods", () => "(module system and mods arrive in migration step 5)");

Log.Info(LogCat.Host, $"Sage {BuildInfo.EngineVersion} ({BuildInfo.Config}), user folder {UserPaths.Root}");
if (Log.File != null)
    Log.Info(LogCat.Host, $"Log file {Log.File.CurrentPath}");
foreach (var option in launch.Options.Keys)
    Log.Warn(LogCat.Host, $"Unknown launch option -{option} (ignored)");

// config.cfg and +args run once every cvar and command is registered (the host's `quit`, `stat`...
// are registered in Game1.Initialize), so `+stat fps` works from the command line.
void ApplyConfigAndLaunchArgs()
{
    if (System.IO.File.Exists(UserPaths.ConfigFile))
        cvars.ExecFile(UserPaths.ConfigFile, ExecSource.Config);
    foreach (string command in launch.Commands)
        cvars.Execute(command, ExecSource.LaunchArgs);
}

try
{
    using var game = new Game1(cvars, core, ApplyConfigAndLaunchArgs);
    game.Run();
}
catch (SageFatalException fatal) when (fatal.Reported)
{
    Environment.ExitCode = 1;   // Log.Fatal already wrote the crash report
}
catch (Exception ex)
{
    CrashReporter.Write(ex, "Unhandled exception in the main loop");
    Environment.ExitCode = 1;
}
finally
{
    try { cvars.SaveArchived(UserPaths.ConfigFile); }
    catch (Exception ex) { Log.Warn(LogCat.Host, $"Couldn't save {UserPaths.ConfigFile}: {ex.Message}"); }
    Log.Info(LogCat.Host, "Shutdown");
    Log.Shutdown();
}
