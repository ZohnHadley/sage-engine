#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Tests;

using Assert = Xunit.Assert;

// JSON Schemas for record files (issue #21). VS Code cannot run here, so what it would show is proved
// the way it computes it: every shipped data file is validated against the committed `schemas/` (the
// files `.vscode/settings.json` maps onto `data/**/*.json`) by SchemaValidator, a misspelt field in a
// Sandbox prefab fails, and the descriptions VS Code shows on hover carry each field's tooltip and unit.
public class SchemaTests
{
    public SchemaTests() { _ = TestEnv.UserRoot; }

    private static string Repo => TestEnv.FolderAbove("Sage.sln");
    private static string Committed => Path.Combine(Repo, "schemas");

    private static SchemaValidator Validator() => new(Committed);

    // What `sage schema games/Sandbox games/Hello` writes, less the client halves' parts: the tests
    // cannot load Sage.Client (it is MonoGame), so its `audio` and `particles` and the Sandbox's
    // `box_mesh` are named but not described here.
    private static SortedDictionary<string, string> Generate(params (string Directory, string Namespace)[] mounts)
    {
        var catalog = new SchemaCatalog();
        foreach (var (game, module) in new (string, IGameModule)[] { ("Sandbox", new Sandbox.SandboxModule()), ("Hello", new Hello.HelloModule()) })
        {
            var report = ContentValidation.Run(new ValidateOptions
            {
                GameDirectory = Path.Combine(Repo, "games", game),
                EngineContentDirectory = Path.Combine(Repo, "engine_content"),
                AvailablePlugins = BasePlugins.All(),
                GameModule = module,
                Mounts = mounts,
                Inspect = catalog.Add,
            });
            Assert.True(report.Ok, $"{game}: " + string.Join("\n", report.Errors));
        }
        return RecordSchemas.Write(catalog);
    }

    private static readonly Lazy<SortedDictionary<string, string>> Generated = new(() => Generate());

