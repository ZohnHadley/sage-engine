#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Testing;

// A headless app for a test (docs/REDESIGN.md §3.2, issue #14): a SageApp with no client, booted
// through the same stages the game executable walks, so a test runs the configuration that ships
// instead of a hand-rolled one. "A check that runs against a different configuration from the one
// that ships is not a check of the thing that ships" (ModuleSetTests).
//
//   using var app = HeadlessApp.Gameplay()
//       .File("data/combat.json", records)     // content, mounted under `sage`
//       .With(new MyRulesModule())
//       .Boot("combat");                       // Register → Configure → LoadContent → Start → a world
//   app.World.RunFixed(1f / 60f);
//
// Start from:
//   Bare()        no modules at all; add what the test is about with With
//   Gameplay()    physics and every gameplay feature: what most gameplay tests want
//   Simulation()  every simulation plugin the engine ships (BasePlugins.All)
//   ForGame(...)  a game's game.json: its plugins, mounts and disables, as the host resolves them
public sealed class HeadlessApp : IDisposable
{
    private World? _world;

    internal HeadlessApp(SageApp app) { App = app; }

    public static HeadlessAppBuilder Bare() => new(includeSimulation: false, game: null);

    public static HeadlessAppBuilder Gameplay() => Bare().With(new PhysicsModule()).WithGameplay();

    public static HeadlessAppBuilder Simulation() => new(includeSimulation: true, game: null);

    // A game from its folder. `game` is the game's module when the test has it in-process (it references
    // the game's project): the manifest's `assembly` is then not loaded from disk, so the test and the
    // game share one copy of its types. The manifest's `modules.add` — the game's client half — is left
    // out, as a dedicated server would: nothing headless can load a window.
    public static HeadlessAppBuilder ForGame(string gameDirectory, IGameModule? game = null)
    {
        var manifest = GameManifest.Load(gameDirectory);
        manifest.Modules.Add.Clear();
        var builder = new HeadlessAppBuilder(includeSimulation: true, manifest);
        if (game != null)
        {
            manifest.Assembly = "";
            builder.With(game);
        }
        return builder;
    }

    public SageApp App { get; }
    public Engine Engine => App.Engine;
    public CVarRegistry CVars => App.CVars;
    public RecordStore Records => Engine.Records;
    public VirtualFileSystem Vfs => Engine.Vfs;

    // The world Boot made.
    public World World => _world ?? throw new InvalidOperationException("This app has no world yet: Boot makes one, or call CreateWorld.");

    // The plugin ids this app loaded, in initialisation order once registered.
    public IReadOnlyList<string> PluginIds => Engine.Modules.Modules.Select(m => Engine.Modules.Plugin(m).Id).ToArray();

    public World CreateWorld(string name)
    {
        var world = App.CreateWorld(name);
        _world ??= world;
        return world;
    }

    public void Dispose() => App.Dispose();
}

public sealed class HeadlessAppBuilder
{
    private readonly bool _includeSimulation;
    private readonly GameManifest? _game;
    private readonly List<IModule> _modules = new();
    private readonly List<MountFixture> _fixtures = new();
    private readonly List<Action<HeadlessApp>> _registered = new();
    private MountFixture? _files;
    private string? _engineContent;
    private HostKind _host = HostKind.Game;

    internal HeadlessAppBuilder(bool includeSimulation, GameManifest? game)
    {
        _ = TestEnv.UserRoot;   // saves, logs and config have somewhere to go
        _includeSimulation = includeSimulation;
        _game = game;
    }

    // Modules added after the simulation ones and before the game's, the way a host adds its own.
    public HeadlessAppBuilder With(params IModule[] modules)
    {
        _modules.AddRange(modules);
        return this;
    }

    // Every gameplay plugin, from the one list the host installs too (GameplayModules.All).
    public HeadlessAppBuilder WithGameplay() => With(GameplayModules.All());

    // A content file written for this test, in a mount with record namespace `ns`. Mounted after
    // engine content and the game's own mounts, so it can patch either.
    public HeadlessAppBuilder File(string path, string text, string ns = "sage")
    {
        _files ??= new MountFixture();
        string mount = ns == "sage" ? "engine" : ns;
        _files.Write(mount, path, text);
        if (!_files.Mounts.Any(m => m.Name == mount)) _files.Mount(mount, ns);
        return this;
    }

    // The folders a MountFixture mounted, in its order, after everything else.
    public HeadlessAppBuilder Mount(MountFixture fixture)
    {
        _fixtures.Add(fixture);
        return this;
    }

    // The repository's engine_content, mounted first under `sage` as the host mounts its Content/.
    public HeadlessAppBuilder WithEngineContent()
    {
        _engineContent = Path.Combine(TestEnv.FolderAbove("engine_content"), "engine_content");
        return this;
    }

    public HeadlessAppBuilder InHost(HostKind host)
    {
        _host = host;
        return this;
    }

    // Runs once every module's Init has, before config and content: where a test registers a record
    // type, a cvar or an input of its own, as a module would in Init. Later than this is sealed.
    public HeadlessAppBuilder OnRegistered(Action<HeadlessApp> register)
    {
        _registered.Add(register);
        return this;
    }

    // The app with its modules added and its content mounted, and nothing run: for a test of the
    // stages themselves, which walks App through them.
    public HeadlessApp Create()
    {
        var app = SageApp.Create(new SageAppOptions
        {
            Game = _game,
            EngineContentDirectory = _engineContent,
            AvailablePlugins = _includeSimulation ? BasePlugins.All() : Array.Empty<IModule>(),
            Host = _host,
            HostModules = _modules.ToArray(),
        });
        foreach (var fixture in _fixtures.Append(_files).OfType<MountFixture>())
            foreach (var mount in fixture.Mounts)
                app.Engine.Vfs.Mount(mount);
        return new HeadlessApp(app);
    }

    // Registered, configured, content loaded and started: ready for worlds.
    public HeadlessApp Build()
    {
        var headless = Create();
        try
        {
            headless.App.Register();
            foreach (var register in _registered) register(headless);
            headless.App.Configure();
            headless.App.LoadContent();
            headless.App.Start();
        }
        catch
        {
            headless.Dispose();
            throw;
        }
        return headless;
    }

    // Build, then a first world (HeadlessApp.World) and the launch commands, as SageApp.Boot does.
    public HeadlessApp Boot(string worldName = "main")
    {
        var headless = Build();
        try
        {
            headless.CreateWorld(worldName);
            headless.App.RunLaunchCommands();
        }
        catch
        {
            headless.Dispose();
            throw;
        }
        return headless;
    }
}
