#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Sage.Gameplay;

// Gameplay phase, before effects tick: turns a queued cast into effects on whatever it reaches
// (docs/design/16 §3.3, TODO F21). Ordered like melee for the same reason — a spell cast this tick is
// felt this tick, health and tags and death landing together (16 §3.2).
//
// The shape is deliberately the same as `MeleeCombatSystem`: gates, a wind-up, then a resolve. A
// spell and a swing are the same kind of thing at this level, which is why `Melee` keeps its own
// timing rather than being folded in — a swing is an ability whose effect is a weapon, and when
// that is worth unifying it will be obvious. It is not yet.
[System("sage.abilities.cast", Phase.Gameplay, Before = new[] { "sage.effects.tick" })]
public sealed class AbilitySystem : ISystem
{
    private readonly Query<Transform, PawnIntent, Abilities> _casters;
    private readonly ActionId _cast;
    private readonly RecordStore _records;
    private readonly IPhysicsWorld _space;
    private readonly AbilityPayload _payload;
    private readonly DebugDraw _debug;
    private readonly CVar<bool> _debugCasts;

    // Resolved after the loop: applying an effect touches another entity's components (R14).
    private readonly Deferred<Pending> _pending = new();

    private readonly record struct Pending(Entity Caster, RecordId Ability, Vector3 Point, Vector3 Aim);

    public AbilitySystem(World world, RecordStore records, ActionRegistry actions, CVar<bool> debugCasts)
    {
        _casters = world.Query<Transform, PawnIntent, Abilities>();
        _cast = actions.Get(world.Conventions().Actions.Cast);
        _records = records;
        _space = world.Resources.Get<IPhysicsWorld>();
        _payload = new AbilityPayload(world);
        _debug = world.Debug();
        _debugCasts = debugCasts;
    }

    public void Run(in SystemContext ctx)
    {
        var world = ctx.World;
        float dt = ctx.Tick.Dt;

        foreach (var (transforms, intents, abilities, entities) in _casters.Chunks)
        {
            var t = transforms.Span;
            var i = intents.Span;
            var a = abilities.Span;
            for (int n = 0; n < a.Length; n++)
            {
                var entity = entities.EntityAt(n);

                // Pressing Cast fires the readied spell, which is how a player casts; `world.Cast`
                // is how everything else does (an AI task, the console), and both arrive here.
                if (i[n].Pressed.Has(_cast) && !a[n].Selected.IsEmpty && a[n].Queued.IsEmpty)
                    a[n].Queued = a[n].Selected;

                if (!a[n].Queued.IsEmpty)
                {
                    var wanted = a[n].Queued;
                    a[n].Queued = default;
                    Begin(world, entity, ref a[n], wanted);
                }

                if (a[n].Casting.IsEmpty) continue;
                if (!_records.TryGet(a[n].Casting, out AbilityRecord record)) { a[n].Casting = default; continue; }

                a[n].Timer += dt;
                if (a[n].Timer < record.CastTime) continue;   // still winding up

                var aim = Vector3.Transform(TransformMath.Forward,
                    Quaternion.CreateFromYawPitchRoll(i[n].Yaw, i[n].Pitch, 0));
                _pending.Add(new Pending(entity, a[n].Casting, Origin(world, entity, in t[n]), aim));
                a[n].Casting = default;
                a[n].Timer = 0f;
            }
        }

        foreach (var cast in _pending.Drain()) Resolve(world, cast);
    }

    // Applies the gates and, if they all pass, starts the wind-up. The gates themselves live in
    // `AbilityRules` so that asking "could I cast this?" and actually casting it cannot answer
    // differently — an AI deciding to cast (16 §3.4) is the first thing that needed to ask.
    private void Begin(World world, Entity entity, ref Abilities abilities, RecordId ability)
    {
        if (!AbilityRules.CanCast(world, _records, entity, ability, in abilities, out var record, out var why))
        {
            Refuse(world, entity, ability, why);
            return;
        }

        // Paid for at the start of the cast, like every game that has ever had an interrupt: the mana
        // is gone whether or not the spell lands, and the cooldown starts now rather than on impact.
        Spend(world, entity, record);
        if (!record.Cooldown.IsEmpty) Effects.Apply(world, entity, record.Cooldown, entity);

        abilities.Casting = ability;
        abilities.Timer = 0f;
        world.PlayClip(entity, record.Animation, _records);
    }

    // Spending is an effect with a magnitude, exactly as damage is (16 §3.3): the record says
    // "this attribute, -1", the cast says how many. Nothing subtracts a pool directly anywhere.
    private void Spend(World world, Entity entity, AbilityRecord record)
    {
        if (record.CostAttribute.IsEmpty || record.Cost <= 0f) return;
        var spend = _records.TryGet(record.CostAttribute, out AttributeRecord pool) ? pool.SpendEffect : default;
        if (spend.IsEmpty)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Warn, $"no-spend-effect:{record.CostAttribute}",
                $"{record.CostAttribute} has no \"spendEffect\", so abilities that cost it are free (16 §3.3)");
            return;
        }
        Effects.Apply(world, entity, spend, entity, record.Cost);
    }

    private void Refuse(World world, Entity entity, RecordId ability, CastRefusal why)
    {
        world.Events.Send(new CastRefused(entity, ability, why));
        Log.Debug(LogCat.Gameplay, $"{World.Describe(entity)} cannot cast {ability}: {why}");
    }

    private Vector3 Origin(World world, Entity entity, in Transform transform)
    {
        if (!world.TryGet<CharacterController>(entity, out var character)) return transform.LocalPosition;
        var profile = CharacterConventions.Of(world).ProfileOf(_records, character.Profile);
        return CharacterController.EyeOf(transform.LocalPosition, in character, profile);
    }

    // ---- landing it ------------------------------------------------------------------------------

    private void Resolve(World world, in Pending cast)
    {
        if (!world.IsAlive(cast.Caster)) return;
        if (!_records.TryGet(cast.Ability, out AbilityRecord record)) return;

        // The spell leaving the caster, wherever it is going: a projectile launched here, or a burst
        // that happens in the same breath. Where it *lands* is the payload's own cues.
        foreach (var cue in record.CastCues) world.Events.Send(new CueTriggered(cue, cast.Caster, cast.Point));

        // Where it lands is the delivery's (issue #28); one that lands later — a projectile, which
        // raises its `Cues` where it arrives — returns false and the payload waits for it.
        if (AbilityDeliveries.Of(world, record) is not { } delivery) return;
        var release = new AbilityRelease
        {
            World = world, Caster = cast.Caster, Ability = cast.Ability, Record = record,
            Origin = cast.Point, Aim = cast.Aim, Space = _space,
        };
        if (!delivery.Release(in release, out var point, out var struck)) return;

        _payload.Deliver(world, cast.Caster, cast.Ability, record, point, struck);

        if (_debugCasts.Value) Draw(cast, record, point);
    }

    private void Draw(in Pending cast, AbilityRecord record, Vector3 point)
    {
        _debug.Line(cast.Point, point, DebugColour.Magenta, 1.5f);
        if (record.Radius > 0f) _debug.Sphere(point, record.Radius, DebugColour.Magenta, 1.5f);
        foreach (var target in _payload.Targets) if (!target.IsNull) _debug.Cross(point, 0.3f, DebugColour.Yellow, 1.5f);
    }
}
