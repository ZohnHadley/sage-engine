#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> Placement = new();

    // A component type whose values derive from where the entity was placed (`[FromPlacement]`, 4m-4).
    public static bool IsFromPlacement(Type type) =>
        Placement.GetOrAdd(type, static t => t.GetCustomAttribute<FromPlacementAttribute>() != null);

    private static bool Contains(IReadOnlyList<string> ids, string id)
    {
        for (int i = 0; i < ids.Count; i++)
            if (string.Equals(ids[i], id, StringComparison.Ordinal)) return true;
        return false;
    }

    // The ids of the entity's `[FromPlacement]` components.
    public IReadOnlyList<string> FromPlacementIds(Entity entity)
    {
        List<string>? ids = null;
        foreach (var (id, value) in _schema.ComponentsOf(entity))
            if (IsFromPlacement(value.GetType())) (ids ??= new List<string>()).Add(id);
        return ids ?? (IReadOnlyList<string>)Array.Empty<string>();
    }

    // The entity's declared components as boxed copies, by id: what a prefab spawned it with, kept beside its
    // baseline (issue #285) so a save can tell an unchanged one without serialising it.
    public Dictionary<string, object> ValuesOf(Entity entity)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (id, value) in _schema.ComponentsOf(entity)) values[id] = value;
        return values;
    }

    public JsonObject WriteComponents(World world, Entity entity, JsonSerializerOptions json) =>
        WriteComponents(world, entity, json, baseline: null, removed: null);

    // With a `baseline` (4i-5, SaveDiff): only what differs from it — the fields that changed, and the
    // components the prefab did not give it in full — and the baseline's components the entity no longer
    // has go in `removed`. `quiet`: taking a baseline at spawn, where an unwritable field is the save's
    // business to report, not the spawn's.
    // `only`: just these component ids (a spawn's `[FromPlacement]` ones, 4m-4).
    // (A save does not come through here: it captures the entity's columns on the tick and writes them on
    // its writer's thread, SaveCapture.cs, with the same Diffed.)
    public JsonObject WriteComponents(World world, Entity entity, JsonSerializerOptions json, SpawnBaseline? baseline,
                                      JsonArray? removed, bool quiet = false, IReadOnlyList<string>? only = null)
    {
        var result = new JsonObject();
        HashSet<string>? had = baseline != null ? new HashSet<string>(StringComparer.Ordinal) : null;
        foreach (var (id, value) in _schema.ComponentsOf(entity))
        {
            var type = value.GetType();
            if (!IsSaved(type)) continue;
            if (only != null && !Contains(only, id)) continue;
            var declaration = _schema.DeclarationOf(type)!;
            had?.Add(id);
            try
            {
                var data = JsonSerializer.SerializeToNode(value, type, json);
                if (Diffed(id, declaration.Version, type, data, baseline, json) is { } entry) result[id] = entry;
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException)
            {
                if (quiet) continue;
                Unwritable(id, ex);
            }
        }
        if (baseline != null && removed != null)
            foreach (var (id, _) in baseline.Components)
                if (!had!.Contains(id)) removed.Add(id);
        return result;
    }

    // A component type a save writes among the entity's components: not one the entity record carries
    // itself, nor one marked [Transient].
    public static bool IsSaved(Type type) => !OnTheEntity(type) && !IsTransient(type);

    // A field type the dialect cannot express. Named once, so it can be given a converter or marked
    // `[Transient]`, rather than quietly missing from every save.
    public static void Unwritable(string id, Exception ex) =>
        Log.Once(LogCat.Save, LogLevel.Error, $"unwritable:{id}",
            $"{id} cannot be saved: {ex.Message}. Give the field a converter or mark it [Transient] (09 §3.3)");

    // The entry a save writes for a component's `data`: in full, or, against the baseline its prefab spawned
    // it with (4i-5, SaveDiff), only the fields that changed. Null when nothing did: the component is left out.
    public static JsonObject? Diffed(string id, int version, Type type, JsonNode? data, SpawnBaseline? baseline,
                                     JsonSerializerOptions json)
    {
        if (baseline != null && id != TransformId
            && baseline.Entry(id) is JsonObject before
            && before["version"] is JsonValue v && v.TryGetValue(out int was) && was == version)
        {
            var wasData = before["data"];
            if (data is JsonObject fields && wasData is JsonObject wasFields && SaveDiff.ByField(type, json))
            {
                var changed = SaveDiff.Fields(fields, wasFields);
                if (changed.Count == 0) return null;
                data = changed;
            }
            else if (JsonNode.DeepEquals(data, wasData)) return null;
        }
        return Entry(version, data);
    }

    // Whether a component is still, bit for bit, what its prefab spawned it with (issue #285): then Diffed
    // would leave it out, and nothing need be serialised to know it. False only means "serialise it".
    public static bool UnchangedSinceSpawn(string id, int version, Type type, object value, SpawnBaseline? baseline) =>
        baseline != null && id != TransformId
        && baseline.Entry(id) is JsonObject spawned
        && spawned["version"] is JsonValue v && v.TryGetValue(out int was) && was == version
        && baseline.ValueAsSpawned(id) is { } asSpawned && SameBits(type, value, asSpawned);

    // Whether a component type can be serialised off the tick: a struct whose fields, all the way down, are
    // values or strings (a copy of it is the whole of it, and nothing it says can change under the writer),
    // with no [JsonConverter] of its own and no converter of a plugin's (which may read the world) for it or
    // anything in it. Asked on the tick; remembered per type.
    private readonly Dictionary<Type, bool> _deferrable = new();

    public bool CanDefer(Type type, JsonSerializerOptions json)
    {
        if (_deferrable.TryGetValue(type, out bool known)) return known;
        bool result = Plain(type, json, new HashSet<Type>());
        _deferrable[type] = result;
        return result;
    }

    private static bool Plain(Type type, JsonSerializerOptions json, HashSet<Type> seen)
    {
        if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)) return true;
        if (!type.IsValueType || type.IsPointer || type.IsByRefLike) return false;
        if (!seen.Add(type)) return true;
        if (type.GetCustomAttribute<JsonConverterAttribute>() != null) return false;
        foreach (var converter in json.Converters)
            if (!IsStateless(converter) && converter.CanConvert(type)) return false;
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetCustomAttribute<JsonConverterAttribute>() != null) return false;
            if (!Plain(field.FieldType, json, seen)) return false;
        }
        return true;
    }

    // SaveJson's own converters that read nothing but the value.
    private static bool IsStateless(JsonConverter converter) =>
        converter is Vector2JsonConverter or Vector3JsonConverter or QuaternionJsonConverter or JsonStringEnumConverter;

    // The same value, bit for bit (a struct with no references), or by its fields' Equals (one with
    // strings). Equal here means the JSON would be the same; unequal only means it is serialised.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, object, bool>> Comparers = new();

    public static bool SameBits(Type type, object a, object b) =>
        a.GetType() == b.GetType() && Comparers.GetOrAdd(type, MakeComparer)(a, b);

    private static Func<object, object, bool> MakeComparer(Type type)
    {
        bool references = (bool)typeof(System.Runtime.CompilerServices.RuntimeHelpers)
            .GetMethod(nameof(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences))!
            .MakeGenericMethod(type).Invoke(null, null)!;
        if (references) return static (a, b) => a.Equals(b);
        return typeof(SaveSerializer).GetMethod(nameof(BitEquals), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(type).CreateDelegate<Func<object, object, bool>>();
    }

    private static bool BitEquals<T>(object a, object b) where T : struct
    {
        ref T x = ref System.Runtime.CompilerServices.Unsafe.Unbox<T>(a);
        ref T y = ref System.Runtime.CompilerServices.Unsafe.Unbox<T>(b);
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref x, 1))
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref y, 1)));
    }

    // Always written in full, never diffed (SaveDiff): a runtime spawn is rebuilt where it stood.
    internal const string TransformId = "sage:transform";

    // `{ "version": n, "data": … }`: the shape of every component and saved resource in a save.
    public static JsonObject Entry(int version, JsonNode? data) => new() { ["version"] = version, ["data"] = data };

    // Tags are presence, so a save writes them as a list of ids: these, the entity's saved ones.
    public List<string> SavedTags(Entity entity)
    {
        var result = new List<string>();
        foreach (string tag in _schema.TagsOf(entity))
            if (_schema.TryTag(tag, out var type) && !IsTransient(type)) result.Add(tag);
        return result;
    }

    // A declared component type's declaration; false for Friflo's own and anything undeclared.
    public bool TryDeclared(Type type, out ComponentDeclaration declaration)
    {
        declaration = _schema.DeclarationOf(type)!;
        return declaration != null && !declaration.IsTag;
    }

    // `unknown` collects, as saved, every entry whose id matches no component here, so the next save
    // can write it back unchanged (issue 4i-2). `merge`: the entries are diffs (4i-5, SaveDiff), laid field
    // by field onto the component the entity has; without it, or when it has none, an entry replaces it.
    public void ReadComponents(World world, Entity entity, JsonObject components, JsonSerializerOptions json, string where,
                               JsonObject? unknown = null, bool merge = false)
    {
        foreach (var (key, node) in components)
        {
            if (node is null) continue;
            ComponentDeclaration? declaration = null;
            if (_schema.TryComponent(key, out var found)) declaration = _schema.DeclarationOf(found);
            else if (_schema.TryFormerComponent(key, typeNames: false, out var former)) declaration = former;

            if (declaration is null)
            {
                // A component from a mod that is no longer loaded, or one since removed. The rest of
                // the entity still loads (09 §3.6), and the entry is kept, as saved, for the next save.
                Log.Once(LogCat.Save, LogLevel.Warn, $"unknown-component:{key}",
                    $"{where}: no component '{key}' in this game; its data is kept for the next save, unused");
                unknown?.Add(key, node.DeepClone());
                continue;
            }
            if (OnTheEntity(declaration.Type) || IsTransient(declaration.Type)) continue;

            // The fields a diff names, as upgraded: only those are laid over what the entity has.
            JsonObject? fields = null;
            Action<JsonObject>? upgraded = merge ? f => fields = (JsonObject)f.DeepClone() : null;
            object? value = ReadEntry(declaration.Type, declaration.Id, declaration.Version, node, json,
                                      $"{where}: component '{declaration.Id}'", upgraded);
            if (value is null || !_schema.TryComponent(declaration.Id, out var type)) continue;
            if (fields != null && SaveDiff.ByField(type, json) && _schema.Read(entity, type) is { } current)
                value = SaveDiff.Merge(type, current, value, fields, json);
            _schema.Write(entity, type, value);
        }
    }

    // A diffed entity's `removed` (4i-5): the components its prefab gave it that it did not have when saved.
    public void RemoveComponents(Entity entity, JsonArray removed)
    {
        foreach (var node in removed)
        {
            string? key = (string?)node;
            if (string.IsNullOrEmpty(key)) continue;
            ComponentDeclaration? declaration = null;
            if (_schema.TryComponent(key, out var found)) declaration = _schema.DeclarationOf(found);
            else if (_schema.TryFormerComponent(key, typeNames: false, out var former)) declaration = former;
            // One this game does not have is not on the entity either.
            if (declaration is null || OnTheEntity(declaration.Type) || IsTransient(declaration.Type)) continue;
            if (_schema.TryComponent(declaration.Id, out var type)) _schema.Remove(entity, type);
        }
    }

    // One `{ "version", "data" }` entry, brought up to date by the type's upgraders and read as that
    // type. Null, having said why, when it cannot be: the caller keeps whatever it had (a prefab's
    // component, a fresh resource), which is the most a load can honestly do with it.
    // `upgraded`: told the data object as upgraded, before it is read (a diff needs to know which fields it names).
    public static object? ReadEntry(Type type, string id, int current, JsonNode entry, JsonSerializerOptions json, string what,
                                    Action<JsonObject>? upgraded = null)
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
            if (data is JsonObject read) upgraded?.Invoke(read);
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

    // Every saved tag off, before a save's are put on an entity content placed (4i-3): a tag the game took
    // off (a creature no longer hostile) stays off, rather than coming back because the prefab has it.
    public void ClearTags(Entity entity)
    {
        foreach (string id in System.Linq.Enumerable.ToList(_schema.TagsOf(entity)))
            if (_schema.TryTag(id, out var type) && !IsTransient(type)) _schema.RemoveTag(entity, type);
    }

    public void ReadTags(World world, Entity entity, JsonArray tags, string where, JsonArray? unknown = null)
    {
        foreach (var node in tags)
        {
            string? name = (string?)node;
            if (string.IsNullOrEmpty(name)) continue;
            Type? tag = null;
            if (_schema.TryTag(name, out var found)) tag = found;
            else if (_schema.TryFormerTag(name, typeNames: false, out var former) && _schema.TryTag(former.Id, out var renamed)) tag = renamed;

            if (tag is null)
            {
                Log.Once(LogCat.Save, LogLevel.Warn, $"unknown-tag:{name}",
                    $"{where}: no tag '{name}' in this game; kept for the next save, unused");
                unknown?.Add(name);
                continue;
            }
            if (IsTransient(tag)) continue;
            _schema.AddTag(entity, tag);
        }
    }
}
