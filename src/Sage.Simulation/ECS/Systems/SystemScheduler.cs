#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;

namespace sage_engine;

public sealed class SystemInfo
{
    internal SystemInfo(ISystem system, string? id, Phase phase, RunCondition condition,
                        IReadOnlyList<string> before, IReadOnlyList<string> after, int registration, string owner)
    {
        System = system;
        Id = id;
        Phase = phase;
        Condition = condition;
        Before = before;
        After = after;
        Registration = registration;
        Owner = owner;
        Name = system.GetType().Name;
        ProfileName = $"{PhaseInfo.ScheduleOf(phase)}.{phase}/{Name}";
    }

    public ISystem System { get; }

    // The declared id ([System]); null for an unnamed system (a test's probe), which nothing can order
    // against, replace or disable.
    public string? Id { get; }
    public Phase Phase { get; }
    public RunCondition Condition { get; }
    public string Name { get; }
    public string ProfileName { get; }

    // The plugin whose code added it ("host" when none was running), and the ones that replaced or
    // disabled it since (issue #17): what `sys_list` shows, and what a mod conflict report is made of
    // (REDESIGN §3.3).
    public string Owner { get; }
    public string? ReplacedBy { get; internal init; }
    public string? DisabledBy { get; internal set; }

    public bool Enabled { get; set; } = true;   // sys_toggle, and world.Systems.Disable
    public IReadOnlyList<string> Before { get; }
    public IReadOnlyList<string> After { get; }
    internal int Registration { get; }
}

// Systems per phase, in a deterministic order: `before`/`after` constraints by system id first,
// registration order otherwise (docs/design/03 §3.5, issue #17). Access declarations (reads/writes)
// come with the parallel scheduler and phase contracts (R16); v1 runs systems sequentially.
//
// A constraint is checked when its system is added rather than when the phase is sorted, so the error
// names the system that is wrong:
//   - naming a system in another phase is an error: the phase order already decides it, and before
//     issue #17 such a constraint was silently ignored;
//   - naming an id no loaded assembly declares is an error in dev builds (a typo, or a plugin that is
//     not installed) and logged in Shipping; "?id" marks a soft dependency on an optional plugin;
//   - naming an id that is declared but not in this world is no constraint at all: its plugin is
//     turned off, or its module adds it only when the world has what it needs.
internal sealed class SystemScheduler
{
    private readonly List<SystemInfo>[] _phases = Enumerable.Range(0, PhaseInfo.Count).Select(_ => new List<SystemInfo>()).ToArray();
    private readonly string[] _phaseProfileNames = Enumerable.Range(0, PhaseInfo.Count)
        .Select(i => $"{PhaseInfo.ScheduleOf((Phase)i)}.{(Phase)i}").ToArray();
    private readonly Dictionary<string, SystemInfo> _byId = new(StringComparer.Ordinal);
    private readonly SystemCatalog _catalog;
    private int _registrations;

    public SystemScheduler(SystemCatalog catalog) { _catalog = catalog; }

    public IEnumerable<SystemInfo> All => _phases.SelectMany(p => p);

    public string PhaseProfileName(Phase phase) => _phaseProfileNames[(int)phase];

    public SystemInfo? Find(string id) => _byId.TryGetValue(id, out var info) ? info : null;

    public SystemInfo Add(ISystem system, string? id, Phase phase, RunCondition condition,
                          IReadOnlyList<string> before, IReadOnlyList<string> after, string owner)
    {
        if (All.Any(s => ReferenceEquals(s.System, system)))
            throw new InvalidOperationException($"System {system.GetType().Name} is already registered.");
        if (id != null && _byId.TryGetValue(id, out var same))
            throw new InvalidOperationException($"System id '{id}' is already in this world ({same.Name}, added by {same.Owner}); " +
                                                "to swap it for another, use world.Systems.Replace.");

        _catalog.Include(system.GetType().Assembly);
        string what = id ?? system.GetType().Name;
        foreach (string name in before) Check(what, phase, name, "before");
        foreach (string name in after) Check(what, phase, name, "after");

        // And the other way round: a system already here that orders itself against this id from
        // another phase. Only possible when this one is an undeclared system given an id by hand,
        // whose phase no catalog knew when that constraint was checked.
        if (id != null)
            foreach (var other in All)
                if (other.Phase != phase && (Names(other.Before, id) || Names(other.After, id)))
                    throw CrossPhase(other.Id ?? other.Name, other.Phase, id, phase);

        var info = new SystemInfo(system, id, phase, condition, before, after, _registrations++, owner);
        Insert(info);
        return info;
    }

