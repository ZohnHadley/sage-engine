#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Healing and buffing, for a thinking creature (issue #393; docs/design/16 §3.4). ChooseSpell asks this
// what an ability is for and, for a heal or a buff, who needs it: the caster itself when it lands on the
// caster, else the nearest ally in reach whom the ability's `affects` lets it reach. Kept out of the think
// so the choice there stays one loop: support first (a heal, then a buff), then the dearest attack.
internal static class AISupport
{
    // How much a use outranks another when ChooseSpell weighs what it can cast.
    public const int AttackRank = 0, BuffRank = 1, HealRank = 2;

    // What a creature would cast it for: `Auto` is an attack unless it lands on the caster or reaches only
    // allies, which is the choice the think made before #393 (it never cast those).
    public static AbilityAIUse UseOf(World world, AbilityRecord record)
    {
        if (record.AIUse != AbilityAIUse.Auto) return record.AIUse;
        return AbilityDeliveries.OnCaster(world, record) || record.Affects == FactionFilter.Allies ? AbilityAIUse.Never : AbilityAIUse.Attack;
    }

    // Who should have it, or null: for a heal someone whose health is below `aiHealBelow` (the lowest first),
    // for a buff someone missing one of its effects (the nearest first). `candidates` is broad-phase scratch.
    public static Entity Patient(World world, RecordStore records, Entity self, Vector3 position, AbilityRecord record,
                                 AbilityAIUse use, Entity[] candidates)
    {
        if (AbilityDeliveries.OnCaster(world, record)) return Needs(world, records, self, record, use, out _) ? self : default;

        float reach = record.Range * 0.9f;   // as an attack's: a little short of the full range
        Entity best = default;
        float bestScore = float.MaxValue;
        int found = world.Resources.Get<IPhysicsWorld>().OverlapBox(position, new Vector3(reach), candidates);
        for (int i = 0; i < found; i++)
        {
            var other = candidates[i];
            if (other == self || other.IsNull || !world.IsAlive(other)) continue;
            // An ally, and one the ability would land on: a heal whose `affects` spares allies is no heal.
            if (!FactionFilters.Allows(world, self, other, FactionFilter.Allies)) continue;
            if (!FactionFilters.Allows(world, self, other, record.Affects)) continue;
            if (!world.TryGet<Transform>(other, out var at)) continue;
            float distance = Vector3.Distance(position, at.LocalPosition);
            if (distance > reach) continue;
            if (!Needs(world, records, other, record, use, out float lack)) continue;
            float score = use == AbilityAIUse.Heal ? -lack : distance;
            if (score >= bestScore) continue;
            best = other;
            bestScore = score;
        }
        return best;
    }

    // Whether `who` needs it now; `lack` is how far below full a heal's patient is.
    private static bool Needs(World world, RecordStore records, Entity who, AbilityRecord record, AbilityAIUse use, out float lack)
    {
        lack = 0f;
        if (!world.Has<Attributes>(who)) return false;
        var conventions = world.Conventions();
        if (world.HasTag(who, conventions.Dead)) return false;
        if (use == AbilityAIUse.Buff)
        {
            foreach (var effect in record.Effects)
                if (!effect.IsEmpty && !Effects.IsActive(world, who, effect.Id)) return true;
            return false;
        }
        if (use != AbilityAIUse.Heal || conventions.Health.IsEmpty) return false;
        if (!records.TryGet(conventions.Health.Id, out AttributeRecord health)) return false;
        // The attribute's ceiling, or what it starts at when it has none.
        float full = health.Max < float.MaxValue && health.Max > 0f ? health.Max : health.Start;
        if (full <= 0f) return false;
        float fraction = world.Attribute(who, conventions.Health.Id) / full;
        lack = 1f - fraction;
        return fraction < Math.Clamp(record.AIHealBelow, 0f, 1f);
    }

    // A heal or a buff thrown at somebody must be able to land on an ally (issue #393): with `affects`
    // Default a creature's spell spares its allies, so the creature would never find anyone to cast it on.
    public static void Check(AbilityRecord ability, RecordCheck check)
    {
        if (ability.AIUse is not (AbilityAIUse.Heal or AbilityAIUse.Buff)) return;
        if (ability.AIHealBelow is < 0f or > 1f)
            check.Error(nameof(AbilityRecord.AIHealBelow), "must be between 0 and 1: a fraction of the health attribute's maximum");
        if (ability.AIUse == AbilityAIUse.Buff && ability.Effects.Count == 0)
            check.Error(nameof(AbilityRecord.AIUse), "a Buff with no effects: a creature can never tell who lacks it, so it never casts it");
        bool onCaster = AbilityDeliveries.NameOf(ability) == "self";
        if (!onCaster && ability.Affects is not (FactionFilter.Allies or FactionFilter.Anyone))
            check.Error(nameof(AbilityRecord.Affects),
                $"a {ability.AIUse} cast on others must say \"affects\": \"Allies\" (or \"Anyone\"); otherwise it spares the allies it is meant for");
    }
}
