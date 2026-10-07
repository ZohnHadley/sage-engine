#nullable enable
using System;
using System.Collections.Generic;
using Sage.Editing;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The outliner's labels and the log panel's lines, headless (issue #374, TODO #41): with both open a frame
// allocates what it does with them closed, which is nothing once the text exists. The windows draw these
// caches through ImGuiListClipper (the smoke run checks the client).
[Xunit.Collection(MeasurementsCollection.Name)]
public class DevToolAllocationTests
{
    public DevToolAllocationTests() { _ = TestEnv.UserRoot; }

    [Xunit.Fact]
    public void OutlinerLabelsAreBuiltOnceAndRebuiltOnlyWhenTheNameChanges()
    {
        using var world = new World("outliner");
        var named = world.Create("rock");
        var plain = world.Create();
        var cache = new EntityLabelCache();

        Assert.Equal($"rock ({named.Id})##{named.Id}", cache.Label(named));
        Assert.Equal($"entity {plain.Id}##{plain.Id}", cache.Label(plain));
        Assert.Same(cache.Label(named), cache.Label(named));

        named.Name = "boulder";
        Assert.Equal($"boulder ({named.Id})##{named.Id}", cache.Label(named));
    }

    [Xunit.Fact]
    public void OutlinerAndLogPanelFramesAllocateNothingOnceTheirTextExists()
    {
        using var world = new World("outliner");
        var entities = new List<Entity>();
        for (int i = 0; i < 500; i++) entities.Add(world.Create(i % 2 == 0 ? "chunk" : null));
        var cache = new EntityLabelCache();

        var ring = new RingBufferLogSink(256);
        for (int i = 0; i < 200; i++)
            ring.Write(new LogEntry(DateTime.UtcNow, i, i, i, 1, "test", LogCat.Records, LogLevel.Info, "line " + i, default, default, default, null, 0));
        var view = new LogView(ring);

        int Frame()
        {
            int total = 0;
            cache.Prune(entities);
            foreach (var e in entities) total += cache.Label(e).Length;
            view.Refresh();
            var lines = view.Lines; var texts = view.Texts;
            for (int i = 0; i < lines.Count; i++) total += texts[i].Length;
            return total;
        }
        Frame(); Frame();   // warm up: builds every label and line

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(200, view.Texts.Count);
    }

    [Xunit.Fact]
    public void LogLinesAreFormattedOnceAsTheRingSlides()
    {
        var ring = new RingBufferLogSink(4);
        var view = new LogView(ring);
        void Write(string m) => ring.Write(new LogEntry(DateTime.UtcNow, 0, 0, 0, 1, "test", LogCat.Records, LogLevel.Info, m, default, default, default, null, 0));
        Write("a"); Write("b");
        view.Refresh();
        string first = view.Texts[0];
        Write("c");
        view.Refresh();
        Assert.Same(first, view.Texts[0]);
        Assert.Equal(3, view.Texts.Count);
        Assert.Contains("c", view.Texts[2]);
    }
}
