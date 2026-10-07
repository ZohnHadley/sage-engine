#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Blocks, parries, knockback and hit reactions (issue #390; docs/design/16 §3.2). Each is an option of
// the records a blow already has, decided in Combat.ApplyHit between "may it hurt this" (the attack's
// `friendlyFire`, a FactionFilter) and the damage pipeline:
//
//   a guard up (AttackStance.Blocking, the Block button or an AI's Block task) with a `block` from the
//   defender's own attack, facing the blow within its `arcDegrees` (and its direction, when `directional`)
//     -> raised no longer than `parryWindow` ago: a parry — nothing lands, and the attacker staggers
//     -> else, with the stamina to pay: blocked — `reduction` of the damage and the knockback stopped
//     -> else the guard breaks: the blow lands whole and the defender staggers
//   a blow that lands pushes along its own direction (`knockback`, `knockbackLift`), sets its `reaction`
//   trigger on the victim's animator and, with `stagger`, interrupts the victim's swing and guard.
//
// Damage over time through resistances is the `damage` effect execution (DamageOverTime.cs), and a
// sweep that strikes several bodies is the attack's `cleave` (Hits.cs).

// How a fighter guards (issue #390): named by an attack's `block`, so a shield, a sword and a bare fist
// each guard their own way, and a weapon with none cannot block at all.
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4r: may change before 1.0
[Record("block", Plugin = "sage.gameplay.combat")]
public sealed class BlockRecord
{
    [Property(Min = 0, Max = 360, Unit = "°", Tooltip = "How wide the guard covers, centred on the way the defender faces; 360 = all round")]
    public float ArcDegrees = 120f;
    [Property(Min = 0, Max = 1, Tooltip = "The fraction of a blocked blow's damage and knockback the guard stops; 1 = all of it")]
    public float Reduction = 1f;
    [Property(Tooltip = "Blocks only a blow coming the way the guard faces (AttackStance.Direction against the swing's), as Warband; a guard or a swing with no direction matches any")]
    public bool Directional;

    [Property(Tooltip = "The attribute a block spends (stamina), through its spendEffect; empty = blocking is free")]
    public RecordRef<AttributeRecord> Stamina;
    [Property(Min = 0, Tooltip = "Stamina each blocked blow costs")]
    public float StaminaCost;
    [Property(Min = 0, Tooltip = "And per point of damage the guard stopped")]
    public float StaminaPerDamage;
    [Property(Min = 0, Unit = "s", Tooltip = "How long a defender who cannot pay for a block is staggered when its guard breaks")]
    public float GuardBreakStagger = 0.5f;

    [Property(Min = 0, Unit = "s", Tooltip = "A blow met this soon after the guard went up is parried: nothing lands, nothing is spent; 0 = never")]
    public float ParryWindow;
    [Property(Min = 0, Unit = "s", Tooltip = "How long a parried attacker is staggered")]
    public float ParryStagger = 0.6f;

    [Property(Category = "Presentation", Tooltip = "The trigger set on the defender's animator when it blocks a blow")]
    public string BlockTrigger = "";
    [Property(Category = "Presentation", Tooltip = "The trigger set on the attacker's animator when its blow is parried (its recoil)")]
    public string ParryTrigger = "";
    [Property(Category = "Presentation", Tooltip = "Raised where a blow is blocked (the clang)")]
    public RecordRef<CueRecord> BlockCue;
    [Property(Category = "Presentation", Tooltip = "Raised where a blow is parried")]
    public RecordRef<CueRecord> ParryCue;

    // Out-of-range numbers, and a stamina that cannot be spent, are load errors.
    internal static void Check(BlockRecord block, RecordCheck check)
    {
        if (!(block.ArcDegrees > 0f && block.ArcDegrees <= 360f)) check.Error(nameof(ArcDegrees), $"arcDegrees must be above 0 and at most 360 (is {block.ArcDegrees})");
        if (!(block.Reduction >= 0f && block.Reduction <= 1f)) check.Error(nameof(Reduction), $"reduction must be between 0 and 1 (is {block.Reduction})");
        if (!(block.StaminaCost >= 0f)) check.Error(nameof(StaminaCost), "staminaCost cannot be negative");
        if (!(block.StaminaPerDamage >= 0f)) check.Error(nameof(StaminaPerDamage), "staminaPerDamage cannot be negative");
        if (!(block.GuardBreakStagger >= 0f)) check.Error(nameof(GuardBreakStagger), "guardBreakStagger cannot be negative");
        if (!(block.ParryWindow >= 0f)) check.Error(nameof(ParryWindow), "parryWindow cannot be negative");
        if (!(block.ParryStagger >= 0f)) check.Error(nameof(ParryStagger), "parryStagger cannot be negative");
        if (block.Stamina.IsEmpty)
        {
            if (block.StaminaCost > 0f || block.StaminaPerDamage > 0f)
                check.Error(nameof(Stamina), "a block with a stamina cost must name the attribute it spends (stamina)");
            return;
        }
#pragma warning disable SAGE0131   // following a reference while checking (experimental, phase 4i)
        if (check.TryGet(block.Stamina.Id, out AttributeRecord? pool) && pool.SpendEffect.IsEmpty)
#pragma warning restore SAGE0131
            check.Error(nameof(Stamina), $"attribute {block.Stamina.Id} has no spendEffect, so a block cannot spend it");
    }

