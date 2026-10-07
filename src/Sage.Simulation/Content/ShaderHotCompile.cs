#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sage.Simulation;

// Dev builds compile shader *source* (issue 4h-3; mods, issue #400): edit a `.fx` or `.fxh`, and the `.mgfxo`
// the game loads is rebuilt. The client's asset hot reload already reloads a `.mgfxo` when it changes on disk,
// so this only has to make the file change; the rest follows the path every other asset takes.
//
// Where the sources are: every folder mount's `shaders/` — the game's and each folder mod's — and the engine's
// `engine_content/shaders/` when the executable runs from a checkout (the build does not copy sources beside
// it). A mount's effect compiles to the `.mgfxo` beside it in that mount, never into another mount that has a
// file at the same path (a mod's `shaders/lit.fx` does not overwrite the game's `lit.mgfxo`); the engine's
// replaces the compiled file the VFS serves. An effect may include the headers of the mounts below its own
// (a mod's, the engine's `common.fxh`; ShaderBuild.Compile).
//
// On load, a mount's effects whose `.mgfxo` is missing or out of date (ShaderBuild.IsStale) are queued as if
// just edited: a folder mod ships `.fx` alone and is compiled when the game starts. Not the engine's checkout
// sources or the engine mount, which the build compiled. Until an effect compiles, the materials that use it draw `sage:error`
// (flat magenta); a later failure keeps the effect already compiled (07 §8).
//
// Same shape as the other watchers: file system events only add to a set, `Poll` runs once a frame on the main
// thread, and a change has to have been quiet for 200 ms. A changed header recompiles the effects that include
// it (ShaderIncludes.Dependents). The compile runs on a background task, one batch at a time: mgfxc takes
// seconds, and a frame must not wait for it. Where the compiler cannot run (mgfxc without Wine), this says so
// once, under `Shaders`, and the shaders already compiled stay as they are.
internal sealed class ShaderHotCompile : IDisposable
{
    private sealed class Source
    {
        public required string Dir;            // absolute
        public required FolderMount? Mount;    // the mount whose `shaders/` this is; null for the engine's checkout
        public required int Order;             // mount order; the engine's checkout is -1
        public readonly HashSet<string> Changed = new(StringComparer.OrdinalIgnoreCase);   // relative names
    }

    private const string EngineNamespace = "sage";

    private readonly VirtualFileSystem _vfs;
    private readonly IShaderCompiler _compiler;
    private readonly List<Source> _sources = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private long _lastChangeTicks;
    private int _running;                       // 1 while a compile task is in flight

    // What the last batch did, for tests and `stat`: effect (as `<mount>:shaders/x.fx`) → compiled.
    public IReadOnlyList<(string Effect, bool Ok)> LastBatch => _lastBatch;
    private volatile List<(string, bool)> _lastBatch = new();

    // How many effects were queued on load because their `.mgfxo` was missing or out of date.
    public int StaleOnLoad { get; }

    public ShaderHotCompile(VirtualFileSystem vfs, IShaderCompiler compiler, string? engineShaders, bool watch = true)
    {
        _vfs = vfs;
        _compiler = compiler;
        if (engineShaders != null && Directory.Exists(engineShaders)) Add(engineShaders, null, -1, watch);
        int order = 0;
        foreach (var mount in vfs.Mounts)
        {
            order++;
            if (mount is FolderMount folder && Directory.Exists(Path.Combine(folder.Root, ShaderBuild.Folder)))
                Add(Path.Combine(folder.Root, ShaderBuild.Folder), folder, order, watch);
        }

        foreach (var source in _sources)
        {
            // Not the engine's: the build compiled it (a Player's Content/ holds both, with no telling timestamps).
            if (source.Mount == null || source.Mount.RecordNamespace == EngineNamespace) continue;
            foreach (string effect in ShaderBuild.Stale(source.Dir))
            {
                source.Changed.Add(effect);
                StaleOnLoad++;
            }
        }
        if (StaleOnLoad > 0)
            Log.Info(LogCat.Shaders, $"{StaleOnLoad} shader(s) not compiled yet or out of date: compiling " +
                                     string.Join(", ", _sources.SelectMany(s => s.Changed.Select(e => Describe(s, e)))));
        Log.Debug(LogCat.Shaders, $"Shader compile watching {_sources.Count} shader folder(s)");
    }

    private void Add(string dir, FolderMount? mount, int order, bool watch)
    {
        dir = Path.GetFullPath(dir);
        foreach (var known in _sources)
            if (string.Equals(known.Dir, dir, StringComparison.OrdinalIgnoreCase)) return;

        var source = new Source { Dir = dir, Mount = mount, Order = order };
        _sources.Add(source);
        if (!watch) return;
        var w = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        FileSystemEventHandler handler = (_, e) => OnChange(source, e.FullPath);
        w.Changed += handler; w.Created += handler;
        w.Renamed += (_, e) => OnChange(source, e.FullPath);
        w.EnableRaisingEvents = true;
        _watchers.Add(w);
    }

