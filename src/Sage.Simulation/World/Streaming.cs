#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Streaming rings, and the origin that follows them (docs/design/14 §3; TODO F14, R6).
//
// The Daggerfall Unity model, which is the one this engine chose: the world is an unbounded grid of
// 1024 m sectors, a **streaming source** (the player) keeps a ring of them loaded around itself, and
// the simulation's frame of reference follows so that nothing is ever far from the origin.
//
// Two rules, both with hysteresis, because the failure mode of a bare threshold is a player standing
// on a sector edge and loading, unloading and loading the world again at 60 Hz:
//
//   * **load** every sector within `stream_radius` of a source; **unload** past `stream_radius + 1`;
//   * **rebase** when the source is more than 1.5 sectors from the origin sector.
//
// What this does *not* do is decide what a sector contains. It loads the heightfield; the client's
// mesh system and physics' collision system react to that. The ring itself (SectorRing) is what a
// streamed scene's content follows (4g-3, StreamedScene): a sector in the ring is placed at a tick
// boundary, and one that leaves it goes dormant with its state (4g-1's cells).
//
// **The ring runs without a generator** (4g-3): a world with no ground still has sectors, so a streamed
// scene in a game with no terrain, or whose terrain comes from a `terrain` record set later, streams.

// Tag: the world loads around this, with the rings `stream_radius` and `stream_far_radius` say. The local
// player is always a source; a followed companion or a remote camera can be one too, and each keeps its own
// ring (#290).
[Tag("sage:streaming_source")]
public struct StreamingSource : ITag { }

// A streaming source with rings of its own (#290): a companion the camera follows that needs only the sector it
// stands in, a camera cut that wants to see further. It is a source whether or not it has the tag; on the player
// it replaces the cvars' rings. Where rings of two sources meet, the larger wins.
[Component("sage:streaming_ring")]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public struct StreamingRing : IComponent
{
    [Property(Min = 0, Max = 8, Unit = "sectors", Tooltip = "Full-detail sectors kept around it (0: only the one it stands in)")]
    public int Radius;
    [Property(Min = 0, Max = 16, Unit = "sectors", Tooltip = "Coarse ground and far looks out to here (0, or not more than Radius: none)")]
    public int FarRadius;
}

