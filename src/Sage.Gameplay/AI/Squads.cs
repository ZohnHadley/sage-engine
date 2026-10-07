#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// Squads (docs/design/16 §3.4, REDESIGN "squads and formations", issue #388): creatures that fight as one.
// A creature is in a squad by its `squad` component — a name and, if it has one, a role:
//
//   { "type": "prefab", "id": "wolf", "components": { "squad": { "name": "pack" } }, … }
//
// What a squad knows is a blackboard the engine keeps for it (SquadInfo, from `Squads.Of(world)`), rebuilt
// every tick from its members, so nothing of it is saved that the members do not already save:
//   - its *target*: whatever a member sees (the leader's first). A member with nothing of its own to chase
//     takes it, and knows where it is while anybody in the squad can see it — so a pack closes in on a
//     player only one of them has seen;
//   - its *roles*: `leader` goes straight in, the rest flank (the `Flank` task spreads them round the
//     target, alternately left and right of the way the squad came at it); any other role is a game's own;
//   - *calls for help*: the `CallForHelp` task shouts (a `Noise` of kind `Alert`), and every squadmate, or
//     ally of the shouter's faction, within earshot that has nothing to fight takes the shouter's target.

// A creature's squad: its name, shared by its squadmates, and its role in it.
[Component("sage:squad")]
public struct Squad : IComponent
{
    [Property(Tooltip = "The squad's name: creatures with the same name share a target and answer each other's calls")]
    public string Name;
    [Property(Tooltip = "Its role: \"leader\" goes straight in while the rest flank; any other is a game's own")]
    public string Role;
    [Property(Min = 0, Unit = "m", Tooltip = "How near a squadmate that sees the target it must be to be told; 0 = anywhere in the world")]
    public float Range;

    public const string Leader = "leader";

    public readonly bool IsLeader => string.Equals(Role, Leader, StringComparison.OrdinalIgnoreCase);
}

// One squad's blackboard (see the top of the file).
public sealed class SquadInfo
{
    internal readonly List<Entity> MemberList = new();
    internal readonly List<Entity> Engaged = new();   // members after its target, in member order
    internal int SeenFrame;
    internal float ApproachYaw;                       // the way it came at its target, from the target

    internal SquadInfo(string name) => Name = name;

    public string Name { get; }
    // Its living members, in the order they were made (a stable order: who flanks which side).
    public IReadOnlyList<Entity> Members => MemberList;
    // The member whose role is `leader`; null without one.
    public Entity Leader { get; internal set; }
    // What it is fighting, if anything, and where that was when a member last saw it (sim time `SeenAt`).
    public Entity Target { get; internal set; }
    public Vector3 LastSeen { get; internal set; }
    public float SeenAt { get; internal set; }
    // The sim time a member last called for help; NegativeInfinity for never.
    public float CalledAt { get; internal set; } = float.NegativeInfinity;

    // How many of its members are after its target.
    public int EngagedCount => Engaged.Count;
}

// Every squad in a world, by name: a world resource the squad system keeps.
public sealed class Squads
{
    internal readonly Dictionary<string, SquadInfo> ByName = new(StringComparer.Ordinal);

    private Squads() { }

    public static Squads Of(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Resources.TryGet<Squads>(out var existing) && existing != null) return existing;
        var made = new Squads();
        world.Resources.Add(made);
        return made;
    }

    public IEnumerable<SquadInfo> All => ByName.Values;

    public SquadInfo? Find(string name) =>
        name != null && ByName.TryGetValue(name, out var info) && info.MemberList.Count > 0 ? info : null;

    // The squad `entity` is in, or null.
    public SquadInfo? Of(World world, Entity entity) =>
        world.TryGet<Squad>(entity, out var squad) && !string.IsNullOrEmpty(squad.Name) ? Find(squad.Name) : null;
}

// Keeps every squad's blackboard and shares what it knows (see the top of the file). Before the think, so a
// creature that was told about a target this tick chases it this tick.
[System("sage.ai.squad", Phase.Commands, After = new[] { "sage.character.player_control" }, Before = new[] { "sage.ai.think" })]
internal sealed class SquadSystem : ISystem
{
    private readonly Query<Squad, AIState, Transform> _members;
    private readonly EventReader<Noise> _noises;
    private readonly RecordStore _records;
    private readonly IPhysicsWorld _space;
    private readonly Entity[] _listeners = new Entity[256];   // broad-phase scratch, reused
    private readonly List<string> _gone = new();
    private int _frame;
    private static readonly Comparison<Entity> ByMade = (a, b) => a.Id.CompareTo(b.Id);

