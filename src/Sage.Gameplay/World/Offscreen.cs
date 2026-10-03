#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Sage.Gameplay;

// Off-screen simulation, A-Life-lite (issue 4g-6, REDESIGN §5 phase 4g; the 4g plan's decisions 3, 4 and
// 7): an NPC keeps its routine, and a fight still happens, while nobody is there to see it.
//
//   { "type": "prefab", "id": "guard", "parts": { "offscreen": { "speed": 1.4, "strength": 8 }, … } }
//
// **Only what opts in** (the `offscreen` part) is simulated. When the cell it is in goes dormant (a streamed
// sector the player walks away from, a scene the player leaves: 4g-1, 4g-3) it is not put to sleep there:
// the cell hands it over (CellContent, the Simulation seam) and it becomes an **agent** in the world's
// saved `offscreen` resource: its id, prefab, scene, absolute position, faction, health, routine, speed,
// strength, and its save entries. Content that placed it has it as a tombstone from then on (decision 3):
// wherever it goes, it is the table's, and placing its home sector again does not place it twice.
//
// **A step is one game minute**, the same whether the clock runs or jumps:
// - *Fights first.* Agents in the same scene within `FightRange` whose factions are hostile (either side's
//   table says so: `Factions.AreHostile`) fight one round; each agent fights at most once a minute, paired
//   west to east. The round is an `offscreen_fight` vocabulary entry, the agent's `fight` (the one with the
//   lower id's, when two differ); the default, `strength`, compares strength × health, rolls `ShotRandom`
//   on the minute and the two ids, and the winner takes its strength from the loser's health. At zero the
//   loser is dead: `OffscreenDied` is raised, and an agent whose part says `"corpse": false` is gone.
// - *Then the rest move*, in a straight line at their speed, toward the anchor of their routine's entry in
//   force (4g-4) — found in the content by name (a placement's `name`), not in the world, so it does not
//   matter what is loaded. An anchor in another scene is reached through a door: the agent walks to a
//   door of its scene that leads to that scene, and comes out at the door's entry (OffscreenMap).
// - **Ticking**, the system catches up with the clock a step at a time, within `offscreen_budget` agent
//   steps a tick; **a skip** (`TimePassed`, 4g-2) runs every step it covers at once. The result does not
//   depend on which: the same steps run in the same order, and a save keeps the step reached (`Minute`).
//
// **Back in the world**: when the scene and sector an agent is in is live, it is spawned there from its save
// entries, as a runtime spawn of that cell, on the ground, with the health the fights left it (dead with
// the dead tag), and its AI starting its routine afresh. Nothing is spawned twice: an id already in the
// world is not spawned again.
//
// The loaded world is not simulated here: a live NPC is the AI's (Routines.cs).

// What a prefab's `offscreen` part puts on an entity: it is simulated while its cell is dormant.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[Component("sage:offscreen")]
public struct Offscreen : IComponent
{
    [Property(Min = 0, Unit = "m/s", Tooltip = "How fast it walks off-screen; 0 = its movement profile's walking speed")]
    public float Speed;
    [Property(Min = 0, Tooltip = "How hard it fights off-screen: its weight in a round (with its health) and the health it takes when it wins one")]
    public float Strength;
    [Property(Tooltip = "Whether it leaves a corpse when it dies off-screen; false, it is gone")]
    public bool Corpse;
    [VocabularyRef("offscreen_fight"), Property(Tooltip = "The rule its off-screen fights are settled by")]
    public string Fight;
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[PrefabPart("offscreen", Plugin = "sage.gameplay.ai")]
public sealed class OffscreenPart : IPrefabPart
{
    [Property(Min = 0, Unit = "m/s", Tooltip = "How fast it walks off-screen; 0 = its movement profile's walking speed")]
    public float Speed;
    [Property(Min = 0, Tooltip = "How hard it fights off-screen: its weight in a round (with its health) and the health it takes when it wins one")]
    public float Strength = 1f;
    [Property(Tooltip = "Whether it leaves a corpse when it dies off-screen; false, it is gone")]
    public bool Corpse = true;
    [VocabularyRef("offscreen_fight"), Property(Tooltip = "The rule its off-screen fights are settled by: an offscreen_fight entry")]
    public string Fight = OffscreenFights.Default;