// "streaming_ring": { "radius": 0, "farRadius": 0 }
[PrefabPart("streaming_ring", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class StreamingRingPart : IPrefabPart
{
    [Property(Min = 0, Max = 8, Unit = "sectors", Tooltip = "Full-detail sectors kept around it (0: only the one it stands in)")]
    public int Radius = 1;
    [Property(Min = 0, Max = 16, Unit = "sectors", Tooltip = "Coarse ground and far looks out to here (0, or not more than radius: none)")]
    public int FarRadius;

    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new StreamingRing { Radius = Math.Clamp(Radius, 0, 8), FarRadius = Math.Clamp(FarRadius, 0, 16) });
}

// What a sector owns: chunk meshes, its collision body, and later the props a map placed in it. When
// the sector goes, these go. It is a component rather than a tag because the *which sector* is the
// whole point, and because a save must be able to tell one sector's belongings from another's.
[Component("sage:sector_owned")]
public struct SectorOwned : IComponent
{
    public SectorCoord Sector;
}

// Streaming is the engine's, not a game's: where the ground comes from is a world question (14 §1).
// A game still chooses the generator and the seed.
[Plugin("sage.streaming", "0.1.0")]
public sealed class StreamingModule : IModule
{
    private CVar<int>? _radius;
    private CVar<bool>? _enabled;
    private CVar<int>? _budget;
    private CVar<int>? _farRadius;
    private CVar<int>? _loadBudget;
    private CVar<bool>? _jobs;

    public void Init(ModuleContext ctx)
    {
        _radius = ctx.Engine.CVars.Register("stream_radius", 1, CVarFlags.Archive,
            "Sectors of terrain kept loaded around the player, each 1024 m (14 §3). 1 = a 3x3 ring.");
        _enabled = ctx.Engine.CVars.Register("stream_enabled", true, CVarFlags.None,
            "Load and unload terrain around the player, and move the origin to follow (R6, F14).");
        _budget = ctx.Engine.CVars.Register("stream_place_budget", 64, CVarFlags.Archive,
            "Entities a streamed scene places per tick at most, as sectors come into the ring (4g-3).", 1, 100000);
        _farRadius = ctx.Engine.CVars.Register("stream_far_radius", 4, CVarFlags.Archive,
            "Sectors kept as coarse ground and far looks past the full ring (#277): drawn, never simulated. " +
            "0, or not more than stream_radius, = no far ring.", 0, 16);
        _loadBudget = ctx.Engine.CVars.Register("stream_load_budget", 1, CVarFlags.Archive,
            "Full-detail sectors made live per tick once the ring is up, nearest first (#277); a jump or the first " +
            "load takes its whole ring at once.", 1, 81);
        _jobs = ctx.Engine.CVars.Register("stream_jobs", true, CVarFlags.None,
            "Generate the sectors just past the ring, and the far ring's coarse ground, on the thread pool (#277).");
        ctx.Engine.CVars.RegisterCommand("warp", CVarFlags.Cheat,
            "warp <x> <z>: put the player at these absolute metres; the world streams in around them.", a =>
        {
            if (a.Count < 2 || !float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x)
                            || !float.TryParse(a[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
            {
                Log.Warn(LogCat.Console, "warp <x> <z>   (absolute metres; a sector is 1024 m)");
                return;
            }
            Warp(ctx.Engine, x, z);
        });

        ctx.Engine.CVars.RegisterCommand("stream_status", CVarFlags.None,
            "What is loaded, where the origin is, and how far the player is from it.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<Terrain>(out var terrain) || terrain == null) continue;
                var origin = world.Origin();
                Log.Info(LogCat.Console, $"{world.Name}: {origin}, {terrain.Sectors.Count} sector(s) loaded, " +
                                         $"{origin.Rebases} rebase(s) so far");
                if (world.Resources.TryGet<SectorRing>(out var ring) && ring != null)
                {
                    string content = world.Resources.TryGet<ActiveScene>(out var scene) && scene?.Streamed is { } streamed
                        ? $"; scene '{streamed.Scene}' streams: {streamed.Count().Placed} sector(s) placed, {streamed.Count().Placing} being placed"
                        : "";
                    Log.Info(LogCat.Console, $"  ring: {ring.Live.Count} sector(s){content}");
                }
                if (world.Resources.TryGet<SectorLod>(out var lod) && lod != null && lod.Count > 0)
                {
                    int ready = 0, proxies = 0;
                    foreach (var far in lod.Sectors)
                    {
                        if (far.Heights != null) ready++;
                        proxies += far.Proxies.Count;
                    }
                    Log.Info(LogCat.Console, $"  far ring: {lod.Count} sector(s) out to {lod.Radius}, {ready} with coarse ground, {proxies} far look(s)");
                }
                Log.Info(LogCat.Console, $"  generated: {terrain.GeneratedHere} here, {terrain.GeneratedOnJobs} on jobs (stream_jobs {(terrain.Jobs ? 1 : 0)})");
                if (world.Resources.TryGet<SectorAssets>(out var assets) && assets != null)
                    Log.Info(LogCat.Console, $"  assets: {assets.HeldPaths} model(s) and texture(s) held; released {assets.ReleasedPaths} and {assets.ReleasedHandles} built mesh(es) with their sectors (asset_list)");
                foreach (var sector in terrain.Sectors)
                    Log.Info(LogCat.Console, $"  {sector.Coord}  corner {terrain.CornerOf(sector.Coord).X:F0},{terrain.CornerOf(sector.Coord).Z:F0} m");
            }
        });
    }

    // Fast travel, in its smallest form (14 §3): put the player somewhere in **absolute** metres and
    // let streaming do the rest — the ring loads around the new place and the origin follows it on the
    // next tick. A real one adds a loading screen and a destination table; the mechanism is this.
    private void Warp(Engine engine, float absoluteX, float absoluteZ)
    {
        var destination = Terrain.SectorOf(absoluteX, absoluteZ);

        foreach (var world in engine.Worlds)
        {
            var terrain = world.Resources.Get<Terrain>();
            if (world.Resources.TryGet<SectorRing>(out var ring) && ring is { Suspended: true })
            {
                // Inside there is no ground to warp across (4g-5): leave by a door, or `travel`.
                Log.Warn(LogCat.Console, $"'{world.Name}' is in an interior: warp works outside (use a door or `travel`)");
                continue;
            }

            // **In this order, and the order is the whole of it (14 §3).** Rebase first so the numbers
            // are small, then generate the ground, *then* put the player on it. Placing first is what
            // the first version did, and the player arrived over a sector that did not exist yet,
            // read a ground height of zero, and fell 1.6 km before the terrain caught up.
            world.Rebase(destination);

            // Without a generator there is no ground to wait for (4g-3): the ring follows on the next tick.
            int radius = Math.Clamp(_radius!.Value, 0, 8);
            if (terrain.Generator != null)
                for (int z = -radius; z <= radius; z++)
                    for (int x = -radius; x <= radius; x++)
                        terrain.Load(new SectorCoord(destination.X + x, destination.Z + z));

            var origin = world.Origin();
            foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList())
            {
                var local = origin.ToOrigin(new Vector3(absoluteX, 0, absoluteZ));
                local.Y = terrain.HeightAt(local.X, local.Z) + 1f;
                world.Teleport(entity, Transform.At(local));
                Followers.Bring(world, entity);   // and whoever follows them (#290)
                Log.Info(LogCat.Console, $"{World.Describe(entity)} warped to {absoluteX:F0}, {absoluteZ:F0} " +
                                         $"(sector {destination}, ground {local.Y - 1f:F1} m)");
            }
        }
    }

    public void OnWorldCreated(World world)
    {
        // The terrain is this plugin's (issue #13): a game without streaming has none, and everything
        // that reads it (collision, navigation, levels, the renderer) copes with that.
        world.Resources.Add(new Terrain { Origin = world.Origin() });
        world.Resources.Add(new SectorRing { Radius = Math.Clamp(_radius!.Value, 0, 8) });
        world.Resources.Add(new SectorLod());                 // the far ring (#277)
        var assets = new SectorAssets(world);                 // what a sector's meshes are released with (#277)
        world.Resources.Add(assets);

        // Late in the tick: everything has moved by now, so the ring is computed from where the player
        // actually ended up, and a rebase lands between ticks rather than in the middle of one.
        world.AddSystem(new StreamingSystem(world, new StreamingCVars(_radius!, _enabled!, _budget!, _farRadius!, _loadBudget!, _jobs!)));
        // And what crossed a sector edge this tick belongs to the sector it is in now (4g-3).
        world.AddSystem(new SectorOwnersSystem(world));

        // Whatever a sector owns goes when the sector does. The terrain raises this; the sweep is here
        // because the entities are the world's, not the terrain resource's.
        var terrain = world.Resources.Get<Terrain>();
        terrain.Unloaded += coord =>
        {
            int destroyed = 0;
            using (assets.Leave())   // its chunk meshes, and whatever else only it drew, are released (#277)
            {
                foreach (var entity in world.Query<SectorOwned>().Entities.ToEntityList())
                {
                    if (world.Get<SectorOwned>(entity).Sector != coord) continue;
                    world.Destroy(entity);
                    destroyed++;
                }
                world.FlushCommands();
            }
            if (destroyed > 0) Log.Debug(LogCat.Streaming, $"sector {coord}: {destroyed} owned entities destroyed");
        };
        // A sculpted sector generated again (#372): what was built from its old heights goes, and is built anew.
        terrain.Refreshed += coord => TerrainWaterSystem.DropBuilt(world, assets, coord);
        world.AddSystem(new TerrainWaterSystem(world));   // a sculpt's water over its sectors (#372)
    }
}