    public SquadSystem(World world, RecordStore records)
    {
        _members = world.Query<Squad, AIState, Transform>();
        _noises = world.Events.Reader<Noise>(this, Schedule.Fixed);
        _records = records;
        _space = world.Resources.Get<IPhysicsWorld>();
        Squads.Of(world);
        world.Origin().Rebased += offset =>
        {
            foreach (var info in Squads.Of(world).All) info.LastSeen += offset;
        };
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float time = (float)ctx.Tick.SimTime;
        var squads = Squads.Of(world);
        _frame++;

        Gather(world, squads);
        foreach (var info in squads.ByName.Values) Share(world, info, time);
        foreach (ref readonly var noise in _noises.Read())
            if (noise.Kind == NoiseKind.Alert) Answer(world, squads, in noise, time);
    }

    // Who is in which squad now: the living members, in the order they were made.
    private void Gather(World world, Squads squads)
    {
        foreach (var info in squads.ByName.Values)
        {
            info.MemberList.Clear();
            info.Leader = default;
        }
        var dead = world.Conventions().Dead;
        foreach (var (squad, _, _, entities) in _members.Chunks)
        {
            var q = squad.Span;
            for (int n = 0; n < q.Length; n++)
            {
                if (string.IsNullOrEmpty(q[n].Name)) continue;
                var entity = entities.EntityAt(n);
                if (world.HasTag(entity, dead)) continue;
                if (!squads.ByName.TryGetValue(q[n].Name, out var info)) squads.ByName[q[n].Name] = info = new SquadInfo(q[n].Name);
                info.MemberList.Add(entity);
                info.SeenFrame = _frame;
            }
        }
        _gone.Clear();
        foreach (var (name, info) in squads.ByName)
        {
            if (info.SeenFrame != _frame) { _gone.Add(name); continue; }
            info.MemberList.Sort(ByMade);
            foreach (var member in info.MemberList)
                if (info.Leader.IsNull && world.Get<Squad>(member).IsLeader) info.Leader = member;
        }
        foreach (string name in _gone) squads.ByName.Remove(name);
    }

    // The squad's target: what a member sees (the leader first, then in order), else what a member still
    // remembers; then every member with nothing else to chase is told.
    private void Share(World world, SquadInfo info, float time)
    {
        Entity spotter = default;
        bool seen = false;
        if (!info.Leader.IsNull && Sees(world, info.Leader)) { spotter = info.Leader; seen = true; }
        for (int i = 0; i < info.MemberList.Count && !seen; i++)
            if (Sees(world, info.MemberList[i])) { spotter = info.MemberList[i]; seen = true; }
        for (int i = 0; i < info.MemberList.Count && spotter.IsNull; i++)
        {
            ref readonly var state = ref world.Get<AIState>(info.MemberList[i]);
            if (!state.Target.IsNull && world.IsAlive(state.Target) && time < state.ForgetAt) spotter = info.MemberList[i];
        }

        if (spotter.IsNull)
        {
            if (!info.Target.IsNull && (!world.IsAlive(info.Target) || time - info.SeenAt > 30f)) info.Target = default;
        }
        else
        {
            ref readonly var knows = ref world.Get<AIState>(spotter);
            if (knows.Target != info.Target) info.ApproachYaw = Approach(world, info, knows.Target);
            info.Target = knows.Target;
            if (seen)
            {
                info.LastSeen = world.Get<Transform>(knows.Target).LocalPosition;
                info.SeenAt = time;
            }
            else info.LastSeen = knows.LastSeen;
        }

        info.Engaged.Clear();
        if (info.Target.IsNull) return;
        var spotterAt = spotter.IsNull ? info.LastSeen : world.Get<Transform>(spotter).LocalPosition;
        foreach (var member in info.MemberList)
        {
            ref var state = ref world.Get<AIState>(member);
            bool free = state.Target.IsNull || !world.IsAlive(state.Target) || time >= state.ForgetAt;
            bool ours = state.Target == info.Target;
            if (!free && !ours) continue;   // it has a fight of its own

            if (!seen && free) continue;    // nobody can see it: only what a member already knows goes round
            float range = world.Get<Squad>(member).Range;
            if (free && range > 0f && SageMath.DistanceXZ(world.Get<Transform>(member).LocalPosition, spotterAt) > range) continue;

            if (seen && (state.Conditions & (ulong)AICondition.SeeEnemy) == 0)
            {
                // Told where it is by whoever sees it: it walks to where it is, not where it last saw it.
                state.Target = info.Target;
                state.LastSeen = info.LastSeen;
                state.ForgetAt = MathF.Max(state.ForgetAt, time + Memory(world, in state));
            }
            if (state.Target == info.Target) info.Engaged.Add(member);
        }
    }

