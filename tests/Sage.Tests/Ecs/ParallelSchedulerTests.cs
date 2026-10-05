#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Sage.Tests;

using Assert = Xunit.Assert;

[Component("test:par_a")] public struct ParA : IComponent { public float V; }
[Component("test:par_b")] public struct ParB : IComponent { public float V; }
[Component("test:par_c")] public struct ParC : IComponent { public int N; }
[Component("test:par_d")] public struct ParD : IComponent { public float V; }
[Component("test:par_sum")] public struct ParSum : IComponent { public float V; }
[Tag("test:par_flag")] public struct ParFlag : ITag { }
[GameEvent] public struct ParPing { public Entity Entity; public float V; }

// The parallel scheduler and access declarations (issue #288, docs/design/03 §3.5): systems that say what
// they touch run side by side when they do not conflict, and the world ends up exactly where running them
// one after the other leaves it.
public class ParallelSchedulerTests
{
    public ParallelSchedulerTests() { _ = TestEnv.UserRoot; }

    // ---- a phase's worth of systems, declared and not -----------------------------------------------

    // Stage 0 with DriftD; reads B, which GrowB writes, so GrowB waits for it.
    [System("test.par.move_a", Phase.Gameplay)]
    private sealed class MoveA : IDeclaresAccess
    {
        private readonly Query<ParA, ParB> _q;
        public MoveA(World w) => _q = w.Query<ParA, ParB>();
        public void Declare(SystemAccess access) => access.Writes<ParA>().Reads<ParB>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (a, b, _) in _q.Chunks)
            {
                var sa = a.Span; var sb = b.Span;
                for (int i = 0; i < sa.Length; i++) sa[i].V = sa[i].V * 0.97f + sb[i].V * 0.5f + (sa[i].V > 1 ? 0.01f : 0.02f);
            }
        }
    }

    [System("test.par.grow_b", Phase.Gameplay)]
    private sealed class GrowB : IDeclaresAccess
    {
        private readonly Query<ParB> _q;
        public GrowB(World w) => _q = w.Query<ParB>();
        public void Declare(SystemAccess access) => access.Writes<ParB>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (b, _) in _q.Chunks)
                foreach (ref var v in b.Span) v.V = v.V * 0.99f + 0.25f;
        }
    }

    // Reads what MoveA writes, so after it; beside GrowB. Sends pings SumPings reads.
    [System("test.par.count_c", Phase.Gameplay)]
    private sealed class CountC : IDeclaresAccess
    {
        private readonly Query<ParC, ParA> _q;
        public CountC(World w) => _q = w.Query<ParC, ParA>();
        public void Declare(SystemAccess access) => access.Writes<ParC>().Reads<ParA>().Sends<ParPing>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (c, a, entities) in _q.Chunks)
            {
                var sc = c.Span; var sa = a.Span;
                for (int i = 0; i < sc.Length; i++)
                {
                    if (sa[i].V > 3f) sc[i].N++;
                    if ((sc[i].N + (int)(sa[i].V * 10)) % 7 == 0)
                        ctx.World.Events.Send(new ParPing { Entity = entities.EntityAt(i), V = sa[i].V });
                }
            }
        }
    }

    // Touches nothing the others do, and records structural changes: a component and a tag toggled.
    [System("test.par.drift_d", Phase.Gameplay)]
    private sealed class DriftD : IDeclaresAccess
    {
        private readonly Query<ParD> _q;
        public DriftD(World w) => _q = w.Query<ParD>();
        public void Declare(SystemAccess access) => access.Writes<ParD>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (d, entities) in _q.Chunks)
            {
                var sd = d.Span;
                for (int i = 0; i < sd.Length; i++)
                {
                    sd[i].V = MathF.Sin(sd[i].V * 1.3f + 0.7f + i * 0.01f);
                    var e = entities.EntityAt(i);
                    int bits = (int)(MathF.Abs(sd[i].V) * 1000);
                    if ((bits & 7) == 0)
                    {
                        if (e.HasComponent<ParSum>()) ctx.Commands.Remove<ParSum>(e);
                        else ctx.Commands.Add(e, new ParSum { V = 1 });
                    }
                    if ((bits & 15) == 3)
                    {
                        if (e.Tags.Has<ParFlag>()) ctx.Commands.RemoveTag<ParFlag>(e);
                        else ctx.Commands.AddTag<ParFlag>(e);
                    }
                }
            }
        }
    }

    // Reads what CountC sends, so after it; keeps the order it saw them in.
    [System("test.par.sum_pings", Phase.Gameplay)]
    private sealed class SumPings : IDeclaresAccess
    {
        private readonly EventReader<ParPing> _pings;
        public readonly List<string?> Seen = new();
        public SumPings(World w) => _pings = w.Events.Reader<ParPing>(this);
        public void Declare(SystemAccess access) => access.ReadsEvents<ParPing>().Writes<ParSum>();
        public void Run(in SystemContext ctx)
        {
            foreach (ref readonly var ping in _pings.Read())
            {
                Seen.Add(ping.Entity.Name);
                if (ctx.World.Has<ParSum>(ping.Entity)) ctx.World.Get<ParSum>(ping.Entity).V += ping.V;
            }
        }
    }

    // No declaration: runs alone, in its place. Flushes mid-phase, so what the declared systems before it
    // recorded must be in by then, as it was when everything ran in order.
    private sealed class Legacy : ISystem
    {
        private readonly Query<ParC, ParD> _q;
        private readonly Query<ParSum> _sums;
        public readonly List<int> SumsAtFlush = new();
        public Legacy(World w) { _q = w.Query<ParC, ParD>(); _sums = w.Query<ParSum>(); }
        public void Run(in SystemContext ctx)
        {
            foreach (var (c, d, _) in _q.Chunks)
            {
                var sc = c.Span; var sd = d.Span;
                for (int i = 0; i < sc.Length; i++) sd[i].V += sc[i].N * 0.001f;
            }
            ctx.World.FlushCommands();
            SumsAtFlush.Add(_sums.Count);
        }
    }

    [System("test.par.scale_a", Phase.Gameplay)]
    private sealed class ScaleA : IDeclaresAccess
    {
        private readonly Query<ParA, ParD> _q;
        public ScaleA(World w) => _q = w.Query<ParA, ParD>();
        public void Declare(SystemAccess access) => access.Writes<ParA>().Reads<ParD>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (a, d, _) in _q.Chunks)
            {
                var sa = a.Span; var sd = d.Span;
                for (int i = 0; i < sa.Length; i++) sa[i].V += sd[i].V * 0.1f;
            }
        }
    }

    // Two in AI that only read A: together.
    [System("test.par.ai_b", Phase.AI)]
    private sealed class AiB : IDeclaresAccess
    {
        private readonly Query<ParA, ParB> _q;
        public AiB(World w) => _q = w.Query<ParA, ParB>();
        public void Declare(SystemAccess access) => access.Reads<ParA>().Writes<ParB>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (a, b, _) in _q.Chunks)
            {
                var sa = a.Span; var sb = b.Span;
                for (int i = 0; i < sa.Length; i++) sb[i].V -= sa[i].V * 0.001f;
            }
        }
    }

    [System("test.par.ai_c", Phase.AI)]
    private sealed class AiC : IDeclaresAccess
    {
        private readonly Query<ParA, ParC> _q;
        public AiC(World w) => _q = w.Query<ParA, ParC>();
        public void Declare(SystemAccess access) => access.Reads<ParA>().Writes<ParC>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (a, c, _) in _q.Chunks)
            {
                var sa = a.Span; var sc = c.Span;
                for (int i = 0; i < sa.Length; i++) if (sa[i].V < 0.5f) sc[i].N--;
            }
        }
    }

    private sealed class Probes
    {
        public SumPings Sums = null!;
        public Legacy Legacy = null!;
        public void Clear() { Sums.Seen.Clear(); Legacy.SumsAtFlush.Clear(); }
    }

    private static Probes AddSystems(World world)
    {
        var probes = new Probes();
        world.AddSystem(new MoveA(world));
        world.AddSystem(new GrowB(world));
        world.AddSystem(new CountC(world));
        world.AddSystem(new DriftD(world));
        world.AddSystem(probes.Sums = new SumPings(world));
        world.AddSystem(probes.Legacy = new Legacy(world), Phase.Gameplay);
        world.AddSystem(new ScaleA(world));
        world.AddSystem(new AiB(world));
        world.AddSystem(new AiC(world));
        return probes;
    }

    private static void Populate(World world, int count, bool persistent)
    {
        for (int i = 0; i < count; i++)
        {
            var e = world.Create(Transform.Identity, $"p{i}");
            world.Add(e, new ParA { V = i * 0.01f });
            world.Add(e, new ParB { V = (i % 13) * 0.1f });
            world.Add(e, new ParC { N = i % 3 });
            world.Add(e, new ParD { V = i * 0.003f });
            if (persistent) world.MakePersistent(e);
        }
    }

    // Every value, bit for bit, by name; and what the probing systems saw, in order.
    private static string Digest(World world, Probes probes)
    {
        var text = new StringBuilder();
        foreach (var e in world.Query<ParA>().Entities.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            text.Append(e.Name).Append(':')
                .Append(BitConverter.SingleToInt32Bits(world.Get<ParA>(e).V)).Append(',')
                .Append(BitConverter.SingleToInt32Bits(world.Get<ParB>(e).V)).Append(',')
                .Append(world.Get<ParC>(e).N).Append(',')
                .Append(BitConverter.SingleToInt32Bits(world.Get<ParD>(e).V)).Append(',')
                .Append(world.TryGet<ParSum>(e, out var sum) ? BitConverter.SingleToInt32Bits(sum.V).ToString() : "-").Append(',')
                .Append(e.Tags.Has<ParFlag>() ? 'F' : '.').Append('\n');
        }
        text.Append("pings ").AppendJoin(' ', probes.Sums.Seen).Append('\n');
        text.Append("flush ").AppendJoin(' ', probes.Legacy.SumsAtFlush).Append('\n');
        return text.ToString();
    }

    private static string Run(bool parallel, int ticks, out int violations, out int stagesWithSeveral)
    {
        using var world = new World(parallel ? "parallel" : "sequential");
        world.Systems.Parallel = parallel;
        world.Systems.Threads = 3;
        world.Systems.AccessCheckLevel = 2;
        Populate(world, 600, persistent: false);
        var probes = AddSystems(world);
        for (int i = 0; i < ticks; i++) world.RunFixed(1f / 60f);
        violations = world.Systems.AccessViolations;
        stagesWithSeveral = world.PlanOf(Phase.Gameplay).Stages.Count(s => s.Length > 1) + world.PlanOf(Phase.AI).Stages.Count(s => s.Length > 1);
        return Digest(world, probes);
    }

    // test: DeclaredSystemsRunInParallelWithResultsIdenticalToSequential
    [Xunit.Fact]
    public void DeclaredSystemsRunInParallelWithResultsIdenticalToSequential()
    {
        string sequential = Run(parallel: false, 120, out int seqViolations, out _);
        string parallel = Run(parallel: true, 120, out int parViolations, out int stages);

        Assert.Equal(0, seqViolations);
        Assert.Equal(0, parViolations);
        Assert.Equal(3, stages);                  // move_a+drift_d, grow_b+count_c, ai_b+ai_c
        Assert.Contains("F", sequential);         // the structural changes did happen
        Assert.DoesNotContain("pings \n", sequential);   // and events were sent and read
        Assert.Equal(sequential, parallel);

        // And again, so a lucky interleaving is not the reason.
        for (int i = 0; i < 3; i++) Assert.Equal(sequential, Run(parallel: true, 120, out _, out _));
    }

    // test: ParallelResultsMatchSequentialAcrossASaveAndALoad
    [Xunit.Fact]
    public void ParallelResultsMatchSequentialAcrossASaveAndALoad()
    {
        using var app = HeadlessApp.Simulation().Build();
        app.Engine.Saves.Root = Path.Combine(TestEnv.NewTempDir(), "saves");
        var world = app.CreateWorld("w");
        world.Systems.Threads = 3;
        Populate(world, 300, persistent: true);
        var probes = AddSystems(world);

        world.Systems.Parallel = true;
        for (int i = 0; i < 30; i++) world.RunFixed(1f / 60f);
        Assert.True(app.Engine.Saves.Save("slot"));

        string Continue(bool parallel)
        {
            world.Systems.Parallel = parallel;
            probes.Clear();
            for (int i = 0; i < 60; i++) world.RunFixed(1f / 60f);
            return Digest(world, probes);
        }

        string fromMemory = Continue(parallel: true);
        Assert.True(app.Engine.Saves.Load("slot"));
        string loadedSequential = Continue(parallel: false);
        Assert.True(app.Engine.Saves.Load("slot"));
        string loadedParallel = Continue(parallel: true);

        Assert.Equal(loadedSequential, loadedParallel);
        // A load may hand the entities back in another order, so the order pings were seen in can differ
        // from the run that never saved; every value may not.
        static string Values(string digest) => digest[..digest.IndexOf("pings", StringComparison.Ordinal)];
        Assert.Equal(Values(fromMemory), Values(loadedParallel));
        Assert.Equal(0, world.Systems.AccessViolations);
    }

    // ---- the plan ------------------------------------------------------------------------------------

    private static string[] Stages(World world, Phase phase) =>
        world.PlanOf(phase).Stages.Select(s => string.Join("+", s.Select(i => i.Id ?? i.Name))).ToArray();

    // test: ConflictingSystemsKeepTheirOrderAndAnUndeclaredOneRunsAlone
    [Xunit.Fact]
    public void ConflictingSystemsKeepTheirOrderAndAnUndeclaredOneRunsAlone()
    {
        using var world = new World("plan");
        AddSystems(world);
        Assert.Equal(new[] { "test.par.move_a+test.par.drift_d", "test.par.grow_b+test.par.count_c", "test.par.sum_pings", "Legacy", "test.par.scale_a" },
                     Stages(world, Phase.Gameplay));
        Assert.Equal(new[] { "test.par.ai_b+test.par.ai_c" }, Stages(world, Phase.AI));
    }

    [System("test.par.ordered_first", Phase.Late, Before = new[] { "test.par.ordered_second" })]
    private sealed class OrderedFirst : IDeclaresAccess
    {
        public void Declare(SystemAccess access) => access.Writes<ParA>();
        public void Run(in SystemContext ctx) { }
    }

    [System("test.par.ordered_second", Phase.Late)]
    private sealed class OrderedSecond : IDeclaresAccess
    {
        public void Declare(SystemAccess access) => access.Writes<ParB>();
        public void Run(in SystemContext ctx) { }
    }

    [System("test.par.free", Phase.Late)]
    private sealed class Free : IDeclaresAccess
    {
        public void Declare(SystemAccess access) => access.Writes<ParC>();
        public void Run(in SystemContext ctx) { }
    }

    [System("test.par.exclusive", Phase.Late)]
    private sealed class ExclusiveOne : IDeclaresAccess
    {
        public void Declare(SystemAccess access) => access.Exclusive();
        public void Run(in SystemContext ctx) { }
    }

    // An explicit order is kept even when the data does not ask for it, and Exclusive runs alone.
    // test: AnExplicitOrderIsKeptAndAnExclusiveSystemRunsAlone
    [Xunit.Fact]
    public void AnExplicitOrderIsKeptAndAnExclusiveSystemRunsAlone()
    {
        using var world = new World("plan");
        world.AddSystem(new OrderedFirst());
        world.AddSystem(new OrderedSecond());
        world.AddSystem(new Free());
        world.AddSystem(new ExclusiveOne());
        Assert.Equal(new[] { "test.par.ordered_first+test.par.free", "test.par.ordered_second", "test.par.exclusive" },
                     Stages(world, Phase.Late));
        Assert.Equal("exclusive", world.Systems.Find("test.par.exclusive")!.Access!.ToString());
        Assert.Equal("writes ParA", world.Systems.Find("test.par.ordered_first")!.Access!.ToString());
    }

    // ---- they really do run at the same time ------------------------------------------------------

    private sealed class Rendezvous
    {
        public int Arrived;
        public readonly HashSet<int> Threads = new();
        public bool Met = true;
        public void Meet()
        {
            Interlocked.Increment(ref Arrived);
            lock (Threads) Threads.Add(Environment.CurrentManagedThreadId);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref Arrived) < 2 && DateTime.UtcNow < until) Thread.SpinWait(100);
            if (Volatile.Read(ref Arrived) < 2) Met = false;
        }
    }

    [System("test.par.meet_a", Phase.Gameplay)]
    private sealed class MeetA : IDeclaresAccess
    {
        private readonly Rendezvous _r;
        public MeetA(Rendezvous r) => _r = r;
        public void Declare(SystemAccess access) => access.Writes<ParA>();
        public void Run(in SystemContext ctx) => _r.Meet();
    }

    [System("test.par.meet_b", Phase.Gameplay)]
    private sealed class MeetB : IDeclaresAccess
    {
        private readonly Rendezvous _r;
        public MeetB(Rendezvous r) => _r = r;
        public void Declare(SystemAccess access) => access.Writes<ParB>();
        public void Run(in SystemContext ctx) => _r.Meet();
    }

    // Each waits for the other to have started: only possible if they run at once.
    // test: TwoSystemsThatDoNotConflictRunAtTheSameTime
    [Xunit.Fact]
    public void TwoSystemsThatDoNotConflictRunAtTheSameTime()
    {
        using var world = new World("meet");
        world.Systems.Threads = 1;
        var r = new Rendezvous();
        world.AddSystem(new MeetA(r));
        world.AddSystem(new MeetB(r));
        world.RunFixed(1f / 60f);
        Assert.True(r.Met);
        Assert.Equal(2, r.Threads.Count);
    }

    // ---- the dev-build check ---------------------------------------------------------------------------

    [System("test.par.sneaky", Phase.Gameplay)]
    private sealed class Sneaky : IDeclaresAccess
    {
        private readonly Query<ParB> _b;
        public Sneaky(World w) => _b = w.Query<ParB>();
        public void Declare(SystemAccess access) => access.Reads<ParA>();
        public void Run(in SystemContext ctx) { foreach (var _ in _b.Chunks) { } }
    }

    [System("test.par.liar", Phase.Gameplay)]
    private sealed class Liar : IDeclaresAccess
    {
        private readonly Query<ParC> _c;
        public Liar(World w) => _c = w.Query<ParC>();
        public void Declare(SystemAccess access) => access.Reads<ParC>();
        public void Run(in SystemContext ctx) { foreach (var (c, _) in _c.Chunks) foreach (ref var v in c.Span) v.N++; }
    }

    [System("test.par.creator", Phase.Gameplay)]
    private sealed class Creator : IDeclaresAccess
    {
        public void Declare(SystemAccess access) => access.Writes<ParD>();
        public void Run(in SystemContext ctx) => ctx.World.Create();
    }

    [System("test.par.gossip", Phase.Gameplay)]
    private sealed class Gossip : IDeclaresAccess
    {
        public void Declare(SystemAccess access) => access.Writes<ParSum>();
        public void Run(in SystemContext ctx)
        {
            ctx.World.Events.Send(new ParPing());
            _ = ctx.World.Resources.Get<Weather>();
        }
    }

    [System("test.par.honest", Phase.Gameplay)]
    private sealed class Honest : IDeclaresAccess
    {
        private readonly Query<ParA, ParB> _q;
        public Honest(World w) => _q = w.Query<ParA, ParB>();
        public void Declare(SystemAccess access) => access.Reads<ParB>().Writes<ParA>().ReadsResource<Weather>();
        public void Run(in SystemContext ctx)
        {
            _ = ctx.World.Resources.Get<Weather>();
            foreach (var (a, b, _) in _q.Chunks) a.Span[0].V += b.Span[0].V;
        }
    }

    // test: ADeclaredSystemTouchingWhatItDidNotDeclareIsReported
    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ADeclaredSystemTouchingWhatItDidNotDeclareIsReported(bool parallel)
    {
        using var world = new World("check");
        world.Systems.Parallel = parallel;
        world.Systems.AccessCheckLevel = 2;
        Populate(world, 4, persistent: false);
        world.AddSystem(new Honest(world));
        world.AddSystem(new Sneaky(world));
        world.AddSystem(new Liar(world));
        world.AddSystem(new Gossip());
        if (!parallel) world.AddSystem(new Creator());   // a race beside others; checked where it is safe to
        world.RunFixed(1f / 60f);
        world.RunFixed(1f / 60f);   // each is reported once

        var reports = world.Systems.AccessReports;
        Assert.DoesNotContain(reports, r => r.Contains("test.par.honest"));
        Assert.Contains(reports, r => r.Contains("test.par.sneaky") && r.Contains("touched component ParB"));
        Assert.Contains(reports, r => r.Contains("test.par.liar") && r.Contains("ParC"));
        Assert.Contains(reports, r => r.Contains("test.par.gossip") && r.Contains("sent event ParPing"));
        Assert.Contains(reports, r => r.Contains("test.par.gossip") && r.Contains("used resource Weather"));
        if (!parallel) Assert.Contains(reports, r => r.Contains("test.par.creator") && r.Contains("structural change"));
        Assert.Equal(parallel ? 4 : 5, world.Systems.AccessViolations);
    }

    // ---- what goes wrong ---------------------------------------------------------------------------

    [System("test.par.thrower", Phase.Gameplay)]
    private sealed class Thrower : IDeclaresAccess
    {
        public void Declare(SystemAccess access) => access.Writes<ParC>();
        public void Run(in SystemContext ctx) => throw new InvalidOperationException("thrown on purpose");
    }

    // A system that throws beside others throws out of RunFixed, as it would alone, and the world is
    // left able to change: Friflo's count of open query loops is put back.
    // test: ASystemThatThrowsInAParallelStageThrowsOutOfTheTickAndLeavesTheWorldUsable
    [Xunit.Fact]
    public void ASystemThatThrowsInAParallelStageThrowsOutOfTheTickAndLeavesTheWorldUsable()
    {
        using var world = new World("throw");
        world.Systems.Threads = 2;
        Populate(world, 50, persistent: false);
        AddSystems(world);
        var thrower = new Thrower();
        world.AddSystem(thrower);
        Assert.Contains(world.PlanOf(Phase.Gameplay).Stages, stage => stage.Length > 1 && stage.Any(s => s.Id == "test.par.thrower"));

        var ex = Assert.Throws<InvalidOperationException>(() => world.RunFixed(1f / 60f));
        Assert.Equal("thrown on purpose", ex.Message);

        var e = world.Create();           // no "structural change within a query loop"
        world.Add(e, new ParA());
        world.RemoveSystem(thrower);
        for (int i = 0; i < 5; i++) world.RunFixed(1f / 60f);
    }

    // sys_parallel and friends are cvars of the app.
    // test: TheSchedulerIsSteeredByCVars
    [Xunit.Fact]
    public void TheSchedulerIsSteeredByCVars()
    {
        using var app = HeadlessApp.Bare().Build();
        Assert.True(app.CVars.Find("sys_parallel") is not null);
        Assert.True(app.CVars.Find("sys_threads") is not null);
        Assert.True(app.CVars.Find("sys_access_check") is not null);
        var world = app.CreateWorld("w");
        var r = new Rendezvous();
        world.AddSystem(new MeetA(r));
        world.AddSystem(new MeetB(r));
        app.CVars.Execute("sys_threads 1", ExecSource.Console);
        world.RunFixed(1f / 60f);
        Assert.True(r.Met);
        Assert.Equal(2, r.Threads.Count);
    }
}

