#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Combat (docs/design/16 §3.2, TODO F20). One pipeline for every hit, whoever threw it:
//
//   a swing or a spell  ->  Combat.ApplyDamage  ->  the damage type's resistance attribute
//                       ->  an effect on the victim's health (16 §3.3)  ->  CombatEvents
//
// Nothing subtracts health directly. Damage is an *effect* with a magnitude, so resistances, the
// `god` tag, damage over time and saves all keep working through the single path F18 built, and a
// future server runs exactly this code.

// What a hit is made of. `Point` and `Direction` are where it landed and which way it was going —
// cues, knockback and hit reactions (F21) read them; v1 only logs them.
public readonly record struct DamageInfo(
    Entity Attacker, Entity Target, RecordId Type, float Amount, Vector3 Point, Vector3 Direction)
{
    public float Applied { get; init; }   // after resistance and tag gating: what health actually lost
}

// A kind of damage and what stands up to it (16 §3.2). Games add their own: frost, poison, falling.
[Record("damage_type")]
public sealed class DamageTypeRecord
{
    public RecordId Resist;   // attribute read as a percentage, 0..95, that reduces this
    public RecordId Effect;   // the effect applied to the victim, scaled by the damage
                              // (element tags and cue ids arrive with cues, F21: a field nothing
                              //  reads is a promise the engine isn't keeping, review #50)

    public static readonly RecordId Physical = new("sage", "physical");
}

// One swing: how far it reaches, how forgiving it is, what it costs in time (16 §3.2). Weapons hand
// the wielder one of these when inventory arrives (F19); until then a character carries its own.
[Record("attack")]
public sealed class AttackRecord
{
    public float Damage = 10f;
    public RecordId DamageType;             // empty = sage:physical
    public float Reach = 2f;                // metres, from the attacker's eye
    public float Radius = 0.35f;            // the swing's thickness: how forgiving it is
    public float ArcDegrees = 120f;         // how far off-centre a target may be
    public float WindupTime = 0.25f;        // swing -> hit, unless the animation says when (12 §3)
    public float RecoverTime = 0.2f;        // hit -> able to do anything else
    public float Cooldown = 0.5f;           // and how long before the next swing
    public string Animation = "attack";     // clip to play; its "hit" event lands the blow
    public List<RecordId> Effects = new();  // applied to the victim on a hit, unscaled (poison, burning)

    public static readonly RecordId Default = new("sage", "default_attack");
}

// The tick's hits, for anything that reacts to them: the death seam's "who killed me", the Sandbox's
// log, AI's "heavy damage" interrupt and cues (F21). Proper game events come with the bus (04).
public sealed class CombatEvents
{
    public readonly List<DamageInfo> Damage = new();

    public void Clear() => Damage.Clear();

    // Who last hurt this entity this tick. The death seam uses it so a game's rules know who gets the
    // credit without every damage path having to remember it.
    public Entity LastAttackerOf(Entity victim)
    {
        for (int i = Damage.Count - 1; i >= 0; i--)
            if (Damage[i].Target == victim && Damage[i].Applied > 0) return Damage[i].Attacker;
        return default;
    }
}

public static class Combat
{
    // Runs one hit through the pipeline and returns the health it actually cost. Zero means nothing
    // happened: blocked by a tag (`god`), already dead, or resisted to nothing.
    //
    // `alsoApply` are the effects that ride along with a landed hit — a poisoned blade, a burning
    // brand. They are applied as written (no magnitude) and only if the damage itself got through,
    // so `god` stops the poison as well as the cut.
    public static float ApplyDamage(World world, in DamageInfo hit, List<RecordId>? alsoApply = null)
    {
        if (!world.IsAlive(hit.Target) || hit.Amount <= 0f) return 0f;

        var records = world.Resources.Get<RecordStore>();
        var typeId = hit.Type.IsEmpty ? DamageTypeRecord.Physical : hit.Type;
        if (!records.TryGet(typeId, out DamageTypeRecord type))
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"damage_type:{typeId}", $"No damage_type record {typeId}");
            return 0f;
        }
        if (type.Effect.IsEmpty)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"damage_effect:{typeId}", $"damage_type {typeId} has no effect, so it can't hurt anything");
            return 0f;
        }

        float amount = Mitigate(world, hit.Target, type, hit.Amount);
        float before = world.Attribute(hit.Target, AttributeRecord.Health);

        // The health change is an effect like any other, so tags gate it and saves see it (16 §3.3).
        if (!Effects.Apply(world, hit.Target, type.Effect, hit.Attacker, amount)) return 0f;

        float applied = MathF.Max(0f, before - world.Attribute(hit.Target, AttributeRecord.Health));
        if (alsoApply != null)
            foreach (var effect in alsoApply) Effects.Apply(world, hit.Target, effect, hit.Attacker);

        world.Resources.Get<CombatEvents>().Damage.Add(hit with { Applied = applied });
        return applied;
    }

    // Resistance is a percentage attribute (armour, fire_resist…), clamped so nothing is ever immune
    // by arithmetic: a resistance record's own Max decides the ceiling (95 for the engine's armour).
    private static float Mitigate(World world, Entity target, DamageTypeRecord type, float amount)
    {
        if (type.Resist.IsEmpty) return amount;
        float resist = Math.Clamp(world.Attribute(target, type.Resist), 0f, 95f);
        return amount * (1f - resist * 0.01f);
    }
}

