#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

public class LoggingTests
{
    public LoggingTests() { _ = TestEnv.UserRoot; }

    [Fact]
    public void DisabledLevel_FormatsNothing_EnabledLevel_Writes()
    {
        var cat = new LogCat("TestGate" + Guid.NewGuid().ToString("N"));
        cat.MinLevel = LogLevel.Warn;
        var probe = new FormatProbe();
        using var sink = new CaptureSink();

        Log.Info(cat, $"hidden {probe}");
        Assert.Equal(0, probe.Count);

        Log.Warn(cat, $"shown {probe}");
        Assert.Equal(1, probe.Count);
        Assert.Contains(sink.Entries, e => e.Category == cat && e.Message == "shown probe" && e.Level == LogLevel.Warn);
        Assert.DoesNotContain(sink.Entries, e => e.Category == cat && e.Message.StartsWith("hidden"));
    }

    [Fact]
    public void TraceAndDebug_ExistInDevBuilds()
    {
        Assert.True(BuildInfo.IsDevBuild, "tests are expected to run in a dev build (Debug/Development)");
        var cat = new LogCat("TestDev" + Guid.NewGuid().ToString("N")) { MinLevel = LogLevel.Trace };
        using var sink = new CaptureSink();

        Log.Trace(cat, "a trace");
        Log.Debug(cat, $"a debug {42}");

        Assert.Contains(sink.Entries, e => e.Category == cat && e.Level == LogLevel.Trace && e.Message == "a trace");
        Assert.Contains(sink.Entries, e => e.Category == cat && e.Level == LogLevel.Debug && e.Message == "a debug 42");
    }

    [Fact]
    public void Entry_CarriesFieldsCallerAndFrame()
    {
        var cat = new LogCat("TestFields" + Guid.NewGuid().ToString("N"));
        using var sink = new CaptureSink();
        Log.SetFrame(1234);
        string msg = TestEnv.Unique("missing texture");

        Log.Warn(cat, msg, new LogField("path", "a.png"), new LogField("mount", "game"));

        var e = Assert.Single(sink.Entries, x => x.Category == cat);
        Assert.Equal(1234, e.Frame);
        Assert.EndsWith("LoggingTests.cs", e.File);
        string line = LogFormatter.Format(e);
        Assert.Contains("WARN ", line);
        Assert.Contains(cat.Name, line);
        Assert.Contains("(path=a.png, mount=game)", line);
        Assert.Contains("(LoggingTests.cs:", line);
    }

    [Fact]
    public void DefaultLevel_AppliesToCategoriesThatWereNotSetExplicitly()
    {
        var before = LogCat.DefaultLevel;
        try
        {
            var follows = new LogCat("TestFollows" + Guid.NewGuid().ToString("N"));
            var pinned = new LogCat("TestPinned" + Guid.NewGuid().ToString("N")) { MinLevel = LogLevel.Error };

            LogCat.DefaultLevel = LogLevel.Debug;
            Assert.True(follows.IsEnabled(LogLevel.Debug));
            Assert.False(pinned.IsEnabled(LogLevel.Warn));

            pinned.ResetToDefault();
            Assert.True(pinned.IsEnabled(LogLevel.Debug));
            Assert.False(pinned.IsOverridden);
        }
        finally { LogCat.DefaultLevel = before; }
    }

    [Fact]
    public void Once_LogsOnce_Every_ReportsSuppressedCount()
    {
        var cat = new LogCat("TestRate" + Guid.NewGuid().ToString("N"));
        using var sink = new CaptureSink();

        for (int i = 0; i < 3; i++)
            Log.Once(cat, LogLevel.Warn, "k", $"once {i}");
        Assert.Single(sink.Entries, e => e.Category == cat && e.Message.StartsWith("once"));

        var interval = TimeSpan.FromMilliseconds(200);
        Log.Every(cat, LogLevel.Warn, "e", interval, $"every a");
        Log.Every(cat, LogLevel.Warn, "e", interval, $"every b");   // suppressed
        Thread.Sleep(300);
        Log.Every(cat, LogLevel.Warn, "e", interval, $"every c");

        var every = sink.Entries.Where(e => e.Category == cat && e.Message.StartsWith("every")).ToList();
        Assert.Equal(2, every.Count);
        Assert.Equal("every a", every[0].Message);
        Assert.Equal("every c (+1 suppressed)", every[1].Message);
    }

    [Fact]
    public void ConsecutiveDuplicates_AreCollapsed()
    {
        var cat = new LogCat("TestDup" + Guid.NewGuid().ToString("N"));
        using var sink = new CaptureSink();
        string msg = TestEnv.Unique("same thing");

        for (int i = 0; i < 5; i++) Log.Warn(cat, msg);
        Log.Warn(cat, TestEnv.Unique("different"));

        var mine = sink.Entries.Where(e => e.Category == cat).Select(e => e.Message).ToList();
        Assert.Equal(msg, mine[0]);
        Assert.Equal("(previous message repeated 4 more times)", mine[1]);
        Assert.StartsWith("different", mine[2]);
        Assert.Equal(3, mine.Count);
    }

    [Fact]
    public void RingBuffer_KeepsNewestEntriesInOrder()
    {
        var ring = new RingBufferLogSink(3);
        var cat = new LogCat("TestRing" + Guid.NewGuid().ToString("N"));
        for (int i = 0; i < 5; i++)
            ring.Write(new LogEntry(DateTime.UtcNow, 0, 0, 0, 1, "t", cat, LogLevel.Info, $"m{i}", default, default, default, null, 0));

        var list = new System.Collections.Generic.List<LogEntry>();
        ring.Snapshot(list);
        Assert.Equal(new[] { "m2", "m3", "m4" }, list.Select(e => e.Message));

        long v = ring.Version;
        ring.Clear();
        Assert.NotEqual(v, ring.Version);
        ring.Snapshot(list);
        Assert.Empty(list);
    }

    [Fact]
    public void FileSink_WritesLines_AndKeepsNewestSessions()
    {
        string dir = TestEnv.NewTempDir();
        for (int i = 0; i < 5; i++)
            File.WriteAllText(Path.Combine(dir, $"sage-2000010{i}-000000.log"), "old");

        using (var sink = new FileLogSink(dir, keep: 3))
        {
            var cat = new LogCat("TestFile" + Guid.NewGuid().ToString("N"));
            sink.Write(new LogEntry(DateTime.UtcNow, 1.5, 7, 0, 1, "main", cat, LogLevel.Error, "boom", default, default, default, "X.cs", 3));
            sink.Flush();

            var files = Directory.GetFiles(dir, "sage-*.log").Select(Path.GetFileName).OrderBy(n => n).ToList();
            Assert.Equal(3, files.Count);   // the current file + the 2 newest old ones
            Assert.Contains("sage-20000104-000000.log", files);
            Assert.Contains("sage-20000103-000000.log", files);
            Assert.Contains(Path.GetFileName(sink.CurrentPath), files);
        }

        string text = File.ReadAllText(Directory.GetFiles(dir, "sage-*.log").OrderBy(n => n).Last());
        Assert.Contains("ERROR", text);
        Assert.Contains("boom", text);
        Assert.Contains("f=7", text);
        Assert.Contains("(X.cs:3)", text);
    }
}
