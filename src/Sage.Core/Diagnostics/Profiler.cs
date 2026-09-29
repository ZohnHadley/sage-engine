#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace sage_engine;

// Named timing scopes (docs/design/02 §4.4):
//   using var _ = Profiler.Begin("AI.Think");
// The world wraps every phase and system automatically. Results are per frame (EndFrame) and shown
// by `stat frame` and `sys_list`. Scope is a struct, so a scope allocates nothing; with Enabled off
// (the Shipping default) Begin returns an empty scope.
//
// **The tables are per thread** (issue #11). A world is ticked on one thread, and the game's is the
// main thread, which is where `stat` and `sys_list` read from — so the game sees exactly what it did
// when this was one static table. What changed is a second world on another thread (a test running in
// parallel; later a server or an editor's play world): it used to write into the same Dictionary,
// corrupting it ("an item with the same key has already been added") and allocating on the other
// world's behalf; now it has tables of its own. `Enabled` is still one setting for the process.
// Later: Chrome-trace dump (profile_start/profile_stop) and Tracy.
public static class Profiler
{
    public sealed class Entry
    {
        internal Entry(string name, int order) { Name = name; Order = order; }
        public string Name { get; }
        public int Order { get; }            // first-seen order (phases and systems appear in run order)
        public double LastMs { get; internal set; }
        public double AverageMs { get; internal set; }   // exponential moving average (~20 frames)
        public int LastCalls { get; internal set; }
        internal long Ticks;
        internal int Calls;
    }

    private sealed class Tables
    {
        public readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
        public readonly List<Entry> Ordered = new();
    }

    [ThreadStatic] private static Tables? _tables;
    private static Tables Current => _tables ??= new Tables();

    public static bool Enabled { get; set; } = BuildInfo.IsDevBuild;

    public readonly struct Scope : IDisposable
    {
        private readonly Entry? _entry;
        private readonly long _start;

        internal Scope(Entry entry)
        {
            _entry = entry;
            _start = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_entry == null) return;
            _entry.Ticks += Stopwatch.GetTimestamp() - _start;
            _entry.Calls++;
        }
    }

    public static Scope Begin(string name)
    {
        if (!Enabled) return default;
        var tables = Current;
        if (!tables.Entries.TryGetValue(name, out var entry))
        {
            entry = new Entry(name, tables.Ordered.Count);
            tables.Entries.Add(name, entry);
            tables.Ordered.Add(entry);
        }
        return new Scope(entry);
    }

    // Call once per frame, on the thread that ran it: publishes this frame's totals and resets the
    // accumulators.
    public static void EndFrame()
    {
        double msPerTick = 1000.0 / Stopwatch.Frequency;
        foreach (var e in Current.Ordered)
        {
            e.LastMs = e.Ticks * msPerTick;
            e.LastCalls = e.Calls;
            e.AverageMs = e.AverageMs == 0 ? e.LastMs : e.AverageMs * 0.95 + e.LastMs * 0.05;
            e.Ticks = 0;
            e.Calls = 0;
        }
    }

    // This thread's entries, in first-seen order.
    public static IReadOnlyList<Entry> All => Current.Ordered;

    public static Entry? Find(string name) => Current.Entries.TryGetValue(name, out var e) ? e : null;
}