// The sectors streaming keeps around its sources (4g-3), whether or not there is ground in them: what a
// streamed scene's content follows. Absolute, like terrain sectors. A world resource of sage.streaming.
internal sealed class SectorRing
{
    // In the ring now: within `stream_radius` of a source, and kept until past it plus the margin.
    public readonly HashSet<SectorCoord> Live = new();

    // The ring has been computed at least once: before that, an empty ring means "not known yet", not
    // "nothing is live", and a streamed scene waits rather than putting everything to sleep.
    public bool Ready;

    // The sector of the primary source (the player's, #290): what a streamed scene places first.
    public SectorCoord Centre;

    // Entities placed per tick at most (`stream_place_budget`).
    public int Budget = 64;

    // `stream_radius` as last read: how far travel generates ground before the player arrives (4g-5).
    public int Radius = 1;

    // The world's scene is an interior (4g-5): no ring, no terrain, until an exterior is placed again.
    public bool Suspended;

    // Full sectors whose ground was loaded on the last tick streaming ran (#277): the per-tick work the
    // scale test bounds by `stream_load_budget`.
    public int LoadedLastTick;

    // Streaming sources on the last tick streaming ran, two in one sector counted once (#290).
    public int Sources;
}

internal sealed record StreamingCVars(CVar<int> Radius, CVar<bool> Enabled, CVar<int> PlaceBudget,
                                      CVar<int> FarRadius, CVar<int> LoadBudget, CVar<bool> Jobs);

