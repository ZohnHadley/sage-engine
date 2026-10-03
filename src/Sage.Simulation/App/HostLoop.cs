#nullable enable
using System;

namespace Sage.Simulation;

// The main loop's simulation half (docs/design/01 §5.2, REDESIGN §3.2, issue #10): a fixed-timestep
// accumulator that runs whole ticks for **every** world the engine has, then one frame for each.
// Before this, the host ticked the one world it had created and ignored `Engine.Worlds`, so a second
// world would have existed and never moved.
//
// No MonoGame: a windowed host calls Update from its Update and Frame from its Draw; a headless one
// calls both from its own loop. Input is the host's business — `beforeTick` is where it hands a world
// the player command for the tick about to run.
public sealed class HostLoop
{
    private readonly Engine _engine;
    private readonly FixedStepClock _clock = new();

    public HostLoop(Engine engine) { _engine = engine; }

    // Seconds of real time the loop has been advanced by (unscaled, unclamped).
    public double RealTime => _clock.RealTime;

    // What the last Update decided: how many ticks ran, their length, and the interpolation alpha the
    // next Frame uses.
    public FixedStepResult Last { get; private set; }

    // Runs as many whole real ticks as `realDt` pays for, every world each tick, in the order the worlds
    // were created. A world created during a tick starts ticking on the next one. `Ticks` in the result
    // counts real ticks; how many simulation steps each world ran in them is its WorldTime's business.
    public FixedStepResult Update(double realDt, int tickRate, double maxFrameTime, double timeScale,
                                  Action<World>? beforeTick = null)
    {
        // Real ticks at the real rate; the time scale is each world's (WorldTime, issue #283), which turns a
        // real tick into as many constant-length steps as are due. `timeScale` (host_timescale) goes there
        // too, as WorldTime.HostScale, rather than into this clock.
        if (!double.IsFinite(timeScale) || timeScale < 0) timeScale = 0;
        var real = _clock.Advance(realDt, tickRate, maxFrameTime, 1.0);
        var step = new FixedStepResult(real.Ticks, real.TickDt, real.Alpha, (float)(real.FrameDt * timeScale));
        var worlds = _engine.Worlds;
        for (int i = 0; i < worlds.Count; i++) WorldTime.Of(worlds[i]).HostScale = timeScale;
        for (int tick = 0; tick < step.Ticks; tick++)
        {
            int count = worlds.Count;   // indexed, not foreach: no allocation, and a new world waits a tick
            for (int i = 0; i < count && i < worlds.Count; i++)
            {
                var world = worlds[i];
                WorldTime.Of(world).HostScale = timeScale;   // a world made during the ticks
                world.RunFixed(step.TickDt, beforeTick);   // the player's command before each of its steps
            }
        }
        Last = step;
        return step;
    }

    // The Frame schedule (FrameUpdate → Extract → Render → Overlay) for every world, with the alpha the
    // last Update left. Each world's Render pass draws the whole screen, so two worlds *on screen* at once
    // needs views and viewports (REDESIGN phase 4a); today a game has one visible world.
    public void Frame()
    {
        var worlds = _engine.Worlds;
        for (int i = 0; i < worlds.Count; i++)
            worlds[i].RunFrame(Last.FrameDt, Last.Alpha, RealTime);
    }

    // Forget any partly accumulated tick (after a load, say), so the next Update starts clean.
    public void ResetAccumulator() => _clock.Reset();
}
