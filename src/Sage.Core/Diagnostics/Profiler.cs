#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Sage.Core;

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
// A capture of every scope as a Chrome trace (trace_start / trace_dump, issue #300) is ProfilerTrace.cs;
// Tracy is later.
public static partial class Profiler
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
        internal long AllocatedBytes;        // on this thread, inside the scope, while TrackAllocations is on; never reset
    }

    internal sealed class Tables
    {
        public readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
        public readonly List<Entry> Ordered = new();
        public bool TrackAllocations;
        public int Depth;                    // open tracked scopes
        public long OutermostBytes;          // allocated inside outermost tracked scopes (no double counting)
    }

    [ThreadStatic] private static Tables? _tables;
    private static Tables Current => _tables ??= new Tables();

    public static bool Enabled { get; set; } = BuildInfo.IsDevBuild;

    public readonly struct Scope : IDisposable
    {
        private readonly Entry? _entry;
        private readonly long _start;
        private readonly Tables? _tracking;   // set only while this thread tracks allocations
        private readonly long _startBytes;

        internal Scope(Entry entry)
        {
            _entry = entry;
            _start = Stopwatch.GetTimestamp();
        }

        private Scope(Entry entry, Tables tracking)
        {
            _entry = entry;
            _tracking = tracking;
            tracking.Depth++;
            _startBytes = GC.GetAllocatedBytesForCurrentThread();
            _start = Stopwatch.GetTimestamp();
        }

        internal static Scope Tracked(Entry entry, Tables tracking) => new(entry, tracking);

        public void Dispose()
        {
            if (_entry == null) return;
            long end = Stopwatch.GetTimestamp();
            _entry.Ticks += end - _start;
            if (_capture != null) Capture(_entry.Name, _start, end);
            _entry.Calls++;
            if (_tracking != null)
            {
                long bytes = GC.GetAllocatedBytesForCurrentThread() - _startBytes;
                _entry.AllocatedBytes += bytes;
                if (--_tracking.Depth == 0) _tracking.OutermostBytes += bytes;
            }
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
        return tables.TrackAllocations ? Scope.Tracked(entry, tables) : new Scope(entry);
    }

    // Adds a span timed elsewhere to `name`'s entry on this thread, as if a scope of that name had been
    // open for `elapsedTicks` (Stopwatch ticks): for work that ran on another thread on this one's behalf —
    // a world's systems run side by side (issue #288) — so `stat frame` and `sys_list` still show it.
    public static void Record(string name, long elapsedTicks)
    {
        if (!Enabled) return;
        var tables = Current;
        if (!tables.Entries.TryGetValue(name, out var entry))
        {
            entry = new Entry(name, tables.Ordered.Count);
            tables.Entries.Add(name, entry);
            tables.Ordered.Add(entry);
        }
        entry.Ticks += elapsedTicks;
        entry.Calls++;
    }

    // **Allocation tracking, for tests** (this thread only): while on, every scope also adds up what was
    // allocated on this thread inside it (Entry.AllocatedBytes, never reset by EndFrame), so a failing
    // zero-allocation test can name the phase or system that allocated (tests: AllocationProbe). Off, it
    // costs Begin one bool read. GC.GetAllocatedBytesForCurrentThread allocates nothing itself.
    internal static bool TrackAllocations
    {
        get => Current.TrackAllocations;
        set { var t = Current; t.TrackAllocations = value; t.Depth = 0; }
    }

    // What the outermost tracked scopes on this thread allocated in all, so far.
    internal static long TrackedOutermostBytes => Current.OutermostBytes;

    // Call once per frame, on the thread that ran it: publishes this frame's totals and resets the
    // accumulators.
    public static void EndFrame()
    {
        if (_capture != null) { long now = Stopwatch.GetTimestamp(); Capture(FrameMarker, now, now); }
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
