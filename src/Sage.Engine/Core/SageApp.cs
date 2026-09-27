#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace sage_engine;

// What kind of host an app runs in. A module's Kind says where it belongs: Runtime modules load in
// every host, Editor modules only in the editor, Tool modules only in tools (issue #12).
public enum HostKind { Game, Server, Editor, Tool }

// What an app is made of (docs/REDESIGN.md §3.2, issue #10). Everything a host would otherwise decide
// in its own boot code, so that the game executable, a headless server, the editor and a test all boot
// the same way from the same description.
public sealed class SageAppOptions
{
    // The game: its record namespace, content mounts, game assembly and `modules.add`/`disable`. Null
    // for a bare engine (a test, a tool).
    public GameManifest? Game { get; init; }

    // Engine content, mounted first under the `sage` namespace (05 §3.1). The host passes the
    // `Content/` folder beside its executable; a test passes the repository's `engine_content/`.
    public string? EngineContentDirectory { get; init; }

    // The engine's simulation modules — physics, streaming, maps and gameplay (SimulationModules). On
    // by default, because a game without them is the exception; a game can still disable any one of
    // them by name in game.json.
    public bool IncludeSimulationModules { get; init; } = true;

    // The host this app runs in; decides which module kinds load.
    public HostKind Host { get; init; } = HostKind.Game;

    // Modules a host adds after the simulation ones and before the game's: the client, for a host
    // with a window. A headless host leaves this empty.
    public IReadOnlyList<IModule> HostModules { get; init; } = Array.Empty<IModule>();

    // Executed once every cvar and command exists, before content loads (Source's config.cfg).
    // Archived cvars are written back to it on shutdown. Null: neither.
    public string? ConfigFile { get; init; }

    // `+command` launch arguments, run once the first world exists.
    public IReadOnlyList<string> LaunchCommands { get; init; } = Array.Empty<string>();
}

// The order an app goes through. Each step checks it comes after the one before, so a host that
// skips or repeats one hears about it at once instead of meeting the consequence later — a cvar that
// silently misses config.cfg, records read before their types were registered (MAKING_A_GAME §10).
public enum AppStage { Created, Registered, Configured, ContentLoaded, Started, Running, Shutdown }

// One boot path for every host (REDESIGN §3.2, issue #10). A host creates the app, then walks it
// through its stages, doing its own work in between: the game executable registers its window's
// cvars after Register, provides its GPU services before Start, and so on.
//
//   Create        cvars, engine, VFS mounts, modules added (the game assembly is loaded here, before
//                 any World exists, because the ECS schema is built from the loaded assemblies, 03 §3.1)
//   Register      every module's Init, then the engine's own console commands
//   Configure     config.cfg
//   LoadContent   records, from every mount
//   Start         every module's Start
//   CreateWorld   worlds (each module's OnWorldCreated, then the game's rules)
//
// Boot() walks a headless app through all of it in one call.
public sealed class SageApp : IDisposable
{
    private readonly SageAppOptions _options;

    private SageApp(SageAppOptions options, Engine engine)
    {
        _options = options;
        Engine = engine;
    }

    public Engine Engine { get; }
    public CVarRegistry CVars => Engine.CVars;
    public GameManifest? Game => _options.Game;
    public AppStage Stage { get; private set; } = AppStage.Created;

    // The engine's simulation modules, in the order a host installs them: physics before anything that
    // sweeps it, streaming and maps, then every gameplay feature (GameplayModules.All, R15). **The**
    // list — the host, a headless server and the tests all take it from here, so none can ship a set
    // the others don't (ModuleSetTests; the LightsModule bug of 2026-09-25).
    public static IModule[] SimulationModules()
    {
        var modules = new List<IModule>
        {
            new PhysicsModule(),
            new StreamingModule(),   // terrain rings and origin rebasing (R6, F14)
            new MapModule(),         // brush levels imported from TrenchBroom (15 §3, F16)
        };
        modules.AddRange(GameplayModules.All());
        return modules.ToArray();
    }

