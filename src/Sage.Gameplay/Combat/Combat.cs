#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// Combat (docs/design/16 §3.2, TODO F20). One pipeline for every hit, whoever threw it:
//
//   a swing or a spell  ->  Combat.ApplyDamage  ->  the damage type's resistance attribute
//                       ->  an effect on the victim's health (16 §3.3)  ->  a Damaged event (04)
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
[Record("damage_type", Plugin = "sage.gameplay.combat")]
public sealed class DamageTypeRecord
{
    public RecordRef<SoundRecord> Sound;    // what landing this sounds like (11 §3, F4)
    public RecordRef<ParticleRecord> Particles; // and what it throws off: sparks, blood, embers (06 §3.12, F39)
    [System.Text.Json.Serialization.JsonConverter(typeof(ColourJsonConverter))]
    public uint Colour;                     // the damage number's colour: "#RRGGBB(AA)"; 0 = the engine's default

    public RecordRef<AttributeRecord> Resist;   // attribute read as a percentage, 0..95, that reduces this
    public RecordRef<EffectRecord> Effect;   // the effect applied to the victim, scaled by the damage
                              // (element tags and cue ids arrive with cues, F21: a field nothing
                              //  reads is a promise the engine isn't keeping, review #50)
}

// One swing: how far it reaches, how forgiving it is, what it costs in time (16 §3.2). Weapons hand
// the wielder one of these when inventory arrives (F19); until then a character carries its own.
[Record("attack", Plugin = "sage.gameplay.combat")]
public sealed class AttackRecord
{
    public float Damage = 10f;
    public RecordRef<DamageTypeRecord> DamageType; // empty = the game's default (gameplay_conventions)
    public float Reach = 2f;                // metres, from the attacker's eye
    public float Radius = 0.35f;            // the swing's thickness: how forgiving it is
    public float ArcDegrees = 120f;         // how far off-centre a target may be
    public float WindupTime = 0.25f;        // swing -> hit, unless the animation says when (12 §3)
    public float RecoverTime = 0.2f;        // hit -> able to do anything else
    public float Cooldown = 0.5f;           // and how long before the next swing
    public string Animation = "";           // clip to play; empty = the game's (gameplay_conventions
                                            // `animations.attack`), and its `animations.hit` event lands the blow
    // Raised where the swing starts (16 §3.3), so a weapon can be heard leaving its scabbard and seen
    // trailing without combat knowing what either looks like. The *hit* is the damage type's business:
    // one entry for fire covers a fireball and a torch, and a blade's own noise is this.
    public RecordRef<CueRecord> SwingCue;
    public RecordRef<SpriteSheetRecord> Viewmodel; // the sprite sheet a first-person wielder sees (13 §3):
                                            // rest, wind-up and strike, in that order
    // The 3D first-person arms and what they hold (issue #121): a `viewmodel` record (a skinned model,
    // its anim_graph, a weapon on a socket), shown on the wielder's camera while this is the swing in its
    // hands. It wins over the sprite `viewmodel`, which the HUD draws when this is empty (or not drawn).
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public RecordRef<ViewmodelRecord> Arms;
    public List<RecordRef<EffectRecord>> Effects = new();  // applied to the victim on a hit, unscaled (poison, burning)
}

// A hit that landed, for anything that reacts to one: the death seam's "who killed me", the game's
// combat log, AI's "heavy damage" interrupt, cues (F21). `DamageInfo` is what was asked for and this
// is what happened, which is why `Applied` lives here and not on the request.
//
// Sent in whatever phase does the damage and read by anyone with a cursor (04 §3.2), so there is no
// longer a rule about who must run after whom to see the tick's hits.
[GameEvent]
public readonly record struct Damaged(DamageInfo Hit, float Applied);

