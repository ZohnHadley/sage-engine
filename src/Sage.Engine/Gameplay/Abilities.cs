#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Abilities and magic (docs/design/16 §3.3, TODO F21). A Daggerfall-like lives or dies on its spells,
// and the point of this one is that **it adds almost no new gameplay machinery**: an ability is a
// cost, a cooldown, a way of choosing targets, and a list of effects. Everything it does to anybody
// is an effect (16 §3.3), which is the rule F18 established and F20 followed — so the `god` tag stops
// a fireball for the same reason it stops a sword, saves see spells for free, and a spell that both
// burns and slows is two effect ids rather than any new code.
//
// The two things that *are* new are the gates: you must be able to afford it, and it must be ready.

// How an ability picks what it lands on.
public enum AbilityTargeting
{
    Self,        // the caster
    Touch,       // the first thing within Range along the aim, like a swing
    Area,        // everything within Radius of the caster
    TouchArea,   // everything within Radius of the point Touch would have hit, *instantly*
    Projectile,  // a thing that flies, and delivers the same payload where it arrives
}

[Record("ability")]
public sealed class AbilityRecord
{
    public string Name = "";

    // What it draws on and how much. The spend is an *effect*, not a subtraction: the pool goes down
    // through the one path everything else uses, so a "free casting" buff is an effect like any other.
    public RecordId CostAttribute;
    public float Cost;

    // The effect applied to the caster on a successful cast, whose granted tags then block the next
    // one (16 §3.3). A cooldown is therefore dispellable, survives a save and needs no second clock.
    public RecordId Cooldown;

    public AbilityTargeting Targeting = AbilityTargeting.Self;
    public float Range = 8f;      // how far Touch and TouchArea reach
    public float Radius = 0f;     // the *burst*: how far from where it lands Area and TouchArea catch
    public float Width = 0.2f;    // how fat the thing that travels is, for what Touch can hit
    public float CastTime;        // seconds of wind-up before it goes off

    // Damage goes through the combat pipeline, not straight into an effect: that is where the
    // damage type's resistance is applied and where the `Damaged` event comes from (16 §3.2). So
    // fire_resist means something against a fireball for the same reason armour means something
    // against a sword, and a kill by spell names its killer like any other.
    public float Damage;
    public RecordId DamageType;               // empty with Damage > 0 means physical

    public List<RecordId> Effects = new();    // and whatever else it does, to whatever it lands on

    // Scales the effects (GAS's set-by-caller) — but only when the ability does no `Damage`. With
    // damage, they ride along with the hit and are applied as written, which is the rule a poisoned
    // blade already follows (16 §3.2): they are blocked with the damage rather than separately, and
    // "as written" is what makes that one rule instead of two.
    public float Magnitude = 1f;

    public List<RecordId> RequireTags = new();   // the caster must have all of these
    public List<RecordId> BlockTags = new();     // and none of these

    // Presentation only (§3.3): sounds, particles, a flash. **A cue has a moment**, which is the whole
    // reason there are two lists: `CastCues` are raised where the spell leaves the caster and `Cues`
    // where it does its work. One list raised at both ends means a burst heard in the caster's hand,
    // which is what the first version did.
    //
    // For an instant spell the two moments are the same instant, and that is correct: a self-buff is
    // cast and takes effect on you at once.
    public List<RecordId> CastCues = new();
    public List<RecordId> Cues = new();
    public string Animation = "";

    // Projectile targeting only: the prefab that flies (F31 — so what a fireball looks like is data)
    // and how fast. `Radius` is still the burst it makes on arrival, `Width` how fat it is in flight.
    public RecordId Projectile;
    public float ProjectileSpeed = 18f;
}

// A cue is a *name for something to show*, and deliberately almost empty: the simulation raises it,
// and what it looks like is the client's business (16 §3.3). It is a record so that cue ids are
// checked like every other reference, and so the sound and particle fields have somewhere to go when
// audio (11) and particles arrive.
[Record("cue")]
public sealed class CueRecord
{
    public string Description = "";

    // What it sounds like (11 §3, F4). The simulation raises the cue and never learns this field
    // exists; the client's audio system reads it, so a mod can give a spell a new noise without
    // touching the spell.
    public RecordId Sound;
}

// What an entity can cast, and what it is casting. `Known` is a list because a spellbook is a list;
// the cooldowns are tags on `GameplayTags`, so there is nothing to track here (16 §3.3).
public struct Abilities : IComponent
{
    public List<RecordId>? Known;
    public RecordId Selected;     // the readied spell: what the Cast button fires (Daggerfall's)
    [Transient] public RecordId Casting;   // mid-wind-up; a load leaves you not casting
    [Transient] public float Timer;        // seconds into the wind-up
    [Transient] public RecordId Queued;    // asked for this tick, taken by the system

    public static Abilities With(params RecordId[] known) =>
        new() { Known = new List<RecordId>(known), Selected = known.Length > 0 ? known[0] : default };
}

// ---- events -------------------------------------------------------------------------------------

// An ability went off: what, who cast it, where it landed and on whom. Anything that reacts to a
// spell reads this rather than being called by the caster (04 §3.2).
[GameEvent]
public readonly record struct AbilityCast(Entity Caster, RecordId Ability, Vector3 Point, int Targets);

// An ability was refused, and why. A HUD says "not enough mana" from this instead of the cast system
// knowing what a HUD is.
public enum CastRefusal { Unknown, NotKnown, OnCooldown, TooExpensive, Blocked, AlreadyCasting }

[GameEvent]
public readonly record struct CastRefused(Entity Caster, RecordId Ability, CastRefusal Why);

// Presentation only (16 §3.3): a sound, a particle burst, a screen flash. The simulation says *what
// happened*, never what it looks like, and a Frame-schedule system turns these into effects on screen
// — so a headless server sends them into a queue nobody reads (04 §3.2).
[GameEvent]
public readonly record struct CueTriggered(RecordId Cue, Entity Source, Vector3 Point);

// ---- reaching them ------------------------------------------------------------------------------

public static class AbilityExtensions
{
    // Teaches an entity an ability. Kept separate from `Abilities.With` so a scroll or a level-up can
    // add one later without rebuilding the component.
    public static void Teach(this World world, Entity entity, RecordId ability)
    {
        if (!world.Has<Abilities>(entity)) world.Add(entity, Abilities.With());
        ref var abilities = ref world.Get<Abilities>(entity);
        abilities.Known ??= new List<RecordId>();
        if (!abilities.Known.Contains(ability)) abilities.Known.Add(ability);
        if (abilities.Selected.IsEmpty) abilities.Selected = ability;   // the first one learned is readied
    }

    // Readies a spell: what the Cast button fires and what a spellbook screen ticks (Daggerfall's
    // readied spell). Refuses one it does not know, so a stale screen cannot ready nothing.
    public static bool Ready(this World world, Entity entity, RecordId ability)
    {
        if (!world.Knows(entity, ability)) return false;
        world.Get<Abilities>(entity).Selected = ability;
        return true;
    }

    public static bool Knows(this World world, Entity entity, RecordId ability) =>
        world.TryGet<Abilities>(entity, out var abilities) && abilities.Known != null && abilities.Known.Contains(ability);

    // Asks for a cast. The system takes it on its next run, so this is safe to call from anywhere —
    // a console command, an AI task, a pressed button — and the rules are applied in one place.
    public static bool Cast(this World world, Entity entity, RecordId ability)
    {
        if (!world.Has<Abilities>(entity)) return false;
        world.Get<Abilities>(entity).Queued = ability;
        return true;
    }
}
