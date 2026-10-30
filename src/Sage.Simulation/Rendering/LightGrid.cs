#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// The frame's lights, bucketed by where they reach (issue #314, 06 §3.9's "the answer is a grid").
//
// `LightRules.Nearest` looks at every light for every draw: fine for a room's four lamps, not for a town's
// hundred with a thousand draws under them. The grid cuts space into cubes of `CellSize` metres and lists
// each light in every cell its range's bounding box touches; a draw then looks only at the lights listed in
// the cells its bounding sphere touches. Those lists hold every light that can reach the sphere (a light
// whose box misses them all cannot), taken in the full list's order, so the answer is exactly `Nearest`'s over
// all of them (test: TheGridChoosesWhatTheFullListChooses) — it is a cache, never a different decision.
//
// Allocation-free once warm (02 §4.6): one sorted array of (cell, light) keys, grown when a frame needs
// more, and searched by bisection. A light so large it would sit in more than `MaxCellsPerLight` cells
// (a sun-sized floodlight) goes on a short list every lookup reads instead, and a draw that wide (a terrain
// chunk) reads every light. With `Threshold` lights or fewer there is no grid at all: a straight pass over a
// handful beats any lookup.
internal sealed class LightGrid
{
    public const float CellSize = 8f;
    public const int Threshold = 16;
    public const int MaxCellsPerLight = 64;

    // Cell coordinates are camera-relative and 16 bits each (±262 km at 8 m), and a light's index is the
    // key's low 16 bits: a grid holds at most 65 535 lights, and any past that go on the everywhere list.
    private const int Bias = 1 << 15;
    private const int MaxIndexed = (1 << 16) - 1;

    private LightSample[] _lights = Array.Empty<LightSample>();
    private int _count;
    private ulong[] _keys = new ulong[256];
    private int _keyCount;
    private int[] _everywhere = new int[8];
    private int _everywhereCount;
    private int[] _found = new int[32];
    private LightSample[] _candidates = new LightSample[32];
    private bool _gridded;

    public int Count => _count;
    public int Cells => _keyCount;   // (cell, light) entries: a measure of the grid's size for r_stats

    // This frame's lights (camera-relative): copied, so the caller's list may change afterwards.
    public void Build(ReadOnlySpan<LightSample> lights)
    {
        _count = lights.Length;
        if (_lights.Length < _count) _lights = new LightSample[Math.Max(_count, _lights.Length * 2)];
        lights.CopyTo(_lights);
        _keyCount = 0;
        _everywhereCount = 0;
        _gridded = _count > Threshold;
        if (!_gridded) return;

        for (int i = 0; i < _count; i++)
        {
            ref readonly var light = ref _lights[i];
            if (!(light.Range > 0f)) continue;   // lights nothing: in no cell
            var lo = CellOf(light.Position - new Vector3(light.Range));
            var hi = CellOf(light.Position + new Vector3(light.Range));
            long cells = (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
            if (cells > MaxCellsPerLight || i > MaxIndexed)
            {
                if (_everywhereCount == _everywhere.Length) Array.Resize(ref _everywhere, _everywhere.Length * 2);
                _everywhere[_everywhereCount++] = i;
                continue;
            }

            if (_keyCount + cells > _keys.Length) Array.Resize(ref _keys, Math.Max(_keys.Length * 2, _keyCount + (int)cells));
            for (int x = lo.X; x <= hi.X; x++)
                for (int y = lo.Y; y <= hi.Y; y++)
                    for (int z = lo.Z; z <= hi.Z; z++)
                        _keys[_keyCount++] = Key(x, y, z) | (uint)i;
        }

        Array.Sort(_keys, 0, _keyCount);   // by cell, then by light: each cell's lights in the list's order
    }

    public int Nearest(Vector3 at, Span<LightSample> result) => Nearest(at, 0f, result);

    // The lights that matter most to a sphere round `at`, strongest first, into `result`; returns how many.
    // The same answer as `LightRules.Nearest` over everything `Build` was given.
    public int Nearest(Vector3 at, float radius, Span<LightSample> result)
    {
        var lo = CellOf(at - new Vector3(radius));
        var hi = CellOf(at + new Vector3(radius));
        long cells = (long)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
        if (!_gridded || cells > MaxCellsPerLight) return LightRules.Nearest(_lights.AsSpan(0, _count), at, radius, result);

        int found = 0;
        for (int x = lo.X; x <= hi.X; x++)
            for (int y = lo.Y; y <= hi.Y; y++)
                for (int z = lo.Z; z <= hi.Z; z++)
                {
                    ulong key = Key(x, y, z);
                    int start = LowerBound(key), end = LowerBound(key + (1UL << 16));
                    Reserve(found + end - start);
                    for (int k = start; k < end; k++) _found[found++] = (int)(_keys[k] & 0xFFFF);
                }
        Reserve(found + _everywhereCount);
        Array.Copy(_everywhere, 0, _found, found, _everywhereCount);
        found += _everywhereCount;

        // In the full list's order, each light once: a light is listed in every cell it touches.
        Array.Sort(_found, 0, found);
        if (_candidates.Length < found) _candidates = new LightSample[Math.Max(found, _candidates.Length * 2)];
        int n = 0;
        for (int i = 0; i < found; i++)
            if (i == 0 || _found[i] != _found[i - 1]) _candidates[n++] = _lights[_found[i]];
        return LightRules.Nearest(_candidates.AsSpan(0, n), at, radius, result);
    }

    private void Reserve(int size)
    {
        if (_found.Length < size) Array.Resize(ref _found, Math.Max(size, _found.Length * 2));
    }

    private int LowerBound(ulong key)
    {
        int lo = 0, hi = _keyCount;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_keys[mid] < key) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static (int X, int Y, int Z) CellOf(Vector3 at) => (Coord(at.X), Coord(at.Y), Coord(at.Z));

    private static int Coord(float v)
    {
        float c = MathF.Floor(v / CellSize);
        if (!(c > -Bias)) return -Bias;          // NaN lands here too: a cell, not a crash
        if (c > Bias - 1) return Bias - 1;
        return (int)c;
    }

    private static ulong Key(int x, int y, int z) =>
        ((ulong)(uint)(x + Bias) << 48) | ((ulong)(uint)(y + Bias) << 32) | ((ulong)(uint)(z + Bias) << 16);
}
