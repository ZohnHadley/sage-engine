#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace Sage.Core;

// **A Chrome trace of the profiler's scopes** (docs/design/02 §4.4, issue #300): `trace_start`, play a
// while, `trace_dump` — and the file in logs/ opens in Perfetto (ui.perfetto.dev) or chrome://tracing,
// one track per thread, every phase and system as a slice, a marker at each frame's end.
//
// Capturing is what `stat frame` is not: every scope on every thread, in time, rather than one average per
// name on one thread. So the second world's thread, the side-by-side systems on the workers (#288; their
// SystemRun reports its span through TraceSpan, since it is timed outside a Scope) and the terrain jobs on
// the thread pool all show up, each on its own track, where they actually ran.
//
// The buffer is a fixed array of structs, claimed with one Interlocked add per event, so a capture
// allocates nothing per scope (a thread's name, once per capture, is the exception) and any thread can
// write. With nothing capturing, a scope pays one static read. A full buffer drops further events and
// counts them; the dump says how many.
public static partial class Profiler
{
    internal const string FrameMarker = "EndFrame";
    internal const int DefaultTraceEvents = 1 << 18;   // ~10 MB of events; a few seconds of a busy game

    private struct TraceEvent
    {
        public string? Name;   // written last: a reader skips a slot whose name is not there yet
        public long Start, End;
        public int Thread;
    }

    private sealed class TraceBuffer
    {
        public TraceBuffer(int capacity, int mainThread)
        {
            Events = new TraceEvent[capacity];
            MainThread = mainThread;
            Started = Stopwatch.GetTimestamp();
        }

        public readonly TraceEvent[] Events;
        public readonly int MainThread;
        public readonly long Started;
        public readonly ConcurrentDictionary<int, string> Threads = new();
        public int Claimed;
        public int Dropped;
        public int Recorded => Math.Min(Volatile.Read(ref Claimed), Events.Length);
    }

    private static volatile TraceBuffer? _capture;   // set while capturing
    private static TraceBuffer? _lastCapture;        // what trace_dump writes
    [ThreadStatic] private static TraceBuffer? _threadNamedIn;

    // Whether a capture is running (trace_start .. trace_dump).
    internal static bool Capturing => _capture != null;

    // Events in the running (or last) capture, and how many a full buffer dropped.
    internal static int CapturedEvents => (_capture ?? _lastCapture)?.Recorded ?? 0;
    internal static int DroppedEvents => (_capture ?? _lastCapture)?.Dropped ?? 0;

    // Starts a capture of at most `maxEvents` events, dropping the last one. False while profiling is
    // off (a Shipping build), when there is nothing to capture.
    internal static bool StartCapture(int maxEvents = DefaultTraceEvents)
    {
        if (!Enabled) return false;
        var buffer = new TraceBuffer(Math.Max(16, maxEvents), Environment.CurrentManagedThreadId);
        _lastCapture = buffer;
        _capture = buffer;
        return true;
    }

    // Stops the capture, keeping it for WriteTrace. False if none was running.
    internal static bool StopCapture()
    {
        var was = _capture;
        _capture = null;
        return was != null;
    }

    // A span timed outside a Scope, on this thread (a system run side by side on a worker, #288): into the
    // capture, if one is running, as if a scope named `name` had been open from `startTimestamp` to
    // `endTimestamp` (Stopwatch timestamps). It does not touch the per-frame tables; Record does that.
    public static void TraceSpan(string name, long startTimestamp, long endTimestamp)
    {
        if (_capture != null) Capture(name, startTimestamp, endTimestamp);
    }

    private static void Capture(string name, long start, long end)
    {
        var buffer = _capture;
        if (buffer == null) return;
        if (Volatile.Read(ref buffer.Claimed) >= buffer.Events.Length) { Interlocked.Increment(ref buffer.Dropped); return; }
        int i = Interlocked.Increment(ref buffer.Claimed) - 1;
        if (i >= buffer.Events.Length) { Interlocked.Increment(ref buffer.Dropped); return; }

        int thread = Environment.CurrentManagedThreadId;
        if (_threadNamedIn != buffer)
        {
            _threadNamedIn = buffer;
            buffer.Threads.TryAdd(thread, ThreadName(thread, buffer.MainThread));
        }
        // A scope that opened before the capture started (on any thread) is cut at the capture's start: a
        // trace's times count from there, and Perfetto rejects a negative one.
        if (start < buffer.Started) start = Math.Min(buffer.Started, end);
        ref var e = ref buffer.Events[i];
        e.Start = start;
        e.End = end;
        e.Thread = thread;
        Volatile.Write(ref e.Name, name);
    }

