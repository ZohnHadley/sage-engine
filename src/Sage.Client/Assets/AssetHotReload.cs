#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Sage.Client;

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
internal sealed class AssetHotReload : IDisposable
{
    // Everything the runtime loader can actually decode, checked against the decoders compiled into
    // MonoGame 3.8.x DesktopGL (StbImageSharp: jpeg, png, bmp, gif, psd, hdr, tga; the engine calls it itself
    // since issue #317, to premultiply and mipmap in one place) and confirmed by
    // loading one of each. Notably TGA *is* supported despite MonoGame's own doc comment saying it
    // is not, and TIFF, DDS and WebP are *not* despite the same comment saying they are — convert those
    // to PNG, since R12 left no content pipeline to take them through. A GIF gives its first frame; an
    // HDR is tone-mapped to 8-bit.
    //
    // Models and sounds reload too (issue 4h-3): the renderer swaps a `.glb` in its mesh slot, and a
    // `.wav` stops or restarts the voices playing it. Shader *source* is `ShaderRecompiler`'s.
    private static readonly string[] Extensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif", ".psd", ".hdr",   // StbImageSharp (CookedTexture.DecodeImage)
        ".mgfxo",                                                          // compiled effects (07 §3.1)
        ".glb",                                                            // models, in place in the renderer's mesh table
        ".wav",                                                            // sounds; voices on them stop or restart
    };

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
            w.Deleted += OnDelete;
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
        Log.Debug(LogCat.Assets, $"Asset hot reload watching {_watchers.Count} mounts for {string.Join(" ", Extensions)}");
    }

    private static bool Wanted(string file)
    {
        string ext = Path.GetExtension(file);
        foreach (string known in Extensions)
            if (string.Equals(ext, known, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        if (!Wanted(e.FullPath)) return;

        lock (_changed) _changed.Add(e.FullPath);
        Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
    }

    // A deleted asset is not reloaded: whatever is on screen is better than a checkerboard, and the
    // file usually comes back a moment later (an export that writes via delete-and-replace, a git
    // checkout). It is said once, though, because "I deleted it and nothing happened" should not be
    // a mystery.
    private void OnDelete(object sender, FileSystemEventArgs e)
    {
        if (!Wanted(e.FullPath)) return;
        if (_vfs.VirtualPathOf(e.FullPath) is not { } path) return;
        Log.Once(LogCat.Assets, LogLevel.Info, $"asset-deleted:{path}",
            $"{path} was deleted; the copy already loaded stays on screen until it comes back");
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
