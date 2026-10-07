#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// Writes the bare record ids inside a placement's overrides in full (issue #375): what a mod's patch of a
// game's level holds. A bare id in an override means the prefab's namespace (PrefabOverrides), and a file
// read later by something that resolves it against the file's own namespace (R11) — or a person copying the
// line into another mod — must not be able to read it as the mod's. Which strings are ids is the generated
// metadata's to say: a field of kind RecordId (`[RecordRef]`, RecordId, RecordRef<T>), found through each
// body's type (a component by its key, a part by its id), into nested objects, lists and maps.
internal static class OverrideIds
{
    // `overrides` is a placement's "overrides" object ({ "components": {...}, "parts": {...} }), changed in place.
    public static void Qualify(Engine engine, JsonObject overrides, string ns)
    {
        foreach (var (section, value) in overrides.ToList())
        {
            if (value is not JsonObject bodies) continue;
            bool components = string.Equals(section, "components", StringComparison.OrdinalIgnoreCase);
            bool parts = string.Equals(section, "parts", StringComparison.OrdinalIgnoreCase);
            if (!components && !parts) continue;
            foreach (var (key, body) in bodies.ToList())
            {
                if (components)
                {
                    if (engine.Components.TryResolveComponent(key, ns, out var type, out _))
                        bodies[key] = Object(body, Metadata.Of(type).Fields, ns);
                }
                else if (engine.Prefabs.TryGet(key, out var part))
                {
                    var fields = Metadata.Of(part.Type).Fields;
                    // A part written in shorthand ("state_machine": "guard") is its shorthand field's value.
                    bodies[key] = body is not JsonObject && part.Shorthand != null && Field(fields, part.Shorthand) is { } shorthand
                        ? Value(body, shorthand, ns)
                        : Object(body, fields, ns);
                }
            }
        }
    }

    private static JsonNode? Object(JsonNode? node, IReadOnlyList<FieldMetadata> fields, string ns)
    {
        if (node is not JsonObject obj) return node?.DeepClone();
        var result = new JsonObject();
        foreach (var (name, value) in obj)
        {
            // "items+" / "items-" are the items field too.
            string field = name.Length > 1 && (name[^1] == '+' || name[^1] == '-') ? name[..^1] : name;
            result[name] = Field(fields, field) is { } meta ? Value(value, meta, ns) : value?.DeepClone();
        }
        return result;
    }

    private static JsonNode? Value(JsonNode? node, FieldMetadata field, string ns)
    {
        switch (field.Kind)
        {
            case ValueKind.RecordId:
                return node is JsonValue v && v.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text) && !text.Contains(':')
                    ? JsonValue.Create($"{ns}:{text.Trim()}")
                    : node?.DeepClone();
            case ValueKind.List when field.Item != null && node is JsonArray array:
                return new JsonArray(array.Select(item => Value(item, field.Item, ns)).ToArray());
            case ValueKind.Map when field.Item != null && node is JsonObject map:
            {
                var result = new JsonObject();
                foreach (var (key, value) in map) result[key] = Value(value, field.Item, ns);
                return result;
            }
            case ValueKind.Object:
                return Object(node, field.Fields, ns);
            default:
                return node?.DeepClone();
        }
    }

    private static FieldMetadata? Field(IReadOnlyList<FieldMetadata> fields, string name)
    {
        foreach (var f in fields)
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(f.JsonName, name, StringComparison.OrdinalIgnoreCase))
                return f;
        return null;
    }
}