    private static string ThreadName(int thread, int main) =>
        thread == main ? "main" : Thread.CurrentThread.Name is { Length: > 0 } name ? $"{name} ({thread})" : $"thread {thread}";

    // Writes the last capture as Chrome trace event JSON (the "JSON Object Format" Perfetto and
    // chrome://tracing read): metadata naming the process and each thread, a complete ("X") event per
    // scope, an instant ("i") event at each frame's end. Times are microseconds from the capture's start.
    // Returns the number of events written (0 with no capture). Stop the capture first, or slots still being
    // written are skipped.
    internal static int WriteTrace(Stream stream, string processName = "Sage")
    {
        var buffer = _lastCapture;
        int count = buffer?.Recorded ?? 0;
        var events = new TraceEvent[count];
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            if (Volatile.Read(ref buffer!.Events[i].Name) == null) continue;
            events[n++] = buffer.Events[i];
        }
        // By thread, then start, the longer first, so parents precede their children.
        Array.Sort(events, 0, n, Comparer<TraceEvent>.Create(static (a, b) =>
            a.Thread != b.Thread ? a.Thread.CompareTo(b.Thread)
            : a.Start != b.Start ? a.Start.CompareTo(b.Start)
            : b.End.CompareTo(a.End)));

        double usPerTick = 1_000_000.0 / Stopwatch.Frequency;
        long origin = buffer?.Started ?? 0;
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        json.WriteStartObject();
        json.WriteStartArray("traceEvents");

        json.WriteStartObject();
        json.WriteString("name", "process_name");
        json.WriteString("ph", "M");
        json.WriteNumber("pid", 1);
        json.WriteNumber("tid", 0);
        json.WriteStartObject("args");
        json.WriteString("name", processName);
        json.WriteEndObject();
        json.WriteEndObject();

        if (buffer != null)
            foreach (var (thread, name) in buffer.Threads)
            {
                json.WriteStartObject();
                json.WriteString("name", "thread_name");
                json.WriteString("ph", "M");
                json.WriteNumber("pid", 1);
                json.WriteNumber("tid", thread);
                json.WriteStartObject("args");
                json.WriteString("name", name);
                json.WriteEndObject();
                json.WriteEndObject();
                // The main thread's track first.
                json.WriteStartObject();
                json.WriteString("name", "thread_sort_index");
                json.WriteString("ph", "M");
                json.WriteNumber("pid", 1);
                json.WriteNumber("tid", thread);
                json.WriteStartObject("args");
                json.WriteNumber("sort_index", thread == buffer.MainThread ? -1 : thread);
                json.WriteEndObject();
                json.WriteEndObject();
            }

        for (int i = 0; i < n; i++)
        {
            ref var e = ref events[i];
            bool marker = ReferenceEquals(e.Name, FrameMarker);
            json.WriteStartObject();
            json.WriteString("name", e.Name);
            json.WriteString("cat", marker ? "frame" : Category(e.Name!));
            json.WriteString("ph", marker ? "i" : "X");
            json.WriteNumber("ts", Math.Round((e.Start - origin) * usPerTick, 3));
            if (marker) json.WriteString("s", "t");
            else json.WriteNumber("dur", Math.Round(Math.Max(0, e.End - e.Start) * usPerTick, 3));
            json.WriteNumber("pid", 1);
            json.WriteNumber("tid", e.Thread);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteString("displayTimeUnit", "ms");
        json.WriteStartObject("otherData");
        json.WriteString("engine", $"Sage {BuildInfo.EngineVersion} ({BuildInfo.ConfigurationName})");
        json.WriteNumber("dropped", buffer?.Dropped ?? 0);
        json.WriteEndObject();
        json.WriteEndObject();
        return n;
    }

    // "Fixed.Gameplay/FaceCameraSystem" -> "Fixed": the schedule, so Perfetto can colour and filter by it.
    private static string Category(string name)
    {
        int dot = name.IndexOf('.');
        int slash = name.IndexOf('/');
        int end = dot < 0 ? slash : slash < 0 ? dot : Math.Min(dot, slash);
        return end > 0 ? name.Substring(0, end) : "sage";
    }

    // Stops the capture and writes it to `path` (a name alone goes in logs/; none = trace-<time>.json).
    // Returns the full path written.
    internal static string DumpTrace(string? path, out int events)
    {
        StopCapture();
        string file = string.IsNullOrWhiteSpace(path) ? $"trace-{DateTime.Now:yyyyMMdd-HHmmss}.json" : path;
        if (!Path.IsPathRooted(file)) file = Path.Combine(UserPaths.Logs, file);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        using var stream = File.Create(file);
        events = WriteTrace(stream, UserPaths.GameId);
        return Path.GetFullPath(file);
    }
}
