#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Numerics;
using Friflo.Engine.ECS;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sage.Generators;
using sage_engine;

namespace sage_engine.Tests;

using Assert = Xunit.Assert;

// The metadata table (docs/REDESIGN.md §3.4 item 2, issue #18): what each declaration's fields are —
// type, default, range, unit, tooltip, category, enum values and what a reference points at — written
// by Sage.Generators for the engine and the games, read by reflection only where the generator did not
// run (this test assembly).
public class MetadataTests
{
    public MetadataTests() { _ = TestEnv.UserRoot; }

    // ---- the engine's table ----------------------------------------------------------------------------

    [Fact]
    public void TheEngineAndTheSandboxCarryGeneratedTables()
    {
        var engine = EngineAssemblies.Base.SelectMany(a => Metadata.In(a).Values).ToDictionary(t => t.Type);
        var sandbox = Metadata.In(typeof(Sandbox.SandboxModule).Assembly);

        Assert.NotEmpty(engine);
        Assert.All(engine.Values.Concat(sandbox.Values), t => Assert.True(t.Generated, $"{t} was read by reflection"));

        // One of each kind of declaration is in it, under its declared id.
        Assert.Equal((DeclarationKind.Component, "sage:point_light"), Kind(typeof(PointLight)));
        Assert.Equal((DeclarationKind.PrefabPart, "light"), Kind(typeof(LightPart)));
        Assert.Equal((DeclarationKind.Record, "attribute"), Kind(typeof(AttributeRecord)));
        Assert.Equal((DeclarationKind.Record, "scene"), Kind(typeof(Sandbox.SceneRecord)));
        Assert.Equal((DeclarationKind.Tag, "sandbox:faces_camera"), Kind(typeof(Sandbox.FacesCamera)));
        Assert.Contains(engine.Values, t => t.Kind == DeclarationKind.SavedResource);

        static (DeclarationKind, string) Kind(Type type) => (Metadata.Of(type).Kind, Metadata.Of(type).Id);
    }

    // What a [Property] says, and what the field's type says for itself.
    [Fact]
    public void AFieldCarriesItsRangeUnitTooltipAndDefault()
    {
        var light = Metadata.Of(typeof(LightPart));
        var range = light.Field("range")!;

        Assert.Equal("Range", range.Name);
        Assert.Equal(ValueKind.Number, range.Kind);
        Assert.Equal(0d, range.Min);
        Assert.Null(range.Max);
        Assert.Equal("m", range.Unit);
        Assert.Equal("Where the light fades to nothing", range.Tooltip);
        Assert.Equal(8f, light.DefaultOf(range));             // the initialiser, read off a new instance

        var body = Metadata.Of(typeof(BodyPart));
        var shape = body.Field("shape")!;
        Assert.Equal(ValueKind.Enum, shape.Kind);
        Assert.Equal(new[] { "Box", "Sphere", "Capsule", "Mesh" }, shape.EnumValues);
        Assert.Equal("Shape", shape.Category);
        Assert.Equal(ColliderShape.Box, body.DefaultOf(shape));

        var rigid = Metadata.Of(typeof(RigidBody)).Field("friction")!;
        Assert.Equal((0d, 1d), (rigid.Min, rigid.Max));

        // [Transient] is in the table too: the inspector says so, and the FGD never offers one.
        Assert.True(Metadata.Of(typeof(Melee)).Field("timer")!.Transient);
        Assert.False(Metadata.Of(typeof(Melee)).Field("cooldown")!.Transient);
    }