// The rings (14 §3; #277 for the far ring, the load budget and generation ahead on jobs):
//
//   * **full**: within `stream_radius` of a source. Its ground is loaded — at once when the source's own
//     sector is not (a jump, or the first tick), otherwise `stream_load_budget` sectors a tick, nearest
//     first — and only a sector whose ground is in joins `SectorRing.Live`, so nothing is placed on ground
//     that is not there yet. Kept until past the ring plus the margin.
//   * **ahead**: the ring plus one is generated on the thread pool before it is needed, so crossing an edge
//     takes finished heights instead of generating three sectors in one tick. Dropped past the ring plus two.
//   * **far**: out to `stream_far_radius`, not full: coarse ground on a job and far looks (SectorLod).
//
// **Several sources, each with its own rings** (#290): the local player always, everything tagged
// `StreamingSource` with the cvars' rings, and everything with a `StreamingRing` with its own. A sector is in
// the full ring when it is within *some* source's radius, and kept until past that radius plus the margin.
// The origin follows the **primary** source — the player, else the first source — so a companion left in
// the next valley keeps its ground loaded without holding the frame of reference back.
[System("sage.streaming.sectors", Phase.Late)]
internal sealed class StreamingSystem : ISystem
{
    // One source as this tick sees it: the sector it stands in and its two radii, in sectors.
    private struct Source
    {
        public SectorCoord Sector;
        public int Radius;
        public int Far;
    }

    private readonly World _world;
    private readonly Terrain _terrain;
    private readonly SectorRing _ring;
    private readonly SectorLod _lod;
    private readonly Origin _origin;
    private readonly StreamingCVars _cvars;

    private readonly Query<Transform> _sources;
    private readonly Query<Transform, StreamingRing> _rings;
    private readonly Query<Transform> _players;

    // Reused every tick: this runs in the fixed schedule, where allocating is not allowed (02 §4.6).
    private readonly List<Source> _sourceSectors = new();
    private readonly List<SectorCoord> _stale = new();
    private readonly List<SectorCoord> _missing = new();
    private int _primary = -1;

    // How far past the ring a sector survives before being dropped, and how far a source may drift
    // from the origin before everything shifts. Both in sectors.
    private const int KeepMargin = 1;
    private const float RebaseDistance = 1.5f;

