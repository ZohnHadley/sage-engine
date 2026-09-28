#nullable enable
using System;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sage_engine;

// `namespace:name` (docs/design/05 §3.5): "sage:lit_default", "sandbox:bunny". A bare name in a
// record file gets the namespace of the mount that defines it.
[JsonConverter(typeof(RecordIdJsonConverter))]
public readonly record struct RecordId(string Namespace, string Name)
{
    public static RecordId Parse(string text, string defaultNamespace)
    {
        text = text.Trim();
        int colon = text.IndexOf(':');
        string ns = colon < 0 ? defaultNamespace : text.Substring(0, colon);
        string name = colon < 0 ? text : text.Substring(colon + 1);
        if (!IsValidPart(ns) || !IsValidPart(name))
            throw new FormatException($"Invalid record id '{text}' (expected [namespace:]name, lower case letters, digits, _ and .)");
        return new RecordId(ns, name);
    }

    public bool IsEmpty => string.IsNullOrEmpty(Name);

    private static bool IsValidPart(string s)
    {
        if (s.Length == 0) return false;
        foreach (char c in s)
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_' || c == '.' || c == '-')) return false;
        return true;
    }

    public override string ToString() => IsEmpty ? "" : $"{Namespace}:{Name}";
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class RecordAttribute : Attribute
{
    public RecordAttribute(string type) { Type = type; }
    public string Type { get; }

    // The plugin that registers this record type (issue #16): Sage.Generators writes the registration,
    // run just before that plugin's Init. Leave it out when the assembly has one plugin (a game);
    // RegistrationOwners.Core for the engine's own.
    public string? Plugin { get; set; }
}

// While the RecordStore deserializes one record, bare RecordId references resolve against that
// record's namespace. Deserialization is single-threaded (main thread), so a thread-static is enough.
internal static class RecordParseContext
{
    [ThreadStatic] public static string? Namespace;
}

internal sealed class RecordIdJsonConverter : JsonConverter<RecordId>
{
    public override RecordId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        if (string.IsNullOrWhiteSpace(text)) return default;
        // As a JsonException, so the serializer adds the path and the record store can say which
        // line of which file holds the bad id (issue #22).
        try { return RecordId.Parse(text, RecordParseContext.Namespace ?? "sage"); }
        catch (FormatException ex) { throw new JsonException(ex.Message, ex); }
    }

    public override void Write(Utf8JsonWriter writer, RecordId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

// [x, y] in record files.
// An entity handle is not data (09 §3.1). A prefab has nothing to point at when it is written, and
// a handle means nothing outside the world that issued it, so reading one gives the null entity and
// writing one gives null. It needs a converter at all because `Entity` exposes a ref struct (`Tags`),
// which System.Text.Json refuses to look at — without this, any component holding one (`AIState`,
// `ActiveEffect`) cannot be read from a prefab at all. Saves give entities stable ids instead (F27).
internal sealed class EntityJsonConverter : JsonConverter<Friflo.Engine.ECS.Entity>
{
    public override Friflo.Engine.ECS.Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, Friflo.Engine.ECS.Entity value, JsonSerializerOptions options) =>
        writer.WriteNullValue();
}

internal sealed class Vector2JsonConverter : JsonConverter<Vector2>
{
    public override Vector2 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected [x, y]");
        var v = new float[2];
        for (int i = 0; i < 2; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("expected [x, y]");
            v[i] = reader.GetSingle();
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("expected [x, y]");
        return new Vector2(v[0], v[1]);
    }

    public override void Write(Utf8JsonWriter writer, Vector2 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteEndArray();
    }
}

// [x, y, z] in record files.
// [x, y, z, w]. Without this a rotation writes X/Y/Z/W *and* a derived `IsIdentity`, which is noise
// in a save and a trap in a record: five members where the maths has four.
internal sealed class QuaternionJsonConverter : JsonConverter<Quaternion>
{
    public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected [x, y, z, w]");
        Span<float> v = stackalloc float[4];
        int n = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            if (n < 4 && reader.TokenType == JsonTokenType.Number) v[n++] = reader.GetSingle();
        // A rotation of all zeroes is not a rotation; an absent or short one means "unrotated".
        var q = new Quaternion(v[0], v[1], v[2], v[3]);
        return q.LengthSquared() > 1e-6f ? q : Quaternion.Identity;
    }

    public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteNumberValue(value.W);
        writer.WriteEndArray();
    }
}

internal sealed class Vector3JsonConverter : JsonConverter<Vector3>
{
    public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected [x, y, z]");
        var v = new float[3];
        for (int i = 0; i < 3; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("expected [x, y, z]");
            v[i] = reader.GetSingle();
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("expected [x, y, z]");
        return new Vector3(v[0], v[1], v[2]);
    }

    public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}