    private static IEnumerable<string> ShippedDataFiles() =>
        new[] { Path.Combine(Repo, "engine_content", "data") }
            .Concat(Directory.GetDirectories(Path.Combine(Repo, "games")).Select(g => Path.Combine(g, "content", "data")))
            .Concat(Directory.GetDirectories(Path.Combine(Repo, "tests", "games")).Select(g => Path.Combine(g, "content", "data")))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal);

    // ---- the acceptance: shipped files pass, mistakes fail ------------------------------------------

    [Fact]
    public void EveryShippedDataFile_ValidatesAgainstTheCommittedSchemas()
    {
        var validator = Validator();
        var files = ShippedDataFiles().ToList();
        Assert.True(files.Count >= 10, $"found only {files.Count} data files");
        foreach (string file in files)
        {
            var errors = validator.Validate(SchemaValidator.ReadData(file));
            Assert.True(errors.Count == 0, $"{Path.GetRelativePath(Repo, file)}:\n" + string.Join("\n", errors));
        }
    }

    [Fact]
    public void AMisspeltComponentFieldInASandboxPrefab_FailsTheSchema()
    {
        var scene = SchemaValidator.ReadData(Path.Combine(Repo, "games", "Sandbox", "content", "data", "scene.json"))!.AsArray();
        var validator = Validator();
        Assert.Empty(validator.Validate(scene));

        // The first prefab that writes a component field: `"ai_state": { "schedule": … }`, misspelt.
        int index = scene.Select((r, i) => (r, i)).First(x => (string?)x.r!["type"] == "prefab"
            && x.r!["components"] is JsonObject c && c.Any(kv => kv.Value is JsonObject { Count: > 0 })).i;
        var components = scene[index]!["components"]!.AsObject();
        var (component, body) = components.First(kv => kv.Value is JsonObject { Count: > 0 });
        var fields = body!.AsObject();
        string field = fields.First().Key;
        string typo = field[..^1];
        var value = fields[field]!.DeepClone();
        fields.Remove(field);
        fields[typo] = value;

        var errors = validator.Validate(scene);
        Assert.Contains($"$[{index}].components.{component}.{typo}: \"{typo}\" is not a field here", errors);
    }

    [Fact]
    public void MistakesInPartsReferencesAndTypes_FailTheSchema()
    {
        var validator = Validator();
        List<string> Errors(string json) => validator.Validate(JsonNode.Parse(json));

        // A part's option misspelt; a part that does not exist.
        Assert.Contains("$.parts.light.rnage: \"rnage\" is not a field here",
                        Errors("""{ "type": "prefab", "id": "lamp", "parts": { "light": { "rnage": 4 } } }"""));
        Assert.Contains("$.parts.lihgt: \"lihgt\" is not a field here",
                        Errors("""{ "type": "prefab", "id": "lamp", "parts": { "lihgt": {} } }"""));
        // A reference to a sound nothing defines; a base that is not a prefab; an unknown record type.
        Assert.Contains(Errors("""{ "type": "item", "id": "rock", "sound": "no_such_sound" }"""), e => e.StartsWith("$.sound: \"no_such_sound\" is not one of"));
        Assert.Contains(Errors("""{ "type": "prefab", "id": "p", "base": "sage:idle" }"""), e => e.StartsWith("$.base: \"sage:idle\" is not one of"));
        Assert.Contains(Errors("""{ "type": "prefabb", "id": "p" }"""), e => e.StartsWith("$.type: \"prefabb\" is not one of"));
        Assert.Contains("$: \"id\" is required", Errors("""{ "type": "prefab" }"""));
        // A value of the wrong shape, and one out of range.
        Assert.Contains("$.components.point_light.range: expected number, not string",
                        Errors("""{ "type": "prefab", "id": "p", "components": { "point_light": { "range": "far" } } }"""));
        Assert.Contains("$.components.point_light.range: -1 is below the minimum 0",
                        Errors("""{ "type": "prefab", "id": "p", "components": { "point_light": { "range": -1 } } }"""));
        // A component that does not exist, by id or bare name.
        Assert.Contains("$.components.sage:pointlight: \"sage:pointlight\" is not a field here",
                        Errors("""{ "type": "prefab", "id": "p", "components": { "sage:pointlight": {} } }"""));
    }

    [Fact]
    public void WhatRecordFilesMayWrite_PassesTheSchema()
    {
        var validator = Validator();
        void Valid(string json) => Assert.Empty(validator.Validate(JsonNode.Parse(json)));

        // One record or an array of them; each chosen by its type.
        Valid("""{ "type": "prefab", "id": "p" }""");
        Valid("""[ { "type": "prefab", "id": "p" }, { "type": "item", "id": "i", "sound": "sandbox:swing" } ]""");
        // A component by full id and by bare name, the game's own and the engine's; a tag likewise.
        Valid("""{ "type": "prefab", "id": "p", "components": { "sage:point_light": { "range": 4 }, "hop": {} }, "tags": ["faces_camera"] }""");
        Valid("""{ "type": "prefab", "id": "p", "components": { "sandbox:hop": {} }, "tags": ["sandbox:faces_camera"] }""");
        // A patch, an abstract base, list operators on a record and inside a component.
        Valid("""{ "type": "prefab", "id": "sandbox:fighter", "patch": true, "base": "creature", "tags+": ["faces_camera"], "tags-": [] }""");
        Valid("""{ "type": "prefab", "id": "t", "abstract": true, "disabled": false, "$schema": "../../schemas/record.schema.json" }""");
        // A part written as its shorthand; a colour in each of its spellings; AI tasks both ways.
        Valid("""{ "type": "prefab", "id": "p", "parts": { "faction": "sage:beasts" } }""".Replace("sage:beasts", FirstId("faction")));
        Valid("""[ { "type": "damage_type", "id": "a", "colour": "#FFB0A0" }, { "type": "damage_type", "id": "b", "colour": [255, 176, 160, 128] } ]""");
        Valid("""{ "type": "ai_schedule", "id": "s", "tasks": ["FaceTarget", { "task": "Wait", "seconds": 1.5 }] }""");

        Assert.NotEmpty(validator.Validate(JsonNode.Parse("""{ "type": "damage_type", "id": "a", "colour": "red" }""")));
        Assert.NotEmpty(validator.Validate(JsonNode.Parse("""{ "type": "ai_schedule", "id": "s", "tasks": ["Wait:1.5"] }""")));
    }

    private static string FirstId(string type) =>
        (string)Validator().Load(RecordSchemas.Ids)["definitions"]![$"record:{type}"]!["enum"]!.AsArray().First()!;

    [Fact]
    public void TheWorkspaceSettings_MapTheRootSchemaOntoEveryDataFile_AsJsonWithComments()
    {
        var settings = SchemaValidator.ReadData(Path.Combine(Repo, ".vscode", "settings.json"))!;
        Assert.Equal("jsonc", (string?)settings["files.associations"]!["**/data/**/*.json"]);
        var mapping = settings["json.schemas"]!.AsArray().Single(m => (string?)m!["url"] == "./schemas/record.schema.json")!;
        Assert.Contains("**/data/**/*.json", mapping["fileMatch"]!.AsArray().Select(n => (string?)n));
        Assert.True(File.Exists(Path.Combine(Repo, "schemas", RecordSchemas.Root)));
    }

    // ---- what a hover shows ---------------------------------------------------------------------------

    [Fact]
    public void HoveringAComponentField_ShowsItsTooltipUnitAndRange()
    {
        var components = Validator().Load(RecordSchemas.Components)["definitions"]!;
        var range = components["sage:point_light"]!["properties"]!["range"]!;
        Assert.Equal("Where the light fades to nothing. Unit: m. At least 0. (float, default 0)", (string?)range["description"]);
        Assert.Equal(0, (double)range["minimum"]!);

        // Every field of every component that has a tooltip or a unit says so on hover.
        int checkedFields = 0;
        foreach (var meta in EngineAssemblies.Base.Append(typeof(Sandbox.SandboxModule).Assembly).SelectMany(a => Metadata.In(a).Values)
                                     .Where(m => m.Kind == DeclarationKind.Component))
            foreach (var field in meta.Fields.Where(f => f.Tooltip != null || f.Unit != null))
            {
                string description = (string)components[meta.Id]!["properties"]![field.JsonName]!["description"]!;
                if (field.Tooltip != null) Assert.Contains(field.Tooltip.TrimEnd('.'), description);
                if (field.Unit != null) Assert.Contains($"Unit: {field.Unit}.", description);
                checkedFields++;
            }
        Assert.True(checkedFields > 20, $"only {checkedFields} described fields");
    }

    [Fact]
    public void APrefabPartsOptions_AreDescribedFromItsDeclaration()
    {
        var parts = Validator().Load(RecordSchemas.Parts);
        // The Sandbox's client part, read from its assembly by `sage schema`, not loaded.
        var size = parts["definitions"]!["box_mesh"]!["properties"]!["size"]!;
        Assert.StartsWith("Full extents of the box. Unit: m.", (string?)size["description"]);
        Assert.Equal(3, (int)size["minItems"]!);
        var material = parts["definitions"]!["box_mesh"]!["properties"]!["material"]!;
        Assert.Equal("ids.schema.json#/definitions/record:material", (string?)material["allOf"]![0]!["$ref"]);
    }

    // ---- the files themselves ---------------------------------------------------------------------------

    [Fact]
    public void TheSchemas_UseOnlyWhatTheValidatorChecks_AndEveryRefResolves()
    {
        var problems = Validator().CheckVocabulary();
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    public void TheCommittedSchemas_AreWhatSageSchemaWrites()
    {
        var generated = Generated.Value;
        var committed = Directory.GetFiles(Committed, "*.schema.json").Select(p => Path.GetFileName(p)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(generated.Keys.ToList(), committed);
        foreach (var (name, text) in generated)
        {
            Assert.DoesNotContain("\r", text);
            string disk = File.ReadAllText(Path.Combine(Committed, name));
            if (name != RecordSchemas.Parts)
            {
                Assert.True(disk == text, $"schemas/{name} is stale: run `sage schema games/Sandbox games/Hello --out schemas` (README)");
                continue;
            }
            // The parts file differs only by the client parts this process cannot read.
            var mine = JsonNode.Parse(text)!;
            var theirs = JsonNode.Parse(disk)!;
            foreach (var (part, schema) in mine["definitions"]!.AsObject())
                Assert.True(JsonNode.DeepEquals(schema, theirs["definitions"]![part]), $"schemas/{name}: part {part} is stale");
            Assert.Equal(theirs["properties"]!.AsObject().Select(p => p.Key), mine["properties"]!.AsObject().Select(p => p.Key));
        }
    }

    [Fact]
    public void IdEnumsComeFromTheLoadedContent_ModsIncluded()
    {
        var mod = TestEnv.NewTempDir();
        Directory.CreateDirectory(Path.Combine(mod, "data"));
        File.WriteAllText(Path.Combine(mod, "data", "mod.json"), """
            [ { "type": "prefab", "id": "barrel", "abstract": true }, { "type": "prefab", "id": "red_barrel", "base": "barrel" } ]
            """);
        var files = Generate((mod, "barrelmod"));
        var ids = JsonNode.Parse(files[RecordSchemas.Ids])!["definitions"]!;
        var prefabs = ids["record:prefab"]!["enum"]!.AsArray().Select(n => (string)n!).ToList();
        var bases = ids["base:prefab"]!["enum"]!.AsArray().Select(n => (string)n!).ToList();

        Assert.Contains("barrelmod:red_barrel", prefabs);
        Assert.Contains("red_barrel", prefabs);            // the bare name, as a file in that namespace writes it
        Assert.DoesNotContain("barrelmod:barrel", prefabs); // abstract: a base, never a thing to spawn
        Assert.Contains("barrelmod:barrel", bases);
        Assert.Contains("sandbox:creature", bases);
        Assert.Equal(prefabs.OrderBy(p => p, StringComparer.Ordinal), prefabs);

        // The same content writes the same bytes: the committed copy can be checked with a diff.
        Assert.Equal(files, Generate((mod, "barrelmod")));
    }
}
