#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// Combat (docs/design/16 §3.2, TODO F20). One pipeline for every hit, whoever threw it:
//
//   a strike or a spell  ->  Combat.ApplyHit (ApplyDamage)  ->  the damage type's resistance attribute
//                       ->  an effect on the victim's health (16 §3.3)  ->  a Damaged event (04)
//
// Nothing subtracts health directly. Damage is an *effect* with a magnitude, so resistances, the
// `god` tag, damage over time and saves all keep working through the single path F18 built, and a
// future server runs exactly this code. How a strike gets to what it hits — a swing, a ray, a bolt —
// is its `hit_delivery` (Hits.cs, issue #133).

// What a hit is made of. `Point` and `Direction` are where it landed and which way it was going —
// cues, knockback and hit reactions (F21) read them; v1 only logs them.
public readonly record struct DamageInfo(
    Entity Attacker, Entity Target, RecordId Type, float Amount, Vector3 Point, Vector3 Direction)
{
    // The sound the weapon asked for in place of its damage type's (issue #330, AttackRecord.Sound); empty =
    // the damage type's. Carried on the hit because the audio system hears a `Damaged`, not an attack.
    public RecordId Sound { get; init; }

    public float Applied { get; init; }   // after resistance and tag gating: what health actually lost

    // Where on the body it landed: the `hit_location` of the hitbox a strike met (issue #137); empty = the body.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public RecordId Location { get; init; }
}

// A kind of damage and what stands up to it (16 §3.2). Games add their own: frost, poison, falling.
[Record("damage_type", Plugin = "sage.gameplay.combat")]
public sealed class DamageTypeRecord
{
    public RecordRef<SoundRecord> Sound;    // what landing this sounds like (11 §3, F4)
    public RecordRef<ParticleRecord> Particles; // and what it throws off: sparks, blood, embers (06 §3.12, F39)
    // And the mark it leaves behind what it hurt (issue #306): blood on the wall or the floor, found along
    // the blow within the decal's `reach`, then straight down.
    [Property(Tooltip = "The decal a hit leaves on the surface behind or below what it hurt; empty = none")]
    public RecordRef<DecalRecord> Decal;
    // And what it feels like to be hit by it (issue #331): the rumble on the victim's pad, if the victim is
    // a player. Only a blow that cost them something is felt (`god` and a full resist are not).
    [Property(Tooltip = "The rumble a player's pad does when this damage lands on them; empty = none")]
    public RecordRef<RumbleRecord> Rumble;
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
    public float WindupTime = 0.25f;        // swing -> hit, unless the animation's `hit` event says when (12 §3)
    public float RecoverTime = 0.2f;        // hit -> able to do anything else
    public float Cooldown = 0.5f;           // and how long before the next swing
    [Obsolete("Clip names are the anim_graph's since issue #119: an attack sets its Trigger on the wielder's animator, and the graph picks the clip. Ignored.")]
    public string Animation = "";           // obsolete (#119), ignored: clip names are the graph's
    // The trigger param the swing sets on the wielder's animator (issue #119); empty = the game's
    // (gameplay_conventions `animations.attackTrigger`). The graph picks the clip, and the clip's `hit`
    // event (`animations.hit`) lands the blow.
    public string Trigger = "";
    // Raised where the swing starts (16 §3.3), so a weapon can be heard leaving its scabbard and seen
    // trailing without combat knowing what either looks like. The *hit* is the damage type's business:
    // one entry for fire covers a fireball and a torch, and a blade's own noise is this.
    public RecordRef<CueRecord> SwingCue;
    [Property(Tooltip = "What landing this attack sounds like, in place of its damage type's sound (a war hammer and a dagger both do physical damage); empty = the damage type's")]
    public RecordRef<SoundRecord> Sound;
    public RecordRef<SpriteSheetRecord> Viewmodel; // the sprite sheet a first-person wielder sees (13 §3):
                                            // rest, wind-up and strike, in that order
    // The 3D first-person arms and what they hold (issue #121): a `viewmodel` record (a skinned model,
    // its anim_graph, a weapon on a socket), shown on the wielder's camera while this is the swing in its
    // hands. It wins over the sprite `viewmodel`, which the HUD draws when this is empty (or not drawn).
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0126", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public RecordRef<ViewmodelRecord> Arms;
    public List<RecordRef<EffectRecord>> Effects = new();  // applied to the victim on a hit, unscaled (poison, burning)

