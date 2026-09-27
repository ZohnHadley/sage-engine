#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace sage_engine;

public enum ModuleKind { Runtime, Editor, Tool }

// A logical engine, framework or game unit (docs/design/01 §3.1, §4). Lifecycle:
//   Init            register only: cvars, commands, record types, services (no records/assets/GPU yet)
//   Start           records are loaded and the GPU exists: read records, load engine-scope assets
//   OnWorldCreated  install per-world resources and systems (every module, every world)
//   Shutdown        reverse dependency order
public interface IModule
{
    string Name => GetType().Name;
    ModuleKind Kind => ModuleKind.Runtime;
    IReadOnlyList<Type> Dependencies => Array.Empty<Type>();

    void Init(ModuleContext ctx);
    void Start(ModuleContext ctx) { }
    void OnWorldCreated(World world) { }
    void Shutdown() { }
}

// The single entry point of a game assembly (01 §4). Exactly one public class per game assembly.
public interface IGameModule : IModule { }

public sealed class ModuleContext
{
    private readonly ModuleManager _manager;
    private readonly IModule _module;

    internal ModuleContext(ModuleManager manager, IModule module)
    {
        _manager = manager;
        _module = module;
    }

    public Engine Engine => _manager.Engine;

    // Services from the host, or from modules this one declares as dependencies. Anything else throws,
    // which keeps dependency lists honest (01 §4).
    public T Get<T>() where T : class => _manager.GetService<T>(_module);

    public void Provide<T>(T service) where T : class => _manager.ProvideService(_module, service);
}

public sealed class ModuleManager
{
    private readonly List<IModule> _modules = new();
    private readonly Dictionary<Type, (object Service, IModule? Owner)> _services = new();
    private List<IModule> _ordered = new();
    private readonly Dictionary<IModule, string> _state = new();

    public ModuleManager(Engine engine) { Engine = engine; }

    public Engine Engine { get; }
    public IReadOnlyList<IModule> Modules => _ordered.Count > 0 ? _ordered : _modules;
    public IGameModule? Game { get; private set; }

    // Closed by InitAll: a module added after that would never run Init.
    public RegistrationSeal Seal { get; } = new("module", "it never ran Init");

    public void Add(IModule module)
    {
        Seal.Check(module.Name);
        if (_modules.Any(m => m.GetType() == module.GetType()))
            throw new InvalidOperationException($"Module {module.Name} is already added.");
        _modules.Add(module);
        if (module is IGameModule game) Game = game;
    }

    // Services the host offers every module (graphics, the MonoGame Game...).
    public void ProvideHostService<T>(T service) where T : class => _services[typeof(T)] = (service, null);

    internal void ProvideService<T>(IModule owner, T service) where T : class
    {
        if (_services.TryGetValue(typeof(T), out var existing))
            throw new InvalidOperationException($"{owner.Name} provides {typeof(T).Name}, already provided by {existing.Owner?.Name ?? "the host"}.");
        _services[typeof(T)] = (service, owner);
    }

    internal T GetService<T>(IModule requester) where T : class
    {
        if (!_services.TryGetValue(typeof(T), out var entry))
            throw new InvalidOperationException($"{requester.Name} asked for {typeof(T).Name}, which nothing provides (yet — services from Start are only available in Start and later).");
        if (entry.Owner != null && entry.Owner != requester && !requester.Dependencies.Contains(entry.Owner.GetType()))
            throw new InvalidOperationException($"{requester.Name} uses {typeof(T).Name} from {entry.Owner.Name} but doesn't list it in Dependencies.");
        return (T)entry.Service;
    }

    // ---- Lifecycle ---------------------------------------------------------------------------------

    public void InitAll()
    {
        Seal.Seal("the modules ran Init");
        _ordered = Sort(_modules);
        foreach (var m in _ordered) Run(m, "Init", () => m.Init(new ModuleContext(this, m)));
        Log.Info(LogCat.Modules, $"Modules: {string.Join(", ", _ordered.Select(m => m.Name))}");
    }

    public void StartAll()
    {
        foreach (var m in _ordered) Run(m, "Start", () => m.Start(new ModuleContext(this, m)));
    }

    public void NotifyWorldCreated(World world)
    {
        foreach (var m in _ordered) Run(m, $"OnWorldCreated({world.Name})", () => m.OnWorldCreated(world));
    }

    public void ShutdownAll()
    {
        for (int i = _ordered.Count - 1; i >= 0; i--)
        {
            var m = _ordered[i];
            try { m.Shutdown(); _state[m] = "shut down"; }
            catch (Exception ex) { Log.Error(LogCat.Modules, $"{m.Name}.Shutdown failed: {ex.Message}"); }
        }
    }

