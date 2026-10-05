#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using F = Friflo.Engine.ECS;

namespace Sage.Simulation;

// One system's run while its phase is scheduled from access declarations (issue #288): where its
// structural changes go, what it threw, how long it took. One per system, made the first time it is
// needed and reused, so a steady state allocates nothing.
internal sealed class SystemRun
{
    public SystemRun(World world, SystemInfo info)
    {
        World = world;
        Info = info;
    }

    public readonly World World;
    public readonly SystemInfo Info;
    public SystemAccess? Access => Info.Access;

    private EntityCommands? _log;
    public EntityCommands Log => _log ??= new EntityCommands(new SystemCommandLog());
    public bool HasLog => _log is { Count: > 0 };

    // Set while it runs: record on the log instead of the world's buffer, and check touches against the
    // declaration.
    public bool Logged;
    public bool Validate;
    public bool OnWorker;

    public Exception? Error;
    public long Ticks;
    public long Bytes;

    // Runs the system on this thread, with `Current` set so the world's hooks know who is running.
    public void Execute(Phase phase, in TickTime tick, in FrameTime frame, bool onWorker)
    {
        var previous = AccessCheck.Current;
        AccessCheck.Current = this;
        OnWorker = onWorker;
        Error = null;
        long bytes = onWorker ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long start = Stopwatch.GetTimestamp();
        try { Info.System.Run(new SystemContext(World, phase, tick, frame)); }
        catch (Exception ex) { Error = ex; }
        finally
        {
            Ticks = Stopwatch.GetTimestamp() - start;
            Bytes = onWorker ? GC.GetAllocatedBytesForCurrentThread() - bytes : 0;
            AccessCheck.Current = previous;
            OnWorker = false;
        }
    }
}

// The dev-build check that a declared system touches only what it declared (issue #288). The world's
// component, resource and event entry points call in here; with no declared system anywhere `On` is
// false and each costs one static read, and in Shipping nothing turns it on.
internal static class AccessCheck
{
    // True once any world has a declared system to check (dev builds). Never turned off again: the hooks
    // then also look at the thread's current run, which is null outside systems.
    internal static volatile bool On;

    [ThreadStatic] internal static SystemRun? Current;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Component<T>() where T : struct, IComponent
    {
        if (On) ComponentSlow<T>();
    }

    private static void ComponentSlow<T>() where T : struct, IComponent
    {
        var run = Current;
        if (run is { Validate: true } && !run.Access!.IsExclusive && !run.Access.Touches<T>())
            run.World.Systems.ReportAccess(run.Info, "touched component", typeof(T));
    }

    public static void Resource(Type type)
    {
        if (!On) return;
        var run = Current;
        if (run is { Validate: true } && !run.Access!.IsExclusive && !run.Access.TouchesResource(type))
            run.World.Systems.ReportAccess(run.Info, "used resource", type);
    }

    public static void ReadEvent(Type type)
    {
        if (!On) return;
        var run = Current;
        if (run is { Validate: true } && !run.Access!.IsExclusive && !run.Access.ReadsEvent(type))
            run.World.Systems.ReportAccess(run.Info, "read event", type);
    }

    public static void SendEvent(Type type)
    {
        if (!On) return;
        var run = Current;
        if (run is { Validate: true } && !run.Access!.IsExclusive && !run.Access.SendsEvent(type))
            run.World.Systems.ReportAccess(run.Info, "sent event", type);
    }

    // A structural change made directly (not on ctx.Commands): only an exclusive system may.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Structural()
    {
        if (On) StructuralSlow();
    }

    private static void StructuralSlow()
    {
        var run = Current;
        if (run is { Access: { IsExclusive: false } } && (run.Validate || run.OnWorker))
            run.World.Systems.ReportAccess(run.Info, "made a structural change directly (record it on ctx.Commands, or declare Exclusive)", null);
    }
}

// How a phase's systems are grouped (issue #288): stages run one after the other, and the systems of a
// stage at the same time. A system's stage is one past the latest stage of any earlier system it
// conflicts with — so of two systems that conflict, the one earlier in the phase's order always runs
// first, which is all sequential execution promised about them. A system with no declaration, or an
// exclusive one, conflicts with everything: it has a stage of its own, after everything before it and
// before everything after it, exactly where it ran before.
internal sealed class PhasePlan
{
    public readonly SystemInfo[][] Stages;
    public readonly ComponentProbe[][] StageProbes;   // the read-only components each stage is checked on
    public readonly bool HasParallelism;              // some stage has more than one system
    public readonly SystemRun?[] Scratch;            // the runnable systems of one stage

