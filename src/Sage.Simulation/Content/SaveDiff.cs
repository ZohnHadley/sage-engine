#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Sage.Simulation;

// Save what changed (phase 4i issue 4i-5, REDESIGN §4.5, decision 1 of the 4i plan).
//
// Until this a save wrote every public field of every component in full, so a prefab or a record
// rebalanced after the save never reached an entity from it: the goblin's old health came back over the
// new prefab's. Now a prefab-spawned entity is written as a **diff against its prefab as it was spawned**
// — overrides included, and a child's body as its parent's prefab placed it — and a load spawns (or
// places) it from the *current* prefab and lays the diff over that. What the game never changed follows
// the content; what it did change is kept.
//
// - **The baseline** is the entity's components serialized in the save dialect right after its prefab
//   was applied (`FromPrefab.Baseline`). It is taken once per prefab and overrides and shared by every
//   entity spawned from them; after the records are loaded again (a hot reload) the next spawn
//   takes a new one, and what was spawned before keeps its own — its baseline is what it was spawned *as*.
// - **Field by field**, at a component's top level: a field whose value differs from the baseline's is
//   written; a component with none is left out. A field is compared and written whole — a list, a
//   dictionary or a nested struct is one value — because a dictionary's missing key cannot be told from
//   an unchanged one. A component whose JSON is not an object of fields (a custom converter) is written
//   whole when it differs.
// - **The transform is written in full** (and, since 4m-4, not at all for content's entity still where it
//   was placed, so it follows the placement): a runtime spawn has no content to say where it is, and
//   a load spawns it there so a part that reads its placement (a mover's closed position, a character's
//   yaw) reads the saved one.
// - **Removal is explicit**: `"removed"` lists the baseline's components the entity no longer has, and a
//   load takes them off again. A component the prefab did not give it is written in full, as before.
// - **Tags are written in full** and, for a diffed entity, are the save's exactly, as for content (4i-3).
// - **Shared, except a value a part derives from the placement** (a mover's closed position): a
//   component marked `[FromPlacement]` is taken again for each entity and compared with its own (issue
//   4m-4), so an unmoved door writes nothing and follows its placement and a rebalance.
// - **An `[Upgrade]` method sees only the fields a diff wrote**: renaming or removing a field works as it
//   did; one that computes a field from another may find the other absent (it was the prefab's).
//
// The saved entity says `"diff": true`, and only then is an entry merged field by field onto what the
// prefab gave; an entry without it (a bare `Create`, any save before this) replaces the component, as it
// always did. So formats 1–3 read as they did, and the format stays 3.
internal sealed class SpawnBaseline
{
    public SpawnBaseline(JsonObject components, IReadOnlyList<string>? fromPlacement = null,
                         IReadOnlyDictionary<string, object>? values = null)
    {
        Components = components;
        FromPlacement = fromPlacement ?? Array.Empty<string>();
        _values = values;
    }

    // The components as spawned, as boxed copies (issue #285): a save compares a component with its own
    // before it serialises anything, and leaves out one that has not changed. A baseline rebased by a
    // prefab hot reload (#287) has none, so its entities are compared field by field as before.
    private readonly IReadOnlyDictionary<string, object>? _values;

    // One entity's own: the shared baseline with its `[FromPlacement]` components as *it* was spawned
    // (issue 4m-4), so a value derived from its placement is compared with its own and not the first spawn's.
    public SpawnBaseline(SpawnBaseline shared, JsonObject own)
    {
        Components = shared.Components;
        FromPlacement = shared.FromPlacement;
        _values = shared._values;
        Body = shared.Body;
        Tags = shared.Tags;
        _shared = shared;
        _own = own;
    }

    private readonly JsonObject? _own;
    private readonly SpawnBaseline? _shared;

    // The shared baseline this one is (itself), or the one an entity's own is made from.
    public SpawnBaseline Shared => _shared ?? this;

    // This entity's own `[FromPlacement]` entries; null for a shared baseline.
    public JsonObject? Own => _own;

    // The prefab's `components` and `tags` as written, overrides merged, when this was taken (issue #287):
    // what a hot reload compares the reloaded prefab with, to know which fields the *prefab* changed —
    // the record itself is updated in place, so the old values are gone from it. Null when not taken (a
    // baseline made by hand); such an entity does not follow a reload.
    public JsonObject? Body { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }

    // Component id -> `{ "version", "data" }`, as a save writes it (the shared part).
    public JsonObject Components { get; }

    // The ids of its components marked `[FromPlacement]`: taken per entity.
    public IReadOnlyList<string> FromPlacement { get; }