public static class Combat
{
    // Runs one hit through the pipeline and returns the health it actually cost. Zero means nothing
    // happened: blocked by a tag (`god`), already dead, or resisted to nothing.
    //
    // `alsoApply` are the effects that ride along with a landed hit — a poisoned blade, a burning
    // brand. They are applied as written (no magnitude) and only if the damage itself got through,
    // so `god` stops the poison as well as the cut.
    public static float ApplyDamage(World world, in DamageInfo hit, List<RecordRef<EffectRecord>>? alsoApply = null)
    {
        if (!world.IsAlive(hit.Target) || hit.Amount <= 0f) return 0f;

        var records = world.Resources.Get<RecordStore>();
        var conventions = GameplayConventions.Of(records);
        var typeId = hit.Type.IsEmpty ? conventions.DamageType.Id : hit.Type;
        if (typeId.IsEmpty)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, "damage_type:none",
                "A hit names no damage type and this game's gameplay_conventions name no default one, so it can't hurt anything");
            return 0f;
        }
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
        float before = world.Attribute(hit.Target, conventions.Health);

        // The health change is an effect like any other, so tags gate it and saves see it (16 §3.3).
        if (!Effects.Apply(world, hit.Target, type.Effect, hit.Attacker, amount)) return 0f;

        float applied = MathF.Max(0f, before - world.Attribute(hit.Target, conventions.Health));
        if (alsoApply != null)
            foreach (var effect in alsoApply) Effects.Apply(world, hit.Target, effect, hit.Attacker);

        world.Events.Send(new Damaged(hit, applied));
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
[Component("sage:melee")]
public struct Melee : IComponent
{
    [RecordRef("attack"), Property(Tooltip = "What it swings now; empty = the game's default attack (gameplay_conventions)")]
    public RecordId Attack;     // what it swings now; empty = the game's default (gameplay_conventions)
    [RecordRef("attack"), Property(Tooltip = "What it swings bare-handed, when a weapon comes off")]
    public RecordId Natural;    // and what it goes back to when a weapon comes off (16 §3.2, F19)
    [Transient] public MeleePhase Phase;   // mid-swing; a load starts you Ready rather than half-way
    [Transient] public float Timer;        // seconds in the current phase
    [Property(Min = 0, Unit = "s", Tooltip = "Time until the next swing may start")]
    public float Cooldown;                 // seconds until the next swing may start (relative)
    [Transient] public bool Swung;         // this swing has landed (or missed): don't resolve it twice

    public static Melee With(RecordId attack) => new() { Attack = attack, Natural = attack };
}

// Gameplay phase, before effects tick: turns "the Attack action was pressed" into a hit, for players
// and creatures alike (16 §3.1 — the payoff of the controller/pawn split is that combat never asks
// which one it is dealing with).
[System("sage.combat.melee", Phase.Gameplay, Before = new[] { "sage.effects.tick" })]
internal sealed class MeleeCombatSystem : ISystem
{
    private readonly Query<Transform, PawnIntent, CharacterController, Melee> _fighters;
    private readonly RecordStore _records;
    private readonly IPhysicsWorld _space;
    private readonly EventReader<AnimationEvent> _animation;
    private readonly List<AnimationEvent> _fired = new();   // the previous tick's animation events,
                                                            // drained once so Lands can ask per entity
    private readonly ActionId _attack;
    private readonly DebugDraw _debug;
    private readonly CVar<bool> _debugSwings;
    private readonly Deferred<(DamageInfo Hit, AttackRecord Attack)> _pending = new();   // dealt after
                        // the loop: applying an effect touches another entity's components (R14)

