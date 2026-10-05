#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Issue #300: the profiler's Chrome trace (trace_start / trace_dump) and the frame's work counters
// (WorkStats) that `stat render` and `stat assets` show. A capture is process-wide, so these run alone.
[Collection(ProcessWideStateCollection.Name)]
public class ProfilerTraceTests
{
    public ProfilerTraceTests() { _ = TestEnv.UserRoot; }

    // What Perfetto and chrome://tracing need of a JSON trace (the Trace Event Format's "JSON Object
    // Format"), checked the way they read it: an object with a traceEvents array; every event a name, a
    // phase, a pid and a tid; a complete event ("X") a start and a duration in microseconds, never negative;
    // an instant event ("i") its scope; every thread with a slice named by a thread_name metadata event; and
    // on each thread the slices nest — a child inside its parent, never straddling its end, or the viewer
    // draws them on the wrong rows. Returns the events.
    internal static List<JsonElement> ValidateChromeTrace(JsonDocument doc)
    {
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.True(root.TryGetProperty("traceEvents", out var array), "no traceEvents array");
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        Assert.Equal("ms", root.GetProperty("displayTimeUnit").GetString());

        var events = array.EnumerateArray().ToList();
        var named = new HashSet<long>();
        var slices = new Dictionary<long, List<(double Start, double End, string Name)>>();
        foreach (var e in events)
        {
            Assert.Equal(JsonValueKind.String, e.GetProperty("name").ValueKind);
            string ph = e.GetProperty("ph").GetString()!;
            Assert.Contains(ph, new[] { "M", "X", "i" });
            Assert.Equal(JsonValueKind.Number, e.GetProperty("pid").ValueKind);
            long tid = e.GetProperty("tid").GetInt64();
            switch (ph)
            {
                case "M":
                    Assert.Equal(JsonValueKind.Object, e.GetProperty("args").ValueKind);
                    if (e.GetProperty("name").GetString() == "thread_name")
                    {
                        Assert.False(string.IsNullOrEmpty(e.GetProperty("args").GetProperty("name").GetString()));
                        named.Add(tid);
                    }
                    break;
                case "X":
                    double ts = e.GetProperty("ts").GetDouble(), dur = e.GetProperty("dur").GetDouble();
                    Assert.True(ts >= 0, $"negative ts {ts}");
                    Assert.True(dur >= 0, $"negative dur {dur}");
                    if (!slices.TryGetValue(tid, out var list)) slices[tid] = list = new();
                    list.Add((ts, ts + dur, e.GetProperty("name").GetString()!));
                    break;
                case "i":
                    Assert.True(e.GetProperty("ts").GetDouble() >= 0);
                    Assert.Contains(e.GetProperty("s").GetString(), new[] { "t", "p", "g" });
                    break;
            }
        }

        const double Slack = 0.01;   // microseconds: the times are rounded to a nanosecond
        foreach (var (tid, list) in slices)
        {
            Assert.True(named.Contains(tid), $"thread {tid} has slices but no thread_name");
            var open = new Stack<(double Start, double End, string Name)>();
            foreach (var slice in list.OrderBy(s => s.Start).ThenByDescending(s => s.End))
            {
                while (open.Count > 0 && open.Peek().End <= slice.Start + Slack) open.Pop();
                if (open.Count > 0)
                    Assert.True(slice.End <= open.Peek().End + Slack,
                        $"on thread {tid}, '{slice.Name}' straddles the end of '{open.Peek().Name}'");
                open.Push(slice);
            }
        }
        return events;
    }

    [Fact]
    public void TraceDumpWritesAChromeTraceOfEveryThreadThatPerfettoOpens()
    {
        Assert.True(Profiler.Enabled);   // a dev build
        using var app = HeadlessApp.Simulation().Boot("traced");
        var world = app.World;
        string file = Path.Combine(TestEnv.NewTempDir(), "trace.json");

        Assert.True(app.CVars.Execute("trace_start"));
        Assert.True(Profiler.Capturing);
        for (int i = 0; i < 5; i++)
        {
            world.RunFixed(1f / 60f);
            Profiler.EndFrame();
        }
        // Work on another thread, the way a terrain job runs: its own track.
        int worker = 0;
        var thread = new Thread(() =>
        {
            worker = Environment.CurrentManagedThreadId;
            using (Profiler.Begin("Job.Test"))
                Thread.Sleep(2);
        }) { Name = "trace test worker" };
        thread.Start();
        thread.Join();
        Assert.True(app.CVars.Execute($"trace_dump {file}"));
        Assert.False(Profiler.Capturing);   // dumping ends the capture

        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var events = ValidateChromeTrace(doc);

        var slices = events.Where(e => e.GetProperty("ph").GetString() == "X").ToList();
        int main = Environment.CurrentManagedThreadId;
        string Name(JsonElement e) => e.GetProperty("name").GetString()!;
        long Tid(JsonElement e) => e.GetProperty("tid").GetInt64();
        Assert.Contains(slices, e => Name(e).StartsWith("Fixed.", StringComparison.Ordinal) && Tid(e) == main);   // phases
        Assert.Contains(slices, e => Name(e).Contains('/') && Tid(e) == main);                                   // systems
        Assert.Contains(slices, e => Name(e) == "Job.Test" && Tid(e) == worker && worker != main);
        Assert.True(events.Count(e => e.GetProperty("ph").GetString() == "i" && Name(e) == "EndFrame") >= 5);
        Assert.Contains(events, e => Name(e) == "thread_name" && Tid(e) == main
                                     && e.GetProperty("args").GetProperty("name").GetString() == "main");
        Assert.Contains(events, e => Name(e) == "thread_name" && Tid(e) == worker
                                     && e.GetProperty("args").GetProperty("name").GetString()!.StartsWith("trace test worker"));
        Assert.Equal(0, doc.RootElement.GetProperty("otherData").GetProperty("dropped").GetInt32());
    }