    // A RecordId names a record type, and a list of them names it for every item; an AssetPath names an
    // asset kind. A nested class is an object with fields of its own.
    [Fact]
    public void ReferencesAndNestedShapesAreDescribed()
    {
        Assert.Equal("attack", Metadata.Of(typeof(Melee)).Field("attack")!.RecordType);
        Assert.Equal("ai_schedule", Metadata.Of(typeof(AIState)).Field("schedule")!.RecordType);

        // A RecordRef<T> (issue #22) names its record type through T's [Record]; a list of them too.
        var faction = Metadata.Of(typeof(FactionPart)).Field("id")!;
        Assert.Equal((ValueKind.RecordId, "faction"), (faction.Kind, faction.RecordType));

        var effects = Metadata.Of(typeof(EffectsPart)).Field("ids")!;
        Assert.Equal(ValueKind.List, effects.Kind);
        Assert.Equal(ValueKind.RecordId, effects.Item!.Kind);
        Assert.Equal("effect", effects.Item.RecordType);

        var items = Metadata.Of(typeof(InventoryPart)).Field("items")!;
        Assert.Equal(ValueKind.Object, items.Item!.Kind);
        Assert.Equal(new[] { "item", "count" }, items.Item.Fields.Select(f => f.JsonName));
        Assert.Equal("item", items.Item.Fields[0].RecordType);

        var map = Metadata.Of(typeof(MapRecord)).Field("file")!;
        Assert.Equal((ValueKind.AssetPath, "map"), (map.Kind, map.AssetKind));
    }

    // Every [RecordRef] names a record type something declares: a reference to a type nobody declares
    // is a form with an empty dropdown and a schema with an empty enum.
    [Fact]
    public void EveryRecordRefNamesADeclaredRecordType()
    {
        var tables = EngineAssemblies.Base.Append(typeof(Sandbox.SandboxModule).Assembly).SelectMany(a => Metadata.In(a).Values).ToList();
        var recordTypes = tables.Where(t => t.Kind == DeclarationKind.Record).Select(t => t.Id).ToHashSet();

        var refs = new List<(string Where, string Type)>();
        void Walk(string where, IEnumerable<FieldMetadata> fields)
        {
            foreach (var f in fields)
            {
                if (f.RecordType != null) refs.Add(($"{where}.{f.JsonName}", f.RecordType));
                if (f.Item != null) Walk($"{where}.{f.JsonName}[]", new[] { f.Item });
                Walk($"{where}.{f.JsonName}", f.Fields);
            }
        }
        foreach (var t in tables) Walk(t.Id, t.Fields);

        Assert.True(refs.Count >= 20, $"only {refs.Count} [RecordRef]s: the annotations went missing");
        Assert.All(refs, r => Assert.True(recordTypes.Contains(r.Type), $"{r.Where} refers to '{r.Type}', which no [Record] declares"));
    }

    // The two ways of reading a table must agree, or a test (reflection) and the game (generated) would
    // be looking at different fields.
    [Fact]
    public void TheGeneratedTableMatchesReflectionForEveryEngineDeclaration()
    {
        var generated = EngineAssemblies.Base.Append(typeof(Sandbox.SandboxModule).Assembly).SelectMany(a => Metadata.In(a).Values).ToList();
        Assert.True(generated.Count > 60, $"only {generated.Count} declarations");

        foreach (var table in generated)
        {
            var reflected = Metadata.Reflect(table.Type, table.Kind, table.Id);
            Assert.False(reflected.Generated);
            Same(table.Id, table.Fields, reflected.Fields);
            foreach (var field in table.Fields)
                Assert.True(Equals(Normal(table.DefaultOf(field)), Normal(reflected.DefaultOf(field))),
                            $"{table.Id}.{field.JsonName}: the default differs");
        }

        static void Same(string where, IReadOnlyList<FieldMetadata> a, IReadOnlyList<FieldMetadata> b)
        {
            Assert.True(a.Select(f => f.Name).SequenceEqual(b.Select(f => f.Name)),
                        $"{where}: generated [{string.Join(", ", a.Select(f => f.Name))}] but reflected [{string.Join(", ", b.Select(f => f.Name))}]");
            for (int i = 0; i < a.Count; i++)
            {
                var (x, y) = (a[i], b[i]);
                string at = $"{where}.{x.Name}";
                Assert.True(x.JsonName == y.JsonName && x.Type == y.Type && x.Kind == y.Kind, $"{at}: name, type or kind");
                Assert.True(x.Min == y.Min && x.Max == y.Max && x.Unit == y.Unit && x.Tooltip == y.Tooltip && x.Category == y.Category,
                            $"{at}: its [Property]");
                Assert.True(x.RecordType == y.RecordType && x.AssetKind == y.AssetKind && x.Transient == y.Transient, $"{at}: its references");
                Assert.True(x.EnumValues.SequenceEqual(y.EnumValues), $"{at}: its enum values");
                Assert.True((x.Get == null) == (y.Get == null) && (x.Set == null) == (y.Set == null), $"{at}: its accessors");
                Assert.True((x.Item == null) == (y.Item == null), $"{at}: its item");
                if (x.Item != null) Same(at + "[]", new[] { x.Item }, new[] { y.Item! });
                Same(at, x.Fields, y.Fields);
            }
        }

        // A list default is a new list each time; what matters is that both are empty or both not.
        // A class without its own Equals (a sampler description) is compared by what it holds.
        static object? Normal(object? value) => value switch
        {
            System.Collections.ICollection c => c.Count,
            null or string or ValueType => value,
            _ => System.Text.Json.JsonSerializer.Serialize(value, value.GetType(), new System.Text.Json.JsonSerializerOptions { IncludeFields = true }),
        };
    }

