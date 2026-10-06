#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Client asset loading through the VFS (docs/design/05 §3.6), interim until the AssetServer with
// scopes and async loading (05 §14 step 2):
//   - models (.glb, read at runtime by `MeshGeometry.ReadGlb`);
//   - compiled effects (.mgfxo, 07 §3.1 — the one thing still built ahead of time);
//   - textures (.png/.jpg/.tga decoded by StbImageSharp, premultiplied (07 §13) and mipmapped on load,
//     issue #317);
//   - sounds (.wav via SoundEffect.FromStream, .ogg decoded by OggVorbis, 11 §3), and the bitmap font,
//     which is a texture.
//
// **Cooked files stand in when there are some** (issue #302, 05 §7): `sage cook` (run by `sage package`)
// writes `<path>.sgmesh` and `<path>.sgtex` beside a model and a texture, and this reads them instead of
// the loose file: no glTF parse, no PNG inflate or premultiply, and BC1/BC3 textures at an eighth or a
// quarter of the memory. `CookedAssets` says when one stands in (a later mount's loose file still wins)
// and falls back to the loose file, with a warning, when one cannot be read.
//
// **Nothing here is built by a content pipeline** (R12): every asset is a file the VFS hands over as
// bytes, which is what lets a mod replace one and a game ship art it made this morning.
// A failed load logs once and returns null; callers draw placeholders (05 §8).
//
// **Textures have scopes** (issue #308, Sage.Simulation's Content/AssetScopes.cs): they live in an
// `AssetTable` by stable id (the renderer's sprite and particle ids are these), each at the strongest
// scope it was asked for. A Sector texture is evicted when the sectors that drew it unload (the renderer
// collects them at its frame's safe point), a Ui one when no screen has drawn it for a while, and an
// Engine or Game one stays. Effects, fonts and sounds are kept for the process, as before.
//
// Dev hot reload (05 §3.6, F32): `Reload` drops one asset and raises `Reloaded`. Anything holding the
// old object by reference — the renderer's texture table, a built material — listens and re-resolves;
// anything that calls Load every frame picks the new one up by itself. When there is a second engine
// signal to raise this becomes `EngineSignals.AssetReloaded` (04 §3.5); one signal does not need a
// bus yet.
public sealed class ContentService : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly VirtualFileSystem _vfs;
    private readonly Dictionary<AssetPath, Effect?> _effects = new();
    private readonly AssetTable<Texture2D> _textures;
    private readonly Dictionary<AssetPath, BitmapFont?> _fonts = new();
    private readonly Dictionary<AssetPath, Microsoft.Xna.Framework.Audio.SoundEffect?> _sounds = new();
    private readonly HashSet<AssetPath> _streamed = new();   // files opened to stream, for hot reload (issue #326)

    internal ContentService(ClientHost host, VirtualFileSystem vfs)
    {
        _device = host.GraphicsDevice;
        _vfs = vfs;
        // Missing textures: a magenta/black checker (05 §8), impossible to mistake for real content. Slot 0.
        MissingTexture = new Texture2D(_device, 2, 2) { Name = "(missing)" };
        MissingTexture.SetData(new[] { Color.Magenta, Color.Black, Color.Black, Color.Magenta });
        _textures = new AssetTable<Texture2D>(MissingTexture);
        _readTexture = path => (ReadTexture(path, out long bytes), bytes);
    }

    private readonly Func<AssetPath, (Texture2D?, long)> _readTexture;   // made once: resolving is per sprite per frame

    // The checker every missing texture draws as: texture id 0, never evicted.
    internal Texture2D MissingTexture { get; }

    // The frame the renderer is on, set at its safe point: what a texture asked for now was last used in.
    internal long Frame { get; set; }

    // The texture table (ids, scopes, sizes), for the renderer and `asset_list`.
    internal AssetTable<Texture2D> Textures => _textures;

    // Raised after a texture was evicted, with its path: whoever holds it by reference lets go (the
    // renderer rebuilds its materials).
    internal event Action<AssetPath>? Evicted;

    // Raised on the main thread after an asset has been reloaded, with the path that changed.
    public event Action<AssetPath>? Reloaded;

    // Raised just before a reloaded sound's old `SoundEffect` is disposed, with its path (issue 4h-3).
    // Disposing a `SoundEffect` disposes the instances made from it, so whoever holds instances of
    // it — the audio backends — drops them here, and the mixers invalidate the voices.
    internal event Action<AssetPath>? SoundReplacing;

    // Meshes live in the renderer's table, not here: it sets this and `Reload` asks it for a `.glb`
    // (issue 4h-3). Returns whether a loaded mesh was replaced.
    internal Func<AssetPath, bool>? ModelReloader { get; set; }

    // Drops one asset and loads it again, keeping the cache key. Returns false when nothing was
    // cached under that path — reloading something nobody has asked for yet is not an error, it just
    // has nothing to do.
    //
    // The old GPU object is disposed *after* the reload so that a failed load leaves the old texture
    // on screen rather than a black one: a typo in an image, or a file caught half-written, is not a
    // reason to lose the frame. The warning from the loader says what went wrong; this says what was
    // done about it.
    public bool Reload(AssetPath path)
    {
        if (_textures.TryFind(path, out int textureId))
        {
            // Read again outside the table, and swapped into the same slot so every id handed out sees it
            // (issue #308). A path that had failed is forgotten instead: it loads on next use.
            if (textureId == 0) _textures.Forget(path);
            else
            {
                var fresh = ReadTexture(path, out long bytes);
                if (fresh == null)
                {
                    Log.Warn(LogCat.Assets, $"{path} did not reload; keeping the copy already loaded");
                    return false;
                }
                _textures.Replace(textureId, fresh, bytes)?.Dispose();
            }

            // A font is a texture with a grid over it, and `LoadFont` goes through `LoadTexture`, which
            // caches even a failure — so a font's path is *always* a texture entry too, and always
            // lands here. The wrapper holds the texture just disposed, so it goes with it and is made
            // again from the fresh one on next use. Without this, editing `font.png` while the game
            // runs leaves every label drawing from a disposed texture.
            _fonts.Remove(path);

            Log.Info(LogCat.Assets, $"Reloaded texture {path}");
            Reloaded?.Invoke(path);
            return true;
        }

        if (_effects.TryGetValue(path, out var oldEffect))
        {
            _effects.Remove(path);
            var fresh = LoadEffect(path);
            if (fresh == null || ReferenceEquals(fresh, oldEffect))
            {
                _effects[path] = oldEffect;
                Log.Warn(LogCat.Assets, $"{path} did not reload; keeping the effect already loaded");
                return false;
            }
            oldEffect?.Dispose();
            Log.Info(LogCat.Assets, $"Reloaded effect {path}");
            Reloaded?.Invoke(path);
            return true;
        }

        // A sound (issue 4h-3). MonoGame's ownership is the catch: disposing a `SoundEffect` disposes
        // the instances made from it, so the backends drop theirs first (`SoundReplacing`) and the
        // mixers stop the one-shots and restart the loops from the fresh file.
        if (_sounds.TryGetValue(path, out var oldSound))
        {
            _sounds.Remove(path);
            var fresh = LoadSound(path);
            if (fresh == null || ReferenceEquals(fresh, oldSound))
            {
                _sounds[path] = oldSound;
                Log.Warn(LogCat.Audio, $"{path} did not reload; keeping the sound already loaded");
                return false;
            }
            SoundReplacing?.Invoke(path);
            oldSound?.Dispose();
            Log.Info(LogCat.Assets, $"Reloaded sound {path}");
            Reloaded?.Invoke(path);
            return true;
        }

        // A streamed sound (issue #326) holds no copy to replace: the voices on it let go of their file and
        // the loops start again from the new one, as a sound loaded whole does.
        if (_streamed.Contains(path) && _vfs.Exists(path.Path))
        {
            SoundReplacing?.Invoke(path);
            Log.Info(LogCat.Assets, $"Reloaded streamed sound {path}");
            Reloaded?.Invoke(path);
            return true;
        }

        if (path.Path.Value.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && ModelReloader?.Invoke(path) == true)
        {
            Log.Info(LogCat.Assets, $"Reloaded model {path}");
            Reloaded?.Invoke(path);
            return true;
        }

        return false;
    }

    // Everything currently loaded, for `asset_reload`. Every one of them is a *file* since R12, and all
    // of them can be reloaded: a sound since issue 4h-3 (the backends drop their instances first).
    // Models are not listed: the renderer's mesh table holds them, and `asset_reload <path>` asks it.
    // `asset_list` reads `Listing`, which adds scopes and sizes (issue #308).
    public IEnumerable<(AssetPath Path, string Kind, bool CanReload)> Cached
    {
        get
        {
            foreach (var (_, entry) in _textures.Entries) yield return (entry.Path, "texture", true);
            foreach (var path in _textures.Failed) yield return (path, "texture", true);
            foreach (var path in _effects.Keys) yield return (path, "effect", true);
            foreach (var path in _fonts.Keys) yield return (path, "font", true);
            foreach (var path in _sounds.Keys) yield return (path, "sound", true);
            foreach (var path in _streamed) yield return (path, "sound (streamed)", true);
        }
    }

    // The engine's own font: an ordinary texture, read at runtime like any other, wrapped in the grid
    // that says where each glyph is (13 §3, R12). Nothing is built at build time any more.
    public BitmapFont? LoadFont(AssetPath path)
    {
        if (_fonts.TryGetValue(path, out var font)) return font;
        // A font's texture is the engine's or the game's for good: a label may be drawn from it any time.
        var texture = LoadTexture(path, AssetScope.Game);

        // A character the font does not have draws as a gap rather than throwing: `SpriteBatch`'s own
        // `DrawString` threw on one, so an em dash in a hint line or a name a player typed took the
        // frame and the process with it (13 §3). The grid answers "is this glyph here" with arithmetic.
        font = texture == null ? null : new BitmapFont(texture);
        if (font != null) Log.Debug(LogCat.Assets, $"Loaded font {path}");
        _fonts[path] = font;
        return font;
    }

    // A model's geometry: its cooked `.sgmesh` when one stands in for it, else the `.glb` read now. Null
    // (logged) when neither can be read; the renderer draws the error mesh.
    internal MeshGeometry? LoadModel(AssetPath path)
    {
        WorkStats.LoadStarted();   // `stat assets` (issue #300)
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return LoadModelTimed(path); }
        finally { WorkStats.LoadFinished(System.Diagnostics.Stopwatch.GetTimestamp() - started); }
    }

    private MeshGeometry? LoadModelTimed(AssetPath path)
    {
        if (CookedAssets.LoadMesh(_vfs, path.Path) is { } cooked)
        {
            Log.Debug(LogCat.Assets, $"Loaded cooked model {path}");
            return cooked;
        }
        using var stream = Open(path);
        return stream == null ? null : MeshGeometry.ReadGlb(stream, path.ToString());
    }

    // Opens an asset as bytes, for whoever knows what to do with them (a model, and later anything
    // else a runtime loader reads). The VFS decides which mount wins (05 §3.2).
    public System.IO.Stream? Open(AssetPath path)
    {
        if (_vfs.Which(path.Path) is not { } mount)
        {
            Log.Warn(LogCat.Assets, $"Asset '{path}' not found in any mount");
            return null;
        }
        try { return mount.Open(path.Path); }
        catch (System.IO.IOException ex)
        {
            Log.Warn(LogCat.Assets, $"Asset '{path}': {ex.Message}");
            return null;
        }
    }

    public Effect? LoadEffect(AssetPath path)
    {
        if (_effects.TryGetValue(path, out var effect)) return effect;
        effect = null;
        var mount = _vfs.Which(path.Path);
        if (mount == null) Log.Warn(LogCat.Shaders, $"Effect '{path}' not found in any mount");
        else
        {
            try
            {
                using var stream = mount.Open(path.Path);
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                effect = new Effect(_device, memory.ToArray()) { Name = path.ToString() };
                Log.Debug(LogCat.Shaders, $"Loaded effect {path} from {mount.Name}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                Log.Error(LogCat.Shaders, $"Effect '{path}' from {mount.Name} failed to load: {ex.Message}");
            }
        }
        return _effects[path] = effect;
    }

    // A WAV or an Ogg Vorbis file through the VFS (11 §3, 05 §3.2). `SoundEffect.FromStream` takes RIFF
    // wave data; a `.ogg` is decoded to PCM whole first (OggVorbis, issue #302).
    public Microsoft.Xna.Framework.Audio.SoundEffect? LoadSound(AssetPath path)
    {
        if (_sounds.TryGetValue(path, out var sound)) return sound;
        sound = null;

        if (_vfs.Which(path.Path) is not { } mount)
        {
            Log.Once(LogCat.Audio, LogLevel.Warn, $"sound:{path}", $"Sound '{path}' not found in any mount");
        }
        else
        {
            try
            {
                using var stream = mount.Open(path.Path);
                if (path.Path.Value.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                {
                    var pcm = OggVorbis.Decode(stream);
                    sound = new Microsoft.Xna.Framework.Audio.SoundEffect(pcm.Samples, pcm.SampleRate,
                        pcm.Channels == 1 ? Microsoft.Xna.Framework.Audio.AudioChannels.Mono : Microsoft.Xna.Framework.Audio.AudioChannels.Stereo);
                }
                else sound = Microsoft.Xna.Framework.Audio.SoundEffect.FromStream(stream);
                sound.Name = path.ToString();
                Log.Debug(LogCat.Audio, $"Loaded sound {path} ({sound.Duration.TotalSeconds:F2}s) from {mount.Name}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException or InvalidDataException)
            {
                // A missing or unreadable sound costs the sound, not the frame (11 §8).
                Log.Once(LogCat.Audio, LogLevel.Error, $"sound-load:{path}", $"Sound '{path}': {ex.Message}");
            }
        }
        return _sounds[path] = sound;
    }

    // An Ogg Vorbis file opened to be decoded as it plays (issue #326, `"stream": true` on the sound): nothing
    // is cached, each voice reads its own copy of the file a few buffers ahead. Null when it is in no mount
    // or is not Ogg Vorbis (logged once).
    internal PcmStreamer? OpenSoundStream(AssetPath path, bool loop, long loopStart = 0, long loopEnd = 0)
    {
        var streamer = PcmStreamer.Open(_vfs, path, loop, out string? error, loopStart: loopStart, loopEnd: loopEnd);
        if (streamer == null)
        {
            Log.Once(LogCat.Audio, LogLevel.Error, $"sound-stream:{path}", $"Sound '{path}' cannot stream: {error}");
            return null;
        }
        if (_streamed.Add(path))
            Log.Debug(LogCat.Audio, $"Streaming sound {path} ({streamer.Channels} channel(s), {streamer.SampleRate} Hz, {streamer.BufferedBytes / 1024} KB buffered)");
        return streamer;
    }

    // A texture, at the game's scope: kept for the process (a game's own code, a post effect's picture).
    public Texture2D? LoadTexture(AssetPath path) => LoadTexture(path, AssetScope.Game);

    // A texture at a scope (issue #308): loaded on first use, and asked for again it keeps the stronger of
    // the two scopes. Null when it cannot be read (logged once; the path is not tried again until reload).
    internal Texture2D? LoadTexture(AssetPath path, AssetScope scope)
    {
        int id = ResolveTexture(path, scope);
        return id == 0 ? null : _textures[id];
    }

    // The texture's id in the table, loading it on first use; 0 (the checker) when it cannot be read.
    internal int ResolveTexture(AssetPath path, AssetScope scope)
    {
        _textures.TryResolve(path, scope, Frame, null, _readTexture, out int id);
        return id;
    }

    // As ResolveTexture, a Sector-scoped load waiting for a frame with upload budget left (05 §3.4).
    internal bool TryResolveTexture(AssetPath path, AssetScope scope, UploadBudget budget, out int id) =>
        _textures.TryResolve(path, scope, Frame, budget, _readTexture, out id);

    // Frees a texture (issue #308): the renderer's eviction, at its safe point. Fonts drawn from it go too
    // (made again on next use), and `Evicted` tells whoever holds it by reference. False when it was not
    // loaded, or is the engine's.
    internal bool EvictTexture(AssetPath path)
    {
        if (!_textures.TryEvict(path, out var texture) || texture == null) return false;
        Released(path, texture);
        return true;
    }

    // UI-scoped textures no screen has drawn for a while (AssetScopes.UiKeepFrames). Returns how many.
    internal int EvictUnusedUi() =>
        _textures.CollectUnusedUi(Frame, AssetScopes.UiKeepFrames, Released);

    private void Released(AssetPath path, Texture2D texture)
    {
        _fonts.Remove(path);
        texture.Dispose();
        Log.Debug(LogCat.Assets, $"Evicted texture {path}");
        Evicted?.Invoke(path);
    }

    // Reads a texture off its mount (or its cooked file) onto the GPU, outside the table.
    private Texture2D? ReadTexture(AssetPath path, out long bytes)
    {
        bytes = 0;
        Texture2D? texture = null;
        if (CookedAssets.LoadTexture(_vfs, path.Path) is { } cooked && FromCooked(path, cooked) is { } fromCooked)
        {
            Log.Debug(LogCat.Assets, $"Loaded cooked texture {path} ({cooked.Width}x{cooked.Height} {cooked.Format})");
            bytes = cooked.Data.Length;
            return fromCooked;
        }
        if (_vfs.Which(path.Path) is not { } mount)
        {
            Log.Warn(LogCat.Assets, $"Texture '{path}' not found in any mount");
        }
        else
        {
            WorkStats.LoadStarted();   // `stat assets` (issue #300)
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // Decoded, premultiplied and mipmapped headlessly (issue #317), as a cook would, uncompressed.
                var loose = CookedAssets.ReadLooseTexture(mount, path.Path);
                texture = FromCooked(path, loose) ?? throw new InvalidDataException("the GPU texture could not be made");
                bytes = loose.GpuBytes;
                WorkStats.Uploaded(bytes);
                Log.Debug(LogCat.Assets, $"Loaded texture {path} ({texture.Width}x{texture.Height}, {texture.LevelCount} level(s)) from {mount.Name}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException or InvalidDataException)
            {
                Log.Warn(LogCat.Assets, $"Texture '{path}' from {mount.Name} failed to decode: {ex.Message}");
            }
            finally { WorkStats.LoadFinished(System.Diagnostics.Stopwatch.GetTimestamp() - started); }
        }
        return texture;
    }

    // A cooked texture on the GPU, every mip level of it: its blocks as they are where the device samples
    // DXT1/DXT5, decoded to RGBA on the CPU where it does not (MonoGame refuses the format with
    // NotSupportedException). A loose texture comes here too, as RGBA (`CookedAssets.ReadLooseTexture`).
    private Texture2D? FromCooked(AssetPath path, CookedTextureData cooked)
    {
        var format = cooked.Format switch
        {
            CookedTextureFormat.Bc1 => SurfaceFormat.Dxt1,
            CookedTextureFormat.Bc3 => SurfaceFormat.Dxt5,
            _ => SurfaceFormat.Color,
        };
        try
        {
            if (format != SurfaceFormat.Color)
            {
                try { return Upload(path, cooked, format, level => cooked.Levels[level]); }
                catch (NotSupportedException)
                {
                    Log.Once(LogCat.Assets, LogLevel.Warn, "cooked-no-s3tc",
                             "This graphics device does not sample DXT1/DXT5: cooked textures are decoded to RGBA as they load");
                }
            }
            return Upload(path, cooked, SurfaceFormat.Color, cooked.ToRgba);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Log.Warn(LogCat.Assets, $"Texture data for '{path}' could not be made into a texture ({ex.Message})");
            return null;
        }
    }

    private Texture2D Upload(AssetPath path, CookedTextureData cooked, SurfaceFormat format, Func<int, byte[]> level)
    {
        var texture = new Texture2D(_device, cooked.Width, cooked.Height, cooked.HasMips, format) { Name = path.ToString() };
        try
        {
            for (int i = 0; i < cooked.LevelCount; i++)
            {
                var data = level(i);
                texture.SetData(i, null, data, 0, data.Length);
            }
            return texture;
        }
        catch
        {
            texture.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var e in _effects.Values) e?.Dispose();
        foreach (var (_, entry) in _textures.Entries) entry.Value?.Dispose();
        MissingTexture.Dispose();
        foreach (var sound in _sounds.Values) sound?.Dispose();
    }
}
