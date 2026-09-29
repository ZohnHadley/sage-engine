#nullable enable
using System;
using System.Numerics;
using System.Diagnostics.CodeAnalysis;

namespace Sage.Gameplay;

// How an ability gets to what it lands on, as an open vocabulary (docs/REDESIGN.md §4.3, issue #28).
// It was the `AbilityTargeting` enum and a switch in AbilitySystem, with the other half of the answer —
// who a landing affects — in the payload. A delivery is both halves in one place:
//
//   Release  the cast goes off from the caster's eye along its aim: where does it land, and on what?
//            (or false: it lands later, by other means — a projectile flies and arrives)
//   Gather   it has landed at a point: who does the payload reach? (the caster, a burst, what it struck)
//
// The engine's are `self`, `touch`, `touch_area`, `area` and `projectile`, the five the enum had, and
// an ability's `targeting` still names one of them; `delivery` names any registered one and wins:
//
//   [AbilityDelivery("chain", Plugin = "mygame")] public sealed class Chain : IAbilityDelivery { … }
//   { "type": "ability", "id": "arc", "delivery": "chain", "effects": ["shock"] }
[Vocabulary("ability_delivery")]
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public interface IAbilityDelivery
{
    // True when it lands on the caster: a buff, which an AI never casts *at* an enemy.
    bool OnCaster => false;

    bool Release(in AbilityRelease cast, out Vector3 point, out Entity struck);

    void Gather(ref AbilityLanding landing) => landing.AddDefault();
}

