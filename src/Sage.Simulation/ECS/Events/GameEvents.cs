#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Simulation;

// Typed game events (docs/design/04 §3.2): the way one system tells another that something happened
// in the simulation — `Damaged`, `Died`, `ItemPickedUp`. Before this, every feature grew its own
// queue with its own `Clear()` and its own lifetime rule written in a comment (engine review
// 2026-09-23, item 1). The rules live in this file instead:
//
// - One queue per event type **per schedule**, named by the sender (Fixed by default, which is
//   where simulation facts belong). A reader names the schedule it wants to read, not the one it
//   runs in, so a HUD drawing in Overlay reads what combat sent in Gameplay.
// - Every reader has its own **cursor**, so it never sees an event twice and never misses one. A
//   Frame reader of a Fixed queue sees every tick's events even when several ticks ran in one frame,
//   and a system that didn't run catches up next time.
// - An event is kept until **every registered reader has passed it**, then dropped. A queue nobody
//   reads drops immediately: sending into the void costs a slot, not a leak.
// - `ev_maxage` is the backstop. Events older than that many ticks are dropped with a warning naming
//   the reader that is behind, so a disabled system shows up as a message instead of as memory. In a
//   world that draws, it is checked at the end of each frame, so a frame reader is never blamed for the
//   ticks of a catch-up it could not read during (EndOfSchedule).
//
// Not for: engine plumbing (use EngineSignals), things a level designer wires (entity I/O, 04 §3.4),
// or anything that wants an answer back. Nothing here calls into another system; facts go in a queue
// and are read in a later phase.

[AttributeUsage(AttributeTargets.Struct)]
public sealed class GameEventAttribute : Attribute { }

// The queue's view of itself, so `GameEvents` can hold queues of every type in one list.
internal interface IEventQueue
{
    string EventName { get; }
    int Count { get; }
    int ReaderCount { get; }
    long OldestTick { get; }
    Schedule Schedule { get; }
    void Prune(long nowTick, int maxAge);
}

public sealed class EventQueue<T> : IEventQueue where T : struct
{
    private T[] _items = new T[8];
    private long[] _ticks = new long[8];
    private int _count;
    private long _base;                                   // sequence number of _items[0]
    private readonly List<EventReader<T>> _readers = new();

    internal EventQueue(Schedule schedule) => Schedule = schedule;

    public Schedule Schedule { get; }
    public string EventName => typeof(T).Name;
    public int Count => _count;
    public int ReaderCount => _readers.Count;
    public long End => _base + _count;                    // sequence one past the last event
    internal long Base => _base;
    public long OldestTick => _count == 0 ? long.MaxValue : _ticks[0];

    public void Send(in T ev, long tick)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);   // grows to the high-water mark, then stops
            Array.Resize(ref _ticks, _ticks.Length * 2);
        }
        _items[_count] = ev;
        _ticks[_count] = tick;
        _count++;
    }

    internal ref readonly T At(long sequence) => ref _items[(int)(sequence - _base)];

    internal void Register(EventReader<T> reader)
    {
        _readers.Add(reader);
        reader.Cursor = End;        // a reader joining late starts with what happens next, not history
    }

    internal bool Unregister(EventReader<T> reader) => _readers.Remove(reader);

    // Called once at the end of the schedule that owns this queue, never mid-phase: readers hold
    // sequence numbers, and moving _base under a reader mid-iteration would skip events.
    public void Prune(long nowTick, int maxAge)
    {
        if (_count == 0) return;

        long passed = End;
        for (int i = 0; i < _readers.Count; i++)
            if (_readers[i].Cursor < passed) passed = _readers[i].Cursor;

        Drop((int)(passed - _base));

        // The backstop: whoever is still holding these is not keeping up (a disabled system, a reader
        // whose phase never runs). Name them, drop the events, carry on.
        if (_count > 0 && maxAge >= 0 && _ticks[0] < nowTick - maxAge)
        {
            int stale = 0;
            while (stale < _count && _ticks[stale] < nowTick - maxAge) stale++;
            Log.Every(LogCat.Events, LogLevel.Warn, "ev_maxage_" + EventName, TimeSpan.FromSeconds(10),
                $"Dropping {stale} {EventName} event(s) older than ev_maxage ({maxAge} ticks); slowest reader: {SlowestReader()}.");
            Drop(stale);
        }
    }

    private void Drop(int n)
    {
        if (n <= 0) return;
        if (n >= _count) { _base += _count; _count = 0; return; }
        Array.Copy(_items, n, _items, 0, _count - n);
        Array.Copy(_ticks, n, _ticks, 0, _count - n);
        _base += n;
        _count -= n;
    }

    private string SlowestReader()
    {
        string name = "(none registered)";
        long lowest = long.MaxValue;
        for (int i = 0; i < _readers.Count; i++)
            if (_readers[i].Cursor < lowest) { lowest = _readers[i].Cursor; name = _readers[i].Owner; }
        return name;
    }

}

