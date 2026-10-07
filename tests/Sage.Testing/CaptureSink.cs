#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Testing;

// Everything a log says while the sink lives, at every level (issue #49: each app has its own log).
//
//   new CaptureSink()        made while an app is current (a test makes one after creating its app):
//                            that app's log. Made outside any app: a log of the test's own, current
//                            for the rest of the test, so nothing another test logs lands here, and
//                            every app the test creates afterwards logs into it too.
//   new CaptureSink(logger)  that log, and the logs made under it (app.Environment.Logger).
public sealed class CaptureSink : ILogSink, IDisposable
{
    private readonly List<LogEntry> _entries = new();
    private readonly Logger _logger;
    private readonly AppEnvironment? _own;
    private readonly AppEnvironment.Scope _scope;

    public CaptureSink()
    {
        var current = AppEnvironment.Current;
        if (current.IsProcess)
        {
            _own = AppEnvironment.CreateChild(current);
            _scope = _own.Enter();
            _logger = _own.Logger;
        }
        else _logger = current.Logger;
        _logger.AddSink(this);
    }

    public CaptureSink(Logger logger)
    {
        _logger = logger;
        _logger.AddSink(this);
    }

    public LogLevel MinLevel => LogLevel.Trace;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            _logger.Flush();
            lock (_entries) return _entries.ToArray();
        }
    }

    public void Write(in LogEntry entry) { lock (_entries) _entries.Add(entry); }
    public void Flush() { }

    public void Dispose()
    {
        _logger.Flush();
        _logger.RemoveSink(this);
        _scope.Dispose();
        _own?.Dispose();
    }
}
