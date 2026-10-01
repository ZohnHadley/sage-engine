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
// **Since 4g-1 a source goes dormant with its state** (Cells.cs): forgetting it writes every live entity
// it placed, and every runtime spawn that belongs to it (InCell), into its dormant cell, and placing it
// again lays that state back and spawns those again. The state a load holds for a source not placed yet is
// a dormant cell too, kept with the sector its positions are relative to.
//
// A world resource; every world with an engine has one (ContentIds.Baseline adds it when first asked).
internal sealed class ContentBaseline
{
    // Every id a source placed, alive or not: the dead ones are its tombstones.
    private readonly Dictionary<string, HashSet<PersistentId>> _placed = new(StringComparer.Ordinal);
    private readonly Dictionary<PersistentId, string> _sourceOf = new();

    // Sources whose placing is done (Finish) and not yet cleared.
    private readonly HashSet<string> _live = new(StringComparer.Ordinal);

    // What waits for a source to be placed: ids to remove again, and its state (4g-1: a dormant cell).
    private readonly Dictionary<string, HashSet<PersistentId>> _pendingTombstones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DormantCell> _dormant = new(StringComparer.Ordinal);

    // While above zero, a source forgotten keeps its tombstones only, and its runtime spawns stay: a hot
    // reload shows the edit rather than the old state, and a load replaces both anyway.
    private int _discarding;

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

    // The source has placed everything it places: it is live, and what waited for it is applied — its
    // dead removed, its state laid on, and its runtime spawns back (4g-1).
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

        if (_dormant.Remove(source, out var cell) && world.Engine is { } engine)
            engine.Saves.Wake(world, source, cell);
    }

    // The content that placed these is going (a scene cleared, a document closed, a level unloaded).
    // Called *before* its entities are destroyed: the ones already dead then are the ones the game
    // destroyed, and they wait as tombstones in case the source is placed again (a hot reload, a
    // `scene_load` back), so what is dead stays dead.
    //
    // And it goes dormant with its state (4g-1): the live ones are written into its dormant cell, and so
    // are its runtime spawns, which are destroyed here — nothing else would take them out of the world.
    internal void Forget(World world, string source)
    {
        if (_discarding == 0 && world.Engine is { } engine) Sleep(world, engine, source);

        _live.Remove(source);
        if (!_placed.Remove(source, out var ids)) return;
        foreach (var id in ids)
        {
            if (world.Resolve(id).IsNull) Pending(_pendingTombstones, source).Add(id);
            if (_sourceOf.TryGetValue(id, out var from) && from == source) _sourceOf.Remove(id);
        }
    }

    private void Sleep(World world, Engine engine, string source)
    {
        var entities = new List<Entity>();
        if (_live.Contains(source) && _placed.TryGetValue(source, out var ids))
            foreach (var id in ids)
                if (world.Resolve(id) is { IsNull: false } placed) entities.Add(placed);
        var members = Cells.Members(world, source);
        entities.AddRange(members);
        if (entities.Count == 0) return;

        var frame = world.Origin().Sector;
        foreach (var saved in engine.Saves.Capture(world, entities)) Dormant(source, frame).Add(saved, frame);

        foreach (var member in members)
            if (world.IsAlive(member)) world.Destroy(member);
        if (members.Count > 0)
            Log.Debug(LogCat.Save, $"{source}: {members.Count} runtime spawn(s) asleep with it");
    }

    // A hot reload or a load clears content without keeping its state (see `_discarding`).
    internal IDisposable Discarding()
    {
        _discarding++;
        return new Scope(this);
    }

    private sealed class Scope : IDisposable
    {
        private ContentBaseline? _baseline;
        public Scope(ContentBaseline baseline) => _baseline = baseline;
        public void Dispose()
        {
            if (_baseline != null) _baseline._discarding--;
            _baseline = null;
        }
    }

    // A load: what was pending or dormant belonged to the game being left. The save's wait instead.
    internal void Reset(IReadOnlyDictionary<string, HashSet<PersistentId>> tombstones, IReadOnlyDictionary<string, DormantCell> dormant)
    {
        _pendingTombstones.Clear();
        _dormant.Clear();
        foreach (var (source, ids) in tombstones) Pending(_pendingTombstones, source).UnionWith(ids);
        foreach (var (source, cell) in dormant) _dormant[source] = cell;
    }

    // Saved state for a source not placed now (a format 3 save's, for a level still waiting for its
    // ground), its positions relative to `frame`.
    internal void AddPendingState(string source, JsonObject saved, SectorCoord frame) => Dormant(source, frame).Add(saved, frame);

    private DormantCell Dormant(string source, SectorCoord frame)
    {
        if (!_dormant.TryGetValue(source, out var cell)) _dormant[source] = cell = new DormantCell(frame);
        return cell;
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

    // The dormant cells, by source: what a save writes under `dormant` (format 4).
    internal IEnumerable<(string Source, DormantCell Cell)> DormantCells =>
        _dormant.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value));

    internal bool IsDormant(string source) => _dormant.ContainsKey(source);

    internal bool TryGetDormant(string source, out DormantCell cell) => _dormant.TryGetValue(source, out cell!);

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

    // Content cleared without going dormant (a hot reload, a load), until disposed.
    public static IDisposable Discarding(World world) => Baseline(world).Discarding();
}
