#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Sage.Core;

// One app's log (issue #49): its queue, sinks, category levels, rate limits, duplicate collapse and
// frame/tick counters. The static `Log` writes to the current app's (AppEnvironment.Current), so call
// sites stay `Log.Info(LogCat.X, ...)` while two apps in one process (an editor and the game it plays)
// keep separate logs.
//
// A logger made with a parent starts with the parent's levels and also hands every line it writes to
// the parent's sinks: the editor's log file and console see its play session's lines, while a sink
// added to the play session sees only that session's.
public sealed class Logger : IDisposable
{
    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private readonly object _writeLock = new();
    private readonly Stopwatch _sessionClock = Stopwatch.StartNew();
    private readonly Logger? _parent;
    private readonly List<WeakReference<Logger>> _children = new();   // under _writeLock

    private volatile ILogSink[] _sinks;
    private volatile bool _disposed;
    private int _queued;
    private long _dropped;

    // Duplicate collapse: consecutive identical messages are written once, then "(repeated N more times)".
    private LogEntry _last;
    private bool _hasLast;
    private int _repeats;
    private long _lastRepeatTimestamp;

    private long _frame;
    private long _tick;

    // Category levels, by LogCat.Index: a category past the end, or not set, follows the default.
    private readonly object _levelLock = new();
    private volatile int[] _levels = Array.Empty<int>();
    private bool[] _set = Array.Empty<bool>();
    private volatile int _defaultLevel = (int)LogLevel.Info;

    // Log.Once / Log.Every keys.
    private readonly ConcurrentDictionary<(LogCat, string), byte> _onceKeys = new();
    private readonly ConcurrentDictionary<(LogCat, string), EveryState> _everyKeys = new();
    private sealed class EveryState { public long LastTimestamp = long.MinValue; public int Suppressed; }

    public Logger(Logger? parent = null)
    {
        _parent = parent;
        _sinks = new ILogSink[] { Ring };
        if (parent != null)
        {
            lock (parent._levelLock)
            {
                _defaultLevel = parent._defaultLevel;
                _levels = (int[])parent._levels.Clone();
                _set = (bool[])parent._set.Clone();
                QueueCapacity = parent.QueueCapacity;
            }
            lock (parent._writeLock) parent._children.Add(new WeakReference<Logger>(this));
        }
        LogWriter.Register(this);
    }

    internal Logger? Parent => _parent;

    // The current app's (AppEnvironment.Current): what the static Log writes to.
    public static Logger Current => AppEnvironment.Current.Logger;

    // Always a sink: the console's scroll-back and the tail of a crash report.
    public RingBufferLogSink Ring { get; } = new(2000);
    public FileLogSink? File { get; private set; }
    public StdoutLogSink? Stdout { get; private set; }

    // log_queue_size. When the queue is over capacity, Trace/Debug entries are dropped (and counted);
    // Info and above are always kept, so producers never block.
    public int QueueCapacity { get; set; } = 16_384;

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void SetFrame(long frame) => Volatile.Write(ref _frame, frame);
    public void SetTick(long tick) => Volatile.Write(ref _tick, tick);

    public void Initialize(LogOptions options)
    {
        string? note = null;
        lock (_writeLock)
        {
            if (options.Stdout && Stdout == null)
            {
                Stdout = new StdoutLogSink();
                AddSinkLocked(Stdout);
            }

            if (options.LogDirectory != null && File == null)
            {
                File = TryCreateFileSink(options.LogDirectory, options.KeepSessions, out note);
                if (File != null)
                    AddSinkLocked(File);
            }
        }
        if (note != null)
            Enqueue(LogCat.Core, LogLevel.Warn, note, default, default, default, null, 0);
    }

    public void AddSink(ILogSink sink) { lock (_writeLock) AddSinkLocked(sink); }

    public void RemoveSink(ILogSink sink)
    {
        lock (_writeLock)
        {
            var list = new List<ILogSink>(_sinks);
            list.Remove(sink);
            _sinks = list.ToArray();
        }
    }

    // ---- Levels -----------------------------------------------------------------------------------

    public bool IsEnabled(LogCat cat, LogLevel level)
    {
        int[] levels = _levels;
        int index = cat.Index;
        return (int)level >= (index < levels.Length ? levels[index] : _defaultLevel);
    }

    public LogLevel GetLevel(LogCat cat)
    {
        int[] levels = _levels;
        return (LogLevel)(cat.Index < levels.Length ? levels[cat.Index] : _defaultLevel);
    }

    public void SetLevel(LogCat cat, LogLevel level)
    {
        lock (_levelLock)
        {
            Grow(cat.Index);
            _set[cat.Index] = true;
            var levels = (int[])_levels.Clone();
            levels[cat.Index] = (int)level;
            _levels = levels;
        }
    }