    // A component's entry as spawned: this entity's own for one derived from its placement.
    // A component's boxed value as the prefab spawned it; null for one this entity has its own of (a
    // `[FromPlacement]` one) or when none was kept.
    public object? ValueAsSpawned(string id) =>
        _own != null && _own.ContainsKey(id) ? null : _values != null && _values.TryGetValue(id, out var value) ? value : null;

    public JsonNode? Entry(string id) =>
        _own != null && _own.TryGetPropertyValue(id, out var own) ? own : Components[id];

    // A copy a save's writer owns (issue #285): it diffs against it on its own thread while the game goes on,
    // and a JSON tree is not safe to read from two threads (a parsed one fills itself in on first read). The
    // shared part is copied once a save (`copies`), an entity's own part once an entity; the boxed values
    // are only ever read.
    public SpawnBaseline CopyForWriter(Dictionary<SpawnBaseline, SpawnBaseline> copies)
    {
        var shared = Shared;
        if (!copies.TryGetValue(shared, out var copy))
            copies[shared] = copy = new SpawnBaseline((JsonObject)shared.Components.DeepClone(), shared.FromPlacement, shared._values);
        return _own == null ? copy : new SpawnBaseline(copy, (JsonObject)_own.DeepClone());
    }
}

// The baselines taken in one world, by prefab and overrides (a world resource: the dialect a baseline is
// written in needs the world, for entity ids and gameplay's names).
internal sealed class PrefabBaselines
{
    private readonly Dictionary<RecordId, (PrefabRecord Record, Dictionary<string, SpawnBaseline> ByOverrides)> _byPrefab = new();

    public JsonSerializerOptions? Dialect;

    private int _records = -1;

    // Records were loaded again (a hot reload) since these were taken: a prefab, or a record a part reads,
    // may say something else now, so the next spawn of each takes a new one. What was spawned before keeps
    // its own.
    public void Since(int recordsLoaded)
    {
        if (_records == recordsLoaded) return;
        _records = recordsLoaded;
        _byPrefab.Clear();
    }

    public bool TryGet(RecordId prefab, PrefabRecord record, string overrides, out SpawnBaseline baseline)
    {
        baseline = null!;
        return _byPrefab.TryGetValue(prefab, out var entry) && ReferenceEquals(entry.Record, record)
               && entry.ByOverrides.TryGetValue(overrides, out baseline!);
    }

    public void Add(RecordId prefab, PrefabRecord record, string overrides, SpawnBaseline baseline)
    {
        // A prefab reloaded since: its old baselines stay with the entities that were spawned from them.
        if (!_byPrefab.TryGetValue(prefab, out var entry) || !ReferenceEquals(entry.Record, record))
            _byPrefab[prefab] = entry = (record, new Dictionary<string, SpawnBaseline>(StringComparer.Ordinal));
        entry.ByOverrides[overrides] = baseline;
    }

    // Overrides as a key: the same bodies are the same baseline. Nothing is allocated for none.
    public static string KeyOf(PrefabOverrides? overrides) =>
        overrides is null || overrides.IsEmpty
            ? ""
            : (overrides.Components?.ToJsonString() ?? "") + "\n" + (overrides.Parts?.ToJsonString() ?? "");
}

// The two halves of a diff: writing one, and laying one over a component.
internal static class SaveDiff
{
    // Whether a component is written field by field: its JSON contract is an object of fields.
    public static bool ByField(Type type, JsonSerializerOptions json) =>
        json.GetTypeInfo(type).Kind == JsonTypeInfoKind.Object;

    // The fields of `data` that differ from `baseline`'s; empty when none do.
    public static JsonObject Fields(JsonObject data, JsonObject baseline)
    {
        var changed = new JsonObject();
        foreach (var (name, value) in data)
            if (!baseline.TryGetPropertyValue(name, out var was) || !JsonNode.DeepEquals(value, was))
                changed[name] = value?.DeepClone();
        return changed;
    }

    // `saved` (read from a diff: only the fields it names mean anything) laid onto `current`, the component
    // the prefab gave: each field the diff names is taken from it, the rest are left as they are.
    public static object Merge(Type type, object current, object saved, JsonObject fields, JsonSerializerOptions json)
    {
        foreach (var property in json.GetTypeInfo(type).Properties)
        {
            if (property.Get is null || property.Set is null || !Names(fields, property.Name)) continue;
            property.Set(current, property.Get(saved));   // on the box: a component is a struct
        }
        return current;
    }

    // The dialect reads names ignoring case (SaveJson), so a diff does too.
    private static bool Names(JsonObject fields, string name)
    {
        if (fields.ContainsKey(name)) return true;
        foreach (var (key, _) in fields)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
