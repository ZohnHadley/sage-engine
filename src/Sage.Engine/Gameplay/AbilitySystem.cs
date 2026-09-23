#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Gameplay phase, before effects tick: turns a queued cast into effects on whatever it reaches
// (docs/design/16 §3.3, TODO F21). Ordered like melee for the same reason — a spell cast this tick is
// felt this tick, health and tags and death landing together (16 §3.2).
//
// The shape is deliberately the same as `MeleeCombatSystem`: gates, a wind-up, then a resolve. A
// spell and a swing are the same kind of thing at this level, which is why `Melee` keeps its own
// timing rather than being folded in — a swing is an ability whose effect is a weapon, and when
// that is worth unifying it will be obvious. It is not yet.
public sealed class AbilitySystem : ISystem
{
    private readonly ArchetypeQuery<Transform, PawnIntent, Abilities> _casters;
    private readonly ActionId _cast;
    private readonly RecordStore _records;
    private readonly PhysicsSpace _space;
    private readonly DebugDraw _debug;
    private readonly CVar<bool> _debugCasts;

    // Resolved after the loop: applying an effect touches another entity's components (R14).
    private readonly Deferred<Pending> _pending = new();
    private readonly List<Entity> _targets = new();
    private readonly Entity[] _nearby = new Entity[64];

    private readonly record struct Pending(Entity Caster, RecordId Ability, Vector3 Point, Vector3 Aim);

    public AbilitySystem(World world, RecordStore records, ActionRegistry actions, CVar<bool> debugCasts)
    {
        _casters = world.Query<Transform, PawnIntent, Abilities>();
        _cast = actions.Get("Cast");
        _records = records;
        _space = world.Resources.Get<PhysicsSpace>();
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

    // The gates, in the order a player would think of them: do I know it, is it ready, can I afford
    // it, am I allowed. Each refusal says which, so a HUD can say why without guessing.
    private void Begin(World world, Entity entity, ref Abilities abilities, RecordId ability)
    {
        if (!abilities.Casting.IsEmpty) { Refuse(world, entity, ability, CastRefusal.AlreadyCasting); return; }
        if (abilities.Known == null || !abilities.Known.Contains(ability)) { Refuse(world, entity, ability, CastRefusal.NotKnown); return; }
        if (!_records.TryGet(ability, out AbilityRecord record)) { Refuse(world, entity, ability, CastRefusal.NotKnown); return; }

        if (OnCooldown(world, entity, record)) { Refuse(world, entity, ability, CastRefusal.OnCooldown); return; }
        if (!TagsAllow(world, entity, record)) { Refuse(world, entity, ability, CastRefusal.Blocked); return; }
        if (!CanAfford(world, entity, record)) { Refuse(world, entity, ability, CastRefusal.TooExpensive); return; }

        // Paid for at the start of the cast, like every game that has ever had an interrupt: the mana
        // is gone whether or not the spell lands, and the cooldown starts now rather than on impact.
        Spend(world, entity, record);
        if (!record.Cooldown.IsEmpty) Effects.Apply(world, entity, record.Cooldown, entity);

        abilities.Casting = ability;
        abilities.Timer = 0f;
        world.PlayClip(entity, record.Animation, _records);
    }

    // An ability is on cooldown when the caster already has a tag its cooldown effect grants. No
    // second clock, and dispelling the tag makes it ready again (16 §3.3).
    private bool OnCooldown(World world, Entity entity, AbilityRecord record)
    {
        if (record.Cooldown.IsEmpty || !_records.TryGet(record.Cooldown, out EffectRecord cooldown)) return false;
        foreach (var tag in cooldown.GrantTags)
            if (world.HasTag(entity, tag)) return true;
        return false;
    }

    private bool TagsAllow(World world, Entity entity, AbilityRecord record)
    {
        foreach (var tag in record.RequireTags) if (!world.HasTag(entity, tag)) return false;
        foreach (var tag in record.BlockTags) if (world.HasTag(entity, tag)) return false;
        return true;
    }

    private bool CanAfford(World world, Entity entity, AbilityRecord record) =>
        record.CostAttribute.IsEmpty || record.Cost <= 0f ||
        world.Attribute(entity, record.CostAttribute) >= record.Cost;

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
        var profile = _records.TryGet(character.Profile.IsEmpty ? MovementProfileRecord.Default : character.Profile,
                                      out MovementProfileRecord found) ? found : MovementProfileRecord.Fallback;
        return CharacterController.EyeOf(transform.LocalPosition, in character, profile);
    }

    // ---- landing it ------------------------------------------------------------------------------

