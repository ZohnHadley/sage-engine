#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// Client asset loading through the VFS (docs/design/05 §3.6), interim until the AssetServer with
// scopes and async loading (05 §14 step 2):
//   - models (.glb, read at runtime by `GltfLoader`);
//   - compiled effects (.mgfxo, 07 §3.1 — the one thing still built ahead of time);
//   - textures (.png/.jpg via Texture2D.FromStream, premultiplied on load, 07 §13);
//   - sounds (.wav via SoundEffect.FromStream, 11 §3), and the bitmap font, which is a texture.
//
// **Nothing here is built by a content pipeline** (R12): every asset is a file the VFS hands over as
// bytes, which is what lets a mod replace one and a game ship art it made this morning.
// Everything loaded is cached for the process and disposed with the service. A failed load logs once
// and returns null; callers draw placeholders (05 §8).
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
    private readonly Dictionary<AssetPath, Texture2D?> _textures = new();
    private readonly Dictionary<AssetPath, BitmapFont?> _fonts = new();
    private readonly Dictionary<AssetPath, Microsoft.Xna.Framework.Audio.SoundEffect?> _sounds = new();

    internal ContentService(ClientHost host, VirtualFileSystem vfs)
    {
        _device = host.GraphicsDevice;
        _vfs = vfs;
    }

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
        if (_textures.TryGetValue(path, out var oldTexture))
        {
            _textures.Remove(path);
            var fresh = LoadTexture(path);
            if (fresh == null || ReferenceEquals(fresh, oldTexture))
            {
                _textures[path] = oldTexture;
                Log.Warn(LogCat.Assets, $"{path} did not reload; keeping the copy already loaded");
                return false;
            }
            oldTexture?.Dispose();

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

        if (path.Path.Value.EndsWith(".glb", StringComparison.OrdinalIgnoreCase) && ModelReloader?.Invoke(path) == true)
        {
            Log.Info(LogCat.Assets, $"Reloaded model {path}");
            Reloaded?.Invoke(path);
            return true;
        }

        return false;
    }

    // Everything currently loaded, for `asset_list`. Every one of them is a *file* since R12, and all
    // of them can be reloaded: a sound since issue 4h-3 (the backends drop their instances first).
    // Models are not listed: the renderer's mesh table holds them, and `asset_reload <path>` asks it.
    public IEnumerable<(AssetPath Path, string Kind, bool CanReload)> Cached
    {
        get
        {
            foreach (var path in _textures.Keys) yield return (path, "texture", true);
            foreach (var path in _effects.Keys) yield return (path, "effect", true);
            foreach (var path in _fonts.Keys) yield return (path, "font", true);
            foreach (var path in _sounds.Keys) yield return (path, "sound", true);
        }
    }

    // The engine's own font: an ordinary texture, read at runtime like any other, wrapped in the grid
    // that says where each glyph is (13 §3, R12). Nothing is built at build time any more.
    public BitmapFont? LoadFont(AssetPath path)
    {
        if (_fonts.TryGetValue(path, out var font)) return font;

        // A character the font does not have draws as a gap rather than throwing: `SpriteBatch`'s own
        // `DrawString` threw on one, so an em dash in a hint line or a name a player typed took the
        // frame and the process with it (13 §3). The grid answers "is this glyph here" with arithmetic.
        var texture = LoadTexture(path);
        font = texture == null ? null : new BitmapFont(texture);
        if (font != null) Log.Debug(LogCat.Assets, $"Loaded font {path}");
        _fonts[path] = font;
        return font;
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

    // A WAV through the VFS (11 §3, 05 §3.2). `SoundEffect.FromStream` takes PCM wave data, which is
    // why v1 is WAV: OGG needs a decoder library, and that decision is still open (05).
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
                sound = Microsoft.Xna.Framework.Audio.SoundEffect.FromStream(stream);
                sound.Name = path.ToString();
                Log.Debug(LogCat.Audio, $"Loaded sound {path} ({sound.Duration.TotalSeconds:F2}s) from {mount.Name}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                // A missing or unreadable sound costs the sound, not the frame (11 §8).
                Log.Once(LogCat.Audio, LogLevel.Error, $"sound-load:{path}", $"Sound '{path}': {ex.Message}");
            }
        }
        return _sounds[path] = sound;
    }

    public Texture2D? LoadTexture(AssetPath path)
    {
        if (_textures.TryGetValue(path, out var texture)) return texture;
        texture = null;
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
                using var stream = mount.Open(path.Path);
                texture = Texture2D.FromStream(_device, stream);
                texture.Name = path.ToString();
                Premultiply(texture);
                WorkStats.Uploaded((long)texture.Width * texture.Height * 4);
                Log.Debug(LogCat.Assets, $"Loaded texture {path} ({texture.Width}x{texture.Height}) from {mount.Name}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                Log.Warn(LogCat.Assets, $"Texture '{path}' from {mount.Name} failed to decode: {ex.Message}");
            }
            finally { WorkStats.LoadFinished(System.Diagnostics.Stopwatch.GetTimestamp() - started); }
        }
        return _textures[path] = texture;
    }

    // Blend states assume premultiplied alpha (07 §13); FromStream returns straight alpha.
    private static void Premultiply(Texture2D texture)
    {
        if (texture.Format != SurfaceFormat.Color) return;
        var pixels = new Color[texture.Width * texture.Height];
        texture.GetData(pixels);
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            if (c.A == 255) continue;
            pixels[i] = new Color(c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255, c.A);
        }
        texture.SetData(pixels);
    }

    public void Dispose()
    {
        foreach (var e in _effects.Values) e?.Dispose();
        foreach (var t in _textures.Values) t?.Dispose();
        foreach (var sound in _sounds.Values) sound?.Dispose();
    }
}
