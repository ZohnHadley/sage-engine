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
public sealed class ContentService : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly VirtualFileSystem _vfs;
    private readonly VfsContentManager _content;
    private readonly Dictionary<AssetPath, Effect?> _effects = new();
    private readonly Dictionary<AssetPath, Texture2D?> _textures = new();
    private readonly Dictionary<AssetPath, Model?> _models = new();
    private readonly Dictionary<AssetPath, SpriteFont?> _fonts = new();

    internal ContentService(ClientHost host, VirtualFileSystem vfs)
    {
        _device = host.GraphicsDevice;
        _vfs = vfs;
        _content = new VfsContentManager(host.Game.Services, vfs);
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
        if (font != null) Log.Debug(LogCat.Assets, $"Loaded font {path}");
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