    // The generated setters write a boxed struct in place — what the inspector edits a component with,
    // without reflection.
    [Fact]
    public void AGeneratedSetterWritesABoxedComponentInPlace()
    {
        var meta = Metadata.Of(typeof(PointLight));
        Assert.True(meta.Generated);
        object boxed = new PointLight { Range = 3f };

        meta.Field("range")!.Set!(boxed, 12f);
        meta.Field("colour")!.Set!(boxed, new Vector3(1, 0, 0));

        var light = (PointLight)boxed;
        Assert.Equal(12f, light.Range);
        Assert.Equal(new Vector3(1, 0, 0), light.Colour);
        Assert.Equal(12f, meta.Field("range")!.Get!(boxed));
    }

    // The dev fallback: this assembly is built without the generator, so its declarations are read by
    // reflection over the same attributes — with the same answers.
    [Fact]
    public void AnAssemblyBuiltWithoutTheGeneratorIsReadByReflection()
    {
        var meta = Metadata.Of(typeof(MetadataProbe));

        Assert.False(meta.Generated);
        Assert.Equal((DeclarationKind.Component, "test:metadata_probe"), (meta.Kind, meta.Id));
        var speed = meta.Field("speed")!;
        Assert.Equal((2d, 9d, "m/s", "How fast"), (speed.Min, speed.Max, speed.Unit, speed.Tooltip));
        Assert.Equal("item", meta.Field("drops")!.Item!.RecordType);
        Assert.True(meta.Field("scratch")!.Transient);

        object boxed = new MetadataProbe();
        speed.Set!(boxed, 4f);
        Assert.Equal(4f, ((MetadataProbe)boxed).Speed);
    }

    // `ent_dump` prints what is not at its default, by JSON name and with its unit; `ent_types` lists a
    // part's fields with their types, units, ranges and references — both from the table.
    [Fact]
    public void TheConsoleDescribesFieldsFromTheTable()
    {
        using var capture = new CaptureSink();
        using var app = HeadlessApp.Gameplay()
            .File("data/lamp.json", """[{ "type": "prefab", "id": "metadata_lamp", "parts": { "light": { "range": 12 } } }]""")
            .Boot();
        app.World.Spawn(new RecordId("sage", "metadata_lamp"));

        app.CVars.Execute("ent_dump metadata_lamp");
        app.CVars.Execute("ent_types light");

        var lines = capture.Entries.Select(e => e.Message).ToList();
        Assert.Contains(lines, l => l.Contains("sage:point_light") && l.Contains("range=12.000 m") && l.Contains("intensity=1.000"));
        Assert.Contains(lines, l => l.Contains("light") && l.Contains("{ colour: Vector3 0.., range: float m 0.., intensity: float 0.. }"));
    }

    // ---- the generator ---------------------------------------------------------------------------------

