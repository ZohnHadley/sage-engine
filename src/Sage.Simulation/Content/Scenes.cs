#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Simulation;

// Scenes: what a world starts with (docs/design/05 §3.5, 15 §3, issue #29).
//
// **Placing things in a world is the engine's business**, so the `scene` record, the spawn loop and
// respawn-on-reload live here rather than in each game (they were the Sandbox's own C# until #29, and
// every game would have written them again). A game names its first scene in game.json (`"scene"`), and
// a game with no C# at all boots into it: the maps it loads, the placements documents it opens, what it
// places itself, where the player starts and the weather it starts in.
//
// Why `Sage.Simulation`: a scene is records, prefabs, placements, maps and the world's origin, which all
// live here, and nothing in it needs physics or gameplay. Like `prefab` and `placements` it belongs to
// the engine (RegistrationOwners.Core) rather than to a plugin, so a game with `"plugins": []` has scenes too.
//
// Not here yet: the game's rules and conventions. Phase 3's `gameplay_conventions` record (#26) is the
// place for those; a scene will name one when it exists (a `conventions` field), not before.
[Record("scene", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public sealed class SceneRecord
{
    // Brush levels to load with the scene (15 §3, F16). A `.map` places its own contents, so this is the
    // whole of what the scene has to say about one.
    [Property(Tooltip = "Brush levels to load with the scene")]
    public List<RecordRef<MapRecord>> Maps = new();

    [Property(Tooltip = "Placements documents (what the editor saves) to load with the scene")]
    public List<RecordRef<PlacementsRecord>> Placements = new();

    [Property(Unit = "m", Tooltip = "Absolute metres that `Origin` and `Ground` placements are measured from")]
    public Vector3 Origin;
    [Property(Tooltip = "What a placement's `at` is measured from when it does not say")]
    public PlacementFrame RelativeTo = PlacementFrame.World;

    [Property(Tooltip = "Where the local player starts, and as what; kept across hot reloads")]
    public Placement? Player;
    [Property(Tooltip = "Everything else the scene places")]
    public List<Placement> Place = new();

    [Property(Tooltip = "What the world is like when the scene starts")]
    public SceneEnvironment Environment = new();

    // Placed sector by sector as the player comes near, and put to sleep with its state as they leave
    // (4g-3, StreamedScene): for an exterior bigger than one sector. Needs sage.streaming; without it the
    // scene is placed whole.
    [Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
    [Property(Tooltip = "Place the scene sector by sector around the player, and put sectors the player leaves to sleep with their state")]
    public bool Streamed;

    // The built-in ground the scene stands on (4g-3); left out, whatever the game's terrain is.
    [Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
    [Property(Tooltip = "A terrain record: the ground the world gets when this scene is placed; left out, the game's own")]
    public RecordRef<TerrainRecord> Terrain;

    // An exterior has the terrain, the streaming rings, the sun and the sky; an interior (a crypt, a house
    // behind a load door) has none of them and is lit by its lights alone (4g-5, Travel.cs).
    [Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
    [Property(Tooltip = "Exterior (terrain, streaming, sun and sky) or Interior (none of them: lit by its lights and environment.ambient)")]
    public SceneSpace Space = SceneSpace.Exterior;
}

// Where a scene is (4g-5): outside, under the sky, or inside, out of it.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public enum SceneSpace
{
    Exterior,
    Interior,
}

// A scene's defaults for the world around it. Time of day joins the weather when there is a clock.
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public sealed class SceneEnvironment
{
    [Property(Tooltip = "The weather the scene starts in; left out, the world's own (clear)")]
    public RecordRef<WeatherRecord> Weather;

    [Property(Tooltip = "The sky the scene's world is lit by (issue 4h-2); left out, the world's own (none: the light the game set)")]
    public RecordRef<SkyRecord> Sky;
    [Property(Min = 0, Max = 24, Unit = "h", Tooltip = "The hour the scene starts at, when the world starts there or scene_load places it (travel keeps the clock); left out, the clock's")]
    public float? Hour;

    // An interior's light besides its lights (4g-5): there is no sky to give any.
    [Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
    [Property(Tooltip = "Interior scenes only: the ambient light, from above and below alike; the sun and sky give none inside")]
    public Vector3 Ambient = new(0.08f, 0.08f, 0.1f);
}

// Placed by a scene, so hot reload and `scene_load` know what to sweep away and put back. Never on the
// player: a reload that took the player with it left the world with nothing to control (review #59).
// [Transient]: a save restores these by their persistent ids, and `Scenes.AfterLoad` tags them again.
[Transient]
[Tag("sage:from_scene")]
public struct FromScene : ITag { }

// Which scene a world is in and what that scene put there. A resource every world has.
internal sealed class ActiveScene
{
    public RecordId Id { get; internal set; }

    // The player the scene's rules spawned, kept across reloads; null when there is none.
    public Entity Player { get; internal set; }

    internal readonly List<MapLevel> Levels = new();
    internal readonly List<RecordId> Documents = new();

    // A streamed scene's sectors (4g-3); null for a scene placed whole.
    internal StreamedScene? Streamed;

    // The scene is an interior (4g-5), and the light the world had outside, put back when it leaves.
    internal bool Interior;
    internal RenderEnvironment? Outside;
}

// The engine's scene service (Engine.Scenes).
[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public sealed class Scenes
{
    private readonly Engine _engine;

    public Scenes(Engine engine)
    {
        _engine = engine;
        // Hot reload: edit a scene (or a prefab it places) while the game runs and it is placed again.
        engine.Records.Reloaded += Respawn;
        engine.Records.AddCheck<SceneRecord>(Check);
        engine.Records.AddCheck<PlacementsRecord>((record, check) => CheckPlacements(record.Place, "Place", check));
        engine.Records.AddCheck<PrefabRecord>((record, check) => TravelChecks.CheckPrefab(engine, record, check));   // 4g-5
    }

    // The scene every new world starts in: game.json's `"scene"`, set by SageApp once content has loaded.
    // Empty: worlds start empty, and the game's rules place what they want.
    public RecordId Start { get; set; }

    // Called by Engine.CreateWorld once every module has furnished the world, before its rules start: the
    // rules find the scene placed, and their SpawnPlayer finds its start.
    internal void Enter(World world)
    {
        world.Resources.Add(new ActiveScene());
        if (Start.IsEmpty) return;
        if (!_engine.Records.TryGet(Start, out SceneRecord scene))
        {
            Log.Error(LogCat.World, $"No scene '{Start}': world '{world.Name}' starts empty");
            return;
        }
        Place(world, Start, scene);
        ApplyEnvironment(world, scene);
    }

    // `scene_load`: the world's scene is swept away and another placed. The player stays, and is moved
    // to the new scene's start; a world with no player yet gets one from its rules.
    public bool Load(World world, RecordId id) => Load(world, id, movePlayer: true);

    // The same, leaving the player where it is when `movePlayer` is false: travel puts it at an entry.
    internal bool Load(World world, RecordId id, bool movePlayer)
    {
        if (!_engine.Records.TryGet(id, out SceneRecord scene))
        {
            Log.Error(LogCat.World, $"No scene '{id}'");
            return false;
        }
        Clear(world);
        Place(world, id, scene);
        // A journey (Travel, movePlayer false) does not set the clock: the hour a scene starts at is for
        // starting there, and coming back through a door at 06:00 must not make it dusk again (4g-7).
        ApplyEnvironment(world, scene, setHour: movePlayer);

        var state = world.Resources.Get<ActiveScene>();
        var player = Player(world);
        if (player.IsNull)
        {
            if (world.Resources.TryGet<GameRules>(out var rules) && rules != null) rules.SpawnPlayer(world);
        }
        else if (movePlayer && PlayerStart(world) is { } start)
        {
            var where = Transform.At(start);
            where.LocalRotation = SageMath.RotationFromYaw(scene.Player!.Yaw * MathF.PI / 180f);
            world.Teleport(player, where);
        }
        Log.Info(LogCat.World, $"'{world.Name}' is in scene '{state.Id}'");
        _engine.Saves.SceneChanged();   // an autosave at the end of the next tick (4i-6)
        return true;
    }

    // Everything the world's scene placed, gone: its own placements, its documents' and its levels. The
    // player is not the scene's to take. Returns how many entities went.
    //
    // **The scene goes dormant** (4g-1): the state of everything it placed, and the runtime spawns that
    // belong to it (a dropped sword, an arrow in flight: InCell), are kept, and placing the scene again
    // lays that state back and spawns those again. The runtime spawns leave the world with it rather than
    // turning up in the next scene at the same coordinates.
    public int Clear(World world)
    {
        if (!world.Resources.TryGet<ActiveScene>(out var state) || state == null) return 0;

        // Before anything goes (4i-3): what the game destroyed is noted, so that placing this scene again
        // (a hot reload, a `scene_load` back to it) leaves it destroyed — and, unless a reload or a load is
        // clearing it (ContentIds.Discarding), its state and its runtime spawns go dormant (4g-1).
        if (!state.Id.IsEmpty) ContentIds.Forget(world, ContentIds.SceneSource(state.Id));
        // A streamed scene's sectors the same way, each its own cell (4g-3).
        if (state.Streamed is { } streamed)
        {
            streamed.Clear(world);
            state.Streamed = null;
        }

        int removed = 0;
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<FromScene>()).Entities.ToEntityList())
        {
            if (entity == state.Player) continue;   // never tagged, but a save could have put it back tagged
            world.Destroy(entity);
            removed++;
        }
        foreach (var document in state.Documents) removed += world.ClearPlacements(document);

        // Levels too, or a reload builds the hut a second time inside the first one. Only the scene's:
        // one a person loaded with `map_load` is theirs.
        if (state.Levels.Count > 0 && world.Resources.TryGet<MapLevels>(out var levels) && levels != null)
            foreach (var level in state.Levels) levels.Remove(level);

        state.Levels.Clear();
        state.Documents.Clear();
        state.Id = default;
        return removed;
    }

    // Records reloaded: every world's scene placed again, from the new records, without duplicating
    // anything and without losing the player (05 §3.6, issue #29).
    //
    // **The same reconcile a load does** (4i-3): the scene is cleared and placed again from the new
    // records, and what the game destroyed stays destroyed (Clear notes it, Place removes it again). What
    // a reload does *not* do is lay the live state back over the new content: the point of a reload is
    // to see the edit, and laying the old health over a prefab whose health was just changed would hide it.
    private void Respawn()
    {
        foreach (var world in _engine.Worlds)
        {
            if (!world.Resources.TryGet<ActiveScene>(out var state) || state == null || state.Id.IsEmpty) continue;
            var id = state.Id;
            if (!Replace(world, id))
            {
                Log.Warn(LogCat.World, $"Scene '{id}' is gone after the reload: '{world.Name}' is left empty");
                continue;
            }
            var scene = Scene(world)!;

            // A player the rules spawned stays where it is, with what it has. One that is gone — or never
            // was, because the scene only now has a start — comes back.
            if (Player(world).IsNull && scene.Player != null
                && world.Resources.TryGet<GameRules>(out var rules) && rules != null)
                rules.SpawnPlayer(world);
        }
    }

    // The reconcile path a load and a hot reload share (4i-3): what the world's scene placed is cleared, and
    // `id` placed again from the content as it is now. `cleared` runs in between, when nothing the scene
    // placed is left: a load puts the save's tombstones in place there. Levels a person loaded with
    // `map_load` are not the scene's; with `levels` their entities are placed again as well. Returns false
    // when there is no such scene (the world is then left with none).
    //
    // Nothing goes dormant here (4g-1): a reload is for seeing the edit, so the live state is not laid back
    // over it and the runtime spawns stay where they are; a load replaces both with the save's.
    internal bool Replace(World world, RecordId id, Action? cleared = null, bool levels = false)
    {
        using (ContentIds.Discarding(world))
        {
            Clear(world);
            if (levels && world.Resources.TryGet<MapLevels>(out var loaded) && loaded != null)
                foreach (var level in loaded.Loaded) MapLoader.ForgetEntities(world, level);
        }
        cleared?.Invoke();

        if (id.IsEmpty || !_engine.Records.TryGet(id, out SceneRecord scene)) return false;
        Place(world, id, scene);
        return true;
    }

    private void Place(World world, RecordId id, SceneRecord scene)
    {
        var state = world.Resources.Get<ActiveScene>();
        state.Id = id;
        string source = ContentIds.SceneSource(id);
        ApplyTerrain(world, scene);
        ApplySpace(world, scene);

        // Streamed (4g-3): nothing is placed now but the levels that do not stand on the terrain. The
        // sectors are placed at tick boundaries as the ring reaches them (TickEnded). An interior has no
        // ring, and is placed whole.
        if (scene.Streamed && scene.Space == SceneSpace.Interior)
            Log.Once(LogCat.World, LogLevel.Warn, $"interior-streamed:{id}", $"Scene '{id}' is an interior and streamed: an interior has no ring, so it is placed whole");
        else if (scene.Streamed && world.Resources.TryGet<SectorRing>(out _))
        {
            state.Streamed = StreamedScene.Build(_engine, id, scene);
            foreach (var map in scene.Maps)
                if (StreamedScene.PlacedWhole(_engine, map.Id) && MapLoader.Load(world, map.Id) is { } whole) state.Levels.Add(whole);
            Log.Info(LogCat.World, $"Scene '{id}' streams by sector in '{world.Name}' ({state.Levels.Count} level(s) loaded whole)");
            return;
        }
        else if (scene.Streamed)
            Log.Once(LogCat.World, LogLevel.Warn, $"unstreamed:{id}", $"Scene '{id}' is streamed, but sage.streaming is not loaded: it is placed whole");

        int placed = 0;
        for (int i = 0; i < scene.Place.Count; i++)
        {
            var placement = scene.Place[i];
            var entity = world.SpawnWithoutId(placement.Prefab.Id, world.PlacementPosition(placement, scene.Origin, scene.RelativeTo), placement.Yaw,
                                     placement.Overrides, $"scene {id} place[{i}]");
            if (entity.IsNull) continue;   // `Spawn` said why; one bad line costs that line
            if (!string.IsNullOrEmpty(placement.Name)) entity.Name = placement.Name;
            PlacementWires.Attach(world, entity, placement);
            entity.AddTag<FromScene>();

            // A stable identity, so a save can find this *same* thing next run (09 §3.5, F27, 4i-3): the
            // placement's authored `id`, else derived from where it is in `place` — which moves when the
            // list is reordered, and is why an authored id wins.
            ContentIds.Place(world, source, entity, ContentIds.ScenePlacement(id, i, placement));
            placed++;
        }
        ContentIds.Finish(world, source);

        foreach (var document in scene.Placements)
        {
            placed += world.SpawnPlacements(document.Id);
            state.Documents.Add(document.Id);
        }

        // Levels after placements: a `.map` spawns its own entities, and they should land in a world
        // that already has everything the scene put in it.
        foreach (var map in scene.Maps)
            if (MapLoader.Load(world, map.Id) is { } level) state.Levels.Add(level);

        Log.Info(LogCat.World, $"Scene '{id}': {placed} placed and {state.Levels.Count} level(s) loaded in '{world.Name}'");
    }

    // A scene's `terrain` (4g-3): the world's ground becomes that built-in generator. Ground generated from
    // another is dropped, and the ring generates it again. A scene that names none leaves the game's own.
    internal void ApplyTerrain(World world, SceneRecord scene)
    {
        if (scene.Terrain.Id.IsEmpty || !world.Resources.TryGet<Terrain>(out var terrain) || terrain == null) return;
        if (!_engine.Records.TryGet(scene.Terrain.Id, out TerrainRecord record)) return;   // the load check says so
        if (BuiltInTerrain.Matches(terrain.Generator, terrain.Seed, record)) return;
        for (int i = terrain.Sectors.Count - 1; i >= 0; i--) terrain.Unload(terrain.Sectors[i].Coord);
        terrain.Generator = BuiltInTerrain.Create(record);
        terrain.Seed = record.Seed;
        Log.Info(LogCat.Streaming, $"'{world.Name}': ground from terrain '{scene.Terrain.Id}' ({record.Generator}, seed {record.Seed})");
    }

    // A scene's `space` (4g-5). An interior stops the streaming rings and unloads the terrain, and the
    // world is lit by its lights and the scene's `ambient` only; the light it had outside is kept and put
    // back when an exterior is placed again, and the ring starts again around the player on the next tick.
    private static void ApplySpace(World world, SceneRecord scene)
    {
        var state = world.Resources.Get<ActiveScene>();
        bool interior = scene.Space == SceneSpace.Interior;
        if (world.Resources.TryGet<SectorRing>(out var ring) && ring != null)
        {
            ring.Suspended = interior;
            if (interior)
            {
                ring.Live.Clear();
                ring.Ready = false;
            }
        }
        if (interior && world.Resources.TryGet<Terrain>(out var terrain) && terrain != null)
            for (int i = terrain.Sectors.Count - 1; i >= 0; i--) terrain.Unload(terrain.Sectors[i].Coord);

        world.Resources.TryGet<RenderEnvironment>(out var environment);
        if (interior)
        {
            if (!state.Interior && environment != null) state.Outside = Interiors.Copy(environment);
            if (environment != null) Interiors.Light(environment, scene.Environment.Ambient);
        }
        else if (state.Interior)
        {
            if (environment != null && state.Outside != null) Interiors.Restore(state.Outside, environment);
            state.Outside = null;
        }
        state.Interior = interior;
    }

    // Travel's half of warp's order (4g-5): the ground `scene` stands on, generated around the origin's
    // sector before anything is placed on it. Nothing for a world without a generator.
    internal void PrepareGround(World world, SceneRecord scene)
    {
        ApplyTerrain(world, scene);
        if (!world.Resources.TryGet<Terrain>(out var terrain) || terrain?.Generator == null) return;
        int radius = world.Resources.TryGet<SectorRing>(out var ring) && ring != null ? Math.Clamp(ring.Radius, 0, 8) : 1;
        var centre = world.Origin().Sector;
        for (int z = -radius; z <= radius; z++)
            for (int x = -radius; x <= radius; x++)
                terrain.Load(new SectorCoord(centre.X + x, centre.Z + z));
    }

    // A named entry of a scene where its content says it is (4g-5): a placement of the scene's (its player
    // start among them) or of its placements documents, by `name`. False when none is named so; a map's
    // entity is only known once the map is placed (TryEntryInWorld).
    internal bool TryEntry(SceneRecord scene, string entry, out SceneEntry found)
    {
        found = default;
        if (string.IsNullOrEmpty(entry)) return false;
        foreach (var placement in scene.Place)
            if (placement.Name == entry) { found = Entry(placement, scene.Origin, scene.RelativeTo); return true; }
        if (scene.Player is { } start && start.Name == entry) { found = Entry(start, scene.Origin, scene.RelativeTo); return true; }
        foreach (var document in scene.Placements)
        {
            if (!_engine.Records.TryGet(document.Id, out PlacementsRecord record)) continue;
            foreach (var placement in record.Place)
                if (placement.Name == entry) { found = Entry(placement, record.Origin, record.RelativeTo); return true; }
        }
        return false;
    }

    private static SceneEntry Entry(Placement placement, Vector3 origin, PlacementFrame frame) =>
        (placement.RelativeTo ?? frame) switch
        {
            PlacementFrame.Origin => new SceneEntry(origin + placement.At, placement.Yaw, Ground: false),
            PlacementFrame.Ground => new SceneEntry(origin + placement.At, placement.Yaw, Ground: true),
            _ => new SceneEntry(placement.At, placement.Yaw, Ground: false),
        };

    // A named entry in what the world has placed (4g-5): a map entity's `targetname` in the scene's levels
    // (a point with no prefab included), else any entity of that name. In the simulation's frame.
    internal bool TryEntryInWorld(World world, string entry, out Vector3 at, out float yaw)
    {
        at = default;
        yaw = 0f;
        if (world.Resources.TryGet<ActiveScene>(out var state) && state != null)
            foreach (var level in state.Levels)
                if (level.TryFindNamed(entry, out at, out yaw)) return true;
        var entity = world.FindByName(entry);
        if (entity.IsNull || !world.TryGet<Transform>(entity, out var transform)) return false;
        at = transform.LocalPosition;
        yaw = SageMath.YawOf(transform.LocalRotation) * 180f / MathF.PI;
        return true;
    }

    // ---- The tick boundary (4g-3) ---------------------------------------------------------------------

    // Called by World.RunFixed once every phase of a tick is done: a streamed scene puts to sleep the sectors
    // that left the ring and places the ones that came into it. Nothing to do costs a lookup or two.
    internal void TickEnded(World world)
    {
        if (!world.Resources.TryGet<ActiveScene>(out var state) || state?.Streamed is not { } streamed) return;
        if (!world.Resources.TryGet<SectorRing>(out var ring) || ring == null) return;
        streamed.Step(world, ring);
    }

    // What a scene's content would be if it is streamed, for a load to re-key an older save's `scene:`
    // tombstones and sources by (SaveSystem.Rekey); null for a scene placed whole.
    internal StreamedScene? StreamedFor(World world, RecordId id) =>
        !id.IsEmpty && _engine.Records.TryGet(id, out SceneRecord scene) && scene.Streamed && scene.Space == SceneSpace.Exterior
        && world.Resources.TryGet<SectorRing>(out _)
            ? StreamedScene.Build(_engine, id, scene)
            : null;

    private void ApplyEnvironment(World world, SceneRecord scene, bool setHour = true)
    {
        if (world.Resources.TryGet<WorldClock>(out var clock) && clock != null)
        {
            if (!scene.Environment.Sky.Id.IsEmpty) clock.Sky = scene.Environment.Sky.Id;
            if (setHour && scene.Environment.Hour is { } hour) clock.Hour = hour;
        }
        if (scene.Environment.Weather.Id.IsEmpty || !world.Resources.TryGet<Weather>(out var weather) || weather == null) return;
        // Settled at once: a scene starts in its weather rather than watching it roll in.
        weather.Current = weather.Target = scene.Environment.Weather.Id;
        weather.Blend = 1f;
    }

    // ---- The player ---------------------------------------------------------------------------------

    // Where the world's scene puts the player, in the simulation's frame; null when it has no start.
    public Vector3? PlayerStart(World world)
    {
        if (Scene(world) is not { Player: { } start } scene) return null;
        return world.PlacementPosition(start, scene.Origin, scene.RelativeTo);
    }

    // What GameRules.SpawnPlayer does unless a game says otherwise: the scene's player placement.
    public Entity SpawnPlayer(World world)
    {
        if (Scene(world) is not { Player: { } start } scene)
        {
            if (world.Resources.TryGet<ActiveScene>(out var active) && active is { Id.IsEmpty: false })
                Log.Warn(LogCat.World, $"Scene '{active.Id}' has no \"player\": there is nothing to control");
            return default;
        }

        var state = world.Resources.Get<ActiveScene>();
        var entity = world.SpawnWithoutId(start.Prefab.Id, world.PlacementPosition(start, scene.Origin, scene.RelativeTo), start.Yaw,
                                 start.Overrides, $"scene {state.Id} player");
        if (entity.IsNull) return entity;
        if (!string.IsNullOrEmpty(start.Name)) entity.Name = start.Name;
        PlacementWires.Attach(world, entity, start);
        world.Add(entity, new Persistent { Id = PlayerId(state.Id) });
        state.Player = entity;
        return entity;
    }

    // The scene the world is in (the one placed last, or travelled to); empty before any is. The RPG kit's map
    // shows the travel points of this one (4g-7).
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
    public static RecordId Current(World world) =>
        world.Resources.TryGet<ActiveScene>(out var state) && state != null ? state.Id : default;

    // The player: the one the scene spawned if it is still alive, else any entity tagged PlayerControlled
    // (a game's own rules may have spawned it their way).
    public static Entity Player(World world)
    {
        if (world.Resources.TryGet<ActiveScene>(out var state) && state != null && world.IsAlive(state.Player))
            return state.Player;
        foreach (var entity in world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>()).Entities)
            return entity;
        return default;
    }

    public SceneRecord? Scene(World world) =>
        world.Resources.TryGet<ActiveScene>(out var state) && state != null && !state.Id.IsEmpty
        && _engine.Records.TryGet(state.Id, out SceneRecord scene) ? scene : null;

    // ---- Saves --------------------------------------------------------------------------------------

    // A save has just been laid over the world (09 §3.5, 4i-3). What the scene placed was placed again by
    // the load itself, tagged and wired; the player is the save's, spawned from it, so it is the scene's
    // player again here, and its wiring (IOConnections is not saved: it is the scene's, like the walls are a
    // map's) is attached again. How often each wire had fired comes back with entity I/O's resource (#90).
    internal void AfterLoad(World world)
    {
        if (!world.Resources.TryGet<ActiveScene>(out var state) || state == null || state.Id.IsEmpty) return;
        if (!_engine.Records.TryGet(state.Id, out SceneRecord scene)) return;

        var player = world.Resolve(PlayerId(state.Id));
        if (!player.IsNull)
        {
            state.Player = player;
            if (scene.Player != null && !player.HasComponent<IOConnections>()) PlacementWires.Attach(world, player, scene.Player);
        }
    }

    private static PersistentId PlayerId(RecordId scene) => PersistentId.FromName($"scene:{scene}:player");

    // ---- Content checks -----------------------------------------------------------------------------

    private void Check(SceneRecord scene, RecordCheck check)
    {
        if (scene.Player != null) CheckPlacements(new[] { scene.Player }, "Player", check, single: true);
        CheckPlacements(scene.Place, "Place", check);
    }

    // A placement that names no prefab would place nothing and say so only at run time; a wire to an
    // input nobody registered would do nothing, and says so here (issue #80).
    private void CheckPlacements(IReadOnlyList<Placement> placements, string field, RecordCheck check, bool single = false)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < placements.Count; i++)
        {
            string path = single ? field : $"{field}[{i}]";
            // An authored id is the placement's identity in saves (4i-3): two of one id would be one entity.
            if (!string.IsNullOrWhiteSpace(placements[i].Id) && !ids.Add(placements[i].Id.Trim()))
                check.Error(path + ".Id", $"another placement already has the id '{placements[i].Id}'");
            if (placements[i].Prefab.Id.IsEmpty)
                check.Error(path, "a placement needs a \"prefab\"");
            else
                PrefabOverriding.Check(_engine, placements[i].Overrides, placements[i].Prefab.Id.Namespace, path + ".Overrides", check);
            PlacementWires.Check(_engine, placements[i], path, check);
            TravelChecks.CheckPlacement(_engine, placements[i], path, check);   // a load door's scene and entry (4g-5)
        }
    }

    // ---- Console ------------------------------------------------------------------------------------

    // Metres a journey covers in a game hour (`travel_speed`, 4g-5); null until the commands are registered.
    internal CVar<float>? TravelSpeed { get; private set; }

    public void RegisterCommands(CVarRegistry cvars)
    {
        TravelSpeed = cvars.Register("travel_speed", Travel.DefaultSpeed, CVarFlags.Archive,
            "Metres fast travel covers in a game hour: a journey costs its distance over this (4g-5).", 1f, 1_000_000f);
        cvars.RegisterCommand("travel", CVarFlags.None,
            "travel [point]: fast travel to a discovered travel point, by its label; the journey takes distance / travel_speed game hours. With no point, list them.", a =>
        {
            foreach (var world in _engine.Worlds)
            {
                if (a.Count == 0)
                {
                    var points = TravelLog.Of(world).Points;
                    Log.Info(LogCat.Console, $"'{world.Name}': {points.Count} travel point(s) discovered");
                    foreach (var point in points)
                        Log.Info(LogCat.Console, $"  {point.Label}  ({point.Scene}, {point.At.X:F0}, {point.At.Z:F0}; {Travel.HoursTo(world, point):0.#} h)");
                    continue;
                }
                Travel.ToPoint(world, a.Rest);
            }
        });

        cvars.RegisterCommand("scene_load", CVarFlags.Cheat,
            "scene_load [scene]: replace every world's scene with another, keeping the player; with no scene, list them.", a =>
        {
            if (a.Count == 0)
            {
                foreach (var world in _engine.Worlds)
                    Log.Info(LogCat.Console, $"  '{world.Name}': {(world.Resources.TryGet<ActiveScene>(out var s) && s is { Id.IsEmpty: false } ? s.Id.ToString() : "no scene")}");
                Log.Info(LogCat.Console, $"Scenes: {string.Join(", ", _engine.Records.Ids("scene").Select(i => i.ToString()).OrderBy(i => i, StringComparer.Ordinal))}");
                return;
            }
            var id = _engine.Records.Resolve("scene", a[0]);
            if (id.IsEmpty) return;   // Resolve said there is no such scene
            foreach (var world in _engine.Worlds) Load(world, id);
        });
    }
}

[Experimental("SAGE0121", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // scenes and placements (#29): the level editor (#61) will reshape them
public static class SceneWorldExtensions
{
    // Where the world's scene starts the player (the rules' respawn point), or null without one.
    public static Vector3? PlayerStart(this World world) => world.Engine?.Scenes.PlayerStart(world);
}