    public StreamingSystem(World world, StreamingCVars cvars)
    {
        _world = world;
        _terrain = world.Resources.Get<Terrain>();
        _ring = world.Resources.Get<SectorRing>();
        _lod = world.Resources.Get<SectorLod>();
        _origin = world.Origin();
        _cvars = cvars;
        _sources = world.Query<Transform>().AllTags(Tags.Get<StreamingSource>());
        _rings = world.Query<Transform, StreamingRing>();
        // The local player is always a source: a world streaming around a camera cut or a companion alone
        // would unload the ground under the only thing standing on it.
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        int radius = Math.Clamp(_cvars.Radius.Value, 0, 8);
        _ring.Radius = radius;
        _terrain.Jobs = _cvars.Jobs.Value;
        if (!_cvars.Enabled.Value) return;
        if (_ring.Suspended)   // an interior has no ring (4g-5), and no horizon
        {
            if (_lod.Count > 0) _lod.Clear();
            return;
        }

        int far = Math.Clamp(_cvars.FarRadius.Value, 0, 16);
        CollectSources(radius, far);
        if (_sourceSectors.Count == 0) return;

        bool ground = _terrain.Generator != null;
        LoadRing(ground);
        if (ground) Ahead();

        // Drop anything past the ring plus its margin. Collected first, because unloading raises an
        // event that destroys entities and the terrain's own list must not be walked while it changes.
        _stale.Clear();
        var loaded = _terrain.Sectors;
        for (int i = 0; i < loaded.Count; i++)
            if (!WithinAnyRing(loaded[i].Coord, KeepMargin)) _stale.Add(loaded[i].Coord);
        for (int i = 0; i < _stale.Count; i++) _terrain.Unload(_stale[i]);

        _stale.Clear();
        foreach (var coord in _ring.Live)
            if (!WithinAnyRing(coord, KeepMargin)) _stale.Add(coord);
        for (int i = 0; i < _stale.Count; i++) _ring.Live.Remove(_stale[i]);
        _ring.Ready = true;
        var primary = _sourceSectors[_primary].Sector;
        _ring.Centre = primary;
        _ring.Budget = _cvars.PlaceBudget.Value;
        _ring.Sources = _sourceSectors.Count;

        Far(far, ground);

        // And the origin follows the primary source once it is far enough that precision would suffer.
        if (Distance(primary, _origin.Sector) > RebaseDistance) _world.Rebase(primary);
    }

    // The full ring around each source: the sectors (4g-3), and their ground when there is a generator.
    private void LoadRing(bool ground)
    {
        _missing.Clear();
        _ring.LoadedLastTick = 0;
        foreach (var source in _sourceSectors)
        {
            var centre = source.Sector;
            int radius = source.Radius;
            // A source standing where there is no ground has jumped (or just arrived): its ring loads now,
            // because it is about to fall through the world otherwise.
            bool jump = ground && !_terrain.IsLoaded(centre);
            for (int z = -radius; z <= radius; z++)
                for (int x = -radius; x <= radius; x++)
                {
                    var coord = new SectorCoord(centre.X + x, centre.Z + z);
                    if (!ground || _terrain.IsLoaded(coord)) { _ring.Live.Add(coord); continue; }
                    if (jump) Load(coord);
                    else if (!_missing.Contains(coord)) _missing.Add(coord);
                }
        }
        if (_missing.Count == 0) return;

        // Walking: the nearest few a tick (`stream_load_budget`), so crossing an edge costs one sector's
        // collision and meshes a tick rather than a column's in one. Insertion sort: no allocation, and
        // a handful of entries.
        for (int i = 1; i < _missing.Count; i++)
        {
            var item = _missing[i];
            int j = i - 1;
            while (j >= 0 && Before(item, _missing[j])) { _missing[j + 1] = _missing[j]; j--; }
            _missing[j + 1] = item;
        }
        int budget = Math.Min(Math.Max(1, _cvars.LoadBudget.Value), _missing.Count);
        for (int i = 0; i < budget; i++)
            if (Load(_missing[i]) is { } sector) sector.Budgeted = true;   // collision may follow over a few ticks
    }

    private TerrainSector? Load(SectorCoord coord)
    {
        var sector = _terrain.Load(coord);
        _ring.Live.Add(coord);
        _ring.LoadedLastTick++;
        return sector;
    }

    // Nearest to a source first, then by coordinate: the same order every run.
    private bool Before(SectorCoord a, SectorCoord b)
    {
        int da = NearestSource(a), db = NearestSource(b);
        if (da != db) return da < db;
        return a.X != b.X ? a.X < b.X : a.Z < b.Z;
    }

    private int NearestSource(SectorCoord coord)
    {
        int best = int.MaxValue;
        for (int i = 0; i < _sourceSectors.Count; i++)
        {
            var s = _sourceSectors[i].Sector;
            best = Math.Min(best, Math.Abs(coord.X - s.X) * Math.Abs(coord.X - s.X) + Math.Abs(coord.Z - s.Z) * Math.Abs(coord.Z - s.Z));
        }
        return best;
    }

