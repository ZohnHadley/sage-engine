#nullable enable
using System;

namespace Sage.Simulation;

// A stable LSD radix sort of 64-bit sort keys with an int payload beside them (docs/design/06 §3.5,
// issue #319): what `RenderViewPlan.Bucket` uses to order a view's items and sprites. Equal keys keep
// their input order, so drawing order is deterministic (the comparison sort it replaces was not stable).
//
// Allocation free once warm: the scratch buffers and histograms are per thread, grown by doubling and
// reused. Eight 8-bit passes at most, and a pass whose digit is the same for every key (sort keys use
// few of their 64 bits at a time) is skipped. Short runs use an insertion sort, which is also stable
// and beats the histograms below `InsertionLimit`.
internal static class RadixSort
{
    private const int InsertionLimit = 48;
    private const int Digits = 8;

    [ThreadStatic] private static ulong[]? _keys;
    [ThreadStatic] private static int[]? _payload;
    [ThreadStatic] private static int[]? _hist;

    // Sorts `keys` ascending, moving `payload[i]` with `keys[i]`.
    public static void Sort(Span<ulong> keys, Span<int> payload)
    {
        int n = keys.Length;
        if (payload.Length < n) throw new ArgumentException("payload needs one entry per key");
        if (n < 2) return;
        if (n <= InsertionLimit)
        {
            for (int i = 1; i < n; i++)
            {
                ulong k = keys[i];
                int p = payload[i];
                int j = i - 1;
                while (j >= 0 && keys[j] > k)
                {
                    keys[j + 1] = keys[j];
                    payload[j + 1] = payload[j];
                    j--;
                }
                keys[j + 1] = k;
                payload[j + 1] = p;
            }
            return;
        }

        if (_keys == null || _keys.Length < n)
        {
            int size = Math.Max(n, (_keys?.Length ?? 0) * 2);
            _keys = new ulong[size];
            _payload = new int[size];
        }
        var hist = _hist ??= new int[Digits * 256];
        var keys2 = _keys.AsSpan(0, n);
        var payload2 = _payload.AsSpan(0, n);
        hist.AsSpan().Clear();
        for (int i = 0; i < n; i++)
        {
            ulong k = keys[i];
            for (int d = 0; d < Digits; d++) hist[(d << 8) + (int)((k >> (d * 8)) & 0xFF)]++;
        }

        var srcK = keys; var srcP = payload.Slice(0, n);
        var dstK = keys2; var dstP = payload2;
        bool inScratch = false;
        for (int d = 0; d < Digits; d++)
        {
            int shift = d * 8;
            var h = hist.AsSpan(d << 8, 256);
            if (h[(int)((srcK[0] >> shift) & 0xFF)] == n) continue;   // every key shares this digit
            int at = 0;
            for (int b = 0; b < 256; b++) { int c = h[b]; h[b] = at; at += c; }
            for (int i = 0; i < n; i++)
            {
                ulong k = srcK[i];
                int slot = h[(int)((k >> shift) & 0xFF)]++;
                dstK[slot] = k;
                dstP[slot] = srcP[i];
            }
            var tk = srcK; srcK = dstK; dstK = tk;
            var tp = srcP; srcP = dstP; dstP = tp;
            inScratch = !inScratch;
        }
        // An odd number of passes leaves the result in scratch.
        if (inScratch)
        {
            srcK.CopyTo(keys);
            srcP.CopyTo(payload);
        }
    }
}
