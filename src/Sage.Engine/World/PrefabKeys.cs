#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

namespace sage_engine;

// Per-entity values in a map (docs/design/15 §3, issue #18). A `.map` entity is a prefab placed by
// classname; these are the keys a mapper may set on it to change one field of one part or component for
// that entity alone — a brighter lamp, a heavier crate:
//
//   "classname" "light"
//   "light.range" "12"
//   "light.colour" "1 0.5 0.2"
//
// A key is `<section>.<field>`: the section is the part or component as the prefab writes it (`light`,
// `body`, `ai_state`; a namespaced one, `sandbox:hop`, is written `sandbox.hop`), and the field is its
// JSON name. The keys are worked out from the prefab and the metadata table (Metadata), so the FGD that
// offers them (FgdExport) and the importer that reads them (MapLoader) cannot disagree: both call For.
// Only fields a mapper can type are offered — numbers, flags, text, choices, vectors and record ids —
// and never a [Transient] one, which a spawn would not keep.
public sealed record PrefabKey(string Key, string Section, bool IsPart, TypeMetadata Owner, FieldMetadata Field, JsonNode? Written)
{
    // The value the prefab gives it, or the field's own default when the prefab does not say.
    public string DefaultText => Written != null ? PrefabKeys.ToMapText(Field, Written) : PrefabKeys.ToMapText(Field, Owner.DefaultOf(Field));
}

public static class PrefabKeys
{
    public static IReadOnlyList<PrefabKey> For(Engine engine, RecordId id, PrefabRecord prefab)
    {
        var keys = new List<PrefabKey>();

        if (prefab.Parts != null)
            foreach (var (name, options) in prefab.Parts)
            {
                if (!engine.Prefabs.TryGet(name, out var part)) continue;
                var meta = Metadata.Of(part.Type);
                foreach (var field in meta.Fields.Where(Offered))
                {
                    JsonNode? written = options is JsonObject o ? Find(o, field)
                        : options != null && part.Shorthand != null && string.Equals(part.Shorthand, field.Name, StringComparison.OrdinalIgnoreCase) ? options
                        : null;
                    keys.Add(new PrefabKey(KeyOf(name, field), name, true, meta, field, written));
                }
            }

        if (prefab.Components != null)
            foreach (var (name, fields) in prefab.Components)
            {
                if (!engine.Components.TryResolveComponent(name, id.Namespace, out var type, out _)) continue;
                var meta = Metadata.Of(type.Type);
                foreach (var field in meta.Fields.Where(Offered))
                    keys.Add(new PrefabKey(KeyOf(name, field), name, false, meta, field, fields is JsonObject o ? Find(o, field) : null));
            }

        return keys;
    }

    // The prefab with a map entity's keys applied; the prefab itself when none of them are its keys.
    // A value that does not read as its field's type is an error naming the key, and is skipped.
    public static PrefabRecord Apply(Engine engine, RecordId id, PrefabRecord prefab, IReadOnlyDictionary<string, string> entityKeys,
                                     string where)
    {
        PrefabRecord? copy = null;
        foreach (var key in For(engine, id, prefab))
        {
            if (!entityKeys.TryGetValue(key.Key, out var text)) continue;
            if (!TryParse(key.Field, text, out var value, out var why))
            {
                Log.Error(LogCat.Level, $"{where}: '{key.Key}' is a {key.Field.TypeName}, and \"{text}\" {why}");
                continue;
            }

            copy ??= new PrefabRecord
            {
                Name = prefab.Name,
                Components = (JsonObject?)prefab.Components?.DeepClone(),
                Tags = prefab.Tags == null ? new List<string>() : new List<string>(prefab.Tags),
                Parts = (JsonObject?)prefab.Parts?.DeepClone(),
            };
            var section = key.IsPart ? copy.Parts! : copy.Components!;
            if (section[key.Section] is not JsonObject body)
            {
                // `{}`, nothing, or a shorthand's bare value: an object now, keeping what it said.
                body = new JsonObject();
                if (key.IsPart && section[key.Section] is { } bare && engine.Prefabs.TryGet(key.Section, out var part) && part.Shorthand != null)
                    body[Metadata.Camel(part.Shorthand)] = bare.DeepClone();
                section[key.Section] = body;
            }
            // Replace whichever spelling the prefab used, so the value read is this one.
            foreach (var existing in body.Select(kv => kv.Key).Where(k => Matches(k, key.Field)).ToList()) body.Remove(existing);
            body[key.Field.JsonName] = value;
        }
        return copy ?? prefab;
    }

