#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using StbTrueTypeSharp;

namespace Sage.UI;

// A TrueType or OpenType font, read whole from its file and kept (issue #338, docs/design/13 "fonts per
// style"). Headless: it answers metrics — line height, advances, kerning — for layout, and rasterises a
// glyph into a byte buffer for whoever draws (GlyphAtlas, then the client's texture). So a label is laid
// out with exactly the advances it is drawn with, in a test as on screen.
//
// Sizes are the em in whatever unit the caller works in (virtual units for layout, pixels for drawing):
// every metric is linear in it, which is what lets layout and drawing agree at any UI scale.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed unsafe class TrueTypeFont : IDisposable
{
    private readonly StbTrueType.stbtt_fontinfo _info;
    private readonly float _perUnit;   // em per font unit: size × this = scale
    private readonly Dictionary<int, (int Glyph, int Advance)> _glyphs = new();
    private bool _disposed;

    private TrueTypeFont(StbTrueType.stbtt_fontinfo info, string name)
    {
        _info = info;
        Name = name;
        _perUnit = StbTrueType.stbtt_ScaleForMappingEmToPixels(info, 1f);
        int ascent, descent, gap;
        StbTrueType.stbtt_GetFontVMetrics(info, &ascent, &descent, &gap);
        _ascent = ascent;
        _descent = descent;
        _gap = gap;
    }

    private readonly int _ascent, _descent, _gap;

    // Reads a font from its file's bytes; InvalidDataException when they are not one stb_truetype can
    // read (TrueType outlines or CFF, the first font of a collection).
    public static TrueTypeFont Load(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12) throw new InvalidDataException($"{name}: not a font (too short)");
        StbTrueType.stbtt_fontinfo? info;
        try { info = StbTrueType.CreateFont(data, 0); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException($"{name}: not a font this engine can read ({ex.Message})");
        }
        if (info == null || info.numGlyphs <= 0) throw new InvalidDataException($"{name}: not a TrueType or OpenType font");
        return new TrueTypeFont(info, name);
    }

    public string Name { get; }

    public int GlyphCount => _info.numGlyphs;

    // Above the baseline, below it (positive), and the line gap, at `size`.
    public float Ascent(float size) => _ascent * _perUnit * size;
    public float Descent(float size) => -_descent * _perUnit * size;

    // From one baseline to the next: ascent, descent and the font's line gap.
    public float LineHeight(float size) => (_ascent - _descent + _gap) * _perUnit * size;

    public bool HasGlyph(int codepoint) => Glyph(codepoint).Glyph != 0;

    // How far the pen moves past `codepoint` at `size`. A character the font lacks advances by its
    // missing-glyph box (glyph 0), which is also what is drawn.
    public float Advance(int codepoint, float size) => Glyph(codepoint).Advance * _perUnit * size;

    // The kerning between two characters (usually negative), from the font's kern table or GPOS.
    public float Kerning(int left, int right, float size)
    {
        int a = Glyph(left).Glyph, b = Glyph(right).Glyph;
        if (a == 0 || b == 0 || _info.kern == 0 && _info.gpos == 0) return 0f;
        return StbTrueType.stbtt_GetGlyphKernAdvance(_info, a, b) * _perUnit * size;
    }

    private (int Glyph, int Advance) Glyph(int codepoint)
    {
        if (_glyphs.TryGetValue(codepoint, out var glyph)) return glyph;
        ObjectDisposedException.ThrowIf(_disposed, this);
        int index = StbTrueType.stbtt_FindGlyphIndex(_info, codepoint);
        int advance, bearing;
        StbTrueType.stbtt_GetGlyphHMetrics(_info, index, &advance, &bearing);
        glyph = (index, advance);
        _glyphs[codepoint] = glyph;
        return glyph;
    }

    // The pixel box a glyph covers at `pixelSize`, relative to the pen on the baseline (y down).
    internal void GlyphBox(int codepoint, float pixelSize, out int x0, out int y0, out int x1, out int y1)
    {
        int glyph = Glyph(codepoint).Glyph;
        float scale = _perUnit * pixelSize;
        int a, b, c, d;
        if (StbTrueType.stbtt_IsGlyphEmpty(_info, glyph) != 0) { x0 = y0 = x1 = y1 = 0; return; }
        StbTrueType.stbtt_GetGlyphBitmapBox(_info, glyph, scale, scale, &a, &b, &c, &d);
        x0 = a; y0 = b; x1 = c; y1 = d;
    }

    // Draws a glyph's coverage (0..255) into `target` at `offset`, rows `stride` bytes apart, `width` by
    // `height` (what GlyphBox said).
    internal void Rasterise(int codepoint, float pixelSize, byte[] target, int offset, int width, int height, int stride)
    {
        if (width <= 0 || height <= 0) return;
        if (offset < 0 || offset + (height - 1) * stride + width > target.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        int glyph = Glyph(codepoint).Glyph;
        float scale = _perUnit * pixelSize;
        fixed (byte* pixels = target)
            StbTrueType.stbtt_MakeGlyphBitmap(_info, pixels + offset, width, height, stride, scale, scale, glyph);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _info.Dispose();
    }

    public override string ToString() => Name;
}

