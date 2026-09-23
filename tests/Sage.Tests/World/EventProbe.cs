#nullable enable
using System.Collections.Generic;
using sage_engine;

namespace sage_engine.Tests;

// A test's view of an event queue (04 §3.2). A production reader drains as it goes and forgets;
// a test usually wants "did this happen at all, over these forty ticks", so this remembers.
//
// Make it *before* the ticks it should see: a reader registered late starts from now, and the queue
// drops what every reader has passed at the end of each schedule.
//
// It installs itself as a system so it keeps up tick by tick. A probe that only read at the end of
// a long run would fall behind `ev_maxage` and be dropped — correctly, but that is the bus catching
// a lagging reader, not a test failing.
public sealed class EventProbe<T> : ISystem where T : struct
{
    private readonly EventReader<T> _reader;
    private readonly List<T> _all = new();
    private readonly List<T> _since = new();

    public EventProbe(World world, Schedule schedule = Schedule.Fixed)
    {
        _reader = world.Events.Reader<T>($"probe<{typeof(T).Name}>", schedule);
        world.AddSystem(this, schedule == Schedule.Fixed ? Phase.Late : Phase.Overlay, RunCondition.Always);
    }

    public void Run(in SystemContext ctx) => Pump();

    // Everything seen since the probe was made.
    public IReadOnlyList<T> All
    {
        get { Pump(); return _all; }
    }

    // Everything seen since the last call to this — the per-tick "did it fire" check.
    public IReadOnlyList<T> Since()
    {
        Pump();
        var batch = _since.ToArray();
        _since.Clear();
        return batch;
    }

    private void Pump()
    {
        foreach (ref readonly var ev in _reader.Read())
        {
            _all.Add(ev);
            _since.Add(ev);
        }
    }
}