    public void Apply(in PrefabPartContext ctx)
    {
        if (Speed < 0 || float.IsNaN(Speed)) { ctx.Error($"speed {Speed} is not a speed"); return; }
        if (Strength < 0 || float.IsNaN(Strength)) { ctx.Error($"strength {Strength} is not a strength"); return; }
        if (ctx.World.Engine is { } engine && engine.Vocabularies.Of<IOffscreenFight>().Find(Fight) == null)
        {
            ctx.Error($"no offscreen_fight '{Fight}'");
            return;
        }
        ctx.World.Add(ctx.Entity, new Offscreen { Speed = Speed, Strength = Strength, Corpse = Corpse, Fight = Fight });
    }
}

// One NPC while its cell is dormant: where it is, what it fights with, and what it is (its save entries).
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class OffscreenAgent
{
    public string Id = "";                  // its persistent id
    public RecordId Prefab;
    public string Name = "";
    public RecordId Scene;                  // the space it is in
    public double X, Y, Z;                  // absolute metres
    public float Lift = float.NaN;          // its height above the ground when it left, or NaN
    public RecordId Faction;
    public float Health = 1f;
    public bool TracksHealth;               // it has the conventions' health attribute, which gets Health back
    public RecordId Routine;
    public float Speed;                     // m/s
    public float Strength;
    public bool Corpse = true;
    public string Fight = OffscreenFights.Default;
    public bool Dead;
    public List<JsonObject> Entity = new(); // its save entries: the root's, then its prefab's children's

    // The way round what is in its way (4m-10): the corners still to walk, absolute, its goal last, toward
    // `RouteGoal`; empty while it walks straight. Saved, so a run saved half way walks the same corners.
    public List<Vector3> Route = new();
    public Vector3 RouteGoal;

    // Worked out from the above when it joins or a save is loaded; never saved.
    internal PersistentId PersistentId;
    internal int Key;
    internal IOffscreenFight? Rule;
    internal bool Fought;

    internal void Prepare()
    {
        PersistentId.TryParse(Id, out PersistentId);
        Key = PersistentId.Value.GetHashCode();
        Rule = null;
    }

    public override string ToString() => $"{(Name.Length > 0 ? Name : Prefab.ToString())} {Id} in {Scene} at ({X:0.#}, {Z:0.#})";
}

// The off-screen table: every agent, and the step the simulation has reached. A saved world resource.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[SavedResource("offscreen", Plugin = "sage.gameplay.ai")]
public sealed class OffscreenAgents : ISavedResource
{
    // The game minute (the clock's `Elapsed` × 60) simulated up to; -1 until the first run.
    public long Minute = -1;
    public List<OffscreenAgent> Agents = new();

    // Something changed outside the step (a load, an agent added by hand): look at every agent's cell.
    internal bool Dirty = true;

    public OffscreenAgent? Find(PersistentId id)
    {
        foreach (var agent in Agents)
            if (agent.PersistentId == id) return agent;
        return null;
    }

    // An agent made by code (a test, a tool): prepared, and looked at on the next run.
    public void Add(OffscreenAgent agent)
    {
        agent.Prepare();
        Agents.Add(agent);
        Dirty = true;
    }

