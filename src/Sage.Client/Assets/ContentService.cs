#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace sage_engine;

// Client asset loading through the VFS (docs/design/05 §3.6), interim until the AssetServer with
// scopes and async loading (05 §14 step 2, TODO R12):
//   - MGCB-built .xnb models (the path without extension, e.g. "stanford_bunny");
//   - compiled effects (.mgfxo, 07 §3.1);
//   - textures (.png/.jpg via Texture2D.FromStream, premultiplied on load, 07 §13; or .xnb).
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
    private readonly VfsContentManager _content;
    private readonly Dictionary<AssetPath, Effect?> _effects = new();
    private readonly Dictionary<AssetPath, Texture2D?> _textures = new();
    private readonly Dictionary<AssetPath, Model?> _models = new();
    private readonly Dictionary<AssetPath, SpriteFont?> _fonts = new();
    private readonly Dictionary<AssetPath, Microsoft.Xna.Framework.Audio.SoundEffect?> _sounds = new();

    internal ContentService(ClientHost host, VirtualFileSystem vfs)
    {
        _device = host.GraphicsDevice;
        _vfs = vfs;
        _content = new VfsContentManager(host.Game.Services, vfs);
    }

    // Raised on the main thread after an asset has been reloaded, with the path that changed.
    public event Action<AssetPath>? Reloaded;

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
            // An extensionless path is an MGCB .xnb, and the ContentManager owns that object and its
            // own cache: reloading would hand back the very same instance, and disposing the "old"
            // one would destroy the live texture. Rebuilding content is the way to change those.
            if (path.Path.Extension.Length == 0)
            {
                Log.Debug(LogCat.Assets, $"{path} is built content (.xnb); rebuild to change it");
                return false;
            }

            _textures.Remove(path);
            var fresh = LoadTexture(path);
            if (fresh == null || ReferenceEquals(fresh, oldTexture))
            {
                _textures[path] = oldTexture;
                Log.Warn(LogCat.Assets, $"{path} did not reload; keeping the copy already loaded");
                return false;
            }
            oldTexture?.Dispose();
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

        // Loaded, but not by us: models and fonts are MGCB .xnb owned by the ContentManager, which
        // has its own cache and hands back the same instance. Say so rather than "not loaded", which
        // would be a lie about a thing that is plainly on screen.
        if (_models.ContainsKey(path) || _fonts.ContainsKey(path))
        {
            Log.Info(LogCat.Assets, $"{path} is built content (.xnb); rebuild the content to change it");
            return false;
        }

        return false;
    }

    // Everything currently loaded, for `asset_list`: the whole truth, including what cannot be
    // reloaded, because a list that quietly leaves out the models is worse than no list.
    public IEnumerable<(AssetPath Path, string Kind, bool CanReload)> Cached
    {
        get
        {
            foreach (var path in _textures.Keys) yield return (path, "texture", path.Path.Extension.Length > 0);
            foreach (var path in _effects.Keys) yield return (path, "effect", true);
            foreach (var path in _models.Keys) yield return (path, "model (.xnb)", false);
            foreach (var path in _fonts.Keys) yield return (path, "font (.xnb)", false);
        }
    }

    // A SpriteFont built by MGCB (13 §3), by path without the extension, like a model.
    public SpriteFont? LoadFont(AssetPath path)
    {
        if (_fonts.TryGetValue(path, out var font)) return font;
        try { font = _content.Load<SpriteFont>(path.ToString()); }
        catch (Exception ex) when (ex is ContentLoadException or System.IO.FileNotFoundException)
        {
            Log.Warn(LogCat.Assets, $"Font '{path}': {ex.Message}");
            font = null;
        }
        if (font != null)
        {
            // A character the font was not built with must not be fatal. `SpriteBatch.DrawString`
            // throws on one, so an em dash in a hint line, a name a player typed, or the first
            // localised string would crash the frame and the process with it (13 §3: glyph coverage is
            // fixed at build time, which is the deviation this guards). A visible '?' is the right
            // answer: it says "font", not "the game is broken".
            font.DefaultCharacter = '?';
            Log.Debug(LogCat.Assets, $"Loaded font {path}");
        }
        _fonts[path] = font;
        return font;
    }

    public Model? LoadModel(AssetPath path)
    {
        if (_models.TryGetValue(path, out var model)) return model;
        try { model = _content.Load<Model>(path.ToString()); }
        catch (ContentLoadException ex) { Log.Warn(LogCat.Assets, $"Model '{path}': {ex.Message}"); model = null; }
        return _models[path] = model;
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
        string ext = path.Path.Extension;
        if (ext.Length == 0)
        {
            try { texture = _content.Load<Texture2D>(path.ToString()); }
            catch (ContentLoadException ex) { Log.Warn(LogCat.Assets, $"Texture '{path}': {ex.Message}"); }
        }
        else if (_vfs.Which(path.Path) is not { } mount)
        {
            Log.Warn(LogCat.Assets, $"Texture '{path}' not found in any mount");
        }
        else
        {
            try
            {
                using var stream = mount.Open(path.Path);
                texture = Texture2D.FromStream(_device, stream);
                texture.Name = path.ToString();
                Premultiply(texture);
                Log.Debug(LogCat.Assets, $"Loaded texture {path} ({texture.Width}x{texture.Height}) from {mount.Name}");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                Log.Warn(LogCat.Assets, $"Texture '{path}' from {mount.Name} failed to decode: {ex.Message}");
            }
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
        foreach (var t in _textures.Values) t?.Dispose();   // xnb textures are owned by the ContentManager too; double Dispose is harmless
        _content.Dispose();
    }

    private sealed class VfsContentManager : ContentManager
    {
        private readonly VirtualFileSystem _vfs;

        public VfsContentManager(IServiceProvider services, VirtualFileSystem vfs) : base(services) { _vfs = vfs; }

        protected override Stream OpenStream(string assetName)
        {
            var path = VirtualPath.Parse(assetName + ".xnb");
            var mount = _vfs.Which(path)
                        ?? throw new ContentLoadException($"not found in any mount (looked for {path})");
            Log.Debug(LogCat.Assets, $"Loading {path} from {mount.Name}");
            return mount.Open(path);
        }
    }
}
