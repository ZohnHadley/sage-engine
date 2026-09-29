#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// "first_person_rig": {}
//
// A first-person rig on this camera (FirstPersonRig), and the CameraPose it writes. What it follows is an
// entity, which content cannot name: PlayerCameraSystem sets it on the player's camera, and a game's code
// sets it on its own. The engine's, like the `camera` part it goes with, so a prefab using it loads in any
// game; the systems that move it come with the character plugin.
[Experimental("SAGE0123")]
[PrefabPart("first_person_rig", Plugin = RegistrationOwners.Core)]
public sealed class FirstPersonRigPart : IPrefabPart
{
    [Property(Tooltip = "Off: it leaves the camera's pose alone (the player camera's other rig may be on)")]
    public bool Enabled = true;
    [Property(Tooltip = "Draw the followed body from this view (a game with full-body awareness); off hides it")]
    public bool ShowBody;

    public void Apply(in PrefabPartContext ctx) => AddTo(ctx.World, ctx.Entity, default);

    internal void AddTo(World world, Entity camera, Entity follow)
    {
        world.Add(camera, new FirstPersonRig { Follow = follow, Enabled = Enabled, ShowBody = ShowBody });
        RigPose.Ensure(world, camera);
    }
}

// "third_person_rig": { "distance": 4, "shoulderOffset": [0.5, 0.3, 0], "collision": ["default"] }
//
// An over-the-shoulder rig on this camera (ThirdPersonRig), with working defaults: `{}` is a camera three
// metres behind the right shoulder that keeps out of the scenery. `collision` names physics layers
// (physics_layers, 10 §3); the component keeps them as a mask. The player camera has one switched off,
// for the 1P/3P toggle. Like `first_person_rig`, the engine's.
[Experimental("SAGE0123")]
[PrefabPart("third_person_rig", Plugin = RegistrationOwners.Core)]
public sealed class ThirdPersonRigPart : IPrefabPart
{
    [Property(Tooltip = "Off: it leaves the camera's pose alone (the player camera's other rig may be on)")]
    public bool Enabled = true;
    [Property(Min = 0, Unit = "m", Tooltip = "How far behind the shoulder, along the view, when nothing is in the way")]
    public float Distance = ThirdPersonRig.DefaultDistance;
    [Property(Unit = "m", Tooltip = "From the eye, in the view's frame: x right, y up, z back")]
    public Vector3 ShoulderOffset = ThirdPersonRig.DefaultShoulderOffset;
    [Property(Min = 0, Unit = "m", Category = "Collision", Tooltip = "Radius of the sphere swept from the head to keep the camera out of walls")]
    public float ProbeRadius = ThirdPersonRig.DefaultProbeRadius;
    [Property(Category = "Collision", Tooltip = "Physics layers the probe stops at, by name; empty = every layer")]
    public List<string> Collision = new() { "default" };
    [Property(Min = 0, Unit = "s", Category = "Collision", Tooltip = "How long it takes to ease back out once the way clears; 0 = at once")]
    public float Smoothing = ThirdPersonRig.DefaultSmoothing;

    public void Apply(in PrefabPartContext ctx)
    {
        if (!(Distance >= 0f)) ctx.Warn($"distance {Distance} is not a distance; 0 is used");
        uint layers = 0;
        LayerMatrix? matrix = ctx.World.Resources.TryGet<IPhysicsWorld>(out var physics) && physics != null ? physics.Layers : null;
        foreach (var name in Collision)
        {
            if (matrix == null)
            {
                if (string.Equals(name, "default", StringComparison.OrdinalIgnoreCase)) layers |= 1u;   // layer 0 without a physics world
                continue;
            }
            if (matrix.TryIndexOf(name, out byte layer)) layers |= 1u << layer;
            else ctx.Warn($"no physics layer '{name}'; the probe ignores it");
        }
        AddTo(ctx.World, ctx.Entity, default, layers);
    }

    internal void AddTo(World world, Entity camera, Entity follow, uint layers = 1u)
    {
        var rig = ThirdPersonRig.Create(follow, Enabled);
        rig.Distance = Distance >= 0f ? Distance : 0f;
        rig.ShoulderOffset = ShoulderOffset;
        rig.ProbeRadius = MathF.Max(ProbeRadius, 0f);
        rig.CollisionLayers = layers;
        rig.Smoothing = MathF.Max(Smoothing, 0f);
        world.Add(camera, rig);
        RigPose.Ensure(world, camera);
    }
}

internal static class RigPose
{
    // A rig drives its camera through CameraPose, and the director only reads a pose that is there.
    public static void Ensure(World world, Entity camera)
    {
        if (!world.Has<CameraPose>(camera))
            world.Add(camera, new CameraPose(default, System.Numerics.Quaternion.Identity));
    }
}