    // True when the category's level was set (log_level) rather than following the default.
    public bool IsOverridden(LogCat cat)
    {
        lock (_levelLock) return cat.Index < _set.Length && _set[cat.Index];
    }

    // Makes the category follow the default level again.
    public void ResetLevel(LogCat cat)
    {
        lock (_levelLock)
        {
            if (cat.Index >= _set.Length) return;
            _set[cat.Index] = false;
            var levels = (int[])_levels.Clone();
            levels[cat.Index] = _defaultLevel;
            _levels = levels;
        }
    }

    // The level of every category not set explicitly (the `developer` cvar switches it).
    public LogLevel DefaultLevel
    {
        get => (LogLevel)_defaultLevel;
        set
        {
            lock (_levelLock)
            {
                _defaultLevel = (int)value;
                var levels = (int[])_levels.Clone();
                for (int i = 0; i < levels.Length; i++)
                    if (!_set[i]) levels[i] = (int)value;
                _levels = levels;
            }
        }
    }

    private void Grow(int index)
    {
        if (index < _set.Length) return;
        int size = Math.Max(index + 1, Math.Max(16, _set.Length * 2));
        var levels = new int[size];
        var set = new bool[size];
        Array.Copy(_levels, levels, _levels.Length);
        Array.Copy(_set, set, _set.Length);
        for (int i = _levels.Length; i < size; i++) levels[i] = _defaultLevel;
        _set = set;
        _levels = levels;
    }

    // ---- Spam control (Log.Once / Log.Every, docs/design/02 §4) -----------------------------------

    internal bool TryOnce(LogCat cat, string key) => _onceKeys.TryAdd((cat, key), 0);

    internal bool TryEvery(LogCat cat, string key, TimeSpan interval, out int suppressedSinceLast)
    {
        long now = Stopwatch.GetTimestamp();
        var state = _everyKeys.GetOrAdd((cat, key), _ => new EveryState());
        lock (state)
        {
            if (state.LastTimestamp != long.MinValue &&
                Stopwatch.GetElapsedTime(state.LastTimestamp, now) < interval)
            {
                state.Suppressed++;
                suppressedSinceLast = 0;
                return false;
            }
            suppressedSinceLast = state.Suppressed;
            state.Suppressed = 0;
            state.LastTimestamp = now;
            return true;
        }
    }

    // ---- Writing ----------------------------------------------------------------------------------

    internal void Enqueue(LogCat cat, LogLevel level, string message,
        in LogField f1, in LogField f2, in LogField f3, string? file, int line)
    {
        if (Interlocked.Increment(ref _queued) > QueueCapacity && level < LogLevel.Info)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }

        var thread = Thread.CurrentThread;
        _queue.Enqueue(new LogEntry(
            DateTime.UtcNow, _sessionClock.Elapsed.TotalSeconds,
            Volatile.Read(ref _frame), Volatile.Read(ref _tick),
            thread.ManagedThreadId, thread.Name,
            cat, level, message, f1, f2, f3, string.IsNullOrEmpty(file) ? null : file, line));