    public PhasePlan(IReadOnlyList<SystemInfo> systems)
    {
        var stage = new int[systems.Count];
        int last = -1;
        for (int i = 0; i < systems.Count; i++)
        {
            var s = systems[i];
            int at = 0;
            if (!Parallel(s)) at = last + 1;
            else
                for (int j = 0; j < i; j++)
                    if (Conflict(systems[j], s)) at = Math.Max(at, stage[j] + 1);
            stage[i] = at;
            last = Math.Max(last, at);
        }
        Stages = Enumerable.Range(0, last + 1)
            .Select(k => Enumerable.Range(0, systems.Count).Where(i => stage[i] == k).Select(i => systems[i]).ToArray())
            .ToArray();
        StageProbes = Stages.Select(members => members
                .Where(m => m.Access is { IsExclusive: false })
                .SelectMany(m => m.Access!.ReadProbes)
                .Distinct().ToArray())
            .ToArray();
        HasParallelism = Stages.Any(s => s.Length > 1);
        Scratch = new SystemRun?[Stages.Length == 0 ? 0 : Stages.Max(s => s.Length)];
    }

    private static bool Parallel(SystemInfo s) => s.Access is { IsExclusive: false };

    private static bool Conflict(SystemInfo a, SystemInfo b)
    {
        if (!Parallel(a) || !Parallel(b)) return true;
        if (Orders(a, b) || Orders(b, a)) return true;   // an explicit order is kept, whatever it is for
        return a.Access!.ConflictsWith(b.Access!);
    }

    private static bool Orders(SystemInfo a, SystemInfo b)
    {
        if (b.Id == null) return false;
        foreach (string c in a.Before) if (Strip(c) == b.Id) return true;
        foreach (string c in a.After) if (Strip(c) == b.Id) return true;
        return false;
    }

    private static string Strip(string name) => name.StartsWith('?') ? name[1..] : name;
}

// A world's worker threads for running a stage's systems at the same time (issue #288). Made the first
// time a stage has more than one system to run, and stopped with the world. The world's own thread runs
// systems too, so `n` workers give n + 1 at once; with none it runs them all itself, in order.
//
// No allocation per stage: the batch is an array the plan owns, claims are an interlocked counter, and
// the threads sleep on a semaphore between stages.
internal sealed class SystemWorkers : IDisposable
{
    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ManualResetEventSlim _done = new(false);
    private SystemRun?[] _batch = Array.Empty<SystemRun?>();
    private int _count;
    private int _next;
    private int _remaining;
    private int _inside;          // workers between checking `_open` and leaving
    private volatile bool _open;  // a stage is being run: workers may claim from it
    private volatile bool _stop;
    private Phase _phase;
    private TickTime _tick;
    private FrameTime _frame;

