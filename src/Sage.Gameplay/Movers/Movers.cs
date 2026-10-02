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
    // wall is (±width, 0, 0); a portcullis is (0, height, 0).
    public Vector3 OpenOffset;

    // How long the trip takes, and where it is now: 0 shut, 1 open. A mover is always *somewhere*, so a
    // save that lands mid-travel reopens mid-travel rather than teleporting.
    public float Seconds;
    public float Position;

    public sbyte Direction;         // -1 closing, 0 still, +1 opening
    public Vector3 Closed;          // where it sits shut, origin space (set when it is built)

    // Shut itself this many seconds after it finishes opening. 0 means it stays open, which is what a
    // door a mapper wants held open by a switch does.
    public float CloseAfter;
    public float HoldRemaining;

    // What it does when something it can't push is in the way (issue #260): a character squeezed against
    // a wall, or under a lift with the floor beneath it. Firing `OnBlocked` with the blocker as activator
    // either way.
    public MoverBlocked OnBlocked;
    [Transient] public bool Blocked;   // blocked last tick: OnBlocked fires once per blockage, except to crush
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
                        if (mover.HoldRemaining <= 0f) mover.Direction = -1;
                    }
                    continue;
                }

                float seconds = mover.Seconds <= 0f ? 1f : mover.Seconds;
                float was = mover.Position;
                mover.Position = Math.Clamp(mover.Position + mover.Direction * dt / seconds, 0f, 1f);

                var at = mover.Closed + mover.OpenOffset * mover.Position;
                transforms[n].LocalPosition = at;

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
                    _space!.MoveKinematic(body, at, Vector3.Zero);

                    // Then whatever it moved into is pushed out of its way, or it is blocked (issue #260).
                    Vector3 velocity = mover.OpenOffset * (mover.Direction / seconds);
                    var blocker = Push(entity, body, velocity);
                    if (!blocker.IsNull)
                    {
                        bool first = !mover.Blocked;
                        mover.Blocked = true;
                        if (mover.OnBlocked != MoverBlocked.Crush)
                        {
                            // Back where it was this tick: it never went into them.
                            mover.Position = was;
                            at = mover.Closed + mover.OpenOffset * was;
                            transforms[n].LocalPosition = at;
                            _space.MoveKinematic(body, at, Vector3.Zero);
                            if (mover.OnBlocked == MoverBlocked.Reverse) mover.Direction = (sbyte)-mover.Direction;
                        }
                        if (first || mover.OnBlocked == MoverBlocked.Crush) _world.FireOutput(entity, "OnBlocked", blocker);
                        continue;
                    }
                }
                mover.Blocked = false;

                if (mover.Direction > 0 && mover.Position >= 1f)
                {
                    mover.Direction = 0;
                    mover.HoldRemaining = mover.CloseAfter;
                    _world.FireOutput(entity, "OnFullyOpen");
                }
                else if (mover.Direction < 0 && mover.Position <= 0f)
                {
                    mover.Direction = 0;
                    _world.FireOutput(entity, "OnFullyClosed");
                }

                // Moving at the speed that takes it to where it will be next tick, so the step carries it
                // there and what rests on it with it; still once it has arrived.
                if (solid)
                {
                    float next = Math.Clamp(mover.Position + mover.Direction * dt / seconds, 0f, 1f);
                    Vector3 speed = mover.Direction == 0 || dt <= 0f ? Vector3.Zero : mover.OpenOffset * ((next - mover.Position) / dt);
                    _space!.MoveKinematic(body, at, speed);
                }
            }
        }
    }

    // Pushes everything the mover now overlaps out of its way: characters along the way out of the
    // mover, as far as they have room to go, and dynamic bodies by giving them at least the mover's speed
    // (the physics step does the rest). Returns the first character that has no room (squeezed against
    // a wall, or between a lift and the floor), or null when everything was pushed clear.
    private Entity Push(Entity self, in PhysicsBody mover, Vector3 velocity)
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
                Vector3 current = space.VelocityOf(body);
                float along = Vector3.Dot(current, away);
                float wanted = MathF.Max(Vector3.Dot(velocity, away), 0f);
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

        // Routed to the mover (issue #91): `Toggle` is a mover's here and a branch's on a logic_branch,
        // and a game may still register a global `Open` for things that are not movers.
        inputs.Register<Mover>("Open", static (World world, in IOContext io) => Set(world, io.Self, +1));
        inputs.Register<Mover>("Close", static (World world, in IOContext io) => Set(world, io.Self, -1));
        inputs.Register<Mover>("Toggle", static (World world, in IOContext io) =>
        {
            ref var mover = ref io.Self.GetComponent<Mover>();

            // Half way through opening, "toggle" means shut it — what it is *doing* matters more than
            // where it happens to be.
            sbyte direction = mover.Direction != 0 ? (sbyte)-mover.Direction
                                                   : mover.Position >= 1f ? (sbyte)-1 : (sbyte)+1;
            Set(world, io.Self, direction);
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
                if (entity.HasComponent<Mover>()) entity.GetComponent<Mover>().Closed = at;
            };
    }

    private static void Set(World world, Entity entity, sbyte direction)
    {
        if (!entity.HasComponent<Mover>()) return;
        ref var mover = ref entity.GetComponent<Mover>();

        // Already there: say so anyway, because a wire that opens an open door still expects its
        // `OnFullyOpen` to reach whatever it feeds (a mapper counting doors, for instance).
        if (direction > 0 && mover.Position >= 1f && mover.Direction == 0)
        {
            mover.HoldRemaining = mover.CloseAfter;
            world.FireOutput(entity, "OnFullyOpen");
            return;
        }
        if (direction < 0 && mover.Position <= 0f && mover.Direction == 0)
        {
            world.FireOutput(entity, "OnFullyClosed");
            return;
        }

        mover.Direction = direction;
    }
}

// A mover a game wants placed by hand rather than drawn in a map: `"mover": { "open": [0, 3, 0],
// "seconds": 1.5 }` on a prefab. Closed is wherever it was placed, which is why it reads the transform
// (Spawn places the entity before any part runs).
[PrefabPart("mover", Plugin = "sage.gameplay.movers")]
public sealed class MoverPart : IPrefabPart
{
    public Vector3 Open;               // where "open" is, relative to where it was placed
    public float Seconds = 1f;
    public float CloseAfter;           // > 0: shuts itself this long after opening
    public MoverBlocked OnBlocked;     // "Reverse" (default), "Stop" or "Crush", when something can't be pushed

    public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new Mover
    {
        OpenOffset = Open,
        Seconds = Seconds <= 0f ? 1f : Seconds,
        CloseAfter = CloseAfter,
        OnBlocked = OnBlocked,
        Closed = ctx.Entity.GetComponent<Transform>().LocalPosition,
    });
}