// A system's own view of one queue. Created once (in the system's constructor) and kept: the cursor
// is the whole point, and a reader made per tick would re-read from wherever it was dropped.
public sealed class EventReader<T> where T : struct
{
    private readonly EventQueue<T> _queue;

    internal EventReader(EventQueue<T> queue, string owner)
    {
        _queue = queue;
        Owner = owner;
        queue.Register(this);
    }

    internal long Cursor { get; set; }
    public string Owner { get; }

    // True when there is something this reader hasn't seen. Cheap enough to guard a phase on.
    public bool HasPending => Cursor < _queue.End;

    // Reads everything sent since the last call and advances the cursor past it. Allocation-free.
    public EventIterator<T> Read()
    {
        long from = Math.Max(Cursor, _queue.Base);   // pruned past us: take what is left, don't crash
        long to = _queue.End;
        Cursor = to;
        return new EventIterator<T>(_queue, from, to);
    }

    // Stops holding the queue back. A system that is removed from a world must do this, or its
    // cursor pins events forever (which ev_maxage would then report against it).
    public void Release() => _queue.Unregister(this);
}

public ref struct EventIterator<T> where T : struct
{
    private readonly EventQueue<T> _queue;
    private long _next;
    private readonly long _end;

    internal EventIterator(EventQueue<T> queue, long from, long end)
    {
        _queue = queue;
        _next = from;
        _end = end;
    }

    public EventIterator<T> GetEnumerator() => this;
    public bool MoveNext() => _next++ < _end;
    public readonly ref readonly T Current => ref _queue.At(_next - 1);
}

// The world's event bus. One per world; reached as `world.Events`.
public sealed class GameEvents
{
    private readonly Dictionary<(Type Type, Schedule Schedule), IEventQueue> _queues = new();
    private readonly List<IEventQueue> _fixed = new();
    private readonly List<IEventQueue> _frame = new();
    private readonly Dictionary<object, List<Action>> _byOwner = new(ReferenceEqualityComparer.Instance);

    private CVar<int>? _maxAgeCVar;
    private CVar<string>? _traceCVar;
    private int _maxAge = 8;
    private string? _trace;

    // ev_maxage: how many ticks an event may sit unread before it is dropped. -1 disables the cap.
    // The cvar wins where there is one; the setter is the fallback a bare world (a test) uses.
    public int MaxAge
    {
        get => _maxAgeCVar?.Value ?? _maxAge;
        set => _maxAge = value;
    }

    // ev_trace: the name of an event type to log every send of, or "*" for all. Dev builds only.
    public string? Trace
    {
        get => _traceCVar?.Value is { Length: > 0 } set_ ? set_ : _trace;
        set => _trace = value;
    }

    // Called by World when there is an engine to take these from, so changing a cvar mid-session
    // takes effect on the next prune rather than on the next launch.
    internal void UseCVars(CVar<int> maxAge, CVar<string> trace)
    {
        _maxAgeCVar = maxAge;
        _traceCVar = trace;
    }

    // The tick an event is stamped with, so ev_maxage can tell how long it has been waiting. The
    // world sets it; a send outside a tick (setup, a console command) stamps the last tick, which is
    // what we want — it ages from now, not from tick zero.
    public long NowTick { get; internal set; }

    // The world's structural changes, for `Added<T>`/`Removed<T>` queues; null on a bus with no world.
    internal StructuralEvents? Structural { get; set; }

