#nullable enable
using System;
using System.Numerics;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Where a screen's parts are (docs/design/13 §3, TODO F38).
//
// This is the arithmetic the **mouse and the drawing share**, which is the whole reason it is a type
// in the engine rather than a few lines inside the draw call. A hit test that worked out row
// positions separately from the code that drew them is the same mistake as a rule applied in one
// place and asked in another (R17), and it fails the same way: the row that lights up is not the row
// that acts, on some window sizes only.
//
// So these tests are about agreement. Every one of them asks "what did you draw there?" and "what is
// under this point?" and insists the answers match.
public class PanelLayoutTests
{
    private static readonly Vector2 Window = new(800, 400);
    private const float Line = 16f;

    private static PanelLayout Layout(int rows, int selected = 0, bool field = false, float width = 460f) =>
        new(Window, Line, width, rows, selected, field);

    // The box is centred, which is what a player expects of a dialogue and what the mouse maths has to
    // agree with.
    [Xunit.Fact]
    public void TheBoxIsCentred()
    {
        var layout = Layout(rows: 4, width: 400f);

        Assert.Equal(200f, layout.Box.X);                       // (800 - 400) / 2
        Assert.Equal(400f, layout.Box.Width);
        Assert.True(layout.Box.Y > 0f && layout.Box.Bottom < Window.Y, "it fits on the screen");
        Assert.Equal(Window.X * 0.5f, layout.Box.X + layout.Box.Width * 0.5f);
    }

    // The point of the type: a click in the middle of a drawn row finds that row, for every row drawn.
    [Xunit.Fact]
    public void EveryDrawnRowIsUnderItsOwnRectangle()
    {
        var layout = Layout(rows: 6, selected: 0);

        for (int i = 0; i < 6; i++)
        {
            var rect = layout.RowRect(i);
            var middle = new Vector2(rect.X + rect.Width * 0.5f, rect.Y + rect.Height * 0.5f);
            Assert.Equal(i, layout.RowAt(middle));
        }
    }

    [Xunit.Fact]
    public void OutsideTheRowsAndOutsideTheBoxFindNothing()
    {
        var layout = Layout(rows: 3);

        Assert.Equal(-1, layout.RowAt(new Vector2(layout.TextX, layout.TitleY)));       // the title
        Assert.Equal(-1, layout.RowAt(new Vector2(layout.TextX, layout.HintY)));        // the hint
        Assert.Equal(-1, layout.RowAt(new Vector2(5f, 5f)));                            // the world
        Assert.False(layout.Contains(new Vector2(5f, 5f)));
        Assert.True(layout.Contains(new Vector2(layout.Box.X + 2f, layout.Box.Y + 2f)));
    }

    // A long list draws a window of rows around the selection, and *only* those rows can be clicked —
    // a point over a row that is scrolled out of sight must not find it.
    [Xunit.Fact]
    public void ALongListDrawsAWindowAroundTheSelection()
    {
        var layout = Layout(rows: 40, selected: 20);

        Assert.Equal(PanelLayout.MaxRows, layout.Visible);
        Assert.True(layout.First > 0 && layout.First <= 20);
        Assert.True(layout.IsDrawn(20), "the selected row is always drawn");
        Assert.False(layout.IsDrawn(0));
        Assert.False(layout.IsDrawn(39));

        // The rows that are not drawn are not clickable either, wherever their rectangles would be.
        var offscreen = layout.RowRect(0);
        Assert.NotEqual(0, layout.RowAt(new Vector2(offscreen.X + 4f, offscreen.Y + 4f)));
    }

    // The window stops at both ends rather than scrolling past them.
    [Xunit.Theory]
    [Xunit.InlineData(0)]
    [Xunit.InlineData(5)]
    [Xunit.InlineData(39)]
    public void TheWindowStaysInsideTheList(int selected)
    {
        var layout = Layout(rows: 40, selected: selected);

        Assert.True(layout.First >= 0);
        Assert.True(layout.First + layout.Visible <= 40);
        Assert.True(layout.IsDrawn(selected), $"row {selected} should be visible when it is selected");
    }

    // A screen with a field is taller by exactly the field, and its rows start below it — otherwise
    // the mouse would find rows one line above where they are drawn.
    [Xunit.Fact]
    public void AFieldPushesTheRowsDownAndTheBoxOut()
    {
        var plain = Layout(rows: 5);
        var withField = Layout(rows: 5, field: true);

        Assert.True(withField.Box.Height > plain.Box.Height);
        Assert.True(withField.FieldY > withField.TitleY);
        Assert.True(withField.RowsY > withField.FieldY);
        Assert.Equal(0f, plain.FieldY);

        var first = withField.RowRect(0);
        Assert.True(first.Y > withField.FieldY, "the first row sits under the field");
        Assert.Equal(0, withField.RowAt(new Vector2(first.X + 4f, first.Y + first.Height * 0.5f)));
    }

    // An empty list still has a box to click on, and nothing in it to click.
    [Xunit.Fact]
    public void AnEmptyListIsStillABox()
    {
        var layout = Layout(rows: 0);

        Assert.True(layout.Box.Width > 0f && layout.Box.Height > 0f);
        Assert.Equal(-1, layout.RowAt(new Vector2(layout.Box.X + 10f, layout.RowsY + 2f)));
        Assert.True(layout.Contains(new Vector2(layout.Box.X + 10f, layout.RowsY + 2f)));
    }

    // The reason line and the hint are inside the box, under the rows: they moved when the field was
    // added, and a screenshot showed the hint spilling out of the box when the width was too small.
    [Xunit.Fact]
    public void TheHintAndTheReasonAreInsideTheBox()
    {
        foreach (var layout in new[] { Layout(rows: 3), Layout(rows: 20, selected: 10, field: true) })
        {
            Assert.True(layout.HintY > layout.RowsY);
            Assert.True(layout.HintY + layout.LineHeight <= layout.Box.Bottom);
            Assert.True(layout.ReasonY < layout.HintY);
            Assert.True(layout.ReasonY > layout.RowY(layout.First + layout.Visible - 1));
        }
    }

    // A conversation's title is the sentence somebody just said, so it may take several lines (F24). The
    // panel grows by exactly what the title needs and the rows move down with it — a title that wrapped
    // into the list would be worse than one that ran off the edge.
    [Fact]
    public void ATitleThatWrapsMakesThePanelTaller()
    {
        var viewport = new Vector2(1280, 720);
        var one = new PanelLayout(viewport, 20f, 400f, rowCount: 3, selected: 0, hasField: false);
        var three = new PanelLayout(viewport, 20f, 400f, rowCount: 3, selected: 0, hasField: false, titleLines: 3);

        Assert.Equal(1, one.TitleLines);
        Assert.Equal(3, three.TitleLines);
        Assert.Equal(one.Box.Height + 40f, three.Box.Height, 3);        // two more lines of 20
        Assert.Equal(one.RowsY - one.Box.Y + 40f, three.RowsY - three.Box.Y, 3);

        // And it stays centred, so a long line does not push the panel off the bottom of the window.
        Assert.Equal(viewport.Y / 2f, three.Box.Y + three.Box.Height / 2f, 1);
    }
}