    // Creates the engine, mounts content and adds every module; nothing runs yet. Throws when a
    // module or the game assembly can't be loaded — a host reports that and stops.
    public static SageApp Create(SageAppOptions options)
    {
        var cvars = new CVarRegistry();
        var engine = new Engine(cvars, CoreCVars.Register(cvars));
        var app = new SageApp(options, engine);
        try
        {
            app.Mount();
            app.AddModules();
        }
        catch
        {
            engine.Dispose();
            throw;
        }
        return app;
    }

    private void Mount()
    {
        // Engine content first, then the game's mounts in order (later wins, 05 §3.1). Mods: F37.
        if (_options.EngineContentDirectory is { } engineContent)
            Engine.Vfs.Mount(new FolderMount("engine", engineContent, "sage"));
        if (Game is { } game)
            foreach (string mount in game.Mounts)
                Engine.Vfs.Mount(new FolderMount($"{game.Id}/{mount}", Path.Combine(game.Directory, mount), game.Id));
    }

    private void AddModules()
    {
        var disabled = new HashSet<string>(Game?.Modules.Disable ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(IModule module)
        {
            string id = PluginInfo.Of(module).Id;
            seen.Add(module.Name);
            seen.Add(id);
            if (disabled.Contains(module.Name) || disabled.Contains(id))
                Log.Info(LogCat.Modules, $"{module.Name} disabled by game.json");
            else if (!KindBelongs(module.Kind, _options.Host))
                Log.Info(LogCat.Modules, $"{module.Name} is a {module.Kind} module; a {_options.Host} host doesn't load it");
            else
                Engine.Modules.Add(module);
        }

        if (_options.IncludeSimulationModules)
            foreach (var module in ChooseSimulationModules(Game?.Plugins)) Add(module);
        foreach (var module in _options.HostModules) Add(module);

        if (Game is { } game)
        {
            if (!string.IsNullOrEmpty(game.Assembly))
                Engine.Modules.Add(ModuleManager.LoadGame(game.AssemblyPath));
            // `modules.add`: the game's other assemblies, such as its client half (01 §3.3, R15).
            foreach (string extra in game.ModuleAssemblies)
                foreach (var module in ModuleManager.LoadModules(extra))
                    Add(module);
        }

        // A name in `disable` that matches nothing is a typo, and a typo there means a module the game
        // meant to turn off is running.
        foreach (string name in disabled.Where(n => !seen.Contains(n)))
            Log.Warn(LogCat.Modules, $"game.json disables '{name}', which is not a module this app has " +
                $"(modules: {string.Join(", ", seen.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))})");
    }

    private static bool KindBelongs(ModuleKind kind, HostKind host) => kind switch
    {
        ModuleKind.Editor => host == HostKind.Editor,
        ModuleKind.Tool => host == HostKind.Tool,
        _ => true,
    };

    // The simulation plugins a game.json asks for, plus everything they require (by type or by id), in
    // SimulationModules' order. Null means all of them. A pattern that matches nothing is an error that
    // lists what there is — a misspelt plugin would otherwise be a game quietly missing a system.
    private static IEnumerable<IModule> ChooseSimulationModules(IReadOnlyList<string>? wanted)
    {
        var all = SimulationModules();
        if (wanted == null) return all;

        var infos = all.ToDictionary(m => m, PluginInfo.Of);
        var chosen = new HashSet<IModule>();
        foreach (string pattern in wanted)
        {
            var matches = all.Where(m => infos[m].Matches(pattern)).ToList();
            if (matches.Count == 0)
                throw new InvalidOperationException($"game.json asks for plugin '{pattern}', which isn't one the engine has. " +
                    $"Plugins: {string.Join(", ", all.Select(m => infos[m].Id))}");
            chosen.UnionWith(matches);
        }

        // Pull in what they need, so "sage.gameplay.items" brings attributes and combat with it.
        var pending = new Stack<IModule>(chosen);
        while (pending.Count > 0)
        {
            var module = pending.Pop();
            var needs = module.Dependencies.Select(t => all.FirstOrDefault(m => m.GetType() == t))
                .Concat(infos[module].Requires.Select(r => all.FirstOrDefault(m => infos[m].Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase))));
            foreach (var need in needs)
                if (need != null && chosen.Add(need))
                {
                    Log.Info(LogCat.Modules, $"{infos[need].Id} added: {infos[module].Id} requires it");
                    pending.Push(need);
                }
        }
        return all.Where(chosen.Contains);
    }

