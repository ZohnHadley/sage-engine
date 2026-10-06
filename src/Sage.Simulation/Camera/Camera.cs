#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// Cameras as entities (issue #76, REDESIGN §5 phase 4a, docs/design/06 §3.11).
//
// A camera is an entity with a `Camera` component: what it sees (projection, field of view or height,
// clip planes), where it draws (a normalised viewport of a target; an empty target is the screen) and
// how much it wants to (priority, enabled). Where it looks *from* is its pose: a rig's `CameraPose` when
// one drives it, otherwise the entity's interpolated `GlobalTransform`. Once a frame the
// `CameraDirector` resolves every camera into the world's `CameraViews` — one view per target, the
// highest-priority enabled camera winning — and mirrors the screen's into `ActiveCamera` for the code
// that still reads that (decision D1, see ActiveCamera).
//
// Experimental (SAGE0123, MAKING_A_GAME §10b): the rigs (#78, #79), the multi-view renderer (#77) and the
// editor (#81) will all push on this shape before it settles.

// How a camera projects: with perspective (3D), or orthographically (2D, top-down, isometric — the
// REDESIGN §0.5 test: 2D is first-class, not a special case of 3D).
[Experimental("SAGE0123")]
public enum CameraProjection
{
    Perspective,
    Orthographic,
}

// A rectangle of a target, normalised: 0..1 across and down from the target's **top-left** corner, the
// way screens and render targets are addressed. { 0, 0, 1, 1 } is all of it; { 0.5, 0, 0.5, 1 } is the
// right half. A rectangle with no area (the default, all zeros) also means all of it, so a camera
// written in content without a viewport draws full-screen.
[Experimental("SAGE0123")]
public struct CameraViewport : IEquatable<CameraViewport>
{
    [Property(Min = 0, Max = 1, Tooltip = "Left edge, 0..1 of the target's width")]
    public float X;
    [Property(Min = 0, Max = 1, Tooltip = "Top edge, 0..1 of the target's height")]
    public float Y;
    [Property(Min = 0, Max = 1, Tooltip = "0..1 of the target's width; 0 = all of it")]
    public float Width;
    [Property(Min = 0, Max = 1, Tooltip = "0..1 of the target's height; 0 = all of it")]
    public float Height;

    public CameraViewport(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public static CameraViewport Full => new(0f, 0f, 1f, 1f);

    public readonly bool IsEmpty => !(Width > 0f) || !(Height > 0f);

    // What the director resolves it to: clamped into the target, and an empty one made whole.
    public readonly CameraViewport Resolved()
    {
        if (IsEmpty) return Full;
        float x = Math.Clamp(X, 0f, 1f), y = Math.Clamp(Y, 0f, 1f);
        float w = Math.Clamp(Width, 0f, 1f - x), h = Math.Clamp(Height, 0f, 1f - y);
        return w > 0f && h > 0f ? new CameraViewport(x, y, w, h) : Full;
    }

    public readonly bool Equals(CameraViewport other) =>
        X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
    public override readonly bool Equals(object? obj) => obj is CameraViewport other && Equals(other);
    public override readonly int GetHashCode() => HashCode.Combine(X, Y, Width, Height);
    public static bool operator ==(CameraViewport a, CameraViewport b) => a.Equals(b);
    public static bool operator !=(CameraViewport a, CameraViewport b) => !a.Equals(b);
    public override readonly string ToString() => $"{X:0.###},{Y:0.###} {Width:0.###}x{Height:0.###}";
}

// "components": { "sage:camera": { … } }, or the `camera` prefab part (CameraPart), which starts from
// the defaults below. What a camera is; where it looks from is its pose (see CameraPose).
//
// A struct's default is all zeros, so build one with `Camera.Perspective(...)` / `Camera.Orthographic(...)`
// (or the part), not `new Camera()`: a zeroed camera is disabled. The director is forgiving about the
// rest — a zero or nonsensical field of view, height or clip plane resolves to the default below, and an
// empty viewport to the whole target — so a half-written camera in content still draws something.
//
// Units: `FovY` is in **degrees**, because people write it; the resolved `CameraView.FovY` (and
// `ActiveCamera.FovY`) are radians, because matrices want them.
[Experimental("SAGE0123")]
[Component("sage:camera")]
public struct Camera : IComponent
{
    public const float DefaultFovY = 45f;           // degrees; ActiveCamera's default, so switching over changes nothing
    public const float DefaultOrthoHeight = 10f;    // metres of world, top to bottom of the viewport
    public const float DefaultNear = 0.1f;          // 06 §10: 0.01 wasted depth precision
    public const float DefaultFar = 1000f;

