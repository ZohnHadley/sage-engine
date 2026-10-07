#nullable enable
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Sage.Core;

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
// - Log is static because it must work before the Engine exists and from any thread (01 §4). It
//   writes to the current app's Logger (AppEnvironment.Current, issue #49): two apps in one process
//   have separate sinks, levels and counters; outside any app, the process's own.
public static class Log
{
    // Always a sink, even before Initialize: early and test logs still reach the console and crash reports.
    public static RingBufferLogSink Ring => Logger.Current.Ring;
    public static FileLogSink? File => Logger.Current.File;
    public static StdoutLogSink? Stdout => Logger.Current.Stdout;

    // log_queue_size. When the queue is over capacity, Trace/Debug entries are dropped (and counted);
    // Info and above are always kept, so producers never block.
    public static int QueueCapacity
    {
        get => Logger.Current.QueueCapacity;
        set => Logger.Current.QueueCapacity = value;
    }

    public static long DroppedCount => Logger.Current.DroppedCount;

    // Set by the host once per frame / per fixed tick, so each entry carries both.
    public static void SetFrame(long frame) => Logger.Current.SetFrame(frame);
    public static void SetTick(long tick) => Logger.Current.SetTick(tick);

    public static void Initialize(LogOptions options) => Logger.Current.Initialize(options);

    public static void AddSink(ILogSink sink) => Logger.Current.AddSink(sink);

    public static void RemoveSink(ILogSink sink) => Logger.Current.RemoveSink(sink);

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
        if (cat.IsEnabled(level) && Logger.Current.TryOnce(cat, key))
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
        if (!cat.IsEnabled(level) || !Logger.Current.TryEvery(cat, key, interval, out int suppressed)) return;
        if (suppressed > 0) message += $" (+{suppressed} suppressed)";
        Enqueue(cat, level, message, default, default, default, file, line);
    }

    // ---- Flushing and shutdown ------------------------------------------------------------------

    // Writes everything queued so far on the calling thread and flushes the sinks.
    public static void Flush() => Logger.Current.Flush();

    // Crash paths use a timeout so a stuck sink can't hang the process.
    public static bool Flush(TimeSpan timeout) => Logger.Current.Flush(timeout);

    // Flushes and closes the log file.
    public static void Shutdown() => Logger.Current.Shutdown();

    // ---- Internals ------------------------------------------------------------------------------

    private static void Enqueue(LogCat cat, LogLevel level, string message,
        in LogField f1, in LogField f2, in LogField f3, string? file, int line)
        => Logger.Current.Enqueue(cat, level, message, f1, f2, f3, file, line);

    [DoesNotReturn]
    private static void FatalCore(LogCat cat, string message, in LogField f1, in LogField f2, in LogField f3, string file, int line)
    {
        Enqueue(cat, LogLevel.Fatal, message, f1, f2, f3, file, line);
        Flush(TimeSpan.FromSeconds(2));
        CrashReporter.Write(null, $"Fatal [{cat.Name}]: {message}");
        throw new SageFatalException(message, reported: true);
    }
}

public sealed class SageFatalException : Exception
{
    // True when the crash report was already written (by Log.Fatal), so the unhandled-exception
    // hook doesn't write a second one.
    public bool Reported { get; }

    public SageFatalException(string message, bool reported) : base(message) { Reported = reported; }
}
