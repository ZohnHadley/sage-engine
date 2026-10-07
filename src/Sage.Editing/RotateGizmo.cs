#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Editing;

// The rotate-about-Y gizmo's maths (docs/design/15 §6): a ring of radius `size` in the horizontal plane
// through `origin`. Angles are radians about +Y, positive as Quaternion.CreateFromAxisAngle(UnitY, a)
// turns things (right-handed: from +Z toward +X).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class RotateGizmo
{
    internal const float RingGrabWidth = 0.1f;   // of size, either side of the ring

    // True when `ray` meets the horizontal plane within a grab width of the ring.
    public static bool HitTest(in EditorRay ray, Vector3 origin, float size)
    {
        if (size <= 0f || ray.IntersectPlane(origin, Vector3.UnitY) is not { } t) return false;
        Vector3 local = ray.At(t) - origin;
        return MathF.Abs(MathF.Sqrt(local.X * local.X + local.Z * local.Z) - size) <= RingGrabWidth * size;
    }

    // How far a drag has turned the thing about Y, in (-π, π], between `startRay` and `currentRay`;
    // `stepDegrees` (0 = off) snaps it. Null when a ray is parallel to the plane or it is behind the
    // camera, or a ray lands on the axis itself (no direction to turn from).
    public static float? Drag(in EditorRay startRay, in EditorRay currentRay, Vector3 origin, float stepDegrees = 0f)
    {
        if (AngleAt(startRay, origin) is not { } a0 || AngleAt(currentRay, origin) is not { } a1) return null;
        float delta = a1 - a0;
        if (delta > MathF.PI) delta -= 2f * MathF.PI;
        else if (delta <= -MathF.PI) delta += 2f * MathF.PI;
        delta = Snap.Angle(delta, stepDegrees);
        if (delta > MathF.PI + 1e-4f) delta -= 2f * MathF.PI;   // a snap can round past +π
        return delta;
    }

    // ---- Three rings (issue #367) ---------------------------------------------------------------------
    //
    // A ring about each of the gizmo's axes, X, Y and Z, turned by `orientation` in local space. Each is
    // the Y ring above seen from a frame whose Y is that axis, so the angles keep the right-handed sense:
    // positive turns about the axis as Quaternion.CreateFromAxisAngle(axis, a) does.

    // The ring under `ray`, the nearest where two cross, or None.
    public static GizmoHandle HitTest(in EditorRay ray, Vector3 origin, float size, Quaternion orientation)
    {
        GizmoHandle best = GizmoHandle.None;
        float nearest = float.MaxValue;
        foreach (var axis in new[] { GizmoHandle.X, GizmoHandle.Y, GizmoHandle.Z })
        {
            var local = TranslateGizmo.Local(ray, origin, RingFrame(axis, orientation));
            if (!HitTest(local, Vector3.Zero, size) || local.IntersectPlane(Vector3.Zero, Vector3.UnitY) is not { } t || t >= nearest) continue;
            best = axis;
            nearest = t;
        }
        return best;
    }

    // How far a drag of the `axis` ring has turned the thing about that axis (radians, as Drag above).
    public static float? Drag(GizmoHandle axis, in EditorRay startRay, in EditorRay currentRay, Vector3 origin, Quaternion orientation, float stepDegrees = 0f)
    {
        if (axis is not (GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z)) return null;
        var frame = RingFrame(axis, orientation);
        return Drag(TranslateGizmo.Local(startRay, origin, frame), TranslateGizmo.Local(currentRay, origin, frame), Vector3.Zero, stepDegrees);
    }

    // The point at `angle` (radians) round the `axis` ring: what the viewport draws the ring through.
    public static Vector3 RingPoint(GizmoHandle axis, Vector3 origin, float size, Quaternion orientation, float angle) =>
        origin + Vector3.Transform(new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle)) * size, RingFrame(axis, orientation));

    // The axis a ring turns about, in origin space.
    public static Vector3 AxisOf(GizmoHandle axis, Quaternion orientation) =>
        Vector3.Transform(TranslateGizmo.AxisOf(axis), orientation);

    // A frame whose +Y is the ring's axis: X's ring is the Y ring tipped a quarter turn about Z (Y onto
    // X), Z's a quarter turn about X (Y onto Z); then the gizmo's own orientation.
    private static Quaternion RingFrame(GizmoHandle axis, Quaternion orientation)
    {
        var tip = axis switch
        {
            GizmoHandle.X => Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2f),
            GizmoHandle.Z => Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f),
            _ => Quaternion.Identity,
        };
        return Quaternion.Normalize(Quaternion.Concatenate(tip, orientation));
    }

    private static float? AngleAt(in EditorRay ray, Vector3 origin)
    {
        if (ray.IntersectPlane(origin, Vector3.UnitY) is not { } t) return null;
        Vector3 local = ray.At(t) - origin;
        if (local.X * local.X + local.Z * local.Z < 1e-10f) return null;
        return MathF.Atan2(-local.Z, local.X);
    }
}
