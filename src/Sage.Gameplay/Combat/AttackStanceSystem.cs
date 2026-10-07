#nullable enable
using System;

namespace Sage.Gameplay;

// Directional swings and guards (issue #359; the stance itself is Sage.Simulation's AttackStance): each
// tick, before the swing system, every fighter with a stance chooses its direction from its intent (a
// player's mouse gesture or movement keys; a Manual stance keeps what code or an AI set) and holds its
// guard while the Block button is held — not while dead, and not mid-swing. Its animator's graph reads
// both (`from: AttackDirection`, `from: Blocking`); MeleeCombatSystem keeps the direction a swing
// started with (Melee.Direction). A staggered fighter cannot guard (issue #390), and how long a guard
// has been up (Melee.Guarding) is what a block's parry window measures.
[System("sage.combat.stance", Phase.Gameplay, Before = new[] { "sage.combat.melee" })]
internal sealed class AttackStanceSystem : ISystem
{
    private readonly Query<PawnIntent, AttackStance> _fighters;
    private readonly ActionId _block;
    private Melee _none;   // stands in for a fighter with no Melee

    public AttackStanceSystem(World world, ActionRegistry actions)
    {
        _fighters = world.Query<PawnIntent, AttackStance>();
        _block = actions.Get(world.Conventions().Actions.Block);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float dt = ctx.Tick.Dt;
        var dead = world.Conventions().Dead;
        foreach (var (intents, stances, entities) in _fighters.Chunks)
        {
            var i = intents.Span;
            var s = stances.Span;
            for (int n = 0; n < s.Length; n++)
            {
                var entity = entities.EntityAt(n);
                AttackStances.Step(ref s[n], in i[n], dt);
                bool armed = world.Has<Melee>(entity);
                ref var melee = ref armed ? ref world.Get<Melee>(entity) : ref _none;
                bool busy = armed && (melee.Phase != MeleePhase.Ready || melee.Staggered > 0f);   // swinging or staggered (#390)
                bool was = s[n].Blocking;
                s[n].Blocking = i[n].Held.Has(_block) && !busy && !world.HasTag(entity, dead);
                // How long the guard has been up, for a parry's window (issue #390): 0 the tick it rises.
                if (armed) melee.Guarding = s[n].Blocking && was ? melee.Guarding + dt : 0f;
            }
        }
    }
}
