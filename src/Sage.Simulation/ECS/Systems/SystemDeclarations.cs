#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Sage.Simulation;

// A system's identity, declared on its type (REDESIGN §3.3–3.4, issue #17):
//
//   [System("sage.ai.think", Phase.Commands, After = new[] { "sage.character.player_control" })]
//   public sealed class AIThinkSystem : ISystem { … }
//
// The id is stable and namespaced, so an ordering constraint, `sys_list`, a profiler scope's owner and
// a mod replacing the system all name the same thing, and none of them depends on a CLR type from
// another assembly. The module still builds the instance — systems take cvars, the renderer and
// records through their constructors, which generated construction could not supply — and adds it
// with `world.AddSystem(new AIThinkSystem(...))`, which reads the phase and the order from here.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SystemAttribute : Attribute
{
    public SystemAttribute(string id, Phase phase)
    {
        Id = id;
        Phase = phase;
    }

    public string Id { get; }
    public Phase Phase { get; }

    // Ids of systems in the *same* phase this one runs before / after. Naming a system in another phase
    // is an error (the phase order already decides it, and a constraint that silently did nothing is
    // how two systems end up in the wrong order without anyone noticing). An id nothing declares is an
    // error too — a typo, or a plugin that is not installed; write "?id" for a soft dependency on a
    // plugin that may not be.
    public string[] Before { get; set; } = Array.Empty<string>();
    public string[] After { get; set; } = Array.Empty<string>();

    public RunCondition Condition { get; set; } = RunCondition.Default;
}

// What Sage.Generators lists for each assembly that declares systems: the declared types, so the
// engine knows every id a loaded assembly declares — including those of systems nobody added to this
// world — without scanning types. Implemented by the generated class the assembly's
// [GeneratedRegistrations] names.
public interface IGeneratedSystems
{
    IReadOnlyList<Type> Systems { get; }
}

// One declared system, as the scheduler needs it.
public sealed class SystemDeclaration
{
    private static readonly ConcurrentDictionary<Type, SystemDeclaration?> ByType = new();
    private static readonly ConcurrentDictionary<Assembly, IReadOnlyList<SystemDeclaration>> ByAssembly = new();

    private SystemDeclaration(Type type, SystemAttribute attribute)
    {
        Type = type;
        Id = attribute.Id;
        Phase = attribute.Phase;
        Before = attribute.Before ?? Array.Empty<string>();
        After = attribute.After ?? Array.Empty<string>();
        Condition = attribute.Condition;
    }

    public Type Type { get; }
    public string Id { get; }
    public Phase Phase { get; }
    public string[] Before { get; }
    public string[] After { get; }
    public RunCondition Condition { get; }

    // The declaration on `type`, or null for a system that has none (a test's probe).
    public static SystemDeclaration? Of(Type type) => ByType.GetOrAdd(type, static t =>
        t.GetCustomAttribute<SystemAttribute>(inherit: false) is { } a ? new SystemDeclaration(t, a) : null);

    // Every system `assembly` declares: from its generated table when the generator ran on it, else by
    // looking at its types — the dev fallback for an assembly built without it (the test assembly).
    // A pure function of the assembly, so it is cached for the process.
    public static IReadOnlyList<SystemDeclaration> In(Assembly assembly) => ByAssembly.GetOrAdd(assembly, static a =>
    {
        var generated = a.GetCustomAttributes<GeneratedRegistrationsAttribute>().ToList();
        IEnumerable<Type> types = generated.Count > 0
            ? generated.Select(g => Activator.CreateInstance(g.Type)).OfType<IGeneratedSystems>().SelectMany(g => g.Systems)
            : AllTypes(a);
        return types.Select(Of).OfType<SystemDeclaration>().ToList();
    });

    private static IEnumerable<Type> AllTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.OfType<Type>(); }
    }
}

// The system ids the loaded assemblies declare, whether or not they are in a given world. What tells a
// typo (declared nowhere: an error) from a system whose plugin is turned off or whose module added it
// conditionally (declared, not added: the constraint is moot) — see SystemScheduler.
public sealed class SystemCatalog
{
    private readonly object _lock = new();
    private readonly HashSet<Assembly> _assemblies = new();
    private readonly Dictionary<string, SystemDeclaration> _byId = new(StringComparer.Ordinal);

    public void Include(Assembly assembly)
    {
        lock (_lock)
        {
            if (!_assemblies.Add(assembly)) return;
            foreach (var declaration in SystemDeclaration.In(assembly))
            {
                if (_byId.TryGetValue(declaration.Id, out var existing) && existing.Type != declaration.Type)
                    throw new InvalidOperationException(
                        $"System id '{declaration.Id}' is declared by both {existing.Type.FullName} and {declaration.Type.FullName}. " +
                        "Ids are unique; to swap one system for another, use world.Systems.Replace.");
                _byId[declaration.Id] = declaration;
            }
        }
    }

    public bool TryGet(string id, out SystemDeclaration declaration)
    {
        lock (_lock) return _byId.TryGetValue(id, out declaration!);
    }

    // Every declared system, by id: for the registry dump (issue #18).
    public IReadOnlyList<SystemDeclaration> All
    {
        get { lock (_lock) return _byId.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToList(); }
    }
}
