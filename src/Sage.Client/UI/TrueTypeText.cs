#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Sage.UI;

namespace Sage.Client;

// Draws text in a TrueType font (issue #338): Sage.UI's GlyphAtlas rasterises each character the first
// time it is drawn at a pixel size, and this keeps a texture per atlas, copies the rows that changed
// into it, and queues a UiDraw image per glyph — so it goes through the same batch, clip and fade as
// every other widget. Positions come from the font's own advances and kerning at the drawn size, the
// metrics layout measured with, so a line ends where its label said it would.
//
// An atlas is per (font, whole pixel size); a fractional size draws the nearest one scaled. When the
// fonts are dropped (content reloaded) every atlas goes with them. A texture an atlas outgrew is
// disposed a frame later, after the batch that may still draw from it.
internal sealed class TrueTypeText : IDisposable
{
    private sealed class Atlas
    {
        public required GlyphAtlas Glyphs;
        public Texture2D? Texture;
        public uint[] Staging = Array.Empty<uint>();
    }

    private readonly GraphicsDevice _device;
    private readonly Dictionary<(TrueTypeFont, int), Atlas> _atlases = new();
    private readonly List<Texture2D> _retired = new();
    private int _fontsVersion = -1;

    public TrueTypeText(GraphicsDevice device) => _device = device;

    public int AtlasCount => _atlases.Count;

    // Once a frame, before anything is drawn: frees what last frame's batch was the last to use.
    public void BeginFrame(UiFonts? fonts)
    {
        foreach (var texture in _retired) texture.Dispose();
        _retired.Clear();
        if (fonts == null || fonts.Version == _fontsVersion) return;
        _fontsVersion = fonts.Version;
        foreach (var atlas in _atlases.Values)
            if (atlas.Texture != null) _retired.Add(atlas.Texture);
        _atlases.Clear();
    }

    // text[start..start+length] at `pixelSize` (the em in pixels), its line's top-left at (x, y).
    public void Draw(UiDraw ui, TrueTypeFont font, float pixelSize, string text, int start, int length, float x, float y, Color colour)
    {
        if (length <= 0 || !(pixelSize > 0f)) return;
        int size = Math.Clamp((int)MathF.Round(pixelSize), 1, 512);
        float k = pixelSize / size;
        var atlas = AtlasFor(font, size);

        // Every glyph rasterised first, so the texture is uploaded once and big enough before a quad
        // names it.
        var span = text.AsSpan(start, length);
        for (var rest = span; !rest.IsEmpty;)
        {
            Rune.DecodeFromUtf16(rest, out var rune, out int used);
            rest = rest[used..];
            atlas.Glyphs.Get(rune.Value);
        }
        Upload(atlas);
        var texture = atlas.Texture;
        if (texture == null) return;

        float baseline = y + font.Ascent(pixelSize);
        float pen = x;
        int previous = -1;
        for (var rest = span; !rest.IsEmpty;)
        {
            Rune.DecodeFromUtf16(rest, out var rune, out int used);
            rest = rest[used..];
            int c = rune.Value;
            if (previous >= 0) pen += font.Kerning(previous, c, pixelSize);
            var glyph = atlas.Glyphs.Get(c);
            if (glyph.Width > 0)
            {
                int left = (int)MathF.Round(pen + glyph.OffsetX * k), top = (int)MathF.Round(baseline + glyph.OffsetY * k);
                var destination = new Rectangle(left, top, Math.Max((int)MathF.Round(glyph.Width * k), 1), Math.Max((int)MathF.Round(glyph.Height * k), 1));
                ui.Image(texture, destination, colour, new Rectangle(glyph.X, glyph.Y, glyph.Width, glyph.Height));
            }
            pen += font.Advance(c, pixelSize);
            previous = c;
        }
    }

    private Atlas AtlasFor(TrueTypeFont font, int size)
    {
        if (_atlases.TryGetValue((font, size), out var atlas)) return atlas;
        atlas = new Atlas { Glyphs = new GlyphAtlas(font, size) };
        _atlases[(font, size)] = atlas;
        return atlas;
    }

    // Copies the rows that changed into the texture: white, with the coverage as alpha (UiDraw blends
    // non-premultiplied), so the colour is the tint.
    private void Upload(Atlas atlas)
    {
        var glyphs = atlas.Glyphs;
        if (atlas.Texture == null || atlas.Texture.Height != glyphs.Height)
        {
            if (atlas.Texture != null) _retired.Add(atlas.Texture);
            atlas.Texture = new Texture2D(_device, GlyphAtlas.Width, glyphs.Height, false, SurfaceFormat.Color) { Name = $"(font {glyphs.Font.Name} {glyphs.PixelSize}px)" };
            atlas.Staging = new uint[GlyphAtlas.Width * glyphs.Height];
            Fill(atlas, 0, glyphs.Height);
            atlas.Texture.SetData(atlas.Staging);
            glyphs.MarkClean();
            return;
        }
        int top = glyphs.DirtyTop, bottom = Math.Min(glyphs.DirtyBottom, glyphs.Height);
        if (top >= bottom) return;
        Fill(atlas, top, bottom);
        atlas.Texture.SetData(0, new Rectangle(0, top, GlyphAtlas.Width, bottom - top), atlas.Staging, top * GlyphAtlas.Width, (bottom - top) * GlyphAtlas.Width);
        glyphs.MarkClean();
    }

    private static void Fill(Atlas atlas, int top, int bottom)
    {
        var pixels = atlas.Glyphs.Pixels;
        var staging = atlas.Staging;
        for (int i = top * GlyphAtlas.Width, end = bottom * GlyphAtlas.Width; i < end; i++)
            staging[i] = 0x00FFFFFFu | ((uint)pixels[i] << 24);
    }

    public void Dispose()
    {
        foreach (var texture in _retired) texture.Dispose();
        _retired.Clear();
        foreach (var atlas in _atlases.Values) atlas.Texture?.Dispose();
        _atlases.Clear();
    }
}
