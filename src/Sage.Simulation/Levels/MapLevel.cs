#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

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
[Record("map", Plugin = "sage.maps")]
[Experimental("SAGE0122", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // brush maps from TrenchBroom (.map): replaced by the level editor (#61)
public sealed class MapRecord
{
    [AssetKind("map")] public AssetPath File;                          // e.g. "maps/hut.map"

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

    // What a face is made of when no physics_material's "textures" names its texture (issue #270).
    [RecordRef("physics_material"), Property(Tooltip = "The surface of a brush face no physics_material's textures name; empty = none")]
    public RecordId Surface;

    // Bake a lightmap for the level's brushes at load (issue #313, Lightmap.cs): texels per metre, 0 = none.
    // Its lamps with `"baked": true` go into it with their shadows, and the sky each face can see.
    [Property(Min = 0, Max = 32, Unit = "texels/m", Tooltip = "Bake a lightmap at load: texels per metre (4 is a good start); 0 = none, the brushes are lit dynamically only")]
    public float Lightmap;
}

// One loaded level: geometry in metres relative to the map's own origin, and the entities that were
// standing in it.
[Experimental("SAGE0122", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // brush maps from TrenchBroom (.map): replaced by the level editor (#61)
public sealed class MapLevel
{
    public required RecordId Record { get; init; }
    public required string Source { get; init; }            // "mount:path", for anything that goes wrong
    public required List<LevelBrush> Brushes { get; init; }
    public required List<MapEntity> PointEntities { get; init; }

    // Brushes that belong to an *entity* rather than to the level: a door, a lift, a trigger volume.
    // They are the same geometry, built around their own origin so the thing can move (15 §3, F17).
    public required List<SolidEntity> Solids { get; init; }
    public required string MaterialNamespace { get; init; }
    public required byte Layer { get; init; }

    // Where the level goes, in absolute metres, and whether it stands on the ground there.
    public required Vector3 At { get; init; }
    public required bool OnTerrain { get; init; }

    // What its faces are made of (issue #270): the record's fallback, and the texture table built from
    // the physics_material records the first time a hull asks.
    internal RecordId Surface;
    internal SurfaceTextures? SurfaceTextures;

    // What a face with this texture is made of, as a hit on it would say: for code that reads a level's
    // brushes itself, like the navmesh's areas (issue #271).
    public RecordId SurfaceOf(string texture, RecordStore records) =>
        (SurfaceTextures ??= SurfaceTextures.From(records, Surface)).Of(texture);

    // Origin space (14 §3), so it moves with a rebase like everything else. Only meaningful once
    // `Placed` is true: a level that stands on terrain cannot know its height until the ground under it
    // exists, which is later than the moment it loads.
    public Vector3 Position;
    public bool Placed;
    public bool EntitiesSpawned;

    // How its coordinates were read, kept so the entities it spawns convert the same way its brushes did.
    internal MapSpace Space;

    // Brushes that enclosed nothing and were left out, for the load's log line.
    internal int Skipped;

    // The lightmap (issue #313): texels per metre asked for (0 = none), the bake once it is made, and
    // whether it has been tried. The client builds the level's meshes after it, so they carry its UVs.
    internal float LightmapDensity;
    internal LevelLightmap? Lightmap;
    internal bool LightmapDone;
    internal bool LightmapPending => LightmapDensity > 0f && !LightmapDone;

    // Reads a level's file and builds its brushes without putting it in a world: for a content check that
    // needs the geometry, such as the navmesh's unreachable markers (#264). Says nothing and returns null
    // when the file cannot be read or parsed; the level's own load says why.
    public static MapLevel? Read(Engine engine, RecordId id, MapRecord record) =>
        MapLoader.Read(engine, id, record, quiet: true);

    // Where a point entity stands relative to the level's own origin, in metres: its `origin` key through
    // the level's `MapSpace`. Add `Position` for where it is in the world once the level is placed.
    public Vector3 LocalPositionOf(MapEntity entity)
    {
        entity.TryGetVector("origin", out var origin);
        return Space.ToEngine(origin);
    }

    // Built once by each half, the way a terrain sector is.
    public bool CollisionBuilt;
    public bool MeshBuilt;

    public int FaceCount
    {
        get { int n = 0; foreach (var brush in Brushes) n += brush.Faces.Length; return n; }
    }

    // Where a classname the engine has no prefab for actually stands (15 §10a).
    //
    // A mapper's furniture is the game's to interpret — `info_player_start` is the one every game wants
    // — and until this existed "the game reads it off the level" was not something a game could do:
    // `PointEntities` hands out raw map coordinates, and turning those into a place in the world needs
    // the level's `MapSpace` and its `Position`, neither of which is a game's to have. Both conversions
    // are subtle enough to get wrong twice (the axis swap, and Quake's quarter turn), so they are done
    // once, here.
    //
    // False if the level has no such entity, or if it is not `Placed` yet — a level standing on terrain
    // does not know where it is until the ground under it exists, and answering with its unplaced
    // position would be a confident wrong answer.
    public bool TryFindPoint(string className, out Vector3 at, out float yaw)
    {
        at = Vector3.Zero;
        yaw = 0f;
        if (!Placed) return false;

        foreach (var entity in PointEntities)
        {
            if (!string.Equals(entity.ClassName, className, StringComparison.OrdinalIgnoreCase)) continue;

            entity.TryGetVector("origin", out var origin);
            at = Position + Space.ToEngine(origin);
            yaw = entity.GetFloat("angle") - 90f;    // the same quarter turn `SpawnEntities` applies
            return true;
        }

        return false;
    }

    // Where the point entity with this `targetname` stands (a load door's entry, 4g-5), whether or not it
    // spawned anything: the same conversion as TryFindPoint. False when there is none or the level is not
    // placed yet.
    internal bool TryFindNamed(string targetName, out Vector3 at, out float yaw)
    {
        at = Vector3.Zero;
        yaw = 0f;
        if (!Placed || string.IsNullOrEmpty(targetName)) return false;
        foreach (var entity in PointEntities)
        {
            if (!entity.Keys.TryGetValue("targetname", out var name) || !string.Equals(name, targetName, StringComparison.Ordinal)) continue;
            entity.TryGetVector("origin", out var origin);
            at = Position + Space.ToEngine(origin);
            yaw = entity.GetFloat("angle") - 90f;
            return true;
        }
        return false;
    }
}

// One brush entity: its brushes, where its own origin is, and what it became once spawned.
[Experimental("SAGE0122", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // brush maps from TrenchBroom (.map): replaced by the level editor (#61)
public sealed class SolidEntity
{
    public required MapEntity Source { get; init; }
    public required List<LevelBrush> Brushes { get; init; }

    // Where the entity's own origin sits inside the level, in metres. Brush positions are relative to
    // it, which is the whole trick that lets a door move: moving the entity moves its geometry, and
    // nothing has to rebuild anything.
    public required Vector3 Origin { get; init; }

    // Every corner of every brush it owns, relative to that origin: one convex hull for the entity.
    public required Vector3[] Hull { get; init; }

    // `"trigger" "1"`: something you walk into rather than against. Read once here so both halves agree
    // — physics makes it a trigger volume, and the client **does not draw it**, which is the whole point
    // of a trigger. (It drew one, the first time: standing in the hut's doorway put you inside a grey box.)
    public required bool IsTrigger { get; init; }

    public Entity Spawned;
}

// Every level a world has loaded. A world can hold several — a village is a level per building long
// before it is one big map.
[Experimental("SAGE0122", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // brush maps from TrenchBroom (.map): replaced by the level editor (#61)
public sealed class MapLevels
{
    public readonly List<MapLevel> Loaded = new();

    // Raised after a level is added or removed, so the halves that build from it can drop what they made.
    public event Action<MapLevel>? Unloaded;

    // Raised for each brush entity a level spawns, with where the mapper drew it, before its collision
    // is built: a plugin whose part needs the drawn position (a mover's "shut") takes it here. The
    // prefab parts that built the entity ran before the map placed it.
    public event Action<World, Entity, Vector3>? SolidSpawned;

    internal void RaiseSolidSpawned(World world, Entity entity, Vector3 at) => SolidSpawned?.Invoke(world, entity, at);

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

internal static class MapLoader
{
    // The `targetname`s a map's entities have, added to `names`, without loading it (a load door's check,
    // 4g-5). False when the file cannot be read or parsed: the map's own load says why.
    public static bool TryTargetNames(Engine engine, MapRecord record, HashSet<string> names)
    {
        var path = record.File.Path;
        if (record.File.IsEmpty || engine.Vfs.Which(path) is not { } mount) return false;
        string text;
        try
        {
            using var stream = mount.Open(path);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException) { return false; }
        if (!MapFile.TryParse(text, out var file, out _)) return false;
        foreach (var entity in file.Entities)
            if (entity.Keys.TryGetValue("targetname", out var name) && !string.IsNullOrEmpty(name)) names.Add(name);
        return true;
    }

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

        if (Read(engine, id, record, quiet: false) is not { } level) return null;

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
        Log.Info(LogCat.Level, $"Loaded {level.Source}: {level.Brushes.Count} brushes, {level.FaceCount} faces, "
                             + $"{level.PointEntities.Count} entities{(level.Skipped > 0 ? $", {level.Skipped} brush(es) skipped" : "")}");
        return level;
    }

    // Reads and builds a `.map` record without putting it in any world: what `Load` does first, and what a
    // content check that needs the geometry (the navmesh's unreachable markers, #264) reads. `quiet`
    // leaves the reasons to the level's own load, so a check does not say them a second time.
    public static MapLevel? Read(Engine engine, RecordId id, MapRecord record, bool quiet)
    {
        var path = record.File.Path;
        if (record.File.IsEmpty || engine.Vfs.Which(path) is not { } mount)
        {
            if (!quiet) Log.Error(LogCat.Level, $"Map '{id}': '{path}' is not in any mount");
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
            if (!quiet) Log.Error(LogCat.Level, $"Map '{id}': {source} could not be read: {ex.Message}");
            return null;
        }

        if (!MapFile.TryParse(text, out var file, out string error))
        {
            if (!quiet) Log.Error(LogCat.Level, $"Map '{id}': {source}:{error}");
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
            brushes = BrushGeometry.Build(worldspawnBrushes, space, out skipped, quiet);
        }

        var points = new List<MapEntity>();
        var solids = new List<SolidEntity>();
        foreach (var entity in file.Entities)
        {
            if (ReferenceEquals(entity, file.Worldspawn)) continue;

            if (entity.Brushes.Count == 0) { points.Add(entity); continue; }

            // A solid entity's brushes are built around the entity's own origin: its `origin` key if the
            // mapper set one (TrenchBroom writes one for a rotating door's hinge), otherwise the middle
            // of the brushes themselves, which is what a mapper means by "the door" when they have not
            // said otherwise.
            var built = BrushGeometry.Build(entity.Brushes, space, out int entitySkipped, quiet);
            skipped += entitySkipped;
            if (built.Count == 0)
            {
                if (!quiet) Log.Warn(LogCat.Level, $"{source}: '{entity.ClassName}' (line {entity.Line}) has no geometry left");
                continue;
            }

            Vector3 origin;
            if (entity.TryGetVector("origin", out var written)) origin = space.ToEngine(written);
            else
            {
                var min = built[0].Min;
                var max = built[0].Max;
                foreach (var brush in built)
                {
                    min = Vector3.Min(min, brush.Min);
                    max = Vector3.Max(max, brush.Max);
                }
                origin = (min + max) * 0.5f;
            }

            var hull = new List<Vector3>();
            foreach (var brush in built)
                foreach (var point in brush.Hull)
                {
                    var local = point - origin;
                    bool seen = false;
                    foreach (var existing in hull)
                        if (Vector3.DistanceSquared(existing, local) < 1e-6f) { seen = true; break; }
                    if (!seen) hull.Add(local);
                }

            solids.Add(new SolidEntity
            {
                Source = entity,
                Brushes = built,
                Origin = origin,
                Hull = hull.ToArray(),
                IsTrigger = entity.Keys.TryGetValue("trigger", out var flag)
                         && (flag == "1" || flag.Equals("true", StringComparison.OrdinalIgnoreCase)),
            });
        }

        return new MapLevel
        {
            Record = id,
            Source = source,
            Brushes = brushes,
            PointEntities = points,
            Solids = solids,
            MaterialNamespace = string.IsNullOrEmpty(record.MaterialNamespace) ? id.Namespace : record.MaterialNamespace,
            Layer = record.Layer,
            At = record.At,
            OnTerrain = record.OnTerrain,
            Surface = record.Surface,
            Space = space,
            Skipped = skipped,
            LightmapDensity = record.Lightmap,
        };
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
    // The same question asked of the whole world: is there an `info_player_start` anywhere? A game asks
    // that, not "in this particular level", because which level is loaded is the game's own business.
    // First match in load order wins, which is the only rule that makes a second one a mistake rather
    // than a coin toss.
    public static bool TryFindPoint(World world, string className, out Vector3 at, out float yaw)
    {
        at = Vector3.Zero;
        yaw = 0f;
        if (!world.Resources.TryGet<MapLevels>(out var levels) || levels == null) return false;

        foreach (var level in levels.Loaded)
            if (level.TryFindPoint(className, out at, out yaw)) return true;

        return false;
    }

    public static void EnsureEntities(World world, MapLevel level)
    {
        if (level.EntitiesSpawned) return;
        level.EntitiesSpawned = true;
        SpawnEntities(world, level, level.Space);
    }

    // Every level that can be placed now spawns its entities now, rather than at the next PrePhysics: a
    // load lays its state onto them before it returns (4i-3). One still waiting for its ground keeps the
    // save's state for it pending, and is given it when it spawns.
    public static void EnsureEntities(World world)
    {
        if (!world.Resources.TryGet<MapLevels>(out var levels) || levels == null) return;
        foreach (var level in levels.Loaded)
            if (TryPlace(world, level)) EnsureEntities(world, level);
    }

    // A level's entities gone, to be spawned again (a load re-placing a level a person loaded by hand).
    public static void ForgetEntities(World world, MapLevel level)
    {
        ContentIds.Forget(world, ContentIds.MapSource(level.Record));
        foreach (var entity in world.Query<FromMap>().Entities.ToEntityList())
            if (world.IsAlive(entity) && entity.GetComponent<FromMap>().Level == level.Record) world.Destroy(entity);
        foreach (var solid in level.Solids) solid.Spawned = default;
        level.EntitiesSpawned = false;
    }

    // A map entity's identity in saves (4i-3): its `id` key, else its `targetname` when no other entity in
    // the level has that name, else the level and its place in the file — which moves when the mapper adds
    // an entity above it, and is why the first two win.
    private static PersistentId MapEntityId(MapLevel level, MapEntity entity, string kind, int index, Dictionary<string, int> names)
    {
        string source = ContentIds.MapSource(level.Record);
        if (entity.Keys.TryGetValue("id", out var authored) && ContentIds.Authored(source, authored) is { } id) return id;
        if (entity.Keys.TryGetValue("targetname", out var name) && !string.IsNullOrEmpty(name) && names.TryGetValue(name, out int n) && n == 1)
            return PersistentId.FromName($"{source}:name:{name}");
        return PersistentId.FromName($"{source}:{kind}:{index}");
    }

    private static Dictionary<string, int> TargetNames(MapLevel level)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(MapEntity entity)
        {
            if (entity.Keys.TryGetValue("targetname", out var name) && !string.IsNullOrEmpty(name))
                names[name] = names.TryGetValue(name, out int n) ? n + 1 : 1;
        }
        foreach (var entity in level.PointEntities) Count(entity);
        foreach (var solid in level.Solids) Count(solid.Source);
        return names;
    }

    // `classname` is a prefab id. That is the whole entity mapping, and it is deliberate: a `.map` says
    // "a watcher stands here", and what a watcher *is* stays in the prefab record where a game can change
    // it without touching the level. Keys the engine understands are `origin`, `angle`, `targetname`,
    // `group` (the groups a wire's `@group` target reaches, issue #276), outputs (`On…`) and a prefab's own fields (`light.range`, PrefabKeys); anything else
    // is kept on the parsed entity for whoever wants it (entity I/O, F17).
    private static void SpawnEntities(World world, MapLevel level, MapSpace space)
    {
        var engine = world.Engine!;
        int spawned = 0;
        List<string>? unknown = null;
        string source = ContentIds.MapSource(level.Record);
        var names = TargetNames(level);

        for (int index = 0; index < level.PointEntities.Count; index++)
        {
            var entity = level.PointEntities[index];
            string className = entity.ClassName;
            if (string.IsNullOrEmpty(className)) continue;

            var prefab = ClassPrefab(level, className);
            if (!engine.Records.Exists(prefab))
            {
                // Not an error: `info_player_start` and the rest of a mapper's furniture is meaningful
                // to the game, not to the engine, and `MapLevel.TryFindPoint` is how the game reads it
                // off the level. They are *named* in the summary below rather than counted, because
                // "1 classname(s) with no prefab" tells you something was ignored and not what.
                Log.Debug(LogCat.Level, $"{level.Source}: no prefab for classname '{className}' (line {entity.Line})");
                unknown ??= new List<string>();
                if (!unknown.Contains(className, StringComparer.OrdinalIgnoreCase)) unknown.Add(className);
                continue;
            }

            entity.TryGetVector("origin", out var origin);
            var at = level.Position + space.ToEngine(origin);

            // Quake's `angle` is degrees counter-clockwise from east; the engine's yaw is degrees about
            // its own up axis, and map north became engine -Z, so the two differ by a quarter turn.
            float yaw = entity.GetFloat("angle") - 90f;

            // With the entity's own values for the prefab's fields (`"light.range" "12"`, PrefabKeys).
            var spawnedEntity = world.SpawnWithoutId(prefab, at, yaw, entity.Keys, $"{level.Source}:{entity.Line}");
            if (spawnedEntity.IsNull) continue;

            MapEntityIO.Attach(world, spawnedEntity,
                               MapEntityIO.Read(entity, engine, $"{level.Source}:{entity.Line}"));

            // Marked with the level that placed it. Without this a level's furniture outlived the level:
            // unloading took the walls and left the watcher standing in the open, and the Sandbox
            // reloads its maps whenever a record file is saved, so every save left another one behind.
            world.Add(spawnedEntity, new FromMap { Level = level.Record });

            if (entity.Keys.TryGetValue("targetname", out var name) && !string.IsNullOrEmpty(name))
                spawnedEntity.Name = name;
            MapEntityIO.Group(world, spawnedEntity, entity);
            ContentIds.Place(world, source, spawnedEntity, MapEntityId(level, entity, "point", index, names));
            spawned++;
        }

        for (int i = 0; i < level.Solids.Count; i++)
        {
            SpawnSolid(world, level, level.Solids[i], i, ref spawned);
            if (level.Solids[i].Spawned is { IsNull: false } solid)
                ContentIds.Place(world, source, solid, MapEntityId(level, level.Solids[i].Source, "solid", i, names));
        }

        // Names are resolved after everything is in the world, so a wire may point either way along the
        // file: a trigger at the top of the map can open a door written at the bottom of it.
        foreach (var entity in world.Query<FromMap>().Entities.ToEntityList())
            if (entity.GetComponent<FromMap>().Level == level.Record)
                MapEntityIO.Resolve(world, entity, level.Source);

        if (spawned > 0 || unknown != null)
            Log.Info(LogCat.Level, $"{level.Source}: spawned {spawned} entit(ies)"
                                 + (unknown != null ? $", left for the game: {string.Join(", ", unknown)}" : ""));

        // What a save said about this level — what was taken, what a counter reached — now it is here.
        ContentIds.Finish(world, source);
    }

    // A classname is a prefab of the level's own namespace, or of another when it says so
    // (`sage:scripted_camera`, issue #80): the engine's prefabs are placeable from a map too. A classname
    // that is not an id at all is looked for as written, and is not found.
    private static RecordId ClassPrefab(MapLevel level, string className)
    {
        if (className.IndexOf(':') < 0) return new RecordId(level.Record.Namespace, className);
        try { return RecordId.Parse(className, level.Record.Namespace); }
        catch (FormatException) { return new RecordId(level.Record.Namespace, className); }
    }

    // A brush entity: a door, a lift, a trigger volume. It is an ordinary entity that happens to own
    // geometry — which is why it can be a prefab like any other, and why "how far does this door open"
    // lives in the prefab rather than in the level.
    private static void SpawnSolid(World world, MapLevel level, SolidEntity solid, int index, ref int spawned)
    {
        var engine = world.Engine!;
        string where = $"{level.Source}:{solid.Source.Line}";
        var at = level.Position + solid.Origin;
        string className = solid.Source.ClassName;

        // With a prefab of that name the entity is built from it (a `door` prefab brings its `mover`);
        // without one it is still a solid you can walk into, which is what an untyped brush entity in a
        // map means.
        // **Not rotated**, unlike a point entity. A brush entity's geometry is already where the mapper
        // drew it, so turning the entity would turn the mesh away from the hull that stayed put — which
        // is what a door two feet wide and facing sideways looked like the first time. Quake's `angle` on
        // a solid means which way it *moves* rather than which way it faces, and nothing reads it yet.
        var prefab = ClassPrefab(level, className);
        var entity = engine.Records.Exists(prefab)
            ? world.SpawnWithoutId(prefab, at, 0f, solid.Source.Keys, where)
            : world.Create(Transform.At(at), className.Length > 0 ? className : "brush entity");

        if (entity.IsNull) return;

        if (solid.Source.Keys.TryGetValue("targetname", out var name) && !string.IsNullOrEmpty(name))
            entity.Name = name;
        MapEntityIO.Group(world, entity, solid.Source);

        world.Add(entity, new FromMap { Level = level.Record });
        world.Add(entity, new MapSolid { Level = level.Record, Index = index });
        MapEntityIO.Attach(world, entity, MapEntityIO.Read(solid.Source, engine, where));

        // A mover has to know where "shut" is, and only now does anybody: the prefab part that added it
        // ran before the map had put the entity where the mapper drew it (MoverModule subscribes).
        if (world.Resources.TryGet<MapLevels>(out var levels) && levels != null) levels.RaiseSolidSpawned(world, entity, at);

        // One hull for the whole entity, from every corner of every brush it owns. A door drawn as one
        // box is exact; an L-shaped one drawn as a single entity collides as the convex hull of both
        // arms, which is solid across the inside of the L. Draw that as two entities — and it is worth
        // knowing rather than guessing at, which is why it is written here and in 15 §10a.
        var body = world.Resources.Get<IPhysicsWorld>()
                        .AddHull(entity, solid.Hull, at, level.Layer, solid.IsTrigger);
        if (!body.IsStatic && body.Handle == 0) Log.Warn(LogCat.Level, $"{where}: '{className}' has no collision");
        else
        {
            world.Add(entity, body);
            MapSurfaces.Give(world, level, body, solid.Brushes);   // issue #270
        }

        // A ladder is climbed from inside its volume (issue #263); a solid one is a wall with rungs drawn on.
        if (!solid.IsTrigger && entity.HasComponent<Ladder>())
            Log.Warn(LogCat.Level, $"{where}: '{className}' is a ladder but not a trigger; add \"trigger\" \"1\" to climb it");

        // A reverb zone drawn as brushes is the box around them (issue #329), walked into rather than against.
        if (entity.HasComponent<ReverbZone>())
        {
            ReverbZones.FitToBrushes(entity, solid.Hull);
            if (!solid.IsTrigger)
                Log.Warn(LogCat.Level, $"{where}: '{className}' is a reverb zone but not a trigger; add \"trigger\" \"1\" so it is not a wall");
        }

        solid.Spawned = entity;
        spawned++;
    }
}

// The module: the record, the resource, the collision half, and the commands.
internal static class MapEntityIO
{
    // A key whose name starts with `On` is an output wired to somewhere, written the way Hammer writes
    // it: `target,input,parameter,delay,times`. Only the first two are required.
    //
    // Nothing else is read as a connection, and that is the same promise the FGD makes: every key this
    // engine offers is a key it acts on. Quake's bare `target`/`targetname` pair is deliberately *not*
    // one of them — it says which entity but never which input, so half of what it means would have to
    // be guessed per classname, which is exactly the untyped string resolution this replaces (04 §3.4).
    public static Connection[]? Read(MapEntity entity, Engine engine, string where)
    {
        List<Connection>? wires = null;

        foreach (var (key, value) in entity.Keys)
        {
            if (key.Length < 3 || !key.StartsWith("On", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(value)) continue;

            var parts = value.Split(',');
            if (parts.Length < 2)
            {
                Log.Error(LogCat.Events, $"{where}: '{key}' should be \"target,input[,parameter,delay,times]\", found '{value}'");
                continue;
            }

            var wire = new Connection
            {
                Output = key,
                Target = parts[0].Trim(),
                Input = parts[1].Trim(),
                Parameter = parts.Length > 2 ? parts[2].Trim() : "",
            };

            if (parts.Length > 3 && float.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float delay))
                wire.Delay = delay;
            if (parts.Length > 4 && int.TryParse(parts[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int times))
                wire.Times = times;

            // Checked here rather than when it fires, which is the point of the whole exercise: a
            // mapper finds out about a typo when the level loads, with the line to fix, instead of
            // wondering at run time why the door does nothing.
            if (!engine.Inputs.Has(wire.Input))
            {
                Log.Error(LogCat.Events, $"{where}: '{key}' sends '{wire.Input}', which is not an input (see io_list)");
                continue;
            }
            if (IOTargets.Problem(engine, wire.Target) is { } problem)
            {
                Log.Error(LogCat.Events, $"{where}: '{key}' targets {problem}");
                continue;
            }

            (wires ??= new List<Connection>()).Add(wire);
        }

        return wires?.ToArray();
    }

    // Attaches the wires and resolves their targets by name. Called after everything in the level is
    // spawned, so a wire can point at anything else in the same map.
    public static void Attach(World world, Entity entity, Connection[]? wires)
    {
        if (wires == null || entity.IsNull) return;
        world.Add(entity, new IOConnections { Wires = wires });
    }

    // A map entity's `group` key: the groups it is in, for wires to `@group` (issue #276). Any entity may
    // have one, whatever its classname, so it is a key of its own rather than one of a prefab's fields.
    public static void Group(World world, Entity entity, MapEntity source)
    {
        if (entity.IsNull || !source.Keys.TryGetValue("group", out var names) || string.IsNullOrWhiteSpace(names)) return;
        if (entity.TryGetComponent<IOGroup>(out var prefab) && !string.IsNullOrWhiteSpace(prefab.Names))
            names = prefab.Names + " " + names;       // the prefab's groups and the map's
        world.Add(entity, new IOGroup { Names = names.Trim() });
    }

    public static void Resolve(World world, Entity entity, string where)
    {
        if (entity.IsNull || !entity.HasComponent<IOConnections>()) return;
        var wires = entity.GetComponent<IOConnections>().Wires;
        if (wires == null) return;

        foreach (var wire in wires)
        {
            if (wire.Target.StartsWith("!", StringComparison.Ordinal)) continue;   // resolved per firing
            if (IOTargets.IsSelector(wire.Target))
            {
                // A group is found when the input arrives; an empty one now is worth a word, not an error
                // (its members may be spawned later, or be in another level).
                var members = new List<Entity>();
                IOTargets.Members(world, wire.Target, members);
                if (members.Count == 0)
                    Log.Warn(LogCat.Events, $"{where}: '{wire.Output}' points at '{wire.Target}', which has no members in the level "
                                          + "(it will be looked for again each time it fires)");
                continue;
            }
            wire.Resolved = world.FindByName(wire.Target);
            if (wire.Resolved.IsNull)
                Log.Warn(LogCat.Events, $"{where}: '{wire.Output}' points at '{wire.Target}', which is not in the level "
                                      + "(it will be looked for again each time it fires)");
        }
    }
}

[Plugin("sage.maps", "0.1.0")]
public sealed class MapModule : IModule
{
    public void Init(ModuleContext ctx)
    {
        ctx.Engine.CVars.RegisterCommand("map_load", CVarFlags.Cheat,
            "map_load <record>: load a .map level into the first world that can hold one.", a =>
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

        // What an `info_player_start` is *for*. The engine will not spawn you there — where a game starts
        // its player is the game's decision, and the Sandbox's is a scene placement — but a mapper
        // testing a room wants to stand in it, and before this the marker they placed did nothing at all
        // and said nothing about it.
        ctx.Engine.CVars.RegisterCommand("map_goto", CVarFlags.Cheat,
            "map_goto [classname]: stand the player where the map says, `info_player_start` by default.", a =>
        {
            string className = a.Count > 0 ? a[0] : "info_player_start";
            int moved = 0;

            ctx.Engine.ForEachPlayer((world, player) =>
            {
                if (!MapLoader.TryFindPoint(world, className, out var at, out float yaw)) return;

                float radians = yaw * MathF.PI / 180f;

                var where = Transform.At(at);
                where.LocalRotation = SageMath.RotationFromYaw(radians);
                world.Teleport(player, where);

                // And *face* that way, which is not the same thing as being turned that way: the body's
                // rotation is rewritten from `PawnIntent.Yaw` every tick, and that comes from the view
                // angles the host accumulates. Setting the transform alone put the player in the room
                // looking whichever way they already were, for one tick, and then not even that.
                if (world.Has<PawnIntent>(player))
                {
                    ref var intent = ref world.Get<PawnIntent>(player);
                    intent.Yaw = radians;
                }

                if (world.Resources.TryGet<PlayerInput>(out var input) && input != null)
                    input.RequestView(radians);

                moved++;
                Log.Info(LogCat.Console, $"  {world.Name}: player moved to '{className}' at {at}");
            });

            // A level that stands on terrain is not placed until the ground under it exists, so "not
            // there" and "not there *yet*" are different answers and the message says which.
            if (moved == 0)
                Log.Warn(LogCat.Console, $"No '{className}' in any level that is loaded and placed "
                                       + "(map_list shows what is), or no player to move.");
        });

        ctx.Engine.CVars.RegisterCommand("map_unload", CVarFlags.Cheat,
            "map_unload: drop every level loaded in every world.", _ =>
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
        var levels = new MapLevels();
        world.Resources.Add(levels);
        // Subscribed before the collision system's handler, which destroys the level's entities: what is
        // dead *before* that is what the game destroyed, and stays so if the level is loaded again (4i-3).
        levels.Unloaded += level => ContentIds.Forget(world, ContentIds.MapSource(level.Record));
        world.AddSystem(new MapCollisionSystem(world));

        // A level's `Position` is a world position held outside the ECS, and the rule for those is the
        // same as for particles, audio and AI: follow the rebase (R6, 14 §3). The entities built from it
        // move themselves — they have transforms — but the level would go on handing out the position it
        // had before the world shifted, and anything built after that would be a sector out.
        world.Origin().Rebased += offset =>
        {
            foreach (var level in levels.Loaded) level.Position += offset;
        };
    }
}

// Brush hulls into the physics space, once per level (10 §3).
[System("sage.maps.collision", Phase.PrePhysics)]
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

            // And whatever the level put in the world. A spawned entity is anything at all — it may well
            // have a mesh of its own — so it carries its own marker rather than sharing the geometry one.
            foreach (var entity in world.Query<FromMap>().Entities.ToEntityList())
                if (entity.GetComponent<FromMap>().Level == level.Record) world.Destroy(entity);

            level.CollisionBuilt = false;
            level.EntitiesSpawned = false;
        };
    }

    public void Run(in SystemContext ctx)
    {
        var levels = _world.Resources.Get<MapLevels>();
        var space = _world.Resources.Get<IPhysicsWorld>();

        for (int i = 0; i < levels.Loaded.Count; i++)
        {
            var level = levels.Loaded[i];
            if (level.CollisionBuilt) continue;
            if (!MapLoader.TryPlace(_world, level)) continue;    // waiting for the ground under it

            MapLoader.EnsureEntities(_world, level);
            MapLightmaps.Ensure(_world, level);   // after its lamps are in the world (issue #313)
            level.CollisionBuilt = true;
            Build(level, space);
        }
    }

    private void Build(MapLevel level, IPhysicsWorld space)
    {
        int built = 0;
        foreach (var brush in level.Brushes)
        {
            var entity = _world.Create(Transform.At(level.Position), $"brush {built}");
            _world.Add(entity, new MapGeometry { Level = level.Record });

            var body = space.AddHull(entity, brush.Hull, level.Position, level.Layer);
            if (body.Handle == 0 && !body.IsStatic) { _world.Destroy(entity); continue; }
            _world.Add(entity, body);
            MapSurfaces.Give(_world, level, body, brush);   // issue #270
            built++;
        }
        Log.Info(LogCat.Level, $"{level.Source}: {built} brush hull(s) into physics");
    }
}

// What a level's hulls are made of (issue #270): each face's texture names a physics_material
// (PhysicsMaterialRecord.Textures, else MapRecord.Surface), and a hit takes the face it is nearest.
internal static class MapSurfaces
{
    public static void Give(World world, MapLevel level, in PhysicsBody body, LevelBrush brush) =>
        Give(world, level, body, brush.Faces.Length, brush, null);

    public static void Give(World world, MapLevel level, in PhysicsBody body, List<LevelBrush> brushes)
    {
        int faces = 0;
        foreach (var brush in brushes) faces += brush.Faces.Length;
        Give(world, level, body, faces, null, brushes);
    }

    private static void Give(World world, MapLevel level, in PhysicsBody body, int count, LevelBrush? one, List<LevelBrush>? many)
    {
        var textures = level.SurfaceTextures ??= world.Engine is { } engine
            ? SurfaceTextures.From(engine.Records, level.Surface)
            : new SurfaceTextures { Fallback = level.Surface };
        if (textures.IsEmpty || count == 0) return;

        var faces = new SurfaceFace[count];
        int n = 0;
        if (one != null) foreach (var face in one.Faces) faces[n++] = new SurfaceFace(face.Normal, textures.Of(face.Texture));
        if (many != null)
            foreach (var brush in many)
                foreach (var face in brush.Faces) faces[n++] = new SurfaceFace(face.Normal, textures.Of(face.Texture));
        world.Resources.Get<IPhysicsWorld>().SetSurfaces(body, faces);
    }
}

// Marks geometry built from a level's brushes — a hull or a mesh — so unloading can find it.
[Component("sage:map_geometry")]
public struct MapGeometry : IComponent
{
    public RecordId Level;
}

// Marks an entity that owns one of a level's solid brush groups, and which one: the client builds its
// meshes as children of it, so they move when it does.
[Component("sage:map_solid")]
public struct MapSolid : IComponent
{
    public RecordId Level;
    public int Index;
}

// Marks an entity a level *spawned* from a `classname`. Separate from `MapGeometry` on purpose: what a
// level spawns is an ordinary entity of any shape, mesh and all, and the two are cleaned up by different
// halves of the engine.
[Component("sage:from_map")]
public struct FromMap : IComponent
{
    public RecordId Level;
}
