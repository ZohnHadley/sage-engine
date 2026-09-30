#nullable enable
using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Sage.Client;

// The renderer's named off-screen targets (issue #77, decision D3): what a minimap, a mirror, a
// security camera's screen or an editor viewport draws into. Small and named, with one fixed size
// each; a camera names one as its target, and a material samples one as `rt:<name>`.
//
// A name gets a stable id the first time anything asks for it, so views and materials carry an int.
// The texture itself is made on first use: at the size code declared (`Renderer.DeclareTarget`), or at
// `DefaultSize` for a target content named and no code declared — a data-only game can still have a
// minimap. Declaring a new size, or releasing a target, remakes it and rebuilds the materials that
// sample it (`Changed`).
internal sealed class RenderTargetPool : IDisposable
{
    public const int DefaultSize = 512;
    public const int MaxSize = 4096;

    private sealed class Slot
    {
        public required string Name;
        public int Width = DefaultSize, Height = DefaultSize;
        public SurfaceFormat Format = SurfaceFormat.Color;
        public DepthFormat Depth = DepthFormat.Depth24;
        public bool Declared;
        public RenderTarget2D? Texture;
    }

    private readonly GraphicsDevice _device;
    private readonly List<Slot> _slots = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

    public RenderTargetPool(GraphicsDevice device) { _device = device; }

    // A target was remade or released: anything holding its texture (a built material) must let go.
    public event Action? Changed;

    public int Count => _slots.Count;

    public string Name(int id) => _slots[id].Name;

    // The id for a name, giving it a slot (undeclared, default size) the first time.
    public int Id(string name)
    {
        if (_ids.TryGetValue(name, out int id)) return id;
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("A render target needs a name.", nameof(name));
        id = _slots.Count;
        _slots.Add(new Slot { Name = name });
        _ids[name] = id;
        return id;
    }

    public bool TryFind(string name, out int id) => _ids.TryGetValue(name, out id);

    public int Declare(string name, int width, int height) => Declare(name, width, height, SurfaceFormat.Color, DepthFormat.Depth24);

    // With its pixel format and depth buffer (issue 4h-1): another size, format or depth remakes it.
    public int Declare(string name, int width, int height, SurfaceFormat format, DepthFormat depth)
    {
        if (width < 1 || height < 1 || width > MaxSize || height > MaxSize)
            throw new ArgumentOutOfRangeException(nameof(width), $"Render target '{name}' is {width}x{height}; each side is 1 to {MaxSize} pixels.");
        int id = Id(name);
        var slot = _slots[id];
        slot.Declared = true;
        if (slot.Width == width && slot.Height == height && slot.Format == format && slot.Depth == depth) return id;

        slot.Width = width;
        slot.Height = height;
        slot.Format = format;
        slot.Depth = depth;
        if (slot.Texture != null)
        {
            slot.Texture.Dispose();
            slot.Texture = null;
            Changed?.Invoke();
        }
        return id;
    }

    public bool Release(string name)
    {
        if (!_ids.TryGetValue(name, out int id)) return false;
        var slot = _slots[id];
        bool had = slot.Texture != null;
        slot.Texture?.Dispose();
        slot.Texture = null;
        slot.Declared = false;
        slot.Width = slot.Height = DefaultSize;
        slot.Format = SurfaceFormat.Color;
        slot.Depth = DepthFormat.Depth24;
        if (had) Changed?.Invoke();
        return true;
    }

    public Point Size(int id) => new(_slots[id].Width, _slots[id].Height);

    public RenderTarget2D? Existing(int id) => _slots[id].Texture;

    // The texture, made now if it has not been. PreserveContents: several views may draw into one
    // target (an atlas of small views), and a target keeps last frame's picture for whoever samples it
    // before it is drawn again (a world that renders later in the frame).
    public RenderTarget2D Texture(int id)
    {
        var slot = _slots[id];
        if (slot.Texture != null) return slot.Texture;
        if (!slot.Declared)
            Log.Info(LogCat.Render, $"Render target '{slot.Name}' was never declared; made at {slot.Width}x{slot.Height}");
        slot.Texture = new RenderTarget2D(_device, slot.Width, slot.Height, false, slot.Format,
                                          slot.Depth, 0, RenderTargetUsage.PreserveContents);
        return slot.Texture;
    }

    public void Dispose()
    {
        foreach (var slot in _slots) slot.Texture?.Dispose();
        _slots.Clear();
        _ids.Clear();
    }
}
