#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// BC1 (DXT1) and BC3 (DXT5) block compression for cooked textures (issue #302): every 4×4 block of pixels as
// two RGB565 endpoints and a 2-bit index per pixel (8 bytes), and for BC3 an alpha block in front of it
// (two 8-bit endpoints and a 3-bit index per pixel, 8 bytes more). Every desktop GPU samples these
// natively, so a texture takes an eighth (BC1) or a quarter (BC3) of the memory RGBA8 does.
//
// The encoder is the classic one: the block's colours are fitted with a line through their mean along their
// principal axis (a few rounds of power iteration on the covariance), its ends are the endpoints, every
// pixel takes the nearest of the four colours on it, and the endpoints are refitted once by least squares
// to those choices. Not the best encoder there is — it is a cook step's, not a texture artist's — but a
// flat block is exact and a gradient is close. The decoders are what a device without S3TC falls back on.
internal static class BlockCompression
{
    public static byte[] EncodeBc1(ReadOnlySpan<byte> rgba, int width, int height)
    {
        Check(rgba, width, height);
        var output = new byte[width * height / 2];
        Span<Vector3> colours = stackalloc Vector3[16];
        int offset = 0;
        for (int by = 0; by < height; by += 4)
            for (int bx = 0; bx < width; bx += 4)
            {
                for (int i = 0; i < 16; i++)
                {
                    int p = ((by + i / 4) * width + bx + i % 4) * 4;
                    colours[i] = new Vector3(rgba[p], rgba[p + 1], rgba[p + 2]);
                }
                EncodeColourBlock(colours, output.AsSpan(offset, 8));
                offset += 8;
            }
        return output;
    }

    public static byte[] EncodeBc3(ReadOnlySpan<byte> rgba, int width, int height)
    {
        Check(rgba, width, height);
        var output = new byte[width * height];
        Span<Vector3> colours = stackalloc Vector3[16];
        Span<byte> alphas = stackalloc byte[16];
        int offset = 0;
        for (int by = 0; by < height; by += 4)
            for (int bx = 0; bx < width; bx += 4)
            {
                for (int i = 0; i < 16; i++)
                {
                    int p = ((by + i / 4) * width + bx + i % 4) * 4;
                    colours[i] = new Vector3(rgba[p], rgba[p + 1], rgba[p + 2]);
                    alphas[i] = rgba[p + 3];
                }
                EncodeAlphaBlock(alphas, output.AsSpan(offset, 8));
                EncodeColourBlock(colours, output.AsSpan(offset + 8, 8));
                offset += 16;
            }
        return output;
    }

    public static byte[] DecodeBc1(ReadOnlySpan<byte> blocks, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        Span<byte> block = stackalloc byte[64];
        int offset = 0;
        for (int by = 0; by < height; by += 4)
            for (int bx = 0; bx < width; bx += 4)
            {
                DecodeColourBlock(blocks.Slice(offset, 8), block, fourColourOnly: false);
                offset += 8;
                Place(block, rgba, width, bx, by);
            }
        return rgba;
    }

    public static byte[] DecodeBc3(ReadOnlySpan<byte> blocks, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        Span<byte> block = stackalloc byte[64];
        Span<byte> alpha = stackalloc byte[8];
        int offset = 0;
        for (int by = 0; by < height; by += 4)
            for (int bx = 0; bx < width; bx += 4)
            {
                DecodeColourBlock(blocks.Slice(offset + 8, 8), block, fourColourOnly: true);
                AlphaPalette(blocks[offset], blocks[offset + 1], alpha);
                ulong bits = 0;
                for (int k = 0; k < 6; k++) bits |= (ulong)blocks[offset + 2 + k] << (8 * k);
                for (int i = 0; i < 16; i++) block[i * 4 + 3] = alpha[(int)((bits >> (3 * i)) & 7)];
                offset += 16;
                Place(block, rgba, width, bx, by);
            }
        return rgba;
    }

