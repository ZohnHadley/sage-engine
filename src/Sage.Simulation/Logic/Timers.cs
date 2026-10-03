#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Timers (issue #90, phase 4b): Source's logic_timer, as a component. "Every three seconds, a gust of
// wind"; "twenty seconds after the alarm, the guards arrive"; "a lamp that flickers at random". Level
// logic that is about *time*, written in data like the rest of entity I/O.
//
//   component  "sage:timer": { "interval": 3, "spread": 0.5, "repeat": true, "running": true }
//   part       "timer": { "interval": 3, "spread": 0.5, "repeat": true, "startOn": true, "seed": 7 }
//   prefab     sage:logic_timer — an entity with nothing but a timer, stopped until TimerStart
//
//   inputs   TimerStart [seconds]   start counting from a full wait (a number: the new interval first)
//            TimerStop              stop; nothing fires until TimerStart
//            TimerReset             begin the wait again from full, running or not
//            TimerFire              fire OnTimer now; a running timer then waits again from full (#281)
//            TimerAdd seconds       add to (or, negative, take from) the wait left (#281)
//   output   OnTimer                the wait ran out; the activator is whoever started the timer
//
// The input names say `Timer` because the input table was one global namespace (EntityInputs): `Toggle`
// is movers', `Start` would be anybody's. (#91 routed inputs by component since; these names stay global.)
//
// **When it fires.** The countdown runs in the EntityIO phase *before* the dispatch, so OnTimer's wires
// with no delay arrive the same tick, and a timer of N seconds started on tick D fires on the tick an
// input sent on tick D with a delay of N arrives
// (test: ATimerFiresOnTheTickADelayedWireArrives_AndRepeatsWithoutDrift).
// A repeating timer carries what it overshot into the next wait, so it does not drift; it fires at most
// once a tick, whatever its interval.
//
// **Random, and still deterministic.** `spread` makes each wait `interval ± spread` (never below zero),
// drawn from the timer's own random stream: its state is in the component and saved, so a loaded game
// draws the same waits it would have, and two runs of a level draw the same ones (the stream starts from
// `seed`, or from the entity when there is none). The part's `randomMin` and `randomMax` say the same
// as bounds, Source's LowerRandomBound and UpperRandomBound: each wait anywhere between them (#281).
//
// Owned by the engine (`sage.core`), like cameras (CameraIO): a data-only game has timers whatever
// plugins it lists; their outputs reach wires when the entity I/O plugin is on, as every output does.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[Component("sage:timer")]
public struct LogicTimer : IComponent
{
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds between one OnTimer and the next")]
    public float Interval;
    [Property(Min = 0, Unit = "s", Tooltip = "Each wait is the interval plus or minus up to this much, at random")]
    public float Spread;
    [Property(Tooltip = "Fire every interval (on), or once and stop (off)")]
    public bool Repeat;
    [Property(Tooltip = "Counting down now")]
    public bool Running;
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds left before the next OnTimer")]
    public float Remaining;
    [Property(Tooltip = "Count real seconds: through a pause, a hit-stop and any world speed (WorldTime)")]
    public bool RealTime;
    // The random stream's state (xorshift32); 0 = not started, and the first draw seeds it from the
    // entity. Saved, so a load draws the waits the game would have drawn.
    public uint Random;

    // Who started it: handed to OnTimer. A handle, meaningless in another session.
    [Transient] public Entity Activator;
}

// "timer": { "interval": 3, "spread": 0, "repeat": true, "startOn": false, "seed": 0 }
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[PrefabPart("timer", Plugin = RegistrationOwners.Core)]
public sealed class LogicTimerPart : IPrefabPart
{
    [Property(Min = 0, Unit = "s", Tooltip = "Seconds between one OnTimer and the next")]
    public float Interval = 1f;
    [Property(Min = 0, Unit = "s", Tooltip = "Each wait is the interval plus or minus up to this much, at random")]
    public float Spread;
    [Property(Tooltip = "Fire every interval (on), or once and stop (off)")]
    public bool Repeat = true;
    [Property(Tooltip = "Start counting as soon as it is spawned; off: wait for TimerStart")]
    public bool StartOn;
    [Property(Tooltip = "Where the random stream for `spread` starts; 0 = from the entity")]
    public uint Seed;
    [Property(Tooltip = "Count real seconds: through a pause, a hit-stop and any world speed (WorldTime)")]
    public bool RealTime;
    [Property(Min = 0, Unit = "s", Tooltip = "With randomMax: each wait is anywhere from this to randomMax (instead of interval and spread)")]
    public float RandomMin;
    [Property(Min = 0, Unit = "s", Tooltip = "With randomMin: each wait is anywhere from randomMin to this (instead of interval and spread)")]
    public float RandomMax;