    public void AfterLoad(World world)
    {
        foreach (var agent in Agents) agent.Prepare();
        Dirty = true;
    }
}

// An off-screen agent died (4g-6): raised in the step that killed it. `Killer` is the other agent's id.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
[GameEvent]
public readonly record struct OffscreenDied(PersistentId Agent, RecordId Faction, PersistentId Killer, RecordId KillerFaction);

// ---- fights ------------------------------------------------------------------------------------------------

// How a round of an off-screen fight goes: one game minute between two hostile agents close together.
// Take health from either; at zero or less it is dead. `roll` is 0 <= roll < 1, the same for the same two
// agents in the same minute on every run, so a fight comes out the same however time was passed. Called in
// the step, which allocates nothing: neither may this.
[Vocabulary("offscreen_fight")]
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public interface IOffscreenFight
{
    void Round(OffscreenAgent a, OffscreenAgent b, float roll);
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class OffscreenFightAttribute : VocabularyEntryAttribute<IOffscreenFight>
{
    public OffscreenFightAttribute(string id) : base(id) { }
}

// The default: strength × health is each side's weight, the roll picks the round's winner by them, and
// the winner takes its strength from the loser's health. Two agents with no strength trade nothing.
[OffscreenFight(OffscreenFights.Default, Plugin = "sage.gameplay.ai")]
internal sealed class StrengthFight : IOffscreenFight
{
    public void Round(OffscreenAgent a, OffscreenAgent b, float roll)
    {
        float wa = a.Strength * MathF.Max(a.Health, 0f), wb = b.Strength * MathF.Max(b.Health, 0f);
        if (wa + wb <= 0f) return;
        if (roll * (wa + wb) < wa) b.Health -= a.Strength;
        else a.Health -= b.Strength;
    }
}

[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public static class OffscreenFights
{
    public const string Default = "strength";

    // How close two agents must be to fight, in metres.
    public const float Range = 12f;
}

// ---- where things are, from the content ------------------------------------------------------------------

// Where an off-screen agent can go: every named placement of every scene and every `targetname` of the
// `.map`s a scene places (its anchors), the doors between scenes, and each scene's coarse graph of walls and
// ways round them (OffscreenGraph, 4m-10), in absolute metres. Read from the records, not the world, so it
// does not depend on what is loaded; made again when records reload.
//
// **Doors** are placements whose prefab (or the placement's overrides) has a `load_door` part, `{ "scene",
// "entry" }` (issue 4g-5's), and map entities whose classname is such a prefab (with `load_door.scene` and
// `load_door.entry` keys over it): the door's position is the way out of its scene, and the named entry in the
// other scene the way in. Code may add doors of its own (`AddDoor`), which a reload keeps.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class OffscreenMap
{
    public const string DoorPart = "load_door";

    // Each anchor's absolute position, and its height above the terrain when it stands on it (else NaN).
    private readonly Dictionary<(RecordId Scene, string Name), (Vector3 At, float Lift)> _anchors = new();
    private readonly Dictionary<(RecordId From, RecordId To), (Vector3 Exit, Vector3 Entry)> _doors = new();
    private readonly Dictionary<(RecordId From, RecordId To), (Vector3 Exit, Vector3 Entry)> _added = new();
    private readonly Dictionary<RecordId, OffscreenGraph> _graphs = new();
    private static readonly OffscreenGraph Empty = new();
    private bool _built;

    public bool TryAnchor(RecordId scene, string name, out Vector3 at)
    {
        bool found = _anchors.TryGetValue((scene, name), out var anchor);
        at = anchor.At;
        return found;
    }

    // `lift`: the anchor's height above the terrain, when it stands on it (a ground-framed placement, a level
    // on the terrain); NaN when its height is absolute.
    internal bool TryAnchor(RecordId scene, string name, out Vector3 at, out float lift)
    {
        bool found = _anchors.TryGetValue((scene, name), out var anchor);
        (at, lift) = found ? anchor : (default, float.NaN);
        return found;
    }

    // The way from `from` to `to`: where to leave `from` and where that comes out in `to`.
    public bool TryDoor(RecordId from, RecordId to, out Vector3 exit, out Vector3 entry)
    {
        if (_added.TryGetValue((from, to), out var door) || _doors.TryGetValue((from, to), out door))
        {
            exit = door.Exit;
            entry = door.Entry;
            return true;
        }
        exit = entry = default;
        return false;
    }

    public void AddDoor(RecordId from, Vector3 exit, RecordId to, Vector3 entry) => _added[(from, to)] = (exit, entry);

    // Whether a wall of `scene`'s content stands between two absolute points (4m-10).
    public bool Blocked(RecordId scene, Vector3 a, Vector3 b) => GraphOf(scene).Blocked(a, b);

    internal OffscreenGraph GraphOf(RecordId scene) => _graphs.TryGetValue(scene, out var graph) ? graph : Empty;

    internal void Invalidate() => _built = false;

    internal OffscreenMap Ensure(RecordStore records, Engine? engine)
    {
        if (_built) return this;
        _built = true;
        _anchors.Clear();
        _doors.Clear();
        _graphs.Clear();
        var exits = new List<(RecordId From, Vector3 Exit, RecordId To, string Entry)>();
        bool maps = records.TypeNameOf(typeof(MapRecord)) != null;   // sage.maps may be off
        foreach (var id in records.Ids("scene"))
        {
            if (!records.TryGet(id, out SceneRecord scene)) continue;
            var graph = new OffscreenGraph();
            _graphs[id] = graph;
            foreach (var placement in scene.Place) Note(records, id, graph, placement, scene.Origin, scene.RelativeTo, exits);
            foreach (var document in scene.Placements)
                if (records.TryGet(document.Id, out PlacementsRecord placements))
                    foreach (var placement in placements.Place) Note(records, id, graph, placement, placements.Origin, placements.RelativeTo, exits);
            if (engine != null && maps)
                foreach (var map in scene.Maps)
                    if (records.TryGet(map.Id, out MapRecord level)) NoteMap(engine, records, id, graph, map.Id, level, exits);
            graph.Finish();
        }
        foreach (var (from, exit, to, entry) in exits)
            if (_anchors.TryGetValue((to, entry), out var inside)) _doors.TryAdd((from, to), (exit, inside.At));
        return this;
    }

    private void Note(RecordStore records, RecordId scene, OffscreenGraph graph, Placement placement, Vector3 origin, PlacementFrame frame,
                      List<(RecordId, Vector3, RecordId, string)> exits)
    {
        var relativeTo = placement.RelativeTo ?? frame;
        var at = relativeTo == PlacementFrame.World ? placement.At : origin + placement.At;
        bool ground = relativeTo == PlacementFrame.Ground;
        if (!string.IsNullOrEmpty(placement.Name)) _anchors.TryAdd((scene, placement.Name), (at, ground ? placement.At.Y : float.NaN));

        records.TryGet(placement.Prefab.Id, out PrefabRecord prefab);
        var parts = placement.Overrides?.Parts;
        var door = Options(prefab?.Parts, parts, DoorPart);
        if (door != null && Text(door, "scene") is { Length: > 0 } to && Text(door, "entry") is { Length: > 0 } entry)
        {
            try { exits.Add((scene, at, RecordId.Parse(to, placement.Prefab.Id.Namespace), entry)); }
            catch (FormatException) { }   // the door's own check says so
        }
        // On the ground, a wall's height is measured from the terrain, as the placement's is.
        if (prefab != null) Solid(graph, prefab, placement.Overrides, ground ? at with { Y = placement.At.Y } : at, placement.Yaw, ground);
    }

    // What a placed prefab is to an agent walking past: a wall, a door, a link, or nothing.
    private static void Solid(OffscreenGraph graph, PrefabRecord prefab, PrefabOverrides? overrides, Vector3 at, float yaw, bool ground)
    {
        var parts = overrides?.Parts;
        if (Options(prefab.Parts, parts, "nav_link") is { } link && Vector(link, "end") is { } end)
            graph.AddLink(at, at + end, Flag(link, "twoWay"), Number(link, "cost") ?? 0f, ground);

        if (!Shape(prefab, overrides, out var centre, out var half)) return;
        var rotation = SageMath.RotationFromYaw(yaw * MathF.PI / 180f);
        var middle = at + Vector3.Transform(centre, rotation);
        bool mover = Options(prefab.Parts, parts, "mover") != null;
        bool locked = Options(prefab.Parts, parts, "nav_door") is { } navDoor && Flag(navDoor, "locked");
        if (mover && !locked)
        {
            graph.AddNode(middle with { Y = middle.Y - half.Y }, ground);   // a door: a way through, at its foot
            return;
        }

        // The box round the turned box.
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int sx = -1; sx <= 1; sx += 2)
            for (int sz = -1; sz <= 1; sz += 2)
            {
                var corner = middle + Vector3.Transform(new Vector3(sx * half.X, 0f, sz * half.Z), rotation);
                min = Vector3.Min(min, corner);
                max = Vector3.Max(max, corner);
            }
        min.Y = middle.Y - half.Y;
        max.Y = middle.Y + half.Y;
        graph.AddWall(min, max, ground);
    }

    // A static, solid shape: its centre (from the placement) and half extents. A `body` part without mass, or
    // a `collider` with no rigid body or a static one; never a trigger.
    private static bool Shape(PrefabRecord prefab, PrefabOverrides? overrides, out Vector3 centre, out Vector3 half)
    {
        centre = half = default;
        if (Options(prefab.Parts, overrides?.Parts, "body") is { } body)
        {
            if (Flag(body, "trigger") || (Number(body, "mass") ?? 0f) > 0f) return false;
            string shape = Text(body, "shape") ?? "Box";
            float radius = Number(body, "radius") ?? 0f, height = Number(body, "height") ?? 0f;
            if (shape.Equals("Capsule", StringComparison.OrdinalIgnoreCase))
            {
                centre = new Vector3(0f, height * 0.5f, 0f);   // a capsule stands on its point
                half = new Vector3(radius, height * 0.5f, radius);
            }
            else if (shape.Equals("Sphere", StringComparison.OrdinalIgnoreCase)) half = new Vector3(radius);
            else half = (Vector(body, "size") ?? Vector3.Zero) * 0.5f;
            return half.X > 0f && half.Z > 0f;
        }
        if (Options(prefab.Components, overrides?.Components, "collider") is { } collider)
        {
            if (Flag(collider, "isTrigger")) return false;
            if (Options(prefab.Components, overrides?.Components, "rigid_body") is { } rigid
                && Text(rigid, "kind") is { } kind && !kind.Equals("Static", StringComparison.OrdinalIgnoreCase)) return false;
            var size = Vector(collider, "size") ?? Vector3.Zero;
            string shape = Text(collider, "shape") ?? "Box";
            if (shape.Equals("Sphere", StringComparison.OrdinalIgnoreCase)) half = new Vector3(size.X);
            else if (shape.Equals("Capsule", StringComparison.OrdinalIgnoreCase)) half = new Vector3(size.X, size.Y * 0.5f + size.X, size.X);
            else if (shape.Equals("Box", StringComparison.OrdinalIgnoreCase)) half = size * 0.5f;
            centre = Vector(collider, "center") ?? Vector3.Zero;
            return half.X > 0f && half.Z > 0f;
        }
        return false;
    }

    // A `.map` a scene places (4m-10): its targetnames are anchors, its worldspawn brushes and solid entities
    // walls, its movers doors, and a classname with a load_door a door.
    private void NoteMap(Engine engine, RecordStore records, RecordId scene, OffscreenGraph graph, RecordId id, MapRecord record,
                         List<(RecordId, Vector3, RecordId, string)> exits)
    {
        if (MapLevel.Read(engine, id, record) is not { } level) return;   // its own load says why
        var origin = record.At;
        bool ground = record.OnTerrain;
        foreach (var brush in level.Brushes) graph.AddWall(origin + brush.Min, origin + brush.Max, ground);

        foreach (var entity in level.PointEntities)
        {
            var at = origin + level.LocalPositionOf(entity);
            if (entity.Keys.TryGetValue("targetname", out var name) && !string.IsNullOrEmpty(name)) _anchors.TryAdd((scene, name), (at, ground ? at.Y : float.NaN));
            if (!records.TryGet(ClassPrefab(id, entity.ClassName), out PrefabRecord prefab)) continue;
            var door = Options(prefab.Parts, null, DoorPart);
            string? to = Key(entity, DoorPart + ".scene") ?? (door != null ? Text(door, "scene") : null);
            string? entry = Key(entity, DoorPart + ".entry") ?? (door != null ? Text(door, "entry") : null);
            if (to is { Length: > 0 } && entry is { Length: > 0 })
            {
                try { exits.Add((scene, at, RecordId.Parse(to, id.Namespace), entry)); }
                catch (FormatException) { }
            }
            if (Options(prefab.Parts, null, "nav_link") is { } link)
            {
                Vector3? end = Key(entity, "nav_link.end") is { } text && ParseVector(text) is { } written ? written : Vector(link, "end");
                if (end is { } offset) graph.AddLink(at, at + offset, Flag(link, "twoWay"), Number(link, "cost") ?? 0f, ground);
            }
        }

        foreach (var solid in level.Solids)
        {
            var at = origin + solid.Origin;
            if (solid.Source.Keys.TryGetValue("targetname", out var name) && !string.IsNullOrEmpty(name)) _anchors.TryAdd((scene, name), (at, ground ? at.Y : float.NaN));
            if (solid.IsTrigger) continue;
            records.TryGet(ClassPrefab(id, solid.Source.ClassName), out PrefabRecord prefab);
            bool mover = prefab != null && Options(prefab.Parts, null, "mover") != null;
            bool locked = prefab != null && Options(prefab.Parts, null, "nav_door") is { } navDoor && Flag(navDoor, "locked");
            if (mover && !locked)
            {
                graph.AddNode(at, ground);
                continue;
            }
            foreach (var brush in solid.Brushes) graph.AddWall(origin + brush.Min, origin + brush.Max, ground);
        }
    }

    // A classname is a prefab of the level's namespace unless it names another (as the level's own load reads it).
    private static RecordId ClassPrefab(RecordId level, string className)
    {
        if (string.IsNullOrEmpty(className)) return default;
        if (className.IndexOf(':') < 0) return new RecordId(level.Namespace, className);
        try { return RecordId.Parse(className, level.Namespace); }
        catch (FormatException) { return default; }
    }

    private static string? Key(MapEntity entity, string key) => entity.Keys.TryGetValue(key, out var text) && text.Length > 0 ? text : null;

    private static Vector3? ParseVector(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length != 3) return null;
        return float.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out float x)
            && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out float y)
            && float.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out float z) ? new Vector3(x, y, z) : null;
    }

    // ---- reading content's JSON -----------------------------------------------------------------------------

    // A part's (or component's) options, the prefab's with the placement's overrides over them; keys matched
    // ignoring case, a component's with or without its namespace.
    private static JsonObject? Options(JsonObject? own, JsonObject? overrides, string key)
    {
        var mine = Find(own, key);
        var theirs = Find(overrides, key);
        if (theirs == null) return mine;
        if (mine == null) return theirs;
        var merged = (JsonObject)mine.DeepClone();
        foreach (var (name, value) in theirs) merged[name] = value?.DeepClone();
        return merged;
    }

    private static JsonObject? Find(JsonObject? json, string key)
    {
        if (json == null) return null;
        foreach (var (name, value) in json)
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)
                || (name.Length > key.Length && name[name.Length - key.Length - 1] == ':' && name.EndsWith(key, StringComparison.OrdinalIgnoreCase)))
                return value as JsonObject;
        return null;
    }

    private static JsonNode? Field(JsonObject json, string key)
    {
        foreach (var (name, value) in json)
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }

    private static string? Text(JsonObject json, string key) =>
        Field(json, key) is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static float? Number(JsonObject json, string key) =>
        Field(json, key) is JsonValue value && value.TryGetValue(out double number) ? (float)number : null;

    private static bool Flag(JsonObject json, string key) =>
        Field(json, key) is JsonValue value && value.TryGetValue(out bool flag) && flag;

    private static Vector3? Vector(JsonObject json, string key)
    {
        if (Field(json, key) is not JsonArray array || array.Count != 3) return null;
        float Get(int i) => array[i] is JsonValue v && v.TryGetValue(out double d) ? (float)d : 0f;
        return new Vector3(Get(0), Get(1), Get(2));
    }

    public static OffscreenMap Of(World world) =>
        world.Resources.GetOrAdd(() => new OffscreenMap()).Ensure(world.Records(), world.Engine);
}

