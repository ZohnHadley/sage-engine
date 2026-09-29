#nullable enable
using System;

namespace Sage.Core;

// Time as systems see it (docs/design/02 §4.3). Systems read these from their SystemContext, never
// DateTime.Now or MonoGame's GameTime; that's what makes headless tests and time scaling work.

// Fixed schedule: one simulation tick.
public readonly record struct TickTime(long Tick, float Dt, double SimTime);

// Frame schedule: one rendered frame. Alpha is how far the current frame is between the previous
// and the current tick (0..1), used to interpolate poses (01 §5.2).
public readonly record struct FrameTime(long Frame, float Dt, float Alpha, double RealTime);

public readonly record struct FixedStepResult(int Ticks, float TickDt, float Alpha, float FrameDt);

// The host's fixed-timestep accumulator (Fiedler, "Fix Your Timestep"; 01 §5.2):
//   frameDt = min(realDt, maxFrameTime) * timeScale; accumulate; run whole ticks; alpha = remainder / tickDt.
// Clamping the frame time avoids the "spiral of death" after a hitch (breakpoint, loading).
public sealed class FixedStepClock
{
    private double _accumulator;

    public double RealTime { get; private set; }

    public FixedStepResult Advance(double realDt, int tickRate, double maxFrameTime, double timeScale)
    {
        if (tickRate < 1) tickRate = 1;
        double tickDt = 1.0 / tickRate;
        realDt = Math.Max(0, realDt);
        RealTime += realDt;

        double frameDt = Math.Min(realDt, maxFrameTime) * Math.Max(0, timeScale);
        _accumulator += frameDt;

        int ticks = 0;
        while (_accumulator >= tickDt)
        {
            _accumulator -= tickDt;
            ticks++;
        }
        return new FixedStepResult(ticks, (float)tickDt, (float)(_accumulator / tickDt), (float)frameDt);
    }

    public void Reset() => _accumulator = 0;
}
