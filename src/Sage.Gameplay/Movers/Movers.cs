#nullable enable
using System;
using System.Numerics;

namespace Sage.Gameplay;

// Geometry that moves when something tells it to (docs/design/15 §3, 04 §3.4, TODO F17).
//
// **The smallest thing that makes a level a level.** A door, a lift, a portcullis, a drawbridge and a
// secret wall are all the same entity: a piece of solid geometry that slides between two places when it
// is sent `Open` or `Close`. Quake called it `func_door` and everything since has had one, because
// without it a level is a sculpture.
//
// It is in the engine rather than in a game because it is about *geometry*, not rules: what opens the
// door — a button, a key, a quest stage — is a game's business and is wired to it from a map (04 §3.4).

[Component("sage:mover")]
public struct Mover : IComponent
{
    // Where "open" is, relative to where the entity was placed, in metres. A door that slides into the
    // wall is (±width, 0, 0); a portcullis is (0, height, 0). A mover with a Path opens to its last stop.
    [Property(Unit = "m", Tooltip = "Where open is, relative to where it was placed")]
    public Vector3 OpenOffset;

    // How long the trip takes, and where it is now: 0 shut, 1 open. A mover is always *somewhere*, so a
    // save that lands mid-travel reopens mid-travel rather than teleporting.
    [Property(Min = 0, Unit = "s", Tooltip = "How long the whole trip takes, shut to open")]
    public float Seconds;
    [Property(Min = 0, Max = 1, Tooltip = "Where it is: 0 shut, 1 open")]
    public float Position;

    public sbyte Direction;         // -1 closing, 0 still, +1 opening
    public Vector3 Closed;          // where it sits shut, origin space (set when it is built)

    // Shut itself this many seconds after it finishes opening. 0 means it stays open, which is what a
    // door a mapper wants held open by a switch does.
    [Property(Min = 0, Unit = "s", Tooltip = "Shuts itself this long after it opens; 0 = stays open")]
    public float CloseAfter;
    public float HoldRemaining;

    // What it does when something it can't push is in the way (issue #260): a character squeezed against
    // a wall, or under a lift with the floor beneath it. Firing `OnBlocked` with the blocker as activator
    // either way.
    public MoverBlocked OnBlocked;
    [Transient] public bool Blocked;   // blocked last tick: OnBlocked fires once per blockage, except to crush

    // ---- Hinges, paths and locks (issue #266) ----------------------------------------------------
    //
    // **A turn.** Opening also turns it OpenAngle degrees about OpenAxis through Pivot, both in its own
    // frame as it stands shut: a door hinged on its west edge is axis (0, 1, 0), pivot (-width/2, 0, 0).
    // It turns in step with Position, so a door half open is half way round.
    [Property(Unit = "deg", Tooltip = "How far it turns as it opens (a hinged door); 0 = it does not turn")]
    public float OpenAngle;
    [Property(Tooltip = "What it turns about, in its own frame: (0, 1, 0) is a door's hinge")]
    public Vector3 OpenAxis;
    [Property(Unit = "m", Tooltip = "Where the hinge is, from its origin, in its own frame")]
    public Vector3 Pivot;
    public Quaternion ClosedRotation;   // how it is turned shut (set when it is built; zero = unturned)

    // **A path.** Stops after the shut one, offsets from Closed like OpenOffset: a lift that stops at
    // every floor, a train. Position runs along the whole path by distance, so it moves at one speed;
    // Open goes to the last stop, Close to the first, Next and Previous one stop at a time. Null is the
    // two stops a door has, shut and OpenOffset.
    public Vector3[]? Path;
    public float Halt;                  // the stop it is heading for, 0..1 (see MoverSystem.LimitOf)

    // **A lock.** Locked, it refuses to start opening (Open, Toggle, Next, Previous, GoTo) and fires
    // `OnLocked` instead; Close still works, so a door locked open can be shut. Navigation reads it.
    [Property(Tooltip = "Refuses to open (fires OnLocked) until it is sent Unlock")]
    public bool Locked;
}

// What a mover does about something it can't push out of the way (issue #260).
public enum MoverBlocked : byte
{
    Reverse,   // go back the way it came: a door that reopens on whoever stands in it (the default)
    Stop,      // wait where it is, and carry on once the way is clear
    Crush,     // keep going, firing OnBlocked every tick so a map can wire damage to it
}

