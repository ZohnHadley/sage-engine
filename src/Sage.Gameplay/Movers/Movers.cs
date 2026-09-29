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
}

// Moves what has to move, and says when it arrives.
[System("sage.movers.move", Phase.Gameplay)]
internal sealed class MoverSystem : ISystem
{
    private readonly World _world;
    private readonly Query<Mover, Transform> _movers;
    private readonly IPhysicsWorld? _space;

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
                mover.Position = Math.Clamp(mover.Position + mover.Direction * dt / seconds, 0f, 1f);

                var at = mover.Closed + mover.OpenOffset * mover.Position;
                transforms[n].LocalPosition = at;

                // The geometry is a static in the physics space, and a static does not follow a
                // transform: it has to be told, and its broadphase bounds rebuilt with it. Without this
                // the door opens on screen and stays shut to walk into.
                if (_space != null && entity.TryGetComponent<PhysicsBody>(out var body))
                    _space.MoveStatic(body, at);

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
            }
        }
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

        inputs.Register("Open", static (World world, in IOContext io) => Set(world, io.Self, +1));
        inputs.Register("Close", static (World world, in IOContext io) => Set(world, io.Self, -1));
        inputs.Register("Toggle", static (World world, in IOContext io) =>
        {
            if (!io.Self.HasComponent<Mover>()) return;
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

    public void Apply(in PrefabPartContext ctx) => ctx.World.Add(ctx.Entity, new Mover
    {
        OpenOffset = Open,
        Seconds = Seconds <= 0f ? 1f : Seconds,
        CloseAfter = CloseAfter,
        Closed = ctx.Entity.GetComponent<Transform>().LocalPosition,
    });
}
