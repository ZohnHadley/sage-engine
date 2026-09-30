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
}
