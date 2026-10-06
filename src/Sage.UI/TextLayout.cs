#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// What a label does with text that does not fit its width (issue #338). Visible: nothing, it runs past
// the edge (what a label did before). Clip: it is cut at the content rect. Ellipsis: each line that does
// not fit ends in "…" where it would cross the edge, and lines past the bottom are dropped, the last
// kept one ending in "…".
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum TextOverflow { Visible, Clip, Ellipsis }

// One line of a label's text: a slice of the string (so nothing is copied), its width at the label's
// scale, and whether "…" follows it.
internal readonly record struct TextLine(int Start, int Length, float Width, bool Ellipsis);

// Breaks text into lines (issue #338): at each '\n', and with `wrap` at the last space that keeps a line
// within `maxWidth`, or in Chinese, Japanese and Korean between two characters (#345,
// Scripts.CanBreakBetween); a word longer than the whole width is broken between characters. Then, for
// Ellipsis, shortens what still does not fit. Widths are ITextMeasure's at `scale`, so a TTF's advances
// and kerning, or the engine font's cells. Reuses `lines`; allocates nothing once it has grown.
internal static class TextLayout
{
    public const string Ellipsis = "…";

    // Fills `lines` and returns the size they take: the widest line, and a line height per line.
    // `maxWidth` and `maxHeight` may be infinite (no limit).
    public static Vector2 Break(string text, ITextMeasure measure, float scale, float maxWidth, float maxHeight,
                                bool wrap, TextOverflow overflow, List<TextLine> lines)
    {
        lines.Clear();
        float lineHeight = measure.LineHeight * scale;
        if (!(maxWidth >= 0f)) maxWidth = 0f;

        int start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n') continue;
            Paragraph(text, start, i - start, measure, scale, maxWidth, wrap, lines);
            start = i + 1;
        }

        if (overflow == TextOverflow.Ellipsis)
        {
            // Lines past the bottom go; the last that stays says there was more.
            if (!float.IsPositiveInfinity(maxHeight) && lines.Count * lineHeight > maxHeight + 0.001f)
            {
                int keep = Math.Max(1, (int)MathF.Floor((maxHeight + 0.001f) / lineHeight));
                if (keep < lines.Count)
                {
                    lines.RemoveRange(keep, lines.Count - keep);
                    var last = lines[keep - 1];
                    lines[keep - 1] = Shorten(text, last, measure, scale, maxWidth, force: true);
                }
            }
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Width > maxWidth + 0.001f && !lines[i].Ellipsis) lines[i] = Shorten(text, lines[i], measure, scale, maxWidth, force: false);
        }

        float widest = 0f;
        foreach (var line in lines) widest = MathF.Max(widest, line.Width);
        return new Vector2(widest, Math.Max(lines.Count, 1) * lineHeight);
    }

    private static void Paragraph(string text, int start, int length, ITextMeasure measure, float scale, float maxWidth, bool wrap, List<TextLine> lines)
    {
        int end = start + length;
        float whole = measure.Width(text.AsSpan(start, length), scale);
        if (!wrap || whole <= maxWidth + 0.001f || length == 0)
        {
            lines.Add(new TextLine(start, length, whole, false));
            return;
        }

        int before = lines.Count;
        int lineStart = start;
        while (lineStart < end)
        {
            // Spaces that a wrap lands on start no line.
            while (lineStart < end && text[lineStart] == ' ') lineStart++;
            if (lineStart >= end) break;

            int fit = lineStart, lastBreak = -1;
            float fitWidth = 0f;
            for (int i = lineStart; i < end; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < end) continue;   // a pair is measured whole
                float width = measure.Width(text.AsSpan(lineStart, i + 1 - lineStart), scale);
                if (width > maxWidth + 0.001f) break;
                fit = i + 1;
                fitWidth = width;
                if (i + 1 < end && (text[i + 1] == ' ' || text[i] != ' ' && Scripts.CanBreakBetween(text[i], text[i + 1]))) lastBreak = i + 1;
            }

            int lineEnd;
            if (fit >= end) lineEnd = end;                      // the rest fits
            else if (lastBreak > lineStart && text[fit] != ' ') lineEnd = lastBreak;   // back to the last space
            else if (fit > lineStart) lineEnd = fit;            // at a space, or one long word: cut here
            else lineEnd = lineStart + (char.IsHighSurrogate(text[lineStart]) && lineStart + 1 < end ? 2 : 1);   // not even a character fits

            int trimmed = lineEnd;
            while (trimmed > lineStart && text[trimmed - 1] == ' ') trimmed--;
            float lineWidth = trimmed == fit ? fitWidth : measure.Width(text.AsSpan(lineStart, trimmed - lineStart), scale);
            lines.Add(new TextLine(lineStart, trimmed - lineStart, lineWidth, false));
            lineStart = lineEnd;
        }
        if (lines.Count == before) lines.Add(new TextLine(start, 0, 0f, false));   // only spaces: still a line
    }

    // The longest beginning of `line` that leaves room for "…" within maxWidth (none of it, if nothing
    // does), with the ellipsis. `force` adds it even when the line fits (text was dropped after it).
    private static TextLine Shorten(string text, TextLine line, ITextMeasure measure, float scale, float maxWidth, bool force)
    {
        float dots = measure.Width(Ellipsis, scale);
        if (force && line.Width + dots <= maxWidth + 0.001f) return line with { Width = line.Width + dots, Ellipsis = true };
        int length = line.Length;
        float width = line.Width;
        while (length > 0)
        {
            length--;
            if (length > 0 && char.IsLowSurrogate(text[line.Start + length])) length--;
            while (length > 0 && text[line.Start + length - 1] == ' ') length--;   // no space before the dots
            width = measure.Width(text.AsSpan(line.Start, length), scale);
            if (width + dots <= maxWidth + 0.001f) break;
        }
        if (length == 0) width = 0f;
        return new TextLine(line.Start, length, width + dots, true);
    }
}
