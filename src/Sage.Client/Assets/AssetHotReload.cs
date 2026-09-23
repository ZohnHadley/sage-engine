#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace sage_engine;

// Dev hot reload for assets (docs/design/05 §3.6, TODO F32): watches every folder mount for the file
// types the client loads and reloads the ones that change. Records have had this since R11; this is
// the other half, and it is what makes dressing a game in art bearable — regenerate a sprite sheet,
// alt-tab, and it is on the creature.
//
// The same shape as RecordHotReload: FileSystemWatcher events arrive on thread-pool threads and only
// add to a list, the host calls Poll() once a frame on the main thread, and a change has to have been
// quiet for 200 ms before anything happens — an art tool writing eight files produces one reload, and
// a file half-written is not read.
//
// Unlike records, assets reload *individually*: a texture is a GPU object with holders, and there is
// no reason to drop the other four hundred because one changed.
public sealed class AssetHotReload : IDisposable
{
    private static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".mgfxo" };

    private readonly ContentService _content;
    private readonly VirtualFileSystem _vfs;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly HashSet<string> _changed = new(StringComparer.OrdinalIgnoreCase);   // disk paths
    private readonly List<AssetPath> _batch = new();
    private long _lastChangeTicks;

    public AssetHotReload(ContentService content, VirtualFileSystem vfs)
    {
        _content = content;
        _vfs = vfs;
        foreach (var mount in vfs.Mounts)
        {
            if (mount is not FolderMount folder || !Directory.Exists(folder.Root)) continue;
            // One watcher per mount root rather than per extension: assets are not confined to a
            // `textures/` folder the way records are to `data/`, and filtering is cheap.
            var w = new FileSystemWatcher(folder.Root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            w.Changed += OnChange; w.Created += OnChange; w.Renamed += OnChange;
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        Log.Debug(LogCat.Assets, $"Asset hot reload watching {_watchers.Count} mounts for {string.Join(" ", Extensions)}");
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        string ext = Path.GetExtension(e.FullPath);
        bool wanted = false;
        foreach (string known in Extensions)
            if (string.Equals(ext, known, StringComparison.OrdinalIgnoreCase)) { wanted = true; break; }
        if (!wanted) return;

        lock (_changed) _changed.Add(e.FullPath);
        Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
    }

    // Main thread, once a frame. Reloads whatever has settled; a file that is still being written
    // keeps pushing the deadline out, which is what a long export wants.
    public void Poll()
    {
        lock (_changed)
        {
            if (_changed.Count == 0) return;
            if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastChangeTicks) < TimeSpan.FromMilliseconds(200).Ticks) return;

            _batch.Clear();
            foreach (string disk in _changed)
            {
                // Only assets that are actually loaded: a watcher sees every file in the mount, and
                // most of them nothing has ever asked for.
                if (_vfs.VirtualPathOf(disk) is not { } path) continue;
                _batch.Add(AssetPath.Intern(path.ToString()));
            }
            _changed.Clear();
        }

        int reloaded = 0;
        foreach (var path in _batch)
            if (_content.Reload(path)) reloaded++;
        if (reloaded > 0) Log.Info(LogCat.Assets, $"{reloaded} asset(s) reloaded");
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
