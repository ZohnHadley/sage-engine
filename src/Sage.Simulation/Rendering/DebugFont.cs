#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// A stroke font for `DebugDraw.Text3D` (docs/design/06 §3.2, issue 4n-18): every glyph is a few
// polylines on a 3 x 5 grid, so a label is line segments like any other debug shape and the client
// needs no font, atlas or text path. Capitals, digits and the punctuation a label wants; lower case
// draws as capitals, anything else as a box.
internal static class DebugFont
{
    public const int GridWidth = 3;    // glyph cell, in grid units
    public const int GridHeight = 5;
    public const float Advance = 4f;   // grid units from one glyph's origin to the next

    // Polylines separated by '|', points "xy" (two digits, y up from the baseline).
    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['A'] = "00 03 14 23 20|01 21", ['B'] = "00 04 14 24 23 12 02|12 21 20 10 00",
        ['C'] = "24 04 00 20", ['D'] = "00 04 23 21 00", ['E'] = "24 04 00 20|02 12", ['F'] = "24 04 00|02 12",
        ['G'] = "24 04 00 20 22 12", ['H'] = "04 00|24 20|02 22", ['I'] = "04 24|14 10|00 20", ['J'] = "04 24 20 00 01",
        ['K'] = "04 00|24 02 20", ['L'] = "04 00 20", ['M'] = "00 04 12 24 20", ['N'] = "00 04 20 24",
        ['O'] = "00 04 24 20 00", ['P'] = "00 04 24 22 02", ['Q'] = "00 04 24 20 00|11 20", ['R'] = "00 04 24 22 02|12 20",
        ['S'] = "24 04 02 22 20 00", ['T'] = "04 24|14 10", ['U'] = "04 00 20 24", ['V'] = "04 10 24",
        ['W'] = "04 00 12 20 24", ['X'] = "04 20|00 24", ['Y'] = "04 12 24|12 10", ['Z'] = "04 24 00 20",
        ['0'] = "00 04 24 20 00|00 24", ['1'] = "03 14 10|00 20", ['2'] = "04 24 22 00 20", ['3'] = "04 24 20 00|02 22",
        ['4'] = "04 02 22|24 20", ['5'] = "24 04 02 22 20 00", ['6'] = "24 04 00 20 22 02", ['7'] = "04 24 10",
        ['8'] = "00 04 24 20 00|02 22", ['9'] = "00 20 24 04 02 22",
        ['.'] = "00 01", [','] = "10 11 01", [':'] = "11 12|13 14", ['-'] = "02 22", ['+'] = "02 22|11 13", ['_'] = "00 20",
        ['/'] = "00 24", ['('] = "14 02 10", [')'] = "04 12 00", ['!'] = "14 12|10 11", ['?'] = "04 24 22 12 11|10 11",
        ['#'] = "10 14|20 24|03 23|01 21", ['%'] = "03 04|00 24|20 21", ['='] = "01 21|03 23", ['['] = "14 04 00 10", [']'] = "04 14 10 00",
    };

    // Whether the font draws `c` itself (case folded) rather than as the unknown box.
    public static bool Has(char c) => c == ' ' || Glyphs.ContainsKey(char.ToUpperInvariant(c));

    // The width of `text` in grid units (the last glyph without its trailing gap).
    public static float Width(string text) => text.Length == 0 ? 0f : (text.Length - 1) * Advance + GridWidth;

    // Appends `text` as segments: centred on `at`, `height` metres tall, laid along `right` and `up`
    // (the camera's, for a label that faces it).
    public static void Layout(string text, Vector3 at, Vector3 right, Vector3 up, float height, uint colour, List<DebugLine> into)
    {
        float unit = height / GridHeight;
        float x0 = -Width(text) * 0.5f;
        for (int i = 0; i < text.Length; i++)
        {
            char c = char.ToUpperInvariant(text[i]);
            if (c == ' ') continue;
            string strokes = Glyphs.TryGetValue(c, out var g) ? g : "00 04 24 20 00";
            float ox = x0 + i * Advance;
            foreach (var stroke in strokes.Split('|'))
            {
                var points = stroke.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                Vector3 previous = default;
                for (int p = 0; p < points.Length; p++)
                {
                    float gx = ox + (points[p][0] - '0'), gy = (points[p][1] - '0') - GridHeight * 0.5f;
                    Vector3 point = at + (right * gx + up * gy) * unit;
                    if (p > 0) into.Add(new DebugLine(previous, point, colour));
                    previous = point;
                }
            }
        }
    }
}