    // The key for one field of a section: `light.range`, `sandbox.hop.base_y`.
    public static string KeyOf(string section, FieldMetadata field) => section.Replace(':', '.') + "." + field.JsonName;

    public static bool Offered(FieldMetadata field) =>
        !field.Transient && field.Kind is ValueKind.Bool or ValueKind.Integer or ValueKind.Number or ValueKind.String
            or ValueKind.Enum or ValueKind.Vector2 or ValueKind.Vector3 or ValueKind.RecordId;

    // A map key's text as the field's JSON.
    public static bool TryParse(FieldMetadata field, string text, out JsonNode? value, out string why)
    {
        value = null;
        why = "";
        text = text.Trim();
        var culture = CultureInfo.InvariantCulture;
        switch (field.Kind)
        {
            case ValueKind.Bool:
                if (text is "1" or "true" or "yes") { value = true; return true; }
                if (text is "0" or "false" or "no" or "") { value = false; return true; }
                why = "is not 0 or 1";
                return false;
            case ValueKind.Integer:
                if (long.TryParse(text, NumberStyles.Integer, culture, out long whole)) { value = whole; return InRange(field, whole, out why); }
                why = "is not a whole number";
                return false;
            case ValueKind.Number:
                if (double.TryParse(text, NumberStyles.Float, culture, out double number)) { value = number; return InRange(field, number, out why); }
                why = "is not a number";
                return false;
            case ValueKind.Enum:
                var match = field.EnumValues.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                if (match != null) { value = match; return true; }
                why = $"is not one of {string.Join(", ", field.EnumValues)}";
                return false;
            case ValueKind.Vector2 or ValueKind.Vector3:
            {
                int count = field.Kind == ValueKind.Vector2 ? 2 : 3;
                var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                var array = new JsonArray();
                foreach (var part in parts)
                {
                    if (!double.TryParse(part, NumberStyles.Float, culture, out double n)) break;
                    array.Add(n);
                }
                if (parts.Length == count && array.Count == count) { value = array; return true; }
                why = $"is not {count} numbers separated by spaces";
                return false;
            }
            default:   // String, RecordId: as written (a bare record id means the map's namespace, as in a prefab)
                value = text;
                return true;
        }
    }

    // A field's value as a map writes it: `12`, `1 0.5 0.2`, `Capsule`, `1` for true.
    public static string ToMapText(FieldMetadata field, object? value)
    {
        var culture = CultureInfo.InvariantCulture;
        switch (value)
        {
            case null: return "";
            case JsonArray array: return string.Join(" ", array.Select(n => n?.ToJsonString() ?? "0"));
            case JsonValue json when json.TryGetValue(out bool b): return b ? "1" : "0";
            case JsonValue json when json.TryGetValue(out string? s): return s ?? "";
            case JsonNode node: return node.ToJsonString();
            case bool b: return b ? "1" : "0";
            case float f: return f.ToString("0.###", culture);
            case double d: return d.ToString("0.###", culture);
            case System.Numerics.Vector3 v: return $"{v.X.ToString("0.###", culture)} {v.Y.ToString("0.###", culture)} {v.Z.ToString("0.###", culture)}";
            case System.Numerics.Vector2 v: return $"{v.X.ToString("0.###", culture)} {v.Y.ToString("0.###", culture)}";
            case RecordId r: return r.IsEmpty ? "" : r.ToString();
            case IRecordRef r: return r.Id.IsEmpty ? "" : r.Id.ToString();
            case IFormattable f: return f.ToString(null, culture);
            default: return value.ToString() ?? "";
        }
    }

    private static bool InRange(FieldMetadata field, double value, out string why)
    {
        why = "";
        if (field.Min is { } min && value < min) { why = $"is below its minimum, {min.ToString(CultureInfo.InvariantCulture)}"; return false; }
        if (field.Max is { } max && value > max) { why = $"is above its maximum, {max.ToString(CultureInfo.InvariantCulture)}"; return false; }
        return true;
    }

    private static JsonNode? Find(JsonObject body, FieldMetadata field)
    {
        foreach (var (name, node) in body)
            if (Matches(name, field)) return node;
        return null;
    }

    private static bool Matches(string name, FieldMetadata field) =>
        string.Equals(name, field.JsonName, StringComparison.OrdinalIgnoreCase) || string.Equals(name, field.Name, StringComparison.OrdinalIgnoreCase);
}