// Moves what has to move, pushes what is in its way, and says when it arrives.
[System("sage.movers.move", Phase.Gameplay)]
internal sealed class MoverSystem : ISystem
{
    private const float Skin = 0.02f;       // as the character controller's: pushed clear, not just touching
    private const float RoomSlop = 0.005f;  // overlaps shallower than this are resting contact (the floor)

    private readonly World _world;
    private readonly Query<Mover, Transform> _movers;
    private readonly IPhysicsWorld? _space;
    private readonly OverlapHit[] _hits = new OverlapHit[8];
    private readonly OverlapHit[] _room = new OverlapHit[1];

    public MoverSystem(World world)
    {
        _world = world;
        _movers = world.Query<Mover, Transform>();
        world.Resources.TryGet<IPhysicsWorld>(out _space);   // a world may have no physics at all
    }

    public void Run(in SystemContext ctx)
    {
        float dt = ctx.Tick.Dt;

        foreach (var (movers, transforms, entities) in _movers.Chunks)
        {
            for (int n = 0; n < movers.Length; n++)
            {
                ref var mover = ref movers[n];
                var entity = entities.EntityAt(n);

                if (mover.Direction == 0)
                {
                    // Waiting to shut on its own.
                    if (mover.CloseAfter > 0f && mover.Position >= 1f && mover.HoldRemaining > 0f)
                    {
                        mover.HoldRemaining -= dt;
                        if (mover.HoldRemaining <= 0f) { mover.Direction = -1; mover.Halt = 0f; }
                    }
                    continue;
                }

                float seconds = mover.Seconds <= 0f ? 1f : mover.Seconds;
                float was = mover.Position;
                float limit = LimitOf(mover);
                mover.Position = Step(mover.Position, mover.Direction * dt / seconds, limit);

                Place(mover, mover.Position, out var at, out var rotation);
                transforms[n].LocalPosition = at;
                bool turns = Turns(mover);
                if (turns) transforms[n].LocalRotation = rotation;

                // The geometry is a body in the physics space, and a body does not follow a transform:
                // it has to be told. Without this the door opens on screen and stays shut to walk into.
                // The brush was built as a static; it becomes kinematic the first time it moves, so the
                // solver sees it move (issue #261).
                PhysicsBody body = default;
                bool solid = _space != null && entity.TryGetComponent(out body);
                if (solid)
                {
                    if (body.IsStatic)
                    {
                        body = _space!.MakeKinematic(body);
                        entity.GetComponent<PhysicsBody>() = body;
                    }
                    Drive(mover, body, at, rotation, default);

                    // Then whatever it moved into is pushed out of its way, or it is blocked (issue #260).
                    var blocker = Push(entity, body, MotionOf(mover, was, mover.Position, dt, at));
                    if (!blocker.IsNull)
                    {
                        bool first = !mover.Blocked;
                        mover.Blocked = true;
                        if (mover.OnBlocked != MoverBlocked.Crush)
                        {
                            // Back where it was this tick: it never went into them.
                            mover.Position = was;
                            Place(mover, was, out at, out rotation);
                            transforms[n].LocalPosition = at;
                            if (turns) transforms[n].LocalRotation = rotation;
                            Drive(mover, body, at, rotation, default);
                            if (mover.OnBlocked == MoverBlocked.Reverse) mover.Direction = (sbyte)-mover.Direction;
                        }
                        if (first || mover.OnBlocked == MoverBlocked.Crush) _world.FireOutput(entity, "OnBlocked", blocker);
                        continue;
                    }
                }
                mover.Blocked = false;

                if (mover.Position == limit)
                {
                    mover.Direction = 0;
                    mover.Halt = limit;
                    if (limit >= 1f)
                    {
                        mover.HoldRemaining = mover.CloseAfter;
                        _world.FireOutput(entity, "OnFullyOpen");
                    }
                    else if (limit <= 0f) _world.FireOutput(entity, "OnFullyClosed");
                    _world.FireOutput(entity, "OnArrived", default, StopIndex(mover, limit));
                }

                // Moving at the speed that takes it to where it will be next tick, so the step carries it
                // there and what rests on it with it; still once it has arrived.
                if (solid)
                {
                    var motion = default(Motion);
                    if (mover.Direction != 0 && dt > 0f)
                        motion = MotionOf(mover, mover.Position, Step(mover.Position, mover.Direction * dt / seconds, LimitOf(mover)), dt, at);
                    Drive(mover, body, at, rotation, motion);
                }
            }
        }
    }

    // ---- Where it is ---------------------------------------------------------------------------

