#nullable enable
using System.Collections.Generic;

namespace Sage.Tests;

using Assert = Xunit.Assert;

[GameEvent] public struct Pinged { public int Value; }
[GameEvent] public struct Ponged { public int Value; }

// The event bus (docs/design/04 §3.2), which replaces the four hand-rolled queues the 2026-09-23
// engine review counted. The rules worth pinning down are the ones those queues each got slightly
// wrong: who sees what, exactly once, and when an event is allowed to disappear.
public class GameEventTests
{
    public GameEventTests() { _ = TestEnv.UserRoot; }

    private static List<int> Drain(EventReader<Pinged> reader)
    {
        var got = new List<int>();
        foreach (ref readonly var ev in reader.Read()) got.Add(ev.Value);
        return got;
    }

    [Xunit.Fact]
    public void AReaderSeesWhatWasSentAndThenNothing()
    {
        var events = new GameEvents();
        var reader = events.Reader<Pinged>("test");

        events.Send(new Pinged { Value = 1 });
        events.Send(new Pinged { Value = 2 });

        Assert.True(reader.HasPending);
        Assert.Equal(new[] { 1, 2 }, Drain(reader));

        // Read twice, and the second call is empty: the cursor moved past them.
        Assert.False(reader.HasPending);
        Assert.Empty(Drain(reader));
    }

    [Xunit.Fact]
    public void EveryReaderSeesEveryEvent()
    {
        var events = new GameEvents();
        var a = events.Reader<Pinged>("a");
        var b = events.Reader<Pinged>("b");

        events.Send(new Pinged { Value = 7 });

        Assert.Equal(new[] { 7 }, Drain(a));
        Assert.Equal(new[] { 7 }, Drain(b));   // b is not starved by a having read first
    }

    [Xunit.Fact]
    public void AReaderThatDidNotRunCatchesUpLater()
    {
        var events = new GameEvents();
        var slow = events.Reader<Pinged>("slow");
        var fast = events.Reader<Pinged>("fast");

        for (int tick = 1; tick <= 3; tick++)
        {
            events.NowTick = tick;
            events.Send(new Pinged { Value = tick });
            Drain(fast);                                  // the fast reader runs every tick
            events.EndOfSchedule(Schedule.Fixed, tick);   // and the queue is pruned every tick
        }

        // The slow reader ran zero times and still sees all three, in order. This is the case the
        // old per-tick `Clear()` got wrong: a Frame reader missed ticks when several ran in a frame.
        Assert.Equal(new[] { 1, 2, 3 }, Drain(slow));
    }

    [Xunit.Fact]
    public void EventsAreDroppedOnceEveryReaderHasPassedThem()
    {
        var events = new GameEvents();
        var queue = events.Queue<Pinged>();
        var reader = events.Reader<Pinged>("only");

        events.Send(new Pinged { Value = 1 });
        events.EndOfSchedule(Schedule.Fixed, 1);
        Assert.Equal(1, queue.Count);        // still there: nobody has read it

        Drain(reader);
        events.EndOfSchedule(Schedule.Fixed, 1);
        Assert.Equal(0, queue.Count);        // read by all, so gone
    }

    [Xunit.Fact]
    public void AQueueNobodyReadsDoesNotGrow()
    {
        var events = new GameEvents();
        var queue = events.Queue<Ponged>();

        for (int tick = 1; tick <= 100; tick++)
        {
            events.NowTick = tick;
            events.Send(new Ponged { Value = tick });
            events.EndOfSchedule(Schedule.Fixed, tick);
        }

        // Sending into the void costs a slot for the tick, not a leak. A headless server that sends
        // messages no HUD reads must not fill up.
        Assert.Equal(0, queue.Count);
    }

    [Xunit.Fact]
    public void ALaggingReaderIsDroppedAtMaxAgeRatherThanHoldingTheQueue()
    {
        var events = new GameEvents { MaxAge = 3 };
        var queue = events.Queue<Pinged>();
        var stuck = events.Reader<Pinged>("stuck");

        for (int tick = 1; tick <= 20; tick++)
        {
            events.NowTick = tick;
            events.Send(new Pinged { Value = tick });
            events.EndOfSchedule(Schedule.Fixed, tick);
        }

        // The stuck reader never read, so without the cap the queue would hold all 20.
        Assert.True(queue.Count <= 4, $"queue held {queue.Count} events past ev_maxage");

        // What it does get is the recent events, not the ones already dropped, and it does not throw.
        var late = Drain(stuck);
        Assert.NotEmpty(late);
        Assert.Equal(20, late[^1]);
    }

    [Xunit.Fact]
    public void AReaderRegisteredLateStartsFromNow()
    {
        var events = new GameEvents();
        events.Send(new Pinged { Value = 1 });

        // A system added to a running world reacts to what happens next, not to history it missed.
        var latecomer = events.Reader<Pinged>("late");
        Assert.False(latecomer.HasPending);

        events.Send(new Pinged { Value = 2 });
        Assert.Equal(new[] { 2 }, Drain(latecomer));
    }

