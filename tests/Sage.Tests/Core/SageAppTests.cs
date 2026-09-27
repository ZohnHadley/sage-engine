#nullable enable
using System;
using System.IO;
using System.Linq;
using Friflo.Engine.ECS;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// One boot path (REDESIGN §3.2, issue #10). The host, a headless server and these tests all walk a
// SageApp through the same stages; what these tests check is that the stages keep their order, that the
// loop moves every world, and that a real game boots headlessly through the path the executable uses.
public class SageAppTests
{
    public SageAppTests() { _ = TestEnv.UserRoot; }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "Sage.sln"))) dir = Path.GetDirectoryName(dir)!;
        return dir;
    }

    // A bare engine: no game, no simulation modules. Everything a test world needs is still there,
    // because World and Engine install it; physics and gameplay are modules on top.
    private static SageApp Bare() => SageApp.Create(new SageAppOptions { IncludeSimulationModules = false });

    [Fact]
    public void TheStagesRunInOrderAndSayWhenTheyDoNot()
    {
        using var app = Bare();
        Assert.Equal(AppStage.Created, app.Stage);

        var early = Assert.Throws<InvalidOperationException>(app.LoadContent);
        Assert.Contains("ContentLoaded has to follow Configured", early.Message);
        Assert.Throws<InvalidOperationException>(() => app.CreateWorld("too soon"));

        app.Register();
        Assert.Throws<InvalidOperationException>(app.Register);   // twice is also out of order
        app.Configure();
        app.LoadContent();
        app.Start();
        Assert.Equal(AppStage.Started, app.Stage);

        app.CreateWorld("main");
        Assert.Equal(AppStage.Running, app.Stage);
        app.CreateWorld("second");   // more worlds are fine once running
        Assert.Equal(2, app.Engine.Worlds.Count);
    }

    [Fact]
    public void ABareEngineBootsWithNoGameAndNoGameplay()
    {
        using var app = Bare();
        var world = app.Boot();

        Assert.Empty(app.Engine.Modules.Modules);
        Assert.True(world.Resources.TryGet<GameRules>(out var rules) && rules is DefaultGameRules);
        world.RunFixed(1f / 60f);
        Assert.Equal(1L, world.Tick);
    }

    [Fact]
    public void TheLoopTicksEveryWorldNotJustTheFirst()
    {
        using var app = Bare();
        var first = app.Boot("first");
        var second = app.CreateWorld("second");
        var loop = new HostLoop(app.Engine);

        int commanded = 0;
        var step = loop.Update(realDt: 0.5, tickRate: 60, maxFrameTime: 1.0, timeScale: 1.0,
                               beforeTick: w => { if (w == first) commanded++; });

        Assert.Equal(30, step.Ticks);
        Assert.Equal(30L, first.Tick);
        Assert.Equal(30L, second.Tick);   // the bug this replaces: a second world existed and never moved
        Assert.Equal(30, commanded);     // the host's hook ran once per tick for the world it cares about

        loop.Frame();                    // and every world gets its frame
    }

    [Fact]
    public void AWorldCreatedDuringATickStartsOnTheNextOne()
    {
        using var app = Bare();
        var first = app.Boot("first");
        var loop = new HostLoop(app.Engine);

        World? late = null;
        loop.Update(2.0 / 60.0 + 1e-6, 60, 1.0, 1.0, w =>
        {
            if (w == first && late == null) late = app.CreateWorld("late");
        });

        Assert.Equal(2L, first.Tick);
        Assert.NotNull(late);
        Assert.Equal(1L, late!.Tick);   // created before tick 1 ran, so it only had tick 2
    }

    [Fact]
    public void ADisabledModuleThatDoesNotExistIsReported()
    {
        // Hello's own assembly, so the manifest is valid; the point is the `disable` list.
        string hello = Path.Combine(RepoRoot(), "games", "Hello").Replace('\\', '/');
        string dir = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "game.json"), $$"""
            { "name": "Typo", "id": "typo", "assembly": "{{hello}}/bin/{config}/net8.0/Hello.dll", "mounts": [],
              "modules": { "disable": ["LightModule", "AIModule"] } }
            """);
        var manifest = GameManifest.Load(dir);

        using var sink = new CaptureSink();
        using var app = SageApp.Create(new SageAppOptions { Game = manifest });

        Assert.DoesNotContain(app.Engine.Modules.Modules, m => m.Name == "AIModule");   // the real one worked
        Assert.Contains(sink.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("'LightModule'")
                                           && e.Message.Contains("LightsModule"));        // and the typo said so
    }

    // The one list the host and every headless app install. If this and GameplayModules.All() ever
    // disagree, the shipped game and the tests are running different engines again (ModuleSetTests).
    [Fact]
    public void TheSimulationModulesAreTheEngineOnesAndEveryGameplayModule()
    {
        var expected = new[] { typeof(PhysicsModule), typeof(StreamingModule), typeof(MapModule) }
            .Concat(GameplayModules.All().Select(m => m.GetType()));
        Assert.Equal(expected, SageApp.SimulationModules().Select(m => m.GetType()));
    }

    // The smallest real game, from its real game.json and content folder, through the same path the
    // executable takes — minus the client module, which is what makes it headless.
    [Fact]
    public void HelloBootsHeadlesslyFromItsOwnManifest()
    {
        string repo = RepoRoot();
        using var app = SageApp.Create(new SageAppOptions
        {
            Game = GameManifest.Load(Path.Combine(repo, "games", "Hello")),
            EngineContentDirectory = Path.Combine(repo, "engine_content"),
        });
        var world = app.Boot();

        Assert.Contains(app.Engine.Modules.Modules, m => m.Name == "HelloModule");
        Assert.Equal(SageApp.SimulationModules().Length + 1, app.Engine.Modules.Modules.Count);

        var players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList();
        Assert.Single(players);

        var loop = new HostLoop(app.Engine);
        loop.Update(1.0 + 1e-6, 60, 2.0, 1.0);   // a hair over (sixty 1/60 s steps sum to just under 1.0), under the frame cap
        Assert.Equal(60L, world.Tick);
    }
}
