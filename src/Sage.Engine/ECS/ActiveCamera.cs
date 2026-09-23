#nullable enable
using System.Numerics;

namespace sage_engine;

// World resource every World has: where the active camera is, in origin space. The host (or the
// camera rig that owns the view) updates it every frame; gameplay reads it (e.g. "face the player's
// view"), without depending on the editor or the renderer.
//
// It is also the view the renderer draws (CameraExtract, docs/design/06 §3.3) until camera
// components and rigs exist (06 §4, 16).
public sealed class ActiveCamera
{
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public float FovY = 45f * System.MathF.PI / 180f;   // vertical field of view, radians
    public float Near = 0.1f;                             // 06 §10: 0.01 wasted depth precision
    public float Far = 1000f;

    // Two different things, and they are both needed (review #46):
    //   RigEnabled   policy, written by the host: may camera rigs drive this camera at all? `cam_free`
    //                clears it so the editor camera can fly while the game keeps running.
    //   DrivenByRig  status, written by a rig: one drove the camera this frame, so the editor's free
    //                camera must leave it alone.
    public bool RigEnabled = true;
    public bool DrivenByRig;
}