// ---- the simulation ----------------------------------------------------------------------------------------

// What runs it: the cells' handoff, the steps, and putting agents back. Commands, before the AI thinks, so
// an agent spawned back thinks in the tick it arrives.
[System("sage.ai.offscreen", Phase.Commands, Before = new[] { "sage.ai.think" })]
internal sealed class OffscreenSystem : ISystem, ICellHandoff, IDisposable
{
    private const uint Salt = 0x0FF5C2EE;
    private static readonly IOffscreenFight Fallback = new StrengthFight();

    private readonly World _world;
    private readonly RecordStore _records;
    private readonly CVar<int>? _budget;
    private readonly EventReader<TimePassed> _timePassed;
    private OffscreenAgent[] _order = new OffscreenAgent[64];
    private int _generation = -1;

    public OffscreenSystem(World world, RecordStore records, CVar<int>? budget)
    {
        _world = world;
        _records = records;
        _budget = budget;
        _timePassed = world.Events.Reader<TimePassed>(this, Schedule.Fixed);
        CellContent.AddHandoff(world, this);
    }

    public void Dispose() => CellContent.RemoveHandoff(_world, this);

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        bool skipped = false;
        if (_timePassed.HasPending)
            foreach (ref readonly var _ in _timePassed.Read()) skipped = true;
        if (!world.Resources.TryGet<OffscreenAgents>(out var table) || table == null) return;
        if (!world.Resources.TryGet<WorldClock>(out var clock) || clock == null) return;

