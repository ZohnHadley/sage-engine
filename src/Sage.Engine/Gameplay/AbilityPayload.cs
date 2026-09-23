#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// What an ability *does*, at a point (docs/design/16 §3.3, TODO F21).
//
// Extracted because two things now deliver it: an instant cast, which resolves where the aim reaches,
// and a projectile, which resolves where it arrives. "What a fireball does when it lands" written
// twice is how the two drift apart — one gets friendly-fire rules or a knockback and the other
// doesn't, and the difference shows up as a bug report about spells behaving differently depending on
// whether they travelled.
internal sealed class AbilityPayload
{
    private readonly PhysicsSpace _space;
    private readonly List<Entity> _targets = new();
    private readonly Entity[] _nearby = new Entity[64];

    public AbilityPayload(World world) => _space = world.Resources.Get<PhysicsSpace>();

    // Everything it caught, for the caller to draw or report.
    public IReadOnlyList<Entity> Targets => _targets;

    // Applies the ability at `point`. `direct` is the one thing it hit, if it hit something — a burst
    // ignores it and takes everything near instead. Returns how many it affected.
    public int Deliver(World world, Entity caster, RecordId abilityId, AbilityRecord record, Vector3 point, Entity direct)
    {
        _targets.Clear();

        // Who it lands on, all three cases in one place. "On me" is not a burst of radius zero and not
        // a touch that happened to hit the caster: it is its own answer, and putting it here is what
        // stops a self-targeted spell quietly skipping the damage pipeline.
        if (record.Targeting == AbilityTargeting.Self) _targets.Add(caster);
        else if (record.Radius > 0f) Gather(world, point, record.Radius, caster, includeCaster: record.Targeting == AbilityTargeting.Area);
        else if (CanBeAffected(world, direct) && direct != caster) _targets.Add(direct);

        foreach (var target in _targets)
        {
            if (!world.IsAlive(target)) continue;

            // Damage first, through the one pipeline (16 §3.2): resistance, then an effect on health,
            // then a Damaged event. Its riders go with it, so a target that shrugs off the whole thing
            // shrugs off the burning too — the way a poisoned blade already works.
            if (record.Damage > 0f)
                Combat.ApplyDamage(world, new DamageInfo(caster, target, record.DamageType, record.Damage,
                    point, Direction(world, target, point)), record.Effects);
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
    private void Gather(World world, Vector3 point, float radius, Entity caster, bool includeCaster)
    {
        int count = _space.OverlapBox(point, new Vector3(radius), _nearby, LayerMask.All);
        for (int i = 0; i < count; i++)
        {
            var entity = _nearby[i];
            if (!CanBeAffected(world, entity) || (!includeCaster && entity == caster)) continue;
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
