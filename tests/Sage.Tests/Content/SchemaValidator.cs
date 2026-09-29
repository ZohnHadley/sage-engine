#nullable enable
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace sage_engine.Tests;

// A JSON Schema validator for exactly the draft-07 vocabulary RecordSchemas writes (issue #21), so the
// tests can prove what VS Code will underline without a NuGet package or a Python module in CI. It
// refuses a schema that uses any other keyword (Keywords), so the generator cannot start relying on
// something this does not check: grow both together. Python's `jsonschema` agreed with it on every
// shipped file when this was written.
public sealed class SchemaValidator
{
    // Every keyword the generated schemas may use. Annotations are read and not checked.
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        // annotations
        "$schema", "title", "description", "default", "definitions",
        // assertions
        "$ref", "type", "enum", "const", "pattern", "minLength", "minimum", "maximum",
        "properties", "additionalProperties", "required", "maxProperties",
        "items", "minItems", "maxItems",
        "allOf", "anyOf", "oneOf", "if", "then", "else",
    };

    private readonly string _directory;
    private readonly Dictionary<string, JsonNode> _files = new(StringComparer.Ordinal);

    public SchemaValidator(string directory) { _directory = directory; }

    public JsonNode Load(string file)
    {
        if (!_files.TryGetValue(file, out var node))
            _files[file] = node = JsonNode.Parse(File.ReadAllText(Path.Combine(_directory, file)))
                                  ?? throw new InvalidDataException($"{file} is empty");
        return node;
    }

    // The problems with `instance` against `file`'s root schema, each as "$.path: what"; empty when valid.
    public List<string> Validate(JsonNode? instance, string file = RecordSchemas.Root)
    {
        var errors = new List<string>();
        Check(instance, Load(file), file, "$", errors);
        return errors;
    }

    // Reads a record file as the loader does: comments and trailing commas allowed.
    public static JsonNode? ReadData(string path) => JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    });

    // Every schema object in every file uses only Keywords, and every $ref resolves.
    public List<string> CheckVocabulary()
    {
        var problems = new List<string>();
        foreach (string path in Directory.GetFiles(_directory, "*.schema.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            string file = Path.GetFileName(path);
            Walk(Load(file), file, "#", problems);
        }
        return problems;
    }

    private void Walk(JsonNode? node, string file, string at, List<string> problems)
    {
        if (node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return;
        if (node is not JsonObject schema) { problems.Add($"{file}{at}: a schema must be an object or a boolean"); return; }
        foreach (var (key, child) in schema)
        {
            if (!Keywords.Contains(key)) problems.Add($"{file}{at}: keyword '{key}' is not one the test validator checks");
            switch (key)
            {
                case "properties" or "definitions":
                    foreach (var (name, sub) in child!.AsObject()) Walk(sub, file, $"{at}/{key}/{name}", problems);
                    break;
                case "additionalProperties" or "items" or "if" or "then" or "else":
                    Walk(child, file, $"{at}/{key}", problems);
                    break;
                case "allOf" or "anyOf" or "oneOf":
                    int i = 0;
                    foreach (var sub in child!.AsArray()) Walk(sub, file, $"{at}/{key}/{i++}", problems);
                    break;
                case "$ref":
                    try { Resolve((string)child!, file); }
                    catch (Exception ex) when (ex is InvalidDataException or FileNotFoundException or KeyNotFoundException)
                    {
                        problems.Add($"{file}{at}: $ref {child}: {ex.Message}");
                    }
                    break;
            }
        }
    }

    private (JsonNode Schema, string File) Resolve(string reference, string file)
    {
        int hash = reference.IndexOf('#');
        string target = hash < 0 ? reference : reference[..hash];
        string pointer = hash < 0 ? "" : reference[(hash + 1)..];
        if (target.Length > 0) file = target;
        JsonNode node = Load(file);
        foreach (string raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string segment = raw.Replace("~1", "/").Replace("~0", "~");
            node = node is JsonObject o && o[segment] is { } next ? next : throw new KeyNotFoundException($"no '{segment}' in {file}");
        }
        return (node, file);
    }

    private void Check(JsonNode? instance, JsonNode schemaNode, string file, string path, List<string> errors)
    {
        if (schemaNode is JsonValue b && b.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            if (b.GetValue<bool>() == false) errors.Add($"{path}: not allowed here");
            return;
        }
        var schema = schemaNode.AsObject();

        if (schema["$ref"] is { } reference)
        {
            var (target, targetFile) = Resolve((string)reference!, file);
            Check(instance, target, targetFile, path, errors);
        }

        if (schema["type"] is { } type)
        {
            var allowed = type is JsonArray many ? many.Select(t => (string)t!).ToList() : new List<string> { (string)type! };
            if (!allowed.Any(t => IsType(instance, t)))
            {
                errors.Add($"{path}: expected {string.Join(" or ", allowed)}, not {KindOf(instance)}");
                return;   // nothing else about it can be judged
            }
        }

        if (schema["enum"] is JsonArray values && !values.Any(v => JsonNode.DeepEquals(v, instance)))
            errors.Add($"{path}: {Show(instance)} is not one of {values.Count} allowed value(s)");
        if (schema.ContainsKey("const") && !JsonNode.DeepEquals(schema["const"], instance))
            errors.Add($"{path}: must be {Show(schema["const"])}");

        if (instance is JsonValue scalar)
        {
            if (scalar.GetValueKind() == JsonValueKind.String)
            {
                string text = scalar.GetValue<string>();
                if (schema["pattern"] is { } pattern && !Regex.IsMatch(text, (string)pattern!))
                    errors.Add($"{path}: \"{text}\" does not match {pattern}");
                if (schema["minLength"] is { } minLength && text.Length < (int)minLength)
                    errors.Add($"{path}: shorter than {minLength}");
            }
            if (scalar.GetValueKind() == JsonValueKind.Number)
            {
                double number = scalar.GetValue<double>();
                if (schema["minimum"] is { } min && number < (double)min) errors.Add($"{path}: {number} is below the minimum {min}");
                if (schema["maximum"] is { } max && number > (double)max) errors.Add($"{path}: {number} is above the maximum {max}");
            }
        }

        if (instance is JsonArray array)
        {
            if (schema["minItems"] is { } minItems && array.Count < (int)minItems) errors.Add($"{path}: fewer than {minItems} items");
            if (schema["maxItems"] is { } maxItems && array.Count > (int)maxItems) errors.Add($"{path}: more than {maxItems} items");
            if (schema["items"] is { } items)
                for (int i = 0; i < array.Count; i++) Check(array[i], items, file, $"{path}[{i}]", errors);
        }

        if (instance is JsonObject obj)
        {
            if (schema["required"] is JsonArray required)
                foreach (var name in required)
                    if (!obj.ContainsKey((string)name!)) errors.Add($"{path}: \"{name}\" is required");
            if (schema["maxProperties"] is { } maxProperties && obj.Count > (int)maxProperties)
                errors.Add($"{path}: more than {maxProperties} properties");
            var properties = schema["properties"] as JsonObject;
            foreach (var (name, value) in obj)
            {
                string child = $"{path}.{name}";
                if (properties != null && properties[name] is { } property) Check(value, property, file, child, errors);
                else if (schema["additionalProperties"] is { } additional)
                {
                    if (additional is JsonValue no && no.GetValueKind() == JsonValueKind.False)
                        errors.Add($"{child}: \"{name}\" is not a field here");
                    else Check(value, additional, file, child, errors);
                }
            }
        }

        if (schema["allOf"] is JsonArray all)
            foreach (var sub in all) Check(instance, sub!, file, path, errors);

        if (schema["anyOf"] is JsonArray any)
        {
            var tries = any.Select(sub => Try(instance, sub!, file, path)).ToList();
            if (tries.All(t => t.Count > 0))
                errors.Add($"{path}: matches none of {tries.Count} choices; nearest: " + string.Join("; ", Nearest(tries, path)));
        }

        if (schema["oneOf"] is JsonArray one)
        {
            var tries = one.Select(sub => Try(instance, sub!, file, path)).ToList();
            int matched = tries.Count(t => t.Count == 0);
            if (matched == 0) errors.AddRange(Nearest(tries, path));
            else if (matched > 1) errors.Add($"{path}: matches {matched} of the oneOf choices, not exactly one");
        }

        if (schema["if"] is { } condition)
        {
            if (Try(instance, condition, file, path).Count == 0) { if (schema["then"] is { } then) Check(instance, then, file, path, errors); }
            else if (schema["else"] is { } otherwise) Check(instance, otherwise, file, path, errors);
        }
    }

    // The choice that came closest, as an editor reports it: one of the right type first (an object
    // against the record schema rather than the array one), then the fewest problems.
    private static List<string> Nearest(List<List<string>> tries, string path) =>
        tries.OrderBy(t => t.Any(e => e.StartsWith(path + ": expected ", StringComparison.Ordinal)) ? 1 : 0).ThenBy(t => t.Count).First();

    private List<string> Try(JsonNode? instance, JsonNode schema, string file, string path)
    {
        var errors = new List<string>();
        Check(instance, schema, file, path, errors);
        return errors;
    }

    private static bool IsType(JsonNode? node, string type) => type switch
    {
        "object" => node is JsonObject,
        "array" => node is JsonArray,
        "null" => node is null,
        "string" => node is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "boolean" => node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "number" => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && Math.Floor(v.GetValue<double>()) == v.GetValue<double>(),
        _ => throw new InvalidDataException($"unknown type '{type}'"),
    };

    private static string KindOf(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject => "an object",
        JsonArray => "an array",
        JsonValue v => v.GetValueKind().ToString().ToLowerInvariant(),
        _ => "?",
    };

    private static string Show(JsonNode? node) => node?.ToJsonString() ?? "null";
}
