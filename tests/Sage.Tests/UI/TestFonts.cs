#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Sage.Tests;

// TrueType fonts made on the spot for the script tests (issue #345), so no font is downloaded or shipped
// for them: each character a solid box, its advance whatever the test says, in an em of 1000 units with
// an ascent of 800. A port of engine_content/tools/make_font.py's writer (cmap format 4, so the Basic
// Multilingual Plane only).
internal static class TestFonts
{
    private const int Em = 1000, Ascent = 800, Descent = -200;

    // A font with a glyph for each codepoint, `advance` units wide (a box inset by a tenth of the em).
    public static byte[] Build(string name, IEnumerable<int> codepoints, int advance) =>
        Build(name, codepoints.Distinct().ToDictionary(c => c, _ => advance));

    public static byte[] Build(string name, IReadOnlyDictionary<int, int> advances)
    {
        var codes = advances.Keys.Where(c => c > 0 && c < 0xFFFF).OrderBy(c => c).ToList();
        // Glyph 0 is .notdef, a box half an em wide.
        var glyphs = new List<(bool Ink, int Advance)> { (true, 500) };
        var map = new Dictionary<int, int>();
        foreach (int c in codes)
        {
            map[c] = glyphs.Count;
            glyphs.Add((c != ' ', advances[c]));
        }

        var glyf = new List<byte>();
        var loca = new List<int> { 0 };
        foreach (var (ink, adv) in glyphs)
        {
            if (ink) glyf.AddRange(Box(Em / 10, 0, Math.Max(adv - Em / 10, Em / 10 + 1), 700));
            loca.Add(glyf.Count);
        }
        int maxAdvance = glyphs.Max(g => g.Advance);

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = Be(w => { w.U32(0x00010000); w.U32(0x00010000); w.U32(0); w.U32(0x5F0F3CF5); w.U16(0x000B); w.U16(Em);
                                  w.I64(0); w.I64(0); w.I16(0); w.I16(0); w.I16((short)maxAdvance); w.I16(700); w.U16(0); w.U16(8);
                                  w.I16(2); w.I16(1); w.I16(0); }),
            ["hhea"] = Be(w => { w.U32(0x00010000); w.I16(Ascent); w.I16(Descent); w.I16(0); w.U16(maxAdvance); w.I16(0); w.I16(0);
                                  w.I16((short)maxAdvance); w.I16(1); w.I16(0); w.I16(0); for (int i = 0; i < 4; i++) w.I16(0); w.I16(0); w.U16(glyphs.Count); }),
            ["maxp"] = Be(w => { w.U32(0x00010000); w.U16(glyphs.Count); w.U16(4); w.U16(1); w.U16(0); w.U16(0); w.U16(2);
                                  for (int i = 0; i < 8; i++) w.U16(0); }),
            ["hmtx"] = Be(w => { foreach (var g in glyphs) { w.U16(g.Advance); w.I16(0); } }),
            ["cmap"] = Cmap(map),
            ["loca"] = Be(w => { foreach (int o in loca) w.U32((uint)o); }),
            ["glyf"] = glyf.ToArray(),
            ["name"] = Name(name),
            ["post"] = Be(w => { w.U32(0x00030000); w.U32(0); w.I16(-100); w.I16(50); for (int i = 0; i < 5; i++) w.U32(0); }),
        };

        int n = tables.Count;
        int search = 1;
        while (search * 2 <= n) search *= 2;
        var output = new Writer();
        output.U32(0x00010000); output.U16(n); output.U16(search * 16); output.U16((int)Math.Log2(search)); output.U16(n * 16 - search * 16);
        int offset = 12 + 16 * n;
        var body = new List<byte>();
        foreach (var (tag, data) in tables)
        {
            output.Bytes(Encoding.ASCII.GetBytes(tag));
            output.U32(Checksum(data));
            output.U32((uint)(offset + body.Count));
            output.U32((uint)data.Length);
            body.AddRange(data);
            while (body.Count % 4 != 0) body.Add(0);
        }
        output.Bytes(body.ToArray());
        return output.ToArray();
    }

    // One rectangle, clockwise: a glyph's whole outline.
    private static byte[] Box(int x0, int y0, int x1, int y1) => Be(w =>
    {
        w.I16(1); w.I16((short)x0); w.I16((short)y0); w.I16((short)x1); w.I16((short)y1);
        w.U16(3);          // the one contour ends at point 3
        w.U16(0);          // no instructions
        for (int i = 0; i < 4; i++) w.Byte(1);   // on the curve, coordinates as shorts
        int[] xs = { x0, x0, x1, x1 }, ys = { y0, y1, y1, y0 };
        int px = 0, py = 0;
        foreach (int x in xs) { w.I16((short)(x - px)); px = x; }
        foreach (int y in ys) { w.I16((short)(y - py)); py = y; }
        if (w.Length % 2 != 0) w.Byte(0);
    });

    private static byte[] Cmap(Dictionary<int, int> map)
    {
        var segments = new List<(int Start, int End, int Delta)>();
        foreach (int c in map.Keys.OrderBy(c => c))
        {
            int g = map[c];
            if (segments.Count > 0 && segments[^1].End == c - 1 && map[c - 1] == g - 1)
                segments[^1] = (segments[^1].Start, c, segments[^1].Delta);
            else segments.Add((c, c, (g - c) & 0xFFFF));
        }
        segments.Add((0xFFFF, 0xFFFF, 1));
        int count = segments.Count;
        int search = 1;
        while (search * 2 <= count) search *= 2;
        search *= 2;
        var sub = Be(w =>
        {
            w.U16(count * 2); w.U16(search); w.U16((int)Math.Log2(search / 2)); w.U16(count * 2 - search);
            foreach (var s in segments) w.U16(s.End);
            w.U16(0);
            foreach (var s in segments) w.U16(s.Start);
            foreach (var s in segments) w.U16(s.Delta);
            foreach (var _ in segments) w.U16(0);
        });
        return Be(w =>
        {
            w.U16(0); w.U16(1); w.U16(3); w.U16(1); w.U32(12);
            w.U16(4); w.U16(6 + sub.Length); w.U16(0); w.Bytes(sub);
        });
    }

    private static byte[] Name(string name)
    {
        var raw = Encoding.BigEndianUnicode.GetBytes(name);
        return Be(w =>
        {
            w.U16(0); w.U16(1); w.U16(6 + 12);
            w.U16(3); w.U16(1); w.U16(0x409); w.U16(4); w.U16(raw.Length); w.U16(0);
            w.Bytes(raw);
        });
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int i = 0; i < data.Length; i += 4)
        {
            uint word = 0;
            for (int k = 0; k < 4; k++) word = (word << 8) | (i + k < data.Length ? data[i + k] : 0u);
            sum += word;
        }
        return sum;
    }

    private static byte[] Be(Action<Writer> write)
    {
        var w = new Writer();
        write(w);
        return w.ToArray();
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();
        public int Length => (int)_stream.Length;
        public void Byte(int b) => _stream.WriteByte((byte)b);
        public void Bytes(byte[] b) => _stream.Write(b);
        public void U16(int v) { Byte(v >> 8); Byte(v); }
        public void I16(int v) => U16(v & 0xFFFF);
        public void U32(uint v) { U16((int)(v >> 16)); U16((int)(v & 0xFFFF)); }
        public void I64(long v) { U32((uint)(v >> 32)); U32((uint)v); }
        public byte[] ToArray() => _stream.ToArray();
    }
}
