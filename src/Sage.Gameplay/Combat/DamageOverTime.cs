#nullable enable
using System.Numerics;

namespace Sage.Gameplay;

// Damage over time through the damage pipeline (issue #390). A periodic effect's modifiers change health
// directly, so a poison written that way ignores `poison_resist`, the `god` tag sees it only as an
// effect, and a death by it names nobody. The `damage` execution is a hit instead, run on each period:
//
//   { "type": "effect", "id": "burning", "duration": "Timed", "time": 5, "period": 1,
//     "executions": [{ "execution": "damage", "amount": 4, "damageType": "fire" }] }
//
// Each pulse is Combat.ApplyDamage from the effect's source: the damage type's resist, a Damaged event
// (with the source as the attacker, so a kill by burning is the burner's) and the death seam, scaled by
// the application's magnitude and the effect's stacks.
[EffectExecution("damage", Plugin = "sage.gameplay.combat")]
internal sealed class DamageExecution : IEffectExecution
{
    [Property(Min = 0, Tooltip = "Damage each application (each period of a periodic effect) deals, times the magnitude and the stacks")]
    public float Amount = 1f;
    [Property(Tooltip = "Its damage type, whose resist stands up to it; empty = the game's default (gameplay_conventions)")]
    public RecordRef<DamageTypeRecord> DamageType;

    // A damage type whose own effect deals this damage again would never end: one pulse at a time.
    private bool _dealing;

    public void Execute(in EffectExecution e)
    {
        if (_dealing || !(Amount > 0f) || !e.World.IsAlive(e.Target)) return;
        var world = e.World;
        var point = world.TryGet<Transform>(e.Target, out var at) ? at.LocalPosition : default;
        var source = !e.Source.IsNull && world.IsAlive(e.Source) ? e.Source : default;
        float amount = Amount * e.Magnitude * System.Math.Max(e.Stacks, 1);
        _dealing = true;
        try
        {
            Combat.ApplyDamage(world, new DamageInfo(source, e.Target, DamageType.Id, amount, point, -Vector3.UnitY));
        }
        finally { _dealing = false; }
    }
}