    [Fact]
    public void TheGeneratorWritesEachFieldWithItsAttributes()
    {
        var (output, diagnostics) = Generate("""
            using sage_engine;
            using Friflo.Engine.ECS;
            using System.Collections.Generic;
            public enum Mood { Calm, Angry }
            [Component("game:health")]
            public struct Health : IComponent
            {
                [Property(Min = 0, Unit = "hp", Tooltip = "Current hit points", Category = "Vitals")] public float Value;
                public Mood Mood;
                [Transient] public float LastHit;
            }
            [Plugin("game", "1.0.0")] public sealed class Game : IModule { public void Init(ModuleContext ctx) { } }
            [Record("loot")] public sealed class LootRecord { [RecordRef("item")] public List<RecordId> Items = new(); public int Rolls { get; set; } = 1; }
            [Record("chest")] public sealed class ChestRecord { public RecordRef<LootRecord> Loot; }
            """);

        Assert.Empty(diagnostics);
        Assert.Contains("[assembly: global::sage_engine.GeneratedMetadataAttribute(typeof(global::Sage.Generated.SageMetadata_Declarations))]", output);
        Assert.Contains("new(global::sage_engine.DeclarationKind.Component, \"game:health\", typeof(global::Health), static () => new global::Health(),", output);
        Assert.Contains("{ JsonName = \"value\", Min = 0d, Unit = \"hp\", Tooltip = \"Current hit points\", Category = \"Vitals\" }", output);
        Assert.Contains("global::System.Runtime.CompilerServices.Unsafe.Unbox<global::Health>(o).Value = (float)v!", output);
        Assert.Contains("{ JsonName = \"mood\", EnumValues = new string[] { \"Calm\", \"Angry\" } }", output);
        Assert.Contains("{ JsonName = \"lastHit\", Transient = true }", output);
        Assert.Contains("{ JsonName = \"item\", RecordType = \"item\" }", output);
        Assert.Contains("new global::sage_engine.FieldMetadata(\"Rolls\", typeof(int), global::sage_engine.ValueKind.Integer", output);
        Assert.Contains("{ JsonName = \"loot\", RecordType = \"loot\" }", output);   // RecordRef<LootRecord>
    }

    [Theory]
    [InlineData("[Property(Min = 0)] public string Name;", "SAGE0040", "Health.Name: [Property(Min/Max)] is for a number or a vector, and this is string")]
    [InlineData("[Property(Min = 5, Max = 1)] public float Value;", "SAGE0040", "Health.Value: [Property] has Min 5 above Max 1")]
    [InlineData("[RecordRef(\"item\")] public float Value;", "SAGE0041", "Health.Value: [RecordRef] names the record type a RecordId points at, and this is float")]
    [InlineData("[RecordRef(\"\")] public RecordId Value;", "SAGE0041", "[RecordRef] needs a record type")]
    [InlineData("[RecordRef(\"item\")] public RecordRef<LootRecord> Value;", "SAGE0041", "Health.Value: RecordRef<LootRecord> already names its record type")]
    [InlineData("[AssetKind(\"texture\")] public RecordId Value;", "SAGE0042", "Health.Value: [AssetKind] names the kind of asset an AssetPath points at, and this is RecordId")]
    public void AnAttributeOnTheWrongKindOfFieldIsABuildError(string field, string id, string message)
    {
        var (_, diagnostics) = Generate($$"""
            using sage_engine;
            using Friflo.Engine.ECS;
            [Component("game:health")] public struct Health : IComponent { {{field}} }
            [Record("loot")] public sealed class LootRecord { }
            """);

        var d = Assert.Single(diagnostics);
        Assert.Equal(id, d.Id);
        Assert.Contains(message, d.GetMessage());
    }

    private static (string Output, ImmutableArray<Diagnostic> Diagnostics) Generate(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(Engine).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(IComponent).Assembly.Location));
        var compilation = CSharpCompilation.Create("Declarations",
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MetadataGenerator());
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

// A component in an assembly built without the generator (this one): read by reflection.
[Component("test:metadata_probe")]
public struct MetadataProbe : IComponent
{
    [Property(Min = 2, Max = 9, Unit = "m/s", Tooltip = "How fast")] public float Speed;
    [RecordRef("item")] public List<RecordId> Drops;
    [Transient] public float Scratch;
}
