#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Sage.Simulation;

// Hot reload reaches the instances (issue #287): edit a prefab's component values while the game runs, and
// every entity spawned from it that the game did not change follows the edit — the player the rules
// spawned, an `ent_spawn` crate, a summon, a prefab's children — not only what the next spawn makes.
// (`prefab` is `[Record(Reload = ReloadPolicy.Live)]`; the scene's own placements are placed again by
// Scenes.Respawn anyway, and arrive here already new.)
//
// **What the prefab changed** is its body as each entity was spawned (`SpawnBaseline.Body`: `components`
// and `tags`, its overrides merged in) against the body now, with the same overrides. The record itself
// cannot say: a reload updates it in place, so the old values are gone from it. Compared field by field
// at a component's top level, as a save diffs (SaveDiff): a list or a nested struct is one value.
//
// **What "unmodified" means** — the same thing a save means by it. A field is unmodified when the
// entity's value is still the one it was spawned with (`SpawnBaseline`, taken right after its prefab and
// overrides were applied: the field a save would *not* write). So:
// - a placement's or a map's override is part of the body on both sides; the reload leaves the field
//   alone unless the override itself changed (it wins over the prefab, as at spawn);
// - a value a save laid back on load differs from what the load spawned it as, so it is kept;
// - a value code or the game set (damage taken, a door opened, a light the player dimmed) differs too,
//   and is kept — field by field, so the same component's other fields still follow the prefab.
// A component a save does not write (`[Transient]`) has no spawn baseline; its field is unmodified when it
// still equals what the old body said.
//
// **What follows a reload**: a changed field of a component the prefab writes; a component the prefab now
// writes that the entity does not have (added, whole); a tag the prefab now has (added) or no longer has
// (taken off, if the entity still has it). **What waits for the next spawn**: a component the prefab no
// longer writes (something else, a part, may have given it), `parts` (a part is a setup that derives
// several components; its options are read once, at spawn), `children`, `name`, `persist`, and a
// component's `Transform` (placement beats the template). A reload does not fire `Spawned` or anything else.
//
// Afterwards each entity's baseline says what it would be spawned as now, so a save diffs it against the
// new prefab — a field it kept is written, and a load spawns the new prefab and lays it back.
public sealed partial class SaveSystem
{
    // Records.Reloaded, after the scenes are placed again (Engine). Returns the fields set, for tests.
    internal int ReloadPrefabInstances()
    {
        if (_engine.Records.ReloadPolicyOf("prefab") != ReloadPolicy.Live) return 0;
        int fields = 0, entities = 0;
        foreach (var world in _engine.Worlds)
            fields += ReloadPrefabInstances(world, ref entities);
        if (entities > 0)
            Log.Info(LogCat.Records, $"Prefabs reloaded: {fields} field(s) and tag(s) on {entities} live instance(s) follow the edit");
        return fields;
    }

    private int ReloadPrefabInstances(World world, ref int entities)
    {
        var instances = world.Query<FromPrefab>().Entities.ToEntityList();
        if (instances.Count == 0) return 0;
        var baselines = world.Resources.GetOrAdd(() => new PrefabBaselines());
        var dialect = baselines.Dialect ??= SaveJson.For(world, _engine.Records, _converters);
        // One delta per shared baseline: every entity spawned from the same prefab and overrides shares one.
        var deltas = new Dictionary<SpawnBaseline, PrefabDelta?>(ReferenceEqualityComparer.Instance);
        int set = 0;
        foreach (var entity in instances)
        {
            if (!world.IsAlive(entity)) continue;
            var from = world.Get<FromPrefab>(entity);
            if (from.Baseline is not { Body: not null } baseline || from.Prefab.IsEmpty) continue;
            var shared = baseline.Shared;
            if (!deltas.TryGetValue(shared, out var delta))
            {
                var overrides = world.TryGet<PrefabOverridden>(entity, out var overridden) ? overridden.Overrides : null;
                deltas[shared] = delta = PrefabDelta.Of(_engine, world, from.Prefab, shared, overrides, dialect);
            }
            if (delta == null) continue;
            int changed = delta.Apply(world, entity, baseline, dialect);
            if (changed > 0) { set += changed; entities++; }
            world.Get<FromPrefab>(entity).Baseline = delta.Rebase(baseline);
        }
        return set;
    }
}

// What one prefab (with one set of overrides) changed across a reload, and how to lay it on an instance.
internal sealed class PrefabDelta
{
    private readonly Engine _engine;
    private readonly RecordId _prefab;

    // A component the body changed: the fields it changed (null: added, or not an object of fields, so the
    // component is one value), and its body now (read again for each entity, so no two share a list).
    private readonly List<(Type Type, string Id, int Version, string[]? Fields, bool Had, JsonNode? Body, JsonNode? OldBody)> _components = new();
    private readonly List<Type> _tagsAdded = new();
    private readonly List<Type> _tagsRemoved = new();
    private readonly SpawnBaseline _newShared;