    public MeleeCombatSystem(World world, RecordStore records, ActionRegistry actions, CVar<bool> debugSwings)
    {
        _debug = world.Debug();
        _debugSwings = debugSwings;
        _fighters = world.Query<Transform, PawnIntent, CharacterController, Melee>();
        _records = records;
        _space = world.Resources.Get<IPhysicsWorld>();
        _animation = world.Events.Reader<AnimationEvent>(this);
        _attack = actions.Get(world.Conventions().Actions.Attack);
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float dt = ctx.Tick.Dt;
        var conventions = world.Conventions();
        var dead = conventions.Dead;

        // Animation raises its events in a later phase, so what this drains is the previous tick's
        // (see Lands). The cursor makes that exact: every event once, none missed, whatever else ran.
        _fired.Clear();
        foreach (ref readonly var e in _animation.Read()) _fired.Add(e);

        foreach (var (transforms, intents, characters, melees, entities) in _fighters.Chunks)
        {
            var t = transforms.Span;
            var i = intents.Span;
            var c = characters.Span;
            var m = melees.Span;
            for (int n = 0; n < m.Length; n++)
            {
                var entity = entities.EntityAt(n);
                var attack = AttackFor(ref m[n], conventions);
                if (attack == null) continue;

                if (m[n].Cooldown > 0f) m[n].Cooldown = MathF.Max(0f, m[n].Cooldown - dt);

                switch (m[n].Phase)
                {
                    case MeleePhase.Ready:
                        if (i[n].Pressed.Has(_attack) && m[n].Cooldown <= 0f && !world.HasTag(entity, dead))
                        {
                            m[n].Phase = MeleePhase.Windup;
                            m[n].Timer = 0f;
                            m[n].Swung = false;
                            PlayAnimation(world, entity, attack.Animation.Length > 0 ? attack.Animation : conventions.Animations.Attack);
                            // The swing, not the hit: a blow that misses still made a noise, and a
                            // creature winding up behind you is the warning you get.
                            if (!attack.SwingCue.IsEmpty)
                                world.Events.Send(new CueTriggered(attack.SwingCue, entity, t[n].LocalPosition));
                        }
                        break;

                    case MeleePhase.Windup:
                        m[n].Timer += dt;
                        // Killed mid-swing: the blow dies with the fighter rather than landing from
                        // a corpse.
                        if (world.HasTag(entity, dead))
                        {
                            m[n].Phase = MeleePhase.Ready;
                            m[n].Timer = 0f;
                            m[n].Swung = true;
                            break;
                        }
                        if (!Lands(entity, attack, m[n].Timer, conventions.Animations.Hit)) break;
                        _pending.Add((Resolve(world, entity, in t[n], in i[n], in c[n], attack), attack));
                        m[n].Swung = true;
                        m[n].Timer = 0f;
                        m[n].Phase = MeleePhase.Recover;
                        break;

                    case MeleePhase.Recover:
                        m[n].Timer += dt;
                        if (m[n].Timer < attack.RecoverTime) break;
                        // Or it stands frozen mid-swing. A sheet without the idle clip keeps whatever it
                        // was playing, which is the best a system this simple can do.
                        PlayAnimation(world, entity, conventions.Animations.Idle);
                        m[n].Phase = MeleePhase.Ready;
                        m[n].Timer = 0f;
                        m[n].Cooldown = attack.Cooldown;
                        break;
                }
            }
        }

        foreach (var (hit, attack) in _pending.Drain())
        {
            if (hit.Target.IsNull) continue;
            // A swing is a physics query and physics has no opinions, so this is where the swing finds
            // out whose side it is on (16 §3.5, F24): a creature's blade passes through its own kind,
            // and the player's lands on whoever was standing there.
            if (!Factions.MayHurt(world, hit.Attacker, hit.Target)) continue;
            Combat.ApplyDamage(world, hit, attack.Effects);
        }
    }

    private AttackRecord? AttackFor(ref Melee melee, GameplayConventionsRecord conventions)
    {
        var id = melee.Attack.IsEmpty ? conventions.Attack.Id : melee.Attack;
        if (id.IsEmpty) return null;   // given no attack, and the game has no default: it cannot swing
        if (_records.TryGet(id, out AttackRecord record)) return record;
        Log.Once(LogCat.Gameplay, LogLevel.Error, $"attack:{id}", $"No attack record {id}: nothing can swing it");
        return null;
    }