    // How the blow gets to what it hits (issue #133): a registered `hit_delivery` — `sweep` (a swing:
    // reach, radius, arc), `ray` (hitscan to `range`) or `projectile` (a carrier flying `range` at
    // `projectileSpeed`). Empty means `sweep`.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [VocabularyRef("hit_delivery"), Property(Tooltip = "A registered hit delivery: sweep, ray or projectile; empty = sweep")]
    public string Delivery = HitDeliveries.Default;
    internal IHitDelivery? DeliveryInstance;   // found once (HitDeliveries.Of)
    internal string? DeliveryFor;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "m", Tooltip = "How far a ray or a projectile reaches; a sweep uses reach")]
    public float Range = 100f;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 1, Tooltip = "Rays or projectiles per strike, each landing its own hit")]
    public int Pellets = 1;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "m/s", Tooltip = "How fast a projectile delivery's carrier flies")]
    public float ProjectileSpeed = 60f;

    // ---- the projectile carrier (issue #134) ----
    // What a `projectile` delivery's bolt looks like (none: invisible, the headless default), how fast it
    // falls (0: straight flight) and how many targets it passes through before one stops it.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "The prefab a projectile delivery's carrier looks like; empty = invisible")]
    public RecordRef<PrefabRecord> Projectile;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "m/s²", Tooltip = "How fast a projectile delivery's carrier falls; 0 flies straight (9.81 is Earth)")]
    public float ProjectileGravity;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Tooltip = "How many targets a projectile delivery's carrier passes through before one stops it")]
    public int ProjectilePierce;

    // ---- ammunition (issue #135; Ammunition.cs) — kept together, away from the hit fields above ----
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "The item this attack spends; empty = infinite ammunition")]
    public RecordRef<ItemRecord> Ammo;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Tooltip = "Rounds the magazine holds, filled from the inventory by Reload; 0 = no magazine, each shot is taken straight from the inventory")]
    public int Magazine;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 1, Tooltip = "Rounds one shot spends")]
    public int AmmoPerShot = 1;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "s", Tooltip = "How long a reload takes when the animation has no mag_in event to say when the magazine goes in")]
    public float ReloadTime = 2f;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "Fires every time the cooldown allows while the button is held, not once per press")]
    public bool Automatic;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "shots/s", Tooltip = "Shots a second (replaces cooldown when above 0)")]
    public float RateOfFire;
    // How far it is heard when it leaves the weapon (issue #386): metres; negative is the conventions'
    // `noise` for its delivery (a gunshot, a swing), 0 is silent (a suppressed pistol, a blowgun).
    [Property(Min = -1, Unit = "m", Tooltip = "How far its firing is heard; -1 = the conventions' for its delivery, 0 = silent")]
    public float NoiseRadius = -1f;

    // ---- spread and recoil (issue #136; Spread.cs) — last, so parallel additions above merge cleanly ----
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "How far a shot may stray from the aim; empty = it flies true")]
    public RecordRef<SpreadRecord> Spread;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "How a shot kicks the wielder's aim; empty = no kick")]
    public RecordRef<RecoilRecord> Recoil;

    // ---- guards and hit reactions (issue #390; Blocking.cs) ----
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "How a fighter holding this guards (a block record); empty = it cannot block")]
    public RecordRef<BlockRecord> Block;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 1, Tooltip = "How many bodies a sweep strikes, nearest first, within its reach and arc; 1 = the first it meets")]
    public int Cleave = 1;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "m/s", Tooltip = "How hard a landed blow pushes a character along its direction, less what a block stopped")]
    public float Knockback;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "m/s", Tooltip = "And how hard it pushes it up")]
    public float KnockbackLift;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "s", Tooltip = "How long a blow that hurt staggers its victim: its swing is lost and it cannot swing or guard; 0 = no stagger")]
    public float Stagger;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "The trigger a blow that hurt sets on its victim's animator (a flinch); empty = none")]
    public string Reaction = "";
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "Who it may hurt, by faction: Default (a player anyone, others not their allies), Anyone, NotAllies, Hostile or Allies")]
    public FactionFilter FriendlyFire = FactionFilter.Default;
}

