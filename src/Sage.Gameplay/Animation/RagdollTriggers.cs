#nullable enable
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// What sends a character ragdoll from gameplay (issue #246, docs/design/12 "As built (going ragdoll)"):
// a death, for a Ragdoll with OnDeath, thrown by the hit that killed it; and a damaging hit on one already
// down. A hit's impulse is the Ragdoll's HitImpulse along the hit's direction, at the point it landed,
// to the body nearest that point. The `Ragdoll` input and Ragdolls.Start are the engine's (Sage.Simulation).
// A death also marks a ragdoll's RagdollGetUp StayDown, so `getUpAfter` never gets a corpse up (issue #247).
//
// Gameplay phase, after the effects tick (where Died is raised) and before the death rules, so a game's
// OnEntityDied sees the body already falling (and may still destroy it).
[System(Id, Phase.Gameplay, After = new[] { "?sage.effects.tick" }, Before = new[] { "?sage.effects.deaths" })]
internal sealed class RagdollTriggerSystem : ISystem
{
    public const string Id = "sage.animation.ragdoll_triggers";

    // A hit counts for the death that follows it within this many ticks.
    private const long HitMemory = 30;

    private readonly EventReader<Died> _died;
    private readonly EventReader<Damaged> _damaged;
    private readonly Dictionary<int, LastHit> _hits = new();
    private readonly List<int> _stale = new();

    private readonly struct LastHit
    {
        public readonly Entity Target;
        public readonly Vector3 Point, Direction;
        public readonly long Tick;
        public LastHit(Entity target, Vector3 point, Vector3 direction, long tick)
        {
            Target = target;
            Point = point;
            Direction = direction;
            Tick = tick;
        }
    }

    public RagdollTriggerSystem(World world)
    {
        _died = world.Events.Reader<Died>(this);
        _damaged = world.Events.Reader<Damaged>(this);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        long tick = world.Tick;
        if (_damaged.HasPending)
            foreach (ref readonly var damaged in _damaged.Read())
            {
                var target = damaged.Hit.Target;
                if (target.IsNull || !world.IsAlive(target) || !world.TryGet<Ragdoll>(target, out var ragdoll)) continue;
                var direction = damaged.Hit.Direction;
                if (direction.LengthSquared() < 1e-8f) continue;
                direction = Vector3.Normalize(direction);
                if (ragdoll.Active)
                {
                    if (ragdoll.HitImpulse > 0f) Ragdolls.ApplyImpulse(world, target, direction * ragdoll.HitImpulse, damaged.Hit.Point);
                }
                else _hits[target.Id] = new LastHit(target, damaged.Hit.Point, direction, tick);
            }

        if (_died.HasPending)
            foreach (ref readonly var died in _died.Read())
            {
                var victim = died.Victim;
                if (victim.IsNull || !world.IsAlive(victim)) continue;
                // The dead stay down: getUpAfter no longer gets it up (issue #247).
                if (world.Has<RagdollGetUp>(victim)) world.Get<RagdollGetUp>(victim).StayDown = true;
                if (!world.TryGet<Ragdoll>(victim, out var ragdoll) || !ragdoll.OnDeath || ragdoll.Active) continue;
                if (_hits.TryGetValue(victim.Id, out var hit) && hit.Target == victim && tick - hit.Tick <= HitMemory && ragdoll.HitImpulse > 0f)
                    Ragdolls.Start(world, victim, hit.Direction * ragdoll.HitImpulse, hit.Point);
                else
                    Ragdolls.Start(world, victim);
                _hits.Remove(victim.Id);
            }

        if (_hits.Count == 0) return;
        _stale.Clear();
        foreach (var (id, hit) in _hits)
            if (tick - hit.Tick > HitMemory) _stale.Add(id);
        foreach (var id in _stale) _hits.Remove(id);
    }
}
