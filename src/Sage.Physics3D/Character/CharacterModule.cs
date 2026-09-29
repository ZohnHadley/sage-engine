#nullable enable
using System;
using System.Collections.Generic;

namespace sage_engine;

// A body that walks (10 §3, 16 §3.1): the capsule the engine moves, the intent its controller writes,
// and the first-person rig that sits in its head.
[Plugin("sage.gameplay.character", "0.1.0")]
public sealed class CharacterModule : IModule
{
    private RecordStore? _records;
    private ActionRegistry? _actions;

    public IReadOnlyList<Type> Dependencies => new[] { typeof(PhysicsModule) };   // characters sweep the space

    public void Init(ModuleContext ctx)
    {
        _records = ctx.Engine.Records;
        _actions = ctx.Engine.Actions;

        // Gameplay actions (08 §3.2): the simulation defines them, so a headless server has the same
        // ids and a PlayerCommand means the same thing on both sides.
        _actions.Register("Move", ActionKind.Axis2D);
        _actions.Register("Jump", ActionKind.Button);
        _actions.Register("Run", ActionKind.Button);
        _actions.Register("Crouch", ActionKind.Button);
    }

    public void OnWorldCreated(World world)
    {
        // What the Commands phase promises everything downstream: by the end of it, a pawn's intent
        // is what this tick will act on. Both controllers write it there and CharacterMovementSystem
        // consumes it in PrePhysics, so anything writing it later is acting a tick late — which is
        // exactly review #48, found by reading code rather than by the engine saying so (03 §3.5).
        world.Contracts.FinalAfter<PawnIntent>(Phase.Commands);

        // A player to command and to look through (issue #13): the host samples into PlayerInput and
        // the first-person rig drives ActiveCamera. The client may have installed the camera already.
        world.Resources.GetOrAdd(() => new PlayerInput());
        world.Resources.GetOrAdd(() => new ActiveCamera());

        world.AddSystem(new PlayerControlSystem(world));
        world.AddSystem(new CharacterMovementSystem(world, _records!, _actions!));
        world.AddSystem(new FirstPersonCameraSystem(world, _records!));
    }
}