    private static void Check(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 4 != 0 || height % 4 != 0)
            throw new ArgumentException($"a block-compressed texture is a multiple of 4 on each side, not {width}x{height}");
        if (rgba.Length != width * height * 4) throw new ArgumentException($"{width}x{height} RGBA is {width * height * 4} bytes, not {rgba.Length}");
    }

    private static void Place(ReadOnlySpan<byte> block, byte[] rgba, int width, int bx, int by)
    {
        for (int i = 0; i < 16; i++)
            block.Slice(i * 4, 4).CopyTo(rgba.AsSpan(((by + i / 4) * width + bx + i % 4) * 4));
    }

    // ---- Colour ----

    private static void EncodeColourBlock(ReadOnlySpan<Vector3> colours, Span<byte> output)
    {
        var mean = Vector3.Zero;
        foreach (var c in colours) mean += c;
        mean /= 16f;

        // The principal axis: power iteration on the covariance, from the longest box diagonal.
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        var lo = new Vector3(255f);
        var hi = Vector3.Zero;
        foreach (var c in colours)
        {
            var d = c - mean;
            xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
            yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
            lo = Vector3.Min(lo, c);
            hi = Vector3.Max(hi, c);
        }
        var axis = hi - lo;
        for (int round = 0; round < 8 && axis.LengthSquared() > 1e-6f; round++)
        {
            axis = new Vector3(xx * axis.X + xy * axis.Y + xz * axis.Z,
                               xy * axis.X + yy * axis.Y + yz * axis.Z,
                               xz * axis.X + yz * axis.Y + zz * axis.Z);
            float length = axis.Length();
            if (length < 1e-6f) break;
            axis /= length;
        }

        Vector3 a, b;
        if (axis.LengthSquared() < 1e-6f) { a = b = mean; }
        else
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (var c in colours)
            {
                float t = Vector3.Dot(c - mean, axis);
                min = Math.Min(min, t);
                max = Math.Max(max, t);
            }
            a = mean + axis * max;
            b = mean + axis * min;
        }

        Span<int> indices = stackalloc int[16];
        ushort c0 = To565(a), c1 = To565(b);
        Choose(colours, ref c0, ref c1, indices);

        // One least-squares refit of the endpoints to the indices chosen, kept if it is better.
        if (c0 != c1 && Refit(colours, indices, out var ra, out var rb))
        {
            ushort r0 = To565(ra), r1 = To565(rb);
            Span<int> refit = stackalloc int[16];
            Choose(colours, ref r0, ref r1, refit);
            if (Error(colours, r0, r1, refit) < Error(colours, c0, c1, indices))
            {
                c0 = r0;
                c1 = r1;
                refit.CopyTo(indices);
            }
        }

        output[0] = (byte)c0; output[1] = (byte)(c0 >> 8);
        output[2] = (byte)c1; output[3] = (byte)(c1 >> 8);
        uint bits = 0;
        for (int i = 0; i < 16; i++) bits |= (uint)indices[i] << (2 * i);
        output[4] = (byte)bits; output[5] = (byte)(bits >> 8); output[6] = (byte)(bits >> 16); output[7] = (byte)(bits >> 24);
    }

    // Orders the endpoints for the four-colour mode (c0 > c1) and picks each pixel's nearest colour. Equal
    // endpoints are a flat block: every index 0.
    private static void Choose(ReadOnlySpan<Vector3> colours, ref ushort c0, ref ushort c1, Span<int> indices)
    {
        if (c0 < c1) (c0, c1) = (c1, c0);
        if (c0 == c1)
        {
            indices.Clear();
            return;
        }
        Span<Vector3> palette = stackalloc Vector3[4];
        ColourPalette(c0, c1, palette);
        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int k = 0; k < 4; k++)
            {
                float d = Vector3.DistanceSquared(colours[i], palette[k]);
                if (d < bestDistance) { bestDistance = d; best = k; }
            }
            indices[i] = best;
        }
    }

    private static float Error(ReadOnlySpan<Vector3> colours, ushort c0, ushort c1, ReadOnlySpan<int> indices)
    {
        Span<Vector3> palette = stackalloc Vector3[4];
        ColourPalette(c0, c1, palette);
        float error = 0;
        for (int i = 0; i < 16; i++) error += Vector3.DistanceSquared(colours[i], palette[indices[i]]);
        return error;
    }

    // Endpoints minimising the squared error for fixed indices (weights 1, 0, 2/3, 1/3 of the first).
    private static bool Refit(ReadOnlySpan<Vector3> colours, ReadOnlySpan<int> indices, out Vector3 a, out Vector3 b)
    {
        ReadOnlySpan<float> weight = stackalloc float[] { 1f, 0f, 2f / 3f, 1f / 3f };
        float aa = 0, ab = 0, bb = 0;
        Vector3 ax = Vector3.Zero, bx = Vector3.Zero;
        for (int i = 0; i < 16; i++)
        {
            float w = weight[indices[i]], v = 1f - w;
            aa += w * w; ab += w * v; bb += v * v;
            ax += colours[i] * w;
            bx += colours[i] * v;
        }
        float det = aa * bb - ab * ab;
        if (Math.Abs(det) < 1e-6f)
        {
            a = b = default;
            return false;
        }
        a = Vector3.Clamp((ax * bb - bx * ab) / det, Vector3.Zero, new Vector3(255f));
        b = Vector3.Clamp((bx * aa - ax * ab) / det, Vector3.Zero, new Vector3(255f));
        return true;
    }

    private static void DecodeColourBlock(ReadOnlySpan<byte> block, Span<byte> pixels, bool fourColourOnly)
    {
        ushort c0 = (ushort)(block[0] | block[1] << 8), c1 = (ushort)(block[2] | block[3] << 8);
        Span<Vector3> palette = stackalloc Vector3[4];
        bool transparent = !fourColourOnly && c0 <= c1;
        if (transparent)
        {
            palette[0] = From565(c0);
            palette[1] = From565(c1);
            palette[2] = (palette[0] + palette[1]) / 2f;
            palette[3] = Vector3.Zero;
        }
        else ColourPalette(c0, c1, palette);

        uint bits = (uint)(block[4] | block[5] << 8 | block[6] << 16 | block[7] << 24);
        for (int i = 0; i < 16; i++)
        {
            int index = (int)((bits >> (2 * i)) & 3);
            var c = palette[index];
            pixels[i * 4] = (byte)MathF.Round(c.X);
            pixels[i * 4 + 1] = (byte)MathF.Round(c.Y);
            pixels[i * 4 + 2] = (byte)MathF.Round(c.Z);
            pixels[i * 4 + 3] = (byte)(transparent && index == 3 ? 0 : 255);
        }
    }

    private static void ColourPalette(ushort c0, ushort c1, Span<Vector3> palette)
    {
        palette[0] = From565(c0);
        palette[1] = From565(c1);
        palette[2] = (palette[0] * 2f + palette[1]) / 3f;
        palette[3] = (palette[0] + palette[1] * 2f) / 3f;
    }

    private static ushort To565(Vector3 c)
    {
        int r = Math.Clamp((int)MathF.Round(c.X * 31f / 255f), 0, 31);
        int g = Math.Clamp((int)MathF.Round(c.Y * 63f / 255f), 0, 63);
        int b = Math.Clamp((int)MathF.Round(c.Z * 31f / 255f), 0, 31);
        return (ushort)(r << 11 | g << 5 | b);
    }

    // Bit replication, as the hardware expands 5 and 6 bits to 8.
    private static Vector3 From565(ushort c)
    {
        int r = c >> 11 & 31, g = c >> 5 & 63, b = c & 31;
        return new Vector3(r << 3 | r >> 2, g << 2 | g >> 4, b << 3 | b >> 2);
    }

    // ---- Alpha (BC3) ----

    private static void EncodeAlphaBlock(ReadOnlySpan<byte> alphas, Span<byte> output)
    {
        byte max = 0, min = 255;
        foreach (byte a in alphas)
        {
            max = Math.Max(max, a);
            min = Math.Min(min, a);
        }
        output[0] = max;
        output[1] = min;
        ulong bits = 0;
        if (max != min)
        {
            Span<byte> palette = stackalloc byte[8];
            AlphaPalette(max, min, palette);
            for (int i = 0; i < 16; i++)
            {
                int best = 0, bestDistance = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int d = Math.Abs(alphas[i] - palette[k]);
                    if (d < bestDistance) { bestDistance = d; best = k; }
                }
                bits |= (ulong)best << (3 * i);
            }
        }
        for (int k = 0; k < 6; k++) output[2 + k] = (byte)(bits >> (8 * k));
    }

    // a0 > a1: eight steps from a0 to a1. a0 <= a1: six steps, then 0 and 255 (the encoder never writes it,
    // but a decoder reads any file).
    private static void AlphaPalette(byte a0, byte a1, Span<byte> palette)
    {
        palette[0] = a0;
        palette[1] = a1;
        if (a0 > a1)
            for (int k = 1; k <= 6; k++) palette[k + 1] = (byte)(((7 - k) * a0 + k * a1 + 3) / 7);
        else
        {
            for (int k = 1; k <= 4; k++) palette[k + 1] = (byte)(((5 - k) * a0 + k * a1 + 2) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }
    }
}