    [Property(Tooltip = "Perspective (3D) or Orthographic (2D, top-down, isometric)")]
    public CameraProjection Projection;
    [Property(Min = 1, Max = 179, Unit = "deg", Category = "Perspective", Tooltip = "Vertical field of view")]
    public float FovY;
    [Property(Min = 0, Unit = "m", Category = "Orthographic", Tooltip = "How much world the viewport shows, top to bottom")]
    public float OrthoHeight;
    [Property(Min = 0, Unit = "m", Tooltip = "Near clip plane")]
    public float Near;
    [Property(Min = 0, Unit = "m", Tooltip = "Far clip plane")]
    public float Far;
    [Property(Tooltip = "Of the enabled cameras drawing to one target, the highest wins")]
    public int Priority;
    [Property(Tooltip = "Off: never chosen, whatever its priority")]
    public bool Enabled;
    [Property(Category = "Output", Tooltip = "The part of the target it draws to, 0..1 from the top-left; empty = all of it")]
    public CameraViewport Viewport;
    [Property(Category = "Output", Tooltip = "The render target it draws to, by name; empty = the screen")]
    public string Target;

    // Split screen (issue 4n-19): the cameras on one target compete per slot, so two players' cameras,
    // each with its own viewport, both draw. The screen's lowest slot is its main view.
    [Property(Min = 0, Max = 15, Category = "Output", Tooltip = "Split screen: each slot of a target draws its own view (its highest-priority camera); the screen's lowest is the main view")]
    public int Slot;

    // The passes a view from this camera leaves out (issue 4n-19). Every view draws the whole pass set by
    // default — the sun's shadows, a first-person viewmodel, the sky, debug lines — and these turn one off:
    // a cheap security monitor or a minimap. Written as "no" so that a camera with none of them set (a
    // zeroed component, an old save) draws everything.
    [Property(Category = "Passes", Tooltip = "This view has no sun shadows of its own (a cheap monitor, a minimap)")]
    public bool NoShadows;
    [Property(Category = "Passes", Tooltip = "This view draws no first-person viewmodel, even from a first-person rig")]
    public bool NoViewmodel;
    [Property(Category = "Passes", Tooltip = "This view draws no sky: the clear colour stays behind the world")]
    public bool NoSky;
    [Property(Category = "Passes", Tooltip = "This view draws no debug lines")]
    public bool NoDebugLines;

    public readonly bool IsScreen => string.IsNullOrEmpty(Target);

    public static Camera Perspective(float fovYDegrees = DefaultFovY, int priority = 0,
                                     float near = DefaultNear, float far = DefaultFar) => new()
    {
        Projection = CameraProjection.Perspective,
        FovY = fovYDegrees,
        OrthoHeight = DefaultOrthoHeight,
        Near = near,
        Far = far,
        Priority = priority,
        Enabled = true,
        Viewport = CameraViewport.Full,
        Target = "",
    };

    public static Camera Orthographic(float height = DefaultOrthoHeight, int priority = 0,
                                      float near = DefaultNear, float far = DefaultFar) => new()
    {
        Projection = CameraProjection.Orthographic,
        FovY = DefaultFovY,
        OrthoHeight = height,
        Near = near,
        Far = far,
        Priority = priority,
        Enabled = true,
        Viewport = CameraViewport.Full,
        Target = "",
    };
}

// Where a camera looks from this frame, in origin space, when a **rig** drives it (decision D2): a rig
// lives on the camera entity (or finds it), follows whatever it follows — a pawn's head, a shoulder, a
// spline — and writes this in FrameUpdate at the display rate, *before* the director
// (`[System(..., Phase.FrameUpdate, Before = new[] { CameraDirector.Id })]`). A camera without one
// looks from its interpolated `GlobalTransform`, which is what a fixed or scripted camera wants: place it
// and move it like any other entity.
//
// Presence is the switch: add it to take the camera over, remove it to give the pose back to the
// transform. Not saved (display-rate, rewritten every frame) and moved by origin rebasing.
[Experimental("SAGE0123")]
[Transient]
[Component("sage:camera_pose")]
public struct CameraPose : IComponent
{
    public Vector3 Position;
    public Quaternion Rotation;

    public CameraPose(Vector3 position, Quaternion rotation)
    {
        Position = position;
        Rotation = rotation;
    }
}
