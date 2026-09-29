#nullable enable
using System.Numerics;

namespace Sage.Simulation;

// The first-person rig (issue #27): it reads only what the simulation owns — the interpolated pose,
// the character's height and profile (CharacterController, CharacterConventions) and the view angles
// in PawnIntent — so it lives here rather than with a physics backend. CharacterModule
// (Sage.Physics3D) still installs it, with the character systems it goes with.

// FrameUpdate: puts the camera in the player's head, at display rate, from the interpolated pose and
// the view angles the command carried (06 §3.3, 16 §3.2). While this drives the camera it says so
// (ActiveCamera.DrivenByRig) and the editor's free camera stands aside; cam_free clears
// ActiveCamera.RigEnabled and this system gives the camera back.
[System("sage.character.camera", Phase.FrameUpdate)]   // before the CameraDirector, which orders itself after this
public sealed class FirstPersonCameraSystem : ISystem
{
    private readonly Query<GlobalTransform, CharacterController, PawnIntent> _pawns;
    private readonly ActiveCamera _camera;
    private readonly RecordStore _records;

    public FirstPersonCameraSystem(World world, RecordStore records)
    {
        _pawns = world.Query<GlobalTransform, CharacterController, PawnIntent>().AllTags(Tags.Get<PlayerControlled>());
        _camera = world.Resources.Get<ActiveCamera>();
        _records = records;
    }

    // The legacy rig: it writes ActiveCamera and its rig flags directly, which are obsolete for games
    // (issue #76) and stay its protocol with cam_free until it becomes a camera-entity rig (#78).
#pragma warning disable CS0618
    public void Run(in SystemContext ctx)
    {
        if (!_camera.RigEnabled)   // cam_free: the editor camera is flying instead
        {
            _camera.DrivenByRig = false;
            return;
        }

        float alpha = ctx.Frame.Alpha;
        foreach (var (globals, characters, intents, _) in _pawns.Chunks)
        {
            if (globals.Length == 0) continue;
            ref readonly var character = ref characters.Span[0];   // one local player
            var profile = CharacterConventions.Of(ctx.World).ProfileOf(_records, character.Profile);
            float eye = character.Height + profile.EyeOffset;
            _camera.Position = globals.Span[0].Interpolated(alpha).Position + Vector3.UnitY * eye;
            _camera.Rotation = Quaternion.CreateFromYawPitchRoll(intents.Span[0].Yaw, intents.Span[0].Pitch, 0);
            _camera.DrivenByRig = true;
            return;
        }
    }
#pragma warning restore CS0618
}