    // Moves `position` by `delta` toward `limit` and no further.
    private static float Step(float position, float delta, float limit) =>
        delta >= 0f ? MathF.Min(position + delta, limit) : MathF.Max(position + delta, limit);

    // Where it stops on its way: the stop it was sent to when that is ahead of it, otherwise the end it
    // is heading for (Halt is 0 on a mover nobody sent anywhere yet, and one that reversed off a block
    // heads back to the end it came from).
    internal static float LimitOf(in Mover mover)
    {
        if (mover.Direction > 0) return mover.Halt > mover.Position && mover.Halt < 1f ? mover.Halt : 1f;
        if (mover.Direction < 0) return mover.Halt < mover.Position && mover.Halt > 0f ? mover.Halt : 0f;
        return mover.Position;
    }

    internal static bool Turns(in Mover mover) => mover.OpenAngle != 0f && mover.OpenAxis != Vector3.Zero;

    private static Quaternion ClosedRotationOf(in Mover mover) =>
        mover.ClosedRotation.LengthSquared() < 0.5f ? Quaternion.Identity : Quaternion.Normalize(mover.ClosedRotation);

    // The entity's place and turn at `position`: along its path, then round its hinge.
    internal static void Place(in Mover mover, float position, out Vector3 at, out Quaternion rotation)
    {
        at = mover.Closed + Along(mover, position);
        rotation = ClosedRotationOf(mover);
        if (!Turns(mover)) return;

        var closed = rotation;
        var axis = Vector3.Normalize(Vector3.Transform(mover.OpenAxis, closed));
        var turn = Quaternion.CreateFromAxisAngle(axis, mover.OpenAngle * position * (MathF.PI / 180f));
        var pivot = Vector3.Transform(mover.Pivot, closed);   // hinge from the origin, shut
        at += pivot - Vector3.Transform(pivot, turn);           // the origin swings round the hinge
        rotation = Quaternion.Normalize(turn * closed);
    }

    // How far along its path it is at `position`, from Closed.
    private static Vector3 Along(in Mover mover, float position)
    {
        var path = mover.Path;
        if (path is null || path.Length == 0) return mover.OpenOffset * position;

        float total = PathLength(path);
        if (total <= 0f) return path[^1] * position;
        float wanted = position * total;
        Vector3 from = Vector3.Zero;
        for (int i = 0; i < path.Length; i++)
        {
            float length = Vector3.Distance(from, path[i]);
            if (wanted <= length || i == path.Length - 1)
                return length <= 0f ? path[i] : Vector3.Lerp(from, path[i], Math.Clamp(wanted / length, 0f, 1f));
            wanted -= length;
            from = path[i];
        }
        return path[^1];
    }

    internal static float PathLength(Vector3[] path)
    {
        float total = 0f;
        Vector3 from = Vector3.Zero;
        foreach (var point in path) { total += Vector3.Distance(from, point); from = point; }
        return total;
    }

    // How many stops it has (shut is stop 0), and where stop `index` is in 0..1.
    internal static int StopCount(in Mover mover) => mover.Path is { Length: > 0 } path ? path.Length + 1 : 2;

    internal static float StopAt(in Mover mover, int index)
    {
        int last = StopCount(mover) - 1;
        if (index <= 0) return 0f;
        if (index >= last) return 1f;
        var path = mover.Path!;
        float total = PathLength(path);
        if (total <= 0f) return (float)index / last;
        float run = 0f;
        Vector3 from = Vector3.Zero;
        for (int i = 0; i < index; i++) { run += Vector3.Distance(from, path[i]); from = path[i]; }
        return run / total;
    }

    // The stop at `position`, or -1 between stops.
    internal static int StopIndex(in Mover mover, float position)
    {
        int count = StopCount(mover);
        for (int i = 0; i < count; i++)
            if (MathF.Abs(StopAt(mover, i) - position) < 1e-4f) return i;
        return -1;
    }

    // ---- How it moves ----------------------------------------------------------------------------

    // The way a mover moves this tick: its origin's velocity, its turn, and where its origin is, so any
    // point on it moves at Linear + Angular × (point - Origin).
    private readonly struct Motion
    {
        public readonly Vector3 Linear, Angular, Origin;
        public Motion(Vector3 linear, Vector3 angular, Vector3 origin) { Linear = linear; Angular = angular; Origin = origin; }
        public Vector3 At(Vector3 point) => Linear + Vector3.Cross(Angular, point - Origin);
    }