    // Puts `replacement` in `old`'s place: the same id, phase, order and run condition.
    public SystemInfo Replace(SystemInfo old, ISystem replacement, string by)
    {
        if (All.Any(s => ReferenceEquals(s.System, replacement)))
            throw new InvalidOperationException($"System {replacement.GetType().Name} is already registered.");
        _catalog.Include(replacement.GetType().Assembly);

        var info = new SystemInfo(replacement, old.Id, old.Phase, old.Condition, old.Before, old.After,
                                  old.Registration, old.Owner) { ReplacedBy = by };
        var list = _phases[(int)old.Phase];
        list[list.IndexOf(old)] = info;
        if (old.Id != null) _byId[old.Id] = info;
        return info;
    }

    public SystemInfo? Remove(ISystem system)
    {
        foreach (var list in _phases)
        {
            int index = list.FindIndex(s => ReferenceEquals(s.System, system));
            if (index < 0) continue;
            var info = list[index];
            list.RemoveAt(index);
            if (info.Id != null) _byId.Remove(info.Id);
            return info;
        }
        return null;
    }

    public IReadOnlyList<SystemInfo> In(Phase phase) => _phases[(int)phase];

    private void Insert(SystemInfo info)
    {
        var list = _phases[(int)info.Phase];
        list.Add(info);
        if (info.Id != null) _byId[info.Id] = info;
        try
        {
            var sorted = Sort(list, info.Phase);
            list.Clear();
            list.AddRange(sorted);
        }
        catch
        {
            list.Remove(info);   // a cycle: leave the world as it was, so the exception is the only effect
            if (info.Id != null) _byId.Remove(info.Id);
            throw;
        }
    }

    private void Check(string system, Phase phase, string name, string relation)
    {
        bool optional = name.StartsWith('?');
        string id = optional ? name[1..] : name;
        if (Find(id) is { } present)
        {
            if (present.Phase != phase) throw CrossPhase(system, phase, id, present.Phase);
            return;
        }
        if (_catalog.TryGet(id, out var declared))
        {
            if (declared.Phase != phase) throw CrossPhase(system, phase, id, declared.Phase);
            return;
        }
        if (optional) return;

        string message = $"System {system} runs {relation} '{id}', which no loaded assembly declares: a typo, or a plugin " +
                         $"that is not installed (write \"?{id}\" if that plugin is optional).";
        if (BuildInfo.IsDevBuild) throw new InvalidOperationException(message);
        Log.Error(LogCat.World, message + " The constraint is ignored.");
    }

    private static InvalidOperationException CrossPhase(string system, Phase phase, string other, Phase otherPhase) =>
        new($"System {system} ({phase}) orders itself against '{other}', which runs in {otherPhase}. " +
            "Before/after orders systems within one phase; the phase order already decides this one.");

    private static bool Names(IReadOnlyList<string> constraints, string id)
    {
        foreach (string c in constraints)
            if (Strip(c) == id) return true;
        return false;
    }

    private static string Strip(string name) => name.StartsWith('?') ? name[1..] : name;

    // Stable topological sort over the constraints between systems present in this phase.
    private static List<SystemInfo> Sort(List<SystemInfo> systems, Phase phase)
    {
        var byId = new Dictionary<string, SystemInfo>(StringComparer.Ordinal);
        foreach (var s in systems)
            if (s.Id != null) byId[s.Id] = s;

        // edges: a -> b means a runs before b
        var edges = systems.ToDictionary(s => s, _ => new List<SystemInfo>());
        var incoming = systems.ToDictionary(s => s, _ => 0);
        foreach (var s in systems)
        {
            foreach (string name in s.Before)
                if (byId.TryGetValue(Strip(name), out var target) && target != s) { edges[s].Add(target); incoming[target]++; }
            foreach (string name in s.After)
                if (byId.TryGetValue(Strip(name), out var source) && source != s) { edges[source].Add(s); incoming[s]++; }
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
            var cycle = systems.Where(s => incoming[s] > 0).Select(s => s.Id ?? s.Name);
            throw new InvalidOperationException($"System ordering cycle in phase {phase}: {string.Join(", ", cycle)}");
        }
        return result;
    }
}
