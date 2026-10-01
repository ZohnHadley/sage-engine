#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Editing;

// A ray in origin space: where it starts and which way it goes (unit length when built by
// EditorPicking.RayFrom). The editor's pointer, as picking and the gizmos see it.
[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public readonly record struct EditorRay(Vector3 Origin, Vector3 Direction)
{
    public Vector3 At(float distance) => Origin + Direction * distance;

    // Distance along the ray to the plane through `point` with `normal`, or null when the ray is parallel
    // to it or the plane is behind the origin.
    internal float? IntersectPlane(Vector3 point, Vector3 normal)
    {
        float denominator = Vector3.Dot(Direction, normal);
        if (MathF.Abs(denominator) < 1e-6f) return null;
        float t = Vector3.Dot(point - Origin, normal) / denominator;
        return t >= 0f ? t : null;
    }

    // Where the ray and the infinite line (`point`, `axis`) pass closest: the distance along the ray and
    // the distance along the line. Null when they are parallel (no single closest point).
    internal (float OnRay, float OnLine)? ClosestToLine(Vector3 point, Vector3 axis)
    {
        Vector3 w = Origin - point;
        float a = Vector3.Dot(Direction, Direction), b = Vector3.Dot(Direction, axis), c = Vector3.Dot(axis, axis);
        float d = Vector3.Dot(Direction, w), e = Vector3.Dot(axis, w);
        float denominator = a * c - b * b;
        if (denominator < 1e-6f * a * c) return null;
        return ((b * e - c * d) / denominator, (a * e - b * d) / denominator);
    }
}
