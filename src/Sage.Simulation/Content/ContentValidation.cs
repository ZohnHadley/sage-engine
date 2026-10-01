#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Simulation;

// `sage validate <game>` (REDESIGN §4.2, issue #22): boots a game headlessly far enough to run every
// content check — every mount's records, references by type, prefab bodies, asset paths, AI schedules
// and the game's own start-up — and reports what the log said. CI runs it for each game; a modder
// runs it before publishing. The command line is src/Sage.Cli; this is the part a test can call.
//
// It loads what a dedicated server loads (no window, no client half of the game), plus the record
// types the engine's client registers — materials, sounds, sprite sheets, particles — because those
// are content too and a reference to one should be checked. A prefab part only a client declares
// (the engine's `audio`, a game's `box_mesh`) cannot have its options read here; the real host's load
// checks those, and the smoke run fails on what it says.
public sealed class ValidateOptions
{
    public required string GameDirectory { get; init; }

    // Engine content, mounted first under `sage`: the repository's engine_content/ or a host's Content/.
    public string? EngineContentDirectory { get; init; }

    // Further folders mounted after the game's, in order, as a mod would be: (directory, namespace).
    public IReadOnlyList<(string Directory, string Namespace)> Mounts { get; init; } = Array.Empty<(string, string)>();

    // The game's module, when the caller already has it loaded (a test that references the game): the
    // manifest's assembly is then not loaded again from disk, as HeadlessApp.ForGame does.
    public IGameModule? GameModule { get; init; }

    // The plugins the game may choose from (SageAppOptions.AvailablePlugins): `sage` passes the base
    // engine's, BasePlugins.All().
    public IReadOnlyList<IModule> AvailablePlugins { get; init; } = Array.Empty<IModule>();

    // Called with the booted engine once its content has loaded, before the game starts: what
    // `sage schema` reads record types, declarations and the loaded ids from (issue #21). What it
    // throws is reported like any other failure of the run.
    public Action<Engine>? Inspect { get; init; }
}

public sealed class ValidationReport
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public int Records { get; internal set; }
    public bool Ok => Errors.Count == 0;
}

public static class ContentValidation
{
    public static ValidationReport Run(ValidateOptions options)
    {
        var report = new ValidationReport();
        using var sink = new ThreadSink(Environment.CurrentManagedThreadId);
        // The log is asynchronous: whatever this thread logged before now may still be queued, and would
        // reach the sink on its first flush and be counted as this run's. Drain it first. (A test that
        // disabled a system earlier on the same xUnit thread turned up in the Sandbox's report in CI.)
        Log.Flush();
        Log.AddSink(sink);
        try
        {
            Boot(options, report);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Error(LogCat.Host, $"validate: {ex.Message}");
        }
        finally
        {
            Log.Flush();
            Log.RemoveSink(sink);
        }
        foreach (var entry in sink.Entries)
            (entry.Level >= LogLevel.Error ? report.Errors : report.Warnings).Add($"{entry.Category.Name}: {entry.Message}");
        return report;
    }

    private static void Boot(ValidateOptions options, ValidationReport report)
    {
        var manifest = GameManifest.Load(options.GameDirectory);
        manifest.Modules.Add.Clear();   // the game's client half: nothing headless can load a window
        if (options.GameModule != null) manifest.Assembly = "";

        using var app = SageApp.Create(new SageAppOptions
        {
            Game = manifest,
            EngineContentDirectory = options.EngineContentDirectory,
            Host = HostKind.Server,
            AvailablePlugins = options.AvailablePlugins,
            HostModules = options.GameModule != null ? new IModule[] { options.GameModule } : Array.Empty<IModule>(),
            Mods = Array.Empty<string>(),   // a game's mods are not validated with it yet: `--mounts` (4j-5 adds --mods)
        });
        foreach (var (directory, ns) in options.Mounts)
        {
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"no folder {Path.GetFullPath(directory)} to mount");
            RecordId.Parse(ns, ns);   // a namespace follows the id rules
            app.Engine.Vfs.Mount(new FolderMount(Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))), directory, ns));
        }

        app.Register();
        // The client's record types, which a server does not register: their records are content,
        // and what references them should be checked.
        if (!app.Engine.Modules.Modules.Any(m => PluginInfo.Of(m).Id == ClientPlugin))
        {
            var generated = new GeneratedRegistrations();
            generated.Include(typeof(Engine).Assembly);
            generated.Register(ClientPlugin, new RegistrationBuilder(app.Engine));
        }

        app.Engine.Records.MissingAssetsAreErrors = true;
        app.Configure();
        app.LoadContent();
        report.Records = app.Engine.Records.Count;
        options.Inspect?.Invoke(app.Engine);
        app.Start();
        app.CreateWorld("validate");   // the game's rules run: its scene is placed, its prefabs spawn
    }

    private const string ClientPlugin = "sage.client";

    // What this thread logs at WARN and above: the validation's own lines, not a neighbour's (tests run
    // in parallel, and the log is the process's).
    private sealed class ThreadSink : ILogSink, IDisposable
    {
        private readonly int _thread;
        private readonly List<LogEntry> _entries = new();

        public ThreadSink(int thread) { _thread = thread; }

        public LogLevel MinLevel => LogLevel.Warn;

        public IReadOnlyList<LogEntry> Entries { get { lock (_entries) return _entries.ToArray(); } }

        public void Write(in LogEntry entry)
        {
            if (entry.ThreadId != _thread || entry.Level < LogLevel.Warn || IsRepeatNote(entry)) return;
            lock (_entries) _entries.Add(entry);
        }

        public void Flush() { }
        public void Dispose() { }
    }

    // The log folds a message repeated back to back into one, and later adds "(previous message repeated
    // N more times)" from whichever thread is draining the queue then — during this thread's Flush on a
    // slow machine, on the writer thread otherwise. The report is a list of distinct problems, so the note
    // is never one: counting it made the report depend on timing (a mod test failed in CI this way).
    internal static bool IsRepeatNote(in LogEntry entry) =>
        entry.File == null && entry.Message.StartsWith("(previous message repeated ", StringComparison.Ordinal);
}