    // From `from` to `to` (both 0..1) in `dt`, with the origin at `origin`.
    private static Motion MotionOf(in Mover mover, float from, float to, float dt, Vector3 origin)
    {
        if (dt <= 0f || from == to) return new Motion(Vector3.Zero, Vector3.Zero, origin);
        Place(mover, from, out var a, out _);
        Place(mover, to, out var b, out _);
        Vector3 angular = Vector3.Zero;
        if (Turns(mover))
        {
            var axis = Vector3.Normalize(Vector3.Transform(mover.OpenAxis, ClosedRotationOf(mover)));
            angular = axis * (mover.OpenAngle * (MathF.PI / 180f) * (to - from) / dt);
        }
        return new Motion((b - a) / dt, angular, origin);
    }

    // Tells the physics space where the mover is and how it is moving.
    private void Drive(in Mover mover, in PhysicsBody body, Vector3 at, Quaternion rotation, in Motion motion)
    {
        if (Turns(mover))
            _space!.MoveKinematic(body, new Pose { Position = at, Rotation = rotation, Scale = Vector3.One }, motion.Linear, motion.Angular);
        else
            _space!.MoveKinematic(body, at, motion.Linear);
    }

    // Pushes everything the mover now overlaps out of its way: characters along the way out of the
    // mover, as far as they have room to go, and dynamic bodies by giving them at least the mover's speed
    // (the physics step does the rest). Returns the first character that has no room (squeezed against
    // a wall, or between a lift and the floor), or null when everything was pushed clear.
    private Entity Push(Entity self, in PhysicsBody mover, in Motion motion)
    {
        var space = _space!;
        int count = space.Overlap(mover, _hits);
        for (int i = 0; i < count; i++)
        {
            var hit = _hits[i];
            var other = hit.Entity;
            if (other.IsNull) continue;
            Vector3 away = -hit.Normal;   // the way out for what the mover hit

            if (other.TryGetComponent<CharacterController>(out _))
            {
                if (!PushCharacter(other, self, away, hit.Depth)) return other;
                continue;
            }

            if (other.TryGetComponent<PhysicsBody>(out var body) && space.IsDynamic(body))
            {
                // A swinging door shoves a crate as fast as the part of it that hits the crate moves.
                Vector3 current = space.VelocityOf(body);
                float along = Vector3.Dot(current, away);
                float wanted = MathF.Max(Vector3.Dot(motion.At(space.PoseOf(body).Position), away), 0f);
                if (along < wanted) space.SetVelocity(body, current + away * (wanted - along));
            }
        }
        return default;
    }

    // Moves a character `depth` (and a skin) along `away`, if it has the room: where it would end up, it
    // overlaps nothing but the mover. A push is a few centimetres a tick, far less than a capsule, so it
    // can't skip through a wall; and a sweep wouldn't do, because a character already touching the wall
    // it is squeezed against starts its sweep inside it and sees nothing. Its body follows at once, so
    // the next mover this tick sees it where it is now.
    private bool PushCharacter(Entity character, Entity mover, Vector3 away, float depth)
    {
        var space = _space!;
        ref var transform = ref character.GetComponent<Transform>();
        var collider = character.GetComponent<Collider>();
        float distance = depth + Skin;
        var pose = new Pose { Position = transform.LocalPosition + away * distance, Rotation = Quaternion.Identity, Scale = Vector3.One };
        int count = space.Overlap(collider, pose, _room, LayerMask.All.Except(collider.Layer), ignore: mover);
        if (count > 0 && _room[0].Depth > RoomSlop) return false;   // deepest first

        transform.LocalPosition += away * distance;
        if (character.TryGetComponent<PhysicsBody>(out var body))
            space.SetPose(body, collider, pose);

        ref var controller = ref character.GetComponent<CharacterController>();
        float into = Vector3.Dot(controller.Velocity, away);
        if (into < 0) controller.Velocity -= away * into;
        return true;
    }
}

