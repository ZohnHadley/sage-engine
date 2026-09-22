#nullable enable
using System.Collections.Generic;
using sage_engine;

namespace sage_engine.Tests;

// Log, LogCat defaults, UserPaths and the crash reporter are process-wide, so core tests share one
// temporary user folder and capture log output with a sink. Parallelization is disabled for the
// whole test assembly (EntityContextTests.cs).
internal static class TestEnv
{
    private static readonly object Lock = new();
    private static string? _root;

    public static string UserRoot
    {
        get
        {
            lock (Lock)
            {
                if (_root == null)
                {
                    _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sage-tests-" + System.Guid.NewGuid().ToString("N"));
                    UserPaths.Initialize("tests", _root);
                }
                return _root;
            }
        }
    }

    public static string NewTempDir()
    {
        string dir = System.IO.Path.Combine(UserRoot, "tmp-" + System.Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    // Messages must be unique per test: the log collapses consecutive identical messages globally.
    public static string Unique(string text) => $"{text} [{System.Guid.NewGuid():N}]";
}

internal sealed class CaptureSink : ILogSink, System.IDisposable
{
    private readonly List<LogEntry> _entries = new();

    public CaptureSink() { Log.AddSink(this); }

    public LogLevel MinLevel => LogLevel.Trace;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            Log.Flush();
            lock (_entries) return _entries.ToArray();
        }
    }

    public void Write(in LogEntry entry) { lock (_entries) _entries.Add(entry); }
    public void Flush() { }
    public void Dispose() { Log.Flush(); Log.RemoveSink(this); }
}

// Counts how often it is formatted, to prove disabled log calls format nothing.
internal sealed class FormatProbe
{
    public int Count;
    public override string ToString() { Count++; return "probe"; }
}
