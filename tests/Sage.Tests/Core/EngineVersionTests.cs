#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The engine's version and the ranges plugins, kits and games declare against it (issue #31).
// The version comes from git tags (build/Sage.Version.props, MinVer); `sage` in [RequiresPlugin] and
// game.json's "sage" are checked against it when a plugin or a game loads.
public class EngineVersionTests
{
    public EngineVersionTests() { _ = TestEnv.UserRoot; }

    // SemVer 2.0: major.minor.patch, an optional pre-release and optional build metadata.
    private static readonly Regex SemVer = new(
        @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?(\+[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$");

    [Fact]
    public void TheEngineVersionIsASemVerFromGitNotTheDefaultAssemblyVersion()
    {
        Assert.Matches(SemVer, BuildInfo.EngineVersion);
        Assert.NotEqual("1.0.0.0", BuildInfo.EngineVersion);
        Assert.True(BuildInfo.EngineSemVersion >= new SemVersion(0, 1, 0), $"{BuildInfo.EngineVersion}: the first release is 0.1.0");
        Assert.StartsWith(BuildInfo.EngineSemVersion.ToString(), BuildInfo.EngineVersion);

        // Every engine assembly is stamped with the same one.
        foreach (var assembly in EngineAssemblies.Base)
            Assert.Equal(BuildInfo.EngineVersion, assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion);
    }

    [Fact]
    public void TheVersionCommandPrintsIt()
    {
        using var app = SageApp.Create(new SageAppOptions());
        using var sink = new CaptureSink();
        app.CVars.Execute("version", ExecSource.Console);
        var line = Assert.Single(sink.Entries, e => e.Message.StartsWith("Sage ", StringComparison.Ordinal)).Message;
        Assert.Equal($"Sage {BuildInfo.EngineVersion} ({BuildInfo.Config})", line);
        Assert.Matches(SemVer, line.Split(' ')[1]);
    }

    // ---- A plugin's `sage` range ------------------------------------------------------------------------

    [Plugin("test.for.this.engine", "1.0.0")]
    [RequiresPlugin("sage", ">=0.1")]
    private sealed class ForThisEngineModule : IModule { public void Init(ModuleContext ctx) { } }

    [Plugin("test.for.a.future.engine", "2.3.4")]
    [RequiresPlugin("sage", ">=99.0")]
    private sealed class ForAFutureEngineModule : IModule { public void Init(ModuleContext ctx) { } }

    [Plugin("sage", "1.0.0")]
    private sealed class CallsItselfSageModule : IModule { public void Init(ModuleContext ctx) { } }

    [Fact]
    public void APluginBuiltForThisEngineLoads()
    {
        using var app = SageApp.Create(new SageAppOptions { HostModules = new IModule[] { new ForThisEngineModule() } });
        app.Register();
        Assert.Equal("test.for.this.engine", app.Engine.Modules.Plugin(app.Engine.Modules.Modules.Single()).Id);
    }

    [Fact]
    public void APluginForAnotherEngineIsALoadErrorThatSaysBothVersions()
    {
        using var app = SageApp.Create(new SageAppOptions { HostModules = new IModule[] { new ForAFutureEngineModule() } });
        var ex = Assert.Throws<InvalidOperationException>(app.Register);
        Assert.Contains("test.for.a.future.engine 2.3.4", ex.Message);
        Assert.Contains("needs Sage >=99.0", ex.Message);
        Assert.Contains($"but this is Sage {BuildInfo.EngineVersion}", ex.Message);
    }

    [Theory]
    [InlineData("^0.1", "0.1.0", true)]
    [InlineData("^0.1", "0.1.7", true)]
    [InlineData("^0.1", "0.2.0", false)]      // 0.x: a minor is a breaking release
    [InlineData(">=0.2", "0.1.9", false)]
    [InlineData("~1.4", "1.4.2", true)]
    public void TheRangeIsCheckedAgainstTheEngineVersion(string range, string engine, bool loads)
    {
        var module = new ForThisEngineModule();
        var plugins = new Dictionary<IModule, PluginInfo>
        {
            [module] = new("test.ranged", new SemVersion(1, 0, 0), ModuleKind.Runtime,
                           new List<(string, VersionRange)> { ("sage", VersionRange.Parse(range, "test")) }),
        };
        var version = SemVersion.Parse(engine, "test");
        if (loads)
            Assert.Single(ModuleManager.Sort(new IModule[] { module }, plugins, version, engine));
        else
            Assert.Contains($"needs Sage {range}, but this is Sage {engine}",
                Assert.Throws<InvalidOperationException>(() => ModuleManager.Sort(new IModule[] { module }, plugins, version, engine)).Message);
    }

    [Fact]
    public void NoPluginMayCallItselfSage()
    {
        using var app = SageApp.Create(new SageAppOptions { HostModules = new IModule[] { new CallsItselfSageModule() } });
        var ex = Assert.Throws<InvalidOperationException>(app.Register);
        Assert.Contains("that id is the engine's own", ex.Message);
    }

    [Fact]
    public void TheRpgKitSaysWhichEnginesItIsBuiltFor()
    {
        var info = PluginInfo.Of(new Sage.Kits.Rpg.RpgKitModule());
        var (_, range) = Assert.Single(info.Requires, r => r.Id == "sage");
        Assert.True(range.Contains(BuildInfo.EngineSemVersion), $"the kit needs Sage {range}; this is {BuildInfo.EngineVersion}");
    }

    // ---- game.json's "sage" ---------------------------------------------------------------------------

    private static string GameWith(string? sage)
    {
        string dir = TestEnv.NewTempDir();
        string key = sage == null ? "" : $", \"sage\": \"{sage}\"";
        File.WriteAllText(Path.Combine(dir, "game.json"), $$"""{ "name": "Ranged", "id": "ranged", "mounts": []{{key}} }""");
        return dir;
    }

    [Fact]
    public void AGameMadeForThisEngineLoads()
    {
        Assert.Null(GameManifest.Load(GameWith(null)).Sage);
        Assert.Equal(">=0.1", GameManifest.Load(GameWith(">=0.1")).Sage);
    }

    [Fact]
    public void AGameMadeForAnotherEngineIsALoadErrorThatSaysBothVersions()
    {
        var ex = Assert.Throws<InvalidDataException>(() => GameManifest.Load(GameWith("^99.0")));
        Assert.Contains("this game was made for Sage ^99.0 (\"sage\")", ex.Message);
        Assert.Contains($"but this is Sage {BuildInfo.EngineVersion}", ex.Message);
        Assert.Contains("game.json", ex.Message);

        var manifest = GameManifest.Load(GameWith(null));
        manifest.Sage = "^0.1";
        manifest.CheckEngine(new SemVersion(0, 1, 4), "0.1.4", "game.json");
        Assert.Throws<InvalidDataException>(() => manifest.CheckEngine(new SemVersion(0, 2, 0), "0.2.0", "game.json"));
    }

    [Fact]
    public void AMalformedSageRangeSaysWhereItIs()
    {
        var ex = Assert.Throws<InvalidDataException>(() => GameManifest.Load(GameWith(">=zero")));
        Assert.Contains("\"sage\"", ex.Message);
        Assert.Contains("'zero' is not a version", ex.Message);
    }
}
