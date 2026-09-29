#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Sage.Core;

// Reads a vocabulary entry from content (issue #28): a bare id (`"consume"`) or an object naming it
// under the vocabulary's key with its settings beside it (`{ "use": "heal", "amount": 25 }`). The rest
// of the object is read as the registered type, with the same strictness as any record: a field the
// entry does not have is an error that says the nearest one it does (issue #22).
//
// The Engine adds one of these to the record store's options, over its own Vocabularies, so every
// field typed as a vocabulary (an interface or abstract class marked [Vocabulary]) reads this way.
public sealed class VocabularyJsonConverterFactory : JsonConverterFactory
{
    private readonly Vocabularies _vocabularies;

    public VocabularyJsonConverterFactory(Vocabularies vocabularies) { _vocabularies = vocabularies; }

    public override bool CanConvert(Type typeToConvert) => Vocabularies.IsVocabulary(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(VocabularyJsonConverter<>).MakeGenericType(typeToConvert),
                                                _vocabularies.Of(typeToConvert))!;
}

internal sealed class VocabularyJsonConverter<TEntry> : JsonConverter<TEntry> where TEntry : class
{
    private readonly Vocabulary _vocabulary;

    public VocabularyJsonConverter(Vocabulary vocabulary) { _vocabulary = vocabulary; }

    public override TEntry? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return (TEntry)_vocabulary.CreateObject(Find(reader.GetString() ?? ""));
            case JsonTokenType.StartObject:
                break;
            default:
                throw new JsonException($"a {_vocabulary.Name} is its name, or {{ \"{_vocabulary.Key}\": \"<name>\", … }}");
        }

        var obj = (JsonObject)JsonNode.Parse(ref reader)!;
        string? id;
        string? keyName = null;
        foreach (var (name, _) in obj)
            if (string.Equals(name, _vocabulary.Key, StringComparison.OrdinalIgnoreCase)) { keyName = name; break; }
        if (keyName != null)
        {
            if (!_vocabulary.KeyOf(obj, out id) || string.IsNullOrWhiteSpace(id))
                throw new JsonException($"\"{_vocabulary.Key}\" names a {_vocabulary.Name}: one of {string.Join(", ", _vocabulary.Ids)}");
            obj.Remove(keyName);
        }
        else
        {
            id = _vocabulary.Default
                 ?? throw new JsonException($"a {_vocabulary.Name} needs \"{_vocabulary.Key}\": one of {string.Join(", ", _vocabulary.Ids)}");
        }

        var entry = Find(id!);
        // Every field this entry does not have, with the nearest it does, rather than the serializer's
        // "could not be mapped" for the first one.
        var unknown = JsonMembers.Find(obj, entry.Type, options);
        if (unknown.Count > 0)
            throw new JsonException($"{_vocabulary.Name} '{entry.Id}': " + string.Join("; ", unknown.ConvertAll(u => u.Message)));
        return (TEntry?)obj.Deserialize(entry.Type, options) ?? (TEntry)_vocabulary.CreateObject(entry);
    }

    public override void Write(Utf8JsonWriter writer, TEntry value, JsonSerializerOptions options)
    {
        string id = _vocabulary.IdOf(value.GetType()) ?? value.GetType().Name;
        var body = JsonSerializer.SerializeToNode(value, value.GetType(), options) as JsonObject;
        writer.WriteStartObject();
        writer.WriteString(_vocabulary.Key, id);
        if (body != null)
            foreach (var (name, node) in body)
            {
                writer.WritePropertyName(name);
                if (node is null) writer.WriteNullValue();
                else node.WriteTo(writer, options);
            }
        writer.WriteEndObject();
    }

    private VocabularyEntry Find(string id) =>
        _vocabulary.TryFind(id, out var entry) ? entry : throw new JsonException(_vocabulary.Unknown(id));
}
