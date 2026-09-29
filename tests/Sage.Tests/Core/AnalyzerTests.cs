#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Sage.Generators;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// The analyzers that turn MAKING_A_GAME §10's "silently does nothing" into build errors (issue #19).
// Each is run over source text compiled against the engine, the way the build runs it over a game.
public class AnalyzerTests
{
    // ---- SAGE0020: registering after the Register stage ----------------------------------------------

    private const string Usings = """
        using Sage.Core;
        using Sage.Simulation;
        using Friflo.Engine.ECS;
        """;

    [Fact]
    public void RegisteringACvarInStartIsABuildError()
    {
        var d = Assert.Single(Analyze(Usings + """
            public sealed class M : IModule
            {
                public void Init(ModuleContext ctx) { }
                public void Start(ModuleContext ctx) => ctx.Engine.CVars.Register("fov", 90f, CVarFlags.Archive, "field of view");
            }
            """, new RegistrationStageAnalyzer()));
        Assert.Equal("SAGE0020", d.Id);
        Assert.Contains("CVarRegistry.Register registers a cvar in M.Start", d.GetMessage());
        Assert.Contains("(cvars are sealed once config.cfg is read, so its saved value would never apply); register it in the module's Init", d.GetMessage());
    }

    // Code written inside OnWorldCreated cannot run before it, so a lambda there is late too; and an
    // explicit implementation, or an override in a derived module, is the same method.
    [Fact]
    public void RegisteringInOnWorldCreatedOrCreateRulesIsABuildErrorLambdasIncluded()
    {
        var diagnostics = Analyze(Usings + """
            public class Base : IGameModule
            {
                protected Engine E = null!;
                public void Init(ModuleContext ctx) { E = ctx.Engine; E.CVars.RegisterCommand("ok", CVarFlags.None, "fine here", _ => { }); }
                public virtual void OnWorldCreated(World world) { }
                GameRules? IGameModule.CreateRules(World world) { E.Inputs.Register("open", (World w, in IOContext io) => { }); return null; }
            }
            public sealed class Derived : Base
            {
                public override void OnWorldCreated(World world)
                {
                    System.Action later = () => E.Actions.Register("jump", ActionKind.Button);
                    later();
                }
            }
            """, new RegistrationStageAnalyzer());
        Assert.Equal(new[] { "SAGE0020", "SAGE0020" }, diagnostics.Select(d => d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("EntityInputs.Register registers an entity input in Base.CreateRules"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("ActionRegistry.Register registers an input action in Derived.OnWorldCreated"));
    }

    [Fact]
    public void RegisteringInASystemIsABuildError()
    {
        var diagnostics = Analyze(Usings + """
            public sealed class Probe : ISystem
            {
                private readonly Engine _engine;
                public Probe(Engine engine) { _engine = engine; engine.Prefabs.Register<Part>(); }
                public void Run(in SystemContext ctx) => _engine.Saves.RegisterResource<Journal>();
                // Not the system's Run, and not late: a helper nobody calls from a late stage.
                public void Setup() => _engine.Records.Register<Journal>();
            }
            [PrefabPart("part")] public sealed class Part : IPrefabPart { public void Apply(in PrefabPartContext ctx) { } }
            [SavedResource("journal")] public sealed class Journal { }
            """, new RegistrationStageAnalyzer());
        Assert.Equal(new[] { "SAGE0020", "SAGE0020" }, diagnostics.Select(d => d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("PrefabRegistry.Register registers a prefab part in Probe's constructor"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("SaveSystem.RegisterResource registers a saved resource in Probe.Run"));
    }

    [Fact]
    public void RegisteringInInitIsNotAnError()
    {
        Assert.Empty(Analyze(Usings + """
            public sealed class M : IModule
            {
                public void Init(ModuleContext ctx)
                {
                    ctx.Engine.CVars.Register("fov", 90f, CVarFlags.Archive, "field of view");
                    ctx.Engine.Actions.Register("jump", ActionKind.Button);
                }
                public void Start(ModuleContext ctx) => ctx.Engine.CVars.Find("fov");
            }
            """, new RegistrationStageAnalyzer()));
    }

    // ---- SAGE0021 and SAGE0022: what the declaration generators leave unchecked ----------------------

    [Fact]
    public void AnEmptyRecordTypeOrResourceNameIsABuildError()
    {
        var diagnostics = Analyze(Usings + """
            [Record("")] public sealed class Nameless { }
            [Record("my item")] public sealed class Spaced { }
            [SavedResource("journal", Version = 0)] public sealed class Journal { }
            [Record("item")] public sealed class Fine { }
            """, new DeclarationAnalyzer());
        Assert.Equal(new[] { "SAGE0021", "SAGE0021", "SAGE0021" }, diagnostics.Select(d => d.Id));
        Assert.Contains(diagnostics, d => d.GetMessage().StartsWith("Nameless is declared with [Record] but its type is empty"));
        Assert.Contains(diagnostics, d => d.GetMessage().StartsWith("Spaced is declared with [Record] but its type 'my item' has whitespace"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("Journal is declared with [SavedResource] but its Version is below 1"));
    }

    [Fact]
    public void AnUpgraderNothingRunsIsABuildError()
    {
        var diagnostics = Analyze(Usings + """
            using System.Text.Json.Nodes;
            public sealed class NotSaved { [Upgrade(1)] static void From1(ref JsonObject o) { } }
            [SavedResource("journal", Version = 3)]
            public sealed class Journal
            {
                [Upgrade(1)] static void From1(ref JsonObject o) { }
                [Upgrade(1)] static void AlsoFrom1(ref JsonObject o) { }
                [Upgrade(3)] static void From3(ref JsonObject o) { }
                [Upgrade(2)] void From2(JsonObject o) { }
            }
            [Component("game:health", Version = 2)]
            public struct Health : IComponent { public float Value; [Upgrade(1)] static void From1(ref JsonObject o) { } }
            """, new DeclarationAnalyzer());
        Assert.All(diagnostics, d => Assert.Equal("SAGE0022", d.Id));
        var messages = diagnostics.Select(d => d.GetMessage()).ToList();
        Assert.Equal(4, messages.Count);
        Assert.Contains(messages, m => m.StartsWith("NotSaved.From1: [Upgrade] runs only on a [Component], [Tag] or [SavedResource] type"));
        Assert.Contains(messages, m => m.StartsWith("Journal.AlsoFrom1: a second [Upgrade(1)] (the first is From1)"));
        Assert.Contains(messages, m => m.StartsWith("Journal.From3: [Upgrade(3)] upgrades from a version before this one (1 to 2)"));
        Assert.Contains(messages, m => m.StartsWith("Journal.From2: an [Upgrade] method is `static void Name(ref JsonObject o)`"));
    }

    // ---- SAGE0023: strict saves ------------------------------------------------------------------

    private const string Components = Usings + """
        [Component("game:health")]
        public struct Health : IComponent
        {
            public float Value;
            [Transient] public float LastHitTime;
            public const float Max = 100;
            private float _cache;
            public float Cached => _cache;
        }
        [Transient, Component("game:derived")] public struct Derived : IComponent { public float Anything; }
        """;

    [Fact]
    public void StrictSavesIsOffByDefault()
    {
        Assert.Empty(Analyze(Components, new StrictSavesAnalyzer()));
        Assert.Empty(Analyze(Components, new StrictSavesAnalyzer(), ("SageStrictSaves", "false")));
    }

    [Fact]
    public void UnderStrictSavesAPublicComponentFieldMustDecide()
    {
        var d = Assert.Single(Analyze(Components, new StrictSavesAnalyzer(), ("SageStrictSaves", "true")));
        Assert.Equal("SAGE0023", d.Id);
        // The engine defines [Property] since issue #18, so the message offers both decisions.
        Assert.StartsWith("Health.Value is public, so it is saved; with SageStrictSaves on, say so: mark it [Property] to save it or [Transient]", d.GetMessage());
    }

    // [Property] is found by name (issue #18 defines it), so once it exists it counts as the decision.
    [Fact]
    public void UnderStrictSavesPropertyIsADecisionOnceItExists()
    {
        var source = Components.Replace("public float Value;", "[Property] public float Value;") + """

            namespace Sage.Core { [System.AttributeUsage(System.AttributeTargets.Field)] public sealed class PropertyAttribute : System.Attribute { } }
            """;
        Assert.Empty(Analyze(source, new StrictSavesAnalyzer(), ("SageStrictSaves", "true")));

        var undecided = Components + """

            namespace Sage.Core { public sealed class PropertyAttribute : System.Attribute { } }
            """;
        var d = Assert.Single(Analyze(undecided, new StrictSavesAnalyzer(), ("SageStrictSaves", "true")));
        Assert.Contains("mark it [Property] to save it or [Transient] to leave it out of saves", d.GetMessage());
    }

    // ---- SAGE0024: no MonoGame in a simulation assembly -------------------------------------------

    // Stands in for MonoGame, which the tests cannot reference (Sage.Tests is simulation-only too).
    private const string FakeMonoGame = """
        namespace Microsoft.Xna.Framework { public struct Vector3 { public static Vector3 Zero => default; } public class Game { } }
        """;

    private const string UsesMonoGame = FakeMonoGame + """

        namespace Game.Rules
        {
            using Microsoft.Xna.Framework;
            using Xna = Microsoft.Xna.Framework;
            public sealed class Rules
            {
                public Vector3 Spawn;
                public void Place() { var at = Xna.Vector3.Zero; _ = at; }
                public System.Numerics.Vector3 Fine;
            }
        }
        """;

    [Fact]
    public void AMonoGameTypeInASimulationOnlyAssemblyIsABuildError()
    {
        var diagnostics = Analyze(UsesMonoGame, new SimulationOnlyAnalyzer(), ("SageSimulationOnly", "true"));
        Assert.All(diagnostics, d => Assert.Equal("SAGE0024", d.Id));
        // The field's type, the qualified name's type, and the `var` that stands for it.
        Assert.Equal(3, diagnostics.Length);
        Assert.StartsWith("Vector3 is a MonoGame type (Microsoft.Xna.Framework), and this assembly is simulation-only (SageSimulationOnly)",
                          diagnostics[0].GetMessage());
    }

    [Fact]
    public void AClientAssemblyMayUseMonoGame()
    {
        Assert.Empty(Analyze(UsesMonoGame, new SimulationOnlyAnalyzer()));
    }

    // ---- SAGE0025: a base assembly never references a kit ----------------------------------------------

    // A kit, built here: Sage.Kits.Rpg with a type, a static method and an extension method.
    private static MetadataReference FakeKit()
    {
        var kit = CSharpCompilation.Create("Sage.Kits.Rpg",
            new[] { CSharpSyntaxTree.ParseText("""
                namespace Sage.Kits.Rpg
                {
                    public sealed class Spellbook { public int Pages; public static Spellbook Open() => new Spellbook(); }
                    public static class SpellbookExtensions { public static int Count(this Spellbook book) => book.Pages; }
                }
                """) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = kit.Emit(image);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private const string UsesAKit = """
        namespace Sage.Gameplay.Screens
        {
            using Sage.Kits.Rpg;
            public sealed class Journal
            {
                public Spellbook? Book;
                public int Pages() { var book = Spellbook.Open(); return book.Count(); }
            }
        }
        """;

    [Fact]
    public void ABaseAssemblyThatUsesAKitIsABuildError()
    {
        var diagnostics = AnalyzeWith(UsesAKit, new KitReferenceAnalyzer(), new[] { FakeKit() }, ("SageBaseAssembly", "true"));
        Assert.All(diagnostics, d => Assert.Equal("SAGE0025", d.Id));

        // The reference, once for the project…
        var reference = Assert.Single(diagnostics, d => d.Location == Location.None);
        Assert.StartsWith("The project reference is from the kit Sage.Kits.Rpg, and this is a base assembly (SageBaseAssembly)", reference.GetMessage());
        // …and every use: the field's type, the `var` and the type named for Open, and the extension method.
        var uses = diagnostics.Where(d => d.Location != Location.None).Select(d => d.GetMessage().Split(' ')[0]).ToList();
        Assert.Equal(new[] { "Spellbook", "Spellbook", "Spellbook", "Count" }, uses);
    }

    [Fact]
    public void AKitOrAGameMayUseAKit()
    {
        Assert.Empty(AnalyzeWith(UsesAKit, new KitReferenceAnalyzer(), new[] { FakeKit() }));
    }

    [Fact]
    public void ABaseAssemblyWithoutAKitHasNothingToReport()
    {
        Assert.Empty(Analyze(Components, new KitReferenceAnalyzer(), ("SageBaseAssembly", "true")));
    }

    // ---- The harness ---------------------------------------------------------------------------------

    // Runs `analyzer` over `source`, compiled against the engine and Friflo with the given MSBuild
    // properties visible (as CompilerVisibleProperty makes them), and returns what it reported in source
    // order — failing if the source itself does not compile, so a test cannot pass on a typo.
    private static ImmutableArray<Diagnostic> Analyze(string source, DiagnosticAnalyzer analyzer,
                                                      params (string Name, string Value)[] properties) =>
        AnalyzeWith(source, analyzer, Array.Empty<MetadataReference>(), properties);

    // The same, with more references (an assembly emitted by the test, such as a fake kit).
    private static ImmutableArray<Diagnostic> AnalyzeWith(string source, DiagnosticAnalyzer analyzer, MetadataReference[] extra,
                                                          params (string Name, string Value)[] properties)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Concat(EngineAssemblies.Base.Select(a => MetadataReference.CreateFromFile(a.Location)))
            .Append(MetadataReference.CreateFromFile(typeof(Friflo.Engine.ECS.IComponent).Assembly.Location))
            .GroupBy(r => r.FilePath).Select(g => g.First())
            .Concat(extra);
        var compilation = CSharpCompilation.Create("Analyzed",
            new[] { CSharpSyntaxTree.ParseText(source, path: "Analyzed.cs") }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "the test source doesn't compile: " + string.Join("; ", errors));

        var options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty,
            new Properties(properties.ToDictionary(p => "build_property." + p.Name, p => p.Value)));
        var diagnostics = compilation.WithAnalyzers(ImmutableArray.Create(analyzer), options)
            .GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
        return diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToImmutableArray();
    }

    private sealed class Properties : AnalyzerConfigOptionsProvider
    {
        private readonly Options _global;
        public Properties(Dictionary<string, string> values) { _global = new Options(values); }
        public override AnalyzerConfigOptions GlobalOptions => _global;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Options.Empty;
    }

    private sealed class Options : AnalyzerConfigOptions
    {
        public static readonly Options Empty = new(new Dictionary<string, string>());
        private readonly Dictionary<string, string> _values;
        public Options(Dictionary<string, string> values) { _values = values; }
        public override bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    }
}
