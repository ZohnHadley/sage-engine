#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace sage_engine;

// Named timing scopes (docs/design/02 §4.4):
//   using var _ = Profiler.Begin("AI.Think");
// The world wraps every phase and system automatically. Results are per frame (EndFrame) and shown
// by `stat frame` and `sys_list`. Main thread only. Scope is a struct, so a scope allocates nothing;
// with Enabled off (the Shipping default) Begin returns an empty scope.
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

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
    private static readonly List<Entry> Ordered = new();

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
        if (!Entries.TryGetValue(name, out var entry))
        {
            entry = new Entry(name, Ordered.Count);
            Entries.Add(name, entry);
            Ordered.Add(entry);
        }
        return new Scope(entry);
    }

    // Call once per frame: publishes this frame's totals and resets the accumulators.
    public static void EndFrame()
    {
        double msPerTick = 1000.0 / Stopwatch.Frequency;
        foreach (var e in Ordered)
        {
            e.LastMs = e.Ticks * msPerTick;
            e.LastCalls = e.Calls;
            e.AverageMs = e.AverageMs == 0 ? e.LastMs : e.AverageMs * 0.95 + e.LastMs * 0.05;
            e.Ticks = 0;
            e.Calls = 0;
        }
    }

    public static IReadOnlyList<Entry> All => Ordered;

    public static Entry? Find(string name) => Entries.TryGetValue(name, out var e) ? e : null;
}
