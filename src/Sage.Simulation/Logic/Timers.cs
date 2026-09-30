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
// `seed`, or from the entity when there is none).
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

    public void Apply(in PrefabPartContext ctx)
    {
        var timer = new LogicTimer
        {
            Interval = Timers.Seconds(Interval, 1f, "interval", ctx),
            Spread = Timers.Seconds(Spread, 0f, "spread", ctx),
            Repeat = Repeat,
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
        if (timer.Random == 0) timer.Random = Seed(entity);
        float unit = Next(ref timer.Random) * 2f - 1f;          // -1..1
        return MathF.Max(0f, interval + unit * timer.Spread);
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
[Experimental("SAGE0124", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // easing, timers and tweens (#90)
[System(Id, Phase.EntityIO, Before = new[] { "?sage.io.dispatch" })]
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
        float dt = ctx.Tick.Dt;
        foreach (var (timers, entities) in _timers.Chunks)
        {
            var t = timers.Span;
            for (int i = 0; i < t.Length; i++)
            {
                ref var timer = ref t[i];
                if (!timer.Running) continue;
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
