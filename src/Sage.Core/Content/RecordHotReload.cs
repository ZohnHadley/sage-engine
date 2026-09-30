#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Sage.Core;

// Dev hot reload for record files (docs/design/05 §3.5): watches `data/` in every folder mount, and
// `strings/` (string tables, 05 §3.7, issue #96), which the UI re-reads when records reload.
// FileSystemWatcher events arrive on thread-pool threads; they only set a flag. The host calls Poll()
// once per frame (main thread), which reloads after changes have been quiet for 200 ms, so a save
// that touches several files reloads once.
public sealed class RecordHotReload : IDisposable
{
    private readonly RecordStore _store;
    private readonly List<FileSystemWatcher> _watchers = new();
    private long _lastChangeTicks;
    private int _dirty;

    // What is watched in each folder mount: records, and string tables.
    private static readonly string[] Folders = { "data", "strings" };

    public RecordHotReload(RecordStore store, VirtualFileSystem vfs)
    {
        _store = store;
        foreach (var mount in vfs.Mounts)
        {
            if (mount is not FolderMount folder) continue;
            foreach (string watched in Folders)
            {
                string data = Path.Combine(folder.Root, watched);
                if (!Directory.Exists(data)) continue;
                var w = new FileSystemWatcher(data, "*.json") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
                w.Changed += OnChange; w.Created += OnChange; w.Deleted += OnChange; w.Renamed += OnChange;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
        }
        Log.Debug(LogCat.Records, $"Record hot reload watching {_watchers.Count} data and strings folders");
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void Poll()
    {
        if (Volatile.Read(ref _dirty) == 0) return;
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastChangeTicks) < TimeSpan.FromMilliseconds(200).Ticks) return;
        Interlocked.Exchange(ref _dirty, 0);
        Log.Info(LogCat.Records, "Record files changed; reloading");
        _store.Reload();
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