    // An attack's hit-reaction numbers (issue #390), checked with the attack.
    internal static void CheckAttack(AttackRecord attack, RecordCheck check)
    {
        if (attack.Cleave < 1) check.Error(nameof(AttackRecord.Cleave), $"cleave is how many targets a sweep strikes, at least 1 (is {attack.Cleave})");
        if (!(attack.Knockback >= 0f)) check.Error(nameof(AttackRecord.Knockback), "knockback cannot be negative");
        if (!(attack.KnockbackLift >= 0f)) check.Error(nameof(AttackRecord.KnockbackLift), "knockbackLift cannot be negative");
        if (!(attack.Stagger >= 0f)) check.Error(nameof(AttackRecord.Stagger), "stagger cannot be negative");
    }
}

// What a guard made of a blow (the `Blocked` event).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4r: may change before 1.0
public enum GuardOutcome
{
    // Stopped `reduction` of it, for the stamina it cost.
    Blocked,
    // Met it inside the parry window: nothing landed and the attacker staggers.
    Parried,
    // Could not pay for the block: the blow landed whole and the defender staggers.
    Broken,
}

// A guard met a blow (issue #390): for a HUD's "blocked!", a sound, a skill that rises with blocking.
// `Stopped` is the damage the guard kept off (the whole blow for a parry, none for a broken guard).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4r: may change before 1.0
[GameEvent]
public readonly record struct Blocked(Entity Defender, Entity Attacker, RecordId Attack, GuardOutcome Outcome, float Stopped);

