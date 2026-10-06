#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace Sage.Simulation;

// A drawn frame's golden (issue #355): the frame cut into a coarse grid of cells and each cell's mean
// colour, written once to a small JSON file and compared with every later run's. The host's
// `r_framecheck <golden> [update]` reads the back buffer and writes the golden or checks against it;
// tools/kit_screens_check.sh opens each of the RPG kit's screens in the real client and runs it, so a
// screen that stops drawing its panel, its text or its focus highlight fails CI. Everything here is
// headless and unit-tested (FrameGridTests); only the readback is the host's.
//
// Cells, not pixels: a cell's mean moves when a label, a border or a box goes missing or moves (a line
// of the engine font covers a good part of a 20-pixel cell), and hardly at all when a driver rounds an
// edge another way, so the golden is small and the check is not a flake.
//
//   {
//     "width": 960, "height": 540,           // the frame it was taken from (the check fails on another size)
//     "columns": 48, "rows": 27,              // the grid: 20-pixel cells here
//     "shaders": false,                       // drawn with the engine's compiled effects or not
//     "tolerance": 0.03,                      // how far a cell's mean may move, on any channel (0..1)
//     "maxCells": 0,                          // how many cells may move further than that
//     "cells": [ "1d1f20 1d1f20 ...", ... ]   // a row a string, each cell `rrggbb`
//   }
internal sealed class FrameGrid
{
    public const int DefaultColumns = 48, DefaultRows = 27;
    public const float DefaultTolerance = 0.03f;

    public int Width { get; private init; }
    public int Height { get; private init; }
    public int Columns { get; private init; }
    public int Rows { get; private init; }
    public bool Shaders { get; init; }
    public float Tolerance { get; init; } = DefaultTolerance;
    public int MaxCells { get; init; }

    // RGB, a byte a channel, a row of cells at a time from the top left.
    private byte[] _cells = System.Array.Empty<byte>();

    public Vector3 Cell(int column, int row)
    {
        int i = (row * Columns + column) * 3;
        return new Vector3(_cells[i] / 255f, _cells[i + 1] / 255f, _cells[i + 2] / 255f);
    }