[Plugin("sage.gameplay.movers", "0.1.0")]
public sealed class MoverModule : IModule
{
    public void Init(ModuleContext ctx)
    {
        var inputs = ctx.Engine.Inputs;
        ctx.Engine.Outputs.Declare("OnFullyOpen", "This mover finished opening.");
        ctx.Engine.Outputs.Declare("OnFullyClosed", "This mover finished closing.");
        ctx.Engine.Outputs.Declare("OnBlocked", "Something this mover could not push was in its way (the activator).");
        ctx.Engine.Outputs.Declare("OnArrived", "This mover stopped at a stop of its path, the ends too (the value: the stop, 0 = shut).");
        ctx.Engine.Outputs.Declare("OnLocked", "Something tried to open this mover while it was locked (the activator).");

        // Routed to the mover (issue #91): `Toggle` is a mover's here and a branch's on a logic_branch,
        // and a game may still register a global `Open` for things that are not movers.
        inputs.Register<Mover>("Open", static (World world, in IOContext io) => Send(world, io, 1f));
        inputs.Register<Mover>("Close", static (World world, in IOContext io) => Send(world, io, 0f));
        inputs.Register<Mover>("Toggle", static (World world, in IOContext io) =>
        {
            ref var mover = ref io.Self.GetComponent<Mover>();

            // Half way through opening, "toggle" means shut it — what it is *doing* matters more than
            // where it happens to be.
            bool open = mover.Direction != 0 ? mover.Direction < 0 : mover.Position < 1f;
            Send(world, io, open ? 1f : 0f);
        });

        // A path's stops (issue #266): one stop on or back, or a stop by number (0 is shut). Past either
        // end it stays where it is.
        inputs.Register<Mover>("Next", static (World world, in IOContext io) => Step(world, io, +1));
        inputs.Register<Mover>("Previous", static (World world, in IOContext io) => Step(world, io, -1));
        inputs.Register<Mover>("GoTo", static (World world, in IOContext io) =>
        {
            ref var mover = ref io.Self.GetComponent<Mover>();
            int stop = (int)MathF.Round(io.Number(-1f));
            if (stop < 0 || stop >= MoverSystem.StopCount(mover))
            {
                Log.Warn(LogCat.Events, $"I/O: GoTo({io.Parameter}) at {World.Describe(io.Self)}: it has stops 0 to {MoverSystem.StopCount(mover) - 1}");
                return;
            }
            Send(world, io, MoverSystem.StopAt(mover, stop));
        });

        // A lock (issue #266): locked, it will not open; Close still shuts it.
        inputs.Register<Mover>("Lock", static (World world, in IOContext io) => io.Self.GetComponent<Mover>().Locked = true);
        inputs.Register<Mover>("Unlock", static (World world, in IOContext io) => io.Self.GetComponent<Mover>().Locked = false);

        // How fast it goes from now on (issue #266): metres a second along its path, or degrees a second
        // when all it does is turn. The trip it is on carries on at the new speed.
        inputs.Register<Mover>("SetSpeed", static (World world, in IOContext io) =>
        {
            ref var mover = ref io.Self.GetComponent<Mover>();
            float speed = io.Number();
            float route = Route(mover);
            if (speed <= 0f || route <= 0f)
            {
                Log.Warn(LogCat.Events, $"I/O: SetSpeed({io.Parameter}) at {World.Describe(io.Self)}: wants a speed above 0");
                return;
            }
            mover.Seconds = route / speed;
        });

        // A mover a game wants placed by hand rather than drawn in a map is the `mover` prefab part
        // (MoverPart below), which this plugin declares.
    }

    public void OnWorldCreated(World world)
    {
        world.AddSystem(new MoverSystem(world));

        // A mover drawn in a map is shut where the mapper drew it; only the level knows where that is
        // (MapModule is installed before this plugin, so its MapLevels is already there).
        if (world.Resources.TryGet<MapLevels>(out var levels) && levels != null)
            levels.SolidSpawned += static (_, entity, at) =>
            {
                if (!entity.HasComponent<Mover>()) return;
                ref var mover = ref entity.GetComponent<Mover>();
                mover.Closed = at;
                mover.ClosedRotation = entity.TryGetComponent<Transform>(out var transform) ? transform.LocalRotation : Quaternion.Identity;
            };
    }

    // How long its whole trip is: metres along its path, or degrees when it only turns.
    internal static float Route(in Mover mover)
    {
        float metres = mover.Path is { Length: > 0 } path ? MoverSystem.PathLength(path) : mover.OpenOffset.Length();
        return metres > 0f ? metres : MathF.Abs(mover.OpenAngle);
    }

    private static void Step(World world, in IOContext io, int by)
    {
        ref var mover = ref io.Self.GetComponent<Mover>();
        int count = MoverSystem.StopCount(mover);
        // The stop it is at, or the one it would reach next going that way.
        int stop = -1;
        if (by > 0) { for (int i = 0; i < count; i++) if (MoverSystem.StopAt(mover, i) > mover.Position + 1e-4f) { stop = i; break; } }
        else { for (int i = count - 1; i >= 0; i--) if (MoverSystem.StopAt(mover, i) < mover.Position - 1e-4f) { stop = i; break; } }
        if (stop < 0) return;   // already at that end
        Send(world, io, MoverSystem.StopAt(mover, stop));
    }

