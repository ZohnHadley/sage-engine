#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Simulation;

// View and projection matrices, in System.Numerics (the simulation has no MonoGame; the client converts
// implicitly). The conventions are MonoGame's and the renderer's today (CameraExtract): right-handed, a
// camera looks down its local -Z with +Y up, and clip-space depth runs 0 (near) to 1 (far) — which is
// what Matrix4x4.CreatePerspectiveFieldOfView / CreateOrthographic produce, and what XNA's did.
//
// Row vectors, as System.Numerics and MonoGame both use: clip = position * View * Projection.
[Experimental("SAGE0123")]
public static class CameraMath
{
    // The largest vertical field of view a perspective camera resolves to; a projection at π is singular.
    public const float MaxFovY = 179f * MathF.PI / 180f;

    public static Vector3 Forward(Quaternion rotation) => Vector3.Transform(TransformMath.Forward, rotation);
    public static Vector3 Up(Quaternion rotation) => Vector3.Transform(TransformMath.Up, rotation);

    // World (origin space) → view space.
    public static Matrix4x4 View(Vector3 position, Quaternion rotation) =>
        Matrix4x4.CreateLookAt(position, position + Forward(rotation), Up(rotation));

    // The same without the translation, for camera-relative rendering (06 §3.3): positions arrive with
    // the camera's already subtracted, so the view only turns them.
    public static Matrix4x4 ViewRelative(Quaternion rotation) =>
        Matrix4x4.CreateLookAt(Vector3.Zero, Forward(rotation), Up(rotation));

    // `fovY` in radians, `aspect` = width / height.
    public static Matrix4x4 Perspective(float fovY, float aspect, float near, float far) =>
        Matrix4x4.CreatePerspectiveFieldOfView(fovY, aspect, near, far);

    // `height` metres of world top to bottom, centred on the camera; the width follows the aspect, so
    // a wider window shows more world rather than stretching it.
    public static Matrix4x4 Orthographic(float height, float aspect, float near, float far) =>
        Matrix4x4.CreateOrthographic(height * aspect, height, near, far);

    public static Matrix4x4 Projection(in CameraView view, float aspect) => view.Projection == CameraProjection.Orthographic
        ? Orthographic(view.OrthoHeight, aspect, view.Near, view.Far)
        : Perspective(view.FovY, aspect, view.Near, view.Far);

    // The aspect of a normalised viewport on a target of this many pixels; 1 when either is degenerate.
    public static float Aspect(int targetWidth, int targetHeight, in CameraViewport viewport)
    {
        var v = viewport.Resolved();
        float w = targetWidth * v.Width, h = targetHeight * v.Height;
        return w > 0f && h > 0f ? w / h : 1f;
    }
}
