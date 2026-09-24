#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Levels built out of brushes (docs/design/15 §3, TODO F16).
//
// **What this buys, and why it is not the editor.** A heightmap is an outdoors; a Daggerfall-like game
// is mostly indoors, and an interior needs walls that meet, a ceiling, and a doorway you can walk
// through. Brushes give all three, and TrenchBroom — which exists, is good, and is what people already
// know — gives the editing. So the engine's first level format is an import rather than an editor: F28's
// editor can arrive later without anybody waiting for it to build a room.
//
// The split is terrain's, exactly (14 §3): the simulation owns the *data* (parsed, built, and in metres),
// physics builds collision from it in `PrePhysics`, and the client builds meshes from it in
// `FrameUpdate`. Each half does its work once and sets a flag, so there is no event to miss and nothing
// to order.

// A level to load, and how to read it. Pointing at a `.map` from a record rather than hard-coding a path
// means a mod can replace a level exactly as it replaces a texture (17 §3).
[Record("map")]
public sealed class MapRecord
{
    public AssetPath File;                          // e.g. "maps/hut.map"

    // Map units to metres, and the texture size its coordinates assume. Both are per level because both
    // are decisions of whoever drew it (see `MapSpace`).
    public float Scale = MapSpace.DefaultScale;
    public float TextureSize = MapSpace.DefaultTextureSize;

    // Where the map's own origin goes, in absolute metres — the same coordinates a `scene` places
    // things in, converted to origin space on load.
    public Vector3 At;

    // Where a face's texture name looks for its material: texture "wall" becomes "<namespace>:wall".
    // Empty means the namespace the record itself came from.
    public string MaterialNamespace = "";

    // The physics layer every brush gets. Level geometry is world collision, which is layer 0 for now.
    public byte Layer;

    // Stand the level on the terrain under `At` rather than at `At.Y` exactly, with `At.Y` as the height
    // above the ground. A building in an outdoor world wants this and an interior does not, and guessing
    // from whether a terrain exists would make a hut's height depend on load order.
    public bool OnTerrain;
}

// One loaded level: geometry in metres relative to the map's own origin, and the entities that were
// standing in it.
public sealed class MapLevel
{
    public required RecordId Record { get; init; }
    public required string Source { get; init; }            // "mount:path", for anything that goes wrong
    public required List<LevelBrush> Brushes { get; init; }
    public required List<MapEntity> PointEntities { get; init; }
    public required string MaterialNamespace { get; init; }
    public required byte Layer { get; init; }

    // Where the level goes, in absolute metres, and whether it stands on the ground there.
    public required Vector3 At { get; init; }
    public required bool OnTerrain { get; init; }

    // Origin space (14 §3), so it moves with a rebase like everything else. Only meaningful once
    // `Placed` is true: a level that stands on terrain cannot know its height until the ground under it
    // exists, which is later than the moment it loads.
    public Vector3 Position;
    public bool Placed;
    public bool EntitiesSpawned;

    // How its coordinates were read, kept so the entities it spawns convert the same way its brushes did.
    internal MapSpace Space;

    // Built once by each half, the way a terrain sector is.
    public bool CollisionBuilt;
    public bool MeshBuilt;

    public int FaceCount
    {
        get { int n = 0; foreach (var brush in Brushes) n += brush.Faces.Length; return n; }
    }
}

// Every level a world has loaded. A world can hold several — a village is a level per building long
// before it is one big map.
public sealed class MapLevels
{
    public readonly List<MapLevel> Loaded = new();

    // Raised after a level is added or removed, so the halves that build from it can drop what they made.
    public event Action<MapLevel>? Unloaded;

    public void Add(MapLevel level) => Loaded.Add(level);

    public bool Remove(MapLevel level)
    {
        if (!Loaded.Remove(level)) return false;
        Unloaded?.Invoke(level);
        return true;
    }

    public void Clear()
    {
        for (int i = Loaded.Count - 1; i >= 0; i--) Remove(Loaded[i]);
    }
}

