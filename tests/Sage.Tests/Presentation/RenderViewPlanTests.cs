#nullable enable
using System;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The planning half of drawing several views a frame (issue #77, docs/design/06 §3.4 "Views"): which
// views draw in which order, and which items and sprites each draws. The drawing half needs a GPU and
// is exercised by the smoke runs (`r_testview`).
public class RenderViewPlanTests
{
    private const int Screen = RenderViewPlan.Screen;

    [Fact]
    public void OffScreenTargetsDrawFirst_ThenTheScreen_EachByOrder()
    {
        // 0: the main view; 1: a picture-in-picture over it; 2: a minimap into target 3;
        // 3: a mirror into target 0; 4: a second view into target 3, drawn before the minimap.
        int[] targets = { Screen, Screen, 3, 0, 3 };
        int[] orders = { 0, 5, 1, 0, 0 };
        var order = new int[5];

        RenderViewPlan.OrderViews(targets, orders, order);

        Assert.Equal(new[] { 3, 4, 2, 0, 1 }, order);
    }

    [Fact]
    public void ViewsWithTheSameTargetAndOrder_KeepTheirSnapshotOrder()
    {
        int[] targets = { Screen, 1, Screen, 1 };
        int[] orders = { 0, 0, 0, 0 };
        var order = new int[4];

        RenderViewPlan.OrderViews(targets, orders, order);

        Assert.Equal(new[] { 1, 3, 0, 2 }, order);
    }

    [Fact]
    public void Bucket_GivesEachViewItsOwnRange_SortedByKey()
    {
        // Entries as extract writes them: interleaved, every view's copy of an entity in turn.
        int[] viewOf = { 0, 1, 2, 0, 1, 0, 2 };
        ulong[] keys = { 30, 7, 5, 10, 3, 20, 1 };
        var starts = new int[3];
        var counts = new int[3];
        var sorted = new ulong[7];
        var order = new int[7];

        RenderViewPlan.Bucket(viewOf, keys, starts, counts, sorted, order);

        Assert.Equal(new[] { 0, 3, 5 }, starts);
        Assert.Equal(new[] { 3, 2, 2 }, counts);
        Assert.Equal(new[] { 3, 5, 0 }, order[0..3]);        // view 0: keys 10, 20, 30
        Assert.Equal(new[] { 4, 1 }, order[3..5]);           // view 1: keys 3, 7
        Assert.Equal(new[] { 6, 2 }, order[5..7]);           // view 2: keys 1, 5
        Assert.Equal(new ulong[] { 10, 20, 30, 3, 7, 1, 5 }, sorted);
    }

    [Fact]
    public void Bucket_AViewWithNothing_HasAnEmptyRange()
    {
        int[] viewOf = { 2, 0, 2 };
        ulong[] keys = { 2, 1, 1 };
        var starts = new int[4];
        var counts = new int[4];
        var sorted = new ulong[3];
        var order = new int[3];

        RenderViewPlan.Bucket(viewOf, keys, starts, counts, sorted, order);

        Assert.Equal(new[] { 1, 0, 2, 0 }, counts);
        Assert.Equal(new[] { 0, 1, 1, 3 }, starts);
        Assert.Equal(new[] { 1, 2, 0 }, order);
    }

    [Fact]
    public void Bucket_KeepsEachPassContiguousWithinAView()
    {
        // The renderer walks a view's range pass by pass on the key's top four bits (06 §3.5).
        ulong Key(ulong pass, ulong rest) => (pass << 60) | rest;
        int[] viewOf = { 1, 0, 1, 0, 1, 0 };
        ulong[] keys = { Key(3, 1), Key(0, 9), Key(0, 4), Key(3, 2), Key(1, 1), Key(0, 1) };
        var starts = new int[2];
        var counts = new int[2];
        var sorted = new ulong[6];
        var order = new int[6];

        RenderViewPlan.Bucket(viewOf, keys, starts, counts, sorted, order);

        Assert.Equal(new ulong[] { 0, 0, 3 }, sorted[0..3].Select(k => k >> 60));
        Assert.Equal(new ulong[] { 0, 1, 3 }, sorted[3..6].Select(k => k >> 60));
    }

    [Fact]
    public void Planning_DoesNotAllocate()   // 02 §4.6: it runs every frame
    {
        var random = new Random(77);
        const int N = 2000, Views = 4;
        var viewOf = new int[N];
        var keys = new ulong[N];
        for (int i = 0; i < N; i++) { viewOf[i] = random.Next(Views); keys[i] = (ulong)random.NextInt64(); }
        int[] targets = { Screen, 0, Screen, 1 };
        int[] orders = { 0, 0, 1, 0 };
        var drawOrder = new int[Views];
        var starts = new int[Views];
        var counts = new int[Views];
        var sorted = new ulong[N];
        var order = new int[N];

        void Plan()
        {
            RenderViewPlan.OrderViews(targets, orders, drawOrder);
            RenderViewPlan.Bucket(viewOf, keys, starts, counts, sorted, order);
        }
        Plan();   // warm up

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) Plan();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(N, counts.Sum());
    }

    [Fact]
    public void Bucket_WithTooSmallOutputs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            RenderViewPlan.Bucket(new[] { 0, 0 }, new ulong[] { 1, 2 }, new int[1], new int[1], new ulong[1], new int[2]));
    }
}
