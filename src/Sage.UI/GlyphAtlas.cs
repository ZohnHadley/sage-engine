#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.UI;

// Where a glyph is in an atlas and how it sits on the pen: its rect in the atlas (pixels) and its
// offset from the pen on the baseline (y down). Width 0: nothing to draw (a space, or no room left).
internal readonly record struct AtlasGlyph(int X, int Y, int Width, int Height, int OffsetX, int OffsetY);

// One TrueType font at one pixel size, rasterised a glyph at a time into a coverage image (issue #338):
// the first time a character is drawn it is rendered by stb_truetype and packed into the next free
// place on a shelf; after that it is a dictionary lookup. Headless — the client copies Pixels into a
// texture when Version moves (the rows from DirtyTop to DirtyBottom) and draws quads from the rects.
//
// The image starts Width × 128 and doubles in height up to MaxHeight; rects already handed out stay
// where they are when it grows. When even that is full, a glyph that does not fit draws as nothing and
// says so once — a page of text in a thousand characters at a huge size, which a UI does not draw.
internal sealed class GlyphAtlas
{
    public const int Width = 512;
    public const int MaxHeight = 2048;
    private const int Gap = 1;   // between glyphs, so filtering never bleeds a neighbour in

    private readonly Dictionary<int, AtlasGlyph> _glyphs = new();
    private int _x = Gap, _y = Gap, _shelf;
    private bool _warned;

    public GlyphAtlas(TrueTypeFont font, int pixelSize)
    {
        Font = font;
        PixelSize = pixelSize;
        Height = 128;
        Pixels = new byte[Width * Height];
        DirtyTop = int.MaxValue;
        DirtyBottom = 0;
    }

    public TrueTypeFont Font { get; }
    public int PixelSize { get; }
    public int Height { get; private set; }

    // Coverage, 0..255, Width bytes to a row.
    public byte[] Pixels { get; private set; }

    // Moves whenever a glyph is added (or the image grows).
    public int Version { get; private set; }

    // The rows changed since MarkClean: [DirtyTop, DirtyBottom). Empty when DirtyTop >= DirtyBottom.
    public int DirtyTop { get; private set; }
    public int DirtyBottom { get; private set; }

    public int Count => _glyphs.Count;

    public void MarkClean()
    {
        DirtyTop = int.MaxValue;
        DirtyBottom = 0;
    }

    // The glyph for `codepoint`, rasterised now if it is new.
    public AtlasGlyph Get(int codepoint)
    {
        if (_glyphs.TryGetValue(codepoint, out var glyph)) return glyph;
        Font.GlyphBox(codepoint, PixelSize, out int x0, out int y0, out int x1, out int y1);
        int w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0)
            glyph = default;
        else if (Place(w, h, out int x, out int y))
        {
            Font.Rasterise(codepoint, PixelSize, Pixels, y * Width + x, w, h, Width);
            DirtyTop = Math.Min(DirtyTop, y);
            DirtyBottom = Math.Max(DirtyBottom, y + h);
            glyph = new AtlasGlyph(x, y, w, h, x0, y0);
        }
        else
        {
            if (!_warned) Log.Warn(LogCat.UI, $"font {Font.Name} at {PixelSize}px: its glyph atlas is full; new characters are drawn as nothing");
            _warned = true;
            glyph = default;
        }
        _glyphs[codepoint] = glyph;
        Version++;
        return glyph;
    }

    // A shelf packer: left to right along a row as tall as its tallest glyph, then the next row.
    private bool Place(int w, int h, out int x, out int y)
    {
        x = y = 0;
        if (w + 2 * Gap > Width) return false;
        if (_x + w + Gap > Width)
        {
            _y += _shelf + Gap;
            _x = Gap;
            _shelf = 0;
        }
        while (_y + h + Gap > Height)
        {
            if (Height >= MaxHeight) return false;
            Grow();
        }
        x = _x;
        y = _y;
        _x += w + Gap;
        _shelf = Math.Max(_shelf, h);
        return true;
    }

    private void Grow()
    {
        var pixels = new byte[Width * Height * 2];
        Array.Copy(Pixels, pixels, Pixels.Length);
        Pixels = pixels;
        Height *= 2;
        DirtyTop = 0;
        DirtyBottom = Height;   // a new image: all of it goes up again
        Version++;
    }
}
