#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Animation events (issue #119, docs/design/12 "As built (animation events)"): `anim_events` records, the
// animator raising each clip event once per crossing (loops, LOD and cross-fades included), events
// driving transitions, sprites as graph leaves, and combat landing its blow on `hit` on the ticks it
// always did. Against SkinnedModelBuilder's rig: "idle" lasts 2 s, "walk" 1 s, "run" 0.5 s.
public class AnimationEventTests
{
    public AnimationEventTests() { _ = TestEnv.UserRoot; }

    private const float Dt = 1f / 60f;

    // Every event the animator raised, with the tick it was raised on. Preallocated: raising allocates nothing.
    private sealed class Recorder : IAnimationEventSink
    {
        public readonly List<(long Tick, Entity Entity, string Name)> Raised = new(4096);
        public void Raise(World world, Entity entity, string name) => Raised.Add((world.Tick, entity, name));
        public int Count(Entity entity, string name) => Raised.Count(r => r.Entity == entity && r.Name == name);
        public long[] Ticks(Entity entity, string name) => Raised.Where(r => r.Entity == entity && r.Name == name).Select(r => r.Tick).ToArray();
    }

    private const string Walker = """
    [
      { "type": "anim_events", "id": "rig", "model": "models/rig.glb",
        "clips": { "walk": [ { "time": 0.25, "name": "quarter" }, { "time": 1.0, "name": "end" }, { "time": 0.0, "name": "start" } ],
                   "run":  [ { "time": 0.1, "name": "early" }, { "time": 0.4, "name": "late" } ] } },
      { "type": "anim_graph", "id": "walker", "initial": "walk", "fade": 0.5, "ease": "Linear",
        "params": { "stop": { "kind": "Trigger" }, "go": { "kind": "Trigger" }, "quarter": { "kind": "Trigger" } },
        "states": {
          "walk":    { "clip": "walk" },
          "halt":    { "clip": "idle" },
          "sprint":  { "clip": "run", "loop": false } },
        "transitions": [ { "to": "halt", "on": "stop" }, { "to": "sprint", "on": "go" } ] },
      { "type": "prefab", "id": "walker", "parts": { "animator": { "graph": "walker", "model": "models/rig.glb" } } }
    ]
    """;

    // What entity I/O delivered to the "Record" input, with the tick (one test uses it).
    [ThreadStatic] private static List<(long Tick, string Parameter)>? _heard;
    private static List<(long Tick, string Parameter)> Heard => _heard ??= new();

