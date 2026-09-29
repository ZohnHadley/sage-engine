#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Sage.Simulation;

// What Sage.Generators writes into each assembly that declares things (issue #16): for a plugin id,
// register every declaration that plugin owns. Not written by hand.
public interface IGeneratedRegistrations
{
    // The plugin ids this assembly has declarations for.
    IReadOnlyList<string> Owners { get; }

    void Register(string plugin, RegistrationBuilder builder);
}

// Marks the generated registrations of an assembly, so the module manager finds them without
// scanning types. One per generator that has something to register (records, and parts: issue #17).
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class GeneratedRegistrationsAttribute : Attribute
{
    public GeneratedRegistrationsAttribute(Type type) { Type = type; }
    public Type Type { get; }
}

// The registrations generated code may make: the same registries a module's Init would call, so the
// seals, the duplicate checks and the ledger apply exactly as they did to hand-written calls.
public sealed class RegistrationBuilder
{
    private readonly Engine _engine;

    internal RegistrationBuilder(Engine engine) { _engine = engine; }

    public void Record<T>() where T : class, new() => _engine.Records.Register<T>();
    public void SavedResource<T>() where T : class, new() => _engine.Saves.RegisterResource<T>();
    public void PrefabPart<T>() where T : class, IPrefabPart, new() => _engine.Prefabs.Register<T>();

    // An entry of an open vocabulary (issue #28): `[QuestObjective("reach")] class ReachObjective`.
    public void Vocabulary<TEntry, T>(string id) where TEntry : class where T : class, TEntry, new() =>
        _engine.Vocabularies.Of<TEntry>().Register<T>(id);
}

// The plugin id of declarations the engine itself owns: record types every game uses (prefabs,
// placements) and state every world saves (the weather). Registered when the Engine is made.
internal static class RegistrationOwners
{
    public const string Core = "sage.core";
}

// The generated registrations of a set of assemblies, run per plugin.
public sealed class GeneratedRegistrations
{
    private readonly List<IGeneratedRegistrations> _all = new();
    private readonly HashSet<Assembly> _assemblies = new();

    // Adds an assembly's generated registrations, if it has any. Safe to call twice.
    public void Include(Assembly assembly)
    {
        if (!_assemblies.Add(assembly)) return;
        foreach (var attr in assembly.GetCustomAttributes<GeneratedRegistrationsAttribute>())
            _all.Add((IGeneratedRegistrations)Activator.CreateInstance(attr.Type)!);
    }

    // Every declaration `plugin` owns, in every included assembly.
    public void Register(string plugin, RegistrationBuilder builder)
    {
        foreach (var generated in _all) generated.Register(plugin, builder);
    }

    // Plugin ids that own a declaration somewhere, for `plugins` and for tests.
    public IEnumerable<string> Owners
    {
        get
        {
            foreach (var generated in _all)
                foreach (string owner in generated.Owners) yield return owner;
        }
    }
}