    // Each ring plus one, generated on jobs before the ring reaches it; what was generated ahead and is
    // now past every ring plus two is dropped unused.
    private void Ahead()
    {
        if (!_terrain.Jobs) return;
        foreach (var source in _sourceSectors)
        {
            int ahead = source.Radius + 1;
            var centre = source.Sector;
            for (int z = -ahead; z <= ahead; z++)
                for (int x = -ahead; x <= ahead; x++)
                    _terrain.Prefetch(new SectorCoord(centre.X + x, centre.Z + z));
        }

        _stale.Clear();
        _terrain.CollectAhead(_stale);
        for (int i = 0; i < _stale.Count; i++)
            if (!WithinAnyRing(_stale[i], 1 + KeepMargin)) _terrain.DropAhead(_stale[i]);
    }

    // The far ring (SectorLod): what is within a source's far radius and in no full ring has coarse ground
    // and far looks; what came into a full ring, or went past every far one plus the margin, is dropped.
    private void Far(int far, bool ground)
    {
        int widest = 0;
        for (int i = 0; i < _sourceSectors.Count; i++)
            if (_sourceSectors[i].Far > _sourceSectors[i].Radius) widest = Math.Max(widest, _sourceSectors[i].Far);
        _lod.Radius = Math.Max(far, widest);
        StreamedScene? streamed = _world.Resources.TryGet<ActiveScene>(out var scene) ? scene?.Streamed : null;
        if (_lod.TerrainVersion != _terrain.Version || !ReferenceEquals(_lod.Scene, streamed))
        {
            _lod.Clear();   // another ground, or another scene: what was far is something else now
            _lod.TerrainVersion = _terrain.Version;
            _lod.Scene = streamed;
        }
        if (widest == 0 || (!ground && streamed == null))
        {
            if (_lod.Count > 0) _lod.Clear();
            return;
        }

        _stale.Clear();
        var sectors = _lod.Sectors;
        for (int i = 0; i < sectors.Count; i++)
        {
            var coord = sectors[i].Coord;
            if (_ring.Live.Contains(coord) || !WithinAnyFar(coord, KeepMargin)) _stale.Add(coord);
        }
        for (int i = 0; i < _stale.Count; i++) _lod.Remove(_stale[i]);

        foreach (var source in _sourceSectors)
        {
            if (source.Far <= source.Radius) continue;
            var centre = source.Sector;
            for (int z = -source.Far; z <= source.Far; z++)
                for (int x = -source.Far; x <= source.Far; x++)
                {
                    var coord = new SectorCoord(centre.X + x, centre.Z + z);
                    if (_lod.Contains(coord) || _ring.Live.Contains(coord) || WithinAnyRing(coord, 0)) continue;
                    var sector = _lod.Add(coord);
                    if (ground) sector.Job = _terrain.Coarse(coord);
                    streamed?.FarProxies(_world, coord, sector.Proxies);
                }
        }
        _lod.Collect();
    }

    // Every source this tick: those with rings of their own, then the tagged ones and the local player with
    // the cvars' rings. Two sources in one sector are one, with the larger of each radius. The primary is
    // the player's (the first player's), else the first source.
    private void CollectSources(int radius, int far)
    {
        _sourceSectors.Clear();
        _primary = -1;
        foreach (var (transforms, rings, entities) in _rings.Chunks)
        {
            var t = transforms.Span;
            var r = rings.Span;
            for (int i = 0; i < t.Length; i++)
            {
                int at = Add(t[i].LocalPosition, Math.Clamp(r[i].Radius, 0, 8), Math.Clamp(r[i].FarRadius, 0, 16));
                if (_primary < 0 && entities.EntityAt(i).Tags.Has<PlayerControlled>()) _primary = at;
            }
        }
        Collect(_players, radius, far, player: true);
        Collect(_sources, radius, far, player: false);
        if (_primary < 0 && _sourceSectors.Count > 0) _primary = 0;
    }

