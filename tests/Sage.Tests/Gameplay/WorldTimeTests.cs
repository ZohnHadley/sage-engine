#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The world's time (issue #283, 4m-9): scale, pause and hit-stop as a world service, honoured by the fixed
// schedule, timers, tweens and the clock, and saved.
public class WorldTimeTests
{
    public WorldTimeTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    // Notes the Dt of every pass it runs in (a step's, or 0 in a held pass).
    private sealed class StepRecorder : ISystem
    {
        public readonly List<float> Dts = new();
        public void Run(in SystemContext ctx) => Dts.Add(ctx.Tick.Dt);
    }

    // A timer of 1 s, a linear tween of 0 → 10 m over 2 s, and the clock at noon running a game minute a
    // real second: in a world at `scale`.
    private static (Entity Timer, Entity Lift) Setup(World world, double scale, bool realTime = false)
    {
        WorldTime.Of(world).Scale = scale;
        var clock = WorldClock.Of(world);
        clock.Day = 0; clock.Hour = 12; clock.Scale = 60;

        var timer = world.Create(Transform.At(Vector3.Zero), "timer");
        var t = new LogicTimer { Interval = 1f, Repeat = false, RealTime = realTime };
        Timers.Start(ref t, timer);
        world.Add(timer, t);

        var lift = world.Create(Transform.At(Vector3.Zero), "lift");
        var tween = new Tween { RealTime = realTime };
        Tweens.Begin(ref tween, world.Get<Transform>(lift), TweenChannel.Position, new Vector3(0, 10, 0), relative: false, 2f, Ease.Linear);
        world.Add(lift, tween);
        return (timer, lift);
    }

    // Done criterion: at 0.5 the timer and the tween get half as far in the same real time, and the clock
    // advances by the scaled time: half a real second of game time a real second.
    [Fact]
    public void HalfScaleHalvesTimerTweenAndClockProgress()
    {
        using var app = HeadlessApp.Bare().Boot("normal");
        var normal = app.World;
        var slow = app.CreateWorld("slow");
        var (normalTimer, normalLift) = Setup(normal, 1.0);
        var (slowTimer, slowLift) = Setup(slow, 0.5);

        Tick(normal, 30);   // half a real second
        Tick(slow, 30);
        Assert.Equal(0.5f, normal.Get<LogicTimer>(normalTimer).Remaining, 3);
        Assert.Equal(0.75f, slow.Get<LogicTimer>(slowTimer).Remaining, 3);           // a quarter second counted
        Assert.Equal(2.5f, normal.Get<Transform>(normalLift).LocalPosition.Y, 3);
        Assert.Equal(1.25f, slow.Get<Transform>(slowLift).LocalPosition.Y, 3);       // half as far

        Tick(normal, 30);   // one real second in all
        Tick(slow, 30);
        Assert.False(normal.Get<LogicTimer>(normalTimer).Running);                 // it fired
        Assert.True(slow.Get<LogicTimer>(slowTimer).Running);
        Assert.Equal(0.5f, slow.Get<LogicTimer>(slowTimer).Remaining, 3);
        Assert.Equal(0.25f, slow.Get<Tween>(slowLift).Progress, 3);

        // The clock: 1 s at x60 is a game minute; at half speed, half of one.
        Assert.Equal(12.0 + 1.0 / 60.0, WorldClock.Of(normal).Hour, 6);
        Assert.Equal(12.0 + 0.5 / 60.0, WorldClock.Of(slow).Hour, 6);
        Assert.Equal(1.0, WorldTime.Of(slow).Unscaled, 4);
        Assert.Equal(0.5, WorldTime.Of(slow).Scaled, 4);
        Assert.Equal(0.5, slow.SimTime, 4);

        Tick(slow, 60);     // and two real seconds bring the slow one where one brought the normal one
        Assert.False(slow.Get<LogicTimer>(slowTimer).Running);
        Assert.Equal(5f, slow.Get<Transform>(slowLift).LocalPosition.Y, 3);
    }