        // What the content placed in sectors never in the world joins the table, at its routine's place, once
        // per placing of the scene (4m-10); and what walked through a door in view goes on off-screen.
        _seeding = true;
        try { CellContent.SeedUnplaced(world, Wants, this, out _); }
        finally { _seeding = false; }
        if (world.Resources.TryGet<OffscreenDoorway>(out var doorway) && doorway != null && doorway.Count > 0) GoThrough(world, table, doorway);

        long now = (long)Math.Floor(clock.Elapsed * 60.0);
        if (table.Minute < 0 || now < table.Minute || table.Agents.Count == 0) table.Minute = now;

        int steps = 0;
        if (table.Minute < now)
        {
            // A skip runs every step it covers; ticking, as many as the budget allows.
            long most = skipped ? long.MaxValue : Math.Max(1, (_budget?.Value ?? DefaultBudget) / Math.Max(1, table.Agents.Count));
            var map = OffscreenMap.Of(world);
            var calendar = Calendars.Of(world);
            while (table.Minute < now && steps < most)
            {
                Step(world, table, map, calendar);
                steps++;
            }
        }

        int generation = CellContent.Generation(world);
        if (steps > 0 || table.Dirty || generation != _generation)
        {
            _generation = generation;
            table.Dirty = false;
            Reconcile(world, table);
        }
    }

    public const int DefaultBudget = 20000;

    // ---- one step: a game minute --------------------------------------------------------------------------

    internal void Step(World world, OffscreenAgents table, OffscreenMap map, CalendarRecord calendar)
    {
        long minute = table.Minute;
        double hours = (minute + 1) / 60.0;   // where the clock is when this minute is done
        var agents = table.Agents;

        if (_order.Length < agents.Count) _order = new OffscreenAgent[Math.Max(agents.Count, _order.Length * 2)];
        int n = 0;
        for (int i = 0; i < agents.Count; i++)
        {
            var agent = agents[i];
            agent.Fought = false;
            if (!agent.Dead) _order[n++] = agent;
        }
        new Span<OffscreenAgent>(_order, 0, n).Sort(ByPlace.Comparison);   // a Comparison: Array.Sort with an IComparer makes a delegate every call

        // Fights: each agent at most one round, paired west to east.
        bool died = false;
        const float range = OffscreenFights.Range;
        for (int i = 0; i < n; i++)
        {
            var a = _order[i];
            if (a.Fought || a.Faction.IsEmpty) continue;
            for (int j = i + 1; j < n; j++)
            {
                var b = _order[j];
                if (b.Scene != a.Scene || b.X - a.X > range) break;
                if (b.Fought || b.Faction.IsEmpty) continue;
                double dx = b.X - a.X, dz = b.Z - a.Z;
                if (dx * dx + dz * dz > range * range) continue;
                if (!Factions.AreHostile(_records, a.Faction, b.Faction)) continue;

                // The lower id first, so the rule and the roll do not depend on where they stand.
                var (p, q) = a.PersistentId.Value.CompareTo(b.PersistentId.Value) <= 0 ? (a, b) : (b, a);
                var rule = p.Rule ??= Find(world, p.Fight);
                rule.Round(p, q, ShotRandom.Value(minute, p.Key, (uint)q.Key, Salt));
                a.Fought = b.Fought = true;
                died |= Die(world, p, q) | Die(world, q, p);
                break;
            }
        }

        // The rest walk.
        for (int i = 0; i < n; i++)
        {
            var agent = _order[i];
            if (!agent.Fought && !agent.Dead) Move(agent, map, calendar, hours);
        }

        // The dead that leave nothing.
        if (died)
            for (int i = agents.Count - 1; i >= 0; i--)
                if (agents[i].Dead && !agents[i].Corpse) agents.RemoveAt(i);

        table.Minute = minute + 1;
    }

    private bool Die(World world, OffscreenAgent agent, OffscreenAgent killer)
    {
        if (agent.Dead || agent.Health > 0f) return false;
        agent.Dead = true;
        agent.Health = 0f;
        world.Events.Send(new OffscreenDied(agent.PersistentId, agent.Faction, killer.PersistentId, killer.Faction));
        return true;
    }

    private IOffscreenFight Find(World world, string id)
    {
        if (world.Engine?.Vocabularies.Of<IOffscreenFight>().Find(id) is { } rule) return rule;
        Log.Once(LogCat.AI, LogLevel.Warn, $"offscreen-fight:{id}", $"no offscreen_fight '{id}'; off-screen fights use '{OffscreenFights.Default}'");
        return Fallback;
    }

    // Toward its routine's anchor; through a door when the anchor is in another scene. Straight while the
    // minute's walk is clear, round what is in the way (OffscreenGraph, 4m-10) when it is not.
    private void Move(OffscreenAgent agent, OffscreenMap map, CalendarRecord calendar, double hours)
    {
        if (agent.Routine.IsEmpty || agent.Speed <= 0f || !_records.TryGet(agent.Routine, out RoutineRecord routine)) return;
        int index = Routines.EntryAt(routine, calendar, hours);
        if (index < 0) return;
        var entry = routine.Entries[index];
        var scene = entry.Scene.IsEmpty ? agent.Scene : entry.Scene;

        Vector3 goal, inside = default;
        float lift = float.NaN;
        bool door = scene != agent.Scene;
        if (door ? !map.TryDoor(agent.Scene, scene, out goal, out inside) : !map.TryAnchor(scene, entry.At, out goal, out lift)) return;

        if (agent.RouteGoal != goal || agent.Route.Count > 0 && agent.Route[^1] != goal)
        {
            agent.Route.Clear();
            agent.RouteGoal = goal;
        }
        var graph = map.GraphOf(agent.Scene);
        double budget = agent.Speed * 60.0;
        while (budget > 0)
        {
            var next = agent.Route.Count > 0 ? agent.Route[0] : goal;
            double dx = next.X - agent.X, dz = next.Z - agent.Z;
            double distance = Math.Sqrt(dx * dx + dz * dz);

            // Straight, until this minute's walk would go through a wall: then the way round, from here.
            if (agent.Route.Count == 0 && graph.WallCount > 0)
            {
                double f = distance > budget ? budget / distance : 1.0;
                var here = new Vector3((float)agent.X, (float)agent.Y, (float)agent.Z);
                var ahead = new Vector3((float)(agent.X + dx * f), (float)(agent.Y + (next.Y - agent.Y) * f), (float)(agent.Z + dz * f));
                if (graph.Blocked(here, ahead))
                {
                    if (!graph.Plan(here, goal, agent.Route)) agent.Route.Add(goal);   // no way round: straight, as before
                    continue;
                }
            }

            if (distance > budget)
            {
                double f = budget / distance;
                agent.X += dx * f;
                agent.Z += dz * f;
                agent.Y += (next.Y - agent.Y) * f;
                return;
            }
            budget -= distance;
            agent.X = next.X;
            agent.Y = next.Y;
            agent.Z = next.Z;
            bool arrived = agent.Route.Count <= 1;   // the last corner is the goal
            if (agent.Route.Count > 0) agent.Route.RemoveAt(0);
            if (!arrived) continue;

            if (door)
            {
                agent.Scene = scene;
                agent.X = inside.X;
                agent.Y = inside.Y;
                agent.Z = inside.Z;
                agent.Lift = float.NaN;   // another space: it stands where the entry is
                agent.Route.Clear();
                agent.RouteGoal = default;
            }
            else if (!float.IsNaN(lift)) agent.Lift = lift;
            return;
        }
    }

    private sealed class ByPlace : IComparer<OffscreenAgent>
    {
        public static readonly ByPlace Instance = new();
        public static readonly Comparison<OffscreenAgent> Comparison = Instance.Compare;

        public int Compare(OffscreenAgent? a, OffscreenAgent? b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a is null) return -1;
            if (b is null) return 1;
            int c = string.CompareOrdinal(a.Scene.Namespace, b.Scene.Namespace);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Scene.Name, b.Scene.Name);
            if (c != 0) return c;
            c = a.X.CompareTo(b.X);
            if (c != 0) return c;
            c = a.Z.CompareTo(b.Z);
            return c != 0 ? c : a.PersistentId.Value.CompareTo(b.PersistentId.Value);
        }
    }

    internal static SectorCoord SectorOf(double x, double z) =>
        new((int)Math.Floor(x / Terrain.SectorSize), (int)Math.Floor(z / Terrain.SectorSize));

    // ---- going and coming back -----------------------------------------------------------------------------

    // A cell is going dormant: what opted in, and is alive, becomes an agent.
    public void Sleeping(World world, string cell, IReadOnlyList<Entity> roots)
    {
        if (!world.Resources.TryGet<OffscreenAgents>(out var table) || table == null) return;
        var scene = CellContent.SceneOf(world);
        if (scene.IsEmpty) return;
        foreach (var root in roots)
        {
            if (!world.IsAlive(root) || !world.TryGet<Offscreen>(root, out var offscreen) || !world.Has<Transform>(root) || IsDead(world, root)) continue;
            var agent = Take(world, root, offscreen, scene);
            if (_seeding) AtRoutine(world, agent);
            table.Add(agent);
            Log.Debug(LogCat.AI, _seeding ? $"{agent} is off-screen ({cell} was never placed)" : $"{agent} is off-screen ({cell} went dormant)");
        }
    }

    private OffscreenAgent Take(World world, Entity root, in Offscreen offscreen, RecordId scene)
    {
        var local = world.Get<Transform>(root).LocalPosition;
        var corner = world.Origin().Sector;
        var agent = new OffscreenAgent
        {
            Scene = scene,
            X = (double)corner.X * Terrain.SectorSize + local.X,
            Y = local.Y,
            Z = (double)corner.Z * Terrain.SectorSize + local.Z,
            Prefab = world.TryGet<FromPrefab>(root, out var from) ? from.Prefab : default,
            Name = root.Name ?? "",
            Faction = Factions.FactionOf(world, root),
            Routine = Routines.IdOf(world, root),
            Strength = offscreen.Strength,
            Corpse = offscreen.Corpse,
            Fight = string.IsNullOrEmpty(offscreen.Fight) ? OffscreenFights.Default : offscreen.Fight,
            Speed = offscreen.Speed > 0 ? offscreen.Speed : WalkSpeed(world, root),
        };
        if (world.Resources.TryGet<Terrain>(out var terrain) && terrain != null && terrain.IsLoaded(SectorOf(agent.X, agent.Z)))
            agent.Lift = local.Y - terrain.HeightAt(local.X, local.Z);

        var health = world.Conventions().Health.Id;
        if (!health.IsEmpty && world.Resources.TryGet<GameplayRegistries>(out var registries) && registries != null
            && registries.Attribute(health) is >= 0 and var index
            && world.TryGet<Attributes>(root, out var attributes) && attributes.Values != null && attributes.Values.Has(index))
        {
            agent.Health = attributes.Values[index];
            agent.TracksHealth = true;
        }

        agent.Entity = CellContent.Release(world, root);
        agent.Id = IdOf(agent.Entity);
        return agent;
    }

    private static string IdOf(List<JsonObject> entries) =>
        entries.Count > 0 && entries[0]["id"] is JsonValue value && value.TryGetValue(out string? id) ? id : "";

    private float WalkSpeed(World world, Entity entity)
    {
        var movement = MovementProfileRecord.Fallback;
        if (world.TryGet<CharacterController>(entity, out var character))
            movement = CharacterConventions.Of(world).ProfileOf(_records, character.Profile);
        return movement.WalkSpeed;
    }

    private static bool IsDead(World world, Entity entity)
    {
        var dead = world.Conventions().Dead.Id;
        return !dead.IsEmpty && world.Resources.TryGet<GameplayRegistries>(out _) && world.HasTag(entity, dead);
    }

    // ---- the never placed, and doors in view (4m-10) ----------------------------------------------------------

    private bool _seeding;

    // What seeding takes: a placement whose prefab, or whose overrides, have the `offscreen` part.
