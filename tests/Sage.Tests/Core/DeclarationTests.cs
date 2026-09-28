#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sage.Generators;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// Declarations register themselves (docs/REDESIGN.md §3.4, issue #16): [Record] and [SavedResource]
// name the plugin that owns them, and Sage.Generators writes the registration that plugin runs just
// before its Init. Nothing to forget, and nothing registered for a plugin that isn't loaded.
public class DeclarationTests
{
    public DeclarationTests() { _ = TestEnv.UserRoot; }

    // ---- What the engine and the Sandbox register --------------------------------------------------

    // The record types, and who registers each, exactly as the hand-written calls did before the
    // generator: a snapshot, so a declaration that loses its owner or its attribute shows up here.
    [Fact]
    public void TheSandboxRegistersTheRecordTypesItDidBeforeByTheSamePlugins()
    {
        using var app = HeadlessApp.ForGame(SandboxDirectory, new Sandbox.SandboxModule()).Build();
        var ledger = app.Engine.Registrations;

        var expected = new Dictionary<string, string>
        {
            ["ability"] = "sage.gameplay.abilities", ["cue"] = "sage.gameplay.abilities",
            ["ai_profile"] = "sage.gameplay.ai", ["ai_schedule"] = "sage.gameplay.ai",
            ["attribute"] = "sage.gameplay.attributes", ["tag"] = "sage.gameplay.attributes",
            ["effect"] = "sage.gameplay.attributes",
            ["movement_profile"] = "sage.gameplay.character",
            ["attack"] = "sage.gameplay.combat", ["damage_type"] = "sage.gameplay.combat",
            ["dialogue"] = "sage.gameplay.factions", ["faction"] = "sage.gameplay.factions",
            ["quest"] = "sage.gameplay.factions",
            ["item"] = "sage.gameplay.items",
            ["map"] = "sage.maps",
            ["physics_layers"] = "sage.physics3d",
            ["prefab"] = RegistrationOwners.Core, ["placements"] = RegistrationOwners.Core,
            ["scene"] = "sandbox",
        };
        Assert.Equal(expected.Keys.OrderBy(k => k, StringComparer.Ordinal), app.Records.TypeNames);
        foreach (var (type, owner) in expected)
            Assert.Equal(owner, ledger.OwnerOf("record type", type));
    }

    // A plugin that isn't loaded registers nothing: no plugins, no gameplay record types.
    [Fact]
    public void APluginThatIsNotLoadedRegistersNothing()
    {
        using var app = HeadlessApp.Bare().Build();
        Assert.Equal(new[] { "placements", "prefab" }, app.Records.TypeNames);
    }

    // Plugin ids on declarations are strings, and one naming a plugin in another assembly (the client)
    // can't be checked by the compiler. Checked here instead: every owner in the engine's generated
    // registrations is a plugin the engine ships.
    [Fact]
    public void EveryOwnerTheEngineDeclaresIsAPluginItShips()
    {
        var generated = new GeneratedRegistrations();
        generated.Include(typeof(Engine).Assembly);
        var known = SageApp.SimulationModules().Select(m => PluginInfo.Of(m).Id)
            .Append(RegistrationOwners.Core).Append("sage.client").ToHashSet();
        Assert.All(generated.Owners, owner => Assert.Contains(owner, known));
    }

    // ---- The generator itself --------------------------------------------------------------------