public static class MapLoader
{
    // Reads a `.map` record into a world: parse, build the brushes, place the point entities. Returns
    // null and logs when it cannot, because a level that fails to load must not take the game with it —
    // a mapper wants to fix it and try again, in the same session.
    public static MapLevel? Load(World world, RecordId id)
    {
        var engine = world.Engine!;
        if (!engine.Records.TryGet(id, out MapRecord record))
        {
            Log.Error(LogCat.Level, $"No map record '{id}'");
            return null;
        }

        var path = record.File.Path;
        if (engine.Vfs.Which(path) is not { } mount)
        {
            Log.Error(LogCat.Level, $"Map '{id}': '{path}' is not in any mount");
            return null;
        }

        string source = $"{mount.Name}:{path}";
        string text;
        try
        {
            using var stream = mount.Open(path);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            Log.Error(LogCat.Level, $"Map '{id}': {source} could not be read: {ex.Message}");
            return null;
        }

        if (!MapFile.TryParse(text, out var file, out string error))
        {
            Log.Error(LogCat.Level, $"Map '{id}': {source}:{error}");
            return null;
        }

        var space = new MapSpace(record.Scale, record.TextureSize);
        var brushes = new List<LevelBrush>();
        int skipped = 0;

        // Only worldspawn's brushes are geometry today. A solid *entity* (a door, a moving platform) is
        // the same brushes with a classname on them, and becomes interesting with entity I/O (F17) —
        // until then it would be a wall you cannot tell from the wall beside it, so its brushes are left
        // out and its classname is spawned like any other entity.
        var worldspawnBrushes = file.Worldspawn?.Brushes;
        if (worldspawnBrushes != null)
        {
            brushes = BrushGeometry.Build(worldspawnBrushes, space, out skipped);
        }

        var points = new List<MapEntity>();
        foreach (var entity in file.Entities)
        {
            if (ReferenceEquals(entity, file.Worldspawn)) continue;
            points.Add(entity);
        }

        var level = new MapLevel
        {
            Record = id,
            Source = source,
            Brushes = brushes,
            PointEntities = points,
            MaterialNamespace = string.IsNullOrEmpty(record.MaterialNamespace) ? id.Namespace : record.MaterialNamespace,
            Layer = record.Layer,
            At = record.At,
            OnTerrain = record.OnTerrain,
        };

        if (world.Resources.TryGet<MapLevels>(out var already) && already != null)
            foreach (var loaded in already.Loaded)
                if (loaded.Record == id)
                {
                    // A level's position comes from its record, so loading one twice puts two copies in
                    // exactly the same place: every surface z-fighting with itself and two hulls to walk
                    // into. `map_load` after a `map_unload` is the intended use; `map_load` twice is a
                    // mistake, and it is cheaper to say so than to debug what it looks like.
                    Log.Warn(LogCat.Level, $"Map '{id}' is already loaded (map_unload first)");
                    return null;
                }

        if (!world.Resources.TryGet<MapLevels>(out var levels) || levels == null)
        {
            // The module is a default one, so a game can switch it off in `game.json`. Saying so beats
            // throwing out of a scene load.
            Log.Error(LogCat.Level, $"Map '{id}': this world has no MapLevels resource (is MapModule disabled?)");
            return null;
        }
        levels.Add(level);
        Log.Info(LogCat.Level, $"Loaded {source}: {brushes.Count} brushes, {level.FaceCount} faces, "
                             + $"{points.Count} entities{(skipped > 0 ? $", {skipped} brush(es) skipped" : "")}");

        level.Space = space;
        return level;
    }

    // Absolute metres to origin space (R6), optionally standing on the ground — the same conversion a
    // scene placement makes, and for the same reason: a level's coordinates are where it is in the
    // world, not where it is in whatever frame the simulation is using this minute.
    //
    // **This can fail, and failing is not an error.** A level loads while the scene is being built, and
    // the terrain under it is generated a tick or two later by streaming; asking for a height then gets
    // a confident zero, which put the hut's floor at sea level under a hillside the first time. So a
    // level that stands on terrain waits for the sector it stands on, and the systems that build from it
    // skip it until it has somewhere to be.
    public static bool TryPlace(World world, MapLevel level)
    {
        if (level.Placed) return true;

        var position = world.Origin().ToOrigin(level.At);
        if (level.OnTerrain)
        {
            if (!world.Resources.TryGet<Terrain>(out var terrain) || terrain == null) return false;
            var absolute = world.Origin().ToAbsolute(position);
            if (terrain.Sector(Terrain.SectorOf(absolute.X, absolute.Z)) == null) return false;
            position.Y = terrain.HeightAt(position.X, position.Z) + level.At.Y;
        }

        level.Position = position;
        level.Placed = true;
        Log.Debug(LogCat.Level, $"{level.Source}: placed at {position}");
        return true;
    }

    // Spawns the level's point entities, once, after it knows where it is.
    public static void EnsureEntities(World world, MapLevel level)
    {
        if (level.EntitiesSpawned) return;
        level.EntitiesSpawned = true;
        SpawnEntities(world, level, level.Space);
    }

    // `classname` is a prefab id. That is the whole entity mapping, and it is deliberate: a `.map` says
    // "a watcher stands here", and what a watcher *is* stays in the prefab record where a game can change
    // it without touching the level. Keys the engine understands are `origin` and `angle`; anything else
    // is kept on the parsed entity for whoever wants it (entity I/O, F17).
    private static void SpawnEntities(World world, MapLevel level, MapSpace space)
    {
        var engine = world.Engine!;
        int spawned = 0, unknown = 0;

        foreach (var entity in level.PointEntities)
        {
            string className = entity.ClassName;
            if (string.IsNullOrEmpty(className)) continue;

            var prefab = new RecordId(level.Record.Namespace, className);
            if (!engine.Records.Exists(prefab))
            {
                // Not an error: `info_player_start`, `light` and the rest of a mapper's furniture are
                // meaningful to the game, not to the engine. The game reads them off the level.
                Log.Debug(LogCat.Level, $"{level.Source}: no prefab for classname '{className}' (line {entity.Line})");
                unknown++;
                continue;
            }

            entity.TryGetVector("origin", out var origin);
            var at = level.Position + space.ToEngine(origin);

            // Quake's `angle` is degrees counter-clockwise from east; the engine's yaw is degrees about
            // its own up axis, and map north became engine -Z, so the two differ by a quarter turn.
            float yaw = entity.GetFloat("angle") - 90f;

            var spawnedEntity = world.Spawn(prefab, at, yaw);
            if (entity.Keys.TryGetValue("targetname", out var name) && !string.IsNullOrEmpty(name))
                spawnedEntity.Name = new EntityName(name);
            spawned++;
        }

        if (spawned > 0 || unknown > 0)
            Log.Info(LogCat.Level, $"{level.Source}: spawned {spawned} entit(ies)"
                                 + (unknown > 0 ? $", {unknown} classname(s) with no prefab" : ""));
    }
}