    private void Collect(Query<Transform> query, int radius, int far, bool player)
    {
        foreach (var (transforms, entities) in query.Chunks)
        {
            var span = transforms.Span;
            for (int i = 0; i < span.Length; i++)
            {
                if (_world.Has<StreamingRing>(entities.EntityAt(i))) continue;   // its own rings, counted above
                int at = Add(span[i].LocalPosition, radius, far);
                if (player && _primary < 0) _primary = at;
            }
        }
    }

    private int Add(Vector3 position, int radius, int far)
    {
        var sector = _origin.SectorOf(position);
        for (int i = 0; i < _sourceSectors.Count; i++)
        {
            if (_sourceSectors[i].Sector != sector) continue;
            var merged = _sourceSectors[i];
            merged.Radius = Math.Max(merged.Radius, radius);
            merged.Far = Math.Max(merged.Far, far);
            _sourceSectors[i] = merged;
            return i;
        }
        _sourceSectors.Add(new Source { Sector = sector, Radius = radius, Far = far });
        return _sourceSectors.Count - 1;
    }

    // Within some source's full ring plus `extra` sectors. Chebyshev distance: rings are squares, so "two
    // sectors away" means two in the longest axis rather than along a diagonal.
    private bool WithinAnyRing(SectorCoord coord, int extra)
    {
        for (int i = 0; i < _sourceSectors.Count; i++)
            if (Distance(coord, _sourceSectors[i].Sector) <= _sourceSectors[i].Radius + extra) return true;
        return false;
    }

    // Within some source's far ring (one that has one) plus `extra` sectors.
    private bool WithinAnyFar(SectorCoord coord, int extra)
    {
        for (int i = 0; i < _sourceSectors.Count; i++)
            if (_sourceSectors[i].Far > _sourceSectors[i].Radius &&
                Distance(coord, _sourceSectors[i].Sector) <= _sourceSectors[i].Far + extra) return true;
        return false;
    }

    private static float Distance(SectorCoord a, SectorCoord b) =>
        MathF.Max(MathF.Abs(a.X - b.X), MathF.Abs(a.Z - b.Z));
}

// A root that crosses a sector edge in a streamed scene belongs to the sector it is in now (4g-3; 14 §3:
// "when a root crosses a sector edge, its sector is updated", in Late). A runtime spawn's cell is moved;
// one the sector placed leaves it — tombstoned there, so placing that sector again does not place it a
// second time — and becomes the new sector's runtime spawn (StreamedScene.Move). One that walks into a
// sector outside the ring goes to sleep there at the tick boundary.
//
// Allocates nothing unless something crossed: it runs in the fixed schedule.
[System("sage.streaming.owners", Phase.Late)]
internal sealed class SectorOwnersSystem : ISystem
{
    private readonly World _world;
    private readonly Origin _origin;
    private readonly Query<Transform, Persistent> _roots;
    private readonly List<(Entity Entity, SectorCoord Now, bool Placed)> _moves = new();

    public SectorOwnersSystem(World world)
    {
        _world = world;
        _origin = world.Origin();
        _roots = world.Query<Transform, Persistent>();
    }

    public void Run(in SystemContext ctx)
    {
        if (!_world.Resources.TryGet<ActiveScene>(out var scene) || scene?.Streamed is not { } streamed) return;
        _world.Resources.TryGet<ContentBaseline>(out var baseline);

        foreach (var (transforms, persistents, entities) in _roots.Chunks)
        {
            var t = transforms.Span;
            var p = persistents.Span;
            for (int n = 0; n < t.Length; n++)
            {
                var entity = entities.EntityAt(n);
                if (!entity.Parent.IsNull) continue;

                string? owner;
                bool placed = false;
                if (_world.TryGet<InCell>(entity, out var cell)) owner = cell.Source;
                else if (baseline != null && baseline.TryGetSource(p[n].Id, out var source)) { owner = source; placed = true; }
                else continue;

                if (owner == null || !streamed.Owns(owner, out var was, out bool known)) continue;
                var now = _origin.SectorOf(t[n].LocalPosition);
                if (known && now == was) continue;
                _moves.Add((entity, now, placed));
            }
        }
        if (_moves.Count == 0) return;

        foreach (var (entity, now, placed) in _moves)
            if (_world.IsAlive(entity)) streamed.Move(_world, entity, now, placed);
        _moves.Clear();
    }
}