#pragma warning disable SAGE0131 // placement overrides (phase 4i): what the seam hands over; this reads them only
    private static readonly Func<PrefabRecord, PrefabOverrides?, bool> Wants = (prefab, overrides) =>
        HasPart(prefab.Parts) || HasPart(overrides?.Parts);
#pragma warning restore SAGE0131

    private static bool HasPart(JsonObject? parts)
    {
        if (parts == null) return false;
        foreach (var (key, _) in parts)
            if (string.Equals(key, "offscreen", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // An NPC whose sector was never placed has been living its routine all along: it is where the entry in
    // force has it, in that entry's scene, when the content says where that is; else where it was put.
    private void AtRoutine(World world, OffscreenAgent agent)
    {
        if (agent.Routine.IsEmpty || !_records.TryGet(agent.Routine, out RoutineRecord routine)
            || !world.Resources.TryGet<WorldClock>(out var clock) || clock == null) return;
        int index = Routines.EntryAt(routine, Calendars.Of(world), clock.Elapsed);
        if (index < 0) return;
        var entry = routine.Entries[index];
        var scene = entry.Scene.IsEmpty ? agent.Scene : entry.Scene;
        if (!OffscreenMap.Of(world).TryAnchor(scene, entry.At, out var at, out float lift)) return;
        agent.Scene = scene;
        agent.X = at.X;
        agent.Y = at.Y;
        agent.Z = at.Z;
        agent.Lift = lift;
    }

    // Live NPCs that reached the door to the scene their anchor is in (MoveToAnchorTask): out of the world,
    // and on the other side of it, off-screen.
    private void GoThrough(World world, OffscreenAgents table, OffscreenDoorway doorway)
    {
        var scene = CellContent.SceneOf(world);
        var map = OffscreenMap.Of(world);
        foreach (var (entity, to) in doorway.Take())
        {
            if (scene.IsEmpty || !world.IsAlive(entity) || !world.TryGet<Offscreen>(entity, out var offscreen) || !world.Has<Transform>(entity)
                || IsDead(world, entity) || !entity.Parent.IsNull || !map.TryDoor(scene, to, out _, out var entry)) continue;
            var agent = Take(world, entity, offscreen, scene);
            agent.Scene = to;
            agent.X = entry.X;
            agent.Y = entry.Y;
            agent.Z = entry.Z;
            agent.Lift = float.NaN;
            table.Add(agent);
            Log.Debug(LogCat.AI, $"{agent} went through the door to {to}");
        }
    }

    // Every agent whose scene and sector are in the world is spawned there.
    private void Reconcile(World world, OffscreenAgents table)
    {
        var agents = table.Agents;
        for (int i = agents.Count - 1; i >= 0; i--)
        {
            var agent = agents[i];
            var sector = SectorOf(agent.X, agent.Z);
            if (!CellContent.IsLive(world, agent.Scene, sector)) continue;
            agents.RemoveAt(i);
            Spawn(world, agent, sector);
        }
    }

    private void Spawn(World world, OffscreenAgent agent, SectorCoord sector)
    {
        var corner = world.Origin().Sector;
        var local = new Vector3((float)(agent.X - (double)corner.X * Terrain.SectorSize), (float)agent.Y,
                                (float)(agent.Z - (double)corner.Z * Terrain.SectorSize));
        if (!float.IsNaN(agent.Lift) && world.Resources.TryGet<Terrain>(out var terrain) && terrain != null && terrain.IsLoaded(sector))
            local.Y = terrain.HeightAt(local.X, local.Z) + agent.Lift;

        var entity = CellContent.Restore(world, agent.Entity, local, CellContent.CellAt(world, agent.Scene, sector));
        if (entity.IsNull)
        {
            Log.Warn(LogCat.AI, $"{agent} could not come back into the world; it is gone");
            return;
        }

        if (agent.TracksHealth && world.TryGet<Attributes>(entity, out var attributes) && attributes.Values != null
            && world.Resources.TryGet<GameplayRegistries>(out var registries) && registries != null
            && registries.Attribute(world.Conventions().Health.Id) is >= 0 and var index)
            attributes.Values.SetBase(index, agent.Health);
        if (agent.Dead && !world.Conventions().Dead.Id.IsEmpty && world.Resources.TryGet<GameplayRegistries>(out _))
            world.AddTag(entity, world.Conventions().Dead.Id);

        // Its AI starts its routine afresh: the entry in force, found again at its first think.
        if (world.Has<AIState>(entity))
        {
            ref var state = ref world.Get<AIState>(entity);
            state.Schedule = default;
            state.TaskIndex = 0;
            state.TaskStarted = false;
            state.TaskTime = 0;
            state.Target = default;
            state.NextThink = 0;
            state.RoutineEntry = 0;
            state.AnchorFor = 0;
            state.Anchor = default;
            state.AnchorElsewhere = false;
            state.AnchorFar = false;
            state.FarDoorTo = default;
            state.Path.Clear();
        }
        Log.Debug(LogCat.AI, $"{agent} is back in the world");
    }
}

// The live NPCs that reached the door to the scene their routine's anchor is in (4m-10): MoveToAnchorTask
// notes them while the AI thinks, where nothing may leave the world, and the off-screen system takes them
// through at its next run. A world resource.
internal sealed class OffscreenDoorway
{
    private readonly List<(Entity Entity, RecordId To)> _arrived = new();

    public int Count => _arrived.Count;

    public static OffscreenDoorway Of(World world) => world.Resources.GetOrAdd(() => new OffscreenDoorway());

    public void Arrived(Entity entity, RecordId to)
    {
        foreach (var (had, _) in _arrived)
            if (had == entity) return;
        _arrived.Add((entity, to));
    }

    public (Entity Entity, RecordId To)[] Take()
    {
        var taken = _arrived.ToArray();
        _arrived.Clear();
        return taken;
    }
}