    public SystemWorkers(int count, string world)
    {
        // The event makes its lock object the first time a wait blocks; make it now, not in some later tick.
        _done.Wait(1);
        _threads = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            _threads[i] = new Thread(Work) { IsBackground = true, Name = $"Sage systems {i + 1} ({world})" };
            _threads[i].Start();
        }
    }

    public int Count => _threads.Length;

    // Runs batch[0..count) — on the workers and this thread — and returns when all are done.
    public void Run(SystemRun?[] batch, int count, Phase phase, in TickTime tick, in FrameTime frame)
    {
        _batch = batch;
        _count = count;
        _phase = phase;
        _tick = tick;
        _frame = frame;
        Volatile.Write(ref _remaining, count);
        _done.Reset();
        Interlocked.Exchange(ref _next, 0);
        _open = true;
        int wake = Math.Min(_threads.Length, count - 1);
        if (wake > 0) _wake.Release(wake);

        Claim(onWorker: false);

        if (Volatile.Read(ref _remaining) > 0)
        {
            var spin = new SpinWait();
            while (Volatile.Read(ref _remaining) > 0 && !spin.NextSpinWillYield) spin.SpinOnce();
            if (Volatile.Read(ref _remaining) > 0) _done.Wait();
        }

        // Close the stage, and wait for any worker still on its way out, so none can claim from the next
        // stage before it is set up.
        _open = false;
        Interlocked.MemoryBarrier();
        var leave = new SpinWait();
        while (Volatile.Read(ref _inside) != 0) leave.SpinOnce();
        _batch = Array.Empty<SystemRun?>();
    }

    private void Claim(bool onWorker)
    {
        while (true)
        {
            int i = Interlocked.Increment(ref _next) - 1;
            if (i >= _count) return;
            _batch[i]!.Execute(_phase, _tick, _frame, onWorker);
            if (Interlocked.Decrement(ref _remaining) == 0) _done.Set();
        }
    }

    private void Work()
    {
        while (true)
        {
            _wake.Wait();
            if (_stop) return;
            Interlocked.Increment(ref _inside);
            if (_open) Claim(onWorker: true);
            Interlocked.Decrement(ref _inside);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Release(_threads.Length);
        foreach (var t in _threads) t.Join(TimeSpan.FromSeconds(1));
        _wake.Dispose();
        _done.Dispose();
    }

    // Friflo counts the query loops open on a store, to refuse a structural change inside one, with a
    // plain ++/--. Loops opened on several threads at once can lose a count, so the world puts back the
    // value it had before each parallel stage. Null when this Friflo has no such field: parallel stages
    // are then not run.
    internal static readonly QueryLoopCounter? LoopCounter = QueryLoopCounter.Create();

    internal sealed class QueryLoopCounter
    {
        private readonly Func<F.EntityStore, int> _get;
        private readonly Action<F.EntityStore, int> _set;

        private QueryLoopCounter(Func<F.EntityStore, int> get, Action<F.EntityStore, int> set)
        {
            _get = get;
            _set = set;
        }

        public int Get(F.EntityStore store) => _get(store);
        public void Set(F.EntityStore store, int value) => _set(store, value);

        public static QueryLoopCounter? Create()
        {
            try
            {
                var intern = typeof(F.EntityStoreBase).GetField("internBase", BindingFlags.Instance | BindingFlags.NonPublic);
                var loops = intern?.FieldType.GetField("activeQueryLoops", BindingFlags.Instance | BindingFlags.NonPublic);
                if (intern == null || loops == null || loops.FieldType != typeof(int)) return null;

                var get = new DynamicMethod("SageGetQueryLoops", typeof(int), new[] { typeof(F.EntityStore) }, typeof(F.EntityStoreBase), skipVisibility: true);
                var il = get.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, intern);
                il.Emit(OpCodes.Ldfld, loops);
                il.Emit(OpCodes.Ret);

                var set = new DynamicMethod("SageSetQueryLoops", null, new[] { typeof(F.EntityStore), typeof(int) }, typeof(F.EntityStoreBase), skipVisibility: true);
                il = set.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Ldflda, intern);
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Stfld, loops);
                il.Emit(OpCodes.Ret);

                return new QueryLoopCounter((Func<F.EntityStore, int>)get.CreateDelegate(typeof(Func<F.EntityStore, int>)),
                                            (Action<F.EntityStore, int>)set.CreateDelegate(typeof(Action<F.EntityStore, int>)));
            }
            catch (Exception ex)
            {
                Log.Warn(LogCat.World, $"Parallel systems are off: this Friflo's query loop counter could not be reached ({ex.Message}).");
                return null;
            }
        }
    }
}

// The scheduler's cvars (issue #288).
internal sealed class SchedulingCVars
{
    public SchedulingCVars(CVarRegistry cvars)
    {
        Parallel = cvars.Register("sys_parallel", true, CVarFlags.None,
            "Run systems that declare their access (IDeclaresAccess) at the same time when they don't conflict. " +
            "Results are identical either way; 0 runs every phase in order on one thread.");
        Threads = cvars.Register("sys_threads", 0, CVarFlags.None,
            "Worker threads per world for parallel systems; 0 = one fewer than the cores, at most 7.", 0, 64);
        AccessCheck = cvars.Register("sys_access_check", 2, CVarFlags.DevOnly,
            "Dev builds: 0 = off, 1 = report what a declared system touches without declaring it, " +
            "2 = and a component it declared only reading that changes.", 0, 2);
    }

    public CVar<bool> Parallel { get; }
    public CVar<int> Threads { get; }
    public CVar<int> AccessCheck { get; }
}
