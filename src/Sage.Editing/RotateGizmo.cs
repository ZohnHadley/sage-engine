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

    private static float? AngleAt(in EditorRay ray, Vector3 origin)
    {
        if (ray.IntersectPlane(origin, Vector3.UnitY) is not { } t) return null;
        Vector3 local = ray.At(t) - origin;
        if (local.X * local.X + local.Z * local.Z < 1e-10f) return null;
        return MathF.Atan2(-local.Z, local.X);
    }
}