    public EventQueue<T> Queue<T>(Schedule schedule = Schedule.Fixed) where T : struct
    {
        var key = (typeof(T), schedule);
        if (_queues.TryGetValue(key, out var existing)) return (EventQueue<T>)existing;

        var queue = new EventQueue<T>(schedule);
        _queues[key] = queue;
        (schedule == Schedule.Fixed ? _fixed : _frame).Add(queue);
        // `Added<T>`/`Removed<T>` (StructuralEvents.cs): the world starts publishing T's changes into
        // this queue now that it exists, and not before. Once per queue, so the box is no matter.
        if (Structural != null && default(T) is IStructuralEvent structural) structural.Attach(Structural, queue);
        return queue;
    }

    // `owner` is what a lagging-reader warning names, so pass the system that holds it (`this`, in its
    // constructor). It is also what the reader is released with: removing a system from a world
    // releases every reader it asked for (Release, issue #17). A string owner is a name only.
    public EventReader<T> Reader<T>(object owner, Schedule schedule = Schedule.Fixed) where T : struct
    {
        var reader = new EventReader<T>(Queue<T>(schedule), owner as string ?? owner.GetType().Name);
        if (owner is not string)
        {
            if (!_byOwner.TryGetValue(owner, out var readers)) _byOwner[owner] = readers = new List<Action>();
            readers.Add(reader.Release);
        }
        return reader;
    }

    // Releases every reader `owner` asked for, so the queues stop waiting for it. World.RemoveSystem
    // and world.Systems.Replace/Disable call it with the system; before issue #17 a removed system's
    // cursors pinned its queues until ev_maxage dropped the events with a warning naming it.
    public int Release(object owner)
    {
        if (!_byOwner.Remove(owner, out var readers)) return 0;
        foreach (var release in readers) release();
        return readers.Count;
    }

    public void Send<T>(in T ev, Schedule schedule = Schedule.Fixed) where T : struct
    {
        Queue<T>(schedule).Send(ev, NowTick);
        if (Trace is not null && BuildInfo.IsDevBuild && (Trace == "*" || Trace == typeof(T).Name))
            Log.Trace(LogCat.Events, $"send {typeof(T).Name} ({schedule})");
    }

    // Called by World at the end of each schedule, after every system in it has had its turn.
    //
    // **The age backstop waits for the frame** in a world that draws (issue 4h-7). A Fixed queue is read
    // by Fixed systems every tick and by display-rate ones (particles, audio, a HUD) once a frame, and a
    // slow frame is followed by a catch-up of up to `sim_maxframetime` × tick rate ticks (15 at 60 Hz) with
    // no frame in between. Checking the age at the end of each of those ticks dropped an event sent early
    // in the catch-up before its frame reader's only chance to read it, and blamed that reader
    // ("Dropping 1 CueTriggered … slowest reader: ParticleSystem" on a slow software renderer). So once a
    // world has drawn a frame, a tick only drops what every reader has passed, and the age check runs at
    // the end of the frame, for both schedules' queues, when every reader has had its turn. A world that
    // never draws (a server, a test that only ticks) checks every tick, as before
    // (test: AFrameReaderKeepsUpThroughACatchUpLongerThanMaxAge).
    public void EndOfSchedule(Schedule schedule, long nowTick)
    {
        if (schedule == Schedule.Fixed)
        {
            int maxAge = _drawsFrames ? -1 : MaxAge;
            for (int i = 0; i < _fixed.Count; i++) _fixed[i].Prune(nowTick, maxAge);
            return;
        }

        _drawsFrames = true;
        for (int i = 0; i < _frame.Count; i++) _frame[i].Prune(nowTick, MaxAge);
        for (int i = 0; i < _fixed.Count; i++) _fixed[i].Prune(nowTick, MaxAge);
    }

    private bool _drawsFrames;   // a frame has ended in this world: Fixed queues' age is checked per frame

    // For `ev_stats`.
    public IEnumerable<(string Event, Schedule Schedule, int Count, int Readers, long OldestTick)> Stats()
    {
        foreach (var q in _queues.Values)
            yield return (q.EventName, q.Schedule, q.Count, q.ReaderCount, q.OldestTick);
    }
}
