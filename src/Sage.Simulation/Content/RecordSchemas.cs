#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sage.Simulation;

// JSON Schemas for record files (REDESIGN §3.4 item 3, §4.2; issue #21): what makes VS Code underline
// `"rnage"` in a prefab's `point_light` and show "Where the light fades to nothing. Unit: m." over
// `"range"` before the game ever runs. Written from the metadata table (Metadata), so a field's
// schema says what its [Property] says, and from the loaded content, so an id field offers the ids
// that exist. `sage schema <game> [--out dir]` writes them (src/Sage.Cli); the repository keeps the
// Sandbox's and Hello's in `schemas/`, which `.vscode/settings.json` maps onto every `data/` file.
//
//   record.schema.json            the root: a file is one record or an array of them, each chosen by
//                                 its "type" (if/then) and checked against that type's schema
//   <type>.schema.json            one per record type: its fields, plus "type", "id", "base",
//                                 "patch", "abstract", "disabled" and "field+" / "field-" on lists
//   prefab-components.schema.json a prefab's "components": every component by id, and by the bare
//                                 name that resolves to it (this file's namespace, then `sage`)
//   prefab-parts.schema.json      a prefab's "parts": every declared part by id, with its options
//   ids.schema.json               the ids content has loaded, per record type: "record:<type>" (what a
//                                 reference may name) and "base:<type>" (abstract ones too), and the
//                                 tag ids; regenerate after adding a record
//   mod.schema.json               a mod's mod.json (phase 4j); game.schema.json, a game's game.json
//   vocabularies.schema.json      the open vocabularies (issue #28): "<name>" is an entry — its id, or
//                                 an object naming it under the vocabulary's key with its settings —
//                                 and "<name>:id" the registered ids, which a [VocabularyRef] field names
//
// JSON Schema draft-07, which every editor reads. `$ref` never has siblings (draft-07 ignores them):
// a field that refers elsewhere and has a description says `allOf: [{ $ref }]`. The output is
// deterministic — sorted where the order is not the declaration's, `\n` line endings everywhere — so a
// committed copy can be checked for staleness (CI regenerates it and runs `git diff --exit-code`).
//
// What a schema cannot say: names are matched ignoring case at load but exactly here (content is
// written in camel case, as the metadata's JsonName has it); a bare id's meaning depends on the file
// it is in, so an id enum holds every full id and every bare name; and a part only a client plugin
// declares is described only when its assembly could be read (`AddAssemblyFile`), otherwise any
// options are accepted and the running game checks them.
public static class RecordSchemas
{
    public const string Draft = "http://json-schema.org/draft-07/schema#";
    public const string Root = "record.schema.json";
    public const string Ids = "ids.schema.json";
    public const string Components = "prefab-components.schema.json";
    public const string Parts = "prefab-parts.schema.json";
    public const string Vocabularies = "vocabularies.schema.json";
    public const string Mod = "mod.schema.json";
    public const string Game = "game.schema.json";

    // A record id as content writes it: `name` or `namespace:name` (RecordId.Parse).
    public const string IdPattern = "^([a-z0-9_.-]+:)?[a-z0-9_.-]+$";

    // Keys every record may have, whatever its type (RecordStore.MetaKeys).
    private static readonly string[] MetaKeys = { "$schema", "type", "id", "base", "patch", "abstract", "disabled" };

    public static string FileOf(string recordType) => recordType + ".schema.json";

    // Every schema file, by file name, as text.
    public static SortedDictionary<string, string> Write(SchemaCatalog catalog)
    {
        foreach (string name in catalog.RecordTypes.Keys)
            if (FileOf(name) is Root or Ids or Components or Parts or Vocabularies or Mod or Game)
                throw new InvalidOperationException($"record type '{name}' would overwrite {FileOf(name)}; rename it");

        var writer = new Writer(catalog);
        var files = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal)
        {
            [Root] = writer.RootSchema(),
            [Ids] = writer.IdsSchema(),
            [Components] = writer.ComponentsSchema(),
            [Parts] = writer.PartsSchema(),
            [Vocabularies] = writer.VocabulariesSchema(),
            [Mod] = ManifestSchemas.ModSchema(),
            [Game] = ManifestSchemas.GameSchema(),
        };
        foreach (var (name, type) in catalog.RecordTypes) files[FileOf(name)] = writer.RecordSchema(name, type);

