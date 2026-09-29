#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// World resource: where the view the player sees is, in origin space. Gameplay reads it ("face the
// player's view", "is this near the camera"), without depending on the editor or the renderer.
//
// Since issue #76 cameras are entities (`Camera`, `CameraPose`, `CameraViews`, `CameraDirector`), and
// this is a **mirror** of the screen's view (decision D1): once a camera entity draws to the screen, only
// the CameraDirector writes it, once a frame in FrameUpdate. Since #78 the player's camera is such an
// entity (PlayerCameraSystem, FirstPersonRig). Until one draws — no player yet, or a world without the
// character plugin — it is left to whoever writes it (the editor's free camera), and the director makes
// the screen view from it. Read it freely; new code that *drives* a camera puts a Camera on an entity (or
// a rig that writes its CameraPose) instead of writing here.
//
// It is also the view the renderer draws (CameraExtract, docs/design/06 §3.3) until the multi-view
// renderer (#77) reads CameraViews.
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
    //   DrivenByRig  status, written by the CameraDirector: a camera entity drew the screen this frame,
    //                so the editor's free camera must leave it alone.
    //
    // Obsolete for games (#76): they are the protocol between the director and `cam_free`, and go when the
    // editor camera becomes a camera entity (#81). The crosshair and viewmodels ask
    // `world.MainViewRig()` instead (#78). They still work exactly as before.
    [Obsolete("Cameras are entities now (issue #76): drive a view with a Camera component and a rig that writes CameraPose, " +
              "and read the resolved views from CameraViews. RigEnabled stays the cam_free switch until the editor camera is an entity (#81).")]
    public bool RigEnabled = true;
    [Obsolete("Cameras are entities now (issue #76): whether a view is driven is CameraViews' business (CameraView.Entity, the " +
              "rig's CameraPose); ask world.MainViewRig() for the rig drawing the screen. DrivenByRig stays the director's signal to cam_free until #81.")]
    public bool DrivenByRig;
}