// How far through a swing a fighter is. One swing is windup (the blow is coming), the hit, then
// recovery (committed, can't swing again), then a cooldown.
public enum MeleePhase { Ready, Windup, Recover }

// A character that can swing something (16 §3.2). The player's controller and an AI's MeleeAttack
// task both just press the Attack action; this component and MeleeCombatSystem are the whole swing.
public struct Melee : IComponent
{
    public RecordId Attack;     // empty = sage:default_attack
    public MeleePhase Phase;
    public float Timer;         // seconds in the current phase
    public float Cooldown;      // seconds until the next swing may start
    public bool Swung;          // this swing has landed (or missed): don't resolve it twice

    public static Melee With(RecordId attack) => new() { Attack = attack };
}

// Gameplay phase, before effects tick: turns "the Attack action was pressed" into a hit, for players
// and creatures alike (16 §3.1 — the payoff of the controller/pawn split is that combat never asks
// which one it is dealing with).
public sealed class MeleeCombatSystem : ISystem
{
    private readonly ArchetypeQuery<Transform, PawnIntent, CharacterController, Melee> _fighters;
    private readonly RecordStore _records;
    private readonly PhysicsSpace _space;
    private readonly CombatEvents _events;
    private readonly AnimationEvents _animation;
    private readonly ActionId _attack;
    private readonly List<(DamageInfo Hit, AttackRecord Attack)> _pending = new();   // dealt after the
                        // loop: applying an effect touches another entity's components

    public MeleeCombatSystem(World world, RecordStore records, ActionRegistry actions)
    {
        _fighters = world.Query<Transform, PawnIntent, CharacterController, Melee>();
        _records = records;
        _space = world.Resources.Get<PhysicsSpace>();
        _events = world.Resources.Get<CombatEvents>();
        _animation = world.Resources.Get<AnimationEvents>();
        _attack = actions.Get("Attack");
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float dt = ctx.Tick.Dt;

        // The tick's hits start empty here. This is the only thing that deals damage inside a tick in
        // v1, and everything that reads the list (the death seam, the game's log) runs later in the
        // tick. Anything else that damages (abilities, F21) must run *after* this system, or the clear
        // moves to its own system at the start of the Commands phase.
        _events.Clear();
        _pending.Clear();

        foreach (var (transforms, intents, characters, melees, entities) in _fighters.Chunks)
        {
            var t = transforms.Span;
            var i = intents.Span;
            var c = characters.Span;
            var m = melees.Span;
            for (int n = 0; n < m.Length; n++)
            {
                var entity = entities.EntityAt(n);
                var attack = AttackFor(ref m[n]);
                if (attack == null) continue;

                if (m[n].Cooldown > 0f) m[n].Cooldown = MathF.Max(0f, m[n].Cooldown - dt);

                switch (m[n].Phase)
                {
                    case MeleePhase.Ready:
                        if (i[n].Pressed.Has(_attack) && m[n].Cooldown <= 0f && !world.HasTag(entity, TagRecord.Dead))
                        {
                            m[n].Phase = MeleePhase.Windup;
                            m[n].Timer = 0f;
                            m[n].Swung = false;
                            PlayAnimation(world, entity, attack);
                        }
                        break;

                    case MeleePhase.Windup:
                        m[n].Timer += dt;
                        // Killed mid-swing: the blow dies with the fighter rather than landing from
                        // a corpse.
                        if (world.HasTag(entity, TagRecord.Dead))
                        {
                            m[n].Phase = MeleePhase.Ready;
                            m[n].Timer = 0f;
                            m[n].Swung = true;
                            break;
                        }
                        if (!Lands(world, entity, attack, m[n].Timer)) break;
                        _pending.Add((Resolve(world, entity, in t[n], in i[n], in c[n], attack), attack));
                        m[n].Swung = true;
                        m[n].Timer = 0f;
                        m[n].Phase = MeleePhase.Recover;
                        break;

                    case MeleePhase.Recover:
                        m[n].Timer += dt;
                        if (m[n].Timer < attack.RecoverTime) break;
                        m[n].Phase = MeleePhase.Ready;
                        m[n].Timer = 0f;
                        m[n].Cooldown = attack.Cooldown;
                        break;
                }
            }
        }

        foreach (var (hit, attack) in _pending)
            if (!hit.Target.IsNull) Combat.ApplyDamage(world, hit, attack.Effects);
    }

