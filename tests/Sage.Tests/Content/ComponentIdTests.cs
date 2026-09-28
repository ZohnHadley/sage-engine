#nullable enable
using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Friflo.Engine.ECS;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sage.Generators;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// A test component in the `test` namespace whose name is also an engine component's: what a game does
// when it deliberately shadows one (`"collider"` in a `test:` prefab means this).
[Component("test:collider")] public struct ShadowCollider : IComponent { public float Radius; }

// Stable component ids (docs/REDESIGN.md §3.4, issue #16): `[Component("sage:health")]`, resolved
// exactly; a bare name in a prefab means the prefab's namespace, then `sage`; and the generator makes
// sure nothing is left without one.
public class ComponentIdTests
{
    public ComponentIdTests() { _ = TestEnv.UserRoot; }

    // ---- What the engine and the Sandbox declare --------------------------------------------------------

    // Every component and tag type in an engine or game assembly has an id — the generator makes a
    // missing one a build error (SAGE0004), and this checks the result from the other side, through
    // the schema a running engine actually reads.
    [Fact]
    public void EveryEngineAndGameComponentHasAStableId()
    {
        using var app = HeadlessApp.Bare().Build();
        var schema = EntityStore.GetEntitySchema();
        var ours = new[] { typeof(Engine).Assembly, typeof(Sandbox.SandboxModule).Assembly };

        var types = schema.ComponentTypeByType.Keys.Concat(schema.TagTypeByType.Keys)
                          .Where(t => ours.Contains(t.Assembly)).ToList();
        Assert.True(types.Count >= 40, $"expected the engine's components, found {types.Count}");
        foreach (var type in types)
        {
            string? id = app.Engine.Components.IdOf(type);
            Assert.True(id != null, $"{type.Name} has no id");
            Assert.StartsWith(type.Assembly == typeof(Engine).Assembly ? "sage:" : "sandbox:", id);
        }
        Assert.Equal("sage:ai_state", app.Engine.Components.IdOf(typeof(AIState)));
        Assert.Equal("sandbox:hop", app.Engine.Components.IdOf(typeof(Sandbox.Hop)));
        Assert.Equal("sage:player_controlled", app.Engine.Components.IdOf(typeof(PlayerControlled)));
    }

