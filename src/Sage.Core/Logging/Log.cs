#nullable enable
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace sage_engine;

public sealed class LogOptions
{
    public string? LogDirectory { get; init; }     // null = no file sink
    public int KeepSessions { get; init; } = 10;   // log_keep
    public bool Stdout { get; init; } = true;
}

// The engine log (docs/design/02-core-services-and-logging.md).
//
//   Log.Info(LogCat.Assets, $"Loaded {path} in {ms:F1} ms");
//   Log.Warn(LogCat.Assets, $"Missing texture", new LogField("path", path));
//   Log.Every(LogCat.Physics, LogLevel.Warn, "tunnel", TimeSpan.FromSeconds(5), $"Body {id} tunnelled");
//
// - Trace/Debug calls are removed from Shipping builds entirely ([Conditional("SAGE_DEV")]).
// - A call whose category/level is disabled formats and allocates nothing.
// - Safe from any thread. Producers enqueue; a background writer thread feeds the sinks.
// - Log is static because it must work before the Engine exists and from any thread (01 §4).
public static class Log
{
    private static readonly ConcurrentQueue<LogEntry> Queue = new();
    private static readonly object WriteLock = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static readonly Stopwatch SessionClock = Stopwatch.StartNew();

    private static volatile ILogSink[] _sinks;
    private static Thread? _writer;
    private static volatile bool _running;
    private static int _queued;
    private static long _dropped;

    // Duplicate collapse: consecutive identical messages are written once, then "(repeated N more times)".
    private static LogEntry _last;
    private static bool _hasLast;
    private static int _repeats;
    private static long _lastRepeatTimestamp;

    private static long _frame;
    private static long _tick;

    // Always a sink, even before Initialize: early and test logs still reach the console and crash reports.
    public static RingBufferLogSink Ring { get; } = new(2000);
    public static FileLogSink? File { get; private set; }
    public static StdoutLogSink? Stdout { get; private set; }

    // log_queue_size. When the queue is over capacity, Trace/Debug entries are dropped (and counted);
    // Info and above are always kept, so producers never block.
    public static int QueueCapacity { get; set; } = 16_384;

    public static long DroppedCount => Interlocked.Read(ref _dropped);

    static Log() { _sinks = new ILogSink[] { Ring }; }

    // Set by the host once per frame / per fixed tick, so each entry carries both.
    public static void SetFrame(long frame) => Volatile.Write(ref _frame, frame);
    public static void SetTick(long tick) => Volatile.Write(ref _tick, tick);

    public static void Initialize(LogOptions options)
    {
        lock (WriteLock)
        {
            if (options.Stdout && Stdout == null)
            {
                Stdout = new StdoutLogSink();
                AddSinkLocked(Stdout);
            }

            if (options.LogDirectory != null && File == null)
            {
                File = TryCreateFileSink(options.LogDirectory, options.KeepSessions, out string? fallbackNote);
                if (File != null)
                    AddSinkLocked(File);
                if (fallbackNote != null)
                    Warn(LogCat.Core, $"{fallbackNote}");
            }
        }
        EnsureWriter();
    }

    public static void AddSink(ILogSink sink) { lock (WriteLock) AddSinkLocked(sink); }

    public static void RemoveSink(ILogSink sink)
    {
        lock (WriteLock)
        {
            var list = new System.Collections.Generic.List<ILogSink>(_sinks);
            list.Remove(sink);
            _sinks = list.ToArray();
        }
    }

    // ---- Level methods: interpolated form (preferred) and plain-string form --------------------

    [Conditional("SAGE_DEV")]
    public static void Trace(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler<LogLevels.Trace> message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (message.Enabled) Enqueue(cat, LogLevel.Trace, message.ToStringAndClear(), f1, f2, f3, file, line); }

    [Conditional("SAGE_DEV")]
    public static void Trace(LogCat cat, string message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (cat.IsEnabled(LogLevel.Trace)) Enqueue(cat, LogLevel.Trace, message, f1, f2, f3, file, line); }

    [Conditional("SAGE_DEV")]
    public static void Debug(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler<LogLevels.Debug> message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (message.Enabled) Enqueue(cat, LogLevel.Debug, message.ToStringAndClear(), f1, f2, f3, file, line); }

    [Conditional("SAGE_DEV")]
    public static void Debug(LogCat cat, string message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (cat.IsEnabled(LogLevel.Debug)) Enqueue(cat, LogLevel.Debug, message, f1, f2, f3, file, line); }

    public static void Info(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler<LogLevels.Info> message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (message.Enabled) Enqueue(cat, LogLevel.Info, message.ToStringAndClear(), f1, f2, f3, file, line); }

    public static void Info(LogCat cat, string message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (cat.IsEnabled(LogLevel.Info)) Enqueue(cat, LogLevel.Info, message, f1, f2, f3, file, line); }

