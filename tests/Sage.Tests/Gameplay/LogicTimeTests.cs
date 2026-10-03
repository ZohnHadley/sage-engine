#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Time in level logic (issue #90): entity I/O's clock and its saved queue, timers (`sage:timer`) and
// tweens (`sage:tween`).
public class LogicTimeTests
{
    public LogicTimeTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // What arrived where, and on which tick: a `Record` input that notes its parameter.
    private sealed class Arrivals
    {
        public readonly List<(long Tick, string Entity, string Parameter)> List = new();
        public int Count(string parameter) => List.Count(a => a.Parameter == parameter);
        public long TickOf(string parameter) => List.Single(a => a.Parameter == parameter).Tick;
    }

    private static HeadlessAppBuilder WithRecorder(HeadlessAppBuilder builder, Arrivals arrivals) =>
        builder.OnRegistered(app => app.Engine.Inputs.Register("Record", (World world, in IOContext io) =>
            arrivals.List.Add((world.Tick, io.Self.Name ?? "", io.Parameter))));

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    // A scene with a button wired to a door (a one-shot wire with a one-second delay), a timer and a lift:
    // persistent, so a save carries them and a load rebuilds them — wires and all.
    private const string Scene = """
    [
      { "type": "prefab", "id": "thing", "name": "thing" },
      { "type": "prefab", "id": "lift", "name": "lift",
        "parts": { "tween": { "channel": "Position", "target": [0, 4, 0], "relative": true, "duration": 2, "ease": "QuadInOut" } } },
      { "type": "prefab", "id": "blinker", "name": "blinker",
        "parts": { "timer": { "interval": 0.5, "spread": 0.2, "repeat": true, "seed": 42 } } },
      { "type": "scene", "id": "logic",
        "place": [
          { "prefab": "thing", "at": [0, 0, 0], "name": "button",
            "outputs": [ { "output": "OnTimer", "target": "door", "input": "Record", "parameter": "opened", "delay": 1, "times": 1 } ] },
          { "prefab": "thing", "at": [5, 0, 0], "name": "door" },
          { "prefab": "lift", "at": [0, 0, 9], "name": "lift",
            "outputs": [ { "output": "OnTweenDone", "target": "door", "input": "Record", "parameter": "arrived" } ] },
          { "prefab": "blinker", "at": [0, 0, 0], "name": "blinker",
            "outputs": [ { "output": "OnTimer", "target": "door", "input": "Record", "parameter": "blink" } ] }
        ] }
    ]
    """;