        var text = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, schema) in files) text[name] = Format(schema);
        return text;
    }

    // Writes every file into `directory` and removes any other `*.schema.json` there (a record type
    // that is gone). Returns the files written.
    public static IReadOnlyList<string> WriteTo(SchemaCatalog catalog, string directory)
    {
        Directory.CreateDirectory(directory);
        var files = Write(catalog);
        foreach (string stale in Directory.GetFiles(directory, "*.schema.json"))
            if (!files.ContainsKey(Path.GetFileName(stale))) File.Delete(stale);
        var written = new List<string>();
        foreach (var (name, text) in files)
        {
            string path = Path.Combine(directory, name);
            // Byte for byte: File.WriteAllText would add nothing, but a UTF-8 BOM or a platform newline
            // would make the committed copy differ on another machine.
            File.WriteAllBytes(path, new System.Text.UTF8Encoding(false).GetBytes(text));
            written.Add(path);
        }
        return written;
    }

    // Indented, relaxed escaping (a tooltip's apostrophe reads as written), `\n` line endings: the
    // .NET 8 writer indents with Environment.NewLine, which is `\r\n` on Windows.
    public static string Format(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).Replace("\r\n", "\n") + "\n";

    // What a field is, for a person reading a tooltip: what it does, its unit and range, what it names
    // and its C# type and default.
    public static string Describe(FieldMetadata field, JsonNode? defaultValue = null)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(field.Tooltip)) parts.Add(Sentence(field.Tooltip!));
        if (!string.IsNullOrWhiteSpace(field.Unit)) parts.Add($"Unit: {field.Unit}.");
        if (field.Min != null && field.Max != null) parts.Add($"Range: {Number(field.Min.Value)} to {Number(field.Max.Value)}.");
        else if (field.Min != null) parts.Add($"At least {Number(field.Min.Value)}.");
        else if (field.Max != null) parts.Add($"At most {Number(field.Max.Value)}.");
        string? target = field.RecordType ?? field.Item?.RecordType;
        if (!string.IsNullOrEmpty(target)) parts.Add($"Names a {target} record.");
        string? asset = field.AssetKind ?? field.Item?.AssetKind;
        if (!string.IsNullOrEmpty(asset)) parts.Add($"A {asset} path.");
        if (!string.IsNullOrWhiteSpace(field.Category)) parts.Add($"Category: {field.Category}.");
        if (field.Transient) parts.Add("Not saved.");
        parts.Add(defaultValue != null ? $"({field.TypeName}, default {Compact(defaultValue)})" : $"({field.TypeName})");
        return string.Join(" ", parts);
    }

    private static string Sentence(string text)
    {
        text = text.Trim();
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') || text.EndsWith(')') ? text : text + ".";
    }

    private static string Number(double value) => JsonValue.Create(value)!.ToJsonString();

    private static string Compact(JsonNode node) => node.ToJsonString(new JsonSerializerOptions
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonObject Ref(string reference) => new() { ["$ref"] = reference };

    // `{ allOf: [{ $ref }] }`, so that the description beside it is read (draft-07 ignores $ref's siblings).
    private static JsonObject RefWith(string reference, string? description)
    {
        var o = new JsonObject();
        if (description != null) o["description"] = description;
        o["allOf"] = new JsonArray(Ref(reference));
        return o;
    }

    // ids.schema.json's definitions: "record:sound" (the sounds), "base:sound" (sounds and abstract ones).
    private static string RecordKey(string type) => "record:" + type;
    private static string BaseKey(string type) => "base:" + type;

    private static string Pointer(string key) => key.Replace("~", "~0").Replace("/", "~1");

    private sealed class Writer
    {
        private const int MaxDepth = 8;
        private readonly SchemaCatalog _catalog;

        public Writer(SchemaCatalog catalog) { _catalog = catalog; }

        // ---- the files ----------------------------------------------------------------------------

        public JsonObject RootSchema()
        {
            var types = _catalog.RecordTypes.Keys.ToList();
            var cases = new JsonArray();
            foreach (string type in types)
                cases.Add(new JsonObject
                {
                    ["if"] = new JsonObject
                    {
                        ["properties"] = new JsonObject { ["type"] = new JsonObject { ["const"] = type } },
                        ["required"] = Strings(new[] { "type" }),
                    },
                    ["then"] = Ref(FileOf(type)),
                });

            return new JsonObject
            {
                ["$schema"] = Draft,
                ["title"] = "Sage record file",
                ["description"] = "A record file (data/**/*.json): one record, or an array of records. Each is checked " +
                                  "against the schema of its \"type\". Generated by `sage schema` (issue #21).",
                ["oneOf"] = new JsonArray(
                    Ref("#/definitions/record"),
                    new JsonObject { ["type"] = "array", ["items"] = Ref("#/definitions/record") }),
                ["definitions"] = new JsonObject
                {
                    ["record"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["required"] = Strings(new[] { "type", "id" }),
                        ["properties"] = new JsonObject
                        {
                            ["type"] = new JsonObject
                            {
                                ["description"] = "The record type: which schema the rest of the record follows.",
                                ["enum"] = Strings(types),
                            },
                        },
                        ["allOf"] = cases,
                    },
                },
            };
        }

        public JsonObject IdsSchema()
        {
            var definitions = new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["description"] = "A record id: name, or namespace:name. Lower-case letters, digits, _ . and -.",
                    ["type"] = "string",
                    ["pattern"] = IdPattern,
                },
            };
            // "record:<type>": what a reference may name; "base:<type>": what "base" may, templates too.
            foreach (string type in _catalog.RecordTypes.Keys)
                definitions[RecordKey(type)] = IdEnum(_catalog.IdsOf(type), $"The id of a {type} record: one of those loaded, in full " +
                                                      "or bare (a bare id is the namespace of the file it is written in).", type);
            foreach (string type in _catalog.RecordTypes.Keys)
                definitions[BaseKey(type)] = IdEnum(_catalog.IdsOf(type).Concat(_catalog.TemplatesOf(type)).ToList(),
                                                    $"Another {type} record to inherit every field from, abstract or not.", type);
            definitions["ecs_tag"] = new JsonObject
            {
                ["description"] = "A tag by id, or by the bare name that resolves to it (this file's namespace, then sage).",
                ["type"] = "string",
                ["enum"] = Strings(WithBareNames(_catalog.Tags.Keys)),
            };
            return new JsonObject
            {
                ["$schema"] = Draft,
                ["title"] = "Sage ids",
                ["description"] = "The ids the loaded content defines, by record type, and the component tag ids. Generated by " +
                                  "`sage schema` from the content it loaded (mods included); regenerate after adding a record.",
                ["definitions"] = definitions,
            };
        }

        // Every vocabulary the engines registered: "<name>" (an entry, bare or as an object) and
        // "<name>:id" (the ids, as registered and in the other common spelling: `kill` and `Kill`).
        public JsonObject VocabulariesSchema()
        {
            var definitions = new JsonObject();
            foreach (var (name, vocabulary) in _catalog.Vocabularies)
            {
                var ids = vocabulary.Entries.Keys.ToList();
                definitions[name + ":id"] = new JsonObject
                {
                    ["description"] = ids.Count == 0 ? $"{A(name)} by id (none is registered)." : $"{A(name)} by id: {string.Join(", ", ids)}.",
                    ["type"] = "string",
                    ["enum"] = Strings(ids.SelectMany(Spellings).Distinct().OrderBy(i => i, StringComparer.Ordinal)),
                };

                // `{ "<id>": value, …settings }` (issue #89): one more way to write each entry.
                bool shorthand = vocabulary.Default == null &&
                                 vocabulary.EntryType.GetCustomAttribute<VocabularyAttribute>(false)?.Shorthand == true;
                var shorthands = new JsonArray();

                var cases = new JsonArray();
                foreach (var (id, type) in vocabulary.Entries)
                {
                    var meta = Metadata.Of(type);
                    var body = ObjectSchema(type, meta.Fields, meta, 1);
                    if (shorthand) foreach (var form in ShorthandSchemas(name, id, type, body)) shorthands.Add(form);
                    var properties = body["properties"] as JsonObject ?? new JsonObject();
                    properties[vocabulary.Key] = new JsonObject { ["description"] = $"The {name}: {id}.", ["enum"] = Strings(Spellings(id)) };
                    body["properties"] = Sorted(properties);
                    body["additionalProperties"] = false;
                    body["description"] = $"{name} '{id}' ({type.Name}).";
                    var when = new JsonObject
                    {
                        ["properties"] = new JsonObject { [vocabulary.Key] = new JsonObject { ["enum"] = Strings(Spellings(id)) } },
                        ["required"] = Strings(new[] { vocabulary.Key }),
                    };
                    cases.Add(new JsonObject { ["if"] = when, ["then"] = body });
                    if (vocabulary.Default != null && Vocabulary.Normalize(vocabulary.Default) == Vocabulary.Normalize(id))
                        cases.Add(new JsonObject   // no key: the default entry
                        {
                            ["if"] = new JsonObject { ["required"] = Strings(new[] { vocabulary.Key }) },
                            ["else"] = body.DeepClone(),
                        });
                }

                var entry = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject { [vocabulary.Key] = Ref($"#/definitions/{Pointer(name + ":id")}") },
                };
                if (vocabulary.Default == null) entry["required"] = Strings(new[] { vocabulary.Key });
                if (cases.Count > 0) entry["allOf"] = cases;
                var forms = new JsonArray(Ref($"#/definitions/{Pointer(name + ":id")}"), entry);
                foreach (var form in shorthands.ToList())
                {
                    shorthands.Remove(form);
                    forms.Add(form);
                }
                definitions[name] = new JsonObject
                {
                    ["description"] = $"{A(name)}: its id, or {{ \"{vocabulary.Key}\": \"<id>\", … }} with its settings" +
                                      (shorthand ? ", or { \"<id>\": <value>, … }" : "") +
                                      (vocabulary.Default != null ? $" (no \"{vocabulary.Key}\" means {vocabulary.Default})" : "") + ".",
                    ["anyOf"] = forms,
                };
            }
            return new JsonObject
            {
                ["$schema"] = Draft,
                ["title"] = "Sage vocabularies",
                ["description"] = "The open vocabularies (issue #28) and the entries plugins registered: AI conditions, quest " +
                                  "objectives, dialogue conditions and actions, ability deliveries, effect executions, item uses. " +
                                  "Generated by `sage schema`.",
                ["definitions"] = definitions,
            };
        }

        // An entry in shorthand, once per spelling of its id: the id as a property, whose value is the
        // entry's [EntryValue] field (or its settings, as an object, when that field is not itself a
        // vocabulary or there is none), beside the rest of its settings (Vocabulary.Expand).
        private static IEnumerable<JsonObject> ShorthandSchemas(string vocabulary, string id, Type type, JsonObject body)
        {
            var fields = body["properties"] as JsonObject ?? new JsonObject();
            var settings = new JsonObject { ["type"] = "object", ["properties"] = fields.DeepClone(), ["additionalProperties"] = false };
            JsonNode value;
            if (Vocabulary.ValueFieldName(type) is { } member && fields[JsonMembers.JsonName(member)] is { } field)
            {
                var memberType = type.GetField(member)?.FieldType ?? type.GetProperty(member)?.PropertyType;
                value = memberType != null && Sage.Core.Vocabularies.IsVocabulary(memberType)
                    ? field.DeepClone()
                    : new JsonObject { ["anyOf"] = new JsonArray(field.DeepClone(), settings) };
            }
            else
            {
                value = new JsonObject { ["anyOf"] = new JsonArray(settings, new JsonObject { ["const"] = true }) };
            }

            foreach (var spelling in Spellings(id))
            {
                var properties = (JsonObject)fields.DeepClone();
                var described = (JsonObject)value.DeepClone();
                described["description"] = $"{vocabulary} '{id}' ({type.Name}), in shorthand.";
                properties[spelling] = described;
                yield return new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = Sorted(properties),
                    ["required"] = Strings(new[] { spelling }),
                    ["additionalProperties"] = false,
                };
            }
        }

        private static string A(string noun) => ("aeiou".Contains(noun[0]) ? "An " : "A ") + noun;

        // An id as registered, and in the other common spelling: snake_case for PascalCase and back.
        private static IEnumerable<string> Spellings(string id)
        {
            yield return id;
            if (id.Contains(':')) yield break;
            if (id.Any(char.IsUpper))
            {
                var snake = new System.Text.StringBuilder();
                for (int i = 0; i < id.Length; i++)
                {
                    if (char.IsUpper(id[i]) && i > 0) snake.Append('_');
                    snake.Append(char.ToLowerInvariant(id[i]));
                }
                if (snake.ToString() != id) yield return snake.ToString();
            }
            else
            {
                string pascal = string.Concat(id.Split('_', '-').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
                if (pascal != id) yield return pascal;
            }
        }

        public JsonObject ComponentsSchema()
        {
            var properties = new JsonObject();
            var definitions = new JsonObject();
            foreach (var (id, meta) in _catalog.Components)
            {
                var body = ObjectSchema(meta.Type, meta.Fields, meta, 0);
                body["description"] = $"Component {id} ({meta.Type.Name}).";
                definitions[id] = body;
            }
            foreach (var (key, ids) in Keys(_catalog.Components.Keys))
                properties[key] = Choice(ids, id => $"#/definitions/{Pointer(id)}", ids.Count == 1 && ids[0] == key
                    ? $"Component {key} ({_catalog.Components[key].Type.Name})."
                    : BareDescription("component", key, ids, id => _catalog.Components[id].Type.Name));

            return new JsonObject
            {
                ["$schema"] = Draft,
                ["title"] = "Sage prefab components",
                ["description"] = "A prefab's \"components\": component data by id. A bare name is this file's namespace, then sage.",
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
                ["definitions"] = definitions,
            };
        }

        public JsonObject PartsSchema()
        {
            var properties = new JsonObject();
            var definitions = new JsonObject();
            foreach (var (id, part) in _catalog.Parts)
            {
                var body = ObjectSchema(part.Meta.Type, part.Meta.Fields, part.Meta, 0);
                string description = $"Prefab part '{id}' ({part.Meta.Type.Name}).";
                body["description"] = description;
                definitions[id] = body;

                // A bare value fills the part's shorthand field: "faction": "beasts".
                var field = part.Shorthand == null ? null : part.Meta.Field(part.Shorthand);
                properties[id] = field == null
                    ? RefWith($"#/definitions/{Pointer(id)}", description)
                    : new JsonObject
                    {
                        ["description"] = description + $" A bare value is its \"{field.JsonName}\".",
                        ["anyOf"] = new JsonArray(Ref($"#/definitions/{Pointer(id)}"), FieldSchema(field, part.Meta.Type, part.Meta, 1)),
                    };
            }
            foreach (string id in _catalog.OptionalParts)
                if (!_catalog.Parts.ContainsKey(id))
                    properties[id] = new JsonObject
                    {
                        ["description"] = $"Prefab part '{id}', declared by a plugin whose assembly was not read here (a client's): " +
                                          "its options are checked when the game runs.",
                    };

            return new JsonObject
            {
                ["$schema"] = Draft,
                ["title"] = "Sage prefab parts",
                ["description"] = "A prefab's \"parts\": setups a plugin declared ([PrefabPart]), by id, with their options.",
                ["type"] = "object",
                ["properties"] = Sorted(properties),
                ["additionalProperties"] = false,
                ["definitions"] = definitions,
            };
        }

        public JsonObject RecordSchema(string type, Type clr)
        {
            var meta = Metadata.Of(clr);
            var properties = new JsonObject
            {
                ["$schema"] = new JsonObject { ["description"] = "The JSON Schema an editor checks this file with; the game ignores it.", ["type"] = "string" },
                ["type"] = new JsonObject { ["description"] = "The record type.", ["const"] = type },
                ["id"] = RefWith($"{Ids}#/definitions/id",
                    "The record's id: name (this file's namespace) or namespace:name. A second definition of an id is an error; change one with \"patch\"."),
                ["base"] = RefWith($"{Ids}#/definitions/{Pointer(BaseKey(type))}",
                    $"Inherit every field from another {type} record, resolved after patches; this record's own fields win."),
                ["patch"] = new JsonObject
                {
                    ["description"] = "true: change a record defined earlier in load order, field by field, instead of defining one. " +
                                      "Objects merge; lists replace unless written \"field+\" (add) or \"field-\" (remove).",
                    ["type"] = "boolean",
                },
                ["abstract"] = new JsonObject
                {
                    ["description"] = "true: a template, usable as a \"base\" and never built into a record itself.",
                    ["type"] = "boolean",
                },
                ["disabled"] = new JsonObject
                {
                    ["description"] = "true (in a patch): remove the record.",
                    ["type"] = "boolean",
                },
            };

            var body = ObjectSchema(clr, meta.Fields, meta, 0, skip: MetaKeys);
            if (body["properties"] is JsonObject fields)   // a record type with no fields has none
                foreach (var (name, schema) in fields)
                    properties[name] = schema?.DeepClone();

            // A prefab's two halves are keyed by what they name, which the metadata cannot say (they are
            // JsonObjects to the record); its tags are tag ids.
            if (clr == typeof(PrefabRecord))
            {
                properties["components"] = RefWith(Components,
                    "Component data by component id: \"sprite_renderer\": { … }. A bare name is this file's namespace, then sage.");
                properties["parts"] = RefWith(Parts, "Setups a plugin declared, by id: \"character\": { \"layer\": \"enemy\" }.");
                var tags = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Ref($"{Ids}#/definitions/ecs_tag"),
                };
                properties["tags"] = WithDescription(tags.DeepClone().AsObject(), "Tags the entity gets, by id or bare name.");
                properties["tags+"] = WithDescription(tags.DeepClone().AsObject(), "Tags to add to the inherited or patched list.");
                properties["tags-"] = WithDescription(tags.DeepClone().AsObject(), "Tags to remove from the inherited or patched list.");
            }

            return new JsonObject
            {
                ["$schema"] = Draft,
                ["title"] = $"Sage {type} record",
                ["description"] = $"A {type} record ({clr.Name}). Generated by `sage schema` from the metadata table (issue #21).",
                ["type"] = "object",
                ["required"] = Strings(new[] { "type", "id" }),
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
        }

        // ---- values -------------------------------------------------------------------------------

        // An object's fields; lists also take "field+" and "field-" (RecordStore.MergeInto applies them at
        // any depth, in a definition as in a patch).
        private JsonObject ObjectSchema(Type type, IReadOnlyList<FieldMetadata> fields, TypeMetadata? owner, int depth,
                                        IReadOnlyCollection<string>? skip = null)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (fields.Count == 0 && depth > 0 && depth < MaxDepth) fields = Metadata.Reflect(type, DeclarationKind.Other, type.Name).Fields;
            if (fields.Count == 0) return new JsonObject { ["type"] = "object" };

            var properties = new JsonObject();
            foreach (var field in fields)
            {
                if (skip != null && skip.Contains(field.JsonName)) continue;
                var schema = FieldSchema(field, type, owner, depth + 1);
                properties[field.JsonName] = schema;
                if (field.Kind == ValueKind.List)
                {
                    properties[field.JsonName + "+"] = Annotated(schema, $"Adds to \"{field.JsonName}\": what a base or an earlier definition listed, then these.");
                    properties[field.JsonName + "-"] = Annotated(schema, $"Removes these from \"{field.JsonName}\" (as a base or an earlier definition listed it).");
                }
            }
            return new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };
        }

        // A field: its value's schema, with the description (tooltip, unit, range, default) and bounds.
        private JsonObject FieldSchema(FieldMetadata field, Type? declaringType, TypeMetadata? owner, int depth)
        {
            JsonNode? defaultValue = owner != null && field.Get != null && IsScalar(field) ? DefaultOf(field, owner, declaringType) : null;
            var value = ValueSchema(field, declaringType, depth);
            var result = new JsonObject { ["description"] = Describe(field, defaultValue) };
            if (defaultValue != null) result["default"] = defaultValue.DeepClone();
            // Draft-07 reads nothing beside a $ref; everything else merges in.
            if (value.ContainsKey("$ref")) result["allOf"] = new JsonArray(value);
            else
                foreach (var (key, node) in value)
                    if (key != "description") result[key] = node?.DeepClone();
            return result;
        }

        private JsonObject ValueSchema(FieldMetadata field, Type? declaringType, int depth)
        {
            // A converter on the member decides the shape (a colour is a uint read from "#RRGGBB").
            if (declaringType != null && Member(declaringType, field.Name) is { } member
                && member.GetCustomAttribute<JsonConverterAttribute>(false) is { } converter)
                return Shape(converter.ConverterType);

            // A string naming a vocabulary entry (issue #28): the ids registered.
            if (declaringType != null && Member(declaringType, field.Name)?.GetCustomAttribute<VocabularyRefAttribute>(false) is { } named)
            {
                var id = Ref($"{Vocabularies}#/definitions/{Pointer(named.Vocabulary + ":id")}");
                if (field.Kind == ValueKind.List) return new JsonObject { ["type"] = "array", ["items"] = id };
                // Empty is "none" for a string that has a fallback (an ability's delivery, a profile's selector).
                if (field.Kind == ValueKind.String) return new JsonObject { ["anyOf"] = new JsonArray(id, new JsonObject { ["const"] = "" }) };
            }

            var type = Nullable.GetUnderlyingType(field.Type) ?? field.Type;
            var schema = KindSchema(field, type, depth);
            return Nullable.GetUnderlyingType(field.Type) != null
                ? new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) }
                : schema;
        }

        private JsonObject KindSchema(FieldMetadata field, Type type, int depth)
        {
            // An entry of an open vocabulary (issue #28), whichever registered type it is.
            if (_catalog.VocabularyOf(type) is { } vocabulary) return Ref($"{Vocabularies}#/definitions/{Pointer(vocabulary)}");

            switch (field.Kind)
            {
                case ValueKind.Bool: return new JsonObject { ["type"] = "boolean" };
                case ValueKind.Integer: return Bounded(new JsonObject { ["type"] = "integer" }, field, type);
                case ValueKind.Number: return Bounded(new JsonObject { ["type"] = "number" }, field, type);
                case ValueKind.String: return new JsonObject { ["type"] = "string" };
                case ValueKind.Enum: return new JsonObject { ["type"] = "string", ["enum"] = Strings(field.EnumValues) };
                case ValueKind.Vector2: return Numbers(2, field);
                case ValueKind.Vector3: return Numbers(3, field);
                case ValueKind.Quaternion: return Numbers(4, null);
                case ValueKind.Vector4:
                    // No converter: System.Text.Json reads its fields, { "x": …, "y": …, "z": …, "w": … }.
                    var xyzw = new JsonObject();
                    foreach (string axis in new[] { "x", "y", "z", "w" }) xyzw[axis] = new JsonObject { ["type"] = "number" };
                    return new JsonObject { ["type"] = "object", ["properties"] = xyzw, ["additionalProperties"] = false };
                case ValueKind.RecordId:
                    return !string.IsNullOrEmpty(field.RecordType) && _catalog.RecordTypes.ContainsKey(field.RecordType!)
                        ? Ref($"{Ids}#/definitions/{Pointer(RecordKey(field.RecordType!))}")
                        : Ref($"{Ids}#/definitions/id");
                case ValueKind.AssetPath: return new JsonObject { ["type"] = "string" };
                case ValueKind.List:
                    return new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = field.Item != null ? ItemSchema(field.Item, depth) : new JsonObject(),
                    };
                case ValueKind.Map:
                    return new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = field.Item != null ? ItemSchema(field.Item, depth) : new JsonObject(),
                    };
                case ValueKind.Object:
                    if (type.GetCustomAttribute<JsonConverterAttribute>(false) is { } converter) return Shape(converter.ConverterType, type);
                    return depth < MaxDepth ? ObjectSchema(type, field.Fields, null, depth) : new JsonObject { ["type"] = "object" };
                case ValueKind.Entity:
                    // A handle is not data: whatever is written is read as no entity.
                    return new JsonObject();
                default:
                    if (type.GetCustomAttribute<JsonConverterAttribute>(false) is { } other) return Shape(other.ConverterType, type);
                    return new JsonObject();
            }
        }

        // One element of a list or value of a map: no member, no description of its own.
        private JsonObject ItemSchema(FieldMetadata item, int depth)
        {
            var type = Nullable.GetUnderlyingType(item.Type) ?? item.Type;
            var schema = KindSchema(item, type, depth + 1);
            return Nullable.GetUnderlyingType(item.Type) != null
                ? new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) }
                : schema;
        }

        // What a [SchemaShape] on the converter (or the type) says the JSON looks like; anything, when
        // nobody said.
        private static JsonObject Shape(Type? converter, Type? type = null)
        {
            var shape = converter?.GetCustomAttribute<SchemaShapeAttribute>(false) ?? type?.GetCustomAttribute<SchemaShapeAttribute>(false);
            if (shape == null) return new JsonObject();
            return JsonNode.Parse(shape.Json) as JsonObject
                   ?? throw new InvalidOperationException($"[SchemaShape] on {converter?.Name ?? type?.Name} is not a JSON object");
        }

        private static JsonObject Bounded(JsonObject schema, FieldMetadata field, Type type)
        {
            double? min = field.Min, max = field.Max;
            if (min == null && (type == typeof(byte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong))) min = 0;
            if (max == null && type == typeof(byte)) max = 255;
            if (min != null) schema["minimum"] = min.Value;
            if (max != null) schema["maximum"] = max.Value;
            return schema;
        }

        private static JsonObject Numbers(int count, FieldMetadata? field)
        {
            var item = new JsonObject { ["type"] = "number" };
            if (field?.Min != null) item["minimum"] = field.Min.Value;
            if (field?.Max != null) item["maximum"] = field.Max.Value;
            return new JsonObject { ["type"] = "array", ["items"] = item, ["minItems"] = count, ["maxItems"] = count };
        }

        private static bool IsScalar(FieldMetadata field) => field.Kind is ValueKind.Bool or ValueKind.Integer or ValueKind.Number
            or ValueKind.String or ValueKind.Enum or ValueKind.Vector2 or ValueKind.Vector3 or ValueKind.Quaternion
            or ValueKind.RecordId or ValueKind.AssetPath;

        // A default as content writes it: a colour as "#RRGGBBAA", nothing for an empty id or path.
        private JsonNode? DefaultOf(FieldMetadata field, TypeMetadata owner, Type? declaringType)
        {
            object? value = owner.DefaultOf(field);
            if (value is RecordId { IsEmpty: true } or AssetPath { IsEmpty: true } or IRecordRef { Id.IsEmpty: true }) return null;
            if (declaringType != null && Member(declaringType, field.Name)?.GetCustomAttribute<JsonConverterAttribute>(false) is { } converter)
                return converter.ConverterType == typeof(ColourJsonConverter) && value is uint packed
                    ? JsonValue.Create(ColourJsonConverter.Format(packed))
                    : null;
            return RegistryDump.Default(value, _catalog.Json);
        }

        private static MemberInfo? Member(Type type, string name) =>
            (MemberInfo?)type.GetField(name, BindingFlags.Public | BindingFlags.Instance)
            ?? type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

        // ---- ids ----------------------------------------------------------------------------------

        private static JsonObject IdEnum(IReadOnlyCollection<string> ids, string description, string type)
        {
            // No record of the type is loaded: an enum would refuse everything, so only the id's form is
            // checked (and the load says whether it exists).
            if (ids.Count == 0)
                return new JsonObject
                {
                    ["description"] = $"The id of a {type} record (none is loaded).",
                    ["type"] = "string",
                    ["pattern"] = IdPattern,
                };
            return new JsonObject { ["description"] = description, ["type"] = "string", ["enum"] = Strings(WithBareNames(ids)) };
        }

        // Every full id, then every bare name that some id has, each once, sorted.
        private static IEnumerable<string> WithBareNames(IEnumerable<string> ids)
        {
            var all = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                all.Add(id);
                int colon = id.IndexOf(':');
                if (colon >= 0) all.Add(id[(colon + 1)..]);
            }
            return all;
        }

        // The keys a table of namespaced ids may be written under: each id, and each bare name with the
        // ids it may mean (a game's own `hop` and the engine's, when both exist).
        private static SortedDictionary<string, List<string>> Keys(IEnumerable<string> ids)
        {
            var keys = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string id in ids)
            {
                keys[id] = new List<string> { id };
                string bare = id[(id.IndexOf(':') + 1)..];
                if (!keys.TryGetValue(bare, out var list)) keys[bare] = list = new List<string>();
                if (!list.Contains(id)) list.Add(id);
            }
            foreach (var list in keys.Values) list.Sort(StringComparer.Ordinal);
            return keys;
        }

        private static JsonObject Choice(IReadOnlyList<string> ids, Func<string, string> reference, string description)
        {
            if (ids.Count == 1) return RefWith(reference(ids[0]), description);
            return new JsonObject { ["description"] = description, ["anyOf"] = new JsonArray(ids.Select(id => (JsonNode?)Ref(reference(id))).ToArray()) };
        }

        private static string BareDescription(string kind, string bare, IReadOnlyList<string> ids, Func<string, string> typeName) =>
            $"The {kind} {string.Join(" or ", ids.Select(id => $"{id} ({typeName(id)})"))}, by its bare name: a bare name is " +
            "this file's namespace, then sage.";

        private static JsonObject Annotated(JsonObject schema, string description)
        {
            var copy = (JsonObject)schema.DeepClone();
            copy.Remove("default");
            copy["description"] = description;
            return copy;
        }

        private static JsonObject WithDescription(JsonObject schema, string description)
        {
            var o = new JsonObject { ["description"] = description };
            foreach (var (key, node) in schema) o[key] = node?.DeepClone();
            return o;
        }

        private static JsonObject Sorted(JsonObject o)
        {
            var sorted = new JsonObject();
            foreach (var (key, node) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sorted[key] = node?.DeepClone();
            return sorted;
        }
    }
}

