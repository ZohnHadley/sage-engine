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

    // Bouncing and sticking (issue #393). Scenery it meets with `Bounces` left turns it back, keeping
    // `Bounciness` of its speed; with none left it stops there as before.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Tooltip = "How many more times it bounces off scenery before scenery stops it")]
    public int Bounces;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Max = 1, Tooltip = "The speed it keeps on each bounce")]
    public float Bounciness;
    // Where it stops on something, it stays — parented to what it struck when that can be hurt (an arrow
    // in a deer) — for `StickSeconds`, then goes.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Tooltip = "It stays where it stops instead of vanishing")]
    public bool Sticks;
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    [Property(Min = 0, Unit = "s", Tooltip = "How long it stays once stuck")]
    public float StickSeconds;
    // It has stopped and stays: it no longer flies or lands, and `Life` is the seconds it has left.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public bool Stuck;
    // The body it is stuck in, whose child it is; null for scenery. Kept here because a save keeps no
    // hierarchy but this one's own: after a load it is made the body's child again, its transform still
    // relative to it, and it goes when the body does.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public Entity Host;
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
    private readonly Deferred<Arrival> _bounces = new();
    private readonly Deferred<Entity> _expired = new();
    private readonly Deferred<Entity> _reattach = new();

    // `Final`: it stops here and is destroyed; otherwise it passed through `Struck` (piercing) and flies on.
    // `Collider` is what the sweep met — `Struck` itself, or one of its hitboxes — and `Location` that
    // hitbox's hit_location (empty: the body).
    private readonly record struct Arrival(Entity Projectile, Entity Caster, RecordId Ability, RecordId Attack, Vector3 Point,
                                           Vector3 Direction, Entity Struck, bool Final = true,
                                           Entity Collider = default, RecordId Location = default)
    {
        // What the sweep met is made of and which way it faced, for its impact cue (issue #306).
        public RecordId Surface { get; init; }
        public Vector3 Normal { get; init; }
    }

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
        var boxes = Hitboxes.Mask(_space);   // the hitbox layer (issue #137), or none; the layers are content

        foreach (var (transforms, projectiles, entities) in _flying.Chunks)
        {
            var t = transforms.Span;
            var p = projectiles.Span;
            for (int n = 0; n < p.Length; n++)
            {
                var entity = entities.EntityAt(n);
                p[n].Life -= dt;
                // Stuck in something (issue #393): it only waits out its time.
                if (p[n].Stuck)
                {
                    if (p[n].Life <= 0f || (!p[n].Host.IsNull && !world.IsAlive(p[n].Host))) _expired.Add(entity);
                    else if (!p[n].Host.IsNull && entity.Parent != p[n].Host) _reattach.Add(entity);   // after a load
                    continue;
                }

                Vector3 from = t[n].LocalPosition;
                float step = p[n].Velocity.Length() * dt;

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
                int spared = 0;
                // A piercing bolt may pass through several things in one tick; each pass is one more sweep
                // from where it left the last, so the loop is bounded by its pierce count.
                while (true)
                {
                    // Its own caster does not stop it: it starts inside them, so they are left out of the
                    // sweep — or, once it has passed through something, that thing, which it may still be
                    // inside. Anything else it starts inside (a creature at point-blank range) stops it
                    // where it is (issue #30; review #55 was the same gap from the other side).
                    var ignore = p[n].Passed.IsNull ? p[n].Caster : p[n].Passed;
                    var sphere = Collider.Sphere(MathF.Max(p[n].Radius, 0.05f));
                    var pose = new Pose { Position = at, Rotation = Quaternion.Identity, Scale = Vector3.One };
                    var hit = _space.Sweep(sphere, pose, direction, left, LayerMask.All, ignore: ignore);
                    bool solid = hit.Hit && !hit.Entity.IsNull && hit.Entity != ignore;

                    // Then the hitboxes (issue #138), which no query for solid things sees: a bolt lands
                    // where a sweep or a ray would (Hits.Sweep), on a box in front of what it met or inside
                    // its owner's capsule — and on a creature with no capsule at all. Leaving out `ignore`
                    // leaves out its boxes too (they are its children).
                    Entity struck = solid ? hit.Entity : default, collider = struck;
                    RecordId location = default, surface = hit.Surface;
                    Vector3 normal = hit.Normal;
                    float distance = hit.Distance;
                    if (boxes.Bits != 0)
                    {
                        var box = _space.Sweep(sphere, pose, direction, left, boxes, ignore: ignore);
                        if (box.Hit && Hitboxes.Landed(p[n].Caster, box.Entity, box.Distance, struck, hit.Distance, out var landed))
                        {
                            struck = landed.Owner;
                            collider = box.Entity;
                            location = landed.Location;
                            distance = box.Distance;
                            surface = box.Surface;
                            normal = box.Normal;
                        }
                    }

                    if (struck.IsNull) { at += direction * left; break; }

                    at += direction * distance;
                    left -= distance;
                    bool hurtable = struck != p[n].Caster && Hits.CanBeHurt(world, struck);

                    // Who its thrower may not hurt it flies past (issue #393): the ability's `affects` or
                    // the attack's `friendlyFire`, so a creature's bolt does not stop at, and burst on, a
                    // packmate in front of it. Bounded, as two spared bodies overlapping could trade places
                    // as `Passed` for ever.
                    if (hurtable && spared < 8 && !FactionFilters.Allows(world, p[n].Caster, struck, FilterOf(in p[n])))
                    {
                        spared++;
                        p[n].Passed = struck;
                        continue;
                    }

                    // Scenery turns a bouncing one back (issue #393): its velocity mirrored about the
                    // surface and slowed, from just off the surface, and the rest of the tick is lost.
                    if (!hurtable && struck != p[n].Caster && p[n].Bounces > 0)
                    {
                        var n0 = normal.LengthSquared() > 1e-6f ? Vector3.Normalize(normal) : -direction;
                        if (Vector3.Dot(p[n].Velocity, n0) > 0f) n0 = -n0;
                        p[n].Velocity = (p[n].Velocity - 2f * Vector3.Dot(p[n].Velocity, n0) * n0) * Math.Clamp(p[n].Bounciness, 0f, 1f);
                        p[n].Bounces--;
                        p[n].Passed = default;
                        at += n0 * 0.02f;
                        _bounces.Add(new Arrival(entity, p[n].Caster, p[n].Ability, p[n].Attack, at, direction, struck, Final: false, collider, location)
                                     { Surface = surface, Normal = n0 });
                        break;
                    }
                    if (p[n].Pierce > 0 && struck != p[n].Caster && Hits.CanBeHurt(world, struck))
                    {
                        p[n].Pierce--;
                        p[n].Passed = struck;
                        _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, p[n].Attack, at, direction, struck, Final: false, collider, location)
                                      { Surface = surface, Normal = normal });
                        continue;
                    }
                    _arrivals.Add(new Arrival(entity, p[n].Caster, p[n].Ability, p[n].Attack, at, direction, struck, Final: true, collider, location)
                                  { Surface = surface, Normal = normal });
                    break;
                }

                t[n].LocalPosition = at;
                if (_debugCasts.Value) _debug.Line(from, at, DebugColour.Orange, 1f);
            }
        }

        foreach (var arrival in _arrivals.Drain()) Arrive(world, arrival);
        // A bounce makes the noise and mark of what it met, as a bolt stopping there would.
        foreach (var bounce in _bounces.Drain()) Impacts.Raise(world, bounce.Caster, bounce.Point, bounce.Normal, bounce.Surface);
        foreach (var gone in _expired.Drain()) if (world.IsAlive(gone)) world.Destroy(gone);
        foreach (var stuck in _reattach.Drain())
            if (world.IsAlive(stuck) && world.IsAlive(world.Get<Projectile>(stuck).Host)) world.SetParent(stuck, world.Get<Projectile>(stuck).Host);
    }

    // Who the payload may reach: the attack's `friendlyFire` or the ability's `affects`.
    private FactionFilter FilterOf(in Projectile projectile)
    {
        if (!projectile.Attack.IsEmpty)
            return _records.TryGet(projectile.Attack, out AttackRecord attack) ? attack.FriendlyFire : FactionFilter.Default;
        return _records.TryGet(projectile.Ability, out AbilityRecord ability) ? ability.Affects : FactionFilter.Default;
    }

    // Stays where it stopped (issue #393): no longer flying, for its `StickSeconds`, and in what it struck
    // when that is a body (a root with a Transform), so it moves with it.
    private static void Stick(World world, in Arrival arrival)
    {
        ref var projectile = ref world.Get<Projectile>(arrival.Projectile);
        projectile.Stuck = true;
        projectile.Velocity = Vector3.Zero;
        projectile.Passed = default;
        projectile.Life = MathF.Max(projectile.StickSeconds, 0f);
        if (!world.TryGet<Transform>(arrival.Projectile, out var transform)) return;
        transform.LocalPosition = arrival.Point;

        var host = arrival.Struck;
        if (!host.IsNull && host != arrival.Caster && world.IsAlive(host) && host.Parent.IsNull && arrival.Projectile.Parent.IsNull
            && Hits.CanBeHurt(world, host) && world.TryGet<Transform>(host, out var body))
        {
            var unturn = Quaternion.Inverse(body.LocalRotation);
            var scale = body.LocalScale;
            if (scale.X == 0f || scale.Y == 0f || scale.Z == 0f) scale = Vector3.One;
            transform.LocalPosition = Vector3.Transform(arrival.Point - body.LocalPosition, unturn) / scale;
            transform.LocalRotation = unturn * transform.LocalRotation;
            projectile.Host = host;                                       // before the hierarchy moves it
            world.Get<Transform>(arrival.Projectile) = transform;
            world.SetParent(arrival.Projectile, host);
            // In a body a save does not keep, it has nowhere to come back to.
            if (!world.Has<Persistent>(host)) world.Remove<Persistent>(arrival.Projectile);
            return;
        }
        world.Get<Transform>(arrival.Projectile) = transform;
    }

    private void Arrive(World world, in Arrival arrival)
    {
        if (arrival.Final && world.IsAlive(arrival.Projectile))
        {
            if (!arrival.Struck.IsNull && world.TryGet<Projectile>(arrival.Projectile, out var flying) && flying.Sticks) Stick(world, in arrival);
            else world.Destroy(arrival.Projectile);
        }

        // An attack's bolt (issue #133) is one strike: what it struck takes the attack's hit, through the
        // same Combat.ApplyHit a sword's swing and a pistol's ray use. Running out of range hits nothing.
        if (!arrival.Attack.IsEmpty)
        {
            // Whatever it struck makes its surface's noise and mark (issue #306), a wall or a target.
            if (!arrival.Struck.IsNull && arrival.Struck != arrival.Caster)
                Impacts.Raise(world, arrival.Caster, arrival.Point, arrival.Normal, arrival.Surface);
            if (!Hits.CanBeHurt(world, arrival.Struck) || arrival.Struck == arrival.Caster) return;
            if (!_records.TryGet(arrival.Attack, out AttackRecord attack)) return;
            var request = new HitRequest(arrival.Caster, arrival.Point, arrival.Direction, arrival.Attack);
            var collider = arrival.Collider.IsNull ? arrival.Struck : arrival.Collider;
            Combat.ApplyHit(world, in request, new HitResult(arrival.Struck, arrival.Point, -arrival.Direction, collider, arrival.Location), attack);
            return;
        }

        // A projectile outlives its caster: a fireball thrown by something that dies mid-flight still
        // lands. The payload credits a dead caster, which the death seam already copes with.
        if (!_records.TryGet(arrival.Ability, out AbilityRecord record)) return;
        _payload.Deliver(world, arrival.Caster, arrival.Ability, record, arrival.Point, arrival.Struck, arrival.Location);

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
                     record.ProjectileSpeed, record.Range, record.Width, ability, default,
                     bounces: record.ProjectileBounces, bounciness: record.ProjectileBounciness,
                     sticks: record.ProjectileSticks, stickSeconds: record.ProjectileStickSeconds);

    // Throws an attack's bolt (issue #134): its `projectile` prefab on the same carrier, flying at its
    // `projectileSpeed` for its `range`, falling at its `projectileGravity` and passing through
    // `projectilePierce` targets. Where it stops, the attack's hit lands through Combat.ApplyHit,
    // credited to `shooter`. `Hits.Launch` calls this from a HitRequest.
    [System.Diagnostics.CodeAnalysis.Experimental("SAGE0127", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]
    public static Entity Launch(this World world, Entity shooter, RecordId attack, AttackRecord record,
                                Vector3 from, Vector3 direction) =>
        Hits.Carrier(world, shooter, record.Projectile.Id, attack.Name, from, direction,
                     record.ProjectileSpeed, record.Range, record.Radius, default, attack,
                     record.ProjectileGravity, record.ProjectilePierce,
                     record.ProjectileBounces, record.ProjectileBounciness, record.ProjectileSticks, record.ProjectileStickSeconds);
}
