#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Gameplay;

// One hit pipeline (issue #133, phase 4e; docs/design/16 "As built (the hit pipeline)"). Every strike —
// a sword's swing, a pistol's shot, a crossbow's bolt — is the same three steps:
//
//   HitRequest   who strikes, from where, which way, with which `attack`
//     -> a hit_delivery   how it gets there: `sweep` (a swing), `ray` (hitscan), `projectile` (a carrier
//                         that flies and lands later); each landing is a HitResult
//     -> Combat.ApplyHit  factions, then the damage pipeline (resistance, an effect on health, Damaged)
//
// The attack system (`sage.combat.melee`, still `MeleeCombatSystem` and still `sage:melee`) presses the
// button and times the blow; the delivery is the attack record's `delivery`, `sweep` by default, which is
// the swing the engine always had. Abilities share the queries (`Hits.Sweep`, `Hits.Carrier`).

// A strike leaving its attacker: from `Origin` (the eye, for a character) along `Aim` (unit length).
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public readonly record struct HitRequest(Entity Attacker, Vector3 Origin, Vector3 Aim, RecordId Attack);

// Where a strike landed. `Target` is what takes the damage; `Collider` is the entity the physics query
// touched — the target itself, or one of its hitboxes (issue #137, a child collider on a limb) — and
// `Location` is the hit_location that hitbox names (empty: the body).
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public readonly record struct HitResult(Entity Target, Vector3 Point, Vector3 Normal, Entity Collider, RecordId Location)
{
    // A bare damage request as a landing: where it says, on what it says.
    public static HitResult Of(in DamageInfo damage) =>
        new(damage.Target, damage.Point, -damage.Direction, damage.Target, damage.Location);
}

// How an attack gets to what it hits, as an open vocabulary beside `ability_delivery` (issue #133). The
// engine's are `sweep`, `ray` and `projectile`; an attack's `delivery` names one:
//
//   [HitDelivery("cleave", Plugin = "mygame")] public sealed class Cleave : IHitDelivery { … }
//   { "type": "attack", "id": "greataxe", "delivery": "cleave" }
//
// A delivery runs when the blow lands (after the windup or on the clip's `hit` event), outside the attack
// system's query, so it may spawn things. It calls `hit.Land` once per target it reached — none for a
// miss, one per pellet that connects — and the attack system deals them all after every delivery ran.
[Vocabulary("hit_delivery")]
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public interface IHitDelivery
{
    void Deliver(in HitContext hit);
}

