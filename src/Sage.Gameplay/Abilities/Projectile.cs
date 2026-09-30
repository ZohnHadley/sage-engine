#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Something an ability threw, on its way (docs/design/16 §3.3, TODO F21). Before this a fireball
// arrived the instant it was cast, which is fine for a test and wrong for a game: you cannot dodge
// it, it cannot be seen coming, and the burst appears at a point nothing travelled to.
//
// It is a normal entity. The ability names a **prefab** for it (F31), so what a fireball looks like —
// a sprite, a light later, a trail later — is data, and this component is only the flight: where it
// is going, how long it has, and what to do when it stops.
[Component("sage:projectile")]
public struct Projectile : IComponent
{
    public RecordId Ability;    // whose payload it carries, delivered where it lands
    // Or the attack whose hit it carries (a `projectile` hit_delivery, issue #133), landed through
    // Combat.ApplyHit on what it strikes. One or the other.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [RecordRef("attack")]
    public RecordId Attack;
    public Entity Caster;       // who threw it: the damage is theirs, and it will not hit them
    public Vector3 Velocity;    // metres per second, already in world space
    public float Life;          // seconds before it gives up and bursts where it is
    public float Radius;        // how fat it is in flight (not the burst: that is the ability's)
}

// Gameplay phase, before effects tick: moves everything in flight and delivers what it was carrying
// when it arrives. Ordered with the ability system for the same reason — a spell that lands this tick
// is felt this tick (16 §3.2).
//
// Movement is a sweep, not a teleport plus an overlap test: a fast projectile covers metres in a tick,
// and anything that only looks at where it *ended up* flies straight through a creature standing
// between the two points. That is the classic bullet-through-paper bug and it is invisible until
// something moves quickly.
[System("sage.abilities.projectiles", Phase.Gameplay, After = new[] { "sage.abilities.cast" }, Before = new[] { "sage.effects.tick" })]
internal sealed class ProjectileSystem : ISystem
{
    private readonly Query<Transform, Projectile> _flying;
    private readonly RecordStore _records;
    private readonly IPhysicsWorld _space;
    private readonly AbilityPayload _payload;
    private readonly DebugDraw _debug;
    private readonly CVar<bool> _debugCasts;

    // Arrivals are handled after the loop: delivering a payload applies effects to other entities and
    // destroys this one, neither of which a query allows (R14).
    private readonly Deferred<Arrival> _arrivals = new();

    private readonly record struct Arrival(Entity Projectile, Entity Caster, RecordId Ability, RecordId Attack, Vector3 Point,
                                           Vector3 Direction, Entity Struck);

    public ProjectileSystem(World world, RecordStore records, CVar<bool> debugCasts)
    {
        _flying = world.Query<Transform, Projectile>();
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

        foreach (var (transforms, projectiles, entities) in _flying.Chunks)
        {
            var t = transforms.Span;
            var p = projectiles.Span;
            for (int n = 0; n < p.Length; n++)
            {
                var entity = entities.EntityAt(n);
                Vector3 from = t[n].LocalPosition;
                float step = p[n].Velocity.Length() * dt;

                p[n].Life -= dt;
                if (step <= 0f || p[n].Life <= 0f)
                {
                    _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, p[n].Attack, from, default, default));
                    continue;
                }

                var direction = Vector3.Normalize(p[n].Velocity);
                // Its own caster does not stop it: it starts inside them, so they are left out of the
                // sweep. Anything else it starts inside (a creature at point-blank range) stops it
                // where it is (issue #30; review #55 was the same gap from the other side).
                var hit = _space.Sweep(Collider.Sphere(MathF.Max(p[n].Radius, 0.05f)),
                    new Pose { Position = from, Rotation = Quaternion.Identity, Scale = Vector3.One },
                    direction, step, LayerMask.All, ignore: p[n].Caster);

                bool stopped = hit.Hit && !hit.Entity.IsNull && hit.Entity != p[n].Caster;

                Vector3 to = stopped ? from + direction * hit.Distance : from + direction * step;
                t[n].LocalPosition = to;
                if (_debugCasts.Value) _debug.Line(from, to, DebugColour.Orange, 1f);

                if (stopped) _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, p[n].Attack, to, direction, hit.Entity));
            }
        }

        foreach (var arrival in _arrivals.Drain()) Arrive(world, arrival);
    }

    private void Arrive(World world, in Arrival arrival)
    {
        if (world.IsAlive(arrival.Projectile)) world.Destroy(arrival.Projectile);

        // An attack's bolt (issue #133) is one strike: what it struck takes the attack's hit, through the
        // same Combat.ApplyHit a sword's swing and a pistol's ray use. Running out of range hits nothing.
        if (!arrival.Attack.IsEmpty)
        {
            if (!Hits.CanBeHurt(world, arrival.Struck) || arrival.Struck == arrival.Caster) return;
            if (!_records.TryGet(arrival.Attack, out AttackRecord attack)) return;
            var request = new HitRequest(arrival.Caster, arrival.Point, arrival.Direction, arrival.Attack);
            Combat.ApplyHit(world, in request, new HitResult(arrival.Struck, arrival.Point, -arrival.Direction, arrival.Struck, default), attack);
            return;
        }

        // A projectile outlives its caster: a fireball thrown by something that dies mid-flight still
        // lands. The payload credits a dead caster, which the death seam already copes with.
        if (!_records.TryGet(arrival.Ability, out AbilityRecord record)) return;
        _payload.Deliver(world, arrival.Caster, arrival.Ability, record, arrival.Point, arrival.Struck);

        if (!_debugCasts.Value) return;
        if (record.Radius > 0f) _debug.Sphere(arrival.Point, record.Radius, DebugColour.Magenta, 1.5f);
        else _debug.Cross(arrival.Point, 0.3f, DebugColour.Magenta, 1.5f);
    }
}

public static class ProjectileExtensions
{
    // Throws one. The prefab is what it looks like; this adds the flight. An empty prefab still works
    // and is simply invisible, which is what a headless server gets.
    // The carrier is the one attacks fly on too (Hits.Carrier, issue #133).
    public static Entity Launch(this World world, Entity caster, RecordId ability, AbilityRecord record,
                                Vector3 from, Vector3 direction) =>
        Hits.Carrier(world, caster, record.Projectile.Id, ability.Name, from, direction,
                     record.ProjectileSpeed, record.Range, record.Width, ability, default);
}