    // Every module's Init — registration only — then the engine's own console commands.
    public void Register()
    {
        Advance(AppStage.Created, AppStage.Registered);
        Engine.Modules.InitAll();
        Engine.Modules.RegisterCommands(CVars);
        VirtualFileSystem.RegisterCommands(CVars, Engine.Vfs);
        Engine.Records.RegisterCommands(CVars);
        Engine.Saves.RegisterCommands(CVars);
    }

    // config.cfg, once every cvar and command a host will register exists.
    public void Configure()
    {
        Advance(AppStage.Registered, AppStage.Configured);
        CVars.CVarSeal.Seal("config.cfg was read");
        if (_options.ConfigFile is { } config && File.Exists(config))
            CVars.ExecFile(config, ExecSource.Config);
    }

    public void LoadContent()
    {
        Advance(AppStage.Configured, AppStage.ContentLoaded);
        Engine.Actions.Seal.Seal("content was loaded");
        Engine.Records.Load(Engine.Vfs);   // seals record types
    }

    public void Start()
    {
        Advance(AppStage.ContentLoaded, AppStage.Started);
        Engine.Modules.StartAll();
    }

    // Worlds can be created from Start on; the first one moves the app to Running.
    public World CreateWorld(string name)
    {
        if (Stage is not (AppStage.Started or AppStage.Running))
            throw new InvalidOperationException($"CreateWorld('{name}') needs a started app; it is {Stage}. Call Start first.");
        Stage = AppStage.Running;
        Engine.Prefabs.Seal.Seal("the first world was created");
        Engine.Inputs.Seal.Seal("the first world was created");
        return Engine.CreateWorld(name);
    }

    // `+command` launch arguments, in order. Once, with a world up (like Source's +map).
    public void RunLaunchCommands()
    {
        if (Stage != AppStage.Running)
            throw new InvalidOperationException($"Launch commands run once a world exists; the app is {Stage}.");
        foreach (string command in _options.LaunchCommands)
            CVars.Execute(command, ExecSource.LaunchArgs);
    }

    // A headless app's whole boot: every stage in order, a first world, then the launch commands.
    public World Boot(string worldName = "main")
    {
        Register();
        Configure();
        LoadContent();
        Start();
        var world = CreateWorld(worldName);
        RunLaunchCommands();
        return world;
    }

    private void Advance(AppStage from, AppStage to)
    {
        if (Stage != from)
            throw new InvalidOperationException($"{to} has to follow {from}, but the app is {Stage}.");
        Stage = to;
    }

    // Worlds first (their systems belong to modules), then modules in reverse dependency order, then
    // the archived cvars. Safe to call twice.
    public void Dispose()
    {
        if (Stage == AppStage.Shutdown) return;
        bool registered = Stage != AppStage.Created;
        Stage = AppStage.Shutdown;
        Engine.Dispose();
        Engine.Modules.ShutdownAll();
        if (registered && _options.ConfigFile is { } config)
        {
            try { CVars.SaveArchived(config); }
            catch (Exception ex) { Log.Warn(LogCat.Host, $"Couldn't save {config}: {ex.Message}"); }
        }
    }
}