    private PrefabDelta(Engine engine, RecordId prefab, SpawnBaseline newShared)
    {
        _engine = engine;
        _prefab = prefab;
        _newShared = newShared;
    }

    // Null when the prefab is gone or its body did not change.
    public static PrefabDelta? Of(Engine engine, World world, RecordId prefab, SpawnBaseline shared, PrefabOverrides? overrides,
                                  JsonSerializerOptions dialect)
    {
        if (!engine.Records.TryGet(prefab, out PrefabRecord record)) return null;
        var now = PrefabOverriding.Apply(engine, prefab, record, overrides);
        var oldBody = shared.Body!;
        var newBody = now.Components ?? new JsonObject();
        var oldTags = shared.Tags ?? Array.Empty<string>();
        if (JsonNode.DeepEquals(oldBody, newBody) && oldTags.SequenceEqual(now.Tags, StringComparer.Ordinal)) return null;

        var schema = engine.Components;
        string ns = prefab.Namespace;
        var before = Resolve(schema, oldBody, ns);
        var after = Resolve(schema, newBody, ns);

        // The baseline as it would be taken now: the old one, with what the prefab changed put in.
        var components = (JsonObject)shared.Components.DeepClone();
        var delta = new PrefabDelta(engine, prefab, new SpawnBaseline(components, shared.FromPlacement)
        {
            Body = (JsonObject)newBody.DeepClone(),
            Tags = now.Tags.ToArray(),
        });

        foreach (var (type, body) in after)
        {
            if (type == typeof(Transform)) continue;   // placement beats the template, at spawn and here
            var declaration = schema.DeclarationOf(type);
            if (declaration == null) continue;
            before.TryGetValue(type, out var was);
            bool had = before.ContainsKey(type);
            if (had && JsonNode.DeepEquals(was, body)) continue;

            string[]? fields = null;
            if (had && was is JsonObject wasFields && body is JsonObject newFields && SaveDiff.ByField(type, dialect))
            {
                fields = Changed(wasFields, newFields, dialect.GetTypeInfo(type));
                if (fields.Length == 0) continue;
            }
            delta._components.Add((type, declaration.Id, declaration.Version, fields, had, body?.DeepClone(), was?.DeepClone()));

            // Into the new baseline: the component as the body now gives it, in the save's dialect.
            // Not a [Transient] one: a save never writes it, and an entry would read as taken off.
            if (!SaveSerializer.IsTransient(type) && delta.Read(type, body) is { } value && Write(value, type, dialect) is { } data)
                Patch(components, declaration.Id, declaration.Version, fields, data);
        }

        foreach (string name in now.Tags.Except(oldTags, StringComparer.Ordinal))
            if (schema.TryResolveTag(name, ns, out var tag, out _)) delta._tagsAdded.Add(tag);
        foreach (string name in oldTags.Except(now.Tags, StringComparer.Ordinal))
            if (schema.TryResolveTag(name, ns, out var tag, out _) && !delta._tagsAdded.Contains(tag)) delta._tagsRemoved.Add(tag);
        return delta;
    }

    // Lays the change on one instance, field by field where it is unmodified. Returns what it set.
    public int Apply(World world, Entity entity, SpawnBaseline baseline, JsonSerializerOptions dialect)
    {
        var schema = _engine.Components;
        int set = 0;
        foreach (var (type, id, version, fields, had, body, oldBody) in _components)
        {
            var current = schema.Read(entity, type);
            if (fields == null)
            {
                if (had)
                {
                    // A changed component that is one value: replaced when the entity still has it as spawned
                    // (absent: the game took it off, a change of its own).
                    if (current == null) continue;
                    var asSpawned = SpawnedAs(baseline, id, version, type, oldBody, dialect);
                    if (asSpawned == null || !JsonNode.DeepEquals(Write(current, type, dialect), asSpawned)) continue;
                }
                // One the prefab now writes: added, unless the entity has it from something else (a part,
                // the game), or had it at spawn and lost it.
                else if (current != null || HadAtSpawn(baseline, id)) continue;
                if (Read(type, body) is { } whole) { schema.Write(entity, type, whole); set++; }
                continue;
            }

            if (current == null) continue;   // the game took it off
            if (SpawnedAs(baseline, id, version, type, oldBody, dialect) is not JsonObject spawnedFields) continue;
            if (Write(current, type, dialect) is not JsonObject nowFields) continue;
            object? fresh = null;
            var info = dialect.GetTypeInfo(type);
            bool any = false;
            foreach (var property in info.Properties)
            {
                if (property.Get is null || property.Set is null || !fields.Contains(property.Name, StringComparer.Ordinal)) continue;
                // Modified since it was spawned: the game's, kept.
                if (!JsonNode.DeepEquals(nowFields[property.Name], spawnedFields[property.Name])) continue;
                fresh ??= Read(type, body);
                if (fresh == null) break;
                property.Set(current, property.Get(fresh));   // on the box: a component is a struct
                any = true;
                set++;
            }
            if (any) schema.Write(entity, type, current);
        }

        foreach (var tag in _tagsAdded)
        {
            string? tagId = schema.IdOf(tag);
            if (tagId != null && !schema.TagsOf(entity).Contains(tagId, StringComparer.Ordinal)) { schema.AddTag(entity, tag); set++; }
        }
        foreach (var tag in _tagsRemoved)
        {
            string? tagId = schema.IdOf(tag);
            if (tagId != null && schema.TagsOf(entity).Contains(tagId, StringComparer.Ordinal)) { schema.RemoveTag(entity, tag); set++; }
        }
        return set;
    }

