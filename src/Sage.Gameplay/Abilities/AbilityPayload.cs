#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// What an ability *does*, at a point (docs/design/16 §3.3, TODO F21).
//
// Extracted because two things now deliver it: an instant cast, which resolves where the aim reaches,
// and a projectile, which resolves where it arrives. "What a fireball does when it lands" written
// twice is how the two drift apart — one gets friendly-fire rules or a knockback and the other
// doesn't, and the difference shows up as a bug report about spells behaving differently depending on
// whether they travelled.
internal sealed class AbilityPayload
{
    private readonly IPhysicsWorld _space;
    private readonly List<Entity> _targets = new();
    private readonly Entity[] _nearby = new Entity[64];

    public AbilityPayload(World world) => _space = world.Resources.Get<IPhysicsWorld>();

    // Everything it caught, for the caller to draw or report.
    public IReadOnlyList<Entity> Targets => _targets;

    // Applies the ability at `point`. `direct` is the one thing it hit, if it hit something — a burst
    // ignores it and takes everything near instead. `location` is the hit_location of the hitbox it struck
    // (issue #139), if it struck one: the damage lands there on `direct`, as an attack's bolt does, and a
    // burst's other targets stay on the body. Returns how many it affected.
    public int Deliver(World world, Entity caster, RecordId abilityId, AbilityRecord record, Vector3 point, Entity direct,
        RecordId location = default)
    {
        _targets.Clear();

        // Who it lands on is the delivery's to say (issue #28): the caster, a burst, or what it struck —
        // and a bolt that strikes an ally fizzles rather than burning it. Without that rule a firebug's
        // ember bolt hurt whatever wandered into its flight path, which was visible in the Sandbox the
        // day factions arrived.
        if (AbilityDeliveries.Of(world, record) is { } delivery)
        {
            var landing = new AbilityLanding
            {
                Payload = this, World = world, Caster = caster, Record = record, Point = point, Struck = direct,
            };
            delivery.Gather(ref landing);
        }

        foreach (var target in _targets)
        {
            if (!world.IsAlive(target)) continue;

            // Damage first, through the one pipeline (16 §3.2): resistance, then an effect on health,
            // then a Damaged event. Its riders go with it, so a target that shrugs off the whole thing
            // shrugs off the burning too — the way a poisoned blade already works.
            if (record.Damage > 0f)
            {
                var damage = new DamageInfo(caster, target, record.DamageType, record.Damage, point, Direction(world, target, point));
                if (target == direct && !location.IsEmpty) damage = damage with { Location = location };
                Combat.ApplyDamage(world, damage, record.Effects);
            }
            else
                foreach (var effect in record.Effects)
                    Effects.Apply(world, target, effect, caster, record.Magnitude);
        }

        world.Events.Send(new AbilityCast(caster, abilityId, point, _targets.Count));
        foreach (var cue in record.Cues) world.Events.Send(new CueTriggered(cue, caster, point));
        return _targets.Count;
    }

    // Everything solid within `radius`. Who a spell is *allowed* to burn is a rules question
    // (factions, F24), not a physics one, so this gathers all of it and the caster is the only
    // special case — and only when the ability says so.
    //
    // The space offers a box, and the box is the broad phase: the distance check is what makes the
    // burst round. `OverlapBox` can also report things whose shapes don't quite touch (10 §4), which
    // matters less for a blast than it would for a sword.
    internal void AddTarget(Entity entity)
    {
        if (!entity.IsNull && !_targets.Contains(entity)) _targets.Add(entity);
    }

    internal void Gather(World world, Vector3 point, float radius, Entity caster, bool includeCaster)
    {
        int count = _space.OverlapBox(point, new Vector3(radius), _nearby, LayerMask.All);
        for (int i = 0; i < count; i++)
        {
            var entity = _nearby[i];
            if (!CanBeAffected(world, entity) || (!includeCaster && entity == caster)) continue;
            // A firebug's burst does not burn other firebugs (16 §3.5, F24). The player's does burn
            // whoever is standing there: aiming is their business, and their name pays for it.
            if (!Factions.MayHurt(world, caster, entity)) continue;
            if (!world.TryGet<Transform>(entity, out var transform)) continue;
            if (Vector3.Distance(transform.LocalPosition, point) > radius + 0.5f) continue;   // +half a body
            if (!_targets.Contains(entity)) _targets.Add(entity);
        }
    }

    // A blast lands on the world, and most of the world is scenery. Only things that can *hold* an
    // effect are targets: a fireball bursting against a tree is a normal Tuesday, not a mis-set-up
    // entity, and warning about it once per trunk would bury the log.
    public static bool CanBeAffected(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.Has<Attributes>(entity);

    // Away from the burst, so a knockback (later) pushes outward. Never a zero vector, which
    // Normalize would turn into NaN and quietly poison a transform.
    private static Vector3 Direction(World world, Entity target, Vector3 point)
    {
        var to = world.TryGet<Transform>(target, out var transform) ? transform.LocalPosition - point : Vector3.Zero;
        return to.LengthSquared() > 1e-6f ? Vector3.Normalize(to) : Vector3.UnitY;
    }
}
