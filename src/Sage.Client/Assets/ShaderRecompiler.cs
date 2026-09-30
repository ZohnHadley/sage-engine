#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Sage.Client;

// Dev hot reload of shader *source* (issue 4h-3): edit a `.fx` or `.fxh`, and the `.mgfxo` the game
// loads is rebuilt with mgfxc. `AssetHotReload` already reloads a `.mgfxo` when it changes on disk,
// so this only has to make the file change; the rest follows the path every other asset takes.
//
// Where the sources are: a game's own `shaders/` folder in a folder mount, and the engine's
// `engine_content/shaders/` when the executable runs from a repository checkout (found by walking up
// from the executable; the build does not copy sources beside it). The result replaces the compiled
// file the VFS serves as `shaders/<name>.mgfxo`.
//
// Same shape as the other watchers: file system events only add to a set, `Poll` runs once a frame on
// the main thread, and a change has to have been quiet for 200 ms. A changed header recompiles the
// effects that include it (`ShaderIncludes.Dependents`). The compile itself runs on a background task,
// one at a time: mgfxc takes seconds, and a frame must not wait for it.
//
// **mgfxc cannot run everywhere**: it is a Windows tool, and on Linux and macOS it needs Wine
// (`MGFXC_WINE_PATH`, the same variable the build reads). Where it cannot run this says so once, under
// `Shaders`, and the shaders already loaded stay as they are.
internal sealed class ShaderRecompiler : IDisposable
{
    private sealed class Source
    {
        public required string Dir;           // absolute
        public required FolderMount? Mount;   // the mount whose `shaders/` this is; null for the engine's checkout
        public readonly HashSet<string> Changed = new(StringComparer.OrdinalIgnoreCase);   // relative names
    }

    private readonly VirtualFileSystem _vfs;
    private readonly List<Source> _sources = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly string? _toolsRoot;      // the folder holding .config/dotnet-tools.json
    private long _lastChangeTicks;
    private int _running;                      // 1 while a compile task is in flight

    public ShaderRecompiler(VirtualFileSystem vfs)
    {
        _vfs = vfs;

        foreach (var mount in vfs.Mounts)
        {
            if (mount is FolderMount folder && Directory.Exists(Path.Combine(folder.Root, "shaders")))
                Add(Path.Combine(folder.Root, "shaders"), folder);
        }
        if (FindUp("engine_content/shaders", directory: true) is { } engine) Add(engine, null);
        _toolsRoot = FindUp(".config/dotnet-tools.json", directory: false) is { } manifest
            ? Path.GetDirectoryName(Path.GetDirectoryName(manifest)) : null;

        Log.Debug(LogCat.Shaders, $"Shader hot reload watching {_sources.Count} shader folder(s)");
    }

    // Whether mgfxc can run on this machine. Public to the client so the log can say why not.
    internal static bool CanRunMgfxc(out string why)
    {
        if (OperatingSystem.IsWindows()) { why = ""; return true; }
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MGFXC_WINE_PATH"))) { why = ""; return true; }
        why = "mgfxc needs Wine on this platform and MGFXC_WINE_PATH is not set";
        return false;
    }

    private void Add(string dir, FolderMount? mount)
    {
        dir = Path.GetFullPath(dir);
        foreach (var known in _sources)
            if (string.Equals(known.Dir, dir, StringComparison.OrdinalIgnoreCase)) return;

        var source = new Source { Dir = dir, Mount = mount };
        _sources.Add(source);
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

    private void OnChange(Source source, string full)
    {
        string ext = Path.GetExtension(full);
        if (!ext.Equals(".fx", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".fxh", StringComparison.OrdinalIgnoreCase)) return;
        string name = Path.GetRelativePath(source.Dir, full).Replace('\\', '/');
        lock (source.Changed) source.Changed.Add(name);
        Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
    }

    // Main thread, once a frame.
    public void Poll()
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastChangeTicks) < TimeSpan.FromMilliseconds(200).Ticks) return;
        if (Volatile.Read(ref _running) != 0) return;   // what changed meanwhile waits in the set

        List<(Source Source, string Name)>? jobs = null;
        foreach (var source in _sources)
        {
            string[] changed;
            lock (source.Changed)
            {
                if (source.Changed.Count == 0) continue;
                changed = new string[source.Changed.Count];
                source.Changed.CopyTo(changed);
                source.Changed.Clear();
            }

            var effects = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? texts = null;
            foreach (string name in changed)
            {
                texts ??= ReadAll(source.Dir);
                foreach (string fx in ShaderIncludes.Dependents(texts, name)) effects.Add(fx);
            }
            foreach (string fx in effects) (jobs ??= new()).Add((source, fx));
        }
        if (jobs == null) return;

        if (!CanRunMgfxc(out string why))
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, "shader-hotreload-unavailable",
                $"Shader source changed but {why}; not recompiling (the compiled shaders already loaded stay)");
            return;
        }

        Interlocked.Exchange(ref _running, 1);
        var snapshot = jobs;
        Task.Run(() =>
        {
            try { foreach (var (source, name) in snapshot) Compile(source, name); }
            finally { Volatile.Write(ref _running, 0); }
        });
    }

    private static Dictionary<string, string> ReadAll(string dir)
    {
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(file);
            if (!ext.Equals(".fx", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".fxh", StringComparison.OrdinalIgnoreCase)) continue;
            try { texts[Path.GetRelativePath(dir, file).Replace('\\', '/')] = File.ReadAllText(file); }
            catch (IOException) { }   // caught mid-write: the next change event reads it
        }
        return texts;
    }

    // Background thread. The compiled file replaces the one the VFS serves; `AssetHotReload` sees it
    // change and reloads the effect on the main thread.
    private void Compile(Source source, string name)
    {
        var virtualPath = VirtualPath.Parse("shaders/" + Path.ChangeExtension(name, ".mgfxo"));
        string? dest = _vfs.Which(virtualPath) is FolderMount served ? served.PhysicalPath(virtualPath)
            : source.Mount?.WritablePath(virtualPath);
        if (dest == null)
        {
            Log.Once(LogCat.Shaders, LogLevel.Warn, $"shader-nodest:{name}",
                $"{name} changed but no folder mount serves {virtualPath}; nothing to replace");
            return;
        }

        string temp = Path.Combine(Path.GetTempPath(), $"sage-{Guid.NewGuid():N}.mgfxo");
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = _toolsRoot ?? source.Dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("mgfxc");
            start.ArgumentList.Add(Path.Combine(source.Dir, name));
            start.ArgumentList.Add(temp);
            start.ArgumentList.Add("/Profile:OpenGL");

            var clock = Stopwatch.StartNew();
            using var process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0 || !File.Exists(temp))
            {
                // The old .mgfxo stays in place: a typo in a shader is not a reason to lose the frame.
                Log.Error(LogCat.Shaders, $"{name} did not compile; keeping the shader already loaded:\n{output.Trim()}");
                return;
            }

            File.Move(temp, dest, overwrite: true);
            Log.Info(LogCat.Shaders, $"Recompiled {name} in {clock.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            Log.Error(LogCat.Shaders, $"{name} could not be recompiled: {ex.Message}");
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    // The nearest ancestor of the executable that has `relative`, for a build run from a checkout.
    private static string? FindUp(string relative, bool directory)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir != null && i < 10; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relative);
            if (directory ? Directory.Exists(candidate) : File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
