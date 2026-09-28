#nullable enable
using System;
using System.IO;
using System.Linq;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Plugins (REDESIGN §3.3, issue #12): a module's id and version, dependencies by id with a version
// range, who registered what, which host a module belongs in, and which plugins a game chooses.
public class PluginTests
{
    public PluginTests() { _ = TestEnv.UserRoot; }

    // ---- Versions ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("*", "0.0.1", true)]
    [InlineData("0.1.0", "0.1.0", true)]
    [InlineData("0.1.0", "0.1.1", false)]
    [InlineData(">=0.1", "0.1.0", true)]
    [InlineData(">=0.1 <0.2", "0.1.9", true)]
    [InlineData(">=0.1, <0.2", "0.2.0", false)]
    [InlineData("^0.1.0", "0.1.7", true)]
    [InlineData("^0.1.0", "0.2.0", false)]   // for 0.x, ^ keeps the minor
    [InlineData("^1.2.0", "1.9.0", true)]
    [InlineData("^1.2.0", "2.0.0", false)]
    [InlineData("~1.2", "1.2.9", true)]
    [InlineData("~1.2", "1.3.0", false)]
    [InlineData(">0.1", "0.1.0", false)]
    [InlineData("<=0.1", "0.1.0-preview", true)]   // a pre-release suffix is accepted and ignored
    public void VersionRangesMeanWhatNpmUsersExpect(string range, string version, bool contains) =>
        Assert.Equal(contains, VersionRange.Parse(range, "test").Contains(SemVersion.Parse(version, "test")));

    [Fact]
    public void ABadVersionSaysWhereItIs()
    {
        var ex = Assert.Throws<FormatException>(() => SemVersion.Parse("one.two", "BadModule's [Plugin]"));
        Assert.Contains("BadModule's [Plugin]", ex.Message);
    }

    // ---- Dependencies by id ------------------------------------------------------------------------

    [Plugin("test.base", "0.2.0")]
    private sealed class BaseModule : IModule { public void Init(ModuleContext ctx) { } }

    [Plugin("test.needs.base", "1.0.0")]
    [RequiresPlugin("test.base", ">=0.2 <0.3")]
    private sealed class NeedsBaseModule : IModule { public void Init(ModuleContext ctx) { } }

    [Plugin("test.needs.newer", "1.0.0")]
    [RequiresPlugin("test.base", ">=0.3")]
    private sealed class NeedsNewerModule : IModule { public void Init(ModuleContext ctx) { } }

    [Plugin("test.base", "9.9.9")]
    private sealed class ImpostorModule : IModule { public void Init(ModuleContext ctx) { } }

    private static SageApp BareWith(params IModule[] modules) =>
        SageApp.Create(new SageAppOptions { IncludeSimulationModules = false, HostModules = modules });

    [Fact]
    public void ADependencyByIdIsInitialisedFirst()
    {
        // Added in the "wrong" order: the id dependency has to reorder them.
        using var app = BareWith(new NeedsBaseModule(), new BaseModule());
        app.Register();
        Assert.Equal(new[] { "test.base", "test.needs.base" },
                     app.Engine.Modules.Modules.Select(m => app.Engine.Modules.Plugin(m).Id));
    }

    [Fact]
    public void AMissingOrTooOldDependencySaysWhich()
    {
        using var missing = BareWith(new NeedsBaseModule());
        var none = Assert.Throws<InvalidOperationException>(missing.Register);
        Assert.Contains("test.needs.base requires plugin test.base >=0.2 <0.3, which isn't loaded", none.Message);

        using var old = BareWith(new BaseModule(), new NeedsNewerModule());
        var tooOld = Assert.Throws<InvalidOperationException>(old.Register);
        Assert.Contains("requires test.base >=0.3, but test.base 0.2.0 is loaded", tooOld.Message);
    }

    [Fact]
    public void TwoModulesCannotClaimOneId()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BareWith(new BaseModule(), new ImpostorModule()));
        Assert.Contains("Two modules claim the plugin id 'test.base'", ex.Message);
    }

    [Fact]
    public void AModuleWithoutPluginIsKnownByItsClassName()
    {
        using var app = BareWith(new SageAppTests_LegacyModule());
        Assert.Equal("SageAppTests_LegacyModule", app.Engine.Modules.Plugin(app.Engine.Modules.Modules.Single()).Id);
        Assert.Equal(SemVersion.Zero, app.Engine.Modules.Plugin(app.Engine.Modules.Modules.Single()).Version);
    }

    // ---- Who registered what -------------------------------------------------------------------------

    [Fact]
    public void EveryRegistrationIsRecordedAgainstItsPlugin()
    {
        using var app = SageApp.Create(new SageAppOptions());   // the engine's simulation plugins
        app.Boot();
        var ledger = app.Engine.Registrations;

        Assert.Equal("sage.gameplay.character", ledger.OwnerOf("prefab part", "character"));
        Assert.Equal("sage.gameplay.items", ledger.OwnerOf("record type", "item"));
        Assert.Equal("host", ledger.OwnerOf("command", "plugins"));   // the app's own commands
        Assert.Contains(ledger.By("sage.gameplay.ai"), r => r.Kind == "record type");

        using var sink = new CaptureSink();
        app.CVars.Execute("plugins sage.gameplay.items", ExecSource.Console);
        app.CVars.Execute("plugins", ExecSource.Console);
        Assert.Contains(sink.Entries, e => e.Message.Contains("sage.gameplay.items registered") && e.Message.Contains("record type"));
        Assert.Contains(sink.Entries, e => e.Message.Contains("sage.gameplay.combat") && e.Message.Contains("0.1.0")
                                           && e.Message.Contains("needs sage.gameplay.attributes"));
    }

    // ---- Kinds ---------------------------------------------------------------------------------------

    private sealed class EditorOnlyModule : IModule
    {
        public ModuleKind Kind => ModuleKind.Editor;
        public void Init(ModuleContext ctx) { }
    }

    [Fact]
    public void AnEditorModuleLoadsOnlyInTheEditor()
    {
        using var game = SageApp.Create(new SageAppOptions { IncludeSimulationModules = false, HostModules = new IModule[] { new EditorOnlyModule() } });
        Assert.Empty(game.Engine.Modules.Modules);

        using var editor = SageApp.Create(new SageAppOptions { IncludeSimulationModules = false, Host = HostKind.Editor,
                                                               HostModules = new IModule[] { new EditorOnlyModule() } });
        Assert.Single(editor.Engine.Modules.Modules);
    }

    // ---- Choosing plugins in game.json -----------------------------------------------------------------

    private static GameManifest Manifest(string plugins)
    {
        string dir = TestEnv.NewTempDir();
        File.WriteAllText(Path.Combine(dir, "game.json"), $$"""{ "name": "Chooser", "id": "chooser" {{plugins}} }""");
        return GameManifest.Load(dir);
    }

    private static string[] Ids(SageApp app) =>
        app.Engine.Modules.Modules.Select(m => app.Engine.Modules.Plugin(m).Id).OrderBy(i => i, StringComparer.Ordinal).ToArray();

    [Fact]
    public void AGameGetsThePluginsItNamesAndWhatTheyNeed()
    {
        using var app = SageApp.Create(new SageAppOptions { Game = Manifest(""", "plugins": ["sage.gameplay.items"]""") });
        Assert.Equal(new[] { "sage.gameplay.attributes", "sage.gameplay.character", "sage.gameplay.combat",
                             "sage.gameplay.items", "sage.physics3d" }, Ids(app));
        app.Boot();
    }

    [Fact]
    public void AFamilyIsNamedWithAWildcard()
    {
        using var app = SageApp.Create(new SageAppOptions { Game = Manifest(""", "plugins": ["sage.gameplay.*"]""") });
        var ids = Ids(app);
        Assert.Equal(GameplayModules.All().Length + 1, ids.Length);   // every gameplay plugin, and physics for characters
        Assert.Contains("sage.physics3d", ids);
        Assert.DoesNotContain("sage.streaming", ids);
    }

    // And none of what those plugins furnish (issue #13): no terrain, no player input, no camera. The
    // world still ticks, and the console commands that would reach for them say so instead of crashing.
    [Fact]
    public void NoPluginsAtAllIsAGameWithNoPhysicsAndNoGameplay()
    {
        var game = Manifest(""", "plugins": [], "mounts": ["content"]""");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(game.Directory, "content", "data")).FullName, "crate.json"),
                          """[{ "type": "prefab", "id": "crate", "name": "crate" }]""");
        using var app = SageApp.Create(new SageAppOptions { Game = game });
        Assert.Empty(app.Engine.Modules.Modules);
        var world = app.Boot();

        Assert.False(world.Resources.TryGet<Terrain>(out _));
        Assert.False(world.Resources.TryGet<PlayerInput>(out _));
        Assert.False(world.Resources.TryGet<ActiveCamera>(out _));

        for (int i = 0; i < 60; i++) world.RunFixed(1f / 60f);
        Assert.Equal(60L, world.Tick);
        string[] commands = { "sv_cheats 1", "ent_spawn crate", "ent_spawn crate 1 0 1", "ent_list", "sys_list", "scale_report", "saves", "plugins" };
        using var sink = new CaptureSink();
        foreach (string command in commands)
            app.CVars.Execute(command, ExecSource.Console);
        var names = commands.Select(c => c.Split(' ')[0] + ":").Distinct().ToArray();
        Assert.DoesNotContain(sink.Entries, e => e.Level == LogLevel.Error && names.Any(n => e.Message.StartsWith(n)));
        Assert.Contains(sink.Entries, e => e.Message.Contains("ent_spawn: this world has no camera"));
        Assert.Equal(1, world.EntityCount);   // the one given a position
    }

    [Fact]
    public void AMisspeltPluginSaysWhatThereIs()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SageApp.Create(new SageAppOptions { Game = Manifest(""", "plugins": ["sage.physics"]""") }));
        Assert.Contains("'sage.physics'", ex.Message);
        Assert.Contains("sage.physics3d", ex.Message);
    }

    [Fact]
    public void LeavingPluginsOutMeansAllOfThem()
    {
        using var app = SageApp.Create(new SageAppOptions { Game = Manifest("") });
        Assert.Equal(SageApp.SimulationModules().Length, app.Engine.Modules.Modules.Count);
    }

    [Fact]
    public void APluginCanBeDisabledByItsId()
    {
        using var app = SageApp.Create(new SageAppOptions { Game = Manifest(""", "modules": { "disable": ["sage.streaming"] }""") });
        Assert.DoesNotContain("sage.streaming", Ids(app));
    }
}

// Deliberately without [Plugin]: the shape every module had before plugins had ids.
public sealed class SageAppTests_LegacyModule : IModule { public void Init(ModuleContext ctx) { } }