    // The frame's grid: each cell the mean of the pixels it covers (a cell edge between two pixels gives
    // the pixel to the cell its start is in, so every pixel counts once).
    public static FrameGrid Of(PixelFrame frame, int columns = DefaultColumns, int rows = DefaultRows, bool shaders = false)
    {
        if (columns <= 0 || rows <= 0 || columns > frame.Width || rows > frame.Height)
            throw new ArgumentOutOfRangeException(nameof(columns), $"a {columns}x{rows} grid does not fit a {frame.Width}x{frame.Height} frame");
        var cells = new byte[columns * rows * 3];
        for (int row = 0; row < rows; row++)
        {
            int y0 = row * frame.Height / rows, y1 = (row + 1) * frame.Height / rows;
            for (int column = 0; column < columns; column++)
            {
                int x0 = column * frame.Width / columns, x1 = (column + 1) * frame.Width / columns;
                long r = 0, g = 0, b = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int p = (y * frame.Width + x) * 4;
                        r += frame.Rgba[p];
                        g += frame.Rgba[p + 1];
                        b += frame.Rgba[p + 2];
                    }
                long n = (long)(x1 - x0) * (y1 - y0);
                int i = (row * columns + column) * 3;
                cells[i] = (byte)((r + n / 2) / n);
                cells[i + 1] = (byte)((g + n / 2) / n);
                cells[i + 2] = (byte)((b + n / 2) / n);
            }
        }
        return new FrameGrid { Width = frame.Width, Height = frame.Height, Columns = columns, Rows = rows, Shaders = shaders, _cells = cells };
    }

    // The same cells with another tolerance and allowance (an update keeps the old golden's).
    public FrameGrid With(float tolerance, int maxCells) => new()
    {
        Width = Width, Height = Height, Columns = Columns, Rows = Rows, Shaders = Shaders,
        Tolerance = tolerance, MaxCells = maxCells, _cells = _cells,
    };

    // The cells that moved further than the tolerance, worst first.
    public FrameGridComparison Compare(FrameGrid frame)
    {
        if (frame.Width != Width || frame.Height != Height || frame.Columns != Columns || frame.Rows != Rows)
            return new FrameGridComparison(false, new List<FrameGridDifference>(), 0f, Columns * Rows);
        var differences = new List<FrameGridDifference>();
        float worst = 0f;
        for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
            {
                Vector3 want = Cell(column, row), got = frame.Cell(column, row);
                var d = Vector3.Abs(got - want);
                float off = MathF.Max(d.X, MathF.Max(d.Y, d.Z));
                worst = MathF.Max(worst, off);
                if (off > Tolerance + 1e-4f) differences.Add(new FrameGridDifference(column, row, want, got, off));
            }
        differences.Sort((a, b) => b.Off.CompareTo(a.Off));
        return new FrameGridComparison(true, differences, worst, Columns * Rows);
    }

    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // Throws FormatException naming what is wrong: a golden with a mistake must not pass by checking less.
    public static FrameGrid Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json, Options); }
        catch (JsonException ex) { throw new FormatException($"not JSON: {ex.Message}"); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("a frame golden is an object");
            int width = 0, height = 0, columns = 0, rows = 0, maxCells = 0;
            bool shaders = false;
            float tolerance = DefaultTolerance;
            List<string>? lines = null;
            foreach (var p in root.EnumerateObject())
            {
                switch (p.Name)
                {
                    case "width": width = Positive(p.Value, "width"); break;
                    case "height": height = Positive(p.Value, "height"); break;
                    case "columns": columns = Positive(p.Value, "columns"); break;
                    case "rows": rows = Positive(p.Value, "rows"); break;
                    case "shaders":
                        if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("'shaders' is true or false");
                        shaders = p.Value.GetBoolean();
                        break;
                    case "tolerance":
                        if (p.Value.ValueKind != JsonValueKind.Number || (tolerance = p.Value.GetSingle()) < 0f) throw new FormatException("'tolerance' is a number, 0 or more");
                        break;
                    case "maxCells":
                        if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out maxCells) || maxCells < 0) throw new FormatException("'maxCells' is a whole number, 0 or more");
                        break;
                    case "cells":
                        if (p.Value.ValueKind != JsonValueKind.Array) throw new FormatException("'cells' is an array of strings, a row each");
                        lines = new List<string>();
                        foreach (var line in p.Value.EnumerateArray())
                            lines.Add(line.ValueKind == JsonValueKind.String ? line.GetString()! : throw new FormatException("'cells' is an array of strings, a row each"));
                        break;
                    default: throw new FormatException($"unknown key '{p.Name}' (width, height, columns, rows, shaders, tolerance, maxCells, cells)");
                }
            }
            if (width == 0 || height == 0 || columns == 0 || rows == 0) throw new FormatException("a frame golden gives width, height, columns and rows");
            if (lines == null) throw new FormatException("no cells");
            if (lines.Count != rows) throw new FormatException($"{lines.Count} rows of cells, and 'rows' says {rows}");
            var cells = new byte[columns * rows * 3];
            for (int row = 0; row < rows; row++)
            {
                var parts = lines[row].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != columns) throw new FormatException($"row {row} has {parts.Length} cells, and 'columns' says {columns}");
                for (int column = 0; column < columns; column++)
                {
                    string hex = parts[column];
                    if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
                        throw new FormatException($"row {row}, cell {column}: '{hex}' is not rrggbb");
                    int i = (row * columns + column) * 3;
                    cells[i] = (byte)(rgb >> 16);
                    cells[i + 1] = (byte)(rgb >> 8);
                    cells[i + 2] = (byte)rgb;
                }
            }
            return new FrameGrid
            {
                Width = width, Height = height, Columns = columns, Rows = rows, Shaders = shaders,
                Tolerance = tolerance, MaxCells = maxCells, _cells = cells,
            };
        }
    }

    // The golden's text: `comment` as // lines first (what it is of, how to write it again).
    public string ToJson(string? comment = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        if (!string.IsNullOrEmpty(comment))
            foreach (var line in comment.Split('\n')) sb.Append("  // ").Append(line.TrimEnd()).Append('\n');
        sb.Append(CultureInfo.InvariantCulture, $"  \"width\": {Width}, \"height\": {Height}, \"columns\": {Columns}, \"rows\": {Rows},\n");
        sb.Append(CultureInfo.InvariantCulture, $"  \"shaders\": {(Shaders ? "true" : "false")}, \"tolerance\": {Tolerance.ToString("0.###", CultureInfo.InvariantCulture)}, \"maxCells\": {MaxCells},\n");
        sb.Append("  \"cells\": [\n");
        for (int row = 0; row < Rows; row++)
        {
            sb.Append("    \"");
            for (int column = 0; column < Columns; column++)
            {
                int i = (row * Columns + column) * 3;
                if (column > 0) sb.Append(' ');
                sb.Append(_cells[i].ToString("x2", CultureInfo.InvariantCulture))
                  .Append(_cells[i + 1].ToString("x2", CultureInfo.InvariantCulture))
                  .Append(_cells[i + 2].ToString("x2", CultureInfo.InvariantCulture));
            }
            sb.Append(row < Rows - 1 ? "\",\n" : "\"\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static int Positive(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int v) && v > 0 ? v : throw new FormatException($"'{key}' is a whole number above 0");
}

internal readonly record struct FrameGridDifference(int Column, int Row, Vector3 Want, Vector3 Got, float Off);

// What comparing a frame with its golden found. SizeMatches false: the frame (or its grid) is not the
// golden's shape, and nothing else was compared.
internal sealed record FrameGridComparison(bool SizeMatches, List<FrameGridDifference> Differences, float Worst, int Cells)
{
    public bool Passed(FrameGrid golden) => SizeMatches && Differences.Count <= golden.MaxCells;

    // The worst few cells, for the log: "cell 12,4 is (0.120, 0.130, 0.140), want (0.500, ...)".
    public string Describe(int count = 5)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Differences.Count && i < count; i++)
        {
            var d = Differences[i];
            if (i > 0) sb.Append("; ");
            sb.Append(CultureInfo.InvariantCulture, $"cell {d.Column},{d.Row} is {PixelCheck.Format(d.Got)}, want {PixelCheck.Format(d.Want)}");
        }
        if (Differences.Count > count) sb.Append(CultureInfo.InvariantCulture, $"; and {Differences.Count - count} more");
        return sb.ToString();
    }
}