    // Two types claiming one id — from two mods, say — is an error naming both, not a silent overwrite.
    [Fact]
    public void TwoTypesClaimingOneIdIsAnErrorNamingBoth()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ComponentSchema.CheckUnique(new[]
        {
            new ComponentDeclaration(typeof(Health), "test:health", 1, Array.Empty<string>(), false),
            new ComponentDeclaration(typeof(Velocity), "test:health", 1, Array.Empty<string>(), false),
        }));
        Assert.Contains("'test:health'", ex.Message);
        Assert.Contains(typeof(Health).FullName!, ex.Message);
        Assert.Contains(typeof(Velocity).FullName!, ex.Message);

        // A tag and a component may share an id: they are different tables, as a prefab's
        // "components" and "tags" are different lists.
        ComponentSchema.CheckUnique(new[]
        {
            new ComponentDeclaration(typeof(Health), "test:thing", 1, Array.Empty<string>(), false),
            new ComponentDeclaration(typeof(FromPrefab), "test:thing", 1, Array.Empty<string>(), true),
        });
    }

    [Theory]
    [InlineData("sage:health", true)]
    [InlineData("my_mod.v2:fire-ball", true)]
    [InlineData("health", false)]         // no namespace
    [InlineData("Sage:Health", false)]    // upper case
    [InlineData("sage:", false)]
    [InlineData("a:b:c", false)]
    public void AnIdIsANamespaceAndAName(string id, bool valid)
    {
        Assert.Equal(valid, ComponentDeclaration.IsValidId(id));
        Assert.Equal(valid, ComponentGenerator.IsValidId(id));   // the generator applies the same rule
    }

    // ---- How a prefab names one --------------------------------------------------------------------

    private static HeadlessApp NewApp(string records, string ns) =>
        HeadlessApp.Gameplay().File("data/ids.json", records, ns).Boot("ids");

    // A bare name is the file's namespace first, then the engine's; an id is exact.
    [Fact]
    public void ABareNameMeansTheFilesNamespaceThenSage()
    {
        using var app = NewApp("""
            [ { "type": "prefab", "id": "thing",
                "components": { "health": { "current": 12 }, "transform": {}, "collider": { "radius": 2 },
                                "sage:rigid_body": { "kind": "Static" } },
                "tags": ["from_prefab", "sage:interactable"] } ]
            """, ns: "test");
        var thing = app.World.Spawn(new RecordId("test", "thing"));

        Assert.Equal(12f, app.World.Get<Health>(thing).Current);          // test:health, the file's own
        Assert.True(app.World.Has<Transform>(thing));                        // sage:transform, the engine's
        Assert.Equal(2f, app.World.Get<ShadowCollider>(thing).Radius);     // test:collider shadows sage:collider
        Assert.False(app.World.Has<Collider>(thing));
        Assert.Equal(BodyKind.Static, app.World.Get<RigidBody>(thing).Kind);   // an id, as written
        Assert.True(thing.Tags.Has<FromPrefab>());                          // test:from_prefab
        Assert.True(thing.Tags.Has<Interactable>());
    }

    // What a file written before issue #16 gets: an error that names the id to write, and one that
    // names candidates in other namespaces when a bare name is in neither the file's nor `sage`.
    [Fact]
    public void AnOldTypeNameOrAMissingNameSaysWhatToWrite()
    {
        using var log = new CaptureSink();
        using var app = NewApp("""
            [ { "type": "prefab", "id": "old_style",
                "components": { "AIState": {}, "velocity": {}, "sage:Transform": {} },
                "tags": ["PlayerControlled"] } ]
            """, ns: "sage");
        var thing = app.World.Spawn(new RecordId("sage", "old_style"));

        Assert.False(thing.IsNull);
        Assert.False(app.World.Has<AIState>(thing));
        // Said at load, where the prefab is written (issue #22), rather than when it spawns.
        var errors = log.Entries.Where(e => e.Level == LogLevel.Error && e.Message.Contains(": prefab sage:old_style: ")).Select(e => e.Message).ToList();
        Assert.Contains(errors, m => m.Contains("'AIState' is a C# type name") && m.Contains("\"sage:ai_state\""));
        Assert.Contains(errors, m => m.Contains("'PlayerControlled' is a C# type name") && m.Contains("\"sage:player_controlled\""));
        Assert.Contains(errors, m => m.Contains("no component 'velocity' here; did you mean \"test:velocity\"?"));
        Assert.Contains(errors, m => m.Contains("'sage:Transform'") && m.Contains("\"sage:transform\""));
    }

    // FGD export finds a prefab's collider however it was spelled, as spawning does.
    [Fact]
    public void FgdExportReadsTheColliderByItsId()
    {
        using var app = NewApp("""
            [ { "type": "prefab", "id": "crate", "components": { "sage:collider": { "shape": "Box", "size": [2, 3, 2] } } } ]
            """, ns: "sage");
        string fgd = FgdExport.Build(app.Engine, scale: 1f / 32f);
        Assert.Contains("size(-32 -32 0, 32 32 96) = crate", fgd);
    }

    // ---- The generator ---------------------------------------------------------------------------------

    [Fact]
    public void TheGeneratorWritesTheAssemblysIdTable()
    {
        var (output, diagnostics) = Generate("""
            using sage_engine;
            using Friflo.Engine.ECS;
            using System.Text.Json.Nodes;
            [Component("game:health", Version = 2, FormerNames = new[] { "Health" })]
            public struct Health : IComponent { public float Value; [Upgrade(1)] static void From1(ref JsonObject o) { } }
            [Tag("game:hostile")] public struct Hostile : ITag { }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("[assembly: global::sage_engine.GeneratedComponentsAttribute(typeof(global::Sage.Generated.SageComponents_Components))]", output);
        Assert.Contains("new(typeof(global::Health), \"game:health\", 2, new string[] { \"Health\" }, false),", output);
        Assert.Contains("new(typeof(global::Hostile), \"game:hostile\", 1, global::System.Array.Empty<string>(), true),", output);
    }

    [Theory]
    [InlineData("public struct Health : IComponent { }", "SAGE0004", "Health is an IComponent but has no [Component(\"namespace:name\")]")]
    [InlineData("public struct Hostile : ITag { }", "SAGE0004", "Hostile is an ITag but has no [Tag(\"namespace:name\")]")]
    [InlineData("[Component(\"Health\")] public struct Health : IComponent { }", "SAGE0005", "'Health' is not a component id")]
    [InlineData("[Component(\"game:health\", Version = 0)] public struct Health : IComponent { }", "SAGE0005", "Version starts at 1")]
    [InlineData("[Component(\"game:health\")] public struct Health { }", "SAGE0005", "[Component] is for a struct that implements IComponent")]
    [InlineData("[Tag(\"game:health\")] public struct Health : IComponent { }", "SAGE0005", "[Tag] is for a struct that implements ITag")]
    [InlineData("public static class Outer { [Component(\"game:health\")] private struct Health : IComponent { } }", "SAGE0005",
                "it is private")]
    [InlineData("[Component(\"game:health\", Version = 2)] public struct Health : IComponent { [Upgrade(1)] static void Up(JsonObject o) { } }",
                "SAGE0007", "Health.Up: an [Upgrade] method is `static void Name(ref JsonObject o)`")]
    [InlineData("[Component(\"game:health\", Version = 2)] public struct Health : IComponent { [Upgrade(2)] static void Up(ref JsonObject o) { } }",
                "SAGE0007", "[Upgrade(2)] upgrades from a version before this one (1 to 1)")]
    public void AHalfDeclaredComponentIsABuildError(string declaration, string id, string message)
    {
        var (_, diagnostics) = Generate($$"""
            using sage_engine;
            using Friflo.Engine.ECS;
            using System.Text.Json.Nodes;
            {{declaration}}
            """);

        var d = Assert.Single(diagnostics);
        Assert.Equal(id, d.Id);
        Assert.Contains(message, d.GetMessage());
    }

    [Fact]
    public void TwoComponentsWithOneIdAreABuildError()
    {
        var (_, diagnostics) = Generate("""
            using sage_engine;
            using Friflo.Engine.ECS;
            [Component("game:health")] public struct Health : IComponent { }
            [Component("game:health")] public struct Hitpoints : IComponent { }
            """);

        var d = Assert.Single(diagnostics);
        Assert.Equal("SAGE0006", d.Id);
        Assert.Contains("[Component(\"game:health\")] is declared by both Health and Hitpoints", d.GetMessage());
    }

    // Runs ComponentGenerator over `source`, compiled against the engine and Friflo, and returns what it
    // wrote and reported — failing on a compile error in what it wrote.
    private static (string Output, ImmutableArray<Diagnostic> Diagnostics) Generate(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(Engine).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(IComponent).Assembly.Location))
            .GroupBy(r => r.FilePath).Select(g => g.First());
        var compilation = CSharpCompilation.Create("Components",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ComponentGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out var diagnostics);

        string output = string.Concat(driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()));
        if (diagnostics.IsEmpty)
        {
            var errors = updated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, "the generated code doesn't compile: " + string.Join("; ", errors));
        }
        return (output, diagnostics);
    }
}