// Zero allocation with stages running side by side, on this thread and on the workers.
[Xunit.Collection(MeasurementsCollection.Name)]
public class ParallelSchedulerAllocationTests
{
    public ParallelSchedulerAllocationTests() { _ = TestEnv.UserRoot; }

    [System("test.par_alloc.write_a", Phase.Gameplay)]
    private sealed class WriteA : IDeclaresAccess
    {
        private readonly Query<ParA> _q;
        public WriteA(World w) => _q = w.Query<ParA>();
        public void Declare(SystemAccess access) => access.Writes<ParA>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (a, entities) in _q.Chunks)
            {
                var span = a.Span;
                for (int i = 0; i < span.Length; i++)
                {
                    span[i].V += 1;
                    var e = entities.EntityAt(i);
                    if (e.Tags.Has<ParFlag>()) ctx.Commands.RemoveTag<ParFlag>(e);
                    else ctx.Commands.AddTag<ParFlag>(e);
                }
            }
        }
    }

    [System("test.par_alloc.write_b", Phase.Gameplay)]
    private sealed class WriteB : IDeclaresAccess
    {
        private readonly Query<ParB> _q;
        public WriteB(World w) => _q = w.Query<ParB>();
        public void Declare(SystemAccess access) => access.Writes<ParB>().Sends<ParPing>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (b, entities) in _q.Chunks)
            {
                var span = b.Span;
                for (int i = 0; i < span.Length; i++)
                {
                    span[i].V += 1;
                    ctx.Commands.Add(entities.EntityAt(i), new ParSum { V = span[i].V });
                }
            }
            ctx.World.Events.Send(new ParPing());
        }
    }

    [System("test.par_alloc.read", Phase.Gameplay)]
    private sealed class ReadBoth : IDeclaresAccess
    {
        private readonly Query<ParC, ParD> _q;
        public float Total;
        public ReadBoth(World w) => _q = w.Query<ParC, ParD>();
        public void Declare(SystemAccess access) => access.Reads<ParC>().Reads<ParD>();
        public void Run(in SystemContext ctx)
        {
            foreach (var (c, d, _) in _q.Chunks)
                for (int i = 0; i < c.Length; i++) Total += c.Span[i].N + d.Span[i].V;
        }
    }

    // test: ParallelStagesAllocateNothingInSteadyState
    [Xunit.Fact]
    public void ParallelStagesAllocateNothingInSteadyState()
    {
        using var world = new World("alloc");
        world.Systems.Threads = 2;
        world.Systems.AccessCheckLevel = 2;
        for (int i = 0; i < 200; i++)
        {
            var e = world.Create();
            world.Add(e, new ParA());
            world.Add(e, new ParB());
            world.Add(e, new ParC { N = i });
            world.Add(e, new ParD());
            world.Add(e, new ParSum());
        }
        world.AddSystem(new WriteA(world));
        world.AddSystem(new WriteB(world));
        world.AddSystem(new ReadBoth(world));
        Assert.Single(world.PlanOf(Phase.Gameplay).Stages);

        for (int i = 0; i < 60; i++) { world.RunFixed(1f / 60f); Profiler.EndFrame(); }
        long workers = world.Systems.WorkerAllocatedBytes;
        AllocationProbe.AssertNone(200, () => { world.RunFixed(1f / 60f); Profiler.EndFrame(); });
        Assert.Equal(workers, world.Systems.WorkerAllocatedBytes);
        Assert.Equal(0, world.Systems.AccessViolations);
    }
}
