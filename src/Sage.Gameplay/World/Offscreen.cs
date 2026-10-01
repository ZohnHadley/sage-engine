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

// Where an off-screen agent can go: every named placement of every scene (its anchors), and the doors
// between scenes, in absolute metres. Read from the records, not the world, so it does not depend on what
// is loaded; made again when records reload.
//
// **Doors** are placements whose prefab (or the placement's overrides) has a `load_door` part, `{ "scene",
// "entry" }` (issue 4g-5's): the door's position is the way out of its scene, and the named entry in the
// other scene the way in. Code may add doors of its own (`AddDoor`), which a reload keeps.
[Experimental("SAGE0129", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
public sealed class OffscreenMap
{
    public const string DoorPart = "load_door";

    private readonly Dictionary<(RecordId Scene, string Name), Vector3> _anchors = new();
    private readonly Dictionary<(RecordId From, RecordId To), (Vector3 Exit, Vector3 Entry)> _doors = new();
    private readonly Dictionary<(RecordId From, RecordId To), (Vector3 Exit, Vector3 Entry)> _added = new();
    private bool _built;

    public bool TryAnchor(RecordId scene, string name, out Vector3 at) => _anchors.TryGetValue((scene, name), out at);

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

    internal void Invalidate() => _built = false;

    internal OffscreenMap Ensure(RecordStore records)
    {
        if (_built) return this;
        _built = true;
        _anchors.Clear();
        _doors.Clear();
        var exits = new List<(RecordId From, Vector3 Exit, RecordId To, string Entry)>();
        foreach (var id in records.Ids("scene"))
        {
            if (!records.TryGet(id, out SceneRecord scene)) continue;
            foreach (var placement in scene.Place) Note(records, id, placement, scene.Origin, scene.RelativeTo, exits);
            foreach (var document in scene.Placements)
                if (records.TryGet(document.Id, out PlacementsRecord placements))
                    foreach (var placement in placements.Place) Note(records, id, placement, placements.Origin, placements.RelativeTo, exits);
        }
        foreach (var (from, exit, to, entry) in exits)
            if (_anchors.TryGetValue((to, entry), out var inside)) _doors.TryAdd((from, to), (exit, inside));
        return this;
    }

    private void Note(RecordStore records, RecordId scene, Placement placement, Vector3 origin, PlacementFrame frame,
                      List<(RecordId, Vector3, RecordId, string)> exits)
    {
        var at = (placement.RelativeTo ?? frame) == PlacementFrame.World ? placement.At : origin + placement.At;
        if (!string.IsNullOrEmpty(placement.Name)) _anchors.TryAdd((scene, placement.Name), at);

        var door = Part(placement.Overrides?.Parts);
        if (records.TryGet(placement.Prefab.Id, out PrefabRecord prefab) && Part(prefab.Parts) is { } own)
            door = door == null ? own : Merge(own, door);
        if (door == null) return;
        string? to = Text(door, "scene"), entry = Text(door, "entry");
        if (to is not { Length: > 0 } || entry is not { Length: > 0 }) return;
        try { exits.Add((scene, at, RecordId.Parse(to, placement.Prefab.Id.Namespace), entry)); }
        catch (FormatException) { }   // the door's own check says so
    }

    // The `load_door` part's options, its key matched as 4g-5's own check matches it (ignoring case).
    private static JsonObject? Part(JsonObject? parts)
    {
        if (parts == null) return null;
        foreach (var (key, value) in parts)
            if (string.Equals(key, DoorPart, StringComparison.OrdinalIgnoreCase)) return value as JsonObject;
        return null;
    }

    private static JsonObject Merge(JsonObject prefab, JsonObject overrides)
    {
        var merged = (JsonObject)prefab.DeepClone();
        foreach (var (key, value) in overrides) merged[key] = value?.DeepClone();
        return merged;
    }

    private static string? Text(JsonObject json, string key)
    {
        foreach (var (name, value) in json)
            if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue(out string? text))
                return text;
        return null;
    }

    public static OffscreenMap Of(World world) =>
        world.Resources.GetOrAdd(() => new OffscreenMap()).Ensure(world.Records());
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

    // Toward its routine's anchor, in a straight line; through a door when the anchor is in another scene.
    private void Move(OffscreenAgent agent, OffscreenMap map, CalendarRecord calendar, double hours)
    {
        if (agent.Routine.IsEmpty || agent.Speed <= 0f || !_records.TryGet(agent.Routine, out RoutineRecord routine)) return;
        int index = Routines.EntryAt(routine, calendar, hours);
        if (index < 0) return;
        var entry = routine.Entries[index];
        var scene = entry.Scene.IsEmpty ? agent.Scene : entry.Scene;

        Vector3 target, inside = default;
        bool door = scene != agent.Scene;
        if (door ? !map.TryDoor(agent.Scene, scene, out target, out inside) : !map.TryAnchor(scene, entry.At, out target)) return;

        double dx = target.X - agent.X, dz = target.Z - agent.Z;
        double distance = Math.Sqrt(dx * dx + dz * dz);
        double step = agent.Speed * 60.0;
        if (distance <= step)
        {
            if (door)
            {
                agent.Scene = scene;
                target = inside;
                agent.Lift = float.NaN;   // another space: it stands where the entry is
            }
            agent.X = target.X;
            agent.Y = target.Y;
            agent.Z = target.Z;
            return;
        }
        double f = step / distance;
        agent.X += dx * f;
        agent.Z += dz * f;
        agent.Y += (target.Y - agent.Y) * f;
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
            table.Add(agent);
            Log.Debug(LogCat.AI, $"{agent} is off-screen ({cell} went dormant)");
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
            state.Path.Clear();
        }
        Log.Debug(LogCat.AI, $"{agent} is back in the world");
    }
}
