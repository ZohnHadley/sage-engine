#nullable enable
using System;
using System.Collections.Generic;

namespace sage_engine.Testing;

// Everything the log says while it lives, at every level. The log is process-wide, so a test that
// runs beside others sees their lines too: look for your own (TestEnv.Unique helps).
public sealed class CaptureSink : ILogSink, IDisposable
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
