#nullable enable
using System;
using System.IO;
using System.Linq;

namespace Sage.Core;

// The engine's own cvars and console commands (docs/design/01 §3.2, 02 §4.2 and §9).
// There is no separate "dev mode": build configurations decide what exists; these cvars switch
// features at runtime, and `developer` only sets the defaults of the others.
public sealed class CoreCVars
{
    public CVar<int> Developer { get; }
    public CVar<bool> Cheats { get; }
    public CVar<bool> ConsoleEnabled { get; }
    public CVar<int> LogKeep { get; }
    public CVar<LogLevel> LogFileLevel { get; }
    public CVar<LogLevel> LogConsoleLevel { get; }
    public CVar<int> LogQueueSize { get; }
    public CVar<int> MemWarnBytes { get; }
    public CVar<int> EventMaxAge { get; }
    public CVar<string> EventTrace { get; }

    // True when the console may be opened: always in dev builds, via con_enable in Shipping.
    public bool ConsoleAvailable => BuildInfo.IsDevBuild || ConsoleEnabled.Value;

    // Whether this app's log cvars configure the process's log: its default level, stdout, the file's
    // level and the queue (issue #11). The log is one per process, so only one app may: the host's.
    // Every other app — a test, a tool, a second app in the same process — still has the cvars (a
    // config.cfg naming them is not an error), and setting them changes nothing outside it.
    public bool OwnsProcessLog { get; }

    private CoreCVars(CVarRegistry r, bool ownsProcessLog)
    {
        OwnsProcessLog = ownsProcessLog;
        Developer = r.Register("developer", BuildInfo.Config == BuildConfig.Debug ? 1 : 0, CVarFlags.DevOnly,
            "Developer defaults: 1 = Debug-level logs, stdout log, hot reload on; 0 = off. Each feature keeps its own cvar.", 0, 2);
        Cheats = r.Register("sv_cheats", false, CVarFlags.None,
            "Allow cheat cvars and commands (god, noclip, give...).");
        ConsoleEnabled = r.Register("con_enable", false, CVarFlags.Archive,
            "Shipping builds: allow opening the console. Dev builds always allow it.");
        LogKeep = r.Register("log_keep", 10, CVarFlags.Archive,
            "Number of session log files to keep in the logs folder.", 1, 1000);
        LogFileLevel = r.Register("log_file_level", LogLevel.Trace, CVarFlags.Archive,
            "Minimum level written to the log file (categories filter first; see log_level).");
        LogConsoleLevel = r.Register("log_console_level", LogLevel.Info, CVarFlags.Archive,
            "Minimum level shown in the console window.");
        LogQueueSize = r.Register("log_queue_size", 16_384, CVarFlags.None,
            "Log queue capacity; beyond it Trace/Debug entries are dropped (and counted).", 256, 1 << 20);
        MemWarnBytes = r.Register("mem_warn_bytes", 0, CVarFlags.DevOnly,
            "Warn when steady-state frames allocate more than this many bytes (0 = off). See `stat mem`.", 0, int.MaxValue);
        EventMaxAge = r.Register("ev_maxage", 8, CVarFlags.None,
            "Ticks a game event may go unread before it is dropped and the lagging reader named (-1 = never).", -1, 1 << 16);
        EventTrace = r.Register("ev_trace", "", CVarFlags.DevOnly,
            "Log every send of this game event type, or * for all. Empty = off. See `ev_stats`.");

        if (!ownsProcessLog) return;
        Developer.Changed += _ => ApplyDeveloperDefaults();
        LogKeep.Changed += _ => Log.File?.Prune(LogKeep.Value);
        LogFileLevel.Changed += _ => { if (Log.File != null) Log.File.MinLevel = LogFileLevel.Value; };
        LogQueueSize.Changed += _ => Log.QueueCapacity = LogQueueSize.Value;
        ApplyDeveloperDefaults();
        if (Log.File != null) Log.File.MinLevel = LogFileLevel.Value;
        Log.QueueCapacity = LogQueueSize.Value;
    }

    // `ownsProcessLog`: see OwnsProcessLog. SageAppOptions.OwnsProcessLog passes it; the executable
    // sets it, and nothing else should.
    public static CoreCVars Register(CVarRegistry registry, bool ownsProcessLog = false)
    {
        var core = new CoreCVars(registry, ownsProcessLog);
        RegisterCommands(registry);
        return core;
    }

    private void ApplyDeveloperDefaults()
    {
        bool dev = BuildInfo.IsDevBuild && Developer.Value >= 1;
        LogCat.DefaultLevel = dev ? LogLevel.Debug : LogLevel.Info;
        if (Log.Stdout != null) Log.Stdout.Enabled = dev;
    }

