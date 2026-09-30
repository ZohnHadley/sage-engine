#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Zero-allocation assertions that say where the bytes went when they fail.
//
//   AllocationProbe.AssertNone(200, () => { world.RunFixed(1f / 60f); Profiler.EndFrame(); });
//
// Measures GC.GetAllocatedBytesForCurrentThread around the whole loop, as the tests always did, and in
// the same run (a flaky allocation may not come back on a re-run) turns on the Profiler's per-thread
// allocation tracking, so every phase and system scope the world opens adds up what it allocated. On
// failure the message names the scopes that allocated, what was allocated outside every scope, the
// iterations that allocated, and whether a garbage collection ran meanwhile. Nothing is allocated by
// the probe between the two readings: the per-iteration buffer is made before, the report after.
//
// **The reading is only exact without background GC** (Sage.Tests.csproj turns it off): a background
// collection started by any thread drops this thread's allocation context without taking its unused
// bytes off the count (dotnet/runtime#134724), and a loop that allocated nothing reads up to 8 KB.
// Measure refuses to run in a process that has it on, rather than report such bytes as the code's.
internal static class AllocationProbe
{
    public static void AssertNone(int iterations, Action step)
    {
        var report = Measure(iterations, step);
        if (report.Bytes != 0) Assert.Fail(report.ToString());
    }

    // Workstation GC reports Interactive only when concurrent (background) GC is on.
    public static bool BackgroundGC => System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.Interactive;

    public static Report Measure(int iterations, Action step)
    {
        if (BackgroundGC)
            Assert.Fail("Background GC is on in this process, so GC.GetAllocatedBytesForCurrentThread can count bytes nobody " +
                        "allocated (dotnet/runtime#134724). The test project sets ConcurrentGarbageCollection=false; keep it.");
        var perIteration = new long[iterations];
        var baseline = Profiler.All.ToDictionary(e => e, e => e.AllocatedBytes);
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        bool wasTracking = Profiler.TrackAllocations;
        Profiler.TrackAllocations = true;
        long scopedBefore = Profiler.TrackedOutermostBytes;

        long before = GC.GetAllocatedBytesForCurrentThread();
        long last = before;
        for (int i = 0; i < iterations; i++)
        {
            step();
            long now = GC.GetAllocatedBytesForCurrentThread();
            perIteration[i] = now - last;
            last = now;
        }
        long total = GC.GetAllocatedBytesForCurrentThread() - before;
        long scoped = Profiler.TrackedOutermostBytes - scopedBefore;
        Profiler.TrackAllocations = wasTracking;

        if (total == 0) return new Report(0, "");

        var text = new StringBuilder();
        text.Append($"Expected no allocation on this thread over {iterations} iterations; {total} bytes were allocated.\n");
        text.Append($"  thread {Environment.CurrentManagedThreadId}; collections during the loop: gen0 {GC.CollectionCount(0) - gen0}, " +
                    $"gen1 {GC.CollectionCount(1) - gen1}, gen2 {GC.CollectionCount(2) - gen2}; Profiler.Enabled {Profiler.Enabled}\n");
        var allocating = perIteration.Select((bytes, i) => (bytes, i)).Where(x => x.bytes != 0).ToList();
        text.Append($"  iterations that allocated ({allocating.Count}): " +
                    string.Join(", ", allocating.Take(12).Select(x => $"#{x.i}: {x.bytes}")) +
                    (allocating.Count > 12 ? ", ..." : "") + "\n");
        text.Append("  by profiler scope (a phase includes its systems):\n");
        bool any = false;
        foreach (var entry in Profiler.All)
        {
            long bytes = entry.AllocatedBytes - (baseline.TryGetValue(entry, out var b) ? b : 0);
            if (bytes == 0) continue;
            text.Append($"    {entry.Name,-56} {bytes,8} bytes\n");
            any = true;
        }
        if (!any) text.Append("    (none)\n");
        text.Append($"  outside every profiler scope (the loop's own code, the runtime): {total - scoped} bytes");
        return new Report(total, text.ToString());
    }

    public readonly record struct Report(long Bytes, string Text)
    {
        public override string ToString() => Text;
    }
}