    // A call for help: squadmates of the shouter, and allies of its faction, within earshot and with nothing
    // to fight, take its target.
    private void Answer(World world, Squads squads, in Noise noise, float time)
    {
        var caller = noise.Source;
        if (caller.IsNull || !world.IsAlive(caller) || !world.TryGet<AIState>(caller, out var calling)) return;
        var squad = squads.Of(world, caller);
        if (squad != null) squad.CalledAt = time;
        var target = calling.Target;
        if (target.IsNull || !world.IsAlive(target)) return;

        int found = _space.OverlapBox(noise.Point, new Vector3(noise.Radius), _listeners);
        var dead = world.Conventions().Dead;
        for (int i = 0; i < found; i++)
        {
            var listener = _listeners[i];
            if (listener == caller || !world.IsAlive(listener) || !world.Has<AIState>(listener) || world.HasTag(listener, dead)) continue;
            if (!world.TryGet<Transform>(listener, out var at) || Vector3.Distance(at.LocalPosition, noise.Point) > noise.Radius) continue;
            bool mate = squad != null && squads.Of(world, listener) == squad;
            if (!mate && Factions.Toward(world, listener, caller) != Stance.Ally) continue;

            ref var state = ref world.Get<AIState>(listener);
            if (!state.Target.IsNull && world.IsAlive(state.Target) && time < state.ForgetAt) continue;
            state.Target = target;
            state.LastSeen = calling.LastSeen;
            state.ForgetAt = time + Memory(world, in state);
        }
    }

    private static bool Sees(World world, Entity member)
    {
        ref readonly var state = ref world.Get<AIState>(member);
        return (state.Conditions & (ulong)AICondition.SeeEnemy) != 0 && !state.Target.IsNull && world.IsAlive(state.Target);
    }

    // The way the squad is coming at a target: from the target toward the middle of its members.
    private static float Approach(World world, SquadInfo info, Entity target)
    {
        if (!world.TryGet<Transform>(target, out var at)) return 0f;
        var middle = Vector3.Zero;
        foreach (var member in info.MemberList) middle += world.Get<Transform>(member).LocalPosition;
        middle /= MathF.Max(info.MemberList.Count, 1);
        return SageMath.DistanceXZ(middle, at.LocalPosition) < 0.01f ? 0f : SageMath.YawTo(at.LocalPosition, middle);
    }

    private float Memory(World world, in AIState state)
    {
        var id = state.Profile.IsEmpty ? world.Conventions().AiProfile.Id : state.Profile;
        return !id.IsEmpty && _records.TryGet(id, out AIProfileRecord profile) ? MathF.Max(profile.MemorySeconds, 1f) : 6f;
    }
}

// ---- measures --------------------------------------------------------------------------------------

// How many are in its squad, itself among them (living, and within its squad's `range` of it); 1 alone.
[AIMeasure("squad_size", Plugin = "sage.gameplay.ai")]
internal sealed class SquadSizeMeasure : IAIMeasure
{
    public float Measure(in AIPerception p)
    {
        var info = Squads.Of(p.World).Of(p.World, p.Entity);
        if (info == null) return 1f;
        float range = p.World.Get<Squad>(p.Entity).Range;
        if (range <= 0f) return info.Members.Count;
        int near = 0;
        foreach (var member in info.Members)
            if (p.World.TryGet<Transform>(member, out var at) && SageMath.DistanceXZ(at.LocalPosition, p.Transform.LocalPosition) <= range) near++;
        return near;
    }
}

// How many of its squad are after the squad's target, itself among them; 0 with no squad or no target.
[AIMeasure("squad_engaged", Plugin = "sage.gameplay.ai")]
internal sealed class SquadEngagedMeasure : IAIMeasure
{
    public float Measure(in AIPerception p) => Squads.Of(p.World).Of(p.World, p.Entity)?.EngagedCount ?? 0;
}
