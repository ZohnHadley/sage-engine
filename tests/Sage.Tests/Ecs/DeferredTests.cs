#nullable enable
using System.Collections.Generic;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Deferred work (docs/design/03 §3.5, TODO R14). The point of the type is not the list — it is that
// there is no `Clear()` to forget, because forgetting one replays last tick's work and that looks
// like a gameplay bug rather than a bookkeeping one.
public class DeferredTests
{
    public DeferredTests() { _ = TestEnv.UserRoot; }

    private static List<T> Read<T>(Deferred<T> deferred)
    {
        var got = new List<T>();
        foreach (var item in deferred.Drain()) got.Add(item);
        return got;
    }

    [Xunit.Fact]
    public void DrainingHandsOverWhatWasQueued()
    {
        var work = new Deferred<int>();
        work.Add(1);
        work.Add(2);

        Assert.Equal(2, work.Count);
        Assert.Equal(new[] { 1, 2 }, Read(work));
    }

    // The whole reason the type exists: a second tick starts empty whether or not anyone cleared it.
    [Xunit.Fact]
    public void WorkIsNeverRunTwice()
    {
        var work = new Deferred<int>();
        work.Add(1);
        Assert.Single(Read(work));

        Assert.True(work.IsEmpty);
        Assert.Empty(Read(work));
    }

    // A hit that kills something queues a death; that must land on the next drain, not appear under
    // the loop that is running — which is the difference between "runs later" and "runs forever".
    [Xunit.Fact]
    public void WorkAddedWhileDrainingWaitsForTheNextDrain()
    {
        var work = new Deferred<int>();
        work.Add(1);

        var seen = new List<int>();
        foreach (var item in work.Drain())
        {
            seen.Add(item);
            if (item == 1) work.Add(2);
        }

        Assert.Equal(new[] { 1 }, seen);
        Assert.Equal(new[] { 2 }, Read(work));
    }

    [Xunit.Fact]
    public void DrainingAnEmptyQueueIsNothing()
    {
        var work = new Deferred<string>();
        Assert.True(work.IsEmpty);
        Assert.Empty(Read(work));
    }

    // Two drains in a row must not hand back the first one's items: the buffer is reused between
    // drains, so this is the mistake the reuse could hide.
    [Xunit.Fact]
    public void ASecondDrainSeesOnlyWhatWasQueuedAfterTheFirst()
    {
        var work = new Deferred<int>();
        work.Add(1);
        work.Add(2);
        Assert.Equal(new[] { 1, 2 }, Read(work));

        work.Add(3);
        Assert.Equal(new[] { 3 }, Read(work));
    }
}
