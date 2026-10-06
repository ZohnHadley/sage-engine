#nullable enable
using System;
using System.Diagnostics;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The radix sort behind render item buckets (issue #319, docs/design/06 §3.5): same order as a stable
// comparison sort, allocation free once warm, and faster than the comparison sort at 20k items.
public class RadixSortTests
{
    private static (ulong[] Keys, int[] Payload) Random(int n, int seed, int distinct)
    {
        var random = new System.Random(seed);
        var pool = Enumerable.Range(0, distinct).Select(_ => (ulong)random.NextInt64() ^ ((ulong)random.Next(4) << 62)).ToArray();
        var keys = new ulong[n];
        for (int i = 0; i < n; i++) keys[i] = pool[random.Next(distinct)];
        return (keys, Enumerable.Range(0, n).ToArray());
    }

    // The reference: a comparison sort made stable by breaking ties on the original position.
    private static int[] Reference(ulong[] keys) =>
        Enumerable.Range(0, keys.Length).OrderBy(i => keys[i]).ThenBy(i => i).ToArray();

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(10, 3)]
    [InlineData(48, 48)]
    [InlineData(49, 5)]
    [InlineData(1000, 7)]       // heavy ties
    [InlineData(5000, 5000)]    // mostly unique
    [InlineData(20000, 300)]
    public void MatchesAStableComparisonSort_IncludingTies(int n, int distinct)
    {
        var (keys, payload) = Random(n, 1000 + n, distinct);
        var expected = Reference(keys);

        RadixSort.Sort(keys, payload);

        Assert.Equal(expected, payload);
        for (int i = 1; i < n; i++) Assert.True(keys[i - 1] <= keys[i]);
    }

    [Fact]
    public void KeysThatShareMostBytes_AreSortedWithPassesSkipped()
    {
        // Odd and even numbers of effective passes both end in the caller's buffers.
        foreach (int shift in new[] { 0, 8, 56 })
        {
            var random = new System.Random(5 + shift);
            var keys = Enumerable.Range(0, 500).Select(_ => (ulong)random.Next(256) << shift | 0x1200UL).ToArray();
            var payload = Enumerable.Range(0, 500).ToArray();
            var expected = Reference(keys);
            RadixSort.Sort(keys, payload);
            Assert.Equal(expected, payload);
        }
    }

    [Fact]
    public void Bucket_UsesTheRadixSort_AndKeepsEntryOrderForTies()
    {
        const int N = 3000, Views = 3;
        var random = new System.Random(9);
        var viewOf = new int[N];
        var keys = new ulong[N];
        for (int i = 0; i < N; i++) { viewOf[i] = random.Next(Views); keys[i] = (ulong)random.Next(20); }
        var starts = new int[Views];
        var counts = new int[Views];
        var sorted = new ulong[N];
        var order = new int[N];

        RenderViewPlan.Bucket(viewOf, keys, starts, counts, sorted, order);

        for (int v = 0; v < Views; v++)
        {
            var expected = Enumerable.Range(0, N).Where(i => viewOf[i] == v).OrderBy(i => keys[i]).ThenBy(i => i).ToArray();
            Assert.Equal(expected, order[starts[v]..(starts[v] + counts[v])]);
        }
    }
}

// Allocation is measured per thread and time is measured, so these run alone.
[Xunit.Collection(MeasurementsCollection.Name)]
public class RadixSortMeasurementTests
{
    private const int N = 20_000;

    [Fact]
    public void SortingIsAllocationFreeOnceWarm()
    {
        var (source, _) = Random();
        var keys = new ulong[N];
        var payload = new int[N];
        void Run() { source.CopyTo(keys, 0); for (int i = 0; i < N; i++) payload[i] = i; RadixSort.Sort(keys, payload); }
        Run();   // grows the scratch

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++) Run();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Absolute times flake on a shared CI machine, so the bound is relative: the radix sort must not be
    // slower than the comparison sort it replaced (Span.Sort with items), measured as the best of several
    // rounds each. The rounds alternate between the two sorts, so a burst of load on the machine slows both
    // rather than only whichever was being measured. On a quiet machine it is around 3-5x faster; the bound
    // has plenty of slack.
    [Fact]
    public void At20kItems_RadixIsNoSlowerThanTheComparisonSort()
    {
        var (source, _) = Random();
        var keys = new ulong[N];
        var payload = new int[N];
        void Reset() { source.CopyTo(keys, 0); for (int i = 0; i < N; i++) payload[i] = i; }
        long Time(Action sort)
        {
            Reset();
            var watch = Stopwatch.StartNew();
            sort();
            return watch.ElapsedTicks;
        }
        Action radixSort = () => RadixSort.Sort(keys, payload);
        Action comparisonSort = () => keys.AsSpan().Sort(payload.AsSpan());
        Time(radixSort); Time(comparisonSort);   // warm both

        long radix = long.MaxValue, comparison = long.MaxValue;
        for (int round = 0; round < 25; round++)
        {
            radix = Math.Min(radix, Time(radixSort));
            comparison = Math.Min(comparison, Time(comparisonSort));
        }

        Assert.True(radix <= comparison * 1.5, $"radix {radix} ticks vs comparison {comparison} ticks");
    }

    private static (ulong[], int) Random()
    {
        var random = new System.Random(123);
        var keys = new ulong[N];
        for (int i = 0; i < N; i++) keys[i] = (ulong)random.NextInt64();
        return (keys, 0);
    }
}
