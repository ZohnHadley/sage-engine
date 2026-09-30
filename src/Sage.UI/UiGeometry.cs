#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.UI;

// The experimental id and link every public type of this assembly carries (docs/MAKING_A_GAME.md §10b).
internal static class UiApi
{
    internal const string Experimental = "SAGE0125";
    internal const string Url = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api";
}

// Space around a widget (Margin, outside its rect) or inside it (Padding, between its rect and its
// content), in virtual units.
// In a record: 4, [h, v] or [left, top, right, bottom] (ThicknessJsonConverter, issue #96).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
[System.Text.Json.Serialization.JsonConverter(typeof(ThicknessJsonConverter))]
public readonly record struct Thickness(float Left, float Top, float Right, float Bottom)
{
    public Thickness(float all) : this(all, all, all, all) { }
    public Thickness(float horizontal, float vertical) : this(horizontal, vertical, horizontal, vertical) { }

    public static Thickness Zero => default;

    // Left + Right and Top + Bottom.
    public Vector2 Size => new(Left + Right, Top + Bottom);

    // `rect` with this taken off each side; never negative.
    public Rect Deflate(Rect rect) =>
        new(rect.X + Left, rect.Y + Top, MathF.Max(rect.Width - Left - Right, 0f), MathF.Max(rect.Height - Top - Bottom, 0f));
}

// Where a child of a Box sits (Godot's anchors): MinX/MaxX are where its left and right edges are
// anchored, MinY/MaxY its top and bottom, each a fraction of the Box's content rect (0 = its left/top
// edge, 1 = its right/bottom edge). When MinX == MaxX the child keeps its measured width and that
// fraction of it sits on the anchor line (0 grows right, 0.5 centres, 1 grows left); when they differ it
// stretches between the two lines. The same for Y. Margins push in from the anchor lines.
// In a record: a preset's name ("bottom_right") or [minX, minY, maxX, maxY] (AnchorsJsonConverter).
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
[System.Text.Json.Serialization.JsonConverter(typeof(AnchorsJsonConverter))]
public readonly record struct Anchors(float MinX, float MinY, float MaxX, float MaxY)
{
    public static Anchors TopLeft => new(0f, 0f, 0f, 0f);
    public static Anchors Top => new(0.5f, 0f, 0.5f, 0f);
    public static Anchors TopRight => new(1f, 0f, 1f, 0f);
    public static Anchors Left => new(0f, 0.5f, 0f, 0.5f);
    public static Anchors Center => new(0.5f, 0.5f, 0.5f, 0.5f);
    public static Anchors Right => new(1f, 0.5f, 1f, 0.5f);
    public static Anchors BottomLeft => new(0f, 1f, 0f, 1f);
    public static Anchors Bottom => new(0.5f, 1f, 0.5f, 1f);
    public static Anchors BottomRight => new(1f, 1f, 1f, 1f);
    public static Anchors Fill => new(0f, 0f, 1f, 1f);
    public static Anchors TopWide => new(0f, 0f, 1f, 0f);
    public static Anchors BottomWide => new(0f, 1f, 1f, 1f);
}

// How a child sits in the slot its container gives it, on one axis. Fill takes the whole slot; the others
// keep the child's measured size.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum Align { Fill, Start, Center, End }

// A Stack's (and a Bar's) main axis: Row is left to right, Column top to bottom.
[Experimental(UiApi.Experimental, UrlFormat = UiApi.Url)]
public enum Orientation { Row, Column }

internal static class RectMath
{
    public static Rect Intersect(Rect a, Rect b)
    {
        float x = MathF.Max(a.X, b.X), y = MathF.Max(a.Y, b.Y);
        float right = MathF.Min(a.Right, b.Right), bottom = MathF.Min(a.Bottom, b.Bottom);
        return new Rect(x, y, MathF.Max(right - x, 0f), MathF.Max(bottom - y, 0f));
    }

    public static Vector2 Center(Rect r) => new(r.X + r.Width * 0.5f, r.Y + r.Height * 0.5f);

    // Where a length `size` sits in [start, start + length) for an alignment (Fill takes it all).
    public static (float Position, float Length) Place(Align align, float start, float length, float size)
    {
        if (align == Align.Fill) return (start, length);
        size = MathF.Min(size, length);
        return align switch
        {
            Align.Center => (start + (length - size) * 0.5f, size),
            Align.End => (start + length - size, size),
            _ => (start, size),
        };
    }
}
