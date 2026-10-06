#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// A drawn frame's golden (issue #355, src/Sage.Simulation/Rendering/FrameGrid.cs): the frame as a grid of
// cell means, its JSON, and the comparison the host's `r_framecheck` runs on the real back buffer, on frames
// made here. tools/kit_screens_check.sh runs it on every RPG kit screen; this proves what it can see.
public class FrameGridTests
{
    // A frame of one colour with a box of another at (x, y), w by h pixels.
    private static PixelFrame Frame(int width, int height, Vector3 background, (int X, int Y, int W, int H, Vector3 Colour)? box = null)
    {
        var rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                var c = box is { } b && x >= b.X && x < b.X + b.W && y >= b.Y && y < b.Y + b.H ? b.Colour : background;
                int i = (y * width + x) * 4;
                rgba[i] = (byte)MathF.Round(c.X * 255f);
                rgba[i + 1] = (byte)MathF.Round(c.Y * 255f);
                rgba[i + 2] = (byte)MathF.Round(c.Z * 255f);
                rgba[i + 3] = 255;
            }
        return new PixelFrame(width, height, rgba);
    }

    private static readonly Vector3 Grey = new(0.2f, 0.2f, 0.2f), White = Vector3.One;

    [Fact]
    public void EachCellIsTheMeanOfThePixelsItCovers()
    {
        // 8x4 pixels in a 4x2 grid: 2x2-pixel cells. The box covers the top left cell and half the one beside it.
        var grid = FrameGrid.Of(Frame(8, 4, Grey, (0, 0, 3, 2, White)), columns: 4, rows: 2);
        Assert.Equal((8, 4, 4, 2), (grid.Width, grid.Height, grid.Columns, grid.Rows));
        Assert.Equal(1f, grid.Cell(0, 0).X, 2);
        Assert.Equal((0.2f + 1f) / 2f, grid.Cell(1, 0).X, 2);
        Assert.Equal(0.2f, grid.Cell(3, 1).Z, 2);

        // A frame that is not a whole number of cells: every pixel is in exactly one cell.
        var uneven = FrameGrid.Of(Frame(10, 5, Grey, (0, 0, 10, 1, White)), columns: 3, rows: 2);
        Assert.Equal((1f + 0.2f) / 2f, uneven.Cell(0, 0).X, 2);   // the top cells are pixel rows 0 and 1 of 5: one white, one grey
        Assert.Equal(0.2f, uneven.Cell(2, 1).X, 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => FrameGrid.Of(Frame(4, 4, Grey), columns: 5, rows: 1));
    }

    [Fact]
    public void AGoldenGoesToJsonAndBack()
    {
        var grid = FrameGrid.Of(Frame(96, 54, Grey, (10, 10, 30, 8, new Vector3(0.9f, 0.5f, 0.1f))), columns: 48, rows: 27, shaders: true)
            .With(0.05f, 2);
        string json = grid.ToJson("what it is of\nhow to write it again");
        Assert.StartsWith("{\n  // what it is of\n  // how to write it again\n", json);

        var back = FrameGrid.Parse(json);
        Assert.Equal((96, 54, 48, 27, true, 0.05f, 2), (back.Width, back.Height, back.Columns, back.Rows, back.Shaders, back.Tolerance, back.MaxCells));
        for (int row = 0; row < 27; row++)
            for (int column = 0; column < 48; column++)
                Assert.Equal(grid.Cell(column, row), back.Cell(column, row));
        Assert.Equal(json, back.ToJson("what it is of\nhow to write it again"));

        // A default golden is the kit check's: 48x27 cells, 0.03, no cell allowed to move.
        var plain = FrameGrid.Parse(FrameGrid.Of(Frame(96, 54, Grey)).ToJson());
        Assert.Equal((FrameGrid.DefaultColumns, FrameGrid.DefaultRows, 0.03f, 0, false),
            (plain.Columns, plain.Rows, plain.Tolerance, plain.MaxCells, plain.Shaders));
    }

    [Theory]
    [InlineData("[]", "an object")]
    [InlineData("""{ "width": 4, "height": 4, "columns": 2, "rows": 1, "cells": [ "000000 000000" ], "colour": 1 }""", "unknown key 'colour'")]
    [InlineData("""{ "width": 4, "height": 4, "columns": 2, "rows": 1 }""", "no cells")]
    [InlineData("""{ "height": 4, "columns": 2, "rows": 1, "cells": [ "000000 000000" ] }""", "width, height, columns and rows")]
    [InlineData("""{ "width": 4, "height": 4, "columns": 2, "rows": 2, "cells": [ "000000 000000" ] }""", "1 rows of cells, and 'rows' says 2")]
    [InlineData("""{ "width": 4, "height": 4, "columns": 2, "rows": 1, "cells": [ "000000" ] }""", "row 0 has 1 cells")]
    [InlineData("""{ "width": 4, "height": 4, "columns": 2, "rows": 1, "cells": [ "000000 00zz00" ] }""", "'00zz00' is not rrggbb")]
    [InlineData("""{ "width": 4, "height": 4, "columns": 2, "rows": 1, "tolerance": -1, "cells": [ "000000 000000" ] }""", "'tolerance'")]
    public void AGoldenWithAMistakeIsAnError(string json, string message)
    {
        var ex = Assert.Throws<FormatException>(() => FrameGrid.Parse(json));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void TheSameFramePassesAndAMissingLabelFails()
    {
        // A dark panel with a line of light text on it, at the kit check's size and grid.
        var panel = (X: 300, Y: 200, W: 360, H: 140, Colour: new Vector3(0.21f, 0.21f, 0.2f));
        var drawn = Frame(960, 540, Grey, panel);
        var golden = FrameGrid.Parse(FrameGrid.Of(drawn).ToJson());

        var same = golden.Compare(FrameGrid.Of(drawn));
        Assert.True(same.SizeMatches);
        Assert.True(same.Passed(golden));
        Assert.Empty(same.Differences);
        Assert.Equal(0f, same.Worst);
        Assert.Equal(48 * 27, same.Cells);

        // The label is a 6-pixel-high strip of text, a third ink: what a hint line of the engine font comes to.
        var rgba = (byte[])drawn.Rgba.Clone();
        for (int y = 300; y < 306; y++)
            for (int x = 320; x < 500; x += 3)
            {
                int i = (y * 960 + x) * 4;
                rgba[i] = rgba[i + 1] = rgba[i + 2] = 230;
            }
        var withLabel = FrameGrid.Of(new PixelFrame(960, 540, rgba));
        var labelGolden = FrameGrid.Parse(withLabel.ToJson());
        var missing = labelGolden.Compare(FrameGrid.Of(drawn));
        Assert.False(missing.Passed(labelGolden));
        Assert.All(missing.Differences, d => Assert.Equal(15, d.Row));                // 300..305 is row 15 of 20-pixel rows
        Assert.True(missing.Differences.Count >= 8, $"{missing.Differences.Count} cells");
        Assert.Contains("cell ", missing.Describe());
        Assert.Contains(", want (", missing.Describe());

        // Moved by more than the tolerance in fewer cells than allowed passes; another frame size never does.
        Assert.True(missing.Passed(labelGolden.With(0.03f, missing.Differences.Count)));
        var smaller = golden.Compare(FrameGrid.Of(Frame(640, 360, Grey)));
        Assert.False(smaller.SizeMatches);
        Assert.False(smaller.Passed(golden.With(1f, 10000)));
    }

    // The kit check's goldens (tests/games/kit-screens/goldens/frame) all read, and are for the frame the
    // script asks for: a broken golden would otherwise only show up in CI's smoke run.
    [Fact]
    public void TheKitScreenFrameGoldensRead()
    {
        string dir = Path.Combine(KitScreenGoldenTests.Game, "goldens", "frame");
        var files = Directory.GetFiles(dir, "*.json");
        var names = files.Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet(StringComparer.Ordinal);
        foreach (var line in KitScreenGoldenTests.Screens())
            Assert.True(names.Contains(line.File), $"{line.Screen} has no frame golden in {dir}: run tools/kit_screens_check.sh <host> --update");
        foreach (var file in files)
        {
            var golden = FrameGrid.Parse(File.ReadAllText(file));
            Assert.Equal((960, 540, false), (golden.Width, golden.Height, golden.Shaders));
        }
    }
}