    // The step never changes length: a slowed or quickened world runs fewer or more steps of the same Dt,
    // and two runs at the same scales take the same steps.
    [Fact]
    public void AScaledWorldIsDeterministic()
    {
        static List<float> Run(double[] scales)
        {
            using var world = new World("w");
            var recorder = new StepRecorder();
            world.AddSystem(recorder, Phase.Gameplay);
            foreach (double scale in scales)
            {
                WorldTime.Of(world).Scale = scale;
                Tick(world, 37);
            }
            return recorder.Dts;
        }

        var scales = new[] { 1.0, 0.25, 0.37, 3.0, 1.0 / 3.0, 0.0, 2.5 };
        var first = Run(scales);
        Assert.Equal(first, Run(scales));
        Assert.All(first, dt => Assert.Equal(Dt, dt));                // constant: physics sees the same step
        // 37 + 9.25 + 13.69 + 111 + 12.33 + 0 + 92.5 = 275.77 steps owed, with carries between: 275 whole ones.
        Assert.Equal(275, first.Count);

        using var quarter = new World("quarter");
        var q = new StepRecorder();
        quarter.AddSystem(q, Phase.Gameplay);
        WorldTime.Of(quarter).Scale = 0.25;
        Tick(quarter, 100);
        Assert.Equal(25, q.Dts.Count);
        Assert.Equal(100L, quarter.Tick);                               // every real tick was a pass
    }

    // A hit-stop holds the world for real seconds, then lets it go; a second one keeps the longer.
    [Fact]
    public void AHitStopFreezesTheWorldForRealSeconds()
    {
        using var world = new World("w");
        var steps = new StepRecorder();
        world.AddSystem(steps, Phase.Gameplay);
        var time = WorldTime.Of(world);

        time.HitStop(0.1);
        time.HitStop(0.05);                       // the longer one stands
        Tick(world, 6);                           // 0.1 s of real ticks: no step
        Assert.Empty(steps.Dts);
        Assert.Equal(0.0, time.HitStopLeft);
        Tick(world);
        Assert.Single(steps.Dts);

        time.HitStop(double.NaN);                 // not a time: ignored
        time.HitStop(-1);
        Tick(world);
        Assert.Equal(2, steps.Dts.Count);
    }

    // The declared opt-out: a `realTime` timer and tween count real seconds through a pause, a hit-stop and
    // a slowed world, and a system that runs on real time (RunCondition.Always) reads RealDt once a tick.
    [Fact]
    public void RealTimeTimersAndTweensIgnoreScalePauseAndHitStop()
    {
        using var app = HeadlessApp.Bare().Boot("w");
        var world = app.World;
        var (timer, lift) = Setup(world, 0.5, realTime: true);
        var (scaledTimer, _) = Setup(world, 0.5);
        double realSeconds = 0;
        world.AddSystem(new RealCounter(world, s => realSeconds += s), Phase.Late, RunCondition.Always);

        world.Paused = true;
        Tick(world, 20);
        WorldTime.Of(world).HitStop(5);
        Tick(world, 20);
        world.Paused = false;
        WorldTime.Of(world).HitStopLeft = 0;
        WorldTime.Of(world).Scale = 4;           // four steps a tick: still one real tick's seconds
        Tick(world, 20);

        Assert.Equal(1.0, realSeconds, 4);
        Assert.False(world.Get<LogicTimer>(timer).Running);              // a real second: it fired
        Assert.Equal(5f, world.Get<Transform>(lift).LocalPosition.Y, 3); // half its two seconds
        // The scaled timer only counted the last twenty ticks, at four steps each: 80 steps, so it fired too,
        // but not before the world ran again.
        Assert.False(world.Get<LogicTimer>(scaledTimer).Running);
    }

    private sealed class RealCounter : ISystem
    {
        private readonly World _world;
        private readonly Action<double> _add;
        public RealCounter(World world, Action<double> add) { _world = world; _add = add; }
        public void Run(in SystemContext ctx) => _add(WorldTime.Of(_world).RealDt);
    }

    // A paused world's scaled timer stands still and its default systems do not run; only real-time ones.
    [Fact]
    public void PauseStopsScaledTimeButNotRealTime()
    {
        using var app = HeadlessApp.Bare().Boot("w");
        var world = app.World;
        var (timer, lift) = Setup(world, 1.0);
        world.Paused = true;
        Assert.True(WorldTime.Of(world).Paused);
        Tick(world, 120);
        Assert.Equal(1f, world.Get<LogicTimer>(timer).Remaining, 4);
        Assert.Equal(0f, world.Get<Transform>(lift).LocalPosition.Y);
        Assert.Equal(12.0, WorldClock.Of(world).Hour, 9);
        Assert.Equal(0.0, world.SimTime);
        Assert.Equal(2.0, WorldTime.Of(world).Unscaled, 4);
    }

