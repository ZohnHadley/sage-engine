#nullable enable
using System;
using System.Text.Json.Serialization;

namespace Sage.Simulation;

// How fast a world's time runs (issue #283, 4m-9): bullet time, a pause that leaves the menus running, the
// short freeze of a hit-stop. One per world, saved as the `time` resource:
//   "time": { "data": { "Scale": 0.5, "Paused": false, "HitStopLeft": 0, "Unscaled": 312.5, "Scaled": 270.1, "Carry": 0.008 } }
//
//   WorldTime.Of(world).Scale = 0.5;      // bullet time: half as many simulation steps a real second
//   WorldTime.Of(world).HitStop(0.08);    // freeze for 80 ms of real time, then carry on
//   world.Paused = true;                  // the same flag as WorldTime.Paused
//
// **The step stays the same length; how many run is what changes.** The host's loop runs real ticks
// (`sim_tickrate` a second, `World.RunFixed(dt)`); each real tick puts `dt × Scale` into the world's own
// accumulator (`Carry`) and the world runs one simulation step of exactly `dt` for every whole `dt` in it.
// At 0.5 a step runs every other real tick; at 2, two a tick; paused, hit-stopped or at 0, none. A step
// is always the same `dt`, so physics (which wants a constant step) behaves the same at any speed, and
// two runs of a world at the same scales agree step for step
// (test: HalfScaleHalvesTimerTweenAndClockProgress, AScaledWorldIsDeterministic). Scaling `dt` instead
// would change every integrator's step under it: a body at 4× would tunnel, one at 0.1× would settle
// differently. The cost is coarser motion at a slow scale, which the frame's interpolation alpha hides
// (World.RunFrame works the world's own alpha out from `Carry`).
//
// **A real tick with no step due is a held pass.** The world still runs its Fixed phases once, with
// `TickTime.Dt` 0 and `SimTime` standing still, but only the systems that declare they run on real time
// (`RunCondition.Always`) run in it, exactly as a paused world always did (test: Pause_SkipsFixedSystems_ButNotFrameOrAlways).
// They read `RealDt` for the real seconds this tick stands for: it is the tick's real length on the first
// pass of a real tick and 0 on the others, so a real-time system counts each real second once at any scale
// (test: RealTimeTimersAndTweensIgnoreScalePauseAndHitStop). The Frame schedule (cameras, the UI,
// rendering) runs every frame whatever the world's time does.
//
// **Timers and tweens** (`sage:timer`, `sage:tween`) run on the world's time; `"realTime": true` on one
// makes it count real seconds instead: through a pause, a hit-stop and any scale. Its outputs still go
// out by entity I/O's dispatch, which runs on simulation steps, so a real-time timer that fires during a
// pause is delivered when the world runs again.
//
// **The world's clock** (`WorldClock`, time of day) advances on steps, so at 0.5 the day goes by at half
// speed too, and a hit-stop does not move the sun.
//
// **The host's `host_timescale`** (a developer's cvar) multiplies `Scale` in every world (`HostScale`, not
// saved) and also slows real time, `RealDt` and the hit-stop countdown with it: it slows the machine, not
// the game. **Input**: the host samples the player's command before each step, so a slowed or
// hit-stopped world keeps a press made between steps for the next one; a paused world is still handed a
// command every tick and drops it, as before.
//
// At most `MaxStepsPerTick` steps run in one real tick: a scale too fast for the machine slows down
// rather than spiralling.
[SavedResource("time", Plugin = RegistrationOwners.Core)]
public sealed class WorldTime
{
    public const double MaxScale = 16.0;
    public const int MaxStepsPerTick = 16;

    private double _scale = 1.0;

    // Simulated seconds per real second: 1 normal, 0.5 bullet time, 0 stopped (as paused). Clamped to
    // 0..MaxScale; NaN or infinity is ignored.
    public double Scale
    {
        get => _scale;
        set { if (double.IsFinite(value)) _scale = Math.Clamp(value, 0.0, MaxScale); }
    }

    // No simulation steps until it is cleared; real-time systems and the Frame schedule keep running.
    public bool Paused { get; set; }

    // Real seconds of hit-stop still to run: no steps until it reaches 0.
    public double HitStopLeft { get; set; }

    // Real seconds this world has run, pause and hit-stop and all (host_timescale aside).
    public double Unscaled { get; set; }

