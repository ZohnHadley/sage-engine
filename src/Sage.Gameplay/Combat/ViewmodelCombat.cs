#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Gameplay;

// The first-person arms follow the fight (issue #121, docs/design/12 "As built (first-person arms)").
//
// Gameplay phase, after the swing: for every pawn with a Melee and a camera with a Viewmodel (the
// player's), the camera shows the `arms` of the attack in the pawn's hands — bare hands' when a weapon
// comes off — and the arms' anim_graph hears about the fight: a swing that started this tick sets its
// trigger — the attack's own `trigger`, else the conventions' `animations.attackTrigger`, the one the
// fighter's own animator gets (issue #119) — and the Reload button its `reload` trigger (`animations.reload`).
// The arms only play the reload; the rounds move in ReloadSystem (issue #135, Ammunition.cs). A graph without the trigger ignores it.
// The arms' clip events (`mag_out`, `mag_in`, `hit`) are raised by the animator like any other's
// (docs/design/12 "As built (animation events)"), on the arms entity.
//
// Allocation-free: two short loops over a handful of entities.
[System(Id, Phase.Gameplay, After = new[] { "sage.combat.melee" })]
internal sealed class ViewmodelCombatSystem : ISystem
{
    public const string Id = "sage.combat.viewmodel";

    private readonly World _world;
    private readonly RecordStore _records;
    private readonly Query<PawnIntent, Melee> _fighters;
    private readonly Query<Viewmodel> _cameras;
    private readonly ActionId _reload;

    public ViewmodelCombatSystem(World world, RecordStore records, ActionRegistry actions)
    {
        _world = world;
        _records = records;
        _fighters = world.Query<PawnIntent, Melee>();
        _cameras = world.Query<Viewmodel>();
        _reload = actions.Get(world.Conventions().Actions.Reload);
    }

    public void Run(in SystemContext ctx)
    {
        var conventions = _world.Conventions();
        foreach (var (intents, melees, entities) in _fighters.Chunks)
        {
            var intent = intents.Span;
            var melee = melees.Span;
            for (int n = 0; n < melee.Length; n++)
            {
                var camera = Viewmodels.CameraOf(_world, _cameras, entities.EntityAt(n));
                if (camera.IsNull) continue;

                var attackId = melee[n].Attack.IsEmpty ? conventions.Attack.Id : melee[n].Attack;
                AttackRecord? attack = null;
                if (!attackId.IsEmpty && _records.TryGet(attackId, out AttackRecord found)) attack = found;
                var arms = attack?.Arms.Id ?? default;
                ref var viewmodel = ref _world.Get<Viewmodel>(camera);
                if (viewmodel.Record != arms) viewmodel.Record = arms;   // ViewmodelSystem respawns in Animation

                var shown = Viewmodels.ArmsOf(_world, camera);
                if (shown.IsNull || !_world.Has<Animator>(shown)) continue;
                // The swing began this tick: MeleeCombatSystem (just before) moved it to Windup at 0.
                if (melee[n].Phase == MeleePhase.Windup && melee[n].Timer == 0f && !melee[n].Swung)
                    Animators.SetTrigger(_world, shown, string.IsNullOrEmpty(attack?.Trigger) ? conventions.Animations.AttackTrigger : attack.Trigger);
                if (_reload.IsValid && intent[n].Pressed.Has(_reload))
                    Animators.SetTrigger(_world, shown, conventions.Animations.Reload);
            }
        }
    }
}
