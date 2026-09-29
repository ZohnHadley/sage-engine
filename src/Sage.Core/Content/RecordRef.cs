#nullable enable
using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Core;

// A reference to a record of one type (REDESIGN §4.2, issue #22): `RecordRef<SoundRecord> Sound` is a
// sound, and the load says so when it names an item, or nothing at all. A plain RecordId only has to
// name *some* record, which is how "sound": "sandbox:boom" pointing at a particle used to pass.
//
// In a file it is the same string a RecordId is ("hit_flesh", "sage:hit_flesh"), resolved in the
// namespace of the record it is written in, so migrating a field from RecordId changes no content.
// It converts to and from RecordId both ways, so code that passes ids around keeps working; `Target`
// says which record type it points at, for the checks, a schema and an editor's picker (#18, #21).
[JsonConverter(typeof(RecordRefJsonConverterFactory))]
public readonly struct RecordRef<T> : IRecordRef, IEquatable<RecordRef<T>> where T : class
{
    public RecordRef(RecordId id) { Id = id; }
    public RecordRef(string ns, string name) { Id = new RecordId(ns, name); }

    public RecordId Id { get; }
    public Type Target => typeof(T);
    public bool IsEmpty => Id.IsEmpty;
    public string Namespace => Id.Namespace;
    public string Name => Id.Name;

    public static implicit operator RecordId(RecordRef<T> reference) => reference.Id;
    public static implicit operator RecordRef<T>(RecordId id) => new(id);

    public bool Equals(RecordRef<T> other) => Id == other.Id;
    public override bool Equals(object? obj) => obj is RecordRef<T> other ? Equals(other) : obj is RecordId id && Id == id;
    public override int GetHashCode() => Id.GetHashCode();
    public static bool operator ==(RecordRef<T> a, RecordRef<T> b) => a.Id == b.Id;
    public static bool operator !=(RecordRef<T> a, RecordRef<T> b) => a.Id != b.Id;
    // Spelled out so comparing with a plain id is not ambiguous between the two conversions.
    public static bool operator ==(RecordRef<T> a, RecordId b) => a.Id == b;
    public static bool operator !=(RecordRef<T> a, RecordId b) => a.Id != b;
    public static bool operator ==(RecordId a, RecordRef<T> b) => a == b.Id;
    public static bool operator !=(RecordId a, RecordRef<T> b) => a != b.Id;

    public override string ToString() => Id.ToString();
}

// What every RecordRef<T> is, for code that walks records without knowing T (the load's checks).
public interface IRecordRef
{
    RecordId Id { get; }
    Type Target { get; }
}

public static class RecordRefs
{
    // The record type name a RecordRef<T> points at ("sound" for SoundRecord), from its [Record];
    // null for a T that is not a record type.
    public static string? TypeNameOf(Type target) => target.GetCustomAttribute<RecordAttribute>()?.Type;
}

internal sealed class RecordRefJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(RecordRef<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(RecordRefJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

internal sealed class RecordRefJsonConverter<T> : JsonConverter<RecordRef<T>> where T : class
{
    private static readonly RecordIdJsonConverter Ids = new();

    public override RecordRef<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(Ids.Read(ref reader, typeof(RecordId), options));

    public override void Write(Utf8JsonWriter writer, RecordRef<T> value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
