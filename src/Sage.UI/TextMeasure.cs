#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// What layout asks of a font, and all it asks: how big a string is. Layout never draws, so it needs no
// font, texture or window — a test measures with MonospaceTextMeasure, and the client implements this
// over its BitmapFont (#97). Sizes are in virtual units, the units the layout is in (UiRoot).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public interface ITextMeasure
{
    // The size `text` takes at `scale` (1 = the font's own size). Lines are separated by '\n'.
    Vector2 Measure(string text, float scale);

    // The height of one line at scale 1: what an empty label measures.
    float LineHeight { get; }

    // The width of one line of `text` at `scale` (#338): what word wrap and ellipsis measure with, on a
    // span so that breaking a paragraph into lines allocates nothing. Implementations override it;
    // this fallback makes a string.
    float Width(ReadOnlySpan<char> text, float scale) => Measure(text.ToString(), scale).X;
}

// Another measure, every size multiplied by Factor: the engine font at a ui_style's fontSize (#338).
internal sealed class ScaledTextMeasure : ITextMeasure
{
    public ScaledTextMeasure(ITextMeasure inner, float factor)
    {
        Inner = inner;
        Factor = factor;
    }

    public ITextMeasure Inner { get; }
    public float Factor { get; }
    public float LineHeight => Inner.LineHeight * Factor;
    public Vector2 Measure(string text, float scale) => Inner.Measure(text, scale * Factor);
    public float Width(ReadOnlySpan<char> text, float scale) => Inner.Width(text, scale * Factor);
}

// Every character the same width: enough for tests and tools, and exactly right for a fixed-width
// bitmap font.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public sealed class MonospaceTextMeasure : ITextMeasure
{
    public MonospaceTextMeasure(float characterWidth, float lineHeight)
    {
        CharacterWidth = characterWidth;
        LineHeight = lineHeight;
    }

    public float CharacterWidth { get; }
    public float LineHeight { get; }

    public Vector2 Measure(string text, float scale)
    {
        int lines = 1, longest = 0, current = 0;
        foreach (char c in text)
        {
            if (c == '\n') { lines++; current = 0; continue; }
            current++;
            longest = Math.Max(longest, current);
        }
        return new Vector2(longest * CharacterWidth, lines * LineHeight) * scale;
    }

    public float Width(ReadOnlySpan<char> text, float scale)
    {
        int count = 0;
        foreach (char c in text) if (c != '\n') count++;
        return count * CharacterWidth * scale;
    }
}