    public static void Warn(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler<LogLevels.Warn> message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (message.Enabled) Enqueue(cat, LogLevel.Warn, message.ToStringAndClear(), f1, f2, f3, file, line); }

    public static void Warn(LogCat cat, string message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (cat.IsEnabled(LogLevel.Warn)) Enqueue(cat, LogLevel.Warn, message, f1, f2, f3, file, line); }

    public static void Error(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler<LogLevels.Error> message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (message.Enabled) Enqueue(cat, LogLevel.Error, message.ToStringAndClear(), f1, f2, f3, file, line); }

    public static void Error(LogCat cat, string message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (cat.IsEnabled(LogLevel.Error)) Enqueue(cat, LogLevel.Error, message, f1, f2, f3, file, line); }

    // Fatal: logs, flushes, writes a crash report, then throws SageFatalException (never returns).
    [DoesNotReturn]
    public static void Fatal(LogCat cat, [InterpolatedStringHandlerArgument("cat")] ref LogHandler<LogLevels.Fatal> message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => FatalCore(cat, message.Enabled ? message.ToStringAndClear() : "(fatal)", f1, f2, f3, file, line);

    [DoesNotReturn]
    public static void Fatal(LogCat cat, string message,
        in LogField f1 = default, in LogField f2 = default, in LogField f3 = default,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
        => FatalCore(cat, message, f1, f2, f3, file, line);

    // Level decided at runtime (console, tools).
    public static void Write(LogCat cat, LogLevel level, [InterpolatedStringHandlerArgument("cat", "level")] ref LogDynamicHandler message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (message.Enabled) Enqueue(cat, level, message.ToStringAndClear(), default, default, default, file, line); }

    public static void Write(LogCat cat, LogLevel level, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (cat.IsEnabled(level)) Enqueue(cat, level, message, default, default, default, file, line); }

    // At most once per session for this (category, key).
    public static void Once(LogCat cat, LogLevel level, string key,
        [InterpolatedStringHandlerArgument("cat", "level", "key")] ref LogDynamicHandler message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    { if (message.Enabled) Enqueue(cat, level, message.ToStringAndClear(), default, default, default, file, line); }

    public static void Once(LogCat cat, LogLevel level, string key, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (cat.IsEnabled(level) && LogRateLimiter.TryOnce(cat, key))
            Enqueue(cat, level, message, default, default, default, file, line);
    }

    // At most once per `interval` for this (category, key); reports how many were suppressed.
    public static void Every(LogCat cat, LogLevel level, string key, TimeSpan interval,
        [InterpolatedStringHandlerArgument("cat", "level", "key", "interval")] ref LogDynamicHandler message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!message.Enabled) return;
        string text = message.ToStringAndClear();
        if (message.Suppressed > 0) text += $" (+{message.Suppressed} suppressed)";
        Enqueue(cat, level, text, default, default, default, file, line);
    }

    public static void Every(LogCat cat, LogLevel level, string key, TimeSpan interval, string message,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!cat.IsEnabled(level) || !LogRateLimiter.TryEvery(cat, key, interval, out int suppressed)) return;
        if (suppressed > 0) message += $" (+{suppressed} suppressed)";
        Enqueue(cat, level, message, default, default, default, file, line);
    }

    // ---- Flushing and shutdown ------------------------------------------------------------------

    // Writes everything queued so far on the calling thread and flushes the sinks.
    public static void Flush() => Flush(Timeout.InfiniteTimeSpan);

    // Crash paths use a timeout so a stuck sink can't hang the process.
    public static bool Flush(TimeSpan timeout)
    {
        if (!Monitor.TryEnter(WriteLock, timeout))
            return false;
        try
        {
            DrainLocked();
            EmitPendingRepeatLocked();
            foreach (var sink in _sinks)
                TrySink(sink, s => s.Flush());
            return true;
        }
        finally { Monitor.Exit(WriteLock); }
    }

    public static void Shutdown()
    {
        _running = false;
        Signal.Set();
        _writer?.Join(TimeSpan.FromSeconds(2));
        _writer = null;
        Flush();
        lock (WriteLock)
        {
            File?.Dispose();
            File = null;
        }
    }

    // ---- Internals ------------------------------------------------------------------------------

    private static void Enqueue(LogCat cat, LogLevel level, string message,
        in LogField f1, in LogField f2, in LogField f3, string? file, int line)
    {
        if (Interlocked.Increment(ref _queued) > QueueCapacity && level < LogLevel.Info)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }

        var thread = Thread.CurrentThread;
        Queue.Enqueue(new LogEntry(
            DateTime.UtcNow, SessionClock.Elapsed.TotalSeconds,
            Volatile.Read(ref _frame), Volatile.Read(ref _tick),
            thread.ManagedThreadId, thread.Name,
            cat, level, message, f1, f2, f3, string.IsNullOrEmpty(file) ? null : file, line));

        EnsureWriter();
        Signal.Set();
    }