    private AttackRecord? AttackFor(ref Melee melee)
    {
        var id = melee.Attack.IsEmpty ? AttackRecord.Default : melee.Attack;
        if (_records.TryGet(id, out AttackRecord record)) return record;
        Log.Once(LogCat.Gameplay, LogLevel.Error, $"attack:{id}", $"No attack record {id}: nothing can swing it");
        return null;
    }

    // When the blow lands: on the animation's "hit" event if the clip has one, otherwise on the
    // record's windup time. The event is raised in the Animation phase, which runs after this one, so
    // a sprite's hit lands one tick (16 ms) after the frame that shows it — not worth a phase shuffle.
    private bool Lands(World world, Entity entity, AttackRecord attack, float timer) =>
        _animation.Fired(entity, "hit") || timer >= attack.WindupTime;

    // The swing itself: a sphere swept along the attacker's aim (10 §4), first thing it touches that
    // can be hurt. One target per swing in v1; cleaving through several is a later flag.
    private DamageInfo Resolve(World world, Entity attacker, in Transform transform, in PawnIntent intent,
                               in CharacterController character, AttackRecord attack)
    {
        var profile = _records.TryGet(character.Profile.IsEmpty ? MovementProfileRecord.Default : character.Profile,
                                      out MovementProfileRecord found) ? found : MovementProfileRecord.Fallback;
        float height = character.Height > 0f ? character.Height : profile.StandHeight;
        Vector3 eye = transform.LocalPosition + Vector3.UnitY * MathF.Max(height + profile.EyeOffset, 0.2f);
        Vector3 aim = Vector3.Transform(TransformMath.Forward, Quaternion.CreateFromYawPitchRoll(intent.Yaw, intent.Pitch, 0));

        // Every solid thing counts, including the attacker's own kind: a swing is physical, and who it
        // is *allowed* to hurt is a rules question (factions, F24), not a physics one. The sweep starts
        // inside the attacker's own capsule, which the space reports as a zero-distance touch and
        // ignores, and the self check below is the belt to that pair of braces.
        var hit = _space.Sweep(Collider.Sphere(attack.Radius), new Pose { Position = eye, Rotation = Quaternion.Identity, Scale = Vector3.One },
                               aim, attack.Reach, LayerMask.All);

        var info = new DamageInfo(attacker, default, attack.DamageType, attack.Damage, eye + aim * attack.Reach, aim);
        if (!hit.Hit || hit.Entity.IsNull || hit.Entity == attacker) return info;
        if (!world.IsAlive(hit.Entity) || !world.Has<Attributes>(hit.Entity)) return info;   // scenery: the swing just stops

        // A target dead ahead is the easy case; the arc decides how much of a glancing angle counts.
        Vector3 toTarget = world.Get<Transform>(hit.Entity).LocalPosition - transform.LocalPosition;
        if (!SageMath.InCone(intent.Yaw, Vector3.Zero, toTarget, attack.ArcDegrees)) return info;

        return info with { Target = hit.Entity, Point = eye + aim * hit.Distance };
    }

    // Plays the swing's clip if the fighter has one (a first-person player has no sprite at all).
    private void PlayAnimation(World world, Entity entity, AttackRecord attack)
    {
        if (string.IsNullOrEmpty(attack.Animation)) return;
        if (!world.Has<SpriteAnimator>(entity) || !world.TryGet<SpriteRenderer>(entity, out var renderer)) return;
        if (!_records.TryGet(renderer.Sheet, out SpriteSheetRecord sheet)) return;

        int clip = sheet.ClipIndex(attack.Animation);
        if (clip < 0) return;
        ref var animator = ref world.Get<SpriteAnimator>(entity);
        animator = SpriteAnimator.Play(clip);
    }
}
