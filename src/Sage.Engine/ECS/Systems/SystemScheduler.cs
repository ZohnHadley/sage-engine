#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace sage_engine;

public sealed class SystemInfo
{
    internal SystemInfo(ISystem system, Phase phase, RunCondition condition, Type[] before, Type[] after, int registration)
    {
        System = system;
        Phase = phase;
        Condition = condition;
        Before = before;
        After = after;
        Registration = registration;
        Name = system.GetType().Name;
        ProfileName = $"{PhaseInfo.ScheduleOf(phase)}.{phase}/{Name}";
    }

    public ISystem System { get; }
    public Phase Phase { get; }
    public RunCondition Condition { get; }
    public string Name { get; }
    public string ProfileName { get; }
    public bool Enabled { get; set; } = true;   // sys_toggle
    internal Type[] Before { get; }
    internal Type[] After { get; }
    internal int Registration { get; }
}

// Systems per phase, in a deterministic order: `before`/`after` constraints first, registration
// order otherwise (docs/design/03 §3.5). Access declarations (reads/writes) come with the event bus
// and the parallel scheduler; v1 runs systems sequentially.
internal sealed class SystemScheduler
{
    private readonly List<SystemInfo>[] _phases = Enumerable.Range(0, PhaseInfo.Count).Select(_ => new List<SystemInfo>()).ToArray();
    private readonly string[] _phaseProfileNames = Enumerable.Range(0, PhaseInfo.Count)
        .Select(i => $"{PhaseInfo.ScheduleOf((Phase)i)}.{(Phase)i}").ToArray();
    private int _registrations;

    public IEnumerable<SystemInfo> All => _phases.SelectMany(p => p);

    public string PhaseProfileName(Phase phase) => _phaseProfileNames[(int)phase];

    public SystemInfo Add(ISystem system, Phase phase, RunCondition condition, Type[] before, Type[] after)
    {
        if (All.Any(s => ReferenceEquals(s.System, system)))
            throw new InvalidOperationException($"System {system.GetType().Name} is already registered.");

        var info = new SystemInfo(system, phase, condition, before, after, _registrations++);
        var list = _phases[(int)phase];
        list.Add(info);
        var sorted = Sort(list, phase);
        list.Clear();
        list.AddRange(sorted);
        return info;
    }

    public bool Remove(ISystem system)
    {
        foreach (var list in _phases)
            if (list.RemoveAll(s => ReferenceEquals(s.System, system)) > 0)
                return true;
        return false;
    }

    public IReadOnlyList<SystemInfo> In(Phase phase) => _phases[(int)phase];

    // Stable topological sort. A constraint naming a type that isn't in this phase is ignored
    // (it may live in another phase, where order is already fixed by the phase order).
    private static List<SystemInfo> Sort(List<SystemInfo> systems, Phase phase)
    {
        var byType = new Dictionary<Type, List<SystemInfo>>();
        foreach (var s in systems)
        {
            if (!byType.TryGetValue(s.System.GetType(), out var l)) byType[s.System.GetType()] = l = new();
            l.Add(s);
        }

        // edges: a -> b means a runs before b
        var edges = systems.ToDictionary(s => s, _ => new List<SystemInfo>());
        var incoming = systems.ToDictionary(s => s, _ => 0);
        foreach (var s in systems)
        {
            foreach (var t in s.Before)
                if (byType.TryGetValue(t, out var targets))
                    foreach (var target in targets) { edges[s].Add(target); incoming[target]++; }
            foreach (var t in s.After)
                if (byType.TryGetValue(t, out var sources))
                    foreach (var source in sources) { edges[source].Add(s); incoming[s]++; }
        }

        var result = new List<SystemInfo>(systems.Count);
        var ready = new SortedSet<SystemInfo>(systems.Where(s => incoming[s] == 0), Comparer<SystemInfo>.Create((a, b) => a.Registration.CompareTo(b.Registration)));
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            result.Add(next);
            foreach (var to in edges[next])
                if (--incoming[to] == 0) ready.Add(to);
        }

        if (result.Count != systems.Count)
        {
            var cycle = systems.Where(s => incoming[s] > 0).Select(s => s.Name);
            throw new InvalidOperationException($"System ordering cycle in phase {phase}: {string.Join(", ", cycle)}");
        }
        return result;
    }
}