// Something was staggered (issue #390): a heavy blow, a parried swing or a broken guard. For `Seconds`
// it cannot swing or guard, and a swing it was winding up is lost (Melee.Staggered).
[System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4r: may change before 1.0
[GameEvent]
public readonly record struct Staggered(Entity Target, Entity Source, float Seconds);

internal static class Guards
{
    // Whether `defender` guards this blow, and how; null when it does not (no guard up, no `block`, the
    // blow from behind or the wrong way). `fraction` is what is left of the blow: 0 parried, 1 − reduction
    // blocked, 1 broken. Spends the stamina, staggers whoever the outcome staggers, raises Blocked.
    public static GuardOutcome? Resolve(World world, RecordStore records, in HitRequest request, Entity defender,
                                        AttackRecord attack, float amount, out float fraction)
    {
        fraction = 1f;
        if (defender == request.Attacker || !world.IsAlive(defender)) return null;
        if (!world.TryGet<AttackStance>(defender, out var stance) || !stance.Blocking) return null;
        if (!world.TryGet<Melee>(defender, out var melee)) return null;
        var block = BlockOf(world, records, in melee);
        if (block == null) return null;

        // Facing the blow: the way it travels, turned round, inside the guard's arc.
        float yaw = world.TryGet<PawnIntent>(defender, out var intent) ? intent.Yaw
                  : world.TryGet<Transform>(defender, out var at) ? SageMath.YawOf(at.LocalRotation) : 0f;
        var coming = new Vector3(-request.Aim.X, 0f, -request.Aim.Z);
        if (coming.LengthSquared() > 1e-8f && !SageMath.InCone(yaw, Vector3.Zero, coming, block.ArcDegrees)) return null;
        if (block.Directional)
        {
            var swing = world.TryGet<Melee>(request.Attacker, out var theirs) ? theirs.Direction : AttackDirection.None;
            if (swing != AttackDirection.None && stance.Direction != AttackDirection.None && swing != stance.Direction) return null;
        }

        var point = world.TryGet<Transform>(defender, out var place) ? place.LocalPosition : default;
        if (block.ParryWindow > 0f && melee.Guarding <= block.ParryWindow)
        {
            fraction = 0f;
            if (!block.ParryCue.IsEmpty) world.Events.Send(new CueTriggered(block.ParryCue, defender, point));
            world.Events.Send(new Blocked(defender, request.Attacker, request.Attack, GuardOutcome.Parried, amount));
            HitReactions.Stagger(world, request.Attacker, defender, block.ParryStagger, block.ParryTrigger);
            return GuardOutcome.Parried;
        }

        float reduction = Math.Clamp(block.Reduction, 0f, 1f);
        float stopped = amount * reduction;
        if (!block.Stamina.IsEmpty)
        {
            float cost = MathF.Max(0f, block.StaminaCost) + MathF.Max(0f, block.StaminaPerDamage) * stopped;
            if (cost > 0f)
            {
                if (world.Attribute(defender, block.Stamina.Id) < cost)
                {
                    world.Events.Send(new Blocked(defender, request.Attacker, request.Attack, GuardOutcome.Broken, 0f));
                    HitReactions.Stagger(world, defender, request.Attacker, block.GuardBreakStagger, "");
                    return GuardOutcome.Broken;
                }
                if (records.TryGet(block.Stamina.Id, out AttributeRecord pool) && !pool.SpendEffect.IsEmpty)
                    Effects.Apply(world, defender, pool.SpendEffect, defender, cost);
            }
        }

        fraction = 1f - reduction;
        if (block.BlockTrigger.Length > 0) Animators.SetTrigger(world, defender, block.BlockTrigger);
        if (!block.BlockCue.IsEmpty) world.Events.Send(new CueTriggered(block.BlockCue, defender, point));
        world.Events.Send(new Blocked(defender, request.Attacker, request.Attack, GuardOutcome.Blocked, stopped));
        return GuardOutcome.Blocked;
    }

    // The defender's guard: its attack's `block` (what it holds now, else the game's default attack).
    private static BlockRecord? BlockOf(World world, RecordStore records, in Melee melee)
    {
        var id = melee.Attack.IsEmpty ? world.Conventions().Attack.Id : melee.Attack;
        if (id.IsEmpty || !records.TryGet(id, out AttackRecord held)) return null;
        var blockId = held.Block.Id;
        return !blockId.IsEmpty && records.TryGet(blockId, out BlockRecord block) ? block : null;
    }
}

internal static class HitReactions
{
    // What a blow that got past the guard does besides damage: the push along its direction (scaled by
    // what the guard left of it), and, if it hurt, its `reaction` trigger and its `stagger`.
    public static void After(World world, in HitRequest request, Entity target, AttackRecord attack, GuardOutcome? guard,
                             float fraction, float applied)
    {
        if (guard == GuardOutcome.Parried || !world.IsAlive(target)) return;
        if ((attack.Knockback > 0f || attack.KnockbackLift > 0f) && (applied > 0f || guard == GuardOutcome.Blocked))
            Knock(world, target, request.Aim, attack.Knockback * fraction, attack.KnockbackLift * fraction);
        if (applied <= 0f || guard == GuardOutcome.Blocked) return;
        if (attack.Stagger > 0f) Stagger(world, target, request.Attacker, attack.Stagger, attack.Reaction);
        else if (attack.Reaction.Length > 0) Animators.SetTrigger(world, target, attack.Reaction);
    }

    // A push across the ground along `direction` at `force` m/s and `lift` up: a character only (a crate
    // has no velocity of its own to change yet), as the `knockback` execution does.
    public static void Knock(World world, Entity target, Vector3 direction, float force, float lift)
    {
        if (!(force > 0f) && !(lift > 0f)) return;
        if (!world.Has<CharacterController>(target)) return;
        var across = new Vector3(direction.X, 0f, direction.Z);
        across = across.LengthSquared() > 1e-8f ? Vector3.Normalize(across) : Vector3.Zero;
        ref var character = ref world.Get<CharacterController>(target);
        character.Velocity += across * MathF.Max(force, 0f) + Vector3.UnitY * MathF.Max(lift, 0f);
        if (lift > 0f) character.Grounded = false;
    }

    // Staggers `target` for `seconds`: a swing it was winding up is lost, its guard drops, and it can do
    // neither until the time is up (MeleeCombatSystem, AttackStanceSystem). `trigger` goes on its animator.
    public static void Stagger(World world, Entity target, Entity source, float seconds, string trigger)
    {
        if (!(seconds > 0f) || !world.IsAlive(target)) return;
        if (world.Has<Melee>(target))
        {
            ref var melee = ref world.Get<Melee>(target);
            melee.Staggered = MathF.Max(melee.Staggered, seconds);
            melee.Guarding = 0f;
            if (melee.Phase == MeleePhase.Windup)
            {
                melee.Phase = MeleePhase.Ready;
                melee.Timer = 0f;
                melee.Swung = true;
            }
        }
        if (world.Has<AttackStance>(target)) world.Get<AttackStance>(target).Blocking = false;
        if (trigger.Length > 0) Animators.SetTrigger(world, target, trigger);
        world.Events.Send(new Staggered(target, source, seconds));
    }
}