    // Scale, pause, a hit-stop under way and the times are saved with the world (the `time` resource).
    [Fact]
    public void TheWorldsTimeSurvivesASave()
    {
        using var app = HeadlessApp.Bare().Boot("w");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.World;
        var time = WorldTime.Of(world);
        time.Scale = 0.5;
        Tick(world, 3);
        time.HitStop(0.25);
        world.Paused = true;

        Assert.True(app.Engine.Saves.Save("slow"));
        var saved = JsonNode.Parse(File.ReadAllText(Directory.GetFiles(Path.Combine(app.Engine.Saves.Root, "slow"), "world_*.json").Single()))!;
        Assert.Equal(0.5, (double)saved["resources"]!["time"]!["data"]!["Scale"]!);

        time.Scale = 3; time.HitStopLeft = 0; world.Paused = false;
        Assert.True(app.Engine.Saves.Load("slow"));

        var loaded = WorldTime.Of(world);
        Assert.Equal(0.5, loaded.Scale);
        Assert.True(loaded.Paused);
        Assert.True(world.Paused);
        Assert.Equal(0.25, loaded.HitStopLeft, 6);
        Assert.Equal(3 * Dt, loaded.Unscaled, 5);
        Assert.Equal(Dt, loaded.Scaled, 5);                             // one step in three half-speed ticks
        Assert.Equal(0.5 * Dt, loaded.Carry, 5);
    }

    // The host's `host_timescale` goes through each world's time, and the player's command is handed over
    // before each step: at half speed, one command every other real tick, and none lost.
    [Fact]
    public void TheHostLoopsTimeScaleRoutesThroughWorldTime()
    {
        using var app = HeadlessApp.Bare().Boot("w");
        var world = app.World;
        var loop = new HostLoop(app.Engine);
        int commanded = 0;
        var step = loop.Update(1.0 + 1e-6, 60, 2.0, 0.5, _ => commanded++);
        Assert.Equal(60, step.Ticks);                                   // real ticks
        Assert.Equal(60L, world.Tick);
        Assert.Equal(0.5, WorldTime.Of(world).HostScale);
        Assert.Equal(0.5, world.SimTime, 4);                            // thirty steps of 1/60
        Assert.Equal(30, commanded);

        WorldTime.Of(world).Scale = 2;                                  // and the world's own on top: x1
        commanded = 0;
        loop.Update(1.0, 60, 2.0, 0.5, _ => commanded++);
        Assert.Equal(60, commanded);

        world.Paused = true;                                            // paused: handed one a tick, dropped
        commanded = 0;
        loop.Update(1.0, 60, 2.0, 1.0, _ => commanded++);
        Assert.Equal(60, commanded);
        Assert.Equal(1.5, world.SimTime, 3);
    }

    // The frame's alpha is the world's own at a speed other than 1: how far it is towards its next step.
    [Fact]
    public void ASlowedWorldInterpolatesBetweenItsOwnSteps()
    {
        using var world = new World("w");
        var time = WorldTime.Of(world);
        Tick(world);
        Assert.Equal(0.3f, time.Alpha(0.3f), 5);                        // scale 1: the host's
        time.Scale = 0.5;
        Tick(world);                                                    // half a step owed
        Assert.Equal(0.5f + 0.3f * 0.5f, time.Alpha(0.3f), 4);
        time.Paused = true;
        Assert.Equal(0.5f, time.Alpha(0.3f), 4);                        // frozen where it stopped
    }

    // The data words: `{ "world_speed": 0.5 }` and `{ "hit_stop": 0.1 }` in any `then`.
    [Fact]
    public void WorldSpeedAndHitStopAreActions()
    {
        const string rules = """
            [{ "type": "gate_rule", "id": "slow", "then": [ { "world_speed": 0.25 }, { "hit_stop": 0.2 } ] }]
            """;
        using var app = HeadlessApp.Bare().With(new LogicTestPlugin()).File("data/rules.json", rules).Boot("w");
        Assert.Equal(0, app.Records.ErrorCount);
        var world = app.World;
        Conditions.Run(world, default, app.Records.Get<GateRule>(new RecordId("sage", "slow")).Then);
        Assert.Equal(0.25, WorldTime.Of(world).Scale);
        Assert.Equal(0.2, WorldTime.Of(world).HitStopLeft, 6);
    }
}
