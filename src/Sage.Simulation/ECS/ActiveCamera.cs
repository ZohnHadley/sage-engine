#nullable enable
using System;
using System.Numerics;

namespace Sage.Simulation;

// World resource: where the view the player sees is, in origin space. Gameplay reads it ("face the
// player's view", "is this near the camera"), without depending on the editor or the renderer.
//
// Since issue #76 cameras are entities (`Camera`, `CameraPose`, `CameraViews`, `CameraDirector`), and
// this is a **mirror** of the screen's view (decision D1): once a camera entity draws to the screen, only
// the CameraDirector writes it, once a frame in FrameUpdate. Until one does — every game today — it is
// driven as it always was: by the first-person rig (FirstPersonCameraSystem) or the editor's free
// camera, and the director makes the screen view from it. Read it freely; new code that *drives* a camera
// puts a Camera on an entity (or a rig that writes its CameraPose) instead of writing here.
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
    //   DrivenByRig  status, written by a rig (or the CameraDirector, for a camera entity): something
    //                drove the camera this frame, so the editor's free camera must leave it alone.
    //
    // Obsolete for games (#76): they are the protocol between the legacy first-person rig, `cam_free` and
    // the crosshair, and go when the rigs become camera entities (#78) and the editor camera becomes one
    // (#81). They still work exactly as before.
    [Obsolete("Cameras are entities now (issue #76): drive a view with a Camera component and a rig that writes CameraPose, " +
              "and read the resolved views from CameraViews. RigEnabled stays the cam_free switch until the editor camera is an entity (#81).")]
    public bool RigEnabled = true;
    [Obsolete("Cameras are entities now (issue #76): whether a view is driven is CameraViews' business (CameraView.Entity, the " +
              "rig's CameraPose). DrivenByRig stays the legacy rig's signal to cam_free and the crosshair until #78/#81.")]
    public bool DrivenByRig;
}
