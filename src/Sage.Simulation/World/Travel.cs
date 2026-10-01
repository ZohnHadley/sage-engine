#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sage.Simulation;

// Load doors, interiors and fast travel (phase 4g issue 4g-5; the 4g plan's decisions 1, 2 and 7; design
// 14 "As built (doors, interiors and travel)").
//
//   { "type": "prefab", "id": "crypt_door", "parts": { "load_door": { "scene": "crypt", "entry": "crypt_in" } } }
//   { "type": "scene", "id": "crypt", "space": "interior", "maps": ["crypt"], "environment": { "ambient": [0.1, 0.1, 0.12] } }
//   { "type": "prefab", "id": "waystone", "parts": { "travel_point": { "label": "Old Mill", "radius": 20 } } }
//
// **One mechanism** (decision 1): an interior is a scene in the same world, and going through a door is the
// world's scene changing. `Travel.To(world, scene, entry, hours)` runs at a tick boundary, like a save
// (test: TravelRunsAtTheTickBoundary): the scene the player is in goes dormant with its state (4g-1's cells),
// the other is placed, the player is put at its named *entry* — a placement's `name` or a map entity's
// `targetname` — the origin follows, and the clock passes `hours` (4g-2's `Time.Pass`, reason "travel" or
// "door"). Within one scene (a door to another entry, a journey across a streamed exterior) nothing is
// unloaded: the player moves and the ring follows.
//
// **Warp's order** (14 §3): when content says where the entry is (a placement), the origin moves there and
// the ground there is generated *before* the scene is placed and the player is put on it, so nobody arrives
// over a sector that does not exist yet and falls.
//
// **Interiors** (`"space": "interior"` on the scene): the streaming rings stop and the terrain unloads, and
// the world is lit by its lights and `environment.ambient` only — no sun, no sky, no shadow from the sun
// (`Scenes.ApplySpace`). Leaving for an exterior restores the light the world had outside, and the ring
// starts again around the entry.
//
// **Doors** are the `load_door` part (the `sage:load_door` component): the Use action on one travels
// (gameplay's interaction system calls `Travel.Use`), and so does the `Travel` input from entity I/O. A door
// to a scene or an entry that does not exist is a load error, which `sage validate` reports.
//
// **Fast travel** is the `travel_point` part: walking within its `radius` discovers it, which the
// component keeps (`discovered`) and the saved `travel` resource lists (`TravelLog`) with where it is.
// `travel <point>` (or `Travel.ToPoint`) goes there and costs the distance over `travel_speed` in game hours.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public static class Travel
{
    // The entity input that travels through a door (or, with "<scene> <entry>", anywhere).
    public const string TravelInput = "Travel";

    // How fast a journey goes when `travel_speed` is not set: metres per game hour, a walk.
    public const float DefaultSpeed = 5000f;

    // Asks for the player to go to `entry` in `scene` at the next tick boundary, with `hours` of game time
    // passing on the way. False (and a warning) for a scene that does not exist, an empty entry, hours that
    // are not a time that can pass, or a journey already asked for this tick.
    public static bool To(World world, RecordId scene, string entry, double hours = 0, string reason = "travel")
    {
        if (!Valid(world, scene, hours)) return false;
        if (string.IsNullOrWhiteSpace(entry))
        {
            Log.Warn(LogCat.World, $"Travel to '{scene}': which entry? (a placement's name or a map entity's targetname)");
            return false;
        }
        return Request(world, new TravelRequest { Scene = scene, Entry = entry.Trim(), Hours = hours, Reason = reason });
    }

    // Fast travel to a discovered travel point, by its label or its entity's name: the journey costs the
    // distance from the player over `travel_speed`, in game hours (HoursTo).
    public static bool ToPoint(World world, string point)
    {
        var destination = TravelLog.Of(world).Find(point);
        if (destination == null)
        {
            Log.Warn(LogCat.World, $"No discovered travel point '{point}' in '{world.Name}'" +
                                   Spelling.Suggest(point, TravelLog.Of(world).Points.Select(p => p.Label)));
            return false;
        }
        double hours = HoursTo(world, destination);
        if (!Valid(world, destination.Scene, hours)) return false;
        return Request(world, new TravelRequest
        {
            Scene = destination.Scene, Entry = destination.Label, At = destination.At, Yaw = destination.Yaw,
            Hours = hours, Reason = "travel",
        });
    }

    // What a journey to `destination` costs: the straight-line distance from the player (absolute, across the
    // ground) over `travel_speed`, in game hours. Zero with no player.
    public static double HoursTo(World world, TravelDestination destination)
    {
        var player = Scenes.Player(world);
        if (player.IsNull || !world.TryGet<Transform>(player, out var transform)) return 0;
        var here = world.Origin().ToAbsolute(transform.LocalPosition);
        float distance = Vector2.Distance(new Vector2(here.X, here.Z), new Vector2(destination.At.X, destination.At.Z));
        float speed = world.Engine?.Scenes.TravelSpeed?.Value ?? DefaultSpeed;
        return speed > 0f ? distance / speed : 0;
    }

    // The Use action on `door` by `user` (gameplay's InteractionSystem): a load door travels when the player
    // uses it. True when that was asked for.
    public static bool Use(World world, Entity door, Entity user)
    {
        if (!world.TryGet<LoadDoor>(door, out var load)) return false;
        if (!user.IsNull && user != Scenes.Player(world))
        {
            Log.Debug(LogCat.World, $"{World.Describe(user)} used {World.Describe(door)}: only the player goes through a load door");
            return false;
        }
        return To(world, load.Scene, load.Entry, load.Hours, "door");
    }

    private static bool Valid(World world, RecordId scene, double hours)
    {
        if (double.IsNaN(hours) || double.IsInfinity(hours) || hours < 0 || hours > Time.MaxHours)
        {
            Log.Warn(LogCat.World, $"Travel: {hours} hours is not a time a journey can take (0 to {Time.MaxHours:0})");
            return false;
        }
        if (scene.IsEmpty || world.Engine is not { } engine || !engine.Records.TryGet(scene, out SceneRecord _))
        {
            Log.Error(LogCat.World, $"Travel: no scene '{scene}'");
            return false;
        }
        return true;
    }

    private static bool Request(World world, TravelRequest request)
    {
        if (world.PendingTravel is { } pending)
        {
            Log.Info(LogCat.World, $"Travel to '{request.Scene}' ({request.Entry}) asked for after travel to '{pending.Scene}' " +
                                   $"({pending.Entry}) in the same tick: the first one goes");
            return false;
        }
        world.PendingTravel = request;
        if (!world.InFixedTick) Run(world);
        return true;
    }

    // The tick boundary (World.RunFixed): the journey asked for.
    internal static void Run(World world)
    {
        var request = world.PendingTravel;
        world.PendingTravel = null;
        if (request == null || world.Engine is not { } engine) return;
        var scenes = engine.Scenes;
        if (!engine.Records.TryGet(request.Scene, out SceneRecord scene))
        {
            Log.Error(LogCat.World, $"Travel: no scene '{request.Scene}'");
            return;
        }

        world.Resources.TryGet<ActiveScene>(out var state);
        bool same = state != null && state.Id == request.Scene;
        bool interior = scene.Space == SceneSpace.Interior;

        // Where to, when content says: a travel point's place, or a placement the scene names.
        SceneEntry? target = request.At is { } at ? new SceneEntry(at, request.Yaw, Ground: false)
                           : scenes.TryEntry(scene, request.Entry, out var entry) ? entry
                           : null;
        if (target == null && scene.Maps.Count == 0 && !(same && !world.FindByName(request.Entry).IsNull))
        {
            Log.Error(LogCat.World, $"Travel: scene '{request.Scene}' has no entry '{request.Entry}' (a placement's name or a map entity's targetname)");
            return;
        }

        // Warp's order (14 §3): the origin first, so the numbers are small, then the ground, then the scene
        // and the player on it.
        if (target is { } known)
        {
            world.Rebase(Terrain.SectorOf(known.At.X, known.At.Z));
            if (!interior) scenes.PrepareGround(world, scene);
        }
        if (!same) scenes.Load(world, request.Scene, movePlayer: false);

        var player = Scenes.Player(world);
        if (player.IsNull) Log.Warn(LogCat.World, $"Travel to '{request.Scene}': there is no player to put at '{request.Entry}'");
        else
        {
            Vector3 local;
            float yaw;
            if (target is { } where)
            {
                local = world.Origin().ToOrigin(where.At with { Y = 0f });
                float ground = Ground(world, local);
                // A travel point lands on the ground; an entry stands where content put it.
                local.Y = where.Ground ? ground + where.At.Y : request.At != null ? MathF.Max(where.At.Y, ground) : where.At.Y;
                yaw = where.Yaw;
            }
            else if (!scenes.TryEntryInWorld(world, request.Entry, out local, out yaw))
            {
                Log.Error(LogCat.World, $"Travel: scene '{request.Scene}' has no entry '{request.Entry}' (a placement's name or a map entity's targetname); " +
                                        "the player is at the scene's start");
                local = scenes.PlayerStart(world) ?? world.Get<Transform>(player).LocalPosition;
                yaw = scene.Player?.Yaw ?? 0f;
            }

            var transform = world.Get<Transform>(player);
            transform.LocalPosition = local;
            transform.LocalRotation = SageMath.RotationFromYaw(yaw * MathF.PI / 180f);
            world.Teleport(player, transform);

            // An entry the origin is far from (a level placed out of the way): the frame follows the player.
            var sector = world.Origin().SectorOf(local);
            if (sector != world.Origin().Sector) world.Rebase(sector);
        }

        // The ring starts again around where the player is now, on the next tick.
        if (!interior && world.Resources.TryGet<SectorRing>(out var ring) && ring != null)
        {
            ring.Live.Clear();
            ring.Ready = false;
        }
        world.FlushCommands();
        Log.Info(LogCat.World, $"'{world.Name}': travelled to '{request.Scene}' ({request.Entry})" +
                               (request.Hours > 0 ? $", {request.Hours:0.##} h on the way" : ""));
        if (request.Hours > 0) Time.Pass(world, request.Hours, request.Reason);
    }

    private static float Ground(World world, Vector3 local) =>
        world.Resources.TryGet<Terrain>(out var terrain) && terrain is { Generator: not null } ? terrain.HeightAt(local.X, local.Z) : 0f;

    // ---- the input ----------------------------------------------------------------------------------------

    internal static void Register(Engine engine)
    {
        engine.Inputs.Register(TravelInput, static (World world, in IOContext io) =>
        {
            if (!io.Activator.IsNull && io.Activator != Scenes.Player(world)) return;   // only the player goes
            if (io.Parameter.Trim().Length > 0)
            {
                // "<scene> <entry>": anywhere, from any entity.
                var parts = io.Parameter.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var scene = world.Engine!.Records.Resolve("scene", parts[0]);
                if (scene.IsEmpty) return;   // Resolve said there is no such scene
                To(world, scene, parts.Length > 1 ? parts[1] : "", 0, "door");
                return;
            }
            if (world.TryGet<LoadDoor>(io.Self, out var door)) To(world, door.Scene, door.Entry, door.Hours, "door");
            else Log.Warn(LogCat.Events, $"I/O: {TravelInput} at {World.Describe(io.Self)}, which is no load door (sage:load_door) " +
                                         "and was given no \"<scene> <entry>\"");
        });
    }
}

