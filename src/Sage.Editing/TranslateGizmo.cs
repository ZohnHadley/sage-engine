#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Editing;

// The handles of the move gizmo: three axes and the three planes between them (world-aligned).
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
// `All` is the scale gizmo's centre (issue #367): every axis at once.
public enum GizmoHandle { None, X, Y, Z, XY, XZ, YZ, All }

// The translate gizmo's maths (docs/design/15 §6), with no drawing: the editor draws handles of length
// `size` at `origin` (see GizmoMath.ScreenConstantSize) and asks this which one the pointer is on and
// how far a drag moved the entity. An axis handle is a segment from the origin; a plane handle is a
// square between 0.25 and 0.5 of the size along its two axes.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class TranslateGizmo
{
    internal const float AxisGrabRadius = 0.08f;     // of size
    internal const float PlaneNear = 0.25f, PlaneFar = 0.5f;

    public static Vector3 AxisOf(GizmoHandle handle) => handle switch
    {
        GizmoHandle.X => Vector3.UnitX,
        GizmoHandle.Y => Vector3.UnitY,
        GizmoHandle.Z => Vector3.UnitZ,
        _ => Vector3.Zero,
    };

    // The handle under `ray`, nearest first with planes winning over the axes they sit between, or None.
    public static GizmoHandle HitTest(in EditorRay ray, Vector3 origin, float size)
    {
        if (size <= 0f) return GizmoHandle.None;

        GizmoHandle plane = GizmoHandle.None;
        float planeDistance = float.MaxValue;
        foreach (GizmoHandle h in new[] { GizmoHandle.XY, GizmoHandle.XZ, GizmoHandle.YZ })
        {
            var (u, v, normal) = PlaneBasis(h);
            if (ray.IntersectPlane(origin, normal) is not { } t) continue;
            Vector3 local = ray.At(t) - origin;
            float a = Vector3.Dot(local, u) / size, b = Vector3.Dot(local, v) / size;
            if (a < PlaneNear || a > PlaneFar || b < PlaneNear || b > PlaneFar || t >= planeDistance) continue;
            plane = h;
            planeDistance = t;
        }
        if (plane != GizmoHandle.None) return plane;
        return AxisHit(ray, origin, size);
    }

    // The axis handle under `ray` (nearest first), or None: the move gizmo's axes, and the scale gizmo's.
    internal static GizmoHandle AxisHit(in EditorRay ray, Vector3 origin, float size)
    {
        if (size <= 0f) return GizmoHandle.None;
        GizmoHandle axis = GizmoHandle.None;
        float nearest = float.MaxValue;
        foreach (GizmoHandle h in new[] { GizmoHandle.X, GizmoHandle.Y, GizmoHandle.Z })
        {
            Vector3 dir = AxisOf(h);
            if (ray.ClosestToLine(origin, dir) is not { } c) continue;   // looking straight down the axis
            float along = Math.Clamp(c.OnLine, 0f, size);
            Vector3 onAxis = origin + dir * along;
            float t = Math.Max(Vector3.Dot(onAxis - ray.Origin, ray.Direction), 0f);
            if (Vector3.Distance(ray.At(t), onAxis) > AxisGrabRadius * size || t >= nearest) continue;
            axis = h;
            nearest = t;
        }
        return axis;
    }

    // How far the pointer has moved the thing, in origin space, between `startRay` (when the drag began)
    // and `currentRay`. `origin` is where the thing was at the start; `grid` (metres, 0 = off) snaps the
    // *result* (origin + delta) on the moved axes, so a thing off the grid lands on it. Null when the
    // drag has no answer: a ray parallel to the axis or plane, or a plane behind the camera.
    public static Vector3? Drag(GizmoHandle handle, in EditorRay startRay, in EditorRay currentRay, Vector3 origin, float grid = 0f)
    {
        Vector3 delta;
        if (handle is GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z)
        {
            Vector3 axis = AxisOf(handle);
            if (startRay.ClosestToLine(origin, axis) is not { } a || currentRay.ClosestToLine(origin, axis) is not { } b) return null;
            delta = axis * (b.OnLine - a.OnLine);
        }
        else if (handle != GizmoHandle.None)
        {
            var (_, _, normal) = PlaneBasis(handle);
            if (startRay.IntersectPlane(origin, normal) is not { } t0 || currentRay.IntersectPlane(origin, normal) is not { } t1) return null;
            delta = currentRay.At(t1) - startRay.At(t0);
        }
        else return null;

        if (grid > 0f)
        {
            Vector3 target = origin + delta;
            if (delta.X != 0f) delta.X = Snap.ToGrid(target.X, grid) - origin.X;
            if (delta.Y != 0f) delta.Y = Snap.ToGrid(target.Y, grid) - origin.Y;
            if (delta.Z != 0f) delta.Z = Snap.ToGrid(target.Z, grid) - origin.Z;
        }
        return delta;
    }

    // ---- Local space (issue #367) --------------------------------------------------------------------

    // The same, with the handles turned by `orientation` (a placement's rotation, in local space): the
    // pointer is taken into the gizmo's own frame and asked there.
    public static GizmoHandle HitTest(in EditorRay ray, Vector3 origin, float size, Quaternion orientation) =>
        orientation.IsIdentity ? HitTest(ray, origin, size) : HitTest(Local(ray, origin, orientation), Vector3.Zero, size);

    // The same, along the turned axes. `grid` snaps how far it moved along each of them (in local space
    // there is no world grid to land on), and the answer is in origin space as before.
    public static Vector3? Drag(GizmoHandle handle, in EditorRay startRay, in EditorRay currentRay, Vector3 origin, Quaternion orientation, float grid = 0f)
    {
        if (orientation.IsIdentity) return Drag(handle, startRay, currentRay, origin, grid);
        if (Drag(handle, Local(startRay, origin, orientation), Local(currentRay, origin, orientation), Vector3.Zero, grid) is not { } local) return null;
        return Vector3.Transform(local, orientation);
    }

    // `ray` seen from a frame at `origin` turned by `orientation`: lengths along it are unchanged.
    internal static EditorRay Local(in EditorRay ray, Vector3 origin, Quaternion orientation)
    {
        var inverse = Quaternion.Inverse(Quaternion.Normalize(orientation));
        return new EditorRay(Vector3.Transform(ray.Origin - origin, inverse), Vector3.Transform(ray.Direction, inverse));
    }

    private static (Vector3 U, Vector3 V, Vector3 Normal) PlaneBasis(GizmoHandle handle) => handle switch
    {
        GizmoHandle.XY => (Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ),
        GizmoHandle.XZ => (Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
        _ => (Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX),
    };
}
