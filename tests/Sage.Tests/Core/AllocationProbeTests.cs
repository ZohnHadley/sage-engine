#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// What the zero-allocation tests stand on: GC.GetAllocatedBytesForCurrentThread reads zero over a window
// in which this thread allocated nothing, whatever the other threads do meanwhile.
//
// With background GC on it does not (dotnet/runtime#134724): a background collection clears every
// thread's allocation context without taking the unused bytes off the thread's count, so the window
// reads up to 8 KB. That was #81's Windows-only flake — 2016, 7408, 3816 and 32 bytes, a different test
// each run, in world loops and in pure arithmetic alike. Sage.Tests.csproj turns background GC off;
// the second test is the flake itself, and fails in a few hundred windows when it is back on
// (DOTNET_gcConcurrent=1 overrides the project's setting).
[Collection(MeasurementsCollection.Name)]
public class AllocationProbeTests
{
    [Fact]
    public void TheTestProcessRunsWithoutBackgroundGC()
    {
        Assert.False(AllocationProbe.BackgroundGC,
            "background GC is on, so zero-allocation windows can read bytes nobody allocated (dotnet/runtime#134724)");
    }

    [Fact]
    public void AWindowThatAllocatesNothing_ReadsZero_WhileAnotherThreadCollects()
    {
        var keep = new object[8];
        bool stop = false;
        var collector = new Thread(() =>
        {
            var random = new Random(1);
            var live = new List<byte[]>();
            while (!Volatile.Read(ref stop))
            {
                for (int i = 0; i < 200; i++) live.Add(new byte[random.Next(16, 2000)]);
                if (live.Count > 20_000) live.RemoveRange(0, 10_000);
                GC.Collect(2, GCCollectionMode.Forced, blocking: false);   // a background GC when that is on
            }
        }) { IsBackground = true, Name = "collector" };
        collector.Start();

        int windows = 300, read = 0;
        long most = 0;
        try
        {
            for (int w = 0; w < windows; w++)
            {
                keep[w % keep.Length] = new byte[40 + w];   // this thread's allocation context part used, as any test's is
                long before = GC.GetAllocatedBytesForCurrentThread();
                Thread.SpinWait(400_000);                    // allocates nothing
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (bytes != 0) { read++; most = Math.Max(most, bytes); }
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            collector.Join();
        }
        GC.KeepAlive(keep);
        Assert.True(read == 0, $"{read} of {windows} windows that allocated nothing read bytes (up to {most})");
    }
}
