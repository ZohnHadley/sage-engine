#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Sage.Simulation;

// A code mod's assemblies (phase 9, issue #396; REDESIGN §4.4): one collectible AssemblyLoadContext per mod.
// The engine's own assemblies (Sage.*) and whatever else the host has loaded are shared with the host, so
// the mod's IModule is the engine's IModule; the mod's own dependencies are found beside its assemblies.
// Unloaded when the app is disposed (ModManager.UnloadCode): nothing in the engine may keep a mod's types in
// a static after that (Upgraders keeps its cache in a ConditionalWeakTable for this reason).
//
// A code mod is trusted code, not sandboxed: .NET has no sandbox for a loaded assembly (17 §2), so a mod's
// code can do whatever the game can. It is never downloaded by the engine; the player installs it.
internal sealed class ModCodeContext : AssemblyLoadContext
{
    private readonly List<string> _folders;

    public ModCodeContext(ModManifest mod) : base($"mod:{mod.Id}", isCollectible: true)
    {
        Mod = mod;
        _folders = mod.AssemblyPaths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.Ordinal).ToList();
    }

    public ModManifest Mod { get; }

    // The mod's assemblies, in the order mod.json names them, once Load has run.
    public List<Assembly> Loaded { get; } = new();

    // The modules the mod's assemblies declare: one of every public IModule.
    public List<IModule> Modules { get; } = new();

    // The engine's assemblies, and anything the host already has (Friflo, System.*), are the host's: a mod
    // that brought its own copy would have types the engine does not know. Otherwise the mod's folders.
    protected override Assembly? Load(AssemblyName name)
    {
        if (IsShared(name)) return null;
        foreach (string folder in _folders)
        {
            string path = Path.Combine(folder, name.Name + ".dll");
            if (File.Exists(path)) return LoadFromAssemblyPath(path);
        }
        return null;   // the framework's, from the default context
    }

    internal static bool IsShared(AssemblyName name) =>
        name.Name is { } n && (n == "Sage" || n.StartsWith("Sage.", StringComparison.Ordinal)
                               || Default.Assemblies.Any(a => a.GetName().Name == n));

    // Loads every assembly mod.json names and creates its modules. What is wrong with the mod itself (a
    // missing file, a bad image, no module) is an InvalidDataException worded for the player; anything else
    // is the mod's code or a type the engine does not have.
    public void LoadAll()
    {
        foreach (string path in Mod.AssemblyPaths)
        {
            string relative = Path.GetRelativePath(Mod.Directory, path).Replace('\\', '/');
            if (!File.Exists(path))
                throw new InvalidDataException($"its assembly {relative} is not there (build the mod: {path})");
            Assembly assembly;
            try { assembly = LoadFromAssemblyPath(path); }
            catch (BadImageFormatException ex) { throw new InvalidDataException($"its assembly {relative} is not a .NET assembly: {ex.Message}", ex); }
            if (IsShared(assembly.GetName()))
                throw new InvalidDataException($"its assembly {relative} is named {assembly.GetName().Name}, which is the engine's or the host's");
            Loaded.Add(assembly);
            Modules.AddRange(ModuleManager.ModulesIn(assembly, $"mod '{Mod.Id}'"));
        }
        if (Modules.Count == 0)
            throw new InvalidDataException($"its assemblies ({string.Join(", ", Loaded.Select(a => a.GetName().Name))}) have no public IModule class, so its code would never run");
    }

    // Lets go of the mod's assemblies and modules, then unloads. Lists of them kept in the context's own fields
    // would keep it alive: an object of a collectible type holds its load context, which holds the object.
    public void Release()
    {
        Loaded.Clear();
        Modules.Clear();
        Unload();
    }

    // The ECS component and tag types the mod's assemblies declare: they exist only if the process builds its
    // component schema after the mod is loaded (03 §3.1).
    public IEnumerable<Type> EcsTypes() =>
        Loaded.SelectMany(a => ModuleManager.Reflecting(a, () => a.GetTypes()))
              .Where(t => t is { IsAbstract: false } && (typeof(IComponent).IsAssignableFrom(t) || typeof(ITag).IsAssignableFrom(t)));
}
