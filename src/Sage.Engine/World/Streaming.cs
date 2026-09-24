#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

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
// mesh system and physics' collision system react to that, and the entities that live in a sector come
// back through the save (09 §3.4) when maps exist. Dormancy is listed in 14 §3 and not built.

// Tag: the world loads around this. The player has it; a followed companion or a remote camera could
// too, and each one keeps its own ring.
public struct StreamingSource : ITag { }

// What a sector owns: chunk meshes, its collision body, and later the props a map placed in it. When
// the sector goes, these go. It is a component rather than a tag because the *which sector* is the
// whole point, and because a save must be able to tell one sector's belongings from another's.
public struct SectorOwned : IComponent
{
    public SectorCoord Sector;
}

// Streaming is the engine's, not a game's: where the ground comes from is a world question (14 §1).
// A game still chooses the generator and the seed.
public sealed class StreamingModule : IModule
{
    private CVar<int>? _radius;
    private CVar<bool>? _enabled;

    public void Init(ModuleContext ctx)
    {
        _radius = ctx.Engine.CVars.Register("stream_radius", 1, CVarFlags.Archive,
            "Sectors of terrain kept loaded around the player, each 1024 m (14 §3). 1 = a 3x3 ring.");
        _enabled = ctx.Engine.CVars.Register("stream_enabled", true, CVarFlags.None,
            "Load and unload terrain around the player, and move the origin to follow (R6, F14).");
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
            if (terrain.Generator == null) continue;

            // **In this order, and the order is the whole of it (14 §3).** Rebase first so the numbers
            // are small, then generate the ground, *then* put the player on it. Placing first is what
            // the first version did, and the player arrived over a sector that did not exist yet,
            // read a ground height of zero, and fell 1.6 km before the terrain caught up.
            world.Rebase(destination);

            int radius = Math.Clamp(_radius!.Value, 0, 8);
            for (int z = -radius; z <= radius; z++)
                for (int x = -radius; x <= radius; x++)
                    terrain.Load(new SectorCoord(destination.X + x, destination.Z + z));

            var origin = world.Origin();
            foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities.ToEntityList())
            {
                var local = origin.ToOrigin(new Vector3(absoluteX, 0, absoluteZ));
                local.Y = terrain.HeightAt(local.X, local.Z) + 1f;
                world.Teleport(entity, Transform.At(local));
                Log.Info(LogCat.Console, $"{World.Describe(entity)} warped to {absoluteX:F0}, {absoluteZ:F0} " +
                                         $"(sector {destination}, ground {local.Y - 1f:F1} m)");
            }
        }
    }

    public void OnWorldCreated(World world)
    {
        // Late in the tick: everything has moved by now, so the ring is computed from where the player
        // actually ended up, and a rebase lands between ticks rather than in the middle of one.
        world.AddSystem(new StreamingSystem(world, _radius!, _enabled!), Phase.Late);

        // Whatever a sector owns goes when the sector does. The terrain raises this; the sweep is here
        // because the entities are the world's, not the terrain resource's.
        var terrain = world.Resources.Get<Terrain>();
        terrain.Unloaded += coord =>
        {
            int destroyed = 0;
            foreach (var entity in world.Query<SectorOwned>().Entities.ToEntityList())
            {
                if (world.Get<SectorOwned>(entity).Sector != coord) continue;
                world.Destroy(entity);
                destroyed++;
            }
            world.FlushCommands();
            if (destroyed > 0) Log.Debug(LogCat.Streaming, $"sector {coord}: {destroyed} owned entities destroyed");
        };
    }
}

public sealed class StreamingSystem : ISystem
{
    private readonly World _world;
    private readonly Terrain _terrain;
    private readonly Origin _origin;
    private readonly CVar<int> _radius;
    private readonly CVar<bool> _enabled;

    private readonly ArchetypeQuery<Transform> _sources;
    private readonly ArchetypeQuery<Transform> _players;

    // Reused every tick: this runs in the fixed schedule, where allocating is not allowed (02 §4.6).
    private readonly List<SectorCoord> _sourceSectors = new();
    private readonly List<SectorCoord> _stale = new();

    // How far past the ring a sector survives before being dropped, and how far a source may drift
    // from the origin before everything shifts. Both in sectors.
    private const int KeepMargin = 1;
    private const float RebaseDistance = 1.5f;

    public StreamingSystem(World world, CVar<int> radius, CVar<bool> enabled)
    {
        _world = world;
        _terrain = world.Resources.Get<Terrain>();
        _origin = world.Origin();
        _radius = radius;
        _enabled = enabled;
        _sources = world.Query<Transform>().AllTags(Tags.Get<StreamingSource>());
        // Before a game marks anything, the local player is the obvious source: a world streaming
        // around nothing would unload the ground under the only thing standing on it.
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        if (!_enabled.Value || _terrain.Generator == null) return;

        CollectSources();
        if (_sourceSectors.Count == 0) return;

        int radius = Math.Clamp(_radius.Value, 0, 8);

        // Load the ring around each source.
        foreach (var centre in _sourceSectors)
            for (int z = -radius; z <= radius; z++)
                for (int x = -radius; x <= radius; x++)
                {
                    var coord = new SectorCoord(centre.X + x, centre.Z + z);
                    if (!_terrain.IsLoaded(coord)) _terrain.Load(coord);
                }

        // Drop anything past the ring plus its margin. Collected first, because unloading raises an
        // event that destroys entities and the terrain's own list must not be walked while it changes.
        _stale.Clear();
        var loaded = _terrain.Sectors;
        for (int i = 0; i < loaded.Count; i++)
            if (!WithinAnySource(loaded[i].Coord, radius + KeepMargin)) _stale.Add(loaded[i].Coord);
        for (int i = 0; i < _stale.Count; i++) _terrain.Unload(_stale[i]);

        // And the origin follows, once the nearest source is far enough that precision would suffer.
        var nearest = _sourceSectors[0];
        float distance = Distance(nearest, _origin.Sector);
        for (int i = 1; i < _sourceSectors.Count; i++)
        {
            float d = Distance(_sourceSectors[i], _origin.Sector);
            if (d < distance) { distance = d; nearest = _sourceSectors[i]; }
        }
        if (distance > RebaseDistance) _world.Rebase(nearest);
    }

    // Tagged sources if there are any, the local player otherwise.
    private void CollectSources()
    {
        _sourceSectors.Clear();
        Collect(_sources);
        if (_sourceSectors.Count == 0) Collect(_players);
    }

    private void Collect(ArchetypeQuery<Transform> query)
    {
        foreach (var (transforms, _) in query.Chunks)
        {
            var span = transforms.Span;
            for (int i = 0; i < span.Length; i++)
            {
                var sector = _origin.SectorOf(span[i].LocalPosition);
                if (!_sourceSectors.Contains(sector)) _sourceSectors.Add(sector);
            }
        }
    }

    // Chebyshev distance in sectors: rings are squares, so "two sectors away" means two in the
    // longest axis rather than along a diagonal.
    private bool WithinAnySource(SectorCoord coord, int radius)
    {
        for (int i = 0; i < _sourceSectors.Count; i++)
            if (Distance(coord, _sourceSectors[i]) <= radius) return true;
        return false;
    }

    private static float Distance(SectorCoord a, SectorCoord b) =>
        MathF.Max(MathF.Abs(a.X - b.X), MathF.Abs(a.Z - b.Z));
}