    // The entity's baseline after the reload: the new shared one, with its own `[FromPlacement]` entries
    // (4m-4) patched the same way.
    public SpawnBaseline Rebase(SpawnBaseline baseline)
    {
        if (baseline.Own is not { } own) return _newShared;
        var patched = (JsonObject)own.DeepClone();
        foreach (var (_, id, version, fields, _, _, _) in _components)
            if (patched.ContainsKey(id) && _newShared.Components[id] is JsonObject entry && entry["data"] is { } data)
                Patch(patched, id, version, fields, data);
        return new SpawnBaseline(_newShared, patched);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    // The body's components by type; a name that resolves to nothing was reported at load and is skipped.
    private static Dictionary<Type, JsonNode?> Resolve(ComponentSchema schema, JsonObject body, string ns)
    {
        var result = new Dictionary<Type, JsonNode?>();
        foreach (var (name, fields) in body)
            if (schema.TryResolveComponent(name, ns, out var type, out _)) result[type] = fields;
        return result;
    }

    // The properties whose value the body changed: written on one side only, or differently. Keys match
    // ignoring case, as the record dialect reads them.
    private static string[] Changed(JsonObject was, JsonObject now, JsonTypeInfo info)
    {
        var result = new List<string>();
        foreach (var property in info.Properties)
        {
            var a = Field(was, property.Name);
            var b = Field(now, property.Name);
            if (a.Found != b.Found || !JsonNode.DeepEquals(a.Value, b.Value)) result.Add(property.Name);
        }
        return result.ToArray();
    }

    private static (bool Found, JsonNode? Value) Field(JsonObject body, string name)
    {
        foreach (var (key, value) in body)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return (true, value);
        return (false, null);
    }

    // What the entity had of this component when it was spawned: its baseline's entry, or — for one a
    // save does not write ([Transient]) — the old body read as the component.
    private JsonNode? SpawnedAs(SpawnBaseline baseline, string id, int version, Type type, JsonNode? oldBody, JsonSerializerOptions dialect)
    {
        if (baseline.Entry(id) is JsonObject entry)
            return entry["version"] is JsonValue v && v.TryGetValue(out int had) && had == version ? entry["data"] : null;
        if (!SaveSerializer.IsTransient(type) || oldBody == null) return null;
        return Read(type, oldBody) is { } value ? Write(value, type, dialect) : null;
    }

    private static bool HadAtSpawn(SpawnBaseline baseline, string id) => baseline.Entry(id) != null;

    // A component body read as the prefab reads it (ComponentSchema.Add): bare ids in the prefab's namespace.
    private object? Read(Type type, JsonNode? body)
    {
        string? outer = RecordParseContext.Namespace;
        RecordParseContext.Namespace = _prefab.Namespace;
        try
        {
            return body is null ? Activator.CreateInstance(type) : JsonSerializer.Deserialize(body.ToJsonString(), type, _engine.Records.Json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            Log.Debug(LogCat.Records, $"{_prefab}: {type.Name} not reloaded: {ex.Message}");   // the load said what is wrong
            return null;
        }
        finally { RecordParseContext.Namespace = outer; }
    }

    private static JsonNode? Write(object value, Type type, JsonSerializerOptions dialect)
    {
        try { return JsonSerializer.SerializeToNode(value, type, dialect); }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or ArgumentException) { return null; }
    }

    // A baseline's entry for a component, with the fields the body changed (or all of it) as `data` says.
    private static void Patch(JsonObject components, string id, int version, string[]? fields, JsonNode data)
    {
        if (fields != null && components[id] is JsonObject entry && entry["data"] is JsonObject old && data is JsonObject fresh)
        {
            foreach (string field in fields) old[field] = fresh[field]?.DeepClone();
            return;
        }
        if (fields != null && !components.ContainsKey(id)) return;   // not written at spawn ([Transient]): nothing to patch
        components[id] = SaveSerializer.Entry(version, data.DeepClone());
    }
}
