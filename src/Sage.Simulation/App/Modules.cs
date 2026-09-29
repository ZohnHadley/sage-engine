#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace Sage.Simulation;

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
public interface IGameModule : IModule
{
    // The game's rules for a new world (16 §3.1). Called once every module's OnWorldCreated has run,
    // then the rules' OnWorldStarted. Null means DefaultGameRules. This replaces having to install the
    // rules as a resource inside OnWorldCreated at exactly the right moment (issue #13); a module that
    // still does that keeps working, and CreateRules wins over it.
    GameRules? CreateRules(World world) => null;
}

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
    private readonly Dictionary<IModule, PluginInfo> _plugins = new();

    public ModuleManager(Engine engine) { Engine = engine; }

    public Engine Engine { get; }
    public IReadOnlyList<IModule> Modules => _ordered.Count > 0 ? _ordered : _modules;
    public IGameModule? Game { get; private set; }

    // A module's plugin identity: id, version, kind and what it requires by id (issue #12).
    public PluginInfo Plugin(IModule module) => _plugins.TryGetValue(module, out var info) ? info : PluginInfo.Of(module);

    // Closed by InitAll: a module added after that would never run Init.
    public RegistrationSeal Seal { get; } = new("module", "it never ran Init");

    public void Add(IModule module)
    {
        Seal.Check(module.Name);
        if (_modules.Any(m => m.GetType() == module.GetType()))
            throw new InvalidOperationException($"Module {module.Name} is already added.");
        var info = PluginInfo.Of(module);
        if (_modules.FirstOrDefault(m => _plugins[m].Id.Equals(info.Id, StringComparison.OrdinalIgnoreCase)) is { } same)
            throw new InvalidOperationException($"Two modules claim the plugin id '{info.Id}': {same.GetType().FullName} and {module.GetType().FullName}.");
        _plugins[module] = info;
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

    // A service for code a plugin declared rather than ran — a prefab part (issue #17) — under the same
    // rule as that plugin's ModuleContext.Get. An owner that is not a loaded plugin (the host, a test
    // registering a part by hand) gets what the host would.
    internal T GetService<T>(string plugin) where T : class
    {
        foreach (var m in _modules)
            if (_plugins[m].Id.Equals(plugin, StringComparison.OrdinalIgnoreCase)) return GetService<T>(m);
        if (!_services.TryGetValue(typeof(T), out var entry))
            throw new InvalidOperationException($"{plugin} asked for {typeof(T).Name}, which nothing provides.");
        return (T)entry.Service;
    }

    // ---- Lifecycle ---------------------------------------------------------------------------------

    public void InitAll()
    {
        Seal.Seal("the modules ran Init");
        _ordered = Sort(_modules, _plugins);
        foreach (var m in _modules)
        {
            Engine.Generated.Include(m.GetType().Assembly);
            Engine.SystemCatalog.Include(m.GetType().Assembly);
        }
        var builder = new RegistrationBuilder(Engine);
        foreach (var m in _ordered)
            Run(m, "Init", () =>
            {
                // What the plugin declares, then what it registers by hand: its declarations exist
                // by the time its own Init runs, as if it had registered them first (issue #16).
                Engine.Generated.Register(_plugins[m].Id, builder);
                m.Init(new ModuleContext(this, m));
            });
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
        var ledger = Engine.Registrations;
        ledger.Owner = Plugin(m).Id;   // whatever it registers now is this plugin's
        try { action(); }   // failures propagate: a module that can't start is fatal (01 §8)
        finally { ledger.Owner = "host"; }
        _state[m] = step;
        Log.Debug(LogCat.Modules, $"{m.Name}.{step} ({watch.Elapsed.TotalMilliseconds:F1} ms)");
    }

    // Topological sort by Dependencies, stable by the order modules were added. A missing dependency
    // or a cycle is fatal, reported with the chain (01 §8).
    internal static List<IModule> Sort(IReadOnlyList<IModule> modules) =>
        Sort(modules, modules.ToDictionary(m => m, PluginInfo.Of));

    // Dependencies come two ways: by C# type (IModule.Dependencies, for modules in assemblies that
    // reference each other) and by plugin id with a version range ([RequiresPlugin], for ones that
    // don't). A missing plugin or a version outside the range is fatal, and says which and why.
    internal static List<IModule> Sort(IReadOnlyList<IModule> modules, IReadOnlyDictionary<IModule, PluginInfo> plugins)
    {
        var byType = modules.ToDictionary(m => m.GetType());
        var byId = modules.ToDictionary(m => plugins[m].Id, StringComparer.OrdinalIgnoreCase);
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
            foreach (var (id, range) in plugins[m].Requires)
            {
                if (!byId.TryGetValue(id, out var d))
                    throw new InvalidOperationException($"{plugins[m].Id} requires plugin {id} {range}, which isn't loaded " +
                        "(not in game.json's \"plugins\", or disabled?).");
                if (!range.Contains(plugins[d].Version))
                    throw new InvalidOperationException($"{plugins[m].Id} requires {id} {range}, but {id} {plugins[d].Version} is loaded.");
                Visit(d, chain);
            }
            chain.Pop();
            state[m] = 2;
            result.Add(m);
        }
    }

    public void RegisterCommands(CVarRegistry cvars)
    {
        cvars.RegisterCommand("plugins", CVarFlags.None,
            "plugins [id]: every plugin with its version, kind and requirements; with an id, what it registered.", a =>
        {
            if (a.Count > 0)
            {
                var owned = Engine.Registrations.By(a[0]).ToList();
                Log.Info(LogCat.Console, owned.Count == 0 ? $"{a[0]} registered nothing (or no such plugin; `plugins` lists them)"
                    : $"{a[0]} registered {owned.Count}:\n" + string.Join("\n", owned.Select(o => $"  {o.Kind,-13} {o.Name}")));
                return;
            }
            foreach (var m in Modules)
            {
                var info = Plugin(m);
                var needs = m.Dependencies.Select(d => _modules.FirstOrDefault(x => x.GetType() == d) is { } x ? Plugin(x).Id : d.Name)
                    .Concat(info.Requires.Select(r => $"{r.Id} {r.Range}")).ToList();
                int count = Engine.Registrations.By(info.Id).Count();
                Log.Info(LogCat.Console, $"  {info.Id,-26} {info.Version,-8} {info.Kind,-8} {count,4} registered" +
                                         (needs.Count > 0 ? $"  (needs {string.Join(", ", needs)})" : ""));
            }
        });
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
        var found = ModulesIn(assembly, "`modules.add`");
        if (found.Count == 0)
            Log.Warn(LogCat.Modules, $"{assembly.GetName().Name} has no public IModule class; nothing was added");
        else
            Log.Info(LogCat.Modules, $"Module assembly {assembly.GetName().Name} {assembly.GetName().Version} → {string.Join(", ", found.Select(m => m.Name))}");
        return found;
    }

    // One of every public IModule in an assembly. An IGameModule is left out: a game has one, in the
    // assembly game.json names in "assembly".
    private static List<IModule> ModulesIn(Assembly assembly, string from)
    {
        var found = assembly.GetExportedTypes()
            .Where(t => typeof(IModule).IsAssignableFrom(t) && !typeof(IGameModule).IsAssignableFrom(t)
                        && t is { IsAbstract: false, IsInterface: false })
            .Select(t => (IModule)Activator.CreateInstance(t)!)
            .ToList();
        foreach (var game in assembly.GetExportedTypes().Where(t => typeof(IGameModule).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false }))
            Log.Warn(LogCat.Modules, $"{assembly.GetName().Name}: {game.Name} is an IGameModule, and a game has one, in the assembly game.json names in \"assembly\"; " +
                                     $"from {from} it is ignored. Make it an IModule, or move it.");
        return found;
    }

    // ---- Kits ---------------------------------------------------------------------------------------

    // The assembly a kit's plugin id names (issue #27): each dot-separated part capitalised, so
    // "sage.kits.rpg" is Sage.Kits.Rpg. Its client half, when it has one, is that name plus ".Client".
    public static string KitAssemblyName(string kit) =>
        string.Join(".", kit.Split('.').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    // A kit a game.json names in "kits" (issue #27): its assembly's modules, one of which must be the
    // plugin the id names. A kit is found the way a game's own assemblies are, by path — beside the
    // game's assembly or its `modules.add` ones, or beside the host — or is already part of the app
    // (a tool or a test that references it). `client`: its client half too, when there is one; a host
    // without a window leaves it out, as it leaves out `modules.add`.
    public static IReadOnlyList<IModule> LoadKit(string kit, IReadOnlyList<string> folders, bool client)
    {
        string name = KitAssemblyName(kit);
        var assembly = FindAssembly(name, folders)
            ?? throw new FileNotFoundException($"game.json names the kit '{kit}', and there is no {name}.dll beside the game or the host " +
                                               $"(looked in {string.Join(", ", folders.Distinct())}). Reference the kit from the game's project and build it.");
        var modules = ModulesIn(assembly, $"the kit {kit}");
        if (!modules.Any(m => PluginInfo.Of(m).Id.Equals(kit, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"{name} has no plugin '{kit}' (its modules: " +
                $"{(modules.Count == 0 ? "none" : string.Join(", ", modules.Select(m => PluginInfo.Of(m).Id)))}), so it is not the kit game.json names.");

        if (client)
        {
            if (FindAssembly(name + ".Client", folders) is { } half)
                modules.AddRange(ModulesIn(half, $"the kit {kit}"));
            else
                Log.Info(LogCat.Modules, $"The kit {kit} has no client half ({name}.Client.dll)");
        }
        Log.Info(LogCat.Modules, $"Kit {kit} ({name} {assembly.GetName().Version}) → {string.Join(", ", modules.Select(m => m.Name))}");
        return modules;
    }

    // Already loaded, part of the app, or a file in one of the folders — in that order, so a kit the
    // process already has is never loaded a second time from somewhere else.
    private static Assembly? FindAssembly(string name, IReadOnlyList<string> folders)
    {
        var loaded = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name);
        if (loaded != null) return loaded;
        try { return Assembly.Load(new AssemblyName(name)); }
        catch (FileNotFoundException) { }   // not one of the app's own: look for the file
        foreach (string folder in folders)
        {
            string path = Path.Combine(folder, name + ".dll");
            if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(path));
        }
        return null;
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