// A hit that landed, for anything that reacts to one: the death seam's "who killed me", the game's
// combat log, AI's "heavy damage" interrupt, cues (F21). `DamageInfo` is what was asked for and this
// is what happened, which is why `Applied` lives here and not on the request.
//
// Sent in whatever phase does the damage and read by anyone with a cursor (04 §3.2), so there is no
// longer a rule about who must run after whom to see the tick's hits.
[GameEvent]
public readonly record struct Damaged(DamageInfo Hit, float Applied)
{
    // Where on the body it landed (issues #133, #137): the hit's `Location` — the hit_location of the
    // hitbox the strike met — empty for the body (a capsule, a sprite creature, a spell, the `hurt` cheat).
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public RecordId Location => Hit.Location;
}

public static class Combat
{
    // Runs one hit through the pipeline and returns the health it actually cost. Zero means nothing
    // happened: blocked by a tag (`god`), already dead, or resisted to nothing.
    //
    // `alsoApply` are the effects that ride along with a landed hit — a poisoned blade, a burning
    // brand. They are applied as written (no magnitude) and only if the damage itself got through,
    // so `god` stops the poison as well as the cut.
    public static float ApplyDamage(World world, in DamageInfo hit, List<RecordRef<EffectRecord>>? alsoApply = null) =>
        ApplyHit(world, in hit, HitResult.Of(in hit), alsoApply);

    // An attack's strike that reached `result` (issue #133): the one place a delivered hit becomes
    // damage. A strike is a physics query and physics has no opinions, so this is where it finds out
    // whose side it is on (16 §3.5, F24): a creature's blade passes through its own kind, and the
    // player's lands on whoever was standing there. Then the attack's damage, type and riders.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public static float ApplyHit(World world, in HitRequest request, in HitResult result, AttackRecord attack)
    {
        var landed = Hitboxes.Resolve(in result);   // a hitbox is its owner's (issue #137)
        if (landed.Target.IsNull) return 0f;
        // Who it may hurt is one faction filter, the attack's `friendlyFire` (issue #390).
        if (!FactionFilters.Allows(world, request.Attacker, landed.Target, attack.FriendlyFire)) return 0f;
        // The weapon's condition scales the blow, and the blow wears the weapon (issue #382).
        var records = world.Resources.Get<RecordStore>();
        float amount = attack.Damage * Durability.DamageScale(world, request.Attacker, request.Attack, records);
        // A guard facing it (issue #390): parried, blocked for what is left, or broken.
        var guard = Guards.Resolve(world, records, in request, landed.Target, attack, amount, out float fraction);
        float applied = 0f;
        if (guard != GuardOutcome.Parried)
        {
            var damage = new DamageInfo(request.Attacker, landed.Target, attack.DamageType, amount * fraction, landed.Point, request.Aim) { Sound = attack.Sound.Id };
            applied = ApplyHit(world, in damage, in landed, attack.Effects);
        }
        HitReactions.After(world, in request, landed.Target, attack, guard, fraction, applied);
        if (world.IsAlive(request.Attacker)) Durability.Landed(world, request.Attacker, request.Attack, records);
        return applied;
    }

