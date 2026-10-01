#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Simulation;

// Entities stream by sector (phase 4g issue 4g-3; the 4g plan's decisions 2 and 5; design 14 §3).
//
// **A scene with `"streamed": true`** is not placed whole. Its placements, its placements documents' and
// its levels that stand on the terrain (`onTerrain`) are put into buckets by the absolute sector they are
// in, and each sector is a cell of its own (4g-1): the source `sector:<scene>:<x>,<z>`.
//
// - **Placed when it comes into the ring** (SectorRing, kept by streaming around the player), at a tick
//   boundary (Scenes.TickEnded, after World.RunFixed's phases), never inside the fixed schedule, where
//   systems must not allocate. At most `stream_place_budget` entities a tick, nearest sector first; a
//   sector half placed is finished on the next tick. Once its placements are in, the sector is finished
//   (ContentBaseline.Finish): its dead stay dead and its dormant state, and its runtime spawns, come back.
// - **Dormant when it leaves the ring**: ContentBaseline.Forget writes what it placed, and the runtime
//   spawns that belong to it (InCell), into its dormant cell, and its entities leave the world.
// - **Ids keep 4i's formula** (ContentIds.ScenePlacement and DocumentPlacement), so a save from before the
//   scene was streamed finds the same things; a load re-keys the `scene:` tombstones and sources it holds to
//   their sectors (SaveSystem.Rekey, from ContentSources).
// - **A sector's tombstones are kept for good**, and a dead placement is never spawned to be removed
//   again: a sector comes and goes many times in a walk, and a goblin killed in it stays dead each time.
// - **Crossing an edge** (SectorOwnersSystem, Late): a runtime spawn's cell follows it into the sector it is
//   in; a placed entity leaves its sector (tombstoned there, ContentBaseline.Leave) and is the new sector's
//   runtime spawn from then on. One that walks out of the ring goes to sleep in the sector it walked into.
internal sealed class StreamedScene
{
    private readonly Engine _engine;
    private readonly string _sceneSource;
    private readonly string _prefix;

    private readonly Dictionary<SectorCoord, Bucket> _buckets = new();
    private readonly Dictionary<SectorCoord, string> _sources = new();
    private readonly Dictionary<string, SectorCoord> _sectors = new(StringComparer.Ordinal);

    // Sectors placed or being placed; the rest are dormant or were never placed.
    private readonly Dictionary<SectorCoord, Progress> _active = new();

    // Sectors something walked (or was spawned) into while they were not in the ring: put to sleep at the
    // tick boundary.
    private readonly HashSet<SectorCoord> _orphans = new();

    // Reused at every tick boundary.
    private readonly List<SectorCoord> _work = new();

    private StreamedScene(Engine engine, RecordId scene)
    {
        _engine = engine;
        Scene = scene;
        _sceneSource = ContentIds.SceneSource(scene);
        _prefix = $"{ContentIds.SectorPrefix}{scene}:";
    }

    public RecordId Scene { get; }

    private sealed class Bucket
    {
        public readonly List<Item> Items = new();
        public readonly List<RecordId> Maps = new();
    }

    // One placement and where it is measured from, with the id content has always given it.
    private readonly record struct Item(Placement Placement, Vector3 Origin, PlacementFrame Frame, PersistentId Id, string Label);

    private sealed class Progress
    {
        public int Next;
        public bool Done;
        public readonly List<MapLevel> Levels = new();
    }

    // ---- building the buckets -------------------------------------------------------------------------