    private static void RegisterCommands(CVarRegistry r)
    {
        r.RegisterCommand("help", CVarFlags.None, "help [name]: describe a cvar or command, or list how to find things.", a =>
        {
            if (a.Count == 0)
            {
                Log.Info(LogCat.Console, "Type a cvar name to see its value, or `name value` to set it. " +
                    "`cvarlist [prefix]`, `cmdlist`, `find <text>`, `help <name>`.");
                return;
            }
            if (a.Registry.Find(a[0]) is { } cvar) Log.Info(LogCat.Console, a.Registry.Describe(cvar));
            else if (a.Registry.FindCommand(a[0]) is { } cmd) Log.Info(LogCat.Console, $"{cmd.Name}: {cmd.Help}");
            else Log.Warn(LogCat.Console, $"help: no cvar or command named {a[0]}");
        });

        r.RegisterCommand("cvarlist", CVarFlags.None, "cvarlist [prefix]: list cvars and their values.", a =>
        {
            string prefix = a.Count > 0 ? a[0] : "";
            var list = a.Registry.CVars.Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var c in list) Log.Info(LogCat.Console, $"  {c.Name} = {c.ValueString}");
            Log.Info(LogCat.Console, $"{list.Count} cvars");
        });

        r.RegisterCommand("cmdlist", CVarFlags.None, "List console commands.", a =>
        {
            foreach (var c in a.Registry.Commands) Log.Info(LogCat.Console, $"  {c.Name} - {c.Help}");
        });

        r.RegisterCommand("find", CVarFlags.None, "find <text>: search cvar/command names and help.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "find <text>"); return; }
            string q = a.Rest;
            foreach (var c in a.Registry.CVars.Where(c => Matches(c.Name, c.Help, q)))
                Log.Info(LogCat.Console, $"  {c.Name} = {c.ValueString} - {c.Help}");
            foreach (var c in a.Registry.Commands.Where(c => Matches(c.Name, c.Help, q)))
                Log.Info(LogCat.Console, $"  {c.Name} - {c.Help}");
        });

        r.RegisterCommand("echo", CVarFlags.None, "echo <text>: print text.", a => Log.Info(LogCat.Console, a.Rest));

        r.RegisterCommand("wait", CVarFlags.None,
            "wait [seconds]: pause a script here and run the rest later (no argument = next frame).", a =>
        {
            float seconds = 0f;
            if (a.Count > 0 && !float.TryParse(a[0], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out seconds))
            {
                Log.Warn(LogCat.Console, "wait [seconds]");
                return;
            }
            a.Registry.Wait(seconds);
        });

        r.RegisterCommand("wait_cancel", CVarFlags.None, "Drop whatever a script still had queued behind a `wait`.", a =>
        {
            a.Registry.ClearPending();
            Log.Info(LogCat.Console, "queued script statements dropped");
        });

        r.RegisterCommand("exec", CVarFlags.None, "exec <file>: run a config file from the user folder.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "exec <file>"); return; }
            string name = a[0].EndsWith(".cfg", StringComparison.OrdinalIgnoreCase) ? a[0] : a[0] + ".cfg";
            a.Registry.ExecFile(Path.Combine(UserPaths.Root, name), ExecSource.Config);
        });

        r.RegisterCommand("log_level", CVarFlags.None,
            "log_level <category|*> [trace|debug|info|warn|error|default]: show or set a log category's minimum level.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "log_level <category|*> [level|default]"); return; }
            var cats = a[0] == "*" ? LogCat.All.ToList() : LogCat.Find(a[0]) is { } one ? new() { one } : new();
            if (cats.Count == 0) { Log.Warn(LogCat.Console, $"log_level: unknown category {a[0]} (see log_list)"); return; }
            if (a.Count == 1)
            {
                foreach (var c in cats) Log.Info(LogCat.Console, $"  {c.Name}: {c.MinLevel}{(c.IsOverridden ? " (set)" : "")}");
                return;
            }
            if (a[1].Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var c in cats) c.ResetToDefault();
            }
            else if (Enum.TryParse(a[1], ignoreCase: true, out LogLevel level) && Enum.IsDefined(level))
            {
                foreach (var c in cats) c.MinLevel = level;
            }
            else { Log.Warn(LogCat.Console, $"log_level: unknown level {a[1]}"); return; }
            Log.Info(LogCat.Console, $"log_level {a[0]} -> {a[1]}");
        });

        r.RegisterCommand("log_list", CVarFlags.None, "List log categories and their levels.", _ =>
        {
            foreach (var c in LogCat.All.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                Log.Info(LogCat.Console, $"  {c.Name,-10} {c.MinLevel}{(c.IsOverridden ? " (set)" : "")}");
            Log.Info(LogCat.Console, $"Default: {LogCat.DefaultLevel}. Log file: {Log.File?.CurrentPath ?? "(none)"}");
        });

        r.RegisterCommand("mem", CVarFlags.None, "Show managed memory and GC statistics.", _ =>
        {
            var info = GC.GetGCMemoryInfo();
            Log.Info(LogCat.Console,
                $"Managed {GC.GetTotalMemory(false) / 1024} KB, heap {info.HeapSizeBytes / 1024} KB, " +
                $"GC gen0 {GC.CollectionCount(0)} / gen1 {GC.CollectionCount(1)} / gen2 {GC.CollectionCount(2)}, " +
                $"allocated total {GC.GetTotalAllocatedBytes() / (1024 * 1024)} MB");
        });

        r.RegisterCommand("crash", CVarFlags.DevOnly, "Throw an exception to test the crash reporter.", _ =>
            throw new InvalidOperationException("Deliberate crash from the `crash` console command."));
    }

    private static bool Matches(string name, string help, string query) =>
        name.Contains(query, StringComparison.OrdinalIgnoreCase) || help.Contains(query, StringComparison.OrdinalIgnoreCase);
}
