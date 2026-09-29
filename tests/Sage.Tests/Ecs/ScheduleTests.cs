#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Migration step 4: fixed-step clock, schedules/phases, command flushing, pause, transform
// propagation and interpolation (docs/design/01 §5.2, 03 §3.5–3.6).
public class ScheduleTests
{
    public ScheduleTests() { _ = TestEnv.UserRoot; }

    // ---- FixedStepClock ------------------------------------------------------------------------

    [Fact]
    public void Clock_RunsWholeTicks_AndCarriesTheRemainder()
    {
        var clock = new FixedStepClock();
        var r = clock.Advance(1.0 / 30, tickRate: 60, maxFrameTime: 0.25, timeScale: 1);   // one 30 fps frame
        Assert.Equal(2, r.Ticks);
        Assert.Equal(1f / 60, r.TickDt, 6);
        Assert.Equal(0f, r.Alpha, 3);

        r = clock.Advance(0.010, 60, 0.25, 1);   // 10 ms: not a whole tick yet
        Assert.Equal(0, r.Ticks);
        Assert.Equal(0.6f, r.Alpha, 3);
        r = clock.Advance(0.010, 60, 0.25, 1);   // 20 ms total: one tick, 3.33 ms left over
        Assert.Equal(1, r.Ticks);
        Assert.Equal(0.2f, r.Alpha, 3);
    }

    [Fact]
    public void Clock_ClampsLongFrames_AndAppliesTimeScale()
    {
        var clock = new FixedStepClock();
        var r = clock.Advance(5.0, 60, 0.25, 1);      // a 5 s hitch catches up only 0.25 s
        Assert.Equal(15, r.Ticks);
        Assert.Equal(0.25f, r.FrameDt, 5);

        clock.Reset();
        r = clock.Advance(0.1, 10, 0.25, 0.5);        // half speed: 0.05 s of sim time
        Assert.Equal(0, r.Ticks);
        Assert.Equal(0.5f, r.Alpha, 3);
        r = clock.Advance(0.1, 10, 0.25, 0);          // paused by time scale 0
        Assert.Equal(0, r.Ticks);
    }

    // ---- Scheduling ---------------------------------------------------------------------------

    private sealed class Recorder : ISystem
    {
        private readonly string _name;
        private readonly List<string> _log;
        public Recorder(string name, List<string> log) { _name = name; _log = log; }
        public void Run(in SystemContext ctx) => _log.Add($"{ctx.Phase}:{_name}");
    }
    [System("test.schedule.a", Phase.Gameplay, Before = new[] { "test.schedule.c" })]
    private sealed class A : ISystem { public List<string> Log; public A(List<string> l) { Log = l; } public void Run(in SystemContext ctx) => Log.Add("A"); }
    [System("test.schedule.b", Phase.Gameplay, After = new[] { "test.schedule.c" })]
    private sealed class B : ISystem { public List<string> Log; public B(List<string> l) { Log = l; } public void Run(in SystemContext ctx) => Log.Add("B"); }
    [System("test.schedule.c", Phase.Gameplay)]
    private sealed class C : ISystem { public List<string> Log; public C(List<string> l) { Log = l; } public void Run(in SystemContext ctx) => Log.Add("C"); }
    [System("test.schedule.cycle_b", Phase.AI, After = new[] { "test.schedule.cycle_c" })]
    private sealed class CycleB : ISystem { public void Run(in SystemContext ctx) { } }
    [System("test.schedule.cycle_c", Phase.AI, After = new[] { "test.schedule.cycle_b" })]
    private sealed class CycleC : ISystem { public void Run(in SystemContext ctx) { } }

    [Fact]
    public void Phases_RunInOrder_FixedAndFrameSeparately()
    {
        using var world = new World("test");
        var log = new List<string>();
        world.AddSystem(new Recorder("late", log), Phase.Late);
        world.AddSystem(new Recorder("render", log), Phase.Render);
        world.AddSystem(new Recorder("gameplay", log), Phase.Gameplay);
        world.AddSystem(new Recorder("commands", log), Phase.Commands);

        world.RunFixed(1 / 60f);
        Assert.Equal(new[] { "Commands:commands", "Gameplay:gameplay", "Late:late" }, log);
        Assert.Equal(1, world.Tick);

        log.Clear();
        world.RunFrame(1 / 144f, 0.5f);
        Assert.Equal(new[] { "Render:render" }, log);
    }