    public void Apply(in PrefabPartContext ctx)
    {
        float interval = Timers.Seconds(Interval, 1f, "interval", ctx), spread = Timers.Seconds(Spread, 0f, "spread", ctx);
        if (RandomMin != 0f || RandomMax != 0f)
        {
            float min = Timers.Seconds(RandomMin, 0f, "randomMin", ctx), max = Timers.Seconds(RandomMax, 0f, "randomMax", ctx);
            if (max < min) ctx.Warn($"randomMax {max} is below randomMin {min}: they are swapped");
            if (max < min) (min, max) = (max, min);
            // interval ± spread is the same range: the middle, give or take half of it.
            interval = (min + max) * 0.5f;
            spread = (max - min) * 0.5f;
        }
        var timer = new LogicTimer
        {
            Interval = interval,
            Spread = spread,
            Repeat = Repeat,
            RealTime = RealTime,
            Random = Seed,
        };
        if (StartOn) Timers.Start(ref timer, ctx.Entity);
        ctx.World.Add(ctx.Entity, timer);
    }
}

// The inputs, the output and the arithmetic. Registered by the engine (Engine's constructor, sage.core).
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
public static class Timers
{
    public const string StartInput = "TimerStart";
    public const string StopInput = "TimerStop";
    public const string ResetInput = "TimerReset";
    public const string FireInput = "TimerFire";
    public const string AddInput = "TimerAdd";
    public const string OnTimer = "OnTimer";

    // What is left at or under this is "done": N seconds counted down in 1/60 s steps leaves float dust
    // either side of zero, and the timer should fire on the Nth tick, not the one after (CameraIO's).
    internal const float Epsilon = 1e-4f;

    internal static void Register(Engine engine)
    {
        engine.Inputs.Register(StartInput, static (World world, in IOContext io) =>
        {
            if (!Find(world, io, StartInput)) return;
            ref var timer = ref world.Get<LogicTimer>(io.Self);
            if (io.Parameter.Length > 0)
            {
                float interval = io.Number(float.NaN);
                if (interval >= 0f && float.IsFinite(interval)) timer.Interval = interval;
                else Log.Warn(LogCat.Events, $"I/O: {StartInput}({io.Parameter}) at {World.Describe(io.Self)}: the parameter is an "
                                           + $"interval in seconds; {timer.Interval} is used");
            }
            timer.Activator = io.Activator;
            Start(ref timer, io.Self);
        });
        engine.Inputs.Register(StopInput, static (World world, in IOContext io) =>
        {
            if (!Find(world, io, StopInput)) return;
            world.Get<LogicTimer>(io.Self).Running = false;
        });
        engine.Inputs.Register(ResetInput, static (World world, in IOContext io) =>
        {
            if (!Find(world, io, ResetInput)) return;
            ref var timer = ref world.Get<LogicTimer>(io.Self);
            timer.Remaining = NextWait(ref timer, io.Self);
        });
        // Source's FireTimer: OnTimer now, from whoever sent it; a running timer starts its wait again.
        engine.Inputs.Register(FireInput, static (World world, in IOContext io) =>
        {
            if (!Find(world, io, FireInput)) return;
            ref var timer = ref world.Get<LogicTimer>(io.Self);
            if (timer.Running) timer.Remaining = NextWait(ref timer, io.Self);
            world.FireOutput(io.Self, OnTimer, io.Activator);
        });
        // Source's AddToTimer / SubtractFromTimer: the wait left, longer or shorter (never below nothing).
        engine.Inputs.Register(AddInput, static (World world, in IOContext io) =>
        {
            if (!Find(world, io, AddInput)) return;
            float seconds = io.Number(float.NaN);
            if (!float.IsFinite(seconds))
            {
                Log.Warn(LogCat.Events, $"I/O: {AddInput}({io.Parameter}) at {World.Describe(io.Self)}: the parameter should be seconds");
                return;
            }
            ref var timer = ref world.Get<LogicTimer>(io.Self);
            timer.Remaining = MathF.Max(0f, timer.Remaining + seconds);
        });
        engine.Outputs.Declare(OnTimer, "This timer's wait ran out (sage:timer; TimerStart starts it).");
    }

