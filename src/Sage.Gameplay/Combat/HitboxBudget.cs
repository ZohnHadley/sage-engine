#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Gameplay;

// The hitbox budget (issue #273, docs/design/16 "As built (hit locations)"). Every hitbox is a kinematic
// body that follows a bone: a dozen per creature, posed every tick, in the broad phase every tick. A
// town of fifty creatures is six hundred bodies whose only use is telling a strike where it landed,
// and the strikes that matter are the ones near a player. So a creature far from every player, or past
// the nearest `MaxCreatures`, has its hitboxes **switched off** (the ColliderOff tag): they lose their
// bodies, cost the step and the physics sync nothing, and a strike on that creature lands on its body,
// as on a creature with no hitboxes at all. Walking back within range switches them on again the next tick.
//
// With no player in the world (a test, a server between connections) nothing is switched off.

// The budget is the record the conventions name (`hitboxBudget`, the engine's `sage:default_hitbox_budget`),
// which a game patches — `{ "type": "hitbox_budget", "id": "sage:default_hitbox_budget", "patch": true,
// "distance": 80 }` for a sniper's game — or points elsewhere. 0 turns either limit off; no budget named,
// every creature's hitboxes stay on.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
[Record("hitbox_budget", Plugin = "sage.gameplay.combat")]
public sealed class HitboxBudgetRecord
{
    [Property(Min = 0, Unit = "m", Tooltip = "A creature farther than this from every player has its hitboxes off (a strike there lands on its body); they come back on within it. 0 = no distance limit")]
    public float Distance = 50f;
    [Property(Min = 0, Tooltip = "At most this many creatures, nearest a player first, have their hitboxes on. 0 = no limit")]
    public int MaxCreatures = 32;
}

// On a creature with hitboxes (Hitboxes.Spawn): whether they are on. Never saved: the part puts the boxes
// back on a load, all on, and the budget decides again on the next tick.
[Transient]
[Component("sage:hitbox_set")]
internal struct HitboxSet : IComponent
{
    public bool On;
    public bool Want;
}

// PrePhysics, after the cleanup and before the physics sync, so a switch takes effect in the same tick.
// Allocation-free once warm; the boxes are walked only on a tick when some creature switched.
[System(Id, Phase.PrePhysics, After = new[] { HitboxCleanupSystem.Id }, Before = new[] { "?sage.physics.sync" })]
internal sealed class HitboxBudgetSystem : ISystem
{
    public const string Id = "sage.combat.hitbox_budget";

    // Hysteresis: a creature switched on stays on until it is this much beyond the distance, so one
    // pacing along the line does not switch every other tick.
    internal const float Hysteresis = 1.1f;

    private readonly RecordStore _records;
    private readonly Query<Transform> _players;
    private readonly Query<HitboxSet> _owners;
    private readonly Query<Hitbox> _boxes;
    private readonly List<Vector3> _eyes = new();
    private readonly List<Near> _near = new();
    private readonly List<Entity> _switch = new();

    public HitboxBudgetSystem(World world, RecordStore records)
    {
        _records = records;
        _players = world.Query<Transform>().AllTags(Tags.Get<PlayerControlled>());
        _owners = world.Query<HitboxSet>();
        _boxes = world.Query<Hitbox>();
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        var named = GameplayConventions.Of(_records).HitboxBudget.Id;
        HitboxBudgetRecord? budget = null;
        if (!named.IsEmpty && _records.TryGet(named, out HitboxBudgetRecord found)) budget = found;

        _eyes.Clear();
        foreach (var (_, entities) in _players.Chunks)
            for (int n = 0; n < entities.Length; n++) _eyes.Add(PositionOf(world, entities.EntityAt(n)));

        bool limited = budget != null && _eyes.Count > 0 && (budget.Distance > 0f || budget.MaxCreatures > 0);
        float distance = budget?.Distance ?? 0f;
        int most = budget?.MaxCreatures ?? 0;
        float near = distance * distance, far = near * Hysteresis * Hysteresis;

        // What each creature wants: within the distance (or, when on, its hysteresis band) ...
        _near.Clear();
        foreach (var (sets, entities) in _owners.Chunks)
        {
            var s = sets.Span;
            for (int n = 0; n < s.Length; n++)
            {
                if (!limited) { s[n].Want = true; continue; }
                float closest = float.MaxValue;
                var at = PositionOf(world, entities.EntityAt(n));
                foreach (var eye in _eyes) closest = MathF.Min(closest, Vector3.DistanceSquared(at, eye));
                s[n].Want = distance <= 0f || closest <= (s[n].On ? far : near);
                if (s[n].Want && most > 0) _near.Add(new Near(closest, entities.EntityAt(n)));
            }
        }
        // ... and among those, the nearest MaxCreatures.
        if (limited && most > 0 && _near.Count > most)
        {
            _near.Sort();
            for (int i = most; i < _near.Count; i++) world.Get<HitboxSet>(_near[i].Owner).Want = false;
        }

        bool changed = false;
        foreach (var (sets, _) in _owners.Chunks)
            foreach (ref var set in sets.Span)
                if (set.On != set.Want) { set.On = set.Want; changed = true; }
        if (!changed) return;

        // Some creature switched: every box whose state is not its owner's follows it.
        _switch.Clear();
        foreach (var (boxes, entities) in _boxes.Chunks)
        {
            var b = boxes.Span;
            for (int n = 0; n < b.Length; n++)
            {
                if (!world.TryGet<HitboxSet>(b[n].Owner, out var set)) continue;
                var box = entities.EntityAt(n);
                if (box.Tags.Has<ColliderOff>() == set.On) _switch.Add(box);
            }
        }
        foreach (var box in _switch)
        {
            bool on = world.Get<HitboxSet>(world.Get<Hitbox>(box).Owner).On;
            if (on) box.RemoveTag<ColliderOff>();
            else box.AddTag<ColliderOff>();
        }
        _switch.Clear();
    }

    private static Vector3 PositionOf(World world, Entity entity) =>
        world.TryGet<GlobalTransform>(entity, out var global) ? global.Current.Position : world.Get<Transform>(entity).LocalPosition;

    private readonly record struct Near(float DistanceSquared, Entity Owner) : IComparable<Near>
    {
        public int CompareTo(Near other)
        {
            int by = DistanceSquared.CompareTo(other.DistanceSquared);
            return by != 0 ? by : Owner.Id.CompareTo(other.Owner.Id);   // the same choice every run
        }
    }
}
