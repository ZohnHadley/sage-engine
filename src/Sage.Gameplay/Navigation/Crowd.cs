#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Creatures getting out of each other's way (docs/design/16 §3.4, issue #271).
//
// Characters on one physics layer pass through each other (CharacterExtensions.AddCharacter), and the
// planner leaves them out on purpose (a crowd that blocks its own way round a corner is worse than one
// that bumps). So without this, twenty villagers walking down one street walk *through* each other.
//
// **Sampled velocity obstacles, Detour's crowd in miniature.** Each tick a walking creature with somebody
// near scores a fan of candidate velocities — the one it wants, sixteen directions at three speeds, and
// standing still — by how far each is from what it wants and from what it is doing now, how soon it would
// touch a neighbour, and a small penalty for passing on the left. Toward another creature that avoids too,
// the time is measured as if each takes half the turn (the *reciprocal* in RVO: both swerve a little,
// rather than both swerving all the way and meeting again); toward the player or anything that does not
// avoid, it takes the whole turn. The cheapest candidate that has room in front of it (one ray at knee
// height, for at most the best few) is what it walks.
//
// **Keep right, and a nudge when stuck.** Two files meeting head-on in a corridor are the case that
// deadlocks a symmetric rule: everybody waits for everybody. The left-hand penalty breaks the symmetry —
// both sides step right and the files pass as lanes — and a creature that has hardly moved for a second
// while it wants to raises it, and cares less about its own current velocity, until it is moving again.
//
// **Cheap where nobody is near.** A creature with no neighbour within four metres walks exactly as it did
// before (the snapshot below is read and nothing is scored), so a lone guard costs one grid lookup. The
// snapshot of every character is taken once a tick, the first time somebody asks, into arrays that are
// kept: steering allocates nothing.
internal sealed class Crowd
{
    private const float Range = 4f;            // metres: neighbours further than this are ignored
    private const int MaxNeighbours = 10;
    private const float Horizon = 2.5f;        // seconds ahead a collision is worth swerving for
    private const float Margin = 0.1f;         // metres between two bodies that still counts as touching
    private const float WeightDesired = 2f, WeightCurrent = 0.75f, WeightSide = 0.75f, WeightToi = 2.5f;
    private const int Directions = 16;
    private const float StuckAfter = 1f;       // seconds of barely moving before it pushes harder
    private const int Rays = 6;                // candidates checked for room, best first

    private const float Cell = Range;
    private const int Buckets = 256;

    private Vector3[] _position = new Vector3[64];
    private Vector3[] _velocity = new Vector3[64];
    private float[] _radius = new float[64];
    private Entity[] _entity = new Entity[64];
    private bool[] _avoids = new bool[64];
    private int[] _next = new int[64];
    private readonly int[] _bucket = new int[Buckets];
    private int _count;

    private long _tick = -1;
    private World? _world;
    private Query<Transform, CharacterController>? _characters;

    private readonly int[] _near = new int[MaxNeighbours];
    private readonly float[] _nearDistance = new float[MaxNeighbours];
    private readonly Vector3[] _candidates = new Vector3[Directions * 3 + 2];
    private readonly float[] _penalty = new float[Directions * 3 + 2];

    // How many characters the last snapshot held, for `nav_stats`.
    public int Count => _count;

    // Steering decisions that had a neighbour to steer round, since the last ResetStats.
    public int Steered { get; private set; }

    public void ResetStats() => Steered = 0;