    public static StreamedScene Build(Engine engine, RecordId id, SceneRecord scene)
    {
        var streamed = new StreamedScene(engine, id);
        for (int i = 0; i < scene.Place.Count; i++)
        {
            var placement = scene.Place[i];
            streamed.Add(placement, scene.Origin, scene.RelativeTo, ContentIds.ScenePlacement(id, i, placement), $"scene {id} place[{i}]");
        }
        foreach (var document in scene.Placements)
        {
            if (!engine.Records.TryGet(document.Id, out PlacementsRecord record)) continue;   // the load check says so
            for (int i = 0; i < record.Place.Count; i++)
            {
                var placement = record.Place[i];
                streamed.Add(placement, record.Origin, record.RelativeTo, ContentIds.DocumentPlacement(document.Id, i, placement),
                             $"placements {document.Id}");
            }
        }
        foreach (var map in scene.Maps)
            if (engine.Records.TryGet(map.Id, out MapRecord record) && record.OnTerrain)
                streamed.BucketOf(Terrain.SectorOf(record.At.X, record.At.Z)).Maps.Add(map.Id);
        return streamed;
    }

    private void Add(Placement placement, Vector3 origin, PlacementFrame frame, PersistentId id, string label)
    {
        if (placement.Prefab.Id.IsEmpty) return;   // the load check says so
        var at = (placement.RelativeTo ?? frame) == PlacementFrame.World ? placement.At : origin + placement.At;
        BucketOf(Terrain.SectorOf(at.X, at.Z)).Items.Add(new Item(placement, origin, frame, id, label));
    }

    private Bucket BucketOf(SectorCoord sector)
    {
        if (!_buckets.TryGetValue(sector, out var bucket)) _buckets[sector] = bucket = new Bucket();
        return bucket;
    }

    // The levels a streamed scene places with the scene itself rather than with a sector: an interior, or
    // anything else not standing on the terrain.
    public static bool PlacedWhole(Engine engine, RecordId map) =>
        !engine.Records.TryGet(map, out MapRecord record) || !record.OnTerrain;

    // ---- sources ----------------------------------------------------------------------------------------

    // `sector:<scene>:<x>,<z>`, made once per sector.
    public string SourceOf(SectorCoord sector)
    {
        if (!_sources.TryGetValue(sector, out var source))
        {
            source = ContentIds.SectorSource(Scene, sector);
            _sources[sector] = source;
            _sectors[source] = sector;
        }
        return source;
    }

    // Whether a cell or source is this scene's: one of its sectors (`known`, with which), or the scene's own
    // cell, which a format 3 save gave its runtime spawns and which a sector replaces on the first tick.
    public bool Owns(string source, out SectorCoord sector, out bool known)
    {
        known = true;
        if (_sectors.TryGetValue(source, out sector)) return true;
        known = false;
        if (string.Equals(source, _sceneSource, StringComparison.Ordinal)) return true;
        // A sector this session has not named yet (a load's runtime spawn): parsed once, then cached.
        if (source.StartsWith(_prefix, StringComparison.Ordinal) && TryParse(source.AsSpan(_prefix.Length), out sector))
        {
            _sources.TryAdd(sector, source);
            _sectors[source] = sector;
            known = true;
            return true;
        }
        return false;
    }

