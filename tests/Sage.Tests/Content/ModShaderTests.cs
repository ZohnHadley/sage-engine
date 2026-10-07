#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// Shaders in mods (issue #400): a mod's `.fx` is compiled when a dev build loads it and when it is edited, into
// the mod's own folder, with the engine's headers to include; a failure keeps the shader already compiled, or
// leaves sage:error (magenta) when there is none; `sage mods pack` compiles every effect into the package, and a
// package with a `.fx` but no `.mgfxo` is refused. mgfxc cannot run here (Linux without Wine), so the compiler
// is a fake that resolves includes as mgfxc does and writes what it compiled.
public class ModShaderTests
{
    public ModShaderTests() { _ = TestEnv.UserRoot; }

    // Inlines includes (relative to the including file), fails on a missing one or an `#error`, and writes
    // "compiled:" and the result.
    private sealed class FakeCompiler : IShaderCompiler
    {
        public bool Runs = true;
        public readonly List<string> Compiled = new();

        public bool CanRun(out string why) { why = Runs ? "" : "the fake compiler is switched off"; return Runs; }

        public ShaderCompileResult Compile(string source, string output)
        {
            lock (Compiled) Compiled.Add(Path.GetFileName(source));
            var text = new System.Text.StringBuilder();
            if (Inline(source, text, 0) is { } error) return new ShaderCompileResult(false, error);
            File.WriteAllText(output, "compiled:" + text);
            return new ShaderCompileResult(true, "");
        }

