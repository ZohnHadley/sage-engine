#nullable enable
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Gameplay;

// The attributes plugin's part of the save dialect (SaveJson), added with SaveSystem.AddConverter in
// AttributesModule.Init: attribute values and gameplay tags written by name, never by index.

// Attribute values by **name**, not by index. `AttributeSet` keeps parallel arrays indexed by the
// order attribute records happened to load in (16 §3.3); add one record and every saved number means
// a different attribute. Only the *base* values are written — the current ones are recomputed from
// base plus whatever effects are running, every tick.
internal sealed class AttributeSetSaveConverter : JsonConverter<AttributeSet>
{
    private readonly World _world;
    private readonly RecordStore _records;

    public AttributeSetSaveConverter(World world, RecordStore records)
    {
        _world = world;
        _records = records;
    }

    private GameplayRegistries Registries => _world.Resources.Get<GameplayRegistries>();

    public override AttributeSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var set = new AttributeSet();
        if (reader.TokenType == JsonTokenType.Null) return set;
        var registries = Registries;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            string name = reader.GetString() ?? "";
            reader.Read();
            float value = reader.TokenType == JsonTokenType.Number ? reader.GetSingle() : 0f;

            int index = registries.Attribute(RecordId.Parse(name, "sage"));
            // An attribute the game no longer has: its value goes, the rest of the entity loads.
            if (index >= 0) set.SetBase(index, value);
        }
        return set;
    }

    public override void Write(Utf8JsonWriter writer, AttributeSet value, JsonSerializerOptions options)
    {
        var registries = Registries;
        writer.WriteStartObject();
        for (int i = 0; i < registries.AttributeCount; i++)
        {
            if (!value.Has(i)) continue;
            writer.WriteNumber(registries.AttributeId(i).ToString(), value.BaseOf(i));
        }
        writer.WriteEndObject();
    }
}

// Tags by name, for the same reason: `GameplayTags.Bits` is a bitset over indices assigned in record
// order. Only the tags the entity *owns* are written — the ones granted by effects come back when the
// effects do, and writing them would strip real tags the moment effects were re-evaluated.
internal sealed class GameplayTagsSaveConverter : JsonConverter<GameplayTags>
{
    private readonly World _world;
    private readonly RecordStore _records;

    public GameplayTagsSaveConverter(World world, RecordStore records)
    {
        _world = world;
        _records = records;
    }

    private GameplayRegistries Registries => _world.Resources.Get<GameplayRegistries>();

    public override GameplayTags Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var tags = new GameplayTags();
        if (reader.TokenType != JsonTokenType.StartArray) return tags;
        var registries = Registries;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) continue;
            int index = registries.Tag(RecordId.Parse(reader.GetString() ?? "", "sage"));
            if (index >= 0) tags.Add(index);
        }
        return tags;
    }

    public override void Write(Utf8JsonWriter writer, GameplayTags value, JsonSerializerOptions options)
    {
        var registries = Registries;
        writer.WriteStartArray();
        for (int i = 0; i < registries.TagCount; i++)
        {
            // Owned, not granted: `Granted` is rebuilt from ActiveEffects.
            if (!value.Has(i) || value.Granted.Has(i)) continue;
            writer.WriteStringValue(registries.TagId(i).ToString());
        }
        writer.WriteEndArray();
    }
}