    private static bool TryParse(ReadOnlySpan<char> text, out SectorCoord sector)
    {
        sector = default;
        int comma = text.IndexOf(',');
        if (comma < 0) return false;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!int.TryParse(text[..comma], System.Globalization.NumberStyles.Integer, culture, out int x)
            || !int.TryParse(text[(comma + 1)..], System.Globalization.NumberStyles.Integer, culture, out int z)) return false;
        sector = new SectorCoord(x, z);
        return true;
    }

    // Every id the scene's content places, with the sector source that places it: roots and the children
    // their prefabs place. What a load re-keys a save's `scene:` tombstones and sources by. `roots`: the
    // placements alone (a format 2 save had no ids for children).
    public Dictionary<PersistentId, string> ContentSources(out HashSet<PersistentId> roots)
    {
        var map = new Dictionary<PersistentId, string>();
        roots = new HashSet<PersistentId>();
        foreach (var (sector, bucket) in _buckets)
        {
            string source = SourceOf(sector);
            foreach (var item in bucket.Items)
            {
                map[item.Id] = source;
                roots.Add(item.Id);
                Children(map, item.Id, item.Placement.Prefab.Id, source, 0);
            }
        }
        return map;
    }

    private void Children(Dictionary<PersistentId, string> map, PersistentId parent, RecordId prefab, string source, int depth)
    {
        if (depth >= PrefabOverriding.MaxDepth || !_engine.Records.TryGet(prefab, out PrefabRecord record)) return;
        for (int i = 0; i < record.Children.Count; i++)
        {
            var child = record.Children[i];
            if (child == null || child.Prefab.Id.IsEmpty) continue;
            var id = ContentIds.Child(parent, PrefabChildKeys.Of(i, child));
            map[id] = source;
            Children(map, id, child.Prefab.Id, source, depth + 1);
        }
    }

    // ---- crossing an edge -------------------------------------------------------------------------------

    // A root that is in `now` and belongs elsewhere (SectorOwnersSystem). `placed`: a sector placed it.
    public void Move(World world, Entity entity, SectorCoord now, bool placed)
    {
        string to = SourceOf(now);
        if (placed)
        {
            if (!world.TryGet<Persistent>(entity, out var persistent)) return;
            ContentIds.Baseline(world).Leave(world, entity, persistent.Id, to);
        }
        else world.Get<InCell>(entity).Source = to;
        Joined(now);
        Log.Debug(LogCat.Streaming, $"{World.Describe(entity)} crossed into sector {now}");
    }

    // A runtime spawn joined, or walked into, `sector`: if that sector is not in the world, it sleeps there.
    public void Joined(SectorCoord sector)
    {
        if (!_active.ContainsKey(sector)) _orphans.Add(sector);
    }

    // ---- the tick boundary ------------------------------------------------------------------------------

    // Puts what left the ring to sleep and places what came into it, within the budget. Allocates nothing
    // when there is nothing to do.
    public void Step(World world, SectorRing ring)
    {
        if (!ring.Ready) return;
        bool changed = false;

        // What left: dormant, with its state.
        _work.Clear();
        foreach (var (sector, _) in _active)
            if (!ring.Live.Contains(sector)) _work.Add(sector);
        for (int i = 0; i < _work.Count; i++) Unplace(world, _work[i]);
        changed |= _work.Count > 0;

        // What walked out of the ring sleeps where it went.
        if (_orphans.Count > 0)
        {
            foreach (var sector in _orphans)
                if (!_active.ContainsKey(sector) && !ring.Live.Contains(sector)) ContentIds.Doze(world, SourceOf(sector));
            _orphans.Clear();
            changed = true;
        }

        // What came in: nearest first, as much as the budget allows.
        _work.Clear();
        foreach (var sector in ring.Live)
            if (!_active.TryGetValue(sector, out var progress) || !progress.Done) _work.Add(sector);
        if (_work.Count > 0)
        {
            var centre = ring.Centre;
            _work.Sort((a, b) =>
            {
                int byDistance = Distance(a, centre).CompareTo(Distance(b, centre));
                if (byDistance != 0) return byDistance;
                return a.X != b.X ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z);
            });
            int budget = Math.Max(1, ring.Budget);
            for (int i = 0; i < _work.Count && budget > 0; i++) Place(world, _work[i], ref budget);
            changed = true;
        }

        if (changed) world.FlushCommands();
    }

    private static int Distance(SectorCoord a, SectorCoord b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));

    private void Place(World world, SectorCoord sector, ref int budget)
    {
        if (!_active.TryGetValue(sector, out var progress)) _active[sector] = progress = new Progress();
        string source = SourceOf(sector);
        var baseline = ContentIds.Baseline(world);
        _buckets.TryGetValue(sector, out var bucket);

        int placed = 0;
        if (bucket != null)
        {
            while (progress.Next < bucket.Items.Count && budget > 0)
            {
                var item = bucket.Items[progress.Next++];
                budget--;
                // Dead, or it walked out of this sector and lives on (or sleeps) in another: not placed again.
                if (baseline.IsTombstoned(source, item.Id) || !world.Resolve(item.Id).IsNull) continue;

                var placement = item.Placement;
                var entity = world.SpawnWithoutId(placement.Prefab.Id, world.PlacementPosition(placement, item.Origin, item.Frame),
                                                  placement.Yaw, placement.Overrides, item.Label);
                if (entity.IsNull) continue;   // `Spawn` said why
                if (!string.IsNullOrEmpty(placement.Name)) entity.Name = placement.Name;
                PlacementWires.Attach(world, entity, placement);
                entity.AddTag<FromScene>();
                ContentIds.Place(world, source, entity, item.Id);
                placed++;
            }
            if (progress.Next < bucket.Items.Count)
            {
                Log.Debug(LogCat.Streaming, $"{source}: {progress.Next} of {bucket.Items.Count} placed; the rest next tick");
                return;
            }
        }

        // Everything in: its dead removed, its state laid back, its runtime spawns awake (4g-1).
        ContentIds.Finish(world, source);
        budget--;
        if (bucket != null && bucket.Maps.Count > 0 && world.Resources.TryGet<ActiveScene>(out var state) && state != null)
            foreach (var map in bucket.Maps)
                if (MapLoader.Load(world, map) is { } level)
                {
                    progress.Levels.Add(level);
                    state.Levels.Add(level);
                }
        progress.Done = true;
        if (bucket != null)
            Log.Debug(LogCat.Streaming, $"{source}: placed ({bucket.Items.Count} placement(s), {progress.Levels.Count} level(s))");
    }

    // The sector leaves the world: dormant with its state, and what it placed gone.
    private void Unplace(World world, SectorCoord sector)
    {
        string source = SourceOf(sector);
        var baseline = ContentIds.Baseline(world);
        var roots = new List<Entity>();
        foreach (var id in baseline.PlacedBy(source))
            if (world.Resolve(id) is { IsNull: false } entity && entity.Parent.IsNull) roots.Add(entity);

        // Before anything goes, so what is dead now is what the game destroyed (4i-3).
        ContentIds.Forget(world, source);
        foreach (var root in roots)
            if (world.IsAlive(root)) world.Destroy(root);

        var progress = _active[sector];
        if (progress.Levels.Count > 0 && world.Resources.TryGet<MapLevels>(out var levels) && levels != null)
        {
            world.Resources.TryGet<ActiveScene>(out var state);
            foreach (var level in progress.Levels)
            {
                levels.Remove(level);
                state?.Levels.Remove(level);
            }
        }
        _active.Remove(sector);
        Log.Debug(LogCat.Streaming, $"{source}: dormant ({roots.Count} placed entit(ies) asleep)");
    }

    // The scene is cleared (Scenes.Clear): every sector in the world is forgotten — dormant with its state,
    // unless a reload or a load is discarding it — before the scene's entities are swept away.
    public void Clear(World world)
    {
        foreach (var (sector, _) in _active) ContentIds.Forget(world, SourceOf(sector));
        foreach (var sector in _orphans)
            if (!_active.ContainsKey(sector)) ContentIds.Doze(world, SourceOf(sector));
        _active.Clear();
        _orphans.Clear();
    }

    // Whether a sector is in the world with everything it places (4g-6: an off-screen agent that walked into
    // it is spawned there).
    public bool IsPlaced(SectorCoord sector) => _active.TryGetValue(sector, out var progress) && progress.Done;

    // How many sectors are placed, and how many are being placed (stream_status).
    public (int Placed, int Placing) Count()
    {
        int placed = 0, placing = 0;
        foreach (var (_, progress) in _active)
            if (progress.Done) placed++;
            else placing++;
        return (placed, placing);
    }
}