    private static HeadlessApp SceneApp(Arrivals arrivals)
    {
        var app = WithRecorder(HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).File("data/logic.json", Scene), arrivals)
            .StartScene("sage:logic").Boot("logic");
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        return app;
    }

    // ---- Entity I/O: the clock and the saved queue ----------------------------------------------------

    // The acceptance test: a save taken half-way through a one-second delay, loaded, delivers the input
    // on the tick it would have — 30 ticks after the load, as it was 30 ticks after the save — and the
    // wire that sent it stays spent.
    [Fact]
    public void ADelayedInputSavedHalfWayArrivesOnTheTickItWould_AndItsWireStaysSpent()
    {
        var arrivals = new Arrivals();
        using var app = SceneApp(arrivals);
        var world = app.World;
        Tick(world);

        var button = world.FindByName("button");
        world.FireOutput(button, "OnTimer");
        long fired = world.Tick;
        Tick(world, 30);                                            // half-way
        Assert.Equal(1, world.IO().PendingCount);
        Assert.True(app.Engine.Saves.Save("half"));
        long savedAt = world.Tick;

        Tick(world, 40);                                            // on to the end, without the save
        Assert.Equal(fired + 60, arrivals.TickOf("opened"));

        arrivals.List.Clear();
        Assert.True(app.Engine.Saves.Load("half"));
        long loadedAt = world.Tick;
        Assert.Equal(1, world.IO().PendingCount);
        Tick(world, 29);
        Assert.Equal(0, arrivals.Count("opened"));                  // not a tick early
        Tick(world);
        Assert.Equal(loadedAt + (fired + 60 - savedAt), arrivals.TickOf("opened"));
        Assert.Equal("door", arrivals.List.Single(a => a.Parameter == "opened").Entity);

        // `times: 1`, and it had fired: the load kept the count, so it does not fire again.
        world.FireOutput(world.FindByName("button"), "OnTimer");
        Tick(world, 90);
        Assert.Equal(1, arrivals.Count("opened"));
    }

    // A save from before #90 has no `entity_io`: it loads as nothing on its way and every wire unfired.
    [Fact]
    public void ASaveWithoutEntityIO_LoadsWithNothingPendingAndEveryWireUnfired()
    {
        var arrivals = new Arrivals();
        using var app = SceneApp(arrivals);
        var world = app.World;
        Tick(world);
        Assert.True(app.Engine.Saves.Save("old"));

        // Strip entity_io out of the file, as a save written before it existed would be.
        string file = Path.Combine(app.Engine.Saves.Root, "old", "world_logic.json");
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.True(root["resources"]!.AsObject().Remove("entity_io"));
        File.WriteAllText(file, root.ToJsonString());

        world.FireOutput(world.FindByName("button"), "OnTimer");     // spent, and something pending
        Tick(world);
        Assert.True(app.Engine.Saves.Load("old"));
        Assert.Equal(0, world.IO().PendingCount);
        world.FireOutput(world.FindByName("button"), "OnTimer");     // unfired again
        Tick(world, 61);
        Assert.Equal(1, arrivals.Count("opened"));
    }

    // What entity_io writes: the input with its time left and its target by persistent id and by name,
    // and the wire's count by entity and index.
    [Fact]
    public void TheSavedQueueNamesItsTargetsByIdentityAndByName()
    {
        var arrivals = new Arrivals();
        using var app = SceneApp(arrivals);
        var world = app.World;
        Tick(world);
        world.FireOutput(world.FindByName("button"), "OnTimer");
        Tick(world, 15);
        Assert.True(app.Engine.Saves.Save("shape"));

        var saved = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(Path.Combine(app.Engine.Saves.Root, "shape", "world_logic.json")))!["resources"]!["entity_io"]!;
        Assert.Equal(1, (int)saved["version"]!);
        var pending = saved["data"]!["Pending"]!.AsArray().Single()!;
        Assert.Equal("Record", (string?)pending["Input"]);
        Assert.Equal("opened", (string?)pending["Parameter"]);
        Assert.Equal("door", (string?)pending["TargetName"]);
        Assert.Equal(world.Get<Persistent>(world.FindByName("door")).Id.ToString(), (string?)pending["Target"]);
        Assert.Equal(0.75, (double)pending["Remaining"]!, 3);
        var wire = saved["data"]!["Wires"]!.AsArray().Single()!;
        Assert.Equal("button", (string?)wire["Name"]);
        Assert.Equal(0, (int)wire["Wire"]!);
        Assert.Equal(1, (int)wire["Fired"]!);
    }

    // An output fired before the dispatch (a trigger, in PostPhysics) measures its delay from the tick it
    // fired in, as one fired from the dispatch does: it used to measure from the tick before (#80's note).
    [Fact]
    public void ATriggerWiresDelayCountsFromTheSameTickAsADispatchWires()
    {
        var arrivals = new Arrivals();
        using var app = WithRecorder(HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()), arrivals).Boot("io");
        var world = app.World;
        var space = world.Resources.Get<IPhysicsWorld>();

        var recorder = world.Create(Transform.At(new Vector3(50, 0, 0)), "recorder");
        recorder.Name = "recorder";
        var relay = world.Create(Transform.At(new Vector3(60, 0, 0)), "relay");
        relay.Name = "relay";
        world.Add(relay, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnTimer", Target = "recorder", Input = "Record", Parameter = "dispatch", Delay = 1f } },
        });

        // The trigger: one wire straight to the recorder, one through the relay (fired from the dispatch
        // on the same tick, since its delay is 0), both with a one-second delay at the end.
        var trigger = world.Create(Transform.At(Vector3.Zero), "trigger");
        var corners = new[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
        };
        world.Add(trigger, space.AddHull(trigger, corners, Vector3.Zero, 0, isTrigger: true));
        world.Add(trigger, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnStartTouch", Target = "recorder", Input = "Record", Parameter = "trigger", Delay = 1f, Times = 1 },
                new Connection { Output = "OnStartTouch", Target = "relay", Input = "Fire", Parameter = "OnTimer", Times = 1 },
            },
        });
        var faller = world.Create(Transform.At(new Vector3(0, 3, 0)), "faller");
        world.Add(faller, Collider.Box(new Vector3(0.5f, 0.5f, 0.5f)));
        world.Add(faller, new RigidBody { Kind = BodyKind.Dynamic, Mass = 10f });

        Tick(world, 180);
        Assert.Equal(1, arrivals.Count("trigger"));
        Assert.Equal(arrivals.TickOf("dispatch"), arrivals.TickOf("trigger"));
    }

    // A paused world's I/O clock stands still, as its dispatch does.
    [Fact]
    public void APausedWorldsDelaysWait()
    {
        var arrivals = new Arrivals();
        using var app = WithRecorder(HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()), arrivals).Boot("io");
        var world = app.World;
        var target = world.Create(Transform.At(Vector3.Zero), "t");
        world.IO().FireInput(target, "Record", "late", delay: 0.5f);
        Tick(world, 10);
        world.Paused = true;
        Tick(world, 100);
        world.Paused = false;
        Assert.Empty(arrivals.List);
        Tick(world, 19);
        Assert.Empty(arrivals.List);
        Tick(world);
        Assert.Single(arrivals.List);
    }

    // FireInput by name (#89's `fire` action uses it): found when it arrives, so a target spawned in the
    // meantime still gets it.
    [Fact]
    public void AnInputSentByNameFindsATargetSpawnedMeanwhile()
    {
        var arrivals = new Arrivals();
        using var app = WithRecorder(HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()), arrivals).Boot("io");
        var world = app.World;
        world.IO().FireInput("latecomer", "Record", "hello", delay: 0.1f);
        Tick(world);
        var late = world.Create(Transform.At(Vector3.Zero), "latecomer");
        late.Name = "latecomer";
        Tick(world, 10);
        Assert.Equal("latecomer", arrivals.List.Single().Entity);
    }

    // ---- Timers --------------------------------------------------------------------------------------

    [Fact]
    public void TimersAndTweensAreTheEngines_InAGameWithNoPlugins()
    {
        using var bare = HeadlessApp.Bare().WithEngineContent().Boot("bare");
        var engine = bare.Engine;
        foreach (var input in new[] { "TimerStart", "TimerStop", "TimerReset", "TweenTo", "TweenStop" })
        {
            Assert.True(engine.Inputs.Has(input), input);
            Assert.Equal("sage.core", engine.Registrations.OwnerOf("entity input", input));
        }
        Assert.True(engine.Outputs.Has("OnTimer"));
        Assert.True(engine.Outputs.Has("OnTweenDone"));
        Assert.True(engine.Prefabs.TryGet("timer", out _));
        Assert.True(engine.Prefabs.TryGet("tween", out _));
        foreach (var id in new[] { "sage.logic.timers", "sage.logic.tweens", "sage.camera.blend" })
        {
            var system = bare.World.Systems.Find(id)!;
            Assert.Equal(Phase.EntityIO, system.Phase);
            Assert.Equal("sage.core", system.Owner);
        }

        // The engine's logic_timer prefab: a stopped, repeating one-second timer.
        var timer = bare.World.Spawn(new RecordId("sage", "logic_timer"));
        var t = bare.World.Get<LogicTimer>(timer);
        Assert.Equal(1f, t.Interval);
        Assert.True(t.Repeat);
        Assert.False(t.Running);
    }

    // A timer started by an input fires on the tick a wire with the same delay, sent alongside that input,
    // arrives; a repeating one every interval after, without drifting.
    [Fact]
    public void ATimerFiresOnTheTickADelayedWireArrives_AndRepeatsWithoutDrift()
    {
        var arrivals = new Arrivals();
        using var app = WithRecorder(HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()), arrivals).Boot("io");
        var world = app.World;
        var timer = world.Create(Transform.At(Vector3.Zero), "timer");
        timer.Name = "timer";
        world.Add(timer, new LogicTimer { Interval = 1f, Repeat = true });
        world.Add(timer, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnTimer", Target = "!self", Input = "Record", Parameter = "tick" } },
        });
        var io = world.IO();
        io.FireInput(timer, "TimerStart");
        Tick(world);                                                // delivered: it starts this tick
        io.FireInput(timer, "Record", "wire", delay: 1f);           // a second from the same tick
        Tick(world, 60 * 10 + 5);

        var ticks = arrivals.List.Where(a => a.Parameter == "tick").Select(a => a.Tick).ToList();
        Assert.Equal(arrivals.TickOf("wire"), ticks[0]);
        Assert.Equal(10, ticks.Count);
        for (int i = 1; i < ticks.Count; i++) Assert.Equal(60, ticks[i] - ticks[i - 1]);

        // Stopped: nothing more. Started with a number: that is the new interval.
        io.FireInput(timer, "TimerStop");
        Tick(world, 120);
        Assert.Equal(10, arrivals.Count("tick"));
        io.FireInput(timer, "TimerStart", "0.25");
        Tick(world, 61);
        Assert.Equal(14, arrivals.Count("tick"));
        Assert.Equal(0.25f, world.Get<LogicTimer>(timer).Interval);
    }

    [Fact]
    public void AOneShotTimerFiresOnceAndStops_AndResetStartsTheWaitAgain()
    {
        var arrivals = new Arrivals();
        using var app = WithRecorder(HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()), arrivals).Boot("io");
        var world = app.World;
        var timer = world.Create(Transform.At(Vector3.Zero), "once");
        world.Add(timer, new LogicTimer { Interval = 0.5f });
        world.Add(timer, new IOConnections
        {
            Wires = new[] { new Connection { Output = "OnTimer", Target = "!self", Input = "Record", Parameter = "once" } },
        });
        world.IO().FireInput(timer, "TimerStart");
        Tick(world, 20);
        world.IO().FireInput(timer, "TimerReset");                  // 20 ticks in: the wait starts over
        Tick(world, 30);
        Assert.Equal(0, arrivals.Count("once"));
        Tick(world, 100);
        Assert.Equal(1, arrivals.Count("once"));
        Assert.False(world.Get<LogicTimer>(timer).Running);
    }

    // `spread` draws each wait from the timer's own stream: two runs draw the same waits, and a save
    // carries the stream, so a load draws what the game would have.
    [Fact]
    public void ARandomTimerIsDeterministic_AcrossRunsAndASave()
    {
        List<long> Run(bool saveAndLoad)
        {
            var arrivals = new Arrivals();
            using var app = SceneApp(arrivals);
            var world = app.World;
            world.IO().FireInput(world.FindByName("blinker"), "TimerStart");
            Tick(world, 100);
            long savedAt = world.Tick, shift = 0;
            if (saveAndLoad)
            {
                Assert.True(app.Engine.Saves.Save("blink"));
                Tick(world, 37);                                    // a different future, thrown away
                Assert.True(app.Engine.Saves.Load("blink"));
                arrivals.List.RemoveAll(a => a.Tick > savedAt);
                shift = world.Tick - savedAt;                       // ticks keep counting across a load
            }
            Tick(world, 400);
            return arrivals.List.Where(a => a.Parameter == "blink").Select(a => a.Tick > savedAt ? a.Tick - shift : a.Tick).ToList();
        }

        var first = Run(false);
        var second = Run(false);
        Assert.Equal(first, second);
        Assert.True(first.Count > 5);
        var gaps = first.Zip(first.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps.Distinct().Count() > 1, "a spread of 0.2 s should vary the waits");
        Assert.All(gaps, g => Assert.InRange(g, 18, 42));           // 0.5 ± 0.2 s, in ticks
        Assert.Equal(first, Run(true));
    }

    // ---- Tweens --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", TweenChannel.Position, 0f, 4f, 0f, true, 2f, Ease.QuadInOut)]
    [InlineData("1.5", TweenChannel.Position, 0f, 4f, 0f, true, 1.5f, Ease.QuadInOut)]
    [InlineData("position 1 2 3", TweenChannel.Position, 1f, 2f, 3f, false, 2f, Ease.QuadInOut)]
    [InlineData("offset 0 -3 0 0.5 BackOut", TweenChannel.Position, 0f, -3f, 0f, true, 0.5f, Ease.BackOut)]
    [InlineData("turn 0 90 0 2 sine_in_out", TweenChannel.Rotation, 0f, 90f, 0f, true, 2f, Ease.SineInOut)]
    [InlineData("  scale 2 2 2   linear ", TweenChannel.Scale, 2f, 2f, 2f, false, 2f, Ease.Linear)]
    [InlineData("rotation 10 20 30 4", TweenChannel.Rotation, 10f, 20f, 30f, false, 4f, Ease.QuadInOut)]
    public void TweenToReadsItsParameter(string parameter, TweenChannel channel, float x, float y, float z, bool relative,
                                         float seconds, Ease ease)
    {
        var tween = new Tween { Channel = TweenChannel.Position, Target = new Vector3(0, 4, 0), Relative = true, Duration = 2f, Ease = Ease.QuadInOut };
        Assert.True(Tweens.TryRead(parameter, tween, out var c, out var goal, out bool r, out float s, out var e), parameter);
        Assert.Equal(channel, c);
        Assert.Equal(new Vector3(x, y, z), goal);
        Assert.Equal(relative, r);
        Assert.Equal(seconds, s);
        Assert.Equal(ease, e);
    }

    [Theory]
    [InlineData("position 1 2")]
    [InlineData("1 2 3 4 5")]
    [InlineData("position 1 2 3 wobble")]
    [InlineData("-1")]
    [InlineData("0,3,0")]
    public void TweenToRefusesWhatItCannotRead(string parameter)
    {
        var tween = new Tween { Duration = 1f };
        Assert.False(Tweens.TryRead(parameter, tween, out _, out _, out _, out _, out _), parameter);
    }

    // Every tick is a pure function of the elapsed time: two runs agree to the bit, a save taken
    // half-way continues exactly as the unsaved run does, and OnTweenDone fires once, on the tick the
    // duration is up.
    [Fact]
    public void TweensAreDeterministicAndSurviveASave()
    {
        (List<Vector3> Path, long Done, long Sent) Run(bool saveAndLoad)
        {
            var arrivals = new Arrivals();
            using var app = SceneApp(arrivals);
            var world = app.World;
            Tick(world);
            var lift = world.FindByName("lift");
            world.IO().FireInput(lift, "TweenTo");                  // its own: up 4 m over 2 s, QuadInOut
            long sent = world.Tick;
            var path = new List<Vector3>();
            long offset = 0;
            for (int i = 0; i < 150; i++)
            {
                if (saveAndLoad && i == 50)
                {
                    Assert.True(app.Engine.Saves.Save("lift"));
                    Tick(world, 13);
                    Assert.True(app.Engine.Saves.Load("lift"));
                    offset = 13;
                    lift = world.FindByName("lift");
                }
                Tick(world);
                path.Add(world.Get<Transform>(lift).LocalPosition);
            }
            var done = arrivals.List.Where(a => a.Parameter == "arrived").ToList();
            Assert.Single(done);
            return (path, done[0].Tick - offset, sent);
        }

        var a = Run(false);
        var b = Run(false);
        Assert.Equal(a.Path, b.Path);
        Assert.Equal(new Vector3(0, 0, 9), a.Path[0]);             // delivered this tick: it moves from the next
        Assert.Equal(new Vector3(0, 4, 9), a.Path[^1]);
        Assert.Equal(a.Sent + 1 + 120, a.Done);                      // delivered, then two seconds, on the tick
        // Half-way in time is half-way in space for an InOut curve.
        Assert.Equal(2f, a.Path[60].Y, 3);

        var saved = Run(true);
        Assert.Equal(a.Path, saved.Path);
        Assert.Equal(a.Done, saved.Done);
    }

    [Fact]
    public void ATweenTurnsAndScales_AndASecondTweenToStartsFromWhereItIs()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Boot("io");
        var world = app.World;
        var thing = world.Create(Transform.At(Vector3.Zero), "thing");
        world.Add(thing, new Tween { Duration = 1f, Ease = Ease.Linear });
        var io = world.IO();

        io.FireInput(thing, "TweenTo", "turn 0 90 0 1");
        Tick(world, 61);
        var forward = Vector3.Transform(TransformMath.Forward, world.Get<Transform>(thing).LocalRotation);
        Assert.Equal(-1f, forward.X, 4);                             // a quarter turn left: -Z to -X

        io.FireInput(thing, "TweenTo", "scale 3 3 3 1");
        Tick(world, 31);
        float half = world.Get<Transform>(thing).LocalScale.X;
        Assert.Equal(2f, half, 1);
        io.FireInput(thing, "TweenTo", "scale 1 1 1 1");            // turned round half-way: no jump
        Tick(world);
        Assert.True(MathF.Abs(world.Get<Transform>(thing).LocalScale.X - half) < 0.1f);
        Tick(world, 60);
        Assert.Equal(Vector3.One, world.Get<Transform>(thing).LocalScale);

        io.FireInput(thing, "TweenTo", "offset 0 0 5 1");
        Tick(world, 20);
        io.FireInput(thing, "TweenStop");
        Tick(world);
        var stopped = world.Get<Transform>(thing).LocalPosition;
        Tick(world, 60);
        Assert.Equal(stopped, world.Get<Transform>(thing).LocalPosition);
        Assert.False(world.Get<Tween>(thing).Playing);
    }
}

