#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Modules and game.json (docs/design/01 §3.1, §3.3, §4).
public class ModuleTests
{
    public ModuleTests() { _ = TestEnv.UserRoot; }

    public sealed class ServiceA { }
    public sealed class ServiceB { }

    private sealed class ModA : IModule
    {
        private readonly List<string> _calls;
        public ModA(List<string> calls) { _calls = calls; }
        public void Init(ModuleContext ctx) { _calls.Add("A.Init"); ctx.Provide(new ServiceA()); }
        public void Start(ModuleContext ctx) => _calls.Add("A.Start");
        public void OnWorldCreated(World world) => _calls.Add($"A.World({world.Name})");
        public void Shutdown() => _calls.Add("A.Shutdown");
    }

    private sealed class ModB : IModule
    {
        private readonly List<string> _calls;
        public ModB(List<string> calls) { _calls = calls; }
        public IReadOnlyList<Type> Dependencies => new[] { typeof(ModA) };
        public ServiceA? Got;
        public void Init(ModuleContext ctx) { _calls.Add("B.Init"); ctx.Provide(new ServiceB()); }
        public void Start(ModuleContext ctx) { _calls.Add("B.Start"); Got = ctx.Get<ServiceA>(); }
        public void Shutdown() => _calls.Add("B.Shutdown");
    }

    // Uses ServiceB without declaring ModB.
    private sealed class ModC : IModule
    {
        public IReadOnlyList<Type> Dependencies => new[] { typeof(ModA) };
        public string? Host;
        public void Init(ModuleContext ctx) { }
        public void Start(ModuleContext ctx) { Host = ctx.Get<string>(); ctx.Get<ServiceB>(); }
    }

    private sealed class Cycle1 : IModule
    {
        public IReadOnlyList<Type> Dependencies => new[] { typeof(Cycle2) };
        public void Init(ModuleContext ctx) { }
    }

    private sealed class Cycle2 : IModule
    {
        public IReadOnlyList<Type> Dependencies => new[] { typeof(Cycle1) };
        public void Init(ModuleContext ctx) { }
    }

    // A game with an overworld and a battle scene has two worlds (03 §3.3, ARCHITECTURE §4.2), and
    // modules install their systems in each. Anything a system registers per world — a cvar, a
    // command — would be registered twice, and the registry rightly refuses that.
    [Fact]
    public void ModulesCanInstallTheirSystemsInMoreThanOneWorld()
    {
        using var app = HeadlessApp.Gameplay().Build();

        var overworld = app.CreateWorld("overworld");
        var battle = app.CreateWorld("battle");

        Assert.NotSame(overworld, battle);
        Assert.Equal(2, app.Engine.Worlds.Count);
    }

    // 16 §3.4 says a game adds its own AI tasks before the first world exists. That is only true if
    // the registry is reachable, which is what AIModule.Provide buys: a module declaring AIModule as
    // a dependency can ask for it. Without this test the Provide would be an untested promise.
    [Fact]
    public void AGameCanReachTheAITaskRegistry()
    {
        var game = new TaskAddingGame();
        using var app = HeadlessApp.Gameplay().With(game).Build();

        Assert.NotNull(game.Tasks);
        Assert.NotNull(game.Tasks!.Find("Loiter"));      // the game's own
        Assert.NotNull(game.Tasks.Find("MeleeAttack"));  // and the engine's, in the same registry
    }

    private sealed class TaskAddingGame : IGameModule
    {
        public AITaskRegistry? Tasks;

        public IReadOnlyList<Type> Dependencies => new[] { typeof(AIModule) };

        public void Init(ModuleContext ctx) { }

        public void Start(ModuleContext ctx)
        {
            Tasks = ctx.Get<AITaskRegistry>();
            Tasks.Register("Loiter", new LoiterTask());
        }
    }

    private sealed class LoiterTask : IAITask
    {
        public AITaskStatus Run(ref AITaskContext context) => AITaskStatus.Succeeded;
    }

    [Fact]
    public void Lifecycle_RunsInDependencyOrder_ShutdownReversed()
    {
        var calls = new List<string>();
        var b = new ModB(calls);
        var app = HeadlessApp.Bare().With(b, new ModA(calls)).Boot("w");   // B before its dependency: sorted anyway
        var order = app.Engine.Modules.Modules.Select(m => m.Name).ToArray();
        app.Dispose();

        Assert.Equal(new[] { "A.Init", "B.Init", "A.Start", "B.Start", "A.World(w)", "B.Shutdown", "A.Shutdown" }, calls);
        Assert.NotNull(b.Got);
        Assert.Equal(new[] { "ModA", "ModB" }, order);
    }

    [Fact]
    public void Get_IsRestrictedToDependencies_AndHostServices()
    {
        var calls = new List<string>();
        var c = new ModC();
        using var app = HeadlessApp.Bare().With(new ModA(calls), new ModB(calls), c).Create();
        app.Engine.Modules.ProvideHostService("host service");
        app.App.Register();
        app.App.Configure();
        app.App.LoadContent();

        var ex = Assert.Throws<InvalidOperationException>(app.App.Start);
        Assert.Equal("host service", c.Host);   // host services need no dependency
        Assert.Contains("ModC uses ServiceB from ModB but doesn't list it", ex.Message);
    }