    // Sends it to `target` (0..1): the way there, and where to stop. Opening while locked fails.
    private static void Send(World world, in IOContext io, float target)
    {
        var entity = io.Self;
        if (!entity.HasComponent<Mover>()) return;
        ref var mover = ref entity.GetComponent<Mover>();

        if (mover.Locked && target > 0f)
        {
            world.FireOutput(entity, "OnLocked", io.Activator);
            return;
        }

        // Already there: say so anyway, because a wire that opens an open door still expects its
        // `OnFullyOpen` to reach whatever it feeds (a mapper counting doors, for instance).
        if (mover.Direction == 0 && MathF.Abs(mover.Position - target) < 1e-4f)
        {
            if (target >= 1f)
            {
                mover.HoldRemaining = mover.CloseAfter;
                world.FireOutput(entity, "OnFullyOpen");
            }
            else if (target <= 0f) world.FireOutput(entity, "OnFullyClosed");
            return;
        }

        mover.Halt = target;
        mover.Direction = target > mover.Position ? (sbyte)+1 : target < mover.Position ? (sbyte)-1 : mover.Direction;
    }
}

// A mover a game wants placed by hand rather than drawn in a map: `"mover": { "open": [0, 3, 0],
// "seconds": 1.5 }` on a prefab. Closed is wherever it was placed, which is why it reads the transform
// (Spawn places the entity before any part runs). A hinged door turns instead (`"angle": 90, "axis":
// [0, 1, 0], "pivot": [-1, 0, 0]`), and a lift with floors follows a `"path"` of stops (issue #266).
[PrefabPart("mover", Plugin = "sage.gameplay.movers")]
public sealed class MoverPart : IPrefabPart
{
    [Property(Unit = "m", Tooltip = "Where open is, relative to where it was placed")]
    public Vector3 Open;               // where "open" is, relative to where it was placed
    [Property(Min = 0, Unit = "s", Tooltip = "How long the whole trip takes, shut to open")]
    public float Seconds = 1f;
    [Property(Min = 0, Tooltip = "Above 0: metres a second along its path (degrees a second if it only turns), instead of seconds")]
    public float Speed;
    [Property(Min = 0, Unit = "s", Tooltip = "Above 0: shuts itself this long after opening")]
    public float CloseAfter;           // > 0: shuts itself this long after opening
    [Property(Tooltip = "Reverse (default), Stop or Crush, when something can't be pushed")]
    public MoverBlocked OnBlocked;     // "Reverse" (default), "Stop" or "Crush", when something can't be pushed
    [Property(Unit = "deg", Tooltip = "How far it turns as it opens (a hinged door: 90)")]
    public float Angle;
    [Property(Tooltip = "What it turns about, in its own frame; default up, a door's hinge")]
    public Vector3 Axis = Vector3.UnitY;
    [Property(Unit = "m", Tooltip = "Where the hinge is, from its origin, in its own frame")]
    public Vector3 Pivot;
    [Property(Unit = "m", Tooltip = "Stops after the shut one, from where it was placed (a lift's floors); open is the last")]
    public Vector3[]? Path;
    [Property(Tooltip = "Starts locked: refuses to open until it is sent Unlock")]
    public bool Locked;

    public void Apply(in PrefabPartContext ctx)
    {
        var path = Path is { Length: > 0 } ? (Vector3[])Path.Clone() : null;
        var mover = new Mover
        {
            OpenOffset = path is null ? Open : path[^1],
            Seconds = Seconds <= 0f ? 1f : Seconds,
            CloseAfter = CloseAfter,
            OnBlocked = OnBlocked,
            OpenAngle = Angle,
            OpenAxis = Angle != 0f ? Axis : Vector3.Zero,
            Pivot = Pivot,
            Path = path,
            Locked = Locked,
        };
        if (Speed > 0f)
        {
            float route = MoverModule.Route(mover);
            if (route > 0f) mover.Seconds = route / Speed;
        }
        var transform = ctx.Entity.GetComponent<Transform>();
        mover.Closed = transform.LocalPosition;
        mover.ClosedRotation = transform.LocalRotation;
        if (Angle != 0f && Axis == Vector3.Zero) ctx.Error("a mover that turns needs an \"axis\"");
        ctx.World.Add(ctx.Entity, mover);
    }
}
