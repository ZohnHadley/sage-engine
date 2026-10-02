#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sage.Generators;

namespace Sage.Tests;

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
            ["effect"] = "sage.gameplay.attributes", ["gameplay_conventions"] = "sage.gameplay.attributes",
            ["movement_profile"] = "sage.gameplay.character",
            ["attack"] = "sage.gameplay.combat", ["damage_type"] = "sage.gameplay.combat",
            ["hit_location"] = "sage.gameplay.combat", ["hitboxes"] = "sage.gameplay.combat",   // hit locations (#137)
            ["hitbox_budget"] = "sage.gameplay.combat",                                          // its budget (#273)
            ["spread"] = "sage.gameplay.combat", ["recoil"] = "sage.gameplay.combat", ["routine"] = "sage.gameplay.ai", ["nav_area"] = "sage.gameplay.ai",
            ["dialogue"] = "sage.gameplay.dialogue", ["dialogue_topic"] = "sage.gameplay.dialogue", ["faction"] = "sage.gameplay.factions",
            ["quest"] = "sage.gameplay.quests",
            ["item"] = "sage.gameplay.items",
            ["map"] = "sage.maps",
            ["physics_layers"] = "sage.physics3d",
            ["physics_material"] = "sage.physics3d",   // issue #270
            ["prefab"] = RegistrationOwners.Core, ["placements"] = RegistrationOwners.Core, ["anim_graph"] = RegistrationOwners.Core, ["anim_events"] = RegistrationOwners.Core,
            ["scene"] = RegistrationOwners.Core,   // the Sandbox's own until the engine took scenes over (#29)
            ["state_machine"] = RegistrationOwners.Core,   // the engine's, like timers (#92)
            ["skeleton_sockets"] = RegistrationOwners.Core,   // sockets on a model's skeleton (#120)
            ["ragdoll"] = RegistrationOwners.Core,            // the bodies a model falls apart into (issue #243)
            ["sky"] = RegistrationOwners.Core,                // the sky over a day (issue 4h-2)
            ["calendar"] = RegistrationOwners.Core,           // what the day count means as a date (issue 4g-2)
            ["terrain"] = RegistrationOwners.Core,            // built-in ground for a data-only game (issue 4g-3)
            ["viewmodel"] = RegistrationOwners.Core,          // first-person arms (#121)
            ["rpg_conventions"] = "sage.kits.rpg",   // the RPG kit's, which the Sandbox names in game.json (#27)
            ["rpg_item"] = "sage.kits.rpg",          // an item's footprint on the inventory grid (#98)
            ["ui_style"] = "sage.ui", ["ui_layout"] = "sage.ui", ["screen"] = "sage.ui",   // the UI's (#96)
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
        Assert.Equal(new[] { "anim_events", "anim_graph", "calendar", "placements", "prefab", "ragdoll", "scene", "skeleton_sockets", "sky", "state_machine", "terrain", "viewmodel" }, app.Records.TypeNames);
    }

    // Plugin ids on declarations are strings, and one naming a plugin in another assembly (the client)
    // can't be checked by the compiler. Checked here instead: every owner in the engine's generated
    // registrations is a plugin the engine ships.
    [Fact]
    public void EveryOwnerTheEngineDeclaresIsAPluginItShips()
    {
        var generated = new GeneratedRegistrations();
        foreach (var assembly in EngineAssemblies.Base) generated.Include(assembly);
        var known = BasePlugins.All().Select(m => PluginInfo.Of(m).Id)
            .Append(RegistrationOwners.Core).Append("sage.client").ToHashSet();
        Assert.All(generated.Owners, owner => Assert.Contains(owner, known));
    }

    // ---- The generator itself --------------------------------------------------------------------

    [Fact]
    public void TheGeneratorRegistersEachDeclarationForItsPlugin()
    {
        var (output, diagnostics) = Generate("""
            using Sage.Core;
            using Sage.Simulation;
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
            using Sage.Core;
            using Sage.Simulation;
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
            using Sage.Core;
            using Sage.Simulation;
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
            using Sage.Core;
            using Sage.Simulation;
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
            using Sage.Core;
            using Sage.Simulation;
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

    // ---- Prefab parts and systems (issue #17) -------------------------------------------------------

    [Fact]
    public void ThePartGeneratorRegistersPartsForTheirPluginAndListsSystems()
    {
        var (output, diagnostics) = Generate("""
            using Sage.Core;
            using Sage.Simulation;
            [Plugin("test.one", "1.0.0")] public sealed class One : IModule { public void Init(ModuleContext ctx) { } }
            [Plugin("test.two", "1.0.0")] public sealed class Two : IModule { public void Init(ModuleContext ctx) { } }
            [PrefabPart("glow", Plugin = "test.one")]
            public sealed class GlowPart : IPrefabPart { public float Range = 4; public void Apply(in PrefabPartContext ctx) { } }
            [System("test.two.spin", Phase.Gameplay, After = new[] { "sage.effects.tick" })]
            internal sealed class SpinSystem : ISystem { public SpinSystem(int speed) { } public void Run(in SystemContext ctx) { } }
            """, new PartGenerator());

        Assert.Empty(diagnostics);
        Assert.Contains("case \"test.one\":", output);
        Assert.Contains("builder.PrefabPart<global::GlowPart>();", output);
        Assert.Contains("typeof(global::SpinSystem),", output);   // listed, not constructed: it takes a speed
    }

    [Fact]
    public void APartWithoutAnOwnerIsAnError()
    {
        var (_, diagnostics) = Generate("""
            using Sage.Core;
            using Sage.Simulation;
            [PrefabPart("glow")] public sealed class GlowPart : IPrefabPart { public void Apply(in PrefabPartContext ctx) { } }
            """, new PartGenerator());

        var d = Assert.Single(diagnostics);
        Assert.Equal("SAGE0010", d.Id);
        Assert.Contains("GlowPart is declared with [PrefabPart] but names no plugin, and this assembly has no [Plugin]", d.GetMessage());
    }

    [Theory]
    [InlineData("[PrefabPart(\"glow\")] public sealed class GlowPart { }", "it does not implement IPrefabPart")]
    [InlineData("[PrefabPart(\"glow\")] public sealed class GlowPart : IPrefabPart { public GlowPart(int x) { } public void Apply(in PrefabPartContext ctx) { } }",
                "it has no public parameterless constructor")]
    [InlineData("public static class Outer { [PrefabPart(\"glow\")] private sealed class GlowPart : IPrefabPart { public void Apply(in PrefabPartContext ctx) { } } }",
                "it is private or nested in a private type")]
    [InlineData("[System(\"test.spin\", Phase.Gameplay)] public sealed class Spin { }", "it does not implement ISystem")]
    [InlineData("[System(\"\", Phase.Gameplay)] public sealed class Spin : ISystem { public void Run(in SystemContext ctx) { } }", "its id is empty")]
    public void APartOrSystemThatCannotBeOneIsAnError(string declaration, string why)
    {
        var (_, diagnostics) = Generate($$"""
            using Sage.Core;
            using Sage.Simulation;
            [Plugin("mygame", "1.0.0")] public sealed class MyGame : IModule { public void Init(ModuleContext ctx) { } }
            {{declaration}}
            """, new PartGenerator());

        var d = Assert.Single(diagnostics);
        Assert.Equal("SAGE0011", d.Id);
        Assert.Contains(why, d.GetMessage());
    }

    [Fact]
    public void TwoPartsOrTwoSystemsWithOneIdAreAnError()
    {
        var (_, diagnostics) = Generate("""
            using Sage.Core;
            using Sage.Simulation;
            [Plugin("mygame", "1.0.0")] public sealed class MyGame : IModule { public void Init(ModuleContext ctx) { } }
            [PrefabPart("glow")] public sealed class GlowPart : IPrefabPart { public void Apply(in PrefabPartContext ctx) { } }
            [PrefabPart("Glow")] public sealed class ShinePart : IPrefabPart { public void Apply(in PrefabPartContext ctx) { } }
            [System("mygame.spin", Phase.Gameplay)] public sealed class Spin : ISystem { public void Run(in SystemContext ctx) { } }
            [System("mygame.spin", Phase.Gameplay)] public sealed class Twirl : ISystem { public void Run(in SystemContext ctx) { } }
            """, new PartGenerator());

        Assert.Equal(new[] { "SAGE0012", "SAGE0012" }, diagnostics.Select(d => d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("[PrefabPart(\"Glow\")] is declared by both GlowPart and ShinePart"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("[System(\"mygame.spin\")] is declared by both Spin and Twirl"));
    }

    // The compile-time half of the cross-phase rule: both systems are in this assembly, so the build
    // can say so. A constraint naming another assembly's system is checked when it is added.
    [Fact]
    public void ASystemOrderedAgainstOneInAnotherPhaseIsACompileError()
    {
        var (_, diagnostics) = Generate("""
            using Sage.Core;
            using Sage.Simulation;
            [System("mygame.perceive", Phase.AI)] public sealed class Perceive : ISystem { public void Run(in SystemContext ctx) { } }
            [System("mygame.think", Phase.Gameplay, After = new[] { "mygame.perceive" })]
            public sealed class Think : ISystem { public void Run(in SystemContext ctx) { } }
            """, new PartGenerator());

        var d = Assert.Single(diagnostics);
        Assert.Equal("SAGE0013", d.Id);
        Assert.Contains("Think (Gameplay) runs after 'mygame.perceive', which runs in AI", d.GetMessage());
    }

    // Runs the generator over `source`, compiled against the engine, and returns what it wrote and
    // reported — plus any compile error in what it wrote, which would otherwise pass unnoticed.
    internal static (string Output, ImmutableArray<Diagnostic> Diagnostics) Generate(string source, IIncrementalGenerator? generator = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Concat(EngineAssemblies.Base.Select(a => MetadataReference.CreateFromFile(a.Location)));
        var compilation = CSharpCompilation.Create("Declarations",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator ?? new RegistrationGenerator());
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