    [Fact]
    public void ABareFileNameGoesInTheLogsFolder()
    {
        string name = $"trace-{Guid.NewGuid():N}.json";
        using var app = HeadlessApp.Simulation().Boot("traced");
        Assert.True(app.CVars.Execute("trace_start 1000"));
        app.World.RunFixed(1f / 60f);
        Assert.True(app.CVars.Execute($"trace_dump {name}"));

        string path = Path.Combine(UserPaths.Logs, name);
        Assert.True(File.Exists(path), path);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.NotEmpty(ValidateChromeTrace(doc));
    }

    [Fact]
    public void AFullBufferDropsTheRestAndSaysHowMany()
    {
        Assert.True(Profiler.StartCapture(16));
        for (int i = 0; i < 40; i++)
            using (Profiler.Begin("Test.Full")) { }
        Profiler.StopCapture();

        Assert.Equal(16, Profiler.CapturedEvents);
        Assert.True(Profiler.DroppedEvents >= 24);
        using var stream = new MemoryStream();
        Assert.Equal(16, Profiler.WriteTrace(stream));
        using var doc = JsonDocument.Parse(stream.ToArray());
        ValidateChromeTrace(doc);
        Assert.True(doc.RootElement.GetProperty("otherData").GetProperty("dropped").GetInt32() >= 24);
    }

    // A capture writes structs into an array it made up front: a scope allocates nothing more for being
    // traced (the thread's name, once per capture, is the exception, and is taken before measuring).
    [Fact]
    public void TracingAScopeAllocatesNothing()
    {
        Assert.True(Profiler.StartCapture(100_000));
        try
        {
            using (Profiler.Begin("Test.Traced")) { }   // the name's entry, and this thread's name, made here
            var report = AllocationProbe.Measure(50, () =>
            {
                using (Profiler.Begin("Test.Traced")) { }
                Profiler.TraceSpan("Test.Span", 1, 2);
            });
            Assert.Equal(0, report.Bytes);
        }
        finally { Profiler.StopCapture(); }
    }

    [Fact]
    public void WithNothingCapturedTraceDumpSaysSoAndWritesNothing()
    {
        using var app = HeadlessApp.Simulation().Boot("traced");
        Profiler.StopCapture();
        if (Profiler.CapturedEvents > 0) return;   // an earlier capture in this process is still there to dump
        using var sink = new CaptureSink();
        app.CVars.Execute("trace_dump");
        Assert.Contains(sink.Entries, e => e.Message.Contains("nothing captured"));
    }

    // `stat render`'s jobs and `stat assets`' loads: a sector generated ahead on the thread pool is a job
    // and a load, counted when it is queued and when it is done, and a frame's numbers are what changed in it.
    [Fact]
    public void ATerrainJobIsCountedAsAJobAndALoadUntilItIsDone()
    {
        WorkStats.EndFrame();
        long jobs = WorkStats.JobsStarted, finished = WorkStats.JobsFinished, loads = WorkStats.LoadsFinished;

        var terrain = new Terrain
        {
            Generator = BuiltInTerrain.Create(new TerrainRecord { Generator = TerrainGeneratorKind.Hills, Seed = 3, Amplitude = 20, Wavelength = 100 }),
        };
        terrain.Prefetch(new SectorCoord(5, 5));
        Assert.Equal(jobs + 1, WorkStats.JobsStarted);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (WorkStats.JobsFinished < finished + 1 && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.Equal(finished + 1, WorkStats.JobsFinished);
        Assert.Equal(loads + 1, WorkStats.LoadsFinished);

        WorkStats.EndFrame();
        var frame = WorkStats.LastFrame;
        Assert.Equal(1, frame.JobsStarted);
        Assert.Equal(1, frame.JobsFinished);
        Assert.Equal(1, frame.LoadsFinished);
        Assert.True(frame.LoadMs > 0);

        // Taking it is not another load: it was generated already.
        terrain.Load(new SectorCoord(5, 5));
        WorkStats.EndFrame();
        Assert.Equal(0, WorkStats.LastFrame.LoadsFinished);
        Assert.Equal(0, WorkStats.LastFrame.JobsStarted);
    }

    [Fact]
    public void UploadsAreCountedInTheFrameTheyHappen()
    {
        WorkStats.EndFrame();
        WorkStats.Uploaded(1024);
        WorkStats.Uploaded(4096);
        WorkStats.EndFrame();
        Assert.Equal(2, WorkStats.LastFrame.Uploads);
        Assert.Equal(5120, WorkStats.LastFrame.UploadBytes);
        WorkStats.EndFrame();
        Assert.Equal(0, WorkStats.LastFrame.Uploads);
    }
}