    private static (HeadlessApp App, Recorder Sink) NewGame(string content = Walker)
    {
        var files = new MountFixture();
        files.Write("game", "data/walker.json", content);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files)
            .OnRegistered(a => a.Engine.Inputs.Register("Record", (World world, in IOContext io) => Heard.Add((world.Tick, io.Parameter))))
            .Boot("events");
        var sink = new Recorder();
        app.World.Resources.Replace<IAnimationEventSink>(sink);
        return (app, sink);
    }

    private static void Tick(World world, int times = 1)
    {
        for (int i = 0; i < times; i++) world.RunFixed(Dt);
    }

    // Acceptance: an anim_events record fills the clip's Events, and each event is raised once every time
    // the clip's time crosses it — at 0.25 s into each pass of a 1 s loop, at its end, and at its start on
    // the pass that begins it (the first tick, and every wrap).
    [Xunit.Fact]
    public void EachEventIsRaisedOncePerCrossingThroughLoopWraps()
    {
        var (app, sink) = NewGame();
        using (app)
        {
            var world = app.World;
            var walker = world.Spawn(new RecordId("game", "walker"), Vector3.Zero);
            var walk = app.Engine.Animations.TryGet(AssetPath.Intern("models/rig.glb"), out var set) ? set.FindClip("walk")! : null;
            Assert.NotNull(walk);
            Assert.Equal(new[] { "start", "quarter", "end" }, walk!.Events.Select(e => e.Name).ToArray());

            Tick(world, 210);                                 // 3.5 s: three and a half passes

            // "start" on the first tick the animator steps, and again on each wrap, with the end of the
            // pass before; "quarter" 15 ticks into each pass (0.25 s at 60 Hz).
            var starts = sink.Ticks(walker, "start");
            var ends = sink.Ticks(walker, "end");
            long first = starts[0];
            Assert.Equal(new long[] { first + 14, first + 74, first + 134, first + 194 }, sink.Ticks(walker, "quarter"));
            Assert.Equal(3, ends.Length);
            Assert.Equal(4, starts.Length);
            Assert.Equal(ends, starts.Skip(1).ToArray());      // a wrap crosses the end and the start, once each
            Assert.Equal(60, ends[1] - ends[0]);
        }
    }

    // Acceptance: animation LOD samples a distant animator less often, but its time steps every tick, so
    // its events fire exactly as often, on the same ticks, as a near one's.
    [Xunit.Fact]
    public void DistantAnimatorsRaiseTheirEventsOnTheSameTicks()
    {
        var (app, sink) = NewGame();
        using (app)
        {
            var world = app.World;
            var near = world.Spawn(new RecordId("game", "walker"), new Vector3(5, 0, 0));
            var far = world.Spawn(new RecordId("game", "walker"), new Vector3(100, 0, 0));
            var views = world.Resources.Get<CameraViews>();
            for (int t = 0; t < 150; t++)
            {
                views.Begin();
                views.SetScreen(new CameraView { Target = "", Position = Vector3.Zero, Rotation = Quaternion.Identity });
                views.End();
                Tick(world);
            }
            Assert.True(Animators.TryGetPose(world, far, out _));
            Assert.Equal(3, sink.Count(far, "quarter"));
            Assert.Equal(sink.Ticks(near, "quarter"), sink.Ticks(far, "quarter"));
            Assert.Equal(sink.Ticks(near, "end"), sink.Ticks(far, "end"));
        }
    }

    // Events drive transitions: an event sets the trigger param of its name, on the tick it is raised.
    [Xunit.Fact]
    public void AnEventSetsTheTriggerOfItsName()
    {
        const string graph = """
        [
          { "type": "anim_events", "id": "rig", "model": "models/rig.glb", "clips": { "walk": [ { "time": 0.25, "name": "quarter" } ] } },
          { "type": "anim_graph", "id": "walker", "initial": "walk", "fade": 0,
            "params": { "quarter": { "kind": "Trigger" } },
            "states": { "walk": { "clip": "walk", "transitions": [ { "to": "halt", "on": "quarter" } ] }, "halt": { "clip": "idle" } } },
          { "type": "prefab", "id": "walker", "parts": { "animator": { "graph": "walker", "model": "models/rig.glb" } } }
        ]
        """;
        var (app, sink) = NewGame(graph);
        using (app)
        {
            var world = app.World;
            var walker = world.Spawn(new RecordId("game", "walker"), Vector3.Zero);
            Tick(world, 14);
            Assert.Equal("walk", Animators.StateOf(world, walker));
            Tick(world);
            Assert.Equal(1, sink.Count(walker, "quarter"));
            Assert.Equal("halt", Animators.StateOf(world, walker));   // the same tick
            Assert.Equal(0f, Animators.GetParam(world, walker, "quarter"));
        }
    }

    // Cross-fades: the state being entered raises its events from its first tick; the one being left
    // raises its own while it still shows more than the one entered (its eased weight above a half).
    [Xunit.Fact]
    public void TheStateBeingLeftRaisesItsEventsWhileItShowsMoreThanHalf()
    {
        var (app, sink) = NewGame();
        using (app)
        {
            var world = app.World;
            var walker = world.Spawn(new RecordId("game", "walker"), Vector3.Zero);
            Tick(world, 3);
            Assert.True(Animators.SetTrigger(world, walker, "go"));   // walk → sprint (run, once), a 0.5 s linear fade
            Tick(world);
            Assert.Equal("sprint", Animators.StateOf(world, walker));
            Assert.True(world.Get<Animator>(walker).Layers![0].Fading);
            Tick(world, 5);
            Assert.Equal(1, sink.Count(walker, "early"));             // the new state's, 0.1 s in, mid-fade
            Assert.Equal(0, sink.Count(walker, "quarter"));

            // walk was 4 ticks in; its 0.25 s event is 11 ticks into the fade (weight 1 - 11/30 > 0.5).
            Tick(world, 10);
            Assert.Equal(1, sink.Count(walker, "quarter"));
            Assert.Equal(0, sink.Count(walker, "late"));               // run's 0.4 s: 24 ticks in
            // Its end (1 s) would be 56 ticks in: long past the fade, and never raised.
            Tick(world, 60);
            Assert.Equal(0, sink.Count(walker, "end"));
            Assert.Equal(1, sink.Count(walker, "early"));             // a one-shot raises each once
            Assert.Equal(1, sink.Count(walker, "late"));
        }
    }

    // OnAnimEvent: entity I/O hears every event, with its name as the value.
    [Xunit.Fact]
    public void EventsReachEntityIOAsOnAnimEvent()
    {
        var (app, _) = NewGame();
        using (app)
        {
            var world = app.World;
            var walker = world.Spawn(new RecordId("game", "walker"), Vector3.Zero);
            Heard.Clear();
            var recorder = world.Create();
            recorder.Name = "recorder";
            world.Add(walker, new IOConnections
            {
                Wires = new[] { new Connection { Output = Animators.AnimEventOutput, Target = "recorder", Input = "Record" } },
            });
            Tick(world, 72);    // start and quarter of the first pass, its end and the second's start
            Assert.Equal(new[] { "start", "quarter", "end", "start" }, Heard.Select(h => h.Parameter).ToArray());
        }
    }

    // A record without a model, an event without a name or with a negative time: load errors.
    [Xunit.Fact]
    public void AnimEventsAreCheckedAtLoad()
    {
        var files = new MountFixture();
        files.Write("game", "data/bad.json", """
        [ { "type": "anim_events", "id": "nomodel", "clips": { "walk": [ { "time": 0.1, "name": "x" } ] } },
          { "type": "anim_events", "id": "bad", "model": "models/rig.glb", "clips": { "walk": [ { "time": -1, "name": "" } ] } } ]
        """);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().Mount(files).Boot("bad");
        Assert.Equal(3, app.Records.ErrorCount);
    }

    // Zero allocation: a hundred animators crossing events every few ticks, raised to a sink that sends
    // them on the event bus as Sage.Gameplay's does, and to entity I/O, allocate nothing per tick.
    [Xunit.Fact]
    public void RaisingEventsAllocatesNothingPerTick()
    {
        var files = new MountFixture();
        files.Write("game", "data/crowd.json", """
        [
          { "type": "anim_events", "id": "rig", "model": "models/rig.glb",
            "clips": { "walk": [ { "time": 0.0, "name": "a" }, { "time": 0.3, "name": "b" }, { "time": 0.6, "name": "go" } ],
                       "run":  [ { "time": 0.2, "name": "c" }, { "time": 0.5, "name": "back" } ] } },
          { "type": "anim_graph", "id": "crowd", "initial": "walk", "fade": 0.1,
            "params": { "go": { "kind": "Trigger" }, "back": { "kind": "Trigger" } },
            "states": { "walk": { "clip": "walk" }, "run": { "clip": "run", "loop": false } },
            "transitions": [ { "to": "run", "on": "go" }, { "to": "walk", "on": "back" } ] },
          { "type": "prefab", "id": "walker", "parts": { "animator": { "graph": "crowd", "model": "models/rig.glb" } } }
        ]
        """);
        SkinnedModelBuilder.Write(Path.Combine(files.Dir("game"), "models", "rig.glb"), withRun: true);
        files.Mount("game", "game");
        using var app = HeadlessApp.Bare().With(new PhysicsModule(), new EntityIOModule()).Mount(files).Boot("crowd");
        var world = app.World;
        var bus = new BusSink();
        world.Resources.Replace<IAnimationEventSink>(bus);
        var reader = world.Events.Reader<Blip>("test");    // someone reads them, so the queue keeps them a while
        var walkers = new Entity[100];
        for (int i = 0; i < walkers.Length; i++) walkers[i] = world.Spawn(new RecordId("game", "walker"), new Vector3(i, 0, 0));

        void Step()
        {
            world.RunFixed(Dt);
            Profiler.EndFrame();
            foreach (ref readonly var _ in reader.Read()) { }
        }
        for (int i = 0; i < 180; i++) Step();
        Assert.True(bus.Raised > 500, $"only {bus.Raised} events raised");
        // Entity I/O needs physics here; neither animation nor the physics step allocates (the step's old 40
        // bytes a tick were a Stopwatch, #273).
        long animationBefore = ScopeBytes("Fixed.Animation"), physicsBefore = ScopeBytes("Fixed.Physics");
        var report = AllocationProbe.Measure(300, Step);
        long animation = ScopeBytes("Fixed.Animation") - animationBefore, physics = ScopeBytes("Fixed.Physics") - physicsBefore;
        Assert.True(animation == 0 && physics == 0 && report.Bytes == 0, report.ToString());
    }

    private static long ScopeBytes(string name)
    {
        foreach (var entry in Profiler.All)
            if (entry.Name == name) return entry.AllocatedBytes;
        return 0;
    }

    [GameEvent]
    private readonly record struct Blip(Entity Entity, string Name);

    private sealed class BusSink : IAnimationEventSink
    {
        public int Raised;
        public void Raise(World world, Entity entity, string name)
        {
            Raised++;
            world.Events.Send(new Blip(entity, name));
        }
    }
}
