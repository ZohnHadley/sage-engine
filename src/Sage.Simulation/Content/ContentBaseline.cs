#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// What content placed, by stable id and by source (phase 4i issue 4i-3, REDESIGN §4.5, decisions 2, 4
// and 8 of the 4i plan).
//
// **A load reconciles instead of rebuilding.** Everything content places — a scene's placements, a
// placements document's, a `.map`'s entities, and the children their prefabs place — gets an id that is
// the same every run, derived from where it was placed (or authored: a placement's `id`, a map entity's
// `id` key). A load places the *current* content again, removes what the save says was destroyed (its
// tombstones), and lays the saved state onto what matches by id. So a placement added to the content
// after the save is there after the load, and a goblin killed before it stays dead.
//
// **A source** is what placed a set of entities: `scene:<id>`, `placements:<id>` or `map:<id>`, and later
// a streamed sector (4g). Ids and tombstones are kept per source, so a source that is not in the world
// right now (a map still waiting for the ground under it, a scene the player has left) keeps its
// tombstones and its saved state *pending* until it is placed again, and a save writes them back.
//
// A world resource; every world with an engine has one (ContentIds.Baseline adds it when first asked).
internal sealed class ContentBaseline
{
    // Every id a source placed, alive or not: the dead ones are its tombstones.
    private readonly Dictionary<string, HashSet<PersistentId>> _placed = new(StringComparer.Ordinal);
    private readonly Dictionary<PersistentId, string> _sourceOf = new();

    // Sources whose placing is done (Finish) and not yet cleared.
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);

    // What waits for a source to be placed: ids to remove again, and saved entities to lay onto it.
    private readonly Dictionary<string, HashSet<PersistentId>> _pendingTombstones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<JsonObject>> _pendingState = new(StringComparer.Ordinal);

    public bool IsLive(string source) => _live.Contains(source);

    // True for an id that a live source placed: content's, not the game's.
    public bool IsBaseline(PersistentId id) => _sourceOf.TryGetValue(id, out var source) && _live.Contains(source);

    public bool TryGetSource(PersistentId id, out string source) => _sourceOf.TryGetValue(id, out source!);

    internal void Register(string source, PersistentId id)
    {
        if (!_placed.TryGetValue(source, out var ids)) _placed[source] = ids = new HashSet<PersistentId>();
        ids.Add(id);
        _sourceOf[id] = source;
    }

    // The source has placed everything it places: it is live, and what waited for it is applied.
    internal void Finish(World world, string source)
    {
        _live.Add(source);
        _placed.TryAdd(source, new HashSet<PersistentId>());

        if (_pendingTombstones.Remove(source, out var dead))
        {
            foreach (var id in dead)
            {
                var entity = world.Resolve(id);
                // Only what this source placed: an id a save names that the content no longer places is
                // simply gone, and the next save does not name it again.
                if (!entity.IsNull && _sourceOf.TryGetValue(id, out var from) && from == source) world.Destroy(entity);
            }
        }

        if (_pendingState.Remove(source, out var saved) && world.Engine is { } engine)
            engine.Saves.LayOnto(world, saved, $"pending state for {source}");
    }

    // The content that placed these is going (a scene cleared, a document closed, a level unloaded).
    // Called *before* its entities are destroyed: the ones already dead then are the ones the game
    // destroyed, and they wait as tombstones in case the source is placed again (a hot reload, a
    // `scene_load` back), so what is dead stays dead.
    internal void Forget(World world, string source)
    {
        _live.Remove(source);
        if (!_placed.Remove(source, out var ids)) return;
        foreach (var id in ids)
        {
            if (world.Resolve(id).IsNull) Pending(_pendingTombstones, source).Add(id);
            if (_sourceOf.TryGetValue(id, out var from) && from == source) _sourceOf.Remove(id);
        }
    }

    // A load: what was pending belonged to the game being left. The save's tombstones wait instead.
    internal void Reset(IReadOnlyDictionary<string, HashSet<PersistentId>> tombstones)
    {
        _pendingTombstones.Clear();
        _pendingState.Clear();
        foreach (var (source, ids) in tombstones) Pending(_pendingTombstones, source).UnionWith(ids);
    }

    internal void AddPendingState(string source, JsonObject saved)
    {
        if (!_pendingState.TryGetValue(source, out var list)) _pendingState[source] = list = new List<JsonObject>();
        list.Add(saved);
    }

    // The ids placed by a live source, alive or not.
    internal IEnumerable<PersistentId> PlacedBy(string source) =>
        _placed.TryGetValue(source, out var ids) ? ids : Enumerable.Empty<PersistentId>();

    internal IEnumerable<string> LiveSources => _live;

    // What a save writes as tombstones: every placed id that is dead, and every one still pending, by source.
    internal SortedDictionary<string, SortedSet<string>> Tombstones(World world)
    {
        var result = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var (source, ids) in _placed)
            foreach (var id in ids)
                if (world.Resolve(id).IsNull) Pending(result, source).Add(id.ToString());
        foreach (var (source, ids) in _pendingTombstones)
            foreach (var id in ids) Pending(result, source).Add(id.ToString());
        return result;
    }

    // Saved state still waiting for its source, written back by a save as it was read.
    internal IEnumerable<JsonObject> PendingState =>
        _pendingState.OrderBy(p => p.Key, StringComparer.Ordinal).SelectMany(p => p.Value);

    private static TSet Pending<TSet>(IDictionary<string, TSet> map, string source) where TSet : new()
    {
        if (!map.TryGetValue(source, out var set)) map[source] = set = new TSet();
        return set;
    }
}

