#nullable enable
using System.Diagnostics.CodeAnalysis;

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

    public void Apply(in PrefabPartContext ctx) => AddTo(ctx.World, ctx.Entity, default);

    internal void AddTo(World world, Entity camera, Entity follow)
    {
        world.Add(camera, new FirstPersonRig { Follow = follow, Enabled = Enabled });
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