// A font and the fonts behind it (issue #345): for each character, the first of them that has it — so
// a label in a Latin font shows the Japanese or Arabic a translation puts in it from the language's own
// fonts. Line height and baseline are the first font's. An Arabic presentation form (Scripts.Shape) that
// no font has is drawn as its letter, unjoined, from whichever font has that.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class FontChain
{
    private readonly TrueTypeFont[] _fonts;
    private readonly Dictionary<int, (TrueTypeFont Font, int Codepoint)> _resolved = new();

    public FontChain(TrueTypeFont primary, IEnumerable<TrueTypeFont>? fallbacks = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        var fonts = new List<TrueTypeFont> { primary };
        if (fallbacks != null)
            foreach (var font in fallbacks)
                if (font != null && !fonts.Contains(font)) fonts.Add(font);
        _fonts = fonts.ToArray();
    }

    public TrueTypeFont Primary => _fonts[0];

    // The primary font, then each fallback in order.
    public IReadOnlyList<TrueTypeFont> Fonts => _fonts;

    // Whether any font in the chain draws `codepoint` (or, for a presentation form, its letter).
    public bool Covers(int codepoint)
    {
        var (font, drawn) = Resolve(codepoint);
        return font.HasGlyph(drawn);
    }

    // The font that draws `codepoint`, and the character it draws: the first that has it; else, for an
    // Arabic form, the first with its letter; else the primary font's missing-glyph box.
    public (TrueTypeFont Font, int Codepoint) Resolve(int codepoint)
    {
        if (_resolved.TryGetValue(codepoint, out var found)) return found;
        found = (_fonts[0], codepoint);
        bool have = false;
        foreach (var font in _fonts)
            if (font.HasGlyph(codepoint)) { found = (font, codepoint); have = true; break; }
        if (!have && Scripts.BaseOf(codepoint) is var letter and not '\0')
            foreach (var font in _fonts)
                if (font.HasGlyph(letter)) { found = (font, letter); break; }
        _resolved[codepoint] = found;
        return found;
    }

    public override string ToString() => string.Join(" > ", (IEnumerable<TrueTypeFont>)_fonts);
}