// What a set of schemas describes: the record types, components, tags and prefab parts that code
// declares, and the ids that content defines. Filled from one or more booted engines (`sage schema`
// with several games writes one set for all of them) and from assemblies read without loading their
// plugins (a client half, for its parts).
public sealed class SchemaCatalog
{
    public sealed record Part(TypeMetadata Meta, string? Shorthand);

    private readonly SortedDictionary<string, SortedSet<string>> _ids = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, SortedSet<string>> _templates = new(StringComparer.Ordinal);

    public SortedDictionary<string, Type> RecordTypes { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, TypeMetadata> Components { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, TypeMetadata> Tags { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, Part> Parts { get; } = new(StringComparer.Ordinal);

    // Parts a simulation says another half declares (PrefabRegistry.Optional): named, not described,
    // unless that half's assembly is read too.
    public SortedSet<string> OptionalParts { get; } = new(StringComparer.Ordinal);

    // The open vocabularies (issue #28), by name: the type an entry is, its JSON key and default, and
    // every registered entry's id and type.
    public sealed record VocabularyInfo(Type EntryType, string Key, string? Default, SortedDictionary<string, Type> Entries);
    public SortedDictionary<string, VocabularyInfo> Vocabularies { get; } = new(StringComparer.Ordinal);

    // The vocabulary a field of `type` holds entries of, or null.
    public string? VocabularyOf(Type type) =>
        Vocabularies.FirstOrDefault(kv => kv.Value.EntryType == type).Key;

    // How the records were read, for quoting defaults as content writes them.
    public JsonSerializerOptions Json { get; private set; } = new RecordStore().Json;

    public IReadOnlyCollection<string> IdsOf(string type) => _ids.TryGetValue(type, out var ids) ? ids : Array.Empty<string>();
    public IReadOnlyCollection<string> TemplatesOf(string type) => _templates.TryGetValue(type, out var ids) ? ids : Array.Empty<string>();

    // Everything a booted engine knows: its record types and the ids loaded for each, the prefab parts
    // its plugins registered and the ones it names as optional, and every declaration in the engine's
    // assembly and its plugins' — a plugin that is not loaded still has its records' types described,
    // so a data file for it validates.
    public void Add(Engine engine)
    {
        Json = engine.Records.Json;
        AddDeclarations(typeof(Engine).Assembly);
        foreach (var module in engine.Modules.Modules) AddDeclarations(module.GetType().Assembly);

        foreach (string type in engine.Records.TypeNames)
        {
            if (engine.Records.TypeOf(type) is { } clr) RecordTypes[type] = clr;
            Set(_ids, type).UnionWith(engine.Records.Ids(type).Select(i => i.ToString()));
            Set(_templates, type).UnionWith(engine.Records.AbstractIds(type).Select(i => i.ToString()));
        }
        foreach (var part in engine.Prefabs.Parts)
            Parts[part.Id] = new Part(Metadata.Of(part.Type), part.Shorthand);
        foreach (string name in engine.Prefabs.OptionalNames) OptionalParts.Add(name);

        foreach (var vocabulary in engine.Vocabularies.All)
        {
            if (!Vocabularies.TryGetValue(vocabulary.Name, out var info))
                Vocabularies[vocabulary.Name] = info = new VocabularyInfo(vocabulary.EntryType, vocabulary.Key, vocabulary.Default,
                                                                          new SortedDictionary<string, Type>(StringComparer.Ordinal));
            foreach (var entry in vocabulary.Entries) info.Entries[entry.Id] = entry.Type;
        }
    }

    // The declarations in one assembly's metadata table: record types, components, tags and parts.
    public void AddDeclarations(Assembly assembly)
    {
        foreach (var meta in Metadata.In(assembly).Values)
        {
            switch (meta.Kind)
            {
                case DeclarationKind.Record: RecordTypes.TryAdd(meta.Id, meta.Type); break;
                case DeclarationKind.Component: Components[meta.Id] = meta; break;
                case DeclarationKind.Tag: Tags[meta.Id] = meta; break;
                case DeclarationKind.PrefabPart:
                    Parts[meta.Id] = new Part(meta, meta.Type.GetCustomAttribute<PrefabPartAttribute>(false)?.Shorthand);
                    break;
            }
        }
    }

    // An assembly on disk that is not loaded as a plugin here — a game's client half, which a headless
    // run leaves out — read for its declarations, with the assemblies beside it that it takes from the
    // engine (Sage.Client, whose `audio` and `particles` parts a server cannot see). Nothing in it runs:
    // only its generated metadata table is read. False when there is no such file or it cannot be read.
    public bool AddAssemblyFile(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var assembly = Assembly.LoadFrom(Path.GetFullPath(path));
            string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == reference.Name)) continue;
                string beside = Path.Combine(directory, reference.Name + ".dll");
                if (!File.Exists(beside)) continue;
                var dependency = Assembly.LoadFrom(beside);
                if (dependency.GetReferencedAssemblies().Any(r => r.Name == EngineName || r.Name == KernelName)) AddDeclarations(dependency);
            }
            AddDeclarations(assembly);
            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or TypeLoadException or ReflectionTypeLoadException)
        {
            return false;
        }
    }

    // What an assembly built on the engine references (Sage.Client does both).
    private static readonly string? EngineName = typeof(Engine).Assembly.GetName().Name;
    private static readonly string? KernelName = typeof(RecordStore).Assembly.GetName().Name;

    private static SortedSet<string> Set(SortedDictionary<string, SortedSet<string>> table, string key)
    {
        if (!table.TryGetValue(key, out var set)) table[key] = set = new SortedSet<string>(StringComparer.Ordinal);
        return set;
    }
}

