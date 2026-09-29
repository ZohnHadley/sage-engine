#nullable enable
using System;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Friflo.Engine.ECS;

namespace Sage.Simulation;

// Components to JSON and back (docs/design/09 §3.3, TODO F27, issue #20).
//
// Whole components through `JsonSerializer`, with the save dialect's converters doing the awkward
// parts (`SaveJson`). The first version of this walked fields by hand to catch `Entity`-valued ones,
// which looked reasonable and was wrong: `ActiveEffect.Source` is an entity *inside a list inside a
// component*, so a rule about a component's own fields silently dropped it. A converter sees it
// wherever it is.
//
// **Keyed by stable id, with a version** (issue #20). Each component is written as
//
//   "sage:transform": { "version": 1, "data": { "LocalPosition": [1, 2, 3], … } }
//
// so renaming the C# struct no longer drops its data (the id does not change), and a field renamed
// or removed is described by an [Upgrade] method that rewrites the old shape on load. A field in the
// save that the type does not have is an error naming the component and the field, never a silent
// loss: the save dialect disallows unmapped members (SaveJson).
//
// What is never written: a component or tag type marked [Transient] (GlobalTransform is derived,
// PhysicsBody is a Bepu handle, …), the two the entity record carries itself (Persistent, FromPrefab),
// and Friflo's own components, which have no id.
//
// The generator (09 §3.2) replaces the reflection with emitted readers and writers. What is written
// does not change when it does, so saves keep loading.
internal sealed class SaveSerializer
{
    private readonly ComponentSchema _schema;

    public SaveSerializer(ComponentSchema schema) => _schema = schema;

    // Written on the entity itself, not among its components: its identity, and how it is rebuilt.
    private static bool OnTheEntity(Type type) => type == typeof(Persistent) || type == typeof(FromPrefab);

    // Asked for every component of every entity a save writes, so the reflection is done once a type.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> Transient = new();

    public static bool IsTransient(Type type) =>
        Transient.GetOrAdd(type, static t => t.GetCustomAttribute<TransientAttribute>() != null);

    public JsonObject WriteComponents(World world, Entity entity, JsonSerializerOptions json)
    {
        var result = new JsonObject();
        foreach (var (id, value) in _schema.ComponentsOf(entity))
        {
            var type = value.GetType();
            if (OnTheEntity(type) || IsTransient(type)) continue;
            var declaration = _schema.DeclarationOf(type)!;
            try
            {
                result[id] = Entry(declaration.Version, JsonSerializer.SerializeToNode(value, type, json));
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException)
            {
                // A field type the dialect cannot express. Named once, so it can be given a converter
                // or marked `[Transient]`, rather than quietly missing from every save.
                Log.Once(LogCat.Save, LogLevel.Error, $"unwritable:{id}",
                    $"{id} cannot be saved: {ex.Message}. Give the field a converter or mark it [Transient] (09 §3.3)");
            }
        }
        return result;
    }

    // `{ "version": n, "data": … }`: the shape of every component and saved resource in a save.
    public static JsonObject Entry(int version, JsonNode? data) => new() { ["version"] = version, ["data"] = data };

    // Tags are presence, so they are a list of ids.
    public JsonArray WriteTags(World world, Entity entity)
    {
        var result = new JsonArray();
        foreach (string tag in _schema.TagsOf(entity))
            if (_schema.TryTag(tag, out var type) && !IsTransient(type.Type)) result.Add(tag);
        return result;
    }

    public void ReadComponents(World world, Entity entity, JsonObject components, JsonSerializerOptions json, string where)
    {
        foreach (var (key, node) in components)
        {
            if (node is null) continue;
            ComponentDeclaration? declaration = null;
            if (_schema.TryComponent(key, out var found)) declaration = _schema.DeclarationOf(found.Type);
            else if (_schema.TryFormerComponent(key, typeNames: false, out var former)) declaration = former;

            if (declaration is null)
            {
                // A component from a mod that is no longer loaded, or one since removed. The rest of
                // the entity still loads (09 §3.6).
                Log.Once(LogCat.Save, LogLevel.Warn, $"unknown-component:{key}",
                    $"{where}: no component '{key}' any more; its data is skipped");
                continue;
            }
            if (OnTheEntity(declaration.Type) || IsTransient(declaration.Type)) continue;

            object? value = ReadEntry(declaration.Type, declaration.Id, declaration.Version, node, json, $"{where}: component '{declaration.Id}'");
            if (value != null && _schema.TryComponent(declaration.Id, out var type))
                EntityUtils.AddEntityComponentValue(entity, type, value);
        }
    }

    // One `{ "version", "data" }` entry, brought up to date by the type's upgraders and read as that
    // type. Null, having said why, when it cannot be: the caller keeps whatever it had (a prefab's
    // component, a fresh resource), which is the most a load can honestly do with it.
    public static object? ReadEntry(Type type, string id, int current, JsonNode entry, JsonSerializerOptions json, string what)
    {
        if (entry is not JsonObject wrapper || wrapper["version"] is not JsonValue v || !v.TryGetValue(out int version))
        {
            Log.Error(LogCat.Save, $"{what}: expected {{ \"version\": n, \"data\": … }}");
            return null;
        }
        if (version > current)
        {
            Log.Error(LogCat.Save, $"{what} was saved at version {version}, and this build knows version {current}: " +
                                   "a newer game wrote it; skipped");
            return null;
        }

        var data = wrapper["data"];
        wrapper.Remove("data");   // detached, so an upgrader may replace it and it can be re-parented
        try
        {
            if (version < current)
            {
                if (data is JsonObject fields)
                    data = Upgraders.Run(type, fields, version, current);
                else if (Upgraders.Of(type).Count > 0)
                {
                    Log.Error(LogCat.Save, $"{what}: saved data is not an object, so its [Upgrade] methods cannot run");
                    return null;
                }
            }
            return data is null ? null : JsonSerializer.Deserialize(data.ToJsonString(), type, json);
        }
        catch (JsonException ex) when (ex.Message.Contains("could not be mapped", StringComparison.Ordinal))
        {
            // The one that used to lose data without a word: a field the save has and the type
            // doesn't, because it was renamed or removed. Named, with what to do about it.
            string field = ex.Path is { Length: > 2 } path ? path.Substring(2) : "?";
            Log.Error(LogCat.Save, $"{what} ({type.Name}), saved at version {version}: field '{field}' is not on {type.Name} " +
                $"any more. Bump its Version and add an [Upgrade({version})] method that renames or removes it (issue #20); skipped");
            return null;
        }
        // Anything else, upgraders included: they are game code run on old data, and one that throws
        // costs its component, not the load.
        catch (Exception ex)
        {
            Log.Error(LogCat.Save, $"{what} ({type.Name}): {ex.InnerException?.Message ?? ex.Message}");
            return null;
        }
    }

    public void ReadTags(World world, Entity entity, JsonArray tags, string where)
    {
        foreach (var node in tags)
        {
            string? name = (string?)node;
            if (string.IsNullOrEmpty(name)) continue;
            TagType? tag = null;
            if (_schema.TryTag(name, out var found)) tag = found;
            else if (_schema.TryFormerTag(name, typeNames: false, out var former) && _schema.TryTag(former.Id, out var renamed)) tag = renamed;

            if (tag is null)
            {
                Log.Once(LogCat.Save, LogLevel.Warn, $"unknown-tag:{name}", $"{where}: no tag '{name}' any more; skipped");
                continue;
            }
            if (IsTransient(tag.Type)) continue;
            var set = new Tags(tag);
            entity.AddTags(set);
        }
    }
}