    // When the blow lands: on the animation's hit event (the conventions name it: "hit" in the engine's)
    // if the clip has one, otherwise on the record's windup time. The event is raised in the Animation
    // phase, which runs after this one, so a sprite's hit lands one tick (16 ms) after the frame that
    // shows it — not worth a phase shuffle.
    private bool Lands(Entity entity, AttackRecord attack, float timer, string hit) =>
        (hit.Length > 0 && Fired(entity, hit)) || timer >= attack.WindupTime;

    private bool Fired(Entity entity, string name)
    {
        for (int i = 0; i < _fired.Count; i++)
            if (_fired[i].Entity == entity && string.Equals(_fired[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // The swing itself: a sphere swept along the attacker's aim (10 §4), first thing it touches that
    // can be hurt. One target per swing in v1; cleaving through several is a later flag.
    private DamageInfo Resolve(World world, Entity attacker, in Transform transform, in PawnIntent intent,
                               in CharacterController character, AttackRecord attack)
    {
        var profile = CharacterConventions.Of(world).ProfileOf(_records, character.Profile);
        Vector3 eye = CharacterController.EyeOf(transform.LocalPosition, in character, profile);
        Vector3 aim = Vector3.Transform(TransformMath.Forward, Quaternion.CreateFromYawPitchRoll(intent.Yaw, intent.Pitch, 0));

        // Every solid thing counts, including the attacker's own kind: a swing is physical, and who it
        // is *allowed* to hurt is a rules question (factions, F24), not a physics one. The sweep starts
        // inside the attacker's own capsule, so the attacker is left out of it (`ignore`), and the self
        // check below is the belt to that pair of braces. Something else the swing starts inside — a
        // creature pressed up against you — is hit at distance 0 (issue #30: it used to be missed).
        var hit = _space.Sweep(Collider.Sphere(attack.Radius), new Pose { Position = eye, Rotation = Quaternion.Identity, Scale = Vector3.One },
                               aim, attack.Reach, LayerMask.All, ignore: attacker);

        var info = new DamageInfo(attacker, default, attack.DamageType, attack.Damage, eye + aim * attack.Reach, aim);
        if (Connects(world, attacker, in transform, in intent, attack, hit))
            info = info with { Target = hit.Entity, Point = eye + aim * hit.Distance };

        DrawSwing(eye, aim, attack, in info);
        return info;
    }

    // Did the sweep find something this swing is allowed to hurt?
    private static bool Connects(World world, Entity attacker, in Transform transform, in PawnIntent intent,
                                 AttackRecord attack, in SweepHit hit)
    {
        if (!hit.Hit || hit.Entity.IsNull || hit.Entity == attacker) return false;
        if (!world.IsAlive(hit.Entity) || !world.Has<Attributes>(hit.Entity)) return false;   // scenery: the swing just stops

        // A target dead ahead is the easy case; the arc decides how much of a glancing angle counts.
        Vector3 toTarget = world.Get<Transform>(hit.Entity).LocalPosition - transform.LocalPosition;
        return SageMath.InCone(intent.Yaw, Vector3.Zero, toTarget, attack.ArcDegrees);
    }

    // What a swing reached and whether it found anything, left on screen long enough to look at:
    // a miss is the hard thing to debug, and a miss draws too.
    private void DrawSwing(Vector3 eye, Vector3 aim, AttackRecord attack, in DamageInfo info)
    {
        if (!_debugSwings.Value || !_debug.Enabled) return;
        uint colour = info.Target.IsNull ? DebugColour.Red : DebugColour.Green;
        _debug.Arrow(eye, eye + aim * attack.Reach, colour, 0.6f);
        _debug.Sphere(eye + aim * attack.Reach, attack.Radius, colour, 0.6f);
        if (!info.Target.IsNull) _debug.Cross(info.Point, 0.25f, DebugColour.Yellow, 0.6f);
    }

    // Plays a clip by name if the fighter has a sheet with one (a first-person player has no sprite
    // at all). Combat driving animation directly is v1: an AnimationStateSystem picking clips from
    // gameplay state is 12 §3's job once there is more than "swinging" and "not swinging".
    private void PlayAnimation(World world, Entity entity, string name) => world.PlayClip(entity, name, _records);
}
