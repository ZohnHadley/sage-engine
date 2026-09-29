#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Sage.Simulation;

// The JSON dialect saves are written in (docs/design/09 §3.3, TODO F27): the record pipeline's, plus
// three things it cannot do and one it does wrongly for this purpose.
//
// It is built per save and per load because its converters need the **world** — an entity id, an
// attribute name and a tag name only mean anything against one. The entity converter is here; a
// plugin adds its own with SaveSystem.AddConverter (attributes and tags by name: AttributesModule).
//
// Doing it with converters rather than field-by-field reflection is what makes nesting work.
// `ActiveEffect.Source` is an `Entity` inside a `List<ActiveEffect>` inside a component; a rule that
// only looked at a component's own fields would write it as null and say nothing, which is exactly
// what the record pipeline's own `Entity` converter does (deliberately, for prefabs — it is wrong
// here, and it is the first thing this file replaces).
internal static class SaveJson
{
    public static JsonSerializerOptions For(World world, RecordStore records,
                                            IReadOnlyList<Func<World, RecordStore, JsonConverter>> extra)
    {
        var options = Base(world);
        // Plugins' own converters (SaveSystem.AddConverter): gameplay's attributes and tags by name.
        foreach (var make in extra) options.Converters.Add(make(world, records));
        return options;
    }

    private static JsonSerializerOptions Base(World world) => new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,               // components are public fields
        // A field in the save that the type no longer has is an error, not a silent loss (issue #20):
        // it was renamed or removed, and an [Upgrade] method says which. SaveSerializer names it.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // An effect with no end has `Remaining = +∞` (16 §3.3), which is real data and not an
        // accident, so the dialect has to be able to say it. Written as "Infinity" and read back.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        // Not `records.Json`'s converter list wholesale: that one maps `Entity` to null on purpose.
        Converters =
        {
            new Vector2JsonConverter(),
            new Vector3JsonConverter(),
            new QuaternionJsonConverter(),
            new JsonStringEnumConverter(),
            new EntitySaveConverter(world),
        },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { DropTransient } },
    };

    // Makes `[Transient]` mean something without waiting for the generator: the member is removed from
    // the type's contract, so it is neither written nor read.
    private static void DropTransient(JsonTypeInfo info)
    {
        for (int i = info.Properties.Count - 1; i >= 0; i--)
        {
            var member = (MemberInfo?)info.Properties[i].AttributeProvider;
            if (member?.GetCustomAttribute<TransientAttribute>() != null) info.Properties.RemoveAt(i);
        }
    }
}

// An entity by identity, not by handle. A handle is a slot and a revision in *this* run; a
// `PersistentId` is the same thing next time. Reading resolves immediately, which is why loading
// creates every entity before it applies any components — with them all present there is nothing to
// fix up afterwards, and a reference nested three deep works like any other field.
internal sealed class EntitySaveConverter : JsonConverter<Entity>
{
    private readonly World _world;

    public EntitySaveConverter(World world) => _world = world;

    public override Entity Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return default;
        // Something the save pointed at that is not in it: the null entity. A creature whose target
        // died between save and load picks another, which is what it would do in play (16 §3.4).
        return PersistentId.TryParse(reader.GetString(), out var id) ? _world.Resolve(id) : default;
    }

    public override void Write(Utf8JsonWriter writer, Entity value, JsonSerializerOptions options)
    {
        // An entity with no `Persistent` cannot be pointed at across a save, so the reference is
        // dropped rather than written wrong: whatever it pointed at will not be there either.
        if (value.IsNull || !_world.TryGet<Persistent>(value, out var persistent) || persistent.Id.IsEmpty)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStringValue(persistent.Id.ToString());
    }
}