// A TrueType font at one size, for layout: what a label whose style names the font measures with. With
// a chain (#345), each character is measured in the font that draws it.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class FontTextMeasure : ITextMeasure
{
    public FontTextMeasure(TrueTypeFont font, float size) : this(new FontChain(font ?? throw new ArgumentNullException(nameof(font))), size) { }

    public FontTextMeasure(FontChain chain, float size)
    {
        Chain = chain ?? throw new ArgumentNullException(nameof(chain));
        Size = size;
        LineHeight = chain.Primary.LineHeight(size);
    }

    public TrueTypeFont Font => Chain.Primary;

    public FontChain Chain { get; }

    // The em, in virtual units.
    public float Size { get; }

    public float LineHeight { get; }

    public System.Numerics.Vector2 Measure(string text, float scale)
    {
        float longest = 0f;
        int lines = 1, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            longest = MathF.Max(longest, Width(text.AsSpan(start, i - start), scale));
            if (i < text.Length) lines++;
            start = i + 1;
        }
        return new System.Numerics.Vector2(longest, lines * LineHeight * scale);
    }

    // One line's width: advances, and the kerning between each pair in the same font. A '\n' in it is skipped.
    public float Width(ReadOnlySpan<char> text, float scale)
    {
        float width = 0f;
        int previous = -1;
        TrueTypeFont? previousFont = null;
        float size = Size * scale;
        while (!text.IsEmpty)
        {
            Rune.DecodeFromUtf16(text, out var rune, out int used);
            text = text[used..];
            if (rune.Value == '\n') { previous = -1; continue; }
            var (font, codepoint) = Chain.Resolve(rune.Value);
            if (previous >= 0 && ReferenceEquals(font, previousFont)) width += font.Kerning(previous, codepoint, size);
            width += font.Advance(codepoint, size);
            previous = codepoint;
            previousFont = font;
        }
        return width;
    }
}

