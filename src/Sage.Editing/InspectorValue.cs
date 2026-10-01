#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Editing;

// A value typed at the console (`ed_set crate health.max 40`) turned into what a file would say there
// (issue #223), by the field's declared shape (FieldMetadata.Kind): the JsonNode an override is written
// with, so `ed_set` and the inspector's widgets write the same thing.
//
//   Bool          true false yes no on off 1 0
//   Integer       42  (whole, inside the field's Min..Max)
//   Number        0.25  (invariant culture: a dot, whatever the machine's locale)
//   Vector2/3/4   "1 2 3", "1,2,3", "[1, 2, 3]"; Quaternion is four numbers, x y z w
//   Enum          a member's name, any case
//   RecordId      "ns:name" or a bare name; checked against the records of the type the field names
//                 ([RecordRef]) when a store is given; "none" or "" is no record
//   String/Asset  the text, without surrounding quotes
//   Json, List, Map, Object   JSON text, set whole
//
// An Entity or a type with no shape content can write (Other) is not a value at all, and says so.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class InspectorValue
{
    public static bool TryParse(FieldMetadata field, string text, [NotNullWhen(true)] out JsonNode? value, out string error,
                                RecordStore? records = null, string defaultNamespace = "sage")
    {
        ArgumentNullException.ThrowIfNull(field);
        value = null;
        error = "";
        text = (text ?? "").Trim();

        switch (field.Kind)
        {
            case ValueKind.Bool:
                switch (text.ToLowerInvariant())
                {
                    case "true" or "yes" or "on" or "1": value = JsonValue.Create(true); return true;
                    case "false" or "no" or "off" or "0": value = JsonValue.Create(false); return true;
                }
                return Fail(out error, $"'{text}' is not true or false");

            case ValueKind.Integer:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long whole))
                    return Fail(out error, $"'{text}' is not a whole number");
                if (!InRange(field, whole, out error)) return false;
                value = JsonValue.Create(whole);
                return true;

            case ValueKind.Number:
                if (!TryNumber(text, out double number)) return Fail(out error, $"'{text}' is not a number");
                if (!InRange(field, number, out error)) return false;
                value = JsonValue.Create(number);
                return true;

            case ValueKind.Vector2: return Numbers(text, 2, "x y", out value, out error);
            case ValueKind.Vector3: return Numbers(text, 3, "x y z", out value, out error);
            case ValueKind.Vector4: return Numbers(text, 4, "x y z w", out value, out error);
            case ValueKind.Quaternion: return Numbers(text, 4, "x y z w", out value, out error);

            case ValueKind.Enum:
            {
                string? name = field.EnumValues.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                if (name == null) return Fail(out error, $"'{text}' is not one of {string.Join(", ", field.EnumValues)}");
                value = JsonValue.Create(name);
                return true;
            }

            case ValueKind.RecordId:
                return RecordIdValue(field, Unquote(text), records, defaultNamespace, out value, out error);

            case ValueKind.String:
            case ValueKind.AssetPath:
                value = JsonValue.Create(Unquote(text));
                return true;

            case ValueKind.Json:
            case ValueKind.List:
            case ValueKind.Map:
            case ValueKind.Object:
                try
                {
                    value = JsonNode.Parse(text);
                }
                catch (JsonException ex)
                {
                    return Fail(out error, $"not JSON: {ex.Message}");
                }
                if (value == null) return Fail(out error, "null is not a value an override can set");
                return true;

            default:
                return Fail(out error, $"a {field.TypeName} cannot be typed in");
        }
    }

    // How a value reads back at the console and in the inspector: what TryParse takes.
    public static string Format(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        float f => f.ToString("0.#####", CultureInfo.InvariantCulture),
        double d => d.ToString("0.#####", CultureInfo.InvariantCulture),
        System.Numerics.Vector2 v => $"{Format(v.X)} {Format(v.Y)}",
        System.Numerics.Vector3 v => $"{Format(v.X)} {Format(v.Y)} {Format(v.Z)}",
        System.Numerics.Vector4 v => $"{Format(v.X)} {Format(v.Y)} {Format(v.Z)} {Format(v.W)}",
        System.Numerics.Quaternion q => $"{Format(q.X)} {Format(q.Y)} {Format(q.Z)} {Format(q.W)}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static bool RecordIdValue(FieldMetadata field, string text, RecordStore? records, string defaultNamespace,
                                      out JsonNode? value, out string error)
    {
        error = "";
        if (text.Length == 0 || string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
        {
            value = JsonValue.Create("");
            return true;
        }
        value = null;
        RecordId id;
        try { id = RecordId.Parse(text, defaultNamespace); }
        catch (FormatException ex) { return Fail(out error, ex.Message); }

        if (records == null || field.RecordType == null)
        {
            value = JsonValue.Create(text);
            return true;
        }
        if (records.Exists(field.RecordType, id))
        {
            // Written as typed: a bare id means the prefab's namespace, as it does in the prefab itself.
            value = JsonValue.Create(text);
            return true;
        }
        if (!text.Contains(':'))
        {
            // A bare name another namespace has: written in full, since bare would mean the wrong one.
            var elsewhere = records.Ids(field.RecordType).Where(i => string.Equals(i.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (elsewhere.Count == 1)
            {
                value = JsonValue.Create(elsewhere[0].ToString());
                return true;
            }
        }
        return Fail(out error, $"there is no {field.RecordType} '{id}'");
    }

    private static bool Numbers(string text, int count, string shape, out JsonNode? value, out string error)
    {
        value = null;
        error = "";
        var parts = text.Trim('[', ']', '(', ')').Split(new[] { ' ', ',', '\t', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count) return Fail(out error, $"'{text}' is not {count} numbers ({shape})");
        var array = new JsonArray();
        foreach (var part in parts)
        {
            if (!TryNumber(part, out double n)) return Fail(out error, $"'{part}' is not a number");
            array.Add(JsonValue.Create(n));
        }
        value = array;
        return true;
    }

    private static bool TryNumber(string text, out double number) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

    private static bool InRange(FieldMetadata field, double number, out string error)
    {
        error = "";
        if (field.Min is { } min && number < min) return Fail(out error, $"{Format(number)} is below the least, {Format(min)}");
        if (field.Max is { } max && number > max) return Fail(out error, $"{Format(number)} is above the most, {Format(max)}");
        return true;
    }

    private static string Unquote(string text) =>
        text.Length >= 2 && (text[0] == '"' && text[^1] == '"' || text[0] == '\'' && text[^1] == '\'') ? text[1..^1] : text;

    private static bool Fail(out string error, string message)
    {
        error = message;
        return false;
    }
}