    // A file changed on disk: what the watchers call, and tests directly. Ignored outside the shader folders.
    public void NoteChange(string fullPath)
    {
        string full = Path.GetFullPath(fullPath);
        Source? best = null;
        foreach (var source in _sources)
            if (full.StartsWith(source.Dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && (best == null || source.Dir.Length > best.Dir.Length)) best = source;
        if (best != null) OnChange(best, full);
    }

    private void OnChange(Source source, string full)
    {
        if (!ShaderBuild.IsSource(full)) return;
        string name = Path.GetRelativePath(source.Dir, full).Replace('\\', '/');
        lock (source.Changed) source.Changed.Add(name);
        Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
    }

    // Main thread, once a frame: what has settled is compiled on a background task.
    public void Poll()
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastChangeTicks) < TimeSpan.FromMilliseconds(200).Ticks) return;
        if (Volatile.Read(ref _running) != 0) return;   // what changed meanwhile waits in the set
        if (Plan() is not { } jobs) return;

        Interlocked.Exchange(ref _running, 1);
        WorkStats.JobStarted();   // `stat render`'s jobs (issue #300)
        Task.Run(() =>
        {
            try { Run(jobs); }
            finally { Volatile.Write(ref _running, 0); WorkStats.JobFinished(); }
        });
    }

    // Everything pending, compiled now on this thread, whatever the time since the last change. For tests and tools.
    public IReadOnlyList<(string Effect, bool Ok)> CompilePending()
    {
        if (Plan() is { } jobs) Run(jobs);
        else _lastBatch = new();
        return _lastBatch;
    }

    // The effects to compile for what changed: each changed `.fx`, and every `.fx` that includes a changed header.
    // Null when there are none, or the compiler cannot run here (said once).
    private List<(Source Source, string Name)>? Plan()
    {
        List<(Source Source, string Name)>? jobs = null;
        foreach (var source in _sources)
        {
            string[] changed;
            lock (source.Changed)
            {
                if (source.Changed.Count == 0) continue;
                changed = source.Changed.ToArray();
                source.Changed.Clear();
            }
            var texts = ShaderBuild.ReadSources(source.Dir);
            var effects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in changed)
                foreach (string fx in ShaderIncludes.Dependents(texts, name)) effects.Add(fx);
            foreach (string fx in effects) (jobs ??= new()).Add((source, fx));
        }
        if (jobs == null) return null;

        if (!_compiler.CanRun(out string why))
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "shader-hotreload-unavailable",
                $"Shader source needs compiling ({string.Join(", ", jobs.Select(j => Describe(j.Source, j.Name)))}) but {why}; " +
                "not compiling: the shaders already compiled stay, and materials with none draw sage:error (magenta)");
            return null;
        }
        return jobs;
    }

    private void Run(List<(Source Source, string Name)> jobs)
    {
        var done = new List<(string, bool)>();
        foreach (var (source, name) in jobs) done.Add((Describe(source, name), Compile(source, name)));
        _lastBatch = done;
    }

    // Background thread (or the caller's, from CompilePending). The compiled file replaces the one beside the
    // source in its own mount (the engine's: the one the VFS serves), which the client sees change and reloads.
    private bool Compile(Source source, string name)
    {
        var virtualPath = VirtualPath.Parse(ShaderBuild.Folder + "/" + Path.ChangeExtension(name, ShaderBuild.CompiledExtension));
        string? dest = source.Mount != null ? source.Mount.WritablePath(virtualPath)
            : _vfs.Which(virtualPath) is FolderMount served ? served.PhysicalPath(virtualPath) : null;
        if (dest == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, $"shader-nodest:{name}",
                $"{name} changed but no folder mount serves {virtualPath}; nothing to replace");
            return false;
        }

        var clock = Stopwatch.StartNew();
        var result = ShaderBuild.Compile(_compiler, source.Dir, name, dest, HeadersBelow(source));
        if (!result.Ok)
        {
            Log.Error(LogCat.Shaders, ShaderBuild.FailureMessage(Where(source), name, result.Output, File.Exists(dest)));
            return false;
        }
        Log.Info(LogCat.Shaders, $"Compiled {Describe(source, name)} in {clock.ElapsedMilliseconds} ms");
        return true;
    }

    // The headers an effect of `source` may include from elsewhere: those of the shader folders mounted below it,
    // the engine's checkout first.
    private Dictionary<string, string> HeadersBelow(Source source) =>
        ShaderBuild.Headers(_sources.Where(s => s.Order < source.Order).OrderBy(s => s.Order).Select(s => (string?)s.Dir));

    private static string Where(Source source) => source.Mount == null ? "engine " : $"{source.Mount.Name}: ";
    private static string Describe(Source source, string name) => $"{(source.Mount?.Name ?? "engine")}:{ShaderBuild.Folder}/{name}";

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
