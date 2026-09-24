#nullable enable
using System;
using System.Numerics;

namespace sage_engine;

// Where a screen's parts are, in pixels (docs/design/13 §3, TODO F38).
//
// One piece of arithmetic, used by **both** the drawing and the mouse. That is the whole reason it
// exists as a type rather than living inside the draw call: a hit test that computes row positions
// separately from the code that drew them is the same mistake as a rule applied in one place and
// asked in another (R17), and it fails the same way — a row that highlights one line above the one
// that activates, on some screen sizes only.
//
// Plain maths over numbers a caller supplies, so it is in the engine and a headless test can ask
// "which row is under this point?" without a window. The client passes the viewport and the font's
// line height; nothing here knows what a font is.
public readonly struct PanelLayout
{
    public const float Padding = 14f;
    public const int MaxRows = 12;          // rows visible at once before the list scrolls

    public PanelLayout(Vector2 viewport, float lineHeight, float width, int rowCount, int selected, bool hasField)
    {
        LineHeight = MathF.Max(lineHeight, 4f);
        RowCount = Math.Max(rowCount, 0);
        Visible = Math.Min(MaxRows, Math.Max(RowCount, 1));

        // The window follows the selection, so a long spellbook scrolls instead of overflowing.
        First = Math.Clamp(selected - Visible / 2, 0, Math.Max(RowCount - Visible, 0));

        float height = Padding * 2f + LineHeight * (Visible + 3f) + (hasField ? LineHeight * 1.4f : 0f);
        float x = MathF.Round((viewport.X - width) * 0.5f);
        float y = MathF.Round((viewport.Y - height) * 0.5f);

        Box = new Rect(x, y, width, height);
        TitleY = y + Padding;
        FieldY = hasField ? TitleY + LineHeight * 1.5f : 0f;
        RowsY = TitleY + LineHeight * 1.5f + (hasField ? LineHeight * 1.4f : 0f);
        HintY = y + height - Padding - LineHeight;
        ReasonY = HintY - LineHeight;
    }

    public Rect Box { get; }
    public float LineHeight { get; }
    public float TitleY { get; }
    public float FieldY { get; }        // 0 when the screen has no field
    public float RowsY { get; }
    public float ReasonY { get; }
    public float HintY { get; }
    public int First { get; }           // the first row drawn
    public int Visible { get; }         // how many are drawn
    public int RowCount { get; }

    public float TextX => Box.X + Padding;

    public float Right => Box.X + Box.Width - Padding;

    public float RowY(int index) => RowsY + (index - First) * LineHeight;

    public Rect RowRect(int index) => new(Box.X + 4f, RowY(index) - 2f, Box.Width - 8f, LineHeight + 2f);

    public bool IsDrawn(int index) => index >= First && index < Math.Min(First + Visible, RowCount);

    // The row under a point, or -1. The mouse asks this; the drawing uses the same `RowRect`, so they
    // cannot disagree about where a row is.
    public int RowAt(Vector2 point)
    {
        if (!Box.Contains(point)) return -1;
        for (int i = First; i < Math.Min(First + Visible, RowCount); i++)
            if (RowRect(i).Contains(point)) return i;
        return -1;
    }

    public bool Contains(Vector2 point) => Box.Contains(point);
}

// A rectangle in screen pixels. `System.Drawing` is not referenced and MonoGame's lives in the
// client, so this is the engine's own — four floats and one question.
public readonly record struct Rect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public bool Contains(Vector2 point) =>
        point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;
}