// The module: the record, the resource, the collision half, and the commands.
public sealed class MapModule : IModule
{
    public void Init(ModuleContext ctx)
    {
        ctx.Engine.Records.Register<MapRecord>();

        ctx.Engine.CVars.RegisterCommand("map_load", CVarFlags.Cheat,
            "map_load <record>: load a .map level into the current world.", a =>
        {
            if (a.Count == 0) { Log.Warn(LogCat.Console, "map_load <record>   (see rec_list map)"); return; }
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<MapLevels>(out _)) continue;
                var id = ctx.Engine.Records.Resolve("map", a[0]);
                if (!id.IsEmpty) MapLoader.Load(world, id);
                return;
            }
        });

        ctx.Engine.CVars.RegisterCommand("map_list", CVarFlags.None, "Levels loaded in each world.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<MapLevels>(out var levels) || levels == null) continue;
                foreach (var level in levels.Loaded)
                    Log.Info(LogCat.Console, $"  {world.Name}: {level.Record} from {level.Source} — "
                                           + $"{level.Brushes.Count} brushes, {level.FaceCount} faces at {level.Position}");
                if (levels.Loaded.Count == 0) Log.Info(LogCat.Console, $"  {world.Name}: no levels loaded");
            }
        });

        ctx.Engine.CVars.RegisterCommand("map_unload", CVarFlags.Cheat,
            "map_unload: drop every level loaded in the current world.", _ =>
        {
            foreach (var world in ctx.Engine.Worlds)
            {
                if (!world.Resources.TryGet<MapLevels>(out var levels) || levels == null) continue;
                int n = levels.Loaded.Count;
                levels.Clear();
                Log.Info(LogCat.Console, $"unloaded {n} level(s) from {world.Name}");
            }
        });

        FgdExport.RegisterCommand(ctx.Engine);
    }

    public void OnWorldCreated(World world)
    {
        world.Resources.Set(new MapLevels());
        world.AddSystem(new MapCollisionSystem(world), Phase.PrePhysics);
    }
}

// Brush hulls into the physics space, once per level (10 §3).
internal sealed class MapCollisionSystem : ISystem
{
    private readonly World _world;

    public MapCollisionSystem(World world)
    {
        _world = world;

        // A level that goes away takes its statics with it: the entities are destroyed, and physics
        // removes the static (and now releases its shape) when an entity holding one dies.
        // Each half destroys **its own** entities, and the filter is the point rather than tidiness: a
        // level's meshes and its hulls both carry `MapGeometry`, and this handler is subscribed first
        // (MapModule is installed before the client). Destroying everything here would take the mesh
        // entities before `MapMeshSystem` could free their GPU buffers, and the leak it avoids would be
        // the leak it caused. Collision entities are the ones with a `PhysicsBody`.
        world.Resources.Get<MapLevels>().Unloaded += level =>
        {
            foreach (var entity in world.Query<MapGeometry, PhysicsBody>().Entities.ToEntityList())
                if (entity.GetComponent<MapGeometry>().Level == level.Record) world.Destroy(entity);
            level.CollisionBuilt = false;
        };
    }

    public void Run(in SystemContext ctx)
    {
        var levels = _world.Resources.Get<MapLevels>();
        var space = _world.Resources.Get<PhysicsSpace>();

        for (int i = 0; i < levels.Loaded.Count; i++)
        {
            var level = levels.Loaded[i];
            if (level.CollisionBuilt) continue;
            if (!MapLoader.TryPlace(_world, level)) continue;    // waiting for the ground under it

            MapLoader.EnsureEntities(_world, level);
            level.CollisionBuilt = true;
            Build(level, space);
        }
    }

    private void Build(MapLevel level, PhysicsSpace space)
    {
        int built = 0;
        foreach (var brush in level.Brushes)
        {
            var entity = _world.Create(Transform.At(level.Position), $"brush {built}");
            _world.Add(entity, new MapGeometry { Level = level.Record });

            var body = space.AddHull(entity, brush.Hull, level.Position, level.Layer);
            if (body.Handle == 0 && !body.IsStatic) { _world.Destroy(entity); continue; }
            _world.Add(entity, body);
            built++;
        }
        Log.Info(LogCat.Level, $"{level.Source}: {built} brush hull(s) into physics");
    }
}

// Marks an entity as belonging to a level, so unloading one can find what it made.
public struct MapGeometry : IComponent
{
    public RecordId Level;
}