    [Fact]
    public void BeforeAfter_OrderSystemsWithinAPhase_CyclesThrow()
    {
        using var world = new World("test");
        var log = new List<string>();
        // Registered B, C, A; constraints: B after C, A before C. Result must be A, C, B.
        world.AddSystem(new B(log));
        world.AddSystem(new C(log));
        world.AddSystem(new A(log));
        world.RunFixed(0.1f);
        Assert.Equal(new[] { "A", "C", "B" }, log);

        using var bad = new World("cycle");
        bad.AddSystem(new CycleB());
        Assert.Throws<InvalidOperationException>(() => bad.AddSystem(new CycleC()));
        Assert.Single(bad.Systems);   // the one that closed the cycle is not left behind
    }

    private sealed class Killer : ISystem
    {
        private readonly Query<Transform> _q;
        public Killer(World w) { _q = w.Query<Transform>(); }
        public void Run(in SystemContext ctx)
        {
            foreach (var e in _q.Entities) ctx.Commands.Destroy(e);   // structural change inside a loop
        }
    }
    private sealed class Counter : ISystem
    {
        public int Seen;
        private readonly Query<Transform> _q;
        public Counter(World w) { _q = w.Query<Transform>(); }
        public void Run(in SystemContext ctx) => Seen = _q.Count;
    }

    [Fact]
    public void BufferedChanges_ApplyAtTheEndOfEachPhase()
    {
        using var world = new World("test");
        world.Create(); world.Create();
        var sameAfterKill = new Counter(world);
        var nextPhase = new Counter(world);
        world.AddSystem(new Killer(world), Phase.Gameplay, id: "test.killer");
        world.AddSystem(sameAfterKill, Phase.Gameplay, after: new[] { "test.killer" });
        world.AddSystem(nextPhase, Phase.AI);

        world.RunFixed(0.1f);
        Assert.Equal(2, sameAfterKill.Seen);   // same phase: not applied yet
        Assert.Equal(0, nextPhase.Seen);       // next phase: applied
    }

    [Fact]
    public void Pause_SkipsFixedSystems_ButNotFrameOrAlways()
    {
        using var world = new World("test");
        var log = new List<string>();
        world.AddSystem(new Recorder("gameplay", log), Phase.Gameplay);
        world.AddSystem(new Recorder("always", log), Phase.AI, RunCondition.Always);
        world.AddSystem(new Recorder("frame", log), Phase.FrameUpdate);
        world.Paused = true;

        world.RunFixed(0.1f);
        world.RunFrame(0.1f, 0);
        Assert.Equal(new[] { "AI:always", "FrameUpdate:frame" }, log);
    }

    [Fact]
    public void DisabledSystems_DoNotRun()
    {
        using var world = new World("test");
        var log = new List<string>();
        var info = world.AddSystem(new Recorder("x", log), Phase.Gameplay);
        info.Enabled = false;
        world.RunFixed(0.1f);
        Assert.Empty(log);
        Assert.Equal("Recorder", info.Name);
        Assert.Equal("Fixed.Gameplay/Recorder", info.ProfileName);
    }

    // ---- Transforms ---------------------------------------------------------------------------

    private sealed class MoveX : ISystem
    {
        private readonly Query<Transform> _q;
        public MoveX(World w) { _q = w.Query<Transform>(); }
        public void Run(in SystemContext ctx)
        {
            foreach (var (t, _) in _q.Chunks)
                foreach (ref var tr in t.Span) tr.LocalPosition.X += 1;
        }
    }

    [Fact]
    public void Propagation_KeepsPreviousAndCurrent_ForInterpolation()
    {
        using var world = new World("test");
        var e = world.Create(Transform.At(new Vector3(0, 0, 0)));
        Assert.Equal(Vector3.Zero, world.Get<GlobalTransform>(e).Previous.Position);   // spawned: no jump from origin
        world.AddSystem(new MoveX(world), Phase.Gameplay);

        world.RunFixed(0.1f);
        world.RunFixed(0.1f);
        ref var g = ref world.Get<GlobalTransform>(e);
        Assert.Equal(1, g.Previous.Position.X);
        Assert.Equal(2, g.Current.Position.X);
        Assert.Equal(1.25f, g.Interpolated(0.25f).Position.X, 5);
    }

