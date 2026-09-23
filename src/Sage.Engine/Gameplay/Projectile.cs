#nullable enable
using System;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Something an ability threw, on its way (docs/design/16 §3.3, TODO F21). Before this a fireball
// arrived the instant it was cast, which is fine for a test and wrong for a game: you cannot dodge
// it, it cannot be seen coming, and the burst appears at a point nothing travelled to.
//
// It is a normal entity. The ability names a **prefab** for it (F31), so what a fireball looks like —
// a sprite, a light later, a trail later — is data, and this component is only the flight: where it
// is going, how long it has, and what to do when it stops.
public struct Projectile : IComponent
{
    public RecordId Ability;    // whose payload it carries, delivered where it lands
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
public sealed class ProjectileSystem : ISystem
{
    private readonly ArchetypeQuery<Transform, Projectile> _flying;
    private readonly RecordStore _records;
    private readonly PhysicsSpace _space;
    private readonly AbilityPayload _payload;
    private readonly DebugDraw _debug;
    private readonly CVar<bool> _debugCasts;

    // Arrivals are handled after the loop: delivering a payload applies effects to other entities and
    // destroys this one, neither of which a query allows (R14).
    private readonly Deferred<Arrival> _arrivals = new();

    private readonly record struct Arrival(Entity Projectile, Entity Caster, RecordId Ability, Vector3 Point, Entity Struck);

    public ProjectileSystem(World world, RecordStore records, CVar<bool> debugCasts)
    {
        _flying = world.Query<Transform, Projectile>();
        _records = records;
        _space = world.Resources.Get<PhysicsSpace>();
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
                    _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, from, default));
                    continue;
                }

                var direction = Vector3.Normalize(p[n].Velocity);
                var hit = _space.Sweep(Collider.Sphere(MathF.Max(p[n].Radius, 0.05f)),
                    new Pose { Position = from, Rotation = Quaternion.Identity, Scale = Vector3.One },
                    direction, step, LayerMask.All);

                // Its own caster does not stop it: it starts inside them, and a sweep that begins
                // overlapping reports a zero-distance touch (10 §4, review #55).
                bool stopped = hit.Hit && !hit.Entity.IsNull && hit.Entity != p[n].Caster;

                Vector3 to = stopped ? from + direction * hit.Distance : from + direction * step;
                t[n].LocalPosition = to;
                if (_debugCasts.Value) _debug.Line(from, to, DebugColour.Orange, 1f);

                if (stopped) _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, to, hit.Entity));
            }
        }

        foreach (var arrival in _arrivals.Drain()) Arrive(world, arrival);
    }

    private void Arrive(World world, in Arrival arrival)
    {
        if (world.IsAlive(arrival.Projectile)) world.Destroy(arrival.Projectile);

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
    public static Entity Launch(this World world, Entity caster, RecordId ability, AbilityRecord record,
                                Vector3 from, Vector3 direction)
    {
        var entity = record.Projectile.IsEmpty
            ? world.Create(Transform.At(from), ability.Name)
            : world.Spawn(record.Projectile, from, SageMath.YawOf(direction) * 180f / MathF.PI);

        if (entity.IsNull) return entity;
        if (!world.Has<Transform>(entity)) return entity;
        world.Get<Transform>(entity).LocalPosition = from;

        world.Add(entity, new Projectile
        {
            Ability = ability,
            Caster = caster,
            Velocity = Vector3.Normalize(direction) * MathF.Max(record.ProjectileSpeed, 0.1f),
            // Enough time to cross its own range, with a little slack, so a spell that never hits
            // anything bursts at the far end instead of flying to the edge of the world.
            Life = MathF.Max(record.Range, 1f) / MathF.Max(record.ProjectileSpeed, 0.1f) + 0.2f,
            Radius = record.Width,
        });
        return entity;
    }
}
