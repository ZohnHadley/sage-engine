#nullable enable
using Friflo.Engine.ECS;

namespace Sage.Gameplay;

// When a cast is allowed (docs/design/16 §3.3, TODO F21).
//
// Extracted for the same reason `AbilityPayload` was: two things now need the answer. `AbilitySystem`
// asks it to *start* a cast, and an AI asks it to *decide whether to try* (§3.4). Written twice, the
// creature's idea of "ready" and the cast system's would drift — a creature would walk into range and
// wind up a spell it cannot pay for, every think, for ever.
//
// It is also what a HUD should ask to grey out a spell, rather than reading mana itself.
public static class AbilityRules
{
    // The gates, in the order a player would think of them: do I know it, am I busy, is it ready, am
    // I allowed, can I afford it. `why` names the first one that failed, so a refusal can say which.
    public static bool CanCast(World world, RecordStore records, Entity caster, RecordId ability,
                               in Abilities abilities, out AbilityRecord record, out CastRefusal why)
    {
        record = null!;
        if (!abilities.Casting.IsEmpty) { why = CastRefusal.AlreadyCasting; return false; }
        if (abilities.Known == null || !abilities.Known.Contains(ability)) { why = CastRefusal.NotKnown; return false; }
        if (!records.TryGet(ability, out record)) { why = CastRefusal.NotKnown; return false; }

        if (OnCooldown(world, records, caster, record)) { why = CastRefusal.OnCooldown; return false; }
        if (!TagsAllow(world, caster, record)) { why = CastRefusal.Blocked; return false; }
        if (!CanAfford(world, caster, record)) { why = CastRefusal.TooExpensive; return false; }

        why = CastRefusal.Unknown;
        return true;
    }

    // For a caller that does not already hold the component — an AI task, a HUD, a console command.
    public static bool CanCast(World world, Entity caster, RecordId ability, out CastRefusal why)
    {
        if (!world.TryGet<Abilities>(caster, out var abilities)) { why = CastRefusal.NotKnown; return false; }
        return CanCast(world, world.Records(), caster, ability, in abilities, out _, out why);
    }

    // A refusal in words, for anything that shows one to a player: a greyed-out row's tooltip, a HUD
    // line, a console command. The enum is what code branches on; this is what a person reads, and
    // having one copy of it means a screen cannot invent its own vocabulary for "no".
    public static string Explain(CastRefusal why) => why switch
    {
        CastRefusal.NotKnown => "you do not know it",
        CastRefusal.OnCooldown => "not ready yet",
        CastRefusal.TooExpensive => "not enough to cast it with",
        CastRefusal.Blocked => "something is stopping you",
        CastRefusal.AlreadyCasting => "you are already casting",
        _ => "you cannot cast it",
    };

    // An ability is on cooldown when the caster already has a tag its cooldown effect grants. No
    // second clock, and dispelling the tag makes it ready again (16 §3.3).
    public static bool OnCooldown(World world, RecordStore records, Entity caster, AbilityRecord record)
    {
        if (record.Cooldown.IsEmpty || !records.TryGet(record.Cooldown, out EffectRecord cooldown)) return false;
        foreach (var tag in cooldown.GrantTags)
            if (world.HasTag(caster, tag)) return true;
        return false;
    }

    public static bool TagsAllow(World world, Entity caster, AbilityRecord record)
    {
        foreach (var tag in record.RequireTags) if (!world.HasTag(caster, tag)) return false;
        foreach (var tag in record.BlockTags) if (world.HasTag(caster, tag)) return false;
        return true;
    }

    public static bool CanAfford(World world, Entity caster, AbilityRecord record) =>
        record.CostAttribute.IsEmpty || record.Cost <= 0f ||
        world.Attribute(caster, record.CostAttribute) >= record.Cost;
}