[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public sealed class AbilityDeliveryAttribute : VocabularyEntryAttribute<IAbilityDelivery>
{
    public AbilityDeliveryAttribute(string id) : base(id) { }
}

// A cast going off: who, what, from where and which way.
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public readonly ref struct AbilityRelease
{
    public World World { get; init; }
    public Entity Caster { get; init; }
    public RecordId Ability { get; init; }
    public AbilityRecord Record { get; init; }
    public Vector3 Origin { get; init; }
    public Vector3 Aim { get; init; }
    public IPhysicsWorld Space { get; init; }
}

// A payload landing at `Point`: a delivery adds who it reaches.
[Experimental("SAGE0120", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // open vocabulary (#28): may change before 1.0
public ref struct AbilityLanding
{
    internal AbilityPayload Payload;
    public World World;
    public Entity Caster;
    public AbilityRecord Record;
    public Vector3 Point;
    public Entity Struck;

    public readonly void AddCaster() => Payload.AddTarget(Caster);

    // Everything that can be affected within the ability's `radius` of the point, that the caster may
    // hurt (factions, F24); the caster itself only when asked.
    public readonly void AddBurst(bool includeCaster) => Payload.Gather(World, Point, Record.Radius, Caster, includeCaster);

    // What it struck, when that can hold an effect and the caster may hurt it. A bolt that strikes an
    // ally fizzles rather than burning it.
    public readonly void AddStruck()
    {
        if (AbilityPayload.CanBeAffected(World, Struck) && Struck != Caster && Factions.MayHurt(World, Caster, Struck))
            Payload.AddTarget(Struck);
    }

    public readonly void Add(Entity entity)
    {
        if (AbilityPayload.CanBeAffected(World, entity)) Payload.AddTarget(entity);
    }

    // A burst when the ability has a radius, else what it struck.
    public readonly void AddDefault()
    {
        if (Record.Radius > 0f) AddBurst(includeCaster: false);
        else AddStruck();
    }
}

// ---- the engine's deliveries ----------------------------------------------------------------------

// On the caster. "On me" is not a burst of radius zero and not a touch that happened to hit the caster:
// it is its own answer, which is what stops a self-targeted spell quietly skipping the damage pipeline.
[AbilityDelivery("self", Plugin = "sage.gameplay.abilities")]
internal sealed class SelfDelivery : IAbilityDelivery
{
    public bool OnCaster => true;

    public bool Release(in AbilityRelease cast, out Vector3 point, out Entity struck)
    {
        point = cast.Origin;
        struck = default;
        return true;
    }

    public void Gather(ref AbilityLanding landing) => landing.AddCaster();
}

// The first thing within `range` along the aim, like a swing; with a `radius`, everything near where
// it touched, at once (touch_area).
[AbilityDelivery("touch", Plugin = "sage.gameplay.abilities")]
[AbilityDelivery("touch_area", Plugin = "sage.gameplay.abilities")]
internal sealed class TouchDelivery : IAbilityDelivery
{
    public bool Release(in AbilityRelease cast, out Vector3 point, out Entity struck)
    {
        var record = cast.Record;
        // `Width`, not `Radius`: how fat the bolt is on its way, not how wide it bursts. Sweeping with
        // the burst radius made a fireball start already overlapping its own caster, and an overlapping
        // sweep used to report nothing at all (review #55) — so a three-metre burst reached exactly
        // nothing. The caster is left out now, and something else the bolt starts inside is struck at
        // distance 0 (issue #30).
        var hit = cast.Space.Sweep(Collider.Sphere(MathF.Max(record.Width, 0.05f)),
            new Pose { Position = cast.Origin, Rotation = Quaternion.Identity, Scale = Vector3.One },
            cast.Aim, record.Range, LayerMask.All, ignore: cast.Caster);

        point = hit.Entity.IsNull || hit.Entity == cast.Caster
            ? cast.Origin + cast.Aim * record.Range
            : cast.Origin + cast.Aim * hit.Distance;
        struck = hit.Entity;
        return true;
    }
}

// Everything within `radius` of the caster, the caster included.
[AbilityDelivery("area", Plugin = "sage.gameplay.abilities")]
internal sealed class AreaDelivery : IAbilityDelivery
{
    public bool Release(in AbilityRelease cast, out Vector3 point, out Entity struck)
    {
        point = cast.Origin;
        struck = default;
        return true;
    }

    public void Gather(ref AbilityLanding landing)
    {
        if (landing.Record.Radius > 0f) landing.AddBurst(includeCaster: true);
        else landing.AddStruck();
    }
}

// A thing that flies (the ability's `projectile` prefab), and delivers the payload where it arrives
// (ProjectileSystem): nothing lands now, and the landing cues are raised on arrival.
[AbilityDelivery("projectile", Plugin = "sage.gameplay.abilities")]
internal sealed class ProjectileDelivery : IAbilityDelivery
{
    public bool Release(in AbilityRelease cast, out Vector3 point, out Entity struck)
    {
        cast.World.Launch(cast.Caster, cast.Ability, cast.Record, cast.Origin + cast.Aim * 0.4f, cast.Aim);
        point = cast.Origin;
        struck = default;
        return false;
    }
}

public static class AbilityDeliveries
{
    // The delivery `targeting` means, when an ability names no `delivery`.
    public static string NameOf(AbilityTargeting targeting) => targeting switch
    {
        AbilityTargeting.Self => "self",
        AbilityTargeting.Touch => "touch",
        AbilityTargeting.Area => "area",
        AbilityTargeting.TouchArea => "touch_area",
        AbilityTargeting.Projectile => "projectile",
        _ => "self",
    };

    public static string NameOf(AbilityRecord record) => record.Delivery.Length > 0 ? record.Delivery : NameOf(record.Targeting);

    // The ability's delivery, found once and remembered on the record: asked on every cast and by every
    // thinking caster. Null (said once) when it names one nobody registered, and the cast fizzles.
    public static IAbilityDelivery? Of(World world, AbilityRecord record)
    {
        string name = NameOf(record);
        if (record.DeliveryInstance != null && ReferenceEquals(record.DeliveryFor, name)) return record.DeliveryInstance;
        var found = world.Engine?.Vocabularies.Of<IAbilityDelivery>().Find(name) ?? BuiltIn(record, name);
        if (found == null)
        {
            Log.Once(LogCat.Gameplay, LogLevel.Error, $"delivery:{name}",
                $"no ability delivery '{name}' is registered; abilities that name it fizzle");
            return null;
        }
        record.DeliveryInstance = found;
        record.DeliveryFor = name;
        return found;
    }

    // Whether it lands on the caster (a buff), for the AI.
    public static bool OnCaster(World world, AbilityRecord record) => Of(world, record)?.OnCaster ?? false;

    // A world with no engine (a bare test world) still has the five the enum names.
    private static IAbilityDelivery? BuiltIn(AbilityRecord record, string name)
    {
        if (record.Delivery.Length > 0) return null;
        return record.Targeting switch
        {
            AbilityTargeting.Touch or AbilityTargeting.TouchArea => new TouchDelivery(),
            AbilityTargeting.Area => new AreaDelivery(),
            AbilityTargeting.Projectile => new ProjectileDelivery(),
            _ => new SelfDelivery(),
        };
    }

    // An ability's `delivery` must be one somebody registered (AbilitiesModule adds this at load).
    public static void Check(Vocabularies vocabularies, AbilityRecord ability, RecordCheck check)
    {
        if (ability.Delivery.Length == 0) return;
        var deliveries = vocabularies.Of<IAbilityDelivery>();
        if (!deliveries.Contains(ability.Delivery)) check.Error(nameof(AbilityRecord.Delivery), deliveries.Unknown(ability.Delivery));
    }
}
