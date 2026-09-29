#nullable enable
using System;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// World resources say what they mean (issue #13): Add installs and refuses a second, Replace swaps on
// purpose and disposes what it replaced. And a game's rules come from IGameModule.CreateRules, not from a
// resource that had to be installed at exactly the right moment.
public class WorldResourceTests
{
    public WorldResourceTests() { _ = TestEnv.UserRoot; }

    private sealed class Thing : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void InstallingTheSameResourceTwiceIsAnError()
    {
        var resources = new WorldResources();
        resources.Add(new Thing());
        var ex = Assert.Throws<InvalidOperationException>(() => resources.Add(new Thing()));
        Assert.Contains("Thing is already installed", ex.Message);
        Assert.Contains("Replace", ex.Message);
    }

    [Fact]
    public void ReplacingDisposesWhatItReplaced()
    {
        var resources = new WorldResources();
        var first = new Thing();
        var second = new Thing();
        resources.Add(first);

        resources.Replace(second);
        Assert.True(first.Disposed);
        Assert.Same(second, resources.Get<Thing>());

        resources.Replace(second);   // the same object again changes nothing
        Assert.False(second.Disposed);
        Assert.Same(second, resources.Get<Thing>());
    }

    // ---- GameRules --------------------------------------------------------------------------------

    private sealed class RecordingRules : GameRules
    {
        public int Started;
        public override void OnWorldStarted(World world) => Started++;
    }

    private sealed class RulesGame : IGameModule
    {
        public RecordingRules? Made;
        public void Init(ModuleContext ctx) { }
        public GameRules CreateRules(World world) => Made = new RecordingRules();
    }

    private sealed class OldStyleGame : IGameModule
    {
        public readonly RecordingRules Rules = new();
        public void Init(ModuleContext ctx) { }
        public void OnWorldCreated(World world) => world.Resources.Replace<GameRules>(Rules);
    }

    private static SageApp AppWith(IModule game) =>
        SageApp.Create(new SageAppOptions { HostModules = new[] { game } });

    [Fact]
    public void AGamesRulesComeFromCreateRulesAndAreStarted()
    {
        var game = new RulesGame();
        using var app = AppWith(game);
        var world = app.Boot();

        Assert.Same(game.Made, world.Resources.Get<GameRules>());
        Assert.Equal(1, game.Made!.Started);
    }

    [Fact]
    public void RulesInstalledTheOldWayStillStart()
    {
        var game = new OldStyleGame();
        using var app = AppWith(game);
        var world = app.Boot();

        Assert.Same(game.Rules, world.Resources.Get<GameRules>());
        Assert.Equal(1, game.Rules.Started);
    }

    [Fact]
    public void WithNoRulesAWorldGetsTheDefaults()
    {
        using var app = SageApp.Create(new SageAppOptions());
        Assert.IsType<DefaultGameRules>(app.Boot().Resources.Get<GameRules>());
    }
}
