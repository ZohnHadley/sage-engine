#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Camera rigs (issues #78, #79; REDESIGN §5 phase 4a, decision D2).
//
// A rig is a component on a **camera entity** that follows something — a pawn's head, a point over its
// shoulder — and writes the camera's `CameraPose` in FrameUpdate, before the CameraDirector reads it. The
// pawn itself carries no camera state, so its saves stay about the pawn; what the player chose about the
// view (which rig is on) is saved on the camera entity, like anything else about it.
//
// The player's own camera is an entity spawned from the `sage:player_camera` prefab by PlayerCameraSystem
// (a character plugin system) for each player pawn that has none following it. Experimental (SAGE0123):
// the multi-view renderer (#77) and the editor camera (#81) still push on this.

// The player's camera: the one PlayerCameraSystem spawned (or relinked) to follow a player pawn. Other
// cameras may follow the player too — a cutscene camera, a security monitor — without being this.
[Experimental("SAGE0123")]
[Tag("sage:player_camera")]
public struct PlayerCamera : ITag { }

// "components": { "sage:first_person_rig": { "enabled": true } }, or the `first_person_rig` part.
//
// Puts the camera in `Follow`'s head: at the character's eye (CharacterController.EyeOf — the height the
// swing and the Use action start from, so all three agree) of its interpolated pose, looking where its
// PawnIntent's view angles say. `Follow` is an entity, so content cannot write it; PlayerCameraSystem sets
// it for the player's camera, and a game's code sets it for its own. A pawn without a character sits the
// camera at its origin; one without a PawnIntent looks the way its transform does.
[Experimental("SAGE0123")]
[Component("sage:first_person_rig")]
public struct FirstPersonRig : IComponent
{
    public Entity Follow;   // by PersistentId in a save; null when it is gone (the rig then leaves the pose alone)
    [Property(Tooltip = "Off: it leaves the camera's pose alone (the player camera's other rig may be on)")]
    public bool Enabled;
}

// What kind of rig draws the screen this frame, for the code that draws differently in each: the
// crosshair, a viewmodel, a body that should not be seen from inside.
[Experimental("SAGE0123")]
public enum CameraRigKind
{
    None,           // no rig drives the screen: a fixed or scripted camera, ActiveCamera (no camera entity), or cam_free
    FirstPerson,
}

[Experimental("SAGE0123")]
public static class CameraRigs
{
    // The rig driving the screen's view this frame: read from CameraViews once the director has run (from
    // late FrameUpdate on — Extract, Render and Overlay all see this frame's). The question the crosshair
    // and a viewmodel ask ("am I looking out of the player's eyes?") in place of the obsolete
    // ActiveCamera.DrivenByRig. Allocates nothing.
    public static CameraRigKind MainViewRig(this World world)
    {
        if (!world.Resources.TryGet<CameraViews>(out var views) || views == null || !views.HasMain) return CameraRigKind.None;
        var camera = views.Main.Entity;
        if (camera.IsNull) return CameraRigKind.None;
        if (world.TryGet<FirstPersonRig>(camera, out var first) && first.Enabled) return CameraRigKind.FirstPerson;
        return CameraRigKind.None;
    }

    // Where `pawn` looks from and which way, at this frame's interpolation: its eye and its view angles.
    // False when it is not there to follow (null, destroyed, or without a transform).
    internal static bool TryHead(World world, RecordStore records, Entity pawn, float alpha,
                                 out Vector3 head, out Quaternion rotation)
    {
        if (!world.TryGet<GlobalTransform>(pawn, out var global))
        {
            head = default;
            rotation = Quaternion.Identity;
            return false;
        }
        var pose = global.Interpolated(alpha);
        head = pose.Position;
        if (world.TryGet<CharacterController>(pawn, out var character))
        {
            var profile = CharacterConventions.Of(world).ProfileOf(records, character.Profile);
            head = CharacterController.EyeOf(pose.Position, character, profile);
        }
        rotation = world.TryGet<PawnIntent>(pawn, out var intent)
            ? Quaternion.CreateFromYawPitchRoll(intent.Yaw, intent.Pitch, 0f)
            : pose.Rotation;
        return true;
    }
}