    // Simulated seconds: the sum of every step this world has run. Unlike `World.SimTime`, saved.
    public double Scaled { get; set; }

    // Scaled seconds owed to the next step: always less than one step.
    public double Carry { get; set; }

    // The host's `host_timescale`, set by HostLoop each update; 1 elsewhere.
    [JsonIgnore] public double HostScale { get; set; } = 1.0;

    // Whether a step is due when real time passes: not paused, not hit-stopped, and a scale above 0.
    [JsonIgnore] public bool Running => !Paused && HitStopLeft <= 0.0 && _scale * HostScale > 0.0;

    // The real seconds the current pass stands for: the real tick's length (× HostScale) on its first
    // pass, 0 on the rest. What a real-time system counts with.
    [JsonIgnore] public float RealDt { get; private set; }

    // True in a pass that is a simulation step (TickTime.Dt is the step); false in a held pass.
    [JsonIgnore] public bool Stepping { get; private set; }

    // The length of the last real tick, for the frame's alpha.
    [JsonIgnore] internal float TickDt { get; private set; }

    // Freezes the world's simulation for `seconds` of real time (a hit-stop: the beat of stillness when a
    // blow lands). A second one while one runs keeps the longer of the two rather than adding up.
    public void HitStop(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0.0) return;
        HitStopLeft = Math.Max(HitStopLeft, Math.Min(seconds, 60.0));
    }

    // The world's; made when it has none.
    public static WorldTime Of(World world) => world.Resources.GetOrAdd(static () => new WorldTime());

    // A real tick of `dt` seconds begins: how many steps of `dt` it runs (0: one held pass).
    internal int BeginTick(float dt)
    {
        TickDt = dt;
        double real = Math.Max(0.0, dt * HostScale);
        Unscaled += real;
        RealDt = (float)real;
        if (dt <= 0f) return 0;

        if (HitStopLeft > 0.0)
        {
            HitStopLeft -= real;
            if (HitStopLeft <= dt * 1e-3) HitStopLeft = 0.0;   // float dust: N ticks of 1/60 are N/60
            return 0;
        }
        if (!Running) return 0;

        Carry += dt * _scale * HostScale;
        double step = dt;
        double due = Math.Floor((Carry + step * 1e-6) / step);   // 1/3 + 1/3 + 1/3 is a step, not 0.99999
        int steps = (int)Math.Min(due, MaxStepsPerTick);
        Carry = steps < due ? 0.0 : Math.Max(0.0, Carry - steps * step);
        return steps;
    }

    // A pass of the Fixed phases begins: a step (`stepDt` simulated) or a held pass.
    internal void BeginPass(bool step, bool first, float stepDt)
    {
        Stepping = step;
        if (!first) RealDt = 0f;
        if (step) Scaled += stepDt;
    }

    // The interpolation alpha for this world, from the host's: how far the world is between its last step
    // and its next, which at scale 1 is the host's own.
    internal float Alpha(float hostAlpha)
    {
        if (TickDt <= 0f) return hostAlpha;
        double rate = Running ? _scale * HostScale : 0.0;
        if (rate == 1.0 && Carry == 0.0) return hostAlpha;
        return (float)Math.Clamp((Carry + hostAlpha * TickDt * rate) / TickDt, 0.0, 1.0);
    }
}

// `{ "world_speed": 0.5 }`: bullet time from data (1 is normal speed, 0 stops it, as a pause would).
[Action("world_speed", Plugin = RegistrationOwners.Core)]
internal sealed class WorldSpeedAction : IAction
{
    [EntryValue, Property(Min = 0, Max = WorldTime.MaxScale, Tooltip = "Simulated seconds per real second: 1 normal, 0.5 half speed")]
    public double Scale = 1.0;

    public void Run(in ActionContext context) => WorldTime.Of(context.World).Scale = Scale;
}

// `{ "hit_stop": 0.08 }`: freeze the world for that many real seconds (the longer, if one is running).
[Action("hit_stop", Plugin = RegistrationOwners.Core)]
internal sealed class HitStopAction : IAction
{
    [EntryValue, Property(Min = 0, Max = 60, Unit = "s", Tooltip = "Real seconds to freeze the world for")]
    public double Seconds = 0.1;

    public void Run(in ActionContext context) => WorldTime.Of(context.World).HitStop(Seconds);
}