// mod.json and game.json as JSON Schemas (phase 4j, 4j-5): the keys their loaders read, so a misspelt one is
// underlined in VS Code as the loader would refuse it. Written beside the record schemas; not built from the
// catalog, because a manifest is read before any content is.
internal static class ManifestSchemas
{
    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    private static JsonObject Strings(string description) => new()
    {
        ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description,
    };

    private static JsonObject Object(string title, JsonObject properties) => new()
    {
        ["$schema"] = "http://json-schema.org/draft-07/schema#",
        ["title"] = title,
        ["type"] = "object",
        ["properties"] = properties,
        ["additionalProperties"] = false,
    };

    public static JsonObject ModSchema()
    {
        var schema = Object("Sage mod.json", new JsonObject
        {
            ["id"] = new JsonObject
            {
                ["type"] = "string", ["pattern"] = "^[a-z0-9_.-]+$",
                ["description"] = "The mod's record namespace and mount name (mods/<id>): what its data/ defines is <id>:... It may not be `sage`, the game's id or a plugin's namespace.",
            },
            ["name"] = Str("The name shown to players."),
            ["version"] = Str("The mod's version, 1.2.0."),
            ["author"] = Str("Who made it."),
            ["description"] = Str("What it does."),
            ["game"] = Str("The id of the game this mod is for; \"*\" or left out, any."),
            ["gameVersion"] = Str("A range against the game's own game.json \"version\": \">=0.1\". Left out, any."),
            ["sage"] = Str("The engine versions the mod was made for, as game.json's \"sage\" is: \"^0.1\"."),
            ["dependencies"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Mods that must be active, loaded before this one: { \"other_mod\": \"^1.0\" } (\"*\" for any version).",
            },
            ["loadAfter"] = Strings("Mod ids this one loads after, when they are there."),
            ["loadBefore"] = Strings("Mod ids this one loads before, when they are there."),
            ["incompatible"] = Strings("Mod ids that refuse this mod when one is loaded before it."),
            ["assemblies"] = Strings("A code mod's assemblies, paths inside the mod's folder ({config} is the build configuration): " +
                                     "\"bin/{config}/MyMod.dll\". Trusted code, loaded with the game's full trust and not sandboxed."),
            ["kind"] = Str("\"data\", or \"code\" for a mod that names \"assemblies\" (flagged \"contains code\" wherever mods are listed)."),
        });
        schema["required"] = new JsonArray("id");
        return schema;
    }

    public static JsonObject GameSchema()
    {
        var modules = Object("modules", new JsonObject
        {
            ["disable"] = Strings("Engine module ids this game switches off."),
            ["add"] = Strings("Further assemblies whose modules are added after the game's own."),
        });
        modules.Remove("$schema");
        var cook = Object("cook", new JsonObject
        {
            ["compress"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Whether `sage cook` block-compresses textures (BC1/BC3). Default true.",
            },
            ["uncompressed"] = Strings("Texture paths inside a mount, with * and ** wildcards, that `sage cook` keeps as exact RGBA (pixel art, UI, fonts)."),
        });
        cook.Remove("$schema");
        var schema = Object("Sage game.json", new JsonObject
        {
            ["name"] = Str("The game's name."),
            ["id"] = new JsonObject
            {
                ["type"] = "string", ["pattern"] = "^[a-z0-9_.-]+$",
                ["description"] = "The game's record namespace.",
            },
            ["assembly"] = Str("The game's assembly, relative to this folder; {config} is the build's configuration. Left out for a game of data only."),
            ["mounts"] = Strings("Content folders, relative to this folder, mounted in order; the last wins."),
            ["modsDirectory"] = Str("The folder, relative to this one, whose subfolders are mods (each with a mod.json). Default \"mods\"."),
            ["modules"] = modules,
            ["plugins"] = Strings("The engine's simulation plugins this game uses (\"sage.physics3d\", \"sage.gameplay.*\"). Left out, all; empty, none."),
            ["kits"] = Strings("The kits this game is built on, by plugin id: \"sage.kits.rpg\"."),
            ["scene"] = Str("The scene record every world starts in."),
            ["title"] = Str("The screen record shown before the world starts (a host with a window; -notitle skips it)."),
            ["sage"] = Str("The engine versions this game was made for: \"^0.1\"."),
            ["version"] = Str("The game's own version, 1.4.0: what a mod's \"gameVersion\" is checked against."),
            ["cook"] = cook,
        });
        schema["required"] = new JsonArray("id");
        return schema;
    }
}