    // The pipeline itself: `damage` is what was asked for (who, how much, of what, which way) and
    // `result` where it landed (on what, where, which location), which wins over the request's own. A
    // landing on a hitbox is its owner's, at the box's location (issue #137); the location's multiplier,
    // then its resist, then the damage type's resist scale the damage, and its effects ride along.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public static float ApplyHit(World world, in DamageInfo damage, in HitResult result, List<RecordRef<EffectRecord>>? alsoApply = null)
    {
        var landed = Hitboxes.Resolve(in result);
        var hit = damage with { Target = landed.Target, Point = landed.Point, Location = landed.Location };
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

        // Where it landed first (issue #137): the location's multiplier and its own armour, then the
        // damage type's resist — each a fraction of what is left, so they multiply.
        var location = LocationOf(records, hit.Location);
        float amount = hit.Amount;
        if (location != null)
        {
            amount *= MathF.Max(0f, location.DamageMultiplier);
            amount = Resist(world, hit.Target, location.Resist.Id, amount);
        }
        amount = Resist(world, hit.Target, type.Resist.Id, amount);
        float before = world.Attribute(hit.Target, conventions.Health);

        // The health change is an effect like any other, so tags gate it and saves see it (16 §3.3).
        if (!Effects.Apply(world, hit.Target, type.Effect, hit.Attacker, amount)) return 0f;

        float applied = MathF.Max(0f, before - world.Attribute(hit.Target, conventions.Health));
        if (alsoApply != null)
            foreach (var effect in alsoApply) Effects.Apply(world, hit.Target, effect, hit.Attacker);
        if (location != null)
            foreach (var effect in location.Effects) Effects.Apply(world, hit.Target, effect, hit.Attacker);

        Durability.Hurt(world, hit.Target, records);   // what it wears takes the blow too (issue #382)
        world.Events.Send(new Damaged(hit, applied));
        return applied;
    }

    // The sound a landed hit makes (issue #330): the attack's own when it names one, else its damage
    // type's (the game's default type when it names none); empty = silence. What the client's audio system plays for a `Damaged`, here so a headless
    // test can ask the same question.
    public static RecordId HitSound(RecordStore records, in DamageInfo hit)
    {
        if (!hit.Sound.IsEmpty) return hit.Sound;
        var typeId = hit.Type.IsEmpty ? GameplayConventions.Of(records).DamageType.Id : hit.Type;   // as the hit itself resolved it
        return !typeId.IsEmpty && records.TryGet(typeId, out DamageTypeRecord type) ? type.Sound.Id : default;
    }

    // Resistance is a percentage attribute (armour, fire_resist, armor_head…), clamped so nothing is
    // ever immune by arithmetic: a resistance record's own Max decides the ceiling (95 for the engine's armour).
    private static float Resist(World world, Entity target, RecordId resist, float amount)
    {
        if (resist.IsEmpty) return amount;
        float percent = Math.Clamp(world.Attribute(target, resist), 0f, 95f);
        return amount * (1f - percent * 0.01f);
    }

    // The body (empty) has no record; a location nobody defined is said once and counts as the body.
    private static HitLocationRecord? LocationOf(RecordStore records, RecordId location)
    {
        if (location.IsEmpty) return null;
        if (records.TryGet(location, out HitLocationRecord record)) return record;
        Log.Once(LogCat.Gameplay, LogLevel.Error, $"hit_location:{location}", $"No hit_location record {location}: a strike there counts as the body");
        return null;
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
    // The direction of the swing under way (issue #359): the fighter's AttackStance.Direction when it
    // started, kept while the stance goes on choosing the next one. None without a stance.
    [Transient] public AttackDirection Direction;
    // Seconds it is still staggered (issue #390): a heavy blow, a parried swing or a broken guard. It
    // cannot swing or guard until this runs out.
    [Transient] public float Staggered;
    // Seconds its guard has been up (AttackStanceSystem): a blow met inside its block's parryWindow is parried.
    [Transient] internal float Guarding;

    public static Melee With(RecordId attack) => new() { Attack = attack, Natural = attack };
}

// Gameplay phase, before effects tick: turns "the Attack action was pressed" into a hit, for players
// and creatures alike (16 §3.1 — the payoff of the controller/pawn split is that combat never asks
// which one it is dealing with). Since issue #133 it is the attack system for every `attack`, not only
// swings: it times the blow (windup -> Lands -> recover -> cooldown, unchanged) and the attack's
// `hit_delivery` finds what the blow reaches — a sweep, a ray or a projectile. The id and the `Melee`
// component keep their names (saves and content use them).
[System("sage.combat.melee", Phase.Gameplay, Before = new[] { "sage.effects.tick" })]
internal sealed class MeleeCombatSystem : ISystem
{
    private readonly Query<Transform, PawnIntent, CharacterController, Melee> _fighters;
    private readonly Query<Viewmodel> _cameras;             // a first-person pawn's arms (issue #121)
    private readonly RecordStore _records;
    private readonly IPhysicsWorld _space;
    private readonly EventReader<AnimationEvent> _animation;
    private readonly List<AnimationEvent> _fired = new();   // the previous tick's animation events,
                                                            // drained once so Lands can ask per entity
    private readonly List<(Entity Fighter, string Trigger)> _swings = new();   // swings started this tick:
                        // their animators are told after the loop (the upgrade may add one, R14)
    private readonly ActionId _attack;
    private readonly DebugDraw _debug;
    private readonly CVar<bool> _debugSwings;
    private readonly Deferred<(HitRequest Request, AttackRecord Attack)> _strikes = new();   // blows that
                        // landed this tick, delivered after the loop: a delivery may spawn a projectile (R14)
    private readonly Deferred<PendingHit> _landed = new();   // what they reached, dealt after every delivery
                        // ran: applying an effect touches another entity's components (R14)