        private static string? Inline(string file, System.Text.StringBuilder into, int depth)
        {
            if (depth > 8) return $"{file}: includes too deep";
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("#error", StringComparison.Ordinal)) return $"{file}({i + 1},1): error X3501: {line[6..].Trim()}";
                if (ShaderIncludes.Includes(line) is [var include])
                {
                    string path = Path.Combine(Path.GetDirectoryName(file)!, include);
                    if (!File.Exists(path)) return $"{file}({i + 1},1): error X1507: failed to open source file: '{include}'";
                    if (Inline(path, into, depth + 1) is { } error) return error;
                    continue;
                }
                into.Append(line).Append('\n');
            }
            return null;
        }
    }

    private static void Write(string dir, string relative, string text, DateTime? at = null)
    {
        string file = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        if (at is { } time) File.SetLastWriteTimeUtc(file, time);
    }

    private static string Text(string dir, string relative) => File.ReadAllText(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));

    private static string ModFolder(string parent, string id)
    {
        string dir = Path.Combine(parent, id);
        Write(dir, "mod.json", $$"""{ "id": "{{id}}", "version": "1.0.0", "game": "village" }""");
        return dir;
    }

    private static readonly DateTime Old = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime New = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    [Xunit.Fact]
    public void AnEffectReachesItselfAndEveryHeaderItIncludes()
    {
        var sources = new Dictionary<string, string>
        {
            ["glow.fx"] = "#include \"lib/noise.fxh\"\n#include \"common.fxh\"",
            ["lib/noise.fxh"] = "#include \"../consts.fxh\"",
            ["consts.fxh"] = "#include \"glow.fx\"",   // a cycle ends
        };
        Assert.Equal(new[] { "glow.fx", "lib/noise.fxh", "consts.fxh", "common.fxh" }, ShaderIncludes.Reached(sources, "glow.fx"));
    }

    [Xunit.Fact]
    public void AnEffectIsStaleWithoutItsCompiledFile_OrWhenItOrAHeaderOfItsFolderIsNewer()
    {
        string dir = TestEnv.NewTempDir();
        Write(dir, "glow.fx", "#include \"glow.fxh\"\n#include \"common.fxh\"", Old);
        Write(dir, "glow.fxh", "float4 Tint;", Old);
        Write(dir, "plain.fx", "float4 x;", Old);
        Assert.Equal(new[] { "glow.fx", "plain.fx" }, ShaderBuild.Stale(dir));

        Write(dir, "glow.mgfxo", "x", New);
        Write(dir, "plain.mgfxo", "x", New);
        Assert.Empty(ShaderBuild.Stale(dir));   // common.fxh is the engine's, not this folder's: not watched here

        File.SetLastWriteTimeUtc(Path.Combine(dir, "glow.fxh"), New.AddDays(1));
        Assert.Equal(new[] { "glow.fx" }, ShaderBuild.Stale(dir));
    }

    [Xunit.Fact]
    public void AModsEffectIncludesTheEnginesHeaders_AndItsOwnWin()
    {
        string engine = TestEnv.NewTempDir();
        Write(engine, "common.fxh", "engine common");
        Write(engine, "noise.fxh", "engine noise");
        string mod = TestEnv.NewTempDir();
        Write(mod, "glow.fx", "#include \"common.fxh\"\n#include \"noise.fxh\"\nglow");
        Write(mod, "noise.fxh", "mod noise");
        var compiler = new FakeCompiler();

        var result = ShaderBuild.Compile(compiler, mod, "glow.fx", Path.Combine(mod, "glow.mgfxo"), ShaderBuild.Headers(new[] { engine }));

        Assert.True(result.Ok, result.Output);
        Assert.Equal("compiled:engine common\nmod noise\nglow\n", Text(mod, "glow.mgfxo"));
        Assert.Equal(new[] { "glow.fx" }, Directory.EnumerateFiles(mod).Select(Path.GetFileName).Where(f => f!.EndsWith(".fx")).ToArray());
    }

    [Xunit.Fact]
    public void AFailedCompileKeepsTheCompiledFile_AndItsMessageNamesTheModsFile()
    {
        string mod = TestEnv.NewTempDir();
        Write(mod, "glow.fx", "float4 x;\n#error typo here");
        Write(mod, "glow.mgfxo", "the last good one");

        var result = ShaderBuild.Compile(new FakeCompiler(), mod, "glow.fx", Path.Combine(mod, "glow.mgfxo"));

        Assert.False(result.Ok);
        Assert.Equal("the last good one", Text(mod, "glow.mgfxo"));
        Assert.Contains(Path.Combine(Path.GetFullPath(mod), "glow.fx") + "(2,1): error X3501: typo here", result.Output);
        Assert.Contains("keeping the shader already compiled", ShaderBuild.FailureMessage("", "glow.fx", result.Output, hadCompiled: true));
        Assert.Contains("draw sage:error (magenta)", ShaderBuild.FailureMessage("", "glow.fx", result.Output, hadCompiled: false));
    }

    // `sage mods pack`: the issue's "a packed mod ships compiled effects". The package holds each effect's
    // `.mgfxo`, compiled now with the engine's headers, and a stale one beside the source is not what is packed.
    [Xunit.Fact]
    public void PackingAModCompilesEveryEffectIntoThePackage()
    {
        string engine = TestEnv.NewTempDir();
        Write(engine, "common.fxh", "engine common");
        string mod = ModFolder(TestEnv.NewTempDir(), "glowing");
        Write(mod, "shaders/glow.fx", "#include \"common.fxh\"\nglow");
        Write(mod, "shaders/fx/ripple.fx", "ripple");
        Write(mod, "shaders/glow.mgfxo", "stale");
        var compiler = new FakeCompiler();
        string output = Path.Combine(TestEnv.NewTempDir(), "glowing.sagemod");

        var packed = ShaderBuild.PackMod(mod, output, compiler, ShaderBuild.Headers(new[] { engine }));

        Assert.Equal(2, packed.Compiled);
        using var zip = new ZipMount("mods/glowing", output, "glowing");
        string Read(string path) { using var r = new StreamReader(zip.Open(VirtualPath.Parse(path))); return r.ReadToEnd(); }
        Assert.Equal("compiled:engine common\nglow\n", Read("shaders/glow.mgfxo"));
        Assert.Equal("compiled:ripple\n", Read("shaders/fx/ripple.mgfxo"));
        Assert.True(zip.Exists(VirtualPath.Parse("shaders/glow.fx")));   // the source travels with it
        Assert.Equal("stale", Text(mod, "shaders/glow.mgfxo"));          // the folder is not written to
    }

    [Xunit.Fact]
    public void WithoutACompiler_AnUpToDateCompiledFileIsPacked_AndAStaleOneRefusesThePack()
    {
        string mod = ModFolder(TestEnv.NewTempDir(), "glowing");
        Write(mod, "shaders/glow.fx", "glow", Old);
        Write(mod, "shaders/glow.mgfxo", "built on windows", New);
        var compiler = new FakeCompiler { Runs = false };
        string output = Path.Combine(TestEnv.NewTempDir(), "glowing.sagemod");

        var packed = ShaderBuild.PackMod(mod, output, compiler, null);
        Assert.Equal((0, 1), (packed.Compiled, packed.UpToDate));
        using (var zip = new ZipMount("mods/glowing", output, "glowing"))
        using (var r = new StreamReader(zip.Open(VirtualPath.Parse("shaders/glow.mgfxo"))))
            Assert.Equal("built on windows", r.ReadToEnd());

        File.SetLastWriteTimeUtc(Path.Combine(mod, "shaders", "glow.fx"), New.AddDays(1));
        string again = Path.Combine(TestEnv.NewTempDir(), "again.sagemod");
        var error = Assert.Throws<InvalidDataException>(() => ShaderBuild.PackMod(mod, again, compiler, null));
        Assert.Contains("shaders/glow.fx has no up-to-date glow.mgfxo and the fake compiler is switched off", error.Message);
        Assert.False(File.Exists(again));
    }

    [Xunit.Fact]
    public void AnEffectThatDoesNotCompileStopsThePack_SayingWhy()
    {
        string mod = ModFolder(TestEnv.NewTempDir(), "broken");
        Write(mod, "shaders/glow.fx", "#include \"missing.fxh\"");
        string output = Path.Combine(TestEnv.NewTempDir(), "broken.sagemod");

        var error = Assert.Throws<InvalidDataException>(() => ShaderBuild.PackMod(mod, output, new FakeCompiler(), null));

        Assert.Contains("shaders/glow.fx did not compile", error.Message);
        Assert.Contains("failed to open source file: 'missing.fxh'", error.Message);
        Assert.False(File.Exists(output));
    }

    // ModPackage alone (what packs without the shader step) refuses a `.fx` with no `.mgfxo`: a package is never
    // compiled where it is played.
    [Xunit.Fact]
    public void APackageWithAnEffectButNoCompiledFileIsRefused()
    {
        string mod = ModFolder(TestEnv.NewTempDir(), "uncompiled");
        Write(mod, "shaders/glow.fx", "glow");
        string output = Path.Combine(TestEnv.NewTempDir(), "uncompiled.sagemod");

        var error = Assert.Throws<InvalidDataException>(() => ModPackage.Pack(mod, output));
        Assert.Contains("'shaders/glow.fx' has no compiled 'shaders/glow.mgfxo'", error.Message);
        Assert.False(File.Exists(output));
    }

    [Xunit.Fact]
    public void BuildingAModCompilesOnlyWhatIsOutOfDate_BesideItsSource()
    {
        string mod = ModFolder(TestEnv.NewTempDir(), "glowing");
        Write(mod, "shaders/glow.fx", "glow", New);
        Write(mod, "shaders/done.fx", "done", Old);
        Write(mod, "shaders/done.mgfxo", "already", New);
        var compiler = new FakeCompiler();

        var results = ShaderBuild.BuildMod(mod, compiler, null);

        Assert.Equal(new[] { ("shaders/glow.fx", true) }, results.Select(r => (r.Effect, r.Result.Ok)).ToArray());
        Assert.Equal("compiled:glow\n", Text(mod, "shaders/glow.mgfxo"));
        Assert.Equal("already", Text(mod, "shaders/done.mgfxo"));
        Assert.Empty(ShaderBuild.BuildMod(mod, compiler, null));
        Assert.Throws<InvalidOperationException>(() =>
        {
            File.SetLastWriteTimeUtc(Path.Combine(mod, "shaders", "glow.fx"), DateTime.UtcNow.AddDays(1));
            ShaderBuild.BuildMod(mod, new FakeCompiler { Runs = false }, null);
        });
    }

    // The dev build's half (ShaderHotCompile, which the client polls): on load, a folder mod's effect with no
    // `.mgfxo` is compiled into the mod's own folder, with the game's and the engine's headers; the game's file at
    // the same path is not touched. An edit to the mod's header recompiles the effects that include it; a failure
    // keeps the last good file and says so.
    [Xunit.Fact]
    public void ADevBuildCompilesAModsEffectsOnLoad_AndAgainWhenOneIsEdited()
    {
        var fx = new MountFixture();
        fx.Write("engine", "shaders/common.fxh", "engine common");
        fx.Write("game", "shaders/lit.fx", "game lit");
        fx.Write("game", "shaders/lit.mgfxo", "game lit compiled");
        fx.Write("mods/glowing", "shaders/lit.fx", "#include \"common.fxh\"\n#include \"glow.fxh\"\nmod lit");
        fx.Write("mods/glowing", "shaders/glow.fxh", "tint 1");
        File.SetLastWriteTimeUtc(Path.Combine(fx.Dir("game"), "shaders", "lit.mgfxo"), DateTime.UtcNow.AddMinutes(1));
        fx.Mount("engine", "sage");
        fx.Mount("game", "village");
        fx.Mount("mods/glowing", "glowing");
        var compiler = new FakeCompiler();
        using var log = new CaptureSink();

        using var shaders = new ShaderHotCompile(fx.Vfs, compiler, engineShaders: null, watch: false);
        Assert.Equal(1, shaders.StaleOnLoad);
        Assert.Equal(new[] { ("mods/glowing:shaders/lit.fx", true) }, shaders.CompilePending().ToArray());
        Assert.Equal("compiled:engine common\ntint 1\nmod lit\n", Text(fx.Dir("mods/glowing"), "shaders/lit.mgfxo"));
        Assert.Equal("game lit compiled", Text(fx.Dir("game"), "shaders/lit.mgfxo"));
        Assert.Equal("glowing:shaders/lit.mgfxo", VirtualPath.InNamespace("glowing", VirtualPath.Parse("shaders/lit.mgfxo")).Value);
        Assert.True(fx.Vfs.Exists(VirtualPath.Parse("glowing:shaders/lit.mgfxo")));

        fx.Write("mods/glowing", "shaders/glow.fxh", "tint 2");
        shaders.NoteChange(Path.Combine(fx.Dir("mods/glowing"), "shaders", "glow.fxh"));
        Assert.Equal(new[] { ("mods/glowing:shaders/lit.fx", true) }, shaders.CompilePending().ToArray());
        Assert.Contains("tint 2", Text(fx.Dir("mods/glowing"), "shaders/lit.mgfxo"));

        fx.Write("mods/glowing", "shaders/glow.fxh", "#error not a tint");
        shaders.NoteChange(Path.Combine(fx.Dir("mods/glowing"), "shaders", "glow.fxh"));
        Assert.Equal(new[] { ("mods/glowing:shaders/lit.fx", false) }, shaders.CompilePending().ToArray());
        Assert.Contains("tint 2", Text(fx.Dir("mods/glowing"), "shaders/lit.mgfxo"));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Category == LogCat.Shaders
            && e.Message.Contains("mods/glowing: lit.fx did not compile; keeping the shader already compiled")
            && e.Message.Contains("error X3501: not a tint"));
    }

    [Xunit.Fact]
    public void AModsEffectThatNeverCompiled_LeavesItsMaterialsOnSageError()
    {
        var fx = new MountFixture();
        fx.Write("mods/broken", "shaders/glow.fx", "#error broken from the start");
        fx.Mount("mods/broken", "broken");
        using var log = new CaptureSink();

        using var shaders = new ShaderHotCompile(fx.Vfs, new FakeCompiler(), engineShaders: null, watch: false);
        Assert.Equal(new[] { ("mods/broken:shaders/glow.fx", false) }, shaders.CompilePending().ToArray());

        Assert.False(fx.Vfs.Exists(VirtualPath.Parse("shaders/glow.mgfxo")));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("materials using it draw sage:error (magenta)"));
    }

    [Xunit.Fact]
    public void WhereTheCompilerCannotRun_NothingIsCompiled_AndItIsSaidOnce()
    {
        var fx = new MountFixture();
        fx.Write("mods/glowing", "shaders/glow.fx", "glow");
        fx.Mount("mods/glowing", "glowing");
        using var log = new CaptureSink();

        using var shaders = new ShaderHotCompile(fx.Vfs, new FakeCompiler { Runs = false }, engineShaders: null, watch: false);
        Assert.Empty(shaders.CompilePending());

        Assert.False(File.Exists(Path.Combine(fx.Dir("mods/glowing"), "shaders", "glow.mgfxo")));
        Assert.Single(log.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("mods/glowing:shaders/glow.fx")
            && e.Message.Contains("the fake compiler is switched off"));
    }

    // A material naming a mod's effect: the `.fx` in a folder mod makes it the mod's own asset (`glowing:...`), which
    // its compile then fills; in a package, only a `.mgfxo` counts, since nothing compiles a zip.
    [Xunit.Fact]
    public void AFolderModsEffectSourceMakesItsCompiledPathItsOwn_AZipsDoesNot()
    {
        var fx = new MountFixture();
        fx.Write("mods/glowing", "shaders/glow.fx", "glow");
        fx.Write("mods/glowing", "data/charm.json", """[{ "type": "test_charm", "id": "charm", "icon": "shaders/glow.mgfxo" }]""");
        fx.Mount("game", "village");
        fx.Mount("mods/glowing", "glowing");
        var store = new RecordStore { MissingAssetsAreErrors = true };
        store.Register<TestItem>();
        store.Register<TestSpell>();
        store.Register<TestCharm>();
        store.Load(fx.Vfs);
        Assert.Equal(0, store.ErrorCount);
        Assert.Equal("glowing:shaders/glow.mgfxo", store.Get<TestCharm>(new RecordId("glowing", "charm")).Icon.ToString());

        // A package with one effect compiled and one not (made by hand: `pack` refuses the second).
        string mod = ModFolder(TestEnv.NewTempDir(), "zipped");
        Write(mod, "shaders/glow.fx", "glow");
        Write(mod, "shaders/glow.mgfxo", "compiled");
        Write(mod, "shaders/ripple.fx", "ripple");
        string package = Path.Combine(TestEnv.NewTempDir(), "zipped.sagemod");
        System.IO.Compression.ZipFile.CreateFromDirectory(mod, package);
        var vfs = new VirtualFileSystem();
        using var zip = new ZipMount("mods/zipped", package, "zipped");
        vfs.Mount(zip);
        Assert.True(AssetChecks.Exists(vfs, VirtualPath.Parse("shaders/glow.mgfxo")));
        Assert.False(AssetChecks.Exists(vfs, VirtualPath.Parse("shaders/ripple.mgfxo")));
        Assert.False(AssetChecks.ExistsIn(zip, VirtualPath.Parse("shaders/ripple.mgfxo")));
    }
}