    private void Run(IModule m, string step, Action action)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        action();   // failures propagate: a module that can't start is fatal (01 §8)
        _state[m] = step;
        Log.Debug(LogCat.Modules, $"{m.Name}.{step} ({watch.Elapsed.TotalMilliseconds:F1} ms)");
    }

    // Topological sort by Dependencies, stable by the order modules were added. A missing dependency
    // or a cycle is fatal, reported with the chain (01 §8).
    internal static List<IModule> Sort(IReadOnlyList<IModule> modules)
    {
        var byType = modules.ToDictionary(m => m.GetType());
        var result = new List<IModule>();
        var state = new Dictionary<IModule, int>();   // 1 = visiting, 2 = done
        foreach (var m in modules) Visit(m, new Stack<IModule>());
        return result;

        void Visit(IModule m, Stack<IModule> chain)
        {
            if (state.TryGetValue(m, out int s))
            {
                if (s == 2) return;
                throw new InvalidOperationException($"Module dependency cycle: {string.Join(" -> ", chain.Reverse().Select(x => x.Name))} -> {m.Name}");
            }
            state[m] = 1;
            chain.Push(m);
            foreach (var dep in m.Dependencies)
            {
                if (!byType.TryGetValue(dep, out var d))
                    throw new InvalidOperationException($"Module {m.Name} depends on {dep.Name}, which isn't loaded (disabled in game.json?).");
                Visit(d, chain);
            }
            chain.Pop();
            state[m] = 2;
            result.Add(m);
        }
    }

    public void RegisterCommands(CVarRegistry cvars)
    {
        cvars.RegisterCommand("modules", CVarFlags.None, "List modules in init order.", _ =>
        {
            foreach (var m in Modules)
            {
                string deps = m.Dependencies.Count == 0 ? "" : $"  (needs {string.Join(", ", m.Dependencies.Select(d => d.Name))})";
                Log.Info(LogCat.Console, $"  {m.Name,-20} {m.Kind,-8} {(_state.TryGetValue(m, out var st) ? st : "added")}{deps}");
            }
        });
    }

    // ---- Game assemblies ----------------------------------------------------------------------------

    // Loads a game assembly and creates its one IGameModule. Must happen before the first World is
    // created (Friflo builds its component schema from the assemblies loaded at that moment, 03 §3.1).
    // Extra module assemblies a game asks for in `game.json`'s `modules.add` (01 §3.3). Every public
    // IModule in them is added — that is how a game ships its client half separately from its
    // simulation (R15), and how a mod will ship a C# module later (17).
    public static IEnumerable<IModule> LoadModules(string assemblyPath)
    {
        string full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Module assembly not found: {full}. Build the solution, or fix \"modules.add\" in game.json.");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
        var found = assembly.GetExportedTypes()
            .Where(t => typeof(IModule).IsAssignableFrom(t) && !typeof(IGameModule).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IModule)Activator.CreateInstance(t)!)
            .ToList();
        foreach (var game in assembly.GetExportedTypes().Where(t => typeof(IGameModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
            Log.Warn(LogCat.Modules, $"{assembly.GetName().Name}: {game.Name} is an IGameModule, and a game has one, in the assembly game.json names in \"assembly\"; " +
                                     "from `modules.add` it is ignored. Make it an IModule, or move it.");
        if (found.Count == 0)
            Log.Warn(LogCat.Modules, $"{assembly.GetName().Name} has no public IModule class; nothing was added");
        else
            Log.Info(LogCat.Modules, $"Module assembly {assembly.GetName().Name} {assembly.GetName().Version} → {string.Join(", ", found.Select(m => m.Name))}");
        return found;
    }

    public static IGameModule LoadGame(string assemblyPath)
    {
        string full = Path.GetFullPath(assemblyPath);
        if (!File.Exists(full))
            throw new FileNotFoundException($"Game assembly not found: {full}. Build the solution (dotnet build Sage.sln) or fix \"assembly\" in game.json.");
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(full);
        var types = assembly.GetExportedTypes()
            .Where(t => typeof(IGameModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }).ToList();
        if (types.Count != 1)
            throw new InvalidOperationException($"{assembly.GetName().Name} must contain exactly one public IGameModule class (found {types.Count}).");
        var game = (IGameModule)Activator.CreateInstance(types[0])!;
        Log.Info(LogCat.Modules, $"Game assembly {assembly.GetName().Name} {assembly.GetName().Version} → {game.Name}");
        return game;
    }
}