    [DoesNotReturn]
    private static void FatalCore(LogCat cat, string message, in LogField f1, in LogField f2, in LogField f3, string file, int line)
    {
        Enqueue(cat, LogLevel.Fatal, message, f1, f2, f3, file, line);
        Flush(TimeSpan.FromSeconds(2));
        CrashReporter.Write(null, $"Fatal [{cat.Name}]: {message}");
        throw new SageFatalException(message, reported: true);
    }

    private static void EnsureWriter()
    {
        if (_running) return;
        lock (WriteLock)
        {
            if (_running) return;
            _running = true;
            _writer = new Thread(WriterLoop) { IsBackground = true, Name = "log-writer" };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        while (_running)
        {
            Signal.WaitOne(250);
            lock (WriteLock)
            {
                DrainLocked();
                // A repeat run that has gone quiet for a second is reported now rather than at the next message.
                if (_repeats > 0 && Stopwatch.GetElapsedTime(_lastRepeatTimestamp) > TimeSpan.FromSeconds(1))
                    EmitPendingRepeatLocked();
                foreach (var sink in _sinks)
                    TrySink(sink, s => s.Flush());
            }
        }
    }

    private static void DrainLocked()
    {
        while (Queue.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _queued);
            DispatchLocked(entry);
        }

        long dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            DispatchLocked(Synthetic(LogCat.Core, LogLevel.Warn, $"{dropped} log entries dropped (log queue full; raise log_queue_size)"));
    }

    private static void DispatchLocked(in LogEntry entry)
    {
        if (_hasLast && entry.Level == _last.Level && ReferenceEquals(entry.Category, _last.Category)
            && entry.Message == _last.Message)
        {
            _repeats++;
            _lastRepeatTimestamp = Stopwatch.GetTimestamp();
            return;
        }

        EmitPendingRepeatLocked();
        _last = entry;
        _hasLast = true;
        WriteToSinksLocked(entry);
    }

    private static void EmitPendingRepeatLocked()
    {
        if (_repeats == 0) return;
        int n = _repeats;
        _repeats = 0;
        WriteToSinksLocked(Synthetic(_last.Category, _last.Level, $"(previous message repeated {n} more time{(n == 1 ? "" : "s")})"));
    }

    private static void WriteToSinksLocked(in LogEntry entry)
    {
        foreach (var sink in _sinks)
        {
            if (entry.Level < sink.MinLevel) continue;
            try { sink.Write(entry); }
            catch (Exception ex) { RemoveBrokenSinkLocked(sink, ex); }
        }
    }

    private static void TrySink(ILogSink sink, Action<ILogSink> action)
    {
        try { action(sink); }
        catch (Exception ex) { RemoveBrokenSinkLocked(sink, ex); }
    }

    // A sink that throws is removed and reported through the remaining sinks (02 §8).
    private static void RemoveBrokenSinkLocked(ILogSink sink, Exception ex)
    {
        var list = new System.Collections.Generic.List<ILogSink>(_sinks);
        if (!list.Remove(sink)) return;
        _sinks = list.ToArray();
        var report = Synthetic(LogCat.Core, LogLevel.Error, $"Log sink {sink.GetType().Name} failed and was removed: {ex.Message}");
        foreach (var s in _sinks)
        {
            try { s.Write(report); } catch { /* reported once is enough */ }
        }
    }

    private static void AddSinkLocked(ILogSink sink)
    {
        var list = new System.Collections.Generic.List<ILogSink>(_sinks);
        if (!list.Contains(sink)) list.Add(sink);
        _sinks = list.ToArray();
    }

    private static LogEntry Synthetic(LogCat cat, LogLevel level, string message)
    {
        var thread = Thread.CurrentThread;
        return new LogEntry(DateTime.UtcNow, SessionClock.Elapsed.TotalSeconds,
            Volatile.Read(ref _frame), Volatile.Read(ref _tick), thread.ManagedThreadId, thread.Name,
            cat, level, message, default, default, default, null, 0);
    }

    // If the file can't be opened (read-only folder), fall back to %LOCALAPPDATA%/Sage/logs,
    // then to no file at all (02 §8).
    private static FileLogSink? TryCreateFileSink(string directory, int keep, out string? note)
    {
        note = null;
        try { return new FileLogSink(directory, keep); }
        catch (Exception first)
        {
            string fallback = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sage", "logs");
            try
            {
                note = $"Couldn't open the log folder {directory} ({first.Message}); logging to {fallback} instead";
                return new FileLogSink(fallback, keep);
            }
            catch (Exception second)
            {
                note = $"Couldn't open a log file ({first.Message}; fallback: {second.Message}); logging to stdout only";
                return null;
            }
        }
    }
}

public sealed class SageFatalException : Exception
{
    // True when the crash report was already written (by Log.Fatal), so the unhandled-exception
    // hook doesn't write a second one.
    public bool Reported { get; }

    public SageFatalException(string message, bool reported) : base(message) { Reported = reported; }
}