        if (_disposed) Flush();   // nothing drains a disposed logger: write it now
        else LogWriter.Wake();
    }

    // Writes everything queued so far on the calling thread and flushes the sinks; a child's lines first,
    // so what it hands this logger's sinks is there too.
    public void Flush() => Flush(Timeout.InfiniteTimeSpan);

    // Crash paths use a timeout so a stuck sink can't hang the process.
    public bool Flush(TimeSpan timeout)
    {
        foreach (var child in LiveChildren())
            child.Flush(timeout);
        if (!Monitor.TryEnter(_writeLock, timeout))
            return false;
        try
        {
            DrainLocked();
            EmitPendingRepeatLocked();
            foreach (var sink in _sinks)
                TrySink(sink, s => s.Flush());
            return true;
        }
        finally { Monitor.Exit(_writeLock); }
    }

    // Flushes and closes the log file. The logger still works afterwards (lines reach its other sinks).
    public void Shutdown()
    {
        Flush();
        lock (_writeLock)
        {
            if (File != null)
            {
                var list = new List<ILogSink>(_sinks);
                list.Remove(File);
                _sinks = list.ToArray();
                File.Dispose();
                File = null;
            }
        }
    }

    // Flushes, closes the file and stops the writer serving this logger. Lines logged to it afterwards are
    // written at once, on the thread that logs them.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Shutdown();
        LogWriter.Unregister(this);
        if (_parent != null)
            lock (_parent._writeLock)
                _parent._children.RemoveAll(w => !w.TryGetTarget(out var c) || c == this);
    }

    private List<Logger> LiveChildren()
    {
        var live = new List<Logger>();
        lock (_writeLock)
        {
            if (_children.Count == 0) return live;
            _children.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in _children)
                if (w.TryGetTarget(out var child)) live.Add(child);
        }
        return live;
    }

    // Called by the writer thread.
    internal void Pump()
    {
        lock (_writeLock)
        {
            DrainLocked();
            // A repeat run that has gone quiet for a second is reported now rather than at the next message.
            if (_repeats > 0 && Stopwatch.GetElapsedTime(_lastRepeatTimestamp) > TimeSpan.FromSeconds(1))
                EmitPendingRepeatLocked();
            foreach (var sink in _sinks)
                TrySink(sink, s => s.Flush());
        }
    }

    private void DrainLocked()
    {
        while (_queue.TryDequeue(out var entry))
        {
            Interlocked.Decrement(ref _queued);
            DispatchLocked(entry);
        }

        long dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            DispatchLocked(Synthetic(LogCat.Core, LogLevel.Warn, $"{dropped} log entries dropped (log queue full; raise log_queue_size)"));
    }

    private void DispatchLocked(in LogEntry entry)
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

    private void EmitPendingRepeatLocked()
    {
        if (_repeats == 0) return;
        int n = _repeats;
        _repeats = 0;
        WriteToSinksLocked(Synthetic(_last.Category, _last.Level, $"(previous message repeated {n} more time{(n == 1 ? "" : "s")})"));
    }

    private void WriteToSinksLocked(in LogEntry entry)
    {
        foreach (var sink in _sinks)
        {
            if (entry.Level < sink.MinLevel) continue;
            try { sink.Write(entry); }
            catch (Exception ex) { RemoveBrokenSinkLocked(sink, ex); }
        }
        // Then the parent's sinks (not its queue: the line was already filtered and collapsed here). Always
        // child, then parent: the locks are taken in that order everywhere.
        if (_parent != null)
            lock (_parent._writeLock)
                _parent.WriteToSinksLocked(entry);
    }

    private void TrySink(ILogSink sink, Action<ILogSink> action)
    {
        try { action(sink); }
        catch (Exception ex) { RemoveBrokenSinkLocked(sink, ex); }
    }

    // A sink that throws is removed and reported through the remaining sinks (02 §8).
    private void RemoveBrokenSinkLocked(ILogSink sink, Exception ex)
    {
        var list = new List<ILogSink>(_sinks);
        if (!list.Remove(sink)) return;
        _sinks = list.ToArray();
        var report = Synthetic(LogCat.Core, LogLevel.Error, $"Log sink {sink.GetType().Name} failed and was removed: {ex.Message}");
        foreach (var s in _sinks)
        {
            try { s.Write(report); } catch { /* reported once is enough */ }
        }
    }

    private void AddSinkLocked(ILogSink sink)
    {
        var list = new List<ILogSink>(_sinks);
        if (!list.Contains(sink)) list.Add(sink);
        _sinks = list.ToArray();
    }

    private LogEntry Synthetic(LogCat cat, LogLevel level, string message)
    {
        var thread = Thread.CurrentThread;
        return new LogEntry(DateTime.UtcNow, _sessionClock.Elapsed.TotalSeconds,
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

// The one background thread that feeds every logger's sinks. Loggers are held weakly: one nobody keeps
// (an app a test never disposed) is collected, not kept alive by the writer.
internal static class LogWriter
{
    private static readonly object Lock = new();
    private static readonly List<WeakReference<Logger>> Loggers = new();
    private static readonly AutoResetEvent Signal = new(false);
    private static Thread? _thread;

    public static void Register(Logger logger)
    {
        lock (Lock)
        {
            Loggers.Add(new WeakReference<Logger>(logger));
            if (_thread != null) return;
            // UnsafeStart: the thread belongs to no app, so it doesn't capture the ambient AppEnvironment.
            _thread = new Thread(Loop) { IsBackground = true, Name = "log-writer" };
            _thread.UnsafeStart();
        }
    }

    public static void Unregister(Logger logger)
    {
        lock (Lock) Loggers.RemoveAll(w => !w.TryGetTarget(out var l) || l == logger);
    }

    public static void Wake() => Signal.Set();

    private static void Loop()
    {
        var live = new List<Logger>();
        while (true)
        {
            Signal.WaitOne(250);
            live.Clear();
            lock (Lock)
            {
                Loggers.RemoveAll(w => !w.TryGetTarget(out _));
                foreach (var w in Loggers)
                    if (w.TryGetTarget(out var l)) live.Add(l);
            }
            foreach (var logger in live)
            {
                try { logger.Pump(); }
                catch { /* a logger never takes the writer down */ }
            }
            live.Clear();   // hold no logger while waiting
        }
    }
}
