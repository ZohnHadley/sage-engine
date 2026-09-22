#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using sage_engine;

namespace sage_engine.Tests;

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

    private static Engine NewEngine()
    {
        var cvars = new CVarRegistry();
        return new Engine(cvars, CoreCVars.Register(cvars));
    }

    [Fact]
    public void Lifecycle_RunsInDependencyOrder_ShutdownReversed()
    {
        var calls = new List<string>();
        using var engine = NewEngine();
        var b = new ModB(calls);
        engine.Modules.Add(b);            // added before its dependency: sorted anyway
        engine.Modules.Add(new ModA(calls));
        engine.Modules.InitAll();
        engine.Modules.StartAll();
        engine.CreateWorld("w");
        engine.Modules.ShutdownAll();

        Assert.Equal(new[] { "A.Init", "B.Init", "A.Start", "B.Start", "A.World(w)", "B.Shutdown", "A.Shutdown" }, calls);
        Assert.NotNull(b.Got);
        Assert.Equal(new[] { "ModA", "ModB" }, engine.Modules.Modules.Select(m => m.Name));
    }

    [Fact]
    public void Get_IsRestrictedToDependencies_AndHostServices()
    {
        var calls = new List<string>();
        using var engine = NewEngine();
        var c = new ModC();
        engine.Modules.Add(new ModA(calls));
        engine.Modules.Add(new ModB(calls));
        engine.Modules.Add(c);
        engine.Modules.ProvideHostService("host service");
        engine.Modules.InitAll();

        var ex = Assert.Throws<InvalidOperationException>(() => engine.Modules.StartAll());
        Assert.Equal("host service", c.Host);   // host services need no dependency
        Assert.Contains("ModC uses ServiceB from ModB but doesn't list it", ex.Message);
    }

    [Fact]
    public void Provide_AnAlreadyProvidedService_Throws()
    {
        using var engine = NewEngine();
        engine.Modules.ProvideHostService(new ServiceA());
        engine.Modules.Add(new ModA(new List<string>()));
        Assert.Throws<InvalidOperationException>(() => engine.Modules.InitAll());
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
        using var engine = NewEngine();
        engine.Modules.Add(new ModA(new List<string>()));
        Assert.Throws<InvalidOperationException>(() => engine.Modules.Add(new ModA(new List<string>())));
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
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "bin", BuildInfo.Config.ToString(), "net8.0", "Test.dll"), m.AssemblyPath);
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
}