// Every font text is drawn with, by asset path (issue #338): what a ui_style's `font` names. A `.ttf` or
// `.otf` is read through the VFS the first time it is asked for and kept until content reloads; a
// `.png` is a grid atlas like the engine's own (the client's BitmapFont), which measures as the engine
// font does. Anything else, or a file that is missing or unreadable, is said once and measures as the
// engine font — a font is never a reason for text to vanish.
//
// Sizes: a font's `size` is its em in virtual units at textScale 1. DefaultSize, 9, is the engine
// font's line, so a style that names a TTF and keeps its textScale keeps about the size it had.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class UiFonts
{
    public const float DefaultSize = 9f;

    // The engine's own letters as a TrueType font (engine_content/fonts/sage.ttf): the last font a label
    // with no font of its own falls back to while a language names fonts (#345).
    public static readonly AssetPath EngineFont = AssetPath.Intern("fonts/sage.ttf");

    private readonly Func<AssetPath, byte[]?> _read;
    private readonly Dictionary<AssetPath, TrueTypeFont?> _fonts = new();
    private readonly Dictionary<(AssetPath, float), FontTextMeasure> _measures = new();
    private readonly Dictionary<AssetPath, FontChain?> _chains = new();
    private readonly HashSet<AssetPath> _warned = new();
    private AssetPath[] _fallbacks = Array.Empty<AssetPath>();

    // Fonts read from `vfs` (what UiModule makes).
    public UiFonts(VirtualFileSystem vfs) : this(path => ReadFile(vfs, path)) { }

    // Fonts read by `read` (null: there is no such file). Tests and tools.
    public UiFonts(Func<AssetPath, byte[]?> read) => _read = read ?? throw new ArgumentNullException(nameof(read));

    // Moves when fonts are dropped (content reloaded): whoever keeps glyph atlases starts again.
    public int Version { get; private set; }

    // Whether `path` names a TrueType or OpenType file (by its extension).
    public static bool IsTrueType(AssetPath path)
    {
        if (path.IsEmpty) return false;
        string text = path.Path.Value;   // not Extension, which makes a string: this is asked every layout and frame
        return text.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || text.EndsWith(".otf", StringComparison.OrdinalIgnoreCase);
    }

    // Whether `path` names a grid atlas (an image the client wraps as a BitmapFont).
    public static bool IsAtlas(AssetPath path) =>
        !path.IsEmpty && path.Path.Value.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

    // The font at `path`, read now if it has not been; null when it is not a TTF/OTF or cannot be read.
    public TrueTypeFont? Get(AssetPath path)
    {
        if (!IsTrueType(path)) return null;
        if (_fonts.TryGetValue(path, out var font)) return font;
        try
        {
            var data = _read(path);
            if (data == null) Warn(path, "there is no such file");
            else font = TrueTypeFont.Load(data, path.ToString());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Warn(path, ex.Message);
        }
        if (font != null) Log.Debug(LogCat.UI, $"Loaded font {path} ({font.GlyphCount} glyphs)");
        _fonts[path] = font;
        return font;
    }

    // The fonts tried after a label's own for a character it lacks, and what a label with no TrueType
    // font of its own is drawn in (#345): the shown language's `language` record's fonts. Setting
    // different ones drops every chain and measure, and moves Version.
    public IReadOnlyList<AssetPath> Fallbacks => _fallbacks;

    public void SetFallbacks(IEnumerable<AssetPath> fonts)
    {
        var next = (fonts ?? Array.Empty<AssetPath>()).Where(f => !f.IsEmpty).ToArray();
        if (next.AsSpan().SequenceEqual(_fallbacks)) return;
        _fallbacks = next;
        _chains.Clear();
        _measures.Clear();
        Version++;
    }

    // What text in `font` is drawn with: the font and the fallbacks behind it, or, when `font` is not a
    // TrueType font that can be read, the fallbacks and then the engine's TTF. Null when there is no
    // TrueType font in it at all — the engine's cells or a grid atlas draw it.
    public FontChain? Chain(AssetPath font)
    {
        if (_chains.TryGetValue(font, out var chain)) return chain;
        var own = IsTrueType(font) ? Get(font) : null;
        var fonts = new List<TrueTypeFont>();
        if (own != null) fonts.Add(own);
        foreach (var path in _fallbacks)
            if (Get(path) is { } fallback) fonts.Add(fallback);
        if (own == null && fonts.Count > 0 && Quiet(EngineFont) is { } engine) fonts.Add(engine);
        chain = fonts.Count == 0 ? null : new FontChain(fonts[0], fonts.Skip(1));
        _chains[font] = chain;
        return chain;
    }

    // How text in `font` at `size` (0: DefaultSize) measures; `engine` (the engine font's measure, at
    // its own size) when the font is not a TTF it can read and the language names no fonts. Allocates
    // only the first time a font and size are asked for.
    public ITextMeasure Measure(AssetPath font, float size, ITextMeasure engine)
    {
        if (size <= 0f) size = DefaultSize;
        if ((IsTrueType(font) || _fallbacks.Length > 0) && Chain(font) is { } chain)
        {
            if (!_measures.TryGetValue((font, size), out var measure)) _measures[(font, size)] = measure = new FontTextMeasure(chain, size);
            return measure;
        }
        if (!font.IsEmpty && !IsTrueType(font) && !IsAtlas(font)) Warn(font, "a font is a .ttf, .otf or a .png grid atlas");
        if (size == DefaultSize) return engine;
        // The engine font (or a grid atlas, which measures the same) at another size: scaled whole.
        if (!_scaled.TryGetValue(size, out var scaled) || !ReferenceEquals(scaled.Inner, engine))
            _scaled[size] = scaled = new ScaledTextMeasure(engine, size / DefaultSize);
        return scaled;
    }

    private readonly Dictionary<float, ScaledTextMeasure> _scaled = new();

    // Forgets every font, so the next ask reads the file again (content reloaded).
    public void Clear()
    {
        foreach (var font in _fonts.Values) font?.Dispose();
        _fonts.Clear();
        _measures.Clear();
        _chains.Clear();
        _scaled.Clear();
        _warned.Clear();
        Version++;
    }

    // A font read without a word when it is not there: the engine's own, which a game need not ship.
    internal TrueTypeFont? Quiet(AssetPath path)
    {
        if (_fonts.TryGetValue(path, out var font)) return font;
        try
        {
            if (_read(path) is { } data) font = TrueTypeFont.Load(data, path.ToString());
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { }
        _fonts[path] = font;
        return font;
    }

    private void Warn(AssetPath path, string why)
    {
        if (_warned.Add(path)) Log.Warn(LogCat.UI, $"font '{path}': {why}; text in it is drawn in the engine font");
    }

    private static byte[]? ReadFile(VirtualFileSystem vfs, AssetPath path)
    {
        if (!vfs.Exists(path.Path)) return null;
        using var stream = vfs.Open(path.Path);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
