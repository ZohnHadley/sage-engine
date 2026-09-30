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

    // Arced flight and piercing (issue #134). Zero gravity is the straight flight abilities always had.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "m/s²", Tooltip = "How fast it falls: its velocity loses this much height every second")]
    public float Gravity;
    // How many more things that can be hurt it passes through, landing on each, before one stops it.
    // Scenery always stops it.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Tooltip = "How many targets it passes through before one stops it")]
    public int Pierce;
    // The last thing it passed through, left out of its sweeps while it is still inside it.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public Entity Passed;
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

    // `Final`: it stops here and is destroyed; otherwise it passed through `Struck` (piercing) and flies on.
    private readonly record struct Arrival(Entity Projectile, Entity Caster, RecordId Ability, RecordId Attack, Vector3 Point,
                                           Vector3 Direction, Entity Struck, bool Final = true);

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

                // Arced flight (issue #134): with gravity the tick's path is the chord of the parabola,
                // exact at both ends (constant acceleration), and it is that segment that is swept, so a
                // fast bolt can't tunnel whatever its arc. No gravity is the straight flight it always was,
                // computed exactly as before so abilities land on the same ticks.
                Vector3 direction;
                if (p[n].Gravity == 0f) direction = Vector3.Normalize(p[n].Velocity);
                else
                {
                    var fall = new Vector3(0f, -p[n].Gravity, 0f);
                    var path = p[n].Velocity * dt + fall * (0.5f * dt * dt);
                    p[n].Velocity += fall * dt;
                    step = path.Length();
                    if (step <= 0f) { _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, p[n].Attack, from, default, default)); continue; }
                    direction = path / step;
                    t[n].LocalRotation = Quaternion.CreateFromYawPitchRoll(SageMath.YawOf(p[n].Velocity), SageMath.PitchOf(p[n].Velocity), 0f);
                }

                Vector3 at = from;
                float left = step;
                // A piercing bolt may pass through several things in one tick; each pass is one more sweep
                // from where it left the last, so the loop is bounded by its pierce count.
                while (true)
                {
                    // Its own caster does not stop it: it starts inside them, so they are left out of the
                    // sweep — or, once it has passed through something, that thing, which it may still be
                    // inside. Anything else it starts inside (a creature at point-blank range) stops it
                    // where it is (issue #30; review #55 was the same gap from the other side).
                    var ignore = p[n].Passed.IsNull ? p[n].Caster : p[n].Passed;
                    var hit = _space.Sweep(Collider.Sphere(MathF.Max(p[n].Radius, 0.05f)),
                        new Pose { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One },
                        direction, left, LayerMask.All, ignore: ignore);

                    if (!hit.Hit || hit.Entity.IsNull || hit.Entity == ignore) { at += direction * left; break; }

                    at += direction * hit.Distance;
                    left -= hit.Distance;
                    if (p[n].Pierce > 0 && hit.Entity != p[n].Caster && Hits.CanBeHurt(world, hit.Entity))
                    {
                        p[n].Pierce--;
                        p[n].Passed = hit.Entity;
                        _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, p[n].Attack, at, direction, hit.Entity, Final: false));
                        continue;
                    }
                    _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, p[n].Attack, at, direction, hit.Entity));
                    break;
                }

                t[n].LocalPosition = at;
                if (_debugCasts.Value) _debug.Line(from, at, DebugColour.Orange, 1f);
            }
        }

        foreach (var arrival in _arrivals.Drain()) Arrive(world, arrival);
    }

    private void Arrive(World world, in Arrival arrival)
    {
        if (arrival.Final && world.IsAlive(arrival.Projectile)) world.Destroy(arrival.Projectile);

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

    // Throws an attack's bolt (issue #134): its `projectile` prefab on the same carrier, flying at its
    // `projectileSpeed` for its `range`, falling at its `projectileGravity` and passing through
    // `projectilePierce` targets. Where it stops, the attack's hit lands through Combat.ApplyHit,
    // credited to `shooter`. `Hits.Launch` calls this from a HitRequest.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public static Entity Launch(this World world, Entity shooter, RecordId attack, AttackRecord record,
                                Vector3 from, Vector3 direction) =>
        Hits.Carrier(world, shooter, record.Projectile.Id, attack.Name, from, direction,
                     record.ProjectileSpeed, record.Range, record.Radius, default, attack,
                     record.ProjectileGravity, record.ProjectilePierce);
}
