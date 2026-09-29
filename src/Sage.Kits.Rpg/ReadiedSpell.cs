#nullable enable
using System.Collections.Generic;

namespace Sage.Kits.Rpg;

// The readied spell (docs/design/16 §3.3, issue #27): Daggerfall's model of casting, where you pick a
// spell from your book once and a button throws it until you pick another. The base only *uses*
// abilities (world.Cast, from an AI task, the console or anything else); readying one and firing it
// from a button is this kit's.
//
// What is readied is `Abilities.Selected`, which stays in the base component because saves already
// carry it there (sage:abilities). The base never reads or writes it.
public static class ReadiedSpell
{
    // What the Cast button fires: the one readied, or — until something is readied, or when the one
    // readied has been forgotten — the first one learned.
    public static RecordId Readied(this World world, Entity entity) =>
        world.TryGet<Abilities>(entity, out var abilities) ? Of(in abilities) : default;

    internal static RecordId Of(in Abilities abilities)
    {
        if (abilities.Known is not { Count: > 0 } known) return default;
        return !abilities.Selected.IsEmpty && known.Contains(abilities.Selected) ? abilities.Selected : known[0];
    }

    // Readies a spell: what the Cast button fires and what a spellbook screen ticks. Refuses one it does
    // not know, so a stale screen cannot ready nothing.
    public static bool Ready(this World world, Entity entity, RecordId ability)
    {
        if (!world.Knows(entity, ability)) return false;
        world.Get<Abilities>(entity).Selected = ability;
        return true;
    }
}

// Gameplay phase, before the cast system: pressing the Cast button asks for the readied spell, and the
// base's AbilitySystem takes it from there — the same queue `world.Cast` fills, so the gates, the
// wind-up and the refusals are the ones every other cast gets.
[System("rpg.readied_spell", Phase.Gameplay, Before = new[] { "sage.abilities.cast" })]
internal sealed class ReadiedSpellSystem : ISystem
{
    private readonly Query<PawnIntent, Abilities> _casters;
    private readonly ActionRegistry _actions;
    private string _castName = "";
    private ActionId _cast = ActionId.None;

    public ReadiedSpellSystem(World world, ActionRegistry actions)
    {
        _casters = world.Query<PawnIntent, Abilities>();
        _actions = actions;
    }

    public void Run(in SystemContext ctx)
    {
        // Asked each run, so a hot-reloaded rpg_conventions takes effect; resolved only when it changed.
        string name = RpgConventions.Of(ctx.World).CastAction;
        if (name != _castName)
        {
            _castName = name;
            _cast = _actions.Get(name);   // None for "" or a name nothing registered (a load error already)
        }
        if (!_cast.IsValid) return;

        foreach (var (intents, abilities, _) in _casters.Chunks)
        {
            var i = intents.Span;
            var a = abilities.Span;
            for (int n = 0; n < a.Length; n++)
            {
                if (!i[n].Pressed.Has(_cast) || !a[n].Queued.IsEmpty) continue;
                var readied = ReadiedSpell.Of(in a[n]);
                if (!readied.IsEmpty) a[n].Queued = readied;
            }
        }
    }
}
