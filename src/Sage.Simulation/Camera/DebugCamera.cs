#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// A camera a tool flies by hand (issue #81): the editor's free camera (`cam_free`) and the camera behind
// its viewport. **Not a special case in the director** — until #81 the director read
// `ActiveCamera.RigEnabled` to hand the screen to the free camera, and the free camera wrote
// `ActiveCamera` whenever `DrivenByRig` said nothing else had. Now it is a camera entity like any other:
//
//   - a `Camera` and a `CameraPose` the tool writes each frame before the director runs (the host's
//     update does, which comes before the frame's FrameUpdate; a system would order itself
//     `Before = new[] { CameraDirector.Id }`), so it is resolved the same frame;
//   - at `OverridePriority` while it overrides its target — above every camera a game places, a
//     scripted cut included — so `cam_free` flies over whatever had the screen and turning it off gives
//     the screen back to the next camera down;
//   - at `IdlePriority` otherwise — below every camera a game places, above only the director's
//     ActiveCamera fallback — so it draws only where nothing else does: the free camera flying over a
//     world with no player, which is what the editor camera did before #81.
//
// The director mirrors it into ActiveCamera when it has the screen, as it does any camera (decision
// D1), so audio, weather and gameplay follow it. It is not saved (no `Persistent`), it is not a rig
// (`world.MainViewRig()` is None: no crosshair, no viewmodel, the player's body drawn), and a scripted
// camera's input lock lets go while it has the screen, because that camera no longer does.
[Experimental("SAGE0123")]
public static class DebugCamera
{
    // Above every camera a game can place. A tie with another override goes to the lower entity id.
    public const int OverridePriority = int.MaxValue;

    // Below every camera a game can place; int.MinValue is the ActiveCamera fallback's (CameraView.Priority).
    public const int IdlePriority = int.MinValue + 1;

    // A debug camera drawing to `target` ("" = the screen): enabled, idle, looking down -Z from the origin
    // until it is driven. The tool keeps the entity and drives it each frame.
    public static Entity Spawn(World world, string name, string target = "")
    {
        var entity = world.Create(Transform.Identity, name);
        var camera = Camera.Perspective(priority: IdlePriority);
        camera.Target = target ?? "";
        world.Add(entity, camera);
        world.Add(entity, new CameraPose(Vector3.Zero, Quaternion.Identity));
        return entity;
    }

    // Where it looks from this frame, and whether it overrides its target. Enables it. Allocates nothing.
    public static void Drive(World world, Entity camera, Vector3 position, Quaternion rotation, bool overriding)
    {
        ref var settings = ref world.Get<Camera>(camera);
        settings.Enabled = true;
        settings.Priority = overriding ? OverridePriority : IdlePriority;
        if (!world.Has<CameraPose>(camera)) world.Add(camera, new CameraPose(position, rotation));
        ref var pose = ref world.Get<CameraPose>(camera);
        pose.Position = position;
        pose.Rotation = rotation;
    }
}