    [Fact]
    public void Propagation_ComposesThroughTheHierarchy()
    {
        using var world = new World("test");
        var parentT = Transform.At(new Vector3(10, 0, 0));
        parentT.LocalRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);   // +X local → -Z world
        parentT.LocalScale = new Vector3(2, 2, 2);
        var parent = world.Create(parentT, "rider");
        var child = world.Create(Transform.At(new Vector3(1, 0, 0)), "sword");
        var grandchild = world.Create(Transform.At(new Vector3(0, 1, 0)), "gem");
        world.SetParent(child, parent);
        world.SetParent(grandchild, child);

        world.RunFixed(0.1f);

        var c = world.Get<GlobalTransform>(child).Current;
        Assert.True(Vector3.Distance(new Vector3(10, 0, -2), c.Position) < 1e-4f, $"child at {c.Position}");
        Assert.Equal(new Vector3(2, 2, 2), c.Scale);
        var gc = world.Get<GlobalTransform>(grandchild).Current;
        Assert.True(Vector3.Distance(new Vector3(10, 2, -2), gc.Position) < 1e-4f, $"grandchild at {gc.Position}");
    }

    [Fact]
    public void Teleport_SnapsBothPoses()
    {
        using var world = new World("test");
        var e = world.Create();
        world.Teleport(e, Transform.At(new Vector3(100, 0, 0)));
        var g = world.Get<GlobalTransform>(e);
        Assert.Equal(100, g.Previous.Position.X);
        Assert.Equal(100, g.Current.Position.X);
        Assert.Equal(100, world.Get<Transform>(e).LocalPosition.X);
    }

    [Fact]
    public void SteadyStateTicksAndFrames_DoNotAllocate()   // 02 §4.6
    {
        using var world = new World("alloc");
        for (int i = 0; i < 200; i++) world.Create();
        world.AddSystem(new MoveX(world), Phase.Gameplay);
        world.AddSystem(new MoveX(world), Phase.FrameUpdate);
        for (int i = 0; i < 3; i++) { world.RunFixed(0.01f); world.RunFrame(0.01f, 0.5f); Profiler.EndFrame(); }   // warm up

        AllocationProbe.AssertNone(100, () => { world.RunFixed(0.01f); world.RunFrame(0.01f, 0.5f); Profiler.EndFrame(); });
    }

    // A failing zero-allocation test names what allocated (AllocationProbe), in the run that failed.
    [Fact]
    public void AnAllocationProbe_NamesTheSystemThatAllocated()
    {
        using var world = new World("probed");
        for (int i = 0; i < 10; i++) world.Create();
        world.AddSystem(new MoveX(world), Phase.Gameplay);
        world.AddSystem(new Allocates(), Phase.Late);
        world.RunFixed(0.01f);
        Profiler.EndFrame();

        var report = AllocationProbe.Measure(10, () => { world.RunFixed(0.01f); Profiler.EndFrame(); });
        Assert.True(report.Bytes > 0);
        Assert.Contains("Fixed.Late/Allocates", report.Text);
        Assert.DoesNotContain("MoveX", report.Text);
        Assert.False(Profiler.TrackAllocations);   // off again afterwards

        Assert.Equal(0, AllocationProbe.Measure(10, () => { }).Bytes);
    }

    private sealed class Allocates : ISystem
    {
        public object? Last;
        public void Run(in SystemContext ctx) => Last = new byte[64];
    }

    [Fact]
    public void Profiler_RecordsPhasesAndSystems()
    {
        Assert.True(Profiler.Enabled);   // dev build
        using var world = new World("profiled");
        world.AddSystem(new Recorder("p", new()), Phase.EntityIO);
        world.RunFixed(0.1f);
        Profiler.EndFrame();

        var phase = Profiler.Find("Fixed.EntityIO");
        var system = Profiler.Find("Fixed.EntityIO/Recorder");
        Assert.NotNull(phase);
        Assert.NotNull(system);
        Assert.True(system!.LastCalls >= 1);
        Assert.True(system.Order > phase!.Order);   // appears after its phase

        Profiler.EndFrame();
        Assert.Equal(0, system.LastCalls);   // counters reset per frame
    }
}