// The key a prefab child is placed under inside its parent (`name:<name>`, else `<index>:<prefab>`), so
// its persistent id can be derived from its parent's: the same child of the same parent is the same thing
// next run, and a save finds it rather than spawning a second one beside it. Transient: the parent's
// prefab says it again when it spawns.
[Transient]
[Component("sage:prefab_child_key")]
internal struct PrefabChildKey : IComponent
{
    public string Key;
}

internal static class PrefabChildKeys
{
    public static string Of(int index, PrefabChild child) =>
        string.IsNullOrEmpty(child.Name) ? $"{index}:{child.Prefab.Id}" : $"name:{child.Name}";
}

// The ids content gives what it places (4i-3).
internal static class ContentIds
{
    public static string SceneSource(RecordId scene) => $"scene:{scene}";
    public static string DocumentSource(RecordId document) => $"placements:{document}";
    public static string MapSource(RecordId level) => $"map:{level}";

    // A scene placement: the formula scenes have always used, so saves written before 4i-3 still find
    // them (decision 4); an authored `id` wins.
    public static PersistentId ScenePlacement(RecordId scene, int index, Placement placement) =>
        Authored(SceneSource(scene), placement.Id)
        ?? PersistentId.FromName($"scene:{scene}:{index}:{placement.Prefab}");

    public static PersistentId DocumentPlacement(RecordId document, int index, Placement placement) =>
        Authored(DocumentSource(document), placement.Id)
        ?? PersistentId.FromName($"placements:{document}:{index}:{placement.Prefab}");

    // An authored id: a GUID is used as written (a tool that writes GUIDs means them); any other text is
    // an id within its source.
    public static PersistentId? Authored(string source, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (Guid.TryParse(text, out var guid) && guid != Guid.Empty) return new PersistentId(guid);
        return PersistentId.FromName($"{source}:id:{text}");
    }

    public static PersistentId Child(PersistentId parent, string key) => PersistentId.FromName($"{parent}/{key}");

    public static ContentBaseline Baseline(World world) => world.Resources.GetOrAdd(() => new ContentBaseline());

    // Gives the entity this id (replacing any it had), and each child its prefab placed an id derived
    // from it: a prefab's children are found by their parent's id plus their key, so they are never spawned
    // a second time by a load.
    public static void Assign(World world, Entity entity, PersistentId id)
    {
        if (world.TryGet(entity, out Persistent had))
        {
            if (had.Id != id)
            {
                world.Remove<Persistent>(entity);
                world.Add(entity, new Persistent { Id = id });
            }
        }
        else world.Add(entity, new Persistent { Id = id });

        if (entity.ChildCount == 0) return;
        foreach (var child in entity.ChildEntities.ToList())
            if (child.TryGetComponent<PrefabChildKey>(out var key))
                Assign(world, child, Child(id, key.Key));
    }

    // Content placed this entity: its id, its children's, and all of them recorded against the source.
    public static void Place(World world, string source, Entity entity, PersistentId id)
    {
        Assign(world, entity, id);
        Record(Baseline(world), source, entity);
    }

    private static void Record(ContentBaseline baseline, string source, Entity entity)
    {
        if (entity.TryGetComponent<Persistent>(out var persistent)) baseline.Register(source, persistent.Id);
        if (entity.ChildCount == 0) return;
        foreach (var child in entity.ChildEntities)
            if (child.HasComponent<PrefabChildKey>()) Record(baseline, source, child);
    }

    public static void Finish(World world, string source) => Baseline(world).Finish(world, source);

    public static void Forget(World world, string source)
    {
        if (world.Resources.TryGet<ContentBaseline>(out var baseline) && baseline != null) baseline.Forget(world, source);
    }
}
