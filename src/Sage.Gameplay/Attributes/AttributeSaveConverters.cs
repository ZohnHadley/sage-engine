#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sage.Gameplay;

// The attributes plugin's part of the save dialect (SaveJson), added with SaveSystem.AddConverter in
// AttributesModule.Init: attribute values and gameplay tags written by name, never by index. Both are
// ISaveCaptures (issue #602): a save copies the values on the tick, with the names as they are then, and its
// writer writes them on its own thread.
#pragma warning disable SAGE0131 // ISaveCapture is experimental with the rest of saves (4i); the engine's own plugin implements it

// Attribute values by **name**, not by index. `AttributeSet` keeps parallel arrays indexed by the
// order attribute records happened to load in (16 §3.3); add one record and every saved number means
// a different attribute. Only the *base* values are written — the current ones are recomputed from
// base plus whatever effects are running, every tick.
internal sealed class AttributeSetSaveConverter : JsonConverter<AttributeSet>, ISaveCapture
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

    public override void Write(Utf8JsonWriter writer, AttributeSet value, JsonSerializerOptions options) =>
        Write(writer, value, Registries.AttributeIds());

    private static void Write(Utf8JsonWriter writer, AttributeSet value, IReadOnlyList<RecordId> ids)
    {
        writer.WriteStartObject();
        for (int i = 0; i < ids.Count; i++)
        {
            if (!value.Has(i)) continue;
            writer.WriteNumber(ids[i].ToString(), value.BaseOf(i));
        }
        writer.WriteEndObject();
    }

    // Off the tick (issue #602): each set copied on the tick, and the attributes' names as they are then.
    public object Capture(object value) => ((AttributeSet)value).Copy();

    public JsonConverter ForWriter() => new Writer(Registries.AttributeIds().ToArray());

    private sealed class Writer : JsonConverter<AttributeSet>
    {
        private readonly RecordId[] _ids;

        public Writer(RecordId[] ids) => _ids = ids;

        public override AttributeSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("A save's writer only writes");

        public override void Write(Utf8JsonWriter writer, AttributeSet value, JsonSerializerOptions options) =>
            AttributeSetSaveConverter.Write(writer, value, _ids);
    }
}

// Tags by name, for the same reason: `GameplayTags.Bits` is a bitset over indices assigned in record
// order. Only the tags the entity *owns* are written — the ones granted by effects come back when the
// effects do, and writing them would strip real tags the moment effects were re-evaluated.
internal sealed class GameplayTagsSaveConverter : JsonConverter<GameplayTags>, ISaveCapture
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

    public override void Write(Utf8JsonWriter writer, GameplayTags value, JsonSerializerOptions options) =>
        Write(writer, value, Registries.TagIds());

    private static void Write(Utf8JsonWriter writer, GameplayTags value, IReadOnlyList<RecordId> ids)
    {
        writer.WriteStartArray();
        for (int i = 0; i < ids.Count; i++)
        {
            // Owned, not granted: `Granted` is rebuilt from ActiveEffects.
            if (!value.Has(i) || value.Granted.Has(i)) continue;
            writer.WriteStringValue(ids[i].ToString());
        }
        writer.WriteEndArray();
    }

    // Off the tick (issue #602): the tags are bits, copied with the column; their names as they are then.
    public object Capture(object value) => value;

    public JsonConverter ForWriter() => new Writer(Registries.TagIds().ToArray());

    private sealed class Writer : JsonConverter<GameplayTags>
    {
        private readonly RecordId[] _ids;

        public Writer(RecordId[] ids) => _ids = ids;

        public override GameplayTags Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException("A save's writer only writes");

        public override void Write(Utf8JsonWriter writer, GameplayTags value, JsonSerializerOptions options) =>
            GameplayTagsSaveConverter.Write(writer, value, _ids);
    }
}
#pragma warning restore SAGE0131
