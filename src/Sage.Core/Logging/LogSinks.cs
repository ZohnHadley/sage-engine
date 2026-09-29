#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Sage.Core;

// A log output (docs/design/02 §3.4). Sinks are called by the log writer thread, or by whichever
// thread calls Log.Flush, always one at a time: implementations don't need their own locking.
public interface ILogSink
{
    LogLevel MinLevel { get; }
    void Write(in LogEntry entry);
    void Flush();
}

public static class LogFormatter
{
    // 2026-09-22T14:03:11.412Z [+00:12.345] f=742 t=0 [main] WARN  Assets  Missing texture ... (path=..., mount=game) (AssetServer.cs:212)
    public static string Format(in LogEntry e)
    {
        var sb = new StringBuilder(128);
        sb.Append(e.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        var t = TimeSpan.FromSeconds(e.SessionSeconds);
        sb.Append(" [+").Append(((int)t.TotalMinutes).ToString("00", CultureInfo.InvariantCulture))
          .Append(':').Append(t.Seconds.ToString("00", CultureInfo.InvariantCulture))
          .Append('.').Append(t.Milliseconds.ToString("000", CultureInfo.InvariantCulture)).Append(']');
        sb.Append(" f=").Append(e.Frame.ToString(CultureInfo.InvariantCulture));
        sb.Append(" t=").Append(e.Tick.ToString(CultureInfo.InvariantCulture));
        sb.Append(" [").Append(e.ThreadName ?? e.ThreadId.ToString(CultureInfo.InvariantCulture)).Append("] ");
        sb.Append(LevelName(e.Level)).Append(' ');
        sb.Append(e.Category.Name.PadRight(9)).Append(' ');
        sb.Append(e.Message);
        AppendFields(sb, e);
        if (e.File != null)
            sb.Append(" (").Append(Path.GetFileName(e.File)).Append(':').Append(e.Line).Append(')');
        return sb.ToString();
    }

    // Shorter form for the in-game console: "WARN  [Assets] message (key=value)".
    public static string FormatShort(in LogEntry e)
    {
        var sb = new StringBuilder(96);
        sb.Append(LevelName(e.Level)).Append(" [").Append(e.Category.Name).Append("] ").Append(e.Message);
        AppendFields(sb, e);
        return sb.ToString();
    }

    public static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "FATAL",
    };

    private static void AppendFields(StringBuilder sb, in LogEntry e)
    {
        if (e.Field1.IsEmpty && e.Field2.IsEmpty && e.Field3.IsEmpty)
            return;
        sb.Append(" (");
        bool first = true;
        foreach (var f in new[] { e.Field1, e.Field2, e.Field3 })
        {
            if (f.IsEmpty) continue;
            if (!first) sb.Append(", ");
            sb.Append(f.Key).Append('=').Append(Convert.ToString(f.Value, CultureInfo.InvariantCulture));
            first = false;
        }
        sb.Append(')');
    }
}

// One file per session: <dir>/sage-YYYYMMDD-HHMMSS.log, keeping the newest `keep` files.
public sealed class FileLogSink : ILogSink, IDisposable
{
    private const string Prefix = "sage-";
    private readonly StreamWriter _writer;

    public LogLevel MinLevel { get; set; } = LogLevel.Trace;
    public string Directory { get; }
    public string CurrentPath { get; }

    public FileLogSink(string directory, int keep)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path = Path.Combine(directory, $"{Prefix}{stamp}.log");
        for (int n = 2; File.Exists(path); n++)
            path = Path.Combine(directory, $"{Prefix}{stamp}-{n}.log");
        CurrentPath = path;
        _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
        Prune(keep);
    }

    public void Write(in LogEntry entry) => _writer.WriteLine(LogFormatter.Format(entry));
    public void Flush() => _writer.Flush();
    public void Dispose() => _writer.Dispose();

    // Deletes the oldest session logs so at most `keep` remain (log_keep). Never deletes the current file.
    public void Prune(int keep)
    {
        try
        {
            var old = new DirectoryInfo(Directory).GetFiles(Prefix + "*.log")
                .Where(f => !string.Equals(f.FullName, CurrentPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(Math.Max(0, keep - 1));
            foreach (var f in old)
                f.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

// stdout, plus the debugger's output window when one is attached. Enabled while developer >= 1.
public sealed class StdoutLogSink : ILogSink
{
    // Captured once: code elsewhere may temporarily redirect Console.Out (EcsSchema does, to catch a
    // library's startup message), and log lines written meanwhile must still reach the real stdout.
    private readonly TextWriter _out = System.Console.Out;

    public bool Enabled { get; set; } = true;
    public LogLevel MinLevel => Enabled ? LogLevel.Trace : (LogLevel)int.MaxValue;

    public void Write(in LogEntry entry)
    {
        string line = LogFormatter.Format(entry);
        _out.WriteLine(line);
        if (System.Diagnostics.Debugger.IsAttached)
            System.Diagnostics.Debug.WriteLine(line);
    }

    public void Flush() => _out.Flush();
}

// Always-on in-memory history: the console's scroll-back and the tail of crash reports.
public sealed class RingBufferLogSink : ILogSink
{
    private readonly object _lock = new();
    private readonly LogEntry[] _items;
    private int _start;
    private int _count;
    private long _version;

    public RingBufferLogSink(int capacity) { _items = new LogEntry[capacity]; }

    public LogLevel MinLevel => LogLevel.Trace;
    public int Capacity => _items.Length;

    // Changes whenever an entry is added or the buffer is cleared (lets the UI skip re-copying).
    public long Version { get { lock (_lock) return _version; } }

    public void Write(in LogEntry entry)
    {
        lock (_lock)
        {
            int index = (_start + _count) % _items.Length;
            _items[index] = entry;
            if (_count < _items.Length) _count++;
            else _start = (_start + 1) % _items.Length;
            _version++;
        }
    }

    public void Flush() { }

    // Copies the buffer, oldest first, into `into` (cleared first). Reuse the list to avoid allocating.
    public void Snapshot(List<LogEntry> into)
    {
        lock (_lock)
        {
            into.Clear();
            for (int i = 0; i < _count; i++)
                into.Add(_items[(_start + i) % _items.Length]);
        }
    }

    public void Clear()
    {
        lock (_lock) { _start = 0; _count = 0; _version++; }
    }
}
