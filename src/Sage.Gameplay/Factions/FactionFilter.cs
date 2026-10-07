#nullable enable
using System;

namespace Sage.Gameplay;

// Who a blow, a blast or a blessing may reach, by faction (issue #390; docs/design/16 §3.5). One filter
// for every rule of the kind "friendly fire": an attack's `friendlyFire`, and — the reason it is a value
// of its own and not a flag on combat — an area or projectile's targets and a heal's or a buff's (#393).
// Each asks the same question, what `source` thinks of `target` right now (Factions.Toward), so a
// change of standing changes who a fireball spares without the fireball knowing.
public enum FactionFilter
{
    // The engine's rule (Factions.MayHurt): a player-controlled source reaches anyone — it is their
    // reputation that pays for it — and anybody else spares its allies.
    Default,
    // Everyone, allies and the source itself included: a grenade that does not care whose it was.
    Anyone,
    // All but allies, for players too: a companion stands in the line of fire unhurt. The source is its
    // own ally, so it is spared.
    NotAllies,
    // Only those the source is hostile to: neutrals and allies alike are spared.
    Hostile,
    // Only its allies, itself included: a heal, a ward, a war cry.
    Allies,
}

public static class FactionFilters
{
    // Whether `filter` lets `source` reach `target`. A source or target that is gone (a thrower killed
    // before its bolt landed) is nobody's ally: the damage filters let it through and Allies does not.
    public static bool Allows(World world, Entity source, Entity target, FactionFilter filter)
    {
        ArgumentNullException.ThrowIfNull(world);
        switch (filter)
        {
            case FactionFilter.Anyone: return true;
            case FactionFilter.Default: return Factions.MayHurt(world, source, target);
        }
        if (source == target) return filter == FactionFilter.Allies;
        if (source.IsNull || target.IsNull || !world.IsAlive(source) || !world.IsAlive(target))
            return filter != FactionFilter.Allies;
        var stance = Factions.Toward(world, source, target);
        return filter switch
        {
            FactionFilter.NotAllies => stance != Stance.Ally,
            FactionFilter.Hostile => stance == Stance.Hostile,
            FactionFilter.Allies => stance == Stance.Ally,
            _ => true,
        };
    }
}