    // Running, from a full wait.
    public static void Start(ref LogicTimer timer, Entity entity)
    {
        timer.Running = true;
        timer.Remaining = NextWait(ref timer, entity);
    }

    // The next wait: the interval, give or take the spread, never below zero.
    public static float NextWait(ref LogicTimer timer, Entity entity)
    {
        float interval = timer.Interval >= 0f && float.IsFinite(timer.Interval) ? timer.Interval : 0f;
        if (!(timer.Spread > 0f) || !float.IsFinite(timer.Spread)) return interval;
        float unit = Random01(ref timer.Random, entity) * 2f - 1f;   // -1..1
        return MathF.Max(0f, interval + unit * timer.Spread);
    }

    // The next draw, 0 (included) to 1 (not), from a stream kept in a saved component: 0 = not started,
    // and the first draw seeds it from the entity. What timers, logic_case and the rest draw from.
    internal static float Random01(ref uint state, Entity entity)
    {
        if (state == 0) state = Seed(entity);
        return Next(ref state);
    }

    // xorshift32 (Marsaglia): tiny, fast, allocation-free and the same on every machine.
    private static float Next(ref uint state)
    {
        uint x = state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        state = x == 0 ? 0x9E3779B9u : x;
        return (x >> 8) * (1f / 16777216f);
    }

    private static uint Seed(Entity entity)
    {
        uint s = unchecked((uint)entity.Id * 2654435761u) ^ 0x6C8E9CF5u;
        return s == 0 ? 0x9E3779B9u : s;
    }

    private static bool Find(World world, in IOContext io, string input)
    {
        if (world.Has<LogicTimer>(io.Self)) return true;
        Log.Warn(LogCat.Events, $"I/O: {input} at {World.Describe(io.Self)}, which has no timer (sage:timer)");
        return false;
    }

    internal static float Seconds(float value, float fallback, string field, in PrefabPartContext ctx)
    {
        if (value >= 0f && float.IsFinite(value)) return value;
        ctx.Warn($"{field} {value} is not a time in seconds; {fallback} is used");
        return fallback;
    }
}

// EntityIO phase, before the dispatch (see Timers for why): counts every running timer down and fires
// OnTimer when it runs out. Allocation-free: firing only adds to entity I/O's queue, safe inside a query.
// Runs in every pass (RunCondition.Always) for the `realTime` timers (issue #283), which count
// WorldTime.RealDt; the rest count the step (TickTime.Dt, 0 in a held pass) and so stand still while the
// world is paused, hit-stopped or between the steps of a slowed world.
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[System(Id, Phase.EntityIO, Before = new[] { "?sage.io.dispatch" }, Condition = RunCondition.Always)]
internal sealed class LogicTimerSystem : ISystem
{
    public const string Id = "sage.logic.timers";

    private readonly World _world;
    private readonly Query<LogicTimer> _timers;

    public LogicTimerSystem(World world)
    {
        _world = world;
        _timers = world.Query<LogicTimer>();
    }

    public void Run(in SystemContext ctx)
    {
        float stepDt = ctx.Tick.Dt;
        float realDt = WorldTime.Of(_world).RealDt;
        if (stepDt <= 0f && realDt <= 0f) return;
        foreach (var (timers, entities) in _timers.Chunks)
        {
            var t = timers.Span;
            for (int i = 0; i < t.Length; i++)
            {
                ref var timer = ref t[i];
                if (!timer.Running) continue;
                float dt = timer.RealTime ? realDt : stepDt;
                if (dt <= 0f) continue;
                timer.Remaining -= dt;
                if (timer.Remaining > Timers.Epsilon) continue;

                var entity = entities.EntityAt(i);
                if (timer.Repeat)
                {
                    // Carried over rather than restarted, so a repeating timer does not drift; a wait of
                    // nothing fires again next tick, not in a loop.
                    timer.Remaining = MathF.Max(timer.Remaining + Timers.NextWait(ref timer, entity), 0f);
                }
                else
                {
                    timer.Running = false;
                    timer.Remaining = 0f;
                }
                _world.FireOutput(entity, Timers.OnTimer, timer.Activator);
            }
        }
    }
}
