#nullable enable
using System;
using System.Collections.Generic;

namespace Sage.Physics3D;

// A body that walks (10 §3, 16 §3.1): the capsule the engine moves, the intent its controller writes,
// and the player's camera that follows it (issue #78: a camera entity with a first-person rig).
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

        // A player to command and to look through (issue #13): the host samples into PlayerInput, and
        // the player's camera entity (issue #78) is mirrored into ActiveCamera by the CameraDirector. The
        // client may have installed ActiveCamera already.
        world.Resources.GetOrAdd(() => new PlayerInput());
        world.Resources.GetOrAdd(() => new ActiveCamera());
        // The game's words for a character (issue #26): gameplay installs one that reads its
        // conventions record before this runs; without gameplay, the engine's own.
        world.Resources.GetOrAdd(() => new CharacterConventions());

        world.AddSystem(new PlayerControlSystem(world));
        world.AddSystem(new CharacterMovementSystem(world, _records!, _actions!));

        // The player's camera (issue #78, decision D2): a camera entity per player pawn, and the rig that
        // puts it in the pawn's head. Cameras are experimental (SAGE0123); this plugin is their first user.
#pragma warning disable SAGE0123
        world.AddSystem(new PlayerCameraSystem(world));
        world.AddSystem(new FirstPersonRigSystem(world, _records!));
#pragma warning restore SAGE0123
    }
}