internal sealed class TravelRequest
{
    public RecordId Scene;
    public string Entry = "";
    public Vector3? At;   // a travel point's absolute place; null: find the entry
    public float Yaw;
    public double Hours;
    public string Reason = "travel";
}

// A named entry of a scene where content says it is: absolute metres, or (Ground) across the ground with Y
// above it. Yaw in degrees.
internal readonly record struct SceneEntry(Vector3 At, float Yaw, bool Ground);

// ---- doors ----------------------------------------------------------------------------------------------

// A load door: using it (or sending it `Travel`) takes the player to `entry` in `scene`. Content, not state:
// a save does not keep it, the prefab and its placement say it again.
[Transient]
[Component("sage:load_door")]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public struct LoadDoor : IComponent
{
    [RecordRef("scene"), Property(Tooltip = "The scene it leads to; the scene the world is in already is fine (a door to another entry)")]
    public RecordId Scene;
    [Property(Tooltip = "Where in that scene: a placement's name or a map entity's targetname")]
    public string Entry;
    [Property(Min = 0, Unit = "h", Tooltip = "Game hours going through takes (0: none)")]
    public float Hours;
}

// "load_door": { "scene": "crypt", "entry": "crypt_in", "hours": 0 }
[PrefabPart("load_door", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class LoadDoorPart : IPrefabPart
{
    [Property(Tooltip = "The scene it leads to")]
    public RecordRef<SceneRecord> Scene;
    [Property(Tooltip = "Where in that scene: a placement's name or a map entity's targetname")]
    public string Entry = "";
    [Property(Min = 0, Unit = "h", Tooltip = "Game hours going through takes (0: none)")]
    public float Hours;

    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new LoadDoor { Scene = Scene.Id, Entry = Entry.Trim(), Hours = MathF.Max(0f, Hours) });
}

// ---- fast travel ----------------------------------------------------------------------------------------

// A place fast travel can go once the player has been near it.
[Component("sage:travel_point")]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public struct TravelPoint : IComponent
{
    [Property(Tooltip = "What a map or `travel` calls it; empty: the entity's name")]
    public string Label;
    [Property(Min = 0, Unit = "m", Tooltip = "How near the player must come to discover it")]
    public float Radius;
    [Property(Tooltip = "The player has been near it, and can travel to it")]
    public bool Discovered;
}

// "travel_point": { "label": "Old Mill", "radius": 20 }
[PrefabPart("travel_point", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class TravelPointPart : IPrefabPart
{
    [Property(Tooltip = "What a map or `travel` calls it; empty: the entity's name")]
    public string Label = "";
    [Property(Min = 0, Unit = "m", Tooltip = "How near the player must come to discover it")]
    public float Radius = 20f;
    [Property(Tooltip = "Known from the start, without going there")]
    public bool Discovered;

    public void Apply(in PrefabPartContext ctx) =>
        ctx.World.Add(ctx.Entity, new TravelPoint { Label = Label.Trim(), Radius = MathF.Max(0f, Radius), Discovered = Discovered });
}

// One discovered travel point: where fast travel goes.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class TravelDestination
{
    public string Label = "";
    public string Name = "";        // the entity's name
    public RecordId Scene;
    public Vector3 At;              // absolute metres
    public float Yaw;               // degrees
}

// The travel points the player has discovered, in the order found: what `travel` and a map's markers list.
// Saved (`travel`), so they are known wherever their cells are, live or dormant.
[SavedResource("travel", Plugin = RegistrationOwners.Core)]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4g: open world
public sealed class TravelLog
{
    private List<TravelDestination> _points = new();

    public List<TravelDestination> Points
    {
        get => _points;
        set => _points = value ?? new();
    }

    // A discovered point by label, else by its entity's name (ignoring case); null when there is none.
    public TravelDestination? Find(string point)
    {
        point = point.Trim();
        foreach (var p in _points) if (string.Equals(p.Label, point, StringComparison.OrdinalIgnoreCase)) return p;
        foreach (var p in _points) if (string.Equals(p.Name, point, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    // The world's (every world has one: Engine.CreateWorld; a load replaces it).
    public static TravelLog Of(World world) => world.Resources.GetOrAdd(static () => new TravelLog());

    internal void Discover(TravelDestination point)
    {
        foreach (var p in _points)
            if (p.Scene == point.Scene && string.Equals(p.Label, point.Label, StringComparison.Ordinal)) return;
        _points.Add(point);
    }
}

// Late: a travel point the player has come within `radius` of is discovered. Allocates nothing until one is.
[System("sage.world.travel_points", Phase.Late)]
internal sealed class TravelPointSystem : ISystem
{
    private readonly World _world;
    private readonly Query<Transform, TravelPoint> _points;
    private readonly Query<Transform> _players;

    public TravelPointSystem(World world)
    {
        _world = world;
        _points = world.Query<Transform, TravelPoint>();
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        foreach (var (transforms, points, entities) in _points.Chunks)
        {
            var t = transforms.Span;
            var p = points.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (p[n].Discovered || !Near(t[n].LocalPosition, p[n].Radius)) continue;
                p[n].Discovered = true;
                Discover(entities.EntityAt(n), in t[n], in p[n]);
            }
        }
    }

    private bool Near(Vector3 at, float radius)
    {
        float squared = radius * radius;
        foreach (var (transforms, _) in _players.Chunks)
        {
            var span = transforms.Span;
            for (int i = 0; i < span.Length; i++)
            {
                float dx = span[i].LocalPosition.X - at.X, dz = span[i].LocalPosition.Z - at.Z;
                if (dx * dx + dz * dz <= squared) return true;
            }
        }
        return false;
    }

    private void Discover(Entity entity, in Transform transform, in TravelPoint point)
    {
        var scene = _world.Resources.TryGet<ActiveScene>(out var state) && state != null ? state.Id : default;
        string name = entity.Name ?? "";
        string label = point.Label is { Length: > 0 } given ? given : name;
        TravelLog.Of(_world).Discover(new TravelDestination
        {
            Label = label,
            Name = name,
            Scene = scene,
            At = _world.Origin().ToAbsolute(transform.LocalPosition),
            Yaw = SageMath.YawOf(transform.LocalRotation) * 180f / MathF.PI,
        });
        Log.Info(LogCat.World, $"'{_world.Name}': discovered '{label}'");
    }
}

// ---- interiors: the light ----------------------------------------------------------------------------

internal static class Interiors
{
    // Whether the world's scene is an interior (the client's weather does not fall in one).
    public static bool Active(World world) =>
        world.Resources.TryGet<ActiveScene>(out var state) && state is { Interior: true };

    // What the outside light was, to put back when the player leaves.
    public static RenderEnvironment Copy(RenderEnvironment from) => new()
    {
        SunColor = from.SunColor, AmbientSky = from.AmbientSky, AmbientGround = from.AmbientGround,
        ShadowStrength = from.ShadowStrength, DrawSky = from.DrawSky, Stars = from.Stars,
        ClearColor = from.ClearColor, Zenith = from.Zenith,
    };

    public static void Restore(RenderEnvironment saved, RenderEnvironment to)
    {
        to.SunColor = saved.SunColor;
        to.AmbientSky = saved.AmbientSky;
        to.AmbientGround = saved.AmbientGround;
        to.ShadowStrength = saved.ShadowStrength;
        to.DrawSky = saved.DrawSky;
        to.Stars = saved.Stars;
        to.ClearColor = saved.ClearColor;
        to.Zenith = saved.Zenith;
    }

    // Lights only: no sun, no sky, no stars; the scene's ambient from above and below.
    public static void Light(RenderEnvironment environment, Vector3 ambient)
    {
        environment.SunColor = Vector3.Zero;
        environment.ShadowStrength = 0f;
        environment.DrawSky = false;
        environment.Stars = 0f;
        environment.AmbientSky = ambient;
        environment.AmbientGround = ambient;
        environment.ClearColor = Vector3.Zero;
        environment.Zenith = Vector3.Zero;
    }
}

// ---- content checks ------------------------------------------------------------------------------------

// A door to a scene or an entry that does not exist is a load error (and so `sage validate`'s). A scene
// that does not exist is the reference check's (`scene` is a RecordRef); the entry is checked here,
// against the names the scene's placements give and the targetnames in its maps.
internal static class TravelChecks
{
    public const string Part = "load_door";

    // A prefab's own door, when it says both where and which entry (a door prefab whose placements finish
    // it is checked at each placement).
    public static void CheckPrefab(Engine engine, PrefabRecord prefab, RecordCheck check)
    {
        if (Options(prefab.Parts) is not { } options || Read(options, check) is not { } door) return;
        if (door.Scene.Id.IsEmpty || door.Entry.Trim().Length == 0) return;
        CheckEntry(engine, door, JsonMembers.Child("Parts", Part), check);
    }

    // A placement of a door: the prefab's options with the placement's overrides over them.
    public static void CheckPlacement(Engine engine, Placement placement, string path, RecordCheck check)
    {
        check.TryGet<PrefabRecord>(placement.Prefab.Id, out var prefab);
        var own = Options(prefab?.Parts);
        var overrides = Options(placement.Overrides?.Parts);
        if (own == null && overrides == null) return;

        var merged = new JsonObject();
        foreach (var source in new[] { own, overrides })
            if (source != null)
                foreach (var (key, value) in source) merged[key] = value?.DeepClone();
        if (Read(merged, check) is not { } door) return;

        if (door.Scene.Id.IsEmpty) check.Error(path, "a load door needs a \"scene\" (its prefab's load_door, or the placement's overrides)");
        else if (door.Entry.Trim().Length == 0) check.Error(path, "a load door needs an \"entry\": a placement's name or a map entity's targetname in its scene");
        else CheckEntry(engine, door, path, check);
    }

    private static JsonObject? Options(JsonObject? parts)
    {
        if (parts == null) return null;
        foreach (var (key, value) in parts)
            if (string.Equals(key, Part, StringComparison.OrdinalIgnoreCase)) return value as JsonObject;
        return null;
    }

    private static LoadDoorPart? Read(JsonObject options, RecordCheck check)
    {
        try { return options.Deserialize<LoadDoorPart>(check.Json); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }   // the body check says so
    }

    private static void CheckEntry(Engine engine, LoadDoorPart door, string path, RecordCheck check)
    {
        if (!check.TryGet<SceneRecord>(door.Scene.Id, out var scene)) return;   // the reference check says so
        var names = EntryNames(engine, scene, check, out bool complete);
        string entry = door.Entry.Trim();
        if (names.Contains(entry) || !complete) return;
        check.Error(path + ".Entry", $"scene {door.Scene.Id} has no entry '{entry}' (a placement's name or a map entity's targetname)" +
                                     Spelling.Suggest(entry, names));
    }

    // Every name an entry could be in `scene`. `complete`: false when a map could not be read, so a name
    // missing here may be in it.
    private static HashSet<string> EntryNames(Engine engine, SceneRecord scene, RecordCheck check, out bool complete)
    {
        complete = true;
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Add(Placement? placement)
        {
            if (placement != null && !string.IsNullOrEmpty(placement.Name)) names.Add(placement.Name);
        }
        Add(scene.Player);
        foreach (var placement in scene.Place) Add(placement);
        foreach (var document in scene.Placements)
            if (check.TryGet<PlacementsRecord>(document.Id, out var record))
                foreach (var placement in record.Place) Add(placement);
        foreach (var map in scene.Maps)
            if (!check.TryGet<MapRecord>(map.Id, out var record) || !MapLoader.TryTargetNames(engine, record, names)) complete = false;
        return names;
    }
}