    [Fact]
    public void TheGeneratorRegistersEachDeclarationForItsPlugin()
    {
        var (output, diagnostics) = Generate("""
            using sage_engine;
            [Plugin("test.one", "1.0.0")] public sealed class One : IModule { public void Init(ModuleContext ctx) { } }
            [Plugin("test.two", "1.0.0")] public sealed class Two : IModule { public void Init(ModuleContext ctx) { } }
            [Record("widget", Plugin = "test.one")] public sealed class WidgetRecord { }
            [SavedResource("ledger", Plugin = "test.two")] public sealed class Ledger { }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("case \"test.one\":", output);
        Assert.Contains("builder.Record<global::WidgetRecord>();", output);
        Assert.Contains("case \"test.two\":", output);
        Assert.Contains("builder.SavedResource<global::Ledger>();", output);
    }

    [Fact]
    public void AnAssemblyWithOnePluginOwnsWhatItDeclares()
    {
        var (output, diagnostics) = Generate("""
            using sage_engine;
            [Plugin("mygame", "1.0.0")] public sealed class MyGame : IModule { public void Init(ModuleContext ctx) { } }
            [Record("scene")] public sealed class SceneRecord { }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("case \"mygame\":", output);
        Assert.Contains("builder.Record<global::SceneRecord>();", output);
    }

    [Fact]
    public void WithSeveralPluginsADeclarationMustSayWhichOwnsIt()
    {
        var (_, diagnostics) = Generate("""
            using sage_engine;
            [Plugin("test.one", "1.0.0")] public sealed class One : IModule { public void Init(ModuleContext ctx) { } }
            [Plugin("test.two", "1.0.0")] public sealed class Two : IModule { public void Init(ModuleContext ctx) { } }
            [Record("widget")] public sealed class WidgetRecord { }
            """);

        var d = Assert.Single(diagnostics);
        Assert.Equal("SAGE0001", d.Id);
        Assert.Contains("WidgetRecord is declared with [Record] but names no plugin, and this assembly has 2 plugins (test.one, test.two)",
                        d.GetMessage());
    }

    [Theory]
    [InlineData("[Record(\"widget\")] public abstract class WidgetRecord { }", "it is abstract")]
    [InlineData("[Record(\"widget\")] public sealed class WidgetRecord { public WidgetRecord(int x) { } }",
                "it has no public parameterless constructor")]
    [InlineData("public static class Outer { [Record(\"widget\")] private sealed class WidgetRecord { } }",
                "it is private or nested in a private type")]
    public void ATypeTheRegistrationCannotCreateIsAnError(string declaration, string why)
    {
        var (_, diagnostics) = Generate($$"""
            using sage_engine;
            [Plugin("mygame", "1.0.0")] public sealed class MyGame : IModule { public void Init(ModuleContext ctx) { } }
            {{declaration}}
            """);

        var d = Assert.Single(diagnostics);
        Assert.Equal("SAGE0002", d.Id);
        Assert.Contains(why, d.GetMessage());
    }

    [Fact]
    public void TwoDeclarationsOfOneIdAreAnError()
    {
        var (_, diagnostics) = Generate("""
            using sage_engine;
            [Plugin("mygame", "1.0.0")] public sealed class MyGame : IModule { public void Init(ModuleContext ctx) { } }
            [Record("widget")] public sealed class WidgetRecord { }
            [Record("widget")] public sealed class GadgetRecord { }
            """);

        var d = Assert.Single(diagnostics);
        Assert.Equal("SAGE0003", d.Id);
        Assert.Contains("[Record(\"widget\")] is declared by both GadgetRecord and WidgetRecord", d.GetMessage());
    }

    [Fact]
    public void AnAssemblyThatDeclaresNothingGetsNothing()
    {
        var (output, diagnostics) = Generate("public sealed class Plain { }");
        Assert.Empty(diagnostics);
        Assert.Equal("", output);
    }

    // Runs the generator over `source`, compiled against the engine, and returns what it wrote and
    // reported — plus any compile error in what it wrote, which would otherwise pass unnoticed.
    private static (string Output, ImmutableArray<Diagnostic> Diagnostics) Generate(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(Engine).Assembly.Location));
        var compilation = CSharpCompilation.Create("Declarations",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new RegistrationGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);

        var result = driver.GetRunResult();
        string output = string.Concat(result.GeneratedTrees.Select(t => t.ToString()));
        if (diagnostics.IsEmpty)
        {
            var errors = updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, "the generated code doesn't compile: " + string.Join("; ", errors));
        }
        return (output, diagnostics);
    }

    private static string SandboxDirectory => Path.Combine(TestEnv.FolderAbove("Sage.sln"), "games", "Sandbox");
}
