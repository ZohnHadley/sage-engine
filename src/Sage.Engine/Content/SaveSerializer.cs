#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Friflo.Engine.ECS;

namespace sage_engine;

// Components to JSON and back (docs/design/09 §3.3, TODO F27).
//
// Whole components through `JsonSerializer`, with the save dialect's converters doing the awkward
// parts (`SaveJson`). The first version of this walked fields by hand to catch `Entity`-valued ones,
// which looked reasonable and was wrong: `ActiveEffect.Source` is an entity *inside a list inside a
// component*, so a rule about a component's own fields silently dropped it. A converter sees it
// wherever it is.
//
// Two components are skipped whole rather than field by field:
// `GlobalTransform` is derived from `Transform` and the parent chain, and its `Previous` is a
// one-tick interpolation snapshot — restoring it makes the first frame after a load lerp from a pose
// that never existed. `PhysicsBody` is a Bepu handle, rebuilt from `Collider`/`RigidBody`.
//
// The generator (09 §3.2) replaces the reflection with emitted readers and writers. What is written
// does not change when it does, so saves keep loading.
internal sealed class SaveSerializer
{
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(Persistent),       // the entity's identity: written on the entity, not among its components
        nameof(FromPrefab),       // how it is rebuilt: likewise
        nameof(GlobalTransform),  // derived from Transform; Previous is a render snapshot
        "PhysicsBody",            // a Bepu handle (10 §3): rebuilt from Collider and RigidBody
        "EntityName",             // a name is a string, written on the entity itself (09 §3.4): the
                                  // component also carries the UTF-8 bytes of it, base64-encoded
        "IOConnections",          // a level's wiring (04 §3.4): it comes from the map, the same as the
                                  // walls do, and it holds resolved entity handles that mean nothing in
                                  // another session. Nothing map-spawned is persistent today, so this is
                                  // a guard rather than a fix — but it is the kind that is cheap now and
                                  // an afternoon later.
    };

    private readonly ComponentSchema _schema;

    public SaveSerializer(ComponentSchema schema) => _schema = schema;

    public JsonObject WriteComponents(World world, Entity entity, JsonSerializerOptions json)
    {
        var result = new JsonObject();
        foreach (var (name, value) in _schema.ComponentsOf(entity))
        {
            if (Skip.Contains(name)) continue;
            try
            {
                result[name] = JsonSerializer.SerializeToNode(value, value.GetType(), json);
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException)
            {
                // A field type the dialect cannot express. Named once, so it can be given a converter
                // or marked `[Transient]`, rather than quietly missing from every save.
                Log.Once(LogCat.Save, LogLevel.Error, $"unwritable:{name}",
                    $"{name} cannot be saved: {ex.Message}. Give the field a converter or mark it [Transient] (09 §3.3)");
            }
        }
        return result;
    }

    // Tags are presence, so they are a list of names.
    public JsonArray WriteTags(World world, Entity entity)
    {
        var result = new JsonArray();
        foreach (string tag in _schema.TagsOf(entity))
            if (!SkipTag(tag)) result.Add(tag);
        return result;
    }

    // `FromScene` marks what the Sandbox's hot reload sweeps away and re-places (R15). Saving it would
    // make loaded entities eligible for that sweep, or have them double up with the scene's own.
    private static bool SkipTag(string tag) => tag is "FromScene" or "Disabled";

    public void ReadComponents(World world, Entity entity, JsonObject components, JsonSerializerOptions json, string where)
    {
        foreach (var (name, node) in components)
        {
            if (node is null || Skip.Contains(name)) continue;
            if (!_schema.TryComponent(name, out var type))
            {
                // A component from a mod that is no longer loaded, or one since removed. The rest of
                // the entity still loads (09 §3.6).
                Log.Once(LogCat.Save, LogLevel.Warn, $"unknown-component:{name}",
                    $"{where}: no component type '{name}' any more; its data is skipped");
                continue;
            }

            try
            {
                object? value = JsonSerializer.Deserialize(node.ToJsonString(), type.Type, json);
                if (value != null) EntityUtils.AddEntityComponentValue(entity, type, value);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                Log.Error(LogCat.Save, $"{where}: {name}: {ex.Message}");
            }
        }
    }

    public void ReadTags(World world, Entity entity, JsonArray tags, string where)
    {
        foreach (var node in tags)
        {
            string? name = (string?)node;
            if (string.IsNullOrEmpty(name) || SkipTag(name)) continue;
            if (!_schema.TryTag(name, out var tag))
            {
                Log.Once(LogCat.Save, LogLevel.Warn, $"unknown-tag:{name}", $"{where}: no tag '{name}' any more; skipped");
                continue;
            }
            var set = new Tags(tag);
            entity.AddTags(set);
        }
    }
}
