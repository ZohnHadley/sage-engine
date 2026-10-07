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

    // Whether the kits game.json names (`kits`, issue #27) bring their client halves too
    // (`Sage.Kits.Rpg.Client` beside `Sage.Kits.Rpg`): the host with a window says yes; a headless
    // one, a tool or a test leaves them out, as it leaves out `modules.add`.
    public bool LoadKitClients { get; init; }

    // Executed once every cvar and command exists, before content loads (Source's config.cfg).
    // Archived cvars are written back to it on shutdown. Null: neither.
    public string? ConfigFile { get; init; }

    // autoexec.cfg (issue #299): commands run right after config.cfg, so they win over saved values.
    // Optional; a missing file is not an error.
    public string? AutoexecFile { get; init; }

    // `+command` launch arguments, run once the first world exists.
    public IReadOnlyList<string> LaunchCommands { get; init; } = Array.Empty<string>();

    // The scene every world starts in, overriding game.json's `"scene"`: for a test, or a host with no
    // manifest. Resolved like the manifest's, in the game's namespace (issue #29).
    public string? StartScene { get; init; }

    // Whether worlds wait at game.json's `"title"` screen before they start (issue #342). A host with a
    // window does, unless it was launched with `-notitle`; a test, a tool or a server starts the world at
    // once unless it asks. The title itself is checked either way: a typo there is a load error.
    public bool ShowTitle { get; init; }

    // The title screen, overriding game.json's `"title"`: for a test, or a host with no manifest. Resolved
    // in the game's namespace, like the start scene.
    public string? Title { get; init; }

    // Whether this app's log, user folder and crash sections are the process's (issue #11): the
    // executable's app, whose `developer`, `log_file_level` and `log_queue_size` configure the log the
    // host opened. A test, a tool or a second app in the same process gets an environment of its own
    // instead (AppEnvironment.CreateForApp, issue #49), under the one current when it is created (beside
    // another app, not under it): its own sinks and levels, its lines reaching that one's sinks too
    // (CoreCVars.OwnsProcessLog).
    public bool OwnsProcessLog { get; init; }

    // Mods (phase 4j, issue 4j-3; ModManager). Null: found in the game's `modsDirectory` and in
    // UserModsDirectory. A list: exactly these mod folders, in this order, and nothing found (the host's
    // -mods; empty is -nomods); the player's list is not applied to them.
    public IReadOnlyList<string>? Mods { get; init; }

    // The player's own mods folder, `user://mods`, looked in before the game's. Null: only the game's.
    public string? UserModsDirectory { get; init; }

    // The player's choices, `user://mods.json` (ModList): order and switched off. Null: every mod on, in
    // id order, and mod_enable / mod_disable / mod_move have nowhere to write.
    public string? ModListFile { get; init; }

    // `user://logs/mod_report.txt`, written once content has loaded. Null: not written.
    public string? ModReportFile { get; init; }

    // The player's rebinds, `user://input.json` (InputRebinds, issue #328): record patches mounted last, so
    // they win over the game and every mod, and where `bind`, `unbind`, `bind_reset` and the controls screen
    // write. Null: no rebinding is kept (the commands say so).
    public string? InputFile { get; init; }
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
    private AppEnvironment.Scope _created;   // the environment made current by Create, until Dispose

    private SageApp(SageAppOptions options, Engine engine)
    {
        _options = options;
        Engine = engine;
    }

    public Engine Engine { get; }

    // This app's log, user folder and crash report sections (issue #49). Create makes it current for the
    // rest of the code that created the app, and each step and world tick makes it current again, so
    // `Log`, `UserPaths` and `CrashReporter` mean this app's while it runs.
    public AppEnvironment Environment => Engine.Environment;
    public CVarRegistry CVars => Engine.CVars;
    public GameManifest? Game => _options.Game;
    public AppStage Stage { get; private set; } = AppStage.Created;

    // Creates the engine, mounts content and adds every module; nothing runs yet. Throws when a
    // module or the game assembly can't be loaded — a host reports that and stops.
    public static SageApp Create(SageAppOptions options)
    {
        var environment = options.OwnsProcessLog ? AppEnvironment.Process : AppEnvironment.CreateForApp();
        var created = environment.Enter();
        SageApp app;
        try
        {
            var cvars = new CVarRegistry();
            var engine = new Engine(cvars, CoreCVars.Register(cvars, options.OwnsProcessLog, environment)) { Environment = environment };
            app = new SageApp(options, engine) { _created = created };
        }
        catch
        {
            created.Dispose();
            environment.Dispose();
            throw;
        }
        var engineMade = app.Engine;
        try
        {
            app.MountEngine();
            app.AddModules();
            app.MountPlugins();
            app.MountGame();
            app.MountMods();
            app.Engine.Rebinds = new InputRebinds(app.Engine.Actions, app.Engine.Records, options.InputFile);
        }
        catch
        {
            engineMade.Dispose();
            engineMade.ModManager.UnloadCode();
            created.Dispose();
            environment.Dispose();
            throw;
        }
        return app;
    }

    // Engine content first, then the content the loaded plugins carry (a kit's, issue #98), then the
    // game's mounts in order, then the mods (MountMods): later wins (05 §3.1), so a game patches a kit as
    // it patches the engine, and a mod patches the game.
    private void MountEngine()
    {
        if (_options.EngineContentDirectory is { } engineContent)
            Engine.Vfs.Mount(new FolderMount("engine", engineContent, "sage"));
    }

    // [PluginContent] on a loaded module: its assembly's embedded `content/` files, one mount per plugin,
    // in the order the modules were added. A plugin that is switched off brings none.
    private void MountPlugins()
    {
        foreach (var module in Engine.Modules.Modules)
        {
            var type = module.GetType();
            if (System.Reflection.CustomAttributeExtensions.GetCustomAttribute<PluginContentAttribute>(type) is not { } content) continue;
            var mount = new AssemblyContentMount(PluginInfo.Of(module).Id, type.Assembly, content.RecordNamespace);
            if (mount.Count == 0)
                Log.Warn(LogCat.VFS, $"{mount.Name} says it carries content ([PluginContent]), and {type.Assembly.GetName().Name}.dll embeds " +
                                     $"no '{AssemblyContentMount.Prefix}' resources");
            Engine.Vfs.Mount(mount);
        }
    }

    private void MountGame()
    {
        if (Game is { } game)
            foreach (string mount in game.Mounts)
                Engine.Vfs.Mount(new FolderMount($"{game.Id}/{mount}", Path.Combine(game.Directory, mount), game.Id));
    }

    // Mods, after the game (phase 4j, issue 4j-3): found or named, ordered, a code mod's modules added (phase
    // 9), and each active one mounted as
    // `mods/<id>` in the namespace `<id>`. A bare engine (no game) has none unless a list names some. A mod
    // may not take the engine's, the game's or a loaded plugin's content namespace (`rpg`).
    private void MountMods()
    {
        if (Game == null && _options.Mods == null) return;
        var reserved = Engine.Modules.Modules
            .Select(m => System.Reflection.CustomAttributeExtensions.GetCustomAttribute<PluginContentAttribute>(m.GetType())?.RecordNamespace)
            .OfType<string>();
        var mods = ModManager.Discover(_options, Game, reserved);
        Engine.ModManager = mods;
        // Code mods (phase 9, issue #396): their assemblies now, before any Init and any world, and their
        // modules after every other one, the game's included. A mod whose code can't be used is refused.
        foreach (var module in mods.LoadCode(Engine.Modules.Modules, _options.Host))
            Engine.Modules.Add(module);
        Engine.Mods = mods.Loaded;
        mods.Mount(Engine.Vfs);
        mods.LogSummary();
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

        // The kits the game is built on (issue #27), before anything of the game's: its assemblies
        // reference them, and an assembly loaded by path finds its references among those loaded. Loaded
        // first so that the base plugins a kit needs come with it (the RPG kit's screens need sage.ui,
        // issue #98) even when game.json's `plugins` names only some; added after the plugins.
        var kits = LoadKits();
        foreach (var module in ChoosePlugins(_options.AvailablePlugins, Game?.Plugins, kits)) Add(module);
        foreach (var module in kits) Add(module);
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

    // game.json's `kits`, each looked for beside the game's assemblies and then beside the host.
    private IReadOnlyList<IModule> LoadKits()
    {
        if (Game is not { Kits.Count: > 0 } game) return Array.Empty<IModule>();
        var folders = new List<string>();
        if (!string.IsNullOrEmpty(game.Assembly)) folders.Add(Path.GetDirectoryName(game.AssemblyPath)!);
        folders.AddRange(game.ModuleAssemblies.Select(path => Path.GetDirectoryName(path)!));
        folders.Add(AppContext.BaseDirectory);
        return game.Kits.SelectMany(kit => ModuleManager.LoadKit(kit, folders, _options.LoadKitClients)).ToList();
    }

    internal static bool KindBelongs(ModuleKind kind, HostKind host) => kind switch
    {
        ModuleKind.Editor => host == HostKind.Editor,
        ModuleKind.Tool => host == HostKind.Tool,
        _ => true,
    };

    // The plugins a game.json asks for, plus everything they require (by type or by id), in
    // AvailablePlugins' order. Null means all of them. A pattern that matches nothing is an error that
    // lists what there is — a misspelt plugin would otherwise be a game quietly missing a system.
    private static IEnumerable<IModule> ChoosePlugins(IReadOnlyList<IModule> all, IReadOnlyList<string>? wanted, IReadOnlyList<IModule> kits)
    {
        if (wanted == null || all.Count == 0) return all;   // a bare engine has none to choose from

        var infos = all.ToDictionary(m => m, PluginInfo.Of);
        var chosen = new HashSet<IModule>();
        // What the kits need (by type or by id), as if game.json had named it.
        foreach (var kit in kits)
        {
            var kitInfo = PluginInfo.Of(kit);
            foreach (var need in kit.Dependencies.Select(t => all.FirstOrDefault(m => m.GetType() == t))
                         .Concat(kitInfo.Requires.Select(r => all.FirstOrDefault(m => infos[m].Id.Equals(r.Id, StringComparison.OrdinalIgnoreCase)))))
                if (need != null && chosen.Add(need))
                    Log.Info(LogCat.Modules, $"{infos[need].Id} added: kit {kitInfo.Id} requires it");
        }
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
        using var _ = Environment.Enter();
        Advance(AppStage.Created, AppStage.Registered);
        Engine.Modules.InitAll();
        Engine.Modules.RegisterCommands(CVars);
        VirtualFileSystem.RegisterCommands(CVars, Engine.Vfs);
        Engine.Records.RegisterCommands(CVars);
        Engine.Saves.RegisterCommands(CVars);
        Engine.Demos.RegisterCommands(CVars);   // record, stop, playdemo, demos, world_hash (#333)
        Engine.Scenes.RegisterCommands(CVars);   // scene_load (issue #29)
        Engine.Rebinds.RegisterCommands(CVars);   // bind, unbind, bind_reset (#328)
        Engine.ModManager.RegisterCommands(CVars);   // mod_list, mod_order, mod_enable/disable/move (4j-3)
        // Entity and scale commands work on any world, so every host has them, not only the one with
        // a window: a server's console and a test can spawn and list entities too.
        WorldConsoleCommands.Register(CVars, Engine);
        ScaleConsoleCommands.Register(CVars, Engine);   // scale_spawn / scale_report (R18)
    }

    // config.cfg, once every cvar and command a host will register exists.
    public void Configure()
    {
        using var _ = Environment.Enter();
        Advance(AppStage.Registered, AppStage.Configured);
        CVars.CVarSeal.Seal("config.cfg was read");
        if (_options.ConfigFile is { } config && File.Exists(config))
            CVars.ExecFile(config, ExecSource.Config);
        if (_options.AutoexecFile is { } autoexec && File.Exists(autoexec))
            CVars.ExecFile(autoexec, ExecSource.Config);
    }

    public void LoadContent()
    {
        using var _ = Environment.Enter();
        Advance(AppStage.Configured, AppStage.ContentLoaded);
        Engine.Actions.Seal.Seal("content was loaded");
        Engine.Vocabularies.Seal("content was loaded");   // records name their entries (issue #28)
        // `user://input.json` last of all (#328), whatever a host mounted since Create: the player's
        // rebinds patch the game's and the mods' maps.
        if (_options.InputFile is { } inputFile) Engine.Vfs.Mount(new UserInputMount(inputFile));
        Engine.Records.Load(Engine.Vfs);   // seals record types
        WriteModReport();
        ChooseStartScene();
        ChooseTitle();
    }

    // `user://logs/mod_report.txt` (4j-3). A report that can't be written is a warning: the game still runs.
    private void WriteModReport()
    {
        if (_options.ModReportFile is not { } path) return;
        try { Engine.ModManager.WriteBootReport(path, Engine.Records, Engine.Vfs); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogCat.Mods, $"Couldn't write {path}: {ex.Message}");
        }
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

    // game.json's `"title"` (issue #342), which must name a `screen` record. Worlds wait at it only when
    // ShowTitle says so; a headless app still checks it, so a test finds the typo the player would.
    private void ChooseTitle()
    {
        string? text = _options.Title ?? Game?.Title;
        if (string.IsNullOrWhiteSpace(text)) return;
        RecordId id;
        try { id = RecordId.Parse(text, Game?.Id ?? "sage"); }
        catch (FormatException ex) { throw new InvalidDataException($"game.json's \"title\" '{text}': {ex.Message}", ex); }
        if (!Engine.Records.Exists("screen", id))
            throw new InvalidDataException($"game.json's \"title\" names '{id}', which is not a screen record (a title screen needs sage.ui and a `screen` of that id)");
        if (!_options.ShowTitle) return;
        Engine.Scenes.Title = id;
        Log.Info(LogCat.World, $"Title screen: {id}");
    }

    public void Start()
    {
        using var _ = Environment.Enter();
        Advance(AppStage.ContentLoaded, AppStage.Started);
        Engine.Modules.StartAll();
    }

    // Worlds can be created from Start on; the first one moves the app to Running.
    public World CreateWorld(string name)
    {
        using var _ = Environment.Enter();
        if (Stage is not (AppStage.Started or AppStage.Running))
            throw new InvalidOperationException($"CreateWorld('{name}') needs a started app; it is {Stage}. Call Start first.");
        Stage = AppStage.Running;
        return Engine.CreateWorld(name);
    }

    // The editor's world (phase 10a, issue #219): `Engine.CreateEditWorld` at the same stage a world is
    // made. A dev host's `-edit` makes this one instead of the main world.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor (phase 10a)
    public World CreateEditWorld(string name, RecordId scene = default)
    {
        using var _ = Environment.Enter();
        if (Stage is not (AppStage.Started or AppStage.Running))
            throw new InvalidOperationException($"CreateEditWorld('{name}') needs a started app; it is {Stage}. Call Start first.");
        Stage = AppStage.Running;
        return Engine.CreateEditWorld(name, scene);
    }

    // `+command` launch arguments, in order. Once, with a world up (like Source's +map).
    public void RunLaunchCommands()
    {
        using var _ = Environment.Enter();
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
    // Last, the environment Create made current is no longer, and an app's own log is flushed and closed
    // (the process's, the host's, stays open).
    public void Dispose()
    {
        if (Stage == AppStage.Shutdown) return;
        bool registered = Stage != AppStage.Created;
        Stage = AppStage.Shutdown;
        using (Environment.Enter())
        {
            Engine.Dispose();
            Engine.Modules.ShutdownAll();
            Engine.ModManager.UnloadCode();   // code mods' load contexts (phase 9), once their modules shut down
            if (registered && _options.ConfigFile is { } config)
            {
                try { CVars.SaveArchived(config); }
                catch (Exception ex) { Log.Warn(LogCat.Host, $"Couldn't save {config}: {ex.Message}"); }
            }
        }
        _created.Dispose();
        Environment.Dispose();
    }
}
