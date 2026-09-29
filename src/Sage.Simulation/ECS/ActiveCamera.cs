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
// entity (PlayerCameraSystem, FirstPersonRig), and since #81 so is the editor's free camera
// (DebugCamera). Until one draws — no player yet, or a world without the character plugin, in a build
// without the editor — it is left to whoever writes it, and the director makes the screen view from it.
// Read it freely (or read `world.TryGetMainView`, the same view with its projection); new code that
// *drives* a camera puts a Camera on an entity (or a rig that writes its CameraPose) instead of writing
// here.
public sealed class ActiveCamera
{
    public Vector3 Position;
    public Quaternion Rotation = Quaternion.Identity;
    public float FovY = 45f * System.MathF.PI / 180f;   // vertical field of view, radians
    public float Near = 0.1f;                             // 06 §10: 0.01 wasted depth precision
    public float Far = 1000f;

    // The protocol between the director and the editor's free camera until #81, which made the free
    // camera a camera entity (DebugCamera). Kept, [Obsolete], because they shipped in 0.1.0 (removing
    // them is a break, RELEASING.md); **the engine neither reads nor writes them any more**, so setting
    // RigEnabled changes nothing and DrivenByRig stays whatever it was set to. Ask `world.MainViewRig()`
    // for the rig drawing the screen, and `CameraViews.Main.Entity` for the camera.
    [Obsolete("Inert since issue #81: nothing reads it. The editor's free camera is a camera entity (DebugCamera) at a priority " +
              "above every other camera; drive a view with a Camera component and a rig that writes CameraPose.")]
    public bool RigEnabled = true;
    [Obsolete("Inert since issue #81: nothing writes it. Whether a view is driven is CameraViews' business (CameraView.Entity, the " +
              "rig's CameraPose); ask world.MainViewRig() for the rig drawing the screen.")]
    public bool DrivenByRig;
}