    [Fact]
    public void Provide_AnAlreadyProvidedService_Throws()
    {
        using var app = HeadlessApp.Bare().With(new ModA(new List<string>())).Create();
        app.Engine.Modules.ProvideHostService(new ServiceA());
        Assert.Throws<InvalidOperationException>(app.App.Register);
    }

    [Fact]
    public void MissingDependency_AndCycles_AreFatal()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => ModuleManager.Sort(new IModule[] { new ModB(new List<string>()) }));
        Assert.Contains("depends on ModA", missing.Message);
        var cycle = Assert.Throws<InvalidOperationException>(() => ModuleManager.Sort(new IModule[] { new Cycle1(), new Cycle2() }));
        Assert.Contains("Cycle1 -> Cycle2 -> Cycle1", cycle.Message);
    }

    [Fact]
    public void Add_SameModuleTypeTwice_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HeadlessApp.Bare().With(new ModA(new List<string>()), new ModA(new List<string>())).Create());
    }

    [Fact]
    public void LoadGame_MissingAssembly_SaysToBuild()
    {
        var ex = Assert.Throws<FileNotFoundException>(() => ModuleManager.LoadGame(Path.Combine(TestEnv.NewTempDir(), "Nope.dll")));
        Assert.Contains("Build the solution", ex.Message);
    }

    [Fact]
    public void GameManifest_Loads_AndSubstitutesTheConfig()
    {
        string dir = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "game.json"), """
            {
              // comment
              "name": "Test", "id": "test_game",
              "assembly": "bin/{config}/net8.0/Test.dll",
              "mounts": ["content", "extra"],
              "modules": { "disable": ["ClientModule"] },
            }
            """);
        var m = GameManifest.Load(dir);

        Assert.Equal("test_game", m.Id);
        Assert.Equal(new[] { "content", "extra" }, m.Mounts);
        Assert.Equal(new[] { "ClientModule" }, m.Modules.Disable);
        Assert.Equal("mods", m.ModsDirectory);
        Assert.Equal(Path.GetFullPath(dir), m.Directory);
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "bin", BuildInfo.ConfigurationName, "net8.0", "Test.dll"), m.AssemblyPath);
    }

    // Without -game no build falls back to a game any more (issue #32): it is an error that says what to
    // pass and, inside this repository, which games there are. A packaged game's ./game still works.
    [Fact]
    public void GameManifest_Locate_WithoutAGameIsAnErrorThatListsTheGames()
    {
        string inRepo = Path.Combine(TestEnv.FolderAbove("Sage.sln"), "src", "Sage.Host", "bin");
        var ex = Assert.Throws<FileNotFoundException>(() => GameManifest.Locate(null, inRepo));
        Assert.Contains("pass -game <folder>", ex.Message);
        Assert.Contains("games/Hello", ex.Message);
        Assert.Contains("games/Sandbox", ex.Message);
        Assert.Contains("tests/games/scene-only", ex.Message);

        string exe = TestEnv.NewTempDir();
        Assert.Throws<FileNotFoundException>(() => GameManifest.Locate("", exe));
        Directory.CreateDirectory(Path.Combine(exe, "game"));
        File.WriteAllText(Path.Combine(exe, "game", "game.json"), """{ "id": "packaged" }""");
        Assert.Equal(Path.GetFullPath(Path.Combine(exe, "game")), GameManifest.Locate(null, exe));
        Assert.Equal(Path.GetFullPath("elsewhere"), GameManifest.Locate("elsewhere", exe));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{ "name": "x" }""")]
    [InlineData("""{ "id": "Bad Id", "assembly": "x.dll" }""")]
    [InlineData("{ nope")]
    public void GameManifest_Invalid_Throws(string? json)
    {
        string dir = TestEnv.NewTempDir();
        if (json != null) File.WriteAllText(Path.Combine(dir, "game.json"), json);
        var ex = Record.Exception(() => GameManifest.Load(dir));
        Assert.True(ex is FileNotFoundException or InvalidDataException or FormatException, ex?.GetType().Name ?? "no exception");
    }

    [Fact]
    public void SandboxManifest_IsValid()
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "Sage.sln"))) repo = Path.GetDirectoryName(repo)!;
        var m = GameManifest.Load(Path.Combine(repo, "games", "Sandbox"));
        Assert.Equal("sandbox", m.Id);
        Assert.Contains("content", m.Mounts);
    }

    // The tests reference Hello and Sandbox, so both were just built in the configuration the tests
    // were — and `{config}` has to name the folder that build wrote, whatever the configuration was
    // called. `-c Release` (what `dotnet publish` uses) once resolved to bin/Shipping, where nothing is.
    [Theory]
    [InlineData("Hello")]
    [InlineData("Sandbox")]
    public void ShippedManifest_PointsAtTheAssemblyThisBuildWrote(string game)
    {
        string repo = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(repo, "Sage.sln"))) repo = Path.GetDirectoryName(repo)!;
        var m = GameManifest.Load(Path.Combine(repo, "games", game));
        Assert.True(File.Exists(m.AssemblyPath), $"{game}: {m.AssemblyPath} does not exist");
    }
}