    // Every character's position, velocity and radius, once a tick.
    private void Snapshot(World world)
    {
        if (world.Tick == _tick && ReferenceEquals(world, _world)) return;
        _tick = world.Tick;
        if (!ReferenceEquals(world, _world))
        {
            _world = world;
            _characters = world.Query<Transform, CharacterController>();
        }

        Array.Fill(_bucket, -1);
        _count = 0;
        foreach (var (transforms, characters, entities) in _characters!.Value.Chunks)
        {
            var t = transforms.Span;
            var c = characters.Span;
            for (int n = 0; n < t.Length; n++)
            {
                if (_count == _position.Length) Grow();
                var entity = entities.EntityAt(n);
                int i = _count++;
                _position[i] = t[n].LocalPosition;
                _velocity[i] = c[n].Velocity with { Y = 0 };
                _radius[i] = world.TryGet<Collider>(entity, out var collider) ? MathF.Max(collider.Size.X, 0.1f) : 0.35f;
                _entity[i] = entity;
                _avoids[i] = world.Has<AIState>(entity);
                int b = BucketOf(_position[i].X, _position[i].Z);
                _next[i] = _bucket[b];
                _bucket[b] = i;
            }
        }
    }

    private void Grow()
    {
        int size = _position.Length * 2;
        Array.Resize(ref _position, size);
        Array.Resize(ref _velocity, size);
        Array.Resize(ref _radius, size);
        Array.Resize(ref _entity, size);
        Array.Resize(ref _avoids, size);
        Array.Resize(ref _next, size);
    }

    private static int BucketOf(float x, float z) => BucketOf((int)MathF.Floor(x / Cell), (int)MathF.Floor(z / Cell));

    private static int BucketOf(int cx, int cz) => (int)((uint)(cx * 73856093 ^ cz * 19349663) % Buckets);

    // The velocity to walk at (horizontal, m/s), or false when nobody is near and `preferred` stands.
    // `ignore` is left out (what it is walking to: a creature does not swerve round its own target).
    // `stuck` is the creature's own count of seconds spent barely moving, kept in its path.
    public bool Steer(World world, IPhysicsWorld space, Entity self, Entity ignore, Vector3 position, Vector3 preferred,
                      Vector3 current, float radius, float maxSpeed, float knee, float dt, ref float stuck, out Vector3 velocity)
    {
        velocity = preferred;
        Snapshot(world);
        int near = Neighbours(self, ignore, position, radius);
        if (near == 0)
        {
            stuck = 0f;
            return false;
        }
        Steered++;
        maxSpeed = MathF.Max(maxSpeed, 0.1f);
        current = current with { Y = 0 };
        preferred = preferred with { Y = 0 };

        // Stuck: wanting to go and hardly going. Pushes the side bias up and lets go of the current
        // velocity, which is what holds two creatures nose to nose.
        float want = preferred.Length();
        float pushing = Math.Clamp(stuck / StuckAfter, 0f, 2f);
        float weightSide = WeightSide * (1f + pushing);
        float weightCurrent = WeightCurrent / (1f + pushing);

        // The fan: what it wants, three speeds in sixteen directions around it, and standing still.
        float baseAngle = want > 1e-3f ? MathF.Atan2(preferred.Z, preferred.X) : MathF.Atan2(current.Z, current.X);
        int count = 0;
        _candidates[count++] = preferred;
        for (int ring = 0; ring < 3; ring++)
        {
            float speed = maxSpeed * (1f - ring / 3f);
            for (int k = 0; k < Directions; k++)
            {
                float angle = baseAngle + k * MathF.Tau / Directions;
                _candidates[count++] = new Vector3(MathF.Cos(angle) * speed, 0, MathF.Sin(angle) * speed);
            }
        }
        _candidates[count++] = Vector3.Zero;

        Vector3 dir = want > 1e-3f ? preferred / want : Vector3.Zero;
        for (int c = 0; c < count; c++)
        {
            var v = _candidates[c];
            float desired = Vector3.Distance(v, preferred) / maxSpeed;
            float now = Vector3.Distance(v, current) / maxSpeed;
            // Left of the way it wants to go: right is (-dir.Z, dir.X), so the cross is positive to the right.
            float cross = dir.X * v.Z - dir.Z * v.X;
            float side = MathF.Max(0f, -cross) / maxSpeed;

            float soonest = Horizon;
            for (int n = 0; n < near; n++)
            {
                int other = _near[n];
                var relative = (_position[other] - position) with { Y = 0 };
                float reach = radius + _radius[other] + Margin;
                // Reciprocal toward a creature that avoids too: each takes half.
                var closing = _avoids[other] ? 2f * v - current - _velocity[other] : v - _velocity[other];
                float distance = relative.Length();
                if (distance < reach)
                {
                    // Already touching: anything that closes the gap is a collision now, anything that opens
                    // it is fine.
                    if (distance > 1e-4f && Vector3.Dot(closing, relative) / distance > 0.05f) { soonest = 0f; break; }
                    continue;
                }
                float t = TimeToTouch(relative, closing, reach);
                if (t >= 0f && t < soonest) soonest = t;
            }
            _penalty[c] = WeightDesired * desired + weightCurrent * now + weightSide * side + WeightToi / (0.1f + soonest / Horizon);
        }

        // The cheapest that has room ahead of it: a ray at knee height, past the body, for the best few.
        var mask = LayerMask.All.Except(space.Layers.Enemy).Except(space.Layers.Player);
        int best = -1;
        for (int attempt = 0; attempt < Rays; attempt++)
        {
            int pick = -1;
            for (int c = 0; c < count; c++)
                if (!float.IsNaN(_penalty[c]) && (pick < 0 || _penalty[c] < _penalty[pick])) pick = c;
            if (pick < 0) break;
            if (best < 0) best = pick;
            var v = _candidates[pick];
            float speed = v.Length();
            if (speed < 1e-3f) { best = pick; break; }
            var eye = position + Vector3.UnitY * knee;
            if (!space.Raycast(eye, v / speed, radius + speed * 0.35f, mask).Hit) { best = pick; break; }
            _penalty[pick] = float.NaN;   // no room that way: the next best
        }
        velocity = best >= 0 ? _candidates[best] : preferred;

        // Hardly moving while it wants to: count it, and let it go once it is on its way again.
        if (want > 0.5f && Vector3.Dot(velocity, dir) < want * 0.25f && current.Length() < want * 0.25f) stuck += dt;
        else stuck = MathF.Max(0f, stuck - dt * 2f);
        return true;
    }

