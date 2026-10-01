#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace Sage.Editing;

[Experimental("SAGE0133", UrlFormat = "https://github.com/ZohnHadley/sage-engine/blob/main/docs/MAKING_A_GAME.md#10b-experimental-api")]   // the editor's model (phase 10a)
public static class GizmoMath
{
    // The world size that makes a gizmo `pixels` tall on a viewport `viewportHeight` pixels high, from a
    // perspective camera with vertical field of view `fovY`: nearer it is smaller, farther larger, so
    // it always looks the same size. Zero for a degenerate viewport.
    public static float ScreenConstantSize(Vector3 cameraPosition, Vector3 origin, float fovY, float viewportHeight, float pixels)
    {
        if (viewportHeight <= 0f) return 0f;
        float distance = Vector3.Distance(cameraPosition, origin);
        return distance * 2f * MathF.Tan(MathF.Min(fovY, CameraMath.MaxFovY) * 0.5f) * pixels / viewportHeight;
    }
}
