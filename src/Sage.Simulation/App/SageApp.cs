#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Simulation;

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

    // The plugins a game may switch on in game.json (`plugins`; all of them when it names none) and
    // off (`modules.disable`), in install order. A host passes the base engine's, BasePlugins.All()
    // (Sage.Gameplay), which this assembly cannot name: the simulation does not know the gameplay built
    // on it. Empty: a bare engine (a test, a tool).
    public IReadOnlyList<IModule> AvailablePlugins { get; init; } = Array.Empty<IModule>();

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

    // The scene every world starts in, overriding game.json's `"scene"`: for a test, or a host with no
    // manifest. Resolved like the manifest's, in the game's namespace (issue #29).
    public string? StartScene { get; init; }

    // Whether `developer`, `log_file_level` and `log_queue_size` configure the process's log (issue
    // #11). The executable's app does; a test, a tool or a second app in the same process leaves the
    // log as the host set it (CoreCVars.OwnsProcessLog).
    public bool OwnsProcessLog { get; init; }
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

    // Creates the engine, mounts content and adds every module; nothing runs yet. Throws when a
    // module or the game assembly can't be loaded — a host reports that and stops.
    public static SageApp Create(SageAppOptions options)
    {
        var cvars = new CVarRegistry();
        var engine = new Engine(cvars, CoreCVars.Register(cvars, options.OwnsProcessLog));
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

        foreach (var module in ChoosePlugins(_options.AvailablePlugins, Game?.Plugins)) Add(module);
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

    // The plugins a game.json asks for, plus everything they require (by type or by id), in
    // AvailablePlugins' order. Null means all of them. A pattern that matches nothing is an error that
    // lists what there is — a misspelt plugin would otherwise be a game quietly missing a system.
    private static IEnumerable<IModule> ChoosePlugins(IReadOnlyList<IModule> all, IReadOnlyList<string>? wanted)
    {
        if (wanted == null || all.Count == 0) return all;   // a bare engine has none to choose from

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
        Engine.Scenes.RegisterCommands(CVars);   // scene_load (issue #29)
        // Entity and scale commands work on any world, so every host has them, not only the one with
        // a window: a server's console and a test can spawn and list entities too.
        WorldConsoleCommands.Register(CVars, Engine);
        ScaleConsoleCommands.Register(CVars, Engine);   // scale_spawn / scale_report (R18)
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
        Engine.Vocabularies.Seal("content was loaded");   // records name their entries (issue #28)
        Engine.Records.Load(Engine.Vfs);   // seals record types
        ChooseStartScene();
    }

    // game.json's `"scene"` (or StartScene), which must name a scene record: a typo there is a load
    // error like any other content mistake, not a world that quietly starts empty.
    private void ChooseStartScene()
    {
        string? text = _options.StartScene ?? Game?.Scene;
        if (string.IsNullOrWhiteSpace(text)) return;
        string where = _options.StartScene != null ? "the start scene" : "game.json's \"scene\"";
        RecordId id;
        try { id = RecordId.Parse(text, Game?.Id ?? "sage"); }
        catch (FormatException ex) { throw new InvalidDataException($"{where} '{text}': {ex.Message}", ex); }
        if (!Engine.Records.Exists("scene", id))
        {
            var scenes = Engine.Records.Ids("scene").Select(i => i.ToString()).OrderBy(i => i, StringComparer.Ordinal).ToList();
            throw new InvalidDataException($"{where} names '{id}', which is not a scene record " +
                (scenes.Count == 0 ? "(there are none)" : $"(scenes: {string.Join(", ", scenes)})"));
        }
        Engine.Scenes.Start = id;
        Log.Info(LogCat.World, $"Start scene: {id}");
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