    [Xunit.Fact]
    public void FixedAndFrameQueuesAreSeparateAndAFrameReaderSeesEveryTick()
    {
        var events = new GameEvents();
        var hud = events.Reader<Pinged>("hud", Schedule.Fixed);   // a Frame system reading Fixed events

        for (int tick = 1; tick <= 3; tick++)
        {
            events.NowTick = tick;
            events.Send(new Pinged { Value = tick }, Schedule.Fixed);
            events.EndOfSchedule(Schedule.Fixed, tick);
        }

        // Three ticks ran before this frame drew; the HUD sees all three, which is the whole reason
        // a reader names the schedule it reads rather than the one it runs in.
        Assert.Equal(new[] { 1, 2, 3 }, Drain(hud));

        // And the Frame queue of the same type is a different queue.
        Assert.Equal(0, events.Queue<Pinged>(Schedule.Frame).Count);
    }

    [Xunit.Fact]
    public void ReleasingAReaderStopsItHoldingTheQueue()
    {
        var events = new GameEvents();
        var queue = events.Queue<Pinged>();
        var leaving = events.Reader<Pinged>("leaving");

        events.Send(new Pinged { Value = 1 });
        events.EndOfSchedule(Schedule.Fixed, 1);
        Assert.Equal(1, queue.Count);

        leaving.Release();                                // a system removed from the world
        events.EndOfSchedule(Schedule.Fixed, 1);
        Assert.Equal(0, queue.Count);
    }

    [Xunit.Fact]
    public void SendingWhileReadingIsSeenNextRead()
    {
        var events = new GameEvents();
        var reader = events.Reader<Pinged>("reentrant");

        events.Send(new Pinged { Value = 1 });

        // A system that reacts to an event by sending another one (damage -> death) must not have
        // the queue move under it mid-loop, and must not see its own send this pass.
        int seen = 0;
        foreach (ref readonly var ev in reader.Read())
        {
            seen++;
            if (ev.Value == 1) events.Send(new Pinged { Value = 2 });
        }

        Assert.Equal(1, seen);
        Assert.Equal(new[] { 2 }, Drain(reader));
    }

    // A display-rate system reading a Fixed queue (particles, audio, a HUD), as a world runs it.
    private sealed class FrameReader : ISystem
    {
        public readonly List<int> Seen = new();
        private readonly EventReader<CatchUpPing> _reader;

        public FrameReader(World world)
        {
            _reader = world.Events.Reader<CatchUpPing>(this, Schedule.Fixed);
            world.AddSystem(this, Phase.FrameUpdate, RunCondition.Always);
        }

        public void Run(in SystemContext ctx)
        {
            foreach (ref readonly var ev in _reader.Read()) Seen.Add(ev.Value);
        }
    }

    // The 4h exit's smoke failure (issue 4h-7): after a slow frame the host runs up to sim_maxframetime ×
    // tick rate ticks (15 at 60 Hz) before the next frame, more than ev_maxage (8). An event sent early in
    // that catch-up was dropped at the end of a tick, before the frame reader's only chance to read it, and
    // the warning blamed the reader ("Dropping 1 CueTriggered … slowest reader: ParticleSystem"). Once a
    // world draws, the age is checked at the end of the frame: the frame reader gets it and nobody is
    // blamed; a reader that really is stuck is still dropped, at the frame.
    [Xunit.Fact]
    public void AFrameReaderKeepsUpThroughACatchUpLongerThanMaxAge()
    {
        using var log = new CaptureSink();
        using var world = new World("catch-up");
        world.Events.MaxAge = 8;
        var particles = new FrameReader(world);
        world.RunFixed(1f / 60f);
        world.RunFrame(1f / 60f, 1f);                                     // a world that draws

        // A frame that took a quarter of a second: fifteen ticks, a cue in the first, then the frame.
        world.Events.Send(new CatchUpPing { Value = 1 });
        for (int tick = 0; tick < 15; tick++) world.RunFixed(1f / 60f);
        world.RunFrame(0.25f, 1f);

        Assert.Equal(new[] { 1 }, particles.Seen);
        Assert.Equal(0, world.Events.Queue<CatchUpPing>().Count);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(nameof(CatchUpPing)));

        // A reader that never reads is still the backstop's: dropped at the frame, and named.
        var stuck = world.Events.Reader<CatchUpPing>("stuck");
        world.Events.Send(new CatchUpPing { Value = 2 });
        for (int tick = 0; tick < 15; tick++) world.RunFixed(1f / 60f);
        Assert.Equal(1, world.Events.Queue<CatchUpPing>().Count);         // held through the catch-up
        world.RunFrame(0.25f, 1f);
        Assert.Equal(new[] { 1, 2 }, particles.Seen);
        Assert.Equal(0, world.Events.Queue<CatchUpPing>().Count);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains(nameof(CatchUpPing)) && e.Message.Contains("stuck"));
        _ = stuck;
    }
}

[GameEvent] public struct CatchUpPing { public int Value; }