    public MeleeCombatSystem(World world, RecordStore records, ActionRegistry actions, CVar<bool> debugSwings)
    {
        _debug = world.Debug();
        _debugSwings = debugSwings;
        _fighters = world.Query<Transform, PawnIntent, CharacterController, Melee>();
        _cameras = world.Query<Viewmodel>();
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
                var attack = AttackFor(ref m[n], conventions, out var attackId);
                if (attack == null) continue;

                if (m[n].Cooldown > 0f) m[n].Cooldown = MathF.Max(0f, m[n].Cooldown - dt);
                if (m[n].Staggered > 0f) m[n].Staggered = MathF.Max(0f, m[n].Staggered - dt);

                switch (m[n].Phase)
                {
                    case MeleePhase.Ready:
                        bool trigger = i[n].Pressed.Has(_attack) || (attack.Automatic && i[n].Held.Has(_attack));
                        if (trigger && m[n].Cooldown <= 0f && m[n].Staggered <= 0f && !world.HasTag(entity, dead))
                        {
                            // Ammunition (issue #135): an empty magazine clicks and the swing never starts; a
                            // reload in progress swallows the press. Attacks with no `ammo` are always Ok.
                            var spent = Ammunition.Spend(world, entity, attackId, attack);
                            if (spent != Ammunition.Spent.Ok)
                            {
                                if (spent == Ammunition.Spent.Dry)
                                {
                                    world.Events.Send(new DryFire(entity, attackId));
                                    m[n].Cooldown = MathF.Max(attack.Cooldown, 0.25f);   // a held trigger clicks, not buzzes
                                }
                                break;
                            }
                            world.Events.Send(new WeaponFired(entity, attackId));
                            m[n].Phase = MeleePhase.Windup;
                            m[n].Timer = 0f;
                            m[n].Swung = false;
                            m[n].Direction = AttackStances.Of(world, entity);   // the graph reads the same (AttackDirection)
                            // The animator's graph picks the clip (issue #119): combat names none.
                            _swings.Add((entity, string.IsNullOrEmpty(attack.Trigger) ? conventions.Animations.AttackTrigger : attack.Trigger));
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
                        if (!Lands(world, entity, attack, m[n].Timer, conventions.Animations.Hit)) break;
                        _strikes.Add((Request(world, entity, attackId, in t[n], in i[n], in c[n]), attack));
                        m[n].Swung = true;
                        m[n].Timer = 0f;
                        m[n].Phase = MeleePhase.Recover;
                        break;

                    case MeleePhase.Recover:
                        m[n].Timer += dt;
                        if (m[n].Timer < attack.RecoverTime) break;
                        // Going back to standing is the graph's (a transition when the clip is done).
                        m[n].Phase = MeleePhase.Ready;
                        m[n].Timer = 0f;
                        m[n].Cooldown = attack.RateOfFire > 0f
                            ? MathF.Max(0f, 1f / attack.RateOfFire - attack.WindupTime - attack.RecoverTime)
                            : attack.Cooldown;
                        break;
                }
            }
        }

        // Same tick as the press, before the Animation phase: the graph enters its swing this tick, as a
        // sprite clip played here used to (12 "As built (animation events)").
        for (int k = 0; k < _swings.Count; k++)
        {
            var (fighter, trigger) = _swings[k];
            if (!world.IsAlive(fighter) || string.IsNullOrEmpty(trigger)) continue;
            SpriteFighterUpgrade.Apply(world, fighter, conventions, _records);
            Animators.SetTrigger(world, fighter, trigger);   // false without an animator: a body with no art still fights
        }
        _swings.Clear();

