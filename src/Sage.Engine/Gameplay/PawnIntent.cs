#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using Friflo.Engine.ECS;

namespace sage_engine;

// Controllers and pawns (docs/design/16 §3.1). A player and an AI drive a body the same way: they
// write PawnIntent, and movement, combat and interaction only ever read that. It is the seed of the
// gameplay framework; GameRules, possession and AI controllers come with 16/F19.

// Marks a body a controller can drive.
public struct Pawn : IComponent { }

// Tag: this pawn is driven by the local player's PlayerCommand (08).
public struct PlayerControlled : ITag { }

// What a controller wants the pawn to do this tick. Written by controllers only.
public struct PawnIntent : IComponent
{
    public Vector2 Move;        // x = right, y = forward; length <= 1
    public float Yaw, Pitch;    // radians, absolute view angles
    public ActionMask Held;
    public ActionMask Pressed;
}

// Commands phase: the local player's command becomes intent (16 §3.1, 08 §3.4). AI controllers write
// the same component from their own state, so everything downstream is shared.
public sealed class PlayerControlSystem : ISystem
{
    private readonly ArchetypeQuery<PawnIntent> _pawns;

    public PlayerControlSystem(World world)
    {
        _pawns = world.Query<PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
    }

    public void Run(in SystemContext ctx)
    {
        var input = ctx.World.Resources.Get<PlayerInput>();
        if (!input.HasCommand) return;
        ref readonly var command = ref input.Command;

        foreach (var (intents, _) in _pawns.Chunks)
        {
            var intent = intents.Span;
            for (int n = 0; n < intent.Length; n++)
            {
                intent[n].Move = command.Move;
                intent[n].Yaw = command.ViewYaw;
                intent[n].Pitch = command.ViewPitch;
                intent[n].Held = command.Held;
                intent[n].Pressed = command.Pressed;
            }
        }
    }
}

// The seed of the gameplay framework module (docs/design/16 §3.2): today it registers the movement
// profile records and installs the character systems in every world. GameRules, possession, AI
// controllers, abilities and combat join it as 16/F19 is built, and it moves to Sage.Framework then.
public sealed class GameplayModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(PhysicsModule) };   // characters sweep the space

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;
        _records.Register<MovementProfileRecord>();

        // Gameplay actions (08 §3.2): the simulation defines them, so a headless server has the same
        // ids and a PlayerCommand means the same thing on both sides.
        _actions.Register("Move", ActionKind.Axis2D);
        _actions.Register("Jump", ActionKind.Button);
        _actions.Register("Run", ActionKind.Button);
        _actions.Register("Crouch", ActionKind.Button);
        _actions.Register("Attack", ActionKind.Button);
        _actions.Register("Use", ActionKind.Button);
    }

    public void OnWorldCreated(World world)
    {
        world.AddSystem(new PlayerControlSystem(world), Phase.Commands);
        world.AddSystem(new CharacterMovementSystem(world, _records!, _actions!), Phase.PrePhysics,
            before: new[] { typeof(PhysicsSyncSystem) });
        world.AddSystem(new FirstPersonCameraSystem(world, _records!), Phase.FrameUpdate);
    }
}