    // The nearest few within range, into `_near`, nearest kept when there are more.
    private int Neighbours(Entity self, Entity ignore, Vector3 position, float radius)
    {
        int found = 0;
        int cx = (int)MathF.Floor(position.X / Cell), cz = (int)MathF.Floor(position.Z / Cell);
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int i = _bucket[BucketOf(cx + dx, cz + dz)]; i >= 0; i = _next[i])
                {
                    if (_entity[i] == self || _entity[i] == ignore) continue;
                    if (MathF.Abs(_position[i].Y - position.Y) > 2f) continue;   // another floor
                    float d = SageMath.DistanceXZ(_position[i], position) - _radius[i] - radius;
                    if (d > Range) continue;
                    // A bucket is shared by every cell its hash folds onto: the same entity is not seen twice
                    // in one 3x3 lookup unless two of the nine cells fold together, so check.
                    bool seen = false;
                    for (int k = 0; k < found; k++) if (_near[k] == i) { seen = true; break; }
                    if (seen) continue;
                    if (found < MaxNeighbours) { _near[found] = i; _nearDistance[found] = d; found++; continue; }
                    int far = 0;
                    for (int k = 1; k < found; k++) if (_nearDistance[k] > _nearDistance[far]) far = k;
                    if (d < _nearDistance[far]) { _near[far] = i; _nearDistance[far] = d; }
                }
            }
        return found;
    }

    // When a point moving at `velocity` from the origin comes within `reach` of `centre`; -1 if never.
    private static float TimeToTouch(Vector3 centre, Vector3 velocity, float reach)
    {
        float a = velocity.X * velocity.X + velocity.Z * velocity.Z;
        if (a < 1e-6f) return -1f;
        float b = velocity.X * centre.X + velocity.Z * centre.Z;
        float c = centre.X * centre.X + centre.Z * centre.Z - reach * reach;
        float disc = b * b - a * c;
        if (disc < 0f) return -1f;
        float t = (b - MathF.Sqrt(disc)) / a;
        return t >= 0f ? t : -1f;
    }
}