// Zero allocation per tick with timers, tweens and entity I/O running (issue #90's acceptance).
[Collection(MeasurementsCollection.Name)]
public class LogicTimeAllocationTests
{
    public LogicTimeAllocationTests() { _ = TestEnv.UserRoot; }

    private static int _count;

    [Fact]
    public void TimersTweensAndEntityIOAllocateNothingPerTick()
    {
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).OnRegistered(a =>
            a.Engine.Inputs.Register("Count", static (World w, in IOContext io) => _count++)).Boot("io");
        var world = app.World;

        // A fast repeating timer wired to a counter and, through a delay, to a lift that goes up and down.
        var timer = world.Create(Transform.At(Vector3.Zero), "timer");
        timer.Name = "timer";
        world.Add(timer, new LogicTimer { Interval = 0.1f, Spread = 0.05f, Repeat = true });
        world.Add(timer, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnTimer", Target = "!self", Input = "Count" },
                new Connection { Output = "OnTimer", Target = "counter", Input = "Count", Delay = 0.25f },
            },
        });
        var counter = world.Create(Transform.At(Vector3.Zero), "counter");
        counter.Name = "counter";
        var lift = world.Create(Transform.At(Vector3.Zero), "lift");
        lift.Name = "lift";
        world.Add(lift, new Tween { Duration = 0.5f, Ease = Ease.ElasticOut });
        world.Add(lift, new IOConnections
        {
            Wires = new[]
            {
                new Connection { Output = "OnTweenDone", Target = "!self", Input = "TweenTo", Parameter = "turn 0 45 0 0.3 SineInOut" },
                new Connection { Output = "OnTweenDone", Target = "!self", Input = "Count", Delay = 0.1f },
            },
        });
        world.IO().FireInput(timer, "TimerStart");
        world.IO().FireInput(lift, "TweenTo", "offset 0 1 0");
        // The I/O plugin needs a physics world (its trigger system); its step allocates nothing (#273).

        for (int i = 0; i < 120; i++) { world.RunFixed(1f / 60f); Profiler.EndFrame(); }   // warm: lists grown, paths JITted
        _count = 0;
        AllocationProbe.AssertNone(300, () => { world.RunFixed(1f / 60f); Profiler.EndFrame(); });
        Assert.True(_count > 50, $"the timer and the tween should have kept the I/O busy ({_count})");
    }

    [Fact]
    public void ACameraBlendAllocatesNothingPerTickOrFrame()
    {
        using var app = HeadlessApp.Gameplay().Boot("io");
        var world = app.World;
        var cam = world.Create(Transform.At(new Vector3(0, 5, 10)), "cam");
        var camera = Camera.Perspective(priority: 100);
        camera.Enabled = false;
        world.Add(cam, camera);
        world.RunFixed(1f / 60f);
        world.RunFrame(1f / 60f, 1f);
        world.IO().FireInput(cam, "CameraOn", "0 5");
        for (int i = 0; i < 10; i++) { world.RunFixed(1f / 60f); world.RunFrame(1f / 60f, 0.5f); Profiler.EndFrame(); }
        Assert.True(world.Get<CameraBlend>(cam).Active);

        AllocationProbe.AssertNone(120, () => { world.RunFixed(1f / 60f); world.RunFrame(1f / 60f, 0.5f); Profiler.EndFrame(); });
        Assert.True(world.Get<CameraBlend>(cam).Active);
    }
}