        // Every blow that landed goes out by its delivery (issue #133) — nothing here moves a body, so a
        // sweep finds what it found inside the loop, on the same tick — and then everything they reached
        // is dealt, in the order the blows landed.
        var debug = _debugSwings.Value && _debug.Enabled ? _debug : null;
        foreach (var (request, attack) in _strikes.Drain())
        {
            // Spread and recoil (issue #136): the cone is what the shots so far and the stance make it,
            // the shot is its number for the deterministic random, and the bloom and kick follow the blow.
            SpreadRecord? spread = attack.Spread.IsEmpty ? null : _records.TryGet(attack.Spread.Id, out SpreadRecord sr) ? sr : null;
            RecoilRecord? recoil = attack.Recoil.IsEmpty ? null : _records.TryGet(attack.Recoil.Id, out RecoilRecord rr) ? rr : null;
            float cone = spread == null ? 0f : Spread.ConeOf(world, request.Attacker, spread);
            uint shot = world.TryGet<WeaponState>(request.Attacker, out var weapon) ? weapon.Shots : 0u;
            if (HitDeliveries.Of(world, attack) is { } delivery)
                delivery.Deliver(new HitContext
                {
                    World = world, Space = _space, Request = request, Attack = attack, Debug = debug, Landed = _landed,
                    Cone = cone, Shot = shot,
                });
            Sage.Gameplay.Spread.Fired(world, request.Attacker, spread, recoil);
        }
        foreach (var hit in _landed.Drain())
            Combat.ApplyHit(world, hit.Request, hit.Result, hit.Attack);
    }

    private AttackRecord? AttackFor(ref Melee melee, GameplayConventionsRecord conventions, out RecordId id)
    {
        id = melee.Attack.IsEmpty ? conventions.Attack.Id : melee.Attack;
        if (id.IsEmpty) return null;   // given no attack, and the game has no default: it cannot swing
        if (_records.TryGet(id, out AttackRecord record)) return record;
        Log.Once(LogCat.Gameplay, LogLevel.Error, $"attack:{id}", $"No attack record {id}: nothing can swing it");
        return null;
    }

    // When the blow lands: on the animation's hit event (the conventions name it: "hit" in the engine's)
    // if the clip has one, otherwise on the record's windup time. The event is raised in the Animation
    // phase, which runs after this one, so a hit lands one tick (16 ms) after the frame that shows it —
    // a sprite's frame event and a skinned clip's anim_events alike (issue #119 kept that timing).
    // A first-person pawn's arms (issue #121) are an animator of their own: their clip's `hit` lands the
    // pawn's blow too, so a swing seen in first person connects when the arms show it connecting.
    private bool Lands(World world, Entity entity, AttackRecord attack, float timer, string hit)
    {
        if (timer >= attack.WindupTime) return true;
        if (hit.Length == 0 || _fired.Count == 0) return false;
        if (Fired(entity, hit)) return true;
        var camera = Viewmodels.CameraOf(world, _cameras, entity);
        if (camera.IsNull) return false;
        var arms = Viewmodels.ArmsOf(world, camera);
        return !arms.IsNull && Fired(arms, hit);
    }

    private bool Fired(Entity entity, string name)
    {
        for (int i = 0; i < _fired.Count; i++)
            if (_fired[i].Entity == entity && string.Equals(_fired[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // The blow leaving the fighter: from its eye, along where its pawn looks (yaw and pitch).
    private HitRequest Request(World world, Entity attacker, RecordId attack, in Transform transform, in PawnIntent intent,
                               in CharacterController character)
    {
        var profile = CharacterConventions.Of(world).ProfileOf(_records, character.Profile);
        Vector3 eye = CharacterController.EyeOf(transform.LocalPosition, in character, profile);
        Vector3 aim = Vector3.Transform(TransformMath.Forward, Quaternion.CreateFromYawPitchRoll(intent.Yaw, intent.Pitch, 0));
        return new HitRequest(attacker, eye, aim, attack);
    }
}