[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public sealed class HitDeliveryAttribute : VocabularyEntryAttribute<IHitDelivery>
{
    public HitDeliveryAttribute(string id) : base(id) { }
}

// One strike being delivered: the request, its record and where to put what it hit.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public readonly ref struct HitContext
{
    public World World { get; init; }
    public IPhysicsWorld Space { get; init; }
    public HitRequest Request { get; init; }
    public AttackRecord Attack { get; init; }
    // Set when `combat_debug` draws strikes (with `r_debugdraw 1`); null otherwise.
    public DebugDraw? Debug { get; init; }
    internal Deferred<PendingHit>? Landed { get; init; }

    // It reached `result`: dealt after every delivery this tick has run.
    public void Land(in HitResult result) => Landed?.Add(new PendingHit(Request, result, Attack));
}

internal readonly record struct PendingHit(HitRequest Request, HitResult Result, AttackRecord Attack);

// The queries every delivery (and `ability_delivery`) is made of.
[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public static class Hits
{
    // A sphere of `radius` swept from `origin` along `aim` for `reach` (10 §4): the first solid thing it
    // touches, scenery included, and never `attacker` (the sweep starts inside it). Something else it
    // starts inside is hit at distance 0 (issue #30). `result.Point` is on the aim line where the sphere
    // stopped, or at full reach on a miss; false and an empty `Target` for a miss.
    // A strike that meets a hitbox (issue #137) lands on the box's owner, `Collider` the box and
    // `Location` its hit_location; one that meets only a capsule lands on the body (an empty location).
    public static bool Sweep(IPhysicsWorld space, Entity attacker, Vector3 origin, Vector3 aim, float radius, float reach,
                             out HitResult result)
    {
        var sphere = Collider.Sphere(radius);
        var from = new Pose { Position = origin, Rotation = Quaternion.Identity, Scale = Vector3.One };
        var hit = space.Sweep(sphere, from, aim, reach, LayerMask.All, ignore: attacker);
        bool solid = hit.Hit && !hit.Entity.IsNull && hit.Entity != attacker;

        // Then the hitboxes (issue #137), which no query for solid things sees: one in front of what the
        // sweep met, or inside its owner's capsule, is where the blow landed.
        var boxes = Hitboxes.Mask(space);
        if (boxes.Bits != 0)
        {
            var box = space.Sweep(sphere, from, aim, reach, boxes, ignore: attacker);
            if (box.Hit && Hitboxes.Landed(attacker, box.Entity, box.Distance, solid ? hit.Entity : default, hit.Distance, out var struck))
            {
                result = new HitResult(struck.Owner, origin + aim * box.Distance, box.Normal, box.Entity, struck.Location);
                return true;
            }
        }

        if (!solid)
        {
            result = new HitResult(default, origin + aim * reach, -aim, default, default);
            return false;
        }
        result = new HitResult(hit.Entity, origin + aim * hit.Distance, hit.Normal, hit.Entity, default);
        return true;
    }

    // A ray from `origin` along `aim` for `range`: the first solid thing, never `attacker`. The point and
    // normal are the surface's. False and an empty `Target` for a miss. Hitboxes as for Sweep.
    public static bool Ray(IPhysicsWorld space, Entity attacker, Vector3 origin, Vector3 aim, float range, out HitResult result)
    {
        var hit = space.Raycast(origin, aim, range, LayerMask.All, ignore: attacker);
        bool solid = hit.Hit && !hit.Entity.IsNull && hit.Entity != attacker;

        // Then the hitboxes (issue #137), as for a sweep.
        var boxes = Hitboxes.Mask(space);
        if (boxes.Bits != 0)
        {
            var box = space.Raycast(origin, aim, range, boxes, ignore: attacker);
            if (box.Hit && Hitboxes.Landed(attacker, box.Entity, box.Distance, solid ? hit.Entity : default, hit.Distance, out var struck))
            {
                result = new HitResult(struck.Owner, box.Position, box.Normal, box.Entity, struck.Location);
                return true;
            }
        }

        if (!solid)
        {
            result = new HitResult(default, origin + aim * range, -aim, default, default);
            return false;
        }
        result = new HitResult(hit.Entity, hit.Position, hit.Normal, hit.Entity, default);
        return true;
    }

    // Can this take a hit at all? Scenery can't: a strike just stops against it.
    public static bool CanBeHurt(World world, Entity entity) =>
        !entity.IsNull && world.IsAlive(entity) && world.Has<Attributes>(entity);

    // Throws an attack's carrier (`sage:projectile`, the one abilities fly on): it lands through
    // Combat.ApplyHit where it stops (ProjectileSystem). Minimal until issue #134: straight flight at the
    // attack's `projectileSpeed` for its `range`, as fat as its `radius`, with no prefab (invisible).
    public static Entity Launch(World world, in HitRequest request, AttackRecord attack) =>
        Carrier(world, request.Attacker, default, request.Attack.Name, request.Origin + request.Aim * 0.4f, request.Aim,
                attack.ProjectileSpeed, attack.Range, attack.Radius, default, request.Attack);

    // The one projectile carrier, for abilities and attacks alike: the prefab is what it looks like (or
    // a bare entity named `name`), and this adds the flight.
    internal static Entity Carrier(World world, Entity caster, RecordId prefab, string name, Vector3 from, Vector3 direction,
                                   float speed, float range, float radius, RecordId ability, RecordId attack)
    {
        var entity = prefab.IsEmpty
            ? world.Create(Transform.At(from), name)
            : world.Spawn(prefab, from, SageMath.YawOf(direction) * 180f / MathF.PI);

        if (entity.IsNull) return entity;
        if (!world.Has<Transform>(entity)) return entity;
        world.Get<Transform>(entity).LocalPosition = from;

        speed = MathF.Max(speed, 0.1f);
        world.Add(entity, new Projectile
        {
            Ability = ability,
            Attack = attack,
            Caster = caster,
            Velocity = Vector3.Normalize(direction) * speed,
            // Enough time to cross its own range, with a little slack, so something that never hits
            // anything bursts at the far end instead of flying to the edge of the world.
            Life = MathF.Max(range, 1f) / speed + 0.2f,
            Radius = radius,
        });
        return entity;
    }
}

[Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // phase 4e: may change before 1.0
public static class HitDeliveries
{
    public const string Default = "sweep";

    public static string NameOf(AttackRecord attack) => attack.Delivery.Length > 0 ? attack.Delivery : Default;

    // The attack's delivery, found once and remembered on the record. Null (said once) when it names one
    // nobody registered: the blow lands on nothing.
    public static IHitDelivery? Of(World world, AttackRecord attack)
    {
        string name = NameOf(attack);
        if (attack.DeliveryInstance != null && ReferenceEquals(attack.DeliveryFor, name)) return attack.DeliveryInstance;
        var found = world.Engine?.Vocabularies.Of<IHitDelivery>().Find(name) ?? BuiltIn(name);
        if (found == null)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"hit_delivery:{name}",
                $"no hit delivery '{name}' is registered; attacks that name it hit nothing");
            return null;
        }
        attack.DeliveryInstance = found;
        attack.DeliveryFor = name;
        return found;
    }

    // A world with no engine (a bare test world) still has the engine's three.
    private static IHitDelivery? BuiltIn(string name) => name switch
    {
        "sweep" => new SweepDelivery(),
        "ray" => new RayDelivery(),
        "projectile" => new ProjectileHitDelivery(),
        _ => null,
    };

    // An attack's `delivery` must be one somebody registered (CombatModule adds this at load).
    public static void Check(Vocabularies vocabularies, AttackRecord attack, RecordCheck check)
    {
        if (attack.Delivery.Length == 0) return;
        var deliveries = vocabularies.Of<IHitDelivery>();
        if (!deliveries.Contains(attack.Delivery)) check.Error(nameof(AttackRecord.Delivery), deliveries.Unknown(attack.Delivery));
    }
}

// ---- the engine's deliveries ----------------------------------------------------------------------

// A swing (the engine's melee since F20, moved here from MeleeCombatSystem): a sphere of the attack's
// `radius` swept from the eye along the aim for its `reach`, the first solid thing it touches, then an
// arc check so a target at the edge of your vision doesn't count. One target per swing; cleaving through
// several is a later flag. Scenery stops it and is not hurt.
[HitDelivery("sweep", Plugin = "sage.gameplay.combat")]
internal sealed class SweepDelivery : IHitDelivery
{
    public void Deliver(in HitContext hit)
    {
        var world = hit.World;
        var request = hit.Request;
        var attack = hit.Attack;

        // Every solid thing counts, including the attacker's own kind: a swing is physical, and who it
        // is *allowed* to hurt is a rules question (factions, F24), answered by Combat.ApplyHit.
        bool found = Hits.Sweep(hit.Space, request.Attacker, request.Origin, request.Aim, attack.Radius, attack.Reach, out var result);
        bool connects = found && Connects(world, request, attack, result.Target);

        if (hit.Debug is { } debug)
        {
            uint colour = connects ? DebugColour.Green : DebugColour.Red;
            var end = request.Origin + request.Aim * attack.Reach;
            debug.Arrow(request.Origin, end, colour, 0.6f);
            debug.Sphere(end, attack.Radius, colour, 0.6f);
            if (connects) debug.Cross(result.Point, 0.25f, DebugColour.Yellow, 0.6f);
        }

        if (connects) hit.Land(result);
    }

    // Did the sweep find something this swing is allowed to hurt? A target dead ahead is the easy case;
    // the arc decides how much of a glancing angle counts, measured from the attacker's feet along the
    // way its pawn faces.
    private static bool Connects(World world, in HitRequest request, AttackRecord attack, Entity target)
    {
        if (!Hits.CanBeHurt(world, target)) return false;   // scenery: the swing just stops
        if (!world.TryGet<Transform>(request.Attacker, out var self) || !world.TryGet<Transform>(target, out var other)) return false;
        float yaw = world.TryGet<PawnIntent>(request.Attacker, out var intent) ? intent.Yaw : SageMath.YawOf(request.Aim);
        return SageMath.InCone(yaw, Vector3.Zero, other.LocalPosition - self.LocalPosition, attack.ArcDegrees);
    }
}

// Hitscan: a ray from the eye along the aim for the attack's `range`, landing on the surface it meets —
// instantly, with no flight time. `pellets` rays per shot (spread arrives with issue #136, so today they
// all fly the same line and each lands its own hit). Scenery stops it and is not hurt.
[HitDelivery("ray", Plugin = "sage.gameplay.combat")]
internal sealed class RayDelivery : IHitDelivery
{
    public void Deliver(in HitContext hit)
    {
        var request = hit.Request;
        int pellets = Math.Max(1, hit.Attack.Pellets);
        for (int p = 0; p < pellets; p++)
        {
            bool found = Hits.Ray(hit.Space, request.Attacker, request.Origin, request.Aim, hit.Attack.Range, out var result);
            bool hurts = found && Hits.CanBeHurt(hit.World, result.Target);
            hit.Debug?.Line(request.Origin, result.Point, hurts ? DebugColour.Green : DebugColour.Red, 0.6f);
            if (hurts) hit.Land(result);
        }
    }
}

// A carrier that flies (`sage:projectile`, abilities' own) and lands through Combat.ApplyHit where it
// stops: nothing lands now. One per pellet. Minimal until issue #134 (arcs, a prefab, piercing).
[HitDelivery("projectile", Plugin = "sage.gameplay.combat")]
internal sealed class ProjectileHitDelivery : IHitDelivery
{
    public void Deliver(in HitContext hit)
    {
        int pellets = Math.Max(1, hit.Attack.Pellets);
        for (int p = 0; p < pellets; p++) Hits.Launch(hit.World, hit.Request, hit.Attack);
    }
}