    private void Resolve(World world, in Pending cast)
    {
        if (!world.IsAlive(cast.Caster)) return;
        if (!_records.TryGet(cast.Ability, out AbilityRecord record)) return;

        _targets.Clear();
        Vector3 point = cast.Point;

        switch (record.Targeting)
        {
            case AbilityTargeting.Self:
                _targets.Add(cast.Caster);
                break;

            case AbilityTargeting.Area:
                Gather(world, cast.Point, record.Radius, cast.Caster, includeCaster: true);
                break;

            case AbilityTargeting.Touch:
            case AbilityTargeting.TouchArea:
            {
                // `Width`, not `Radius`: how fat the bolt is on its way, not how wide it bursts.
                // Sweeping with the burst radius makes a fireball start already overlapping its own
                // caster, and an overlapping sweep reports nothing at all (10 §4, review #55) — so a
                // three-metre burst reached exactly nothing.
                var hit = _space.Sweep(Collider.Sphere(MathF.Max(record.Width, 0.05f)),
                    new Pose { Position = cast.Point, Rotation = Quaternion.Identity, Scale = Vector3.One },
                    cast.Aim, record.Range, LayerMask.All);

                point = hit.Entity.IsNull || hit.Entity == cast.Caster
                    ? cast.Point + cast.Aim * record.Range
                    : cast.Point + cast.Aim * hit.Distance;

                if (record.Targeting == AbilityTargeting.TouchArea) Gather(world, point, record.Radius, cast.Caster, includeCaster: false);
                else if (CanBeAffected(world, hit.Entity) && hit.Entity != cast.Caster) _targets.Add(hit.Entity);
                break;
            }
        }

        foreach (var target in _targets)
        {
            if (!world.IsAlive(target)) continue;

            // Damage first, through the one pipeline (16 §3.2): resistance, then an effect on health,
            // then a Damaged event. Its riders are applied with it, so a target that shrugs the whole
            // thing off shrugs off the burning as well, the way a poisoned blade already works.
            if (record.Damage > 0f)
                Combat.ApplyDamage(world, new DamageInfo(cast.Caster, target, record.DamageType, record.Damage,
                    point, Vector3.Normalize(SafeDirection(world, target, point))), record.Effects);
            else
                foreach (var effect in record.Effects)
                    Effects.Apply(world, target, effect, cast.Caster, record.Magnitude);
        }

        world.Events.Send(new AbilityCast(cast.Caster, cast.Ability, point, _targets.Count));
        foreach (var cue in record.Cues) world.Events.Send(new CueTriggered(cue, cast.Caster, point));

        if (_debugCasts.Value) Draw(cast, record, point);
    }

    // Everything solid within `radius`. Who a spell is *allowed* to burn is a rules question
    // (factions, F24), not a physics one, so this gathers all of it and the caster is the only
    // special case — and only when the ability says so.
    //
    // The space offers a box, and the box is the broad phase: the distance check below is what makes
    // the burst round. `OverlapBox` can also report things whose shapes don't quite touch (10 §4),
    // which matters less for a blast than it would for a sword.
    private void Gather(World world, Vector3 point, float radius, Entity caster, bool includeCaster)
    {
        if (radius <= 0f) return;
        int count = _space.OverlapBox(point, new Vector3(radius), _nearby, LayerMask.All);
        for (int i = 0; i < count; i++)
        {
            var entity = _nearby[i];
            if (!CanBeAffected(world, entity) || (!includeCaster && entity == caster)) continue;
            if (!world.TryGet<Transform>(entity, out var transform)) continue;
            if (Vector3.Distance(transform.LocalPosition, point) > radius + 0.5f) continue;   // +half a body
            if (!_targets.Contains(entity)) _targets.Add(entity);
        }
    }

    // Away from the burst, so a knockback (later) pushes outward. Never a zero vector, which
    // Normalize would turn into NaN and quietly poison a transform.
    private static Vector3 SafeDirection(World world, Entity target, Vector3 point)
    {
        var to = world.TryGet<Transform>(target, out var transform) ? transform.LocalPosition - point : Vector3.Zero;
        return to.LengthSquared() > 1e-6f ? to : Vector3.UnitY;
    }

    // A blast lands on the world, and most of the world is scenery. Only things that can *hold* an
    // effect are targets: a fireball bursting against a tree is a normal Tuesday, not a mis-set-up
    // entity, and warning about it once per trunk would bury the log (the warning in Effects.Apply
    // is for the other case — a creature somebody forgot to give attributes to).
    private static bool CanBeAffected(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.Has<Attributes>(entity);

    private void Draw(in Pending cast, AbilityRecord record, Vector3 point)
    {
        _debug.Line(cast.Point, point, DebugColour.Magenta, 1.5f);
        if (record.Radius > 0f) _debug.Sphere(point, record.Radius, DebugColour.Magenta, 1.5f);
        foreach (var target in _targets) if (!target.IsNull) _debug.Cross(point, 0.3f, DebugColour.Yellow, 1.5f);
    }
}
